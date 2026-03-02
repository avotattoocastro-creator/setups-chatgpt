using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Reference.Import;

/// <summary>
/// Describes how CSV column headers map to <see cref="ReferenceLapSample"/>
/// fields, and which strategy to use when deriving
/// <see cref="ReferenceLapSample.LapDistPct"/>.
/// </summary>
/// <remarks>
/// Obtain an instance via <see cref="KnownFormats.DetectMapping"/> (auto-detect)
/// or by constructing one manually for a fully custom format.
/// </remarks>
public sealed class CsvMapping
{
    // ── LapDistPct derivation strategy ────────────────────────────────────────
    // Exactly one of the three strategies must be non-null for a usable mapping.

    /// <summary>
    /// Header whose values are already normalised LapDistPct (0..1).
    /// Takes priority over the distance and time strategies.
    /// </summary>
    public string? LapDistPctColumn     { get; set; }

    /// <summary>
    /// Header that provides cumulative distance from the start line in metres.
    /// Normalised by <see cref="TrackLengthMeters"/> (or by the maximum value
    /// in the column when <see cref="TrackLengthMeters"/> is 0).
    /// </summary>
    public string? DistanceMetersColumn { get; set; }

    /// <summary>
    /// Total track length in metres; used with <see cref="DistanceMetersColumn"/>.
    /// When 0 the importer infers the length from the maximum distance in the file.
    /// </summary>
    public float   TrackLengthMeters    { get; set; }

    /// <summary>
    /// Fallback header that provides elapsed time in seconds when no distance
    /// channel is available. LapDistPct is derived by dividing each time by the
    /// total lap time, giving a uniform-time grid (not ideal for distance
    /// alignment, but acceptable when distance is unavailable).
    /// </summary>
    public string? TimeSecondsColumn    { get; set; }

    // ── Per-channel column headers ────────────────────────────────────────────

    public string? SpeedKmhColumn          { get; set; }
    public string? ThrottleColumn          { get; set; }
    public string? BrakeColumn             { get; set; }
    public string? SteeringColumn          { get; set; }
    public string? GearColumn              { get; set; }
    public string? RpmColumn               { get; set; }
    public string? LatGColumn              { get; set; }
    public string? LongGColumn             { get; set; }
    public string? SlipAngleFrontAvgColumn { get; set; }
    public string? SlipAngleRearAvgColumn  { get; set; }
    public string? WheelSlipRearAvgColumn  { get; set; }
    public string? TyreTempAvgColumn       { get; set; }
    public string? TyreTempFLColumn        { get; set; }
    public string? TyreTempFRColumn        { get; set; }
    public string? TyreTempRLColumn        { get; set; }
    public string? TyreTempRRColumn        { get; set; }
    public string? TyrePressureFLColumn    { get; set; }
    public string? TyrePressureFRColumn    { get; set; }
    public string? TyrePressureRLColumn    { get; set; }
    public string? TyrePressureRRColumn    { get; set; }

    // ── Derived properties ────────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when the mapping contains at least one usable
    /// LapDistPct source and a speed column, which are the minimum required to
    /// produce a meaningful <see cref="ReferenceLapSample"/> sequence.
    /// </summary>
    public bool IsUsable =>
        (LapDistPctColumn is not null ||
         DistanceMetersColumn is not null ||
         TimeSecondsColumn is not null)
        && SpeedKmhColumn is not null;

    // ── Auto-mapping from header list ─────────────────────────────────────────

    /// <summary>
    /// Builds a best-effort <see cref="CsvMapping"/> by matching each element of
    /// <paramref name="headers"/> against <see cref="KnownFormats.DefaultAliases"/>.
    /// Headers that don't match any alias are silently ignored.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is null.</exception>
    public static CsvMapping FromHeaders(IReadOnlyList<string> headers)
    {
        if (headers is null) throw new ArgumentNullException(nameof(headers));

        var mapping = new CsvMapping();
        foreach (var h in headers)
        {
            var key = h.Trim().ToLowerInvariant();
            if (!KnownFormats.DefaultAliases.TryGetValue(key, out var field)) continue;

            switch (field)
            {
                case "LapDistPct":         mapping.LapDistPctColumn          = h; break;
                case "DistanceMeters":     mapping.DistanceMetersColumn       = h; break;
                case "TimeSeconds":        mapping.TimeSecondsColumn          = h; break;
                case "SpeedKmh":           mapping.SpeedKmhColumn             = h; break;
                case "Throttle":           mapping.ThrottleColumn             = h; break;
                case "Brake":              mapping.BrakeColumn                = h; break;
                case "Steering":           mapping.SteeringColumn             = h; break;
                case "Gear":               mapping.GearColumn                 = h; break;
                case "Rpm":                mapping.RpmColumn                  = h; break;
                case "LatG":               mapping.LatGColumn                 = h; break;
                case "LongG":              mapping.LongGColumn                = h; break;
                case "SlipAngleFrontAvg":  mapping.SlipAngleFrontAvgColumn    = h; break;
                case "SlipAngleRearAvg":   mapping.SlipAngleRearAvgColumn     = h; break;
                case "WheelSlipRearAvg":   mapping.WheelSlipRearAvgColumn     = h; break;
                case "TyreTempAvg":        mapping.TyreTempAvgColumn          = h; break;
                case "TyreTempFL":         mapping.TyreTempFLColumn           = h; break;
                case "TyreTempFR":         mapping.TyreTempFRColumn           = h; break;
                case "TyreTempRL":         mapping.TyreTempRLColumn           = h; break;
                case "TyreTempRR":         mapping.TyreTempRRColumn           = h; break;
                case "TyrePressureFL":     mapping.TyrePressureFLColumn       = h; break;
                case "TyrePressureFR":     mapping.TyrePressureFRColumn       = h; break;
                case "TyrePressureRL":     mapping.TyrePressureRLColumn       = h; break;
                case "TyrePressureRR":     mapping.TyrePressureRRColumn       = h; break;
            }
        }
        return mapping;
    }
}
