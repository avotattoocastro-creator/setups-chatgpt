using System.Text.Json.Serialization;

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

/// <summary>Body for POST /api/setup/save</summary>
public sealed class SaveSetupRequest
{
    public string CarId     { get; set; } = string.Empty;
    public string TrackId   { get; set; } = string.Empty;
    public string FileName  { get; set; } = string.Empty;
    /// <summary>Raw INI text of the setup.</summary>
    public string SetupText { get; set; } = string.Empty;
    public bool   Overwrite { get; set; } = true;
    public bool   Versioned { get; set; } = false;
}

/// <summary>Response from POST /api/setup/save</summary>
public sealed class SaveResult
{
    // Some agents return "success", others return "ok"
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("ok")]
    public bool SuccessAlias { set => Success = value; }

    // Path or file name of the saved setup
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    // Some agents return fileName instead of path
    [JsonPropertyName("fileName")]
    public string FileNameAlias
    {
        get => Path;
        set { if (!string.IsNullOrEmpty(value)) Path = value; }
    }

    // Error text; some agents use "message"
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string ErrorAlias
    {
        get => Error;
        set { if (!string.IsNullOrEmpty(value)) Error = value; }
    }

    /// <summary>The final file name chosen by the Agent (may differ from the requested FileName).</summary>
    [JsonPropertyName("savedFileName")]
    public string SavedFileName  { get; set; } = string.Empty;
}

/// <summary>Body for POST /api/setup/apply</summary>
public sealed class ApplySetupRequestDto
{
    public string Car        { get; set; } = string.Empty;
    public string Track      { get; set; } = string.Empty;
    public string File       { get; set; } = string.Empty;
    public string IniContent { get; set; } = string.Empty;
    public bool   Versioned  { get; set; } = true;
    public string Tag        { get; set; } = string.Empty;
}

/// <summary>Response from POST /api/setup/apply</summary>
public sealed class ApplySetupResult
{
    public bool   SavedOk   { get; set; }
    public string SavedFile { get; set; } = string.Empty;
    public string SavedPath { get; set; } = string.Empty;
    public bool   AppliedOk { get; set; }
    public string Reason    { get; set; } = string.Empty;
}

/// <summary>Response from GET /api/setups/versions</summary>
public sealed class VersionsResponse
{
    public List<string> Files { get; set; } = new();
}

/// <summary>
/// Live state reported by GET /api/admin/state.
/// Used to decide whether Live Apply is available.
/// </summary>
public sealed class AgentAdminState
{
    /// <summary>True when Assetto Corsa is detected as running.</summary>
    public bool   AcRunning               { get; set; }

    /// <summary>True when the Agent has a valid shared-memory connection to AC.
    /// The Agent reports this field as "acConnected" in its JSON response.</summary>
    [JsonPropertyName("acConnected")]
    public bool   SharedMemoryConnected   { get; set; }

    /// <summary>The car folder name currently active in the simulator, e.g. "ks_porsche_911_gt3_r".</summary>
    public string ActiveCarId             { get; set; } = string.Empty;

    /// <summary>The track folder name currently active in the simulator, e.g. "monza".</summary>
    public string ActiveTrackId           { get; set; } = string.Empty;
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
