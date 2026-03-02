using System;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Utility methods for computing yaw-based vehicle balance metrics from raw
/// telemetry values.
/// </summary>
/// <remarks>
/// <para><b>Yaw gain</b> compares the car's actual yaw rate to the rate that
/// would be expected from a neutral (linear) single-track model at the same
/// speed and steering angle. A gain near 1.0 indicates neutral balance;
/// below 0.8 indicates understeer; above 1.2 indicates oversteer.</para>
/// <para><b>Balance index</b> encodes both understeer and oversteer as a single
/// signed float in [−1, +1]:</para>
/// <list type="bullet">
///   <item>Negative values → understeer index (magnitude 0..1).</item>
///   <item>Positive values → oversteer index (magnitude 0..1).</item>
///   <item>0 → neutral balance.</item>
/// </list>
/// </remarks>
public static class BalanceMetrics
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Small epsilon added to the expected yaw denominator to prevent
    /// division by zero when the car is stationary or steering is near zero.
    /// </summary>
    private const float Epsilon = 1e-4f;

    /// <summary>
    /// Steering angles with an absolute value below this threshold (rad) are
    /// treated as zero — the car is effectively going straight.
    /// </summary>
    private const float SteerDeadband = 1e-3f;

    /// <summary>
    /// AC steering angles are already in radians; if an integrator passes
    /// values in degrees (|steerAngle| > π) they are converted automatically.
    /// Threshold chosen so that a realistic maximum lock of ~540° in degrees
    /// is safely detected.
    /// </summary>
    private const float DegreeDetectionThreshold = (float)Math.PI;

    /// <summary>
    /// Yaw gain below this value is classified as understeer.
    /// </summary>
    public const float UndersteerThreshold = 0.8f;

    /// <summary>
    /// Yaw gain above this value is classified as oversteer.
    /// </summary>
    public const float OversteerThreshold = 1.2f;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the yaw gain: the ratio of actual yaw rate to the yaw rate
    /// expected from a linear single-track model.
    /// </summary>
    /// <param name="yawRate">
    /// Body yaw rate in rad/s (from <see cref="TelemetrySample.YawRate"/>).
    /// </param>
    /// <param name="steeringAngle">
    /// Steering wheel angle in radians (AC convention: positive = left).
    /// Angles with |value| > π are automatically converted from degrees.
    /// </param>
    /// <param name="speedKmh">Vehicle speed in km/h.</param>
    /// <returns>
    /// Yaw gain clamped to [−2, +2].
    /// A value near 1.0 is neutral; &lt; 0.8 indicates understeer;
    /// &gt; 1.2 indicates oversteer.
    /// </returns>
    public static float ComputeYawGain(float yawRate, float steeringAngle, float speedKmh)
    {
        // Normalise steering angle to radians if the caller supplied degrees
        var steerRad = Math.Abs(steeringAngle) > DegreeDetectionThreshold
            ? steeringAngle * (float)(Math.PI / 180.0)
            : steeringAngle;

        // Speed factor: linear proxy for the neutral single-track yaw gain
        var speedFactor = speedKmh / 100f;

        // Expected yaw rate from a neutral model (zero when steering is in deadband)
        var steerEffective = Math.Abs(steerRad) < SteerDeadband ? 0f : steerRad;
        var expectedYaw = steerEffective * speedFactor;

        // Yaw gain ratio — use |expectedYaw| + epsilon to prevent division by zero
        var yawGain = yawRate / (Math.Abs(expectedYaw) + Epsilon);

        return Math.Clamp(yawGain, -2f, 2f);
    }

    /// <summary>
    /// Converts a <paramref name="yawGain"/> value (from
    /// <see cref="ComputeYawGain"/>) into a signed balance index.
    /// </summary>
    /// <param name="yawGain">Yaw gain clamped to [−2, +2].</param>
    /// <returns>
    /// A signed float in [−1, +1]:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Negative</b> — UndersteerIndex (magnitude 0..1). Mapped linearly
    ///     from yawGain 0.8 → 0 down to yawGain 0.0 (or below) → −1.
    ///   </item>
    ///   <item>
    ///     <b>Positive</b> — OversteerIndex (magnitude 0..1). Mapped linearly
    ///     from yawGain 1.2 → 0 up to yawGain 2.0 (or above) → +1.
    ///   </item>
    ///   <item>
    ///     <b>0</b> — Neutral balance (yawGain in [0.8, 1.2]).
    ///   </item>
    /// </list>
    /// </returns>
    public static float ComputeBalanceIndex(float yawGain)
    {
        if (yawGain < UndersteerThreshold)
        {
            // UndersteerIndex: 0 at the threshold, 1 when yawGain ≤ 0
            var understeerIndex = Math.Clamp(
                (UndersteerThreshold - yawGain) / UndersteerThreshold, 0f, 1f);
            return -understeerIndex;
        }

        if (yawGain > OversteerThreshold)
        {
            // OversteerIndex: 0 at the threshold, 1 when yawGain ≥ 2
            var oversteerIndex = Math.Clamp(
                (yawGain - OversteerThreshold) / (2f - OversteerThreshold), 0f, 1f);
            return oversteerIndex;
        }

        // Neutral zone: [UndersteerThreshold, OversteerThreshold]
        return 0f;
    }
}
