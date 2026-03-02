using System;
using System.Collections.Generic;
using System.Linq;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Compares two lists of <see cref="SetupParameter"/> and produces a flat diff.
/// Parameters are keyed by <c>section::name</c> (lower-invariant).
/// </summary>
public sealed class SetupDiffEngine
{
    private const double DefaultEpsilon = 1e-9;

    /// <summary>
    /// Produces Added / Removed / Changed entries between <paramref name="aParams"/> (baseline)
    /// and <paramref name="bParams"/> (candidate).  Only numeric parameters are compared.
    /// </summary>
    public IReadOnlyList<SetupDiffEntry> Diff(
        IEnumerable<SetupParameter> aParams,
        IEnumerable<SetupParameter> bParams,
        double epsilon = DefaultEpsilon)
    {
        static string MakeKey(SetupParameter p)
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? p.Section : p.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "UNKNOWN";
            return $"{p.Section}::{name}".ToLowerInvariant();
        }

        var A = aParams.ToDictionary(MakeKey);
        var B = bParams.ToDictionary(MakeKey);

        var allKeys = new HashSet<string>(A.Keys);
        allKeys.UnionWith(B.Keys);

        var result = new List<SetupDiffEntry>();

        foreach (var k in allKeys.OrderBy(x => x))
        {
            var hasA = A.TryGetValue(k, out var a);
            var hasB = B.TryGetValue(k, out var b);

            if (!hasA && hasB)
            {
                result.Add(new SetupDiffEntry
                {
                    Kind     = SetupDiffKind.Added,
                    Key      = k,
                    Name     = b!.Name,
                    Section  = b.Section,
                    Group    = NullIfEmpty(b.Group),
                    NewValue = b.Value,
                });
                continue;
            }

            if (hasA && !hasB)
            {
                result.Add(new SetupDiffEntry
                {
                    Kind     = SetupDiffKind.Removed,
                    Key      = k,
                    Name     = a!.Name,
                    Section  = a.Section,
                    Group    = NullIfEmpty(a.Group),
                    OldValue = a.Value,
                });
                continue;
            }

            // Both present — emit Changed only when the difference is significant
            var delta = b!.Value - a!.Value;
            if (Math.Abs(delta) > epsilon)
            {
                result.Add(new SetupDiffEntry
                {
                    Kind         = SetupDiffKind.Changed,
                    Key          = k,
                    Name         = b.Name,
                    Section      = b.Section,
                    Group        = NullIfEmpty(b.Group) ?? NullIfEmpty(a.Group),
                    OldValue     = a.Value,
                    NewValue     = b.Value,
                    Delta        = delta,
                    DeltaPercent = Math.Abs(a.Value) > epsilon
                                       ? (delta / a.Value) * 100.0
                                       : null,
                });
            }
        }

        return result;
    }

    // ── Group summary ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns changed-count per group, sorted descending by count.
    /// </summary>
    public IReadOnlyList<(string Group, int Count)> GroupSummary(
        IReadOnlyList<SetupDiffEntry> diff)
    {
        return diff
            .Where(e => e.Kind == SetupDiffKind.Changed)
            .GroupBy(e => e.Group ?? "Other")
            .Select(g => (Group: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList();
    }

    /// <summary>
    /// Returns the top-<paramref name="n"/> entries by absolute delta, descending.
    /// </summary>
    public IReadOnlyList<SetupDiffEntry> TopDeltas(
        IReadOnlyList<SetupDiffEntry> diff, int n = 5)
    {
        return diff
            .Where(e => e.Delta.HasValue)
            .OrderByDescending(e => Math.Abs(e.Delta!.Value))
            .Take(n)
            .ToList();
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;
}
