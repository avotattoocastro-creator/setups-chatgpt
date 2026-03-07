namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Outcome of a save operation initiated via <see cref="IProposalSaver"/>.
/// </summary>
/// <param name="Ok">
/// <see langword="true"/> when the file was persisted successfully.
/// </param>
/// <param name="File">
/// Absolute path (local) or remote reference returned by the Agent.
/// Empty string when <paramref name="File"/> is not applicable (failure case).
/// </param>
/// <param name="Reason">
/// Human-readable failure message, or <see langword="null"/> when <paramref name="Reason"/>
/// is not applicable (success case).
/// </param>
public sealed record ProposalSaveResult(bool Ok, string File, string? Reason)
{
    /// <summary>Creates a successful result containing the saved file path.</summary>
    public static ProposalSaveResult Success(string file)  => new(true,  file,          null);

    /// <summary>Creates a failed result containing the error description.</summary>
    public static ProposalSaveResult Failed(string reason) => new(false, string.Empty, reason);
}
