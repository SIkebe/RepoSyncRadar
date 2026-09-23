using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

internal interface IStructuredDraftCopilotSession
{
    bool SupportsStructuredDrafts { get; }

    Task<DraftBundle> SendStructuredDraftAsync(
        string prompt,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
