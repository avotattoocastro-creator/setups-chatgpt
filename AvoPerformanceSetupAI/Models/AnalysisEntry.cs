using System;

namespace AvoPerformanceSetupAI.Models;

/// <summary>
/// A single entry in one of the three analysis terminal panels on the Telemetría tab.
/// Immutable — new entries are appended to the collection, existing ones are never modified.
/// </summary>
public class AnalysisEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string   Tag       { get; init; } = string.Empty;
    public string   Message   { get; init; } = string.Empty;

    public string Time => Timestamp.ToString("HH:mm:ss");
}
