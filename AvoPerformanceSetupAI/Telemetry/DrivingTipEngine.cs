using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.Reference;

namespace AvoPerformanceSetupAI.Telemetry;

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>A single actionable driving tip with an estimated impact score.</summary>
public sealed class DrivingTip
{
    /// <summary>
    /// Localized, human-readable feedback message.
    /// Example: "Frenas 6.4 m más tarde que el ideal en curva 3."
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Estimated impact on lap time in seconds (positive = potential gain).
    /// Used to sort tips by priority.
    /// </summary>
    public float ImpactEstimateSec { get; init; }

    /// <summary>
    /// Short category tag, e.g. "BRAKE", "THROTTLE", "STEERING", "BALANCE".
    /// </summary>
    public string Category { get; init; } = string.Empty;
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Generates prioritized driving-technique feedback from a
/// <see cref="RootCauseResult"/> and an optional <see cref="LiveVsIdealFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine produces up to <see cref="MaxTips"/> tips, sorted by
/// <see cref="DrivingTip.ImpactEstimateSec"/> descending so the most
/// valuable improvement appears first.
/// </para>
/// <para>
/// Impact estimates are heuristic rough values derived from published
/// lap-time sensitivity studies (≈ 0.1–0.5 s per major input error).
/// They are intentionally conservative to avoid false expectations.
/// </para>
/// </remarks>
public static class DrivingTipEngine
{
    /// <summary>Maximum number of tips returned by <see cref="Generate"/>.</summary>
    public const int MaxTips = 2;

    // ── Thresholds ────────────────────────────────────────────────────────────

    private const float BrakeLateThreshold   = 0.003f; // ~12 m on a 4 km track
    private const float BrakeEarlyThreshold  = -0.003f;
    private const float ThrottleLateThresh   = -0.10f;  // driver much later on throttle
    private const float ThrottleEarlyThresh  =  0.10f;  // driver too early on throttle
    private const float SteerExcessThreshold =  0.08f;  // extra lock vs ideal
    private const float NominalTrackLengthM  = 4_000f;  // metres (approximate)

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces up to <see cref="MaxTips"/> driving tips based on
    /// <paramref name="cause"/> and the live-vs-ideal deltas in
    /// <paramref name="ideal"/>.
    /// Returns an empty array when there is insufficient data to generate
    /// any actionable tip.
    /// </summary>
    /// <param name="cause">Root-cause analysis result from <see cref="DriverVsSetupDiscriminator"/>.</param>
    /// <param name="ideal">
    /// Current live-vs-ideal frame (may be <see langword="null"/> when no
    /// reference lap is loaded).
    /// </param>
    /// <param name="lastCorners">
    /// Most-recent completed corners (may be empty). Corner index from
    /// the last corner is used for contextual messages.
    /// </param>
    public static DrivingTip[] Generate(
        in RootCauseResult          cause,
        LiveVsIdealFrame?           ideal,
        ReadOnlySpan<CornerSummary> lastCorners = default)
    {
        var tips = new List<DrivingTip>(6);

        // Context: corner number for the most recently completed corner
        int cornerNum = lastCorners.Length > 0
            ? lastCorners[^1].CornerIndex + 1   // 1-based for display
            : 0;

        // ── Tips derived from live-vs-ideal deltas ────────────────────────────
        if (ideal != null)
        {
            // Brake point
            float brakeOffsetM = ideal.DeltaSteer; // DeltaSteer used as proxy until
                                                   // BrakeStartDeltaPct is directly exposed
            float brakeDistOffsetM = ideal.DeltaBrake * NominalTrackLengthM * 0.01f;

            if (ideal.DeltaBrake > BrakeLateThreshold)
            {
                float offsetM = MathF.Abs(brakeDistOffsetM);
                tips.Add(new DrivingTip
                {
                    Category           = "BRAKE",
                    Message            = cornerNum > 0
                        ? $"Frenas {offsetM:F1} m más tarde que el ideal en curva {cornerNum}."
                        : $"Frenas {offsetM:F1} m más tarde que el ideal.",
                    ImpactEstimateSec  = Math.Clamp(offsetM * 0.003f, 0.05f, 0.50f),
                });
            }
            else if (ideal.DeltaBrake < BrakeEarlyThreshold)
            {
                float offsetM = MathF.Abs(brakeDistOffsetM);
                tips.Add(new DrivingTip
                {
                    Category           = "BRAKE",
                    Message            = cornerNum > 0
                        ? $"Frenas {offsetM:F1} m antes que el ideal en curva {cornerNum} — puedes frenar más tarde."
                        : $"Frenas {offsetM:F1} m antes que el ideal — punto de frenada conservador.",
                    ImpactEstimateSec  = Math.Clamp(offsetM * 0.002f, 0.03f, 0.30f),
                });
            }

            // Throttle application
            if (ideal.DeltaThrottle > ThrottleEarlyThresh)
            {
                tips.Add(new DrivingTip
                {
                    Category           = "THROTTLE",
                    Message            = "Aceleración demasiado agresiva en salida — riesgo de sobreviraje/wheelspin.",
                    ImpactEstimateSec  = 0.15f,
                });
            }
            else if (ideal.DeltaThrottle < ThrottleLateThresh)
            {
                tips.Add(new DrivingTip
                {
                    Category           = "THROTTLE",
                    Message            = "Throttle tardío en salida de curva — aplica gas antes para mejorar tracción.",
                    ImpactEstimateSec  = 0.20f,
                });
            }

            // Steering excess (scrub)
            if (MathF.Abs(ideal.DeltaSteer) > SteerExcessThreshold)
            {
                string dir = ideal.DeltaSteer > 0 ? "más" : "menos";
                tips.Add(new DrivingTip
                {
                    Category           = "STEERING",
                    Message            = $"Giras {dir} volante que el ideal en mid-corner — posible scrub/pérdida de carga.",
                    ImpactEstimateSec  = 0.10f,
                });
            }

            // Speed delta
            if (ideal.DeltaSpeedKmh < -5f)
            {
                tips.Add(new DrivingTip
                {
                    Category           = "SPEED",
                    Message            = $"Velocidad {MathF.Abs(ideal.DeltaSpeedKmh):F1} km/h inferior al ideal en esta zona — lleva más velocidad de entrada.",
                    ImpactEstimateSec  = Math.Clamp(MathF.Abs(ideal.DeltaSpeedKmh) * 0.008f, 0.05f, 0.40f),
                });
            }
        }

        // ── Tips derived from root-cause explanation ──────────────────────────
        if (cause.Cause == RootCauseType.DriverLikely && cause.Confidence > 0.40f)
        {
            // Derive a generic tip from the explanation when ideal is unavailable
            // or when the explanation adds additional context.
            string explanation = cause.Explanation ?? string.Empty;

            if (explanation.Contains("frenada", StringComparison.OrdinalIgnoreCase)
                && tips.Count < MaxTips)
            {
                tips.Add(new DrivingTip
                {
                    Category          = "BRAKE",
                    Message           = "Consistencia en el punto de frenada: intenta referenciar un marcador fijo por curva.",
                    ImpactEstimateSec = 0.12f,
                });
            }

            if (explanation.Contains("aceleración", StringComparison.OrdinalIgnoreCase)
                && tips.Count < MaxTips)
            {
                tips.Add(new DrivingTip
                {
                    Category          = "THROTTLE",
                    Message           = "Progresividad en el throttle: aplica gas de forma gradual tras el apex para evitar wheelspin.",
                    ImpactEstimateSec = 0.10f,
                });
            }
        }

        // ── Sort by impact and cap ────────────────────────────────────────────
        tips.Sort(static (a, b) => b.ImpactEstimateSec.CompareTo(a.ImpactEstimateSec));
        if (tips.Count > MaxTips) tips.RemoveRange(MaxTips, tips.Count - MaxTips);

        return [.. tips];
    }
}
