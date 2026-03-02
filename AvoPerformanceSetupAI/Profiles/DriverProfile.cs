using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvoPerformanceSetupAI.Profiles;

/// <summary>
/// Persistent per-driver behavioural profile computed by
/// <c>DriverStyleAnalyzer</c> from recent lap telemetry.
/// All index fields are in the range 0..1 unless otherwise noted.
/// </summary>
/// <remarks>
/// <para>
/// Stored as a single JSON file at
/// <c>%USERPROFILE%\Documents\AvoPerformanceSetupAI\DriverProfile.json</c>.
/// </para>
/// <para>
/// All floating-point fields are updated gradually via Exponential Moving
/// Average (EMA) smoothing so that a single unusual lap does not dominate
/// the profile. Use <see cref="Save"/> after each analysis run to persist
/// the updated values.
/// </para>
/// </remarks>
public sealed class DriverProfile
{
    // ── Driver-style indices ──────────────────────────────────────────────────

    /// <summary>
    /// Overall aggressiveness (0..1).
    /// Derived from normalised throttle variance plus brake-spike frequency.
    /// Higher values indicate a driver who applies inputs abruptly rather than
    /// progressively.
    /// </summary>
    public float AggressivenessIndex { get; set; }

    /// <summary>
    /// Brake aggressiveness (0..1).
    /// Measures how steeply the brake pedal is applied relative to the ideal
    /// reference. Higher values indicate hard, late braking.
    /// </summary>
    public float BrakeAggressionIndex { get; set; }

    /// <summary>
    /// Throttle aggressiveness (0..1).
    /// Measures how sharply the throttle is opened on corner exit relative to
    /// the ideal reference. Higher values indicate aggressive power application.
    /// </summary>
    public float ThrottleAggressionIndex { get; set; }

    /// <summary>
    /// Steering aggressiveness (0..1).
    /// Measures peak steering lock relative to the ideal reference. Higher
    /// values indicate larger and faster steering inputs.
    /// </summary>
    public float SteeringAggressionIndex { get; set; }

    /// <summary>
    /// Consistency index (0..1, higher = more consistent).
    /// Computed as 1 minus the normalised variance of corner-entry speed across
    /// the same corners on multiple laps. A value near 1 means the driver hits
    /// the same speed every lap; a value near 0 indicates large variability.
    /// </summary>
    public float ConsistencyIndex { get; set; } = 1f;

    /// <summary>
    /// Preferred balance bias (−1..+1).
    /// Positive values indicate a preference for rear-rotation / oversteer;
    /// negative values indicate a preference for front-grip / understeer.
    /// Computed as the average yaw-gain bias across corners.
    /// </summary>
    public float PreferredBalanceBias { get; set; }

    // ── Metadata ──────────────────────────────────────────────────────────────

    /// <summary>UTC timestamp of the most recent update to this profile.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Full path of the driver-profile JSON file.
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AvoPerformanceSetupAI", "DriverProfile.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Loads the driver profile from <see cref="DefaultFilePath"/>.
    /// Returns a new default profile when the file does not exist or cannot be
    /// deserialised.
    /// </summary>
    public static DriverProfile Load()
    {
        if (!File.Exists(DefaultFilePath)) return new DriverProfile();
        try
        {
            var json = File.ReadAllText(DefaultFilePath);
            return JsonSerializer.Deserialize<DriverProfile>(json, _jsonOptions)
                   ?? new DriverProfile();
        }
        catch
        {
            // Corrupted or incompatible file — start fresh
            return new DriverProfile();
        }
    }

    /// <summary>
    /// Persists this profile to <see cref="DefaultFilePath"/>, updating
    /// <see cref="LastUpdated"/> to the current UTC time.
    /// </summary>
    public void Save()
    {
        LastUpdated = DateTime.UtcNow;
        var directory = Path.GetDirectoryName(DefaultFilePath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(DefaultFilePath, json);
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a compact one-line summary useful for logging, e.g.
    /// "Aggr:0.72 Brk:0.65 Thr:0.58 Str:0.40 Con:0.83 Bal:+0.12".
    /// </summary>
    public override string ToString() =>
        $"Aggr:{AggressivenessIndex:F2} " +
        $"Brk:{BrakeAggressionIndex:F2} " +
        $"Thr:{ThrottleAggressionIndex:F2} " +
        $"Str:{SteeringAggressionIndex:F2} " +
        $"Con:{ConsistencyIndex:F2} " +
        $"Bal:{PreferredBalanceBias:+0.00;-0.00;0.00}";
}
