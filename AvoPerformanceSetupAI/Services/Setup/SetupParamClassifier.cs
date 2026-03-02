using System;
using System.Linq;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>High-level setup parameter categories used for weighted AI proposal generation.</summary>
public enum SetupCategory
{
    Tyres,
    Alignment,
    Electronics,
    Aero,
    Suspension,
    Drivetrain,
    Brakes,
    Gearing,
    Misc,
}

/// <summary>A setup key extended with its <see cref="SetupCategory"/> classification.</summary>
public sealed class CategorizedKeyInfo
{
    /// <summary>Section name from the INI file.</summary>
    public required string Section { get; init; }

    /// <summary>Key name from the INI file.</summary>
    public required string Key { get; init; }

    /// <summary>Raw string value from the INI file.</summary>
    public required string RawValue { get; init; }

    /// <summary>Parsed numeric value, if applicable.</summary>
    public double? NumericValue { get; init; }

    /// <summary>Whether the value could be parsed as numeric.</summary>
    public bool IsNumeric { get; init; }

    /// <summary>Category assigned by <see cref="SetupParamClassifier.Classify"/>.</summary>
    public SetupCategory Category { get; init; }

    /// <summary>Unique identifier in the form "SECTION.KEY".</summary>
    public string Id => $"{Section}.{Key}";
}

/// <summary>
/// Deterministic rule-based classifier for Assetto Corsa setup INI parameters.
/// Provides <see cref="Classify"/>, <see cref="ImpactWeight"/>, and
/// <see cref="SafeStep"/> to drive weighted AI proposal generation.
/// </summary>
public static class SetupParamClassifier
{
    // Shorthand for case-sensitive ordinal comparison (all inputs are already upper-cased).
    private const StringComparison SC = StringComparison.Ordinal;

    // ── Classify ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="SetupCategory"/> for a (section, key) pair.
    /// <para>
    /// Priority order — first matching rule wins:
    /// <list type="number">
    ///   <item>Alignment  — CAMBER/TOE/CASTER/KINGPIN always win regardless of section.</item>
    ///   <item>Brakes     — BIAS (always); BRAKE key (not ENGINE_BRAKE); DUCT (non-tyre section).</item>
    ///   <item>Electronics — ENGINE_BRAKE, ENGINE_MAP, ERS/MGUK, TC, ABS tokens.</item>
    ///   <item>Tyres      — TYRE/TIRE section, or PRESSURE/TEMP/TREAD key.</item>
    ///   <item>Aero       — AERO/WING section; WING/SPLITTER/DIFFUSER/RAKE key.</item>
    ///   <item>Suspension — SUSP/DAMP/SPRING/ARB section; SPRING/ARB/BUMP/BUMPSTOP/RIDE_HEIGHT key.</item>
    ///   <item>Drivetrain — DIFF/DRIVETRAIN section; PRELOAD/*_RAMP/DIFF/CLUTCH key.</item>
    ///   <item>Gearing    — GEAR/RATIO/FINAL section or key.</item>
    ///   <item>Misc       — everything else.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static SetupCategory Classify(string section, string key)
    {
        var sec = (section ?? string.Empty).ToUpperInvariant();
        var k   = (key     ?? string.Empty).ToUpperInvariant();

        // ── 1. Alignment: geometry keys always win regardless of section ───────
        if (HasWord(k, "CAMBER") || HasWord(k, "TOE")    ||
            HasWord(k, "CASTER") || k.Contains("KINGPIN", SC) || k.Contains("ACKERMANN", SC))
            return SetupCategory.Alignment;

        // ── 2. Brakes ─────────────────────────────────────────────────────────
        // BIAS is always a brake parameter.
        if (k.Contains("BIAS", SC)) return SetupCategory.Brakes;
        // BRAKE section → all keys inside are brake-related.
        if (sec.Contains("BRAKE", SC)) return SetupCategory.Brakes;
        // BRAKE in the key, but not ENGINE_BRAKE (that goes to Electronics, rule 3).
        if (k.Contains("BRAKE", SC) && !k.Contains("ENGINE_BRAKE", SC)) return SetupCategory.Brakes;
        // DUCT only when NOT in a tyre section (tyre ducts are cooling, counted as Tyres).
        if (HasWord(k, "DUCT") && !sec.Contains("TYRE", SC) && !sec.Contains("TIRE", SC))
            return SetupCategory.Brakes;

        // ── 3. Electronics ────────────────────────────────────────────────────
        if (sec.Contains("ELECTRONIC", SC) || sec.Contains("ECU", SC))
            return SetupCategory.Electronics;
        if (k.Contains("ENGINE_BRAKE", SC) || k.Contains("ENGINE_MAP", SC) ||
            k.Contains("ERS", SC)          || k.Contains("MGUK", SC))
            return SetupCategory.Electronics;
        // TC and ABS are very short — use whole-word matching to avoid false positives.
        if (HasWord(k, "TC") || HasWord(k, "ABS")) return SetupCategory.Electronics;
        if (k.Contains("_MAP", SC) || k.Contains("MAP_", SC)) return SetupCategory.Electronics;

        // ── 4. Tyres ──────────────────────────────────────────────────────────
        if (sec.Contains("TYRE", SC) || sec.Contains("TIRE", SC)) return SetupCategory.Tyres;
        if (k.Contains("PRESSURE", SC) || k.Contains("TYRE", SC) || k.Contains("TIRE", SC) ||
            k.Contains("TREAD", SC)    || HasWord(k, "TEMP"))
            return SetupCategory.Tyres;

        // ── 5. Aero ───────────────────────────────────────────────────────────
        // DIFFUSER/DIFFUSOR checked here (before DIFF in rule 7) to prevent misclassification.
        if (sec.Contains("AERO", SC) || sec.Contains("WING", SC)) return SetupCategory.Aero;
        if (k.Contains("WING", SC)     || k.Contains("SPLITTER", SC) ||
            k.Contains("DIFFUSER", SC) || k.Contains("DIFFUSOR", SC))
            return SetupCategory.Aero;
        if (HasWord(k, "RAKE")) return SetupCategory.Aero;

        // ── 6. Suspension ─────────────────────────────────────────────────────
        if (sec.Contains("SUSP", SC)  || sec.Contains("DAMP", SC)  ||
            sec.Contains("SPRING", SC) || sec.Contains("SHOCK", SC) || HasWord(sec, "ARB"))
            return SetupCategory.Suspension;
        if (k.Contains("SPRING", SC)     || k.Contains("DAMPER", SC)     || k.Contains("REBOUND", SC) ||
            k.Contains("BUMPSTOP", SC)   || k.Contains("PACKER", SC)     ||
            k.Contains("RIDE_HEIGHT", SC) || k.Contains("RIDEHEIGHT", SC) ||
            k.Contains("ANTIROLL", SC)   || k.Contains("ANTI_ROLL", SC))
            return SetupCategory.Suspension;
        // Short ARB and BUMP tokens — whole-word to avoid matching ABSORBER or BUMPER body parts.
        if (HasWord(k, "ARB") || HasWord(k, "BUMP")) return SetupCategory.Suspension;

        // ── 7. Drivetrain ─────────────────────────────────────────────────────
        if (sec.Contains("DIFF", SC) || sec.Contains("DRIVETRAIN", SC)) return SetupCategory.Drivetrain;
        if (k.Contains("PRELOAD", SC) || k.Contains("POWER_RAMP", SC) ||
            k.Contains("COAST_RAMP", SC) || k.Contains("CLUTCH", SC))
            return SetupCategory.Drivetrain;
        if (HasWord(k, "DIFF")) return SetupCategory.Drivetrain;

        // ── 8. Gearing ────────────────────────────────────────────────────────
        if (sec.Contains("GEAR", SC) || sec.Contains("RATIO", SC) || sec.Contains("FINAL", SC))
            return SetupCategory.Gearing;
        if (HasWord(k, "GEAR") || k.Contains("RATIO", SC) || k.Contains("FINAL", SC))
            return SetupCategory.Gearing;

        return SetupCategory.Misc;
    }

    // ── ImpactWeight ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a 0–1 weight representing the expected influence on lap time.
    /// Higher values make the AI prioritise this parameter when selecting candidates.
    /// <para>
    /// Scale reference:
    /// 0.9 = aero wing (very high lap-time sensitivity);
    /// 0.8 = tyre pressure/temp;  0.7 = camber/toe;
    /// 0.6 = springs/dampers/ARB/brake bias;
    /// 0.5 = misc suspension / drivetrain;
    /// 0.4 = bumpstop/packer (high risk) / TC/ABS;
    /// 0.3 = gearing / brake ducts;
    /// 0.2 = Misc/unknown.
    /// </para>
    /// </summary>
    public static double ImpactWeight(SetupCategory cat, string key)
    {
        var k = (key ?? string.Empty).ToUpperInvariant();
        return cat switch
        {
            SetupCategory.Aero =>
                k.Contains("WING", SC) || k.Contains("SPLITTER", SC) ? 0.9 : 0.7,

            SetupCategory.Tyres =>
                k.Contains("PRESSURE", SC) || HasWord(k, "TEMP") ? 0.8 : 0.5,

            SetupCategory.Alignment =>
                k.Contains("CAMBER", SC) || HasWord(k, "TOE") ? 0.7 : 0.6,

            SetupCategory.Brakes =>
                k.Contains("BIAS", SC)   ? 0.6 :
                HasWord(k, "DUCT")       ? 0.3 : 0.5,

            SetupCategory.Suspension =>
                k.Contains("BUMPSTOP", SC) || k.Contains("PACKER", SC)   ? 0.4 :
                k.Contains("SPRING", SC)   || k.Contains("DAMPER", SC)   ||
                k.Contains("REBOUND", SC)  || HasWord(k, "BUMP")         ||
                HasWord(k, "ARB")                                         ? 0.6 : 0.5,

            SetupCategory.Drivetrain =>
                k.Contains("PRELOAD", SC) || k.Contains("POWER_RAMP", SC) ||
                k.Contains("COAST_RAMP", SC) ? 0.5 : 0.4,

            SetupCategory.Electronics =>
                HasWord(k, "TC") || HasWord(k, "ABS") ? 0.4 : 0.35,

            SetupCategory.Gearing => 0.3,

            _ => 0.2,
        };
    }

    // ── SafeStep ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a safe absolute step (delta) for the given parameter.
    /// Always positive; the caller applies the sign (usually negative = more conservative).
    /// <para>
    /// Steps are tuned for typical AC/ACC value ranges:
    /// wing=1 click; tyre pressure=0.1 PSI/bar; camber=0.05–0.1°; toe=0.001–0.01;
    /// springs/dampers=1 click; gear ratios=0.01; diff preload=1–5 Nm; misc=%‑based.
    /// </para>
    /// </summary>
    public static double SafeStep(SetupCategory cat, string key, double currentValue)
    {
        var k   = (key ?? string.Empty).ToUpperInvariant();
        var abs = Math.Abs(currentValue);

        return cat switch
        {
            // Aero: one click at a time
            SetupCategory.Aero => 1.0,

            // Tyres: small physical units
            SetupCategory.Tyres =>
                k.Contains("PRESSURE", SC) ? 0.1 :
                HasWord(k, "TEMP")         ? 1.0 : 0.05,

            // Alignment: very small physical degrees
            SetupCategory.Alignment =>
                k.Contains("CAMBER", SC) ? (abs >= 10.0 ? 0.1 : 0.05) :
                HasWord(k, "TOE")        ? (abs >= 1.0  ? 0.01 : 0.001) : 0.05,

            // Brakes: bias in 0.5 % steps; ducts & others integer
            SetupCategory.Brakes =>
                k.Contains("BIAS", SC) ? 0.5 : 1.0,

            // Suspension: 1-click steps for all (standard AC setup granularity)
            SetupCategory.Suspension => 1.0,

            // Drivetrain: preload in larger Nm steps when value is large
            SetupCategory.Drivetrain =>
                k.Contains("PRELOAD", SC) && abs >= 100.0 ? 5.0 : 1.0,

            // Electronics: integer steps
            SetupCategory.Electronics => 1.0,

            // Gearing: ratio steps — small
            SetupCategory.Gearing => abs >= 1.0 ? 0.01 : 0.001,

            // Misc / default: percentage-based (legacy behaviour)
            _ => abs >= 100.0 ? Math.Round(abs * 0.02, 0)
               : abs >= 1.0   ? Math.Round(abs * 0.02, 3)
               : 0.05,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="word"/> appears as an exact
    /// underscore-delimited token inside <paramref name="text"/>.
    /// Used for short keywords (≤ 4 chars) to avoid substring false positives
    /// (e.g. "TC" matching "PITCH", or "ARB" matching "GARBAGE").
    /// Avoids array allocation by using boundary checks instead of <c>Split</c>.
    /// </summary>
    private static bool HasWord(string text, string word)
    {
        if (text.Length < word.Length) return false;
        // Exact match
        if (text.Length == word.Length) return text == word;
        // First token: "WORD_..."
        if (text.StartsWith(word, SC) && text[word.Length] == '_') return true;
        // Last token: "..._WORD"
        if (text.EndsWith(word, SC) && text[text.Length - word.Length - 1] == '_') return true;
        // Middle token: "..._WORD_..."
        return text.Contains($"_{word}_", SC);
    }
}
