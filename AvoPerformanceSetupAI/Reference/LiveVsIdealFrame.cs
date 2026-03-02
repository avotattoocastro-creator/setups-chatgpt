namespace AvoPerformanceSetupAI.Reference;

/// <summary>
/// Point-in-time deltas between live telemetry and the loaded
/// <see cref="ReferenceLap"/>.
/// </summary>
/// <remarks>
/// <b>Distance alignment</b>: every field is looked up by
/// <see cref="LapDistPct"/> — never by elapsed time.
/// <br/>
/// <b>Sign convention</b>: Live − Ideal.
/// Positive DeltaSpeedKmh means the driver is faster than the reference at
/// this track position; negative means slower.
/// <br/>
/// <b>Smoothing</b>: all delta values are EMA-smoothed with
/// α = <see cref="ReferenceLapComparator.EmaAlpha"/> before being stored
/// here to avoid noisy UI updates.
/// </remarks>
public sealed class LiveVsIdealFrame
{
    /// <summary>Normalised lap distance used to look up this frame (0..1).</summary>
    public float LapDistPct        { get; set; }

    /// <summary>
    /// EMA-smoothed speed delta (km/h): Live − Ideal.
    /// Positive = driver carries more speed than the reference.
    /// </summary>
    public float DeltaSpeedKmh     { get; set; }

    /// <summary>
    /// EMA-smoothed brake delta (0..1): Live − Ideal.
    /// Positive = driver applies more brake pressure than the reference.
    /// </summary>
    public float DeltaBrake        { get; set; }

    /// <summary>
    /// EMA-smoothed throttle delta (0..1): Live − Ideal.
    /// Positive = driver is more on throttle than the reference.
    /// </summary>
    public float DeltaThrottle     { get; set; }

    /// <summary>
    /// EMA-smoothed yaw-gain delta: Live − Ideal.
    /// Positive = more rotation (or more oversteer tendency) than the reference.
    /// </summary>
    public float DeltaYawGain      { get; set; }

    /// <summary>
    /// EMA-smoothed steer delta (rad): Live − Ideal.
    /// Positive = more steering lock than the reference.
    /// </summary>
    public float DeltaSteer        { get; set; }

    /// <summary>
    /// Human-readable summary generated when a notable event is detected,
    /// e.g. "You brake 6.3 m late vs ideal".
    /// Empty string when no notable event is active.
    /// </summary>
    public string SummaryText      { get; set; } = string.Empty;
}
