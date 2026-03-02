using System;
using System.Collections.Generic;

namespace AvoPerformanceSetupAI.Profiles;

/// <summary>
/// Optimal tyre-temperature operating window for a given car/track combination.
/// Both values are in degrees Celsius.
/// </summary>
public sealed class TyreTempRange
{
    /// <summary>Lower bound of the optimal tyre-temperature window (°C).</summary>
    public float Min { get; set; } = 75f;

    /// <summary>Upper bound of the optimal tyre-temperature window (°C).</summary>
    public float Max { get; set; } = 95f;
}

/// <summary>
/// Persistent per-car-and-track configuration profile.
/// Keyed by the combination of <see cref="CarModel"/> and <see cref="TrackName"/>.
/// Both values are populated automatically from Assetto Corsa shared-memory statics
/// / graphics when available; otherwise the caller may supply them manually.
/// </summary>
/// <remarks>
/// Serialised to and from JSON by <see cref="ProfileStore"/>.
/// All floating-point biases are additive offsets applied on top of the rule-engine
/// thresholds, not multipliers — a bias of 0 leaves the engine unchanged.
/// </remarks>
public sealed class CarTrackProfile
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Assetto Corsa car model identifier (e.g. "ks_ferrari_488_gt3").
    /// Populated from <c>acpmf_static.carModel</c> when AC is connected;
    /// otherwise supplied manually.
    /// </summary>
    public string CarModel { get; set; } = string.Empty;

    /// <summary>
    /// Assetto Corsa track name (e.g. "ks_silverstone").
    /// Populated from <c>acpmf_static.track</c> when AC is connected;
    /// otherwise supplied manually.
    /// </summary>
    public string TrackName { get; set; } = string.Empty;

    // ── Bias parameters ───────────────────────────────────────────────────────

    /// <summary>
    /// Additive offset (0..1) applied to the understeer feature indices before
    /// evaluating rules. A positive bias makes understeer rules easier to trigger;
    /// a negative bias raises the effective threshold.
    /// Clamped to [−1, 1] on use.
    /// </summary>
    public float BaselineUndersteerBias { get; set; }

    /// <summary>
    /// Additive offset (0..1) applied to the oversteer feature indices before
    /// evaluating rules. A positive bias makes oversteer rules easier to trigger;
    /// a negative bias raises the effective threshold.
    /// Clamped to [−1, 1] on use.
    /// </summary>
    public float BaselineOversteerBias { get; set; }

    // ── Tyre temperature ──────────────────────────────────────────────────────

    /// <summary>
    /// Optimal tyre-temperature operating window for this car/track combination.
    /// Used to contextualise the tyre-temperature proposals from the rule engine.
    /// </summary>
    public TyreTempRange OptimalTyreTempRange { get; set; } = new();

    // ── Proposal weights ──────────────────────────────────────────────────────

    /// <summary>
    /// Per-proposal weight multipliers, indexed by the canonical "Section:Parameter"
    /// key (e.g. <c>"ARB:FRONT"</c>, <c>"TYRES:PRESSURE_LF"</c>).
    /// Values ≥ 1.0 boost a proposal's effective confidence; values in (0, 1) reduce
    /// it. A missing key is treated as weight 1.0.
    /// </summary>
    public Dictionary<string, float> PreferredProposalWeights { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // ── Metadata ──────────────────────────────────────────────────────────────

    /// <summary>UTC timestamp of the most recent save of this profile.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Canonical storage key — "CarModel|TrackName" (lower-invariant).
    /// Used as the JSON file name by <see cref="ProfileStore"/>.
    /// </summary>
    public string Key =>
        $"{CarModel.ToLowerInvariant()}|{TrackName.ToLowerInvariant()}";

    /// <summary>
    /// Returns the weight multiplier for <paramref name="proposalKey"/>.
    /// <paramref name="proposalKey"/> must be in "Section:Parameter" format.
    /// Returns 1.0 when no override is stored.
    /// </summary>
    /// <param name="proposalKey">Key in "Section:Parameter" format.</param>
    public float GetWeight(string proposalKey)
        => PreferredProposalWeights.TryGetValue(proposalKey, out var w) ? w : 1.0f;

    /// <summary>
    /// Stores or updates a weight for <paramref name="proposalKey"/>.
    /// <paramref name="proposalKey"/> must be in "Section:Parameter" format.
    /// The value is clamped to [0.01, 10.0].
    /// </summary>
    public void SetWeight(string proposalKey, float weight)
    {
        if (string.IsNullOrWhiteSpace(proposalKey))
            throw new ArgumentException("Proposal key must not be empty.", nameof(proposalKey));
        PreferredProposalWeights[proposalKey] = Math.Clamp(weight, 0.01f, 10.0f);
    }
}
