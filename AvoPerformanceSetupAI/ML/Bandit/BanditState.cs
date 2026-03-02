using System.Collections.Generic;

namespace AvoPerformanceSetupAI.ML.Bandit;

/// <summary>
/// Persistent, serialisable state snapshot for <see cref="ContextualBanditEngine"/>.
/// Contains all arm statistics needed to reconstruct the Thompson Sampling
/// Gaussian posterior on load.
/// </summary>
public sealed class BanditState
{
    /// <summary>
    /// Per-context-bucket arm statistics.
    /// Key: context bucket string (from <see cref="ContextualBanditEngine.BucketContext"/>).
    /// Value: dictionary of arm key → <see cref="ArmStats"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, ArmStats>> ContextArms { get; set; } = [];

    /// <summary>Total number of pulls recorded across all contexts and arms.</summary>
    public int TotalPulls { get; set; }
}

/// <summary>
/// Sufficient statistics for a single arm in a single context bucket.
/// Maintains a Gaussian posterior over the expected reward using
/// conjugate Bayesian updating with a known prior.
/// </summary>
public sealed class ArmStats
{
    // ── Bayesian Gaussian posterior: N(μ, σ²) ────────────────────────────────

    /// <summary>
    /// Posterior mean of the reward for this arm in this context.
    /// Initialised to 0 (prior: no expected improvement).
    /// </summary>
    public float MuPosterior    { get; set; }

    /// <summary>
    /// Posterior variance.
    /// Initialised to <see cref="ContextualBanditEngine.PriorVariance"/>.
    /// Decreases as more samples are observed (Bayesian update).
    /// </summary>
    public float VarPosterior   { get; set; } = ContextualBanditEngine.PriorVariance;

    /// <summary>Number of times this arm has been pulled in this context bucket.</summary>
    public int   PullCount      { get; set; }

    /// <summary>Running sum of observed rewards for this arm in this context.</summary>
    public float RewardSum      { get; set; }
}
