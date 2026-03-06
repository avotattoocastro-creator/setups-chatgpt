using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Models;

public class BaselineEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Car { get; set; } = string.Empty;
    public string Track { get; set; } = string.Empty;
    public string SessionType { get; set; } = "RACE"; // QUALY/RACE/ENDU
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
    public Dictionary<string, string> KeyParams { get; set; } = new();
    public Dictionary<string, double> TyreTargets { get; set; } = new();
    public double AeroFront { get; set; }
    public double AeroRear { get; set; }
    public double AeroBalance { get; set; }
}
