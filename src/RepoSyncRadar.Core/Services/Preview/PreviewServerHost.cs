using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Runs a single sidecar preview server (e.g. <c>next dev</c>) for the docs worktree
/// (IMPLEMENTATION_PLAN.md §Step 19). Owns at most one process at a time; switching
/// worktrees stops the previous server before starting the next one.
/// </summary>
public sealed partial class PreviewServerHost : IAsyncDisposable
{
    private readonly IProcessRunner _runner;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<PreviewServerHost> _logger;
    private IProcessHandle? _current;
    private int _currentPort;

    public PreviewServerHost(
        IProcessRunner runner,
        IOptions<DocsRepositoryOptions> options,
        ILogger<PreviewServerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.PreviewCommand);

    /// <summary>Current port (only valid while a process is running).</summary>
    public int CurrentPort => _currentPort;

    /// <summary>
    /// Starts the preview server in <paramref name="worktreePath"/> bound to
    /// <paramref name="port"/>. If a server is already running it is stopped first.
    /// Returns <c>null</c> when the preview pipeline is disabled.
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

        var args = _options.PreviewArguments.Replace(
            "{port}",
            port.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var handle = _runner.Start(_options.PreviewCommand, args, worktreePath);
        _current = handle;
        _currentPort = port;
        LogStarted(_logger, _options.PreviewCommand, args, port);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Preview server started: {Command} {Arguments} (port {Port}).")]
    private static partial void LogStarted(ILogger logger, string command, string arguments, int port);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to kill preview server process.")]
    private static partial void LogKillFailed(ILogger logger, Exception ex);
}
