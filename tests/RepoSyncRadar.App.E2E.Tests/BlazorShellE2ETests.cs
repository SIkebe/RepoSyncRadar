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
    private static readonly string[] _statusKeys =
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
        foreach (var status in _statusKeys)
        {
            Assert.True(
                await page.Locator($"[data-testid='sidebar-item-{status}']").IsVisibleAsync(),
                $"Sidebar item for status '{status}' should be visible");
        }

        // CommitList / CommitDetail run their own async reload separately from
        // Sidebar. This mount test should verify that the list settles, not that
        // the shared app host remains empty for the whole run: a concurrent or
        // inherited Triage can legitimately populate the temp DB before this
        // assertion observes it.
        await WaitForCommitListSettledAsync(page);
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
        foreach (var status in _statusKeys)
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
        => await E2EPageHelpers.GetBlazorPageAsync(_fixture.BlazorBrowser).ConfigureAwait(false);

    private static async Task WaitForCommitListSettledAsync(IPage page)
    {
        await page.Locator("[data-testid='commit-list']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await page.WaitForFunctionAsync(
            """
            () => {
                const root = document.querySelector('[data-testid="commit-list"]');
                if (!root) {
                    return false;
                }
                return !root.querySelector('[data-testid="commit-list-loading"]')
                    && (root.querySelector('[data-testid="commit-list-empty"]')
                        || root.querySelector('[data-testid="commit-row"]'));
            }
            """,
            null,
            new() { Timeout = 15000 });
    }
}
