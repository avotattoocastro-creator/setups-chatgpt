using System;
using System.Collections.Generic;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.Reference;

/// <summary>
/// Compares live <see cref="TelemetrySample"/> data to a loaded
/// <see cref="ReferenceLap"/>, producing <see cref="LiveVsIdealFrame"/>
/// deltas aligned exclusively by <see cref="TelemetrySample.NormalizedLapPos"/>
/// (distance-based — time is never used for alignment).
/// </summary>
/// <remarks>
/// <para>
/// <b>Distance alignment</b>: for every live sample the nearest reference
/// point is found via a direct index computation on the uniform 1000-point grid
/// (<c>round(lapPct × 999)</c>).  No time stamps are compared.
/// </para>
/// <para>
/// <b>EMA smoothing</b>: raw per-channel deltas are low-pass filtered with
/// α = <see cref="EmaAlpha"/> before being exposed through the
/// <c>Current*</c> properties and the returned <see cref="LiveVsIdealFrame"/>.
/// This suppresses high-frequency noise (wheel-slip spikes, sensor jitter)
/// while preserving meaningful trends.
/// </para>
/// <para>
/// Thread-safety: not thread-safe.  Call from the UI thread only (the
/// 50 ms <c>DispatcherTimer</c> in <c>TelemetryViewModel</c>).
/// </para>
/// </remarks>
public sealed class ReferenceLapComparator
{
    // ── EMA constants ─────────────────────────────────────────────────────────

    /// <summary>
    /// EMA smoothing weight applied to each new raw delta.
    /// α = 0.15 gives a time-constant of ≈ 6 samples at 20 Hz, effectively
    /// a ~300 ms low-pass filter that suppresses sensor noise without
    /// introducing perceptible lag on genuine driver inputs.
    /// </summary>
    public const float EmaAlpha      = 0.15f;
    private const float EmaComplement = 1f - EmaAlpha;

    // ── AC status code ────────────────────────────────────────────────────────

    /// <summary>
    /// Assetto Corsa status value for a live driving session
    /// (acpmf_graphics.status = AC_LIVE = 2).
    /// </summary>
    private const int AcStatusLive = 2;

    // ── Brake-start event thresholds ──────────────────────────────────────────

    private const float BrakeThreshold           = 0.10f;  // 10 % pedal
    private const float BrakeOffsetSignificanceM = 3.0f;   // minimum notable offset (m)

    // ── Nominal track length used for pct → metres conversion ─────────────────

    /// <summary>
    /// Approximate track length (m) used to convert a LapDistPct difference
    /// into metres for the brake-start summary message.
    /// A real track length is not available here; the nominal value keeps the
    /// message in a plausible range and can be refined in future.
    /// </summary>
    private const float NominalTrackLengthM = 4_000f;

    // ── Reference data ────────────────────────────────────────────────────────

    private ReferenceLap? _reference;

    // ── EMA state ─────────────────────────────────────────────────────────────

    private float _emaSpeed;
    private float _emaBrake;
    private float _emaThrottle;
    private float _emaYawGain;
    private float _emaSteer;
    private bool  _emaInitialised;

    // ── Brake-start offset tracking ───────────────────────────────────────────

    private float? _refBrakeStartPct;   // LapDistPct where reference first brakes
    private float? _liveBrakeStartPct;  // LapDistPct where live driver first brakes
    private bool   _inBrakeZone;

    // ── Rolling 3-second buffer ───────────────────────────────────────────────

    private readonly record struct TimedFrame(DateTime Timestamp, LiveVsIdealFrame Frame);
    private readonly Queue<TimedFrame> _rollingBuffer = new();
    private static readonly TimeSpan  Rolling3s       = TimeSpan.FromSeconds(3.0);

    // ── Public read-only state ────────────────────────────────────────────────

    /// <summary><see langword="true"/> when a reference lap is loaded and comparisons are active.</summary>
    public bool IsActive => _reference is not null;

    /// <summary>Normalised lap position of the most recent processed sample (0..1).</summary>
    public float CurrentLapDistPct    { get; private set; }

    /// <summary>EMA-smoothed speed delta (km/h): Live − Ideal.</summary>
    public float CurrentDeltaSpeedKmh { get; private set; }

    /// <summary>EMA-smoothed brake delta (0..1): Live − Ideal.</summary>
    public float CurrentDeltaBrake    { get; private set; }

    /// <summary>EMA-smoothed throttle delta (0..1): Live − Ideal.</summary>
    public float CurrentDeltaThrottle { get; private set; }

    /// <summary>EMA-smoothed yaw-gain delta: Live − Ideal.</summary>
    public float CurrentDeltaYawGain  { get; private set; }

    /// <summary>EMA-smoothed steer delta (rad): Live − Ideal.</summary>
    public float CurrentDeltaSteer    { get; private set; }

    /// <summary>
    /// Most recent notable event text, e.g. "You brake 6.3 m late vs ideal".
    /// Empty string when no event is active.
    /// </summary>
    public string CurrentSummaryText  { get; private set; } = string.Empty;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a new reference lap and resets all EMA and brake-tracking state.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="lap"/> is null.</exception>
    public void LoadReference(ReferenceLap lap)
    {
        _reference = lap ?? throw new ArgumentNullException(nameof(lap));
        ResetState();
    }

    /// <summary>Removes the active reference and resets all state.</summary>
    public void ClearReference()
    {
        _reference = null;
        ResetState();
    }

    /// <summary>
    /// Processes one live telemetry sample.
    /// <list type="bullet">
    ///   <item>Looks up the nearest reference sample by LapDistPct
    ///   (O(1) index on the uniform 1000-point grid — distance-based only).</item>
    ///   <item>Computes raw deltas (Live − Ideal) for speed, brake, throttle,
    ///   yaw gain, and steer.</item>
    ///   <item>Applies EMA smoothing (α = <see cref="EmaAlpha"/>).</item>
    ///   <item>Updates all <c>Current*</c> properties.</item>
    ///   <item>Maintains the rolling 3-second buffer.</item>
    /// </list>
    /// Returns the current <see cref="LiveVsIdealFrame"/>, or
    /// <see langword="null"/> when no reference is loaded or the session is not Live.
    /// </summary>
    public LiveVsIdealFrame? Update(in TelemetrySample s)
    {
        if (_reference is null)               return null;
        if (s.AcStatus != AcStatusLive)       return null;
        if (_reference.Samples.Length == 0)   return null;

        var lapPct = Math.Clamp(s.NormalizedLapPos, 0f, 1f);

        // Distance-aligned lookup: O(1) on the uniform grid
        var refIdx = NearestGridIndex(_reference.Samples.Length, lapPct);
        var r      = _reference.Samples[refIdx];

        // Compute yaw gain for the live sample
        var liveYaw = BalanceMetrics.ComputeYawGain(s.YawRate, s.SteerAngle, s.SpeedKmh);

        // Raw deltas: Live − Ideal
        var rawSpeed    = s.SpeedKmh    - r.SpeedKmh;
        var rawBrake    = s.Brake       - r.Brake;
        var rawThrottle = s.Throttle    - r.Throttle;
        var rawYaw      = liveYaw       - r.YawGain;
        var rawSteer    = s.SteerAngle  - r.Steering;

        // EMA smoothing — seed on first sample to avoid a large initial spike
        if (!_emaInitialised)
        {
            _emaSpeed       = rawSpeed;
            _emaBrake       = rawBrake;
            _emaThrottle    = rawThrottle;
            _emaYawGain     = rawYaw;
            _emaSteer       = rawSteer;
            _emaInitialised = true;
        }
        else
        {
            _emaSpeed    = EmaAlpha * rawSpeed    + EmaComplement * _emaSpeed;
            _emaBrake    = EmaAlpha * rawBrake    + EmaComplement * _emaBrake;
            _emaThrottle = EmaAlpha * rawThrottle + EmaComplement * _emaThrottle;
            _emaYawGain  = EmaAlpha * rawYaw      + EmaComplement * _emaYawGain;
            _emaSteer    = EmaAlpha * rawSteer    + EmaComplement * _emaSteer;
        }

        CurrentLapDistPct    = lapPct;
        CurrentDeltaSpeedKmh = _emaSpeed;
        CurrentDeltaBrake    = _emaBrake;
        CurrentDeltaThrottle = _emaThrottle;
        CurrentDeltaYawGain  = _emaYawGain;
        CurrentDeltaSteer    = _emaSteer;

        // Brake-start offset detection (fires once per brake zone)
        UpdateBrakeTracking(in s, r, lapPct);

        var frame = new LiveVsIdealFrame
        {
            LapDistPct    = lapPct,
            DeltaSpeedKmh = _emaSpeed,
            DeltaBrake    = _emaBrake,
            DeltaThrottle = _emaThrottle,
            DeltaYawGain  = _emaYawGain,
            DeltaSteer    = _emaSteer,
            SummaryText   = CurrentSummaryText,
        };

        // Maintain rolling 3-second buffer
        var now = DateTime.UtcNow;
        _rollingBuffer.Enqueue(new TimedFrame(now, frame));
        while (_rollingBuffer.Count > 0 && (now - _rollingBuffer.Peek().Timestamp) > Rolling3s)
            _rollingBuffer.Dequeue();

        return frame;
    }

    /// <summary>
    /// Returns a <see cref="LiveVsIdealFrame"/> whose delta fields are the
    /// arithmetic mean of all frames in the rolling 3-second buffer.
    /// Returns a zeroed frame when the buffer is empty.
    /// </summary>
    public LiveVsIdealFrame GetRolling3sAverage()
    {
        if (_rollingBuffer.Count == 0) return new LiveVsIdealFrame();

        float sumSpeed = 0, sumBrake = 0, sumThrottle = 0, sumYaw = 0, sumSteer = 0;
        foreach (var tf in _rollingBuffer)
        {
            sumSpeed    += tf.Frame.DeltaSpeedKmh;
            sumBrake    += tf.Frame.DeltaBrake;
            sumThrottle += tf.Frame.DeltaThrottle;
            sumYaw      += tf.Frame.DeltaYawGain;
            sumSteer    += tf.Frame.DeltaSteer;
        }

        var n = (float)_rollingBuffer.Count;
        return new LiveVsIdealFrame
        {
            LapDistPct    = CurrentLapDistPct,
            DeltaSpeedKmh = sumSpeed    / n,
            DeltaBrake    = sumBrake    / n,
            DeltaThrottle = sumThrottle / n,
            DeltaYawGain  = sumYaw      / n,
            DeltaSteer    = sumSteer    / n,
        };
    }

    // ── Grid lookup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the index of the grid sample whose LapDistPct is nearest to
    /// <paramref name="lapPct"/>.
    /// O(1): the 1000-point grid is uniformly spaced so a direct multiply
    /// gives the exact (or nearest) index without binary search.
    /// </summary>
    private static int NearestGridIndex(int gridLength, float lapPct)
    {
        var approx = (int)MathF.Round(lapPct * (gridLength - 1));
        return Math.Clamp(approx, 0, gridLength - 1);
    }

    // ── Brake-start offset detection ──────────────────────────────────────────

    private void UpdateBrakeTracking(
        in TelemetrySample live,
        ReferenceLapSample refSample,
        float lapPct)
    {
        var liveBraking = live.Brake > BrakeThreshold;
        var refBraking  = refSample.Brake > BrakeThreshold;

        // Track where the reference first starts braking in this zone
        if (refBraking && _refBrakeStartPct is null)
            _refBrakeStartPct = refSample.LapDistPct;

        // Detect live brake-start (rising edge)
        if (liveBraking && !_inBrakeZone)
        {
            _inBrakeZone       = true;
            _liveBrakeStartPct = lapPct;

            if (_refBrakeStartPct.HasValue)
            {
                var offsetM = (_liveBrakeStartPct.Value - _refBrakeStartPct.Value)
                              * NominalTrackLengthM;

                CurrentSummaryText = MathF.Abs(offsetM) >= BrakeOffsetSignificanceM
                    ? $"You brake {MathF.Abs(offsetM):F1} m {(offsetM > 0f ? "late" : "early")} vs ideal"
                    : string.Empty;

                // Reset for the next brake zone
                _refBrakeStartPct  = null;
                _liveBrakeStartPct = null;
            }
        }
        else if (!liveBraking)
        {
            _inBrakeZone = false;
        }
    }

    // ── State reset ───────────────────────────────────────────────────────────

    private void ResetState()
    {
        _emaInitialised    = false;
        _emaSpeed          = 0f;
        _emaBrake          = 0f;
        _emaThrottle       = 0f;
        _emaYawGain        = 0f;
        _emaSteer          = 0f;
        _refBrakeStartPct  = null;
        _liveBrakeStartPct = null;
        _inBrakeZone       = false;
        CurrentLapDistPct  = 0f;
        CurrentDeltaSpeedKmh  = 0f;
        CurrentDeltaBrake     = 0f;
        CurrentDeltaThrottle  = 0f;
        CurrentDeltaYawGain   = 0f;
        CurrentDeltaSteer     = 0f;
        CurrentSummaryText    = string.Empty;
        _rollingBuffer.Clear();
    }
}
