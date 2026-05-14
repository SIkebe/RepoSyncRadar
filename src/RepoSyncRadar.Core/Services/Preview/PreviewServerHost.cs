using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

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
    private readonly IProcessRunner _runner;
    private readonly IPortReadyProbe _probe;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewServerHost> _logger;
    private IProcessHandle? _current;
    private int _currentPort;

    public PreviewServerHost(
        IProcessRunner runner,
        IPortReadyProbe probe,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _probe = probe;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.PreviewCommand);

    /// <summary>Current port (only valid while a process is running).</summary>
    public int CurrentPort => _currentPort;

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!IsEnabled)
        {
            return null;
        }
        await StopAsync(cancellationToken).ConfigureAwait(false);

        var args = ReplacePort(_options.PreviewArguments, port);
        var env = BuildEnvironment(_options.PreviewEnvironment, port);
        var handle = _runner.Start(_options.PreviewCommand, args, worktreePath, env);
        _current = handle;
        _currentPort = port;
        LogStarted(_logger, _options.PreviewCommand, args, port);

        var timeout = TimeSpan.FromSeconds(_options.PreviewReadyTimeoutSeconds);
        var ready = await _probe.WaitForListenAsync(
            port,
            timeout,
            processStillAlive: () => !handle.HasExited,
            cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            var exited = handle.HasExited;
            LogReadyFailed(_logger, port, (int)timeout.TotalSeconds, exited);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(exited
                ? $"プレビューサーバが起動直後に終了しました (port {port.ToString(CultureInfo.InvariantCulture)})。ログを確認してください。"
                : $"プレビューサーバが {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒以内に port {port.ToString(CultureInfo.InvariantCulture)} で待ち受け状態になりませんでした。");
        }
        LogReady(_logger, port);
        return handle;
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
}
