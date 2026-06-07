using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

public sealed class TriagePreflightSummaryBuilderTests
{
    [Fact]
    public async Task BuildAsync_Includes_GitHub_And_Local_Estimates()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(new Dictionary<ReviewStatus, int>
            {
                [ReviewStatus.Unseen] = 4,
                [ReviewStatus.Adopted] = 2,
                [ReviewStatus.Rejected] = 1,
            }));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([
                MakeCommit("sha-1"),
                MakeCommit("sha-2"),
                MakeCommit("sha-3"),
            ]));
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([
                new() { Pattern = "content/copilot/**", CreatedAt = DateTime.UtcNow },
                new() { Pattern = "data/release-notes/**", CreatedAt = DateTime.UtcNow },
            ]));
        var docs = Substitute.For<IDocsGitHubClient>();
        docs.EstimateTriageAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocsGitHubTriageEstimate(5, 7)));

        var sut = new TriagePreflightSummaryBuilder(repo, docs, MakeOptions());

        var summary = await sut.BuildAsync(includeGitHubEstimate: true, ct);

        Assert.Equal("github", summary.GitHubSettings.Owner);
        Assert.Equal("docs", summary.GitHubSettings.Repo);
        Assert.Equal(TriagePreflightGitHubEstimateStatus.Succeeded, summary.GitHubEstimateStatus);
        Assert.Equal(5, summary.CandidatePullRequestCount);
        Assert.Equal(7, summary.NewUnseenCommitCount);
        Assert.Equal(3, summary.UnscoredUnreviewedCommitCount);
        Assert.Equal(10, summary.EstimatedScoringTargetCount);
        Assert.Equal(2, summary.IgnoreRuleCount);
        Assert.Equal(4, summary.ReviewCounts[ReviewStatus.Unseen]);
        Assert.Equal(0, summary.ReviewCounts[ReviewStatus.Later]);
        await docs.DidNotReceive().GetCommitFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildAsync_When_GitHub_Estimate_Fails_Still_Returns_Local_Counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(new Dictionary<ReviewStatus, int>
            {
                [ReviewStatus.Unseen] = 6,
            }));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([MakeCommit("sha-1"), MakeCommit("sha-2")]));
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));
        var docs = Substitute.For<IDocsGitHubClient>();
        docs.EstimateTriageAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("GitHub rate limit"));

        var sut = new TriagePreflightSummaryBuilder(repo, docs, MakeOptions());

        var summary = await sut.BuildAsync(includeGitHubEstimate: true, ct);

        Assert.Equal(TriagePreflightGitHubEstimateStatus.Failed, summary.GitHubEstimateStatus);
        Assert.Null(summary.CandidatePullRequestCount);
        Assert.Null(summary.NewUnseenCommitCount);
        Assert.Contains("GitHub rate limit", summary.GitHubEstimateUnavailableReason, StringComparison.Ordinal);
        Assert.Equal(2, summary.UnscoredUnreviewedCommitCount);
        Assert.Equal(2, summary.EstimatedScoringTargetCount);
        Assert.Equal(0, summary.IgnoreRuleCount);
    }

    [Fact]
    public async Task BuildAsync_When_GitHub_Estimate_Skipped_Does_Not_Call_GitHub()
    {
        var ct = TestContext.Current.CancellationToken;
        var repo = Substitute.For<IRadarRepository>();
        repo.GetReviewCountsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<ReviewStatus, int>>(new Dictionary<ReviewStatus, int>()));
        repo.QueryCommitsAsync(Arg.Any<CommitQueryFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Commit>>([MakeCommit("sha-1")]));
        repo.GetIgnoreRulesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IgnoreRule>>([]));
        var docs = Substitute.For<IDocsGitHubClient>();

        var sut = new TriagePreflightSummaryBuilder(repo, docs, MakeOptions());

        var summary = await sut.BuildAsync(includeGitHubEstimate: false, ct);

        Assert.Equal(TriagePreflightGitHubEstimateStatus.Skipped, summary.GitHubEstimateStatus);
        Assert.Null(summary.CandidatePullRequestCount);
        Assert.Null(summary.NewUnseenCommitCount);
        Assert.Equal(1, summary.EstimatedScoringTargetCount);
        await docs.DidNotReceive().EstimateTriageAsync(Arg.Any<CancellationToken>());
    }

    private static IOptions<GitHubOptions> MakeOptions()
        => Microsoft.Extensions.Options.Options.Create(new GitHubOptions
        {
            Owner = "github",
            Repo = "docs",
            PullRequestTitleFilter = "Repo sync",
            MaxPullRequests = 5,
            PullRequestCreatedAtOrAfter = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        });

    private static Commit MakeCommit(string sha)
        => new()
        {
            Sha = sha,
            PrNumber = 1,
            Message = $"commit {sha}",
            Author = "octocat",
            AuthoredAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };
}
