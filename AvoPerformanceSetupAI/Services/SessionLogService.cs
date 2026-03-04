using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AvoPerformanceSetupAI.Models;
using AvoPerformanceSetupAI.Telemetry;

namespace AvoPerformanceSetupAI.Services;

/// <summary>
/// Persists recent AC session logs (by car/track/setup) and exposes them for UI binding.
/// </summary>
public sealed class SessionLogService
{
    public static SessionLogService Instance { get; } = new();

    public ObservableCollection<SessionLogEntry> Entries { get; } = new();

    private readonly object _lock = new();
    private readonly string _filePath;

    // Runtime state per key to detect lap completions
    private readonly Dictionary<string, (int LastLapCount, int BestMs)> _state = new();

    private SessionLogService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvoPerformanceSetupAI");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "session-log.json");
        _ = LoadAsync();
    }

    public void ProcessSample(in TelemetrySample sample, string carId, string trackId, string? setupFile)
    {
        if (string.IsNullOrWhiteSpace(carId) || string.IsNullOrWhiteSpace(trackId)) return;
        if (sample.AcStatus != (int)AcStatus.Live) return;

        var key = $"{carId}|{trackId}|{setupFile ?? string.Empty}";

        lock (_lock)
        {
            if (!_state.TryGetValue(key, out var s))
                s = (0, 0);

            var bestMs = sample.BestLapMs > 0 ? sample.BestLapMs : s.BestMs;
            var completed = sample.CompletedLaps;

            // Only log when a new lap is completed or best improves
            var isNewLap = completed > s.LastLapCount;
            var isBestImproved = bestMs > 0 && (s.BestMs == 0 || bestMs < s.BestMs);

            if (!isNewLap && !isBestImproved)
            {
                _state[key] = (completed, bestMs);
                return;
            }

            _state[key] = (completed, bestMs);

            var existing = FindEntry(key);
            if (existing is null)
            {
                Entries.Insert(0, new SessionLogEntry
                {
                    CarId = carId,
                    TrackId = trackId,
                    SetupFile = setupFile ?? string.Empty,
                    CompletedLaps = completed,
                    BestLapMs = bestMs,
                    SessionDateUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.CompletedLaps = Math.Max(existing.CompletedLaps, completed);
                if (bestMs > 0 && (existing.BestLapMs == 0 || bestMs < existing.BestLapMs))
                    existing.BestLapMs = bestMs;
                existing.SessionDateUtc = DateTime.UtcNow;
            }

            _ = SaveAsync();
        }
    }

    private SessionLogEntry? FindEntry(string key)
    {
        foreach (var e in Entries)
        {
            if ($"{e.CarId}|{e.TrackId}|{e.SetupFile}".Equals(key, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            await using var stream = File.OpenRead(_filePath);
            var entries = await JsonSerializer.DeserializeAsync<SessionLogEntry[]>(stream);
            if (entries is null) return;
            Entries.Clear();
            foreach (var e in entries)
                Entries.Add(e);
        }
        catch
        {
            // ignore load errors
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            await using var stream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(stream, Entries, options);
        }
        catch
        {
            // ignore save errors
        }
    }
}
