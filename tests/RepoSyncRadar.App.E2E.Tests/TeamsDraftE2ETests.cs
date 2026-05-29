using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// Regression E2E for legacy Teams draft rows. The app no longer generates or
/// displays Teams-oriented sharing drafts, but old databases may still contain
/// <c>Channel="teams"</c> rows. We seed one and assert that the DraftsPanel:
/// <list type="bullet">
///   <item>does not render a Teams section.</item>
///   <item>does not contain the substring "Slack" anywhere in the Blazor view.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(SeededE2ETests.Name)]
public sealed class LegacyTeamsDraftE2ETests
{
    private readonly SeededAppHostFixture _fixture;

    public LegacyTeamsDraftE2ETests(SeededAppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DraftsPanel_Does_Not_Render_Legacy_Teams_Section()
    {
        var page = await GetBlazorPageAsync();
        await SelectSeededCommitAsync(page);

        await page.Locator("[data-testid='drafts-section-explanation']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        Assert.Equal(0, await page.Locator("[data-testid='drafts-section-teams']").CountAsync());
        Assert.Equal(0, await page.Locator("[data-testid='drafts-body-teams']").CountAsync());

        // The other two channels must also render their seeded bodies so we know
        // the rename did not collide with neighbouring sections.
        Assert.Equal(
            SeededAppHostFixture.SeededTwitterBody,
            (await page.Locator("[data-testid='drafts-body-twitter']").InnerTextAsync()).Trim());
        Assert.Equal(
            SeededAppHostFixture.SeededCustomerBody,
            (await page.Locator("[data-testid='drafts-body-customer']").InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task Blazor_View_Does_Not_Contain_Stale_Slack_Label()
    {
        var page = await GetBlazorPageAsync();
        await SelectSeededCommitAsync(page);

        // Wait for the DraftsPanel to mount so the entire workbench tree is in
        // the DOM before we inspect it.
        await page.Locator("[data-testid='drafts-section-explanation']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var visibleText = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Slack", visibleText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SelectSeededCommitAsync(IPage page)
    {
        var adoptedItem = page.Locator("[data-testid='sidebar-item-Adopted']");
        await adoptedItem.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await adoptedItem.ClickAsync();

        var row = page.Locator($"[data-testid='commit-row'][data-sha='{SeededAppHostFixture.SeededSha}']");
        await row.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await row.ClickAsync();
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
