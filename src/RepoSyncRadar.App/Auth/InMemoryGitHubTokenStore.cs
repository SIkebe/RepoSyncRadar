namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Non-persistent token store used by tests and as a fallback when a custom path is
/// not configured. Thread-safe.
/// </summary>
internal sealed class InMemoryGitHubTokenStore : IGitHubTokenStore
{
    private readonly Lock _gate = new();
    private StoredGitHubToken? _token;

    public Task<StoredGitHubToken?> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_token);
        }
    }

    public Task SaveAsync(StoredGitHubToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            _token = token;
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _token = null;
        }

        return Task.CompletedTask;
    }
}
