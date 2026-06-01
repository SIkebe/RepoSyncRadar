using Microsoft.Extensions.Logging.Abstractions;
using RepoSyncRadar.App.Settings;
using RepoSyncRadar.App.Updates;
using Velopack;
using Xunit;

namespace RepoSyncRadar.App.Tests.Updates;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task CheckAndDownloadAsync_When_Disabled_Does_Not_Create_Manager()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = false;
        var factory = new FakeUpdateManagerFactory(new FakeUpdateManager { IsInstalled = true });
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateStatus.Disabled, result.Status);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_When_Not_Installed_Returns_NotInstalled()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        var factory = new FakeUpdateManagerFactory(new FakeUpdateManager { IsInstalled = false, CurrentVersion = null });
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateStatus.NotInstalled, result.Status);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_When_No_Update_Returns_NoUpdate()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        settings.Updates.Channel = "win-x64-preview";
        var manager = new FakeUpdateManager { IsInstalled = true, CurrentVersion = "0.1.0" };
        var factory = new FakeUpdateManagerFactory(manager);
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateStatus.NoUpdate, result.Status);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("https://github.com/example/RepoSyncRadar", factory.FeedUrl);
        Assert.Equal("win-x64-preview", factory.Channel);
        Assert.Equal(1, manager.CheckCount);
        Assert.Equal(0, manager.DownloadCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_When_Explicit_Ignores_CheckOnStartup_Disabled()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.CheckOnStartup = false;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        var manager = new FakeUpdateManager { IsInstalled = true, CurrentVersion = "0.1.0" };
        var factory = new FakeUpdateManagerFactory(manager);
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken, ignoreCheckOnStartup: true);

        Assert.Equal(AppUpdateStatus.NoUpdate, result.Status);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, manager.CheckCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_When_Startup_Check_Disabled_Returns_Disabled()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.CheckOnStartup = false;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        var factory = new FakeUpdateManagerFactory(new FakeUpdateManager { IsInstalled = true });
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateStatus.Disabled, result.Status);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_When_Remote_Http_Feed_Is_Loaded_Does_Not_Create_Manager()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "http://updates.example.com/RepoSyncRadar";
        var factory = new FakeUpdateManagerFactory(new FakeUpdateManager { IsInstalled = true });
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateStatus.FeedNotConfigured, result.Status);
        Assert.Contains("Updates.FeedUrl", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_When_Loopback_Http_Feed_Is_Loaded_Creates_Manager()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "http://127.0.0.1:4510/updates";
        var factory = new FakeUpdateManagerFactory(new FakeUpdateManager { IsInstalled = true });
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var result = await service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AppUpdateStatus.NoUpdate, result.Status);
        Assert.Equal("http://127.0.0.1:4510/updates", factory.FeedUrl);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_Applies_Timeout_To_Update_Check()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        settings.Updates.CheckTimeoutSeconds = 0;
        var manager = new FakeUpdateManager
        {
            IsInstalled = true,
            CheckResult = new TaskCompletionSource<UpdateInfo?>().Task,
        };
        var factory = new FakeUpdateManagerFactory(manager);
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAndDownloadAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, manager.CheckCount);
        Assert.Equal(0, manager.DownloadCount);
    }

    [Fact]
    public void TryApplyDownloadedUpdateAndRestart_When_Pending_Update_Applies_Update()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        var manager = new FakeUpdateManager
        {
            IsInstalled = true,
            HasUpdatePendingRestart = true,
        };
        var factory = new FakeUpdateManagerFactory(manager);
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var applied = service.TryApplyDownloadedUpdateAndRestart();

        Assert.True(applied);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, manager.ApplyCount);
    }

    [Fact]
    public void TryApplyDownloadedUpdateAndRestart_When_No_Pending_Update_Returns_False()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        var manager = new FakeUpdateManager
        {
            IsInstalled = true,
            HasUpdatePendingRestart = false,
        };
        var factory = new FakeUpdateManagerFactory(manager);
        var service = new AppUpdateService(new FakeLocalAppSettingsStore(settings), factory, NullLogger<AppUpdateService>.Instance);

        var applied = service.TryApplyDownloadedUpdateAndRestart();

        Assert.False(applied);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, manager.ApplyCount);
    }

    private sealed class FakeLocalAppSettingsStore(LocalAppSettings settings) : ILocalAppSettingsStore
    {
        public string SettingsPath { get; } = "appsettings.local.json";

        public LocalAppSettings Current { get; } = settings.Clone();

        public event Action<LocalAppSettings>? SettingsChanged;

        public Task<LocalAppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current.Clone());

        public Task SaveAsync(LocalAppSettings settings, CancellationToken cancellationToken = default)
        {
            SettingsChanged?.Invoke(settings.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpdateManagerFactory(IVelopackUpdateManager manager) : IVelopackUpdateManagerFactory
    {
        public int CreateCount { get; private set; }

        public string? FeedUrl { get; private set; }

        public string? Channel { get; private set; }

        public IVelopackUpdateManager Create(string feedUrl, string? channel)
        {
            CreateCount++;
            FeedUrl = feedUrl;
            Channel = channel;
            return manager;
        }
    }

    private sealed class FakeUpdateManager : IVelopackUpdateManager
    {
        public bool IsInstalled { get; init; }

        public string? CurrentVersion { get; init; }

        public bool HasUpdatePendingRestart { get; init; }

        public int CheckCount { get; private set; }

        public int DownloadCount { get; private set; }

        public int ApplyCount { get; private set; }

        public Task<UpdateInfo?>? CheckResult { get; init; }

        public Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return CheckResult ?? Task.FromResult<UpdateInfo?>(null);
        }

        public Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress, CancellationToken cancellationToken)
        {
            DownloadCount++;
            return Task.CompletedTask;
        }

        public void ApplyUpdatesAndRestart()
        {
            ApplyCount++;
        }
    }
}
