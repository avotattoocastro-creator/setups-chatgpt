using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Reference.Import;

/// <summary>
/// Named CSV format presets and the universal alias table used by
/// <see cref="CsvMapping.FromHeaders"/> for best-effort header auto-detection.
/// </summary>
public static class KnownFormats
{
    // ── Universal alias table ─────────────────────────────────────────────────

    /// <summary>
    /// Maps lower-case CSV header aliases to canonical internal field names.
    /// Covers MoTeC i2, Garage61, AC Logger, and common generic naming conventions.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── LapDistPct (preferred: already normalised 0..1) ────────────────
            ["lap_dist_pct"]              = "LapDistPct",
            ["lapdistpct"]                = "LapDistPct",
            ["norm_dist"]                 = "LapDistPct",
            ["normalized_pos"]            = "LapDistPct",
            ["lap position"]              = "LapDistPct",
            ["lap_position"]              = "LapDistPct",
            ["dist_pct"]                  = "LapDistPct",

            // ── Distance in metres ─────────────────────────────────────────────
            ["distance"]                  = "DistanceMeters",
            ["dist"]                      = "DistanceMeters",
            ["distancem"]                 = "DistanceMeters",
            ["distance_m"]                = "DistanceMeters",
            ["lap_distance"]              = "DistanceMeters",
            ["odometer"]                  = "DistanceMeters",

            // ── Elapsed time in seconds (last-resort LapDistPct derivation) ────
            ["time"]                      = "TimeSeconds",
            ["time_s"]                    = "TimeSeconds",
            ["timestamp"]                 = "TimeSeconds",
            ["lap_time"]                  = "TimeSeconds",
            ["elapsed_time"]              = "TimeSeconds",
            ["time (s)"]                  = "TimeSeconds",

            // ── Speed ──────────────────────────────────────────────────────────
            ["speed"]                     = "SpeedKmh",
            ["v"]                         = "SpeedKmh",
            ["velocity"]                  = "SpeedKmh",
            ["speed_kmh"]                 = "SpeedKmh",
            ["kmh"]                       = "SpeedKmh",
            ["vcar"]                      = "SpeedKmh",
            ["ground speed"]              = "SpeedKmh",
            ["ground_speed"]              = "SpeedKmh",
            ["speed (km/h)"]              = "SpeedKmh",
            ["speedkmh"]                  = "SpeedKmh",

            // ── Throttle ──────────────────────────────────────────────────────
            ["throttle"]                  = "Throttle",
            ["gas"]                       = "Throttle",
            ["tps"]                       = "Throttle",
            ["throttle_pos"]              = "Throttle",
            ["throttle (%)"]              = "Throttle",
            ["accel"]                     = "Throttle",
            ["accelerator"]               = "Throttle",
            ["throttlepos"]               = "Throttle",

            // ── Brake ─────────────────────────────────────────────────────────
            ["brake"]                     = "Brake",
            ["brake_pos"]                 = "Brake",
            ["brakepressure"]             = "Brake",
            ["brake (%)"]                 = "Brake",
            ["brakes"]                    = "Brake",
            ["brakepos"]                  = "Brake",

            // ── Steering ──────────────────────────────────────────────────────
            ["steer"]                     = "Steering",
            ["steering"]                  = "Steering",
            ["steer_angle"]               = "Steering",
            ["steering_angle"]            = "Steering",
            ["steer (deg)"]               = "Steering",
            ["steering_wheel_angle"]      = "Steering",
            ["steerangle"]                = "Steering",

            // ── Gear ──────────────────────────────────────────────────────────
            ["gear"]                      = "Gear",
            ["current_gear"]              = "Gear",
            ["selectedgear"]              = "Gear",
            ["gear_pos"]                  = "Gear",

            // ── RPM ───────────────────────────────────────────────────────────
            ["rpm"]                       = "Rpm",
            ["engine_rpm"]                = "Rpm",
            ["enginespeed"]               = "Rpm",
            ["engine_speed"]              = "Rpm",
            ["revs"]                      = "Rpm",
            ["rpm (1/min)"]               = "Rpm",
            ["rpms"]                      = "Rpm",

            // ── G-forces ──────────────────────────────────────────────────────
            ["latg"]                      = "LatG",
            ["lat_g"]                     = "LatG",
            ["lateral_g"]                 = "LatG",
            ["g_lateral"]                 = "LatG",
            ["lat_acc"]                   = "LatG",
            ["lateral acceleration"]      = "LatG",
            ["accglateral"]               = "LatG",

            ["longg"]                     = "LongG",
            ["long_g"]                    = "LongG",
            ["longitudinal_g"]            = "LongG",
            ["g_longitudinal"]            = "LongG",
            ["lon_acc"]                   = "LongG",
            ["longitudinal acceleration"] = "LongG",
            ["accglongitudinal"]          = "LongG",

            // ── Slip angles ───────────────────────────────────────────────────
            ["slip_front"]                = "SlipAngleFrontAvg",
            ["sa_front"]                  = "SlipAngleFrontAvg",
            ["slip_angle_front"]          = "SlipAngleFrontAvg",

            ["slip_rear"]                 = "SlipAngleRearAvg",
            ["sa_rear"]                   = "SlipAngleRearAvg",
            ["slip_angle_rear"]           = "SlipAngleRearAvg",

            ["wheel_slip_rear"]           = "WheelSlipRearAvg",
            ["slip_rear_avg"]             = "WheelSlipRearAvg",

            // ── Tyre temperatures ─────────────────────────────────────────────
            ["tyre_temp_avg"]             = "TyreTempAvg",
            ["tyre_temp"]                 = "TyreTempAvg",
            ["tire_temp"]                 = "TyreTempAvg",

            ["tyre_temp_fl"]              = "TyreTempFL",
            ["tyretempfl"]                = "TyreTempFL",
            ["tfl"]                       = "TyreTempFL",

            ["tyre_temp_fr"]              = "TyreTempFR",
            ["tyretempfr"]                = "TyreTempFR",
            ["tfr"]                       = "TyreTempFR",

            ["tyre_temp_rl"]              = "TyreTempRL",
            ["tyretemprl"]                = "TyreTempRL",
            ["trl"]                       = "TyreTempRL",

            ["tyre_temp_rr"]              = "TyreTempRR",
            ["tyretemprr"]                = "TyreTempRR",
            ["trr"]                       = "TyreTempRR",

            // ── Tyre pressures ────────────────────────────────────────────────
            ["tyre_press_fl"]             = "TyrePressureFL",
            ["tyre_pressure_fl"]          = "TyrePressureFL",
            ["tyrepressurefl"]            = "TyrePressureFL",
            ["pfl"]                       = "TyrePressureFL",

            ["tyre_press_fr"]             = "TyrePressureFR",
            ["tyre_pressure_fr"]          = "TyrePressureFR",
            ["tyrepressurefr"]            = "TyrePressureFR",
            ["pfr"]                       = "TyrePressureFR",

            ["tyre_press_rl"]             = "TyrePressureRL",
            ["tyre_pressure_rl"]          = "TyrePressureRL",
            ["tyrepressurerl"]            = "TyrePressureRL",
            ["prl"]                       = "TyrePressureRL",

            ["tyre_press_rr"]             = "TyrePressureRR",
            ["tyre_pressure_rr"]          = "TyrePressureRR",
            ["tyrepressurerr"]            = "TyrePressureRR",
            ["prr"]                       = "TyrePressureRR",
        };

    // ── Named format presets ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a pre-filled <see cref="CsvMapping"/> for a known named format,
    /// or <see langword="null"/> when the name is not recognised.
    /// Known names (case-insensitive): "GARAGE61", "MOTEC", "ACLOGGER".
    /// </summary>
    public static CsvMapping? TryGetPreset(string? formatName) =>
        formatName?.ToUpperInvariant() switch
        {
            "GARAGE61" => Garage61Preset(),
            "MOTEC"    => MoTeCPreset(),
            "ACLOGGER" => AcLoggerPreset(),
            _          => null,
        };

    // ── Format auto-detection ─────────────────────────────────────────────────

    /// <summary>
    /// Inspects <paramref name="headers"/> for fingerprint columns that identify
    /// a known format.  Falls back to alias-based best-effort matching when no
    /// preset matches.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is null.</exception>
    public static CsvMapping DetectMapping(IReadOnlyList<string> headers)
    {
        if (headers is null) throw new ArgumentNullException(nameof(headers));

        var set = new HashSet<string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers) set.Add(h.Trim());

        // Garage61 — lowercase canonical headers + latg
        if (set.Contains("lap_dist_pct") && set.Contains("latg"))
            return Garage61Preset();

        // MoTeC i2 — title-case "Ground Speed" + "Engine Speed"
        if (set.Contains("Ground Speed") && set.Contains("Engine Speed"))
            return MoTeCPreset();

        // AC Logger internal format — Pascal-case "NormalizedLapPos" + "Rpms"
        if (set.Contains("NormalizedLapPos") && set.Contains("Rpms"))
            return AcLoggerPreset();

        // Best-effort alias matching
        return CsvMapping.FromHeaders(headers);
    }

    // ── Preset factories ──────────────────────────────────────────────────────

    private static CsvMapping Garage61Preset() => new()
    {
        LapDistPctColumn       = "lap_dist_pct",
        SpeedKmhColumn         = "speed",
        ThrottleColumn         = "throttle",
        BrakeColumn            = "brake",
        SteeringColumn         = "steer",
        GearColumn             = "gear",
        RpmColumn              = "rpm",
        LatGColumn             = "latg",
        LongGColumn            = "longg",
    };

    private static CsvMapping MoTeCPreset() => new()
    {
        DistanceMetersColumn   = "Distance",
        SpeedKmhColumn         = "Ground Speed",
        ThrottleColumn         = "Throttle Pos",
        BrakeColumn            = "Brake Pos",
        SteeringColumn         = "Steering Angle",
        GearColumn             = "Gear",
        RpmColumn              = "Engine Speed",
        LatGColumn             = "Lateral Acceleration",
        LongGColumn            = "Longitudinal Acceleration",
        TyreTempFLColumn       = "Tyre Temp FL",
        TyreTempFRColumn       = "Tyre Temp FR",
        TyreTempRLColumn       = "Tyre Temp RL",
        TyreTempRRColumn       = "Tyre Temp RR",
        TyrePressureFLColumn   = "Tyre Press FL",
        TyrePressureFRColumn   = "Tyre Press FR",
        TyrePressureRLColumn   = "Tyre Press RL",
        TyrePressureRRColumn   = "Tyre Press RR",
    };

    private static CsvMapping AcLoggerPreset() => new()
    {
        LapDistPctColumn       = "NormalizedLapPos",
        SpeedKmhColumn         = "SpeedKmh",
        ThrottleColumn         = "Throttle",
        BrakeColumn            = "Brake",
        SteeringColumn         = "SteerAngle",
        GearColumn             = "Gear",
        RpmColumn              = "Rpms",
        LatGColumn             = "AccGLateral",
        LongGColumn            = "AccGLongitudinal",
        TyreTempFLColumn       = "TyreTempFL",
        TyreTempFRColumn       = "TyreTempFR",
        TyreTempRLColumn       = "TyreTempRL",
        TyreTempRRColumn       = "TyreTempRR",
        TyrePressureFLColumn   = "TyrePressureFL",
        TyrePressureFRColumn   = "TyrePressureFR",
        TyrePressureRLColumn   = "TyrePressureRL",
        TyrePressureRRColumn   = "TyrePressureRR",
    };
}
