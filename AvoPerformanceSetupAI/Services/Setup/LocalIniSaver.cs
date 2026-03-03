using AvoPerformanceSetupAI.Services;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Writes the versioned setup file to the local Assetto Corsa setups folder.
/// </summary>
/// <remarks>
/// When <see cref="ProposalSaveRequest.IsRemote"/> is <see langword="true"/> the write is
/// unconditionally rejected so that no ghost <c>iter_v*.ini</c> files are ever created on
/// the client machine while an Agent is active.
/// </remarks>
public sealed class LocalIniSaver : IProposalSaver
{
    /// <inheritdoc/>
    public async Task<ProposalSaveResult> SaveAsync(ProposalSaveRequest req)
    {
        // Hard guard — local writes are forbidden whenever a remote Agent is active.
        if (req.IsRemote)
        {
            const string remoteBlockReason = "REMOTE mode: local iter write blocked";
            AppLogger.Instance.Warn(remoteBlockReason);
            return ProposalSaveResult.Failed(remoteBlockReason);
        }

        var rootFolder = SetupSettings.Instance.RootFolder;
        if (string.IsNullOrEmpty(rootFolder))
            return ProposalSaveResult.Failed(
                "Carpeta raíz de setups no configurada. Ve a Configuración y selecciona la carpeta raíz.");

        var destFolder = Path.Combine(rootFolder, req.Car, req.Track);
        Directory.CreateDirectory(destFolder);
        var destPath = Path.Combine(destFolder, req.FileName);
        await File.WriteAllTextAsync(destPath, req.Content);
        AppLogger.Instance.Ai("Propuesta de IA aplicada al archivo de setup (local).");
        return ProposalSaveResult.Success(destPath);
    }
}
