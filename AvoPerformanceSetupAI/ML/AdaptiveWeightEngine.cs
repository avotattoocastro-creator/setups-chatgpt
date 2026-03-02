using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ML;

// ── Per-test record ───────────────────────────────────────────────────────────

/// <summary>
/// Snapshot of accuracy metrics captured after a single completed A/B test.
/// Stored in the rolling 20-sample history inside <see cref="AdaptiveWeightEngine"/>.
/// </summary>
internal sealed class AbTestAccuracyRecord
{
    /// <summary>
    /// Normalised ML prediction error: |predicted − actual| / <see cref="AdaptiveWeightEngine.MaxScoreDelta"/>.
    /// Range [0, 1]; lower = more accurate prediction.
    /// </summary>
    public float MlNormalizedError  { get; init; }

    /// <summary>
    /// Normalised RL reward error: |Q_predicted − actualReward| / <see cref="AdaptiveWeightEngine.RewardRange"/>.
    /// Range [0, 1]; lower = more accurate prediction.
    /// </summary>
    public float RlNormalizedError  { get; init; }

    /// <summary>
    /// <see langword="true"/> when the A/B test <see cref="AbTestResult.Improved"/>
    /// flag was set, indicating the setup change was genuinely beneficial.
    /// </summary>
    public bool  Improved           { get; init; }
}

// ── Serialisable state ────────────────────────────────────────────────────────

/// <summary>
/// Serialisable snapshot of <see cref="AdaptiveWeightEngine"/> fields written
/// to / read from JSON.  Only the three weights are persisted; the rolling
/// history is volatile and is rebuilt from scratch on the next session.
/// </summary>
internal sealed class AdaptiveWeightState
{
    public float MlWeight        { get; set; } = AdaptiveWeightEngine.InitialMlWeight;
    public float RlWeight        { get; set; } = AdaptiveWeightEngine.InitialRlWeight;
    public float HeuristicWeight { get; set; } = AdaptiveWeightEngine.InitialHeuristicWeight;
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Dynamically maintains the blend weights used by
/// <see cref="AvoPerformanceSetupAI.Telemetry.UltraSetupAdvisor"/> to combine
/// the three scoring sources:
/// <list type="bullet">
///   <item><b>ML</b> — FastTree regression model (<see cref="ImpactPredictor"/>).</item>
///   <item><b>RL</b> — Q-value from <see cref="RLPolicyEngine"/>.</item>
///   <item><b>Heuristic</b> — rule-table fallback
///     (<c>SimulationImpactEstimator</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// After each completed A/B test the caller records the prediction errors via
/// <see cref="RecordAbTestResult"/>.  The engine keeps a rolling window of the
/// last <see cref="HistoryCapacity"/> records and re-derives the weights after
/// every update using the following rules:
/// </para>
/// <list type="number">
///   <item>If the average normalised ML error over the window is below
///     <see cref="MlLowErrorThreshold"/> (&lt;5 % of the 0–30 score range),
///     <see cref="MlWeight"/> is incremented by <see cref="WeightAdjustStep"/>.</item>
///   <item>If the average normalised RL error over the window is below
///     <see cref="RlHighAccuracyThreshold"/> (&lt;20 % of the reward range),
///     <see cref="RlWeight"/> is incremented by <see cref="WeightAdjustStep"/>.</item>
///   <item>When both ML and RL are <em>unstable</em> (errors above their
///     respective thresholds), <see cref="HeuristicWeight"/> is incremented by
///     <see cref="WeightAdjustStep"/> as a temporary fallback.</item>
///   <item>Each weight is clamped to [<see cref="WeightMin"/>, <see cref="WeightMax"/>]
///     and the three weights are re-normalised so that they sum to 1.</item>
/// </list>
/// <para>
/// The engine is <b>not</b> thread-safe; access should be confined to a single
/// thread (typically the UI / telemetry update thread).
/// </para>
/// </remarks>
public sealed class AdaptiveWeightEngine
{
    // ── Constants: initial weights ────────────────────────────────────────────

    /// <summary>Initial ML blend weight (50 %).</summary>
    public const float InitialMlWeight        = 0.50f;

    /// <summary>Initial RL blend weight (30 %).</summary>
    public const float InitialRlWeight        = 0.30f;

    /// <summary>Initial heuristic blend weight (20 %).</summary>
    public const float InitialHeuristicWeight = 0.20f;

    // ── Constants: adaptation thresholds ─────────────────────────────────────

    /// <summary>
    /// Number of recent A/B test records kept in the rolling history window.
    /// When the window is full the oldest record is evicted.
    /// </summary>
    public const int HistoryCapacity = 20;

    /// <summary>
    /// Minimum number of history records required before weight adjustments are
    /// applied.  Below this count the initial weights are preserved unchanged.
    /// </summary>
    public const int MinRecordsBeforeAdjust = 3;

    /// <summary>
    /// Maximum normalised ML prediction error (0..1) below which ML is
    /// considered "consistently accurate" and its weight is increased.
    /// Corresponds to &lt;5 % of <see cref="MaxScoreDelta"/>.
    /// </summary>
    public const float MlLowErrorThreshold = 0.05f;

    /// <summary>
    /// Maximum normalised RL reward error (0..1) below which RL is considered
    /// "highly accurate" and its weight is increased.
    /// Corresponds to &lt;20 % of <see cref="RewardRange"/>.
    /// </summary>
    public const float RlHighAccuracyThreshold = 0.20f;

    /// <summary>
    /// Amount by which a single weight is adjusted (up or down) per rebalance call.
    /// </summary>
    public const float WeightAdjustStep = 0.05f;

    /// <summary>Minimum value any single weight may hold (10 %).</summary>
    public const float WeightMin = 0.10f;

    /// <summary>Maximum value any single weight may hold (70 %).</summary>
    public const float WeightMax = 0.70f;

    // ── Constants: normalisation denominators ─────────────────────────────────

    /// <summary>
    /// Upper bound of the score-delta range used to normalise ML prediction errors.
    /// Matches the 0..30 clamp applied to <c>DeltaOverallScore</c>.
    /// </summary>
    public const float MaxScoreDelta = 30f;

    /// <summary>
    /// Total reward range used to normalise RL prediction errors.
    /// Q-values / rewards are clamped to [−15, 30], giving a range of 45.
    /// </summary>
    public const float RewardRange = 45f;

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Default path of the adaptive-weights JSON file.
    /// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\adaptive_weights.json</c>
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "adaptive_weights.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>Current ML blend weight.</summary>
    public float MlWeight        { get; private set; } = InitialMlWeight;

    /// <summary>Current RL blend weight.</summary>
    public float RlWeight        { get; private set; } = InitialRlWeight;

    /// <summary>Current heuristic blend weight.</summary>
    public float HeuristicWeight { get; private set; } = InitialHeuristicWeight;

    /// <summary>
    /// Number of A/B test records currently stored in the rolling history.
    /// </summary>
    public int HistoryCount => _history.Count;

    private readonly Queue<AbTestAccuracyRecord> _history = new();

    // ── Construction / load ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a new engine, optionally loading persisted weights from
    /// <paramref name="filePath"/>.
    /// When the file does not exist or cannot be read, the initial defaults
    /// (<see cref="InitialMlWeight"/>, <see cref="InitialRlWeight"/>,
    /// <see cref="InitialHeuristicWeight"/>) are used.
    /// </summary>
    /// <param name="filePath">
    /// Full path to the JSON weights file.
    /// Defaults to <see cref="DefaultFilePath"/> when <see langword="null"/>.
    /// </param>
    public AdaptiveWeightEngine(string? filePath = null)
    {
        Load(filePath ?? DefaultFilePath);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Records one completed A/B test outcome, updates the rolling history,
    /// and rebalances the blend weights.
    /// </summary>
    /// <param name="mlPredictedDelta">
    /// Score delta predicted by the ML model (or heuristic when ML is not
    /// loaded) at proposal-selection time.
    /// </param>
    /// <param name="actualScoreDelta">
    /// Actual score improvement observed from the A/B test result.
    /// A simple proxy is <c>result.Confidence × (result.Improved ? 10f : −5f)</c>
    /// — callers can supply a more precise value when available.
    /// </param>
    /// <param name="rlQValueUsed">
    /// Q-value returned by <see cref="RLPolicyEngine.GetQValue"/> at the time
    /// the proposal was selected (i.e. before the Q-table was updated).
    /// Pass <c>0f</c> when RL was not active.
    /// </param>
    /// <param name="actualReward">
    /// Reward computed by <see cref="RLPolicyEngine.ComputeReward"/> after the
    /// A/B test completed.
    /// Pass <c>0f</c> when RL was not active.
    /// </param>
    /// <param name="improved">
    /// <see cref="AbTestResult.Improved"/> from the completed test.
    /// </param>
    public void RecordAbTestResult(
        float mlPredictedDelta,
        float actualScoreDelta,
        float rlQValueUsed,
        float actualReward,
        bool  improved)
    {
        float mlError = Math.Abs(mlPredictedDelta - actualScoreDelta) / MaxScoreDelta;
        float rlError = Math.Abs(rlQValueUsed     - actualReward)     / RewardRange;

        var record = new AbTestAccuracyRecord
        {
            MlNormalizedError = Math.Clamp(mlError, 0f, 1f),
            RlNormalizedError = Math.Clamp(rlError, 0f, 1f),
            Improved          = improved,
        };

        _history.Enqueue(record);
        if (_history.Count > HistoryCapacity)
            _history.Dequeue();   // evict oldest

        Rebalance();
    }

    /// <summary>
    /// Saves the current weight values to <paramref name="filePath"/>.
    /// Silently ignores I/O failures so that a read-only environment does not
    /// crash the application.
    /// </summary>
    /// <param name="filePath">
    /// Full path to write. Defaults to <see cref="DefaultFilePath"/> when
    /// <see langword="null"/>.
    /// </param>
    public void Save(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new AdaptiveWeightState
            {
                MlWeight        = MlWeight,
                RlWeight        = RlWeight,
                HeuristicWeight = HeuristicWeight,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch
        {
            // Swallow — persistence is best-effort; the in-memory state is still valid.
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads persisted weights from <paramref name="filePath"/>; silently falls
    /// back to defaults when the file is absent or malformed.
    /// </summary>
    private void Load(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json  = File.ReadAllText(filePath);
            var state = JsonSerializer.Deserialize<AdaptiveWeightState>(json, _jsonOptions);
            if (state is null) return;

            MlWeight        = Math.Clamp(state.MlWeight,        WeightMin, WeightMax);
            RlWeight        = Math.Clamp(state.RlWeight,        WeightMin, WeightMax);
            HeuristicWeight = Math.Clamp(state.HeuristicWeight, WeightMin, WeightMax);
            NormalizeWeights();
        }
        catch
        {
            // Malformed file — use defaults.
        }
    }

    /// <summary>
    /// Recomputes the three blend weights from the rolling history.
    /// Does nothing when the history contains fewer than
    /// <see cref="MinRecordsBeforeAdjust"/> records.
    /// </summary>
    private void Rebalance()
    {
        if (_history.Count < MinRecordsBeforeAdjust) return;

        // Compute averages over the rolling window.
        float sumMlError = 0f, sumRlError = 0f;
        foreach (var r in _history)
        {
            sumMlError += r.MlNormalizedError;
            sumRlError += r.RlNormalizedError;
        }
        float n            = _history.Count;
        float avgMlError   = sumMlError / n;
        float avgRlError   = sumRlError / n;

        bool mlAccurate  = avgMlError < MlLowErrorThreshold;
        bool rlAccurate  = avgRlError < RlHighAccuracyThreshold;
        bool bothUnstable = !mlAccurate && !rlAccurate;

        float ml  = MlWeight;
        float rl  = RlWeight;
        float heu = HeuristicWeight;

        if (mlAccurate)
        {
            ml  += WeightAdjustStep;
            // Reduce other weights proportionally to keep sum manageable before normalisation.
            rl  -= WeightAdjustStep * 0.5f;
            heu -= WeightAdjustStep * 0.5f;
        }

        if (rlAccurate)
        {
            rl  += WeightAdjustStep;
            ml  -= WeightAdjustStep * 0.5f;
            heu -= WeightAdjustStep * 0.5f;
        }

        if (bothUnstable)
        {
            heu += WeightAdjustStep;
            ml  -= WeightAdjustStep * 0.5f;
            rl  -= WeightAdjustStep * 0.5f;
        }

        // Clamp each weight to the allowed range.
        MlWeight        = Math.Clamp(ml,  WeightMin, WeightMax);
        RlWeight        = Math.Clamp(rl,  WeightMin, WeightMax);
        HeuristicWeight = Math.Clamp(heu, WeightMin, WeightMax);

        NormalizeWeights();
    }

    /// <summary>
    /// Re-scales the three weights so that they sum exactly to 1.0.
    /// If all three are zero (which should not occur after clamping), the
    /// initial defaults are restored.
    /// </summary>
    private void NormalizeWeights()
    {
        float total = MlWeight + RlWeight + HeuristicWeight;
        if (total <= 0f)
        {
            MlWeight        = InitialMlWeight;
            RlWeight        = InitialRlWeight;
            HeuristicWeight = InitialHeuristicWeight;
            return;
        }
        MlWeight        /= total;
        RlWeight        /= total;
        HeuristicWeight /= total;
    }
}
