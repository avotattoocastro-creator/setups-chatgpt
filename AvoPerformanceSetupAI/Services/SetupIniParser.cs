using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Parses Assetto Corsa / ACC setup <c>.ini</c> files into a flat list of <see cref="IniEntry"/> records,
/// preserving the section context of every key so proposals can be applied back accurately.
/// <para>
/// Two formats are supported:
/// <list type="bullet">
/// <item><description>
///   <b>Format A (AC per-section)</b>: each tunable parameter is a section name and its
///   numeric value is stored as <c>VALUE=n</c> inside that section.
///   The emitted <see cref="IniEntry.Key"/> is set to the section name.
/// </description></item>
/// <item><description>
///   <b>Format B (generic key=value)</b>: the key name is the parameter name and the
///   section is the category — standard INI behaviour.
/// </description></item>
/// </list>
/// </para>
/// </summary>
public static class SetupIniParser
{
    /// <summary>
    /// Reads <paramref name="filePath"/> and returns every key=value pair together with its INI section.
    /// Comment lines (starting with <c>;</c>, <c>//</c>, or <c>#</c>) and blank lines are ignored.
    /// </summary>
    public static List<IniEntry> Parse(string filePath)
        => ParseLines(File.ReadLines(filePath));

    /// <summary>
    /// Normalizes JSON-escaped newline / tab sequences (<c>\\r\\n</c>, <c>\\n</c>, <c>\\t</c>)
    /// into real control characters.  Safe to call on text that is already unescaped — the fast
    /// path (<see cref="string.Contains(string)"/>) exits immediately when no escapes are present.
    /// </summary>
    public static string NormalizeText(string text)
    {
        if (!text.Contains("\\n") && !text.Contains("\\t")) return text;
        return text
            .Replace("\\r\\n", "\n")
            .Replace("\\n",    "\n")
            .Replace("\\t",    "\t");
    }

    /// <summary>
    /// Parses INI content already loaded as a string.
    /// Accepts both real line endings (<c>\r\n</c>, <c>\n</c>) and JSON-escaped sequences
    /// (<c>\\r\\n</c>, <c>\\n</c>) that arise when setup text is transmitted as a JSON
    /// string value and the caller has not yet unescaped it.
    /// </summary>
    public static List<IniEntry> ParseText(string text)
        => ParseLines(NormalizeText(text).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

    private static List<IniEntry> ParseLines(IEnumerable<string> lines)
    {
        var entries = new List<IniEntry>();
        var currentSection = string.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Skip blank lines and comments
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("//") || line.StartsWith('#'))
                continue;

            // Section header
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            // Key=value pair — split only at the first '='
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = line[..eqIdx].Trim();
                var val = line[(eqIdx + 1)..].Trim();

                // Format A (AC per-section): VALUE key inside a named section →
                // use the section name as the parameter key so callers see e.g. CAMBER_LF.
                // Both Section and Key are set to currentSection intentionally: the section
                // name IS the parameter identity in this format.
                if (key.Equals("VALUE", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(currentSection) &&
                    double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                {
                    entries.Add(new IniEntry
                    {
                        Section = currentSection,
                        Key     = currentSection,
                        Value   = val
                    });
                }
                else
                {
                    // Format B (generic key=value): key name is the parameter
                    entries.Add(new IniEntry
                    {
                        Section = currentSection,
                        Key     = key,
                        Value   = val
                    });
                }
            }
        }

        return entries;
    }
}
