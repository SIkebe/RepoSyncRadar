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
    internal const string DefaultFallbackModel = "gpt-5";

    private static readonly string[] PreferredFallbackModels =
    [
        DefaultFallbackModel,
        "claude-sonnet-4.5",
        "gpt-4.1",
    ];

    private readonly IOptions<CopilotOptions> _options;
    private readonly RadarPermissionPolicy _permissionPolicy;
    private readonly IGitHubAccessTokenProvider _tokenProvider;
    private readonly ToolAuditHook? _auditHook;
    private readonly RadarTools _radarTools;
    private readonly RadarWriteTools _radarWriteTools;
    private readonly ICopilotUsageTracker? _usageTracker;
    private readonly ILogger<CopilotSessionFactory> _logger;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private CopilotClient? _client;
    private string? _modelOverride;
    private bool _disposed;

    public CopilotSessionFactory(
        IOptions<CopilotOptions> options,
        RadarPermissionPolicy permissionPolicy,
        IGitHubAccessTokenProvider tokenProvider,
        ILogger<CopilotSessionFactory> logger,
        RadarTools radarTools,
        RadarWriteTools radarWriteTools,
        ICopilotUsageTracker? usageTracker = null,
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
        _usageTracker = usageTracker;
        _auditHook = auditHook;
    }

    public async Task<ICopilotSession> CreateSessionAsync(
        SessionPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);
        var configuredModel = NormalizeModel(_options.Value.DefaultModel);
        var model = _modelOverride ?? configuredModel;
        var config = SessionConfigBuilder.Build(
            purpose,
            _options,
            _permissionPolicy.HandleAsync,
            _auditHook,
            CreateToolsFor(purpose));
        config.Model = model;

        CopilotSession session;
        try
        {
            session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsModelUnavailableException(ex) && _modelOverride is null)
        {
            var fallbackModel = await ResolveFallbackModelAsync(client, configuredModel, cancellationToken).ConfigureAwait(false);
            if (fallbackModel is null)
            {
                throw;
            }

            LogRetryingWithFallbackModel(_logger, ex, model, fallbackModel);
            _modelOverride = fallbackModel;
            config.Model = fallbackModel;
            session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
        }

        return new SdkCopilotSession(session, purpose, _usageTracker);
    }

    internal static string? ResolveFallbackModel(string? configuredModel, IReadOnlyList<string> availableModelIds)
    {
        var requested = NormalizeModel(configuredModel);
        if (availableModelIds.Count == 0)
        {
            return string.Equals(requested, DefaultFallbackModel, StringComparison.OrdinalIgnoreCase)
                ? null
                : DefaultFallbackModel;
        }

        var available = new HashSet<string>(availableModelIds, StringComparer.OrdinalIgnoreCase);
        foreach (var fallback in PreferredFallbackModels)
        {
            if (!string.Equals(fallback, requested, StringComparison.OrdinalIgnoreCase)
                && available.Contains(fallback))
            {
                return fallback;
            }
        }

        return availableModelIds.FirstOrDefault(id =>
            !string.Equals(id, requested, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveFallbackModelAsync(
        CopilotClient client,
        string configuredModel,
        CancellationToken cancellationToken)
    {
        try
        {
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var availableModelIds = models
                .Select(static model => model.Id)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            return ResolveFallbackModel(configuredModel, availableModelIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallback = ResolveFallbackModel(configuredModel, []);
            LogModelCatalogUnavailable(_logger, ex, configuredModel, fallback ?? "<none>");
            return fallback;
        }
    }

    private static string NormalizeModel(string? model)
    {
        return string.IsNullOrWhiteSpace(model)
            ? DefaultFallbackModel
            : model.Trim();
    }

    private static bool IsModelUnavailableException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Model", StringComparison.OrdinalIgnoreCase)
                && current.Message.Contains("not available", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

            var copilot = _options.Value;
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var clientOptions = BuildClientOptions(copilot, _logger, token);
            LogForwardingToken(_logger);

            _client = new CopilotClient(clientOptions);
            return _client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    internal static CopilotClientOptions BuildClientOptions(
        CopilotOptions copilot,
        ILogger logger,
        string token)
    {
        ArgumentNullException.ThrowIfNull(copilot);
        ArgumentNullException.ThrowIfNull(logger);

        var clientOptions = new CopilotClientOptions
        {
            AutoStart = true,
            Logger = logger,
            LogLevel = string.IsNullOrWhiteSpace(copilot.LogLevel) ? "info" : copilot.LogLevel.Trim(),
            GitHubToken = token,
            // Force the SDK to use the token we hand it instead of falling back to
            // whatever the bundled CLI / gh CLI happens to be signed in as.
            UseLoggedInUser = false,
        };

        if (!string.IsNullOrWhiteSpace(copilot.CliPath))
        {
            clientOptions.CliPath = copilot.CliPath.Trim();
        }

        if (!string.IsNullOrWhiteSpace(copilot.CopilotHome))
        {
            clientOptions.CopilotHome = copilot.CopilotHome.Trim();
        }

        if (copilot.SessionIdleTimeoutSeconds is > 0)
        {
            clientOptions.SessionIdleTimeoutSeconds = copilot.SessionIdleTimeoutSeconds;
        }

        if (!string.IsNullOrWhiteSpace(copilot.TelemetryFilePath))
        {
            clientOptions.Telemetry = new TelemetryConfig
            {
                ExporterType = "file",
                FilePath = copilot.TelemetryFilePath.Trim(),
                SourceName = "RepoSyncRadar",
                CaptureContent = copilot.CaptureContent,
            };
        }

        return clientOptions;
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

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Could not list Copilot models after {ConfiguredModel} failed; fallback candidate is {FallbackModel}.")]
    private static partial void LogModelCatalogUnavailable(
        ILogger logger,
        Exception ex,
        string configuredModel,
        string fallbackModel);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Copilot model {Model} failed during session creation; retrying with {FallbackModel}.")]
    private static partial void LogRetryingWithFallbackModel(
        ILogger logger,
        Exception ex,
        string model,
        string fallbackModel);
}
