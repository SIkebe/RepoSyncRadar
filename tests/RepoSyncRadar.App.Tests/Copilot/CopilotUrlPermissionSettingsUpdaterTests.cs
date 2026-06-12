using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.App.Settings;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotUrlPermissionSettingsUpdaterTests
{
    [Fact]
    public async Task AddHostFromUrlAsync_Adds_Host_To_Settings_And_Runtime_AllowList()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Copilot.AllowedUrlHosts = ["docs.github.com", "api.github.com"];
        var store = new FakeLocalAppSettingsStore(settings);
        var allowList = new UrlAllowList(Options.Create(new CopilotOptions
        {
            AllowedUrlHosts = settings.Copilot.AllowedUrlHosts,
        }));
        var updater = new CopilotUrlPermissionSettingsUpdater(store, allowList);

        var added = await updater.AddHostFromUrlAsync(
            "https://raw.githubusercontent.com/github/docs/main/data/example.json",
            TestContext.Current.CancellationToken);

        Assert.True(added);
        Assert.NotNull(store.Saved);
        Assert.Equal(
            ["docs.github.com", "api.github.com", "raw.githubusercontent.com"],
            store.Saved.Copilot.AllowedUrlHosts);
        Assert.True(allowList.IsAllowed("https://raw.githubusercontent.com/github/docs/main/data/example.json"));
    }

    [Fact]
    public async Task AddHostFromUrlAsync_Does_Not_Duplicate_Existing_Host()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Copilot.AllowedUrlHosts = ["docs.github.com", "GitHub.com"];
        var store = new FakeLocalAppSettingsStore(settings);
        var allowList = new UrlAllowList(Options.Create(new CopilotOptions
        {
            AllowedUrlHosts = settings.Copilot.AllowedUrlHosts,
        }));
        var updater = new CopilotUrlPermissionSettingsUpdater(store, allowList);

        var added = await updater.AddHostFromUrlAsync(
            "https://github.com/github/docs/commit/abc123",
            TestContext.Current.CancellationToken);

        Assert.True(added);
        Assert.Null(store.Saved);
        Assert.Equal(["docs.github.com", "GitHub.com"], store.Current.Copilot.AllowedUrlHosts);
        Assert.True(allowList.IsAllowed("https://github.com/github/docs/commit/abc123"));
    }

    [Theory]
    [InlineData("http://github.com/github/docs/commit/abc123")]
    [InlineData("not a url")]
    public async Task AddHostFromUrlAsync_Rejects_NonPersistable_Urls(string url)
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Copilot.AllowedUrlHosts = ["docs.github.com"];
        var store = new FakeLocalAppSettingsStore(settings);
        var allowList = new UrlAllowList(Options.Create(new CopilotOptions
        {
            AllowedUrlHosts = settings.Copilot.AllowedUrlHosts,
        }));
        var updater = new CopilotUrlPermissionSettingsUpdater(store, allowList);

        var added = await updater.AddHostFromUrlAsync(url, TestContext.Current.CancellationToken);

        Assert.False(added);
        Assert.Null(store.Saved);
        Assert.Equal(["docs.github.com"], store.Current.Copilot.AllowedUrlHosts);
    }

    private sealed class FakeLocalAppSettingsStore(LocalAppSettings initial) : ILocalAppSettingsStore
    {
        public string SettingsPath { get; } = Path.Combine(Path.GetTempPath(), "appsettings.local.json");

        public LocalAppSettings Current { get; private set; } = initial.Clone();

        public LocalAppSettings? Saved { get; private set; }

        public event Action<LocalAppSettings>? SettingsChanged;

        public Task<LocalAppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current.Clone());
        }

        public Task SaveAsync(LocalAppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = settings.Clone();
            Saved = settings.Clone();
            SettingsChanged?.Invoke(Current.Clone());
            return Task.CompletedTask;
        }
    }
}