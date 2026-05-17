using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// xUnit fixture that launches the App against a throwaway, fully seeded SQLite
/// database. Used by E2E regression tests that need to verify rendering of data
/// that is normally produced by the LLM (Scoring rows, Drafts rows). Without
/// seeding the App boots into an empty inbox and these surfaces never appear.
/// </summary>
/// <remarks>
/// The DB lives under <c>Path.GetTempPath()</c> so the developer's real
/// <c>%LOCALAPPDATA%\RepoSyncRadar\radar.db</c> stays untouched. The directory
/// is removed on dispose; if dispose races the App's WAL flush we swallow the
/// failure because the directory is in TEMP anyway.
/// </remarks>
public sealed class SeededAppHostFixture : IAsyncLifetime
{
    public const string SeededSha = "abc123abc123abc123abc123abc123abc123abc1";
    public const string SeededMessage = "docs: clarify Copilot Workspace docs";
    public const string SeededAuthor = "octocat";
    public const string SeededFilePath = "content/copilot/about-copilot.md";

    public const double SeededScore = 0.81;
    public const string SeededCategory = "feature-update";
    public const string SeededAudienceJson = "[\"devrel\",\"customer\"]";
    public const string SeededSummaryJa = "Copilot Workspace の挙動を明確化する変更。";
    public const string SeededWhyJa = "公式 docs の更新で、顧客向け説明にも影響するため重要。";
    public const string SeededDetailsJa = "変更内容: Copilot Workspace の説明を具体化。\n根拠: content/copilot/about-copilot.md の本文更新。\n影響: DevRel と顧客向け説明で参照しやすい。\n確認観点: 既存の GA/preview 表現と矛盾しないか確認。";

    public const string SeededTeamsBody = "【Teams 共有用 (E2E seed)】\n変更点: Copilot Workspace の説明を改訂。";
    public const string SeededTwitterBody = "Twitter 用本文 (E2E seed)";
    public const string SeededCustomerBody = "顧客向け本文 (E2E seed)";

    private string? _dbDir;
    private string? _dbPath;
    private AppHost? _host;
    private IPlaywright? _playwright;
    private IBrowser? _blazorBrowser;
    private IBrowser? _docsBrowser;

    public AppHost Host => _host
        ?? throw new InvalidOperationException("Fixture not initialized yet.");

    public IBrowser BlazorBrowser => _blazorBrowser
        ?? throw new InvalidOperationException("Fixture not initialized yet.");

    public IBrowser DocsBrowser => _docsBrowser
        ?? throw new InvalidOperationException("Fixture not initialized yet.");

    public async ValueTask InitializeAsync()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "RepoSyncRadar-E2E-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
        _dbPath = Path.Combine(_dbDir, "radar.db");

        await SeedAsync(_dbPath).ConfigureAwait(false);

        _host = await AppHost.StartAsync(_dbPath, AppHost.PreviewDisabledEnvironment).ConfigureAwait(false);
        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _blazorBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{_host.BlazorCdpPort}").ConfigureAwait(false);
        _docsBrowser = await _playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{_host.DocsCdpPort}").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_blazorBrowser is not null)
        {
            await _blazorBrowser.CloseAsync().ConfigureAwait(false);
        }
        if (_docsBrowser is not null)
        {
            await _docsBrowser.CloseAsync().ConfigureAwait(false);
        }
        _playwright?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
        }
        TryCleanupDb();
    }

    private static async Task SeedAsync(string dbPath)
    {
        var options = new DbContextOptionsBuilder<RadarDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var db = new RadarDbContext(options);
        await db.Database.MigrateAsync().ConfigureAwait(false);

        var authoredAt = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc);
        var fetchedAt = new DateTime(2026, 5, 13, 9, 1, 0, DateTimeKind.Utc);

        db.Commits.Add(new Commit
        {
            Sha = SeededSha,
            PrNumber = 1234,
            Message = SeededMessage,
            Author = SeededAuthor,
            AuthoredAt = authoredAt,
            FetchedAt = fetchedAt,
            Files = new List<CommitFile>
            {
                new()
                {
                    Sha = SeededSha,
                    Path = SeededFilePath,
                    Status = "modified",
                    Additions = 12,
                    Deletions = 3,
                },
            },
        });

        db.Scorings.Add(new Scoring
        {
            Sha = SeededSha,
            Score = SeededScore,
            Category = SeededCategory,
            AudienceJson = SeededAudienceJson,
            SummaryJa = SeededSummaryJa,
            WhyJa = SeededWhyJa,
            DetailsJa = SeededDetailsJa,
            Model = "gpt-5",
            PromptHash = "e2e-seed",
            ScoredAt = fetchedAt,
        });

        db.Reviews.Add(new Review
        {
            Sha = SeededSha,
            Status = ReviewStatus.Adopted,
            ReviewedAt = fetchedAt,
        });

        db.Drafts.AddRange(
            new Draft
            {
                Sha = SeededSha,
                Channel = "twitter",
                Body = SeededTwitterBody,
                GeneratedAt = fetchedAt,
            },
            new Draft
            {
                Sha = SeededSha,
                Channel = "teams",
                Body = SeededTeamsBody,
                GeneratedAt = fetchedAt,
            },
            new Draft
            {
                Sha = SeededSha,
                Channel = "customer",
                Body = SeededCustomerBody,
                GeneratedAt = fetchedAt,
            });

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private void TryCleanupDb()
    {
        if (string.IsNullOrEmpty(_dbDir))
        {
            return;
        }
        try
        {
            if (Directory.Exists(_dbDir))
            {
                Directory.Delete(_dbDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // SQLite WAL handles may still be open momentarily; the directory is
            // in TEMP so OS cleanup is acceptable.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as IOException above.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SeededE2ETests : ICollectionFixture<SeededAppHostFixture>
{
    public const string Name = "Seeded App End-to-end tests";
}
