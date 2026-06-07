using System.Text.Json;
using Microsoft.Extensions.AI;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Copilot.Tools;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot.Tools;

/// <summary>
/// Validates the read-only <c>radar_*</c> Copilot tools registered under
/// <see cref="RadarTools"/>. The underlying stubs simulate the EF / Octokit / docs API layers.
/// </summary>
public sealed class RadarToolsTests
{
    [Fact]
    public async Task RadarListCommits_Filters_By_Status()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new StubRadarRepository(
            new List<Commit>
            {
                MakeCommit("aaa", ReviewStatus.Unseen),
                MakeCommit("bbb", ReviewStatus.Adopted),
            });
        var tools = new RadarTools(repo, new StubDocsGitHubClient(), new StubDocsApiClient());

        var result = await tools.ListCommitsAsync("Adopted", limit: null, ct);

        Assert.Equal("Adopted", repo.LastFilter?.Status?.ToString());
        var commit = Assert.Single(result.Commits);
        Assert.Equal("bbb", commit.Sha);
    }

    [Fact]
    public async Task RadarListCommits_Honors_Limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new StubRadarRepository(new List<Commit> { MakeCommit("aaa", ReviewStatus.Unseen) });
        var tools = new RadarTools(repo, new StubDocsGitHubClient(), new StubDocsApiClient());

        // Default limit
        await tools.ListCommitsAsync(status: null, limit: null, ct);
        Assert.Equal(50, repo.LastFilter?.Limit);

        // Explicit limit
        await tools.ListCommitsAsync(status: null, limit: 7, ct);
        Assert.Equal(7, repo.LastFilter?.Limit);
    }

    [Fact]
    public async Task RadarListCommits_Reports_Unseen_Total_To_Triage_Progress()
    {
        var ct = TestContext.Current.CancellationToken;
        var progress = new CapturingProgress();
        var tracker = new TriageScoringProgressTracker();
        var repo = new StubRadarRepository([
            MakeCommit("aaa1111111111111111111111111111111111111", ReviewStatus.Unseen),
            MakeCommit("bbb2222222222222222222222222222222222222", ReviewStatus.Unseen),
        ]);
        var tools = new RadarTools(repo, new StubDocsGitHubClient(), new StubDocsApiClient(), tracker);

        using var scope = tracker.Begin(progress);
        await tools.ListCommitsAsync("Unseen", limit: 50, ct);

        var message = Assert.Single(progress.Messages);
        Assert.Contains("対象 2 件", message, StringComparison.Ordinal);
        Assert.Contains("分析 0 / 2 件", message, StringComparison.Ordinal);
        Assert.Contains("スコア保存 0 / 2 件", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RadarGetDiff_Reports_Triage_Analysis_Started()
    {
        var ct = TestContext.Current.CancellationToken;
        var progress = new CapturingProgress();
        var tracker = new TriageScoringProgressTracker();
        var tools = new RadarTools(new StubRadarRepository(), new StubDocsGitHubClient(), new StubDocsApiClient(), tracker);

        using var scope = tracker.Begin(progress);
        tracker.ReportCommitList([
            "aaa1111111111111111111111111111111111111",
            "bbb2222222222222222222222222222222222222",
        ]);

        await tools.GetDiffAsync("aaa1111111111111111111111111111111111111", ct);

        Assert.Contains(progress.Messages, message => message.Contains("分析 1 / 2 件", StringComparison.Ordinal)
            && message.Contains("スコア保存 0 / 2 件", StringComparison.Ordinal)
            && message.Contains("aaa11111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RadarListCommits_Unseen_Requests_Unscored_Commits_Only()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = new StubRadarRepository([
            MakeCommit("aaa1111111111111111111111111111111111111", ReviewStatus.Unseen, scored: true),
            MakeCommit("bbb2222222222222222222222222222222222222", ReviewStatus.Unseen, scored: false),
        ]);
        var tools = new RadarTools(repo, new StubDocsGitHubClient(), new StubDocsApiClient());

        var result = await tools.ListCommitsAsync("Unseen", limit: 50, ct);

        Assert.True(repo.LastFilter?.UnscoredOnly);
        var commit = Assert.Single(result.Commits);
        Assert.Equal("bbb2222222222222222222222222222222222222", commit.Sha);
    }

    [Fact]
    public async Task RadarGetDiff_Masks_Secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        const string pat = "ghp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJ";
        var github = new StubDocsGitHubClient { Diff = $"+TOKEN={pat}\n" };
        var tools = new RadarTools(new StubRadarRepository(), github, new StubDocsApiClient());

        var result = await tools.GetDiffAsync("deadbee", ct);

        Assert.DoesNotContain(pat, result.Diff, StringComparison.Ordinal);
        Assert.Contains("***GITHUB_PAT***", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RadarGetDiff_Wraps_Untrusted()
    {
        var ct = TestContext.Current.CancellationToken;
        var github = new StubDocsGitHubClient { Diff = "+harmless\n" };
        var tools = new RadarTools(new StubRadarRepository(), github, new StubDocsApiClient());

        var result = await tools.GetDiffAsync("deadbee", ct);

        Assert.Contains("<<<UNTRUSTED:", result.Diff, StringComparison.Ordinal);
        Assert.Contains("<<<END>>>", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RadarResolveUrl_Returns_Resolver_Output()
    {
        var ct = TestContext.Current.CancellationToken;
        var docs = new StubDocsApiClient
        {
            PageListByLangVersion = new Dictionary<(string Lang, string Version), IReadOnlyList<string>>
            {
                [("en", "fpt")] = new[] { "/en/copilot/about-copilot" },
            },
        };
        var tools = new RadarTools(new StubRadarRepository(), new StubDocsGitHubClient(), docs);

        var result = await tools.ResolveUrlAsync(
            repoPath: "content/copilot/about-copilot.md",
            frontmatterVersions: "fpt: '*'",
            language: "en",
            ct);

        Assert.Contains("/en/copilot/about-copilot", result.Urls);
    }

    [Fact]
    public async Task RadarFetchRendered_Returns_Body_Html()
    {
        var ct = TestContext.Current.CancellationToken;
        var docs = new StubDocsApiClient { Body = "<p>hello</p>" };
        var tools = new RadarTools(new StubRadarRepository(), new StubDocsGitHubClient(), docs);

        var result = await tools.FetchRenderedAsync("/en/copilot", ct);

        Assert.Equal("<p>hello</p>", result.BodyHtml);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RadarFetchRendered_Throws_On_NotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var docs = new StubDocsApiClient { ThrowOnFetch = new HttpRequestException("404") };
        var tools = new RadarTools(new StubRadarRepository(), new StubDocsGitHubClient(), docs);

        var result = await tools.FetchRenderedAsync("/en/missing", ct);

        Assert.Null(result.BodyHtml);
        Assert.NotNull(result.Error);
        Assert.Contains("404", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAll_Registers_Four_Named_Tools()
    {
        var tools = new RadarTools(new StubRadarRepository(), new StubDocsGitHubClient(), new StubDocsApiClient());
        var functions = tools.CreateAll();

        var names = functions.Select(f => f.Name).ToHashSet();
        Assert.Equal(4, functions.Count);
        Assert.Contains("radar_list_commits", names);
        Assert.Contains("radar_get_diff", names);
        Assert.Contains("radar_resolve_url", names);
        Assert.Contains("radar_fetch_rendered", names);
        // All read-only tools opt out of the permission prompt.
        Assert.All(functions, f =>
        {
            Assert.True(f.AdditionalProperties.TryGetValue("skip_permission", out var value));
            Assert.Equal(true, value);
        });
    }

    [Fact]
    public void Dto_RoundTrips_Through_System_Text_Json()
    {
        IReadOnlyList<string> files = ["content/copilot/x.md"];
        IReadOnlyList<CommitDto> dtos = [
            new CommitDto("abc", PrNumber: 12, Message: "msg", Author: "octo",
                AuthoredAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Status: "Unseen",
                Files: files),
        ];
        var original = new CommitsResult(dtos);

        var json = JsonSerializer.Serialize(original);
        var clone = JsonSerializer.Deserialize<CommitsResult>(json);

        Assert.NotNull(clone);
        var c = Assert.Single(clone!.Commits);
        Assert.Equal("abc", c.Sha);
        Assert.Equal(12, c.PrNumber);
        Assert.Equal("Unseen", c.Status);
        Assert.Equal("content/copilot/x.md", Assert.Single(c.Files));
    }

    private static Commit MakeCommit(string sha, ReviewStatus status, bool scored = false)
    {
        return new Commit
        {
            Sha = sha,
            PrNumber = 1,
            Message = "msg",
            Author = "octo",
            AuthoredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Files = new List<CommitFile>
            {
                new() { Sha = sha, Path = "content/x.md", Status = "modified", Additions = 1, Deletions = 0 },
            },
            Review = new Review { Sha = sha, Status = status },
            Scoring = scored
                ? new Scoring
                {
                    Sha = sha,
                    Score = 0.7,
                    Category = "feature-update",
                    AudienceJson = "[]",
                    SummaryJa = "要約",
                    WhyJa = "理由",
                    DetailsJa = "詳細",
                    Model = "gpt-5",
                    ScoredAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                }
                : null,
        };
    }

    private sealed class StubRadarRepository(List<Commit>? commits = null) : IRadarRepository
    {
        private readonly List<Commit> _commits = commits ?? new List<Commit>();

        public CommitQueryFilter? LastFilter { get; private set; }

        public Task<IReadOnlySet<string>> GetKnownShasAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(_commits.Select(c => c.Sha).ToHashSet());

        public Task<IReadOnlySet<string>> GetKnownShasAsync(IEnumerable<string> candidateShas, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(candidateShas.Intersect(_commits.Select(c => c.Sha)).ToHashSet());

        public Task<IReadOnlyList<string>> UpsertCommitsAsync(IEnumerable<Commit> commits, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task SetReviewAsync(string sha, ReviewStatus status, string? reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> DeleteUnseenCommitsAsync(IEnumerable<string> shas, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<Commit>> QueryCommitsAsync(CommitQueryFilter filter, CancellationToken cancellationToken = default)
        {
            LastFilter = filter;
            var filtered = filter.Status is null
                ? _commits
                : _commits.Where(c => (c.Review?.Status ?? ReviewStatus.Unseen) == filter.Status);
            if (filter.UnscoredOnly)
            {
                filtered = filtered.Where(c => c.Scoring is null);
            }
            if (filter.Limit is int n)
            {
                filtered = filtered.Take(n);
            }
            return Task.FromResult<IReadOnlyList<Commit>>(filtered.ToList());
        }

        public Task<IReadOnlyDictionary<ReviewStatus, int>> GetReviewCountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(new Dictionary<ReviewStatus, int>());

        public Task<bool> AddIgnoreRuleAsync(string pattern, string? reason, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<IgnoreRule>> GetIgnoreRulesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IgnoreRule>>(Array.Empty<IgnoreRule>());

        public Task<int> DeleteIgnoreRulesAsync(IEnumerable<string> patterns, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> BulkRejectByPathPrefixAsync(string pathPrefix, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class StubDocsGitHubClient : IDocsGitHubClient
    {
        public string Diff { get; set; } = string.Empty;

        public Task<DocsGitHubTriageEstimate> EstimateTriageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DocsGitHubTriageEstimate(0, 0));

        public Task<IReadOnlyList<Commit>> FetchUnseenCommitsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Commit>>(Array.Empty<Commit>());

        public Task<IReadOnlyList<CommitFile>> GetCommitFilesAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CommitFile>>(Array.Empty<CommitFile>());

        public Task<string> GetUnifiedDiffAsync(string sha, CancellationToken cancellationToken = default)
            => Task.FromResult(Diff);

        public Task<string> GetFileContentAsync(string path, string gitRef, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }

    private sealed class StubDocsApiClient : IDocsApiClient
    {
        public string Body { get; set; } = string.Empty;

        public Exception? ThrowOnFetch { get; set; }

        public Dictionary<(string Lang, string Version), IReadOnlyList<string>> PageListByLangVersion { get; set; } =
            new Dictionary<(string Lang, string Version), IReadOnlyList<string>>();

        public Task<IReadOnlyList<string>> GetPageListAsync(string language, string version, CancellationToken cancellationToken = default)
        {
            if (PageListByLangVersion.TryGetValue((language, version), out var list))
            {
                return Task.FromResult(list);
            }
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task<string?> ResolveCanonicalAsync(string pathname, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(pathname);

        public Task<string> GetArticleBodyAsync(string pathname, CancellationToken cancellationToken = default)
        {
            if (ThrowOnFetch is not null)
            {
                throw ThrowOnFetch;
            }
            return Task.FromResult(Body);
        }
    }

    private sealed class CapturingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
