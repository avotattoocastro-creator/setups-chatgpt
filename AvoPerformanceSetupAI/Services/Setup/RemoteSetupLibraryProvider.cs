using AvoPerformanceSetupAI.Services.Agent;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Setup library provider backed by the remote AVO Performance Agent.
/// All operations call the Agent HTTP API.
/// </summary>
public sealed class RemoteSetupLibraryProvider : ISetupLibraryProvider
{
    private readonly AgentApiClient _client;

    public RemoteSetupLibraryProvider(AgentApiClient client)
        => _client = client;

    // ── Root selection ────────────────────────────────────────────────────────

    /// <summary>
    /// Tells the agent to open a folder picker on the simulator PC.
    /// Returns the path chosen by the user, or null if cancelled / error.
    /// </summary>
    public async Task<string?> SelectRootAsync()
    {
        try
        {
            var resp = await _client.BrowseReferenceRootAsync();
            if (string.IsNullOrWhiteSpace(resp.Path)) return null;
            // Persist to settings so the UI textbox updates
            SetupSettings.Instance.RootFolder = resp.Path;
            return resp.Path;
        }
        catch (AgentException ex)
        {
            AppLogger.Instance.Error($"Error al abrir selector remoto: {ex.Message}");
            return null;
        }
    }

    // ── Library queries ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetCarsAsync()
    {
        var list = await _client.GetCarsAsync();
        return list.AsReadOnly();
    }

    public async Task<IReadOnlyList<string>> GetTracksAsync(string car)
    {
        var list = await _client.GetTracksAsync(car);
        return list.AsReadOnly();
    }

    public async Task<IReadOnlyList<SetupItem>> GetSetupsAsync(string car, string track)
    {
        var list = await _client.GetSetupsAsync(car, track);
        return list.AsReadOnly();
    }

    public async Task<string> ReadSetupTextAsync(string car, string track, string file)
        => await _client.ReadSetupAsync(car, track, file);
}
