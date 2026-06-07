using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

public sealed class OfficialDocsUrlResolverTests
{
    [Fact]
    public async Task LoadAsync_Returns_Empty_When_Commit_Has_No_Files()
    {
        using var fixture = new SqliteFixture();
        await using var db = fixture.CreateContext();
        db.Database.Migrate();
        var commit = CreateCommit("empty");

        var urls = await OfficialDocsUrlResolver.LoadAsync(db, commit, TestContext.Current.CancellationToken);

        Assert.Empty(urls);
    }

    [Fact]
    public async Task LoadAsync_Converts_Relative_PathUrlMap_To_Absolute_Docs_Url()
    {
        using var fixture = new SqliteFixture();
        await using var db = fixture.CreateContext();
        db.Database.Migrate();
        var commit = CreateCommit("mapped", "content/copilot/about.md");
        db.PathUrlMaps.Add(new PathUrlMap
        {
            Path = "content/copilot/about.md",
            Version = "fpt",
            Language = "en",
            Url = "/en/copilot/about-copilot",
            ResolvedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var urls = await OfficialDocsUrlResolver.LoadAsync(db, commit, TestContext.Current.CancellationToken);

        Assert.Contains("https://docs.github.com/en/copilot/about-copilot", urls);
    }

    [Fact]
    public async Task LoadAsync_Rejects_Non_Docs_Domain_Mapped_Urls_And_Uses_Fallback()
    {
        using var fixture = new SqliteFixture();
        await using var db = fixture.CreateContext();
        db.Database.Migrate();
        var commit = CreateCommit("external", "content/actions/index.md");
        db.PathUrlMaps.Add(new PathUrlMap
        {
            Path = "content/actions/index.md",
            Version = "fpt",
            Language = "en",
            Url = "https://example.com/actions",
            ResolvedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var urls = await OfficialDocsUrlResolver.LoadAsync(db, commit, TestContext.Current.CancellationToken);

        var url = Assert.Single(urls);
        Assert.Equal("https://docs.github.com/en/actions", url);
    }

    [Fact]
    public async Task LoadAsync_Normalizes_Index_Suffix_From_Mapped_Urls()
    {
        using var fixture = new SqliteFixture();
        await using var db = fixture.CreateContext();
        db.Database.Migrate();
        var commit = CreateCommit("index", "content/actions/reference/index.md");
        db.PathUrlMaps.Add(new PathUrlMap
        {
            Path = "content/actions/reference/index.md",
            Version = "fpt",
            Language = "en",
            Url = "https://docs.github.com/en/actions/reference/index",
            ResolvedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var urls = await OfficialDocsUrlResolver.LoadAsync(db, commit, TestContext.Current.CancellationToken);

        Assert.Contains("https://docs.github.com/en/actions/reference", urls);
    }

    [Fact]
    public async Task LoadAsync_Prefers_Japanese_Fpt_Map_And_Limits_To_Five_Urls()
    {
        using var fixture = new SqliteFixture();
        await using var db = fixture.CreateContext();
        db.Database.Migrate();
        var commit = CreateCommit(
            "many",
            "content/doc0.md",
            "content/doc1.md",
            "content/doc2.md",
            "content/doc3.md",
            "content/doc4.md",
            "content/doc5.md");
        db.PathUrlMaps.AddRange(
            new PathUrlMap
            {
                Path = "content/doc0.md",
                Version = "ghes-3.17",
                Language = "en",
                Url = "/en/enterprise-server@3.17/doc0",
                ResolvedAt = DateTime.UtcNow,
            },
            new PathUrlMap
            {
                Path = "content/doc0.md",
                Version = "fpt",
                Language = "ja",
                Url = "/ja/doc0",
                ResolvedAt = DateTime.UtcNow,
            });
        for (var i = 1; i <= 5; i++)
        {
            db.PathUrlMaps.Add(new PathUrlMap
            {
                Path = $"content/doc{i}.md",
                Version = "fpt",
                Language = "en",
                Url = $"/en/doc{i}",
                ResolvedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var urls = await OfficialDocsUrlResolver.LoadAsync(db, commit, TestContext.Current.CancellationToken);

        Assert.Equal(5, urls.Count);
        Assert.Equal("https://docs.github.com/ja/doc0", urls[0]);
    }

    [Fact]
    public void BuildFallbackUrls_Returns_Empty_For_Non_Content_Paths()
    {
        var commit = CreateCommit("data", "data/release-notes/index.yml");

        var urls = OfficialDocsUrlResolver.BuildFallbackUrls(commit);

        Assert.Empty(urls);
    }

    private static Commit CreateCommit(string sha, params string[] paths)
        => new()
        {
            Sha = sha,
            PrNumber = 1,
            Message = "test",
            Author = "octo",
            AuthoredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FetchedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Files = paths.Select(path => new CommitFile
            {
                Sha = sha,
                Path = path,
                Status = "modified",
                Additions = 1,
                Deletions = 0,
            }).ToList(),
        };

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
