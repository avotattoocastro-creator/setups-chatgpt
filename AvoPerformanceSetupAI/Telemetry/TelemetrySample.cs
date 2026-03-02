using System;

namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Immutable snapshot of one Assetto Corsa telemetry frame captured from shared memory.
/// Per-wheel field order: FL = 0, FR = 1, RL = 2, RR = 3.
/// </summary>
public readonly record struct TelemetrySample
{
    public DateTime Timestamp { get; init; }

    // ── Driver inputs ─────────────────────────────────────────────────────────

    /// <summary>Throttle pedal position 0..1.</summary>
    public float Throttle   { get; init; }
    /// <summary>Brake pedal position 0..1.</summary>
    public float Brake      { get; init; }
    /// <summary>Clutch pedal position 0..1.</summary>
    public float Clutch     { get; init; }
    /// <summary>Steering wheel angle in radians (positive = left in AC).</summary>
    public float SteerAngle { get; init; }
    /// <summary>Selected gear: 0 = Reverse, 1 = Neutral, 2 = 1st, 3 = 2nd …</summary>
    public int   Gear       { get; init; }
    /// <summary>Engine revolutions per minute.</summary>
    public int   Rpms       { get; init; }
    /// <summary>Vehicle speed in km/h.</summary>
    public float SpeedKmh   { get; init; }
    /// <summary>Remaining fuel in litres.</summary>
    public float Fuel       { get; init; }

    // ── G-forces ──────────────────────────────────────────────────────────────

    /// <summary>Lateral G-force (accG[0]); positive = left.</summary>
    public float AccGLateral      { get; init; }
    /// <summary>Vertical G-force (accG[1]).</summary>
    public float AccGVertical     { get; init; }
    /// <summary>Longitudinal G-force (accG[2]); positive = forward.</summary>
    public float AccGLongitudinal { get; init; }

    // ── Yaw rate ──────────────────────────────────────────────────────────────

    /// <summary>Body yaw rate in rad/s (localAngularVel[1]).</summary>
    public float YawRate { get; init; }

    // ── Per-wheel — tyre slip ─────────────────────────────────────────────────

    public float WheelSlipFL { get; init; }
    public float WheelSlipFR { get; init; }
    public float WheelSlipRL { get; init; }
    public float WheelSlipRR { get; init; }

    // ── Per-wheel — tyre core temperature (°C) ───────────────────────────────

    public float TyreTempFL { get; init; }
    public float TyreTempFR { get; init; }
    public float TyreTempRL { get; init; }
    public float TyreTempRR { get; init; }

    // ── Per-wheel — tyre pressure (bar) ──────────────────────────────────────

    public float TyrePressureFL { get; init; }
    public float TyrePressureFR { get; init; }
    public float TyrePressureRL { get; init; }
    public float TyrePressureRR { get; init; }

    // ── Per-wheel — normalised brake pressure (0..1) ─────────────────────────

    public float BrakePressureFL { get; init; }
    public float BrakePressureFR { get; init; }
    public float BrakePressureRL { get; init; }
    public float BrakePressureRR { get; init; }

    // ── Per-wheel — tyre slip angle (rad) ────────────────────────────────────

    public float SlipAngleFL { get; init; }
    public float SlipAngleFR { get; init; }
    public float SlipAngleRL { get; init; }
    public float SlipAngleRR { get; init; }

    // ── Session context  (acpmf_graphics) ────────────────────────────────────

    /// <summary>Normalised position on the lap spline: 0 = start/finish, 1 = start/finish.</summary>
    public float NormalizedLapPos { get; init; }
    /// <summary>Elapsed lap time in milliseconds.</summary>
    public int   LapTimeMs        { get; init; }
    /// <summary>Assetto Corsa session status (0 = Off, 1 = Replay, 2 = Live, 3 = Pause).</summary>
    public int   AcStatus         { get; init; }

    // ── Session statics  (acpmf_static — constant for the whole session) ─────

    /// <summary>Maximum engine RPM for the current car (from acpmf_static).</summary>
    public int   MaxRpm           { get; init; }
    /// <summary>Maximum fuel capacity in litres for the current car (from acpmf_static).</summary>
    public float MaxFuel          { get; init; }
}
