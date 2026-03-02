using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Telemetry;

// ─── CornerPhaseAnalyzer ──────────────────────────────────────────────────────

/// <summary>
/// Segments a stream of <see cref="TelemetrySample"/> entries into discrete corner
/// events using lateral G and brake/throttle thresholds. Each identified corner is
/// characterized by a phase-aware <see cref="CornerSummary"/> built by delegating
/// per-phase feature extraction to <see cref="FeatureExtractor"/>.
/// </summary>
/// <remarks>
/// Corner detection uses a two-stage hysteresis state machine driven by
/// <see cref="LateralGThreshold"/>: a corner starts after <see cref="HysteresisSamples"/>
/// consecutive samples above the threshold, and ends after the same number of
/// consecutive samples below it. Within each corner the three sub-phases are
/// classified sample-by-sample using brake/throttle inputs:
/// <list type="bullet">
///   <item><description>Entry — <c>Brake &gt; 0.1</c></description></item>
///   <item><description>Exit  — <c>Throttle &gt; 0.3</c></description></item>
///   <item><description>Mid   — everything else</description></item>
/// </list>
/// </remarks>
public static class CornerPhaseAnalyzer
{
    // ── Detection thresholds ──────────────────────────────────────────────────

    /// <summary>Minimum |AccGLateral| (G) to consider a sample as part of a corner.</summary>
    public const float LateralGThreshold = 0.40f;

    /// <summary>
    /// Consecutive samples above/below <see cref="LateralGThreshold"/> required to
    /// commit to a state transition (hysteresis). Suppresses false triggers from
    /// transient G spikes. At 250 Hz, 10 samples ≈ 40 ms.
    /// </summary>
    public const int   HysteresisSamples = 10;

    /// <summary>
    /// Minimum corner length (samples) to be included in the output.
    /// Filters out micro-events shorter than ~100 ms (25 samples at 250 Hz).
    /// </summary>
    public const int   MinCornerSamples  = 25;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the most recent <paramref name="windowSeconds"/> of data from
    /// <paramref name="buffer"/> and returns per-corner summaries, oldest first.
    /// Returns an empty array when the buffer contains no data within the window.
    /// </summary>
    /// <param name="buffer">Ring buffer populated by <see cref="AcTelemetryReader"/>.</param>
    /// <param name="windowSeconds">Time window to examine (e.g. 30.0 = last 30 s).</param>
    public static CornerSummary[] Analyze(TelemetryRingBuffer buffer, double windowSeconds)
    {
        if (buffer is null)     throw new ArgumentNullException(nameof(buffer));
        if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));

        // 260 Hz = 250 Hz nominal + 4 % safety margin; actual window trimmed by timestamp below
        var maxSamples = (int)(windowSeconds * 260) + 1;
        var temp       = new TelemetrySample[maxSamples];
        var total      = buffer.CopyTail(temp, maxSamples);
        if (total == 0) return [];

        var cutoff = temp[total - 1].Timestamp - TimeSpan.FromSeconds(windowSeconds);
        int start  = 0;
        while (start < total && temp[start].Timestamp < cutoff) start++;
        var count = total - start;
        return count == 0 ? [] : DetectCorners(temp, start, count);
    }

    /// <summary>
    /// Analyzes the first <paramref name="count"/> entries of <paramref name="samples"/>
    /// (oldest first) and returns per-corner summaries, oldest first.
    /// </summary>
    public static CornerSummary[] Analyze(TelemetrySample[] samples, int count)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (count <= 0) return [];
        count = Math.Min(count, samples.Length);
        return DetectCorners(samples, 0, count);
    }

    // ── FormatLog ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns two (tag, message) pairs for the supplied <see cref="CornerSummary"/>,
    /// suitable for appending to an analysis terminal.
    /// </summary>
    public static (string Tag, string Msg)[] FormatLog(in CornerSummary cs)
    {
        var cornerTag = $"CURVA {cs.CornerIndex + 1}";
        var header    = $"Pos {cs.LapPos:P0}  Dur {cs.Duration.TotalSeconds:F1}s  Lat {cs.PeakLateralG:F1}G";

        var tf    = cs.TotalFrame;
        var usMax = Math.Max(tf.UndersteerEntry, Math.Max(tf.UndersteerMid, tf.UndersteerExit));
        var osMax = Math.Max(tf.OversteerEntry,  tf.OversteerExit);

        string detailMsg;
        if (tf.LockupRatioFront > 0.25f)
            detailMsg = $"Bloqueo del. {tf.LockupRatioFront:P0}  —  revisar punto / presión de frenada";
        else if (tf.WheelspinRatioRear > 0.25f)
            detailMsg = $"Patinamiento tra. {tf.WheelspinRatioRear:P0}  —  diferencial / TCS / apertura de gas";
        else if (usMax > 0.10f)
            detailMsg = $"Subviraje — ent {tf.UndersteerEntry:P0}  med {tf.UndersteerMid:P0}  sal {tf.UndersteerExit:P0}";
        else if (osMax > 0.10f)
            detailMsg = $"Sobreviraje — ent {tf.OversteerEntry:P0}  sal {tf.OversteerExit:P0}";
        else
            detailMsg = "Balance neutro — sin anomalías detectadas en esta curva";

        return
        [
            (cornerTag,          header),
            ("→ " + cs.Dominant, detailMsg),
        ];
    }

    // ── Corner detection state machine ────────────────────────────────────────

    private static CornerSummary[] DetectCorners(TelemetrySample[] buf, int offset, int count)
    {
        var summaries   = new List<CornerSummary>();
        int cornerIndex = 0;

        bool inCorner       = false;
        int  cornerStartIdx = 0;
        int  hysteresis     = 0;

        for (int i = 0; i < count; i++)
        {
            var g = Math.Abs(buf[offset + i].AccGLateral);

            if (!inCorner)
            {
                if (g >= LateralGThreshold)
                {
                    if (++hysteresis >= HysteresisSamples)
                    {
                        inCorner       = true;
                        cornerStartIdx = i - HysteresisSamples + 1;
                        hysteresis     = 0;
                    }
                }
                else
                {
                    hysteresis = 0;
                }
            }
            else
            {
                if (g < LateralGThreshold)
                {
                    if (++hysteresis >= HysteresisSamples)
                    {
                        // Last confirmed in-corner sample is (i - HysteresisSamples)
                        var cornerEnd = i - HysteresisSamples;
                        var len       = cornerEnd - cornerStartIdx + 1;
                        if (len >= MinCornerSamples)
                        {
                            summaries.Add(BuildSummary(buf, offset + cornerStartIdx, len, cornerIndex));
                            cornerIndex++;
                        }
                        inCorner   = false;
                        hysteresis = 0;
                    }
                }
                else
                {
                    hysteresis = 0;
                }
            }
        }

        // Include a corner still active at the end of the window (partial corner)
        if (inCorner)
        {
            var len = count - cornerStartIdx;
            if (len >= MinCornerSamples)
            {
                summaries.Add(BuildSummary(buf, offset + cornerStartIdx, len, cornerIndex));
            }
        }

        return [.. summaries];
    }

    // ── Per-corner feature aggregation ────────────────────────────────────────

    private static CornerSummary BuildSummary(TelemetrySample[] buf, int start, int count, int index)
    {
        // Collect per-phase sample lists and identify apex
        var entryList = new List<TelemetrySample>(count / 3 + 1);
        var midList   = new List<TelemetrySample>(count / 3 + 1);
        var exitList  = new List<TelemetrySample>(count / 3 + 1);

        float  peakLatG  = 0;
        int    apexIdx   = 0;
        double latGSum   = 0;

        for (int i = 0; i < count; i++)
        {
            ref readonly var s = ref buf[start + i];

            if (s.Brake > 0.12f)
                entryList.Add(s);
            else if (s.Throttle > 0.25f)
                exitList.Add(s);
            else
                midList.Add(s);

            var lat = Math.Abs(s.AccGLateral);
            if (lat > peakLatG) { peakLatG = (float)lat; apexIdx = i; }
            latGSum += s.AccGLateral;
        }

        // Build FeatureFrames via FeatureExtractor (handles all normalization)
        var cornerArr = new TelemetrySample[count];
        Array.Copy(buf, start, cornerArr, 0, count);

        var totalFrame = FeatureExtractor.ExtractFrame(cornerArr, count);

        var entryArr   = entryList.ToArray();
        var midArr     = midList.ToArray();
        var exitArr    = exitList.ToArray();

        var entryFrame = entryArr.Length > 0
            ? FeatureExtractor.ExtractFrame(entryArr, entryArr.Length) : default;
        var midFrame   = midArr.Length   > 0
            ? FeatureExtractor.ExtractFrame(midArr,   midArr.Length)   : default;
        var exitFrame  = exitArr.Length  > 0
            ? FeatureExtractor.ExtractFrame(exitArr,  exitArr.Length)  : default;

        var direction = latGSum > 0.0 ? CornerDirection.Left : CornerDirection.Right;
        var dominant  = CornerDetector.DeriveDominant(in totalFrame);

        return new CornerSummary
        {
            CornerIndex  = index,
            StartTime    = buf[start].Timestamp,
            EndTime      = buf[start + count - 1].Timestamp,
            Direction    = direction,
            LapPos       = buf[start + apexIdx].NormalizedLapPos,
            PeakLateralG = peakLatG,
            SampleCount  = count,
            EntryFrame   = entryFrame,
            MidFrame     = midFrame,
            ExitFrame    = exitFrame,
            TotalFrame   = totalFrame,
            Dominant     = dominant,
        };
    }
}

