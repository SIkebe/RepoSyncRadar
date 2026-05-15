using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.App.Copilot.Tools;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Production <see cref="ICopilotSessionFactory"/> backed by <see cref="CopilotClient"/>.
/// The client is created lazily on first use so that tests / DI graph validation never
/// trigger the embedded CLI process. The GitHub user token comes from
/// <see cref="IGitHubAccessTokenProvider"/> (env override → cached → DPAPI store →
/// OAuth Device Flow) and we set <c>UseLoggedInUser = false</c> so the Copilot CLI
/// never falls back to whatever <c>gh</c> happens to be signed in as on the machine.
/// </summary>
public sealed partial class CopilotSessionFactory : ICopilotSessionFactory
{
    private readonly IOptions<CopilotOptions> _options;
    private readonly RadarPermissionPolicy _permissionPolicy;
    private readonly IGitHubAccessTokenProvider _tokenProvider;
    private readonly ToolAuditHook? _auditHook;
    private readonly RadarTools _radarTools;
    private readonly RadarWriteTools _radarWriteTools;
    private readonly ILogger<CopilotSessionFactory> _logger;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private CopilotClient? _client;
    private bool _disposed;

    public CopilotSessionFactory(
        IOptions<CopilotOptions> options,
        RadarPermissionPolicy permissionPolicy,
        IGitHubAccessTokenProvider tokenProvider,
        ILogger<CopilotSessionFactory> logger,
        RadarTools radarTools,
        RadarWriteTools radarWriteTools,
        ToolAuditHook? auditHook = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionPolicy);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(radarTools);
        ArgumentNullException.ThrowIfNull(radarWriteTools);

        _options = options;
        _permissionPolicy = permissionPolicy;
        _tokenProvider = tokenProvider;
        _logger = logger;
        _radarTools = radarTools;
        _radarWriteTools = radarWriteTools;
        _auditHook = auditHook;
    }

    public async Task<ICopilotSession> CreateSessionAsync(
        SessionPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);
        var config = SessionConfigBuilder.Build(
            purpose,
            _options,
            _permissionPolicy.HandleAsync,
            _auditHook,
            CreateToolsFor(purpose));
        var session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        return new SdkCopilotSession(session);
    }

    private List<AIFunction> CreateToolsFor(SessionPurpose purpose)
    {
        return purpose switch
        {
            SessionPurpose.Triage or SessionPurpose.Maintenance => CreateAllRadarTools(),
            _ => [],
        };
    }

    private List<AIFunction> CreateAllRadarTools()
    {
        var tools = new List<AIFunction>();
        tools.AddRange(_radarTools.CreateAll());
        tools.AddRange(_radarWriteTools.CreateAll());
        return tools;
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
                // Force the SDK to use the token we hand it instead of falling back to
                // whatever the bundled CLI / gh CLI happens to be signed in as.
                UseLoggedInUser = false,
            };

            var copilot = _options.Value;
            if (!string.IsNullOrWhiteSpace(copilot.CliPath))
            {
                clientOptions.CliPath = copilot.CliPath;
            }

            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            clientOptions.GitHubToken = token;
            LogForwardingToken(_logger);

            _client = new CopilotClient(clientOptions);
            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
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
        Message = "Forwarding GitHub user token to Copilot CLI.")]
    private static partial void LogForwardingToken(ILogger logger);
}
