using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.ML;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Profiles;
using AvoPerformanceSetupAI.Reference;

namespace AvoPerformanceSetupAI.Telemetry;

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>Risk classification for a proposed setup change.</summary>
public enum RiskLevel
{
    /// <summary>Change is conservative; unlikely to destabilize the car.</summary>
    Low,

    /// <summary>Change has a moderate effect; should be validated on-track.</summary>
    Medium,

    /// <summary>Change is aggressive; high potential for unexpected handling shift.</summary>
    High,
}

/// <summary>
/// An enriched setup-change proposal produced by <see cref="UltraSetupAdvisor"/>.
/// Extends the baseline <see cref="Proposal"/> with estimated lap-time and
/// score impact.
/// </summary>
public sealed class AdvisedProposal
{
    // ── Baseline proposal fields (mirrors Proposal) ───────────────────────────

    /// <summary>Setup section, e.g. "ARB", "AERO", "ELECTRONICS".</summary>
    public string Section    { get; init; } = string.Empty;

    /// <summary>Parameter within the section, e.g. "FRONT", "DIFF_ACC".</summary>
    public string Parameter  { get; init; } = string.Empty;

    /// <summary>Signed adjustment step, e.g. "+1", "-0.05".</summary>
    public string Delta      { get; init; } = string.Empty;

    /// <summary>Human-readable explanation.</summary>
    public string Reason     { get; init; } = string.Empty;

    /// <summary>Rule-engine confidence (0..1), possibly reduced by discriminator gating.</summary>
    public float  Confidence { get; init; }

    // ── Impact estimates ──────────────────────────────────────────────────────

    /// <summary>
    /// Rough estimated lap-time delta in seconds (positive = faster).
    /// Derived from a simple heuristic table; not a simulation result.
    /// </summary>
    public float EstimatedLapDeltaSec { get; init; }

    /// <summary>
    /// Estimated overall <see cref="DrivingScores.OverallScore"/> delta
    /// (positive = score improvement).
    /// </summary>
    public float EstimatedScoreDelta  { get; init; }

    /// <summary>Risk level of this change.</summary>
    public RiskLevel RiskLevel { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="EstimatedScoreDelta"/> was produced
    /// by the ML.NET <see cref="ImpactPredictor"/>; <see langword="false"/> when
    /// it came from the heuristic <c>SimulationImpactEstimator</c>.
    /// </summary>
    public bool ScoredByMlModel { get; init; }

    /// <summary>
    /// 3-lap performance projection computed by <see cref="VirtualLapSimulator"/>,
    /// or <see langword="null"/> when the simulator was not run for this proposal.
    /// Populated by <see cref="UltraSetupAdvisor.Advise"/> for the top
    /// <see cref="UltraSetupAdvisor.MaxTopProposals"/> candidates when a
    /// <see cref="VirtualLapSimulator"/> instance is supplied.
    /// </summary>
    public VirtualSimulationResult? VirtualSimulation { get; set; }
}

// ── SimulationImpactEstimator (private heuristic) ────────────────────────────

/// <summary>
/// Lightweight heuristic estimator that converts a normalized <see cref="FeatureFrame"/>
/// index and a section/parameter change into expected sub-score deltas.
/// All values are rough approximations; the goal is relative ranking, not accuracy.
/// </summary>
file static class SimulationImpactEstimator
{
    // Impact table: (Section:Parameter) -> (balanceGain, tractionGain, brakeGain, lapDeltaSec, risk)
    private static readonly Dictionary<string, (float balance, float traction, float brake, float lapDelta, RiskLevel risk)>
        _table = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARB:FRONT"]              = (12f,  0f,  2f, 0.08f, RiskLevel.Low),
            ["ARB:REAR"]               = (10f,  2f,  0f, 0.07f, RiskLevel.Low),
            ["SPRINGS:FRONT_SPRING"]   = (8f,   0f,  3f, 0.06f, RiskLevel.Medium),
            ["AERO:FRONT_WING"]        = (10f,  0f,  0f, 0.12f, RiskLevel.Medium),
            ["ELECTRONICS:DIFF_ACC"]   = (5f,  12f,  0f, 0.10f, RiskLevel.Medium),
            ["ELECTRONICS:TRACTION_CONTROL"] = (0f, 8f, 0f, 0.05f, RiskLevel.Low),
            ["BRAKES:BRAKE_BIAS"]      = (0f,   0f, 14f, 0.09f, RiskLevel.Medium),
            ["BRAKES:BRAKE_POWER"]     = (0f,   0f, 10f, 0.07f, RiskLevel.Medium),
            ["TYRES:PRESSURE_LF"]      = (3f,   4f,  3f, 0.05f, RiskLevel.Low),
            ["ALIGNMENT:CAMBER_LF"]    = (4f,   3f,  2f, 0.06f, RiskLevel.High),
            ["DAMPERS:BUMP_REAR"]      = (3f,   2f,  2f, 0.04f, RiskLevel.Low),
        };

    public static (float balanceDelta, float tractionDelta, float brakeDelta,
                   float lapDelta, RiskLevel risk)
        Estimate(string section, string parameter, float featureIndex)
    {
        var key = $"{section}:{parameter}";
        if (!_table.TryGetValue(key, out var row))
            return (0f, 0f, 0f, 0f, RiskLevel.Low);

        // Scale impact linearly with feature severity; cap at 1 to avoid inflating minor issues
        float scale = Math.Clamp(featureIndex * 1.5f, 0.2f, 1.0f);
        return (row.balance * scale,
                row.traction * scale,
                row.brake    * scale,
                row.lapDelta * scale,
                row.risk);
    }
}

// ── UltraSetupAdvisor ─────────────────────────────────────────────────────────

/// <summary>
/// High-level setup advisor that:
/// <list type="number">
///   <item>Generates up to 10–15 candidate <see cref="Proposal"/> objects using
///     the <see cref="RuleEngine"/> as a baseline.</item>
///   <item>Scores each candidate with <c>SimulationImpactEstimator</c>.</item>
///   <item>Applies discriminator gating via <see cref="DriverVsSetupDiscriminator.ApplyGate"/>.</item>
///   <item>Returns the top <see cref="MaxTopProposals"/> <see cref="AdvisedProposal"/>
///     objects sorted by <see cref="AdvisedProposal.EstimatedLapDeltaSec"/> descending,
///     or delegates to <see cref="MultiParameterOptimizer"/> when
///     <see cref="EnableMultiParameterOptimization"/> is <see langword="true"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The estimated lap delta and score delta are rough heuristics, not simulation
/// results. They are useful for relative ranking only. More accurate estimates
/// require a vehicle-dynamics model beyond the scope of this module.
/// </para>
/// <para>
/// Thread-safety: this class is stateless. All methods are safe to call from
/// any thread simultaneously.
/// </para>
/// </remarks>
public static class UltraSetupAdvisor
{
    /// <summary>Maximum number of top proposals returned by <see cref="Advise"/>.</summary>
    public const int MaxTopProposals = 3;

    /// <summary>Maximum number of candidates generated before scoring.</summary>
    public const int MaxCandidates = 15;

    /// <summary>
    /// Minimum number of training samples required before multi-parameter
    /// optimization is allowed. Below this threshold
    /// <see cref="EnableMultiParameterOptimization"/> is automatically treated
    /// as <see langword="false"/> to avoid combining unreliable predictions.
    /// </summary>
    public const int MinSamplesForMultiOptimize = 20;

    // ── Mode flag ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/> and the ML model is loaded with at least
    /// <see cref="MinSamplesForMultiOptimize"/> training samples, <see cref="Advise"/>
    /// delegates ranking to <see cref="MultiParameterOptimizer"/> and returns
    /// <see cref="CombinedProposal"/> instances wrapped as
    /// <see cref="AdvisedProposal"/> objects (one per combination).
    /// Falls back to single-change mode automatically when:
    /// <list type="bullet">
    ///   <item>The ML model is not loaded.</item>
    ///   <item>The local training dataset has fewer than
    ///     <see cref="MinSamplesForMultiOptimize"/> samples.</item>
    /// </list>
    /// </summary>
    public static bool EnableMultiParameterOptimization { get; set; }

    /// <summary>
    /// Active driving session mode that governs heuristic score weights and
    /// virtual-simulator lap weighting.
    /// Defaults to <see cref="DrivingMode.Endurance"/>.
    /// </summary>
    public static DrivingMode CurrentMode { get; set; } = DrivingMode.Endurance;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates scored setup proposals from the supplied context.
    /// Returns an empty array when no rules are triggered.
    /// </summary>
    /// <param name="frame">Aggregate <see cref="FeatureFrame"/> from the current session.</param>
    /// <param name="corners">Last completed corners (used for cross-validation).</param>
    /// <param name="scores">
    /// Current <see cref="DrivingScores"/> (used for relative scoring of improvements).
    /// When <see langword="null"/> the estimator still works but score deltas are scaled down.
    /// </param>
    /// <param name="rootCause">Discriminator result; gates proposals when DriverLikely.</param>
    /// <param name="profile">Optional car/track profile passed to <see cref="RuleEngine"/>.</param>
    /// <param name="predictor">
    /// Optional trained <see cref="ImpactPredictor"/>.
    /// When non-<see langword="null"/> and <see cref="ImpactPredictor.IsModelLoaded"/>
    /// is <see langword="true"/>, candidates are ranked by the ML-predicted score
    /// delta instead of the heuristic table.
    /// Falls back to heuristic automatically when the model is not available.
    /// </param>
    /// <param name="driverProfile">
    /// Optional <see cref="DriverProfile"/> computed by <c>DriverStyleAnalyzer</c>.
    /// When supplied, risk penalties, stability preference, brake-bias sensitivity,
    /// and rear-rotation weighting are adjusted to match the driver's style.
    /// </param>
    /// <param name="rlEngine">
    /// Optional <see cref="RLPolicyEngine"/> instance.
    /// When non-<see langword="null"/> and <see cref="RLPolicyEngine.IsEnabled"/> is
    /// <see langword="true"/>, the Q-value for each candidate action is blended into
    /// the final score.  The blend weights are taken from <paramref name="weightEngine"/>
    /// when provided, otherwise the fixed <see cref="RLPolicyEngine.MlWeight"/> /
    /// <see cref="RLPolicyEngine.RlWeight"/> constants are used as fallback.
    /// Use <see cref="NotifyAbTestResult"/> to keep the engine updated.
    /// </param>
    /// <param name="simulator">
    /// Optional context object used to enable the 3-lap virtual simulation.
    /// When non-<see langword="null"/>, <see cref="VirtualLapSimulator.Simulate"/> is
    /// called for each of the top <see cref="MaxTopProposals"/> candidates, storing
    /// the result in <see cref="AdvisedProposal.VirtualSimulation"/>.
    /// Candidates are then re-sorted using <see cref="CurrentMode"/>-weighted composite
    /// scores: <see cref="DrivingMode.Sprint"/> favours Lap 1 performance;
    /// <see cref="DrivingMode.Endurance"/> favours Lap 2 + Lap 3 stability.
    /// Pass any non-<see langword="null"/> object (e.g. the <see cref="ImpactPredictor"/>)
    /// to activate; the simulator is stateless.
    /// </param>
    /// <param name="weightEngine">
    /// Optional <see cref="AdaptiveWeightEngine"/> instance.
    /// When non-<see langword="null"/>, the three-way blend
    /// <c>FinalScore = MlWeight × mlScore + RlWeight × Q(s,a) + HeuristicWeight × heuristicScore</c>
    /// uses the adaptive weights instead of the fixed <see cref="RLPolicyEngine"/> constants.
    /// The heuristic score is always computed and stored on the <see cref="AdvisedProposal"/>
    /// even when ML or RL scoring is available, so that <see cref="NotifyAbTestResult"/>
    /// can record the prediction error later.
    /// </param>
    public static AdvisedProposal[] Advise(
        in FeatureFrame             frame,
        ReadOnlySpan<CornerSummary> corners,
        DrivingScores?              scores,
        in RootCauseResult          rootCause,
        CarTrackProfile?            profile       = null,
        ImpactPredictor?            predictor     = null,
        DriverProfile?              driverProfile = null,
        RLPolicyEngine?             rlEngine      = null,
        object?                     simulator     = null,
        AdaptiveWeightEngine?       weightEngine  = null)
    {
        // ── Step 1: generate candidates from RuleEngine ───────────────────────
        var rawProposals = profile != null
            ? RuleEngine.Evaluate(in frame, profile)
            : RuleEngine.Evaluate(in frame);

        // Expand with corner-phase rules to reach MaxCandidates if possible
        var candidates = new List<Proposal>(rawProposals);
        EnrichFromCorners(candidates, corners, frame);

        // Cap at MaxCandidates
        if (candidates.Count > MaxCandidates)
            candidates.RemoveRange(MaxCandidates, candidates.Count - MaxCandidates);

        // ── Step 2: apply discriminator gating ────────────────────────────────
        var gated = DriverVsSetupDiscriminator.ApplyGate([.. candidates], in rootCause);

        // ── Step 3: decide scoring path ───────────────────────────────────────
        bool useML = predictor?.IsModelLoaded == true;

        // Build an ImpactTrainingSample template from current context (reused per candidate)
        ImpactTrainingSample? sampleTemplate = useML
            ? BuildSampleTemplate(in frame, scores)
            : null;

        // Precompute RL state once for all candidates (null when RL is disabled or not provided)
        RLState? rlState = (rlEngine != null && RLPolicyEngine.IsEnabled)
            ? RLPolicyEngine.BuildState(in frame, scores, driverProfile)
            : (RLState?)null;

        // ── Step 4: score each candidate ──────────────────────────────────────
        var advised = new List<AdvisedProposal>(gated.Length);
        float baseOverall = scores?.OverallScore ?? 50f;

        foreach (var p in gated)
        {
            // Always run the heuristic to obtain risk + lap-delta (ML doesn't cover those)
            var (balDelta, tracDelta, brkDelta, lapDelta, risk) =
                SimulationImpactEstimator.Estimate(p.Section, p.Parameter, p.Confidence);

            // Heuristic score: computed once and reused for blending and prediction-error tracking.
            float heuristicScore = ComputeHeuristicScoreDelta(balDelta, tracDelta, brkDelta, scores);

            float scoreDelta;
            bool  scoredByMl = false;

            if (useML && sampleTemplate != null)
            {
                // Populate candidate-specific fields on a copy of the template
                var sample = CloneSampleTemplate(sampleTemplate, p);
                var prediction = predictor!.Predict(sample);

                if (prediction != null)
                {
                    scoreDelta  = Math.Clamp(prediction.DeltaOverallScore, 0f, 30f);
                    scoredByMl  = true;
                }
                else
                {
                    // Model returned null unexpectedly — fall back to heuristic
                    scoreDelta = heuristicScore;
                }
            }
            else
            {
                scoreDelta = heuristicScore;
            }

            // ── RL + adaptive three-way blend ────────────────────────────────
            // Blending happens here — before the driver-profile adjustments and
            // risk penalties below — so those subsequent multipliers are applied
            // to the already-blended score rather than to the raw ML score alone.
            if (rlState.HasValue)
            {
                var rlAction   = RLPolicyEngine.ActionFromProposal(p);
                var stateValue = rlState.Value;   // local copy required for 'in' parameter
                float qValue   = rlEngine!.GetQValue(in stateValue, in rlAction);

                if (weightEngine != null)
                {
                    // Three-way adaptive blend:
                    // FinalScore = MlWeight × mlScore + RlWeight × Q(s,a) + HeuristicWeight × heuristicScore
                    scoreDelta = weightEngine.MlWeight        * scoreDelta
                               + weightEngine.RlWeight        * qValue
                               + weightEngine.HeuristicWeight * heuristicScore;
                }
                else
                {
                    // Fallback: fixed two-way blend (original behaviour).
                    scoreDelta = RLPolicyEngine.MlWeight * scoreDelta
                               + RLPolicyEngine.RlWeight  * qValue;
                }
            }
            else if (weightEngine != null)
            {
                // RL not active — use adaptive weights for ML + heuristic only.
                // Re-normalise the two active weights so they still sum to 1.
                float mw    = weightEngine.MlWeight + weightEngine.RlWeight;
                float hw    = weightEngine.HeuristicWeight;
                float total = mw + hw;
                scoreDelta  = (mw / total) * scoreDelta
                            + (hw / total) * heuristicScore;
            }

            // Penalize high-risk changes (applied regardless of scoring path)
            // ── Driver-profile adjustments ────────────────────────────────────
            // Adjust penalties and weights based on the driver's measured style.
            float riskHighMult   = 0.70f;
            float riskMediumMult = 0.90f;
            ApplyDriverProfileAdjustments(
                driverProfile,
                p.Section, p.Parameter,
                ref lapDelta, ref scoreDelta,
                ref riskHighMult, ref riskMediumMult);

            if (risk == RiskLevel.High)   { lapDelta *= riskHighMult;   scoreDelta *= riskHighMult;   }
            if (risk == RiskLevel.Medium) { lapDelta *= riskMediumMult; scoreDelta *= riskMediumMult; }

            advised.Add(new AdvisedProposal
            {
                Section               = p.Section,
                Parameter             = p.Parameter,
                Delta                 = p.Delta,
                Reason                = p.Reason,
                Confidence            = p.Confidence,
                EstimatedLapDeltaSec  = lapDelta,
                EstimatedScoreDelta   = Math.Clamp(scoreDelta, 0f, 30f),
                RiskLevel             = risk,
                ScoredByMlModel       = scoredByMl,
            });
        }

        // ── Step 5: multi-parameter optimization (optional) ──────────────────
        // Requirements: flag on, model loaded, sufficient training samples.
        bool mlReady          = predictor?.IsModelLoaded == true;
        bool datasetSufficient = ImpactModelTrainer.DatasetSampleCount >= MinSamplesForMultiOptimize;

        if (EnableMultiParameterOptimization && mlReady && datasetSufficient)
        {
            var combos = MultiParameterOptimizer.Optimize(
                [.. advised], in frame, scores, predictor);

            if (combos.Length > 0)
            {
                // Wrap each CombinedProposal as an AdvisedProposal so the return
                // type is unchanged and callers need no special handling.
                // The Section/Parameter/Delta fields reflect the first change;
                // the Reason encodes both changes for display.
                var multiAdvised = new List<AdvisedProposal>(combos.Length);
                foreach (var combo in combos)
                {
                    multiAdvised.Add(new AdvisedProposal
                    {
                        Section              = combo.Changes[0].Section,
                        Parameter            = combo.Changes[0].Parameter,
                        Delta                = combo.Changes[0].Delta,
                        Reason               = combo.ChangesDisplay,
                        Confidence           = combo.Changes[0].Confidence,
                        EstimatedLapDeltaSec = combo.EstimatedLapDelta,
                        EstimatedScoreDelta  = Math.Clamp(combo.CombinedScoreDelta, -30f, 30f),
                        RiskLevel            = combo.RiskLevel,
                        ScoredByMlModel      = combo.ScoredByMlModel,
                    });
                }
                return [.. multiAdvised];
            }
            // Fall through to single-change results if combos is empty
        }

        // ── Step 6: sort by estimated score delta descending, cap at MaxTopProposals ──
        // When ML scores are in use we rank by ML-predicted score delta for accuracy;
        // otherwise fall back to lap delta (heuristic) as before.
        if (useML)
            advised.Sort(static (a, b) => b.EstimatedScoreDelta.CompareTo(a.EstimatedScoreDelta));
        else
            advised.Sort(static (a, b) => b.EstimatedLapDeltaSec.CompareTo(a.EstimatedLapDeltaSec));

        if (advised.Count > MaxTopProposals)
            advised.RemoveRange(MaxTopProposals, advised.Count - MaxTopProposals);

        // ── Step 7: virtual 3-lap simulation + mode-weighted re-sort ──────────
        // Runs only when the caller supplies a non-null `simulator` sentinel object.
        if (simulator != null && advised.Count > 0)
        {
            // Simulate each of the top candidates.
            foreach (var ap in advised)
            {
                ap.VirtualSimulation = VirtualLapSimulator.Simulate(
                    in frame, scores, driverProfile, profile,
                    ap, predictor, rlEngine);
            }

            // Re-sort by composite score according to driving mode.
            // Sprint  : 60 % Lap1 + 25 % Lap2 + 15 % Lap3
            // Endurance: 20 % Lap1 + 45 % Lap2 + 35 % Lap3
            // Penalise candidates where only Lap1 benefits (classic Lap1-spike pattern).
            advised.Sort((a, b) =>
            {
                float sa = VirtualCompositeScore(a, CurrentMode);
                float sb = VirtualCompositeScore(b, CurrentMode);
                return sb.CompareTo(sa);   // descending
            });
        }

        return [.. advised];
    }

    // ── Virtual-simulation composite scorer ───────────────────────────────────

    /// <summary>
    /// Computes a single scalar ranking score from a candidate's
    /// <see cref="AdvisedProposal.VirtualSimulation"/> result using
    /// mode-specific lap weights.
    /// Penalises candidates with a Lap-1-only spike pattern by 30 %.
    /// Falls back to <see cref="AdvisedProposal.EstimatedScoreDelta"/> when
    /// <see cref="AdvisedProposal.VirtualSimulation"/> is <see langword="null"/>.
    /// </summary>
    private static float VirtualCompositeScore(AdvisedProposal ap, DrivingMode mode)
    {
        if (ap.VirtualSimulation is not { } sim)
            return ap.EstimatedScoreDelta;

        float composite = mode switch
        {
            DrivingMode.Sprint    => 0.60f * sim.Lap1DeltaSec
                                   + 0.25f * sim.Lap2DeltaSec
                                   + 0.15f * sim.Lap3DeltaSec,

            DrivingMode.Endurance => 0.20f * sim.Lap1DeltaSec
                                   + 0.45f * sim.Lap2DeltaSec
                                   + 0.35f * sim.Lap3DeltaSec,

            _                     => sim.Lap1DeltaSec,
        };

        // Penalise Lap1-only spike: Lap1 positive but both Lap2 and Lap3 negative.
        if (sim.Lap1DeltaSec > 0f && sim.Lap2DeltaSec < 0f && sim.Lap3DeltaSec < 0f)
            composite *= Lap1SpikePenaltyFactor;

        return composite;
    }

    // ── ML helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a template <see cref="ImpactTrainingSample"/> from the current
    /// telemetry context. Candidate-specific fields are populated separately via
    /// <see cref="CloneSampleTemplate"/>.
    /// </summary>
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

    /// <summary>
    /// Returns a copy of <paramref name="template"/> with the
    /// candidate-specific fields from <paramref name="proposal"/> set.
    /// When the section or parameter is not in the encoding dictionary the
    /// field is set to 0f, which is the "unknown category" sentinel used by
    /// both the training pipeline (<see cref="ImpactModelTrainer"/>) and the
    /// predictor, keeping training labels and inference inputs consistent.
    /// </summary>
    private static ImpactTrainingSample CloneSampleTemplate(
        ImpactTrainingSample template,
        Proposal             proposal)
    {
        // 0f = unknown category sentinel; 1-based codes are used for known entries.
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

    /// <summary>
    /// Heuristic score delta using <see cref="CurrentMode"/>-specific sub-score weights.
    /// Used as fallback when no model is loaded.
    /// </summary>
    private static float ComputeHeuristicScoreDelta(
        float balDelta, float tracDelta, float brkDelta,
        DrivingScores? scores)
    {
        // Mode-specific weights for the three heuristic channels.
        // Stability has no separate heuristic delta, so its weight is folded
        // into the endurance balance/traction reduction.
        float wBal, wTrac, wBrk;
        switch (CurrentMode)
        {
            case DrivingMode.Sprint:
                wBal = 0.35f; wTrac = 0.30f; wBrk = 0.20f;
                break;
            case DrivingMode.Endurance:
                wBal = 0.25f; wTrac = 0.25f; wBrk = 0.15f;
                break;
            default:
                wBal = 0.35f; wTrac = 0.25f; wBrk = 0.20f;
                break;
        }

        float scoreDelta = balDelta * wBal + tracDelta * wTrac + brkDelta * wBrk;
        if (scores != null)
        {
            if (balDelta  > 0 && scores.BalanceScore   < 60f) scoreDelta *= 1.2f;
            if (tracDelta > 0 && scores.TractionScore  < 60f) scoreDelta *= 1.2f;
            if (brkDelta  > 0 && scores.BrakeScore     < 60f) scoreDelta *= 1.2f;
        }
        return scoreDelta;
    }

    /// <summary>
    /// Updates the RL policy engine's Q-table after a completed A/B test, and
    /// optionally records the outcome in the <see cref="AdaptiveWeightEngine"/>
    /// so that blend weights can be dynamically adjusted.
    /// Call this once per <see cref="AbTestResult"/> when
    /// <see cref="RLPolicyEngine.IsEnabled"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="frame">
    /// The <see cref="FeatureFrame"/> that was current when <see cref="Advise"/> was called
    /// for the proposal under test (i.e. the state at action-selection time).
    /// </param>
    /// <param name="scores">Driving scores at the time the proposal was selected.</param>
    /// <param name="driverProfile">Driver profile at action-selection time (may be <see langword="null"/>).</param>
    /// <param name="testedProposal">The proposal that was A/B tested.</param>
    /// <param name="result">Completed A/B test result.</param>
    /// <param name="risk">Risk level of the tested proposal.</param>
    /// <param name="rlEngine">The engine to update.</param>
    /// <param name="weightEngine">
    /// Optional <see cref="AdaptiveWeightEngine"/>.
    /// When non-<see langword="null"/>, <see cref="AdaptiveWeightEngine.RecordAbTestResult"/>
    /// is called with the ML-predicted delta and computed reward so the engine
    /// can gradually adjust the three-way blend weights.
    /// </param>
    /// <param name="mlPredictedDelta">
    /// The ML (or heuristic) score delta that was predicted for
    /// <paramref name="testedProposal"/> at selection time.
    /// Used to compute the ML prediction error in <paramref name="weightEngine"/>.
    /// Defaults to <c>0f</c> when unknown.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="testedProposal"/>, <paramref name="result"/>, or
    /// <paramref name="rlEngine"/> is <see langword="null"/>.
    /// </exception>
    public static void NotifyAbTestResult(
        in FeatureFrame       frame,
        DrivingScores?        scores,
        DriverProfile?        driverProfile,
        Proposal              testedProposal,
        AbTestResult          result,
        RiskLevel             risk,
        RLPolicyEngine        rlEngine,
        AdaptiveWeightEngine? weightEngine     = null,
        float                 mlPredictedDelta = 0f)
    {
        if (testedProposal is null) throw new ArgumentNullException(nameof(testedProposal));
        if (result         is null) throw new ArgumentNullException(nameof(result));
        if (rlEngine       is null) throw new ArgumentNullException(nameof(rlEngine));

        var state  = RLPolicyEngine.BuildState(in frame, scores, driverProfile);
        var action = RLPolicyEngine.ActionFromProposal(testedProposal);

        // Retrieve the Q-value that was current before this update (for error tracking).
        float rlQValueBefore = rlEngine.GetQValue(in state, in action);

        float reward = rlEngine.ComputeReward(
            result, risk, driverProfile?.ConsistencyIndex ?? 1f);
        rlEngine.UpdateQ(in state, in action, reward);

        // Notify the adaptive weight engine when supplied.
        if (weightEngine != null)
        {
            // Approximate actual score delta from A/B test outcome.
            // A/B test results do not directly expose a "score delta" number, so
            // we approximate using result confidence as a magnitude proxy:
            //   +10 × confidence when the change improved the car (positive signal),
            //   −5  × confidence when it did not (smaller penalty reflects that a
            //         neutral/marginal outcome is less informative than a clear gain).
            float actualScoreDelta = result.Confidence * (result.Improved ? 10f : -5f);

            weightEngine.RecordAbTestResult(
                mlPredictedDelta: mlPredictedDelta,
                actualScoreDelta: actualScoreDelta,
                rlQValueUsed:     rlQValueBefore,
                actualReward:     reward,
                improved:         result.Improved);

            weightEngine.Save();
        }
    }

    /// <summary>
    /// Adjusts lap-delta, score-delta, and risk-penalty multipliers based on
    /// the driver's measured style from <paramref name="driverProfile"/>.
    /// All modifications are applied in-place via <see langword="ref"/> parameters.
    /// </summary>
    /// <remarks>
    /// Rules applied:
    /// <list type="bullet">
    ///   <item><b>Aggressive driver</b> (AggressivenessIndex &gt; 0.6) — risk penalties
    ///     are relaxed by up to 20 % (High → 0.74, Medium → 0.92) because an
    ///     aggressive driver can extract more benefit from edge-of-envelope changes.</item>
    ///   <item><b>Inconsistent driver</b> (ConsistencyIndex &lt; 0.5) — stability-related
    ///     proposals (ARB, SPRINGS, DAMPERS) receive a 20 % score boost; aggressive
    ///     balance changes (AERO, ALIGNMENT) are penalised by 15 %.</item>
    ///   <item><b>High brake aggression</b> (BrakeAggressionIndex &gt; 0.65) — brake-bias
    ///     suggestions (BRAKES:BRAKE_BIAS, BRAKES:BRAKE_POWER) are penalised by 15 %
    ///     because the driver's own behaviour already dominates the brake signal.</item>
    ///   <item><b>Positive balance bias</b> (PreferredBalanceBias &gt; 0.15) — rear-rotation
    ///     proposals (ARB:REAR, ELECTRONICS:DIFF_ACC, DAMPERS:BUMP_REAR) receive a
    ///     25 % score boost to match the driver's preferred handling feel.</item>
    /// </list>
    /// </remarks>
    private static void ApplyDriverProfileAdjustments(
        DriverProfile? driverProfile,
        string         section,
        string         parameter,
        ref float      lapDelta,
        ref float      scoreDelta,
        ref float      riskHighMult,
        ref float      riskMediumMult)
    {
        if (driverProfile is null) return;

        var key = $"{section}:{parameter}";

        // ── 1. Aggressive driver: relax risk penalty ──────────────────────────
        if (driverProfile.AggressivenessIndex > AggressivenessThreshold)
        {
            // Lerp: at aggr = AggressivenessThreshold → no change;
            //       at aggr = 1.0 → relax by MaxRiskRelaxAmount.
            float relaxFactor = (driverProfile.AggressivenessIndex - AggressivenessThreshold)
                                / AggressivenessRange;   // 0..1
            float relaxAmount = relaxFactor * MaxRiskRelaxAmount;
            riskHighMult   = Math.Min(riskHighMult   + relaxAmount, 1.0f);
            riskMediumMult = Math.Min(riskMediumMult + relaxAmount, 1.0f);
        }

        // ── 2. Inconsistent driver: boost stability, penalise aggressive balance ──
        if (driverProfile.ConsistencyIndex < ConsistencyThreshold)
        {
            // Stability sections
            if (string.Equals(section, "ARB",     StringComparison.OrdinalIgnoreCase) ||
                string.Equals(section, "SPRINGS",  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(section, "DAMPERS",  StringComparison.OrdinalIgnoreCase))
            {
                lapDelta   *= StabilityBoostMult;
                scoreDelta *= StabilityBoostMult;
            }
            // Aggressive balance sections
            if (string.Equals(section, "AERO",      StringComparison.OrdinalIgnoreCase) ||
                string.Equals(section, "ALIGNMENT",  StringComparison.OrdinalIgnoreCase))
            {
                lapDelta   *= AggressiveBalancePenaltyMult;
                scoreDelta *= AggressiveBalancePenaltyMult;
            }
        }

        // ── 3. High brake aggression: reduce brake-bias suggestion sensitivity ──
        if (driverProfile.BrakeAggressionIndex > BrakeAggressionThreshold &&
            (string.Equals(key, "BRAKES:BRAKE_BIAS",  StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "BRAKES:BRAKE_POWER", StringComparison.OrdinalIgnoreCase)))
        {
            lapDelta   *= BrakeSensitivityPenaltyMult;
            scoreDelta *= BrakeSensitivityPenaltyMult;
        }

        // ── 4. Positive balance bias: boost rear-rotation suggestions ─────────
        if (driverProfile.PreferredBalanceBias > RotationBiasThreshold &&
            (string.Equals(key, "ARB:REAR",                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "ELECTRONICS:DIFF_ACC",     StringComparison.OrdinalIgnoreCase) ||
             string.Equals(key, "DAMPERS:BUMP_REAR",        StringComparison.OrdinalIgnoreCase)))
        {
            // Scale boost with bias magnitude: at PreferredBalanceBias = +1.0 → MaxRotationBoost.
            float boostFactor = 1f + MaxRotationBoost
                                   * Math.Clamp(driverProfile.PreferredBalanceBias, 0f, 1f);
            lapDelta   *= boostFactor;
            scoreDelta *= boostFactor;
        }
    }

    /// <summary>
    /// Internal façade over <c>SimulationImpactEstimator</c> so that other
    /// types in the same assembly (e.g. <see cref="TelemetryViewModel"/>) can
    /// obtain heuristic impact estimates without duplicating the impact table.
    /// </summary>
    internal static (float balance, float traction, float brake, float lapDelta, RiskLevel risk)
        HeuristicEstimate(string section, string parameter, float featureIndex)
        => SimulationImpactEstimator.Estimate(section, parameter, featureIndex);

    // ── Corner enrichment ─────────────────────────────────────────────────────

    /// <summary>
    /// Score multiplier applied to a candidate whose virtual-simulation shows
    /// a Lap-1-only spike pattern (Lap1 positive but both Lap2 and Lap3 negative).
    /// Penalises changes that look good in the first lap but degrade over the stint.
    /// </summary>
    private const float Lap1SpikePenaltyFactor = 0.70f;

    /// <summary>AggressivenessIndex threshold above which risk penalties are relaxed.</summary>
    private const float AggressivenessThreshold = 0.60f;

    /// <summary>
    /// Width of the aggressiveness range used to lerp the risk-relaxation amount
    /// (from threshold to threshold + range = 1.0).
    /// </summary>
    private const float AggressivenessRange = 0.40f;

    /// <summary>Maximum risk-penalty relaxation applied to an extremely aggressive driver.</summary>
    private const float MaxRiskRelaxAmount = 0.20f;

    /// <summary>ConsistencyIndex below which stability is preferred over balance changes.</summary>
    private const float ConsistencyThreshold = 0.50f;

    /// <summary>Score/lap multiplier applied to stability proposals for inconsistent drivers.</summary>
    private const float StabilityBoostMult = 1.20f;

    /// <summary>Score/lap multiplier applied to aggressive-balance proposals for inconsistent drivers.</summary>
    private const float AggressiveBalancePenaltyMult = 0.85f;

    /// <summary>BrakeAggressionIndex above which brake-bias suggestion sensitivity is reduced.</summary>
    private const float BrakeAggressionThreshold = 0.65f;

    /// <summary>Score/lap multiplier applied to brake-bias/power proposals for highly aggressive brakers.</summary>
    private const float BrakeSensitivityPenaltyMult = 0.85f;

    /// <summary>PreferredBalanceBias above which rear-rotation proposals are boosted.</summary>
    private const float RotationBiasThreshold = 0.15f;

    /// <summary>Maximum score/lap boost applied to rear-rotation proposals (at PreferredBalanceBias = 1.0).</summary>
    private const float MaxRotationBoost = 0.25f;

    /// <summary>
    /// These supplement the aggregate-frame rules when corner-phase signals
    /// are more pronounced than the aggregate.
    /// </summary>
    private static void EnrichFromCorners(
        List<Proposal>              candidates,
        ReadOnlySpan<CornerSummary> corners,
        in FeatureFrame             frame)
    {
        if (corners.Length == 0) return;

        // Average phase-specific signals across the last corners
        float avgUsEntry = 0, avgUsMid = 0, avgUsExit = 0;
        float avgOsEntry = 0, avgOsExit = 0;
        float avgWheelspin = 0, avgLockup = 0;

        foreach (var c in corners)
        {
            avgUsEntry   += c.EntryFrame.UndersteerEntry;
            avgUsMid     += c.MidFrame.UndersteerMid;
            avgUsExit    += c.ExitFrame.UndersteerExit;
            avgOsEntry   += c.EntryFrame.OversteerEntry;
            avgOsExit    += c.ExitFrame.OversteerExit;
            avgWheelspin += c.ExitFrame.WheelspinRatioRear;
            avgLockup    += c.EntryFrame.LockupRatioFront;
        }

        float n = corners.Length;
        avgUsEntry   /= n; avgUsMid   /= n; avgUsExit  /= n;
        avgOsEntry   /= n; avgOsExit  /= n;
        avgWheelspin /= n; avgLockup  /= n;

        const float EnrichThreshold = 0.12f;

        // Only add a proposal if it's not already in the candidate list
        if (avgUsMid > EnrichThreshold && !ContainsKey(candidates, "SPRINGS", "FRONT_SPRING"))
            candidates.Add(Make("SPRINGS", "FRONT_SPRING", "-1",
                "Subviraje mid-corner persistente en curva — suavizar muelle delantero", avgUsMid));

        if (avgUsExit > EnrichThreshold && !ContainsKey(candidates, "AERO", "FRONT_WING"))
            candidates.Add(Make("AERO", "FRONT_WING", "-1",
                "Subviraje en salida persistente — reducir ala delantera", avgUsExit));

        if (avgOsExit > EnrichThreshold && !ContainsKey(candidates, "ELECTRONICS", "DIFF_ACC"))
            candidates.Add(Make("ELECTRONICS", "DIFF_ACC", "+2",
                "Sobreviraje en salida persistente — aumentar diferencial", avgOsExit));

        if (avgWheelspin > EnrichThreshold && !ContainsKey(candidates, "ELECTRONICS", "TRACTION_CONTROL"))
            candidates.Add(Make("ELECTRONICS", "TRACTION_CONTROL", "+1",
                "Wheelspin trasero en salida de curva", avgWheelspin));

        if (avgLockup > EnrichThreshold && !ContainsKey(candidates, "BRAKES", "BRAKE_BIAS"))
            candidates.Add(Make("BRAKES", "BRAKE_BIAS", "-1",
                "Bloqueo delantero recurrente — reducir reparto de frenos", avgLockup));
    }

    private static bool ContainsKey(List<Proposal> list, string section, string parameter)
    {
        foreach (var p in list)
            if (string.Equals(p.Section, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Parameter, parameter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static Proposal Make(string section, string parameter, string delta,
                                  string reason, float confidence)
        => new()
        {
            Section    = section,
            Parameter  = parameter,
            From       = string.Empty,
            To         = string.Empty,
            Delta      = delta,
            Reason     = reason,
            Confidence = confidence,
        };
}
