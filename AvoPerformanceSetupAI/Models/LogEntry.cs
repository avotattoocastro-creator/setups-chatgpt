using System;

namespace AvoPerformanceSetupAI.Models;

public enum LogCategory
{
    INFO,
    AI,
    DATA,
    WARN,
    ERROR
}

public class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogCategory Category { get; init; }
    public string Message { get; init; } = string.Empty;

    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");

    public string CategoryText => Category.ToString();

    /// <summary>Full formatted line, e.g.: [12:34:56.789] [AI] Starting iteration 3</summary>
    public string FormattedLine => $"[{TimestampText}] [{CategoryText}] {Message}";
}
