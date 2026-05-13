using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Ask Palette orchestrator (IMPLEMENTATION_PLAN.md §Step 18). Receives a natural
/// language question, asks the Copilot agent to compose a single <c>SELECT</c>, then
/// executes it via <see cref="IRadarQueryRunner"/>.
/// </summary>
/// <remarks>
/// The session prompt instructs Copilot to return its SQL inside a <c>```sql ... ```</c>
/// fenced block. Any other output (e.g. "I don't know") is relayed verbatim. The
/// guard pipeline still runs even when no fenced block is present so that malicious
/// content cannot bypass <see cref="SqlGuard"/>.
/// </remarks>
public sealed partial class AskSession
{
    private static readonly Regex SqlBlockRegex = new(
        @"```sql\s*([\s\S]*?)```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IRadarQueryRunner _runner;
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly ILogger<AskSession> _logger;

    public AskSession(
        IRadarQueryRunner runner,
        ICopilotSessionFactory sessionFactory,
        ILogger<AskSession> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Sends <paramref name="question"/> to the Ask session and returns the result.
    /// When <paramref name="debug"/> is false (the default) the SQL is hidden — only
    /// the rendered Markdown table is returned. Set <paramref name="debug"/> to true to
    /// also surface the (post-guard) SQL.
    /// </summary>
    public async Task<string> AskAsync(
        string question,
        bool debug = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Ask, cancellationToken).ConfigureAwait(false);
        string raw;
        await using (session.ConfigureAwait(false))
        {
            raw = await session.SendAsync(BuildPrompt(question), cancellationToken).ConfigureAwait(false);
        }

        var sql = ExtractSql(raw);
        if (sql is null)
        {
            return raw;
        }

        var result = await _runner.RunAsync(sql, parameters: null, cancellationToken).ConfigureAwait(false);
        if (!result.IsValid)
        {
            LogQueryRejected(_logger, result.Reason ?? "(no reason)");
            return $"クエリは実行できませんでした (理由: {result.Reason}). 安全な SELECT 文だけが利用できます。";
        }

        var table = FormatMarkdown(result);
        if (!debug)
        {
            return table;
        }
        return $"```sql\n{result.TransformedSql}\n```\n\n{table}";
    }

    internal static string BuildPrompt(string question)
    {
        return $"""
        # RepoSyncRadar Ask Palette
        以下の質問に答えるための SQL を考えてください。
        - 必ず 1 文の `SELECT` 文を ```sql ``` フェンスで囲んで返してください。
        - 許可されているテーブル: Commits, Files, Reviews, Drafts, Scores, IgnoreRules, BoostRules, Audits, PathUrlMap
        - 書き込み系 (INSERT/UPDATE/DELETE/DROP 等) は禁止されています。

        ## 質問
        {question}
        """;
    }

    internal static string? ExtractSql(string assistantMessage)
    {
        if (string.IsNullOrWhiteSpace(assistantMessage))
        {
            return null;
        }
        var match = SqlBlockRegex.Match(assistantMessage);
        if (!match.Success)
        {
            return null;
        }
        return match.Groups[1].Value.Trim();
    }

    internal static string FormatMarkdown(RadarQueryResult result)
    {
        if (result.Columns.Count == 0)
        {
            return "(no columns)";
        }
        var sb = new StringBuilder();
        sb.Append('|');
        foreach (var col in result.Columns)
        {
            sb.Append(' ').Append(col).Append(" |");
        }
        sb.AppendLine();
        sb.Append('|');
        for (var i = 0; i < result.Columns.Count; i++)
        {
            sb.Append(" --- |");
        }
        sb.AppendLine();
        if (result.Rows.Count == 0)
        {
            sb.Append('|');
            for (var i = 0; i < result.Columns.Count; i++)
            {
                sb.Append(" (none) |");
            }
            sb.AppendLine();
            return sb.ToString();
        }
        foreach (var row in result.Rows)
        {
            sb.Append('|');
            foreach (var cell in row)
            {
                sb.Append(' ').Append(FormatCell(cell)).Append(" |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatCell(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }
        if (value is IFormattable f)
        {
            return f.ToString(null, CultureInfo.InvariantCulture).Replace("|", "\\|", StringComparison.Ordinal);
        }
        return (value.ToString() ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "AskSession rejected the query: {Reason}.")]
    private static partial void LogQueryRejected(ILogger logger, string reason);
}
