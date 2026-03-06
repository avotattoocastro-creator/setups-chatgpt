using Microsoft.UI.Xaml;

namespace AvoPerformanceSetupAI.Models;

/// <summary>
/// Represents a setup entry with its current value and an AI-proposed change.
/// Used to display side-by-side comparisons in the UI.
/// </summary>
public sealed class SetupEntryProposal
{
    /// <summary>INI section name, e.g. "SUSPENSION_FRONT".</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Parameter key name, e.g. "SPRING_RATE".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Current value in the loaded setup file.</summary>
    public string Current { get; set; } = string.Empty;

    /// <summary>AI-proposed value (same as Current when no change is proposed).</summary>
    public string Proposed { get; set; } = string.Empty;

    /// <summary>Signed adjustment step, e.g. "+1", "-0.05". Empty when no change.</summary>
    public string Delta { get; set; } = string.Empty;

    /// <summary>True when the AI has proposed a different value for this entry.</summary>
    public bool HasChange { get; set; }

    /// <summary>Visibility for UI elements that should only show when there's a change.</summary>
    public Visibility ChangeVisibility => HasChange ? Visibility.Visible : Visibility.Collapsed;
}
