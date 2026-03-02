using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvoPerformanceSetupAI.ML.Uncertainty;

// ── Serialisable calibration state ────────────────────────────────────────────

/// <summary>
/// JSON-serialisable snapshot of <see cref="ConformalPredictor"/> state.
/// Only the calibration pairs are persisted; derived nonconformity scores are
/// recomputed on load.
/// </summary>
internal sealed class ConformalCalibratorState
{
    /// <summary>
    /// Recent (predicted_mean, actual_delta) pairs in insertion order.
    /// Capped at <see cref="ConformalPredictor.MaxCalibrationSamples"/>.
    /// </summary>
    public List<CalibrationPair> Pairs { get; set; } = [];
}

/// <summary>A single calibration observation: the predicted mean and the observed delta.</summary>
internal sealed class CalibrationPair
{
    public float PredictedMean { get; set; }
    public float ActualDelta   { get; set; }
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Inductive Conformal Predictor (ICP) that wraps ensemble predictions with
/// statistically valid prediction intervals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm</b>:
/// <list type="number">
///   <item>After each A/B test, record the pair
///     (<c>predicted_mean</c>, <c>actual_delta</c>) in a rolling calibration
///     window of up to <see cref="MaxCalibrationSamples"/> samples.</item>
///   <item>The <em>nonconformity score</em> for each calibration sample is
///     <c>|actual − predicted_mean|</c>.</item>
///   <item>For a target coverage level α (e.g. 80 %), the quantile
///     <c>q</c> is the ⌈(n+1)(1−α)⌉/n empirical quantile of the sorted
///     nonconformity scores.  The prediction interval is
///     <c>[mean − q, mean + q]</c>.</item>
/// </list>
/// </para>
/// <para>
/// When the calibration set is empty, symmetric intervals around the mean are
/// returned using multiples of <see cref="EnsembleImpactPredictor"/>-provided
/// <c>StdDev</c> (1.28 × σ for 80 %, 1.96 × σ for 95 %).
/// </para>
/// <para>
/// Thread-safety: this class is <b>not</b> thread-safe.  All calls should come
/// from the UI/telemetry thread.
/// </para>
/// </remarks>
public sealed class ConformalPredictor
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Maximum calibration samples retained in the rolling window.</summary>
    public const int MaxCalibrationSamples = 200;

    /// <summary>
    /// Minimum calibration samples needed before conformal intervals are used.
    /// Below this threshold, Gaussian fallback (σ-based) intervals are returned.
    /// </summary>
    public const int MinCalibrationSamples = 5;

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Default path for the calibration state JSON.
    /// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\conformal_calibration.json</c>
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "conformal_calibration.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly List<CalibrationPair> _pairs = [];

    /// <summary>Number of calibration samples currently stored.</summary>
    public int CalibrationCount => _pairs.Count;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new predictor, loading persisted calibration from
    /// <paramref name="filePath"/> if it exists.
    /// </summary>
    public ConformalPredictor(string? filePath = null)
    {
        Load(filePath ?? DefaultFilePath);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a new calibration observation and persists the updated state.
    /// </summary>
    /// <param name="predictedMean">The ensemble mean predicted at scoring time.</param>
    /// <param name="actualDelta">Observed score delta from the completed A/B test.</param>
    /// <param name="filePath">Optional override for the JSON file path.</param>
    public void AddCalibrationSample(float predictedMean, float actualDelta, string? filePath = null)
    {
        _pairs.Add(new CalibrationPair { PredictedMean = predictedMean, ActualDelta = actualDelta });
        if (_pairs.Count > MaxCalibrationSamples)
            _pairs.RemoveAt(0);   // evict oldest

        Save(filePath ?? DefaultFilePath);
    }

    /// <summary>
    /// Builds an <see cref="UncertaintyEstimate"/> for a new prediction.
    /// </summary>
    /// <param name="predictedMean">Ensemble mean from <see cref="EnsembleImpactPredictor"/>.</param>
    /// <param name="predictedStdDev">Ensemble standard deviation (used as Gaussian fallback).</param>
    public UncertaintyEstimate Estimate(float predictedMean, float predictedStdDev)
    {
        float lower80, upper80, lower95, upper95;
        float coverage80, coverage95;

        if (_pairs.Count >= MinCalibrationSamples)
        {
            // Compute sorted nonconformity scores
            var scores = new float[_pairs.Count];
            for (int i = 0; i < _pairs.Count; i++)
                scores[i] = MathF.Abs(_pairs[i].ActualDelta - _pairs[i].PredictedMean);
            Array.Sort(scores);

            float q80 = ConformalQuantile(scores, 0.80f);
            float q95 = ConformalQuantile(scores, 0.95f);

            lower80 = predictedMean - q80;
            upper80 = predictedMean + q80;
            lower95 = predictedMean - q95;
            upper95 = predictedMean + q95;

            // Empirical coverage on the calibration set itself
            coverage80 = EmpiricalCoverage(scores, q80);
            coverage95 = EmpiricalCoverage(scores, q95);
        }
        else
        {
            // Gaussian fallback: 1.28σ ≈ 80 %, 1.96σ ≈ 95 %
            float sigma = predictedStdDev > 0f ? predictedStdDev : 1f;
            lower80 = predictedMean - 1.28f * sigma;
            upper80 = predictedMean + 1.28f * sigma;
            lower95 = predictedMean - 1.96f * sigma;
            upper95 = predictedMean + 1.96f * sigma;
            coverage80 = 0.80f;
            coverage95 = 0.95f;
        }

        return new UncertaintyEstimate
        {
            Mean       = predictedMean,
            StdDev     = predictedStdDev,
            Lower80    = lower80,
            Upper80    = upper80,
            Lower95    = lower95,
            Upper95    = upper95,
            Coverage80 = coverage80,
            Coverage95 = coverage95,
        };
    }

    /// <summary>Saves calibration state to <paramref name="filePath"/>.</summary>
    public void Save(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new ConformalCalibratorState { Pairs = new List<CalibrationPair>(_pairs) };
            File.WriteAllText(path, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch { /* best-effort */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Load(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json  = File.ReadAllText(filePath);
            var state = JsonSerializer.Deserialize<ConformalCalibratorState>(json, _jsonOptions);
            if (state?.Pairs is null) return;
            _pairs.Clear();
            _pairs.AddRange(state.Pairs);
            if (_pairs.Count > MaxCalibrationSamples)
                _pairs.RemoveRange(0, _pairs.Count - MaxCalibrationSamples);
        }
        catch { /* malformed — start fresh */ }
    }

    /// <summary>
    /// Returns the conformal quantile at coverage level <paramref name="alpha"/>
    /// from a <b>pre-sorted</b> array of nonconformity scores.
    /// </summary>
    /// <remarks>
    /// Standard ICP formula: for n calibration samples and target coverage (1−α),
    /// we want the smallest q such that at least ⌈(n+1)(1−α)⌉ / n of the
    /// calibration scores are ≤ q.  Concretely we need the element at
    /// 0-based index <c>⌈(n+1)(1−α)⌉ − 1</c> in the sorted array.
    /// This differs from the "upper α quantile" form used in hypothesis
    /// testing; here we are seeking coverage on the <em>high</em> side of
    /// the nonconformity distribution.
    /// </remarks>
    private static float ConformalQuantile(float[] sortedScores, float alpha)
    {
        int n   = sortedScores.Length;
        int idx = (int)MathF.Ceiling((n + 1) * (1f - alpha)) - 1;
        idx = Math.Clamp(idx, 0, n - 1);
        return sortedScores[idx];
    }

    /// <summary>
    /// Empirical coverage fraction: proportion of nonconformity scores ≤ <paramref name="q"/>.
    /// </summary>
    private static float EmpiricalCoverage(float[] sortedScores, float q)
    {
        int count = 0;
        foreach (var s in sortedScores)
            if (s <= q) count++;
        return (float)count / sortedScores.Length;
    }
}
