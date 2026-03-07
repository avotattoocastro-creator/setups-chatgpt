using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AvoPerformanceSetupAI.Models;

namespace AvoPerformanceSetupAI.Services;

public class ReferenceLapLoader
{
    private const string RefFolder = "AvoPerformanceSetupAI\\ReferenceLaps";

    private static string GetFolder(string car, string track) => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), RefFolder, car ?? string.Empty, track ?? string.Empty);

    public async Task<IReadOnlyList<ReferenceLapPoint>> LoadCsvAsync(string car, string track, string fileName)
    {
        var path = Path.Combine(GetFolder(car, track), fileName);
        if (!File.Exists(path)) return new List<ReferenceLapPoint>();
        var lines = await File.ReadAllLinesAsync(path);
        return lines.Select(l => ReferenceLapPoint.TryParseCsv(l, out var p) ? p : null)
                    .Where(p => p != null)
                    .Select(p => p!)
                    .ToList();
    }

    public Task SaveReferenceAsync(string car, string track, string fileName, IEnumerable<ReferenceLapPoint> points)
    {
        var folder = GetFolder(car, track);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        var lines = points.Select(p => string.Join(',', p.Time, p.LapDistPct, p.Speed, p.Throttle, p.Brake));
        return File.WriteAllLinesAsync(path, lines);
    }
}
