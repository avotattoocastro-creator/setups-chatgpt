namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>Writes a generated setup file to either the local disk or the remote Agent.</summary>
public interface ISetupSaver
{
    /// <summary>
    /// Saves the setup text and returns a human-readable path/result message.
    /// Throws on failure.
    /// </summary>
    Task<string> SaveAsync(string car, string track, string fileName, string setupText);
}
