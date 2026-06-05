using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace RepoSyncRadar.App.Settings;

public sealed class FileLocalAppSettingsStore : ILocalAppSettingsStore, IDisposable
{
    internal const string LocalSettingsPathEnv = "REPOSYNCRADAR_LOCAL_APPSETTINGS_PATH";
    internal const string SettingsFileName = "appsettings.local.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IConfiguration? _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLocalAppSettingsStore(string settingsPath, IConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        SettingsPath = Path.GetFullPath(settingsPath);
        _configuration = configuration;
        Current = LoadFromDiskOrConfiguration();
    }

    public string SettingsPath { get; }

    public LocalAppSettings Current { get; private set; }

    public event Action<LocalAppSettings>? SettingsChanged;

    public static FileLocalAppSettingsStore CreateDefault(IConfiguration configuration)
    {
        var settingsPath = ResolveDefaultSettingsPath();
        TryCopyLegacyLocalSettings(settingsPath, AppContext.BaseDirectory);
        return new(settingsPath, configuration);
    }

    public static string ResolveDefaultSettingsPath()
        => ResolveDefaultSettingsPath(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable(LocalSettingsPathEnv),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string ResolveDefaultSettingsPath(
        string baseDirectory,
        string? configuredPath,
        string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var projectPath = TryResolveProjectLocalSettingsPath(baseDirectory);
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            return projectPath;
        }

        return Path.Combine(localApplicationDataPath, "RepoSyncRadar", SettingsFileName);
    }

    internal static string? TryResolveLegacyLocalSettingsPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var lowerPath = Path.Combine(baseDirectory, SettingsFileName);
        if (File.Exists(lowerPath))
        {
            return lowerPath;
        }

        var legacyCasePath = Path.Combine(baseDirectory, "appsettings.Local.json");
        return File.Exists(legacyCasePath) ? legacyCasePath : null;
    }

    internal static void TryCopyLegacyLocalSettings(string settingsPath, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var fullSettingsPath = Path.GetFullPath(settingsPath);
        if (File.Exists(fullSettingsPath))
        {
            return;
        }

        var legacyPath = TryResolveLegacyLocalSettingsPath(baseDirectory);
        if (string.IsNullOrWhiteSpace(legacyPath)
            || string.Equals(Path.GetFullPath(legacyPath), fullSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(fullSettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(legacyPath, fullSettingsPath, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task<LocalAppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = LoadFromDiskOrConfiguration();
            return Current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LocalAppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        Validate(normalized);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = ReadRootOrNew(SettingsPath);
            WriteSettings(root, normalized);

            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = root.ToJsonString(_jsonOptions);
            await File.WriteAllTextAsync(SettingsPath, json, cancellationToken).ConfigureAwait(false);
            Current = normalized.Clone();
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(Current.Clone());
    }

    public void Dispose()
        => _gate.Dispose();

    private static string? TryResolveProjectLocalSettingsPath(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var projectFile = Path.Combine(directory.FullName, "RepoSyncRadar.App.csproj");
            if (File.Exists(projectFile))
            {
                return Path.Combine(directory.FullName, SettingsFileName);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private LocalAppSettings LoadFromDiskOrConfiguration()
    {
        var fallback = _configuration is null
            ? LocalAppSettings.Default.Clone()
            : LoadFromConfiguration(_configuration);

        try
        {
            if (!File.Exists(SettingsPath))
            {
                return fallback;
            }

            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            return root is null ? fallback : LoadFromJson(root, fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    private static LocalAppSettings LoadFromConfiguration(IConfiguration configuration)
    {
        var defaults = LocalAppSettings.Default;
        return new LocalAppSettings
        {
            GitHub = new GitHubLocalAppSettings
            {
                Owner = GetString(configuration, "GitHub:Owner", defaults.GitHub.Owner),
                Repo = GetString(configuration, "GitHub:Repo", defaults.GitHub.Repo),
                PullRequestTitleFilter = GetString(configuration, "GitHub:PullRequestTitleFilter", defaults.GitHub.PullRequestTitleFilter),
                MaxPullRequests = GetInt(configuration, "GitHub:MaxPullRequests", defaults.GitHub.MaxPullRequests),
                PullRequestCreatedAtOrAfter = GetNullableString(configuration, "GitHub:PullRequestCreatedAtOrAfter"),
            },
            DocsApi = new DocsApiLocalAppSettings
            {
                BaseAddress = GetString(configuration, "DocsApi:BaseAddress", defaults.DocsApi.BaseAddress),
                DefaultLanguage = GetString(configuration, "DocsApi:DefaultLanguage", defaults.DocsApi.DefaultLanguage),
                ClientName = GetString(configuration, "DocsApi:ClientName", defaults.DocsApi.ClientName),
                PageListCacheSeconds = GetInt(configuration, "DocsApi:PageListCacheSeconds", defaults.DocsApi.PageListCacheSeconds),
            },
            Copilot = new CopilotLocalAppSettings
            {
                DefaultModel = GetString(configuration, "Copilot:DefaultModel", defaults.Copilot.DefaultModel),
                Streaming = GetBool(configuration, "Copilot:Streaming", defaults.Copilot.Streaming),
                ContextTier = GetNullableString(configuration, "Copilot:ContextTier")?.ToLowerInvariant(),
                LogLevel = GetString(configuration, "Copilot:LogLevel", defaults.Copilot.LogLevel),
                SessionIdleTimeoutSeconds = GetNullableInt(configuration, "Copilot:SessionIdleTimeoutSeconds"),
                CopilotHome = GetNullableString(configuration, "Copilot:CopilotHome"),
                TelemetryFilePath = GetNullableString(configuration, "Copilot:TelemetryFilePath"),
                CaptureContent = GetBool(configuration, "Copilot:CaptureContent", defaults.Copilot.CaptureContent),
                EnableRemoteSessions = GetBool(configuration, "Copilot:EnableRemoteSessions", defaults.Copilot.EnableRemoteSessions),
                EnableSessionTelemetry = GetBool(configuration, "Copilot:EnableSessionTelemetry", defaults.Copilot.EnableSessionTelemetry),
                AllowedUrlHosts = GetStringList(configuration, "Copilot:AllowedUrlHosts", defaults.Copilot.AllowedUrlHosts),
                OAuthClientId = GetNullableString(configuration, "Copilot:OAuthClientId"),
                OAuthScopes = GetStringList(configuration, "Copilot:OAuthScopes", defaults.Copilot.OAuthScopes),
            },
            WebView = new WebViewLocalAppSettings
            {
                AllowedUrlHosts = GetStringList(configuration, "WebView:AllowedUrlHosts", defaults.WebView.AllowedUrlHosts),
            },
            DocsRepository = new DocsRepositoryLocalAppSettings
            {
                BareCloneDir = GetString(configuration, "DocsRepository:BareCloneDir", defaults.DocsRepository.BareCloneDir),
                CloneUrl = GetString(configuration, "DocsRepository:CloneUrl", defaults.DocsRepository.CloneUrl),
                WorktreeRoot = GetString(configuration, "DocsRepository:WorktreeRoot", defaults.DocsRepository.WorktreeRoot),
                PrewarmOnStartup = GetBool(configuration, "DocsRepository:PrewarmOnStartup", defaults.DocsRepository.PrewarmOnStartup),
                PreviewBasePort = GetInt(configuration, "DocsRepository:PreviewBasePort", defaults.DocsRepository.PreviewBasePort),
                PreviewReadyTimeoutSeconds = GetInt(configuration, "DocsRepository:PreviewReadyTimeoutSeconds", defaults.DocsRepository.PreviewReadyTimeoutSeconds),
            },
            Logging = new LoggingLocalAppSettings
            {
                DefaultLogLevel = GetString(configuration, "Logging:LogLevel:Default", defaults.Logging.DefaultLogLevel),
                MicrosoftLogLevel = GetString(configuration, "Logging:LogLevel:Microsoft", defaults.Logging.MicrosoftLogLevel),
            },
            Updates = new UpdatesLocalAppSettings
            {
                Enabled = GetBool(configuration, "Updates:Enabled", defaults.Updates.Enabled),
                CheckOnStartup = GetBool(configuration, "Updates:CheckOnStartup", defaults.Updates.CheckOnStartup),
                FeedUrl = GetString(configuration, "Updates:FeedUrl", defaults.Updates.FeedUrl),
                Channel = GetNullableString(configuration, "Updates:Channel"),
                CheckTimeoutSeconds = GetInt(configuration, "Updates:CheckTimeoutSeconds", defaults.Updates.CheckTimeoutSeconds),
            },
        };
    }

    private static LocalAppSettings LoadFromJson(JsonObject root, LocalAppSettings fallback)
        => Normalize(new LocalAppSettings
        {
            GitHub = new GitHubLocalAppSettings
            {
                Owner = GetString(root, "GitHub", "Owner", fallback.GitHub.Owner),
                Repo = GetString(root, "GitHub", "Repo", fallback.GitHub.Repo),
                PullRequestTitleFilter = GetString(root, "GitHub", "PullRequestTitleFilter", fallback.GitHub.PullRequestTitleFilter),
                MaxPullRequests = GetInt(root, "GitHub", "MaxPullRequests", fallback.GitHub.MaxPullRequests),
                PullRequestCreatedAtOrAfter = GetNullableString(root, "GitHub", "PullRequestCreatedAtOrAfter", fallback.GitHub.PullRequestCreatedAtOrAfter),
            },
            DocsApi = new DocsApiLocalAppSettings
            {
                BaseAddress = GetString(root, "DocsApi", "BaseAddress", fallback.DocsApi.BaseAddress),
                DefaultLanguage = GetString(root, "DocsApi", "DefaultLanguage", fallback.DocsApi.DefaultLanguage),
                ClientName = GetString(root, "DocsApi", "ClientName", fallback.DocsApi.ClientName),
                PageListCacheSeconds = GetInt(root, "DocsApi", "PageListCacheSeconds", fallback.DocsApi.PageListCacheSeconds),
            },
            Copilot = new CopilotLocalAppSettings
            {
                DefaultModel = GetString(root, "Copilot", "DefaultModel", fallback.Copilot.DefaultModel),
                Streaming = GetBool(root, "Copilot", "Streaming", fallback.Copilot.Streaming),
                ContextTier = GetNullableString(root, "Copilot", "ContextTier", fallback.Copilot.ContextTier),
                LogLevel = GetString(root, "Copilot", "LogLevel", fallback.Copilot.LogLevel),
                SessionIdleTimeoutSeconds = GetNullableInt(root, "Copilot", "SessionIdleTimeoutSeconds", fallback.Copilot.SessionIdleTimeoutSeconds),
                CopilotHome = GetNullableString(root, "Copilot", "CopilotHome", fallback.Copilot.CopilotHome),
                TelemetryFilePath = GetNullableString(root, "Copilot", "TelemetryFilePath", fallback.Copilot.TelemetryFilePath),
                CaptureContent = GetBool(root, "Copilot", "CaptureContent", fallback.Copilot.CaptureContent),
                EnableRemoteSessions = GetBool(root, "Copilot", "EnableRemoteSessions", fallback.Copilot.EnableRemoteSessions),
                EnableSessionTelemetry = GetBool(root, "Copilot", "EnableSessionTelemetry", fallback.Copilot.EnableSessionTelemetry),
                AllowedUrlHosts = GetStringList(root, "Copilot", "AllowedUrlHosts", fallback.Copilot.AllowedUrlHosts),
                OAuthClientId = GetNullableString(root, "Copilot", "OAuthClientId", fallback.Copilot.OAuthClientId),
                OAuthScopes = GetStringList(root, "Copilot", "OAuthScopes", fallback.Copilot.OAuthScopes),
            },
            WebView = new WebViewLocalAppSettings
            {
                AllowedUrlHosts = GetStringList(root, "WebView", "AllowedUrlHosts", fallback.WebView.AllowedUrlHosts),
            },
            DocsRepository = new DocsRepositoryLocalAppSettings
            {
                BareCloneDir = GetString(root, "DocsRepository", "BareCloneDir", fallback.DocsRepository.BareCloneDir),
                CloneUrl = GetString(root, "DocsRepository", "CloneUrl", fallback.DocsRepository.CloneUrl),
                WorktreeRoot = GetString(root, "DocsRepository", "WorktreeRoot", fallback.DocsRepository.WorktreeRoot),
                PrewarmOnStartup = GetBool(root, "DocsRepository", "PrewarmOnStartup", fallback.DocsRepository.PrewarmOnStartup),
                PreviewBasePort = GetInt(root, "DocsRepository", "PreviewBasePort", fallback.DocsRepository.PreviewBasePort),
                PreviewReadyTimeoutSeconds = GetInt(root, "DocsRepository", "PreviewReadyTimeoutSeconds", fallback.DocsRepository.PreviewReadyTimeoutSeconds),
            },
            Logging = new LoggingLocalAppSettings
            {
                DefaultLogLevel = GetString(root, "Logging", "LogLevel", "Default", fallback.Logging.DefaultLogLevel),
                MicrosoftLogLevel = GetString(root, "Logging", "LogLevel", "Microsoft", fallback.Logging.MicrosoftLogLevel),
            },
            Updates = new UpdatesLocalAppSettings
            {
                Enabled = GetBool(root, "Updates", "Enabled", fallback.Updates.Enabled),
                CheckOnStartup = GetBool(root, "Updates", "CheckOnStartup", fallback.Updates.CheckOnStartup),
                FeedUrl = GetString(root, "Updates", "FeedUrl", fallback.Updates.FeedUrl),
                Channel = GetNullableString(root, "Updates", "Channel", fallback.Updates.Channel),
                CheckTimeoutSeconds = GetInt(root, "Updates", "CheckTimeoutSeconds", fallback.Updates.CheckTimeoutSeconds),
            },
        });

    private static void WriteSettings(JsonObject root, LocalAppSettings settings)
    {
        var github = GetOrReplaceObject(root, "GitHub");
        github["Owner"] = settings.GitHub.Owner;
        github["Repo"] = settings.GitHub.Repo;
        github["PullRequestTitleFilter"] = settings.GitHub.PullRequestTitleFilter;
        github["MaxPullRequests"] = settings.GitHub.MaxPullRequests;
        github["PullRequestCreatedAtOrAfter"] = string.IsNullOrWhiteSpace(settings.GitHub.PullRequestCreatedAtOrAfter)
            ? null
            : settings.GitHub.PullRequestCreatedAtOrAfter;

        var docsApi = GetOrReplaceObject(root, "DocsApi");
        docsApi["BaseAddress"] = settings.DocsApi.BaseAddress;
        docsApi["DefaultLanguage"] = settings.DocsApi.DefaultLanguage;
        docsApi["ClientName"] = settings.DocsApi.ClientName;
        docsApi["PageListCacheSeconds"] = settings.DocsApi.PageListCacheSeconds;

        var copilot = GetOrReplaceObject(root, "Copilot");
        copilot["DefaultModel"] = settings.Copilot.DefaultModel;
        copilot["Streaming"] = settings.Copilot.Streaming;
        copilot["ContextTier"] = string.IsNullOrWhiteSpace(settings.Copilot.ContextTier)
            ? null
            : settings.Copilot.ContextTier;
        copilot["LogLevel"] = settings.Copilot.LogLevel;
        copilot["SessionIdleTimeoutSeconds"] = settings.Copilot.SessionIdleTimeoutSeconds;
        copilot["CopilotHome"] = string.IsNullOrWhiteSpace(settings.Copilot.CopilotHome)
            ? null
            : settings.Copilot.CopilotHome;
        copilot["TelemetryFilePath"] = string.IsNullOrWhiteSpace(settings.Copilot.TelemetryFilePath)
            ? null
            : settings.Copilot.TelemetryFilePath;
        copilot["CaptureContent"] = settings.Copilot.CaptureContent;
        copilot["EnableRemoteSessions"] = settings.Copilot.EnableRemoteSessions;
        copilot["EnableSessionTelemetry"] = settings.Copilot.EnableSessionTelemetry;
        copilot["AllowedUrlHosts"] = ToJsonArray(settings.Copilot.AllowedUrlHosts);
        copilot["OAuthClientId"] = string.IsNullOrWhiteSpace(settings.Copilot.OAuthClientId)
            ? string.Empty
            : settings.Copilot.OAuthClientId;
        copilot["OAuthScopes"] = ToJsonArray(settings.Copilot.OAuthScopes);

        var webView = GetOrReplaceObject(root, "WebView");
        webView["AllowedUrlHosts"] = ToJsonArray(settings.WebView.AllowedUrlHosts);

        var docsRepository = GetOrReplaceObject(root, "DocsRepository");
        docsRepository["BareCloneDir"] = settings.DocsRepository.BareCloneDir;
        docsRepository["CloneUrl"] = settings.DocsRepository.CloneUrl;
        docsRepository["WorktreeRoot"] = settings.DocsRepository.WorktreeRoot;
        docsRepository["PrewarmOnStartup"] = settings.DocsRepository.PrewarmOnStartup;
        docsRepository["PreviewBasePort"] = settings.DocsRepository.PreviewBasePort;
        docsRepository["PreviewReadyTimeoutSeconds"] = settings.DocsRepository.PreviewReadyTimeoutSeconds;

        var logging = GetOrReplaceObject(root, "Logging");
        var logLevel = GetOrReplaceObject(logging, "LogLevel");
        logLevel["Default"] = settings.Logging.DefaultLogLevel;
        logLevel["Microsoft"] = settings.Logging.MicrosoftLogLevel;

        var updates = GetOrReplaceObject(root, "Updates");
        updates["Enabled"] = settings.Updates.Enabled;
        updates["CheckOnStartup"] = settings.Updates.CheckOnStartup;
        updates["FeedUrl"] = settings.Updates.FeedUrl;
        updates["Channel"] = string.IsNullOrWhiteSpace(settings.Updates.Channel)
            ? null
            : settings.Updates.Channel;
        updates["CheckTimeoutSeconds"] = settings.Updates.CheckTimeoutSeconds;
    }

    private static LocalAppSettings Normalize(LocalAppSettings settings)
        => new()
        {
            GitHub = new GitHubLocalAppSettings
            {
                Owner = TrimOrEmpty(settings.GitHub.Owner),
                Repo = TrimOrEmpty(settings.GitHub.Repo),
                PullRequestTitleFilter = TrimOrEmpty(settings.GitHub.PullRequestTitleFilter),
                MaxPullRequests = settings.GitHub.MaxPullRequests,
                PullRequestCreatedAtOrAfter = NormalizeNullable(settings.GitHub.PullRequestCreatedAtOrAfter),
            },
            DocsApi = new DocsApiLocalAppSettings
            {
                BaseAddress = TrimOrEmpty(settings.DocsApi.BaseAddress),
                DefaultLanguage = TrimOrEmpty(settings.DocsApi.DefaultLanguage),
                ClientName = TrimOrEmpty(settings.DocsApi.ClientName),
                PageListCacheSeconds = settings.DocsApi.PageListCacheSeconds,
            },
            Copilot = new CopilotLocalAppSettings
            {
                DefaultModel = TrimOrEmpty(settings.Copilot.DefaultModel),
                Streaming = settings.Copilot.Streaming,
                ContextTier = NormalizeNullable(settings.Copilot.ContextTier)?.ToLowerInvariant(),
                LogLevel = string.IsNullOrWhiteSpace(settings.Copilot.LogLevel)
                    ? "info"
                    : settings.Copilot.LogLevel.Trim().ToLowerInvariant(),
                SessionIdleTimeoutSeconds = settings.Copilot.SessionIdleTimeoutSeconds,
                CopilotHome = NormalizeNullable(settings.Copilot.CopilotHome),
                TelemetryFilePath = NormalizeNullable(settings.Copilot.TelemetryFilePath),
                CaptureContent = settings.Copilot.CaptureContent,
                EnableRemoteSessions = settings.Copilot.EnableRemoteSessions,
                EnableSessionTelemetry = settings.Copilot.EnableSessionTelemetry,
                AllowedUrlHosts = NormalizeHosts(settings.Copilot.AllowedUrlHosts),
                OAuthClientId = NormalizeNullable(settings.Copilot.OAuthClientId),
                OAuthScopes = NormalizeStringList(settings.Copilot.OAuthScopes),
            },
            WebView = new WebViewLocalAppSettings
            {
                AllowedUrlHosts = NormalizeHosts(settings.WebView.AllowedUrlHosts),
            },
            DocsRepository = new DocsRepositoryLocalAppSettings
            {
                BareCloneDir = TrimOrEmpty(settings.DocsRepository.BareCloneDir),
                CloneUrl = TrimOrEmpty(settings.DocsRepository.CloneUrl),
                WorktreeRoot = TrimOrEmpty(settings.DocsRepository.WorktreeRoot),
                PrewarmOnStartup = settings.DocsRepository.PrewarmOnStartup,
                PreviewBasePort = settings.DocsRepository.PreviewBasePort,
                PreviewReadyTimeoutSeconds = settings.DocsRepository.PreviewReadyTimeoutSeconds,
            },
            Logging = new LoggingLocalAppSettings
            {
                DefaultLogLevel = TrimOrEmpty(settings.Logging.DefaultLogLevel),
                MicrosoftLogLevel = TrimOrEmpty(settings.Logging.MicrosoftLogLevel),
            },
            Updates = new UpdatesLocalAppSettings
            {
                Enabled = settings.Updates.Enabled,
                CheckOnStartup = settings.Updates.CheckOnStartup,
                FeedUrl = TrimOrEmpty(settings.Updates.FeedUrl),
                Channel = NormalizeNullable(settings.Updates.Channel),
                CheckTimeoutSeconds = settings.Updates.CheckTimeoutSeconds,
            },
        };

    private static void Validate(LocalAppSettings settings)
    {
        var errors = new List<string>();
        Require(settings.GitHub.Owner, "GitHub.Owner", errors);
        Require(settings.GitHub.Repo, "GitHub.Repo", errors);
        Require(settings.GitHub.PullRequestTitleFilter, "GitHub.PullRequestTitleFilter", errors);
        ValidateRange(settings.GitHub.MaxPullRequests, 1, 100, "GitHub.MaxPullRequests", errors);
        if (!string.IsNullOrWhiteSpace(settings.GitHub.PullRequestCreatedAtOrAfter)
            && !DateTimeOffset.TryParse(settings.GitHub.PullRequestCreatedAtOrAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
        {
            errors.Add("GitHub.PullRequestCreatedAtOrAfter は日時として解釈できる値にしてください。");
        }

        Require(settings.DocsApi.BaseAddress, "DocsApi.BaseAddress", errors);
        if (!Uri.TryCreate(settings.DocsApi.BaseAddress, UriKind.Absolute, out var docsUri)
            || !string.Equals(docsUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("DocsApi.BaseAddress は https の絶対 URL にしてください。");
        }
        Require(settings.DocsApi.DefaultLanguage, "DocsApi.DefaultLanguage", errors);
        Require(settings.DocsApi.ClientName, "DocsApi.ClientName", errors);
        ValidateRange(settings.DocsApi.PageListCacheSeconds, 1, int.MaxValue, "DocsApi.PageListCacheSeconds", errors);

        Require(settings.Copilot.DefaultModel, "Copilot.DefaultModel", errors);
        if (settings.Copilot.ContextTier is { } contextTier
            && !string.Equals(contextTier, "default", StringComparison.Ordinal)
            && !string.Equals(contextTier, "long_context", StringComparison.Ordinal))
        {
            errors.Add("Copilot.ContextTier は default または long_context にしてください。");
        }
        Require(settings.Copilot.LogLevel, "Copilot.LogLevel", errors);
        if (settings.Copilot.SessionIdleTimeoutSeconds is { } idleTimeout)
        {
            ValidateRange(idleTimeout, 0, int.MaxValue, "Copilot.SessionIdleTimeoutSeconds", errors);
        }
        if (settings.Copilot.AllowedUrlHosts.Count == 0)
        {
            errors.Add("Copilot.AllowedUrlHosts は 1 件以上指定してください。");
        }
        foreach (var host in settings.Copilot.AllowedUrlHosts)
        {
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                errors.Add($"Copilot.AllowedUrlHosts の '{host}' はホスト名として解釈できません。");
            }
        }
        if (settings.WebView.AllowedUrlHosts.Count == 0)
        {
            errors.Add("WebView.AllowedUrlHosts は 1 件以上指定してください。");
        }
        foreach (var host in settings.WebView.AllowedUrlHosts)
        {
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            {
                errors.Add($"WebView.AllowedUrlHosts の '{host}' はホスト名として解釈できません。");
            }
        }

        ValidateRange(settings.DocsRepository.PreviewBasePort, 1024, 65535, "DocsRepository.PreviewBasePort", errors);
        ValidateRange(settings.DocsRepository.PreviewReadyTimeoutSeconds, 5, 1800, "DocsRepository.PreviewReadyTimeoutSeconds", errors);

        Require(settings.Logging.DefaultLogLevel, "Logging.LogLevel.Default", errors);
        Require(settings.Logging.MicrosoftLogLevel, "Logging.LogLevel.Microsoft", errors);

        ValidateRange(settings.Updates.CheckTimeoutSeconds, 5, 1800, "Updates.CheckTimeoutSeconds", errors);
        if (settings.Updates.Enabled)
        {
            Require(settings.Updates.FeedUrl, "Updates.FeedUrl", errors);
            if (!Uri.TryCreate(settings.Updates.FeedUrl, UriKind.Absolute, out var updateUri)
                || !IsAllowedUpdateFeedUri(updateUri))
            {
                errors.Add("Updates.FeedUrl は https の絶対 URL にしてください。ローカル検証では http://localhost、http://127.0.0.1、http://[::1] も許可されます。");
            }
        }

        if (errors.Count > 0)
        {
            throw new LocalAppSettingsValidationException(errors);
        }
    }

    private static JsonObject ReadRootOrNew(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new JsonObject();
            }

            return JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool IsAllowedUpdateFeedUri(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback);

    private static JsonObject GetOrReplaceObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }

    private static string GetString(IConfiguration configuration, string key, string fallback)
        => string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!;

    private static string? GetNullableString(IConfiguration configuration, string key)
        => NormalizeNullable(configuration[key]);

    private static int GetInt(IConfiguration configuration, string key, int fallback)
        => int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static int? GetNullableInt(IConfiguration configuration, string key)
        => int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool GetBool(IConfiguration configuration, string key, bool fallback)
        => bool.TryParse(configuration[key], out var value) ? value : fallback;

    private static List<string> GetStringList(IConfiguration configuration, string key, IReadOnlyList<string> fallback)
    {
        var values = configuration.GetSection(key).GetChildren()
            .Select(static child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
        return values.Count == 0 ? [.. fallback] : values;
    }

    private static string GetString(JsonObject root, string sectionName, string propertyName, string fallback)
        => root[sectionName] is JsonObject section && ReadString(section[propertyName]) is { Length: > 0 } value ? value : fallback;

    private static string GetString(JsonObject root, string sectionName, string childSectionName, string propertyName, string fallback)
        => root[sectionName] is JsonObject section
            && section[childSectionName] is JsonObject childSection
            && ReadString(childSection[propertyName]) is { Length: > 0 } value
                ? value
                : fallback;

    private static string? GetNullableString(JsonObject root, string sectionName, string propertyName, string? fallback)
        => root[sectionName] is JsonObject section && section.ContainsKey(propertyName)
            ? NormalizeNullable(ReadString(section[propertyName]))
            : fallback;

    private static int GetInt(JsonObject root, string sectionName, string propertyName, int fallback)
        => root[sectionName] is JsonObject section && ReadInt(section[propertyName]) is { } value ? value : fallback;

    private static int? GetNullableInt(JsonObject root, string sectionName, string propertyName, int? fallback)
        => root[sectionName] is JsonObject section && section.ContainsKey(propertyName)
            ? ReadInt(section[propertyName])
            : fallback;

    private static bool GetBool(JsonObject root, string sectionName, string propertyName, bool fallback)
        => root[sectionName] is JsonObject section && ReadBool(section[propertyName]) is { } value ? value : fallback;

    private static List<string> GetStringList(JsonObject root, string sectionName, string propertyName, IReadOnlyList<string> fallback)
    {
        if (root[sectionName] is not JsonObject section || section[propertyName] is not JsonArray array)
        {
            return [.. fallback];
        }

        var values = array
            .Select(ReadString)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToList();
        return values;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        return node.ToJsonString();
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
            if (value.TryGetValue<string>(out var stringValue)
                && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }
            if (value.TryGetValue<string>(out var stringValue) && bool.TryParse(stringValue, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string TrimOrEmpty(string? value)
        => value?.Trim() ?? string.Empty;

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> NormalizeStringList(IEnumerable<string> values)
        => values
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static List<string> NormalizeHosts(IEnumerable<string> values)
        => NormalizeStringList(values.Select(NormalizeHost));

    private static string NormalizeHost(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : trimmed;
    }

    private static void Require(string value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} は必須です。");
        }
    }

    private static void ValidateRange(int value, int min, int max, string name, List<string> errors)
    {
        if (value < min || value > max)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} は {min} から {max} の範囲で指定してください。"));
        }
    }
}