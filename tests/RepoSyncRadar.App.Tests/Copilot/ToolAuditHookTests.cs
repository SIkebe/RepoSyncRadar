using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.Core.Data;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

/// <summary>
/// Validates that <see cref="ToolAuditHook"/> persists a row per <c>OnPreToolUse</c>/<c>OnPostToolUse</c>
/// pair to <see cref="RadarDbContext.CopilotToolLogs"/> and mirrors the events to a JSONL sink.
/// The hook is exercised directly — the Copilot SDK is not started.
/// </summary>
public sealed class ToolAuditHookTests
{
    [Fact]
    public async Task PreUse_Inserts_Row_With_Started()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync(ct);
        var hook = harness.CreateHook();

        var pre = new PreToolUseHookInput
        {
            ToolName = "radar_list_commits",
            ToolArgs = new { status = "Unseen" },
            Timestamp = harness.Clock.GetUtcNow(),
        };
        var invocation = new HookInvocation { SessionId = "S1" };

        var result = await hook.OnPreToolUseAsync(pre, invocation, ct);

        Assert.NotNull(result);
        await using var db = harness.CreateDb();
        var rows = await db.CopilotToolLogs.ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.True(row.Id > 0);
        Assert.Equal("S1", row.SessionId);
        Assert.Equal("radar_list_commits", row.ToolName);
        Assert.Contains("\"status\":\"Unseen\"", row.ArgsJson);
        Assert.Equal(harness.UtcNow, row.StartedAt);
        Assert.Equal(default, row.EndedAt);
        Assert.Equal(string.Empty, row.ResultJson);
    }

    [Fact]
    public async Task PostUse_Completes_Row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync(ct);
        var hook = harness.CreateHook();

        var invocation = new HookInvocation { SessionId = "S1" };
        await hook.OnPreToolUseAsync(
            new PreToolUseHookInput { ToolName = "radar_get_diff", ToolArgs = new { sha = "abc" } },
            invocation, ct);

        var preTime = harness.UtcNow;
        harness.Advance(TimeSpan.FromMilliseconds(750));

        await hook.OnPostToolUseAsync(
            new PostToolUseHookInput
            {
                ToolName = "radar_get_diff",
                ToolArgs = new { sha = "abc" },
                ToolResult = new { diff = "@@ -1 +1 @@\n-old\n+new" },
            },
            invocation, ct);

        await using var db = harness.CreateDb();
        var row = Assert.Single(await db.CopilotToolLogs.ToListAsync(ct));
        Assert.Equal(preTime, row.StartedAt);
        Assert.Equal(harness.UtcNow, row.EndedAt);
        Assert.Contains("\"diff\":", row.ResultJson);
    }

    [Fact]
    public async Task PostUse_On_Error_Records_Error_Json()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync(ct);
        var hook = harness.CreateHook();
        var invocation = new HookInvocation { SessionId = "S1" };

        await hook.OnPreToolUseAsync(
            new PreToolUseHookInput { ToolName = "radar_save_review", ToolArgs = new { sha = "abc" } },
            invocation, ct);

        // Simulate the SDK reporting an error in the result payload.
        await hook.OnPostToolUseAsync(
            new PostToolUseHookInput
            {
                ToolName = "radar_save_review",
                ToolResult = new { error = "Unknown SHA: abc" },
            },
            invocation, ct);

        await using var db = harness.CreateDb();
        var row = Assert.Single(await db.CopilotToolLogs.ToListAsync(ct));
        using var doc = JsonDocument.Parse(row.ResultJson);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errProp));
        Assert.Equal("Unknown SHA: abc", errProp.GetString());
    }

    [Fact]
    public async Task Jsonl_Appends_Line()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync(ct);
        var hook = harness.CreateHook();
        var invocation = new HookInvocation { SessionId = "S1" };

        await hook.OnPreToolUseAsync(
            new PreToolUseHookInput { ToolName = "radar_list_commits", ToolArgs = new { } },
            invocation, ct);
        await hook.OnPostToolUseAsync(
            new PostToolUseHookInput { ToolName = "radar_list_commits", ToolResult = new { count = 3 } },
            invocation, ct);

        var records = harness.JsonlSink.Records;
        Assert.Equal(2, records.Count);
        Assert.Equal("pre", records[0].Phase);
        Assert.Equal("post", records[1].Phase);
        Assert.Equal(records[0].RowId, records[1].RowId);
    }

    [Fact]
    public async Task Concurrent_PreUse_Has_Unique_Ids()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await TestHarness.CreateAsync(ct);
        var hook = harness.CreateHook();
        var invocation = new HookInvocation { SessionId = "S-concurrent" };

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            hook.OnPreToolUseAsync(
                new PreToolUseHookInput { ToolName = "radar_list_commits", ToolArgs = new { i } },
                invocation, ct), ct)).ToArray();
        await Task.WhenAll(tasks);

        await using var db = harness.CreateDb();
        var ids = await db.CopilotToolLogs.Select(x => x.Id).ToListAsync(ct);
        Assert.Equal(10, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly FakeTimeProvider _clock;

        private TestHarness(string dbPath)
        {
            _dbPath = dbPath;
            // Pooling=false so we can delete the file in DisposeAsync without leaving cached connections.
            _connectionString = $"Data Source={dbPath};Pooling=False";
            _clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 13, 9, 0, 0, TimeSpan.Zero));
            JsonlSink = new InMemoryJsonlSink();
        }

        public InMemoryJsonlSink JsonlSink { get; }

        public TimeProvider Clock => _clock;

        public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

        public void Advance(TimeSpan delta) => _clock.Advance(delta);

        public static async Task<TestHarness> CreateAsync(CancellationToken cancellationToken)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"reposyncradar-audit-{Guid.NewGuid():N}.db");
            var harness = new TestHarness(dbPath);
            var options = new DbContextOptionsBuilder<RadarDbContext>().UseSqlite(harness._connectionString).Options;
            await using (var db = new RadarDbContext(options))
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
            return harness;
        }

        public ToolAuditHook CreateHook()
        {
            var factory = new ConnectionStringDbContextFactory(_connectionString);
            return new ToolAuditHook(factory, JsonlSink, _clock, NullLogger<ToolAuditHook>.Instance);
        }

        public RadarDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<RadarDbContext>().UseSqlite(_connectionString).Options;
            return new RadarDbContext(options);
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
                // Best-effort cleanup; the temp file will be reaped by Windows eventually.
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectionStringDbContextFactory(string connectionString) : IDbContextFactory<RadarDbContext>
    {
        public RadarDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<RadarDbContext>().UseSqlite(connectionString).Options;
            return new RadarDbContext(options);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        private readonly object _gate = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public void Advance(TimeSpan delta)
        {
            lock (_gate)
            {
                _now = _now.Add(delta);
            }
        }
    }

    private sealed class InMemoryJsonlSink : IAuditJsonlSink
    {
        private readonly object _gate = new();
        private readonly List<AuditRecord> _records = new();

        public IReadOnlyList<AuditRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return _records.ToArray();
                }
            }
        }

        public Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _records.Add(record);
            }
            return Task.CompletedTask;
        }
    }
}
