using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AvoPerformanceSetupAI.Models;

/// <summary>
/// Represents one telemetry channel with a real (current) value and an ideal (target) value.
/// Both are observable so the UI refreshes automatically when the simulation updates them.
/// </summary>
public partial class TelemetryChannel : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    [ObservableProperty]
    private double _realValue;

    [ObservableProperty]
    private double _idealValue;

    // ── Computed display strings ──────────────────────────────────────────────

    public string RealDisplay  => Format(_realValue);
    public string IdealDisplay => Format(_idealValue);

    public string DeltaDisplay
    {
        get
        {
            var d = _realValue - _idealValue;
            return d >= 0 ? $"+{Format(d)}" : Format(d);
        }
    }

    private static string Format(double v) =>
        Math.Abs(v) >= 1000 ? v.ToString("F0") : v.ToString("F1");

    // ── Propagate change notifications to computed properties ─────────────────

    partial void OnRealValueChanged(double value)
    {
        _ = value; // new value already stored in _realValue; notify computed dependents
        OnPropertyChanged(nameof(RealDisplay));
        OnPropertyChanged(nameof(DeltaDisplay));
    }

    partial void OnIdealValueChanged(double value)
    {
        _ = value;
        OnPropertyChanged(nameof(IdealDisplay));
        OnPropertyChanged(nameof(DeltaDisplay));
    }
}
