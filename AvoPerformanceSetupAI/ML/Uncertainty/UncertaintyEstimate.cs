namespace AvoPerformanceSetupAI.ML.Uncertainty;

/// <summary>
/// Probabilistic estimate of a predicted score delta, combining ensemble
/// variance with conformal prediction intervals.
/// Produced by <see cref="EnsembleImpactPredictor"/> and enriched by
/// <see cref="ConformalPredictor"/>.
/// </summary>
public record struct UncertaintyEstimate
{
    /// <summary>Mean predicted score delta across the N ensemble models.</summary>
    public float Mean      { get; init; }

    /// <summary>
    /// Standard deviation of the N ensemble predictions.
    /// A low value indicates the models agree; a high value indicates
    /// disagreement and thus higher intrinsic uncertainty.
    /// </summary>
    public float StdDev    { get; init; }

    /// <summary>Lower bound of the 80 % conformal prediction interval.</summary>
    public float Lower80   { get; init; }

    /// <summary>Upper bound of the 80 % conformal prediction interval.</summary>
    public float Upper80   { get; init; }

    /// <summary>Lower bound of the 95 % conformal prediction interval.</summary>
    public float Lower95   { get; init; }

    /// <summary>Upper bound of the 95 % conformal prediction interval.</summary>
    public float Upper95   { get; init; }

    /// <summary>
    /// Empirical coverage fraction for the 80 % interval over the calibration set.
    /// When the calibration set is empty, this is initialised to <c>0.80f</c>.
    /// </summary>
    public float Coverage80 { get; init; }

    /// <summary>
    /// Empirical coverage fraction for the 95 % interval over the calibration set.
    /// When the calibration set is empty, this is initialised to <c>0.95f</c>.
    /// </summary>
    public float Coverage95 { get; init; }

    // ── Derived helpers ───────────────────────────────────────────────────────

    /// <summary>Width of the 80 % interval: <see cref="Upper80"/> − <see cref="Lower80"/>.</summary>
    public float Width80 => Upper80 - Lower80;

    /// <summary>Width of the 95 % interval: <see cref="Upper95"/> − <see cref="Lower95"/>.</summary>
    public float Width95 => Upper95 - Lower95;

    /// <summary>
    /// <see langword="true"/> when the lower bound of the 80 % interval is
    /// non-negative, i.e. the model predicts improvement even in the worst
    /// likely case at 80 % coverage.
    /// </summary>
    public bool IsPositiveLower80 => Lower80 >= 0f;

    /// <summary>
    /// Returns a compact one-line summary for logging, e.g.
    /// "μ=+5.2 σ=1.4 [80%: +1.3..+9.1] [95%: −0.8..+11.2]".
    /// </summary>
    public override string ToString() =>
        $"μ={Mean:+0.0;-0.0} σ={StdDev:F1} " +
        $"[80%: {Lower80:+0.0;-0.0}..{Upper80:+0.0;-0.0}] " +
        $"[95%: {Lower95:+0.0;-0.0}..{Upper95:+0.0;-0.0}]";
}
