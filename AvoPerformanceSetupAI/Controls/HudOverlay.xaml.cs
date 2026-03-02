using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AvoPerformanceSetupAI.Controls;

/// <summary>
/// Semi-transparent HUD quick-info overlay shown when the user presses TAB on
/// the Telemetry page.  Displays 5 critical lines (status, delta, speed, top
/// proposal, phase) and auto-hides after <see cref="AutoHideMs"/> milliseconds.
/// Re-pressing TAB while visible resets the 2-second countdown.
/// </summary>
public sealed partial class HudOverlay : UserControl
{
    private const int AutoHideMs = 2000;

    /// <summary>
    /// Tracks the current auto-hide cycle.  Cancelling it prevents the fade-out
    /// storyboard from running so the next <see cref="ShowAsync"/> call owns it.
    /// </summary>
    private CancellationTokenSource? _hideCts;

    public HudOverlay()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// Populates the 5 content lines, fades the overlay in, waits
    /// <see cref="AutoHideMs"/> ms, then fades it out and collapses it.
    /// If called while the overlay is already visible the auto-hide timer is
    /// reset (the previous cycle is cancelled and a fresh one begins).
    /// Safe to fire-and-forget from the UI thread.
    /// </summary>
    public async Task ShowAsync(
        string line1,
        string line2,
        string line3,
        string line4,
        string line5)
    {
        // Cancel any in-flight auto-hide so this call takes ownership.
        _hideCts?.Cancel();
        _hideCts = new CancellationTokenSource();
        var ct = _hideCts.Token;

        // Update content while still invisible (or mid-fade) to avoid flicker.
        Line1Block.Text = line1;
        Line2Block.Text = line2;
        Line3Block.Text = line3;
        Line4Block.Text = line4;
        Line5Block.Text = line5;

        this.Visibility = Visibility.Visible;
        FadeOutStoryboard.Stop();   // stop any in-progress fade-out before re-appearing
        FadeInStoryboard.Begin();

        try
        {
            await Task.Delay(AutoHideMs, ct);
        }
        catch (OperationCanceledException)
        {
            // A fresh ShowAsync call cancelled this cycle; it will handle fade-out.
            return;
        }

        // Animate out and then collapse.
        var tcs = new TaskCompletionSource<bool>();
        void OnFadeOutCompleted(object? s, object e) => tcs.TrySetResult(true);
        FadeOutStoryboard.Completed += OnFadeOutCompleted;
        FadeOutStoryboard.Begin();
        await tcs.Task;
        FadeOutStoryboard.Completed -= OnFadeOutCompleted;

        if (!ct.IsCancellationRequested)
            this.Visibility = Visibility.Collapsed;
    }
}
