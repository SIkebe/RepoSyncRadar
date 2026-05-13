using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Adoption session orchestrator (IMPLEMENTATION_PLAN.md §Step 17). For an already
/// adopted commit:
/// <list type="number">
///   <item><description>Loads the commit, its diff, and up to five previously adopted commits as few-shot context.</description></item>
///   <item><description>Sends an Adoption session prompt asking Copilot to return JSON with twitter/slack/customer drafts.</description></item>
///   <item><description>Persists the three drafts to the local <c>radar.db</c>.</description></item>
/// </list>
/// Diffs larger than <see cref="MaxDiffBytes"/> are truncated with a marker so the prompt
/// stays bounded.
/// </summary>
public sealed partial class AdoptionSession
{
    internal const int MaxDiffBytes = 50 * 1024;
    internal const int FewShotLimit = 5;
    internal const string TruncatedMarker = "\n…[truncated by RepoSyncRadar — original diff exceeded 50KB]\n";

    private static readonly JsonSerializerOptions DraftJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IDbContextFactory<RadarDbContext> _dbFactory;
    private readonly IDocsGitHubClient _github;
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly ILogger<AdoptionSession> _logger;

    public AdoptionSession(
        IDbContextFactory<RadarDbContext> dbFactory,
        IDocsGitHubClient github,
        ICopilotSessionFactory sessionFactory,
        ILogger<AdoptionSession> logger)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _github = github;
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generates a three-channel <see cref="DraftBundle"/> for the adopted commit and
    /// persists it. Throws <see cref="InvalidOperationException"/> when the commit is
    /// not in <see cref="ReviewStatus.Adopted"/>.
    /// </summary>
    public async Task<DraftBundle> GenerateDraftsAsync(string sha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var commit = await db.Commits
            .AsNoTracking()
            .Include(c => c.Review)
            .Include(c => c.Files)
            .FirstOrDefaultAsync(c => c.Sha == sha, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Commit not found: {sha}.");
        if (commit.Review?.Status is not ReviewStatus.Adopted)
        {
            throw new InvalidOperationException(
                $"Commit {sha} is not adopted (current status: {commit.Review?.Status.ToString() ?? "Unseen"}). " +
                "Drafts can only be generated for adopted commits.");
        }

        var fewShot = await db.Commits
            .AsNoTracking()
            .Where(c => c.Sha != sha && c.Review != null && c.Review.Status == ReviewStatus.Adopted)
            .OrderByDescending(c => c.Review!.ReviewedAt)
            .Take(FewShotLimit)
            .Select(c => new { c.Sha, c.Message })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rawDiff = await _github.GetUnifiedDiffAsync(sha, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var diff = TruncateDiff(rawDiff);

        var prompt = BuildPrompt(commit, fewShot.Select(x => (x.Sha, x.Message)), diff);
        LogPromptBuilt(_logger, sha, prompt.Length);

        var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Adoption, cancellationToken).ConfigureAwait(false);
        DraftBundle bundle;
        await using (session.ConfigureAwait(false))
        {
            var raw = await session.SendAsync(prompt, cancellationToken).ConfigureAwait(false);
            bundle = ParseBundle(raw);
        }

        await PersistDraftsAsync(db, sha, bundle, cancellationToken).ConfigureAwait(false);
        return bundle;
    }

    internal static string TruncateDiff(string diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return string.Empty;
        }
        var byteCount = Encoding.UTF8.GetByteCount(diff);
        if (byteCount <= MaxDiffBytes)
        {
            return diff;
        }

        // Conservative cut on a character boundary; UTF-8 oversized => trim until under cap.
        var bytes = Encoding.UTF8.GetBytes(diff);
        var slice = new byte[MaxDiffBytes];
        Buffer.BlockCopy(bytes, 0, slice, 0, MaxDiffBytes);
        var truncated = Encoding.UTF8.GetString(slice);
        // Trim any partial trailing char to keep the string valid.
        var lastValid = truncated.Length;
        while (lastValid > 0 && char.IsLowSurrogate(truncated[lastValid - 1]))
        {
            lastValid--;
        }
        return truncated[..lastValid] + TruncatedMarker;
    }

    internal static string BuildPrompt(
        Commit commit,
        IEnumerable<(string Sha, string Message)> fewShot,
        string diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Adoption — 媒体別下書き生成");
        sb.AppendLine();
        sb.AppendLine("以下のコミットを採用しました。Twitter / Slack / 顧客向けの 3 つの日本語下書きを生成してください。");
        sb.AppendLine();
        sb.AppendLine("## 入出力");
        sb.AppendLine("- 出力は **必ず JSON のみ**。説明文や Markdown コードブロックは禁止。");
        sb.AppendLine("- スキーマ: `{ \"twitter\": string, \"slack\": string, \"customer\": string }`");
        sb.AppendLine("- twitter は 140 文字以内、slack は 800 文字以内、customer は 1600 文字以内を目安。");
        sb.AppendLine();
        sb.AppendLine("## 過去の採用例 (Few-shot)");
        var any = false;
        foreach (var ex in fewShot)
        {
            any = true;
            sb.Append(CultureInfo.InvariantCulture, $"- `{ex.Sha}` — {ex.Message}").AppendLine();
        }
        if (!any)
        {
            sb.AppendLine("- (まだ採用例がありません)");
        }
        sb.AppendLine();
        sb.AppendLine("## 対象コミット");
        sb.Append(CultureInfo.InvariantCulture, $"- SHA: `{commit.Sha}`").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- メッセージ: {commit.Message}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- 著者: {commit.Author}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- 変更ファイル数: {commit.Files.Count}").AppendLine();
        sb.AppendLine();
        sb.AppendLine("## 差分 (一部省略あり)");
        sb.AppendLine("```diff");
        sb.AppendLine(diff);
        sb.AppendLine("```");
        return sb.ToString();
    }

    internal static DraftBundle ParseBundle(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Adoption session returned an empty response.");
        }
        var trimmed = json.Trim();
        // Strip markdown code fences if Copilot wrapped them.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
            trimmed = trimmed.Trim();
        }

        DraftJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DraftJson>(trimmed, DraftJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Adoption session returned non-JSON output.", ex);
        }
        if (parsed is null)
        {
            throw new InvalidOperationException("Adoption session returned a null JSON document.");
        }

        return new DraftBundle(
            parsed.Twitter ?? string.Empty,
            parsed.Slack ?? string.Empty,
            parsed.Customer ?? string.Empty);
    }

    private static async Task PersistDraftsAsync(
        RadarDbContext db,
        string sha,
        DraftBundle bundle,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var entries = new[]
        {
            new Draft { Sha = sha, Channel = "twitter", Body = bundle.TwitterJa, Posted = false, GeneratedAt = nowUtc },
            new Draft { Sha = sha, Channel = "slack", Body = bundle.SlackJa, Posted = false, GeneratedAt = nowUtc },
            new Draft { Sha = sha, Channel = "customer", Body = bundle.CustomerJa, Posted = false, GeneratedAt = nowUtc },
        };
        db.Drafts.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class DraftJson
    {
        public string? Twitter { get; set; }
        public string? Slack { get; set; }
        public string? Customer { get; set; }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Adoption prompt built for {Sha} ({PromptLength} chars).")]
    private static partial void LogPromptBuilt(ILogger logger, string sha, int promptLength);
}
