using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.Profiles;
using AvoPerformanceSetupAI.Reference;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Computes the six driver-style indices that populate a <see cref="DriverProfile"/>
/// from recent lap telemetry data and updates the profile using EMA smoothing.
/// </summary>
/// <remarks>
/// <para><b>Inputs</b></para>
/// <list type="bullet">
///   <item><b>Last-5-laps samples</b> — flat list of <see cref="TelemetrySample"/>
///     covering (up to) the last five completed laps.</item>
///   <item><b>Corner events</b> — recent <see cref="CornerSummary"/> objects from
///     <c>CornerDetector</c>. Used for entry-speed consistency and steering analysis.</item>
///   <item><b>LiveVsIdeal</b> (optional) — EMA-smoothed deltas from the reference-lap
///     comparator. When supplied they drive the brake, throttle, steering, and
///     yaw-gain indices; when absent those indices fall back to absolute signal
///     statistics.</item>
/// </list>
/// <para><b>EMA smoothing</b></para>
/// <para>
/// All indices are blended into the existing <see cref="DriverProfile"/> using
/// <see cref="EmaAlpha"/> so that a single unusual lap cannot abruptly change the
/// profile. Call <see cref="Analyze"/> once per completed lap, then persist the
/// updated profile with <see cref="DriverProfile.Save"/>.
/// </para>
/// <para>
/// Thread-safety: this class is stateless. All public methods are safe to call
/// from any thread simultaneously.
/// </para>
/// </remarks>
public static class DriverStyleAnalyzer
{
    // ── EMA constant ──────────────────────────────────────────────────────────

    /// <summary>
    /// EMA weight applied to the newly computed observation when blending into
    /// the existing profile value (<c>newValue = α * observed + (1−α) * existing</c>).
    /// 0.25 means roughly 25 % of the update comes from the latest lap.
    /// </summary>
    public const float EmaAlpha = 0.25f;

    // ── Index thresholds ──────────────────────────────────────────────────────

    /// <summary>
    /// Brake pedal position above which a sample is classified as a braking event
    /// for spike-frequency counting.
    /// </summary>
    private const float BrakeSpikeThreshold = 0.60f;

    /// <summary>
    /// Expected brake-spike rate (spikes per sample) for an aggressive driver.
    /// Used to normalise the spike count to 0..1.
    /// Typical range: 0.05–0.15 spikes/sample at 20 Hz with a 50 ms sample interval.
    /// </summary>
    private const float TypicalBrakeSpikeFrequency = 0.10f;

    /// <summary>
    /// Maximum realistic throttle variance for normalisation. A completely
    /// random 0..1 signal has variance 1/12 ≈ 0.083; a driver with maximum
    /// aggression producing a bimodal 0/1 distribution peaks near 0.25.
    /// </summary>
    private const float MaxThrottleVariance = 0.25f;

    /// <summary>
    /// Maximum expected steering angle (rad) used to normalise the peak-steering
    /// index. Corresponds to approximately 40° of steering lock, typical for
    /// a GT3 car at low-speed corners in Assetto Corsa.
    /// </summary>
    private const float MaxTypicalSteerAngleRad = 0.70f;

    /// <summary>
    /// Weight of the slope component when blending slope + LiveVsIdeal delta
    /// for brake/throttle/steering indices.
    /// </summary>
    private const float SlopeBlendWeight = 0.60f;

    /// <summary>
    /// Weight of the LiveVsIdeal delta component when blending with the slope.
    /// Must equal 1 − <see cref="SlopeBlendWeight"/>.
    /// </summary>
    private const float DeltaBlendWeight = 0.40f;

    /// <summary>
    /// Scale factor applied to <c>DeltaBrake</c> / <c>DeltaThrottle</c>
    /// when normalising to 0..1. LiveVsIdeal deltas are in roughly 0..0.5 range,
    /// so ×2 maps them to 0..1.
    /// </summary>
    private const float DeltaBrakeThrottleScale = 2.0f;

    /// <summary>
    /// Scale factor applied to <c>|DeltaSteer|</c> when normalising steering aggression.
    /// LiveVsIdeal steer deltas are roughly 0..0.35 rad, so dividing by 0.35 maps them
    /// to 0..1. Expressed as the divisor.
    /// </summary>
    private const float DeltaSteerNormDivisor = 0.35f;

    /// <summary>
    /// Scale factor applied to <c>DeltaYawGain</c> when normalising the balance-bias
    /// from LiveVsIdeal data. Typical range ±0.5 → ×2 maps to ±1.
    /// </summary>
    private const float DeltaYawGainScale = 2.0f;

    /// <summary>
    /// Scale factor applied to the raw (OversteerExit − UndersteerEntry) corner-frame
    /// delta when LiveVsIdeal is unavailable. Raw deltas are in ≈ 0..0.33 range;
    /// ×3 maps them to ≈ 0..1.
    /// </summary>
    private const float CornerBiasScale = 3.0f;

    /// <summary>
    /// Lap-position bucket width used to group corners with the same track position
    /// for consistency analysis.
    /// </summary>
    private const float ConsistencyBucketWidth = 0.03f;

    /// <summary>
    /// Minimum number of training samples required before multi-parameter
    /// optimization is allowed. Below this threshold
    /// <see cref="EnableMultiParameterOptimization"/> is automatically treated
    /// as <see langword="false"/> to avoid combining unreliable predictions.
    /// </summary>
    private const float MaxConsistencyVarianceKmh = 15f;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Analyses the supplied telemetry and corner data, computes updated
    /// driver-style index observations, and blends them into
    /// <paramref name="profile"/> using EMA smoothing.
    /// </summary>
    /// <param name="lapSamples">
    /// Flat collection of <see cref="TelemetrySample"/> covering the last (up to 5)
    /// completed laps. Order is irrelevant; samples are processed in whatever order
    /// they appear.
    /// </param>
    /// <param name="corners">
    /// Recent <see cref="CornerSummary"/> objects, ordered chronologically.
    /// A minimum of 2 distinct corners is needed to compute consistency.
    /// </param>
    /// <param name="profile">
    /// Driver profile to update in-place. All fields are blended via EMA;
    /// the caller is responsible for persisting the profile afterwards.
    /// </param>
    /// <param name="liveVsIdeal">
    /// Optional most-recent <see cref="LiveVsIdealFrame"/> from the reference-lap
    /// comparator. When non-<see langword="null"/>, the delta fields improve
    /// accuracy of brake/throttle/steering/yaw-gain indices.
    /// </param>
    public static void Analyze(
        IReadOnlyList<TelemetrySample> lapSamples,
        IReadOnlyList<CornerSummary>   corners,
        DriverProfile                  profile,
        LiveVsIdealFrame?              liveVsIdeal = null)
    {
        if (lapSamples is null)  throw new ArgumentNullException(nameof(lapSamples));
        if (corners    is null)  throw new ArgumentNullException(nameof(corners));
        if (profile    is null)  throw new ArgumentNullException(nameof(profile));

        if (lapSamples.Count == 0) return;   // nothing to compute

        // ── Compute each index from the raw data ──────────────────────────────

        float aggr = ComputeAggressivenessIndex(lapSamples);
        float brk  = ComputeBrakeAggressionIndex(lapSamples, liveVsIdeal);
        float thr  = ComputeThrottleAggressionIndex(lapSamples, liveVsIdeal);
        float str  = ComputeSteeringAggressionIndex(lapSamples, liveVsIdeal);
        float con  = ComputeConsistencyIndex(corners);
        float bal  = ComputePreferredBalanceBias(corners, liveVsIdeal);

        // ── Blend into profile with EMA ───────────────────────────────────────

        profile.AggressivenessIndex      = Ema(profile.AggressivenessIndex,      aggr);
        profile.BrakeAggressionIndex     = Ema(profile.BrakeAggressionIndex,      brk);
        profile.ThrottleAggressionIndex  = Ema(profile.ThrottleAggressionIndex,   thr);
        profile.SteeringAggressionIndex  = Ema(profile.SteeringAggressionIndex,   str);
        profile.ConsistencyIndex         = Ema(profile.ConsistencyIndex,          con);
        profile.PreferredBalanceBias     = EmaUnclamped(profile.PreferredBalanceBias, bal);
    }

    // ── Index computations ────────────────────────────────────────────────────

    /// <summary>
    /// AggressivenessIndex = 0.5 × normalised throttle variance
    ///                     + 0.5 × normalised brake-spike frequency.
    /// </summary>
    private static float ComputeAggressivenessIndex(IReadOnlyList<TelemetrySample> samples)
    {
        // — Throttle variance —
        float throttleSum  = 0f;
        float throttleSum2 = 0f;
        int   brakeSpikeCount   = 0;
        bool  prevBrakeHigh     = false;

        foreach (var s in samples)
        {
            throttleSum  += s.Throttle;
            throttleSum2 += s.Throttle * s.Throttle;

            bool curHigh = s.Brake >= BrakeSpikeThreshold;
            if (curHigh && !prevBrakeHigh) brakeSpikeCount++;
            prevBrakeHigh = curHigh;
        }

        int n = samples.Count;
        float mean = throttleSum / n;
        float variance = throttleSum2 / n - mean * mean;
        float normThrottleVar = Math.Clamp(variance / MaxThrottleVariance, 0f, 1f);

        // Brake spikes per sample, normalised against TypicalBrakeSpikeFrequency.
        float normBrakeSpike = Math.Clamp(brakeSpikeCount / (n * TypicalBrakeSpikeFrequency), 0f, 1f);

        return Math.Clamp(0.50f * normThrottleVar + 0.50f * normBrakeSpike, 0f, 1f);
    }

    /// <summary>
    /// BrakeAggressionIndex = normalised brake-onset slope.
    /// When LiveVsIdeal is available, the signed DeltaBrake is also factored in.
    /// </summary>
    private static float ComputeBrakeAggressionIndex(
        IReadOnlyList<TelemetrySample> samples,
        LiveVsIdealFrame?              liveVsIdeal)
    {
        // Find the maximum positive difference between consecutive brake values
        // (i.e. steepest onset) and normalise it.
        float maxOnset = 0f;
        for (int i = 1; i < samples.Count; i++)
        {
            float onset = samples[i].Brake - samples[i - 1].Brake;
            if (onset > maxOnset) maxOnset = onset;
        }
        // A full 0→1 transition in one sample (≈50 ms) = 1.0 normalised.
        float fromSlope = Math.Clamp(maxOnset, 0f, 1f);

        if (liveVsIdeal is null) return fromSlope;

        // Positive DeltaBrake means driver brakes harder than ideal → more aggressive.
        float fromDelta = Math.Clamp(liveVsIdeal.DeltaBrake * DeltaBrakeThrottleScale, 0f, 1f);
        return Math.Clamp(SlopeBlendWeight * fromSlope + DeltaBlendWeight * fromDelta, 0f, 1f);
    }

    /// <summary>
    /// ThrottleAggressionIndex = normalised throttle-ramp slope on corner exit.
    /// When LiveVsIdeal is available, positive DeltaThrottle raises the index.
    /// </summary>
    private static float ComputeThrottleAggressionIndex(
        IReadOnlyList<TelemetrySample> samples,
        LiveVsIdealFrame?              liveVsIdeal)
    {
        float maxRamp = 0f;
        for (int i = 1; i < samples.Count; i++)
        {
            float ramp = samples[i].Throttle - samples[i - 1].Throttle;
            if (ramp > maxRamp) maxRamp = ramp;
        }
        float fromSlope = Math.Clamp(maxRamp, 0f, 1f);

        if (liveVsIdeal is null) return fromSlope;

        float fromDelta = Math.Clamp(liveVsIdeal.DeltaThrottle * DeltaBrakeThrottleScale, 0f, 1f);
        return Math.Clamp(SlopeBlendWeight * fromSlope + DeltaBlendWeight * fromDelta, 0f, 1f);
    }

    /// <summary>
    /// SteeringAggressionIndex = normalised peak steering angle across all samples.
    /// When LiveVsIdeal is available, DeltaSteer (excess lock vs ideal) is factored in.
    /// </summary>
    private static float ComputeSteeringAggressionIndex(
        IReadOnlyList<TelemetrySample> samples,
        LiveVsIdealFrame?              liveVsIdeal)
    {
        float peakAbs = 0f;
        foreach (var s in samples)
        {
            float abs = Math.Abs(s.SteerAngle);
            if (abs > peakAbs) peakAbs = abs;
        }
        float fromPeak = Math.Clamp(peakAbs / MaxTypicalSteerAngleRad, 0f, 1f);

        if (liveVsIdeal is null) return fromPeak;

        // Excess steering lock vs ideal (positive = more lock than ideal → more aggressive)
        float fromDelta = Math.Clamp(Math.Abs(liveVsIdeal.DeltaSteer) / DeltaSteerNormDivisor, 0f, 1f);
        return Math.Clamp(SlopeBlendWeight * fromPeak + DeltaBlendWeight * fromDelta, 0f, 1f);
    }

    /// <summary>
    /// ConsistencyIndex = 1 − normalised entry-speed variance across same corners.
    /// Groups corners by their <see cref="CornerSummary.LapPos"/> bucket (±3 %) and
    /// computes the coefficient of variation of entry speeds in each group.
    /// </summary>
    private static float ComputeConsistencyIndex(IReadOnlyList<CornerSummary> corners)
    {
        if (corners.Count < 2) return 1f;   // not enough data — assume consistent

        // Build lap-position buckets (bucket width = ConsistencyBucketWidth)
        var buckets = new Dictionary<int, List<float>>();

        foreach (var c in corners)
        {
            // Entry speed = speed at first sample of the entry phase.
            // We use UndersteerEntry as a proxy for entry intensity; actual
            // entry speed would require raw samples (not available here).
            // Instead, approximate from CornerSummary.PeakLateralG, which
            // correlates with corner entry speed.
            var key = (int)(c.LapPos / ConsistencyBucketWidth);
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<float>();
            list.Add(c.PeakLateralG);
        }

        // Average normalised std-dev across all buckets with ≥ 2 samples
        float totalCv = 0f;
        int   groups  = 0;
        foreach (var (_, speeds) in buckets)
        {
            if (speeds.Count < 2) continue;

            float mean = 0f;
            foreach (var v in speeds) mean += v;
            mean /= speeds.Count;

            if (mean <= 0f) continue;

            float variance = 0f;
            foreach (var v in speeds) variance += (v - mean) * (v - mean);
            variance /= speeds.Count;

            float stdDev = MathF.Sqrt(variance);
            // Coefficient of variation (CV); cap at 1 to avoid huge outlier effect
            totalCv += Math.Clamp(stdDev / mean, 0f, 1f);
            groups++;
        }

        if (groups == 0) return 1f;

        float avgCv = totalCv / groups;
        return Math.Clamp(1f - avgCv, 0f, 1f);
    }

    /// <summary>
    /// PreferredBalanceBias = average yaw-gain deviation across corners.
    /// Positive → driver prefers (or induces) rear rotation.
    /// Negative → driver prefers front-grip / understeer.
    /// When LiveVsIdeal is available, the signed DeltaYawGain from the comparator
    /// is the primary signal; otherwise the average OversteerExit − UndersteerEntry
    /// delta from the corner frames is used as a proxy.
    /// Result is clamped to [−1, +1].
    /// </summary>
    private static float ComputePreferredBalanceBias(
        IReadOnlyList<CornerSummary> corners,
        LiveVsIdealFrame?            liveVsIdeal)
    {
        if (liveVsIdeal != null)
        {
            // DeltaYawGain is already EMA-smoothed by the comparator.
            // Normalise with DeltaYawGainScale (÷2 maps ±0.5 → ±1).
            return Math.Clamp(liveVsIdeal.DeltaYawGain * DeltaYawGainScale, -1f, 1f);
        }

        if (corners.Count == 0) return 0f;

        float biaSum = 0f;
        foreach (var c in corners)
        {
            // Positive = more oversteer exit than understeer entry → rotation bias
            float rotation = c.ExitFrame.OversteerExit - c.EntryFrame.UndersteerEntry;
            biaSum += rotation;
        }
        float avgBias = biaSum / corners.Count;
        return Math.Clamp(avgBias * CornerBiasScale, -1f, 1f);
    }

    // ── EMA helpers ───────────────────────────────────────────────────────────

    /// <summary>EMA blend for a 0..1 clamped index.</summary>
    private static float Ema(float existing, float observed)
        => Math.Clamp(EmaAlpha * observed + (1f - EmaAlpha) * existing, 0f, 1f);

    /// <summary>EMA blend without clamping (used for PreferredBalanceBias which is −1..+1).</summary>
    private static float EmaUnclamped(float existing, float observed)
        => Math.Clamp(EmaAlpha * observed + (1f - EmaAlpha) * existing, -1f, 1f);
}
