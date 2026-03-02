using System;
using System.IO;
using Microsoft.ML;

namespace AvoPerformanceSetupAI.ML;

/// <summary>
/// Thread-safe wrapper around a loaded ML.NET FastTree regression model.
/// Exposes <see cref="Predict"/> to score a candidate setup change, and
/// <see cref="LoadModel"/> to hot-swap the model without restarting the app.
/// </summary>
/// <remarks>
/// <para>
/// The model file is expected at
/// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\impact_model.zip</c>.
/// </para>
/// <para>
/// Thread-safety: <see cref="Predict"/> and <see cref="LoadModel"/> are both
/// guarded by a reader/writer lock so the background retraining thread can
/// hot-swap the model while the UI thread continues to call
/// <see cref="Predict"/> without blocking.
/// </para>
/// </remarks>
public sealed class ImpactPredictor
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Path where the trained model zip is stored.
    /// Resolved at construction time so the instance is independent of the CWD.
    /// </summary>
    public static string DefaultModelPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "impact_model.zip");

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly MLContext _mlContext = new(seed: 0);
    private readonly System.Threading.ReaderWriterLockSlim _lock = new();

    private PredictionEngine<ImpactTrainingSample, ImpactPrediction>? _engine;

    // ── Construction / lazy load ──────────────────────────────────────────────

    /// <summary>
    /// Creates a new instance. If <paramref name="modelPath"/> exists, the model
    /// is loaded immediately; otherwise <see cref="IsModelLoaded"/> will be
    /// <see langword="false"/> until <see cref="LoadModel"/> is called.
    /// </summary>
    /// <param name="modelPath">
    /// Full path to the model zip. Defaults to <see cref="DefaultModelPath"/>
    /// when <see langword="null"/>.
    /// </param>
    public ImpactPredictor(string? modelPath = null)
    {
        var path = modelPath ?? DefaultModelPath;
        if (File.Exists(path))
            LoadModel(path);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when a trained model is loaded and
    /// <see cref="Predict"/> will return a ML-backed score.
    /// </summary>
    public bool IsModelLoaded
    {
        get
        {
            _lock.EnterReadLock();
            try   { return _engine != null; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Predicts the expected change in overall driving score for
    /// <paramref name="sample"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="ImpactPrediction"/> with
    /// <see cref="ImpactPrediction.DeltaOverallScore"/> set.
    /// Returns <see langword="null"/> when no model is loaded — callers should
    /// fall back to the heuristic estimator.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <see langword="null"/>.</exception>
    public ImpactPrediction? Predict(ImpactTrainingSample sample)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));

        _lock.EnterReadLock();
        try
        {
            // Capture a local reference; the engine cannot be disposed while
            // the read lock is held, so this is safe.
            var engine = _engine;
            if (engine is null) return null;
            return engine.Predict(sample);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Hot-swaps the loaded model with the one at <paramref name="modelPath"/>.
    /// Safe to call from any thread (including a background retrain thread).
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="modelPath"/> does not exist.
    /// </exception>
    public void LoadModel(string? modelPath = null)
    {
        var path = modelPath ?? DefaultModelPath;
        if (!File.Exists(path))
            throw new FileNotFoundException($"ML model not found at: {path}", path);

        var transformer = _mlContext.Model.Load(path, out _);
        var newEngine   = _mlContext.Model
            .CreatePredictionEngine<ImpactTrainingSample, ImpactPrediction>(transformer);

        PredictionEngine<ImpactTrainingSample, ImpactPrediction>? oldEngine;
        _lock.EnterWriteLock();
        try
        {
            oldEngine = _engine;
            _engine   = newEngine;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // Dispose the previous engine after releasing the write lock so that
        // any inflight Predict() calls that captured the old reference finish
        // without accessing a disposed object inside the read lock.
        oldEngine?.Dispose();
    }
}
