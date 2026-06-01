using Microsoft.Extensions.Logging;
using RepoSyncRadar.App.Settings;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace RepoSyncRadar.App.Updates;

public enum AppUpdateStatus
{
    Disabled,
    FeedNotConfigured,
    NotInstalled,
    NoUpdate,
    Downloaded,
}

public enum AppUpdateActivityStatus
{
    Checking,
    Downloading,
    Downloaded,
    Failed,
}

public sealed record AppUpdateActivity(
    AppUpdateActivityStatus Status,
    int? Progress = null,
    string? CurrentVersion = null,
    string? AvailableVersion = null,
    string? Message = null);

public sealed record AppUpdateResult(
    AppUpdateStatus Status,
    string? CurrentVersion = null,
    string? AvailableVersion = null,
    string? Message = null);

public interface IAppUpdateService
{
    AppUpdateActivity? CurrentActivity { get; }

    event Action? ActivityChanged;

    Task<AppUpdateResult> CheckAndDownloadAsync(
        IProgress<int>? progress = null,
        bool ignoreCheckOnStartup = false,
        CancellationToken cancellationToken = default);

    bool TryApplyDownloadedUpdateAndRestart();
}

public interface IVelopackUpdateManager
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    bool HasUpdatePendingRestart { get; }

    Task<UpdateInfo?> CheckForUpdatesAsync();

    Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken);

    void ApplyUpdatesAndRestart();
}

public interface IVelopackUpdateManagerFactory
{
    IVelopackUpdateManager Create(string feedUrl, string? channel);
}

public sealed partial class AppUpdateService : IAppUpdateService
{
    private readonly ILocalAppSettingsStore _settingsStore;
    private readonly IVelopackUpdateManagerFactory _updateManagerFactory;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly object _activityGate = new();
    private AppUpdateActivity? _currentActivity;

    public AppUpdateService(
        ILocalAppSettingsStore settingsStore,
        IVelopackUpdateManagerFactory updateManagerFactory,
        ILogger<AppUpdateService> logger)
    {
        _settingsStore = settingsStore;
        _updateManagerFactory = updateManagerFactory;
        _logger = logger;
    }

    public AppUpdateActivity? CurrentActivity
    {
        get
        {
            lock (_activityGate)
            {
                return _currentActivity;
            }
        }
    }

    public event Action? ActivityChanged;

    public async Task<AppUpdateResult> CheckAndDownloadAsync(
        IProgress<int>? progress = null,
        bool ignoreCheckOnStartup = false,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Current.Updates.Clone();
        if (!settings.Enabled || (!ignoreCheckOnStartup && !settings.CheckOnStartup))
        {
            return new AppUpdateResult(AppUpdateStatus.Disabled);
        }

        if (string.IsNullOrWhiteSpace(settings.FeedUrl))
        {
            return new AppUpdateResult(AppUpdateStatus.FeedNotConfigured);
        }

        if (!Uri.TryCreate(settings.FeedUrl, UriKind.Absolute, out var feedUri)
            || !IsAllowedUpdateFeedUri(feedUri))
        {
            return new AppUpdateResult(
                AppUpdateStatus.FeedNotConfigured,
                Message: "Updates.FeedUrl must use https for remote feeds. Loopback http is allowed for local smoke tests.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.CheckTimeoutSeconds));

        var manager = _updateManagerFactory.Create(settings.FeedUrl, settings.Channel);
        if (!manager.IsInstalled)
        {
            PublishActivity(null);
            return new AppUpdateResult(AppUpdateStatus.NotInstalled, manager.CurrentVersion, Message: "Application is not installed by Velopack.");
        }

        try
        {
            PublishActivity(new AppUpdateActivity(
                AppUpdateActivityStatus.Checking,
                CurrentVersion: manager.CurrentVersion,
                Message: "Checking for updates..."));
            var update = await manager.CheckForUpdatesAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            if (update is null)
            {
                PublishActivity(null);
                return new AppUpdateResult(AppUpdateStatus.NoUpdate, manager.CurrentVersion);
            }

            var availableVersion = update.TargetFullRelease.Version.ToString();
            PublishActivity(new AppUpdateActivity(
                AppUpdateActivityStatus.Downloading,
                0,
                manager.CurrentVersion,
                availableVersion,
                "Downloading update..."));
            Action<int> progressCallback = value =>
            {
                var normalizedValue = Math.Clamp(value, 0, 100);
                PublishActivity(new AppUpdateActivity(
                    AppUpdateActivityStatus.Downloading,
                    normalizedValue,
                    manager.CurrentVersion,
                    availableVersion,
                    "Downloading update..."));
                progress?.Report(normalizedValue);
            };
            await manager.DownloadUpdatesAsync(update, progressCallback, timeout.Token).ConfigureAwait(false);
            LogUpdateDownloaded(_logger, manager.CurrentVersion, availableVersion);
            var result = new AppUpdateResult(
                AppUpdateStatus.Downloaded,
                manager.CurrentVersion,
                availableVersion,
                "Update downloaded and will be applied on next launch.");
            PublishActivity(new AppUpdateActivity(
                AppUpdateActivityStatus.Downloaded,
                100,
                result.CurrentVersion,
                result.AvailableVersion,
                result.Message));
            return result;
        }
        catch (NotInstalledException)
        {
            PublishActivity(null);
            return new AppUpdateResult(
                AppUpdateStatus.NotInstalled,
                manager.CurrentVersion,
                Message: "Application is not installed by Velopack.");
        }
        catch (OperationCanceledException)
        {
            PublishActivity(null);
            throw;
        }
        catch (Exception ex)
        {
            PublishActivity(new AppUpdateActivity(
                AppUpdateActivityStatus.Failed,
                CurrentVersion: manager.CurrentVersion,
                Message: ex.Message));
            throw;
        }
    }

    public bool TryApplyDownloadedUpdateAndRestart()
    {
        var settings = _settingsStore.Current.Updates.Clone();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.FeedUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(settings.FeedUrl, UriKind.Absolute, out var feedUri)
            || !IsAllowedUpdateFeedUri(feedUri))
        {
            return false;
        }

        var manager = _updateManagerFactory.Create(settings.FeedUrl, settings.Channel);
        if (!manager.IsInstalled || !manager.HasUpdatePendingRestart)
        {
            return false;
        }

        manager.ApplyUpdatesAndRestart();
        return true;
    }

    private void PublishActivity(AppUpdateActivity? activity)
    {
        lock (_activityGate)
        {
            _currentActivity = activity;
        }

        ActivityChanged?.Invoke();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Downloaded Velopack update. Current={CurrentVersion}; Available={AvailableVersion}")]
    private static partial void LogUpdateDownloaded(ILogger logger, string? currentVersion, string availableVersion);

    private static bool IsAllowedUpdateFeedUri(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback);
}

public sealed class VelopackUpdateManagerFactory : IVelopackUpdateManagerFactory
{
    public IVelopackUpdateManager Create(string feedUrl, string? channel)
        => new VelopackUpdateManagerAdapter(feedUrl, channel);
}

internal sealed class VelopackUpdateManagerAdapter : IVelopackUpdateManager
{
    private readonly UpdateManager _manager;

    public VelopackUpdateManagerAdapter(string feedUrl, string? channel)
    {
        var options = string.IsNullOrWhiteSpace(channel)
            ? null
            : new UpdateOptions { ExplicitChannel = channel };
        _manager = TryCreateGitHubSource(feedUrl, channel, out var source)
            ? new UpdateManager(source, options)
            : new UpdateManager(feedUrl, options);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public bool HasUpdatePendingRestart => _manager.UpdatePendingRestart is not null;

    public Task<UpdateInfo?> CheckForUpdatesAsync()
        => _manager.CheckForUpdatesAsync();

    public Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken)
        => _manager.DownloadUpdatesAsync(updates, progress, cancellationToken);

    public void ApplyUpdatesAndRestart()
    {
        var pendingUpdate = _manager.UpdatePendingRestart;
        if (pendingUpdate is null)
        {
            return;
        }

        _manager.ApplyUpdatesAndRestart(pendingUpdate, []);
    }

    internal static bool TryCreateGitHubSource(string feedUrl, string? channel, out GithubSource source)
    {
        source = default!;
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length != 2)
        {
            return false;
        }

        source = new GithubSource(feedUrl, accessToken: null, prerelease: ShouldIncludeGitHubPrereleases(channel), downloader: null);
        return true;
    }

    internal static bool ShouldIncludeGitHubPrereleases(string? channel)
        => !string.IsNullOrWhiteSpace(channel)
            && (channel.Contains("beta", StringComparison.OrdinalIgnoreCase)
                || channel.Contains("preview", StringComparison.OrdinalIgnoreCase));
}
