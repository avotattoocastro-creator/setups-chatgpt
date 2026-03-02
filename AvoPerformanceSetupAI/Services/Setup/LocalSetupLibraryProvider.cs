using AvoPerformanceSetupAI.Services.Agent;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Setup library provider that reads from the local file system.
/// Folder selection uses a <see cref="FolderPicker"/> bound to the main window.
/// </summary>
public sealed class LocalSetupLibraryProvider : ISetupLibraryProvider
{
    /// <summary>File extensions recognised as setup files (case-insensitive).</summary>
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ini", ".json" };

    private readonly Window _window;
    private string _rootFolder;

    public LocalSetupLibraryProvider(Window window)
    {
        _window     = window;
        _rootFolder = SetupSettings.Instance.RootFolder;
    }

    // ── Root selection ────────────────────────────────────────────────────────

    public async Task<string?> SelectRootAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return null;

        _rootFolder = folder.Path;
        SetupSettings.Instance.RootFolder = _rootFolder;
        return _rootFolder;
    }

    // ── Library queries ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetCarsAsync()
    {
        if (!Directory.Exists(_rootFolder))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var cars = Directory.GetDirectories(_rootFolder)
                            .Select(Path.GetFileName)
                            .Where(n => n is not null)
                            .Order()
                            .Cast<string>()
                            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(cars);
    }

    public Task<IReadOnlyList<string>> GetTracksAsync(string car)
    {
        var carPath = Path.Combine(_rootFolder, car);
        if (!Directory.Exists(carPath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var tracks = Directory.GetDirectories(carPath)
                              .Select(Path.GetFileName)
                              .Where(n => n is not null)
                              .Order()
                              .Cast<string>()
                              .ToList();
        return Task.FromResult<IReadOnlyList<string>>(tracks);
    }

    public Task<IReadOnlyList<SetupItem>> GetSetupsAsync(string car, string track)
    {
        var trackPath = Path.Combine(_rootFolder, car, track);
        if (!Directory.Exists(trackPath))
            return Task.FromResult<IReadOnlyList<SetupItem>>(Array.Empty<SetupItem>());

        var items = Directory.EnumerateFiles(trackPath)
                             .Where(f =>
                             {
                                 var ext  = Path.GetExtension(f);
                                 var name = Path.GetFileName(f);
                                 // Accept only known setup extensions; skip backups and temp files.
                                 return AllowedExtensions.Contains(ext)
                                     && !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                                     && !name.StartsWith("~",  StringComparison.Ordinal)
                                     && !name.StartsWith(".",  StringComparison.Ordinal);
                             })
                             .Select(f => new SetupItem
                             {
                                 FileName = Path.GetFileName(f)!,
                                 Car      = car,
                                 Track    = track,
                             })
                             .OrderBy(s => s.FileName)
                             .ToList();
        return Task.FromResult<IReadOnlyList<SetupItem>>(items);
    }

    public async Task<string> ReadSetupTextAsync(string car, string track, string file)
    {
        var filePath = Path.Combine(_rootFolder, car, track, file);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Setup no encontrado: {filePath}");

        return await File.ReadAllTextAsync(filePath);
    }
}
