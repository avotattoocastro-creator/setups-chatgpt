using CommunityToolkit.Mvvm.ComponentModel;

namespace AvoPerformanceSetupAI.UI.Progress;

/// <summary>
/// Observable progress model used by the startup splash and the global busy overlay.
/// Property changes are raised on whatever thread the property is set from;
/// <see cref="Services.Initialization.InitOrchestrator"/> always updates these via
/// <c>DispatcherQueue.TryEnqueue</c> so all changes arrive on the UI thread.
/// </summary>
public sealed partial class InitProgress : ObservableObject
{
    /// <summary>Completion percentage (0..100).</summary>
    [ObservableProperty] private double _percent;

    /// <summary>Primary headline shown in large bold text.</summary>
    [ObservableProperty] private string _title = string.Empty;

    /// <summary>Secondary detail line shown below the title.</summary>
    [ObservableProperty] private string _detail = string.Empty;

    /// <summary>
    /// When <see langword="true"/> the spinner ring is shown instead of
    /// the percentage progress bar.
    /// </summary>
    [ObservableProperty] private bool _isIndeterminate = true;

    // ── Step completion flags ─────────────────────────────────────────────────

    /// <summary>Interface warmup + settings load complete.</summary>
    [ObservableProperty] private bool _stepUiDone;

    /// <summary>Folder preparation + profile data loaded.</summary>
    [ObservableProperty] private bool _stepSettingsDone;

    /// <summary>ML / RL model warmup complete.</summary>
    [ObservableProperty] private bool _stepModelsDone;

    /// <summary>Telemetry subsystem ready.</summary>
    [ObservableProperty] private bool _stepTelemetryDone;
}
