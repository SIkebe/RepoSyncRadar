namespace RepoSyncRadar.Core.Services;

public interface ITriagePreflightSummaryBuilder
{
    Task<TriagePreflightSummary> BuildAsync(
        bool includeGitHubEstimate,
        CancellationToken cancellationToken = default);
}
