namespace AvoPerformanceSetupAI.Models;

/// <summary>Represents a single key-value pair parsed from an Assetto Corsa setup INI file.</summary>
public class IniEntry
{
    public string Section { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
