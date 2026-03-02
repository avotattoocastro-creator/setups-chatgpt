namespace AvoPerformanceSetupAI.Telemetry;

/// <summary>
/// Session-level driving context that governs how setup proposals are scored
/// and ranked by <see cref="UltraSetupAdvisor"/> and how the 3-lap virtual
/// simulation is weighted by <see cref="VirtualLapSimulator"/>.
/// </summary>
public enum DrivingMode
{
    /// <summary>
    /// Short-format session (qualifying, sprint race).
    /// Lap 1 performance is weighted more heavily; traction and balance are
    /// prioritised over long-run stability.
    /// <para>
    /// OverallScore weights — Balance 35 %, Traction 30 %, Brake 20 %, Stability 15 %.
    /// </para>
    /// </summary>
    Sprint,

    /// <summary>
    /// Long-format session (endurance race).
    /// Lap 2 / Lap 3 consistency is weighted more heavily; stability and
    /// tyre management are prioritised over peak single-lap performance.
    /// <para>
    /// OverallScore weights — Stability 35 %, Traction 25 %, Balance 25 %, Brake 15 %.
    /// </para>
    /// </summary>
    Endurance,
}
