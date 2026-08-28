using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Thin orchestrator that ties <see cref="DocsWorktreeManager"/>,
/// <see cref="ILocalPreviewContentServer"/> and <see cref="PreviewSession"/>
/// together so the UI only has to know about a single async method to render a
/// per-commit Markdown comparison preview.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator caches per-PR/sha preparation so switching files within the
/// same commit reuses the fetched commits and parent resolution.
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
    /// Renders a non-publishable Markdown file from the first parent and PR HEAD
    /// commits, hosts both rendered pages on localhost, and returns before/after URLs.
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

    Task<PreviewComparisonLink?> PrepareMarkdownReusableComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        string? renderedFilePath,
        IProgress<string>? progress,
        DocsVersion? version,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes a Markdown file without starting the preview server so the file list can
    /// identify renames, rendered-body changes, and source-only changes before the user
    /// opens the comparison.
    /// </summary>
    Task<MarkdownFileChangeSummary?> AnalyzeMarkdownFileChangeAsync(
        int prNumber,
        string sha,
        string filePath,
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
    /// Predictively warms the repository and first-parent resolution for a freshly
    /// selected commit so the user's first "ローカルプレビュー" click does not pay
    /// the network fetch or parent resolution cost when the data is already local. Performs
    /// <see cref="DocsWorktreeManager.EnsureBareCloneAsync"/>,
    /// commit availability check / PR fetch when needed,
    /// and <see cref="DocsWorktreeManager.ResolveFirstParentAsync"/>.
    /// Per-file Liquid context is loaded lazily from only the clicked
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
    /// Predictively renders one Markdown comparison without replacing the pages
    /// currently hosted by the local preview server. The next regular prepare call
    /// for the same file and version consumes the cached render.
    /// </summary>
    Task PredictivePrewarmFileAsync(
        int prNumber,
        string sha,
        string filePath,
        DocsVersion? version = null,
        IReadOnlyList<string>? changedFilePaths = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running preview and removes cached Markdown assets plus legacy
    /// worktrees left by older preview implementations. Returns the number of
    /// legacy worktree directories actually removed. Returns 0 when the preview
    /// pipeline is disabled.
    /// </summary>
    /// <remarks>
    /// Wired to the UI &quot;キャッシュをクリーンアップ&quot; button so users can reclaim
    /// disk space without leaving the app or memorizing git commands.
    /// </remarks>
    Task<int> CleanupCacheAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a local before/after visual preview comparison.</summary>
public sealed record PreviewComparisonLink(
    Uri BeforeUrl,
    Uri AfterUrl,
    int BeforePort,
    int AfterPort,
    string BeforeSha,
    string AfterSha)
{
    /// <summary>
    /// Markdown プレビューで描画したときの <see cref="DocsVersion"/>。
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

    /// <summary>reusable を展開して確認できる参照元 content ページ。</summary>
    public IReadOnlyList<string> ReusableReferencePaths { get; init; } = [];
}

/// <summary>Lightweight Markdown change information available before opening a preview.</summary>
public sealed record MarkdownFileChangeSummary(
    bool IsRenamed,
    string? PreviousPath,
    bool HasRenderedBodyChanges,
    int FrontmatterChangeCount,
    MarkdownSourceChangeSummary? SourceChange = null)
{
    public IReadOnlyList<string> ReusableReferencePaths { get; init; } = [];
}

internal sealed record ReusablePreviewTarget(
    string FilePath,
    IReadOnlyList<string> ReferencePaths);

/// <inheritdoc cref="IPreviewCoordinator" />
public sealed partial class PreviewCoordinator : IPreviewCoordinator
{
    private const string _markdownBeforeAssetRoute = "/markdown-assets/before";
    private const string _markdownAfterAssetRoute = "/markdown-assets/after";

    private readonly DocsWorktreeManager _worktree;
    private readonly ILocalPreviewContentServer _contentServer;
    private readonly PreviewSession _session;
    private readonly IPreviewPortAllocator _portAllocator;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewCoordinator> _logger;
    private string? _activeMarkdownAssetRoot;

    // §Step 19.10 (perf): 同一 PR HEAD で 1/7 → 2/7 → 3/7 のように別ファイルへ
    // 切り替えるたびに毎回 git fetch + git rev-parse を走らせると、
    // 公式 docs (数千ファイル) では不要な待ち時間が発生する。
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
    private readonly ConcurrentDictionary<PreparedPageKey, Lazy<Task<PreparedMarkdownPage?>>> _preparedPageOperations = new();
    private long _markdownPreviewGeneration;

    private readonly record struct PreparedSessionKey(int PrNumber, string Sha);

    private readonly record struct LiquidContextCacheKey(string CommitSha, string FilePath);

    private readonly record struct PreparedPageKey(
        int PrNumber,
        string Sha,
        string FilePath,
        string ReusableRenderedFilePath,
        string VersionSlug,
        string ChangedFilePathsKey);

    private sealed record PreparedMarkdownSession(
        string BeforeSha,
        IReadOnlyDictionary<string, string> PreviousPaths);

    private sealed record PreparedMarkdownPage(
        string BeforeSha,
        string RequestedFilePath,
        string RenderedFilePath,
        IReadOnlyList<string> ReusableReferencePaths,
        DocsVersion EffectiveVersion,
        IReadOnlyList<DocsVersion> AffectedVersions,
        int SourceChangeCount,
        string BeforeHtml,
        string AfterHtml);

    private sealed record MarkdownComparisonSources(
        string BeforeFilePath,
        string? BeforeMarkdown,
        string? AfterMarkdown,
        DocsLiquidContext BeforeLiquid,
        DocsLiquidContext AfterLiquid);

    public PreviewCoordinator(
        DocsWorktreeManager worktree,
        ILocalPreviewContentServer contentServer,
        PreviewSession session,
        IPreviewPortAllocator portAllocator,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(contentServer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(portAllocator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _worktree = worktree;
        _contentServer = contentServer;
        _session = session;
        _portAllocator = portAllocator;
        _options = options.Value;
        _logger = logger;
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
        => await PrepareMarkdownReusableComparisonPreviewAsync(
                prNumber,
                sha,
                filePath,
                renderedFilePath: null,
                progress,
                version,
                changedFilePaths,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<PreviewComparisonLink?> PrepareMarkdownReusableComparisonPreviewAsync(
        int prNumber,
        string sha,
        string filePath,
        string? renderedFilePath,
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

        var changedPaths = changedFilePaths ?? [];
        var key = BuildPreparedPageKey(
            prNumber,
            sha,
            filePath,
            renderedFilePath,
            version,
            changedPaths);
        var prepared = await GetOrPrepareMarkdownPageAsync(
                key,
                version,
                changedPaths,
                consumePredictiveCache: true,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (prepared is null)
        {
            return null;
        }

        var pages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/markdown/before"] = prepared.BeforeHtml,
            ["/markdown/after"] = prepared.AfterHtml,
        };

        var (assetRoots, markdownAssetRoot) = await PrepareMarkdownAssetRootsAsync(
                prepared.BeforeSha,
                sha,
                prepared.BeforeHtml,
                prepared.AfterHtml,
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
        // r はレンダリング世代。同じ version/file の再生成でも WebView2 に別 URL として
        // 認識させ、古い DOM/HTTP cache の表示を避ける。
        var renderGeneration = Interlocked.Increment(ref _markdownPreviewGeneration);
        var query = BuildMarkdownPreviewQuery(
            prepared.EffectiveVersion,
            prepared.RequestedFilePath,
            prepared.RenderedFilePath,
            renderGeneration);
        var beforeUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/markdown/before?{query}"));
        var afterUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/markdown/after?{query}"));
        LogMarkdownComparisonReady(_logger, prepared.BeforeSha, sha, prepared.RenderedFilePath, beforeUrl.AbsoluteUri, afterUrl.AbsoluteUri);
        return new PreviewComparisonLink(
            beforeUrl,
            afterUrl,
            port,
            port,
            prepared.BeforeSha,
            sha)
        {
            CurrentVersion = prepared.EffectiveVersion,
            AffectedVersions = prepared.AffectedVersions,
            SourceChangeCount = prepared.SourceChangeCount,
            RequestedFilePath = prepared.RequestedFilePath,
            RenderedFilePath = prepared.RenderedFilePath,
            ReusableReferenceCount = prepared.ReusableReferencePaths.Count,
            ReusableReferencePaths = prepared.ReusableReferencePaths,
        };
    }

    public async Task<MarkdownFileChangeSummary?> AnalyzeMarkdownFileChangeAsync(
        int prNumber,
        string sha,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!_worktree.IsEnabled || !PreviewPathMapper.IsMarkdown(filePath))
        {
            return null;
        }

        try
        {
            var session = await EnsurePreparedSessionAsync(
                    prNumber,
                    sha,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                return null;
            }

            IReadOnlyList<string> reusableReferencePaths = [];
            if (TryBuildReusableKey(filePath, out _))
            {
                var reusableTarget = await TryResolveReusablePreviewTargetAsync(
                        session.BeforeSha,
                        sha,
                        filePath,
                        preferredReferencePath: null,
                        changedFilePaths: null,
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                reusableReferencePaths = reusableTarget?.ReferencePaths ?? [];
            }

            var sources = await LoadMarkdownComparisonSourcesAsync(
                    session,
                    sha,
                    filePath.Trim(),
                    progress: null,
                    cacheLiquidContexts: false,
                    cancellationToken)
                .ConfigureAwait(false);
            var affectedVersions = DocsVersionImpactAnalyzer.AnalyzeCancellable(
                sources.BeforeMarkdown,
                sources.BeforeLiquid,
                sources.AfterMarkdown,
                sources.AfterLiquid,
                sources.BeforeFilePath,
                filePath.Trim(),
                cancellationToken);
            var frontmatterChanges = MarkdownFrontmatterDiffAnalyzer
                .Analyze(sources.BeforeMarkdown, sources.AfterMarkdown);
            var previousPath = string.Equals(sources.BeforeFilePath, filePath, StringComparison.Ordinal)
                ? null
                : sources.BeforeFilePath;
            return new MarkdownFileChangeSummary(
                IsRenamed: previousPath is not null,
                PreviousPath: previousPath,
                HasRenderedBodyChanges: affectedVersions.Count > 0,
                FrontmatterChangeCount: frontmatterChanges.Count,
                SourceChange: affectedVersions.Count == 0
                    ? MarkdownSourceChangeAnalyzer.Analyze(
                        sources.BeforeMarkdown,
                        sources.AfterMarkdown,
                        frontmatterChanges)
                    : null)
            {
                ReusableReferencePaths = reusableReferencePaths,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMarkdownFileChangeAnalysisFailed(_logger, prNumber, sha, filePath, ex);
            return null;
        }
    }

    private static PreparedPageKey BuildPreparedPageKey(
        int prNumber,
        string sha,
        string filePath,
        string? reusableRenderedFilePath,
        DocsVersion? version,
        IReadOnlyList<string> changedFilePaths)
    {
        var changedFilePathsKey = string.Join(
            '\n',
            changedFilePaths
                .Select(NormalizeRepoPathForComparison)
                .OrderBy(static path => path, StringComparer.Ordinal));
        return new PreparedPageKey(
            prNumber,
            sha.Trim(),
            NormalizeRepoPathForComparison(filePath),
            NormalizeRepoPathForComparison(reusableRenderedFilePath ?? string.Empty),
            version?.Slug ?? string.Empty,
            changedFilePathsKey);
    }

    private async Task<PreparedMarkdownPage?> GetOrPrepareMarkdownPageAsync(
        PreparedPageKey key,
        DocsVersion? version,
        IReadOnlyList<string> changedFilePaths,
        bool consumePredictiveCache,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!consumePredictiveCache)
        {
            RemoveCompletedPreparedPageOperationsExcept(key);
        }

        var candidate = new Lazy<Task<PreparedMarkdownPage?>>(
            () => PrepareMarkdownPageCoreAsync(
                key.PrNumber,
                key.Sha,
                key.FilePath,
                key.ReusableRenderedFilePath,
                version,
                changedFilePaths,
                progress,
                cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var operation = _preparedPageOperations.GetOrAdd(key, candidate);
        var reusedOperation = !ReferenceEquals(operation, candidate);
        if (consumePredictiveCache && reusedOperation)
        {
            progress?.Report($"{key.FilePath} の先読み済みプレビューを再利用します");
        }

        Task<PreparedMarkdownPage?> task;
        try
        {
            task = operation.Value;
            _ = task.ContinueWith(
                completed =>
                {
                    if (completed.IsCanceled || completed.IsFaulted)
                    {
                        _preparedPageOperations.TryRemove(
                            new KeyValuePair<PreparedPageKey, Lazy<Task<PreparedMarkdownPage?>>>(key, operation));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (consumePredictiveCache)
            {
                _preparedPageOperations.TryRemove(
                    new KeyValuePair<PreparedPageKey, Lazy<Task<PreparedMarkdownPage?>>>(key, operation));
            }
        }
    }

    private void RemoveCompletedPreparedPageOperationsExcept(PreparedPageKey retainedKey)
    {
        foreach (var entry in _preparedPageOperations)
        {
            if (!entry.Key.Equals(retainedKey)
                && entry.Value.IsValueCreated
                && entry.Value.Value.IsCompleted)
            {
                _preparedPageOperations.TryRemove(entry);
            }
        }
    }

    private async Task<PreparedMarkdownPage?> PrepareMarkdownPageCoreAsync(
        int prNumber,
        string sha,
        string filePath,
        string? reusableRenderedFilePath,
        DocsVersion? version,
        IReadOnlyList<string> changedFilePaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var session = await EnsurePreparedSessionAsync(prNumber, sha, progress, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var requestedFilePath = filePath.Trim();
        var renderedFilePath = requestedFilePath;
        IReadOnlyList<string> reusableReferencePaths = [];
        if (await TryResolveReusablePreviewTargetAsync(
                session.BeforeSha,
                sha,
                requestedFilePath,
                reusableRenderedFilePath,
                changedFilePaths,
                progress,
                cancellationToken)
            .ConfigureAwait(false) is { } reusableTarget)
        {
            renderedFilePath = reusableTarget.FilePath;
            reusableReferencePaths = reusableTarget.ReferencePaths;
            progress?.Report($"{requestedFilePath} は使用箇所 {renderedFilePath} でプレビューします");
        }

        var sources = await LoadMarkdownComparisonSourcesAsync(
                session,
                sha,
                renderedFilePath,
                progress,
                cacheLiquidContexts: true,
                cancellationToken)
            .ConfigureAwait(false);
        var beforeMarkdown = sources.BeforeMarkdown;
        var afterMarkdown = sources.AfterMarkdown;
        var beforeLiquid = sources.BeforeLiquid;
        var afterLiquid = sources.AfterLiquid;

        progress?.Report("公式版 (fpt/ghec/ghes) で差分の出る版を解析中…");
        var versionImpacts = DocsVersionImpactAnalyzer.AnalyzeDetailsCancellable(
            beforeMarkdown,
            beforeLiquid,
            afterMarkdown,
            afterLiquid,
            sources.BeforeFilePath,
            renderedFilePath,
            cancellationToken);
        var affectedVersions = versionImpacts.Select(static impact => impact.Version).ToArray();
        var effectiveVersion = version ?? ResolveInitialMarkdownPreviewVersion(affectedVersions);
        progress?.Report("フロントマターの変更点を解析中…");
        var frontmatterChanges = MarkdownFrontmatterDiffAnalyzer.Analyze(beforeMarkdown, afterMarkdown);
        progress?.Report("Liquid 条件と関連 data ファイルの差分を解析中…");
        var sourceDiff = MarkdownSourceDiffAnalyzer.Analyze(beforeMarkdown, afterMarkdown);
        var sourceChangeCount = frontmatterChanges.Count
            + sourceDiff.IfversionChanges.Count
            + sourceDiff.RelatedFileChanges.Sum(static file => file.Changes.Count);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("変更前 Markdown を HTML に変換中…");
        var beforeHtml = MarkdownPreviewRenderer.RenderDocument(
            sources.BeforeFilePath,
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
            diffAgainstRepoPath: renderedFilePath,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before);

        cancellationToken.ThrowIfCancellationRequested();
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
            diffAgainstRepoPath: sources.BeforeFilePath,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedMarkdownPage(
            session.BeforeSha,
            requestedFilePath,
            renderedFilePath,
            reusableReferencePaths,
            effectiveVersion,
            affectedVersions,
            sourceChangeCount,
            beforeHtml,
            afterHtml);
    }

    private async Task<MarkdownComparisonSources> LoadMarkdownComparisonSourcesAsync(
        PreparedMarkdownSession session,
        string afterSha,
        string afterFilePath,
        IProgress<string>? progress,
        bool cacheLiquidContexts,
        CancellationToken cancellationToken)
    {
        progress?.Report($"{afterFilePath} の変更前 Markdown を bare clone から読み込み中…");
        var beforeMarkdown = await _worktree
            .ReadFileTextAsync(session.BeforeSha, afterFilePath, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report($"{afterFilePath} の PR HEAD Markdown を bare clone から読み込み中…");
        var afterMarkdown = await _worktree
            .ReadFileTextAsync(afterSha, afterFilePath, cancellationToken)
            .ConfigureAwait(false);
        var beforeFilePath = afterFilePath;
        if (beforeMarkdown is null && afterMarkdown is not null)
        {
            if (session.PreviousPaths.TryGetValue(afterFilePath, out var previousPath))
            {
                beforeFilePath = previousPath;
                progress?.Report($"{afterFilePath} の変更前パス {previousPath} を読み込み中…");
                beforeMarkdown = await _worktree
                    .ReadFileTextAsync(session.BeforeSha, previousPath, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        progress?.Report("変更前 Markdown の Liquid 変数・再利用ブロック・ページタイトルを読み込み中…");
        var beforeLiquid = await LoadLiquidContextAsync(
                session.BeforeSha,
                beforeFilePath,
                beforeMarkdown,
                cacheLiquidContexts,
                cancellationToken)
            .ConfigureAwait(false);
        progress?.Report("PR HEAD Markdown の Liquid 変数・再利用ブロック・ページタイトルを読み込み中…");
        var afterLiquid = await LoadLiquidContextAsync(
                afterSha,
                afterFilePath,
                afterMarkdown,
                cacheLiquidContexts,
                cancellationToken)
            .ConfigureAwait(false);
        return new MarkdownComparisonSources(
            beforeFilePath,
            beforeMarkdown,
            afterMarkdown,
            beforeLiquid,
            afterLiquid);
    }

    private static string BuildMarkdownPreviewQuery(
        DocsVersion version,
        string filePath,
        string? renderedFilePath = null,
        long renderGeneration = 0)
    {
        var trimmedFilePath = filePath.Trim();
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"v={Uri.EscapeDataString(version.Slug)}&file={Uri.EscapeDataString(trimmedFilePath)}");
        var trimmedRenderedPath = renderedFilePath?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedRenderedPath)
            && !string.Equals(trimmedFilePath, trimmedRenderedPath, StringComparison.Ordinal))
        {
            query += "&rendered=" + Uri.EscapeDataString(trimmedRenderedPath);
        }
        if (renderGeneration > 0)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&r={renderGeneration}");
        }
        return query;
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
        string? preferredReferencePath,
        IReadOnlyList<string>? changedFilePaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryBuildReusableKey(filePath, out var reusableKey))
        {
            return null;
        }

        progress?.Report($"{filePath} の使用箇所を content ページから検索中…");
        var beforeReferences = await FindAffectedContentReferencesAsync(
                beforeSha,
                reusableKey,
                cancellationToken)
            .ConfigureAwait(false);
        var afterReferences = await FindAffectedContentReferencesAsync(
                afterSha,
                reusableKey,
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
        var orderedCandidates = candidates
            .OrderBy(path => GetReusableReferencePriority(path, changed, beforeSet, afterSet))
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var normalizedPreferred = NormalizeRepoPathForComparison(preferredReferencePath ?? string.Empty);
        var selected = orderedCandidates.FirstOrDefault(
            candidate => string.Equals(
                NormalizeRepoPathForComparison(candidate),
                normalizedPreferred,
                StringComparison.Ordinal)) ?? orderedCandidates[0];
        return new ReusablePreviewTarget(selected, orderedCandidates);
    }

    private async Task<IReadOnlyList<string>> FindAffectedContentReferencesAsync(
        string sha,
        string reusableKey,
        CancellationToken cancellationToken)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var pendingKeys = new Queue<string>();
        var visitedKeys = new HashSet<string>(StringComparer.Ordinal);
        pendingKeys.Enqueue(reusableKey);

        while (pendingKeys.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentKey = pendingKeys.Dequeue();
            if (!visitedKeys.Add(currentKey))
            {
                continue;
            }

            var needle = "reusables." + currentKey;
            var contentMatches = await _worktree.FindFilesContainingAsync(
                    sha,
                    "content",
                    needle,
                    ".md",
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var reference in await FilterReusableReferencesAsync(
                         sha,
                         contentMatches,
                         currentKey,
                         cancellationToken)
                     .ConfigureAwait(false))
            {
                references.Add(reference);
            }

            var reusableMatches = await _worktree.FindFilesContainingAsync(
                    sha,
                    "data/reusables",
                    needle,
                    ".md",
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var reference in await FilterReusableReferencesAsync(
                         sha,
                         reusableMatches,
                         currentKey,
                         cancellationToken)
                     .ConfigureAwait(false))
            {
                if (TryBuildReusableKey(reference, out var parentKey)
                    && !visitedKeys.Contains(parentKey))
                {
                    pendingKeys.Enqueue(parentKey);
                }
            }
        }

        return references.ToArray();
    }

    private async Task<IReadOnlyList<string>> FilterReusableReferencesAsync(
        string sha,
        IReadOnlyList<string> candidates,
        string reusableKey,
        CancellationToken cancellationToken)
    {
        var references = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var markdown = await _worktree.ReadFileTextAsync(sha, candidate, cancellationToken).ConfigureAwait(false);
            if (DocsLiquidContextLoader.ContainsReusableReference(markdown, reusableKey))
            {
                references.Add(candidate);
            }
        }
        return references;
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
    /// (git fetch / rev-parse) を 1 回だけ走らせる。
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
        if (TryGetValidPreparedSession(key) is { } fast)
        {
            progress?.Report("このファイルの比較に使う準備済みデータを再利用します");
            return fast;
        }

        var gate = _preparedSessionLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        if (gate.CurrentCount == 0)
        {
            progress?.Report("このファイルの比較に必要な PR データを準備中です… (初回は github/docs の取得とPRデータ取得で数分かかることがあります。次回以降はキャッシュを再利用します)");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — a concurrent prewarm may have
            // just finished and populated the cache.
            if (TryGetValidPreparedSession(key) is { } slow)
            {
                progress?.Report("このファイルの比較に使う準備済みデータを再利用します");
                return slow;
            }

            progress?.Report("リポジトリキャッシュを準備中… (初回は github/docs の取得に数分かかることがあります。次回以降はキャッシュを再利用します)");
            await _worktree.EnsureBareCloneAsync(cancellationToken).ConfigureAwait(false);

            await _worktree.EnsureCommitAvailableAsync(prNumber, sha, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("比較元の親コミットを解決中… (git rev-parse <sha>^)");
            var beforeSha = await _worktree.ResolveFirstParentAsync(sha, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(beforeSha))
            {
                throw new InvalidOperationException("比較元になる親コミットを解決できませんでした。");
            }

            var previousPaths = await _worktree.ResolvePreviousPathsAsync(
                    beforeSha,
                    sha,
                    cancellationToken)
                .ConfigureAwait(false);
            var session = new PreparedMarkdownSession(beforeSha, previousPaths);
            _preparedSessions[key] = session;
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    private PreparedMarkdownSession? TryGetValidPreparedSession(PreparedSessionKey key)
        => _preparedSessions.TryGetValue(key, out var cached) ? cached : null;

    private async Task<DocsLiquidContext> LoadLiquidContextAsync(
        string commitSha,
        string filePath,
        string? markdown,
        bool cacheResult,
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
        if (cacheResult)
        {
            // DocsLiquidContext.Empty を含めキャッシュに入れる: data/ 配下が無いのも
            // 一定の事実なので 2 回目以降のディスク I/O を避ける。
            _liquidContextCache[key] = loaded;
        }
        return loaded;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _contentServer.StopAsync(cancellationToken).ConfigureAwait(false);
        ReplaceActiveMarkdownAssetRoot(null);
        _session.Deactivate();
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
            // git rev-parse <sha>^ が走る。EnsurePreparedSessionAsync を呼んでおけば
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

    public async Task PredictivePrewarmFileAsync(
        int prNumber,
        string sha,
        string filePath,
        DocsVersion? version = null,
        IReadOnlyList<string>? changedFilePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (!_worktree.IsEnabled
            || prNumber <= 0
            || string.IsNullOrWhiteSpace(sha)
            || string.IsNullOrWhiteSpace(filePath)
            || !PreviewPathMapper.IsMarkdown(filePath))
        {
            return;
        }

        var changedPaths = changedFilePaths ?? [];
        var key = BuildPreparedPageKey(
            prNumber,
            sha,
            filePath,
            reusableRenderedFilePath: null,
            version,
            changedPaths);
        try
        {
            await GetOrPrepareMarkdownPageAsync(
                    key,
                    version,
                    changedPaths,
                    consumePredictiveCache: false,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
            LogPredictiveFilePrewarmCompleted(_logger, prNumber, sha, key.FilePath);
        }
        catch (OperationCanceledException)
        {
            // The visible preview changed before this speculative render completed.
        }
        catch (Exception ex)
        {
            LogPredictiveFilePrewarmFailed(_logger, prNumber, sha, key.FilePath, ex);
        }
    }

    public async Task<int> CleanupCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!_worktree.IsEnabled)
        {
            LogDisabled(_logger);
            return 0;
        }

        // Stop the running Markdown server before removing cached preview assets.
        await StopAsync(cancellationToken).ConfigureAwait(false);
        DeleteMarkdownAssetCacheRoot();
        var removed = await _worktree.PruneAllAsync(cancellationToken).ConfigureAwait(false);
        // PruneAllAsync の後は worktree ディレクトリが消えているので
        // _preparedSessions / _liquidContextCache を残しておくと TryGetValidPreparedSession
        // の Directory.Exists チェックでは弾けるものの無駄なメモリを抱え続けることになる。
        _preparedSessions.Clear();
        _liquidContextCache.Clear();
        _preparedPageOperations.Clear();
        return removed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Preview pipeline is disabled (DocsRepository.BareCloneDir or CloneUrl empty).")]
    private static partial void LogDisabled(ILogger logger);

    private int[] GetReusablePorts()
    {
        var ports = new List<int>(capacity: 1);
        if (_contentServer is { IsRunning: true, CurrentPort: > 0 }
            && !ports.Contains(_contentServer.CurrentPort))
        {
            ports.Add(_contentServer.CurrentPort);
        }
        return ports.ToArray();
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

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "Predictive file preview prewarm completed for PR #{PrNumber} (sha {Sha}), file {FilePath}.")]
    private static partial void LogPredictiveFilePrewarmCompleted(
        ILogger logger,
        int prNumber,
        string sha,
        string filePath);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug,
        Message = "Predictive file preview prewarm failed for PR #{PrNumber} (sha {Sha}), file {FilePath}; the regular navigation path will retry.")]
    private static partial void LogPredictiveFilePrewarmFailed(
        ILogger logger,
        int prNumber,
        string sha,
        string filePath,
        Exception exception);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "Markdown file change analysis failed for PR #{PrNumber} (sha {Sha}), file {FilePath}; the file list will omit the pre-preview summary.")]
    private static partial void LogMarkdownFileChangeAnalysisFailed(
        ILogger logger,
        int prNumber,
        string sha,
        string filePath,
        Exception exception);
}
