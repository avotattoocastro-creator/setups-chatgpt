using System.Collections.Generic;
using System.Linq;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Classifies <see cref="SetupParameter"/> instances into high-level groups and sub-groups
/// based on keyword heuristics applied to the parameter name and section.
/// <para>
/// Priority order (first match wins):
/// Tyres → Alignment → Suspension → Aero → Brakes → Electronics → Drivetrain → Engine → Other
/// </para>
/// </summary>
public static class SetupParameterClassifier
{
    // ── Classification rules ──────────────────────────────────────────────────
    // Each entry: (Group, SubGroup, keywords[])
    // Keywords ≤ 4 chars are matched as whole tokens (split on '_' / ' ').
    // Keywords ≥ 5 chars are matched with simple string.Contains.
    private static readonly (string Group, string SubGroup, string[] Keywords)[] Rules =
    [
        // ── Tyres (pressure before generic tyre/tire to get the right SubGroup) ──
        ("Tyres",       "Pressure",   ["pressure", "psi", "kpa"]),
        ("Tyres",       "Compound",   ["tyre", "tire"]),

        // ── Alignment ──────────────────────────────────────────────────────────
        ("Alignment",   "Camber",     ["camber"]),
        ("Alignment",   "Toe",        ["toe"]),
        ("Alignment",   "Caster",     ["caster"]),
        ("Alignment",   "Other",      ["ackermann"]),

        // ── Suspension (bumpstop before bump so BUMPSTOP_RANGE gets its own SubGroup) ─
        ("Suspension",  "Bumpstop",   ["bumpstop"]),
        ("Suspension",  "Spring",     ["spring"]),
        ("Suspension",  "Damper",     ["damper", "rebound", "bump"]),
        ("Suspension",  "ARB",        ["arb", "antiroll", "anti_roll"]),
        ("Suspension",  "RideHeight", ["ride_height", "rideheight"]),
        ("Suspension",  "Other",      ["suspension", "susp"]),

        // ── Aero (diffuser before diff so DIFFUSER_* hits Aero not Drivetrain) ──
        ("Aero",        "Wing",       ["wing"]),
        ("Aero",        "Splitter",   ["splitter"]),
        ("Aero",        "Diffuser",   ["diffuser"]),
        ("Aero",        "Rake",       ["rake"]),
        ("Aero",        "Other",      ["aero"]),

        // ── Brakes ─────────────────────────────────────────────────────────────
        ("Brakes",      "Bias",       ["bias", "bbias"]),
        ("Brakes",      "Duct",       ["duct"]),
        ("Brakes",      "Pad",        ["pad"]),
        ("Brakes",      "Other",      ["brake"]),

        // ── Electronics (engine_map before engine so it goes here, not Engine) ─
        ("Electronics", "Map",        ["engine_map"]),
        ("Electronics", "TC",         ["tc"]),
        ("Electronics", "ABS",        ["abs"]),
        ("Electronics", "ERS",        ["ers", "mguk"]),
        ("Electronics", "Other",      ["electronics"]),

        // ── Drivetrain ──────────────────────────────────────────────────────────
        ("Drivetrain",  "Diff",       ["diff", "preload", "ramp"]),
        ("Drivetrain",  "Gears",      ["gear", "ratio", "final"]),

        // ── Engine ─────────────────────────────────────────────────────────────
        ("Engine",      "Other",      ["engine", "turbo", "boost"]),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Assigns <see cref="SetupParameter.Group"/> and <see cref="SetupParameter.SubGroup"/> in-place.</summary>
    public static void Classify(SetupParameter p)
    {
        var token = $"{p.Name}_{p.Section}".ToLowerInvariant();

        foreach (var (group, subGroup, keywords) in Rules)
        {
            if (keywords.Any(k => Matches(token, k)))
            {
                p.Group    = group;
                p.SubGroup = subGroup;
                return;
            }
        }

        p.Group    = "Other";
        p.SubGroup = string.Empty;
    }

    /// <summary>Classifies every parameter in the collection in-place.</summary>
    public static void ClassifyAll(IEnumerable<SetupParameter> parameters)
    {
        foreach (var p in parameters)
            Classify(p);
    }

    // ── Matching helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// For short keywords (≤ 4 chars) uses token-split equality to avoid false positives
    /// (e.g. "rake" matching "brake", or "tc" matching "pitch").
    /// For longer keywords uses <see cref="string.Contains"/>.
    /// </summary>
    private static bool Matches(string token, string keyword)
    {
        if (keyword.Length <= 4)
            return token.Split('_', ' ').Any(part => part == keyword);

        return token.Contains(keyword, System.StringComparison.Ordinal);
    }
}
