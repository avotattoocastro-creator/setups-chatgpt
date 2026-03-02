using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services.Setup;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Provides a simple INI-to-dictionary parser and a text-level setup diff calculator.
/// Unlike <see cref="SetupDiffEngine"/> (which requires pre-parsed numeric parameters),
/// this service works directly on raw INI text and preserves every key/value — including
/// non-numeric metadata — so the diff faithfully mirrors what the Agent wrote to disk.
/// </summary>
public sealed class SetupDiffService
{
    // ── INI parsing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <paramref name="text"/> into a two-level dictionary:
    /// <c>SECTION_UPPER → (KEY_UPPER → rawValue)</c>.
    /// <list type="bullet">
    ///   <item>Section and key names are normalised to <see cref="string.ToUpperInvariant"/>.</item>
    ///   <item>Comment lines starting with <c>;</c> or <c>#</c> are ignored.</item>
    ///   <item>Leading/trailing whitespace on names and values is trimmed.</item>
    ///   <item>Duplicate keys within the same section take the last occurrence.</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ParseIniToDictionary(string text)
    {
        var result  = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var section = string.Empty;

        foreach (var raw in SetupIniParser.NormalizeText(text)
                                          .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            // Skip blank lines and comment lines (;, #, //)
            if (line.Length == 0) continue;
            if (line[0] == ';' || line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            // Section header
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim().ToUpperInvariant();
                if (!result.ContainsKey(section))
                    result[section] = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            // Key=value pair
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                var key = line[..eq].Trim().ToUpperInvariant();
                var val = line[(eq + 1)..].Trim();

                if (!result.TryGetValue(section, out var keys))
                    result[section] = keys = new Dictionary<string, string>(StringComparer.Ordinal);

                keys[key] = val;
            }
        }

        return result;
    }

    // ── Diff computation ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes the diff between <paramref name="baseText"/> (before changes) and
    /// <paramref name="proposedText"/> (after changes).
    /// Returns only entries where the value actually changed (or a key was added/removed).
    /// Each entry is enriched with a <see cref="SetupCategory"/> from
    /// <see cref="SetupParamClassifier.Classify"/>.
    /// </summary>
    public IReadOnlyList<SetupDiffEntry> ComputeDiff(string baseText, string proposedText)
    {
        var baseDict     = ParseIniToDictionary(baseText);
        var proposedDict = ParseIniToDictionary(proposedText);

        var allSections = new HashSet<string>(baseDict.Keys, StringComparer.Ordinal);
        allSections.UnionWith(proposedDict.Keys);

        var result = new List<SetupDiffEntry>();

        foreach (var sec in allSections.OrderBy(s => s))
        {
            var hasBase = baseDict    .TryGetValue(sec, out var baseKeys);
            var hasProp = proposedDict.TryGetValue(sec, out var propKeys);

            var allKeys = new HashSet<string>(StringComparer.Ordinal);
            if (hasBase) allKeys.UnionWith(baseKeys!.Keys);
            if (hasProp) allKeys.UnionWith(propKeys!.Keys);

            foreach (var key in allKeys.OrderBy(k => k))
            {
                var oldRaw = hasBase && baseKeys!.TryGetValue(key, out var ov) ? ov : null;
                var newRaw = hasProp && propKeys!.TryGetValue(key, out var nv) ? nv : null;

                // No change — skip
                if (oldRaw == newRaw) continue;

                var kind = oldRaw is null ? SetupDiffKind.Added
                         : newRaw is null ? SetupDiffKind.Removed
                         : SetupDiffKind.Changed;

                var cat    = SetupParamClassifier.Classify(sec, key);
                var oldNum = TryParseDouble(oldRaw);
                var newNum = TryParseDouble(newRaw);
                var delta  = oldNum.HasValue && newNum.HasValue ? newNum - oldNum : (double?)null;

                result.Add(new SetupDiffEntry
                {
                    Kind         = kind,
                    Key          = $"{sec}.{key}",
                    Name         = key,
                    Section      = sec,
                    Category     = cat,
                    Group        = cat.ToString(),
                    OldRaw       = oldRaw ?? string.Empty,
                    NewRaw       = newRaw ?? string.Empty,
                    OldValue     = oldNum,
                    NewValue     = newNum,
                    Delta        = delta,
                    DeltaPercent = delta.HasValue && oldNum.HasValue && Math.Abs(oldNum.Value) > 1e-9
                                       ? delta.Value / oldNum.Value * 100.0
                                       : (double?)null,
                });
            }
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double? TryParseDouble(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : (double?)null;
}
