using RepoSyncRadar.App;
using RepoSyncRadar.App.Settings;
using Xunit;

namespace RepoSyncRadar.App.Tests.Settings;

public sealed class FileAppUserSettingsStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public FileAppUserSettingsStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rsr-user-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task LoadAsync_When_File_Is_Missing_Returns_Dark_Default()
    {
        var store = new FileAppUserSettingsStore(Path.Combine(_tempRoot, "settings.json"));

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DocsThemeMode.Dark, settings.DefaultDocsTheme);
        Assert.Equal("ja", settings.DisplayCulture);
        Assert.Equal(DocsThemeMode.Dark, store.Current.DefaultDocsTheme);
        Assert.Equal("ja", store.Current.DisplayCulture);
    }

    [Fact]
    public async Task SaveDisplayCultureAsync_Persists_Culture_And_Raises_Changed()
    {
        var path = Path.Combine(_tempRoot, "settings.json");
        using var store = new FileAppUserSettingsStore(path);
        var events = new List<AppUserSettings>();
        store.SettingsChanged += events.Add;

        await store.SaveDisplayCultureAsync("en-US", TestContext.Current.CancellationToken);

        Assert.Equal("en", store.Current.DisplayCulture);
        Assert.Single(events);
        Assert.Equal("en", events[0].DisplayCulture);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"displayCulture\": \"en\"", json, StringComparison.Ordinal);

        using var reloaded = new FileAppUserSettingsStore(path);
        Assert.Equal("en", reloaded.Current.DisplayCulture);
    }

    [Fact]
    public async Task SaveDefaultDocsThemeAsync_Persists_Theme_And_Raises_Changed()
    {
        var path = Path.Combine(_tempRoot, "settings.json");
        using var store = new FileAppUserSettingsStore(path);
        var events = new List<AppUserSettings>();
        store.SettingsChanged += events.Add;

        await store.SaveDefaultDocsThemeAsync(DocsThemeMode.Light, TestContext.Current.CancellationToken);

        Assert.Equal(DocsThemeMode.Light, store.Current.DefaultDocsTheme);
        Assert.Single(events);
        Assert.Equal(DocsThemeMode.Light, events[0].DefaultDocsTheme);
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"defaultDocsTheme\": \"Light\"", json, StringComparison.Ordinal);

        using var reloaded = new FileAppUserSettingsStore(path);
        Assert.Equal(DocsThemeMode.Light, reloaded.Current.DefaultDocsTheme);
    }

    [Fact]
    public async Task LoadAsync_When_File_Is_Invalid_Returns_Dark_Default()
    {
        var path = Path.Combine(_tempRoot, "settings.json");
        await File.WriteAllTextAsync(path, "{ invalid json", TestContext.Current.CancellationToken);

        var store = new FileAppUserSettingsStore(path);

        Assert.Equal(DocsThemeMode.Dark, store.Current.DefaultDocsTheme);
        Assert.Equal("ja", store.Current.DisplayCulture);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}