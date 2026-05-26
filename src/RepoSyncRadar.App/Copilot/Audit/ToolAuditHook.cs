using System.Collections.Concurrent;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;

namespace RepoSyncRadar.App.Copilot.Audit;

/// <summary>
/// Wires <see cref="SessionHooks.OnPreToolUse"/> and <see cref="SessionHooks.OnPostToolUse"/>
/// to a durable audit trail: one row in <see cref="CopilotToolLog"/> per tool invocation plus
/// one line per phase in the JSONL sink. Pre rows are matched to post rows by a per-(session, tool)
/// FIFO queue of open ids so that parallel tool calls within the same session still pair correctly.
/// </summary>
public sealed partial class ToolAuditHook
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IDbContextFactory<RadarDbContext> _dbFactory;
    private readonly IAuditJsonlSink _jsonlSink;
    private readonly TimeProvider _clock;
    private readonly ILogger<ToolAuditHook> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<int>> _pendingByKey = new(StringComparer.Ordinal);

    public ToolAuditHook(
        IDbContextFactory<RadarDbContext> dbFactory,
        IAuditJsonlSink jsonlSink,
        TimeProvider clock,
        ILogger<ToolAuditHook> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(jsonlSink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _jsonlSink = jsonlSink;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Inserts a fresh <see cref="CopilotToolLog"/> row capturing the tool invocation that is
    /// about to start. The auto-generated row id is enqueued for the matching post hook to find.
    /// </summary>
    public async Task<PreToolUseHookOutput> OnPreToolUseAsync(
        PreToolUseHookInput input,
        HookInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(invocation);

        var startedAt = _clock.GetUtcNow().UtcDateTime;
        var sessionId = invocation.SessionId ?? string.Empty;
        var toolName = input.ToolName ?? string.Empty;
        var argsJson = SerializeJson(input.ToolArgs);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = new CopilotToolLog
        {
            SessionId = sessionId,
            ToolName = toolName,
            ArgsJson = argsJson,
            ResultJson = string.Empty,
            StartedAt = startedAt,
            EndedAt = default,
        };
        db.CopilotToolLogs.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _pendingByKey
            .GetOrAdd(MakeKey(sessionId, toolName), static _ => new ConcurrentQueue<int>())
            .Enqueue(row.Id);

        await _jsonlSink.AppendAsync(
            new AuditRecord("pre", row.Id, sessionId, toolName, argsJson, ResultJson: null, startedAt, EndedAt: null),
            cancellationToken).ConfigureAwait(false);

        LogPreToolUse(_logger, row.Id, sessionId, toolName);
        return new PreToolUseHookOutput();
    }

    /// <summary>
    /// Closes the row created in <see cref="OnPreToolUseAsync"/> with the tool's result.
    /// If no matching pre row is found (e.g. the audit hook was attached mid-session) a new row
    /// is created so the audit trail is never silently dropped.
    /// </summary>
    public async Task<PostToolUseHookOutput> OnPostToolUseAsync(
        PostToolUseHookInput input,
        HookInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(invocation);

        var endedAt = _clock.GetUtcNow().UtcDateTime;
        var sessionId = invocation.SessionId ?? string.Empty;
        var toolName = input.ToolName ?? string.Empty;
        var resultJson = SerializeJson(input.ToolResult);

        var openId = TryDequeueOpenId(sessionId, toolName);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CopilotToolLog? row = null;
        if (openId is int id)
        {
            row = await db.CopilotToolLogs.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (row is null)
        {
            row = new CopilotToolLog
            {
                SessionId = sessionId,
                ToolName = toolName,
                ArgsJson = SerializeJson(input.ToolArgs),
                StartedAt = endedAt,
            };
            db.CopilotToolLogs.Add(row);
            LogPostToolUseOrphan(_logger, sessionId, toolName);
        }

        row.ResultJson = resultJson;
        row.EndedAt = endedAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _jsonlSink.AppendAsync(
            new AuditRecord("post", row.Id, sessionId, toolName, row.ArgsJson, resultJson, row.StartedAt, endedAt),
            cancellationToken).ConfigureAwait(false);

        LogPostToolUse(_logger, row.Id, sessionId, toolName);
        return new PostToolUseHookOutput();
    }

    private int? TryDequeueOpenId(string sessionId, string toolName)
    {
        if (_pendingByKey.TryGetValue(MakeKey(sessionId, toolName), out var queue) && queue.TryDequeue(out var id))
        {
            return id;
        }
        return null;
    }

    private static string MakeKey(string sessionId, string toolName) => $"{sessionId}\u0000{toolName}";

    private static string SerializeJson(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }
        try
        {
            return JsonSerializer.Serialize(value, _jsonOptions);
        }
        catch (NotSupportedException ex)
        {
            return JsonSerializer.Serialize(new { error = "serialize-failed", message = ex.Message }, _jsonOptions);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "[ToolAudit] pre #{RowId} session={SessionId} tool={ToolName}")]
    private static partial void LogPreToolUse(ILogger logger, int rowId, string sessionId, string toolName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "[ToolAudit] post #{RowId} session={SessionId} tool={ToolName}")]
    private static partial void LogPostToolUse(ILogger logger, int rowId, string sessionId, string toolName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "[ToolAudit] no matching pre-row for post hook session={SessionId} tool={ToolName}; created orphan row")]
    private static partial void LogPostToolUseOrphan(ILogger logger, string sessionId, string toolName);
}
