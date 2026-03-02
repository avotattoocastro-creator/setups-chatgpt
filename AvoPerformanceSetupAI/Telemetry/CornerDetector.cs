using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Stateful corner segmentation module for Assetto Corsa telemetry.
/// Processes <see cref="TelemetryRingBuffer"/> samples and emits a
/// <see cref="CornerSummary"/> for each completed corner.
/// </summary>
/// <remarks>
/// <para>
/// Corner detection uses three independent signals that must all be true
/// simultaneously to open a corner:
/// <list type="bullet">
///   <item><description>|<see cref="TelemetrySample.SteerAngle"/>| &gt; <see cref="SteerOpenThreshold"/></description></item>
///   <item><description><see cref="TelemetrySample.SpeedKmh"/> &gt; <see cref="MinSpeedKmh"/></description></item>
///   <item><description>|<see cref="TelemetrySample.AccGLateral"/>| &gt; <see cref="LateralGThreshold"/></description></item>
/// </list>
/// A corner closes after <see cref="TelemetrySample.SteerAngle"/> magnitude
/// remains below <see cref="SteerCloseThreshold"/> for at least
/// <see cref="CloseHoldSeconds"/> continuous seconds.
/// </para>
/// <para>
/// Within a corner, samples are classified into three sub-phases:
/// <list type="bullet">
///   <item><description>Entry — <c>Brake &gt; <see cref="BrakeEntryThreshold"/></c></description></item>
///   <item><description>Exit  — <c>Throttle &gt; <see cref="ThrottleExitThreshold"/></c></description></item>
///   <item><description>Mid   — everything else</description></item>
/// </list>
/// On corner close, <see cref="FeatureExtractor.ExtractFrame(TelemetrySample[],int)"/>
/// is called for each phase set and for all samples to build the
/// four <see cref="FeatureFrame"/> objects stored in the resulting
/// <see cref="CornerSummary"/>.
/// </para>
/// <para>
/// Thread-safety: all public members are safe to call from any thread.
/// <see cref="Update"/> does not block the 250 Hz producer loop because it
/// only calls <see cref="TelemetryRingBuffer.CopyTail"/> (which takes the
/// buffer's own brief lock) and then processes new samples under a separate
/// internal lock.
/// </para>
/// </remarks>
public sealed class CornerDetector
{
    // ── Corner-open thresholds ─────────────────────────────────────────────────

    /// <summary>Minimum |SteerAngle| (rad) that, combined with speed and lateral G, opens a corner.</summary>
    public const float  SteerOpenThreshold  = 0.12f;

    /// <summary>|SteerAngle| (rad) that must be sustained below this for <see cref="CloseHoldSeconds"/> to close the corner.</summary>
    public const float  SteerCloseThreshold = 0.06f;

    /// <summary>Minimum vehicle speed (km/h) required for corner detection.</summary>
    public const float  MinSpeedKmh         = 40.0f;

    /// <summary>Minimum |AccGLateral| (G) required for corner detection.</summary>
    public const float  LateralGThreshold   = 0.20f;

    /// <summary>Seconds of sustained low steering required to close the current corner.</summary>
    public const double CloseHoldSeconds    = 0.70;

    // ── Per-phase input thresholds ─────────────────────────────────────────────

    private const float BrakeEntryThreshold   = 0.12f;
    private const float ThrottleExitThreshold = 0.25f;

    /// <summary>
    /// Sample-rate ceiling used when sizing the temporary copy array
    /// (250 Hz nominal + 4 % safety margin). Matches the constant in
    /// <see cref="FeatureExtractor"/> and <see cref="CornerPhaseAnalyzer"/>.
    /// </summary>
    private const int SampleRateCeiling = 260; // Hz

    private readonly int                  _maxHistory;
    private readonly List<CornerSummary>  _completed = new();
    private readonly object               _lock      = new();

    private DateTime _lastProcessed = DateTime.MinValue;
    private CornerSummary? _latest;

    // ── In-corner tracking ─────────────────────────────────────────────────────

    private bool     _inCorner;
    private int      _cornerIndex;
    private DateTime _cornerStart;
    private float    _peakLateralG;
    private float    _lapPosAtApex;
    private double   _latGSum;       // sum of AccGLateral for direction inference

    // Close-hysteresis
    private bool     _waitingClose;
    private DateTime _lowSteerSince;

    // Per-phase sample buffers (cleared and refilled for each corner)
    private readonly List<TelemetrySample> _entrySamples = new();
    private readonly List<TelemetrySample> _midSamples   = new();
    private readonly List<TelemetrySample> _exitSamples  = new();
    private readonly List<TelemetrySample> _allSamples   = new();

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <param name="maxHistory">Maximum number of completed corners to retain (default 10).</param>
    public CornerDetector(int maxHistory = 10)
    {
        if (maxHistory <= 0) throw new ArgumentOutOfRangeException(nameof(maxHistory));
        _maxHistory = maxHistory;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>The most recently completed corner, or <see langword="null"/> when no corner has been detected yet.</summary>
    public CornerSummary? LatestCorner
    {
        get { lock (_lock) return _latest; }
    }

    /// <summary>Snapshot of all retained completed corners (oldest first, up to <c>maxHistory</c>).</summary>
    public CornerSummary[] CompletedCorners
    {
        get { lock (_lock) return [.. _completed]; }
    }

    /// <summary>
    /// Drains all samples from <paramref name="buffer"/> that have not yet been
    /// processed, runs the corner state machine on each, and returns any corners
    /// that completed during this call (oldest first).
    /// Returns an empty array when no new corners were completed.
    /// Safe to call from any thread; does not block the 250 Hz producer.
    /// </summary>
    /// <param name="buffer">Ring buffer populated by <see cref="AcTelemetryReader"/>.</param>
    public CornerSummary[] Update(TelemetryRingBuffer buffer)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));

        // Copy recent samples — only the brief CopyTail lock touches the buffer
        var maxCopy = Math.Min(buffer.Capacity, (int)(30.0 * SampleRateCeiling) + 1); // ~30 s
        var temp    = new TelemetrySample[maxCopy];
        var total   = buffer.CopyTail(temp, maxCopy);
        if (total == 0) return [];

        List<CornerSummary>? newCorners = null;

        lock (_lock)
        {
            // Find first sample not yet processed (compare inside lock to avoid races)
            int startIdx = 0;
            while (startIdx < total && temp[startIdx].Timestamp <= _lastProcessed)
                startIdx++;

            if (startIdx < total)
            {
                _lastProcessed = temp[total - 1].Timestamp;

                for (int i = startIdx; i < total; i++)
                {
                    var result = ProcessSampleCore(in temp[i]);
                    if (result.HasValue)
                    {
                        newCorners ??= [];
                        newCorners.Add(result.Value);
                    }
                }
            }
        }

        return newCorners is null ? [] : [.. newCorners];
    }

    // ── State machine ──────────────────────────────────────────────────────────

    private CornerSummary? ProcessSampleCore(in TelemetrySample s)
    {
        var absSteer = Math.Abs(s.SteerAngle);
        var absLatG  = Math.Abs(s.AccGLateral);

        if (!_inCorner)
        {
            // Open: all three signals must exceed their thresholds
            if (absSteer > SteerOpenThreshold &&
                s.SpeedKmh > MinSpeedKmh      &&
                absLatG    > LateralGThreshold)
            {
                _inCorner     = true;
                _cornerStart  = s.Timestamp;
                _peakLateralG = absLatG;
                _lapPosAtApex = s.NormalizedLapPos;
                _latGSum      = s.AccGLateral;
                _waitingClose = false;
                _entrySamples.Clear();
                _midSamples.Clear();
                _exitSamples.Clear();
                _allSamples.Clear();
                AddSample(in s);
            }
        }
        else
        {
            // Track apex
            if (absLatG > _peakLateralG)
            {
                _peakLateralG = absLatG;
                _lapPosAtApex = s.NormalizedLapPos;
            }

            _latGSum += s.AccGLateral;
            AddSample(in s);

            // Close hysteresis: steer below threshold for CloseHoldSeconds
            if (absSteer < SteerCloseThreshold)
            {
                if (!_waitingClose)
                {
                    _waitingClose  = true;
                    _lowSteerSince = s.Timestamp;
                }
                else if ((s.Timestamp - _lowSteerSince).TotalSeconds >= CloseHoldSeconds)
                {
                    return FinalizeCorner(s.Timestamp);
                }
            }
            else
            {
                _waitingClose = false;
            }
        }

        return null;
    }

    private void AddSample(in TelemetrySample s)
    {
        _allSamples.Add(s);

        if      (s.Brake    > BrakeEntryThreshold)   _entrySamples.Add(s);
        else if (s.Throttle > ThrottleExitThreshold) _exitSamples.Add(s);
        else                                          _midSamples.Add(s);
    }

    // ── Corner finalization ────────────────────────────────────────────────────

    private CornerSummary FinalizeCorner(DateTime endTime)
    {
        var allArr   = _allSamples.ToArray();
        var entryArr = _entrySamples.ToArray();
        var midArr   = _midSamples.ToArray();
        var exitArr  = _exitSamples.ToArray();

        var totalFrame = FeatureExtractor.ExtractFrame(allArr, allArr.Length);
        var entryFrame = entryArr.Length > 0
            ? FeatureExtractor.ExtractFrame(entryArr, entryArr.Length) : default;
        var midFrame   = midArr.Length   > 0
            ? FeatureExtractor.ExtractFrame(midArr,   midArr.Length)   : default;
        var exitFrame  = exitArr.Length  > 0
            ? FeatureExtractor.ExtractFrame(exitArr,  exitArr.Length)  : default;

        var dir = _latGSum > 0.0 ? CornerDirection.Left : CornerDirection.Right;

        var dominant = DeriveDominant(in totalFrame);

        var summary = new CornerSummary
        {
            CornerIndex  = _cornerIndex++,
            StartTime    = _cornerStart,
            EndTime      = endTime,
            Direction    = dir,
            LapPos       = _lapPosAtApex,
            PeakLateralG = _peakLateralG,
            SampleCount  = allArr.Length,
            EntryFrame   = entryFrame,
            MidFrame     = midFrame,
            ExitFrame    = exitFrame,
            TotalFrame   = totalFrame,
            Dominant     = dominant,
        };

        // Reset in-corner state
        _inCorner = false;
        _entrySamples.Clear();
        _midSamples.Clear();
        _exitSamples.Clear();
        _allSamples.Clear();

        // Store in history
        _latest = summary;
        _completed.Add(summary);
        while (_completed.Count > _maxHistory)
            _completed.RemoveAt(0);

        return summary;
    }

    // ── Shared dominant-issue helper ───────────────────────────────────────────

    internal static string DeriveDominant(in FeatureFrame tf)
    {
        var usMax = Math.Max(tf.UndersteerEntry, Math.Max(tf.UndersteerMid, tf.UndersteerExit));
        var osMax = Math.Max(tf.OversteerEntry,  tf.OversteerExit);

        if (tf.LockupRatioFront >= tf.WheelspinRatioRear && tf.LockupRatioFront > 0.25f)
            return "LOCK";
        if (tf.WheelspinRatioRear > tf.LockupRatioFront  && tf.WheelspinRatioRear > 0.25f)
            return "SPIN";
        if (usMax >= osMax && usMax > 0.10f) return "US";
        if (osMax > usMax  && osMax > 0.10f) return "OS";
        return "OK";
    }
}
