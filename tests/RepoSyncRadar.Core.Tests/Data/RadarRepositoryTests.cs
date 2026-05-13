using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Data;

/// <summary>
/// Tests for <see cref="RadarRepository"/>. Each test owns its own in-memory SQLite
/// connection via <see cref="SqliteFixture"/>; the migration is applied once per fixture
/// so the table schema is identical to a real <c>radar.db</c>.
/// </summary>
public sealed class RadarRepositoryTests
{
    private static readonly string[] BulkShas = ["sha-a", "sha-b", "sha-c"];
    private static readonly string[] OnlyShaB = ["sha-b"];
    private static readonly string[] KnownIntersectionInput = ["sha-known", "sha-new-1", "sha-new-2"];

    [Fact]
    public async Task UpsertCommitsAsync_Inserts_New()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        var inserted = await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("sha-a", prNumber: 1),
                MakeCommit("sha-b", prNumber: 1),
                MakeCommit("sha-c", prNumber: 2),
            },
            ct);

        Assert.Equal(BulkShas, inserted.ToArray());

        using var verify = fixture.CreateContext();
        Assert.Equal(3, verify.Commits.Count());
        Assert.Equal(
            BulkShas,
            verify.Commits.OrderBy(c => c.Sha).Select(c => c.Sha).ToArray());
    }

    [Fact]
    public async Task UpsertCommitsAsync_Skips_Existing()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        var firstFetch = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var secondFetch = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc);

        await repository.UpsertCommitsAsync(
            new[] { MakeCommit("sha-a", prNumber: 1, fetchedAt: firstFetch, message: "first") },
            ct);

        var inserted = await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("sha-a", prNumber: 99, fetchedAt: secondFetch, message: "rewritten"),
                MakeCommit("sha-b", prNumber: 1, fetchedAt: secondFetch),
            },
            ct);

        Assert.Equal(OnlyShaB, inserted.ToArray());

        using var verify = fixture.CreateContext();
        var preserved = verify.Commits.Single(c => c.Sha == "sha-a");
        Assert.Equal(firstFetch, preserved.FetchedAt);
        Assert.Equal(1, preserved.PrNumber);
        Assert.Equal("first", preserved.Message);
        Assert.Equal(2, verify.Commits.Count());
    }

    [Fact]
    public async Task UpsertCommitsAsync_Persists_Files()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        var commit = MakeCommit("sha-a", prNumber: 1);
        commit.Files.Add(new CommitFile
        {
            Sha = "sha-a",
            Path = "content/get-started/index.md",
            Status = "modified",
            Additions = 3,
            Deletions = 1,
        });
        commit.Files.Add(new CommitFile
        {
            Sha = "sha-a",
            Path = "data/release-notes/index.yml",
            Status = "added",
            Additions = 10,
            Deletions = 0,
        });

        await repository.UpsertCommitsAsync(new[] { commit }, ct);

        using var verify = fixture.CreateContext();
        var persistedFiles = verify.CommitFiles
            .Where(f => f.Sha == "sha-a")
            .OrderBy(f => f.Path)
            .ToList();
        Assert.Equal(2, persistedFiles.Count);
        Assert.Equal("content/get-started/index.md", persistedFiles[0].Path);
        Assert.Equal("modified", persistedFiles[0].Status);
        Assert.Equal(3, persistedFiles[0].Additions);
        Assert.Equal("data/release-notes/index.yml", persistedFiles[1].Path);
        Assert.Equal("added", persistedFiles[1].Status);
        Assert.Equal(10, persistedFiles[1].Additions);
    }

    [Fact]
    public async Task GetKnownShasAsync_Returns_Intersection()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        await repository.UpsertCommitsAsync(
            new[] { MakeCommit("sha-known", prNumber: 1) },
            ct);

        var known = await repository.GetKnownShasAsync(
            KnownIntersectionInput,
            ct);

        Assert.Single(known);
        Assert.Contains("sha-known", known);
        Assert.DoesNotContain("sha-new-1", known);
        Assert.DoesNotContain("sha-new-2", known);
    }

    [Fact]
    public async Task SetReviewAsync_Creates_When_Missing()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        await repository.UpsertCommitsAsync(
            new[] { MakeCommit("sha-a", prNumber: 1) },
            ct);

        await repository.SetReviewAsync("sha-a", ReviewStatus.Adopted, "shipped", ct);

        using var verify = fixture.CreateContext();
        var review = verify.Reviews.Single(r => r.Sha == "sha-a");
        Assert.Equal(ReviewStatus.Adopted, review.Status);
        Assert.Equal("shipped", review.Reason);
        Assert.NotNull(review.ReviewedAt);
    }

    [Fact]
    public async Task SetReviewAsync_Updates_When_Present()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        await repository.UpsertCommitsAsync(
            new[] { MakeCommit("sha-a", prNumber: 1) },
            ct);
        await repository.SetReviewAsync("sha-a", ReviewStatus.Seen, null, ct);

        await repository.SetReviewAsync("sha-a", ReviewStatus.Rejected, "off-topic", ct);

        using var verify = fixture.CreateContext();
        Assert.Equal(1, verify.Reviews.Count(r => r.Sha == "sha-a"));
        var review = verify.Reviews.Single(r => r.Sha == "sha-a");
        Assert.Equal(ReviewStatus.Rejected, review.Status);
        Assert.Equal("off-topic", review.Reason);
    }

    private static Commit MakeCommit(
        string sha,
        int prNumber,
        DateTime? authoredAt = null,
        DateTime? fetchedAt = null,
        string? message = null)
    {
        var now = new DateTime(2026, 5, 13, 12, 0, 0, DateTimeKind.Utc);
        return new Commit
        {
            Sha = sha,
            PrNumber = prNumber,
            Message = message ?? $"commit {sha}",
            Author = "octocat",
            AuthoredAt = authoredAt ?? now,
            FetchedAt = fetchedAt ?? now,
        };
    }

    /// <summary>
    /// Owns a single in-memory SQLite connection and exposes both raw
    /// <see cref="RadarDbContext"/> instances (for verification) and a
    /// <see cref="RadarRepository"/> bound to a matching <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    private sealed class SqliteFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<RadarDbContext> _options;

        public SqliteFixture()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<RadarDbContext>()
                .UseSqlite(_connection)
                .Options;

            using var bootstrap = new RadarDbContext(_options);
            bootstrap.Database.Migrate();
        }

        public RadarDbContext CreateContext() => new(_options);

        public RadarRepository CreateRepository()
            => new(new TestDbContextFactory(_options));

        public void Dispose() => _connection.Dispose();
    }

    private sealed class TestDbContextFactory(DbContextOptions<RadarDbContext> options)
        : IDbContextFactory<RadarDbContext>
    {
        public RadarDbContext CreateDbContext() => new(options);
    }
}
