using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// End-to-end checks for the left BlazorWebView. These guard against the exact
/// regressions seen during Step 10 manual smoke:
/// <list type="bullet">
///   <item>Blazor never mounts and the host HTML's "Loading…" text stays visible.</item>
///   <item>Sidebar buttons render inline instead of stacked because of missing CSS.</item>
///   <item>The queue heading is clipped at the left edge because the aside has no
///         horizontal padding.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ETests.Name)]
public sealed class BlazorShellE2ETests
{
    private static readonly string[] StatusKeys =
        ["Unseen", "Adopted", "Later", "Rejected", "Archived"];

    private readonly AppHostFixture _fixture;

    public BlazorShellE2ETests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BlazorShell_Mounts_With_Sidebar_And_CommitList()
    {
        var page = await GetBlazorPageAsync();

        var sidebar = page.Locator("[data-testid='sidebar']");
        await sidebar.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // All user-facing status counters must render. If only one renders the Razor
        // components mounted partially and we want a clear failure here.
        foreach (var status in StatusKeys)
        {
            Assert.True(
                await page.Locator($"[data-testid='sidebar-item-{status}']").IsVisibleAsync(),
                $"Sidebar item for status '{status}' should be visible");
        }

        // The empty-state placeholders are part of the contract when the DB is
        // empty; their absence would also indicate a broken render path. Wait for
        // them — CommitList / CommitDetail run their own async reload separately
        // from Sidebar, so the placeholders can land a moment after sidebar mounts.
        await page.Locator("[data-testid='commit-list-empty']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await page.Locator("[data-testid='commit-detail-empty']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
    }

    [Fact]
    public async Task BlazorShell_HostPage_Loading_Placeholder_Is_Replaced()
    {
        var page = await GetBlazorPageAsync();

        // Wait for Razor to mount.
        await page.Locator("[data-testid='sidebar']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // The literal index.html bootstrap text must be gone once Razor renders.
        var appText = await page.Locator("#app").InnerTextAsync();
        Assert.DoesNotContain("Loading…", appText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SidebarItems_Are_Stacked_Vertically_Not_Inline()
    {
        var page = await GetBlazorPageAsync();
        await page.Locator("[data-testid='sidebar']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

        var ys = new List<double>();
        foreach (var status in StatusKeys)
        {
            var box = await page.Locator($"[data-testid='sidebar-item-{status}']").BoundingBoxAsync();
            Assert.NotNull(box);
            ys.Add(box!.Y);
        }

        // If buttons are inline-block (the bug we hit), every item shares a Y.
        // Stacked items must monotonically increase in Y.
        for (var i = 1; i < ys.Count; i++)
        {
            Assert.True(
                ys[i] > ys[i - 1] + 1.0,
                $"Sidebar items appear to be on the same row (Y[{i - 1}]={ys[i - 1]}, Y[{i}]={ys[i]}). " +
                "This usually means the .sidebar-item CSS rule is missing.");
        }
    }

    [Fact]
    public async Task Sidebar_Pane_Has_Horizontal_Padding_So_Headings_Are_Not_Clipped()
    {
        var page = await GetBlazorPageAsync();
        await page.Locator("[data-testid='sidebar']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // The aside wraps the queue heading + sidebar. Without padding it sits
        // flush against x=0 and the leading character gets visually clipped.
        var paddingLeftPx = await page
            .Locator(".radar-sidebar-pane")
            .EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).paddingLeft) || 0");

        Assert.True(
            paddingLeftPx >= 4.0,
            $"Sidebar pane should have ≥4px left padding; got {paddingLeftPx}px.");
    }

    private async Task<IPage> GetBlazorPageAsync()
    {
        var contexts = _fixture.BlazorBrowser.Contexts;
        if (contexts.Count == 0)
        {
            throw new InvalidOperationException("Blazor browser has no contexts.");
        }

        var context = contexts[0];
        var pages = context.Pages;
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("Blazor page not found over CDP.");
        }

        // BlazorWebView serves the HostPage from a virtual host. The Docs CDP
        // endpoint is a separate browser process, so anything here is the
        // Blazor view by definition; we still filter defensively to avoid
        // selecting docs.github.com if CDP wiring ever changes.
        IPage page = pages[0];
        foreach (var candidate in pages)
        {
            if (!candidate.Url.Contains("docs.github.com", StringComparison.OrdinalIgnoreCase))
            {
                page = candidate;
                break;
            }
        }

        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }
}
