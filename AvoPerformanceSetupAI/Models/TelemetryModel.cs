using System;

namespace AvoPerformanceSetupAI.Models;

public class TelemetryModel
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double LapDistPct { get; set; }
    public double SpeedKph { get; set; }
    public double Throttle { get; set; }
    public double Brake { get; set; }
    public int Gear { get; set; }
    public int Rpm { get; set; }
    public double SteeringDeg { get; set; }
    public double[] TyreTemps { get; set; } = new double[4];
    public double DeltaToRef { get; set; }
}
