using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// Regression E2E for the Morning Triage Scoring panel. The bug this guards
/// against is the one observed during the implementation audit: <c>radar_score_commit</c>
/// happily persisted Score / Category / Audience / Summary / Why rows, but the
/// CommitDetail Razor component never read them so the user could not see any
/// triage output. We seed a Scoring row before launching the App and assert the
/// fields appear in the Blazor view once the user clicks the seeded commit.
/// </summary>
[Trait("Category", "E2E")]
[Collection(SeededE2ETests.Name)]
public sealed class ScoringDisplayE2ETests
{
    private readonly SeededAppHostFixture _fixture;

    public ScoringDisplayE2ETests(SeededAppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Scoring_Fields_Render_For_Seeded_Commit()
    {
        var page = await GetBlazorPageAsync();

        // The seeded commit must appear in the inbox; click it to drive
        // CommitDetail.
        var row = page.Locator($"[data-testid='commit-row'][data-sha='{SeededAppHostFixture.SeededSha}']");
        await row.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await row.ClickAsync();

        // Score formatted to two decimals.
        var score = page.Locator("[data-testid='commit-detail-score']");
        await score.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var scoreText = (await score.InnerTextAsync()).Trim();
        Assert.Contains(
            SeededAppHostFixture.SeededScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            scoreText,
            StringComparison.Ordinal);

        // Category, summary, why must render verbatim.
        Assert.Equal(
            SeededAppHostFixture.SeededCategory,
            (await page.Locator("[data-testid='commit-detail-category']").InnerTextAsync()).Trim());
        Assert.Equal(
            SeededAppHostFixture.SeededSummaryJa,
            (await page.Locator("[data-testid='commit-detail-summary']").InnerTextAsync()).Trim());
        Assert.Equal(
            SeededAppHostFixture.SeededWhyJa,
            (await page.Locator("[data-testid='commit-detail-why']").InnerTextAsync()).Trim());

        // Audience JSON contains both tags; the UI joins them so we only assert
        // membership to stay tolerant of the chosen separator.
        var audienceText = (await page.Locator("[data-testid='commit-detail-audience']").InnerTextAsync()).Trim();
        Assert.Contains("devrel", audienceText, StringComparison.Ordinal);
        Assert.Contains("customer", audienceText, StringComparison.Ordinal);

        // The "未スコアリング" hint must not appear once Scoring is bound.
        Assert.Empty(await page.Locator("[data-testid='commit-detail-unscored']").AllAsync());
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
