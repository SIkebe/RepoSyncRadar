namespace RepoSyncRadar.App.Settings;

public sealed class LocalAppSettings
{
    public static LocalAppSettings Default { get; } = new();

    public GitHubLocalAppSettings GitHub { get; set; } = new();

    public DocsApiLocalAppSettings DocsApi { get; set; } = new();

    public CopilotLocalAppSettings Copilot { get; set; } = new();

    public WebViewLocalAppSettings WebView { get; set; } = new();

    public DocsRepositoryLocalAppSettings DocsRepository { get; set; } = new();

    public LoggingLocalAppSettings Logging { get; set; } = new();

    public LocalAppSettings Clone()
        => new()
        {
            GitHub = GitHub.Clone(),
            DocsApi = DocsApi.Clone(),
            Copilot = Copilot.Clone(),
            WebView = WebView.Clone(),
            DocsRepository = DocsRepository.Clone(),
            Logging = Logging.Clone(),
        };
}

public sealed class GitHubLocalAppSettings
{
    public string Owner { get; set; } = "github";

    public string Repo { get; set; } = "docs";

    public string PullRequestTitleFilter { get; set; } = "Repo sync";

    public int MaxPullRequests { get; set; } = 5;

    public string? PullRequestCreatedAtOrAfter { get; set; }

    public GitHubLocalAppSettings Clone()
        => new()
        {
            Owner = Owner,
            Repo = Repo,
            PullRequestTitleFilter = PullRequestTitleFilter,
            MaxPullRequests = MaxPullRequests,
            PullRequestCreatedAtOrAfter = PullRequestCreatedAtOrAfter,
        };
}

public sealed class DocsApiLocalAppSettings
{
    public string BaseAddress { get; set; } = "https://docs.github.com/";

    public string DefaultLanguage { get; set; } = "en";

    public string ClientName { get; set; } = "reposyncradar";

    public int PageListCacheSeconds { get; set; } = 86_400;

    public DocsApiLocalAppSettings Clone()
        => new()
        {
            BaseAddress = BaseAddress,
            DefaultLanguage = DefaultLanguage,
            ClientName = ClientName,
            PageListCacheSeconds = PageListCacheSeconds,
        };
}

public sealed class CopilotLocalAppSettings
{
    public string DefaultModel { get; set; } = "gpt-5";

    public bool Streaming { get; set; } = true;

    public bool CaptureContent { get; set; }

    public List<string> AllowedUrlHosts { get; set; } =
    [
        "docs.github.com",
        "api.github.com",
    ];

    public string? OAuthClientId { get; set; }

    public List<string> OAuthScopes { get; set; } = [];

    public CopilotLocalAppSettings Clone()
        => new()
        {
            DefaultModel = DefaultModel,
            Streaming = Streaming,
            CaptureContent = CaptureContent,
            AllowedUrlHosts = [.. AllowedUrlHosts],
            OAuthClientId = OAuthClientId,
            OAuthScopes = [.. OAuthScopes],
        };
}

public sealed class WebViewLocalAppSettings
{
    public List<string> AllowedUrlHosts { get; set; } =
    [
        "docs.github.com",
        "github.com",
        "github.githubassets.com",
        "avatars.githubusercontent.com",
    ];

    public WebViewLocalAppSettings Clone()
        => new()
        {
            AllowedUrlHosts = [.. AllowedUrlHosts],
        };
}

public sealed class DocsRepositoryLocalAppSettings
{
    public string BareCloneDir { get; set; } = string.Empty;

    public string CloneUrl { get; set; } = string.Empty;

    public string WorktreeRoot { get; set; } = string.Empty;

    public int MaxWorktrees { get; set; } = 5;

    public string PreviewCommand { get; set; } = "npm";

    public string PreviewArguments { get; set; } = "run dev";

    public string PreviewInstallArguments { get; set; } = "install";

    public Dictionary<string, string> PreviewEnvironment { get; set; } = new(StringComparer.Ordinal)
    {
        ["PORT"] = "{port}",
        ["REQUEST_TIMEOUT"] = "600000",
    };

    public int PreviewBasePort { get; set; } = 4500;

    public int PreviewReadyTimeoutSeconds { get; set; } = 600;

    public DocsRepositoryLocalAppSettings Clone()
        => new()
        {
            BareCloneDir = BareCloneDir,
            CloneUrl = CloneUrl,
            WorktreeRoot = WorktreeRoot,
            MaxWorktrees = MaxWorktrees,
            PreviewCommand = PreviewCommand,
            PreviewArguments = PreviewArguments,
            PreviewInstallArguments = PreviewInstallArguments,
            PreviewEnvironment = new Dictionary<string, string>(PreviewEnvironment, StringComparer.Ordinal),
            PreviewBasePort = PreviewBasePort,
            PreviewReadyTimeoutSeconds = PreviewReadyTimeoutSeconds,
        };
}

public sealed class LoggingLocalAppSettings
{
    public string DefaultLogLevel { get; set; } = "Information";

    public string MicrosoftLogLevel { get; set; } = "Warning";

    public LoggingLocalAppSettings Clone()
        => new()
        {
            DefaultLogLevel = DefaultLogLevel,
            MicrosoftLogLevel = MicrosoftLogLevel,
        };
}

public interface ILocalAppSettingsStore
{
    string SettingsPath { get; }

    LocalAppSettings Current { get; }

    event Action<LocalAppSettings>? SettingsChanged;

    Task<LocalAppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LocalAppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class LocalAppSettingsValidationException : Exception
{
    public LocalAppSettingsValidationException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}