using AngleSharp.Html.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Settings;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class LocalAppSettingsEditorTests
{
    [Fact]
    public void Renders_Local_Appsettings_Values()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.GitHub.Owner = "github-local";
        settings.Copilot.DefaultModel = "gpt-5.5";
        settings.Copilot.ContextTier = "long_context";
        settings.Copilot.EnableRemoteSessions = true;
        settings.Copilot.EnableSessionTelemetry = false;
        settings.Copilot.AllowedUrlHosts = ["docs.github.com", "api.github.com"];
        settings.WebView.AllowedUrlHosts = ["docs.github.com", "github.com"];
        settings.Updates.Enabled = true;
        settings.Updates.FeedUrl = "https://github.com/example/RepoSyncRadar";
        settings.Updates.Channel = "win-arm64-preview";
        settings.DocsRepository.PrewarmOnStartup = true;
        var store = new FakeLocalAppSettingsStore(settings);
        using var ctx = new BunitContext();

        var cut = ctx.Render<LocalAppSettingsEditor>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(BuildServices(store)));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("github-local", cut.Find("[data-testid=\"settings-github-owner\"]").GetAttribute("value"));
            Assert.Equal("gpt-5.5", cut.Find("[data-testid=\"settings-copilot-model\"]").GetAttribute("value"));
            var contextTier = Assert.IsAssignableFrom<IHtmlSelectElement>(cut.Find("[data-testid=\"settings-copilot-context-tier\"]"));
            Assert.Equal("long_context", contextTier.Value);
            Assert.True(cut.Find("[data-testid=\"settings-copilot-enable-remote-sessions\"]").HasAttribute("checked"));
            Assert.False(cut.Find("[data-testid=\"settings-copilot-enable-session-telemetry\"]").HasAttribute("checked"));
            Assert.Contains("api.github.com", cut.Find("[data-testid=\"settings-copilot-allowed-hosts\"]").GetAttribute("value"), StringComparison.Ordinal);
            Assert.Contains("github.com", cut.Find("[data-testid=\"settings-webview-allowed-hosts\"]").GetAttribute("value"), StringComparison.Ordinal);
            Assert.Equal("https://github.com/example/RepoSyncRadar", cut.Find("[data-testid=\"settings-updates-feed-url\"]").GetAttribute("value"));
            Assert.Equal("win-arm64-preview", cut.Find("[data-testid=\"settings-updates-channel\"]").GetAttribute("value"));
            Assert.True(cut.Find("[data-testid=\"settings-docsrepo-prewarm-on-startup\"]").HasAttribute("checked"));
            Assert.Contains("通常は配布版に同梱", cut.Find("[data-testid=\"settings-copilot-oauth-client-id\"]").ParentElement!.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Renders_ContextTier_Unset_Option_With_Label()
    {
        var settings = LocalAppSettings.Default.Clone();
        var store = new FakeLocalAppSettingsStore(settings);
        using var ctx = new BunitContext();

        var cut = ctx.Render<LocalAppSettingsEditor>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(BuildServices(store)));

        cut.WaitForAssertion(() =>
        {
            var contextTier = Assert.IsAssignableFrom<IHtmlSelectElement>(cut.Find("[data-testid=\"settings-copilot-context-tier\"]"));
            var unsetOption = Assert.Single(contextTier.Options, static option => string.IsNullOrEmpty(option.Value));
            Assert.Equal("SDK 既定 (未設定)", unsetOption.TextContent.Trim());
        });
    }

    [Fact]
    public void Renders_PrewarmOnStartup_Checkbox_With_Boolean_Layout_Class()
    {
        var store = new FakeLocalAppSettingsStore(LocalAppSettings.Default.Clone());
        using var ctx = new BunitContext();

        var cut = ctx.Render<LocalAppSettingsEditor>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(BuildServices(store)));

        cut.WaitForAssertion(() =>
        {
            var checkbox = cut.Find("[data-testid='settings-docsrepo-prewarm-on-startup']");
            var label = checkbox.ParentElement;

            Assert.NotNull(label);
            Assert.Contains("local-settings-check", label.ClassList);
        });
    }

    [Fact]
    public void Save_Writes_Edited_Local_Appsettings()
    {
        var settings = LocalAppSettings.Default.Clone();
        settings.Copilot.AllowedUrlHosts = ["docs.github.com"];
        settings.WebView.AllowedUrlHosts = ["docs.github.com"];
        var store = new FakeLocalAppSettingsStore(settings);
        using var ctx = new BunitContext();
        var cut = ctx.Render<LocalAppSettingsEditor>(
            parameters => parameters.AddCascadingValue<IServiceProvider>(BuildServices(store)));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=\"settings-copilot-model\"]")));

        cut.Find("[data-testid=\"settings-github-owner\"]").Input("contoso");
        cut.Find("[data-testid=\"settings-copilot-model\"]").Input("gpt-5.5");
        cut.Find("[data-testid=\"settings-copilot-context-tier\"]").Change("long_context");
        cut.Find("[data-testid=\"settings-copilot-enable-remote-sessions\"]").Change(true);
        cut.Find("[data-testid=\"settings-copilot-enable-session-telemetry\"]").Change(false);
        cut.Find("[data-testid=\"settings-copilot-allowed-hosts\"]").Input("docs.github.com\napi.github.com");
        cut.Find("[data-testid=\"settings-webview-allowed-hosts\"]").Input("docs.github.com\ngithub.com\ngithub.githubassets.com");
        cut.Find("[data-testid=\"settings-docsrepo-prewarm-on-startup\"]").Change(true);
        cut.Find("[data-testid=\"settings-updates-enabled\"]").Change(true);
        cut.Find("[data-testid=\"settings-updates-feed-url\"]").Input("https://github.com/example/RepoSyncRadar");
        cut.Find("[data-testid=\"settings-updates-channel\"]").Input("win-x64-preview");
        cut.Find("[data-testid=\"settings-local-appsettings-save\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(store.Saved);
            Assert.Equal("contoso", store.Saved.GitHub.Owner);
            Assert.Equal("gpt-5.5", store.Saved.Copilot.DefaultModel);
            Assert.Equal("long_context", store.Saved.Copilot.ContextTier);
            Assert.True(store.Saved.Copilot.EnableRemoteSessions);
            Assert.False(store.Saved.Copilot.EnableSessionTelemetry);
            Assert.Equal(["docs.github.com", "api.github.com"], store.Saved.Copilot.AllowedUrlHosts);
            Assert.Equal(["docs.github.com", "github.com", "github.githubassets.com"], store.Saved.WebView.AllowedUrlHosts);
            Assert.True(store.Saved.DocsRepository.PrewarmOnStartup);
            Assert.True(store.Saved.Updates.Enabled);
            Assert.Equal("https://github.com/example/RepoSyncRadar", store.Saved.Updates.FeedUrl);
            Assert.Equal("win-x64-preview", store.Saved.Updates.Channel);
            Assert.Contains("保存", cut.Find("[data-testid=\"settings-local-appsettings-status\"]").TextContent, StringComparison.Ordinal);
        });
    }

    private static ServiceProvider BuildServices(ILocalAppSettingsStore store)
        => new ServiceCollection()
            .AddSingleton(store)
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .BuildServiceProvider();

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