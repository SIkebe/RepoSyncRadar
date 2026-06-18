using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Data;

/// <summary>
/// Schema-level tests for <see cref="RadarDbContext"/>. Each test owns its own in-memory
/// SQLite connection so that <c>db.Database.Migrate()</c> can run end-to-end without
/// touching a physical file.
/// </summary>
public sealed class RadarDbContextTests
{
    [Fact]
    public void Migrate_Creates_All_Tables()
    {
        using var fixture = new SqliteFixture();
        using var db = fixture.CreateContext();

        db.Database.Migrate();

        // Querying each DbSet must succeed (i.e. the underlying table exists).
        Assert.Empty(db.Commits.ToList());
        Assert.Empty(db.CommitFiles.ToList());
        Assert.Empty(db.Scorings.ToList());
        Assert.Empty(db.Reviews.ToList());
        Assert.Empty(db.ReviewHistories.ToList());
        Assert.Empty(db.Drafts.ToList());
        Assert.Empty(db.PathUrlMaps.ToList());
        Assert.Empty(db.IgnoreRules.ToList());
        Assert.Empty(db.BoostRules.ToList());
        Assert.Empty(db.CopilotToolLogs.ToList());

        var columns = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('CommitFiles')")
            .ToList();
        Assert.Contains("ViewedAt", columns);
    }

    [Fact]
    public void Commit_Cascade_Deletes_Children()
    {
        using var fixture = new SqliteFixture();
        using (var db = fixture.CreateContext())
        {
            db.Database.Migrate();

            var commit = new Commit
            {
                Sha = "abc",
                PrNumber = 1,
                Message = "test",
                Author = "alice",
                AuthoredAt = DateTime.UtcNow,
                FetchedAt = DateTime.UtcNow,
                Files =
                {
                    new CommitFile { Sha = "abc", Path = "content/x.md", Status = "modified", Additions = 1, Deletions = 0 },
                },
                Scoring = new Scoring { Sha = "abc", Score = 0.5, Category = "feature", ScoredAt = DateTime.UtcNow },
                Review = new Review { Sha = "abc", Status = ReviewStatus.Seen },
                ReviewHistory =
                {
                    new ReviewHistory
                    {
                        Sha = "abc",
                        Status = ReviewStatus.Seen,
                        ChangedAt = DateTime.UtcNow,
                        Source = ReviewHistorySources.User,
                    },
                },
                Drafts =
                {
                    new Draft { Sha = "abc", Channel = "twitter", Body = "hi", GeneratedAt = DateTime.UtcNow },
                },
            };
            db.Commits.Add(commit);
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            var commit = db.Commits.Single(c => c.Sha == "abc");
            db.Commits.Remove(commit);
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            Assert.Empty(db.Commits.ToList());
            Assert.Empty(db.CommitFiles.ToList());
            Assert.Empty(db.Scorings.ToList());
            Assert.Empty(db.Reviews.ToList());
            Assert.Empty(db.ReviewHistories.ToList());
            Assert.Empty(db.Drafts.ToList());
        }
    }

    [Fact]
    public void Review_Status_Roundtrip()
    {
        using var fixture = new SqliteFixture();
        using (var db = fixture.CreateContext())
        {
            db.Database.Migrate();
            db.Commits.Add(new Commit
            {
                Sha = "deadbeef",
                PrNumber = 2,
                Message = "m",
                Author = "a",
                AuthoredAt = DateTime.UtcNow,
                FetchedAt = DateTime.UtcNow,
                Review = new Review { Sha = "deadbeef", Status = ReviewStatus.Adopted, ReviewedAt = DateTime.UtcNow },
            });
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            var review = db.Reviews.Single(r => r.Sha == "deadbeef");
            Assert.Equal(ReviewStatus.Adopted, review.Status);

            // Status column must be persisted as TEXT (string), not an integer enum value.
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Status FROM Reviews WHERE Sha = 'deadbeef'";
            var raw = cmd.ExecuteScalar();
            Assert.Equal("Adopted", raw as string);
        }
    }

    [Fact]
    public void PathUrlMap_Composite_Key_Unique()
    {
        using var fixture = new SqliteFixture();
        using (var db = fixture.CreateContext())
        {
            db.Database.Migrate();
            db.PathUrlMaps.Add(new PathUrlMap
            {
                Path = "content/a.md",
                Version = "fpt",
                Language = "en",
                Url = "https://docs.github.com/en/a",
                ResolvedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            db.PathUrlMaps.Add(new PathUrlMap
            {
                Path = "content/a.md",
                Version = "fpt",
                Language = "en",
                Url = "https://docs.github.com/en/a-duplicate",
                ResolvedAt = DateTime.UtcNow,
            });

            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void IgnoreRule_Pattern_Unique()
    {
        using var fixture = new SqliteFixture();
        using (var db = fixture.CreateContext())
        {
            db.Database.Migrate();
            db.IgnoreRules.Add(new IgnoreRule { Pattern = "data/release-notes/**", CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            db.IgnoreRules.Add(new IgnoreRule { Pattern = "data/release-notes/**", CreatedAt = DateTime.UtcNow });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void CopilotToolLog_Auto_Id()
    {
        using var fixture = new SqliteFixture();
        using var db = fixture.CreateContext();
        db.Database.Migrate();

        var log = new CopilotToolLog
        {
            SessionId = "s1",
            ToolName = "radar_list_commits",
            ArgsJson = "{}",
            ResultJson = "{}",
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow,
        };
        db.CopilotToolLogs.Add(log);
        db.SaveChanges();

        Assert.True(log.Id > 0, $"Expected auto-generated Id, but got {log.Id}.");
    }

    /// <summary>
    /// Owns a single in-memory SQLite connection so that the underlying database
    /// survives across <see cref="RadarDbContext"/> instances created from the same fixture.
    /// </summary>
    private sealed class SqliteFixture : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SqliteFixture()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
        }

        public RadarDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<RadarDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new RadarDbContext(options);
        }

        public void Dispose() => _connection.Dispose();
    }
}
