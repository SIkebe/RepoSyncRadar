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
    private static readonly string[] _bulkShas = ["sha-a", "sha-b", "sha-c"];
    private static readonly string[] _onlyShaB = ["sha-b"];
    private static readonly string[] _knownIntersectionInput = ["sha-known", "sha-new-1", "sha-new-2"];

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

        Assert.Equal(_bulkShas, inserted.ToArray());

        using var verify = fixture.CreateContext();
        Assert.Equal(3, verify.Commits.Count());
        Assert.Equal(
            _bulkShas,
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

        Assert.Equal(_onlyShaB, inserted.ToArray());

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
            _knownIntersectionInput,
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

        await repository.SetReviewAsync("sha-a", ReviewStatus.Archived, "off-topic", ct);

        using var verify = fixture.CreateContext();
        Assert.Equal(1, verify.Reviews.Count(r => r.Sha == "sha-a"));
        var review = verify.Reviews.Single(r => r.Sha == "sha-a");
        Assert.Equal(ReviewStatus.Archived, review.Status);
        Assert.Equal("off-topic", review.Reason);
    }

    [Fact]
    public async Task QueryCommitsAsync_Filters_By_Status_And_Limit()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        // Four commits, two of which become Adopted; unresolved includes missing Review and legacy Seen rows.
        var baseTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("sha-unseen", prNumber: 1, authoredAt: baseTime),
                MakeCommit("sha-seen", prNumber: 1, authoredAt: baseTime.AddHours(12)),
                MakeCommit("sha-adopt-old", prNumber: 1, authoredAt: baseTime.AddDays(1)),
                MakeCommit("sha-adopt-new", prNumber: 1, authoredAt: baseTime.AddDays(2)),
            },
            ct);
        await repository.SetReviewAsync("sha-seen", ReviewStatus.Seen, null, ct);
        await repository.SetReviewAsync("sha-adopt-old", ReviewStatus.Adopted, null, ct);
        await repository.SetReviewAsync("sha-adopt-new", ReviewStatus.Adopted, null, ct);

        // Adopted, ordered by AuthoredAt desc, limited to 1 → only the newest Adopted row.
        var adopted = await repository.QueryCommitsAsync(
            new CommitQueryFilter { Status = ReviewStatus.Adopted, Limit = 1 },
            ct);

        Assert.Single(adopted);
        Assert.Equal("sha-adopt-new", adopted[0].Sha);

        // Unseen returns both missing Review rows and legacy Seen rows.
        var unseen = await repository.QueryCommitsAsync(
            new CommitQueryFilter { Status = ReviewStatus.Unseen },
            ct);
        Assert.Equal(["sha-seen", "sha-unseen"], unseen.Select(c => c.Sha).ToArray());

        // No filter returns all four, newest first.
        var all = await repository.QueryCommitsAsync(new CommitQueryFilter(), ct);
        Assert.Equal(["sha-adopt-new", "sha-adopt-old", "sha-seen", "sha-unseen"], all.Select(c => c.Sha).ToArray());
    }

    [Fact]
    public async Task QueryCommitsAsync_Filters_By_Search_Query()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;
        var baseTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("abc1234abc1234abc1234abc1234abc1234abc1", prNumber: 42, message: "docs: setup intro", authoredAt: baseTime),
                MakeCommit("bbb2222bbb2222bbb2222bbb2222bbb2222bbb2", prNumber: 77, message: "docs: workspace guide", authoredAt: baseTime.AddDays(1)),
                MakeCommit("abc9999abc9999abc9999abc9999abc9999abc9", prNumber: 88, message: "docs: enterprise guide", authoredAt: baseTime.AddDays(2)),
            },
            ct);
        await repository.SetReviewAsync("abc9999abc9999abc9999abc9999abc9999abc9", ReviewStatus.Adopted, null, ct);

        var unseenMatches = await repository.QueryCommitsAsync(
            new CommitQueryFilter { Status = ReviewStatus.Unseen, ShaQuery = "ABC1234" },
            ct);

        var allMatches = await repository.QueryCommitsAsync(
            new CommitQueryFilter { ShaQuery = "abc" },
            ct);

        var prMatches = await repository.QueryCommitsAsync(
            new CommitQueryFilter { ShaQuery = "#77" },
            ct);

        var messageMatches = await repository.QueryCommitsAsync(
            new CommitQueryFilter { ShaQuery = "WORKSPACE" },
            ct);

        Assert.Equal(["abc1234abc1234abc1234abc1234abc1234abc1"], unseenMatches.Select(c => c.Sha).ToArray());
        Assert.Equal(
            ["abc9999abc9999abc9999abc9999abc9999abc9", "abc1234abc1234abc1234abc1234abc1234abc1"],
            allMatches.Select(c => c.Sha).ToArray());
        Assert.Equal(["bbb2222bbb2222bbb2222bbb2222bbb2222bbb2"], prMatches.Select(c => c.Sha).ToArray());
        Assert.Equal(["bbb2222bbb2222bbb2222bbb2222bbb2222bbb2"], messageMatches.Select(c => c.Sha).ToArray());
    }

    [Fact]
    public async Task QueryCommitsAsync_Includes_Scoring_When_Present()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        await repository.UpsertCommitsAsync(
            new[] { MakeCommit("sha-with-score", prNumber: 1) },
            ct);

        // Seed a Scoring row out-of-band; the repository surfaces it via Include.
        using (var seed = fixture.CreateContext())
        {
            seed.Scorings.Add(new Scoring
            {
                Sha = "sha-with-score",
                Score = 0.81,
                Category = "feature-update",
                AudienceJson = "[\"devrel\"]",
                SummaryJa = "要約",
                WhyJa = "理由",
                DetailsJa = "詳細",
                Model = "gpt-5",
                ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync(ct);
        }

        var commits = await repository.QueryCommitsAsync(new CommitQueryFilter(), ct);

        var commit = Assert.Single(commits);
        Assert.NotNull(commit.Scoring);
        Assert.Equal(0.81, commit.Scoring!.Score);
        Assert.Equal("feature-update", commit.Scoring.Category);
        Assert.Equal("要約", commit.Scoring.SummaryJa);
        Assert.Equal("詳細", commit.Scoring.DetailsJa);
    }

    [Fact]
    public async Task DeleteUnseenCommitsAsync_Removes_Only_Unseen_And_Cascades_Local_Rows()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("sha-missing-review", prNumber: 1),
                MakeCommit("sha-unseen", prNumber: 1),
                MakeCommit("sha-seen", prNumber: 1),
                MakeCommit("sha-adopted", prNumber: 1),
            },
            ct);
        await repository.SetReviewAsync("sha-unseen", ReviewStatus.Unseen, null, ct);
        await repository.SetReviewAsync("sha-seen", ReviewStatus.Seen, null, ct);
        await repository.SetReviewAsync("sha-adopted", ReviewStatus.Adopted, null, ct);

        using (var seed = fixture.CreateContext())
        {
            seed.Scorings.Add(new Scoring
            {
                Sha = "sha-unseen",
                Score = 0.75,
                Category = "feature-update",
                AudienceJson = "[]",
                SummaryJa = "要約",
                WhyJa = "理由",
                DetailsJa = "詳細",
                Model = "gpt-5",
                ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            });
            seed.CommitFiles.Add(new CommitFile
            {
                Sha = "sha-unseen",
                Path = "content/unseen.md",
                Status = "modified",
                Additions = 1,
                Deletions = 0,
            });
            seed.Drafts.Add(new Draft
            {
                Sha = "sha-unseen",
                Channel = "teams",
                Body = "draft",
                GeneratedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync(ct);
        }

        var deleted = await repository.DeleteUnseenCommitsAsync(
            ["sha-missing-review", "sha-unseen", "sha-seen", "sha-adopted"],
            ct);

        Assert.Equal(3, deleted);
        using var verify = fixture.CreateContext();
        Assert.Equal(["sha-adopted"], verify.Commits.Select(static commit => commit.Sha).ToArray());
        Assert.Empty(verify.Scorings.Where(static scoring => scoring.Sha == "sha-unseen"));
        Assert.Empty(verify.CommitFiles.Where(static file => file.Sha == "sha-unseen"));
        Assert.Empty(verify.Drafts.Where(static draft => draft.Sha == "sha-unseen"));
        Assert.Equal(ReviewStatus.Adopted, verify.Reviews.Single().Status);
    }

    [Fact]
    public async Task QueryCommitsAsync_UnscoredOnly_Filters_Before_Limit()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;
        var baseTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("sha-unscored-old", prNumber: 1, authoredAt: baseTime),
                MakeCommit("sha-unscored-new", prNumber: 1, authoredAt: baseTime.AddDays(1)),
                MakeCommit("sha-scored-newest", prNumber: 1, authoredAt: baseTime.AddDays(2)),
            },
            ct);

        using (var seed = fixture.CreateContext())
        {
            seed.Scorings.Add(new Scoring
            {
                Sha = "sha-scored-newest",
                Score = 0.75,
                Category = "feature-update",
                AudienceJson = "[]",
                SummaryJa = "要約",
                WhyJa = "理由",
                DetailsJa = "詳細",
                Model = "gpt-5",
                ScoredAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync(ct);
        }

        var commits = await repository.QueryCommitsAsync(
            new CommitQueryFilter
            {
                Status = ReviewStatus.Unseen,
                Limit = 1,
                UnscoredOnly = true,
            },
            ct);

        var commit = Assert.Single(commits);
        Assert.Equal("sha-unscored-new", commit.Sha);
        Assert.Null(commit.Scoring);
    }

    [Fact]
    public async Task GetReviewCountsAsync_Counts_All_Buckets()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        await repository.UpsertCommitsAsync(
            new[]
            {
                MakeCommit("sha-1", prNumber: 1),
                MakeCommit("sha-2", prNumber: 1),
                MakeCommit("sha-3", prNumber: 1),
                MakeCommit("sha-4", prNumber: 1),
                MakeCommit("sha-5", prNumber: 1),
                MakeCommit("sha-6", prNumber: 1),
            },
            ct);
        await repository.SetReviewAsync("sha-2", ReviewStatus.Adopted, null, ct);
        await repository.SetReviewAsync("sha-3", ReviewStatus.Rejected, null, ct);
        await repository.SetReviewAsync("sha-4", ReviewStatus.Later, null, ct);
        await repository.SetReviewAsync("sha-5", ReviewStatus.Seen, null, ct);
        await repository.SetReviewAsync("sha-6", ReviewStatus.Archived, null, ct);

        var counts = await repository.GetReviewCountsAsync(ct);

        // sha-1 has no Review row, sha-5 is legacy Seen → both count toward Unseen.
        Assert.Equal(2, counts[ReviewStatus.Unseen]);
        Assert.Equal(0, counts[ReviewStatus.Seen]);
        Assert.Equal(1, counts[ReviewStatus.Adopted]);
        Assert.Equal(1, counts[ReviewStatus.Rejected]);
        Assert.Equal(1, counts[ReviewStatus.Archived]);
        Assert.Equal(1, counts[ReviewStatus.Later]);
    }

    [Fact]
    public async Task GetIgnoreRulesAsync_Returns_Rules_Newest_First()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        using (var seed = fixture.CreateContext())
        {
            seed.IgnoreRules.AddRange(
                new IgnoreRule
                {
                    Pattern = "data/release-notes/**",
                    Reason = "noisy",
                    CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc),
                },
                new IgnoreRule
                {
                    Pattern = "content/copilot/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc),
                });
            await seed.SaveChangesAsync(ct);
        }

        var rules = await repository.GetIgnoreRulesAsync(ct);

        Assert.Equal(["content/copilot/**", "data/release-notes/**"], rules.Select(rule => rule.Pattern).ToArray());
        Assert.Equal("ignore-directory", rules[0].Reason);
    }

    [Fact]
    public async Task DeleteIgnoreRulesAsync_Removes_Selected_Patterns()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.CreateRepository();
        var ct = TestContext.Current.CancellationToken;

        using (var seed = fixture.CreateContext())
        {
            seed.IgnoreRules.AddRange(
                new IgnoreRule
                {
                    Pattern = "data/release-notes/**",
                    Reason = "noisy",
                    CreatedAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc),
                },
                new IgnoreRule
                {
                    Pattern = "content/copilot/**",
                    Reason = "ignore-directory",
                    CreatedAt = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc),
                },
                new IgnoreRule
                {
                    Pattern = "content/actions/**",
                    Reason = "keep",
                    CreatedAt = new DateTime(2026, 5, 15, 9, 0, 0, DateTimeKind.Utc),
                });
            await seed.SaveChangesAsync(ct);
        }

        var deleted = await repository.DeleteIgnoreRulesAsync(
            [" content/copilot/** ", "data/release-notes/**", "missing/**", "content/copilot/**"],
            ct);

        Assert.Equal(2, deleted);
        var rules = await repository.GetIgnoreRulesAsync(ct);
        var rule = Assert.Single(rules);
        Assert.Equal("content/actions/**", rule.Pattern);
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
