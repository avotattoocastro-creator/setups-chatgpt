using System;

namespace AvoPerformanceSetupAI.Telemetry;

// ─── Domain types ─────────────────────────────────────────────────────────────

/// <summary>The driving phase detected from the most recent telemetry sample.</summary>
public enum CornerPhase
{
    Straight,
    BrakingZone,
    Cornering,
    Acceleration,
}

/// <summary>
/// Computed telemetry features derived from a window of <see cref="TelemetrySample"/> entries.
/// </summary>
public readonly record struct TelemetryFeatures
{
    /// <summary>
    /// Average front – rear tyre slip-angle delta (rad).
    /// Positive = front slips more than rear → understeer tendency.
    /// </summary>
    public double UndersteerId { get; init; }

    /// <summary>
    /// Average rear – front tyre slip-angle delta (rad), clamped to 0 when positive is front.
    /// Positive = rear slips more than front → oversteer tendency.
    /// </summary>
    public double OversteerId { get; init; }

    /// <summary>
    /// Ratio of rear-to-front average wheel-slip magnitude.
    /// Values significantly above 1.0 indicate rear wheelspin.
    /// </summary>
    public double WheelspinRatio { get; init; }

    /// <summary>
    /// Coefficient of variation of the four brake pressures during braking samples.
    /// 0 = perfectly balanced; higher values indicate uneven brake distribution.
    /// </summary>
    public double BrakeInstability { get; init; }

    /// <summary>
    /// Left – right tyre-temperature difference (°C).
    /// Positive = left side warmer; negative = right side warmer.
    /// </summary>
    public double TyreBalanceLateral { get; init; }

    /// <summary>
    /// Front – rear tyre-temperature difference (°C).
    /// Positive = front warmer; negative = rear warmer.
    /// </summary>
    public double TyreBalanceLongitudinal { get; init; }

    /// <summary>Driving phase inferred from the most recent sample in the window.</summary>
    public CornerPhase Phase { get; init; }
}

// ─── FeatureFrame (normalized, phase-aware, ring-buffer–backed) ──────────────

/// <summary>
/// Rich, phase-aware telemetry features derived from a time-bounded window of
/// <see cref="TelemetrySample"/> entries. All fields are normalized to 0..1
/// against the declared physical thresholds. A value of 1.0 means the signal
/// has reached (or exceeded) the threshold; 0.0 means no signal detected.
/// </summary>
public readonly record struct FeatureFrame
{
    // ── Phase-specific understeer ─────────────────────────────────────────────

    /// <summary>Understeer index during braking / corner entry (0..1).</summary>
    public float UndersteerEntry { get; init; }

    /// <summary>Understeer index during mid-corner (0..1).</summary>
    public float UndersteerMid   { get; init; }

    /// <summary>Understeer index during throttle / corner exit (0..1).</summary>
    public float UndersteerExit  { get; init; }

    // ── Phase-specific oversteer ──────────────────────────────────────────────

    /// <summary>Oversteer index during braking / corner entry (0..1).</summary>
    public float OversteerEntry  { get; init; }

    /// <summary>Oversteer index during throttle / corner exit (0..1).</summary>
    public float OversteerExit   { get; init; }

    // ── Wheelspin / lockup ────────────────────────────────────────────────────

    /// <summary>
    /// Rear-wheel-spin index (0..1). Threshold: rear/front wheel-slip ratio of 2.0
    /// (i.e. rear slipping twice as much as front = 1.0).
    /// </summary>
    public float WheelspinRatioRear { get; init; }

    /// <summary>
    /// Front wheel-lockup index during braking (0..1). Threshold: front/rear
    /// wheel-slip ratio of 2.0 while brake > 10 %.
    /// </summary>
    public float LockupRatioFront   { get; init; }

    // ── Tyre temperature balance ──────────────────────────────────────────────

    /// <summary>
    /// Left-to-right average tyre-temperature imbalance, normalized (0..1).
    /// Threshold: 20 °C absolute difference.
    /// </summary>
    public float TyreTempDeltaLR { get; init; }

    /// <summary>
    /// Front-to-rear average tyre-temperature imbalance, normalized (0..1).
    /// Threshold: 30 °C absolute difference.
    /// </summary>
    public float TyreTempDeltaFR { get; init; }

    // ── Stability indices ─────────────────────────────────────────────────────

    /// <summary>
    /// Brake pressure imbalance (CoV across four corners) while braking, normalized (0..1).
    /// Threshold CoV: 0.5.
    /// </summary>
    public float BrakeStabilityIndex { get; init; }

    /// <summary>
    /// Suspension / vertical-G oscillation index (0..1).
    /// Computed as stddev(AccGVertical) / 0.5 g threshold.
    /// </summary>
    public float SuspensionOscillationIndex { get; init; }

    /// <summary>Number of samples included in this frame computation.</summary>
    public int SampleCount { get; init; }
}

// ─── Extractor ────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless calculator that derives <see cref="TelemetryFeatures"/> from a
/// window of <see cref="TelemetrySample"/> entries.
/// </summary>
public static class FeatureExtractor
{
    private const double Epsilon = 1e-6;

    /// <summary>
    /// Computes features from the <paramref name="count"/> samples stored at the
    /// start of <paramref name="samples"/>.
    /// Returns a zeroed <see cref="TelemetryFeatures"/> when <paramref name="count"/>
    /// is zero.
    /// </summary>
    public static TelemetryFeatures Extract(TelemetrySample[] samples, int count)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (count <= 0) return new TelemetryFeatures();

        count = Math.Min(count, samples.Length);

        // ── Accumulators ──────────────────────────────────────────────────────

        double sumFrontSlip = 0, sumRearSlip  = 0;
        double sumFrontWheelSlip = 0, sumRearWheelSlip = 0;

        double sumTyreFL = 0, sumTyreFR = 0, sumTyreRL = 0, sumTyreRR = 0;

        double sumBpFL = 0, sumBpFR = 0, sumBpRL = 0, sumBpRR = 0;
        int    brakingSamples = 0;

        for (int i = 0; i < count; i++)
        {
            var s = samples[i];

            // Slip angles — use absolute value; sign depends on corner direction
            sumFrontSlip += (Math.Abs(s.SlipAngleFL) + Math.Abs(s.SlipAngleFR)) * 0.5;
            sumRearSlip  += (Math.Abs(s.SlipAngleRL) + Math.Abs(s.SlipAngleRR)) * 0.5;

    // ── Wheel-slip magnitudes for wheelspin ratio (absolute values to handle deceleration)
            sumFrontWheelSlip += (Math.Abs(s.WheelSlipFL) + Math.Abs(s.WheelSlipFR)) * 0.5;
            sumRearWheelSlip  += (Math.Abs(s.WheelSlipRL) + Math.Abs(s.WheelSlipRR)) * 0.5;

            // Tyre temperatures
            sumTyreFL += s.TyreTempFL;
            sumTyreFR += s.TyreTempFR;
            sumTyreRL += s.TyreTempRL;
            sumTyreRR += s.TyreTempRR;

            // Brake pressure — collect only during actual braking
            if (s.Brake > 0.1f)
            {
                sumBpFL += s.BrakePressureFL;
                sumBpFR += s.BrakePressureFR;
                sumBpRL += s.BrakePressureRL;
                sumBpRR += s.BrakePressureRR;
                brakingSamples++;
            }
        }

        // ── Average ───────────────────────────────────────────────────────────

        var n = (double)count;

        var avgFrontSlip = sumFrontSlip / n;
        var avgRearSlip  = sumRearSlip  / n;

        var avgFrontWheelSlip = sumFrontWheelSlip / n;
        var avgRearWheelSlip  = sumRearWheelSlip  / n;

        var avgTyreFL = sumTyreFL / n;
        var avgTyreFR = sumTyreFR / n;
        var avgTyreRL = sumTyreRL / n;
        var avgTyreRR = sumTyreRR / n;

        // ── Understeer / oversteer ────────────────────────────────────────────

        // Positive delta (front > rear) = understeer; negative = oversteer
        var slipDelta = avgFrontSlip - avgRearSlip;

        var understeerId = Math.Max(0.0, slipDelta);
        var oversteerId  = Math.Max(0.0, -slipDelta);

        // ── Wheelspin ratio ───────────────────────────────────────────────────

        var wheelspinRatio = avgFrontWheelSlip > Epsilon
            ? avgRearWheelSlip / avgFrontWheelSlip
            : 1.0;

        // ── Brake instability ─────────────────────────────────────────────────

        double brakeInstability = 0.0;
        if (brakingSamples > 0)
        {
            var nb   = (double)brakingSamples;
            var bpFL = sumBpFL / nb;
            var bpFR = sumBpFR / nb;
            var bpRL = sumBpRL / nb;
            var bpRR = sumBpRR / nb;
            var mean = (bpFL + bpFR + bpRL + bpRR) * 0.25;

            if (mean > Epsilon)
            {
                var variance = ((bpFL - mean) * (bpFL - mean) +
                                (bpFR - mean) * (bpFR - mean) +
                                (bpRL - mean) * (bpRL - mean) +
                                (bpRR - mean) * (bpRR - mean)) * 0.25;

                brakeInstability = Math.Min(Math.Sqrt(variance) / mean, 1.0);
            }
        }

        // ── Tyre balance ──────────────────────────────────────────────────────

        // Left  = FL + RL ;  Right = FR + RR ;  positive → left warmer
        var tyreBalanceLateral = (avgTyreFL + avgTyreRL) - (avgTyreFR + avgTyreRR);
        // Front = FL + FR ;  Rear  = RL + RR ;  positive → front warmer
        var tyreBalanceLong    = (avgTyreFL + avgTyreFR) - (avgTyreRL + avgTyreRR);

        // ── Corner phase ──────────────────────────────────────────────────────

        var phase = DetectPhase(samples[count - 1]);

        return new TelemetryFeatures
        {
            UndersteerId          = Math.Round(understeerId,      4),
            OversteerId           = Math.Round(oversteerId,       4),
            WheelspinRatio        = Math.Round(wheelspinRatio,    3),
            BrakeInstability      = Math.Round(brakeInstability,  4),
            TyreBalanceLateral    = Math.Round(tyreBalanceLateral, 2),
            TyreBalanceLongitudinal = Math.Round(tyreBalanceLong,  2),
            Phase                 = phase,
        };
    }

    // ── Corner-phase detection ────────────────────────────────────────────────

    private static CornerPhase DetectPhase(TelemetrySample s)
    {
        // Priority: braking → cornering → acceleration → straight
        if (s.Brake > 0.1f)
            return CornerPhase.BrakingZone;

        var absLateral = Math.Abs(s.AccGLateral);

        if (absLateral > 0.3f)
            return s.Throttle > 0.3f ? CornerPhase.Acceleration : CornerPhase.Cornering;

        return CornerPhase.Straight;
    }

    // ── Human-readable log lines ──────────────────────────────────────────────

    /// <summary>
    /// Returns a short list of (tag, message) pairs suitable for appending to an
    /// analysis terminal, derived from the supplied <see cref="TelemetryFeatures"/>.
    /// </summary>
    public static (string Tag, string Msg)[] FormatLog(in TelemetryFeatures f)
    {
        var phaseLabel = f.Phase switch
        {
            CornerPhase.BrakingZone  => "FRENADA",
            CornerPhase.Cornering    => "CURVA",
            CornerPhase.Acceleration => "ACELERACIÓN",
            _                        => "RECTA",
        };

        // Understeer / oversteer — report whichever is dominant
        string behaviorTag, behaviorMsg;
        if (f.UndersteerId > 0.005)
        {
            behaviorTag = "SUBVIRAJE";
            behaviorMsg = $"Subviraje detectado — delta ángulo deslizamiento: +{f.UndersteerId:F4} rad  [{phaseLabel}]";
        }
        else if (f.OversteerId > 0.005)
        {
            behaviorTag = "SOBREVIRAJE";
            behaviorMsg = $"Sobreviraje detectado — delta ángulo deslizamiento: +{f.OversteerId:F4} rad  [{phaseLabel}]";
        }
        else
        {
            behaviorTag = "BALANCE";
            behaviorMsg = $"Balance neutro — deslizamiento frontal/trasero en límites  [{phaseLabel}]";
        }

        // Wheelspin
        var spinTag = f.WheelspinRatio > 1.15
            ? "WHEELSPIN"
            : "TRACCIÓN";
        var spinMsg = f.WheelspinRatio > 1.15
            ? $"Patinamiento trasero — ratio rueda trasera/delantera: {f.WheelspinRatio:F2}"
            : $"Tracción OK — ratio rueda trasera/delantera: {f.WheelspinRatio:F2}";

        // Brake instability
        var brakeTag = f.BrakeInstability > 0.10
            ? "FRENOS!"
            : "FRENOS";
        var brakeMsg = f.BrakeInstability > 0.10
            ? $"Inestabilidad de frenada — coef. variación: {f.BrakeInstability:P0} — revisar reparto"
            : $"Frenada equilibrada — coef. variación: {f.BrakeInstability:P0}";

        // Tyre balance
        var lat  = f.TyreBalanceLateral       >= 0 ? $"izq. +{f.TyreBalanceLateral:F1}°C"  : $"der. +{-f.TyreBalanceLateral:F1}°C";
        var lon  = f.TyreBalanceLongitudinal >= 0 ? $"del. +{f.TyreBalanceLongitudinal:F1}°C" : $"tras. +{-f.TyreBalanceLongitudinal:F1}°C";
        var tyreTag = "NEUMÁTICOS";
        var tyreMsg = $"Balance temp: {lat} / {lon}";

        return
        [
            (behaviorTag, behaviorMsg),
            (spinTag,     spinMsg),
            (brakeTag,    brakeMsg),
            (tyreTag,     tyreMsg),
        ];
    }

    // ── FormatLog overload for FeatureFrame ───────────────────────────────────

    /// <summary>
    /// Returns human-readable (tag, message) pairs from a <see cref="FeatureFrame"/>,
    /// suitable for appending to an analysis terminal.
    /// </summary>
    public static (string Tag, string Msg)[] FormatLog(in FeatureFrame f)
    {
        // Understeer — pick the most severe phase
        var usMax = Math.Max(f.UndersteerEntry, Math.Max(f.UndersteerMid, f.UndersteerExit));
        var osMax = Math.Max(f.OversteerEntry,  f.OversteerExit);

        string behaviorTag, behaviorMsg;
        if (usMax > 0.05f)
        {
            behaviorTag = "SUBVIRAJE";
            behaviorMsg = $"Subviraje — entrada {f.UndersteerEntry:P0}  medio {f.UndersteerMid:P0}  salida {f.UndersteerExit:P0}";
        }
        else if (osMax > 0.05f)
        {
            behaviorTag = "SOBREVIRAJE";
            behaviorMsg = $"Sobreviraje — entrada {f.OversteerEntry:P0}  salida {f.OversteerExit:P0}";
        }
        else
        {
            behaviorTag = "BALANCE";
            behaviorMsg = "Balance neutro — deslizamiento dentro de límites";
        }

        var spinTag = f.WheelspinRatioRear > 0.15f ? "WHEELSPIN" : "TRACCIÓN";
        var spinMsg = $"Patinamiento trasero {f.WheelspinRatioRear:P0}  Bloqueo delantero {f.LockupRatioFront:P0}";

        var brakeTag = f.BrakeStabilityIndex > 0.20f ? "FRENOS!" : "FRENOS";
        var brakeMsg = $"Estabilidad frenada {1f - f.BrakeStabilityIndex:P0}  Oscilación susp. {f.SuspensionOscillationIndex:P0}";

        var tempTag = "NEUMÁTICOS";
        var tempMsg = $"Balance temp: L-R {f.TyreTempDeltaLR:P0}  F-R {f.TyreTempDeltaFR:P0}";

        return
        [
            (behaviorTag, behaviorMsg),
            (spinTag,     spinMsg),
            (brakeTag,    brakeMsg),
            (tempTag,     tempMsg),
        ];
    }

    // ── Normalization thresholds ──────────────────────────────────────────────

    /// <summary>Slip-angle delta at which understeer/oversteer index reaches 1.0 (radians).</summary>
    public const  double SlipAngleThreshold       = 0.10; // rad
    private const double WheelspinThreshold       = 1.0;  // ratio excess above 1.0 (→ 2.0× = 1.0 index)
    private const double LockupThreshold          = 1.0;  // ratio excess above 1.0 (front/rear during braking)
    private const double TyreTempLRThreshold      = 20.0; // °C
    private const double TyreTempFRThreshold      = 30.0; // °C
    private const double BrakeStabilityThreshold  = 0.5;  // CoV
    private const double SuspOscillationThreshold = 0.5;  // g

    // ── ExtractFrame: ring-buffer window ──────────────────────────────────────

    /// <summary>
    /// Reads the most recent <paramref name="windowSeconds"/> seconds from
    /// <paramref name="buffer"/> and returns a normalized <see cref="FeatureFrame"/>.
    /// Returns a zeroed frame when the buffer is empty.
    /// </summary>
    /// <param name="buffer">Ring buffer populated by <see cref="AcTelemetryReader"/>.</param>
    /// <param name="windowSeconds">Time window to examine (e.g. 2.0 = last 2 s at 250 Hz).</param>
    public static FeatureFrame ExtractFrame(TelemetryRingBuffer buffer, double windowSeconds)
    {
        if (buffer is null)         throw new ArgumentNullException(nameof(buffer));
        if (windowSeconds <= 0)     throw new ArgumentOutOfRangeException(nameof(windowSeconds));

        // Generous estimate: 260 Hz = 250 Hz nominal + 4 % safety margin; actual window is trimmed by timestamp below
        var maxSamples = (int)(windowSeconds * 260) + 1;
        var temp       = new TelemetrySample[maxSamples];
        var total      = buffer.CopyTail(temp, maxSamples);
        if (total == 0) return new FeatureFrame();

        var cutoff = temp[total - 1].Timestamp - TimeSpan.FromSeconds(windowSeconds);
        int start  = 0;
        while (start < total && temp[start].Timestamp < cutoff) start++;
        var count = total - start;
        return count == 0 ? new FeatureFrame() : ComputeFrame(temp, start, count);
    }

    /// <summary>
    /// Computes a <see cref="FeatureFrame"/> from <paramref name="count"/> samples
    /// stored at the beginning of <paramref name="samples"/> (oldest first).
    /// </summary>
    public static FeatureFrame ExtractFrame(TelemetrySample[] samples, int count)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (count <= 0) return new FeatureFrame();
        count = Math.Min(count, samples.Length);
        return ComputeFrame(samples, 0, count);
    }

    // ── ComputeFrame implementation ───────────────────────────────────────────

    // ── ExtractFrameComponents: exposes raw slip/yaw frames for agreement checks ──

    /// <summary>
    /// Returns the blended <see cref="FeatureFrame"/> together with the raw
    /// slip-angle-only and yaw-gain-only component frames.
    /// Pass the two raw frames to
    /// <see cref="RuleEngine.Evaluate(in FeatureFrame, in FeatureFrame)"/>
    /// to enable signal-agreement confidence adjustment.
    /// </summary>
    public static (FeatureFrame Blended, FeatureFrame SlipBased, FeatureFrame YawBased)
        ExtractFrameComponents(TelemetrySample[] samples, int count)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (count <= 0) return (new FeatureFrame(), new FeatureFrame(), new FeatureFrame());
        count = Math.Min(count, samples.Length);
        return ComputeFrameComponents(samples, 0, count);
    }

    private static FeatureFrame ComputeFrame(TelemetrySample[] buf, int offset, int count)
        => ComputeFrameComponents(buf, offset, count).Blended;

    private static (FeatureFrame Blended, FeatureFrame SlipBased, FeatureFrame YawBased)
        ComputeFrameComponents(TelemetrySample[] buf, int offset, int count)
    {
        // Per-phase slip-angle accumulators
        double sumFrontSlipEntry = 0, sumRearSlipEntry = 0; int nEntry = 0;
        double sumFrontSlipMid   = 0, sumRearSlipMid   = 0; int nMid   = 0;
        double sumFrontSlipExit  = 0, sumRearSlipExit  = 0; int nExit  = 0;

        // Per-phase yaw-gain accumulators (used to blend with slip-based indices)
        float sumYawGainEntry = 0f, sumYawGainMid = 0f, sumYawGainExit = 0f;

        // Wheelspin (all samples) and lockup (braking samples)
        double sumFrontWS = 0, sumRearWS = 0;
        double sumFrontWSBrake = 0, sumRearWSBrake = 0; int nBraking = 0;

        // Tyre temperatures
        double sumTyreFL = 0, sumTyreFR = 0, sumTyreRL = 0, sumTyreRR = 0;

        // Brake pressures (braking samples only)
        double sumBpFL = 0, sumBpFR = 0, sumBpRL = 0, sumBpRR = 0;

        // Vertical G for suspension oscillation index
        double sumVertG = 0, sumVertGSq = 0;

        for (int i = 0; i < count; i++)
        {
            ref readonly var s = ref buf[offset + i];

            var frontSlip = (Math.Abs(s.SlipAngleFL) + Math.Abs(s.SlipAngleFR)) * 0.5;
            var rearSlip  = (Math.Abs(s.SlipAngleRL) + Math.Abs(s.SlipAngleRR)) * 0.5;

            var yawGain = BalanceMetrics.ComputeYawGain(s.YawRate, s.SteerAngle, s.SpeedKmh);

            switch (DetectPhase(s))
            {
                case CornerPhase.BrakingZone:
                    sumFrontSlipEntry += frontSlip; sumRearSlipEntry += rearSlip; nEntry++;
                    sumYawGainEntry += yawGain;
                    break;
                case CornerPhase.Cornering:
                    sumFrontSlipMid += frontSlip; sumRearSlipMid += rearSlip; nMid++;
                    sumYawGainMid += yawGain;
                    break;
                case CornerPhase.Acceleration:
                    sumFrontSlipExit += frontSlip; sumRearSlipExit += rearSlip; nExit++;
                    sumYawGainExit += yawGain;
                    break;
            }

            var frontWS = (Math.Abs(s.WheelSlipFL) + Math.Abs(s.WheelSlipFR)) * 0.5;
            var rearWS  = (Math.Abs(s.WheelSlipRL) + Math.Abs(s.WheelSlipRR)) * 0.5;
            sumFrontWS += frontWS;
            sumRearWS  += rearWS;

            if (s.Brake > 0.1f)
            {
                sumFrontWSBrake += frontWS; sumRearWSBrake += rearWS; nBraking++;
                sumBpFL += s.BrakePressureFL; sumBpFR += s.BrakePressureFR;
                sumBpRL += s.BrakePressureRL; sumBpRR += s.BrakePressureRR;
            }

            sumTyreFL += s.TyreTempFL; sumTyreFR += s.TyreTempFR;
            sumTyreRL += s.TyreTempRL; sumTyreRR += s.TyreTempRR;

            var g = (double)s.AccGVertical;
            sumVertG   += g;
            sumVertGSq += g * g;
        }

        var n = (double)count;

        // ── Phase-specific understeer / oversteer ─────────────────────────────

        // Raw per-phase slip-angle and yaw-gain component variables (hoisted for component frames)
        float slipUnderEntry = 0f, slipOverEntry = 0f, yawIndexEntry = 0f;
        float slipUnderMid   = 0f,                     yawIndexMid   = 0f;
        float slipUnderExit  = 0f, slipOverExit  = 0f, yawIndexExit  = 0f;

        var understeerEntry = 0f; var oversteerEntry = 0f;
        if (nEntry > 0)
        {
            var d = sumFrontSlipEntry / nEntry - sumRearSlipEntry / nEntry;
            slipUnderEntry  = Norm(Math.Max(0.0, d),  SlipAngleThreshold);
            slipOverEntry   = Norm(Math.Max(0.0, -d), SlipAngleThreshold);
            yawIndexEntry   = BalanceMetrics.ComputeBalanceIndex(sumYawGainEntry / nEntry);
            understeerEntry = BlendIndex(slipUnderEntry, Math.Max(0f, -yawIndexEntry));
            oversteerEntry  = BlendIndex(slipOverEntry,  Math.Max(0f,  yawIndexEntry));
        }

        var understeerMid = 0f;
        if (nMid > 0)
        {
            slipUnderMid  = Norm(Math.Max(0.0, sumFrontSlipMid / nMid - sumRearSlipMid / nMid), SlipAngleThreshold);
            yawIndexMid   = BalanceMetrics.ComputeBalanceIndex(sumYawGainMid / nMid);
            understeerMid = BlendIndex(slipUnderMid, Math.Max(0f, -yawIndexMid));
        }

        var understeerExit = 0f; var oversteerExit = 0f;
        if (nExit > 0)
        {
            var d = sumFrontSlipExit / nExit - sumRearSlipExit / nExit;
            slipUnderExit  = Norm(Math.Max(0.0, d),  SlipAngleThreshold);
            slipOverExit   = Norm(Math.Max(0.0, -d), SlipAngleThreshold);
            yawIndexExit   = BalanceMetrics.ComputeBalanceIndex(sumYawGainExit / nExit);
            understeerExit = BlendIndex(slipUnderExit, Math.Max(0f, -yawIndexExit));
            oversteerExit  = BlendIndex(slipOverExit,  Math.Max(0f,  yawIndexExit));
        }

        // ── Wheelspin ratio rear ──────────────────────────────────────────────

        var avgFrontWS     = sumFrontWS / n;
        var avgRearWS      = sumRearWS  / n;
        var wsRatio        = avgFrontWS > Epsilon ? avgRearWS / avgFrontWS : 1.0;
        var wheelspinRatioRear = Norm(Math.Max(0.0, wsRatio - 1.0), WheelspinThreshold);

        // ── Front lockup ratio ────────────────────────────────────────────────

        var lockupRatioFront = 0f;
        if (nBraking > 0)
        {
            var avgFB = sumFrontWSBrake / nBraking;
            var avgRB = sumRearWSBrake  / nBraking;
            var lr    = avgRB > Epsilon ? avgFB / avgRB : 1.0;
            lockupRatioFront = Norm(Math.Max(0.0, lr - 1.0), LockupThreshold);
        }

        // ── Tyre temperature balance ──────────────────────────────────────────

        var avgFL = sumTyreFL / n; var avgFR = sumTyreFR / n;
        var avgRL = sumTyreRL / n; var avgRR = sumTyreRR / n;
        var tyreTempDeltaLR = Norm(Math.Abs((avgFL + avgRL) - (avgFR + avgRR)) * 0.5, TyreTempLRThreshold);
        var tyreTempDeltaFR = Norm(Math.Abs((avgFL + avgFR) - (avgRL + avgRR)) * 0.5, TyreTempFRThreshold);

        // ── Brake stability index ─────────────────────────────────────────────

        var brakeStabilityIndex = 0f;
        if (nBraking > 0)
        {
            var nb   = (double)nBraking;
            var bFL  = sumBpFL / nb; var bFR = sumBpFR / nb;
            var bRL  = sumBpRL / nb; var bRR = sumBpRR / nb;
            var mean = (bFL + bFR + bRL + bRR) * 0.25;
            if (mean > Epsilon)
            {
                var variance = ((bFL - mean) * (bFL - mean) + (bFR - mean) * (bFR - mean) +
                                (bRL - mean) * (bRL - mean) + (bRR - mean) * (bRR - mean)) * 0.25;
                brakeStabilityIndex = Norm(Math.Sqrt(variance) / mean, BrakeStabilityThreshold);
            }
        }

        // ── Suspension oscillation index ──────────────────────────────────────

        var meanG       = sumVertG   / n;
        var varVertG    = Math.Max(0.0, sumVertGSq / n - meanG * meanG);
        var suspIndex   = Norm(Math.Sqrt(varVertG), SuspOscillationThreshold);

        var blended = new FeatureFrame
        {
            UndersteerEntry            = understeerEntry,
            UndersteerMid              = understeerMid,
            UndersteerExit             = understeerExit,
            OversteerEntry             = oversteerEntry,
            OversteerExit              = oversteerExit,
            WheelspinRatioRear         = wheelspinRatioRear,
            LockupRatioFront           = lockupRatioFront,
            TyreTempDeltaLR            = tyreTempDeltaLR,
            TyreTempDeltaFR            = tyreTempDeltaFR,
            BrakeStabilityIndex        = brakeStabilityIndex,
            SuspensionOscillationIndex = suspIndex,
            SampleCount                = count,
        };

        // Slip-based frame: pure slip-angle component indices
        var slipBased = blended with
        {
            UndersteerEntry = slipUnderEntry,
            UndersteerMid   = slipUnderMid,
            UndersteerExit  = slipUnderExit,
            OversteerEntry  = slipOverEntry,
            OversteerExit   = slipOverExit,
        };

        // Yaw-based frame: pure yaw-gain component indices
        var yawBased = blended with
        {
            UndersteerEntry = Math.Max(0f, -yawIndexEntry),
            UndersteerMid   = Math.Max(0f, -yawIndexMid),
            UndersteerExit  = Math.Max(0f, -yawIndexExit),
            OversteerEntry  = Math.Max(0f,  yawIndexEntry),
            OversteerExit   = Math.Max(0f,  yawIndexExit),
        };

        return (blended, slipBased, yawBased);
    }

    private static float Norm(double raw, double threshold)
        => (float)Math.Min(raw / threshold, 1.0);

    /// <summary>
    /// Blends a slip-angle-based index with a yaw-gain-based index at 50 % each.
    /// Result is clamped to [0, 1].
    /// </summary>
    private static float BlendIndex(float slipIndex, float yawIndex)
        => Math.Min(0.5f * slipIndex + 0.5f * yawIndex, 1f);
}
