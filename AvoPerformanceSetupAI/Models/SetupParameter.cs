using System.Globalization;

namespace AvoPerformanceSetupAI.Models;

/// <summary>
/// A parsed and classified tunable setup parameter with a strongly-typed numeric value.
/// Created from a numeric <see cref="IniEntry"/> after parsing; <see cref="Group"/> and
/// <see cref="SubGroup"/> are filled by <c>SetupParameterClassifier</c>.
/// </summary>
public sealed class SetupParameter
{
    /// <summary>Parameter identity — the INI key (Format B) or section name (Format A).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>INI section the parameter belongs to.</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Numeric value parsed from the INI with <see cref="CultureInfo.InvariantCulture"/>.</summary>
    public double Value { get; set; }

    /// <summary>High-level group assigned by the classifier (e.g. "Tyres", "Alignment").</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Optional sub-group within <see cref="Group"/> (e.g. "Pressure", "Damper").</summary>
    public string SubGroup { get; set; } = string.Empty;

    /// <summary>
    /// Tries to create a <see cref="SetupParameter"/> from an <see cref="IniEntry"/>.
    /// Returns <see langword="null"/> when the entry's value is not a finite number.
    /// </summary>
    public static SetupParameter? FromIniEntry(IniEntry entry)
    {
        if (!double.TryParse(entry.Value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var v))
            return null;

        return new SetupParameter
        {
            Name    = entry.Key,
            Section = entry.Section,
            Value   = v,
        };
    }
}
