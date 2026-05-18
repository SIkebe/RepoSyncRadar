using Bunit;
using RepoSyncRadar.App.Components;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class ThirdPartyNoticesPanelTests
{
    [Fact]
    public void Renders_Third_Party_Notices()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ThirdPartyNoticesPanel>();

        Assert.Contains("サードパーティ ライセンス", cut.Markup, StringComparison.Ordinal);
        var packages = cut.FindAll("[data-testid=\"settings-third-party-package\"]")
            .Select(static node => node.TextContent)
            .ToArray();
        Assert.Contains("MudBlazor", packages);
        Assert.Contains("GitHub.Copilot.SDK", packages);
        Assert.Contains("Microsoft.Web.WebView2", packages);
        Assert.Contains("BSD-2-Clause", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MIT", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ライセンス本文", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Copyright 2026 MudBlazor", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Redistribution and use in source and binary forms", cut.Markup, StringComparison.Ordinal);
    }
}