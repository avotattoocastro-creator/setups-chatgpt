using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AvoPerformanceSetupAI.Services.Agent;

/// <summary>
/// Consumes live structured log entries from the Agent over WebSocket.
/// Connect with <see cref="StartAsync"/>, disconnect with <see cref="StopAsync"/>.
/// Each received entry fires <see cref="OnLog"/>.
/// Status events (connecting, connected, failed) fire <see cref="OnStatus"/>.
/// </summary>
public sealed class AgentLogStream : IDisposable
{
    private const int MaxConnectAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions _jsonOpts =
        new(JsonSerializerDefaults.Web);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fired on the background receive loop when a valid log entry arrives.</summary>
    public event Action<AgentLogEntry>? OnLog;

    /// <summary>
    /// Fired with a human-readable status string: "Connecting to …",
    /// "Connected", "Connection failed: …", "Disconnected".
    /// Always fired on the background thread — callers must marshal to UI.
    /// </summary>
    public event Action<string>? OnStatus;

    /// <summary>
    /// Opens the WebSocket connection to <paramref name="wsUrl"/> and starts receiving
    /// log entries asynchronously. Retries up to <see cref="MaxConnectAttempts"/> times
    /// with a <see cref="RetryDelay"/> pause between attempts.
    /// Safe to call again after <see cref="StopAsync"/>.
    /// </summary>
    public async Task StartAsync(string wsUrl)
    {
        await StopAsync().ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Warn if the host looks like it is still set to the default localhost
        // while Remote mode is active — a common misconfiguration.
        if (wsUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            wsUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            OnStatus?.Invoke(
                "⚠ RemoteHost is 'localhost' — check Configuracion if the Agent is on another PC.");
        }

        System.Diagnostics.Debug.WriteLine($"[AgentLogStream] Connecting to: {wsUrl}");

        Exception? lastEx = null;

        for (var attempt = 1; attempt <= MaxConnectAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return;

            OnStatus?.Invoke(
                $"Connecting to {wsUrl} (attempt {attempt}/{MaxConnectAttempts})…");

            _ws = new ClientWebSocket();
            try
            {
                await _ws.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine("[AgentLogStream] Connected.");
                OnStatus?.Invoke("Connected ✓");

                _readTask = ReadLoopAsync(_ws, ct);
                return;                  // success — leave
            }
            catch (OperationCanceledException)
            {
                _ws.Dispose();
                _ws = null;
                return;                  // disposed / StopAsync called
            }
            catch (Exception ex)
            {
                lastEx = ex;
                var msg = $"Connection failed (attempt {attempt}/{MaxConnectAttempts}): {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[AgentLogStream] {msg}");
                OnStatus?.Invoke(msg);

                _ws.Dispose();
                _ws = null;

                if (attempt < MaxConnectAttempts && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        // All attempts exhausted — notify and throw so the caller can observe the failure.
        var finalMsg = $"Could not connect to {wsUrl} after {MaxConnectAttempts} attempts.";
        System.Diagnostics.Debug.WriteLine($"[AgentLogStream] {finalMsg}");
        OnStatus?.Invoke(finalMsg);
        _cts.Dispose();
        _cts = null;
        throw new InvalidOperationException(finalMsg, lastEx);
    }

    /// <summary>Closes the WebSocket and stops the receive loop.</summary>
    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_ws is { State: WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Stop", closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch { /* ignore errors during graceful close */ }
        }

        if (_readTask is not null)
        {
            try { await _readTask.ConfigureAwait(false); }
            catch { /* already cancelled */ }
            _readTask = null;
        }

        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Prefer calling <see cref="StopAsync"/> explicitly from async code.
    /// This synchronous overload is provided for <see cref="IDisposable"/> compatibility;
    /// it signals cancellation and does not wait for the receive loop to fully drain.
    /// </remarks>
    public void Dispose()
    {
        _cts?.Cancel();
        _ws?.Dispose();
        _ws  = null;
        _cts?.Dispose();
        _cts = null;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb     = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested &&
                   ws.State == WebSocketState.Open)
            {
                sb.Clear();

                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnStatus?.Invoke("Disconnected (server closed connection).");
                        return;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                TryDispatch(sb.ToString());
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentLogStream] Read error: {ex.Message}");
            OnStatus?.Invoke($"Stream error: {ex.Message}");
        }
    }

    private void TryDispatch(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var entry = JsonSerializer.Deserialize<AgentLogEntry>(json, _jsonOpts);
            if (entry is not null)
                OnLog?.Invoke(entry);
        }
        catch { /* invalid JSON — ignore */ }
    }
}

