using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.App.Copilot.Tools;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.App.Tests.Copilot.Tools;

/// <summary>
/// Temp-file SQLite harness used by <see cref="WriteToolsTests"/>. Mirrors the production
/// <see cref="IDbContextFactory{RadarDbContext}"/> registration so concurrent contexts share
/// a real on-disk database file (rather than the cross-process-unfriendly <c>:memory:</c>).
/// </summary>
internal sealed class WriteHarness : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    private WriteHarness(string dbPath)
    {
        _dbPath = dbPath;
        _connectionString = $"Data Source={dbPath};Pooling=False";
    }

    public static async Task<WriteHarness> CreateAsync(CancellationToken cancellationToken)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"reposyncradar-write-{Guid.NewGuid():N}.db");
        var harness = new WriteHarness(dbPath);
        var options = new DbContextOptionsBuilder<RadarDbContext>().UseSqlite(harness._connectionString).Options;
        await using var db = new RadarDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
        return harness;
    }

    /// <summary>Lazy creator that does not migrate the DB; useful for tests that only inspect tool metadata.</summary>
    public static WriteHarness CreateLazy()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"reposyncradar-write-lazy-{Guid.NewGuid():N}.db");
        return new WriteHarness(dbPath);
    }

    public RadarWriteTools CreateTools()
        => new(new ConnectionStringDbContextFactory(_connectionString));

    public IDbContextFactory<RadarDbContext> DbFactory
        => new ConnectionStringDbContextFactory(_connectionString);

    public RadarDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RadarDbContext>().UseSqlite(_connectionString).Options;
        return new RadarDbContext(options);
    }

    public async Task InsertCommitAsync(string sha, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        db.Commits.Add(new Commit
        {
            Sha = sha,
            PrNumber = 1,
            Message = "msg",
            Author = "octo",
            AuthoredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task InsertReviewedCommitAsync(
        string sha,
        ReviewStatus status,
        string? message = null,
        DateTime? reviewedAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = CreateDb();
        db.Commits.Add(new Commit
        {
            Sha = sha,
            PrNumber = 1,
            Message = message ?? $"msg-{sha}",
            Author = "octo",
            AuthoredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Review = new Review
            {
                Sha = sha,
                Status = status,
                ReviewedAt = reviewedAtUtc ?? DateTime.UtcNow,
            },
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
        return ValueTask.CompletedTask;
    }

    private sealed class ConnectionStringDbContextFactory(string connectionString) : IDbContextFactory<RadarDbContext>
    {
        public RadarDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<RadarDbContext>().UseSqlite(connectionString).Options;
            return new RadarDbContext(options);
        }
    }
}
