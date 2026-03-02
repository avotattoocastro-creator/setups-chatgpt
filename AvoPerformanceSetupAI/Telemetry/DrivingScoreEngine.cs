using System;

namespace AvoPerformanceSetupAI.Telemetry;

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>
/// Scalar driving-quality scores in the range [0, 100].
/// 100 = perfect; 0 = worst possible.
/// Computed from a <see cref="FeatureFrame"/> by
/// <see cref="DrivingScoreEngine.Compute(in FeatureFrame)"/>.
/// </summary>
public sealed class DrivingScores
{
    /// <summary>Balance score (0..100): low understeer and oversteer across all phases.</summary>
    public float BalanceScore   { get; init; }

    /// <summary>Stability score (0..100): smooth braking, low oscillation.</summary>
    public float StabilityScore { get; init; }

    /// <summary>Traction score (0..100): low rear wheelspin on exit.</summary>
    public float TractionScore  { get; init; }

    /// <summary>Brake score (0..100): low front lockup and brake imbalance.</summary>
    public float BrakeScore     { get; init; }

    /// <summary>
    /// Overall weighted score (0..100), combining all sub-scores.
    /// Weights: Balance 35 %, Stability 20 %, Traction 25 %, Brake 20 %.
    /// </summary>
    public float OverallScore   { get; init; }

    /// <summary>
    /// Corner-phase score for a single <see cref="CornerSummary"/>,
    /// or <see langword="null"/> when computed from a <see cref="FeatureFrame"/> only.
    /// </summary>
    public float? CornerScore   { get; init; }
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless engine that maps a <see cref="FeatureFrame"/> (and optionally a
/// <see cref="CornerSummary"/>) to a <see cref="DrivingScores"/> snapshot.
/// </summary>
/// <remarks>
/// <para>
/// All feature indices are already normalized to [0, 1] by
/// <see cref="FeatureExtractor"/>. A normalized index of 0 means no issue
/// (score contribution = 100); an index of 1 means worst case
/// (score contribution = 0). The mapping is linear per channel, then the
/// sub-scores are clamped to [0, 100].
/// </para>
/// <para>
/// The <see cref="OverallScore"/> is the key metric used for A/B test
/// comparison and proposal ranking in <c>UltraSetupAdvisor</c>.
/// </para>
/// </remarks>
public static class DrivingScoreEngine
{
    // ── Sub-score weights (must sum to 1.0) ───────────────────────────────────
    private const float WeightBalance   = 0.35f;
    private const float WeightStability = 0.20f;
    private const float WeightTraction  = 0.25f;
    private const float WeightBrake     = 0.20f;

    // ── Corner-phase weights for CornerScore (entry / mid / exit) ─────────────
    private const float PhaseWeightEntry = 0.30f;
    private const float PhaseWeightMid   = 0.30f;
    private const float PhaseWeightExit  = 0.40f;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes <see cref="DrivingScores"/> from the aggregate
    /// <paramref name="frame"/>. <see cref="DrivingScores.CornerScore"/>
    /// is <see langword="null"/> in the returned object.
    /// </summary>
    public static DrivingScores Compute(in FeatureFrame frame)
    {
        float balance   = ComputeBalance(in frame);
        float stability = ComputeStability(in frame);
        float traction  = ComputeTraction(in frame);
        float brake     = ComputeBrake(in frame);
        float overall   = Clamp(WeightBalance   * balance +
                                WeightStability * stability +
                                WeightTraction  * traction +
                                WeightBrake     * brake);

        return new DrivingScores
        {
            BalanceScore   = balance,
            StabilityScore = stability,
            TractionScore  = traction,
            BrakeScore     = brake,
            OverallScore   = overall,
        };
    }

    /// <summary>
    /// Computes <see cref="DrivingScores"/> from the aggregate frame AND
    /// fills <see cref="DrivingScores.CornerScore"/> from the per-phase frames
    /// of <paramref name="corner"/>.
    /// </summary>
    public static DrivingScores Compute(in FeatureFrame frame, in CornerSummary corner)
    {
        float balance   = ComputeBalance(in frame);
        float stability = ComputeStability(in frame);
        float traction  = ComputeTraction(in frame);
        float brake     = ComputeBrake(in frame);
        float overall   = Clamp(WeightBalance   * balance +
                                WeightStability * stability +
                                WeightTraction  * traction +
                                WeightBrake     * brake);

        // CornerScore: weighted by phase (entry, mid, exit)
        // Copy frames to locals so they can be passed as `in` parameters.
        FeatureFrame entryF = corner.EntryFrame;
        FeatureFrame midF   = corner.MidFrame;
        FeatureFrame exitF  = corner.ExitFrame;
        float entryScore  = CornerPhaseScore(in entryF);
        float midScore    = CornerPhaseScore(in midF);
        float exitScore   = CornerPhaseScore(in exitF);
        float cornerScore = Clamp(PhaseWeightEntry * entryScore +
                                  PhaseWeightMid   * midScore   +
                                  PhaseWeightExit  * exitScore);

        return new DrivingScores
        {
            BalanceScore   = balance,
            StabilityScore = stability,
            TractionScore  = traction,
            BrakeScore     = brake,
            OverallScore   = overall,
            CornerScore    = cornerScore,
        };
    }

    // ── Sub-score calculators ─────────────────────────────────────────────────

    /// <summary>Balance (low US + low OS across all phases).</summary>
    private static float ComputeBalance(in FeatureFrame f)
    {
        float usMax = MathF.Max(f.UndersteerEntry, MathF.Max(f.UndersteerMid, f.UndersteerExit));
        float osMax = MathF.Max(f.OversteerEntry, f.OversteerExit);
        float penalty = MathF.Max(usMax, osMax); // worst offender drives balance
        return Clamp(100f * (1f - penalty));
    }

    /// <summary>Stability (low brake instability + low suspension oscillation).</summary>
    private static float ComputeStability(in FeatureFrame f)
    {
        float penalty = (f.BrakeStabilityIndex + f.SuspensionOscillationIndex) * 0.5f;
        return Clamp(100f * (1f - penalty));
    }

    /// <summary>Traction (low rear wheelspin).</summary>
    private static float ComputeTraction(in FeatureFrame f)
        => Clamp(100f * (1f - f.WheelspinRatioRear));

    /// <summary>Brake (low front lockup + low tyre temp imbalance).</summary>
    private static float ComputeBrake(in FeatureFrame f)
    {
        float penalty = (f.LockupRatioFront + f.TyreTempDeltaFR * 0.5f + f.TyreTempDeltaLR * 0.5f) / 2f;
        return Clamp(100f * (1f - Math.Clamp(penalty, 0f, 1f)));
    }

    /// <summary>
    /// Single-phase corner score from one of the entry/mid/exit frames.
    /// Uses the worst individual issue index as the main penalty.
    /// </summary>
    private static float CornerPhaseScore(in FeatureFrame f)
    {
        float usMax = MathF.Max(f.UndersteerEntry, MathF.Max(f.UndersteerMid, f.UndersteerExit));
        float osMax = MathF.Max(f.OversteerEntry, f.OversteerExit);
        float worst = MathF.Max(MathF.Max(usMax, osMax),
                     MathF.Max(f.WheelspinRatioRear, f.LockupRatioFront));
        return Clamp(100f * (1f - worst));
    }

    private static float Clamp(float v) => Math.Clamp(v, 0f, 100f);

    /// <summary>
    /// Re-computes the <see cref="DrivingScores.OverallScore"/> from the
    /// pre-computed sub-scores using the weight set defined by
    /// <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// Weights by mode:
    /// <list type="table">
    ///   <listheader><term>Mode</term><term>Balance</term><term>Traction</term><term>Brake</term><term>Stability</term></listheader>
    ///   <item><term>Sprint</term>    <term>35 %</term><term>30 %</term><term>20 %</term><term>15 %</term></item>
    ///   <item><term>Endurance</term> <term>25 %</term><term>25 %</term><term>15 %</term><term>35 %</term></item>
    /// </list>
    /// The default (no-mode) weights match those used by <see cref="Compute(in FeatureFrame)"/>.
    /// </remarks>
    public static float ComputeOverallScore(DrivingScores scores, DrivingMode mode)
    {
        if (scores is null) throw new ArgumentNullException(nameof(scores));

        return mode switch
        {
            DrivingMode.Sprint    => Clamp(0.35f * scores.BalanceScore
                                         + 0.30f * scores.TractionScore
                                         + 0.20f * scores.BrakeScore
                                         + 0.15f * scores.StabilityScore),

            DrivingMode.Endurance => Clamp(0.25f * scores.BalanceScore
                                         + 0.25f * scores.TractionScore
                                         + 0.15f * scores.BrakeScore
                                         + 0.35f * scores.StabilityScore),

            // Default weights (same as Compute) — returned for any future mode values.
            _                     => scores.OverallScore,
        };
    }
}
