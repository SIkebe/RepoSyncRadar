using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.Core.Services;

/// <summary>
/// Pulls Repo sync PR commits and per-commit diffs from <c>github/docs</c>.
/// </summary>
/// <remarks>
/// Implementation is wired up in Phase 1 using Octokit.NET. The interface lives here so the
/// Copilot tool layer and the UI can take a stable dependency from Phase 0 onward.
/// </remarks>
public interface IDocsGitHubClient
{
    /// <summary>
    /// Estimates the Repo sync PRs and new commits that the next triage ingestion would consider.
    /// This method is read-only: it does not fetch per-commit file metadata or write to the local DB.
    /// </summary>
    Task<DocsGitHubTriageEstimate> EstimateTriageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the most recent <c>Repo sync</c> PRs that have not yet been mirrored to the local store.
    /// The returned <see cref="Commit"/>s have an empty <see cref="Commit.Files"/> list; callers that
    /// need per-file metadata must call <see cref="GetCommitFilesAsync"/> explicitly.
    /// </summary>
    Task<IReadOnlyList<Commit>> FetchUnseenCommitsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of <see cref="CommitFile"/> entries for a given commit SHA. Loaded lazily
    /// so the unseen-commit listing does not pay the per-commit roundtrip up front.
    /// </summary>
    Task<IReadOnlyList<CommitFile>> GetCommitFilesAsync(string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unified diff for the given commit. The result may be paginated by the GitHub API.
    /// </summary>
    Task<string> GetUnifiedDiffAsync(string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the raw markdown content for a file at the specified git ref.
    /// </summary>
    Task<string> GetFileContentAsync(string path, string gitRef, CancellationToken cancellationToken = default);
}

public sealed record DocsGitHubTriageEstimate(int CandidatePullRequestCount, int NewUnseenCommitCount);
