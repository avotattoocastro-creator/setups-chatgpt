namespace AvoPerformanceSetupAI.Services.Setup;

/// <summary>
/// Input passed to <see cref="IProposalSaver"/> for every setup-proposal save operation.
/// </summary>
/// <param name="Car">Car folder name, e.g. "ks_porsche_911_gt3_r".</param>
/// <param name="Track">Track folder name, e.g. "monza".</param>
/// <param name="FileName">Versioned file name, e.g. "setup_AI_20260302_v001.ini".</param>
/// <param name="Content">Full INI text to persist.</param>
/// <param name="IsRemote">
/// <see langword="true"/> when the save must go to the remote Agent
/// (<c>AgentConnected == true &amp;&amp; AgentBaseUrl != ""</c>).
/// <see langword="false"/> for a plain local-disk write.
/// </param>
public sealed record ProposalSaveRequest(
    string Car,
    string Track,
    string FileName,
    string Content,
    bool   IsRemote
);
