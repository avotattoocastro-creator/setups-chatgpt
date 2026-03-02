using AvoPerformanceSetupAI.Services.Agent;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Saves generated setup files on the simulator PC via the remote Agent.
/// Automatically computes the next versioned file name by querying
/// <c>GET /api/setups/versions</c> before each save.
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

        // Query the Agent for existing versioned files so we can pick the next v### number.
        int maxVersion = 0;
        try
        {
            var versionsResp = await _client.GetVersionsAsync(car, track, stem);
            foreach (var f in versionsResp.Files)
            {
                var fStem = Path.GetFileNameWithoutExtension(f);
                if (!fStem.StartsWith(aiPrefix, StringComparison.Ordinal)) continue;
                var vIdx = fStem.LastIndexOf("_v", StringComparison.Ordinal);
                if (vIdx < 0) continue;
                var numStr = fStem[(vIdx + 2)..];
                if (int.TryParse(numStr, out int n) && n > maxVersion)
                    maxVersion = n;
            }
        }
        catch
        {
            // If GetVersionsAsync fails, start version numbering from v001.
        }

        var ts            = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var versionedName = $"{stem}_AI_{ts}_v{(maxVersion + 1):D3}{ext}";

        // SaveSetupAsync already throws AgentException on HTTP errors and on success=false.
        var result = await _client.SaveSetupAsync(car, track, versionedName, setupText, overwrite: true);
        return result.Path;
    }
}
