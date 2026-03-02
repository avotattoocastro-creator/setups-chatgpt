using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Serialises a list of <see cref="SetupParameter"/> back to an Assetto Corsa INI string
/// using the VALUE-per-section (Format A) layout:
/// <code>
/// [PARAM_NAME]
/// VALUE=&lt;numeric&gt;
/// </code>
/// Additional metadata sections (e.g. [CAR], [INFO]) whose keys are non-numeric are
/// written verbatim after the tunable parameters.
/// All numeric values use <see cref="CultureInfo.InvariantCulture"/> (dot decimal).
/// </summary>
public static class SetupIniWriter
{
    /// <summary>
    /// Produces an INI string for the given <paramref name="parameters"/> followed by
    /// the raw key=value lines in <paramref name="nonNumericSections"/>.
    /// </summary>
    /// <param name="parameters">Tunable parameters to serialise as VALUE-per-section.</param>
    /// <param name="nonNumericSections">
    /// Metadata sections to append verbatim, keyed by section name; each value is a
    /// dictionary of key → raw-string pairs (e.g. <c>"MODEL" → "ks_toyota_supra_mkiv"</c>).
    /// Pass an empty dictionary if there are none.
    /// </param>
    public static string WriteValuePerSection(
        IEnumerable<SetupParameter> parameters,
        IDictionary<string, IDictionary<string, string>> nonNumericSections)
    {
        var sb = new StringBuilder();

        // ── Tunable parameters in VALUE-per-section format ────────────────────
        foreach (var p in parameters)
        {
            sb.Append('[').Append(p.Name).AppendLine("]");
            sb.Append("VALUE=")
              .AppendLine(p.Value.ToString(CultureInfo.InvariantCulture));
        }

        // ── Metadata / non-numeric sections ───────────────────────────────────
        foreach (var (section, kvPairs) in nonNumericSections)
        {
            if (kvPairs.Count == 0) continue;

            sb.Append('[').Append(section).AppendLine("]");
            foreach (var (key, value) in kvPairs)
                sb.Append(key).Append('=').AppendLine(value);
        }

        return sb.ToString();
    }
}
