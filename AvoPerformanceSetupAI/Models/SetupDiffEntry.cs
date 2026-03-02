using AvoPerformanceSetupAI.Services.Setup;

namespace AvoPerformanceSetupAI.Models;

public enum SetupDiffKind { Added, Removed, Changed }

public sealed class SetupDiffEntry
{
    public SetupDiffKind Kind { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Section { get; init; } = "";
    public string? Group { get; init; }
    public double? OldValue { get; init; }
    public double? NewValue { get; init; }
    public double? Delta { get; init; }
    public double? DeltaPercent { get; init; }

    // ── Extended properties for real-diff support ─────────────────────────────

    /// <summary>Parameter category from <see cref="SetupParamClassifier"/>.</summary>
    public SetupCategory Category { get; init; } = SetupCategory.Misc;

    /// <summary>Raw string value from the base INI (before changes).</summary>
    public string OldRaw { get; init; } = string.Empty;

    /// <summary>Raw string value from the proposed INI (after changes).</summary>
    public string NewRaw { get; init; } = string.Empty;

    /// <summary>
    /// Signed direction of the change: +1 = increased, -1 = decreased, 0 = non-numeric / neutral.
    /// Used by <c>DeltaSignToColorConverter</c> to colour the badge.
    /// </summary>
    public int DeltaSign =>
        Delta.HasValue
            ? (Delta.Value > 0 ? 1 : Delta.Value < 0 ? -1 : 0)
            : Kind == SetupDiffKind.Added   ?  1
            : Kind == SetupDiffKind.Removed ? -1
            : 0;

    /// <summary>Badge symbol shown next to the row: ▲ up, ▼ down, or ~ neutral.</summary>
    public string BadgeText => DeltaSign switch
    {
         1 => "▲",
        -1 => "▼",
        _  => "~",
    };

    // ── Display helpers for x:Bind ────────────────────────────────────────────

    public string KindDisplay => Kind switch
    {
        SetupDiffKind.Added   => "+ Added",
        SetupDiffKind.Removed => "- Removed",
        _                     => "~ Changed",
    };

    public string OldValueDisplay => OldValue.HasValue
        ? OldValue.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    public string NewValueDisplay => NewValue.HasValue
        ? NewValue.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    public string DeltaDisplay => Delta.HasValue
        ? (Delta.Value >= 0 ? "+" : "") +
          Delta.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    public string DeltaPercentDisplay => DeltaPercent.HasValue
        ? (DeltaPercent.Value >= 0 ? "+" : "") +
          DeltaPercent.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "%"
        : string.Empty;
}
