using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// End-to-end checks for the "比較プレビュー" wiring added in
/// IMPLEMENTATION_PLAN.md §Step 19.5. The pipeline itself (git clone --bare,
/// worktree, sidecar) is exercised by Core / Integrations tests with a stubbed
/// <c>IProcessRunner</c>; running the real tool chain inside an E2E pass would
/// take minutes and require <c>git</c> + <c>npm</c> on the test host. Here we
/// pin down the UI surface that wraps the pipeline:
/// <list type="bullet">
///   <item>The file-row preview button and cleanup button render and become
///         interactive once a commit is selected.</item>
///   <item>Clicking a file-row preview button with the preview pipeline disabled
///         surfaces the inline disabled message instead of crashing, and the
///         button returns to its enabled state for retries.</item>
///   <item>Clicking "キャッシュをクリーンアップ" reports 0 removed when the
///         pipeline is disabled.</item>
///   <item>The right pane (DocsView WebView2) is not navigated away from
///         <c>docs.github.com</c> when no preview link was produced.</item>
/// </list>
/// </summary>
/// <remarks>
/// The seeded fixture intentionally clears <c>DocsRepository</c> via
/// <c>RADAR_*</c> environment variables (see
/// <see cref="SeededAppHostFixture"/>), so the click path is guaranteed to take
/// the disabled branch in <c>PreviewCoordinator.PreparePreviewAsync</c>. This
/// avoids accidentally invoking <c>git</c> on a developer machine that already
/// has a real bare clone in <c>appsettings.Local.json</c>.
/// </remarks>
[Trait("Category", "E2E")]
[Collection(SeededE2ETests.Name)]
public sealed class PreviewE2ETests
{
    private const string _previewDisabledStatus =
        "プレビュー機能は無効です (DocsRepository 未設定)";

    private readonly SeededAppHostFixture _fixture;

    public PreviewE2ETests(SeededAppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Preview_And_Cleanup_Buttons_Are_Enabled_After_Commit_Selection()
    {
        var page = await GetBlazorPageAsync();

        await SelectSeededCommitAsync(page);

        var previewButton = page.Locator("[data-testid='commit-detail-open-in-webview']").First;
        await previewButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await Assertions.Expect(previewButton).ToBeEnabledAsync(
            new() { Timeout = 5000 });
        Assert.Equal("WebView2 で開く", (await previewButton.InnerTextAsync()).Trim());

        var cleanupButton = page.Locator("[data-testid='commit-detail-preview-cleanup-button']");
        await cleanupButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await Assertions.Expect(cleanupButton).ToBeEnabledAsync(
            new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Clicking_Preview_Reports_Disabled_When_DocsRepository_Is_Empty()
    {
        var page = await GetBlazorPageAsync();
        await SelectSeededCommitAsync(page);

        var previewButton = page.Locator("[data-testid='commit-detail-open-in-webview']").First;
        await previewButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await previewButton.ClickAsync();

        // The coordinator short-circuits to null when DocsRepository is empty so
        // the busy state collapses back to idle almost immediately. Wait for the
        // status paragraph to settle on the disabled message rather than racing
        // the intermediate "リポジトリを準備中…" string.
        var status = page.Locator("[data-testid='commit-detail-preview-status']");
        await status.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await Assertions.Expect(status).ToHaveTextAsync(
            _previewDisabledStatus,
            new() { Timeout = 10000 });

        // The button is busy while the click handler runs; once we see the
        // disabled status it must become clickable again so the user can retry.
        await Assertions.Expect(previewButton).ToBeEnabledAsync(
            new() { Timeout = 5000 });
    }

    [Fact]
    public async Task Clicking_Cleanup_Reports_Zero_Removed_When_Pipeline_Disabled()
    {
        var page = await GetBlazorPageAsync();
        await SelectSeededCommitAsync(page);

        var cleanupButton = page.Locator("[data-testid='commit-detail-preview-cleanup-button']");
        await cleanupButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await cleanupButton.ClickAsync();

        // PreviewCoordinator.CleanupCacheAsync returns 0 when DocsWorktreeManager
        // is disabled, and the UI maps that to the "{n} 件の worktree を削除しました"
        // string. We assert the leading "0 件" substring so we are not coupled to
        // future tweaks of the status wording.
        var status = page.Locator("[data-testid='commit-detail-preview-cleanup-status']");
        await status.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await Assertions.Expect(status).ToContainTextAsync(
            "0 件の worktree",
            new() { Timeout = 10000 });

        await Assertions.Expect(cleanupButton).ToBeEnabledAsync(
            new() { Timeout = 5000 });
    }

    [Fact]
    public async Task DocsView_Stays_On_GitHub_Docs_When_Preview_Pipeline_Is_Disabled()
    {
        var blazorPage = await GetBlazorPageAsync();
        await SelectSeededCommitAsync(blazorPage);

        // Capture the docs pane URL before the click so we can assert it does not
        // get rewritten to a localhost preview link (which only the enabled path
        // would publish through IPreviewNavigator).
        var docsPage = await GetDocsPageAsync();
        var beforeUrl = docsPage.Url;

        var previewButton = blazorPage.Locator("[data-testid='commit-detail-open-in-webview']").First;
        await previewButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await previewButton.ClickAsync();

        // Wait for the disabled status to land so we know the click has fully
        // round-tripped through the coordinator.
        var status = blazorPage.Locator("[data-testid='commit-detail-preview-status']");
        await Assertions.Expect(status).ToHaveTextAsync(
            _previewDisabledStatus,
            new() { Timeout = 10000 });

        // DocsView must still be on docs.github.com (never navigated to a
        // http://localhost:PORT/... preview).
        Assert.Contains(
            "docs.github.com",
            docsPage.Url,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "localhost",
            docsPage.Url,
            StringComparison.OrdinalIgnoreCase);

        // Best-effort sanity check: the URL we captured up-front did not change.
        // Tolerate trailing redirects on docs.github.com (locale rewrite) by
        // comparing only the host.
        var beforeHost = new Uri(beforeUrl).Host;
        var afterHost = new Uri(docsPage.Url).Host;
        Assert.Equal(beforeHost, afterHost);
    }

    private static async Task SelectSeededCommitAsync(IPage page)
    {
        var adoptedItem = page.Locator("[data-testid='sidebar-item-Adopted']");
        await adoptedItem.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await adoptedItem.ClickAsync();

        var row = page.Locator($"[data-testid='commit-row'][data-sha='{SeededAppHostFixture.SeededSha}']");
        await row.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await row.ClickAsync();

        // Ensure CommitDetail bound to the row before any caller starts probing
        // preview controls.
        await page.Locator("[data-testid='commit-detail-preview-toolbar']")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
    }

    private async Task<IPage> GetBlazorPageAsync()
        => await E2EPageHelpers.GetBlazorPageAsync(_fixture.BlazorBrowser).ConfigureAwait(false);

    private async Task<IPage> GetDocsPageAsync()
    {
        var contexts = _fixture.DocsBrowser.Contexts;
        if (contexts.Count == 0)
        {
            throw new InvalidOperationException("Docs browser has no contexts.");
        }

        var page = await FindDocsPageAsync(contexts[0]);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    private static async Task<IPage> FindDocsPageAsync(IBrowserContext context)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var page in context.Pages)
            {
                if (page.Url.Contains("docs.github.com", StringComparison.OrdinalIgnoreCase))
                {
                    return page;
                }
            }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("Docs page not found over CDP.");
    }
}
