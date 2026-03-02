using System;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AvoPerformanceSetupAI.Converters;
using AvoPerformanceSetupAI.ViewModels;

namespace AvoPerformanceSetupAI.Views;

// ── Page ─────────────────────────────────────────────────────────────────────

public sealed partial class TerminalPage : Page
{
    public TerminalViewModel ViewModel { get; } = new TerminalViewModel();

    public TerminalPage()
    {
        this.InitializeComponent();

        // Register converters as page resources so the DataTemplate can find them
        Resources["CategoryToBadgeBrushConverter"] = new CategoryToBadgeBrushConverter();
        Resources["CategoryToTextBrushConverter"]  = new CategoryToTextBrushConverter();

        // FilterPillStyle now declared in App.xaml resources.

        // React to collection changes for auto-scroll and counter
        ViewModel.FilteredEntries.CollectionChanged += OnFilteredEntriesChanged;
        UpdateEntryCount();
    }

    // ── Filter button clicks ─────────────────────────────────────────────────

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            ViewModel.SetFilterCommand.Execute(tag);
            UpdateStatusBar(tag);
        }
    }

    private void UpdateStatusBar(string filter)
    {
        StatusBarText.Text = string.IsNullOrEmpty(filter)
            ? "● En línea  |  Mostrando todos los eventos"
            : $"● En línea  |  Filtro activo: {filter}";
        UpdateEntryCount();
    }

    // ── Auto-scroll + counter ────────────────────────────────────────────────

    private void OnFilteredEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEntryCount();

        // Auto-scroll to bottom when new items are added
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // Defer to let the layout pass complete before scrolling
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null));
        }
    }

    private void UpdateEntryCount()
    {
        int count = ViewModel.FilteredEntries.Count;
        EntryCountText.Text = $"{count} entrada{(count == 1 ? "" : "s")}";
    }
}

