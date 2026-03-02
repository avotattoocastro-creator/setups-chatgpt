using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Reference;

namespace AvoPerformanceSetupAI.Telemetry;

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>Identifies the most likely root cause of an observed handling issue.</summary>
public enum RootCauseType
{
    /// <summary>Not enough data to determine a root cause.</summary>
    Unknown,

    /// <summary>The issue is most likely caused by driver inputs (braking point, throttle application, steering).</summary>
    DriverLikely,

    /// <summary>The issue is most likely caused by the car setup (balance, traction, tyre pressures).</summary>
    SetupLikely,

    /// <summary>Both driver and setup signals are present; root cause is ambiguous.</summary>
    Mixed,
}

/// <summary>
/// Result produced by <see cref="DriverVsSetupDiscriminator.Evaluate"/>.
/// </summary>
public record struct RootCauseResult
{
    /// <summary>Most likely root cause of the observed handling issue.</summary>
    public RootCauseType Cause      { get; init; }

    /// <summary>Confidence in <see cref="Cause"/>, in the range [0, 1].</summary>
    public float         Confidence { get; init; }

    /// <summary>
    /// Human-readable explanation of the decision, e.g.
    /// "Cause: Setup (0.72) – persistent under-rotation mid-corner with consistent steering."
    /// </summary>
    public string        Explanation { get; init; }
}

// ── Discriminator ─────────────────────────────────────────────────────────────

/// <summary>
/// Analyses a short history of <see cref="CornerSummary"/> objects,
/// the current <see cref="FeatureFrame"/>, optional <see cref="LiveVsIdealFrame"/>
/// reference deltas, and <see cref="DrivingScores"/> to distinguish between
/// driver-induced and setup-induced handling issues.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works</b>: each rule votes a signed float in [−1, 1] towards one of
/// the two causes (positive = SetupLikely, negative = DriverLikely). The votes are
/// summed, clamped, and mapped to a <see cref="RootCauseType"/> and confidence.
/// </para>
/// <para>
/// <b>RuleEngine gating</b>: when the result is
/// <see cref="RootCauseType.DriverLikely"/> with confidence &gt; 0.7,
/// <see cref="ApplyGate"/> reduces every proposal confidence by 40 % and
/// appends a driving tip proposal instead of a setup change.
/// </para>
/// </remarks>
public static class DriverVsSetupDiscriminator
{
    // ── Tuning constants ──────────────────────────────────────────────────────

    /// <summary>Minimum number of completed corners required to evaluate any rule.</summary>
    public const int MinCorners = 2;

    /// <summary>UndersteerMid threshold above which mid-corner push is considered persistent (per corner).</summary>
    public const float UsMidPersistThreshold = 0.30f;

    /// <summary>DeltaThrottle magnitude that indicates an "aggressive throttle spike vs ideal".</summary>
    public const float ThrottleSpikeThreshold = 0.15f;

    /// <summary>DeltaBrake magnitude that indicates "more aggressive braking than ideal".</summary>
    public const float BrakeSpikeThreshold = 0.20f;

    /// <summary>DeltaSteer magnitude below which the steering trace is considered "similar to ideal".</summary>
    public const float SteerSimilarityThreshold = 0.08f;

    /// <summary>Confidence above which driver-likely gating reduces proposal confidences.</summary>
    public const float DriverGateConfidenceThreshold = 0.70f;

    /// <summary>Multiplier applied to proposal confidence when driver-likely gating is active.</summary>
    public const float ProposalConfidenceReductionFactor = 0.60f; // reduces by 40 %

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates all available rules and returns a <see cref="RootCauseResult"/>.
    /// <para>
    /// When fewer than <see cref="MinCorners"/> are supplied, the result is
    /// <see cref="RootCauseType.Unknown"/> with zero confidence.
    /// </para>
    /// </summary>
    /// <param name="corners">
    /// Last 2–3 completed corners (oldest first).
    /// Produced by <see cref="CornerDetector"/>.
    /// </param>
    /// <param name="frame">Current aggregate <see cref="FeatureFrame"/>.</param>
    /// <param name="ideal">Live-vs-ideal deltas (may be <see langword="null"/> when no reference is loaded).</param>
    /// <param name="scores">Current driving scores (may be <see langword="null"/>).</param>
    public static RootCauseResult Evaluate(
        ReadOnlySpan<CornerSummary> corners,
        in FeatureFrame             frame,
        LiveVsIdealFrame?           ideal,
        DrivingScores?              scores)
    {
        if (corners.Length < MinCorners)
            return new RootCauseResult { Cause = RootCauseType.Unknown, Explanation = "Insufficient corner data." };

        // Votes: positive = SetupLikely, negative = DriverLikely
        float vote        = 0f;
        var   evidence    = new List<string>(6);
        int   rulesActive = 0;

        // ── Rule 1: UndersteerMid persistence ────────────────────────────────
        // If UndersteerMid > threshold in ALL supplied corners AND DeltaSteer is small
        // (steering trace is similar to ideal) → SetupLikely.
        int usMidPersistCount = 0;
        foreach (var c in corners)
            if (c.MidFrame.UndersteerMid > UsMidPersistThreshold) usMidPersistCount++;

        bool usMidPersistent = usMidPersistCount == corners.Length;
        bool steerSimilarToIdeal = ideal is null || MathF.Abs(ideal.DeltaSteer) < SteerSimilarityThreshold;

        if (usMidPersistent && steerSimilarToIdeal)
        {
            vote += 0.5f;
            evidence.Add("subviraje mid persistente con volante similar al ideal → Setup");
            rulesActive++;
        }

        // ── Rule 2: Brake-point variance ─────────────────────────────────────
        // If brake stability index varies across corners but UndersteerMid does NOT
        // persist → DriverLikely (inconsistent braking points).
        if (!usMidPersistent)
        {
            float brakeVariance = BrakeStabilityVariance(corners);
            if (brakeVariance > 0.10f)
            {
                vote -= 0.4f;
                evidence.Add($"variabilidad de punto de frenada ({brakeVariance:F2}) → Piloto");
                rulesActive++;
            }
        }

        // ── Rule 3: WheelspinRear vs throttle spike ───────────────────────────
        // If rear wheelspin correlates with aggressive throttle vs ideal → DriverLikely.
        // If wheelspin happens with similar throttle → SetupLikely.
        float avgWheelspin = AvgOverCorners(corners, static c => c.ExitFrame.WheelspinRatioRear);
        if (avgWheelspin > 0.20f)
        {
            if (ideal != null && ideal.DeltaThrottle > ThrottleSpikeThreshold)
            {
                vote -= 0.4f;
                evidence.Add("wheelspin trasero con aceleración más agresiva que ideal → Piloto");
                rulesActive++;
            }
            else
            {
                vote += 0.35f;
                evidence.Add("wheelspin trasero con aceleración similar al ideal → Setup");
                rulesActive++;
            }
        }

        // ── Rule 4: LockupFront vs brake aggression ───────────────────────────
        // If front lockup happens with more aggressive braking than ideal → DriverLikely.
        // If lockup happens with similar brake trace → SetupLikely.
        float avgLockup = AvgOverCorners(corners, static c => c.EntryFrame.LockupRatioFront);
        if (avgLockup > 0.20f)
        {
            if (ideal != null && ideal.DeltaBrake > BrakeSpikeThreshold)
            {
                vote -= 0.4f;
                evidence.Add("bloqueo delantero con frenada más agresiva que ideal → Piloto");
                rulesActive++;
            }
            else
            {
                vote += 0.35f;
                evidence.Add("bloqueo delantero con frenada similar al ideal → Setup");
                rulesActive++;
            }
        }

        // ── Rule 5: OversteerExit driven by scores ────────────────────────────
        if (scores != null && scores.TractionScore < 40f)
        {
            float avgOsExit = AvgOverCorners(corners, static c => c.ExitFrame.OversteerExit);
            if (avgOsExit > 0.20f)
            {
                if (ideal != null && ideal.DeltaThrottle > ThrottleSpikeThreshold)
                {
                    vote -= 0.25f;
                    evidence.Add("sobreviraje en salida con aceleración agresiva → Piloto");
                    rulesActive++;
                }
                else
                {
                    vote += 0.25f;
                    evidence.Add("sobreviraje en salida con aceleración moderada → Setup");
                    rulesActive++;
                }
            }
        }

        // ── No rules fired ────────────────────────────────────────────────────
        if (rulesActive == 0)
            return new RootCauseResult
            {
                Cause       = RootCauseType.Unknown,
                Explanation = "No se detectaron patrones concluyentes.",
            };

        // ── Map accumulated vote to cause + confidence ────────────────────────
        float absVote    = MathF.Abs(vote);
        float confidence = Math.Clamp(absVote / (rulesActive * 0.5f), 0f, 1f);

        RootCauseType cause;
        if      (vote >  0.15f) cause = RootCauseType.SetupLikely;
        else if (vote < -0.15f) cause = RootCauseType.DriverLikely;
        else                    cause = RootCauseType.Mixed;

        string causeLabel = cause switch
        {
            RootCauseType.SetupLikely  => "Setup",
            RootCauseType.DriverLikely => "Piloto",
            _                          => "Mixto",
        };
        string summary = string.Join("; ", evidence);
        string explanation = $"Causa: {causeLabel} ({confidence:F2}) – {summary}.";

        return new RootCauseResult { Cause = cause, Confidence = confidence, Explanation = explanation };
    }

    // ── RuleEngine gating ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies discriminator gating to <paramref name="proposals"/> in-place.
    /// <para>
    /// When <paramref name="result"/> is <see cref="RootCauseType.DriverLikely"/>
    /// and confidence &gt; <see cref="DriverGateConfidenceThreshold"/>, every
    /// proposal confidence is reduced by 40 % and a driving-tip proposal is
    /// prepended to the array.
    /// </para>
    /// </summary>
    /// <returns>Possibly-replaced proposal array (same reference if no gating applied).</returns>
    public static Proposal[] ApplyGate(Proposal[] proposals, in RootCauseResult result)
    {
        if (proposals is null) throw new ArgumentNullException(nameof(proposals));

        if (result.Cause != RootCauseType.DriverLikely ||
            result.Confidence <= DriverGateConfidenceThreshold)
            return proposals;

        // Reduce all existing proposal confidences by 40 %
        for (int i = 0; i < proposals.Length; i++)
            proposals[i].Confidence = Math.Clamp(
                proposals[i].Confidence * ProposalConfidenceReductionFactor, 0f, 1f);

        // Prepend a driving-tip pseudo-proposal
        var tip = new Proposal
        {
            Section    = "DRIVING_TIP",
            Parameter  = "TECHNIQUE",
            Delta      = "—",
            Reason     = $"Consejo de pilotaje (IA): {ExtractTip(result.Explanation)}",
            Confidence = result.Confidence,
        };

        var gated = new Proposal[proposals.Length + 1];
        gated[0] = tip;
        proposals.CopyTo(gated, 1);
        return gated;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Standard deviation of BrakeStabilityIndex across corners as a variance proxy.</summary>
    private static float BrakeStabilityVariance(ReadOnlySpan<CornerSummary> corners)
    {
        if (corners.Length < 2) return 0f;
        float sum = 0f, sumSq = 0f;
        foreach (var c in corners) { sum += c.TotalFrame.BrakeStabilityIndex; sumSq += c.TotalFrame.BrakeStabilityIndex * c.TotalFrame.BrakeStabilityIndex; }
        float mean = sum / corners.Length;
        float variance = sumSq / corners.Length - mean * mean;
        return MathF.Sqrt(MathF.Max(variance, 0f));
    }

    private static float AvgOverCorners(ReadOnlySpan<CornerSummary> corners, Func<CornerSummary, float> selector)
    {
        if (corners.Length == 0) return 0f;
        float sum = 0f;
        foreach (var c in corners) sum += selector(c);
        return sum / corners.Length;
    }

    /// <summary>Extracts the first driving tip from the explanation string.</summary>
    private static string ExtractTip(string explanation)
    {
        // Return the first semicolon-delimited evidence fragment, or the full text.
        int idx = explanation.IndexOf(';');
        return idx > 0 ? explanation[..idx].Trim() : explanation;
    }
}
