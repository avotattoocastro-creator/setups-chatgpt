using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

public class BaselineStorage
{
    private const string RootFolderName = "AvoPerformanceSetupAI\\Baselines";

    private static string GetBasePath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), RootFolderName);

    private static string GetTrackPath(string car, string track) => Path.Combine(GetBasePath(), car ?? "", track ?? "");

    public async Task<IReadOnlyList<BaselineEntry>> LoadAllAsync(string? carFilter = null, string? trackFilter = null)
    {
        var results = new List<BaselineEntry>();
        var root = GetBasePath();
        if (!Directory.Exists(root)) return results;

        foreach (var carDir in Directory.GetDirectories(root))
        {
            if (!string.IsNullOrWhiteSpace(carFilter) && !carDir.EndsWith(carFilter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var trackDir in Directory.GetDirectories(carDir))
            {
                if (!string.IsNullOrWhiteSpace(trackFilter) && !trackDir.EndsWith(trackFilter, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var file in Directory.GetFiles(trackDir, "baseline_*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var entry = JsonSerializer.Deserialize<BaselineEntry>(json);
                        if (entry != null)
                        {
                            results.Add(entry);
                        }
                    }
                    catch
                    {
                        // ignore malformed entries
                    }
                }
            }
        }
        return results;
    }

    public async Task SaveAsync(BaselineEntry entry)
    {
        var folder = GetTrackPath(entry.Car, entry.Track);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"baseline_{entry.Name.Replace(' ', '_')}.json");
        entry.LastModified = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(file, json);
    }

    public Task DeleteAsync(BaselineEntry entry)
    {
        var folder = GetTrackPath(entry.Car, entry.Track);
        if (!Directory.Exists(folder)) return Task.CompletedTask;
        foreach (var file in Directory.GetFiles(folder, "baseline_*.json"))
        {
            if (Path.GetFileNameWithoutExtension(file).Contains(entry.Name.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    public async Task ExportAsync(BaselineEntry entry, string targetPath)
    {
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(targetPath, json);
    }

    public async Task<BaselineEntry?> ImportAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return null;
        var json = await File.ReadAllTextAsync(sourcePath);
        var entry = JsonSerializer.Deserialize<BaselineEntry>(json);
        if (entry != null)
        {
            await SaveAsync(entry);
        }
        return entry;
    }
}
