using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Thin orchestrator that ties <see cref="DocsWorktreeManager"/>,
/// <see cref="PreviewServerHost"/> and <see cref="PreviewSession"/> together so the
/// UI only has to know about a single async method to spin up a per-commit preview
/// (IMPLEMENTATION_PLAN.md §Step 19.5).
/// </summary>
/// <remarks>
/// <para>
/// The coordinator is serial: callers should not invoke
/// <see cref="PreparePreviewAsync"/> concurrently. It caches the last SHA prepared so
/// that switching files within the same commit reuses the running server, and only
/// restarts on a SHA change.
/// </para>
/// <para>
/// When <see cref="DocsWorktreeManager.IsEnabled"/> is <c>false</c> every call
/// returns <c>null</c> and no subordinate service is touched — the feature is opt-in
/// via <c>DocsRepository</c> in <c>appsettings.Local.json</c>.
/// </para>
/// </remarks>
public interface IPreviewCoordinator
{
    /// <summary>
    /// Ensures the bare clone exists, fetches the PR, checks out a worktree for
    /// <paramref name="sha"/>, starts (or reuses) the preview sidecar, and returns a
    /// loopback URL pointing at the mapped article. Returns <c>null</c> when the
    /// preview pipeline is disabled.
    /// </summary>
    /// <param name="progress">
    /// Optional sink that receives a short human-readable message before each major
    /// step (clone / fetch / worktree / server). The UI uses this to show real-time
    /// feedback so users can tell the pipeline is running rather than frozen.
    /// </param>
    Task<PreviewLink?> PreparePreviewAsync(
        int prNumber,
        string sha,
        string? filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts two local preview servers for the same article: the first parent of
    /// <paramref name="sha"/> as the before pane, and <paramref name="sha"/> as PR HEAD.
    /// This remains useful even after docs.github.com has already deployed the PR.
    /// </summary>
    Task<PreviewComparisonLink?> PrepareComparisonPreviewAsync(
        int prNumber,
        string sha,
        string? filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a non-publishable Markdown file from the first parent and PR HEAD
    /// worktrees, hosts both rendered pages on localhost, and returns before/after URLs.
    /// </summary>
    /// <param name="version">
    /// 描画する <see cref="DocsVersion"/>。未指定なら差分が出る最初の版を使う。
    /// 差分が版依存でない場合は <see cref="DocsVersionCatalog.Default"/> (= fpt) を使う。
    /// <c>{% ifversion ... %}</c> がこの版で評価される。
    /// </param>
    Task<PreviewComparisonLink?> PrepareMarkdownComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        IProgress<string>? progress = null,
        DocsVersion? version = null,
        CancellationToken cancellationToken = default);

    Task<PreviewComparisonLink?> PrepareMarkdownComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        IProgress<string>? progress,
        DocsVersion? version,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken = default);

    /// <summary>Stops the running preview server (if any) and clears the active session.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Eagerly creates the bare clone so the user's first preview click no longer
    /// pays the 1-2 minute <c>git clone --bare</c> cost. Best-effort: when the
    /// pipeline is disabled or the bare clone already exists, this is a fast no-op.
    /// Wired by the App layer as fire-and-forget right after the main window
    /// shows; it runs in the background while the user does anything else.
    /// </summary>
    Task PrewarmAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Predictively warms the repository and before/after worktrees for a freshly
    /// selected commit so the user's first "ローカルプレビュー" click does not pay
    /// the network fetch or worktree setup cost when the data is already local. Performs
    /// <see cref="DocsWorktreeManager.EnsureBareCloneAsync"/>,
    /// commit availability check / PR fetch when needed,
    /// <see cref="DocsWorktreeManager.ResolveFirstParentAsync"/>,
    /// <see cref="DocsWorktreeManager.CheckoutAsync"/> for both the before/after
    /// commits. Per-file Liquid context is loaded lazily from only the clicked
    /// Markdown's referenced variables/reusables/AUTOTITLE targets. All work is
    /// serialized through a per-(prNumber, sha) <see cref="SemaphoreSlim"/>,
    /// so concurrent invocations from the predictive path and the user-click
    /// path coalesce instead of racing the internal Dictionary in
    /// <see cref="DocsWorktreeManager"/>. Best-effort: any failure (network,
    /// bad SHA, etc.) is swallowed; the regular preview path will surface the
    /// error when clicked.
    /// </summary>
    Task PredictivePrewarmAsync(int prNumber, string sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running preview and removes every worktree this manager tracks
    /// (including those rehydrated from disk on startup). Returns the number of
    /// worktrees actually removed. Returns 0 when the preview pipeline is disabled.
    /// </summary>
    /// <remarks>
    /// Wired to the UI &quot;キャッシュをクリーンアップ&quot; button so users can reclaim
    /// disk space without leaving the app or memorizing git commands.
    /// </remarks>
    Task<int> CleanupCacheAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IPreviewCoordinator.PreparePreviewAsync"/>.</summary>
public sealed record PreviewLink(Uri Url, int Port, string WorktreePath);

/// <summary>Outcome of a local before/after visual preview comparison.</summary>
public sealed record PreviewComparisonLink(
    Uri BeforeUrl,
    Uri AfterUrl,
    int BeforePort,
    int AfterPort,
    string BeforeWorktreePath,
    string AfterWorktreePath,
    string BeforeSha,
    string AfterSha)
{
    /// <summary>
    /// Markdown プレビューで描画したときの <see cref="DocsVersion"/>。
    /// 通常の Next.js 経路では <see cref="DocsVersionCatalog.Default"/> 固定 (= fpt)。
    /// </summary>
    public DocsVersion? CurrentVersion { get; init; }

    /// <summary>
    /// この PR でレンダリング結果が変わる <see cref="DocsVersion"/> の一覧。
    /// 公式 docs と同じく fpt/ghec/ghes を並べた dropdown 順 (見落とし防止用)。
    /// Markdown プレビュー以外では <c>null</c>。
    /// </summary>
    public IReadOnlyList<DocsVersion>? AffectedVersions { get; init; }

    /// <summary>
    /// Frontmatter / Liquid 条件など、補足表示するソース差分の件数。
    /// </summary>
    public int SourceChangeCount { get; init; }

    /// <summary>ユーザーが選択した元の Markdown パス。reusable 使用箇所プレビューでは reusable 自体のパス。</summary>
    public string? RequestedFilePath { get; init; }

    /// <summary>実際にレンダリングした Markdown パス。reusable 使用箇所プレビューでは参照元 content ページ。</summary>
    public string? RenderedFilePath { get; init; }

    /// <summary>reusable の参照元候補数。通常 Markdown プレビューでは 0。</summary>
    public int ReusableReferenceCount { get; init; }
}

internal sealed record ReusablePreviewTarget(string FilePath, int ReferenceCount);

/// <inheritdoc cref="IPreviewCoordinator" />
public sealed partial class PreviewCoordinator : IPreviewCoordinator
{
    private const string _markdownBeforeAssetRoute = "/markdown-assets/before";
    private const string _markdownAfterAssetRoute = "/markdown-assets/after";

    private readonly DocsWorktreeManager _worktree;
    private readonly PreviewServerHost _server;
    private readonly PreviewServerHost _beforeServer;
    private readonly ILocalPreviewContentServer _contentServer;
    private readonly PreviewSession _session;
    private readonly IPreviewPortAllocator _portAllocator;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewCoordinator> _logger;
    private string? _activeSha;
    private string? _activeBeforeSha;
    private string? _activeMarkdownAssetRoot;

    // §Step 19.10 (perf): 同一 PR HEAD で 1/7 → 2/7 → 3/7 のように別ファイルへ
    // 切り替えるたびに毎回 git fetch + git rev-parse + git worktree add ×2 を走らせると、
    // 公式 docs (数千ファイル) では 1 ファイル切り替えに数秒〜10 秒掛かる。
    // (prNumber, sha) ごとに「準備済みセッション」をキャッシュして、ファイル切替の
    // たびに走らせるのはファイル読込 + 対象Markdown用Liquid context + Markdigだけにする。
    private readonly ConcurrentDictionary<PreparedSessionKey, PreparedMarkdownSession> _preparedSessions = new();

    // (prNumber, sha) ごとの準備処理は 1 本にシリアライズする。先読み (PredictivePrewarmAsync)
    // とユーザクリック (PrepareMarkdownComparisonPreviewAsync) が同時に走っても、後発は
    // 先発の完了を待ってキャッシュヒットだけで終わる。DocsWorktreeManager の内部
    // Dictionary は thread-safe ではないので、これを介して保護する。
    private readonly ConcurrentDictionary<PreparedSessionKey, SemaphoreSlim> _preparedSessionLocks = new();

    // commitSha + filePath ごとに、クリックされた Markdown が実際に参照する
    // variables/reusables/page titles だけを読み込んだ DocsLiquidContext を使い回す。
    private readonly ConcurrentDictionary<LiquidContextCacheKey, DocsLiquidContext> _liquidContextCache = new();

    private readonly record struct PreparedSessionKey(int PrNumber, string Sha);

    private readonly record struct LiquidContextCacheKey(string CommitSha, string FilePath);

    private sealed record PreparedMarkdownSession(string BeforeSha);

    public PreviewCoordinator(
        DocsWorktreeManager worktree,
        PreviewServerHost server,
        IPreviewServerHostFactory serverFactory,
        ILocalPreviewContentServer contentServer,
        PreviewSession session,
        IPreviewPortAllocator portAllocator,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(serverFactory);
        ArgumentNullException.ThrowIfNull(contentServer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(portAllocator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _worktree = worktree;
        _server = server;
        _beforeServer = serverFactory.Create();
        _contentServer = contentServer;
        _session = session;
        _portAllocator = portAllocator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PreviewLink?> PreparePreviewAsync(
        int prNumber,
        string sha,
        string? filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        if (!_worktree.IsEnabled || !_server.IsEnabled)
        {
            LogDisabled(_logger);
            return null;
        }

        progress?.Report("リポジトリを準備中… (初回は git clone --bare で 1〜2 分)");
        await _worktree.EnsureBareCloneAsync(cancellationToken).ConfigureAwait(false);

        await _worktree.EnsureCommitAvailableAsync(prNumber, sha, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("worktree を作成中… (git worktree add)");
        var worktreePath = await _worktree.CheckoutAsync(sha, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(worktreePath))
        {
            return null;
        }

        var port = string.Equals(_activeSha, sha, StringComparison.Ordinal)
            && _server.IsProcessRunning
            && _server.CurrentPort > 0
                ? _server.CurrentPort
                : _portAllocator.AllocateSingle(_options.PreviewBasePort, GetReusablePorts());
        if (!string.Equals(_activeSha, sha, StringComparison.Ordinal)
            || _server.CurrentPort != port
            || !_server.IsProcessRunning)
        {
            progress?.Report(
                $"依存関係を確認してプレビューサーバを起動中… (node_modules がなければ {_options.PreviewCommand} {_options.PreviewInstallArguments} を自動実行 / ポート {port.ToString(CultureInfo.InvariantCulture)})");
            await _server.StartAsync(worktreePath, port, progress, cancellationToken).ConfigureAwait(false);
            _activeSha = sha;
        }
        else
        {
            progress?.Report("既存のプレビューサーバを再利用します");
        }

        _session.Activate(port);

        var path = string.IsNullOrWhiteSpace(filePath)
            ? "/"
            : PreviewPathMapper.Map(filePath, "en") ?? "/";
        var url = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}{path}"));
        LogPreviewReady(_logger, sha, url.AbsoluteUri);
        return new PreviewLink(url, port, worktreePath);
    }

    public async Task<PreviewComparisonLink?> PrepareComparisonPreviewAsync(
        int prNumber,
        string sha,
        string? filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        if (!_worktree.IsEnabled || !_server.IsEnabled)
        {
            LogDisabled(_logger);
            return null;
        }

        progress?.Report("リポジトリを準備中… (初回は git clone --bare で 1〜2 分)");
        await _worktree.EnsureBareCloneAsync(cancellationToken).ConfigureAwait(false);

        await _worktree.EnsureCommitAvailableAsync(prNumber, sha, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("比較元の親コミットを解決中… (git rev-parse <sha>^)");
        var beforeSha = await _worktree.ResolveFirstParentAsync(sha, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(beforeSha))
        {
            throw new InvalidOperationException("比較元になる親コミットを解決できませんでした。");
        }

        progress?.Report("変更前 worktree を作成中… (git worktree add)");
        var beforeWorktreePath = await _worktree.CheckoutAsync(beforeSha, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(beforeWorktreePath))
        {
            return null;
        }

        progress?.Report("PR HEAD worktree を作成中… (git worktree add)");
        var afterWorktreePath = await _worktree.CheckoutAsync(sha, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(afterWorktreePath))
        {
            return null;
        }

        var ports = _portAllocator.AllocateComparison(_options.PreviewBasePort, GetReusablePorts());
        var afterPort = ports.AfterPort;
        var beforePort = ports.BeforePort;

        var beforeNeedsStart = !string.Equals(_activeBeforeSha, beforeSha, StringComparison.Ordinal)
            || _beforeServer.CurrentPort != beforePort
            || !_beforeServer.IsProcessRunning;
        var afterNeedsStart = !string.Equals(_activeSha, sha, StringComparison.Ordinal)
            || _server.CurrentPort != afterPort
            || !_server.IsProcessRunning;

        if (beforeNeedsStart && afterNeedsStart)
        {
            // Parallel cold start cuts wall-clock time roughly in half because
            // each PreviewServerHost instance is independent (own process, own
            // worktree path) and the long phases (`npm install`, Next.js
            // initial compile) overlap instead of running back-to-back.
            progress?.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"変更前 (ポート {beforePort}) と PR HEAD (ポート {afterPort}) のプレビューサーバを並列起動中…"));
            var beforeTask = _beforeServer.StartAsync(beforeWorktreePath, beforePort, progress, cancellationToken);
            var afterTask = _server.StartAsync(afterWorktreePath, afterPort, progress, cancellationToken);
            await Task.WhenAll(beforeTask, afterTask).ConfigureAwait(false);
            _activeBeforeSha = beforeSha;
            _activeSha = sha;
        }
        else if (beforeNeedsStart)
        {
            progress?.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"変更前プレビューサーバを起動中… (ポート {beforePort})"));
            await _beforeServer.StartAsync(beforeWorktreePath, beforePort, progress, cancellationToken).ConfigureAwait(false);
            _activeBeforeSha = beforeSha;
            progress?.Report("既存の PR HEAD プレビューサーバを再利用します");
        }
        else if (afterNeedsStart)
        {
            progress?.Report("既存の変更前プレビューサーバを再利用します");
            progress?.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"PR HEAD プレビューサーバを起動中… (ポート {afterPort})"));
            await _server.StartAsync(afterWorktreePath, afterPort, progress, cancellationToken).ConfigureAwait(false);
            _activeSha = sha;
        }
        else
        {
            progress?.Report("既存の変更前および PR HEAD プレビューサーバを再利用します");
        }

        _session.Activate(afterPort, beforePort);

        var path = string.IsNullOrWhiteSpace(filePath)
            ? "/"
            : PreviewPathMapper.Map(filePath, "en") ?? "/";
        var beforeUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{beforePort}{path}"));
        var afterUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://localhost:{afterPort}{path}"));
        LogPreviewComparisonReady(_logger, beforeSha, sha, beforeUrl.AbsoluteUri, afterUrl.AbsoluteUri);
        return new PreviewComparisonLink(
            beforeUrl,
            afterUrl,
            beforePort,
            afterPort,
            beforeWorktreePath,
            afterWorktreePath,
            beforeSha,
            sha);
    }

    public async Task<PreviewComparisonLink?> PrepareMarkdownComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        IProgress<string>? progress = null,
        DocsVersion? version = null,
        CancellationToken cancellationToken = default)
        => await PrepareMarkdownComparisonPreviewAsync(
                prNumber,
                sha,
                filePath,
                progress,
                version,
                [],
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<PreviewComparisonLink?> PrepareMarkdownComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        IProgress<string>? progress,
        DocsVersion? version,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!PreviewPathMapper.IsMarkdown(filePath))
        {
            throw new InvalidOperationException($"'{filePath}' は Markdown ファイルではありません。");
        }

        if (!_worktree.IsEnabled)
        {
            LogDisabled(_logger);
            return null;
        }

        var session = await EnsurePreparedSessionAsync(prNumber, sha, progress, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var requestedFilePath = filePath.Trim();
        var renderedFilePath = requestedFilePath;
        var reusableReferenceCount = 0;
        if (await TryResolveReusablePreviewTargetAsync(
                session.BeforeSha,
                sha,
                requestedFilePath,
                changedFilePaths,
                progress,
                cancellationToken)
            .ConfigureAwait(false) is { } reusableTarget)
        {
            renderedFilePath = reusableTarget.FilePath;
            reusableReferenceCount = reusableTarget.ReferenceCount;
            progress?.Report($"{requestedFilePath} は使用箇所 {renderedFilePath} でプレビューします");
        }

        progress?.Report($"{renderedFilePath} の変更前 Markdown を bare clone から読み込み中…");
        var beforeMarkdown = await _worktree.ReadFileTextAsync(session.BeforeSha, renderedFilePath, cancellationToken).ConfigureAwait(false);
        progress?.Report($"{renderedFilePath} の PR HEAD Markdown を bare clone から読み込み中…");
        var afterMarkdown = await _worktree.ReadFileTextAsync(sha, renderedFilePath, cancellationToken).ConfigureAwait(false);

        progress?.Report("変更前 Markdown の Liquid 変数・再利用ブロック・ページタイトルを読み込み中…");
        var beforeLiquid = await LoadLiquidContextCachedAsync(
            session.BeforeSha,
                renderedFilePath,
                beforeMarkdown,
                cancellationToken)
            .ConfigureAwait(false);
        progress?.Report("PR HEAD Markdown の Liquid 変数・再利用ブロック・ページタイトルを読み込み中…");
        var afterLiquid = await LoadLiquidContextCachedAsync(
            sha,
                renderedFilePath,
                afterMarkdown,
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report("公式版 (fpt/ghec/ghes) で差分の出る版を解析中…");
        var versionImpacts = DocsVersionImpactAnalyzer.AnalyzeDetails(
            beforeMarkdown,
            beforeLiquid,
            afterMarkdown,
            afterLiquid);
        var affectedVersions = versionImpacts.Select(static impact => impact.Version).ToArray();
        var effectiveVersion = version ?? ResolveInitialMarkdownPreviewVersion(affectedVersions);
        progress?.Report("フロントマターの変更点を解析中…");
        var frontmatterChanges = MarkdownFrontmatterDiffAnalyzer.Analyze(beforeMarkdown, afterMarkdown);
        progress?.Report("Liquid 条件と関連 data ファイルの差分を解析中…");
        var sourceDiff = MarkdownSourceDiffAnalyzer.Analyze(
            beforeMarkdown,
            afterMarkdown);
        var sourceChangeCount = frontmatterChanges.Count
            + sourceDiff.IfversionChanges.Count
            + sourceDiff.RelatedFileChanges.Sum(static file => file.Changes.Count);

        progress?.Report("変更前 Markdown を HTML に変換中…");
        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            renderedFilePath,
            beforeMarkdown,
            session.BeforeSha,
            "変更前",
            beforeLiquid,
            effectiveVersion,
            affectedVersions,
            versionImpacts: versionImpacts,
            frontmatterChanges: frontmatterChanges,
            sourceDiff: sourceDiff,
            assetBasePath: _markdownBeforeAssetRoute,
            diffAgainstMarkdown: afterMarkdown,
            diffAgainstLiquidContext: afterLiquid,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);

        progress?.Report("PR HEAD Markdown を HTML に変換中…");
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            renderedFilePath,
            afterMarkdown,
            sha,
            "PR HEAD",
            afterLiquid,
            effectiveVersion,
            affectedVersions,
            versionImpacts: versionImpacts,
            frontmatterChanges: frontmatterChanges,
            sourceDiff: sourceDiff,
            assetBasePath: _markdownAfterAssetRoute,
            diffAgainstMarkdown: beforeMarkdown,
            diffAgainstLiquidContext: beforeLiquid,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/markdown/before"] = beforeHtml,
            ["/markdown/after"] = afterHtml,
        };

        var (assetRoots, markdownAssetRoot) = await PrepareMarkdownAssetRootsAsync(
                session.BeforeSha,
                sha,
                beforeHtml,
                afterHtml,
                cancellationToken)
            .ConfigureAwait(false);

        var port = _portAllocator.AllocateSingle(_options.PreviewBasePort, GetReusablePorts());
        progress?.Report($"Markdown 比較プレビューを起動中… (ポート {port.ToString(CultureInfo.InvariantCulture)})");
        try
        {
            await _contentServer.StartAsync(port, pages, assetRoots, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteDirectoryBestEffort(markdownAssetRoot);
            throw;
        }
        ReplaceActiveMarkdownAssetRoot(markdownAssetRoot);
        _session.Activate(port);

        // §Step 19.9/19.10: バージョン切替でもファイル切替でも同じポートで
        // /markdown/before の内容を差し替える運用なので、URL に version slug と
        // file path を埋め込まないと WebView2 は「Source が変わっていない」と
        // 判断して navigation をスキップし、「変更前ページを準備中…」の
        // オーバーレイから先に進めなくなる。
        // LocalPreviewContentServer.NormalizeRoute は query を捨てるためルーティングには影響しない。
        var query = BuildMarkdownPreviewQuery(effectiveVersion, requestedFilePath, renderedFilePath);
        var beforeUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/markdown/before?{query}"));
        var afterUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/markdown/after?{query}"));
        LogMarkdownComparisonReady(_logger, session.BeforeSha, sha, renderedFilePath, beforeUrl.AbsoluteUri, afterUrl.AbsoluteUri);
        return new PreviewComparisonLink(
            beforeUrl,
            afterUrl,
            port,
            port,
            string.Empty,
            string.Empty,
            session.BeforeSha,
            sha)
        {
            CurrentVersion = effectiveVersion,
            AffectedVersions = affectedVersions,
            SourceChangeCount = sourceChangeCount,
            RequestedFilePath = requestedFilePath,
            RenderedFilePath = renderedFilePath,
            ReusableReferenceCount = reusableReferenceCount,
        };
    }

    private static string BuildMarkdownPreviewQuery(DocsVersion version, string filePath, string? renderedFilePath = null)
    {
        var trimmedFilePath = filePath.Trim();
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"v={Uri.EscapeDataString(version.Slug)}&file={Uri.EscapeDataString(trimmedFilePath)}");
        var trimmedRenderedPath = renderedFilePath?.Trim();
        return string.IsNullOrWhiteSpace(trimmedRenderedPath)
            || string.Equals(trimmedFilePath, trimmedRenderedPath, StringComparison.Ordinal)
            ? query
            : query + "&rendered=" + Uri.EscapeDataString(trimmedRenderedPath);
    }

    internal static DocsVersion ResolveInitialMarkdownPreviewVersion(IReadOnlyList<DocsVersion> affectedVersions)
    {
        ArgumentNullException.ThrowIfNull(affectedVersions);

        return affectedVersions.Count > 0
            ? affectedVersions[0]
            : DocsVersionCatalog.Default;
    }

    private async Task<ReusablePreviewTarget?> TryResolveReusablePreviewTargetAsync(
        string beforeSha,
        string afterSha,
        string filePath,
        IReadOnlyList<string>? changedFilePaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryBuildReusableKey(filePath, out var reusableKey))
        {
            return null;
        }

        var needle = "reusables." + reusableKey;
        progress?.Report($"{filePath} の使用箇所を content ページから検索中…");
        var beforeReferences = await _worktree.FindFilesContainingAsync(
                beforeSha,
                "content",
                needle,
                ".md",
                cancellationToken)
            .ConfigureAwait(false);
        var afterReferences = await _worktree.FindFilesContainingAsync(
                afterSha,
                "content",
                needle,
                ".md",
                cancellationToken)
            .ConfigureAwait(false);

        var candidates = beforeReferences
            .Concat(afterReferences)
            .Where(static path => path.StartsWith("content/", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            progress?.Report($"{filePath} の content 使用箇所が見つからないため、reusable 単体で表示します");
            return null;
        }

        var changed = changedFilePaths is { Count: > 0 }
            ? new HashSet<string>(changedFilePaths.Select(NormalizeRepoPathForComparison), StringComparer.Ordinal)
            : [];
        var beforeSet = new HashSet<string>(beforeReferences, StringComparer.Ordinal);
        var afterSet = new HashSet<string>(afterReferences, StringComparer.Ordinal);
        var selected = candidates
            .OrderBy(path => GetReusableReferencePriority(path, changed, beforeSet, afterSet))
            .ThenBy(static path => path, StringComparer.Ordinal)
            .First();
        return new ReusablePreviewTarget(selected, candidates.Length);
    }

    private static int GetReusableReferencePriority(
        string path,
        HashSet<string> changedFilePaths,
        HashSet<string> beforeReferences,
        HashSet<string> afterReferences)
    {
        if (changedFilePaths.Contains(NormalizeRepoPathForComparison(path)))
        {
            return 0;
        }
        if (beforeReferences.Contains(path) && afterReferences.Contains(path))
        {
            return 10;
        }
        if (afterReferences.Contains(path))
        {
            return 20;
        }
        return 30;
    }

    private static bool TryBuildReusableKey(string filePath, out string key)
    {
        key = string.Empty;
        var normalized = NormalizeRepoPathForComparison(filePath);
        const string prefix = "data/reusables/";
        const string suffix = ".md";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)
            || !normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var relative = normalized[prefix.Length..^suffix.Length];
        if (string.IsNullOrWhiteSpace(relative))
        {
            return false;
        }
        key = relative.Replace('/', '.');
        return true;
    }

    private static string NormalizeRepoPathForComparison(string path)
        => path.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// §Step 19.10 (perf): 同一 (prNumber, sha) で繰り返し呼ばれても重い前準備
    /// (git fetch / rev-parse / worktree add / data 配下の全件読み込み) を 1 回だけ走らせる。
    /// 2 回目以降はキャッシュヒットでほぼ即返る。<paramref name="cancellationToken"/> は
    /// 内部の git/I/O 操作にだけ流すので、キャンセル時にキャッシュは汚染されない。
    /// </summary>
    private async Task<PreparedMarkdownSession?> EnsurePreparedSessionAsync(
        int prNumber,
        string sha,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var key = new PreparedSessionKey(prNumber, sha);
        progress?.Report("このファイルの比較に使う準備済みデータを確認中…");
        if (await TryGetValidPreparedSessionAsync(key, progress, cancellationToken).ConfigureAwait(false) is { } fast)
        {
            progress?.Report("このファイルの比較に使う準備済みデータを再利用します");
            return fast;
        }

        var gate = _preparedSessionLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        if (gate.CurrentCount == 0)
        {
            progress?.Report("このファイルの比較に必要な PR データを準備中です…");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — a concurrent prewarm may have
            // just finished and populated the cache.
            if (await TryGetValidPreparedSessionAsync(key, progress, cancellationToken).ConfigureAwait(false) is { } slow)
            {
                progress?.Report("このファイルの比較に使う準備済みデータを再利用します");
                return slow;
            }

            progress?.Report("リポジトリを準備中… (初回は git clone --bare で 1〜2 分)");
            await _worktree.EnsureBareCloneAsync(cancellationToken).ConfigureAwait(false);

            await _worktree.EnsureCommitAvailableAsync(prNumber, sha, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("比較元の親コミットを解決中… (git rev-parse <sha>^)");
            var beforeSha = await _worktree.ResolveFirstParentAsync(sha, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(beforeSha))
            {
                throw new InvalidOperationException("比較元になる親コミットを解決できませんでした。");
            }

            var session = new PreparedMarkdownSession(beforeSha);
            _preparedSessions[key] = session;
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PreparedMarkdownSession?> TryGetValidPreparedSessionAsync(
        PreparedSessionKey key,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (_preparedSessions.TryGetValue(key, out var cached))
        {
            return cached;
        }
        return null;
    }

    private async Task<DocsLiquidContext> LoadLiquidContextCachedAsync(
        string commitSha,
        string filePath,
        string? markdown,
        CancellationToken cancellationToken)
    {
        var key = new LiquidContextCacheKey(commitSha, filePath);
        if (_liquidContextCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var loaded = await DocsLiquidContextLoader.LoadForMarkdownAsync(
                new GitCommitDocsFileSource(_worktree, commitSha),
                filePath,
                markdown,
                cancellationToken)
            .ConfigureAwait(false);
        // DocsLiquidContext.Empty を含めキャッシュに入れる: data/ 配下が無いのも
        // 一定の事実なので 2 回目以降のディスク I/O を避ける。
        _liquidContextCache[key] = loaded;
        return loaded;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _contentServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await _beforeServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        ReplaceActiveMarkdownAssetRoot(null);
        _session.Deactivate();
        _activeBeforeSha = null;
        _activeSha = null;
    }

    public async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        if (!_worktree.IsEnabled)
        {
            return;
        }
        await _worktree.EnsureBareCloneAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PredictivePrewarmAsync(int prNumber, string sha, CancellationToken cancellationToken = default)
    {
        if (!_worktree.IsEnabled || prNumber <= 0 || string.IsNullOrEmpty(sha))
        {
            return;
        }
        try
        {
            // §Step 19.10 (perf): 単に bare clone と git fetch を warm up するだけだと、
            // 実際に最初の Markdown ファイルをクリックしたタイミングで
            //   - git rev-parse <sha>^
            //   - git worktree add ×2
            // が直列で走る。EnsurePreparedSessionAsync を呼んでおけば
            // ユーザクリック時は対象MarkdownのLiquid contextとレンダリングだけで済む。
            // 同じキーに対する並列呼び出しは内部 SemaphoreSlim でシリアライズされる
            // ため DocsWorktreeManager の Dictionary レースは起こらない。
            await EnsurePreparedSessionAsync(prNumber, sha, progress: null, cancellationToken).ConfigureAwait(false);
            LogPredictivePrewarmCompleted(_logger, prNumber, sha);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — typically because the user picked a different PR
            // before this one finished. Don't log; the new selection's prewarm
            // will take over.
        }
        catch (Exception ex)
        {
            LogPredictivePrewarmFailed(_logger, prNumber, sha, ex);
        }
    }

    public async Task<int> CleanupCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!_worktree.IsEnabled)
        {
            LogDisabled(_logger);
            return 0;
        }

        // Stop the running server first so we don't try to remove a worktree that
        // still has a child process holding file handles inside it.
        await StopAsync(cancellationToken).ConfigureAwait(false);
        DeleteMarkdownAssetCacheRoot();
        var removed = await _worktree.PruneAllAsync(cancellationToken).ConfigureAwait(false);
        // PruneAllAsync の後は worktree ディレクトリが消えているので
        // _preparedSessions / _liquidContextCache を残しておくと TryGetValidPreparedSession
        // の Directory.Exists チェックでは弾けるものの無駄なメモリを抱え続けることになる。
        _preparedSessions.Clear();
        _liquidContextCache.Clear();
        return removed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Preview pipeline is disabled (DocsRepository.BareCloneDir or PreviewCommand empty).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Preview ready for sha {Sha}: {Url}")]
    private static partial void LogPreviewReady(ILogger logger, string sha, string url);

    private int[] GetReusablePorts()
    {
        var ports = new List<int>(capacity: 2);
        if (_server is { IsProcessRunning: true, CurrentPort: > 0 })
        {
            ports.Add(_server.CurrentPort);
        }
        if (_beforeServer is { IsProcessRunning: true, CurrentPort: > 0 }
            && !ports.Contains(_beforeServer.CurrentPort))
        {
            ports.Add(_beforeServer.CurrentPort);
        }
        if (_contentServer is { IsRunning: true, CurrentPort: > 0 }
            && !ports.Contains(_contentServer.CurrentPort))
        {
            ports.Add(_contentServer.CurrentPort);
        }
        return ports.ToArray();
    }

    private static async Task<string?> ReadWorktreeFileOrNullAsync(
        string worktreePath,
        string repoPath,
        CancellationToken cancellationToken)
    {
        var fullPath = ResolveWorktreeFilePath(worktreePath, repoPath);
        return File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static string ResolveWorktreeFilePath(string worktreePath, string repoPath)
    {
        var root = Path.GetFullPath(worktreePath);
        var relative = repoPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"worktree 外のパスはプレビューできません: {repoPath}");
        }
        return fullPath;
    }

    private async Task<(IReadOnlyDictionary<string, string> AssetRoots, string? MarkdownAssetRoot)> PrepareMarkdownAssetRootsAsync(
        string beforeSha,
        string afterSha,
        string beforeHtml,
        string afterHtml,
        CancellationToken cancellationToken)
    {
        var beforeAssets = ExtractMarkdownAssetRepoPaths(beforeHtml, _markdownBeforeAssetRoute);
        var afterAssets = ExtractMarkdownAssetRepoPaths(afterHtml, _markdownAfterAssetRoute);
        if (beforeAssets.Count == 0 && afterAssets.Count == 0)
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal), null);
        }

        var assetRoot = CreateMarkdownAssetRoot(beforeSha, afterSha);
        var assetRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (beforeAssets.Count > 0)
        {
            var beforeRoot = Path.Combine(assetRoot, "before");
            var materialized = await _worktree.MaterializeFilesAsync(beforeSha, beforeAssets, beforeRoot, cancellationToken).ConfigureAwait(false);
            if (materialized.Count > 0)
            {
                assetRoots[_markdownBeforeAssetRoute] = beforeRoot;
            }
        }

        if (afterAssets.Count > 0)
        {
            var afterRoot = Path.Combine(assetRoot, "after");
            var materialized = await _worktree.MaterializeFilesAsync(afterSha, afterAssets, afterRoot, cancellationToken).ConfigureAwait(false);
            if (materialized.Count > 0)
            {
                assetRoots[_markdownAfterAssetRoute] = afterRoot;
            }
        }

        if (assetRoots.Count == 0)
        {
            DeleteDirectoryBestEffort(assetRoot);
            return (new Dictionary<string, string>(StringComparer.Ordinal), null);
        }

        return (assetRoots, assetRoot);
    }

    private string CreateMarkdownAssetRoot(string beforeSha, string afterSha)
    {
        var cacheRoot = GetMarkdownAssetCacheRoot();
        Directory.CreateDirectory(cacheRoot);
        var beforeSlug = beforeSha.Length >= 7 ? beforeSha[..7] : beforeSha;
        var afterSlug = afterSha.Length >= 7 ? afterSha[..7] : afterSha;
        return Path.Combine(cacheRoot, beforeSlug + "-" + afterSlug + "-" + Guid.NewGuid().ToString("N"));
    }

    private string GetMarkdownAssetCacheRoot()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorktreeRoot))
        {
            return Path.Combine(_options.WorktreeRoot, ".markdown-assets");
        }

        var bareParent = Path.GetDirectoryName(_options.BareCloneDir);
        return Path.Combine(
            string.IsNullOrWhiteSpace(bareParent) ? Path.GetTempPath() : bareParent,
            "reposyncradar-markdown-assets");
    }

    private void ReplaceActiveMarkdownAssetRoot(string? markdownAssetRoot)
    {
        var previous = _activeMarkdownAssetRoot;
        _activeMarkdownAssetRoot = markdownAssetRoot;
        if (!string.Equals(previous, markdownAssetRoot, StringComparison.OrdinalIgnoreCase))
        {
            DeleteDirectoryBestEffort(previous);
        }
    }

    private void DeleteMarkdownAssetCacheRoot()
        => DeleteDirectoryBestEffort(GetMarkdownAssetCacheRoot());

    private static HashSet<string> ExtractMarkdownAssetRepoPaths(string html, string assetRoute)
    {
        var routePrefix = assetRoute.TrimEnd('/') + "/";
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in MarkdownAssetUrlRegex().Matches(html))
        {
            TryAddMarkdownAssetRepoPath(match.Groups["url"].Value, routePrefix, paths);
        }

        foreach (Match match in MarkdownAssetSrcSetRegex().Matches(html))
        {
            AddMarkdownAssetSrcSetRepoPaths(match.Groups["value"].Value, routePrefix, paths);
        }
        return paths;
    }

    private static void AddMarkdownAssetSrcSetRepoPaths(string srcset, string routePrefix, ISet<string> paths)
    {
        foreach (var candidate in WebUtility.HtmlDecode(srcset).Split(',', StringSplitOptions.None))
        {
            var core = candidate.Trim();
            if (core.Length == 0)
            {
                continue;
            }

            var whitespace = FindFirstWhitespace(core);
            var url = whitespace < 0 ? core : core[..whitespace];
            TryAddMarkdownAssetRepoPath(url, routePrefix, paths);
        }
    }

    private static void TryAddMarkdownAssetRepoPath(string url, string routePrefix, ISet<string> paths)
    {
        var decodedUrl = WebUtility.HtmlDecode(url).Trim();
        if (decodedUrl.Length == 0)
        {
            return;
        }

        var suffixStart = FindUrlSuffixStart(decodedUrl);
        var route = suffixStart < 0 ? decodedUrl : decodedUrl[..suffixStart];
        if (!route.StartsWith(routePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var relative = route[routePrefix.Length..];
        if (TryNormalizeMarkdownAssetRepoPath(relative, out var repoPath))
        {
            paths.Add(repoPath);
        }
    }

    private static bool TryNormalizeMarkdownAssetRepoPath(string relativeRoute, out string repoPath)
    {
        var parts = new List<string>();
        foreach (var segment in relativeRoute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0
                || decoded.Equals(".", StringComparison.Ordinal)
                || decoded.Equals("..", StringComparison.Ordinal)
                || decoded.Contains('/', StringComparison.Ordinal)
                || decoded.Contains('\\', StringComparison.Ordinal))
            {
                repoPath = string.Empty;
                return false;
            }
            parts.Add(decoded);
        }

        repoPath = string.Join('/', parts);
        return repoPath.Length > 0;
    }

    private static int FindUrlSuffixStart(string url)
    {
        var queryIndex = url.IndexOf('?');
        var fragmentIndex = url.IndexOf('#');
        return (queryIndex, fragmentIndex) switch
        {
            (>= 0, >= 0) => Math.Min(queryIndex, fragmentIndex),
            (>= 0, _) => queryIndex,
            (_, >= 0) => fragmentIndex,
            _ => -1,
        };
    }

    private static int FindFirstWhitespace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                return i;
            }
        }
        return -1;
    }

    private static void DeleteDirectoryBestEffort(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Markdown asset directories are short-lived cache entries.
        }
    }

    [GeneratedRegex("""\b(?:src|poster)\s*=\s*(?<quote>["'])(?<url>[^"']+)\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownAssetUrlRegex();

    [GeneratedRegex("""\bsrcset\s*=\s*(?<quote>["'])(?<value>[^"']+)\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownAssetSrcSetRegex();

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Preview comparison ready before {BeforeSha} / after {AfterSha}: {BeforeUrl} -> {AfterUrl}")]
    private static partial void LogPreviewComparisonReady(
        ILogger logger,
        string beforeSha,
        string afterSha,
        string beforeUrl,
        string afterUrl);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Markdown preview comparison ready for {FilePath} before {BeforeSha} / after {AfterSha}: {BeforeUrl} -> {AfterUrl}")]
    private static partial void LogMarkdownComparisonReady(
        ILogger logger,
        string beforeSha,
        string afterSha,
        string filePath,
        string beforeUrl,
        string afterUrl);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "Predictive preview prewarm completed for PR #{PrNumber} (sha {Sha}); subsequent click should skip git fetch.")]
    private static partial void LogPredictivePrewarmCompleted(ILogger logger, int prNumber, string sha);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Predictive preview prewarm failed for PR #{PrNumber} (sha {Sha}); the regular click path will retry.")]
    private static partial void LogPredictivePrewarmFailed(ILogger logger, int prNumber, string sha, Exception exception);
}
