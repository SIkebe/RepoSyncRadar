using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RepoSyncRadar.App.Settings;
using Xunit;

namespace RepoSyncRadar.App.Tests.Settings;

public sealed class FileLocalAppSettingsStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public FileLocalAppSettingsStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rsr-local-appsettings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task LoadAsync_Reads_Local_Json_And_Uses_Configuration_Fallbacks()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "GitHub": {
                "Owner": "github-local",
                "MaxPullRequests": 8
              },
              "Copilot": {
                                "AllowedUrlHosts": [ "docs.github.com", "api.github.com" ],
                                "OAuthScopes": []
                            },
                            "WebView": {
                                                                "AllowedUrlHosts": [ "docs.github.com", "github.com" ]
              }
            }
            """,
            TestContext.Current.CancellationToken);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Repo"] = "docs-from-config",
                ["GitHub:PullRequestTitleFilter"] = "Repo sync",
                ["Copilot:DefaultModel"] = "gpt-config",
                ["Copilot:ContextTier"] = " Long_Context ",
                ["Copilot:OAuthScopes:0"] = "public_repo",
                ["DocsRepository:PrewarmOnStartup"] = "true",
            })
            .Build();
        var store = new FileLocalAppSettingsStore(path, configuration);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("github-local", settings.GitHub.Owner);
        Assert.Equal("docs-from-config", settings.GitHub.Repo);
        Assert.Equal(8, settings.GitHub.MaxPullRequests);
        Assert.Equal("gpt-config", settings.Copilot.DefaultModel);
        Assert.Equal("long_context", settings.Copilot.ContextTier);
        Assert.Equal(["docs.github.com", "api.github.com"], settings.Copilot.AllowedUrlHosts);
        Assert.Empty(settings.Copilot.OAuthScopes);
        Assert.Equal(["docs.github.com", "github.com"], settings.WebView.AllowedUrlHosts);
        Assert.True(settings.DocsRepository.PrewarmOnStartup);
    }

    [Fact]
    public async Task SaveAsync_Writes_Known_Sections_And_Preserves_Unknown_Values()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "Custom": { "Value": 1 },
              "GitHub": {
                "Owner": "old",
                "Unknown": "keep"
                            },
                            "DocsRepository": {
                                "MaxWorktrees": 5,
                                "PreviewCommand": "npm",
                                "PreviewArguments": "run dev",
                                "InstallArguments": "install",
                                "PreviewInstallArguments": "install",
                                "PreviewEnvironment": {
                                    "PORT": "{port}"
                                },
                                "PreviewEnvironmentVariables": "PORT={port}",
                                "Unknown": "keep-docsrepo"
              }
            }
            """,
            TestContext.Current.CancellationToken);
        var settings = LocalAppSettings.Default.Clone();
        settings.GitHub.Owner = "github";
        settings.GitHub.Repo = "docs";
        settings.GitHub.PullRequestCreatedAtOrAfter = "2026-05-15T00:00:00Z";
        settings.Copilot.DefaultModel = "gpt-5.5";
        settings.Copilot.ContextTier = " Long_Context ";
        settings.Copilot.LogLevel = " Debug ";
        settings.Copilot.SessionIdleTimeoutSeconds = 120;
        settings.Copilot.CopilotHome = " C:\\Users\\me\\.reposyncradar-copilot ";
        settings.Copilot.TelemetryFilePath = " C:\\logs\\copilot.jsonl ";
        settings.Copilot.TelemetryOtlpProtocol = " HTTP/JSON ";
        settings.Copilot.CaptureContent = true;
        settings.Copilot.EnableRemoteSessions = true;
        settings.Copilot.EnableSessionTelemetry = false;
        settings.Copilot.AllowedUrlHosts = ["https://docs.github.com", "api.github.com"];
        settings.WebView.AllowedUrlHosts = ["https://github.com", "github.githubassets.com"];
        settings.DocsRepository.BareCloneDir = "C:\\github\\.cache\\docs.git";
        settings.DocsRepository.PrewarmOnStartup = true;
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = " https://github.com/example/RepoSyncRadar ";
        settings.Updates.Channel = " win-arm64-preview ";
        settings.Updates.CheckTimeoutSeconds = 180;
        var events = new List<LocalAppSettings>();
        var store = new FileLocalAppSettingsStore(path);
        store.SettingsChanged += events.Add;

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);

        Assert.Single(events);
        Assert.Equal("docs.github.com", store.Current.Copilot.AllowedUrlHosts[0]);
        Assert.Equal("github.com", store.Current.WebView.AllowedUrlHosts[0]);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("Custom").GetProperty("Value").GetInt32());
        Assert.Equal("keep", root.GetProperty("GitHub").GetProperty("Unknown").GetString());
        Assert.Equal("github", root.GetProperty("GitHub").GetProperty("Owner").GetString());
        Assert.Equal("gpt-5.5", root.GetProperty("Copilot").GetProperty("DefaultModel").GetString());
        Assert.Equal("long_context", root.GetProperty("Copilot").GetProperty("ContextTier").GetString());
        Assert.Equal("debug", root.GetProperty("Copilot").GetProperty("LogLevel").GetString());
        Assert.Equal(120, root.GetProperty("Copilot").GetProperty("SessionIdleTimeoutSeconds").GetInt32());
        Assert.Equal("C:\\Users\\me\\.reposyncradar-copilot", root.GetProperty("Copilot").GetProperty("CopilotHome").GetString());
        Assert.Equal("C:\\logs\\copilot.jsonl", root.GetProperty("Copilot").GetProperty("TelemetryFilePath").GetString());
        Assert.Equal("http/json", root.GetProperty("Copilot").GetProperty("TelemetryOtlpProtocol").GetString());
        Assert.True(root.GetProperty("Copilot").GetProperty("CaptureContent").GetBoolean());
        Assert.True(root.GetProperty("Copilot").GetProperty("EnableRemoteSessions").GetBoolean());
        Assert.False(root.GetProperty("Copilot").GetProperty("EnableSessionTelemetry").GetBoolean());
        Assert.Equal("docs.github.com", root.GetProperty("Copilot").GetProperty("AllowedUrlHosts")[0].GetString());
        Assert.Equal("github.com", root.GetProperty("WebView").GetProperty("AllowedUrlHosts")[0].GetString());
        var docsRepository = root.GetProperty("DocsRepository");
        Assert.True(docsRepository.GetProperty("PrewarmOnStartup").GetBoolean());
        Assert.Equal("keep-docsrepo", docsRepository.GetProperty("Unknown").GetString());
        Assert.False(docsRepository.TryGetProperty("MaxWorktrees", out _));
        Assert.False(docsRepository.TryGetProperty("PreviewCommand", out _));
        Assert.False(docsRepository.TryGetProperty("PreviewArguments", out _));
        Assert.False(docsRepository.TryGetProperty("InstallArguments", out _));
        Assert.False(docsRepository.TryGetProperty("PreviewInstallArguments", out _));
        Assert.False(docsRepository.TryGetProperty("PreviewEnvironment", out _));
        Assert.False(docsRepository.TryGetProperty("PreviewEnvironmentVariables", out _));
        Assert.True(root.GetProperty("Updates").GetProperty("Enabled").GetBoolean());
        Assert.Equal("https://github.com/example/RepoSyncRadar", root.GetProperty("Updates").GetProperty("FeedUrl").GetString());
        Assert.Equal("win-arm64-preview", root.GetProperty("Updates").GetProperty("Channel").GetString());
        Assert.Equal(180, root.GetProperty("Updates").GetProperty("CheckTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task SaveAsync_Invalid_DocsApi_BaseAddress_Throws_Validation_Error()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        var settings = LocalAppSettings.Default.Clone();
        settings.DocsApi.BaseAddress = "http://docs.github.com/";
        var store = new FileLocalAppSettingsStore(path);

        var ex = await Assert.ThrowsAsync<LocalAppSettingsValidationException>(
            () => store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Contains("DocsApi.BaseAddress", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_Invalid_Copilot_ContextTier_Throws_Validation_Error()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        var settings = LocalAppSettings.Default.Clone();
        settings.Copilot.ContextTier = "huge";
        var store = new FileLocalAppSettingsStore(path);

        var ex = await Assert.ThrowsAsync<LocalAppSettingsValidationException>(
            () => store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Contains("Copilot.ContextTier", ex.Message, StringComparison.Ordinal);
        Assert.Contains("default", ex.Message, StringComparison.Ordinal);
        Assert.Contains("long_context", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_Enabled_Updates_Without_FeedUrl_Throws_Validation_Error()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = string.Empty;
        var store = new FileLocalAppSettingsStore(path);

        var ex = await Assert.ThrowsAsync<LocalAppSettingsValidationException>(
            () => store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Contains("Updates.FeedUrl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_Enabled_Updates_With_Remote_Http_Feed_Throws_Validation_Error()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "http://updates.example.com/reposyncradar";
        var store = new FileLocalAppSettingsStore(path);

        var ex = await Assert.ThrowsAsync<LocalAppSettingsValidationException>(
            () => store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Contains("Updates.FeedUrl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_Enabled_Updates_Allows_Loopback_Http_Feed()
    {
        var path = Path.Combine(_tempRoot, "appsettings.local.json");
        var settings = LocalAppSettings.Default.Clone();
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "http://127.0.0.1:4510/updates";
        var store = new FileLocalAppSettingsStore(path);

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:4510/updates", store.Current.Updates.FeedUrl);
    }

    [Fact]
    public void ResolveDefaultSettingsPath_When_Not_In_Project_Uses_LocalAppData()
    {
        var basePath = Path.Combine(_tempRoot, "installed", "current");
        var localAppData = Path.Combine(_tempRoot, "local-app-data");
        Directory.CreateDirectory(basePath);

        var path = FileLocalAppSettingsStore.ResolveDefaultSettingsPath(basePath, null, localAppData);

        Assert.Equal(
            Path.Combine(localAppData, "RepoSyncRadar", "appsettings.local.json"),
            path);
    }

    [Fact]
    public async Task TryCopyLegacyLocalSettings_When_User_Settings_Missing_Copies_Base_Local_Settings()
    {
        var basePath = Path.Combine(_tempRoot, "installed", "current");
        var userSettingsPath = Path.Combine(_tempRoot, "local-app-data", "RepoSyncRadar", "appsettings.local.json");
        Directory.CreateDirectory(basePath);
        await File.WriteAllTextAsync(
            Path.Combine(basePath, "appsettings.local.json"),
            """
            {
              "Updates": {
                "Enabled": true,
                "FeedUrl": "https://github.com/example/RepoSyncRadar"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        FileLocalAppSettingsStore.TryCopyLegacyLocalSettings(userSettingsPath, basePath);

        Assert.True(File.Exists(userSettingsPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(userSettingsPath, TestContext.Current.CancellationToken));
        Assert.True(document.RootElement.GetProperty("Updates").GetProperty("Enabled").GetBoolean());
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