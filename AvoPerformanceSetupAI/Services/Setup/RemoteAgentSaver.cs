using AvoPerformanceSetupAI.Services.Agent;

namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Saves setup proposals on the simulator PC via the remote Agent REST API
/// (<c>POST /api/setup/save</c>).
/// </summary>
/// <remarks>
/// All errors are returned as <see cref="ProposalSaveResult.Failed"/> — the caller
/// must NOT fall back to local disk writes on failure.
/// </remarks>
public sealed class RemoteAgentSaver : IProposalSaver
{
    private readonly AgentApiClient _client;

    public RemoteAgentSaver(AgentApiClient client) => _client = client;

    /// <inheritdoc/>
    public async Task<ProposalSaveResult> SaveAsync(ProposalSaveRequest req)
    {
        AppLogger.Instance.Info(
            $"REMOTE SAVE: car={req.Car}  track={req.Track}  file={req.FileName}  " +
            $"size={System.Text.Encoding.UTF8.GetByteCount(req.Content)} bytes");

        try
        {
            var result = await _client.SaveSetupAsync(
                req.Car, req.Track, req.FileName, req.Content, overwrite: true);
            return ProposalSaveResult.Success(result.Path);
        }
        catch (AgentException ex)
        {
            return ProposalSaveResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            return ProposalSaveResult.Failed($"Agent no accesible: {ex.Message}");
        }
    }
}
