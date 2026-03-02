using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.ML.Uncertainty;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ML;

/// <summary>
/// Selects the top-3 setup-change candidates — Safe, Balanced, Aggressive —
/// by computing a utility score that combines expected improvement with
/// penalties for uncertainty, risk, and driver-induced issues.
/// </summary>
/// <remarks>
/// <para>
/// <b>Utility formula</b>:
/// <code>
/// Utility = MeanImprovement
///         − RiskPenalty
///         − InstabilityPenalty
///         − DriverLikelyPenalty
/// </code>
/// where:
/// <list type="bullet">
///   <item><b>RiskPenalty</b> = interval-width proxy + negative-lower-bound penalty + mode penalty.</item>
///   <item><b>InstabilityPenalty</b> = wideness of the 80 % interval beyond a comfortable threshold.</item>
///   <item><b>DriverLikelyPenalty</b> = applied when the discriminator says the issue is driver-induced
///     rather than setup-induced.</item>
/// </list>
/// </para>
/// <para>
/// The engine then assigns tiers:
/// <list type="bullet">
///   <item><b>Safe</b> — highest <see cref="DecisionCandidate.CalibratedConfidence"/> with positive lower-80 %.</item>
///   <item><b>Balanced</b> — highest utility among remaining candidates.</item>
///   <item><b>Aggressive</b> — highest <see cref="UncertaintyEstimate.Mean"/> regardless of width.</item>
/// </list>
/// </para>
/// </remarks>
public static class RiskAwareDecisionEngine
{
    // ── Penalty weights ───────────────────────────────────────────────────────

    /// <summary>Weight applied to the normalised interval-width term in RiskPenalty.</summary>
    private const float IntervalWidthPenaltyScale = 0.30f;

    /// <summary>Penalty when Lower80 &lt; 0 (chance of worsening).</summary>
    private const float NegativeLower80Penalty = 2.0f;

    /// <summary>Additional risk penalty in Endurance mode (stability matters more).</summary>
    private const float EnduranceModeExtraPenalty = 1.0f;

    /// <summary>Base risk penalty multiplier by <see cref="RiskLevel"/>.</summary>
    private const float HighRiskPenalty   = 3.0f;
    private const float MediumRiskPenalty = 1.5f;

    /// <summary>Instability penalty applied per unit of interval width beyond the threshold.</summary>
    private const float InstabilityThreshold    = 8.0f;   // score-delta units (same 0..30 scale)
    private const float InstabilityPenaltyScale = 0.20f;

    /// <summary>Penalty applied when the root cause is driver-likely.</summary>
    private const float DriverLikelyPenalty = 4.0f;

    /// <summary>Penalty applied when the root cause is mixed (partial driver).</summary>
    private const float MixedCausePenalty = 1.5f;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Selects up to three <see cref="DecisionCandidate"/> objects from
    /// <paramref name="scored"/> representing the Safe, Balanced, and Aggressive choices.
    /// </summary>
    /// <param name="scored">Pre-scored candidates (any order).</param>
    /// <param name="drivingMode">
    /// Current driving mode — Endurance incurs an additional risk penalty.
    /// </param>
    /// <returns>
    /// Array of up to 3 <see cref="DecisionCandidate"/> objects in
    /// (Safe, Balanced, Aggressive) order.  Fewer than 3 are returned
    /// when <paramref name="scored"/> contains fewer distinct candidates.
    /// </returns>
    public static DecisionCandidate[] SelectTop3(
        IReadOnlyList<ScoredInput> scored,
        DrivingMode drivingMode = DrivingMode.Endurance)
    {
        if (scored is null || scored.Count == 0)
            return [];

        // Build full DecisionCandidate list with utility scores
        var candidates = new List<DecisionCandidate>(scored.Count);
        foreach (var s in scored)
        {
            float utility = ComputeUtility(s, drivingMode);
            candidates.Add(new DecisionCandidate
            {
                Proposal             = s.Proposal,
                Uncertainty          = s.Uncertainty,
                CalibratedConfidence = s.CalibratedConfidence,
                RiskLevel            = s.RiskLevel,
                RootCause            = s.RootCause,
                Utility              = utility,
                Tier                 = DecisionTier.Balanced, // placeholder; overwritten below
                Explanation          = string.Empty,
            });
        }

        if (candidates.Count == 0) return [];

        // ── Safe: highest CalibratedConfidence + positive Lower80 ─────────────
        DecisionCandidate? safe = null;
        float bestSafeScore = float.MinValue;
        foreach (var c in candidates)
        {
            if (!c.Uncertainty.IsPositiveLower80) continue;
            if (c.CalibratedConfidence > bestSafeScore)
            {
                bestSafeScore = c.CalibratedConfidence;
                safe          = c;
            }
        }
        // Fallback: highest calibrated confidence even without positive lower80
        if (safe is null)
        {
            foreach (var c in candidates)
            {
                if (c.CalibratedConfidence > bestSafeScore)
                {
                    bestSafeScore = c.CalibratedConfidence;
                    safe          = c;
                }
            }
        }

        // ── Aggressive: highest Mean ─────────────────────────────────────────
        DecisionCandidate? aggressive = null;
        float bestMean = float.MinValue;
        foreach (var c in candidates)
        {
            if (c.Uncertainty.Mean > bestMean)
            {
                bestMean    = c.Uncertainty.Mean;
                aggressive  = c;
            }
        }

        // ── Balanced: highest utility among remaining ─────────────────────────
        DecisionCandidate? balanced = null;
        float bestUtility = float.MinValue;
        foreach (var c in candidates)
        {
            if (ReferenceEquals(c, safe) || ReferenceEquals(c, aggressive)) continue;
            if (c.Utility > bestUtility)
            {
                bestUtility = c.Utility;
                balanced    = c;
            }
        }
        // Fallback when < 3 distinct candidates available
        if (balanced is null && candidates.Count >= 2)
        {
            foreach (var c in candidates)
            {
                if (!ReferenceEquals(c, safe) && c.Utility > bestUtility)
                {
                    bestUtility = c.Utility;
                    balanced    = c;
                }
            }
        }
        // When there are fewer than 3 distinct candidates, safe doubles as balanced.
        // The deduplication check at result-assembly time (ReferenceEquals) prevents
        // the same candidate appearing twice in the output.
        if (balanced is null && !ReferenceEquals(safe, aggressive))
            balanced = safe;

        // ── Build final result with tiers and explanations ────────────────────
        var result = new List<DecisionCandidate>(3);
        if (safe       is not null) result.Add(WithTierAndExplanation(safe,       DecisionTier.Safe));
        if (balanced   is not null && !ReferenceEquals(balanced, safe))
            result.Add(WithTierAndExplanation(balanced, DecisionTier.Balanced));
        if (aggressive is not null && !ReferenceEquals(aggressive, safe) && !ReferenceEquals(aggressive, balanced))
            result.Add(WithTierAndExplanation(aggressive, DecisionTier.Aggressive));

        return result.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static float ComputeUtility(ScoredInput s, DrivingMode mode)
    {
        float utility = s.Uncertainty.Mean;

        // ── RiskPenalty ───────────────────────────────────────────────────────
        float riskPenalty = s.RiskLevel switch
        {
            RiskLevel.High   => HighRiskPenalty,
            RiskLevel.Medium => MediumRiskPenalty,
            _                => 0f,
        };

        // Interval-width contribution: wide intervals = high uncertainty risk
        riskPenalty += s.Uncertainty.Width80 * IntervalWidthPenaltyScale;

        // Chance of worsening
        if (!s.Uncertainty.IsPositiveLower80)
            riskPenalty += NegativeLower80Penalty;

        // Endurance mode: stability matters more — extra penalty for risky changes
        if (mode == DrivingMode.Endurance && s.RiskLevel != RiskLevel.Low)
            riskPenalty += EnduranceModeExtraPenalty;

        utility -= riskPenalty;

        // ── InstabilityPenalty ────────────────────────────────────────────────
        float excess = Math.Max(0f, s.Uncertainty.Width80 - InstabilityThreshold);
        utility -= excess * InstabilityPenaltyScale;

        // ── DriverLikelyPenalty ───────────────────────────────────────────────
        utility -= s.RootCause switch
        {
            RootCauseType.DriverLikely => DriverLikelyPenalty,
            RootCauseType.Mixed        => MixedCausePenalty,
            _                          => 0f,
        };

        return utility;
    }

    private static DecisionCandidate WithTierAndExplanation(DecisionCandidate c, DecisionTier tier)
    {
        string explanation = tier switch
        {
            DecisionTier.Safe => BuildSafeExplanation(c),
            DecisionTier.Balanced => BuildBalancedExplanation(c),
            DecisionTier.Aggressive => BuildAggressiveExplanation(c),
            _ => string.Empty,
        };

        return new DecisionCandidate
        {
            Proposal             = c.Proposal,
            Uncertainty          = c.Uncertainty,
            CalibratedConfidence = c.CalibratedConfidence,
            RiskLevel            = c.RiskLevel,
            RootCause            = c.RootCause,
            Utility              = c.Utility,
            Tier                 = tier,
            Explanation          = explanation,
        };
    }

    private static string BuildSafeExplanation(DecisionCandidate c)
    {
        var parts = new List<string>();
        if (c.Uncertainty.IsPositiveLower80)
            parts.Add("lower-80 % bound is positive (likely beneficial)");
        parts.Add($"calibrated confidence {c.CalibratedConfidence:P0}");
        if (c.RiskLevel == RiskLevel.Low)
            parts.Add("low risk");
        if (c.Uncertainty.Width80 < InstabilityThreshold)
            parts.Add("tight prediction interval");
        return "SAFE — " + (parts.Count > 0 ? string.Join(", ", parts) : "best confidence in pool") + ".";
    }

    private static string BuildBalancedExplanation(DecisionCandidate c)
    {
        var parts = new List<string>();
        parts.Add($"utility {c.Utility:+0.0;-0.0}");
        parts.Add($"μΔscore {c.Uncertainty.Mean:+0.0;-0.0}");
        if (c.Uncertainty.Width80 <= InstabilityThreshold)
            parts.Add("moderate uncertainty");
        return "BALANCED — best risk-adjusted tradeoff: " + string.Join(", ", parts) + ".";
    }

    private static string BuildAggressiveExplanation(DecisionCandidate c)
    {
        var parts = new List<string>();
        parts.Add($"highest predicted mean Δscore {c.Uncertainty.Mean:+0.0;-0.0}");
        if (!c.Uncertainty.IsPositiveLower80)
            parts.Add($"note: lower-80 % is {c.Uncertainty.Lower80:+0.0;-0.0} (accept downside risk)");
        return "AGGRESSIVE — " + string.Join("; ", parts) + ".";
    }

    // ── Input record ─────────────────────────────────────────────────────────

    /// <summary>
    /// Input bundle supplied by the caller to
    /// <see cref="RiskAwareDecisionEngine.SelectTop3"/>.
    /// Encapsulates all per-candidate data that the engine needs.
    /// </summary>
    public sealed class ScoredInput
    {
        /// <summary>The underlying setup-change proposal.</summary>
        public Proposal Proposal { get; init; } = new();

        /// <summary>Probabilistic prediction from the ensemble + conformal predictor.</summary>
        public UncertaintyEstimate Uncertainty { get; init; }

        /// <summary>Calibrated confidence from <see cref="ConfidenceCalibrationEngine"/>.</summary>
        public float CalibratedConfidence { get; init; }

        /// <summary>Risk level of the change.</summary>
        public RiskLevel RiskLevel { get; init; }

        /// <summary>Root-cause classification from the discriminator.</summary>
        public RootCauseType RootCause { get; init; }
    }
}
