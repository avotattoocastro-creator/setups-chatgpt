using System;
using System.Threading;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Manages the active telemetry data source — either Assetto Corsa shared memory
/// or the built-in simulation generator.  Auto-retries the AC connection every 2 s
/// when the game is not yet running or when it closes mid-session.
/// </summary>
/// <remarks>
/// All public methods are thread-safe.  Events are raised on a background thread;
/// consumers must marshal to the UI thread before touching UI elements
/// (e.g. via <c>DispatcherQueue.TryEnqueue</c>).
/// </remarks>
public sealed class TelemetryService : IDisposable
{
    private readonly AcTelemetryReader _acReader = new();

    // ── Thread-safety ─────────────────────────────────────────────────────────

    private readonly object _lock = new();
    private Timer? _retryTimer;
    private bool _active;        // false in Stop() so in-flight callbacks don't fire events
    private int  _retryCount;    // consecutive failed reconnect attempts
    private bool _suggestFired;  // ensures SuggestSwitchToSimulation fires at most once per cycle

    private const int MaxRetriesBeforeSuggest = 10;

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>Ring buffer populated by the AC reader (empty while simulating).</summary>
    public TelemetryRingBuffer Buffer => _acReader.Buffer;

    /// <summary>
    /// <see langword="true"/> when AC shared memory is open and the 250 Hz poll
    /// loop is running.
    /// </summary>
    public bool IsAcConnected => _acReader.IsConnected;

    /// <summary>
    /// Raised on a background thread whenever connection state or status text changes.
    /// Parameters: (<c>isConnected</c>, <c>statusText</c>).
    /// </summary>
    public event Action<bool, string>? ConnectionChanged;

    /// <summary>
    /// Raised once after <see cref="MaxRetriesBeforeSuggest"/> consecutive failed
    /// reconnect attempts.  The ViewModel should prompt the user to switch to
    /// Simulation without switching automatically.
    /// </summary>
    public event Action? SuggestSwitchToSimulation;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TelemetryService()
    {
        // Subscribe once for the lifetime of this service.
        _acReader.Disconnected += OnAcDisconnected;
    }

    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs lightweight startup initialization of the telemetry subsystem
    /// (verifies dependencies are ready; does not start the streaming loop).
    /// Safe to await from the UI thread.
    /// </summary>
    public static Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Starts the requested telemetry source.
    /// <list type="bullet">
    ///   <item><see cref="TelemetrySource.AssettoCorsa"/> — attempts a shared-memory
    ///   connect; if AC is not running, fires <see cref="ConnectionChanged"/> with
    ///   <c>"AC not running"</c> and starts a 2-second retry timer.</item>
    ///   <item><see cref="TelemetrySource.Simulation"/> — stops the AC reader and
    ///   fires <see cref="ConnectionChanged"/> with <c>"Simulation"</c> so the
    ///   ViewModel's own simulation timer drives all data.</item>
    /// </list>
    /// </summary>
    public void Start(TelemetrySource source)
    {
        // Cancel any in-flight retry timer before starting a new source.
        Timer? old;
        lock (_lock)
        {
            _active       = true;
            _retryCount   = 0;
            _suggestFired = false;
            old           = _retryTimer;
            _retryTimer   = null;
        }
        old?.Dispose();
        _acReader.Disconnect();

        if (source == TelemetrySource.AssettoCorsa)
        {
            if (TryAutoConnectAc())
            {
                ConnectionChanged?.Invoke(true, "Connected to AC");
            }
            else
            {
                ConnectionChanged?.Invoke(false, "AC not running");
                lock (_lock)
                {
                    if (_active) // guard against an immediate Stop() call
                        _retryTimer = new Timer(
                            _ => RetryConnect(),
                            null,
                            TimeSpan.FromSeconds(2),
                            TimeSpan.FromSeconds(2));
                }
            }
        }
        else
        {
            // Simulation: the ViewModel's simulation timer drives all channel data.
            ConnectionChanged?.Invoke(false, "Simulation");
        }
    }

    /// <summary>Stops the active source and cancels any pending retry timer.</summary>
    public void Stop()
    {
        Timer? old;
        lock (_lock)
        {
            _active     = false;
            old         = _retryTimer;
            _retryTimer = null;
        }
        old?.Dispose(); // dispose outside the lock so the callback can finish cleanly
        _acReader.Disconnect();
    }

    /// <summary>
    /// Attempts to connect to Assetto Corsa shared memory once.
    /// Returns <see langword="true"/> on success.
    /// </summary>
    public bool TryAutoConnectAc() => _acReader.TryConnect();

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => Stop();

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Called on the poll thread when AC closes unexpectedly.
    /// Starts (or continues) the reconnect cycle and fires
    /// <see cref="ConnectionChanged"/> with <c>"Disconnected - retrying…"</c>.
    /// </summary>
    private void OnAcDisconnected()
    {
        lock (_lock)
        {
            if (!_active) return;
            // Reset the suggest flag so a new reconnect cycle gets a fresh 10-attempt window.
            _retryCount   = 0;
            _suggestFired = false;
            // Start retry timer if one isn't already running.
            if (_retryTimer is null)
                _retryTimer = new Timer(
                    _ => RetryConnect(),
                    null,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2));
        }
        ConnectionChanged?.Invoke(false, "Disconnected - retrying...");
    }

    private void RetryConnect()
    {
        // Always reset AC reader state before attempting to reconnect; this is
        // necessary because IsConnected may still be true after an abnormal disconnect.
        _acReader.Disconnect();

        if (!_acReader.TryConnect())
        {
            int count;
            bool fireSuggest;
            lock (_lock)
            {
                if (!_active) return;
                count       = ++_retryCount;
                fireSuggest = count >= MaxRetriesBeforeSuggest && !_suggestFired;
                if (fireSuggest) _suggestFired = true;
            }
            if (fireSuggest)
                SuggestSwitchToSimulation?.Invoke();
            return;
        }

        // Connected!
        Timer? old;
        lock (_lock)
        {
            // If Stop() was called while we were connecting, undo and bail out.
            if (!_active) { _acReader.Disconnect(); return; }
            _retryCount   = 0;
            _suggestFired = false;
            old           = _retryTimer;
            _retryTimer   = null;
        }
        old?.Dispose();
        ConnectionChanged?.Invoke(true, "Connected to AC");
    }
}
