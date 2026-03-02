using System;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>Corner turning direction inferred from the sign of average lateral G.</summary>
public enum CornerDirection { Unknown, Left, Right }

/// <summary>
/// Full characterization of a single completed corner, produced by
/// <see cref="CornerDetector"/> or <see cref="CornerPhaseAnalyzer"/>.
/// Phase-specific <see cref="FeatureFrame"/> objects carry all normalized
/// feature indices (understeer, oversteer, wheelspin, lockup, tyre temps, etc.)
/// for each corner sub-phase.
/// </summary>
public readonly record struct CornerSummary
{
    /// <summary>Sequential corner index within the analysis window (0 = oldest).</summary>
    public int CornerIndex { get; init; }

    /// <summary>Timestamp of the first sample of this corner.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Timestamp of the last sample of this corner.</summary>
    public DateTime EndTime { get; init; }

    /// <summary>Total corner duration (<see cref="EndTime"/> − <see cref="StartTime"/>).</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Corner turning direction inferred from average lateral G sign.</summary>
    public CornerDirection Direction { get; init; }

    /// <summary>
    /// Normalized track position (0..1) at the corner apex
    /// (sample with the peak absolute lateral G).
    /// </summary>
    public float LapPos { get; init; }

    /// <summary>Peak absolute lateral G recorded in this corner.</summary>
    public float PeakLateralG { get; init; }

    /// <summary>Total number of telemetry samples comprising this corner.</summary>
    public int SampleCount { get; init; }

    // ── Phase-specific feature frames ──────────────────────────────────────────

    /// <summary>
    /// Normalized feature snapshot computed exclusively from entry (braking) phase
    /// samples. A zeroed <see cref="FeatureFrame"/> when no braking samples were
    /// detected in this corner.
    /// </summary>
    public FeatureFrame EntryFrame { get; init; }

    /// <summary>
    /// Normalized feature snapshot computed exclusively from mid-corner
    /// (neutral throttle) phase samples. A zeroed frame when absent.
    /// </summary>
    public FeatureFrame MidFrame { get; init; }

    /// <summary>
    /// Normalized feature snapshot computed exclusively from exit (acceleration)
    /// phase samples. A zeroed frame when absent.
    /// </summary>
    public FeatureFrame ExitFrame { get; init; }

    /// <summary>
    /// Normalized feature snapshot computed from all samples in this corner,
    /// with phase labels assigned sample-by-sample.
    /// </summary>
    public FeatureFrame TotalFrame { get; init; }

    /// <summary>
    /// Primary issue tag derived from <see cref="TotalFrame"/>:
    /// "LOCK", "SPIN", "US" (understeer), "OS" (oversteer), or "OK".
    /// </summary>
    public string Dominant { get; init; }
}
