namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Abstracts the single central save path for AI setup proposals.
/// Implementations must never write local <c>iter*.ini</c> files when
/// <see cref="ProposalSaveRequest.IsRemote"/> is <see langword="true"/>.
/// </summary>
public interface IProposalSaver
{
    /// <summary>
    /// Saves the proposal and returns a <see cref="ProposalSaveResult"/> describing the outcome.
    /// Implementations must NOT throw; all errors are surfaced via
    /// <see cref="ProposalSaveResult.Failed"/>.
    /// </summary>
    Task<ProposalSaveResult> SaveAsync(ProposalSaveRequest req);
}
