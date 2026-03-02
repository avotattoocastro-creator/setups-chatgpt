using AvoPerformanceSetupAI.Services.Agent;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Abstraction over the setup folder library so the UI does not need to know
/// whether the data comes from the local file system or the remote Agent.
/// </summary>
public interface ISetupLibraryProvider
{
    /// <summary>
    /// Asks the user (or agent) to select a root folder.
    /// Returns the display path string, or <see langword="null"/> if cancelled.
    /// </summary>
    Task<string?> SelectRootAsync();

    Task<IReadOnlyList<string>> GetCarsAsync();
    Task<IReadOnlyList<string>> GetTracksAsync(string car);
    Task<IReadOnlyList<SetupItem>> GetSetupsAsync(string car, string track);
    Task<string> ReadSetupTextAsync(string car, string track, string file);
}
