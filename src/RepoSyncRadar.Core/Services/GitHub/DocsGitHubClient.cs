using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Options;
using DomainCommit = RepoSyncRadar.Core.Models.Commit;
using DomainCommitFile = RepoSyncRadar.Core.Models.CommitFile;

namespace RepoSyncRadar.Core.Services.GitHub;

/// <summary>
/// Implementation of <see cref="IDocsGitHubClient"/> backed by Octokit. Talks to the
/// <c>github/docs</c> repository to pull <c>Repo sync</c> PRs, per-commit file lists, unified
/// diffs, and individual file contents at a given ref.
/// </summary>
/// <remarks>
/// <para>
/// The client refreshes the Octokit <see cref="IGitHubClient.Connection"/> credentials on every
/// call using the same OAuth user token consumed by the Copilot SDK
/// (<see cref="IGitHubAccessTokenProvider"/>). This keeps GitHub auth on a single credential
/// surface; there is no separate personal access token.
/// </para>
/// <para>
/// PR listing is paginated using Octokit's <see cref="ApiOptions"/> until
/// <see cref="GitHubOptions.MaxPullRequests"/> title-matching PRs have been collected.
/// If <see cref="GitHubOptions.PullRequestCreatedAtOrAfter"/> is set, only PRs created at or
/// after that timestamp are eligible for triage.
/// Per-PR commit listings are auto-paginated by Octokit; the resulting <see cref="DomainCommit"/>
/// objects are returned with an empty <see cref="DomainCommit.Files"/> list and callers must invoke
/// <see cref="GetCommitFilesAsync"/> when they need file metadata.
/// </para>
/// </remarks>
public sealed class DocsGitHubClient : IDocsGitHubClient
{
    private const int _gitHubMaxPageSize = 100;
    private const string _diffMediaType = "application/vnd.github.v3.diff";

    private readonly IGitHubClient _github;
    private readonly IGitHubAccessTokenProvider _tokenProvider;
    private readonly IRadarRepository _repository;
    private readonly GitHubOptions _options;
    private readonly ILogger<DocsGitHubClient> _logger;

    public DocsGitHubClient(
        IGitHubClient github,
        IGitHubAccessTokenProvider tokenProvider,
        IRadarRepository repository,
        IOptions<GitHubOptions> options,
        ILogger<DocsGitHubClient> logger)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _github = github;
        _tokenProvider = tokenProvider;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DomainCommit>> FetchUnseenCommitsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

        var pullRequestRequest = new PullRequestRequest
        {
            State = ItemStateFilter.All,
            SortProperty = _options.PullRequestCreatedAtOrAfter is null
                ? PullRequestSort.Updated
                : PullRequestSort.Created,
            SortDirection = SortDirection.Descending,
        };
        var matchingPrs = await FetchMatchingPullRequestsAsync(pullRequestRequest, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var knownShas = await _repository.GetKnownShasAsync(cancellationToken).ConfigureAwait(false);

        var fetchedAt = DateTime.UtcNow;
        var commits = new List<DomainCommit>();
        foreach (var pr in matchingPrs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prCommits = await _github.PullRequest.Commits(_options.Owner, _options.Repo, pr.Number).ConfigureAwait(false);
            foreach (var prCommit in prCommits)
            {
                if (string.IsNullOrEmpty(prCommit.Sha) || knownShas.Contains(prCommit.Sha))
                {
                    continue;
                }

                commits.Add(MapCommit(prCommit, pr.Number, fetchedAt));
            }
        }

        return commits;
    }

    public async Task<IReadOnlyList<DomainCommitFile>> GetCommitFilesAsync(string sha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var commit = await _github.Repository.Commit.Get(_options.Owner, _options.Repo, sha).ConfigureAwait(false);
        if (commit.Files is null)
        {
            return Array.Empty<DomainCommitFile>();
        }

        var files = new List<DomainCommitFile>(commit.Files.Count);
        foreach (var file in commit.Files)
        {
            files.Add(new DomainCommitFile
            {
                Sha = sha,
                Path = file.Filename ?? string.Empty,
                Status = file.Status ?? string.Empty,
                Additions = file.Additions,
                Deletions = file.Deletions,
            });
        }
        return files;
    }

    public async Task<string> GetUnifiedDiffAsync(string sha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var uri = new Uri($"repos/{_options.Owner}/{_options.Repo}/commits/{sha}", UriKind.Relative);
        var response = await _github.Connection
            .Get<string>(uri, parameters: null, accepts: _diffMediaType, cancellationToken)
            .ConfigureAwait(false);

        return response.Body ?? string.Empty;
    }

    public async Task<string> GetFileContentAsync(string path, string gitRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);
        await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var contents = await _github.Repository.Content
            .GetAllContentsByRef(_options.Owner, _options.Repo, path, gitRef)
            .ConfigureAwait(false);

        if (contents.Count == 0)
        {
            return string.Empty;
        }

        var first = contents[0];

        if (!string.IsNullOrEmpty(first.EncodedContent))
        {
            // GitHub returns base64 with embedded newlines; strip them before decoding because
            // older .NET runtimes are strict about whitespace inside the base64 stream.
            var sanitized = first.EncodedContent
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal);
            return Encoding.UTF8.GetString(Convert.FromBase64String(sanitized));
        }

        return first.Content ?? string.Empty;
    }

    private async Task<List<PullRequest>> FetchMatchingPullRequestsAsync(
        PullRequestRequest pullRequestRequest,
        CancellationToken cancellationToken)
    {
        var matchingPrs = new List<PullRequest>(_options.MaxPullRequests);
        for (var page = 1; matchingPrs.Count < _options.MaxPullRequests; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prs = await _github.PullRequest.GetAllForRepository(
                _options.Owner,
                _options.Repo,
                pullRequestRequest,
                BuildPullRequestPageOptions(page)).ConfigureAwait(false);

            var reachedCreatedAtLowerBound = false;
            foreach (var pr in prs)
            {
                if (IsOlderThanCreatedAtLowerBound(pr))
                {
                    reachedCreatedAtLowerBound = true;
                    break;
                }

                if (IsTriageCandidate(pr))
                {
                    matchingPrs.Add(pr);
                    if (matchingPrs.Count == _options.MaxPullRequests)
                    {
                        break;
                    }
                }
            }

            if (reachedCreatedAtLowerBound || prs.Count < _gitHubMaxPageSize)
            {
                break;
            }
        }
        return matchingPrs;
    }

    private bool IsTriageCandidate(PullRequest pr)
    {
        if (pr.Title is null
            || !pr.Title.StartsWith(_options.PullRequestTitleFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (_options.PullRequestCreatedAtOrAfter is not { } lowerBound)
        {
            return true;
        }

        return pr.CreatedAt >= lowerBound;
    }

    private bool IsOlderThanCreatedAtLowerBound(PullRequest pr)
        => _options.PullRequestCreatedAtOrAfter is { } lowerBound
            && pr.CreatedAt < lowerBound;

    private static ApiOptions BuildPullRequestPageOptions(int page)
        => new()
        {
            PageSize = _gitHubMaxPageSize,
            PageCount = 1,
            StartPage = page,
        };

    private static DomainCommit MapCommit(PullRequestCommit prCommit, int prNumber, DateTime fetchedAt)
    {
        var inner = prCommit.Commit;
        var authoredAt = inner?.Author?.Date
            ?? inner?.Committer?.Date
            ?? new DateTimeOffset(fetchedAt, TimeSpan.Zero);
        var author = inner?.Author?.Name
            ?? inner?.Committer?.Name
            ?? prCommit.Author?.Login
            ?? string.Empty;

        return new DomainCommit
        {
            Sha = prCommit.Sha,
            PrNumber = prNumber,
            Message = inner?.Message ?? string.Empty,
            Author = author,
            AuthoredAt = authoredAt.UtcDateTime,
            FetchedAt = fetchedAt,
        };
    }

    /// <summary>
    /// Refreshes <see cref="IGitHubClient.Connection"/> credentials with a freshly resolved
    /// OAuth user token before each Octokit call. The token provider caches in-memory and
    /// only triggers DPAPI / device-flow when truly necessary, so the cost here is minimal.
    /// </summary>
    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        _github.Connection.Credentials = new Credentials(token);
    }
}
