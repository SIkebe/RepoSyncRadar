using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="CommitIngestionService"/>. Both <see cref="IDocsGitHubClient"/> and
/// <see cref="IRadarRepository"/> are stubbed so the suite stays free of file I/O and HTTP.
/// </summary>
public sealed class CommitIngestionServiceTests
{
    private static readonly string[] BothCandidateShas = ["sha-known", "sha-new"];
    private static readonly string[] OnlyShaNew = ["sha-new"];

    [Fact]
    public async Task IngestAsync_Persists_Only_New()
    {
        var docs = Substitute.For<IDocsGitHubClient>();
        var repo = Substitute.For<IRadarRepository>();
        var ct = TestContext.Current.CancellationToken;

        var known = MakeCommit("sha-known", prNumber: 1);
        var fresh = MakeCommit("sha-new", prNumber: 2);
        docs.FetchUnseenCommitsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Commit>)new[] { known, fresh });

        repo.GetKnownShasAsync(
                Arg.Is<IEnumerable<string>>(s => s.SequenceEqual(BothCandidateShas)),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "sha-known" });

        docs.GetCommitFilesAsync("sha-new", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CommitFile>)new[]
            {
                new CommitFile { Sha = "sha-new", Path = "content/x.md", Status = "modified", Additions = 2, Deletions = 1 },
            });

        var sut = new CommitIngestionService(docs, repo);
        await sut.IngestAsync(ct);

        // GetCommitFilesAsync must only be called for the genuinely new SHA.
        await docs.Received(1).GetCommitFilesAsync("sha-new", Arg.Any<CancellationToken>());
        await docs.DidNotReceive().GetCommitFilesAsync("sha-known", Arg.Any<CancellationToken>());

        await repo.Received(1).UpsertCommitsAsync(
            Arg.Is<IEnumerable<Commit>>(seq => seq.Select(c => c.Sha).SequenceEqual(OnlyShaNew)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_Counts_Returned_Correctly()
    {
        var docs = Substitute.For<IDocsGitHubClient>();
        var repo = Substitute.For<IRadarRepository>();
        var ct = TestContext.Current.CancellationToken;

        docs.FetchUnseenCommitsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Commit>)new[]
            {
                MakeCommit("sha-1", prNumber: 1),
                MakeCommit("sha-2", prNumber: 1),
                MakeCommit("sha-3", prNumber: 2),
            });

        repo.GetKnownShasAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal) { "sha-2" });

        docs.GetCommitFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CommitFile>)Array.Empty<CommitFile>());

        repo.UpsertCommitsAsync(Arg.Any<IEnumerable<Commit>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var batch = callInfo.Arg<IEnumerable<Commit>>().ToList();
                return (IReadOnlyList<string>)batch.Select(c => c.Sha).ToList();
            });

        var sut = new CommitIngestionService(docs, repo);
        var report = await sut.IngestAsync(ct);

        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Inserted);
        Assert.Equal(1, report.Skipped);
    }

    [Fact]
    public async Task IngestAsync_Reports_Each_Inserted_Commit_After_It_Is_Saved()
    {
        var docs = Substitute.For<IDocsGitHubClient>();
        var repo = Substitute.For<IRadarRepository>();
        var progress = new CapturingProgress();
        var ct = TestContext.Current.CancellationToken;

        docs.FetchUnseenCommitsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Commit>)new[]
            {
                MakeCommit("sha-1", prNumber: 1),
                MakeCommit("sha-2", prNumber: 1),
            });
        repo.GetKnownShasAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal));
        docs.GetCommitFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CommitFile>)Array.Empty<CommitFile>());
        repo.UpsertCommitsAsync(Arg.Any<IEnumerable<Commit>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var commit = Assert.Single(callInfo.Arg<IEnumerable<Commit>>());
                return (IReadOnlyList<string>)[commit.Sha];
            });

        var sut = new CommitIngestionService(docs, repo);
        var report = await sut.IngestAsync(progress, ct);

        Assert.Equal(2, report.Inserted);
        Assert.Collection(progress.Values,
            first =>
            {
                Assert.Equal(2, first.Total);
                Assert.Equal(1, first.Processed);
                Assert.Equal(1, first.Inserted);
                Assert.Equal("sha-1", first.InsertedSha);
            },
            second =>
            {
                Assert.Equal(2, second.Total);
                Assert.Equal(2, second.Processed);
                Assert.Equal(2, second.Inserted);
                Assert.Equal("sha-2", second.InsertedSha);
            });
        await repo.Received(1).UpsertCommitsAsync(
            Arg.Is<IEnumerable<Commit>>(commits => commits.Single().Sha == "sha-1"),
            Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertCommitsAsync(
            Arg.Is<IEnumerable<Commit>>(commits => commits.Single().Sha == "sha-2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_Respects_CancellationToken()
    {
        var docs = Substitute.For<IDocsGitHubClient>();
        var repo = Substitute.For<IRadarRepository>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        docs.FetchUnseenCommitsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new InvalidOperationException("unreachable");
            });

        var sut = new CommitIngestionService(docs, repo);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.IngestAsync(cts.Token));

        await repo.DidNotReceive().UpsertCommitsAsync(
            Arg.Any<IEnumerable<Commit>>(),
            Arg.Any<CancellationToken>());
    }

    private static Commit MakeCommit(string sha, int prNumber)
    {
        var now = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc);
        return new Commit
        {
            Sha = sha,
            PrNumber = prNumber,
            Message = $"commit {sha}",
            Author = "octocat",
            AuthoredAt = now,
            FetchedAt = now,
        };
    }

    private sealed class CapturingProgress : IProgress<CommitIngestionProgress>
    {
        public List<CommitIngestionProgress> Values { get; } = [];

        public void Report(CommitIngestionProgress value) => Values.Add(value);
    }
}
