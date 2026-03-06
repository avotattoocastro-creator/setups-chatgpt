using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Models;

public class SetupModel
{
    public string Id { get; set; } = string.Empty;
    public string Car { get; set; } = string.Empty;
    public string Track { get; set; } = string.Empty;
    public Dictionary<string, string> Sections { get; set; } = new();
}
