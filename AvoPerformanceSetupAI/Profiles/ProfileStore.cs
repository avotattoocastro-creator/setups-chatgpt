using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvoPerformanceSetupAI.Profiles;

/// <summary>
/// Persists and retrieves <see cref="CarTrackProfile"/> objects as JSON files under
/// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\Profiles\</c>.
/// </summary>
/// <remarks>
/// Each profile is stored as a separate JSON file whose name is the
/// <see cref="CarTrackProfile.Key"/> (with the <c>|</c> separator replaced by
/// <c>__</c> for filesystem compatibility) followed by <c>.json</c>.
/// <para>
/// Thread-safety: this class is <b>not</b> thread-safe. Callers are expected to
/// access it from a single thread (typically the UI thread).
/// </para>
/// </remarks>
public sealed class ProfileStore
{
    // ── Singleton ─────────────────────────────────────────────────────────────

    /// <summary>Application-wide singleton instance.</summary>
    public static ProfileStore Instance { get; } = new();

    // ── Storage directory ─────────────────────────────────────────────────────

    /// <summary>
    /// Root directory where profile JSON files are stored.
    /// Resolved once at construction time.
    /// </summary>
    public string ProfilesDirectory { get; }

    // ── JSON options ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented              = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.Never,
    };

    // ── Constructor ───────────────────────────────────────────────────────────

    private ProfileStore()
    {
        var documents      = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        ProfilesDirectory  = Path.Combine(documents, "AvoPerformanceSetupAI", "Profiles");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the profile for the given <paramref name="carModel"/> /
    /// <paramref name="trackName"/> combination if one is stored on disk, or
    /// creates and immediately saves a new default profile when none exists.
    /// </summary>
    /// <param name="carModel">AC car model identifier.</param>
    /// <param name="trackName">AC track name identifier.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="carModel"/> or <paramref name="trackName"/> is null or
    /// whitespace.
    /// </exception>
    public CarTrackProfile GetOrCreate(string carModel, string trackName)
    {
        if (string.IsNullOrWhiteSpace(carModel))
            throw new ArgumentException("Car model must not be empty.", nameof(carModel));
        if (string.IsNullOrWhiteSpace(trackName))
            throw new ArgumentException("Track name must not be empty.", nameof(trackName));

        var path = FilePath(carModel, trackName);
        if (File.Exists(path))
        {
            var loaded = Load(path);
            if (loaded is not null) return loaded;
        }

        // Create default profile and persist it immediately
        var profile = new CarTrackProfile
        {
            CarModel    = carModel,
            TrackName   = trackName,
            LastUpdated = DateTime.UtcNow,
        };
        SaveInternal(profile, path);
        return profile;
    }

    /// <summary>
    /// Persists <paramref name="profile"/> to disk, updating
    /// <see cref="CarTrackProfile.LastUpdated"/> to the current UTC time.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public void Save(CarTrackProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        profile.LastUpdated = DateTime.UtcNow;
        SaveInternal(profile, FilePath(profile.CarModel, profile.TrackName));
    }

    /// <summary>
    /// Returns a snapshot list of all profiles currently stored on disk.
    /// Profiles that cannot be deserialised are silently skipped.
    /// </summary>
    public IReadOnlyList<CarTrackProfile> ListAll()
    {
        var result = new List<CarTrackProfile>();
        if (!Directory.Exists(ProfilesDirectory)) return result;

        foreach (var file in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
        {
            var p = Load(file);
            if (p is not null) result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Deletes the profile for the given <paramref name="carModel"/> /
    /// <paramref name="trackName"/> combination. Silently does nothing when no
    /// such file exists.
    /// </summary>
    public void Delete(string carModel, string trackName)
    {
        var path = FilePath(carModel, trackName);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full path of the JSON file for the given car/track combination.
    /// The <c>|</c> separator in <see cref="CarTrackProfile.Key"/> is replaced by
    /// <c>__</c> to produce a filesystem-safe filename.
    /// </summary>
    private string FilePath(string carModel, string trackName)
    {
        var profile = new CarTrackProfile { CarModel = carModel, TrackName = trackName };
        var safeName = profile.Key.Replace("|", "__") + ".json";
        return Path.Combine(ProfilesDirectory, safeName);
    }

    private static CarTrackProfile? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CarTrackProfile>(json, SerializerOptions);
        }
        catch
        {
            // Corrupted or incompatible file — skip silently
            return null;
        }
    }

    private void SaveInternal(CarTrackProfile profile, string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ProfilesDirectory;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(profile, SerializerOptions);
        File.WriteAllText(path, json);
    }
}
