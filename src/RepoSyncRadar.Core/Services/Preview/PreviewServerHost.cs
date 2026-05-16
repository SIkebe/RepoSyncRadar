using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

public interface IPreviewServerHostFactory
{
    PreviewServerHost Create();
}

public sealed class PreviewServerHostFactory : IPreviewServerHostFactory
{
    private readonly IProcessRunner _runner;
    private readonly IPortReadyProbe _probe;
    private readonly IOptions<DocsRepositoryOptions> _options;
    private readonly ILogger<PreviewServerHost> _logger;
    private readonly IPreviewServerProcessCleaner _processCleaner;
    private readonly INodeModulesShareManager _shareManager;

    public PreviewServerHostFactory(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger)
        : this(runner, probe, options, logger, NoopPreviewServerProcessCleaner.Instance, NoopNodeModulesShareManager.Instance)
    {
    }

    public PreviewServerHostFactory(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger,
        IPreviewServerProcessCleaner processCleaner)
        : this(runner, probe, options, logger, processCleaner, NoopNodeModulesShareManager.Instance)
    {
    }

    public PreviewServerHostFactory(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger,
        IPreviewServerProcessCleaner processCleaner,
        INodeModulesShareManager shareManager)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processCleaner);
        ArgumentNullException.ThrowIfNull(shareManager);
        _runner = runner;
        _probe = probe;
        _options = options;
        _logger = logger;
        _processCleaner = processCleaner;
        _shareManager = shareManager;
    }

    public PreviewServerHost Create()
        => new(_runner, _probe, _options, _logger, _processCleaner, _shareManager);
}

/// <summary>
/// Runs a single sidecar preview server (e.g. <c>nodemon src/frame/server.ts</c>)
/// for the docs worktree (IMPLEMENTATION_PLAN.md §Step 19). Owns at most one
/// process at a time; switching worktrees stops the previous server before
/// starting the next one. Waits for the server to actually accept TCP
/// connections before returning, so the WebView2 host never navigates to a
/// port that is still warming up.
/// </summary>
public sealed partial class PreviewServerHost : IAsyncDisposable
{
    private const string PreviewRequestTimeoutEnvironmentKey = "REQUEST_TIMEOUT";
    private const string DefaultPreviewRequestTimeoutMilliseconds = "600000";

    private readonly IProcessRunner _runner;
    private readonly IPortReadyProbe _probe;
    private readonly IPreviewServerProcessCleaner _processCleaner;
    private readonly INodeModulesShareManager _shareManager;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewServerHost> _logger;
    private IProcessHandle? _current;
    private int _currentPort;

    public PreviewServerHost(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger)
        : this(runner, probe, options, logger, NoopPreviewServerProcessCleaner.Instance, NoopNodeModulesShareManager.Instance)
    {
    }

    public PreviewServerHost(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger,
        IPreviewServerProcessCleaner processCleaner)
        : this(runner, probe, options, logger, processCleaner, NoopNodeModulesShareManager.Instance)
    {
    }

    public PreviewServerHost(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger,
        IPreviewServerProcessCleaner processCleaner,
        INodeModulesShareManager shareManager)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processCleaner);
        ArgumentNullException.ThrowIfNull(shareManager);
        _runner = runner;
        _probe = probe;
        _processCleaner = processCleaner;
        _shareManager = shareManager;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.PreviewCommand);

    /// <summary>Current port (only valid while a process is running).</summary>
    public int CurrentPort => _currentPort;

    /// <summary>
    /// Snapshot of the currently-running child's stdout tail. Empty array when
    /// no child is running. UI components poll this during the startup wait to
    /// surface npm/Next.js output to the user (P1 — IMPLEMENTATION_PLAN.md §Step 19.6).
    /// </summary>
    public IReadOnlyList<string> RecentStdoutLines
        => _current?.RecentStdoutLines ?? Array.Empty<string>();

    /// <summary>
    /// Snapshot of the currently-running child's stderr tail. Empty array when
    /// no child is running. See <see cref="RecentStdoutLines"/>.
    /// </summary>
    public IReadOnlyList<string> RecentStderrLines
        => _current?.RecentStderrLines ?? Array.Empty<string>();

    /// <summary>
    /// True while a child preview process is alive. Lets the UI distinguish
    /// "waiting for repo clone / npm install" (no process yet) from
    /// "waiting for port to listen" (process running, polling logs).
    /// </summary>
    public bool IsProcessRunning => _current is { } handle && !handle.HasExited;

    /// <summary>
    /// OS process id of the currently-running child, or <c>null</c> when none.
    /// Surfaced so the UI can show the PID and the user can confirm with Task
    /// Manager / <c>netstat -ano | findstr :PORT</c> whether the npm tree is
    /// alive when nothing else seems to be happening.
    /// </summary>
    public int? CurrentProcessId
    {
        get
        {
            try
            {
                return _current is { } h && !h.HasExited ? h.ProcessId : null;
            }
            catch (InvalidOperationException)
            {
                // Process has already exited between our HasExited check and ProcessId access.
                return null;
            }
        }
    }

    /// <summary>
    /// Starts the preview server in <paramref name="worktreePath"/> bound to
    /// <paramref name="port"/> and waits until the port accepts TCP connections
    /// (bounded by <see cref="DocsRepositoryOptions.PreviewReadyTimeoutSeconds"/>).
    /// If a server is already running it is stopped first. Returns <c>null</c>
    /// when the preview pipeline is disabled. Throws
    /// <see cref="InvalidOperationException"/> when the child exits early or the
    /// ready timeout elapses; in both cases the child is torn down before the
    /// exception bubbles up.
    /// </summary>
    public async Task<IProcessHandle?> StartAsync(
        string worktreePath,
        int port,
        CancellationToken cancellationToken = default)
        => await StartAsync(worktreePath, port, progress: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Overload that surfaces sub-phase progress ("node_modules を準備中…", "dev サーバを起動中…")
    /// so the UI can distinguish the long npm-install phase from the Next.js
    /// compile phase. Both phases live before <see cref="IPortReadyProbe.WaitForListenAsync"/>
    /// returns, so without this signal the user only sees a single opaque "起動中…".
    /// </summary>
    public async Task<IProcessHandle?> StartAsync(
        string worktreePath,
        int port,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!IsEnabled)
        {
            return null;
        }
        await StopAsync(cancellationToken).ConfigureAwait(false);

        if (IsNpmCommand(_options.PreviewCommand))
        {
            await _processCleaner.StopStaleServersAsync(worktreePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureNodeModulesAsync(worktreePath, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report($"Next.js dev サーバを起動中 (ポート {port.ToString(CultureInfo.InvariantCulture)})…");

        var args = ReplacePort(_options.PreviewArguments, port);
        var env = BuildEnvironment(
            WithDefaultRequestTimeout(_options.PreviewEnvironment, _options.PreviewCommand),
            port);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var handle = _runner.Start(_options.PreviewCommand, args, worktreePath, env);
            NextDevServerProcessCleaner.RememberPreviewProcess(worktreePath, handle.ProcessId);
            _current = handle;
            _currentPort = port;
            LogStarted(_logger, _options.PreviewCommand, args, port);

            var timeout = TimeSpan.FromSeconds(_options.PreviewReadyTimeoutSeconds);
            bool ready;
            try
            {
                ready = await _probe.WaitForListenAsync(
                    port,
                    timeout,
                    processStillAlive: () => !handle.HasExited,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // P1-F: User cancelled mid-startup. Kill the orphan child and clear
                // `_current` so the next StartAsync does not see stale state. Use a
                // fresh token because the caller's token is already cancelled.
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            if (ready)
            {
                LogReady(_logger, port);
                return handle;
            }

            var exited = handle.HasExited;
            LogReadyFailed(_logger, port, (int)timeout.TotalSeconds, exited);
            var stderrTail = SnapshotTail(handle.RecentStderrLines, take: 8);
            // On a ready-probe timeout the Next.js dev child is still running and
            // its progress markers ("▲ Next.js ...", "✓ Compiling ... in 42s",
            // "ready - started server on http://localhost:4500") live on stdout —
            // stderr typically only carries informational warnings. Surface
            // stdout in that case so the user can tell "still compiling" apart
            // from "stuck/dead". When the process already exited and stderr
            // explained why, keep the dialog concise by omitting stdout.
            var stdoutTail = (!exited || stderrTail.Length == 0)
                ? SnapshotTail(handle.RecentStdoutLines, take: 8)
                : string.Empty;
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (attempt == 0
                && IsNpmCommand(_options.PreviewCommand)
                && NextDevServerProcessCleaner.IsDuplicateNextDevServerMessage(stderrTail)
                && await _processCleaner.StopStaleServersAsync(
                        worktreePath,
                        stderrTail,
                        CancellationToken.None).ConfigureAwait(false) > 0)
            {
                LogRetryAfterStaleNextDevCleanup(_logger, port);
                continue;
            }

            var header = exited
                ? $"プレビューサーバが起動直後に終了しました (port {port.ToString(CultureInfo.InvariantCulture)})。"
                : $"プレビューサーバが {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒以内に port {port.ToString(CultureInfo.InvariantCulture)} で待ち受け状態になりませんでした。";
            throw new InvalidOperationException(BuildFailureMessage(header, stderrTail, stdoutTail));
        }

        throw new InvalidOperationException("プレビューサーバの起動に失敗しました。");
    }

    /// <summary>Stops the current process, if any. Safe to call multiple times.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var handle = _current;
        if (handle is null)
        {
            return;
        }
        _current = null;
        _currentPort = 0;
        try
        {
            await handle.KillAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            LogKillFailed(_logger, ex);
        }
        await handle.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces every <c>{port}</c> placeholder in <paramref name="template"/>
    /// with <paramref name="port"/>. Extracted so unit tests can pin the
    /// substitution behavior without spawning a server.
    /// </summary>
    internal static string ReplacePort(string template, int port)
        => (template ?? string.Empty).Replace(
            "{port}",
            port.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    /// <summary>
    /// Materializes the <c>PreviewEnvironment</c> dictionary with <c>{port}</c>
    /// substituted in every value. Returns <c>null</c> for an empty/no-op
    /// override so <see cref="IProcessRunner.Start(string, string, string, IReadOnlyDictionary{string, string?}?)"/>
    /// short-circuits the merge.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?>? BuildEnvironment(
        IReadOnlyDictionary<string, string>? template,
        int port)
    {
        if (template is null || template.Count == 0)
        {
            return null;
        }
        var result = new Dictionary<string, string?>(template.Count, StringComparer.Ordinal);
        foreach (var (key, value) in template)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }
            result[key] = ReplacePort(value ?? string.Empty, port);
        }
        return result;
    }

    internal static IReadOnlyDictionary<string, string>? WithDefaultRequestTimeout(
        IReadOnlyDictionary<string, string>? template,
        string? command)
    {
        if (!IsNpmCommand(command))
        {
            return template;
        }

        var result = template is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(template, StringComparer.Ordinal);
        result.TryAdd(PreviewRequestTimeoutEnvironmentKey, DefaultPreviewRequestTimeoutMilliseconds);
        return result;
    }

    /// <summary>
    /// When the preview command is Node-based, installs dependencies before
    /// starting the sidecar if the worktree does not have <c>node_modules</c> yet.
    /// Delegates the heavy install to <see cref="INodeModulesShareManager"/>
    /// so multiple worktrees can share a single <c>node_modules</c> tree via a
    /// Windows directory junction (see <c>INodeModulesShareManager</c> for the
    /// fast-path / install-once mechanics). When the share manager opts out
    /// it just runs the same install that was here historically — making this
    /// change a pure no-op for tests that wire <c>NoopNodeModulesShareManager</c>.
    /// The check is intentionally narrow so non-Node preview commands (e.g.
    /// <c>hugo</c>, <c>jekyll</c>) keep working.
    /// </summary>
    private async Task EnsureNodeModulesAsync(string worktreePath, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!IsNpmCommand(_options.PreviewCommand)
            || string.IsNullOrWhiteSpace(_options.PreviewInstallArguments))
        {
            return;
        }
        var nodeModules = Path.Combine(worktreePath, "node_modules");
        if (Directory.Exists(nodeModules))
        {
            return;
        }

        progress?.Report("node_modules を準備中… (このリポジトリでの初回は数分かかります)");

        await _shareManager.EnsureAsync(
            worktreePath,
            ct => RunNodeModulesInstallAsync(worktreePath, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>{PreviewCommand} {PreviewInstallArguments}</c> in the worktree
    /// and waits for completion. Extracted as a callback so the share manager
    /// can pre-link <c>node_modules</c> to a shared store before the install
    /// writes through it.
    /// </summary>
    private async Task RunNodeModulesInstallAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var installArgs = _options.PreviewInstallArguments;
        LogInstallStarted(_logger, _options.PreviewCommand, installArgs, worktreePath);
        var handle = _runner.Start(
            _options.PreviewCommand,
            installArgs,
            worktreePath,
            environment: null);
        NextDevServerProcessCleaner.RememberPreviewProcess(worktreePath, handle.ProcessId);
        _current = handle;
        var exitCode = 0;
        try
        {
            exitCode = await handle.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        if (exitCode != 0)
        {
            var stderrTail = SnapshotTail(handle.RecentStderrLines, take: 12);
            var stdoutTail = SnapshotTail(handle.RecentStdoutLines, take: 12);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(BuildFailureMessage(
                $"{_options.PreviewCommand} {installArgs} が失敗しました (exit {exitCode.ToString(CultureInfo.InvariantCulture)})。",
                stderrTail,
                stdoutTail));
        }
        await handle.DisposeAsync().ConfigureAwait(false);
        _current = null;
        LogInstallCompleted(_logger, _options.PreviewCommand, installArgs, worktreePath);
    }

    internal static bool IsNpmCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        var name = Path.GetFileNameWithoutExtension(command);
        return string.Equals(name, "npm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pnpm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "yarn", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the last <paramref name="take"/> entries from <paramref name="lines"/>,
    /// joined with newlines and indented for inclusion in the user-facing
    /// failure message. Returns an empty string when there is nothing to show.
    /// </summary>
    internal static string SnapshotTail(IReadOnlyList<string>? lines, int take)
    {
        if (lines is null || lines.Count == 0 || take <= 0)
        {
            return string.Empty;
        }
        var start = Math.Max(0, lines.Count - take);
        var sb = new System.Text.StringBuilder();
        for (var i = start; i < lines.Count; i++)
        {
            sb.Append("  ").AppendLine(lines[i]);
        }
        return sb.ToString().TrimEnd();
    }

    internal static string SnapshotTail(string? text, int take)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        return SnapshotTail(
            text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            take);
    }

    internal static string BuildFailureMessage(string header, string stderrTail, string stdoutTail)
    {
        var hasStderr = !string.IsNullOrEmpty(stderrTail);
        var hasStdout = !string.IsNullOrEmpty(stdoutTail);
        if (!hasStderr && !hasStdout)
        {
            return header + " ログを確認してください。";
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        if (hasStderr)
        {
            sb.AppendLine("最新の stderr:");
            sb.AppendLine(stderrTail);
        }
        if (hasStdout)
        {
            sb.AppendLine("最新の stdout:");
            sb.AppendLine(stdoutTail);
        }
        return sb.ToString().TrimEnd();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Preview server started: {Command} {Arguments} (port {Port}).")]
    private static partial void LogStarted(ILogger logger, string command, string arguments, int port);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to kill preview server process.")]
    private static partial void LogKillFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Preview server ready on port {Port}.")]
    private static partial void LogReady(ILogger logger, int port);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Preview server not ready on port {Port} after {TimeoutSeconds}s (process exited: {Exited}).")]
    private static partial void LogReadyFailed(ILogger logger, int port, int timeoutSeconds, bool exited);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Installing preview dependencies: {Command} {Arguments} (cwd: {WorktreePath}).")]
    private static partial void LogInstallStarted(ILogger logger, string command, string arguments, string worktreePath);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Preview dependencies installed: {Command} {Arguments} (cwd: {WorktreePath}).")]
    private static partial void LogInstallCompleted(ILogger logger, string command, string arguments, string worktreePath);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Retrying preview server startup on port {Port} after stopping stale Next dev server.")]
    private static partial void LogRetryAfterStaleNextDevCleanup(ILogger logger, int port);
}
