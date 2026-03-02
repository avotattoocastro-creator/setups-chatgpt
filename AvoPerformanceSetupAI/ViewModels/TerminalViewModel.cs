using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Services;

namespace AvoPerformanceSetupAI.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private readonly AppLogger _logger = AppLogger.Instance;

    /// <summary>The category filter currently selected. Empty string = show all.</summary>
    [ObservableProperty]
    private string _activeFilter = string.Empty;

    /// <summary>Filtered log entries displayed in the terminal.</summary>
    public ObservableCollection<LogEntry> FilteredEntries { get; } = new();

    public TerminalViewModel()
    {
        // Seed filtered list with whatever is already logged
        RebuildFilter();

        // React to new entries
        _logger.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (LogEntry entry in e.NewItems)
            {
                if (MatchesFilter(entry))
                    FilteredEntries.Add(entry);
            }
        }
        else
        {
            RebuildFilter();
        }
    }

    private bool MatchesFilter(LogEntry entry) =>
        string.IsNullOrEmpty(ActiveFilter) ||
        entry.Category.ToString().Equals(ActiveFilter, StringComparison.OrdinalIgnoreCase);

    private void RebuildFilter()
    {
        FilteredEntries.Clear();
        foreach (var entry in _logger.Entries)
        {
            if (MatchesFilter(entry))
                FilteredEntries.Add(entry);
        }
    }

    [RelayCommand]
    private void SetFilter(string filter)
    {
        ActiveFilter = filter;
        RebuildFilter();
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logger.Entries.Clear();
        FilteredEntries.Clear();
    }
}
