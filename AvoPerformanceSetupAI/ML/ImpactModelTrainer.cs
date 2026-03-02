using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Telemetry;
using Microsoft.ML;
using Microsoft.ML.Trainers.FastTree;

namespace AvoPerformanceSetupAI.ML;

/// <summary>
/// Manages the on-device training dataset and background model retraining.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dataset file</b>: one JSON array per line at
/// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\training_data.json</c>.
/// Each element is an <see cref="ImpactTrainingSample"/>.
/// </para>
/// <para>
/// <b>Workflow</b>
/// <list type="number">
///   <item>After every completed A/B test call
///     <see cref="AppendSample"/> to persist the new labelled example.</item>
///   <item>Call <see cref="RetrainIfReady"/> immediately after; it is a no-op
///     until the dataset exceeds <see cref="MinSamplesForTraining"/> rows.</item>
///   <item>When the threshold is crossed the method spawns a background
///     <see cref="Task"/> that trains a FastTree regression model, saves it to
///     <see cref="ImpactPredictor.DefaultModelPath"/>, then calls
///     <see cref="ImpactPredictor.LoadModel"/> to hot-swap the predictor.</item>
/// </list>
/// </para>
/// <para>
/// Thread-safety: <see cref="AppendSample"/> and <see cref="RetrainIfReady"/>
/// are guarded by a <c>lock</c> on the dataset file path string, preventing
/// concurrent writes. The background retrain task holds a separate
/// <see cref="_trainLock"/> flag so that at most one retrain runs at a time.
/// </para>
/// </remarks>
public static class ImpactModelTrainer
{
    // ── Paths ─────────────────────────────────────────────────────────────────

    private static readonly string _datasetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "training_data.json");

    private static readonly string _modelPath = ImpactPredictor.DefaultModelPath;

    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum number of labelled samples required before a model is trained.
    /// Set to 100 to ensure the FastTree model (20 leaves, 100 trees, 15 features)
    /// has enough data to generalise beyond the training set.
    /// </summary>
    public const int MinSamplesForTraining = 100;

    /// <summary>Number of FastTree leaves (depth proxy).</summary>
    private const int NumLeaves            = 20;

    /// <summary>Minimum sample count per leaf (prevents over-fitting on small data).</summary>
    private const int MinDatapointsInLeaf  = 5;

    /// <summary>Number of boosting iterations.</summary>
    private const int NumTrees             = 100;

    // ── Label weights (must match UltraSetupAdvisor.ComputeHeuristicScoreDelta) ──

    /// <summary>Weight applied to the balance-signal delta when computing the training label.</summary>
    private const float LabelWeightBalance   = 0.35f;

    /// <summary>Weight applied to the traction-signal delta when computing the training label.</summary>
    private const float LabelWeightTraction  = 0.25f;

    /// <summary>Weight applied to the brake-signal delta when computing the training label.</summary>
    private const float LabelWeightBrake     = 0.20f;

    /// <summary>Weight applied to the stability-signal delta when computing the training label.</summary>
    private const float LabelWeightStability = 0.20f;

    // ── State ─────────────────────────────────────────────────────────────────

    private static int _isTraining; // interlocked flag: 0 = idle, 1 = running

    /// <summary>Dedicated lock for dataset file I/O (avoids interning issues with string locks).</summary>
    private static readonly object _datasetLock = new();

    /// <summary>
    /// Number of skipped malformed lines encountered since startup.
    /// Exposed for diagnostic purposes (tests, logging).
    /// </summary>
    public static int SkippedMalformedLines { get; private set; }

    /// <summary>
    /// Current number of valid samples in the local dataset file.
    /// Refreshed each time <see cref="AppendSample"/> or
    /// <see cref="RetrainIfReady"/> is called.
    /// Exposed so <see cref="UltraSetupAdvisor"/> can gate multi-parameter
    /// optimization on dataset size without a separate file scan.
    /// </summary>
    public static int DatasetSampleCount { get; private set; }

    // ── Section / parameter encoding ─────────────────────────────────────────

    /// <summary>Maps section strings to integer codes (1-based).</summary>
    public static readonly Dictionary<string, float> EncodedSections =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARB"]         = 1f,
            ["SPRINGS"]     = 2f,
            ["AERO"]        = 3f,
            ["ELECTRONICS"] = 4f,
            ["BRAKES"]      = 5f,
            ["TYRES"]       = 6f,
            ["ALIGNMENT"]   = 7f,
            ["DAMPERS"]     = 8f,
            ["DRIVING_TIP"] = 9f,
        };

    /// <summary>Maps parameter strings to integer codes (1-based).</summary>
    public static readonly Dictionary<string, float> EncodedParameters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FRONT"]            = 1f,
            ["REAR"]             = 2f,
            ["FRONT_SPRING"]     = 3f,
            ["FRONT_WING"]       = 4f,
            ["DIFF_ACC"]         = 5f,
            ["TRACTION_CONTROL"] = 6f,
            ["BRAKE_BIAS"]       = 7f,
            ["BRAKE_POWER"]      = 8f,
            ["PRESSURE_LF"]      = 9f,
            ["CAMBER_LF"]        = 10f,
            ["BUMP_REAR"]        = 11f,
            ["TECHNIQUE"]        = 12f,
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <see cref="ImpactTrainingSample"/> from a completed
    /// <see cref="AbTestResult"/>, appends it to the persistent dataset, and
    /// returns the sample (for logging or testing purposes).
    /// </summary>
    /// <remarks>
    /// The method is a no-op and returns <see langword="null"/> when
    /// <paramref name="result"/>'s <see cref="AbTestResult.Confidence"/> is
    /// below 0.3, as very low-confidence results add noise to the model.
    /// </remarks>
    /// <param name="result">Completed A/B test result.</param>
    /// <param name="baselineScores">
    /// <see cref="DrivingScores"/> computed <em>before</em> the setup change was
    /// applied (used to populate the telemetry feature fields).
    /// </param>
    /// <param name="baselineFrame">
    /// Aggregate <see cref="FeatureFrame"/> from the baseline phase.
    /// </param>
    public static ImpactTrainingSample? AppendSample(
        AbTestResult   result,
        DrivingScores? baselineScores,
        in FeatureFrame baselineFrame)
    {
        if (result is null)                     throw new ArgumentNullException(nameof(result));
        if (result.Confidence < 0.3f)           return null; // low-quality result — skip

        // Derive DeltaOverallScore from A/B metrics using the same weights as
        // UltraSetupAdvisor.ComputeHeuristicScoreDelta so training labels are
        // consistent with the heuristic fallback.
        // A/B metric deltas are in [0,1]; scale to [0,100] by ×100.
        // Negative understeer/oversteer/wheelspin/brakeStability = improvement.
        // Positive stabilityScore delta = improvement.
        float deltaOverall =
              LabelWeightStability * result.Deltas.StabilityScore       * 100f
            - LabelWeightBalance   * result.Deltas.UndersteerMidAvg     * 100f
            - LabelWeightTraction  * result.Deltas.WheelspinRatioRearAvg* 100f
            - LabelWeightBrake     * result.Deltas.BrakeStabilityAvg    * 100f;

        var proposal = result.TestedProposal;
        var sample = new ImpactTrainingSample
        {
            UndersteerEntry         = baselineFrame.UndersteerEntry,
            UndersteerMid           = baselineFrame.UndersteerMid,
            OversteerEntry          = baselineFrame.OversteerEntry,
            OversteerExit           = baselineFrame.OversteerExit,
            WheelspinRatioRear      = baselineFrame.WheelspinRatioRear,
            LockupRatioFront        = baselineFrame.LockupRatioFront,
            BrakeStabilityIndex     = baselineFrame.BrakeStabilityIndex,
            SuspensionOscillationIndex = baselineFrame.SuspensionOscillationIndex,
            BalanceScore            = baselineScores?.BalanceScore   ?? 50f,
            StabilityScore          = baselineScores?.StabilityScore ?? 50f,
            TractionScore           = baselineScores?.TractionScore  ?? 50f,
            BrakeScore              = baselineScores?.BrakeScore     ?? 50f,
            SectionEncoded          = EncodedSections.TryGetValue(proposal.Section, out var sec) ? sec : 0f,
            ParameterEncoded        = EncodedParameters.TryGetValue(proposal.Parameter, out var par) ? par : 0f,
            DeltaValue              = ParseDelta(proposal.Delta),
            DeltaOverallScore       = deltaOverall,
        };

        Persist(sample);
        DatasetSampleCount++;
        return sample;
    }

    /// <summary>
    /// Triggers a background model retrain when the dataset has grown beyond
    /// <see cref="MinSamplesForTraining"/> rows. Returns immediately; the
    /// training happens on a background <see cref="Task"/>.
    /// </summary>
    /// <param name="predictor">
    /// Predictor instance to hot-swap once training completes.
    /// </param>
    public static void RetrainIfReady(ImpactPredictor predictor)
    {
        if (predictor is null) throw new ArgumentNullException(nameof(predictor));

        // Quick check without loading the full file
        int count = CountSamples();
        if (count < MinSamplesForTraining) return;

        // Only one background retrain at a time
        if (Interlocked.CompareExchange(ref _isTraining, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try   { TrainAndSave(predictor); }
            finally { Interlocked.Exchange(ref _isTraining, 0); }
        });
    }

    // ── Training ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all samples from disk, trains a FastTree regression model, saves
    /// the model to <see cref="_modelPath"/>, and hot-swaps
    /// <paramref name="predictor"/>.
    /// </summary>
    private static void TrainAndSave(ImpactPredictor predictor)
    {
        var samples = LoadSamples();
        if (samples.Count < MinSamplesForTraining) return;

        var ctx = new MLContext(seed: 0);

        var dataView = ctx.Data.LoadFromEnumerable(samples);

        var pipeline = ctx.Transforms
            .Concatenate("Features",
                nameof(ImpactTrainingSample.UndersteerEntry),
                nameof(ImpactTrainingSample.UndersteerMid),
                nameof(ImpactTrainingSample.OversteerEntry),
                nameof(ImpactTrainingSample.OversteerExit),
                nameof(ImpactTrainingSample.WheelspinRatioRear),
                nameof(ImpactTrainingSample.LockupRatioFront),
                nameof(ImpactTrainingSample.BrakeStabilityIndex),
                nameof(ImpactTrainingSample.SuspensionOscillationIndex),
                nameof(ImpactTrainingSample.BalanceScore),
                nameof(ImpactTrainingSample.StabilityScore),
                nameof(ImpactTrainingSample.TractionScore),
                nameof(ImpactTrainingSample.BrakeScore),
                nameof(ImpactTrainingSample.SectionEncoded),
                nameof(ImpactTrainingSample.ParameterEncoded),
                nameof(ImpactTrainingSample.DeltaValue))
            .Append(ctx.Regression.Trainers.FastTree(new FastTreeRegressionTrainer.Options
            {
                NumberOfLeaves          = NumLeaves,
                MinimumExampleCountPerLeaf = MinDatapointsInLeaf,
                NumberOfTrees           = NumTrees,
                LabelColumnName         = "Label",
                FeatureColumnName       = "Features",
            }));

        var model = pipeline.Fit(dataView);

        // Ensure output directory exists
        var dir = Path.GetDirectoryName(_modelPath)!;
        Directory.CreateDirectory(dir);

        ctx.Model.Save(model, dataView.Schema, _modelPath);

        // Hot-swap the live predictor
        predictor.LoadModel(_modelPath);
    }

    // ── Persistence helpers ───────────────────────────────────────────────────

    /// <summary>Appends <paramref name="sample"/> to the JSON-lines dataset file.</summary>
    private static void Persist(ImpactTrainingSample sample)
    {
        var dir = Path.GetDirectoryName(_datasetPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(sample) + Environment.NewLine;

        lock (_datasetLock)
        {
            File.AppendAllText(_datasetPath, json);
        }
    }

    /// <summary>Loads all persisted samples. Returns empty list if file absent.</summary>
    private static List<ImpactTrainingSample> LoadSamples()
    {
        if (!File.Exists(_datasetPath)) return [];

        string[] lines;
        lock (_datasetLock) { lines = File.ReadAllLines(_datasetPath); }

        var list = new List<ImpactTrainingSample>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<ImpactTrainingSample>(line);
                if (s != null) list.Add(s);
            }
            catch (JsonException)
            {
                // Count malformed lines so callers can detect dataset corruption.
                SkippedMalformedLines++;
            }
        }
        return list;
    }

    /// <summary>
    /// Counts the number of non-empty lines in the dataset file without fully
    /// deserialising. O(n) file scan but avoids JSON parsing overhead.
    /// Also updates <see cref="DatasetSampleCount"/>.
    /// </summary>
    private static int CountSamples()
    {
        if (!File.Exists(_datasetPath)) return 0;
        int count = 0;
        lock (_datasetLock)
        {
            foreach (var line in File.ReadLines(_datasetPath))
                if (!string.IsNullOrWhiteSpace(line)) count++;
        }
        DatasetSampleCount = count;
        return count;
    }

    /// <summary>
    /// Parses the signed numeric step from a <see cref="Proposal.Delta"/> string
    /// such as "+1", "-0.05", "+2".
    /// Returns 0 on failure.
    /// </summary>
    private static float ParseDelta(string? delta)
    {
        if (string.IsNullOrWhiteSpace(delta)) return 0f;
        return float.TryParse(delta,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var v) ? v : 0f;
    }
}
