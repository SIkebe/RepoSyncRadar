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

/// <inheritdoc cref="IPreviewCoordinator" />
public sealed partial class PreviewCoordinator : IPreviewCoordinator
{
    private readonly DocsWorktreeManager _worktree;
    private readonly PreviewServerHost _server;
    private readonly PreviewSession _session;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewCoordinator> _logger;
    private string? _activeSha;

    public PreviewCoordinator(
        DocsWorktreeManager worktree,
        PreviewServerHost server,
        PreviewSession session,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _worktree = worktree;
        _server = server;
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

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
        _session.Deactivate();
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
}
