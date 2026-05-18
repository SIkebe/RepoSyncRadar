using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Manages a bare clone of <c>github/docs</c> plus a small LRU cache of worktrees
/// for individual commit SHAs (IMPLEMENTATION_PLAN.md §Step 19). When
/// <see cref="DocsRepositoryOptions.BareCloneDir"/> is empty every public method
/// becomes a no-op so the app keeps starting without a configured preview path.
/// </summary>
public sealed partial class DocsWorktreeManager
{
    private readonly IProcessRunner _runner;
    private readonly DocsRepositoryOptions _options;
    private readonly ILogger<DocsWorktreeManager> _logger;
    private readonly IPreviewServerProcessCleaner _processCleaner;
    private readonly Dictionary<string, WorktreeEntry> _worktrees = new(StringComparer.OrdinalIgnoreCase);
    private long _tick;
    private bool _restored;

    public DocsWorktreeManager(
        IProcessRunner runner,
        IOptions<DocsRepositoryOptions> options,
        ILogger<DocsWorktreeManager> logger)
        : this(runner, options, logger, NoopPreviewServerProcessCleaner.Instance)
    {
    }

    public DocsWorktreeManager(
        IProcessRunner runner,
        IOptions<DocsRepositoryOptions> options,
        ILogger<DocsWorktreeManager> logger,
        IPreviewServerProcessCleaner processCleaner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processCleaner);
        _runner = runner;
        _options = options.Value;
        _logger = logger;
        _processCleaner = processCleaner;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.BareCloneDir)
        && !string.IsNullOrWhiteSpace(_options.CloneUrl);

    /// <summary>
    /// Creates the bare clone if it does not yet exist. Idempotent.
    /// </summary>
    public async Task EnsureBareCloneAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }
        if (Directory.Exists(_options.BareCloneDir))
        {
            return;
        }
        var parent = Path.GetDirectoryName(_options.BareCloneDir);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var args = string.Create(CultureInfo.InvariantCulture, $"clone --bare {_options.CloneUrl} {_options.BareCloneDir}");
        var result = await _runner.RunAsync("git", args, parent ?? Directory.GetCurrentDirectory(), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git clone --bare failed (exit {result.ExitCode}): {result.StandardError}");
        }
    }

    /// <summary>
    /// Runs <c>git fetch origin +refs/pull/{pr}/head:refs/pull/{pr}/head</c> against
    /// the bare clone so that the PR HEAD becomes a checkout-able ref.
    /// </summary>
    public async Task FetchPrAsync(int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pullRequestNumber);
        var refspec = string.Create(
            CultureInfo.InvariantCulture,
            $"+refs/pull/{pullRequestNumber}/head:refs/pull/{pullRequestNumber}/head");
        var args = string.Create(CultureInfo.InvariantCulture, $"fetch origin {refspec}");
        var result = await _runner.RunAsync("git", args, _options.BareCloneDir, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git fetch failed (exit {result.ExitCode}): {result.StandardError}");
        }
    }

    /// <summary>Resolves the first parent SHA for <paramref name="commitSha"/> inside the bare clone.</summary>
    public async Task<string?> ResolveFirstParentAsync(string commitSha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        if (!IsEnabled)
        {
            return null;
        }

        var args = string.Create(CultureInfo.InvariantCulture, $"rev-parse {commitSha}^");
        var result = await _runner.RunAsync("git", args, _options.BareCloneDir, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git rev-parse failed (exit {result.ExitCode}): {result.StandardError}");
        }

        var parent = result.StandardOutput.Trim();
        return parent.Length == 0 ? null : parent;
    }

    /// <summary>
    /// Adds a worktree for <paramref name="commitSha"/> if one does not already exist.
    /// Returns the path on disk. Calling with the same SHA twice in a row reuses the
    /// existing worktree and only refreshes its LRU position.
    /// </summary>
    public async Task<string?> CheckoutAsync(string commitSha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        if (!IsEnabled)
        {
            return null;
        }
        await RestoreFromDiskAsync(cancellationToken).ConfigureAwait(false);
        if (_worktrees.TryGetValue(commitSha, out var existing))
        {
            if (IsInitializingLockedWorktree(existing.Path))
            {
                _worktrees.Remove(commitSha);
                await RemoveWorktreeAsync(existing.Path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                existing.LastUsed = ++_tick;
                await ResetWorktreeAsync(existing.Path, cancellationToken).ConfigureAwait(false);
                await EnsureNextWebpackOptOutAsync(existing.Path, cancellationToken).ConfigureAwait(false);
                return existing.Path;
            }
        }

        var slug = commitSha.Length >= 12 ? commitSha[..12] : commitSha;
        var path = Path.Combine(_options.WorktreeRoot, slug);
        if (Directory.Exists(path))
        {
            var recovered = await TryRecoverExistingPathAsync(path, commitSha, cancellationToken).ConfigureAwait(false);
            if (recovered)
            {
                _worktrees[commitSha] = new WorktreeEntry(path, ++_tick);
                await ResetWorktreeAsync(path, cancellationToken).ConfigureAwait(false);
                await EnsureNextWebpackOptOutAsync(path, cancellationToken).ConfigureAwait(false);
                return path;
            }
        }

        var args = string.Create(CultureInfo.InvariantCulture, $"worktree add {path} {commitSha}");
        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync("git", args, _options.BareCloneDir, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CleanupInterruptedWorktreeAsync(path).ConfigureAwait(false);
            throw;
        }
        if (result.ExitCode != 0)
        {
            await CleanupInterruptedWorktreeAsync(path).ConfigureAwait(false);
            throw new InvalidOperationException($"git worktree add failed (exit {result.ExitCode}): {result.StandardError}");
        }
        _worktrees[commitSha] = new WorktreeEntry(path, ++_tick);
        await EnsureNextWebpackOptOutAsync(path, cancellationToken).ConfigureAwait(false);

        while (_worktrees.Count > _options.MaxWorktrees)
        {
            var oldest = default(KeyValuePair<string, WorktreeEntry>);
            var oldestTick = long.MaxValue;
            foreach (var kv in _worktrees)
            {
                if (kv.Value.LastUsed < oldestTick)
                {
                    oldestTick = kv.Value.LastUsed;
                    oldest = kv;
                }
            }
            await RemoveWorktreeAsync(oldest.Value.Path, cancellationToken).ConfigureAwait(false);
            _worktrees.Remove(oldest.Key);
        }
        return path;
    }

    public async Task<bool> TryRepairExistingWorktreeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsEnabled || !Directory.Exists(path))
        {
            return false;
        }

        if (IsInitializingLockedWorktree(path))
        {
            await RemoveWorktreeAsync(path, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            await ResetWorktreeAsync(path, cancellationToken).ConfigureAwait(false);
            await EnsureNextWebpackOptOutAsync(path, cancellationToken).ConfigureAwait(false);
            return Directory.Exists(path);
        }
        catch (InvalidOperationException)
        {
            await RemoveWorktreeAsync(path, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> TryRecoverExistingPathAsync(
        string path,
        string commitSha,
        CancellationToken cancellationToken)
    {
        if (IsInitializingLockedWorktree(path))
        {
            await RemoveWorktreeAsync(path, cancellationToken).ConfigureAwait(false);
            LogStaleWorktreeDeleted(_logger, path);
            return false;
        }

        var head = await _runner.RunAsync(
            "git",
            "rev-parse HEAD",
            path,
            cancellationToken).ConfigureAwait(false);
        if (head.ExitCode == 0 && CommitMatches(head.StandardOutput, commitSha))
        {
            LogExistingWorktreeReused(_logger, path, commitSha);
            return true;
        }

        try
        {
            await StopStaleServersBeforeRemovalAsync(path, cancellationToken).ConfigureAwait(false);
            await DeleteDirectoryRobustAsync(path, cancellationToken).ConfigureAwait(false);
            LogStaleWorktreeDeleted(_logger, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"既存の preview worktree ディレクトリ '{path}' を削除できませんでした。起動中の npm/node プロセスを停止するか、キャッシュをクリーンアップしてから再試行してください。",
                ex);
        }

        return false;
    }

    private async Task ResetWorktreeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "git",
            "reset --hard",
            path,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0
            && LooksLikeIndexLockFailure(result.StandardError)
            && TryDeleteStaleIndexLock(path))
        {
            result = await _runner.RunAsync(
                "git",
                "reset --hard",
                path,
                cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git reset --hard failed (exit {result.ExitCode}): {result.StandardError}");
        }
    }

    private async Task CleanupInterruptedWorktreeAsync(string path)
    {
        try
        {
            await RemoveWorktreeAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogInterruptedWorktreeCleanupFailed(_logger, path, ex.Message);
        }
    }

    private async Task<bool> RemoveWorktreeAsync(string path, CancellationToken cancellationToken)
    {
        await StopStaleServersBeforeRemovalAsync(path, cancellationToken).ConfigureAwait(false);

        await _runner.RunAsync(
            "git",
            string.Create(CultureInfo.InvariantCulture, $"worktree unlock {path}"),
            _options.BareCloneDir,
            cancellationToken).ConfigureAwait(false);

        var removeResult = await _runner.RunAsync(
            "git",
            string.Create(CultureInfo.InvariantCulture, $"worktree remove --force --force {path}"),
            _options.BareCloneDir,
            cancellationToken).ConfigureAwait(false);
        if (removeResult.ExitCode == 0)
        {
            return true;
        }

        LogWorktreeRemoveFailed(_logger, path, removeResult.StandardError);
        if (Directory.Exists(path))
        {
            await DeleteDirectoryRobustAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await _runner.RunAsync(
            "git",
            "worktree prune",
            _options.BareCloneDir,
            cancellationToken).ConfigureAwait(false);
        return Directory.Exists(path) is false;
    }

    private static bool IsInitializingLockedWorktree(string worktreePath)
    {
        if (!TryReadWorktreeGitDir(worktreePath, out var gitDir))
        {
            return false;
        }

        var lockedPath = Path.Combine(gitDir, "locked");
        if (!File.Exists(lockedPath))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(lockedPath).Trim();
            return string.Equals(text, "initializing", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadWorktreeGitDir(string worktreePath, out string gitDir)
    {
        gitDir = string.Empty;
        var gitFile = Path.Combine(worktreePath, ".git");
        if (!File.Exists(gitFile))
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(gitFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        const string prefix = "gitdir:";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Trim())
            .FirstOrDefault(static value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return false;
        }

        gitDir = line[prefix.Length..].Trim();
        if (!Path.IsPathRooted(gitDir))
        {
            gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));
        }
        return true;
    }

    private static bool LooksLikeIndexLockFailure(string standardError) =>
        standardError.Contains("index.lock", StringComparison.OrdinalIgnoreCase)
        && standardError.Contains("File exists", StringComparison.OrdinalIgnoreCase);

    private static bool TryDeleteStaleIndexLock(string worktreePath)
    {
        if (!TryReadWorktreeGitDir(worktreePath, out var gitDir))
        {
            return false;
        }

        var lockPath = Path.Combine(gitDir, "index.lock");
        try
        {
            var lockInfo = new FileInfo(lockPath);
            if (!lockInfo.Exists
                || DateTime.UtcNow - lockInfo.LastWriteTimeUtc < TimeSpan.FromMinutes(5))
            {
                return false;
            }

            lockInfo.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CommitMatches(string actualHead, string requestedSha)
    {
        var actual = actualHead.Trim();
        var requested = requestedSha.Trim();
        return string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase)
            || actual.StartsWith(requested, StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith(actual, StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureNextWebpackOptOutAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var nextMiddlewarePath = Path.Combine(worktreePath, "src", "frame", "middleware", "next.ts");
        if (!File.Exists(nextMiddlewarePath))
        {
            return;
        }

        var text = await File.ReadAllTextAsync(nextMiddlewarePath, cancellationToken).ConfigureAwait(false);
        if (text.Contains("webpack: true", StringComparison.Ordinal))
        {
            return;
        }

        const string singleLine = "export const nextApp = next({ dev: isDevelopment })";
        const string singleLinePatched = "export const nextApp = next({ dev: isDevelopment, webpack: true })";
        var updated = text.Replace(singleLine, singleLinePatched, StringComparison.Ordinal);
        if (string.Equals(updated, text, StringComparison.Ordinal))
        {
            LogNextPatchSkipped(_logger, nextMiddlewarePath);
            return;
        }

        await File.WriteAllTextAsync(nextMiddlewarePath, updated, cancellationToken).ConfigureAwait(false);
        LogNextPatched(_logger, nextMiddlewarePath);
    }

    private sealed class WorktreeEntry(string path, long lastUsed)
    {
        public string Path { get; } = path;

        public long LastUsed { get; set; } = lastUsed;
    }

    /// <summary>
    /// Populates the in-memory LRU from <c>git worktree list --porcelain</c> on first
    /// use. Without this, restarting the app forgets every previously-created worktree
    /// and <see cref="DocsRepositoryOptions.MaxWorktrees"/> never kicks in, so
    /// <c>WorktreeRoot</c> grows without bound across sessions.
    /// </summary>
    /// <remarks>
    /// The porcelain output is a sequence of blank-line-separated stanzas like
    /// <c>worktree &lt;path&gt;\nHEAD &lt;sha&gt;\ndetached</c> (or a single
    /// <c>bare</c> entry for the bare repo). We ignore the <c>bare</c> stanza and key
    /// the dictionary by HEAD sha. LastUsed is ordered by filesystem mtime so the
    /// oldest leftover gets evicted first.
    /// </remarks>
    private async Task RestoreFromDiskAsync(CancellationToken cancellationToken)
    {
        if (_restored || !IsEnabled)
        {
            return;
        }
        _restored = true;
        if (!Directory.Exists(_options.BareCloneDir))
        {
            return;
        }
        ProcessRunResult result;
        try
        {
            result = await _runner.RunAsync(
                "git",
                "worktree list --porcelain",
                _options.BareCloneDir,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            LogRestoreFailed(_logger, ex.Message);
            return;
        }
        if (result.ExitCode != 0)
        {
            LogRestoreFailed(_logger, result.StandardError);
            return;
        }

        var entries = new List<(string Path, string Sha, DateTime Mtime)>();
        string? path = null;
        string? sha = null;
        var isBare = false;
        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (path is not null && sha is not null && !isBare)
                {
                    var mtime = SafeGetMtime(path);
                    entries.Add((path, sha, mtime));
                }
                path = null;
                sha = null;
                isBare = false;
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                sha = line["HEAD ".Length..];
            }
            else if (string.Equals(line, "bare", StringComparison.Ordinal))
            {
                isBare = true;
            }
        }
        if (path is not null && sha is not null && !isBare)
        {
            entries.Add((path, sha, SafeGetMtime(path)));
        }

        foreach (var entry in entries.OrderBy(e => e.Mtime))
        {
            _worktrees[entry.Sha] = new WorktreeEntry(entry.Path, ++_tick);
        }
        if (entries.Count > 0)
        {
            LogRestored(_logger, entries.Count);
        }
    }

    private static DateTime SafeGetMtime(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Removes every tracked worktree via <c>git worktree remove --force</c> followed
    /// by <c>git worktree prune</c>. Returns the number of worktrees removed. Intended
    /// to be wired to a "Clean up cache" UI action / CLI script so users can free disk
    /// space without dropping to the shell.
    /// </summary>
    public async Task<int> PruneAllAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return 0;
        }
        await RestoreFromDiskAsync(cancellationToken).ConfigureAwait(false);

        var paths = _worktrees.Values.Select(v => v.Path).ToList();
        var removed = 0;
        foreach (var p in paths)
        {
            if (await RemoveWorktreeAsync(p, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }
        _worktrees.Clear();
        await _runner.RunAsync("git", "worktree prune", _options.BareCloneDir, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private async Task StopStaleServersBeforeRemovalAsync(string path, CancellationToken cancellationToken)
    {
        var stopped = await _processCleaner.StopStaleServersAsync(
                path,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (stopped > 0)
        {
            LogStalePreviewServersStopped(_logger, path, stopped);
        }
    }

    private static async Task DeleteDirectoryRobustAsync(string path, CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && (ex is IOException or UnauthorizedAccessException))
            {
                ClearDeleteBlockingAttributes(path);
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        ClearDeleteBlockingAttributes(path);
        Directory.Delete(path, recursive: true);
    }

    private static void ClearDeleteBlockingAttributes(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return;
            }

            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                FileAttributes currentAttributes;
                try
                {
                    currentAttributes = File.GetAttributes(current);
                    File.SetAttributes(current, NormalizeDeleteAttributes(currentAttributes));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((currentAttributes & FileAttributes.Directory) == 0
                    || (currentAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        pending.Push(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static FileAttributes NormalizeDeleteAttributes(FileAttributes attributes)
    {
        var normalized = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        return normalized == 0 ? FileAttributes.Normal : normalized;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed to remove worktree {Path}: {StandardError}")]
    private static partial void LogWorktreeRemoveFailed(ILogger logger, string path, string standardError);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Rehydrated {Count} worktree(s) from disk.")]
    private static partial void LogRestored(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "git worktree list --porcelain failed: {StandardError}")]
    private static partial void LogRestoreFailed(ILogger logger, string standardError);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Patched github/docs Next.js custom server to use webpack: true at {Path}.")]
    private static partial void LogNextPatched(ILogger logger, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Could not patch github/docs Next.js custom server at {Path}; expected next({{ dev: isDevelopment }}) shape was not found.")]
    private static partial void LogNextPatchSkipped(ILogger logger, string path);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Reusing existing preview worktree directory {Path} for sha {Sha}.")]
    private static partial void LogExistingWorktreeReused(ILogger logger, string path, string sha);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Deleted stale preview worktree directory {Path} before recreating it.")]
    private static partial void LogStaleWorktreeDeleted(ILogger logger, string path);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Stopped {Count} stale preview server process(es) before removing worktree {Path}.")]
    private static partial void LogStalePreviewServersStopped(ILogger logger, string path, int count);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "Failed to clean interrupted preview worktree {Path}: {Message}")]
    private static partial void LogInterruptedWorktreeCleanupFailed(ILogger logger, string path, string message);
}
