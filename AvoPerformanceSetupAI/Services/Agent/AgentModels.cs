namespace AvoPerformanceSetupAI.Services.Agent;

// ── DTOs returned / sent by AgentApiClient ────────────────────────────────────

/// <summary>Response from GET /api/reference/root</summary>
public sealed class ReferenceRootResponse
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>Response from POST /api/admin/referenceRoot/browse (returns chosen path)</summary>
public sealed class BrowseRootResponse
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>Single setup file entry returned by GET /api/reference/setups</summary>
public sealed class SetupItem
{
    public string FileName  { get; set; } = string.Empty;
    public string Car       { get; set; } = string.Empty;
    public string Track     { get; set; } = string.Empty;
}

/// <summary>Body for POST /api/setups/save</summary>
public sealed class SaveSetupRequest
{
    public string Car      { get; set; } = string.Empty;
    public string Track    { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>Raw INI text of the setup. Must match the Agent field name "content".</summary>
    public string Content  { get; set; } = string.Empty;
    public bool   Overwrite { get; set; } = true;
}

/// <summary>Response from POST /api/setups/save</summary>
public sealed class SaveResult
{
    public bool   Success { get; set; }
    public string Path    { get; set; } = string.Empty;
    public string Error   { get; set; } = string.Empty;
}

/// <summary>
/// Live state reported by GET /api/admin/state.
/// Used to decide whether Live Apply is available.
/// </summary>
public sealed class AgentAdminState
{
    /// <summary>True when Assetto Corsa is detected as running.</summary>
    public bool   AcRunning               { get; set; }

    /// <summary>True when the Agent has a valid shared-memory connection to AC.</summary>
    public bool   SharedMemoryConnected   { get; set; }

    /// <summary>The car folder name currently active in the simulator, e.g. "ks_porsche_911_gt3_r".</summary>
    public string ActiveCarId             { get; set; } = string.Empty;
}

/// <summary>
/// A single structured log entry streamed by the Agent over WebSocket
/// (<c>ws://HOST:PORT/ws/logs</c>).
/// </summary>
public sealed class AgentLogEntry
{
    /// <summary>UTC timestamp string, e.g. "2026-03-01T12:00:00.000Z".</summary>
    public string? TUtc { get; set; }
    /// <summary>Severity level, e.g. "INF", "WRN", "ERR".</summary>
    public string? Lvl  { get; set; }
    /// <summary>Log category / source, e.g. "Agent.Setups".</summary>
    public string? Cat  { get; set; }
    /// <summary>Human-readable log message.</summary>
    public string? Msg  { get; set; }

    /// <summary>
    /// Formatted display string: <c>[HH:mm:ss] LEVEL Category - Message</c>
    /// with UTC converted to local time.
    /// </summary>
    public string DisplayText
    {
        get
        {
            var time = string.Empty;
            if (DateTime.TryParse(TUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var utc))
                time = utc.ToLocalTime().ToString("HH:mm:ss");

            return $"[{time}] {Lvl ?? "???",-5} {Cat ?? string.Empty} - {Msg ?? string.Empty}";
        }
    }
}
