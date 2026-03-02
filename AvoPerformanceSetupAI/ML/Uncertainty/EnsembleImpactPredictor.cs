using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML;
using Microsoft.ML.Trainers.FastTree;

namespace AvoPerformanceSetupAI.ML.Uncertainty;

/// <summary>
/// Trains and hosts an ensemble of <see cref="EnsembleSize"/> FastTree regression
/// models, each seeded with a different random seed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose</b>: by training multiple models on the same data with different
/// seeds, we obtain diverse predictions whose spread (standard deviation) serves
/// as an uncertainty proxy — a simple form of <em>ensemble uncertainty</em>.
/// </para>
/// <para>
/// <b>Model paths</b>: each model is saved as
/// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\ensemble_model_{i}.zip</c>
/// for <c>i</c> in 0..(<see cref="EnsembleSize"/>−1).
/// </para>
/// <para>
/// Thread-safety: <see cref="Predict"/> and <see cref="TrainAsync"/> are guarded
/// by a <see cref="ReaderWriterLockSlim"/> so hot-swap on retrain is safe.
/// </para>
/// </remarks>
public sealed class EnsembleImpactPredictor
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Number of models in the ensemble.</summary>
    public const int EnsembleSize = 5;

    /// <summary>Number of FastTree boosting rounds per model.</summary>
    private const int NumTrees = 100;

    /// <summary>Number of leaves per tree.</summary>
    private const int NumLeaves = 20;

    /// <summary>Minimum samples per leaf.</summary>
    private const int MinLeafSamples = 5;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Directory where the ensemble model files are stored.
    /// Default: <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\</c>.
    /// </summary>
    public static string ModelDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML");

    /// <summary>Returns the path for model index <paramref name="i"/>.</summary>
    public static string ModelPath(int i) =>
        Path.Combine(ModelDirectory, $"ensemble_model_{i}.zip");

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Per-model prediction engines. Index aligns with <c>i</c> in
    /// <see cref="ModelPath"/>.  <see langword="null"/> slots indicate a model
    /// that has not been loaded yet.
    /// </summary>
    private readonly PredictionEngine<ImpactTrainingSample, ImpactPrediction>?[] _engines =
        new PredictionEngine<ImpactTrainingSample, ImpactPrediction>?[EnsembleSize];

    private readonly MLContext[] _contexts;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new instance.  If model files already exist on disk they are
    /// loaded immediately; otherwise <see cref="IsAnyModelLoaded"/> will be
    /// <see langword="false"/> until <see cref="TrainAsync"/> is called.
    /// </summary>
    public EnsembleImpactPredictor()
    {
        _contexts = new MLContext[EnsembleSize];
        for (int i = 0; i < EnsembleSize; i++)
            _contexts[i] = new MLContext(seed: i);

        TryLoadAllModels();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when at least one model in the ensemble is loaded
    /// and <see cref="Predict"/> will return a result.
    /// </summary>
    public bool IsAnyModelLoaded
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                foreach (var e in _engines)
                    if (e != null) return true;
                return false;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Runs <paramref name="sample"/> through every loaded model and returns the
    /// mean, standard deviation, and per-model raw scores.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no model is loaded.
    /// </returns>
    public (float Mean, float StdDev, float[] Scores)? Predict(ImpactTrainingSample sample)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));

        _lock.EnterReadLock();
        try
        {
            var scores = new List<float>(EnsembleSize);
            for (int i = 0; i < EnsembleSize; i++)
            {
                var engine = _engines[i];
                if (engine is null) continue;
                var pred = engine.Predict(sample);
                scores.Add(pred.DeltaOverallScore);
            }
            if (scores.Count == 0) return null;

            float mean   = ComputeMean(scores);
            float stddev = ComputeStdDev(scores, mean);
            return (mean, stddev, scores.ToArray());
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Trains all <see cref="EnsembleSize"/> models on <paramref name="samples"/>
    /// in parallel, saves each model to disk, and hot-swaps the prediction engines.
    /// </summary>
    /// <param name="samples">Training samples (same schema as <see cref="ImpactModelTrainer"/>).</param>
    /// <param name="cancellationToken">Optional cancellation.</param>
    public async Task TrainAsync(
        IReadOnlyList<ImpactTrainingSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (samples.Count == 0) return;

        Directory.CreateDirectory(ModelDirectory);

        // Train all models concurrently on the thread pool.
        var tasks = new Task<PredictionEngine<ImpactTrainingSample, ImpactPrediction>>[EnsembleSize];
        for (int i = 0; i < EnsembleSize; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(() => TrainOne(idx, samples), cancellationToken);
        }

        var newEngines = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Swap all engines under write lock.
        PredictionEngine<ImpactTrainingSample, ImpactPrediction>?[] oldEngines;
        _lock.EnterWriteLock();
        try
        {
            oldEngines = (PredictionEngine<ImpactTrainingSample, ImpactPrediction>?[])_engines.Clone();
            for (int i = 0; i < EnsembleSize; i++)
                _engines[i] = newEngines[i];
        }
        finally { _lock.ExitWriteLock(); }

        // Dispose old engines outside the lock.
        foreach (var old in oldEngines)
            old?.Dispose();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Trains a single ensemble member at index <paramref name="idx"/> and
    /// returns a new <see cref="PredictionEngine{TSrc,TDst}"/>.
    /// </summary>
    private PredictionEngine<ImpactTrainingSample, ImpactPrediction> TrainOne(
        int idx,
        IReadOnlyList<ImpactTrainingSample> samples)
    {
        var ctx      = _contexts[idx];
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
                NumberOfLeaves             = NumLeaves,
                MinimumExampleCountPerLeaf = MinLeafSamples,
                NumberOfTrees              = NumTrees,
                Seed                       = idx,          // different seed per member
                LabelColumnName            = "Label",
                FeatureColumnName          = "Features",
            }));

        var model = pipeline.Fit(dataView);
        ctx.Model.Save(model, dataView.Schema, ModelPath(idx));
        return ctx.Model.CreatePredictionEngine<ImpactTrainingSample, ImpactPrediction>(model);
    }

    /// <summary>
    /// Attempts to load all ensemble model files that exist on disk.
    /// Silently skips files that are missing or corrupt.
    /// </summary>
    private void TryLoadAllModels()
    {
        for (int i = 0; i < EnsembleSize; i++)
        {
            var path = ModelPath(i);
            if (!File.Exists(path)) continue;
            try
            {
                var transformer = _contexts[i].Model.Load(path, out _);
                _engines[i] = _contexts[i].Model
                    .CreatePredictionEngine<ImpactTrainingSample, ImpactPrediction>(transformer);
            }
            catch
            {
                // Corrupt or incompatible model — leave slot null.
            }
        }
    }

    private static float ComputeMean(List<float> values)
    {
        float sum = 0f;
        foreach (var v in values) sum += v;
        return sum / values.Count;
    }

    private static float ComputeStdDev(List<float> values, float mean)
    {
        if (values.Count < 2) return 0f;
        float sumSq = 0f;
        foreach (var v in values) sumSq += (v - mean) * (v - mean);
        return MathF.Sqrt(sumSq / values.Count);
    }
}
