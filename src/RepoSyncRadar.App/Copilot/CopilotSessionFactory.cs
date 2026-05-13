using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Production <see cref="ICopilotSessionFactory"/> backed by <see cref="CopilotClient"/>.
/// The client is created lazily on first use so that tests / DI graph validation never
/// trigger the embedded CLI process. Authentication follows the environment variable
/// fallback chain documented in <c>docs/IMPLEMENTATION_PLAN.md §Step 11</c>:
/// <c>COPILOT_GITHUB_TOKEN</c> → <c>GH_TOKEN</c> → <c>GITHUB_TOKEN</c>. If none are set the
/// factory falls back to whatever credentials the bundled CLI already has on disk;
/// callers can detect "not signed in" via <see cref="EnsureReadyAsync"/>.
/// </summary>
public sealed partial class CopilotSessionFactory : ICopilotSessionFactory
{
    private static readonly string[] TokenEnvironmentVariables =
    [
        "COPILOT_GITHUB_TOKEN",
        "GH_TOKEN",
        "GITHUB_TOKEN",
    ];

    private readonly IOptions<CopilotOptions> _options;
    private readonly RadarPermissionPolicy _permissionPolicy;
    private readonly ToolAuditHook? _auditHook;
    private readonly ILogger<CopilotSessionFactory> _logger;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public CopilotSessionFactory(
        IOptions<CopilotOptions> options,
        RadarPermissionPolicy permissionPolicy,
        ILogger<CopilotSessionFactory> logger,
        ToolAuditHook? auditHook = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionPolicy);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _permissionPolicy = permissionPolicy;
        _logger = logger;
        _auditHook = auditHook;
    }

    public async Task<ICopilotSession> CreateSessionAsync(
        SessionPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);
        var config = SessionConfigBuilder.Build(purpose, _options, _permissionPolicy.HandleAsync, _auditHook);
        var session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        return new SdkCopilotSession(session);
    }

    /// <summary>
    /// Lightweight liveness probe against the underlying Copilot CLI. Lets the UI layer
    /// fail fast on first launch when no <c>COPILOT_GITHUB_TOKEN</c> / signed-in CLI is
    /// available, instead of waiting for the first <see cref="CreateSessionAsync"/> to
    /// blow up.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);
        await client.PingAsync("startup", cancellationToken).ConfigureAwait(false);
    }

    private async Task<CopilotClient> GetOrCreateClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var clientOptions = new CopilotClientOptions
            {
                AutoStart = true,
            };

            var copilot = _options.Value;
            if (!string.IsNullOrWhiteSpace(copilot.CliPath))
            {
                clientOptions.CliPath = copilot.CliPath;
            }

            var token = ResolveTokenFromEnvironment();
            if (!string.IsNullOrWhiteSpace(token))
            {
                LogForwardingToken(_logger);
                clientOptions.GitHubToken = token;
            }
            else
            {
                LogNoTokenInEnvironment(_logger);
            }

            _client = new CopilotClient(clientOptions);
            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private static string? ResolveTokenFromEnvironment()
    {
        foreach (var name in TokenEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _clientGate.Dispose();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Forwarding GitHub token from environment to Copilot CLI.")]
    private static partial void LogForwardingToken(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "No COPILOT_GITHUB_TOKEN / GH_TOKEN / GITHUB_TOKEN set; relying on the Copilot CLI's existing login.")]
    private static partial void LogNoTokenInEnvironment(ILogger logger);
}
