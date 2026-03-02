using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvoPerformanceSetupAI.ML.Uncertainty;

// ── Serialisable state ────────────────────────────────────────────────────────

/// <summary>
/// Persistent state for <see cref="ConfidenceCalibrationEngine"/>.
/// One <see cref="BinState"/> entry per reliability bin (0..9).
/// </summary>
internal sealed class CalibrationMapState
{
    /// <summary>The 10 reliability bins in ascending confidence order.</summary>
    public List<BinState> Bins { get; set; } = [];
}

/// <summary>One reliability bin — tracks raw counts used to compute empirical accuracy.</summary>
internal sealed class BinState
{
    /// <summary>Total A/B tests whose raw confidence fell into this bin.</summary>
    public int Total { get; set; }

    /// <summary>Number of those tests where <see cref="AvoPerformanceSetupAI.Telemetry.AbTestResult.Improved"/> was <see langword="true"/>.</summary>
    public int Successes { get; set; }
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Calibrates raw confidence values — from RuleEngine proposals, the
/// Driver-vs-Setup discriminator, or RL Q-values — to empirical accuracy using
/// a reliability-diagram approach with 10 equal-width bins (0..0.1, 0.1..0.2, …)
/// and a simple piecewise-linear isotonic calibration map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage</b>:
/// <list type="number">
///   <item>After each completed A/B test call
///     <see cref="RecordOutcome"/> with the raw confidence that was predicted
///     and whether the test improved the car.</item>
///   <item>Call <see cref="CalibrateConfidence"/> to obtain a calibrated
///     probability for a new raw confidence value.</item>
/// </list>
/// </para>
/// <para>
/// <b>Isotonic constraint</b>: the empirical accuracy values per bin are passed
/// through pool-adjacent violators (PAV) to enforce the monotone non-decreasing
/// constraint required by a proper calibration map.
/// </para>
/// <para>
/// Thread-safety: this class is <b>not</b> thread-safe.
/// </para>
/// </remarks>
public sealed class ConfidenceCalibrationEngine
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Number of equal-width reliability bins covering [0, 1].</summary>
    public const int BinCount = 10;

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Default path:
    /// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ML\confidence_calibration.json</c>
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "ML", "confidence_calibration.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly BinState[] _bins = new BinState[BinCount];

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new engine.  Loads persisted bins from
    /// <paramref name="filePath"/> if it exists.
    /// </summary>
    public ConfidenceCalibrationEngine(string? filePath = null)
    {
        for (int i = 0; i < BinCount; i++)
            _bins[i] = new BinState();
        Load(filePath ?? DefaultFilePath);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the outcome of a single A/B test for calibration purposes.
    /// </summary>
    /// <param name="rawConfidence">
    /// The raw (uncalibrated) confidence value in [0, 1] that was produced
    /// by the rule engine, discriminator, or RL policy at prediction time.
    /// </param>
    /// <param name="improved">
    /// <see langword="true"/> when <see cref="AvoPerformanceSetupAI.Telemetry.AbTestResult.Improved"/>
    /// was <see langword="true"/> for the test.
    /// </param>
    /// <param name="filePath">Optional override for the JSON file path.</param>
    public void RecordOutcome(float rawConfidence, bool improved, string? filePath = null)
    {
        int bin = RawToBin(rawConfidence);
        _bins[bin].Total++;
        if (improved) _bins[bin].Successes++;
        Save(filePath ?? DefaultFilePath);
    }

    /// <summary>
    /// Maps <paramref name="rawConfidence"/> to a calibrated probability in [0, 1]
    /// using the piecewise-linear isotonic calibration map built from the
    /// accumulated reliability bins.
    /// </summary>
    /// <remarks>
    /// When a bin has received fewer than 2 observations, its raw midpoint is
    /// used as-is (no calibration data available for that bin).
    /// </remarks>
    public float CalibrateConfidence(float rawConfidence)
    {
        rawConfidence = Math.Clamp(rawConfidence, 0f, 1f);

        // Build isotonic empirical-accuracy array (Pool-Adjacent Violators)
        float[] empirical = BuildIsotonicMap();

        // Interpolate between adjacent bin midpoints
        float binWidth = 1f / BinCount;
        int   binIdx   = RawToBin(rawConfidence);
        float binMid   = (binIdx + 0.5f) * binWidth;

        if (rawConfidence <= binMid || binIdx == 0)
        {
            // Left edge or within-bin left half — interpolate bin(binIdx-1) → bin(binIdx)
            int leftIdx   = Math.Max(0, binIdx - 1);
            float leftMid = (leftIdx + 0.5f) * binWidth;
            float t       = (rawConfidence - leftMid) / (binMid - leftMid + 1e-6f);
            t = Math.Clamp(t, 0f, 1f);
            return empirical[leftIdx] + t * (empirical[binIdx] - empirical[leftIdx]);
        }
        else
        {
            // Within-bin right half — interpolate bin(binIdx) → bin(binIdx+1)
            int rightIdx   = Math.Min(BinCount - 1, binIdx + 1);
            float rightMid = (rightIdx + 0.5f) * binWidth;
            float t        = (rawConfidence - binMid) / (rightMid - binMid + 1e-6f);
            t = Math.Clamp(t, 0f, 1f);
            return empirical[binIdx] + t * (empirical[rightIdx] - empirical[binIdx]);
        }
    }

    /// <summary>Total number of A/B test outcomes recorded across all bins.</summary>
    public int TotalRecorded
    {
        get
        {
            int sum = 0;
            foreach (var b in _bins) sum += b.Total;
            return sum;
        }
    }

    /// <summary>Saves the bin state to <paramref name="filePath"/>.</summary>
    public void Save(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new CalibrationMapState { Bins = new List<BinState>(_bins) };
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
            var state = JsonSerializer.Deserialize<CalibrationMapState>(json, _jsonOptions);
            if (state?.Bins is null || state.Bins.Count != BinCount) return;
            for (int i = 0; i < BinCount; i++)
            {
                _bins[i].Total    = state.Bins[i].Total;
                _bins[i].Successes = state.Bins[i].Successes;
            }
        }
        catch { /* malformed — start fresh */ }
    }

    /// <summary>Maps a raw confidence in [0,1] to a bin index in [0, BinCount−1].</summary>
    private static int RawToBin(float raw)
        => Math.Clamp((int)(raw * BinCount), 0, BinCount - 1);

    /// <summary>
    /// Returns the per-bin empirical accuracy array after applying the
    /// Pool-Adjacent Violators (PAV) isotonic regression to enforce
    /// non-decreasing order.
    /// </summary>
    private float[] BuildIsotonicMap()
    {
        var raw = new float[BinCount];
        for (int i = 0; i < BinCount; i++)
        {
            var b = _bins[i];
            // Fall back to bin midpoint when no data
            raw[i] = b.Total >= 2
                ? (float)b.Successes / b.Total
                : (i + 0.5f) / BinCount;
        }

        // Pool-Adjacent Violators (PAV) — O(n²) but n=10 so negligible.
        return IsotonicRegression(raw);
    }

    /// <summary>
    /// Simple isotonic regression (monotone non-decreasing) via
    /// pool-adjacent violators.
    /// A maximum-iteration guard prevents pathological infinite loops
    /// from floating-point edge cases and future <see cref="BinCount"/> increases.
    /// </summary>
    private static float[] IsotonicRegression(float[] values)
    {
        var result  = (float[])values.Clone();
        int maxIter = result.Length * result.Length + 1;   // O(n²) upper bound
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < result.Length - 1; i++)
            {
                if (result[i] > result[i + 1])
                {
                    // Pool the two adjacent values
                    float avg = (result[i] + result[i + 1]) / 2f;
                    result[i]     = avg;
                    result[i + 1] = avg;
                    changed       = true;
                }
            }
        } while (changed && --maxIter > 0);

        return result;
    }
}
