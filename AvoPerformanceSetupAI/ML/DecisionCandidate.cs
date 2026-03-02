using AvoPerformanceSetupAI.ML.Uncertainty;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.ML;

/// <summary>Tier assigned to a <see cref="DecisionCandidate"/> by <see cref="RiskAwareDecisionEngine"/>.</summary>
public enum DecisionTier
{
    /// <summary>Conservative choice: highest calibrated confidence and positive lower-80 % bound.</summary>
    Safe,

    /// <summary>Balanced tradeoff between expected improvement and uncertainty.</summary>
    Balanced,

    /// <summary>Highest expected mean improvement; higher uncertainty accepted.</summary>
    Aggressive,
}

/// <summary>
/// A fully-enriched recommendation candidate produced by
/// <see cref="RiskAwareDecisionEngine"/>.
/// </summary>
public sealed class DecisionCandidate
{
    // ── Source proposal ───────────────────────────────────────────────────────

    /// <summary>The underlying setup-change proposal.</summary>
    public Proposal Proposal { get; init; } = new();

    // ── Probabilistic estimates ───────────────────────────────────────────────

    /// <summary>Probabilistic score-delta estimate from the ensemble + conformal predictor.</summary>
    public UncertaintyEstimate Uncertainty { get; init; }

    // ── Calibrated confidence ─────────────────────────────────────────────────

    /// <summary>
    /// Confidence after isotonic calibration through
    /// <see cref="ConfidenceCalibrationEngine.CalibrateConfidence"/>.
    /// </summary>
    public float CalibratedConfidence { get; init; }

    // ── Risk classification ───────────────────────────────────────────────────

    /// <summary>Risk level of the setup change.</summary>
    public RiskLevel RiskLevel { get; init; }

    // ── Root-cause context ────────────────────────────────────────────────────

    /// <summary>Driver-vs-setup discriminator result at the time the candidate was scored.</summary>
    public RootCauseType RootCause { get; init; }

    // ── Utility score ─────────────────────────────────────────────────────────

    /// <summary>
    /// Final utility score used to rank candidates:
    /// <c>Utility = MeanImprovement − RiskPenalty − InstabilityPenalty − DriverLikelyPenalty</c>.
    /// </summary>
    public float Utility { get; init; }

    // ── Tier ─────────────────────────────────────────────────────────────────

    /// <summary>Safe / Balanced / Aggressive tier assigned by the engine.</summary>
    public DecisionTier Tier { get; init; }

    // ── Explanation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Human-readable explanation of why this candidate was assigned its tier,
    /// e.g. "Chosen because lower-80 % interval is positive and uncertainty is low."
    /// </summary>
    public string Explanation { get; init; } = string.Empty;

    // ── Display helpers for XAML bindings ────────────────────────────────────

    /// <summary>"SAFE", "BALANCED", or "AGGRESSIVE".</summary>
    public string TierDisplay => Tier switch
    {
        DecisionTier.Safe       => "SAFE",
        DecisionTier.Balanced   => "BALANCED",
        DecisionTier.Aggressive => "AGGRESSIVE",
        _                       => Tier.ToString().ToUpperInvariant(),
    };

    /// <summary>"SECTION:PARAM Δ", e.g. "ARB:REAR +1".</summary>
    public string ProposalDisplay => $"{Proposal.Section}:{Proposal.Parameter} {Proposal.Delta}";

    /// <summary>Mean Δscore formatted with sign, e.g. "+5.2".</summary>
    public string MeanDisplay => $"{Uncertainty.Mean:+0.0;-0.0}";

    /// <summary>Risk level label, e.g. "Low", "Med", "High".</summary>
    public string RiskDisplay => RiskLevel switch
    {
        RiskLevel.High   => "High",
        RiskLevel.Medium => "Med",
        _                => "Low",
    };

    /// <summary>WinUI hex foreground colour for the risk badge.</summary>
    public string RiskForeground => RiskLevel switch
    {
        RiskLevel.High   => "#FFE04040",
        RiskLevel.Medium => "#FFFFA040",
        _                => "#FF80E040",
    };

    /// <summary>Calibrated confidence as a percentage string, e.g. "73 %".</summary>
    public string ConfidenceDisplay => $"{CalibratedConfidence:P0}";

    /// <summary>80 % interval formatted, e.g. "[+1.3 .. +9.1]".</summary>
    public string Interval80Display =>
        $"[{Uncertainty.Lower80:+0.0;-0.0} .. {Uncertainty.Upper80:+0.0;-0.0}]";

    /// <summary>95 % interval formatted, e.g. "[-0.8 .. +11.2]".</summary>
    public string Interval95Display =>
        $"[{Uncertainty.Lower95:+0.0;-0.0} .. {Uncertainty.Upper95:+0.0;-0.0}]";
}
