using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvoPerformanceSetupAI.Reference;

/// <summary>
/// Lightweight metadata for a stored reference lap, returned by
/// <see cref="ReferenceLapStore.ListReferences"/> without loading the full
/// sample array.
/// </summary>
public sealed class ReferenceLapMeta
{
    /// <summary>Full path to the JSON file on disk.</summary>
    public string             FilePath   { get; set; } = string.Empty;

    /// <summary>Car model identifier stored in the file.</summary>
    public string             CarId      { get; set; } = string.Empty;

    /// <summary>Track name identifier stored in the file.</summary>
    public string             TrackId    { get; set; } = string.Empty;

    /// <summary>Whether the lap was recorded live or imported.</summary>
    public ReferenceLapSource Source     { get; set; }

    /// <summary>UTC creation timestamp stored in the file.</summary>
    public DateTime           CreatedUtc { get; set; }

    /// <summary>Optional notes stored in the file.</summary>
    public string             Notes      { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly label suitable for display in a list or combo-box.
    /// Format: "dd/MM/yy HH:mm [Source]  Notes".
    /// </summary>
    public string DisplayName =>
        $"{CreatedUtc.ToLocalTime():dd/MM/yy HH:mm} [{Source}]" +
        (string.IsNullOrWhiteSpace(Notes) ? string.Empty : $"  {Notes}");
}

/// <summary>
/// Persists and retrieves <see cref="ReferenceLap"/> objects as versioned JSON
/// files under
/// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\ReferenceLaps\{CarId}\{TrackId}\</c>.
/// </summary>
/// <remarks>
/// Thread-safety: this class is <b>not</b> thread-safe.  Access from a single
/// thread (typically the UI thread) or synchronise externally.
/// </remarks>
public sealed class ReferenceLapStore
{
    // ── Singleton ─────────────────────────────────────────────────────────────

    /// <summary>Application-wide singleton instance.</summary>
    public static ReferenceLapStore Instance { get; } = new();

    // ── JSON options ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Storage path ──────────────────────────────────────────────────────────

    private readonly string _basePath;

    private ReferenceLapStore()
    {
        _basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AvoPerformanceSetupAI", "ReferenceLaps");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises <paramref name="lap"/> to a timestamped JSON file and returns
    /// the full path of the new file.
    /// File name pattern: <c>ref_{yyyyMMdd_HHmmss}_{Source}.json</c>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="lap"/> is null.</exception>
    public string SaveReference(ReferenceLap lap)
    {
        if (lap is null) throw new ArgumentNullException(nameof(lap));

        var dir = LapDirectory(lap.CarId, lap.TrackId);
        Directory.CreateDirectory(dir);

        var ts       = lap.CreatedUtc.ToString("yyyyMMdd_HHmmss");
        var fileName = $"ref_{ts}_{lap.Source}.json";
        var path     = Path.Combine(dir, fileName);

        File.WriteAllText(path, JsonSerializer.Serialize(lap, SerializerOptions));
        return path;
    }

    /// <summary>
    /// Returns lightweight metadata for every stored reference lap for the given
    /// car/track combination, sorted newest-first.
    /// Files that cannot be read are silently skipped.
    /// </summary>
    public IReadOnlyList<ReferenceLapMeta> ListReferences(string carId, string trackId)
    {
        var dir    = LapDirectory(carId, trackId);
        var result = new List<ReferenceLapMeta>();

        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.EnumerateFiles(dir, "ref_*.json"))
        {
            var meta = TryReadMeta(file);
            if (meta is not null) result.Add(meta);
        }

        result.Sort(static (a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return result;
    }

    /// <summary>
    /// Loads and deserialises a full <see cref="ReferenceLap"/> from
    /// <paramref name="filePath"/>.  Returns <see langword="null"/> when the
    /// file is missing or malformed.
    /// </summary>
    public ReferenceLap? LoadReference(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ReferenceLap>(
                File.ReadAllText(filePath), SerializerOptions);
        }
        catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string LapDirectory(string carId, string trackId) =>
        Path.Combine(_basePath, Sanitize(carId), Sanitize(trackId));

    /// <summary>Strips characters that are illegal in file/directory names.</summary>
    private static string Sanitize(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "_unknown";
        return string.Concat(id.Split(Path.GetInvalidFileNameChars()))
                     .ToLowerInvariant();
    }

    private ReferenceLapMeta? TryReadMeta(string filePath)
    {
        var lap = LoadReference(filePath);
        if (lap is null) return null;
        return new ReferenceLapMeta
        {
            FilePath   = filePath,
            CarId      = lap.CarId,
            TrackId    = lap.TrackId,
            Source     = lap.Source,
            CreatedUtc = lap.CreatedUtc,
            Notes      = lap.Notes,
        };
    }
}
