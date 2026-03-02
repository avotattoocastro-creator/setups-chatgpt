using System;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Snapshot of the five computed metrics used by <see cref="AbTestManager"/>
/// to compare setup configurations across a set of corner samples.
/// </summary>
public readonly record struct AbTestMetrics
{
    /// <summary>Average understeer index during mid-corner (0..1).</summary>
    public float UndersteerMidAvg      { get; init; }

    /// <summary>Average oversteer index during corner exit (0..1).</summary>
    public float OversteerExitAvg      { get; init; }

    /// <summary>Average rear-wheel-spin index (0..1).</summary>
    public float WheelspinRatioRearAvg { get; init; }

    /// <summary>
    /// Average brake-pressure-stability index (0..1).
    /// Lower values indicate a more stable, consistent braking input.
    /// </summary>
    public float BrakeStabilityAvg     { get; init; }

    /// <summary>
    /// Composite stability score (0..1) = 1 − (OversteerEntry avg + SuspensionOscillationIndex avg) / 2.
    /// Higher values indicate a more stable, planted car.
    /// </summary>
    public float StabilityScore        { get; init; }

    /// <summary>Number of corner samples used to compute these averages.</summary>
    public int   SampleCount           { get; init; }
}

/// <summary>
/// Result produced by <see cref="AbTestManager"/> after collecting baseline and
/// post-change corner data for a single <see cref="Proposal"/>.
/// </summary>
public sealed class AbTestResult
{
    /// <summary>The proposal that was applied to produce the "changed" configuration.</summary>
    public Proposal TestedProposal  { get; init; } = new();

    /// <summary>Metrics computed from the baseline corners (before the setup change).</summary>
    public AbTestMetrics BaselineMetrics { get; init; }

    /// <summary>Metrics computed from the changed corners (after the setup change).</summary>
    public AbTestMetrics ChangedMetrics  { get; init; }

    /// <summary>
    /// Per-metric deltas: Changed − Baseline.
    /// Negative deltas for understeer / oversteer / wheelspin / brake-stability metrics
    /// indicate improvement; a positive delta for <see cref="AbTestMetrics.StabilityScore"/>
    /// also indicates improvement.
    /// </summary>
    public AbTestMetrics Deltas          { get; init; }

    /// <summary>
    /// <see langword="true"/> when at least three of the five metrics moved in the
    /// beneficial direction (reduced understeer, oversteer, wheelspin, or brake
    /// instability, or an improved stability score).
    /// </summary>
    public bool Improved                 { get; init; }

    /// <summary>
    /// Human-readable summary of the comparison result with per-metric highlights.
    /// </summary>
    public string SummaryText            { get; init; } = string.Empty;

    /// <summary>
    /// Confidence in the result (0..1), derived from sample count and intra-phase
    /// variance. Two corners per side yields ≈ 0.40; four corners per side ≈ 0.57.
    /// </summary>
    public float Confidence              { get; init; }

    /// <summary>UTC timestamp of when this result was produced.</summary>
    public DateTime Timestamp            { get; init; }
}
