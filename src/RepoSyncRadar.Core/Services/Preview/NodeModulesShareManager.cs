using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Content-addressed <c>node_modules</c> share manager. Hashes
/// <c>package-lock.json</c> from the worktree, parks the install under
/// <c>&lt;WorktreeRoot&gt;/.shared-node-modules/&lt;hash&gt;/node_modules</c>,
/// and creates a Windows directory junction from the worktree's own
/// <c>node_modules</c> into that shared store. Subsequent worktrees with an
/// identical lockfile junction directly and skip the install entirely —
/// turning a 5-15 minute step into milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// Failure modes (cmd.exe missing, junction not supported, target on a
/// different volume) silently fall back to <paramref name="installFallback"/>
/// so the preview pipeline never gets worse than the historical "install per
/// worktree" path. The <c>.complete</c> sentinel is only written after a
/// junction succeeded AND the install completed, so a partially populated
/// shared store cannot be reused.
/// </para>
/// <para>
/// Per-hash <see cref="SemaphoreSlim"/> gates prevent two concurrent
/// <see cref="EnsureAsync"/> calls (e.g. before-server + after-server, see
/// <c>PreviewCoordinator</c>) from both running the install. The second call
/// waits for the first to finish and then sees the <c>.complete</c> sentinel.
/// </para>
/// </remarks>
public sealed partial class NodeModulesShareManager : INodeModulesShareManager
{
    private const string StoreDirectoryName = ".shared-node-modules";
    private const string CompleteSentinelName = ".complete";
    private const string LockFileName = "package-lock.json";

    // Per-hash gates are static so multiple PreviewServerHost instances (which
    // each own their own PreviewServerHost) coordinate through the same lock.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates
        = new(StringComparer.Ordinal);

    private readonly IProcessRunner _runner;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<NodeModulesShareManager> _logger;

    public NodeModulesShareManager(
        IProcessRunner runner,
        IOptions<DocsRepositoryOptions> options,
        ILogger<NodeModulesShareManager> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureAsync(
        string worktreePath,
        Func<CancellationToken, Task> installFallback,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(installFallback);

        var storeRoot = GetStoreRoot();
        var lockHash = TryComputeLockHash(worktreePath);
        if (storeRoot is null || string.IsNullOrEmpty(lockHash))
        {
            LogShareUnavailable(_logger, worktreePath);
            await installFallback(cancellationToken).ConfigureAwait(false);
            return;
        }

        var slotDir = Path.Combine(storeRoot, lockHash);
        var sharedNodeModules = Path.Combine(slotDir, "node_modules");
        var completeFlag = Path.Combine(slotDir, CompleteSentinelName);
        var link = Path.Combine(worktreePath, "node_modules");

        Directory.CreateDirectory(slotDir);
        var gate = _gates.GetOrAdd(lockHash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Recheck after the gate — the previous holder may have completed
            // the install via this same worktree path.
            if (Directory.Exists(link))
            {
                LogShareLocalAlreadyExists(_logger, link);
                return;
            }

            if (File.Exists(completeFlag) && Directory.Exists(sharedNodeModules))
            {
                if (await TryCreateJunctionAsync(link, sharedNodeModules, worktreePath, cancellationToken).ConfigureAwait(false))
                {
                    LogShareReusedExisting(_logger, sharedNodeModules);
                    return;
                }
                LogShareLinkFailedFallingBack(_logger);
                await installFallback(cancellationToken).ConfigureAwait(false);
                return;
            }

            // Create the shared target up front so the junction can succeed,
            // then let `npm install` write through it. The install effectively
            // populates the shared store.
            Directory.CreateDirectory(sharedNodeModules);
            if (!await TryCreateJunctionAsync(link, sharedNodeModules, worktreePath, cancellationToken).ConfigureAwait(false))
            {
                LogShareLinkFailedFallingBack(_logger);
                await installFallback(cancellationToken).ConfigureAwait(false);
                return;
            }

            LogShareInstallStarted(_logger, sharedNodeModules);
            await installFallback(cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                completeFlag,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            LogShareInstallCompleted(_logger, sharedNodeModules);
        }
        finally
        {
            gate.Release();
        }
    }

    private string? GetStoreRoot()
        => string.IsNullOrWhiteSpace(_options.WorktreeRoot)
            ? null
            : Path.Combine(_options.WorktreeRoot, StoreDirectoryName);

    private static string TryComputeLockHash(string worktreePath)
    {
        var lockPath = Path.Combine(worktreePath, LockFileName);
        if (!File.Exists(lockPath))
        {
            return string.Empty;
        }
        using var stream = File.OpenRead(lockPath);
        var hash = SHA256.HashData(stream);
        // First 16 hex characters (8 bytes) is plenty of collision resistance
        // for a single-user dev cache and keeps directory names short.
        return Convert.ToHexString(hash, 0, 8);
    }

    private async Task<bool> TryCreateJunctionAsync(
        string link,
        string target,
        string workingDir,
        CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(
                "cmd",
                $"/c mklink /J \"{link}\" \"{target}\"",
                workingDir,
                ct).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJunctionThrew(_logger, ex);
            return false;
        }
    }

    [LoggerMessage(EventId = 4400, Level = LogLevel.Debug,
        Message = "node_modules share manager opted out for worktree {WorktreePath} (no store configured or missing package-lock.json)")]
    private static partial void LogShareUnavailable(ILogger logger, string worktreePath);

    [LoggerMessage(EventId = 4401, Level = LogLevel.Debug,
        Message = "Local node_modules already exists at {Path}; sharing skipped")]
    private static partial void LogShareLocalAlreadyExists(ILogger logger, string path);

    [LoggerMessage(EventId = 4402, Level = LogLevel.Information,
        Message = "Reused shared node_modules store at {Path}")]
    private static partial void LogShareReusedExisting(ILogger logger, string path);

    [LoggerMessage(EventId = 4403, Level = LogLevel.Warning,
        Message = "Junction creation failed; falling back to standalone npm install")]
    private static partial void LogShareLinkFailedFallingBack(ILogger logger);

    [LoggerMessage(EventId = 4404, Level = LogLevel.Information,
        Message = "Installing node_modules into shared store {Path}")]
    private static partial void LogShareInstallStarted(ILogger logger, string path);

    [LoggerMessage(EventId = 4405, Level = LogLevel.Information,
        Message = "Shared node_modules store at {Path} is now ready")]
    private static partial void LogShareInstallCompleted(ILogger logger, string path);

    [LoggerMessage(EventId = 4406, Level = LogLevel.Warning,
        Message = "mklink invocation threw")]
    private static partial void LogJunctionThrew(ILogger logger, Exception exception);
}
