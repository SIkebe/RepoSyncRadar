using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// Regression E2E for the channel naming fix (Slack → Teams). The bug this
/// guards against is the one observed in commit 0946a44: the Adoption session
/// persisted Drafts with <c>Channel="teams"</c>, but stale UI / prompt code
/// still referred to the channel as "Slack", confusing users. We seed a Draft
/// row with <c>Channel="teams"</c> and assert that the DraftsPanel:
/// <list type="bullet">
///   <item>shows the seeded Teams body verbatim in <c>drafts-body-teams</c>.</item>
///   <item>uses the label "Teams" — not "Slack" — in the section header.</item>
///   <item>does not contain the substring "Slack" anywhere in the Blazor view.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(SeededE2ETests.Name)]
public sealed class TeamsDraftE2ETests
{
    private readonly SeededAppHostFixture _fixture;

    public TeamsDraftE2ETests(SeededAppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DraftsPanel_Renders_Teams_Section_With_Seeded_Body()
    {
        var page = await GetBlazorPageAsync();

        var row = page.Locator($"[data-testid='commit-row'][data-sha='{SeededAppHostFixture.SeededSha}']");
        await row.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await row.ClickAsync();

        var section = page.Locator("[data-testid='drafts-section-teams']");
        await section.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        // Header label must be "Teams" — not "Slack".
        var header = (await section.Locator("header").InnerTextAsync()).Trim();
        Assert.Contains("Teams", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Slack", header, StringComparison.OrdinalIgnoreCase);

        // Body text matches the seeded Draft row verbatim.
        var body = (await page.Locator("[data-testid='drafts-body-teams']").InnerTextAsync()).Trim();
        Assert.Equal(SeededAppHostFixture.SeededTeamsBody.Trim(), body);

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

        var row = page.Locator($"[data-testid='commit-row'][data-sha='{SeededAppHostFixture.SeededSha}']");
        await row.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await row.ClickAsync();

        // Wait for the DraftsPanel to mount so the entire workbench tree is in
        // the DOM before we inspect it.
        await page.Locator("[data-testid='drafts-section-teams']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var visibleText = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Slack", visibleText, StringComparison.OrdinalIgnoreCase);
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
