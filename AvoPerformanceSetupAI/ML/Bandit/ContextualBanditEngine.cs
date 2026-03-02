using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvoPerformanceSetupAI.ML.Uncertainty;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Profiles;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ML.Bandit;

/// <summary>
/// Safe contextual bandit for exploring setup changes using Thompson Sampling
/// with Gaussian reward posteriors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Context bucketing</b>: the high-dimensional context (telemetry frame +
/// driving mode + driver profile) is hashed into a compact string key so that
/// arms with similar contexts share statistics.  The bucket encodes:
/// <list type="bullet">
///   <item>Understeer mid level (Low/Mid/High)</item>
///   <item>Oversteer entry level (Low/Mid/High)</item>
///   <item>Wheelspin rear level (Low/Mid/High)</item>
///   <item><see cref="DrivingMode"/> (Sprint / Endurance)</item>
///   <item>Driver aggressiveness bucket (Low/Mid/High)</item>
/// </list>
/// </para>
/// <para>
/// <b>Thompson Sampling</b>: for each arm in the current context bucket, a
/// reward sample is drawn from the Gaussian posterior N(μ, σ²).  The arm with
/// the highest sample is selected.  After the A/B test the arm's posterior is
/// updated via the conjugate Bayesian update rule.
/// </para>
/// <para>
/// <b>Safety gate</b>: exploration is only allowed when all three of the
/// following conditions hold:
/// <list type="number">
///   <item>The discriminator result is <see cref="RootCauseType.SetupLikely"/>
///     or <see cref="RootCauseType.Mixed"/>.</item>
///   <item>The calibrated confidence is &gt; <see cref="MinCalibratedConfidence"/>.</item>
///   <item>The uncertainty lower-80 % bound is ≥ <see cref="MinLower80"/>.</item>
/// </list>
/// </para>
/// <para>
/// Thread-safety: this class is <b>not</b> thread-safe.
/// </para>
/// </remarks>
public sealed class ContextualBanditEngine
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Initial prior variance on reward for unseen arms.</summary>
    public const float PriorVariance = 25f;   // σ² = 25 → σ = 5 score-delta units

    /// <summary>
    /// Assumed noise variance of the observed reward signal.
    /// Lower values → faster posterior contraction per observation.
    /// </summary>
    public const float NoiseVariance = 16f;   // σ_noise² = 16

    /// <summary>
    /// Minimum calibrated confidence required before the engine will recommend
    /// an exploratory arm.
    /// </summary>
    public const float MinCalibratedConfidence = 0.55f;

    /// <summary>
    /// Minimum lower-80 % uncertainty bound (score-delta units) required.
    /// Exploratory arms with lower-80 % below this threshold are suppressed.
    /// </summary>
    public const float MinLower80 = -1.0f;

    /// <summary>
    /// Number of arms that must have been pulled before exploration rate
    /// decreases noticeably (soft-ramp decay denominator).
    /// </summary>
    public const int ExplorationDecayBase = 20;

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Default path for the bandit state JSON.
    /// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\bandit_state.json</c>
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "bandit_state.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    // ── State ─────────────────────────────────────────────────────────────────

    private BanditState _state = new();
    private readonly Random _rng;

    /// <summary>Cached second Box-Muller sample (or NaN when no cache is available).</summary>
    private float _cachedNormal = float.NaN;

    /// <summary>Total pulls recorded across all arms and contexts.</summary>
    public int TotalPulls => _state.TotalPulls;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new engine.  Loads persisted state from
    /// <paramref name="filePath"/> if it exists.
    /// </summary>
    /// <param name="filePath">Optional override for the JSON state file.</param>
    /// <param name="seed">Optional RNG seed (for deterministic tests).</param>
    public ContextualBanditEngine(string? filePath = null, int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        Load(filePath ?? DefaultFilePath);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Given a set of <paramref name="candidates"/>, selects the arm to
    /// explore using Thompson Sampling — subject to the safety gate.
    /// </summary>
    /// <param name="candidates">
    /// Proposals enriched with <see cref="UncertaintyEstimate"/> and calibrated
    /// confidence.
    /// </param>
    /// <param name="context">
    /// Current telemetry + driver context used for context bucketing.
    /// </param>
    /// <param name="rootCause">Discriminator result for the safety gate.</param>
    /// <returns>
    /// The selected <see cref="Proposal"/>, or <see langword="null"/> if the
    /// safety gate blocks exploration entirely.
    /// </returns>
    public Proposal? SelectArm(
        IReadOnlyList<BanditCandidate> candidates,
        BanditContext                  context,
        RootCauseResult                rootCause)
    {
        if (candidates is null || candidates.Count == 0) return null;

        // ── Safety gate ───────────────────────────────────────────────────────
        if (rootCause.Cause == RootCauseType.DriverLikely)
            return null;   // discriminator says setup isn't the issue

        string contextKey = BucketContext(context);

        // Filter candidates that pass the per-arm safety constraints
        var eligible = new List<BanditCandidate>(candidates.Count);
        foreach (var c in candidates)
        {
            if (c.CalibratedConfidence < MinCalibratedConfidence) continue;
            if (c.Uncertainty.Lower80  < MinLower80)              continue;
            eligible.Add(c);
        }
        if (eligible.Count == 0) return null;

        // Current exploration rate: decays softly as TotalPulls grows.
        // At 0 pulls  → rate = 1.0 (pure explore)
        // At 20 pulls → rate ≈ 0.5
        // At 100 pulls → rate ≈ 0.17
        float explorationRate = ExplorationDecayBase / (float)(ExplorationDecayBase + _state.TotalPulls);

        // Thompson Sampling: draw one reward sample per eligible arm
        BanditCandidate? selected = null;
        float highestSample       = float.MinValue;

        foreach (var c in eligible)
        {
            string armKey = ArmKey(c.Proposal);
            var stats     = GetOrCreateArm(contextKey, armKey);

            // With probability explorationRate: explore by sampling from prior/posterior.
            // With probability (1 − explorationRate): exploit via posterior mean.
            float sample;
            if ((float)_rng.NextDouble() < explorationRate)
            {
                // Thompson sample from N(μ_posterior, σ²_posterior)
                float sigma = MathF.Sqrt(stats.VarPosterior);
                sample      = stats.MuPosterior + sigma * SampleStandardNormal();
            }
            else
            {
                // Exploit: use posterior mean directly
                sample = stats.MuPosterior;
            }

            if (sample > highestSample)
            {
                highestSample = sample;
                selected      = c;
            }
        }

        return selected?.Proposal;
    }

    /// <summary>
    /// Updates the bandit posterior for the arm that was pulled after the
    /// A/B test completes.
    /// </summary>
    /// <param name="proposal">The proposal that was A/B tested.</param>
    /// <param name="context">Context at the time the arm was selected.</param>
    /// <param name="actualReward">
    /// Observed reward — typically the <c>DeltaOverallScore</c> from
    /// <see cref="ImpactModelTrainer.AppendSample"/> or a simple ±10 proxy.
    /// </param>
    /// <param name="filePath">Optional override for the JSON state file.</param>
    public void UpdateArm(
        Proposal      proposal,
        BanditContext  context,
        float         actualReward,
        string?       filePath = null)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        string contextKey = BucketContext(context);
        string armKey     = ArmKey(proposal);
        var    stats      = GetOrCreateArm(contextKey, armKey);

        // Conjugate Gaussian update:
        //   σ²_new = 1 / (1/σ²_prior + 1/σ²_noise)
        //   μ_new  = σ²_new × (μ_prior/σ²_prior + observed/σ²_noise)
        float varNew = 1f / (1f / stats.VarPosterior + 1f / NoiseVariance);
        float muNew  = varNew * (stats.MuPosterior / stats.VarPosterior + actualReward / NoiseVariance);

        stats.MuPosterior  = muNew;
        stats.VarPosterior = varNew;
        stats.PullCount++;
        stats.RewardSum   += actualReward;
        _state.TotalPulls++;

        Save(filePath ?? DefaultFilePath);
    }

    /// <summary>
    /// Returns the current arm statistics for a proposal in a given context,
    /// or <see langword="null"/> if the arm has never been pulled.
    /// </summary>
    public ArmStats? GetArmStats(Proposal proposal, BanditContext context)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));
        string contextKey = BucketContext(context);
        string armKey     = ArmKey(proposal);
        if (_state.ContextArms.TryGetValue(contextKey, out var arms) &&
            arms.TryGetValue(armKey, out var stats))
            return stats;
        return null;
    }

    // ── Context bucketing ─────────────────────────────────────────────────────

    /// <summary>
    /// Converts a rich <see cref="BanditContext"/> to a compact string bucket key
    /// by discretising each dimension to Low/Mid/High (or Sprint/Endurance).
    /// </summary>
    public static string BucketContext(BanditContext ctx)
    {
        string us   = BucketLevel(ctx.UndersteerMid);
        string os   = BucketLevel(ctx.OversteerEntry);
        string ws   = BucketLevel(ctx.WheelspinRear);
        string mode = ctx.DrivingMode == DrivingMode.Sprint ? "S" : "E";
        string aggr = BucketLevel(ctx.DriverAggressiveness);
        return $"{us}{os}{ws}{mode}{aggr}";
    }

    /// <summary>Encodes a proposal as a compact arm key.</summary>
    public static string ArmKey(Proposal p)
        => $"{p.Section}|{p.Parameter}|{p.Delta}";

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>Saves the bandit state to <paramref name="filePath"/>.</summary>
    public void Save(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(_state, _jsonOptions));
        }
        catch { /* best-effort */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Load(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<BanditState>(json, _jsonOptions);
            if (loaded != null) _state = loaded;
        }
        catch { /* malformed — start fresh */ }
    }

    private ArmStats GetOrCreateArm(string contextKey, string armKey)
    {
        if (!_state.ContextArms.TryGetValue(contextKey, out var arms))
        {
            arms = new Dictionary<string, ArmStats>();
            _state.ContextArms[contextKey] = arms;
        }
        if (!arms.TryGetValue(armKey, out var stats))
        {
            stats = new ArmStats { VarPosterior = PriorVariance };
            arms[armKey] = stats;
        }
        return stats;
    }

    /// <summary>Discretises a 0..1 index to L (low), M (mid), or H (high).</summary>
    private static string BucketLevel(float value)
        => value < 0.33f ? "L" : value < 0.67f ? "M" : "H";

    /// <summary>
    /// Generates a standard normal sample using the Box-Muller transform.
    /// Caches the second (sin) sample to halve the number of transcendental
    /// function calls.
    /// </summary>
    private float SampleStandardNormal()
    {
        if (!float.IsNaN(_cachedNormal))
        {
            float cached = _cachedNormal;
            _cachedNormal = float.NaN;
            return cached;
        }

        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double mag = Math.Sqrt(-2.0 * Math.Log(u1));
        _cachedNormal = (float)(mag * Math.Sin(2.0 * Math.PI * u2));
        return          (float)(mag * Math.Cos(2.0 * Math.PI * u2));
    }
}

// ── Context and candidate DTOs ────────────────────────────────────────────────

/// <summary>
/// Compact context representation passed to <see cref="ContextualBanditEngine.SelectArm"/>
/// and <see cref="ContextualBanditEngine.UpdateArm"/>.
/// </summary>
public sealed class BanditContext
{
    /// <summary>Understeer mid-corner index (0..1).</summary>
    public float UndersteerMid       { get; init; }

    /// <summary>Oversteer entry index (0..1).</summary>
    public float OversteerEntry      { get; init; }

    /// <summary>Rear wheelspin index (0..1).</summary>
    public float WheelspinRear       { get; init; }

    /// <summary>Current driving mode.</summary>
    public DrivingMode DrivingMode   { get; init; }

    /// <summary>Driver aggressiveness index (0..1), from <see cref="DriverProfile"/>.</summary>
    public float DriverAggressiveness { get; init; }

    /// <summary>
    /// Optional track temperature (°C). Not bucketed — included for future extensions.
    /// </summary>
    public float? TrackTemp          { get; init; }

    /// <summary>
    /// Builds a <see cref="BanditContext"/> from existing telemetry objects.
    /// </summary>
    public static BanditContext From(
        in AvoPerformanceSetupAI.Telemetry.FeatureFrame frame,
        DrivingMode                                     mode,
        DriverProfile?                                  driverProfile = null,
        float?                                          trackTemp     = null)
        => new()
        {
            UndersteerMid        = frame.UndersteerMid,
            OversteerEntry       = frame.OversteerEntry,
            WheelspinRear        = frame.WheelspinRatioRear,
            DrivingMode          = mode,
            DriverAggressiveness = driverProfile?.AggressivenessIndex ?? 0f,
            TrackTemp            = trackTemp,
        };
}

/// <summary>
/// A proposal enriched with the data needed by <see cref="ContextualBanditEngine.SelectArm"/>.
/// </summary>
public sealed class BanditCandidate
{
    /// <summary>The underlying setup-change proposal.</summary>
    public Proposal Proposal { get; init; } = new();

    /// <summary>Probabilistic prediction estimate.</summary>
    public UncertaintyEstimate Uncertainty { get; init; }

    /// <summary>Calibrated confidence from <see cref="ConfidenceCalibrationEngine"/>.</summary>
    public float CalibratedConfidence { get; init; }
}
