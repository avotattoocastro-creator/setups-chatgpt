namespace AvoPerformanceSetupAI.Models;

public class Proposal
{
    public string Section { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Signed adjustment step, e.g. "+1", "-0.05", "+2".
    /// Encodes both direction (+/-) and magnitude (step size).
    /// </summary>
    public string Delta { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable explanation for this proposal (telemetry-rule context).
    /// Empty string when generated from a file-parse heuristic.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Rule confidence in 0..1, proportional to the normalized feature index
    /// that triggered this proposal. 0 when generated from a file-parse heuristic.
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>Confidence formatted as a percentage string, e.g. "73%".</summary>
    public string ConfidenceDisplay => $"{Confidence:P0}";
}
