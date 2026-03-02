using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Application-wide logger singleton.
/// All entries are appended on the UI thread so the ObservableCollection
/// can be bound directly in XAML without extra marshalling in consumers.
/// </summary>
public sealed class AppLogger
{
    public static AppLogger Instance { get; } = new AppLogger();

    /// <summary>Full, unfiltered log — bind to this in XAML.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    private DispatcherQueue? _dispatcher;

    private AppLogger() { }

    /// <summary>
    /// Must be called once from the UI thread (e.g. MainWindow constructor)
    /// so the logger can marshal entries onto the UI thread.
    /// </summary>
    public void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Log(LogCategory category, string message)
    {
        var entry = new LogEntry { Category = category, Message = message };

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            Entries.Add(entry);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Entries.Add(entry));
        }
    }

    public void Info(string message)  => Log(LogCategory.INFO,  message);
    public void Ai(string message)    => Log(LogCategory.AI,    message);
    public void Data(string message)  => Log(LogCategory.DATA,  message);
    public void Warn(string message)  => Log(LogCategory.WARN,  message);
    public void Error(string message) => Log(LogCategory.ERROR, message);
}
