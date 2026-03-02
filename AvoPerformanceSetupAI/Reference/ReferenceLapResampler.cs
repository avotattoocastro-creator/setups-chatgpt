using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Reference;

/// <summary>
/// Converts a variable-rate, distance-tagged raw sample stream into a fixed
/// <see cref="GridSize"/>-point grid evenly spaced by
/// <see cref="ReferenceLapSample.LapDistPct"/> (0..1), using per-channel
/// linear interpolation.
/// </summary>
/// <remarks>
/// All interpolation is strictly <b>distance-based</b> — LapDistPct is the
/// sole alignment axis.  Time information is never used.
/// </remarks>
public static class ReferenceLapResampler
{
    /// <summary>
    /// Number of evenly-spaced output grid points.
    /// Grid positions: 0/(GridSize−1), 1/(GridSize−1), … (GridSize−1)/(GridSize−1) = 1.0.
    /// </summary>
    public const int GridSize = 1000;

    /// <summary>
    /// Resamples <paramref name="rawSamples"/> onto a fixed <see cref="GridSize"/>-point
    /// LapDistPct grid using per-channel linear interpolation.
    /// </summary>
    /// <param name="rawSamples">
    /// Raw samples in any order; sorted internally by
    /// <see cref="ReferenceLapSample.LapDistPct"/> before interpolation.
    /// Must contain at least 2 samples with distinct LapDistPct values.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="rawSamples"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Fewer than 2 samples, or all samples share the same LapDistPct.
    /// </exception>
    public static ReferenceLapSample[] Resample(IReadOnlyList<ReferenceLapSample> rawSamples)
    {
        if (rawSamples is null) throw new ArgumentNullException(nameof(rawSamples));

        // Sort ascending by LapDistPct
        var sorted = new List<ReferenceLapSample>(rawSamples);
        sorted.Sort(static (a, b) => a.LapDistPct.CompareTo(b.LapDistPct));

        if (sorted.Count < 2 ||
            MathF.Abs(sorted[^1].LapDistPct - sorted[0].LapDistPct) < 1e-6f)
            throw new ArgumentException(
                "At least 2 raw samples with distinct LapDistPct values are required.",
                nameof(rawSamples));

        var output = new ReferenceLapSample[GridSize];
        for (int i = 0; i < GridSize; i++)
        {
            // Target distance for this grid slot (0.0 … 1.0)
            var targetPct = i / (float)(GridSize - 1);

            // Find the surrounding pair in the sorted list
            int lo = FindFloorIndex(sorted, targetPct);
            int hi = Math.Min(lo + 1, sorted.Count - 1);

            var s0 = sorted[lo];
            var s1 = sorted[hi];

            // Interpolation weight in [0, 1]
            float t = 0f;
            var span = s1.LapDistPct - s0.LapDistPct;
            if (span > 1e-6f)
                t = (targetPct - s0.LapDistPct) / span;

            output[i] = Lerp(s0, s1, t, targetPct);
        }

        return output;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the index of the last element in <paramref name="sorted"/>
    /// whose LapDistPct ≤ <paramref name="pct"/>.
    /// Returns 0 when every element is above <paramref name="pct"/>.
    /// </summary>
    private static int FindFloorIndex(List<ReferenceLapSample> sorted, float pct)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (sorted[mid].LapDistPct <= pct)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Linearly interpolates between <paramref name="a"/> and <paramref name="b"/>
    /// at weight <paramref name="t"/> (0 = full a, 1 = full b).
    /// Integer channels (Gear, Rpm) are rounded to the nearest integer.
    /// </summary>
    private static ReferenceLapSample Lerp(
        ReferenceLapSample a,
        ReferenceLapSample b,
        float t,
        float distPct)
    {
        var it = 1f - t;
        return new ReferenceLapSample
        {
            LapDistPct        = distPct,
            SpeedKmh          = it * a.SpeedKmh          + t * b.SpeedKmh,
            Throttle          = it * a.Throttle          + t * b.Throttle,
            Brake             = it * a.Brake             + t * b.Brake,
            Steering          = it * a.Steering          + t * b.Steering,
            Gear              = (int)MathF.Round(it * a.Gear + t * b.Gear),
            Rpm               = (int)MathF.Round(it * a.Rpm  + t * b.Rpm),
            LatG              = it * a.LatG              + t * b.LatG,
            LongG             = it * a.LongG             + t * b.LongG,
            YawGain           = it * a.YawGain           + t * b.YawGain,
            SlipAngleFrontAvg = it * a.SlipAngleFrontAvg + t * b.SlipAngleFrontAvg,
            SlipAngleRearAvg  = it * a.SlipAngleRearAvg  + t * b.SlipAngleRearAvg,
            WheelSlipRearAvg  = it * a.WheelSlipRearAvg  + t * b.WheelSlipRearAvg,
            TyreTempAvg       = it * a.TyreTempAvg       + t * b.TyreTempAvg,
            TyreTempFL        = it * a.TyreTempFL        + t * b.TyreTempFL,
            TyreTempFR        = it * a.TyreTempFR        + t * b.TyreTempFR,
            TyreTempRL        = it * a.TyreTempRL        + t * b.TyreTempRL,
            TyreTempRR        = it * a.TyreTempRR        + t * b.TyreTempRR,
            TyrePressureFL    = it * a.TyrePressureFL    + t * b.TyrePressureFL,
            TyrePressureFR    = it * a.TyrePressureFR    + t * b.TyrePressureFR,
            TyrePressureRL    = it * a.TyrePressureRL    + t * b.TyrePressureRL,
            TyrePressureRR    = it * a.TyrePressureRR    + t * b.TyrePressureRR,
        };
    }
}
