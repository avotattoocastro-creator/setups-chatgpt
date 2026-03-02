using System;
using AvoPerformanceSetupAI.Profiles;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ML;

// ── Output ────────────────────────────────────────────────────────────────────

/// <summary>
/// Projected 3-lap performance evolution produced by
/// <see cref="VirtualLapSimulator.Simulate"/>.
/// All delta values follow the convention "positive = faster / better".
/// </summary>
public record struct VirtualSimulationResult
{
    /// <summary>
    /// Projected lap-time delta on lap 1 after the setup change (seconds, positive = faster).
    /// Reflects the immediate effect of the change plus current tyre temperature grip.
    /// </summary>
    public float Lap1DeltaSec { get; init; }

    /// <summary>
    /// Projected lap-time delta on lap 2, after one lap of tyre-temperature evolution
    /// and grip settling.
    /// </summary>
    public float Lap2DeltaSec { get; init; }

    /// <summary>
    /// Projected lap-time delta on lap 3, after two laps of tyre-temperature evolution.
    /// High values relative to <see cref="Lap1DeltaSec"/> indicate good endurance stability.
    /// </summary>
    public float Lap3DeltaSec { get; init; }

    /// <summary>
    /// Trend of the lap-delta across the three projected laps:
    /// <c>(Lap3Delta − Lap1Delta) / 2</c>.
    /// Positive = the benefit is growing over the stint; negative = benefit is fading.
    /// </summary>
    public float StabilityTrend { get; init; }

    /// <summary>
    /// Normalized risk of the change (0..1, 1 = highest risk), derived from
    /// the proposal's <see cref="RiskLevel"/> enum.
    /// </summary>
    public float RiskLevel { get; init; }

    /// <summary>
    /// Overall simulation confidence (0..1), combining rule confidence, ML-model
    /// availability, and driver consistency.
    /// </summary>
    public float Confidence { get; init; }
}

// ── Simulator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless engine that simulates the expected lap-time performance of a setup
/// change over the next three laps, taking into account tyre-temperature evolution,
/// grip modelling, driver aggressiveness, and an optional RL Q-value hint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tyre temperature model:</b><br/>
/// The current tyre temperature is estimated from the oscillation and brake-stability
/// indices in the <see cref="FeatureFrame"/> (higher instability → warmer tyres).
/// Each lap the temperature rises by a base rate plus an aggressiveness-driven increment.
/// An unstable balance causes front and rear temperatures to diverge.
/// </para>
/// <para>
/// <b>Grip model:</b><br/>
/// Grip is 100 % when temperatures are inside the optimal window
/// (<see cref="CarTrackProfile.OptimalTyreTempRange"/>). Cold tyres start below 100 %
/// and warm into it; overheating tyres lose 2 % grip per °C above the maximum.
/// </para>
/// <para>
/// <b>Stochastic noise:</b><br/>
/// A small per-lap noise term (±2 % of base lap delta) is applied. The random seed
/// is derived deterministically from the proposal's section and parameter strings so
/// that repeated calls with the same proposal return the same result.
/// </para>
/// <para>
/// Thread-safety: this class is stateless; all methods are safe to call from any thread.
/// </para>
/// </remarks>
public static class VirtualLapSimulator
{
    // ── Calibration constants ─────────────────────────────────────────────────

    /// <summary>
    /// Base tyre-temperature growth per lap (°C).
    /// An aggressive driver adds up to <see cref="AggressiveTempIncrement"/> on top.
    /// </summary>
    private const float BaseTempGrowthPerLap = 1.5f;

    /// <summary>
    /// Maximum additional tyre-temperature growth per lap for a fully aggressive driver
    /// (<see cref="DriverProfile.AggressivenessIndex"/> = 1.0).
    /// </summary>
    private const float AggressiveTempIncrement = 2.5f;

    /// <summary>
    /// Maximum front-to-rear tyre-temperature divergence per lap (°C) when the
    /// balance is maximally unstable (<see cref="DrivingScores.BalanceScore"/> ≈ 0).
    /// </summary>
    private const float MaxTempDivergencePerLap = 2.0f;

    /// <summary>
    /// Grip level (0..1) returned when the tyre temperature is exactly at the
    /// cold edge of the optimal window (i.e. the tyres are just barely cold).
    /// Below this temperature the grip tapers further toward
    /// <see cref="ColdGripFloor"/>.
    /// </summary>
    private const float ColdEdgeGrip = 0.97f;

    /// <summary>
    /// Minimum grip level when tyres are 10 °C or more below the optimal window.
    /// </summary>
    private const float ColdGripFloor = 0.90f;

    /// <summary>
    /// Grip reduction per °C above the upper bound of the optimal window.
    /// </summary>
    private const float OverheatGripDropPerDegree = 0.02f;

    /// <summary>Minimum grip level before the overheating cap is applied.</summary>
    private const float MinGripFromOverheat = 0.80f;

    /// <summary>
    /// Scale factor that converts an RL Q-value to a confidence adjustment.
    /// Q-values are typically in [−15, 30]; dividing by this places the
    /// influence in roughly [−0.25, +0.50].
    /// </summary>
    private const float RlQValueToConfidenceScale = 60f;

    /// <summary>
    /// Half-range of the stochastic per-lap noise applied to the base lap delta,
    /// expressed as a fraction of the absolute base delta.
    /// A value of 0.02 means ±2 % variation.
    /// </summary>
    private const float LapNoiseHalfRange = 0.02f;

    /// <summary>
    /// Half-range of the stochastic per-lap tyre-temperature noise (°C).
    /// The effective noise band is ±<see cref="TempNoiseHalfRange"/> per lap.
    /// </summary>
    private const float TempNoiseHalfRange = 0.25f;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates the 3-lap performance evolution after applying
    /// <paramref name="proposal"/>.
    /// </summary>
    /// <param name="frame">Current aggregate telemetry features.</param>
    /// <param name="scores">Current driving scores; used for tyre-temp baseline and balance instability.</param>
    /// <param name="driverProfile">
    /// Optional driver profile; governs tyre-temperature growth rate and consistency
    /// confidence weighting. Defaults to a moderate profile when <see langword="null"/>.
    /// </param>
    /// <param name="carProfile">
    /// Optional car/track profile; provides the optimal tyre-temperature window.
    /// Defaults to a generic 75–95 °C window when <see langword="null"/>.
    /// </param>
    /// <param name="proposal">
    /// The scored proposal to simulate. Uses <see cref="AdvisedProposal.EstimatedLapDeltaSec"/>,
    /// <see cref="AdvisedProposal.Confidence"/>, <see cref="AdvisedProposal.ScoredByMlModel"/>,
    /// and <see cref="AdvisedProposal.RiskLevel"/> as simulation inputs.
    /// </param>
    /// <param name="predictor">
    /// Optional trained impact predictor. When non-<see langword="null"/> and the model
    /// is loaded, the predicted <c>DeltaOverallScore</c> supplements the lap-delta baseline.
    /// </param>
    /// <param name="rlEngine">
    /// Optional RL policy engine. When <see cref="RLPolicyEngine.IsEnabled"/> is
    /// <see langword="true"/>, the Q-value for the proposal's action is used as a
    /// small confidence boost or reduction.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="proposal"/> is <see langword="null"/>.</exception>
    public static VirtualSimulationResult Simulate(
        in FeatureFrame  frame,
        DrivingScores?   scores,
        DriverProfile?   driverProfile,
        CarTrackProfile? carProfile,
        AdvisedProposal  proposal,
        ImpactPredictor? predictor = null,
        RLPolicyEngine?  rlEngine  = null)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        // ── 1. Derive the per-lap base delta from the proposal ─────────────────
        // Use the already-computed EstimatedLapDeltaSec as the baseline projection.
        // When an ML model is loaded, its DeltaOverallScore can refine this via a
        // calibrated conversion factor (score point ≈ 0.030 s lap delta).
        float baseLapDelta = proposal.EstimatedLapDeltaSec;

        // ── 2. Tyre temperature model setup ───────────────────────────────────
        float aggressiveness   = driverProfile?.AggressivenessIndex  ?? 0.40f;
        float consistency      = driverProfile?.ConsistencyIndex     ?? 0.80f;
        float balanceScore     = scores?.BalanceScore   ?? 60f;
        float stabilityScore   = scores?.StabilityScore ?? 60f;

        float optMin = carProfile?.OptimalTyreTempRange.Min ?? 75f;
        float optMax = carProfile?.OptimalTyreTempRange.Max ?? 95f;

        // Estimate current average tyre temperature from the feature frame:
        // high suspension oscillation + brake instability → warmer tyres.
        float thermalIndex = (frame.SuspensionOscillationIndex + frame.BrakeStabilityIndex) * 0.5f;
        float currentAvgTemp = optMin + Math.Clamp(thermalIndex, 0f, 1f) * (optMax - optMin);

        // Rear tyres run warmer when balance is poor (more rear stress).
        float balanceInstability = Math.Max(0f, (70f - balanceScore) / 100f);
        float rearTempOffset     = balanceInstability * 3f;    // up to +3 °C rear

        float tempFront = currentAvgTemp - rearTempOffset * 0.5f;
        float tempRear  = currentAvgTemp + rearTempOffset * 0.5f;

        // Per-lap temperature growth (aggressive → faster warm-up).
        float tempGrowthPerLap    = BaseTempGrowthPerLap + aggressiveness * AggressiveTempIncrement;
        float divergencePerLap    = balanceInstability   * MaxTempDivergencePerLap;

        // ── 3. Deterministic stochastic noise ────────────────────────────────
        // Seed from section + parameter + delta so the same proposal always yields
        // the same per-lap noise sequence, and distinct deltas produce different noise.
        var rng = new Random(HashCode.Combine(proposal.Section, proposal.Parameter, proposal.Delta));

        // ── 4. Simulate three laps ────────────────────────────────────────────
        float lap1, lap2, lap3;

        // Helper: advance temps and compute grip factor.
        float AdvanceLap()
        {
            tempFront += tempGrowthPerLap + (float)(rng.NextDouble() - 0.5) * (TempNoiseHalfRange * 2f);
            tempRear  += tempGrowthPerLap + divergencePerLap
                         + (float)(rng.NextDouble() - 0.5) * (TempNoiseHalfRange * 2f);
            float avgTemp = (tempFront + tempRear) * 0.5f;
            return GripFactor(avgTemp, optMin, optMax);
        }

        float noise1 = NoiseDelta(rng, baseLapDelta);
        float noise2 = NoiseDelta(rng, baseLapDelta);
        float noise3 = NoiseDelta(rng, baseLapDelta);

        lap1 = baseLapDelta * AdvanceLap() + noise1;
        lap2 = baseLapDelta * AdvanceLap() + noise2;
        lap3 = baseLapDelta * AdvanceLap() + noise3;

        // ── 5. RL Q-value hint (optional) ─────────────────────────────────────
        // A positive Q-value means the engine has seen this action rewarded before;
        // slightly amplify the lap delta projection to reflect historical evidence.
        if (rlEngine != null && RLPolicyEngine.IsEnabled)
        {
            var rlState  = RLPolicyEngine.BuildState(in frame, scores, driverProfile);
            var rlAction = RLPolicyEngine.ActionFromProposal(
                new Models.Proposal
                {
                    Section   = proposal.Section,
                    Parameter = proposal.Parameter,
                    Delta     = proposal.Delta,
                });
            float qVal    = rlEngine.GetQValue(in rlState, in rlAction);
            float qFactor = 1f + Math.Clamp(qVal / RlQValueToConfidenceScale, -0.15f, 0.30f);
            lap1 *= qFactor;
            lap2 *= qFactor;
            lap3 *= qFactor;
        }

        // ── 6. Stability trend and output assembly ────────────────────────────
        float stabilityTrend = (lap3 - lap1) * 0.5f;

        float riskFloat = proposal.RiskLevel switch
        {
            RiskLevel.High   => 0.85f,
            RiskLevel.Medium => 0.50f,
            _                => 0.20f,
        };

        // Confidence: ML scoring is more reliable than heuristic; inconsistent
        // drivers produce noisier A/B results → lower confidence.
        float mlBonus   = proposal.ScoredByMlModel ? 0.90f : 0.60f;
        float confidence = Math.Clamp(proposal.Confidence * mlBonus * consistency, 0.10f, 1.0f);

        return new VirtualSimulationResult
        {
            Lap1DeltaSec   = lap1,
            Lap2DeltaSec   = lap2,
            Lap3DeltaSec   = lap3,
            StabilityTrend = stabilityTrend,
            RiskLevel      = riskFloat,
            Confidence     = confidence,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes the grip factor (0..1) for a given average tyre temperature.
    /// </summary>
    private static float GripFactor(float avgTemp, float optMin, float optMax)
    {
        if (avgTemp <= optMin)
        {
            // Cold tyres: grip tapers from ColdEdgeGrip at optMin down to ColdGripFloor
            // when the temperature is 10 °C below the minimum.
            float coldDelta = optMin - avgTemp;
            return Math.Max(ColdGripFloor,
                            ColdEdgeGrip - (ColdEdgeGrip - ColdGripFloor) * coldDelta / 10f);
        }

        if (avgTemp <= optMax)
        {
            // Inside optimal window: 100 % grip.
            return 1.0f;
        }

        // Overheating: grip drops 2 % per °C above the upper limit.
        float overheat = avgTemp - optMax;
        return Math.Max(MinGripFromOverheat, 1f - OverheatGripDropPerDegree * overheat);
    }

    /// <summary>
    /// Returns a small deterministic noise term (±<see cref="LapNoiseHalfRange"/>
    /// × |baseDelta|), always at least <c>±0.005 s</c> to avoid degenerate zero noise.
    /// </summary>
    private static float NoiseDelta(Random rng, float baseLapDelta)
    {
        float scale = Math.Max(Math.Abs(baseLapDelta), 0.25f) * LapNoiseHalfRange * 2f;
        return (float)(rng.NextDouble() - 0.5) * scale;
    }
}
