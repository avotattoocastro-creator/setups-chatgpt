using AvoPerformanceSetupAI.Services.Agent;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Saves generated setup files on the simulator PC via the remote Agent.
/// Computes the next sequential file name (<c>{stem}_AI_{n:D3}.ini</c>) by
/// querying <c>GET /api/setups/versions</c> before each save.
/// </summary>
public sealed class RemoteSetupSaver : ISetupSaver
{
    private readonly AgentApiClient _client;

    public RemoteSetupSaver(AgentApiClient client)
        => _client = client;

    public async Task<string> SaveAsync(string car, string track, string fileName, string setupText)
    {
        var ext      = Path.GetExtension(fileName);
        var stem     = Path.GetFileNameWithoutExtension(fileName);
        var aiPrefix = $"{stem}_AI_";

        // Query the Agent for existing files so we can pick the next sequential number.
        int maxVersion = 0;
        try
        {
            var versionsResp = await _client.GetVersionsAsync(car, track, stem);
            foreach (var f in versionsResp.Files)
            {
                var fStem = Path.GetFileNameWithoutExtension(f);
                if (!fStem.StartsWith(aiPrefix, StringComparison.Ordinal)) continue;
                // Pattern: {stem}_AI_NNN  (e.g. "baseline_AI_001")
                var numStr = fStem[aiPrefix.Length..];
                if (int.TryParse(numStr, out int n) && n > maxVersion)
                    maxVersion = n;
            }
        }
        catch
        {
            // If GetVersionsAsync fails, start version numbering from 001.
        }

        var sequencedName = $"{stem}_AI_{(maxVersion + 1):D3}{ext}";

        // When saving versioned files, let the Agent reject duplicates instead of forcing overwrite.
        var result = await _client.SaveSetupAsync(car, track, sequencedName, setupText,
            overwrite: false, versioned: true);

        if (!result.Success)
        {
            var reason = string.IsNullOrWhiteSpace(result.Error)
                ? "El Agent rechazó el guardado."
                : result.Error;
            throw new AgentException(reason);
        }

        return result.Path;
    }
}
