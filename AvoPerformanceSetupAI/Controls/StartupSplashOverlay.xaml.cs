using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AvoPerformanceSetupAI.Services;
using AvoPerformanceSetupAI.UI.Progress;

namespace AvoPerformanceSetupAI.Controls;

public sealed partial class StartupSplashOverlay : UserControl
{
    private const int MinVisibleMs = 2500;
    private DateTime _shownAt = DateTime.MinValue;
    private InitProgress? _progress;

    // ── IsOpen ───────────────────────────────────────────────────────────────

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(
            nameof(IsOpen), typeof(bool), typeof(StartupSplashOverlay),
            new PropertyMetadata(true, OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (StartupSplashOverlay)d;
        bool isOpen = (bool)e.NewValue;
        ctrl.RootOverlay.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        if (isOpen)
        {
            ctrl.RootOverlay.Opacity = 1.0;
            ctrl._shownAt = DateTime.UtcNow;
            ctrl.ContentEntranceStoryboard.Begin();
        }
    }

    // ── StatusText ────────────────────────────────────────────────────────────

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText), typeof(string), typeof(StartupSplashOverlay),
            new PropertyMetadata("Inicializando módulos...", OnStatusTextChanged));

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    private static void OnStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (StartupSplashOverlay)d;
        // When Progress is active, Progress.Detail drives StatusTextBlock; ignore StatusText.
        if (ctrl._progress is null)
            ctrl.StatusTextBlock.Text = (string)(e.NewValue ?? string.Empty);
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress), typeof(InitProgress), typeof(StartupSplashOverlay),
            new PropertyMetadata(null, OnProgressChanged));

    public InitProgress? Progress
    {
        get => (InitProgress?)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (StartupSplashOverlay)d;

        if (e.OldValue is InitProgress old)
            old.PropertyChanged -= ctrl.OnProgressPropertyChanged;

        ctrl._progress = e.NewValue as InitProgress;

        if (ctrl._progress is not null)
        {
            ctrl._progress.PropertyChanged += ctrl.OnProgressPropertyChanged;
            ctrl.ChecklistPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ctrl.ChecklistPanel.Visibility = Visibility.Collapsed;
        }

        ctrl.ApplyProgress();
    }

    private void OnProgressPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
            ApplyProgress();
        else
            DispatcherQueue.TryEnqueue(ApplyProgress);
    }

    private void ApplyProgress()
    {
        if (_progress is not { } p)
        {
            // No progress object — show spinner and legacy StatusText
            Ring.Visibility            = Visibility.Visible;
            BusyProgressBar.Visibility = Visibility.Collapsed;
            TitleTextBlock.Visibility  = Visibility.Collapsed;
            return;
        }

        TitleTextBlock.Visibility  = Visibility.Visible;
        TitleTextBlock.Text        = p.Title;
        StatusTextBlock.Text       = p.Detail;
        BusyProgressBar.Value      = p.Percent;

        if (p.IsIndeterminate)
        {
            Ring.Visibility            = Visibility.Visible;
            BusyProgressBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            Ring.Visibility            = Visibility.Collapsed;
            BusyProgressBar.Visibility = Visibility.Visible;
        }

        // Update checklist rows: full opacity = done, dimmed = pending.
        CheckRow1.Opacity = p.StepUiDone       ? 1.0 : 0.3;
        CheckRow2.Opacity = p.StepSettingsDone  ? 1.0 : 0.3;
        CheckRow3.Opacity = p.StepModelsDone    ? 1.0 : 0.3;
        CheckRow4.Opacity = p.StepTelemetryDone ? 1.0 : 0.3;
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public StartupSplashOverlay()
    {
        this.InitializeComponent();
        StatusTextBlock.Text = StatusText;
        // Default IsOpen=true: DependencyProperty callbacks don't fire for the
        // initial default value, so we track _shownAt here for the common case.
        // OnIsOpenChanged handles subsequent explicit IsOpen=true assignments.
        _shownAt = DateTime.UtcNow;
        Loaded += (_, _) => ContentEntranceStoryboard.Begin();
    }

    // ── ShowAsync ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns <paramref name="progress"/>, opens the overlay and starts the
    /// entrance animation.  The <see cref="IsOpen"/> setter records the
    /// <c>_shownAt</c> timestamp used by <see cref="CloseAsync"/>.
    /// </summary>
    public Task ShowAsync(InitProgress progress)
    {
        Progress = progress;
        IsOpen   = true;
        return Task.CompletedTask;
    }

    // ── CloseAsync ───────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the overlay has been visible for at least <see cref="MinVisibleMs"/> ms,
    /// then fades it out over 600 ms and collapses it.
    /// Safe to call from the UI thread.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_shownAt != DateTime.MinValue)
        {
            var elapsed   = DateTime.UtcNow - _shownAt;
            var remaining = TimeSpan.FromMilliseconds(MinVisibleMs) - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);
        }

        var tcs = new TaskCompletionSource<bool>();

        void Completed(object? s, object e)
        {
            tcs.TrySetResult(true);
        }

        FadeOutStoryboard.Completed += Completed;
        FadeOutStoryboard.Begin();
        await tcs.Task;
        FadeOutStoryboard.Completed -= Completed;

        IsOpen = false;
    }

    // ── CloseWhenReadyAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Awaits <paramref name="initTask"/>, then ensures the minimum visible
    /// duration before fading out and collapsing the overlay.
    /// Errors from <paramref name="initTask"/> are swallowed so the overlay
    /// always closes even if initialisation fails.
    /// </summary>
    public async Task CloseWhenReadyAsync(Func<Task> initTask)
    {
        try
        {
            await initTask();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Info($"Splash init error: {ex.Message}");
        }

        await CloseAsync();
    }
}
