using Microsoft.ML.Data;

namespace AvoPerformanceSetupAI.ML;

/// <summary>
/// A single labelled training example used to train the
/// <see cref="ImpactModelTrainer"/> FastTree regression model.
/// Each row represents one completed A/B test where a specific
/// setup change (<see cref="SectionEncoded"/> / <see cref="ParameterEncoded"/> /
/// <see cref="DeltaValue"/>) was evaluated against a telemetry baseline.
/// </summary>
/// <remarks>
/// All feature fields are pre-normalized to [0, 1] (or a small numeric range for
/// encoded categoricals) so the FastTree learner does not need per-column scaling.
/// <br/>
/// Serialized to JSON / CSV via <see cref="ImpactModelTrainer"/>.
/// </remarks>
public sealed class ImpactTrainingSample
{
    // ── Telemetry context at the time of the A/B test ─────────────────────────

    /// <summary>Understeer index during braking / corner entry (0..1).</summary>
    [LoadColumn(0)]
    public float UndersteerEntry { get; set; }

    /// <summary>Understeer index during mid-corner (0..1).</summary>
    [LoadColumn(1)]
    public float UndersteerMid { get; set; }

    /// <summary>Oversteer index during braking / corner entry (0..1).</summary>
    [LoadColumn(2)]
    public float OversteerEntry { get; set; }

    /// <summary>Oversteer index during throttle / corner exit (0..1).</summary>
    [LoadColumn(3)]
    public float OversteerExit { get; set; }

    /// <summary>Rear-wheel-spin index (0..1).</summary>
    [LoadColumn(4)]
    public float WheelspinRatioRear { get; set; }

    /// <summary>Front wheel-lockup index during braking (0..1).</summary>
    [LoadColumn(5)]
    public float LockupRatioFront { get; set; }

    /// <summary>Brake pressure instability coefficient of variation (0..1).</summary>
    [LoadColumn(6)]
    public float BrakeStabilityIndex { get; set; }

    /// <summary>Suspension oscillation index (0..1).</summary>
    [LoadColumn(7)]
    public float SuspensionOscillationIndex { get; set; }

    // ── Driving quality scores before the change ──────────────────────────────

    /// <summary>Balance score before the change (0..100).</summary>
    [LoadColumn(8)]
    public float BalanceScore { get; set; }

    /// <summary>Stability score before the change (0..100).</summary>
    [LoadColumn(9)]
    public float StabilityScore { get; set; }

    /// <summary>Traction score before the change (0..100).</summary>
    [LoadColumn(10)]
    public float TractionScore { get; set; }

    /// <summary>Brake score before the change (0..100).</summary>
    [LoadColumn(11)]
    public float BrakeScore { get; set; }

    // ── Proposed change characteristics ──────────────────────────────────────

    /// <summary>
    /// Integer encoding of the setup section (e.g. "ARB" = 1, "AERO" = 2, …).
    /// See <see cref="ImpactModelTrainer.EncodedSections"/> for the mapping.
    /// </summary>
    [LoadColumn(12)]
    public float SectionEncoded { get; set; }

    /// <summary>
    /// Integer encoding of the parameter within the section
    /// (e.g. "FRONT" = 1, "DIFF_ACC" = 2, …).
    /// See <see cref="ImpactModelTrainer.EncodedParameters"/> for the mapping.
    /// </summary>
    [LoadColumn(13)]
    public float ParameterEncoded { get; set; }

    /// <summary>
    /// Numeric size of the applied adjustment (+1 / −1 / +2 / −0.05 etc.),
    /// extracted from <c>Proposal.Delta</c>.
    /// </summary>
    [LoadColumn(14)]
    public float DeltaValue { get; set; }

    // ── Label ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Observed change in overall score (after − before) as measured by the
    /// completed A/B test. Positive = improvement.
    /// This is the regression label.
    /// </summary>
    [LoadColumn(15), ColumnName("Label")]
    public float DeltaOverallScore { get; set; }
}
