using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.ML;
using AvoPerformanceSetupAI.Profiles;

namespace AvoPerformanceSetupAI.Telemetry;

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>
/// A scored combination of two setup changes produced by
/// <see cref="MultiParameterOptimizer"/>.
/// </summary>
public sealed class CombinedProposal
{
    /// <summary>The two individual changes that make up this combination.</summary>
    public AdvisedProposal[] Changes { get; init; } = [];

    /// <summary>
    /// Sum of the two individual score deltas, reduced by the interaction
    /// penalty and any conflict/risk-stacking adjustments.
    /// Positive = net improvement.
    /// </summary>
    public float CombinedScoreDelta { get; init; }

    /// <summary>Sum of the two individual estimated lap-time deltas (seconds).</summary>
    public float EstimatedLapDelta { get; init; }

    /// <summary>
    /// Worst risk level among the two changes, escalated to
    /// <see cref="RiskLevel.High"/> when both changes are <see cref="RiskLevel.Medium"/>
    /// or one is already <see cref="RiskLevel.High"/>.
    /// </summary>
    public RiskLevel RiskLevel { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="CombinedScoreDelta"/> was computed
    /// using the ML predictor; <see langword="false"/> for the heuristic path.
    /// </summary>
    public bool ScoredByMlModel { get; init; }

    // ── Display helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Compact description of the combined changes, e.g. "ARB:FRONT +1 / BRAKES:BRAKE_BIAS −1".
    /// </summary>
    public string ChangesDisplay
    {
        get
        {
            if (Changes.Length == 0) return string.Empty;
            if (Changes.Length == 1) return $"{Changes[0].Section}:{Changes[0].Parameter} {Changes[0].Delta}";
            return $"{Changes[0].Section}:{Changes[0].Parameter} {Changes[0].Delta}  /  " +
                   $"{Changes[1].Section}:{Changes[1].Parameter} {Changes[1].Delta}";
        }
    }

    /// <summary>Risk formatted as a coloured emoji badge for display in the UI.</summary>
    public string RiskDisplay => RiskLevel switch
    {
        RiskLevel.Low    => "🟢 LOW",
        RiskLevel.Medium => "🟡 MED",
        RiskLevel.High   => "🔴 HIGH",
        _                => "—",
    };

    /// <summary>Combined score delta formatted as "+X.X" or "−X.X".</summary>
    public string ScoreDeltaDisplay =>
        CombinedScoreDelta >= 0
            ? $"+{CombinedScoreDelta:F1}"
            : $"{CombinedScoreDelta:F1}";
}

// ── MultiParameterOptimizer ───────────────────────────────────────────────────

/// <summary>
/// Evaluates all 2-change combinations taken from the top candidates produced
/// by <see cref="UltraSetupAdvisor"/> and returns the top 3 scored combinations.
/// </summary>
/// <remarks>
/// <para><b>Scoring path</b></para>
/// <list type="bullet">
///   <item><b>ML available</b> — sums the two individual ML-predicted score
///     deltas, then subtracts a 10 % interaction-uncertainty penalty.</item>
///   <item><b>Heuristic fallback</b> — sums the two
///     <see cref="AdvisedProposal.EstimatedScoreDelta"/> values and applies the
///     same 10 % penalty.</item>
/// </list>
/// <para><b>Additional penalties applied to every combo</b></para>
/// <list type="bullet">
///   <item>Conflicting parameters — e.g. stiffening ARB:FRONT while simultaneously
///     softening SPRINGS:FRONT_SPRING. A 20 % deduction is applied to the combined
///     delta when a known conflict pair is detected.</item>
///   <item>High-risk stacking — when both changes are
///     <see cref="RiskLevel.Medium"/>, the escalated risk is
///     <see cref="RiskLevel.High"/> and an additional 15 % deduction is applied.</item>
/// </list>
/// <para>
/// Thread-safety: this class is stateless; all public methods are safe to call
/// from any thread simultaneously.
/// </para>
/// </remarks>
public static class MultiParameterOptimizer
{
    /// <summary>Maximum number of candidate changes taken from <see cref="UltraSetupAdvisor"/>.</summary>
    public const int MaxCandidatePool = 6;

    /// <summary>
    /// Minimum number of candidates required to form at least one pair.
    /// </summary>
    public const int MinCandidatesForCombo = 2;

    /// <summary>Maximum number of 2-change combinations evaluated.</summary>
    public const int MaxCombinations = 15;

    /// <summary>Number of top combinations returned.</summary>
    public const int MaxTopCombinations = 3;

    // ── Score bounds ──────────────────────────────────────────────────────────

    /// <summary>
    /// Lower bound for <see cref="CombinedProposal.CombinedScoreDelta"/>.
    /// Negative values represent a net degradation from applying both changes.
    /// </summary>
    private const float MinCombinedScore = -30f;

    /// <summary>
    /// Upper bound for <see cref="CombinedProposal.CombinedScoreDelta"/>.
    /// 30 points is the maximum expected improvement across all four sub-scores.
    /// </summary>
    private const float MaxCombinedScore = 30f;

    // ── Penalty constants ─────────────────────────────────────────────────────

    /// <summary>Synergy-uncertainty deduction applied to every combined score (10 %).</summary>
    private const float InteractionPenalty = 0.10f;

    /// <summary>Additional deduction when a known parameter conflict is detected (20 %).</summary>
    private const float ConflictPenalty = 0.20f;

    /// <summary>
    /// Additional deduction when two Medium-risk changes are stacked (15 %).
    /// The escalated risk is also promoted to <see cref="RiskLevel.High"/>.
    /// </summary>
    private const float RiskStackPenalty = 0.15f;

    // ── Conflict pairs ────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs of "Section:Parameter" keys that are known to conflict when applied
    /// simultaneously — e.g. increasing ARB stiffness while softening the spring
    /// on the same axle sends opposing balance signals.
    /// Each entry is an unordered pair; the check is symmetric.
    /// </summary>
    private static readonly (string A, string B)[] ConflictPairs =
    [
        ("ARB:FRONT",           "SPRINGS:FRONT_SPRING"),
        ("ARB:REAR",            "DAMPERS:BUMP_REAR"),
        ("AERO:FRONT_WING",     "ALIGNMENT:CAMBER_LF"),
        ("BRAKES:BRAKE_BIAS",   "BRAKES:BRAKE_POWER"),
        ("ELECTRONICS:DIFF_ACC","ELECTRONICS:TRACTION_CONTROL"),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates the top <see cref="MaxTopCombinations"/> 2-change combinations
    /// from the supplied candidate pool.
    /// </summary>
    /// <param name="candidatePool">
    /// Scored single-change proposals (e.g. from
    /// <see cref="UltraSetupAdvisor.Advise"/>). Only the first
    /// <see cref="MaxCandidatePool"/> entries are used.
    /// </param>
    /// <param name="frame">
    /// Current telemetry <see cref="FeatureFrame"/> — passed to the ML predictor
    /// when scoring.
    /// </param>
    /// <param name="scores">
    /// Current <see cref="DrivingScores"/> — used to build the ML sample template.
    /// </param>
    /// <param name="predictor">
    /// Optional <see cref="ImpactPredictor"/>. When non-<see langword="null"/> and
    /// loaded, combo deltas are computed as the sum of two ML predictions minus the
    /// interaction penalty. Falls back to heuristic when <see langword="null"/> or
    /// not loaded.
    /// </param>
    /// <returns>
    /// Up to <see cref="MaxTopCombinations"/> <see cref="CombinedProposal"/> objects,
    /// sorted by <see cref="CombinedProposal.CombinedScoreDelta"/> descending.
    /// Returns an empty array when fewer than 2 candidates are provided.
    /// </returns>
    public static CombinedProposal[] Optimize(
        ReadOnlySpan<AdvisedProposal> candidatePool,
        in FeatureFrame               frame,
        DrivingScores?                scores,
        ImpactPredictor?              predictor = null)
    {
        // Need at least 2 candidates to form a pair
        var pool = candidatePool.Length > MaxCandidatePool
            ? candidatePool[..MaxCandidatePool]
            : candidatePool;

        if (pool.Length < 2) return [];

        bool useML = predictor?.IsModelLoaded == true;
        ImpactTrainingSample? sampleTemplate = useML
            ? BuildSampleTemplate(in frame, scores)
            : null;

        // ── Generate all pairs ────────────────────────────────────────────────
        var combos = new List<CombinedProposal>(pool.Length * (pool.Length - 1) / 2);

        for (int i = 0; i < pool.Length - 1; i++)
        {
            for (int j = i + 1; j < pool.Length; j++)
            {
                if (combos.Count >= MaxCombinations) break;

                var a = pool[i];
                var b = pool[j];

                combos.Add(ScoreCombo(a, b, useML, sampleTemplate, predictor));
            }
            if (combos.Count >= MaxCombinations) break;
        }

        // ── Sort by combined score delta descending, cap ──────────────────────
        combos.Sort(static (x, y) => y.CombinedScoreDelta.CompareTo(x.CombinedScoreDelta));
        if (combos.Count > MaxTopCombinations)
            combos.RemoveRange(MaxTopCombinations, combos.Count - MaxTopCombinations);

        return [.. combos];
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static CombinedProposal ScoreCombo(
        AdvisedProposal      a,
        AdvisedProposal      b,
        bool                 useML,
        ImpactTrainingSample? sampleTemplate,
        ImpactPredictor?     predictor)
    {
        // ── Base score delta: sum of individual scores ────────────────────────
        float deltaA, deltaB;
        bool  scoredByMl = false;

        if (useML && sampleTemplate != null)
        {
            var predA = predictor!.Predict(CloneSampleTemplate(sampleTemplate, a));
            var predB = predictor!.Predict(CloneSampleTemplate(sampleTemplate, b));

            if (predA != null && predB != null)
            {
                deltaA     = predA.DeltaOverallScore;
                deltaB     = predB.DeltaOverallScore;
                scoredByMl = true;
            }
            else
            {
                // Partial failure — fall back fully to heuristic
                deltaA = a.EstimatedScoreDelta;
                deltaB = b.EstimatedScoreDelta;
            }
        }
        else
        {
            deltaA = a.EstimatedScoreDelta;
            deltaB = b.EstimatedScoreDelta;
        }

        float combined = deltaA + deltaB;

        // ── Apply interaction-uncertainty penalty (always 10 %) ───────────────
        combined *= (1f - InteractionPenalty);

        // ── Conflict penalty ──────────────────────────────────────────────────
        if (IsConflict(a, b))
            combined *= (1f - ConflictPenalty);

        // ── Risk calculation + stacking penalty ───────────────────────────────
        var risk = EscalateRisk(a.RiskLevel, b.RiskLevel);
        if (a.RiskLevel == RiskLevel.Medium && b.RiskLevel == RiskLevel.Medium)
            combined *= (1f - RiskStackPenalty);

        // ── Lap delta: simple sum ─────────────────────────────────────────────
        float lapDelta = a.EstimatedLapDeltaSec + b.EstimatedLapDeltaSec;

        return new CombinedProposal
        {
            Changes             = [a, b],
            CombinedScoreDelta  = Math.Clamp(combined, MinCombinedScore, MaxCombinedScore),
            EstimatedLapDelta   = lapDelta,
            RiskLevel           = risk,
            ScoredByMlModel     = scoredByMl,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsConflict(AdvisedProposal a, AdvisedProposal b)
    {
        var keyA = $"{a.Section}:{a.Parameter}";
        var keyB = $"{b.Section}:{b.Parameter}";

        foreach (var (x, y) in ConflictPairs)
        {
            if ((string.Equals(keyA, x, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(keyB, y, StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(keyA, y, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(keyB, x, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static RiskLevel EscalateRisk(RiskLevel a, RiskLevel b)
    {
        // Both Medium → escalate to High (stacking risk)
        if (a == RiskLevel.Medium && b == RiskLevel.Medium) return RiskLevel.High;
        return a > b ? a : b;
    }

    private static ImpactTrainingSample BuildSampleTemplate(
        in FeatureFrame frame,
        DrivingScores?  scores)
        => new()
        {
            UndersteerEntry            = frame.UndersteerEntry,
            UndersteerMid              = frame.UndersteerMid,
            OversteerEntry             = frame.OversteerEntry,
            OversteerExit              = frame.OversteerExit,
            WheelspinRatioRear         = frame.WheelspinRatioRear,
            LockupRatioFront           = frame.LockupRatioFront,
            BrakeStabilityIndex        = frame.BrakeStabilityIndex,
            SuspensionOscillationIndex = frame.SuspensionOscillationIndex,
            BalanceScore               = scores?.BalanceScore   ?? 50f,
            StabilityScore             = scores?.StabilityScore ?? 50f,
            TractionScore              = scores?.TractionScore  ?? 50f,
            BrakeScore                 = scores?.BrakeScore     ?? 50f,
        };

    private static ImpactTrainingSample CloneSampleTemplate(
        ImpactTrainingSample template,
        AdvisedProposal      proposal)
    {
        ImpactModelTrainer.EncodedSections.TryGetValue(proposal.Section,     out var secCode);
        ImpactModelTrainer.EncodedParameters.TryGetValue(proposal.Parameter, out var parCode);
        float deltaValue = float.TryParse(
            proposal.Delta,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var dv) ? dv : 0f;

        return new ImpactTrainingSample
        {
            UndersteerEntry            = template.UndersteerEntry,
            UndersteerMid              = template.UndersteerMid,
            OversteerEntry             = template.OversteerEntry,
            OversteerExit              = template.OversteerExit,
            WheelspinRatioRear         = template.WheelspinRatioRear,
            LockupRatioFront           = template.LockupRatioFront,
            BrakeStabilityIndex        = template.BrakeStabilityIndex,
            SuspensionOscillationIndex = template.SuspensionOscillationIndex,
            BalanceScore               = template.BalanceScore,
            StabilityScore             = template.StabilityScore,
            TractionScore              = template.TractionScore,
            BrakeScore                 = template.BrakeScore,
            SectionEncoded             = secCode,
            ParameterEncoded           = parCode,
            DeltaValue                 = deltaValue,
        };
    }
}
