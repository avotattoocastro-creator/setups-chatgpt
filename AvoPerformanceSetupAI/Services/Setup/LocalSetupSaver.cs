namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Writes setup files to the circuit sub-folder inside the root library
/// (<c>RootFolder/car/track/fileName</c>), which is where Assetto Corsa
/// reads them from.
/// </summary>
public sealed class LocalSetupSaver : ISetupSaver
{
    public async Task<string> SaveAsync(string car, string track, string fileName, string setupText)
    {
        var rootFolder = SetupSettings.Instance.RootFolder;
        if (string.IsNullOrEmpty(rootFolder))
            throw new InvalidOperationException(
                "Carpeta raíz de setups no configurada. Ve a Configuración y selecciona la carpeta raíz.");

        var destFolder = Path.Combine(rootFolder, car, track);
        Directory.CreateDirectory(destFolder);
        var destPath = Path.Combine(destFolder, fileName);
        await File.WriteAllTextAsync(destPath, setupText);
        return destPath;
    }
}
