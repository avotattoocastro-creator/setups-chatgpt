using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AvoPerformanceSetupAI.Reference.Import;

/// <summary>
/// Imports a reference lap from a CSV file, auto-detecting the column
/// delimiter, applying a <see cref="CsvMapping"/> (auto-detected via
/// <see cref="KnownFormats.DetectMapping"/> or supplied by the caller),
/// deriving <see cref="ReferenceLapSample.LapDistPct"/> when not directly
/// provided, and resampling to the fixed
/// <see cref="ReferenceLapResampler.GridSize"/>-point distance grid.
/// </summary>
/// <remarks>
/// <b>Distance alignment</b>: the importer always writes a valid
/// LapDistPct (0..1) for every sample before handing the list to the
/// resampler.  Time-based columns are converted to a [0, 1] fraction
/// only as a last resort and are clearly documented as such.
/// </remarks>
public sealed class CsvReferenceImporter
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the CSV at <paramref name="filePath"/>, auto-detects the format
    /// and delimiter, produces raw samples, derives LapDistPct if needed, and
    /// returns a <see cref="ReferenceLap"/> resampled to exactly
    /// <see cref="ReferenceLapResampler.GridSize"/> distance-aligned points.
    /// </summary>
    /// <param name="filePath">Full path to the CSV file.</param>
    /// <param name="carId">Car identifier stored in the result (not in CSV).</param>
    /// <param name="trackId">Track identifier stored in the result (not in CSV).</param>
    /// <param name="trackLengthMeters">
    /// Track length in metres; only used when the CSV provides distance (metres)
    /// rather than normalised lap position.  Pass 0 to let the importer infer the
    /// length from the maximum distance value found in the file.
    /// </param>
    /// <param name="notes">Free-text notes stored in the returned <see cref="ReferenceLap"/>.</param>
    /// <param name="mapping">
    /// Optional explicit column mapping.  When <see langword="null"/> the mapping
    /// is auto-detected via <see cref="KnownFormats.DetectMapping"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// File not found, fewer than 2 data rows, or no usable columns detected.
    /// </exception>
    public ReferenceLap Import(
        string      filePath,
        string      carId             = "",
        string      trackId           = "",
        float       trackLengthMeters = 0f,
        string      notes             = "",
        CsvMapping? mapping           = null)
    {
        if (!File.Exists(filePath))
            throw new ArgumentException($"File not found: {filePath}", nameof(filePath));

        var lines = File.ReadAllLines(filePath);
        if (lines.Length < 2)
            throw new ArgumentException("CSV has fewer than 2 lines.", nameof(filePath));

        var delimiter = DetectDelimiter(lines[0]);
        var headers   = SplitLine(lines[0], delimiter);

        mapping ??= KnownFormats.DetectMapping(headers);
        if (trackLengthMeters > 0f)
            mapping.TrackLengthMeters = trackLengthMeters;

        if (!mapping.IsUsable)
            throw new ArgumentException(
                "Could not find a usable LapDistPct source or SpeedKmh column in the CSV headers.",
                nameof(filePath));

        var idx        = BuildIndexMap(headers);
        var rawSamples = new List<ReferenceLapSample>(lines.Length);

        for (int row = 1; row < lines.Length; row++)
        {
            if (string.IsNullOrWhiteSpace(lines[row])) continue;
            var sample = ParseRow(SplitLine(lines[row], delimiter), idx, mapping);
            if (sample is not null) rawSamples.Add(sample);
        }

        if (rawSamples.Count < 2)
            throw new ArgumentException(
                "CSV produced fewer than 2 parseable rows.", nameof(filePath));

        // Normalise LapDistPct values to [0, 1] when raw metres or seconds were stored
        DeriveLapDistPct(rawSamples, mapping);

        return new ReferenceLap
        {
            CarId      = carId,
            TrackId    = trackId,
            Source     = ReferenceLapSource.Imported,
            CreatedUtc = DateTime.UtcNow,
            Notes      = notes,
            Samples    = ReferenceLapResampler.Resample(rawSamples),
        };
    }

    // ── Delimiter detection ───────────────────────────────────────────────────

    private static char DetectDelimiter(string headerLine)
    {
        char[] candidates = [',', ';', '\t'];
        int    bestCount  = -1;
        char   best       = ',';
        foreach (var c in candidates)
        {
            int count = 0;
            foreach (var ch in headerLine) if (ch == c) count++;
            if (count > bestCount) { bestCount = count; best = c; }
        }
        return best;
    }

    // ── Line splitting ────────────────────────────────────────────────────────

    private static string[] SplitLine(string line, char delimiter)
    {
        var parts = line.Split(delimiter);
        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim().Trim('"');
        return parts;
    }

    // ── Column index map ──────────────────────────────────────────────────────

    private static Dictionary<string, int> BuildIndexMap(string[] headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
            map.TryAdd(headers[i].Trim(), i);
        return map;
    }

    // ── Row parsing ───────────────────────────────────────────────────────────

    private static ReferenceLapSample? ParseRow(
        string[]                fields,
        Dictionary<string, int> idx,
        CsvMapping              mapping)
    {
        // Speed is required for a valid row
        if (!TryFloat(fields, idx, mapping.SpeedKmhColumn, out var speed))
            return null;

        var s = new ReferenceLapSample
        {
            SpeedKmh          = speed,
            Throttle          = GetFloat(fields, idx, mapping.ThrottleColumn),
            Brake             = GetFloat(fields, idx, mapping.BrakeColumn),
            Steering          = GetFloat(fields, idx, mapping.SteeringColumn),
            Gear              = GetInt  (fields, idx, mapping.GearColumn),
            Rpm               = GetInt  (fields, idx, mapping.RpmColumn),
            LatG              = GetFloat(fields, idx, mapping.LatGColumn),
            LongG             = GetFloat(fields, idx, mapping.LongGColumn),
            SlipAngleFrontAvg = GetFloat(fields, idx, mapping.SlipAngleFrontAvgColumn),
            SlipAngleRearAvg  = GetFloat(fields, idx, mapping.SlipAngleRearAvgColumn),
            WheelSlipRearAvg  = GetFloat(fields, idx, mapping.WheelSlipRearAvgColumn),
            TyreTempFL        = GetFloat(fields, idx, mapping.TyreTempFLColumn),
            TyreTempFR        = GetFloat(fields, idx, mapping.TyreTempFRColumn),
            TyreTempRL        = GetFloat(fields, idx, mapping.TyreTempRLColumn),
            TyreTempRR        = GetFloat(fields, idx, mapping.TyreTempRRColumn),
            TyrePressureFL    = GetFloat(fields, idx, mapping.TyrePressureFLColumn),
            TyrePressureFR    = GetFloat(fields, idx, mapping.TyrePressureFRColumn),
            TyrePressureRL    = GetFloat(fields, idx, mapping.TyrePressureRLColumn),
            TyrePressureRR    = GetFloat(fields, idx, mapping.TyrePressureRRColumn),
        };

        // TyreTempAvg: prefer explicit column; fall back to per-wheel average
        if (TryFloat(fields, idx, mapping.TyreTempAvgColumn, out var tAvg))
            s.TyreTempAvg = tAvg;
        else if (s.TyreTempFL > 0f || s.TyreTempFR > 0f || s.TyreTempRL > 0f || s.TyreTempRR > 0f)
            s.TyreTempAvg = (s.TyreTempFL + s.TyreTempFR + s.TyreTempRL + s.TyreTempRR) * 0.25f;

        // LapDistPct: store the raw value for now; DeriveLapDistPct will normalise later
        if (TryFloat(fields, idx, mapping.LapDistPctColumn,     out var pct))
            s.LapDistPct = Math.Clamp(pct, 0f, 1f);
        else if (TryFloat(fields, idx, mapping.DistanceMetersColumn, out var dist))
            s.LapDistPct = dist;   // raw metres — normalised in DeriveLapDistPct
        else if (TryFloat(fields, idx, mapping.TimeSecondsColumn,    out var t))
            s.LapDistPct = t;      // raw seconds — normalised in DeriveLapDistPct

        return s;
    }

    // ── LapDistPct derivation ─────────────────────────────────────────────────

    private static void DeriveLapDistPct(List<ReferenceLapSample> samples, CsvMapping mapping)
    {
        // Already in [0, 1]: nothing to do
        if (mapping.LapDistPctColumn is not null)
            return;

        if (mapping.DistanceMetersColumn is not null)
        {
            // Normalise by track length (configured or inferred from data)
            var trackLen = mapping.TrackLengthMeters > 0f
                ? mapping.TrackLengthMeters
                : samples[^1].LapDistPct;   // best estimate: last row's raw distance

            // Sanity-check: typical circuits are 1 000 m – 10 000 m
            if (trackLen < 500f || trackLen > 15_000f)
            {
                // Inferred length is implausible; fall back to raw position 0..1 range
                var range = samples[^1].LapDistPct - samples[0].LapDistPct;
                if (range < 1f) return;  // already normalised
                trackLen = range;
            }
            foreach (var s in samples)
                s.LapDistPct = Math.Clamp(s.LapDistPct / trackLen, 0f, 1f);
            return;
        }

        if (mapping.TimeSecondsColumn is not null)
        {
            // Normalise by total elapsed time (time-based grid — not ideal but acceptable)
            var t0        = samples[0].LapDistPct;
            var totalTime = samples[^1].LapDistPct - t0;
            if (totalTime < 1e-6f) return;
            foreach (var s in samples)
                s.LapDistPct = Math.Clamp((s.LapDistPct - t0) / totalTime, 0f, 1f);
        }
    }

    // ── Field accessor helpers ────────────────────────────────────────────────

    private static float GetFloat(string[] f, Dictionary<string, int> idx, string? col)
    {
        TryFloat(f, idx, col, out var v);
        return v;
    }

    private static int GetInt(string[] f, Dictionary<string, int> idx, string? col)
    {
        if (col is null || !idx.TryGetValue(col, out var i) || i >= f.Length)
            return 0;
        if (int.TryParse(f[i], out var iv)) return iv;
        if (float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
            return (int)fv;
        return 0;
    }

    private static bool TryFloat(
        string[] f, Dictionary<string, int> idx, string? col, out float value)
    {
        value = 0f;
        if (col is null || !idx.TryGetValue(col, out var i) || i >= f.Length)
            return false;
        return float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
