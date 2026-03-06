using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

public class CompareReportExporter
{
    private const string ReportsFolder = "AvoPerformanceSetupAI\\Reports";

    private static string GetFolderPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ReportsFolder);

    public async Task<string> ExportTextAsync(string baselineId, string currentId, string selectedId, CompareImpactModel impact)
    {
        Directory.CreateDirectory(GetFolderPath());
        var file = Path.Combine(GetFolderPath(), $"compare_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
        var content = $"BASELINE: {baselineId}\nCURRENT: {currentId}\nSELECTED: {selectedId}\n" +
                      $"Predicted LapTime: {impact.PredictedLapTime:F3}\n" +
                      $"Balance Score: {impact.BalanceScore:F1}\n" +
                      $"Tyre Heat Risk: {impact.TyreHeatRisk:F1}\n" +
                      $"Stability Risk: {impact.StabilityRisk:F1}\n" +
                      $"Aero Efficiency: {impact.AeroEfficiency:F1}\n";
        await File.WriteAllTextAsync(file, content);
        return file;
    }

    public async Task<string> ExportJsonAsync(CompareImpactModel impact)
    {
        Directory.CreateDirectory(GetFolderPath());
        var file = Path.Combine(GetFolderPath(), $"compare_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        var json = JsonSerializer.Serialize(impact, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(file, json);
        return file;
    }
}
