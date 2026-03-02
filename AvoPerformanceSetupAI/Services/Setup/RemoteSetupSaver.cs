using AvoPerformanceSetupAI.Services.Agent;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Saves generated setup files on the simulator PC via the remote Agent.
/// </summary>
public sealed class RemoteSetupSaver : ISetupSaver
{
    private readonly AgentApiClient _client;

    public RemoteSetupSaver(AgentApiClient client)
        => _client = client;

    public async Task<string> SaveAsync(string car, string track, string fileName, string setupText)
    {
        var result = await _client.SaveSetupAsync(car, track, fileName, setupText, overwrite: true);
        if (!result.Success)
            throw new AgentException(
                string.IsNullOrEmpty(result.Error) ? "Error al guardar setup en el Agent." : result.Error);
        return result.Path;
    }
}
