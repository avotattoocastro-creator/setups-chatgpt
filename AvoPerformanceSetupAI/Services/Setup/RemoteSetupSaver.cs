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
        // SaveSetupAsync already throws AgentException on HTTP errors and on success=false.
        var result = await _client.SaveSetupAsync(car, track, fileName, setupText, overwrite: true);
        return result.Path;
    }
}
