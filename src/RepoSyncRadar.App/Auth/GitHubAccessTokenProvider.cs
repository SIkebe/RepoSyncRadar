using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Production token provider. Resolution order:
/// <list type="number">
///   <item>
///     <description>
///       Environment variable <c>COPILOT_GITHUB_TOKEN</c> — kept only as a CI/debug
///       shortcut. Earlier env-var fallbacks (<c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>)
///       were removed so RepoSyncRadar never silently consumes another tool's PAT.
///     </description>
///   </item>
///   <item><description>In-memory cache from a prior sign-in this session.</description></item>
///   <item><description>The DPAPI <see cref="IGitHubTokenStore"/> on disk.</description></item>
///   <item>
///     <description>Interactive GitHub OAuth device flow via <see cref="IGitHubDeviceFlowAuthenticator"/>
///     + <see cref="IDeviceCodePrompt"/>.</description>
///   </item>
/// </list>
/// </summary>
public sealed partial class GitHubAccessTokenProvider : IGitHubAccessTokenProvider, IDisposable
{
    internal const string EnvironmentOverrideName = "COPILOT_GITHUB_TOKEN";

    private readonly IGitHubTokenStore _store;
    private readonly IGitHubDeviceFlowAuthenticator _authenticator;
    private readonly IDeviceCodePrompt _prompt;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<GitHubAccessTokenProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoredGitHubToken? _cached;

    public GitHubAccessTokenProvider(
        IGitHubTokenStore store,
        IGitHubDeviceFlowAuthenticator authenticator,
        IDeviceCodePrompt prompt,
        IOptions<CopilotOptions> options,
        ILogger<GitHubAccessTokenProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _authenticator = authenticator;
        _prompt = prompt;
        _options = options;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var envToken = Environment.GetEnvironmentVariable(EnvironmentOverrideName);
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            LogUsingEnvOverride(_logger);
            return envToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && !cached.IsExpired)
            {
                return cached.AccessToken;
            }

            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is not null && !loaded.IsExpired)
            {
                _cached = loaded;
                LogUsingStoredToken(_logger);
                return loaded.AccessToken;
            }

            var options = _options.Value;
            if (string.IsNullOrWhiteSpace(options.OAuthClientId))
            {
                throw new InvalidOperationException(
                    "Copilot:OAuthClientId is not configured and no token is available. " +
                    "Restore the bundled appsettings.json value, set a custom client id in appsettings.local.json, " +
                    "or provide COPILOT_GITHUB_TOKEN for debug use.");
            }

            LogStartingDeviceFlow(_logger);
            var challenge = await _authenticator
                .RequestCodeAsync(options.OAuthClientId, options.OAuthScopes, cancellationToken)
                .ConfigureAwait(false);

            await _prompt.DisplayAsync(challenge, cancellationToken).ConfigureAwait(false);
            try
            {
                var token = await _authenticator
                    .PollForTokenAsync(options.OAuthClientId, challenge, cancellationToken)
                    .ConfigureAwait(false);

                await _store.SaveAsync(token, cancellationToken).ConfigureAwait(false);
                _cached = token;
                LogSignInCompleted(_logger);
                return token.AccessToken;
            }
            finally
            {
                await _prompt.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = null;
            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            LogSignedOut(_logger);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Using GitHub token from COPILOT_GITHUB_TOKEN environment variable (debug override).")]
    private static partial void LogUsingEnvOverride(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Loaded GitHub token from DPAPI store.")]
    private static partial void LogUsingStoredToken(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Starting GitHub OAuth device flow to acquire a new user token.")]
    private static partial void LogStartingDeviceFlow(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "GitHub OAuth device flow completed; token saved.")]
    private static partial void LogSignInCompleted(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Cleared cached and stored GitHub token (sign-out).")]
    private static partial void LogSignedOut(ILogger logger);
}
