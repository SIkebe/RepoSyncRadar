using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services.GitHub;
using Xunit;
using OctokitCommit = Octokit.Commit;

namespace RepoSyncRadar.Integrations.Tests.GitHub;

/// <summary>
/// Exercises <see cref="DocsGitHubClient"/> against an <see cref="NSubstitute"/>-backed
/// <see cref="IGitHubClient"/>. All Octokit calls are stubbed so the suite stays
/// network-independent.
/// </summary>
public class DocsGitHubClientTests
{
    private const string _owner = "github";
    private const string _repo = "docs";

    private static readonly int[] _expectedPrNumbers = [1001, 1003, 1005];
    private static readonly string[] _expectedShas = ["sha-1001", "sha-1003", "sha-1005"];

    [Fact]
    public async Task FetchUnseenCommitsAsync_Filters_By_Title()
    {
        var (client, github, _, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // 5 PRs: 3 with the "Repo sync" prefix, 2 unrelated.
        var prs = new List<PullRequest>
        {
            MakePullRequest(1001, "Repo sync 2026-05-10"),
            MakePullRequest(1002, "Update README"),
            MakePullRequest(1003, "Repo sync 2026-05-11"),
            MakePullRequest(1004, "Bump dependency"),
            MakePullRequest(1005, "Repo sync 2026-05-12"),
        };
        github.PullRequest.GetAllForRepository(
            _owner, _repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>()).Returns(prs);

        github.PullRequest.Commits(_owner, _repo, Arg.Any<int>())
            .Returns(call =>
            {
                var number = call.Arg<int>();
                return (IReadOnlyList<PullRequestCommit>)new[]
                {
                    MakePullRequestCommit($"sha-{number}", "msg", "octocat"),
                };
            });

        var commits = await client.FetchUnseenCommitsAsync(ct);

        Assert.Equal(3, commits.Count);
        Assert.Equal(_expectedPrNumbers, commits.Select(c => c.PrNumber).ToArray());
        Assert.Equal(_expectedShas, commits.Select(c => c.Sha).ToArray());
    }

    [Fact]
    public async Task EstimateTriageAsync_Counts_Matching_Prs_And_New_Commits_Without_File_Enrichment()
    {
        var (client, github, repo, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        repo.GetKnownShasAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "sha-known" });
        github.PullRequest.GetAllForRepository(
            _owner, _repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
            .Returns((IReadOnlyList<PullRequest>)new[]
            {
                MakePullRequest(1001, "Repo sync 2026-05-10"),
                MakePullRequest(1002, "Update README"),
                MakePullRequest(1003, "Repo sync 2026-05-11"),
            });
        github.PullRequest.Commits(_owner, _repo, 1001)
            .Returns((IReadOnlyList<PullRequestCommit>)new[]
            {
                MakePullRequestCommit("sha-known", "old", "octocat"),
                MakePullRequestCommit("sha-new-1", "fresh", "octocat"),
            });
        github.PullRequest.Commits(_owner, _repo, 1003)
            .Returns((IReadOnlyList<PullRequestCommit>)new[]
            {
                MakePullRequestCommit("sha-new-2", "fresh", "octocat"),
            });

        var estimate = await client.EstimateTriageAsync(ct);

        Assert.Equal(2, estimate.CandidatePullRequestCount);
        Assert.Equal(2, estimate.NewUnseenCommitCount);
        await github.Repository.Commit.DidNotReceive().Get(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FetchUnseenCommitsAsync_Excludes_Known_Shas()
    {
        var (client, github, repo, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        repo.GetKnownShasAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "sha-known" });

        github.PullRequest.GetAllForRepository(
            _owner, _repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
            .Returns((IReadOnlyList<PullRequest>)new[] { MakePullRequest(42, "Repo sync 2026-05-12") });

        github.PullRequest.Commits(_owner, _repo, 42)
            .Returns((IReadOnlyList<PullRequestCommit>)new[]
            {
                MakePullRequestCommit("sha-known", "old", "octocat"),
                MakePullRequestCommit("sha-new", "fresh", "octocat"),
            });

        var commits = await client.FetchUnseenCommitsAsync(ct);

        var only = Assert.Single(commits);
        Assert.Equal("sha-new", only.Sha);
    }

    [Fact]
    public async Task FetchUnseenCommitsAsync_Paginates_Until_Title_Matches_Reach_Limit()
    {
        var (client, github, _, _) = CreateClient(options => options.MaxPullRequests = 2);
        var ct = TestContext.Current.CancellationToken;

        var firstPage = Enumerable.Range(1, 100)
            .Select(i => MakePullRequest(1000 + i, $"Unrelated docs update {i}"))
            .ToArray();
        var secondPage = new[]
        {
            MakePullRequest(2001, "Repo sync 2026-05-15"),
            MakePullRequest(2002, "Fix typo"),
            MakePullRequest(2003, "Repo sync 2026-05-16"),
            MakePullRequest(2004, "Repo sync 2026-05-17"),
        };

        github.PullRequest.GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 100 && o.PageCount == 1))
            .Returns(firstPage);
        github.PullRequest.GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 2 && o.PageSize == 100 && o.PageCount == 1))
            .Returns(secondPage);
        github.PullRequest.Commits(_owner, _repo, Arg.Any<int>())
            .Returns(call =>
            {
                var number = call.Arg<int>();
                return (IReadOnlyList<PullRequestCommit>)new[]
                {
                    MakePullRequestCommit($"sha-{number}", "msg", "octocat"),
                };
            });

        var commits = await client.FetchUnseenCommitsAsync(ct);

        Assert.Equal([2001, 2003], commits.Select(static commit => commit.PrNumber).ToArray());

        await github.PullRequest.Received(1).GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 100 && o.PageCount == 1));
        await github.PullRequest.Received(1).GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 2 && o.PageSize == 100 && o.PageCount == 1));
        await github.PullRequest.DidNotReceive().GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 3));
    }

    [Fact]
    public async Task FetchUnseenCommitsAsync_Filters_By_PullRequest_CreatedAt_Lower_Bound()
    {
        var lowerBound = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);
        var (client, github, _, _) = CreateClient(options => options.PullRequestCreatedAtOrAfter = lowerBound);
        var ct = TestContext.Current.CancellationToken;

        github.PullRequest.GetAllForRepository(
            _owner, _repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
            .Returns((IReadOnlyList<PullRequest>)new[]
            {
                MakePullRequest(3003, "Repo sync 2026-05-16", lowerBound.AddDays(1)),
                MakePullRequest(3002, "Repo sync 2026-05-15", lowerBound),
                MakePullRequest(3004, "Unrelated docs update", lowerBound),
                MakePullRequest(3001, "Repo sync 2026-05-14", lowerBound.AddTicks(-1)),
            });
        github.PullRequest.Commits(_owner, _repo, Arg.Any<int>())
            .Returns(call =>
            {
                var number = call.Arg<int>();
                return (IReadOnlyList<PullRequestCommit>)new[]
                {
                    MakePullRequestCommit($"sha-{number}", "msg", "octocat"),
                };
            });

        var commits = await client.FetchUnseenCommitsAsync(ct);

        Assert.Equal([3003, 3002], commits.Select(static commit => commit.PrNumber).ToArray());
        await github.PullRequest.DidNotReceive().Commits(_owner, _repo, 3001);
        await github.PullRequest.DidNotReceive().Commits(_owner, _repo, 3004);
        await github.PullRequest.Received(1).GetAllForRepository(
            _owner,
            _repo,
            Arg.Is<PullRequestRequest>(request => request.SortProperty == PullRequestSort.Created
                && request.SortDirection == SortDirection.Descending),
            Arg.Any<ApiOptions>());
    }

    [Fact]
    public async Task FetchUnseenCommitsAsync_Stops_Paging_When_CreatedAt_Is_Older_Than_Lower_Bound()
    {
        var lowerBound = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);
        var (client, github, _, _) = CreateClient(options => options.PullRequestCreatedAtOrAfter = lowerBound);
        var ct = TestContext.Current.CancellationToken;

        var firstPage = Enumerable.Range(1, 100)
            .Select(i => i <= 2
                ? MakePullRequest(4000 + i, $"Repo sync recent {i}", lowerBound.AddDays(3 - i))
                : MakePullRequest(4000 + i, $"Repo sync old {i}", lowerBound.AddTicks(-1)))
            .ToArray();

        github.PullRequest.GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 100 && o.PageCount == 1))
            .Returns(firstPage);
        github.PullRequest.GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 2))
            .Returns((IReadOnlyList<PullRequest>)Array.Empty<PullRequest>());
        github.PullRequest.Commits(_owner, _repo, Arg.Any<int>())
            .Returns(call =>
            {
                var number = call.Arg<int>();
                return (IReadOnlyList<PullRequestCommit>)new[]
                {
                    MakePullRequestCommit($"sha-{number}", "msg", "octocat"),
                };
            });

        var commits = await client.FetchUnseenCommitsAsync(ct);

        Assert.Equal([4001, 4002], commits.Select(static commit => commit.PrNumber).ToArray());
        await github.PullRequest.DidNotReceive().GetAllForRepository(
            _owner,
            _repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 2));
    }

    [Fact]
    public async Task GetCommitFilesAsync_Paginates_All_Changed_Files()
    {
        var (client, github, _, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var firstPage = Enumerable.Range(1, 100)
            .Select(i => MakeGitHubCommitFile($"content/ignored/{i}.md"))
            .ToArray();
        var secondPage = new[]
        {
            MakeGitHubCommitFile("content/review-me.md", status: "added", additions: 2),
        };
        var firstResponse = Substitute.For<IApiResponse<GitHubCommit>>();
        firstResponse.Body.Returns(MakeGitHubCommit(firstPage));
        var secondResponse = Substitute.For<IApiResponse<GitHubCommit>>();
        secondResponse.Body.Returns(MakeGitHubCommit(secondPage));
        github.Connection.Get<GitHubCommit>(
                Arg.Any<Uri>(),
                Arg.Is<IDictionary<string, string>>(parameters => parameters["page"] == "1"),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(firstResponse);
        github.Connection.Get<GitHubCommit>(
                Arg.Any<Uri>(),
                Arg.Is<IDictionary<string, string>>(parameters => parameters["page"] == "2"),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(secondResponse);

        var files = await client.GetCommitFilesAsync("deadbeef", ct);

        Assert.Equal(101, files.Count);
        Assert.Equal("content/review-me.md", files[^1].Path);
        await github.Connection.Received(2).Get<GitHubCommit>(
            Arg.Is<Uri>(uri => uri.ToString() == $"repos/{_owner}/{_repo}/commits/deadbeef"),
            Arg.Is<IDictionary<string, string>>(parameters => parameters["per_page"] == "100"),
            "application/vnd.github+json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnifiedDiffAsync_Sets_Accept_Header()
    {
        var (client, github, _, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var apiResponse = Substitute.For<IApiResponse<string>>();
        apiResponse.Body.Returns("--- a/x\n+++ b/x\n");

        github.Connection.Get<string>(
                Arg.Any<Uri>(),
                Arg.Any<IDictionary<string, string>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(apiResponse);

        var diff = await client.GetUnifiedDiffAsync("deadbeef", ct);

        Assert.Equal("--- a/x\n+++ b/x\n", diff);
        await github.Connection.Received(1).Get<string>(
            Arg.Is<Uri>(u => u.ToString() == $"repos/{_owner}/{_repo}/commits/deadbeef"),
            Arg.Any<IDictionary<string, string>?>(),
            "application/vnd.github.v3.diff",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileContentAsync_Decodes_Base64()
    {
        var (client, github, _, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        const string Payload = "# Hello, RepoSyncRadar";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload));
        var content = new RepositoryContent(
            name: "intro.md",
            path: "content/intro.md",
            sha: "blob-sha",
            size: Payload.Length,
            type: ContentType.File,
            downloadUrl: null,
            url: null,
            gitUrl: null,
            htmlUrl: null,
            encoding: "base64",
            encodedContent: encoded,
            target: null,
            submoduleGitUrl: null);

        github.Repository.Content.GetAllContentsByRef(_owner, _repo, "content/intro.md", "feature-branch")
            .Returns((IReadOnlyList<RepositoryContent>)new[] { content });

        var result = await client.GetFileContentAsync("content/intro.md", "feature-branch", ct);

        Assert.Equal(Payload, result);
    }

    [Fact]
    public async Task EachCall_Refreshes_Octokit_Credentials_From_TokenProvider()
    {
        var tokenProvider = Substitute.For<IGitHubAccessTokenProvider>();
        tokenProvider.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("ghu_oauth_user_token");
        var (client, github, _, _) = CreateClient(tokenProvider: tokenProvider);
        var ct = TestContext.Current.CancellationToken;

        github.PullRequest.GetAllForRepository(
            _owner, _repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
            .Returns((IReadOnlyList<PullRequest>)Array.Empty<PullRequest>());

        _ = await client.FetchUnseenCommitsAsync(ct);

        await tokenProvider.Received(1).GetAccessTokenAsync(Arg.Any<CancellationToken>());
        github.Connection.Received().Credentials = Arg.Is<Credentials>(c =>
            c.Password == "ghu_oauth_user_token");
    }

    private static (
        DocsGitHubClient client,
        IGitHubClient github,
        IRadarRepository repository,
        CapturingLogger<DocsGitHubClient> logger)
        CreateClient(
            Action<GitHubOptions>? configure = null,
            IGitHubAccessTokenProvider? tokenProvider = null)
    {
        var github = Substitute.For<IGitHubClient>();
        var repository = Substitute.For<IRadarRepository>();
        repository.GetKnownShasAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal));

        var options = new GitHubOptions
        {
            Owner = _owner,
            Repo = _repo,
            PullRequestTitleFilter = "Repo sync",
            MaxPullRequests = 5,
        };
        configure?.Invoke(options);

        var resolvedTokenProvider = tokenProvider ?? StubTokenProvider("ghu_stub_token");

        var logger = new CapturingLogger<DocsGitHubClient>();
        var client = new DocsGitHubClient(
            github,
            resolvedTokenProvider,
            repository,
            Options.Create(options),
            logger);
        return (client, github, repository, logger);
    }

    private static IGitHubAccessTokenProvider StubTokenProvider(string token)
    {
        var provider = Substitute.For<IGitHubAccessTokenProvider>();
        provider.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(token);
        return provider;
    }

    private static PullRequest MakePullRequest(int number, string title)
        => MakePullRequest(number, title, new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero));

    private static PullRequest MakePullRequest(int number, string title, DateTimeOffset createdAt)
    {
        var pr = new PullRequest(number);
        // Title has a private setter; reflection is the only practical seam since Octokit
        // does not expose builders for response DTOs in v14.
        var titleProp = typeof(PullRequest).GetProperty(nameof(PullRequest.Title))!;
        titleProp.SetValue(pr, title);
        var createdAtProp = typeof(PullRequest).GetProperty(nameof(PullRequest.CreatedAt))!;
        createdAtProp.SetValue(pr, createdAt);
        return pr;
    }

    private static PullRequestCommit MakePullRequestCommit(string sha, string message, string authorLogin)
    {
        var authoredAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var inner = new OctokitCommit(
            nodeId: string.Empty,
            url: string.Empty,
            label: string.Empty,
            @ref: string.Empty,
            sha: sha,
            user: null!,
            repository: null!,
            message: message,
            author: new Committer(authorLogin, "octo@example.invalid", authoredAt),
            committer: new Committer(authorLogin, "octo@example.invalid", authoredAt),
            tree: null!,
            parents: Array.Empty<GitReference>(),
            commentCount: 0,
            verification: null!);

        return new PullRequestCommit(
            nodeId: string.Empty,
            author: new User(),
            commentsUrl: string.Empty,
            commit: inner,
            committer: new User(),
            htmlUrl: string.Empty,
            parents: Array.Empty<GitReference>(),
            sha: sha,
            url: string.Empty);
    }

    private static GitHubCommit MakeGitHubCommit(IReadOnlyList<GitHubCommitFile> files)
    {
        var commit = new GitHubCommit();
        typeof(GitHubCommit).GetProperty(nameof(GitHubCommit.Files))!.SetValue(commit, files);
        return commit;
    }

    private static GitHubCommitFile MakeGitHubCommitFile(
        string filename,
        string status = "modified",
        int additions = 1,
        int deletions = 0)
        => new(
            filename,
            additions,
            deletions,
            additions + deletions,
            status,
            blobUrl: string.Empty,
            contentsUrl: string.Empty,
            rawUrl: string.Empty,
            sha: string.Empty,
            patch: string.Empty,
            previousFileName: string.Empty);

    /// <summary>
    /// Minimal <see cref="ILogger{TCategoryName}"/> capture that lets a test assert against
    /// <see cref="LogLevel"/> and rendered message without pulling in
    /// <c>Microsoft.Extensions.Logging.Testing</c>.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
