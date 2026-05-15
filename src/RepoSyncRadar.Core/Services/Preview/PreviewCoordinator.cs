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

    /// <summary>Stops the running preview server (if any) and clears the active session.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

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
    string AfterSha);

/// <inheritdoc cref="IPreviewCoordinator" />
public sealed partial class PreviewCoordinator : IPreviewCoordinator
{
    private readonly DocsWorktreeManager _worktree;
    private readonly PreviewServerHost _server;
    private readonly PreviewServerHost _beforeServer;
    private readonly PreviewSession _session;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewCoordinator> _logger;
    private string? _activeSha;
    private string? _activeBeforeSha;

    public PreviewCoordinator(
        DocsWorktreeManager worktree,
        PreviewServerHost server,
        IPreviewServerHostFactory serverFactory,
        PreviewSession session,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(serverFactory);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _worktree = worktree;
        _server = server;
        _beforeServer = serverFactory.Create();
        _session = session;
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

        var port = _options.PreviewBasePort;
        if (!string.Equals(_activeSha, sha, StringComparison.Ordinal))
        {
            progress?.Report(
                $"依存関係を確認してプレビューサーバを起動中… (node_modules がなければ {_options.PreviewCommand} {_options.PreviewInstallArguments} を自動実行 / ポート {port.ToString(CultureInfo.InvariantCulture)})");
            await _server.StartAsync(worktreePath, port, cancellationToken).ConfigureAwait(false);
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

        var afterPort = _options.PreviewBasePort;
        var beforePort = GetBeforePreviewPort(afterPort);

        if (!string.Equals(_activeBeforeSha, beforeSha, StringComparison.Ordinal)
            || _beforeServer.CurrentPort != beforePort
            || !_beforeServer.IsProcessRunning)
        {
            progress?.Report($"変更前プレビューサーバを起動中… (ポート {beforePort.ToString(CultureInfo.InvariantCulture)})");
            await _beforeServer.StartAsync(beforeWorktreePath, beforePort, cancellationToken).ConfigureAwait(false);
            _activeBeforeSha = beforeSha;
        }
        else
        {
            progress?.Report("既存の変更前プレビューサーバを再利用します");
        }

        if (!string.Equals(_activeSha, sha, StringComparison.Ordinal)
            || _server.CurrentPort != afterPort
            || !_server.IsProcessRunning)
        {
            progress?.Report($"PR HEAD プレビューサーバを起動中… (ポート {afterPort.ToString(CultureInfo.InvariantCulture)})");
            await _server.StartAsync(afterWorktreePath, afterPort, cancellationToken).ConfigureAwait(false);
            _activeSha = sha;
        }
        else
        {
            progress?.Report("既存の PR HEAD プレビューサーバを再利用します");
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

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _beforeServer.StopAsync(cancellationToken).ConfigureAwait(false);
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        _session.Deactivate();
        _activeBeforeSha = null;
        _activeSha = null;
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
        return await _worktree.PruneAllAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Preview pipeline is disabled (DocsRepository.BareCloneDir or PreviewCommand empty).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Preview ready for sha {Sha}: {Url}")]
    private static partial void LogPreviewReady(ILogger logger, string sha, string url);

    private static int GetBeforePreviewPort(int afterPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(afterPort, 1);
        if (afterPort >= 65535)
        {
            throw new InvalidOperationException("PreviewBasePort は比較プレビュー用に +1 したポートも使うため、65534 以下にしてください。");
        }
        return afterPort + 1;
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Preview comparison ready before {BeforeSha} / after {AfterSha}: {BeforeUrl} -> {AfterUrl}")]
    private static partial void LogPreviewComparisonReady(
        ILogger logger,
        string beforeSha,
        string afterSha,
        string beforeUrl,
        string afterUrl);
}
