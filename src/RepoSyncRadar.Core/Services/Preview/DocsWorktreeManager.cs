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
    private readonly Dictionary<string, WorktreeEntry> _worktrees = new(StringComparer.OrdinalIgnoreCase);
    private long _tick;

    public DocsWorktreeManager(
        IProcessRunner runner,
        IOptions<DocsRepositoryOptions> options,
        ILogger<DocsWorktreeManager> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _options = options.Value;
        _logger = logger;
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
        if (_worktrees.TryGetValue(commitSha, out var existing))
        {
            existing.LastUsed = ++_tick;
            return existing.Path;
        }

        var slug = commitSha.Length >= 12 ? commitSha[..12] : commitSha;
        var path = Path.Combine(_options.WorktreeRoot, slug);
        var args = string.Create(CultureInfo.InvariantCulture, $"worktree add {path} {commitSha}");
        var result = await _runner.RunAsync("git", args, _options.BareCloneDir, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git worktree add failed (exit {result.ExitCode}): {result.StandardError}");
        }
        _worktrees[commitSha] = new WorktreeEntry(path, ++_tick);

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
            var removeArgs = string.Create(CultureInfo.InvariantCulture, $"worktree remove --force {oldest.Value.Path}");
            var removeResult = await _runner.RunAsync("git", removeArgs, _options.BareCloneDir, cancellationToken).ConfigureAwait(false);
            if (removeResult.ExitCode != 0)
            {
                LogWorktreeRemoveFailed(_logger, oldest.Value.Path, removeResult.StandardError);
            }
            _worktrees.Remove(oldest.Key);
        }
        return path;
    }

    private sealed class WorktreeEntry(string path, long lastUsed)
    {
        public string Path { get; } = path;

        public long LastUsed { get; set; } = lastUsed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed to remove worktree {Path}: {StandardError}")]
    private static partial void LogWorktreeRemoveFailed(ILogger logger, string path, string standardError);
}
