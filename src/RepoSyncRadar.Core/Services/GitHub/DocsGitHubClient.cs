using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
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
/// The client expects an <see cref="IGitHubClient"/> registered in DI and pre-configured with an
/// authentication token (when available). When <see cref="GitHubOptions.PersonalAccessToken"/> is
/// blank, the client emits a single <c>Warning</c> log the first time it is used so that the
/// anonymous rate limit does not silently surprise the caller.
/// </para>
/// <para>
/// PR listing is paginated using Octokit's <see cref="ApiOptions"/> so that arbitrary
/// <see cref="GitHubOptions.MaxPullRequests"/> values are honored even when they exceed the
/// per-page maximum of 100. Per-PR commit listings are auto-paginated by Octokit; the resulting
/// <see cref="DomainCommit"/> objects are returned with an empty <see cref="DomainCommit.Files"/>
/// list and callers must invoke <see cref="GetCommitFilesAsync"/> when they need file metadata.
/// </para>
/// </remarks>
public sealed class DocsGitHubClient : IDocsGitHubClient
{
    private const int GitHubMaxPageSize = 100;
    private const string DiffMediaType = "application/vnd.github.v3.diff";

    private readonly IGitHubClient _github;
    private readonly IRadarRepository _repository;
    private readonly GitHubOptions _options;
    private readonly ILogger<DocsGitHubClient> _logger;
    private int _tokenWarningEmitted;

    public DocsGitHubClient(
        IGitHubClient github,
        IRadarRepository repository,
        IOptions<GitHubOptions> options,
        ILogger<DocsGitHubClient> logger)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _github = github;
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DomainCommit>> FetchUnseenCommitsAsync(CancellationToken cancellationToken = default)
    {
        WarnIfTokenMissing();

        var pullRequestRequest = new PullRequestRequest
        {
            State = ItemStateFilter.All,
            SortProperty = PullRequestSort.Updated,
            SortDirection = SortDirection.Descending,
        };
        var apiOptions = BuildPullRequestApiOptions(_options.MaxPullRequests);

        cancellationToken.ThrowIfCancellationRequested();
        var prs = await _github.PullRequest.GetAllForRepository(
            _options.Owner,
            _options.Repo,
            pullRequestRequest,
            apiOptions).ConfigureAwait(false);

        var matchingPrs = prs
            .Where(pr => pr.Title is not null && pr.Title.StartsWith(_options.PullRequestTitleFilter, StringComparison.Ordinal))
            .Take(_options.MaxPullRequests)
            .ToList();

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
        WarnIfTokenMissing();

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
        WarnIfTokenMissing();

        cancellationToken.ThrowIfCancellationRequested();
        var uri = new Uri($"repos/{_options.Owner}/{_options.Repo}/commits/{sha}", UriKind.Relative);
        var response = await _github.Connection
            .Get<string>(uri, parameters: null, accepts: DiffMediaType, cancellationToken)
            .ConfigureAwait(false);

        return response.Body ?? string.Empty;
    }

    public async Task<string> GetFileContentAsync(string path, string gitRef, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);
        WarnIfTokenMissing();

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

    /// <summary>
    /// Computes <see cref="ApiOptions"/> that fetch enough pages to cover
    /// <paramref name="maxPullRequests"/> entries while never overshooting GitHub's per-page cap.
    /// </summary>
    private static ApiOptions BuildPullRequestApiOptions(int maxPullRequests)
    {
        var pageSize = Math.Clamp(maxPullRequests, 1, GitHubMaxPageSize);
        var pageCount = Math.Max(1, (int)Math.Ceiling(maxPullRequests / (double)pageSize));
        return new ApiOptions
        {
            PageSize = pageSize,
            PageCount = pageCount,
            StartPage = 1,
        };
    }

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

    private void WarnIfTokenMissing()
    {
        if (!string.IsNullOrWhiteSpace(_options.PersonalAccessToken))
        {
            return;
        }

        if (Interlocked.Exchange(ref _tokenWarningEmitted, 1) == 0)
        {
            s_anonymousAccessWarning(_logger, null);
        }
    }

    private static readonly Action<ILogger, Exception?> s_anonymousAccessWarning =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1001, nameof(DocsGitHubClient) + ".AnonymousAccess"),
            "GitHub personal access token is empty; falling back to anonymous API access. Rate limits will be tight.");
}
