using System.IO;
using System.Linq;
using System.Net;
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

        ProposalSaveResult MakeFail(string reason) => ProposalSaveResult.Failed(reason);

        static string WithConflictSuffix(string name)
        {
            var ext  = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            return string.IsNullOrEmpty(ext)
                ? $"{stem}_conflict_{ts}"
                : $"{stem}_conflict_{ts}{ext}";
        }

        async Task<(bool ok, string? path, string reason, HttpStatusCode? status)> TrySaveAsync(
            string fileName, bool overwrite, bool versioned, string tag)
        {
            try
            {
                var result = await _client.SaveSetupAsync(
                    req.Car, req.Track, fileName, req.Content, overwrite: overwrite, versioned: versioned);

                if (result.Success)
                {
                    var resolvedPath = !string.IsNullOrWhiteSpace(result.Path)
                        ? result.Path
                        : (!string.IsNullOrWhiteSpace(result.SavedFileName) ? result.SavedFileName : fileName);
                    return (true, resolvedPath, string.Empty, null);
                }

                var reason = string.IsNullOrWhiteSpace(result.Error)
                    ? $"{tag}: Agent devolvió success=false sin mensaje"
                    : result.Error;
                AppLogger.Instance.Warn($"REMOTE SAVE attempt '{tag}' failed: {reason}");
                return (false, string.Empty, reason, null);
            }
            catch (AgentException ex)
            {
                AppLogger.Instance.Warn($"REMOTE SAVE attempt '{tag}' threw AgentException: {ex.Message}");
                return (false, string.Empty, ex.Message, ex.HttpStatus);
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warn($"REMOTE SAVE attempt '{tag}' threw: {ex.Message}");
                return (false, string.Empty, ex.Message, null);
            }
        }

        static ProposalSaveResult ToSuccess((bool ok, string? path, string reason, HttpStatusCode? status) attempt, string requested)
        {
            var file = string.IsNullOrWhiteSpace(attempt.path) ? requested : attempt.path!;
            return ProposalSaveResult.Success(file);
        }

        // 1) Try versioned save without overwrite (preferred path)
        var attempt1 = await TrySaveAsync(req.FileName, overwrite: false, versioned: true, tag: "versioned/no-overwrite");
        if (attempt1.ok)
            return ToSuccess(attempt1, req.FileName);

        // 2) Plain overwrite without versioning (some agents require this when they auto-version server-side)
        var attempt2 = await TrySaveAsync(req.FileName, overwrite: true, versioned: false, tag: "plain/overwrite");
        if (attempt2.ok)
            return ToSuccess(attempt2, req.FileName);

        // 3) Versioned overwrite
        var attempt3 = await TrySaveAsync(req.FileName, overwrite: true, versioned: true, tag: "versioned/overwrite");
        if (attempt3.ok)
            return ToSuccess(attempt3, req.FileName);

        // 4) If conflicts, try with a conflict suffix and overwrite/versioned
        if (attempt1.status == HttpStatusCode.Conflict || attempt2.status == HttpStatusCode.Conflict || attempt3.status == HttpStatusCode.Conflict)
        {
            var conflictName = WithConflictSuffix(req.FileName);
            var attemptConflict = await TrySaveAsync(conflictName, overwrite: true, versioned: true, tag: "conflict-renamed");
            if (attemptConflict.ok)
                return ProposalSaveResult.Success(attemptConflict.path ?? string.Empty);
            attempt3 = (attempt3.ok, attempt3.path, string.Join(" | ", new[] { attempt3.reason, attemptConflict.reason }.Where(r => !string.IsNullOrWhiteSpace(r))), attempt3.status);
        }

        // Pick the first non-empty reason to avoid overly long messages.
        var reason = new[] { attempt1.reason, attempt2.reason, attempt3.reason }
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
            ?? "El Agent rechazó el guardado en todos los intentos.";
        return MakeFail(reason);
    }
}
