using System.Globalization;

namespace AvoPerformanceSetupAI.Models;

public class ReferenceLapPoint
{
    public double Time { get; set; }
    public double LapDistPct { get; set; }
    public double Speed { get; set; }
    public double Throttle { get; set; }
    public double Brake { get; set; }

    public static bool TryParseCsv(string line, out ReferenceLapPoint point)
    {
        point = new ReferenceLapPoint();
        var parts = line.Split(',');
        if (parts.Length < 5) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dist)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)) return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var throttle)) return false;
        if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var brake)) return false;
        point.Time = time;
        point.LapDistPct = dist;
        point.Speed = speed;
        point.Throttle = throttle;
        point.Brake = brake;
        return true;
    }
}
