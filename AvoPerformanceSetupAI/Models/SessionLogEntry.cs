using System;

namespace AvoPerformanceSetupAI.Models;

public sealed class SessionLogEntry
{
    public string CarId { get; set; } = string.Empty;
    public string TrackId { get; set; } = string.Empty;
    public string SetupFile { get; set; } = string.Empty;
    public int CompletedLaps { get; set; }
    public int BestLapMs { get; set; }
    public DateTime SessionDateUtc { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }

    public string BestLapDisplay => BestLapMs > 0
        ? TimeSpan.FromMilliseconds(BestLapMs).ToString(@"m\:ss\.fff")
        : "--:--.---";

    public string SessionDateLocal => SessionDateUtc.ToLocalTime().ToString("g");
}
