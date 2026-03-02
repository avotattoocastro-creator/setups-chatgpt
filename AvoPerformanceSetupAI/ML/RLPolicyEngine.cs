using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Profiles;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ML;

// ── State ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Discretized state representation for the Q-learning engine.
/// Each field holds a bucket index — 0 = Low, 1 = Mid, 2 = High — derived by
/// splitting the continuous signal into three equal-width bands.
/// The 3-bucket scheme yields 3⁹ = 19 683 possible states, keeping the Q-table
/// small enough for real-time on-device storage.
/// </summary>
public readonly record struct RLState
{
    /// <summary>Bucketed <c>UndersteerMid</c> index (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte UndersteerMid             { get; init; }

    /// <summary>Bucketed <c>OversteerEntry</c> index (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte OversteerEntry            { get; init; }

    /// <summary>Bucketed <c>WheelspinRatioRear</c> index (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte WheelspinRear             { get; init; }

    /// <summary>Bucketed <c>LockupRatioFront</c> index (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte LockupFront               { get; init; }

    /// <summary>Bucketed <c>BalanceScore</c> (0 = Low &lt;40, 1 = Mid 40–70, 2 = High &gt;70).</summary>
    public byte BalanceScore              { get; init; }

    /// <summary>Bucketed <c>StabilityScore</c> (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte StabilityScore            { get; init; }

    /// <summary>Bucketed <c>TractionScore</c> (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte TractionScore             { get; init; }

    /// <summary>Bucketed driver <c>AggressivenessIndex</c> (0 = Low, 1 = Mid, 2 = High).</summary>
    public byte DriverAggressivenessIndex { get; init; }

    /// <summary>
    /// Bucketed driver <c>PreferredBalanceBias</c>:
    /// 0 = Negative (≤ −0.20), 1 = Neutral (−0.20..+0.20), 2 = Positive (≥ +0.20).
    /// </summary>
    public byte PreferredBalanceBias      { get; init; }

    /// <summary>
    /// Returns a compact 9-character key string used as part of the Q-table lookup key.
    /// Each character is '0', '1', or '2'.
    /// </summary>
    public string ToKey() =>
        $"{UndersteerMid}{OversteerEntry}{WheelspinRear}{LockupFront}" +
        $"{BalanceScore}{StabilityScore}{TractionScore}" +
        $"{DriverAggressivenessIndex}{PreferredBalanceBias}";
}

// ── Action ────────────────────────────────────────────────────────────────────

/// <summary>
/// Encoded representation of a setup-parameter action for the Q-learning engine.
/// Matches the encoding used by <see cref="ImpactModelTrainer"/> so that the same
/// integer codes are used consistently across training and inference.
/// </summary>
/// <remarks>
/// <c>SectionEncoded</c> and <c>ParameterEncoded</c> hold integer-valued codes
/// (1-based, 0 = unknown) stored as <c>float</c> for consistency with
/// <see cref="ImpactTrainingSample"/> and the ML.NET feature pipeline, which
/// represent all features as <c>float</c> arrays.
/// </remarks>
public readonly record struct RLAction
{
    /// <summary>
    /// Integer-encoded setup section (1-based; see
    /// <see cref="ImpactModelTrainer.EncodedSections"/>). 0 = unknown.
    /// Stored as <c>float</c> to match the ML.NET feature pipeline convention.
    /// </summary>
    public float SectionEncoded   { get; init; }

    /// <summary>
    /// Integer-encoded parameter within the section (see
    /// <see cref="ImpactModelTrainer.EncodedParameters"/>). 0 = unknown.
    /// Stored as <c>float</c> to match the ML.NET feature pipeline convention.
    /// </summary>
    public float ParameterEncoded { get; init; }

    /// <summary>
    /// Numeric adjustment value (same sign and magnitude as <c>Proposal.Delta</c>),
    /// rounded to two decimal places.
    /// </summary>
    public float DeltaValue       { get; init; }

    /// <summary>Returns a compact key string identifying this action in the Q-table.</summary>
    public string ToKey() =>
        $"{SectionEncoded:F0}_{ParameterEncoded:F0}_{DeltaValue:F2}";
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Lightweight on-device Q-learning engine that maintains a Q-table of
/// (state, action) → expected reward values and gradually improves setup
/// recommendations from real A/B test outcomes.
/// </summary>
/// <remarks>
/// <para><b>State</b>: 9-dimensional, each dimension bucketed to 3 levels
/// (Low / Mid / High), yielding at most 19 683 distinct states.</para>
/// <para><b>Action</b>: an encoded setup-parameter change
/// (section + parameter + signed delta).</para>
/// <para><b>Reward</b>: derived from <see cref="AbTestResult"/> metrics,
/// penalised for high-risk changes and driver inconsistency.</para>
/// <para><b>Update rule</b> (single-step episodic, no next-state):
/// <c>Q(s,a) ← Q(s,a) + α × (reward − Q(s,a))</c>.</para>
/// <para><b>Blend</b>: <see cref="UltraSetupAdvisor.Advise"/> computes
/// <c>FinalScore = <see cref="MlWeight"/> × mlScore + <see cref="RlWeight"/> × Q(s,a)</c>
/// when <see cref="IsEnabled"/> is <see langword="true"/>.</para>
/// <para>
/// Thread-safety: this class is <b>not</b> thread-safe. Callers should access
/// it from a single thread (typically the UI/telemetry thread).
/// </para>
/// </remarks>
public sealed class RLPolicyEngine
{
    // ── Blend weights ─────────────────────────────────────────────────────────

    /// <summary>Weight applied to the ML/heuristic score in the final blend (60 %).</summary>
    public const float MlWeight = 0.60f;

    /// <summary>Weight applied to the RL Q-value in the final blend (40 %).</summary>
    public const float RlWeight = 0.40f;

    // ── Hyperparameters ───────────────────────────────────────────────────────

    /// <summary>Q-learning rate α (0..1). Higher values adapt faster to new observations.</summary>
    public float LearningRate   { get; set; } = 0.10f;

    /// <summary>Discount factor γ (0..1). Kept for future multi-step extensions.</summary>
    public float DiscountFactor { get; set; } = 0.90f;

    // ── State-space bucketing thresholds ─────────────────────────────────────

    /// <summary>Boundary between Low and Mid buckets for 0..1 telemetry indices.</summary>
    private const float IndexBucketLowMid  = 0.33f;

    /// <summary>Boundary between Mid and High buckets for 0..1 telemetry indices.</summary>
    private const float IndexBucketMidHigh = 0.67f;

    /// <summary>Boundary between Low and Mid buckets for 0..100 score values.</summary>
    private const float ScoreBucketLowMid  = 40f;

    /// <summary>Boundary between Mid and High buckets for 0..100 score values.</summary>
    private const float ScoreBucketMidHigh = 70f;

    /// <summary><c>PreferredBalanceBias</c> threshold at or below which the bucket is Negative.</summary>
    private const float BiasBucketNegThreshold = -0.20f;

    /// <summary><c>PreferredBalanceBias</c> threshold above which the bucket is Positive.</summary>
    private const float BiasBucketPosThreshold =  0.20f;

    // ── Reward weights ────────────────────────────────────────────────────────

    /// <summary>Reward weight for balance improvement (mirrors training-label weight).</summary>
    private const float RewardWeightBalance   = 0.30f;

    /// <summary>Reward weight for traction improvement.</summary>
    private const float RewardWeightTraction  = 0.25f;

    /// <summary>Reward weight for brake improvement.</summary>
    private const float RewardWeightBrake     = 0.20f;

    /// <summary>Reward weight for stability improvement.</summary>
    private const float RewardWeightStability = 0.25f;

    /// <summary>
    /// Scale factor mapping normalised metric deltas (typically 0..0.5 range)
    /// to the 0..30 reward scale used by <see cref="UltraSetupAdvisor"/>.
    /// </summary>
    private const float RewardScaleFactor = 60f;

    /// <summary>Fixed reward penalty applied when the tested change was <c>High</c> risk.</summary>
    private const float HighRiskRewardPenalty   = 3f;

    /// <summary>Fixed reward penalty applied when the tested change was <c>Medium</c> risk.</summary>
    private const float MediumRiskRewardPenalty = 1f;

    // ── Enable flag ───────────────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/>, <see cref="UltraSetupAdvisor.Advise"/> queries
    /// this engine and blends its Q-values into the final candidate score.
    /// Defaults to <see langword="false"/> so existing behaviour is unchanged
    /// until the caller explicitly opts in.
    /// </summary>
    public static bool IsEnabled { get; set; }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Full path of the Q-table JSON file on the local machine:
    /// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\rl_qtable.json</c>.
    /// </summary>
    public static string DefaultQTablePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "rl_qtable.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    // ── Q-table ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Q-table mapping compact key strings to Q-values.
    /// Key format: <c>"{stateKey}|{actionKey}"</c>.
    /// </summary>
    private Dictionary<string, float> _qTable = new();

    /// <summary>Number of (state, action) pairs currently stored in the Q-table.</summary>
    public int EntryCount => _qTable.Count;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the stored Q-value for the given state–action pair,
    /// or <c>0</c> when the pair has not yet been observed.
    /// </summary>
    public float GetQValue(in RLState state, in RLAction action)
        => _qTable.TryGetValue(MakeKey(in state, in action), out var q) ? q : 0f;

    /// <summary>
    /// Performs a single-step Q-learning update:
    /// <c>Q(s,a) ← Q(s,a) + α × (reward − Q(s,a))</c>.
    /// </summary>
    /// <param name="state">State observed immediately before the action was applied.</param>
    /// <param name="action">The action (setup change) that was applied.</param>
    /// <param name="reward">
    /// Observed reward signal produced by <see cref="ComputeReward"/>
    /// after the resulting A/B test completed.
    /// </param>
    public void UpdateQ(in RLState state, in RLAction action, float reward)
    {
        var key     = MakeKey(in state, in action);
        float current = _qTable.TryGetValue(key, out var q) ? q : 0f;
        // Single-step episodic Q-update (no follow-on state max term needed)
        _qTable[key] = current + LearningRate * (reward - current);
    }

    /// <summary>
    /// Computes a reward signal in the range [−15, 30] from a completed A/B test.
    /// </summary>
    /// <param name="result">Completed A/B test result.</param>
    /// <param name="risk">Risk level of the tested setup change.</param>
    /// <param name="consistencyIndex">
    /// Driver consistency index (0..1) from <see cref="DriverProfile.ConsistencyIndex"/>.
    /// Lower consistency reduces the reward because noisy A/B data is less reliable.
    /// Defaults to 1 (no penalty).
    /// </param>
    public float ComputeReward(AbTestResult result, RiskLevel risk, float consistencyIndex = 1f)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var d = result.Deltas;

        // Improvement signals: negative delta = better for error indices;
        // positive delta = better for the stability score.
        float improvement =
            Math.Max(0f, -d.UndersteerMidAvg)      * RewardWeightBalance   +
            Math.Max(0f, -d.WheelspinRatioRearAvg) * RewardWeightTraction  +
            Math.Max(0f, -d.BrakeStabilityAvg)     * RewardWeightBrake     +
            Math.Max(0f,  d.StabilityScore)         * RewardWeightStability;

        // Deterioration signals (opposite sign from improvement)
        float deterioration =
            Math.Max(0f,  d.UndersteerMidAvg)      * RewardWeightBalance   +
            Math.Max(0f,  d.WheelspinRatioRearAvg) * RewardWeightTraction  +
            Math.Max(0f,  d.BrakeStabilityAvg)     * RewardWeightBrake     +
            Math.Max(0f, -d.StabilityScore)         * RewardWeightStability;

        float rawReward = (improvement - deterioration) * RewardScaleFactor;

        // Scale by A/B test confidence (low confidence → reduce reward magnitude)
        rawReward *= result.Confidence;

        // Risk penalties applied after confidence scaling
        rawReward -= risk switch
        {
            RiskLevel.High   => HighRiskRewardPenalty,
            RiskLevel.Medium => MediumRiskRewardPenalty,
            _                => 0f,
        };

        // Driver inconsistency penalty: an inconsistent driver produces noisier
        // A/B test data, so we down-weight the reward proportionally.
        rawReward *= Math.Clamp(consistencyIndex, 0f, 1f);

        return Math.Clamp(rawReward, -15f, 30f);
    }

    // ── State / action helpers (static, used by UltraSetupAdvisor) ────────────

    /// <summary>
    /// Builds a discretized <see cref="RLState"/> from the current telemetry
    /// context, optional driving scores, and optional driver profile.
    /// Missing optional values are replaced with neutral defaults.
    /// </summary>
    public static RLState BuildState(
        in FeatureFrame frame,
        DrivingScores?  scores,
        DriverProfile?  driverProfile)
        => new()
        {
            UndersteerMid             = BucketIndex(frame.UndersteerMid),
            OversteerEntry            = BucketIndex(frame.OversteerEntry),
            WheelspinRear             = BucketIndex(frame.WheelspinRatioRear),
            LockupFront               = BucketIndex(frame.LockupRatioFront),
            BalanceScore              = BucketScore(scores?.BalanceScore   ?? 50f),
            StabilityScore            = BucketScore(scores?.StabilityScore ?? 50f),
            TractionScore             = BucketScore(scores?.TractionScore  ?? 50f),
            DriverAggressivenessIndex = BucketIndex(driverProfile?.AggressivenessIndex ?? 0f),
            PreferredBalanceBias      = BucketBias(driverProfile?.PreferredBalanceBias  ?? 0f),
        };

    /// <summary>
    /// Builds an <see cref="RLAction"/> from a <see cref="Proposal"/> using the
    /// same section/parameter encoding as <see cref="ImpactModelTrainer"/>.
    /// Unknown sections or parameters produce an encoding of 0.
    /// </summary>
    public static RLAction ActionFromProposal(Proposal proposal)
    {
        if (proposal is null) throw new ArgumentNullException(nameof(proposal));

        ImpactModelTrainer.EncodedSections.TryGetValue(proposal.Section,     out var sec);
        ImpactModelTrainer.EncodedParameters.TryGetValue(proposal.Parameter, out var par);
        float deltaValue = float.TryParse(
            proposal.Delta,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var dv) ? dv : 0f;

        return new RLAction
        {
            SectionEncoded   = sec,
            ParameterEncoded = par,
            DeltaValue       = deltaValue,
        };
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the Q-table from <see cref="DefaultQTablePath"/>.
    /// Returns an empty (fresh) engine when the file does not exist or cannot
    /// be deserialised.
    /// </summary>
    public static RLPolicyEngine Load()
    {
        var engine = new RLPolicyEngine();
        if (!File.Exists(DefaultQTablePath)) return engine;
        try
        {
            var json  = File.ReadAllText(DefaultQTablePath);
            var table = JsonSerializer.Deserialize<Dictionary<string, float>>(json, _jsonOptions);
            if (table is not null) engine._qTable = table;
        }
        catch
        {
            // Corrupted or incompatible file — start fresh silently.
            // Diagnostic note: the engine will rebuild the Q-table from new A/B tests.
        }
        return engine;
    }

    /// <summary>
    /// Persists the current Q-table to <see cref="DefaultQTablePath"/>.
    /// Creates the parent directory if necessary.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(DefaultQTablePath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(_qTable, _jsonOptions);
        File.WriteAllText(DefaultQTablePath, json);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MakeKey(in RLState state, in RLAction action)
        => $"{state.ToKey()}|{action.ToKey()}";

    /// <summary>Buckets a 0..1 telemetry index into 3 bins: 0 = Low, 1 = Mid, 2 = High.</summary>
    private static byte BucketIndex(float value) =>
        value < IndexBucketLowMid  ? (byte)0 :
        value < IndexBucketMidHigh ? (byte)1 : (byte)2;

    /// <summary>Buckets a 0..100 score into 3 bins: 0 = Low, 1 = Mid, 2 = High.</summary>
    private static byte BucketScore(float value) =>
        value < ScoreBucketLowMid  ? (byte)0 :
        value < ScoreBucketMidHigh ? (byte)1 : (byte)2;

    /// <summary>
    /// Buckets a −1..+1 balance bias into 3 bins:
    /// 0 = Negative (≤ −0.20), 1 = Neutral, 2 = Positive (≥ +0.20).
    /// </summary>
    private static byte BucketBias(float value) =>
        value <= BiasBucketNegThreshold ? (byte)0 :
        value <  BiasBucketPosThreshold ? (byte)1 : (byte)2;
}
