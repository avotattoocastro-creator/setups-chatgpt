namespace AvoPerformanceSetupAI.Reference;

/// <summary>
/// One sample on the fixed-distance reference lap grid (0..1 LapDistPct).
/// LapDistPct is the sole alignment key — all comparisons are distance-based,
/// never time-based.
/// </summary>
public sealed class ReferenceLapSample
{
    /// <summary>Normalised lap distance in [0, 1]. Primary index for distance alignment.</summary>
    public float LapDistPct        { get; set; }

    // ── Driver inputs ─────────────────────────────────────────────────────────

    /// <summary>Vehicle speed in km/h.</summary>
    public float SpeedKmh          { get; set; }

    /// <summary>Throttle pedal position 0..1.</summary>
    public float Throttle          { get; set; }

    /// <summary>Brake pedal position 0..1.</summary>
    public float Brake             { get; set; }

    /// <summary>Steering wheel angle in radians (AC convention: positive = left).</summary>
    public float Steering          { get; set; }

    /// <summary>Selected gear (0 = Reverse, 1 = Neutral, 2 = 1st …).</summary>
    public int   Gear              { get; set; }

    /// <summary>Engine revolutions per minute.</summary>
    public int   Rpm               { get; set; }

    // ── G-forces ──────────────────────────────────────────────────────────────

    /// <summary>Lateral G-force; positive = left.</summary>
    public float LatG              { get; set; }

    /// <summary>Longitudinal G-force; positive = forward.</summary>
    public float LongG             { get; set; }

    // ── Balance ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Yaw gain computed by <see cref="Telemetry.BalanceMetrics.ComputeYawGain"/>
    /// at the time this sample was recorded or imported. Near 1.0 = neutral.
    /// </summary>
    public float YawGain           { get; set; }

    // ── Slip angles ───────────────────────────────────────────────────────────

    /// <summary>Average absolute slip angle of the two front tyres (rad).</summary>
    public float SlipAngleFrontAvg { get; set; }

    /// <summary>Average absolute slip angle of the two rear tyres (rad).</summary>
    public float SlipAngleRearAvg  { get; set; }

    /// <summary>Average of rear wheel-slip magnitudes (normalised ratio).</summary>
    public float WheelSlipRearAvg  { get; set; }

    // ── Tyre temperatures ─────────────────────────────────────────────────────

    /// <summary>All-four-tyre average temperature (°C).</summary>
    public float TyreTempAvg       { get; set; }

    public float TyreTempFL        { get; set; }
    public float TyreTempFR        { get; set; }
    public float TyreTempRL        { get; set; }
    public float TyreTempRR        { get; set; }

    // ── Tyre pressures ────────────────────────────────────────────────────────

    /// <summary>Front-left tyre pressure (bar).</summary>
    public float TyrePressureFL    { get; set; }

    /// <summary>Front-right tyre pressure (bar).</summary>
    public float TyrePressureFR    { get; set; }

    /// <summary>Rear-left tyre pressure (bar).</summary>
    public float TyrePressureRL    { get; set; }

    /// <summary>Rear-right tyre pressure (bar).</summary>
    public float TyrePressureRR    { get; set; }
}
