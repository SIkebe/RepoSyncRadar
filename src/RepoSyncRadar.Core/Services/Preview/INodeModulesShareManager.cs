namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Avoids re-running `npm install` for every worktree by parking the heavy
/// <c>node_modules</c> tree in a content-addressed shared store keyed by the
/// hash of <c>package-lock.json</c>. Subsequent worktrees with an identical
/// lockfile just create a Windows directory junction into the shared tree —
/// which costs milliseconds instead of the 5-15 minute install seen for a
/// fresh worktree on the github/docs repository.
/// </summary>
/// <remarks>
/// The fallback is intentional: when the manager cannot share (no store
/// configured, missing <c>package-lock.json</c>, junction creation failed) it
/// must invoke <paramref name="installFallback"/> so that PreviewServerHost
/// keeps working with the historical "install per worktree" behavior.
/// </remarks>
public interface INodeModulesShareManager
{
    Task EnsureAsync(
        string worktreePath,
        Func<CancellationToken, Task> installFallback,
        CancellationToken cancellationToken);
}

/// <summary>
/// Pass-through manager that always defers to <paramref name="installFallback"/>.
/// Wired by default so the share-store path is opt-in (real implementation is
/// registered explicitly in <c>CoreServiceCollectionExtensions</c>). Tests that
/// instantiate <see cref="PreviewServerHost"/> directly also get the NoOp.
/// </summary>
public sealed class NoopNodeModulesShareManager : INodeModulesShareManager
{
    public static NoopNodeModulesShareManager Instance { get; } = new();

    public Task EnsureAsync(
        string worktreePath,
        Func<CancellationToken, Task> installFallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installFallback);
        return installFallback(cancellationToken);
    }
}
