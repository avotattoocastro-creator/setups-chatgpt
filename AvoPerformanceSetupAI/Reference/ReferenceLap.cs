using System;

namespace AvoPerformanceSetupAI.Reference;

/// <summary>Identifies whether a <see cref="ReferenceLap"/> was recorded live or imported from a file.</summary>
public enum ReferenceLapSource
{
    /// <summary>Captured directly from Assetto Corsa shared memory during a valid lap.</summary>
    Recorded,

    /// <summary>Imported from an external CSV file (e.g. MoTeC, Garage61, AC Logger).</summary>
    Imported,
}

/// <summary>
/// Unified internal representation of a reference lap, used by both the live
/// recorder and the CSV importer.  Samples are always stored on the fixed
/// 1000-point LapDistPct grid produced by <see cref="ReferenceLapResampler"/>.
/// </summary>
/// <remarks>
/// Serialised to JSON by <see cref="ReferenceLapStore"/>.
/// <see cref="SchemaVersion"/> must be incremented whenever the on-disk layout
/// changes in a backwards-incompatible way.
/// </remarks>
public sealed class ReferenceLap
{
    // ── Versioning ────────────────────────────────────────────────────────────

    /// <summary>Current JSON schema version. Increment on breaking format changes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version persisted in the JSON file.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Assetto Corsa car model identifier (e.g. "ks_ferrari_488_gt3").</summary>
    public string CarId      { get; set; } = string.Empty;

    /// <summary>Assetto Corsa track name identifier (e.g. "ks_silverstone").</summary>
    public string TrackId    { get; set; } = string.Empty;

    /// <summary>How this lap was obtained.</summary>
    public ReferenceLapSource Source { get; set; }

    /// <summary>UTC timestamp when this reference was created.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Optional free-text notes (e.g. "Qualy run, 26°C, slicks").</summary>
    public string Notes { get; set; } = string.Empty;

    // ── Samples ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Exactly <see cref="ReferenceLapResampler.GridSize"/> samples on a fixed
    /// LapDistPct grid (0 / (GridSize−1), 1 / (GridSize−1), … 1.0).
    /// Never empty after resampling.
    /// </summary>
    public ReferenceLapSample[] Samples { get; set; } = [];
}
