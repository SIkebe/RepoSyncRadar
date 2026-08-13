using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RepoSyncRadar.App.Components;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class ThirdPartyNoticesPanelTests
{
    [Fact]
    public void Renders_Third_Party_Notices()
    {
        using var ctx = new BunitContext();
        var sp = new ServiceCollection()
            .AddLogging()
            .AddLocalization(options => options.ResourcesPath = "Resources")
            .BuildServiceProvider();

        var cut = ctx.Render<ThirdPartyNoticesPanel>(parameters => parameters.AddCascadingValue<IServiceProvider>(sp));

        Assert.Contains("サードパーティ ライセンス", cut.Markup, StringComparison.Ordinal);
        var packages = cut.FindAll("[data-testid=\"settings-third-party-package\"]")
            .Select(static node => node.TextContent)
            .ToArray();
        var versions = cut.FindAll("[data-testid=\"settings-third-party-version\"]")
            .Select(static node => node.TextContent)
            .ToArray();
        Assert.Contains("MudBlazor", packages);
        var sdkIndex = Array.IndexOf(packages, "GitHub.Copilot.SDK");
        Assert.NotEqual(-1, sdkIndex);
        Assert.Equal("1.0.11-preview.2", versions[sdkIndex]);
        Assert.Contains("Microsoft.Web.WebView2", packages);
        Assert.Contains("BSD-2-Clause", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MIT", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ライセンス本文", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Copyright 2026 MudBlazor", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Redistribution and use in source and binary forms", cut.Markup, StringComparison.Ordinal);
    }
}