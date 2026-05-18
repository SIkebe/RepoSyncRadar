using System.Collections.Concurrent;
using System.Globalization;
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
    /// 描画する <see cref="DocsVersion"/>。未指定なら <see cref="DocsVersionCatalog.Default"/> (= fpt)。
    /// <c>{% ifversion ... %}</c> がこの版で評価される。
    /// </param>
    Task<PreviewComparisonLink?> PrepareMarkdownComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        IProgress<string>? progress = null,
        DocsVersion? version = null,
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
    /// Predictively warms everything needed to render Markdown comparison previews
    /// for a freshly selected commit so the user's first "ローカルプレビュー" click
    /// only pays the per-file Markdown render cost (typically 10-50 ms). Performs
    /// <see cref="DocsWorktreeManager.EnsureBareCloneAsync"/>,
    /// <see cref="DocsWorktreeManager.FetchPrAsync"/>,
    /// <see cref="DocsWorktreeManager.ResolveFirstParentAsync"/>,
    /// <see cref="DocsWorktreeManager.CheckoutAsync"/> for both the before/after
    /// commits, and loads the <c>DocsLiquidContext</c> (data/variables and
    /// data/reusables, thousands of files for the public docs repo). All work
    /// is serialized through a per-(prNumber, sha) <see cref="SemaphoreSlim"/>,
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
}

/// <inheritdoc cref="IPreviewCoordinator" />
public sealed partial class PreviewCoordinator : IPreviewCoordinator
{
    private const string MarkdownBeforeAssetRoute = "/markdown-assets/before";
    private const string MarkdownAfterAssetRoute = "/markdown-assets/after";

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

    // §Step 19.10 (perf): 同一 PR HEAD で 1/7 → 2/7 → 3/7 のように別ファイルへ
    // 切り替えるたびに毎回 git fetch + git rev-parse + git worktree add ×2 +
    // data/variables/**/*.yml と data/reusables/**/*.md の全件読み込みを走らせると、
    // 公式 docs (数千ファイル) では 1 ファイル切り替えに数秒〜10 秒掛かる。
    // (prNumber, sha) ごとに「準備済みセッション」をキャッシュして、ファイル切替の
    // たびに走らせるのは ReadWorktreeFile + Markdig レンダリングだけにする。
    private readonly ConcurrentDictionary<PreparedSessionKey, PreparedMarkdownSession> _preparedSessions = new();

    // (prNumber, sha) ごとの準備処理は 1 本にシリアライズする。先読み (PredictivePrewarmAsync)
    // とユーザクリック (PrepareMarkdownComparisonPreviewAsync) が同時に走っても、後発は
    // 先発の完了を待ってキャッシュヒットだけで終わる。DocsWorktreeManager の内部
    // Dictionary は thread-safe ではないので、これを介して保護する。
    private readonly ConcurrentDictionary<PreparedSessionKey, SemaphoreSlim> _preparedSessionLocks = new();

    // 同じ worktreePath なら data/variables / data/reusables の中身は不変なので、
    // 1 回読んだ DocsLiquidContext は何度でも使い回す。
    private readonly ConcurrentDictionary<string, DocsLiquidContext> _liquidContextCache
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct PreparedSessionKey(int PrNumber, string Sha);

    private sealed record PreparedMarkdownSession(
        string BeforeSha,
        string BeforeWorktreePath,
        string AfterWorktreePath,
        DocsLiquidContext BeforeLiquid,
        DocsLiquidContext AfterLiquid);

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

        progress?.Report($"PR #{prNumber.ToString(CultureInfo.InvariantCulture)} を取得中… (git fetch)");
        await _worktree.FetchPrAsync(prNumber, cancellationToken).ConfigureAwait(false);

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

        progress?.Report($"PR #{prNumber.ToString(CultureInfo.InvariantCulture)} を取得中… (git fetch)");
        await _worktree.FetchPrAsync(prNumber, cancellationToken).ConfigureAwait(false);

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
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!PreviewPathMapper.IsMarkdown(filePath))
        {
            throw new InvalidOperationException($"'{filePath}' は Markdown ファイルではありません。");
        }

        var effectiveVersion = version ?? DocsVersionCatalog.Default;

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

        progress?.Report($"{filePath} の変更前 Markdown を読み込み中…");
        var beforeMarkdown = await ReadWorktreeFileOrNullAsync(session.BeforeWorktreePath, filePath, cancellationToken).ConfigureAwait(false);
        progress?.Report($"{filePath} の PR HEAD Markdown を読み込み中…");
        var afterMarkdown = await ReadWorktreeFileOrNullAsync(session.AfterWorktreePath, filePath, cancellationToken).ConfigureAwait(false);

        progress?.Report("公式版 (fpt/ghec/ghes) で差分の出る版を解析中…");
        var versionImpacts = DocsVersionImpactAnalyzer.AnalyzeDetails(
            beforeMarkdown,
            session.BeforeLiquid,
            afterMarkdown,
            session.AfterLiquid);
        var affectedVersions = versionImpacts.Select(static impact => impact.Version).ToArray();
        progress?.Report("フロントマターの変更点を解析中…");
        var frontmatterChanges = MarkdownFrontmatterDiffAnalyzer.Analyze(beforeMarkdown, afterMarkdown);

        progress?.Report("変更前 Markdown を HTML に変換中…");
        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            filePath,
            beforeMarkdown,
            session.BeforeSha,
            "変更前",
            session.BeforeLiquid,
            effectiveVersion,
            affectedVersions,
            versionImpacts: versionImpacts,
            frontmatterChanges: frontmatterChanges,
            assetBasePath: MarkdownBeforeAssetRoute);

        progress?.Report("PR HEAD Markdown を HTML に変換中…");
        var afterHtml = MarkdownPreviewRenderer.RenderDocument(
            filePath,
            afterMarkdown,
            sha,
            "PR HEAD",
            session.AfterLiquid,
            effectiveVersion,
            affectedVersions,
            versionImpacts: versionImpacts,
            frontmatterChanges: frontmatterChanges,
            assetBasePath: MarkdownAfterAssetRoute);

        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/markdown/before"] = beforeHtml,
            ["/markdown/after"] = afterHtml,
        };

        var assetRoots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MarkdownBeforeAssetRoute] = session.BeforeWorktreePath,
            [MarkdownAfterAssetRoute] = session.AfterWorktreePath,
        };

        var port = _portAllocator.AllocateSingle(_options.PreviewBasePort, GetReusablePorts());
        progress?.Report($"Markdown 比較プレビューを起動中… (ポート {port.ToString(CultureInfo.InvariantCulture)})");
            await _contentServer.StartAsync(port, pages, assetRoots, cancellationToken).ConfigureAwait(false);
        _session.Activate(port);

        // §Step 19.9/19.10: バージョン切替でもファイル切替でも同じポートで
        // /markdown/before の内容を差し替える運用なので、URL に version slug と
        // file path を埋め込まないと WebView2 は「Source が変わっていない」と
        // 判断して navigation をスキップし、「変更前ページを準備中…」の
        // オーバーレイから先に進めなくなる。
        // LocalPreviewContentServer.NormalizeRoute は query を捨てるためルーティングには影響しない。
        var query = BuildMarkdownPreviewQuery(effectiveVersion, filePath);
        var beforeUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/markdown/before?{query}"));
        var afterUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/markdown/after?{query}"));
        LogMarkdownComparisonReady(_logger, session.BeforeSha, sha, filePath, beforeUrl.AbsoluteUri, afterUrl.AbsoluteUri);
        return new PreviewComparisonLink(
            beforeUrl,
            afterUrl,
            port,
            port,
            session.BeforeWorktreePath,
            session.AfterWorktreePath,
            session.BeforeSha,
            sha)
        {
            CurrentVersion = effectiveVersion,
            AffectedVersions = affectedVersions,
        };
    }

    private static string BuildMarkdownPreviewQuery(DocsVersion version, string filePath)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"v={Uri.EscapeDataString(version.Slug)}&file={Uri.EscapeDataString(filePath.Trim())}");

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
        progress?.Report("Markdown 比較キャッシュを確認中…");
        if (await TryGetValidPreparedSessionAsync(key, progress, cancellationToken).ConfigureAwait(false) is { } fast)
        {
            progress?.Report("準備済みの Markdown 比較キャッシュを再利用します");
            return fast;
        }

        var gate = _preparedSessionLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        if (gate.CurrentCount == 0)
        {
            progress?.Report("同じ PR の Markdown 比較準備が完了するのを待機中…");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — a concurrent prewarm may have
            // just finished and populated the cache.
            if (await TryGetValidPreparedSessionAsync(key, progress, cancellationToken).ConfigureAwait(false) is { } slow)
            {
                progress?.Report("直前に完了した Markdown 比較キャッシュを再利用します");
                return slow;
            }

            progress?.Report("リポジトリを準備中… (初回は git clone --bare で 1〜2 分)");
            await _worktree.EnsureBareCloneAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report($"PR #{prNumber.ToString(CultureInfo.InvariantCulture)} を取得中… (git fetch)");
            await _worktree.FetchPrAsync(prNumber, cancellationToken).ConfigureAwait(false);

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

            progress?.Report("変更前 worktree の Liquid 変数・再利用ブロック・ページタイトルを読み込み中…");
            var beforeLiquid = await LoadLiquidContextCachedAsync(beforeWorktreePath, cancellationToken).ConfigureAwait(false);
            progress?.Report("PR HEAD worktree の Liquid 変数・再利用ブロック・ページタイトルを読み込み中…");
            var afterLiquid = await LoadLiquidContextCachedAsync(afterWorktreePath, cancellationToken).ConfigureAwait(false);

            var session = new PreparedMarkdownSession(
                beforeSha,
                beforeWorktreePath,
                afterWorktreePath,
                beforeLiquid,
                afterLiquid);
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
        if (_preparedSessions.TryGetValue(key, out var cached)
            && Directory.Exists(cached.BeforeWorktreePath)
            && Directory.Exists(cached.AfterWorktreePath)
            && await TryRepairPreparedWorktreeAsync("変更前", cached.BeforeWorktreePath, progress, cancellationToken).ConfigureAwait(false)
            && await TryRepairPreparedWorktreeAsync("PR HEAD", cached.AfterWorktreePath, progress, cancellationToken).ConfigureAwait(false))
        {
            return cached;
        }
        // Worktree directory was pruned out from under us (e.g. CleanupCacheAsync
        // on another caller). Drop the stale entry so the next call rebuilds.
        if (cached is not null)
        {
            _preparedSessions.TryRemove(key, out _);
        }
        return null;
    }

    private async Task<bool> TryRepairPreparedWorktreeAsync(
        string label,
        string worktreePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"キャッシュ済み {label} worktree の状態を検証中…");
        return await _worktree.TryRepairExistingWorktreeAsync(worktreePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocsLiquidContext> LoadLiquidContextCachedAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        if (_liquidContextCache.TryGetValue(worktreePath, out var cached))
        {
            return cached;
        }
        var loaded = await DocsLiquidContextLoader.LoadAsync(worktreePath, cancellationToken).ConfigureAwait(false);
        // DocsLiquidContext.Empty を含めキャッシュに入れる: data/ 配下が無いのも
        // 一定の事実なので 2 回目以降のディスク I/O を避ける。
        _liquidContextCache[worktreePath] = loaded;
        return loaded;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _contentServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await _beforeServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
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
            //   - data/variables / data/reusables の全件読み込み
            // が直列で走り、1/7 → 2/7 へのファイル切替時にも data 配下の I/O が
            // 再走するため重く感じられる。EnsurePreparedSessionAsync を呼んでおけば
            // ユーザクリック時はファイル単位の Markdown レンダリングだけで済む。
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
