using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using RepoSyncRadar.Core.Data;
using RepoSyncRadar.Core.Models;
using RepoSyncRadar.Core.Services;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Adoption session orchestrator (IMPLEMENTATION_PLAN.md §Step 17). For an already
/// focused commit:
/// <list type="number">
///   <item><description>Loads the commit, its diff, and up to five previously focused commits as few-shot context.</description></item>
///   <item><description>Sends an Adoption session prompt asking Copilot to return JSON with a diff explanation and twitter/customer drafts.</description></item>
///   <item><description>Persists the explanation and two drafts to the local <c>radar.db</c>.</description></item>
/// </list>
/// Diffs larger than <see cref="MaxDiffBytes"/> are truncated with a marker so the prompt
/// stays bounded.
/// </summary>
public sealed partial class AdoptionSession
{
    internal const int MaxDiffBytes = 50 * 1024;
    internal const int MaxBatchCommits = 10;
    internal const int FewShotLimit = 5;
    internal const string TruncatedMarker = "\n…[truncated by RepoSyncRadar — original diff exceeded 50KB]\n";
    internal static readonly TimeSpan DraftSendTimeout = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<RadarDbContext> _dbFactory;
    private readonly IDocsGitHubClient _github;
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly ILogger<AdoptionSession> _logger;
    private readonly IStringLocalizer<SharedResource>? _localizer;

    public AdoptionSession(
        IDbContextFactory<RadarDbContext> dbFactory,
        IDocsGitHubClient github,
        ICopilotSessionFactory sessionFactory,
        ILogger<AdoptionSession> logger,
        IStringLocalizer<SharedResource>? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _dbFactory = dbFactory;
        _github = github;
        _sessionFactory = sessionFactory;
        _logger = logger;
        _localizer = localizer;
    }

    /// <summary>
    /// Generates a <see cref="DraftBundle"/> for the focused commit and
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
                $"Commit {sha} is not focused (current status: {commit.Review?.Status.ToString() ?? "Unseen"}). " +
                "Drafts can only be generated for focused commits.");
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
        var officialDocUrls = await OfficialDocsUrlResolver.LoadAsync(db, commit, cancellationToken).ConfigureAwait(false);

        var prompt = BuildPrompt(commit, fewShot.Select(x => (x.Sha, x.Message)), diff, officialDocUrls);
        LogPromptBuilt(_logger, sha, prompt.Length);

        var session = await _sessionFactory.CreateSessionAsync(SessionPurpose.Adoption, cancellationToken).ConfigureAwait(false);
        DraftBundle bundle;
        await using (session.ConfigureAwait(false))
        {
            bundle = await session.SendStructuredDraftAsync(prompt, DraftSendTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        bundle = EnsureOfficialDocUrls(bundle, officialDocUrls);

        await PersistDraftsAsync(db, sha, bundle, cancellationToken).ConfigureAwait(false);
        return bundle;
    }

    /// <summary>
    /// Generates individual explanations and sharing drafts for several focused commits.
    /// </summary>
    public async Task<int> GenerateBatchExplanationAsync(
        IReadOnlyList<string> commitShas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitShas);

        var shas = commitShas
            .Where(static sha => !string.IsNullOrWhiteSpace(sha))
            .Select(static sha => sha.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxBatchCommits)
            .ToArray();
        if (shas.Length < 2)
        {
            throw new InvalidOperationException(
                _localizer?["AdoptionSession.BatchRequiresAtLeastTwo"]
                ?? "個別解説は 2 件以上の注目コミットを選択して生成してください。");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var commits = await db.Commits
            .AsNoTracking()
            .Include(c => c.Review)
            .Include(c => c.Files)
            .Include(c => c.Scoring)
            .Where(c => shas.Contains(c.Sha))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var bySha = commits.ToDictionary(static commit => commit.Sha, StringComparer.Ordinal);

        var missing = shas.Where(sha => !bySha.ContainsKey(sha)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Commit not found: {string.Join(", ", missing)}.");
        }

        var notAdopted = commits
            .Where(static commit => commit.Review?.Status is not ReviewStatus.Adopted)
            .Select(static commit => commit.Sha)
            .ToArray();
        if (notAdopted.Length > 0)
        {
            throw new InvalidOperationException(
                _localizer?["AdoptionSession.BatchIncludesNonFocused", string.Join(", ", notAdopted)]
                ?? $"注目以外のコミットが含まれています: {string.Join(", ", notAdopted)}.");
        }

        foreach (var sha in shas)
        {
            await GenerateDraftsAsync(sha, cancellationToken).ConfigureAwait(false);
        }

        return shas.Length;
    }

    internal static string TruncateDiff(string diff)
        => TruncateDiff(diff, MaxDiffBytes, TruncatedMarker);

    private static string TruncateDiff(string diff, int maxBytes, string marker)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return string.Empty;
        }
        var byteCount = Encoding.UTF8.GetByteCount(diff);
        if (byteCount <= maxBytes)
        {
            return diff;
        }

        // Conservative cut on a character boundary; UTF-8 oversized => trim until under cap.
        var bytes = Encoding.UTF8.GetBytes(diff);
        var slice = new byte[maxBytes];
        Buffer.BlockCopy(bytes, 0, slice, 0, maxBytes);
        var truncated = Encoding.UTF8.GetString(slice);
        // Trim any partial trailing char to keep the string valid.
        var lastValid = truncated.Length;
        while (lastValid > 0 && char.IsLowSurrogate(truncated[lastValid - 1]))
        {
            lastValid--;
        }
        return truncated[..lastValid] + marker;
    }

    internal static string BuildPrompt(
        Commit commit,
        IEnumerable<(string Sha, string Message)> fewShot,
        string diff,
        IReadOnlyList<string>? officialDocUrls = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 注目コミット — 差分解説と共有文案生成");
        sb.AppendLine();
        sb.AppendLine("以下のコミットを注目対象にしました。差分を読まなくても変更点を理解できる日本語解説と、Twitter / 顧客向けの 2 つの日本語下書きを生成してください。");
        sb.AppendLine();
        sb.AppendLine("## 入出力");
        sb.AppendLine("- 出力は **必ず JSON のみ**。説明文や Markdown コードブロックは禁止。");
        sb.AppendLine("- スキーマ: `{ \"explanation\": string, \"twitter\": string, \"customer\": string }`");
        sb.AppendLine("- explanation は 1200〜2000 文字程度。差分の細部を読まなくても変更点を理解できる密度にする。");
        sb.AppendLine("- twitter は 140 文字以内、customer は 1600 文字以内を目安。");
        sb.AppendLine("- twitter / customer には、必ず下記の公式ドキュメント URL を 1 つ以上含める。");
        sb.AppendLine();
        sb.AppendLine("## 用語ルール");
        sb.AppendLine("- GitHub の product scope としての `Organization` / `Enterprise` は英語のまま書き、どちらも `組織` と訳さない。");
        sb.AppendLine();
        sb.AppendLine("## explanation の要件");
        sb.AppendLine("- 次の見出しをこの順序で含める: `何が変わったか`, `差分の見方`, `重要なポイント`, `影響と次に見るべき点`。");
        sb.AppendLine("- `何が変わったか`: 変更の目的と利用者に見える差分を要約する。");
        sb.AppendLine("- `差分の見方`: 追加・削除・移動・設定値・API 名など、差分のどこを見れば理解できるかを説明する。");
        sb.AppendLine("- `重要なポイント`: 差分だけでは読み取りづらい意味、背景、仕様上の位置づけ、注意点を整理する。");
        sb.AppendLine("- `影響と次に見るべき点`: 影響を受ける読者、確認すべき API/権限/バージョン/既存記述との関係を書く。");
        sb.AppendLine("- 推測は断定せず、差分から確認できた事実と確認観点を分ける。");
        sb.AppendLine();
        var openApiReferencePaths = GetOpenApiReferenceDataPaths(commit).ToArray();
        if (openApiReferencePaths.Length > 0)
        {
            sb.AppendLine("## OpenAPI / API reference 差分の追加要件");
            sb.AppendLine("- このコミットは OpenAPI 由来の API reference data を含むため、Markdown 差分だけで判断しない。");
            sb.AppendLine("- explanation では、当該 API の差分に関する詳細な解説を必ず含める。エンドポイント、HTTP メソッド、権限/permission、認証方式、request/response schema、preview/version、追加・削除・意味変更を差分から確認できる範囲で具体的に書く。");
            sb.AppendLine("- `差分の見方` では、下記の API data file と対応する `content/rest/**` ページの両方を参照して、生成された Markdown だけでは落ちやすい API 仕様上の意味を説明する。");
            sb.AppendLine("- `影響と次に見るべき点` では、API 利用者、SDK/クライアント生成、GitHub Apps/Fine-grained PAT/Webhook 利用者への確認観点を含める。");
            sb.AppendLine("- トリガーになった API data file:");
            foreach (var path in openApiReferencePaths.Take(20))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  - `{path}`").AppendLine();
            }
            if (openApiReferencePaths.Length > 20)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  - ... and {openApiReferencePaths.Length - 20} more").AppendLine();
            }
            sb.AppendLine();
        }
        sb.AppendLine("## 過去の注目例 (Few-shot)");
        var any = false;
        foreach (var ex in fewShot)
        {
            any = true;
            sb.Append(CultureInfo.InvariantCulture, $"- `{ex.Sha}` — {ex.Message}").AppendLine();
        }
        if (!any)
        {
            sb.AppendLine("- (まだ注目例がありません)");
        }
        sb.AppendLine();
        sb.AppendLine("## 対象コミット");
        sb.Append(CultureInfo.InvariantCulture, $"- SHA: `{commit.Sha}`").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- メッセージ: {commit.Message}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- 著者: {commit.Author}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"- 変更ファイル数: {commit.Files.Count}").AppendLine();
        sb.AppendLine();
        sb.AppendLine("## 公式ドキュメント URL");
        var urls = officialDocUrls is { Count: > 0 }
            ? officialDocUrls
            : OfficialDocsUrlResolver.BuildFallbackUrls(commit);
        foreach (var url in urls)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- {url}").AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("## 差分 (一部省略あり)");
        sb.AppendLine("```diff");
        sb.AppendLine(diff);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static IEnumerable<string> GetOpenApiReferenceDataPaths(Commit commit)
        => commit.Files
            .Select(static file => file.Path)
            .Where(IsOpenApiReferenceDataPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static bool IsOpenApiReferenceDataPath(string path)
        => path.StartsWith("src/rest/data/", StringComparison.Ordinal)
            || path.StartsWith("src/github-apps/data/", StringComparison.Ordinal)
            || path.StartsWith("src/webhooks/data/", StringComparison.Ordinal)
            || string.Equals(path, "src/rest/lib/config.json", StringComparison.Ordinal)
            || string.Equals(path, "src/github-apps/lib/config.json", StringComparison.Ordinal)
            || string.Equals(path, "src/webhooks/lib/config.json", StringComparison.Ordinal);

    private static DraftBundle EnsureOfficialDocUrls(DraftBundle bundle, IReadOnlyList<string> officialDocUrls)
    {
        var url = officialDocUrls.Count > 0 ? officialDocUrls[0] : string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return bundle;
        }

        return new DraftBundle(
            TwitterJa: EnsureOfficialDocUrl(bundle.TwitterJa, url),
            TeamsJa: string.Empty,
            CustomerJa: EnsureOfficialDocUrl(bundle.CustomerJa, url),
            ExplanationJa: bundle.ExplanationJa);
    }

    private static string EnsureOfficialDocUrl(string draft, string url)
    {
        if (string.IsNullOrWhiteSpace(draft))
        {
            return url;
        }
        if (draft.Contains("docs.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return draft;
        }
        return draft.TrimEnd() + Environment.NewLine + url;
    }

    private static async Task PersistDraftsAsync(
        RadarDbContext db,
        string sha,
        DraftBundle bundle,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var channels = new[] { "twitter", "teams", "customer", "explanation" };
        var existing = await db.Drafts
            .Where(d => d.Sha == sha && channels.Contains(d.Channel))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.Drafts.RemoveRange(existing);

        var entries = new[]
        {
            new Draft { Sha = sha, Channel = "twitter", Body = bundle.TwitterJa, Posted = false, GeneratedAt = nowUtc },
            new Draft { Sha = sha, Channel = "customer", Body = bundle.CustomerJa, Posted = false, GeneratedAt = nowUtc },
            new Draft { Sha = sha, Channel = "explanation", Body = bundle.ExplanationJa, Posted = false, GeneratedAt = nowUtc },
        };
        db.Drafts.AddRange(entries);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Adoption prompt built for {Sha} ({PromptLength} chars).")]
    private static partial void LogPromptBuilt(ILogger logger, string sha, int promptLength);
}
