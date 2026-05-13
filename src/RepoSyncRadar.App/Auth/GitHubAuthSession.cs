using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Default <see cref="IGitHubAuthSession"/> implementation. Reads the local DPAPI
/// store + configured <c>Copilot:OAuthClientId</c> to compute the UI state, and
/// delegates sign-in / sign-out to the shared
/// <see cref="IGitHubAccessTokenProvider"/> so the device-flow path stays unified.
/// </summary>
public sealed partial class GitHubAuthSession : IGitHubAuthSession
{
    private readonly IGitHubTokenStore _store;
    private readonly IGitHubAccessTokenProvider _provider;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<GitHubAuthSession> _logger;
    private readonly IGitHubUserApi? _userApi;
    private (string Token, string Login)? _cachedLogin;

    public GitHubAuthSession(
        IGitHubTokenStore store,
        IGitHubAccessTokenProvider provider,
        IOptions<CopilotOptions> options,
        ILogger<GitHubAuthSession> logger,
        IGitHubUserApi? userApi = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _provider = provider;
        _options = options;
        _logger = logger;
        _userApi = userApi;
    }

    public async Task<GitHubAuthState> GetStateAsync(CancellationToken cancellationToken)
    {
        var clientId = _options.Value.OAuthClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return GitHubAuthState.NotConfigured;
        }

        StoredGitHubToken? stored;
        try
        {
            stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DPAPI failure (profile moved, key reset, corrupted file) is recoverable
            // from the user's POV — they just need to sign in again. Surface as
            // NotSignedIn rather than blowing up the AppHeader rendering.
            LogTokenLoadFailed(_logger, ex);
            return GitHubAuthState.NotSignedIn;
        }

        if (stored is null || stored.IsExpired)
        {
            return GitHubAuthState.NotSignedIn;
        }

        return GitHubAuthState.SignedIn;
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.OAuthClientId))
        {
            throw new InvalidOperationException(
                "Copilot:OAuthClientId is not configured; cannot run the device flow.");
        }

        _ = await _provider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SignOutAsync(CancellationToken cancellationToken)
    {
        // Clear the cached login alongside the persisted token so a follow-up sign-in
        // as a different user doesn't briefly render the old @login.
        _cachedLogin = null;
        return _provider.SignOutAsync(cancellationToken);
    }

    public async Task<string?> GetCurrentLoginAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.OAuthClientId))
        {
            return null;
        }

        StoredGitHubToken? stored;
        try
        {
            stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTokenLoadFailed(_logger, ex);
            return null;
        }

        if (stored is null || stored.IsExpired || string.IsNullOrEmpty(stored.AccessToken))
        {
            return null;
        }

        var token = stored.AccessToken;
        if (_cachedLogin is { } cached && string.Equals(cached.Token, token, StringComparison.Ordinal))
        {
            return cached.Login;
        }

        if (_userApi is null)
        {
            // No user API was wired up (e.g. in some tests). The AppHeader will simply
            // skip rendering @login rather than blow up.
            return null;
        }

        try
        {
            var login = await _userApi
                .GetCurrentLoginAsync(token, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(login))
            {
                _cachedLogin = (token, login);
            }

            return login;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLoginLookupFailed(_logger, ex);
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed to load GitHub token from store; treating as not signed in.")]
    private static partial void LogTokenLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to look up authenticated GitHub user; AppHeader will hide the login handle.")]
    private static partial void LogLoginLookupFailed(ILogger logger, Exception exception);
}
