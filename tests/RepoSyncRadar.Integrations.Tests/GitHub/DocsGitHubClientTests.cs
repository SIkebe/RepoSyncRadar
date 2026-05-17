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
    private const string Owner = "github";
    private const string Repo = "docs";

    private static readonly int[] ExpectedPrNumbers = [1001, 1003, 1005];
    private static readonly string[] ExpectedShas = ["sha-1001", "sha-1003", "sha-1005"];

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
            Owner, Repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>()).Returns(prs);

        github.PullRequest.Commits(Owner, Repo, Arg.Any<int>())
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
        Assert.Equal(ExpectedPrNumbers, commits.Select(c => c.PrNumber).ToArray());
        Assert.Equal(ExpectedShas, commits.Select(c => c.Sha).ToArray());
    }

    [Fact]
    public async Task FetchUnseenCommitsAsync_Excludes_Known_Shas()
    {
        var (client, github, repo, _) = CreateClient();
        var ct = TestContext.Current.CancellationToken;
        repo.GetKnownShasAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "sha-known" });

        github.PullRequest.GetAllForRepository(
            Owner, Repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
            .Returns((IReadOnlyList<PullRequest>)new[] { MakePullRequest(42, "Repo sync 2026-05-12") });

        github.PullRequest.Commits(Owner, Repo, 42)
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
            Owner,
            Repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 100 && o.PageCount == 1))
            .Returns(firstPage);
        github.PullRequest.GetAllForRepository(
            Owner,
            Repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 2 && o.PageSize == 100 && o.PageCount == 1))
            .Returns(secondPage);
        github.PullRequest.Commits(Owner, Repo, Arg.Any<int>())
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
            Owner,
            Repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 1 && o.PageSize == 100 && o.PageCount == 1));
        await github.PullRequest.Received(1).GetAllForRepository(
            Owner,
            Repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 2 && o.PageSize == 100 && o.PageCount == 1));
        await github.PullRequest.DidNotReceive().GetAllForRepository(
            Owner,
            Repo,
            Arg.Any<PullRequestRequest>(),
            Arg.Is<ApiOptions>(o => o.StartPage == 3));
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
            Arg.Is<Uri>(u => u.ToString() == $"repos/{Owner}/{Repo}/commits/deadbeef"),
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

        github.Repository.Content.GetAllContentsByRef(Owner, Repo, "content/intro.md", "feature-branch")
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
            Owner, Repo, Arg.Any<PullRequestRequest>(), Arg.Any<ApiOptions>())
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
            Owner = Owner,
            Repo = Repo,
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
    {
        var pr = new PullRequest(number);
        // Title has a private setter; reflection is the only practical seam since Octokit
        // does not expose builders for response DTOs in v14.
        var titleProp = typeof(PullRequest).GetProperty(nameof(PullRequest.Title))!;
        titleProp.SetValue(pr, title);
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
