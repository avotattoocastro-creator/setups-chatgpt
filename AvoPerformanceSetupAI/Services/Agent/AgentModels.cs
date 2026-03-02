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
    public string Car       { get; set; } = string.Empty;
    public string Track     { get; set; } = string.Empty;
    public string FileName  { get; set; } = string.Empty;
    public string SetupText { get; set; } = string.Empty;
    public bool   Overwrite { get; set; } = true;
}

/// <summary>Response from POST /api/setup/save</summary>
public sealed class SaveResult
{
    public bool   Success { get; set; }
    public string Path    { get; set; } = string.Empty;
    public string Error   { get; set; } = string.Empty;
}

// ── POST /api/reference/setup/apply ──────────────────────────────────────────

/// <summary>One INI change in an <see cref="ApplySetupRequestDto"/>.</summary>
public sealed class ApplySetupChangeDto
{
    public string Section { get; set; } = string.Empty;
    public string Key     { get; set; } = string.Empty;
    public string Value   { get; set; } = string.Empty;
}

/// <summary>
/// Body for POST /api/reference/setup/apply — applies a list of INI key changes
/// to the specified base file and (optionally) saves a versioned copy.
/// </summary>
public sealed class ApplySetupRequestDto
{
    public string                    Car                 { get; set; } = string.Empty;
    public string                    Track               { get; set; } = string.Empty;
    public string                    BaseFile            { get; set; } = string.Empty;
    public List<ApplySetupChangeDto> Changes             { get; set; } = [];
    public bool                      CreateVersionedCopy { get; set; } = true;
    public string?                   Reason              { get; set; }
}

/// <summary>Response from POST /api/reference/setup/apply.</summary>
public sealed class ApplySetupResult
{
    // ── New Agent response shape ───────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when the versioned copy was written to disk successfully.
    /// This is the primary success indicator — the client should treat the operation
    /// as a success whenever <see cref="SavedOk"/> is <see langword="true"/>.
    /// </summary>
    public bool SavedOk { get; set; }

    /// <summary>
    /// <see langword="true"/> when the changes were also applied to the simulator
    /// process in real time. May be <see langword="false"/> even on a successful save
    /// (e.g. simulator not running). <em>Not</em> an error condition.
    /// </summary>
    public bool AppliedOk { get; set; }

    /// <summary>
    /// Human-readable explanation when <see cref="AppliedOk"/> is <see langword="false"/>,
    /// e.g. "Simulator not detected". <see langword="null"/> when <see cref="AppliedOk"/>
    /// is <see langword="true"/>.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>File name of the saved (versioned) setup, e.g. "Supra MKIV Race mid__AI__v003.ini".</summary>
    public string  SavedFile { get; set; } = string.Empty;

    public string  Path { get; set; } = string.Empty;

    // ── Backward-compat aliases (old Agent returned "success"/"error") ────────

    /// <summary>
    /// Alias for <see cref="SavedOk"/>. Old Agent versions return a <c>"success"</c>
    /// JSON property; new Agent versions return <c>"savedOk"</c>. Setting either
    /// updates the same underlying state.
    /// </summary>
    public bool    Success { get => SavedOk; set => SavedOk = value; }

    /// <summary>Error message from old Agent response shape. Mapped as the <see cref="Reason"/>.</summary>
    public string? Error   { get => Reason; set => Reason = value; }
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
