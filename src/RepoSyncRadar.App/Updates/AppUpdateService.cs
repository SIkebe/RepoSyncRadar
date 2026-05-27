using Microsoft.Extensions.Logging;
using RepoSyncRadar.App.Settings;
using Velopack;
using Velopack.Exceptions;

namespace RepoSyncRadar.App.Updates;

public enum AppUpdateStatus
{
    Disabled,
    FeedNotConfigured,
    NotInstalled,
    NoUpdate,
    Downloaded,
}

public sealed record AppUpdateResult(
    AppUpdateStatus Status,
    string? CurrentVersion = null,
    string? AvailableVersion = null,
    string? Message = null);

public interface IAppUpdateService
{
    Task<AppUpdateResult> CheckAndDownloadAsync(
        IProgress<int>? progress = null,
        bool ignoreCheckOnStartup = false,
        CancellationToken cancellationToken = default);
}

public interface IVelopackUpdateManager
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    Task<UpdateInfo?> CheckForUpdatesAsync();

    Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken);
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

    public AppUpdateService(
        ILocalAppSettingsStore settingsStore,
        IVelopackUpdateManagerFactory updateManagerFactory,
        ILogger<AppUpdateService> logger)
    {
        _settingsStore = settingsStore;
        _updateManagerFactory = updateManagerFactory;
        _logger = logger;
    }

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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.CheckTimeoutSeconds));

        var manager = _updateManagerFactory.Create(settings.FeedUrl, settings.Channel);
        if (!manager.IsInstalled)
        {
            return new AppUpdateResult(AppUpdateStatus.NotInstalled, manager.CurrentVersion, Message: "Application is not installed by Velopack.");
        }

        try
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return new AppUpdateResult(AppUpdateStatus.NoUpdate, manager.CurrentVersion);
            }

            Action<int>? progressCallback = progress is null ? null : progress.Report;
            await manager.DownloadUpdatesAsync(update, progressCallback, timeout.Token).ConfigureAwait(false);
            var availableVersion = update.TargetFullRelease.Version.ToString();
            LogUpdateDownloaded(_logger, manager.CurrentVersion, availableVersion);
            return new AppUpdateResult(
                AppUpdateStatus.Downloaded,
                manager.CurrentVersion,
                availableVersion,
                "Update downloaded and will be applied on next launch.");
        }
        catch (NotInstalledException)
        {
            return new AppUpdateResult(AppUpdateStatus.NotInstalled, manager.CurrentVersion, Message: "Application is not installed by Velopack.");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Downloaded Velopack update. Current={CurrentVersion}; Available={AvailableVersion}")]
    private static partial void LogUpdateDownloaded(ILogger logger, string? currentVersion, string availableVersion);
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
        _manager = new UpdateManager(feedUrl, options);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public Task<UpdateInfo?> CheckForUpdatesAsync()
        => _manager.CheckForUpdatesAsync();

    public Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken)
        => _manager.DownloadUpdatesAsync(updates, progress, cancellationToken);
}