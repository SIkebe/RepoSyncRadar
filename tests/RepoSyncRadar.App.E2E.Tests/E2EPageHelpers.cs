using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

internal static class E2EPageHelpers
{
    /// <summary>
    /// Budget for the Blazor shell to render its first component tree after the
    /// WebView2 CDP endpoint answered <c>/json/version</c>. A cold CI runner has
    /// to JIT the App, create a fresh WebView2 profile, and run the initial
    /// Blazor Hybrid render, which can take well over half a minute, so the
    /// budget is deliberately generous. Individual assertions keep their own
    /// tighter timeouts, so a genuinely broken UI still fails the test quickly
    /// once the page is found.
    /// </summary>
    private static readonly TimeSpan _blazorReadyTimeout = TimeSpan.FromMinutes(2);

    public static async Task<IPage> GetBlazorPageAsync(IBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var deadline = DateTime.UtcNow + _blazorReadyTimeout;
        IReadOnlyList<IPage> lastSeenPages = [];
        while (DateTime.UtcNow < deadline)
        {
            // Contexts and pages are re-read on every attempt: the Blazor
            // WebView can attach its target to CDP after we connected, so a
            // snapshot taken up front may never contain the shell page.
            lastSeenPages = GetPages(browser);
            foreach (var page in lastSeenPages)
            {
                if (await HasSelectorAsync(page, "[data-testid='sidebar']").ConfigureAwait(false))
                {
                    return page;
                }
            }

            await Task.Delay(250, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Blazor page not found over CDP within {_blazorReadyTimeout.TotalSeconds:F0}s. Open pages: {DescribePages(lastSeenPages)}.");
    }

    private static IReadOnlyList<IPage> GetPages(IBrowser browser)
        => [.. browser.Contexts.SelectMany(static context => context.Pages)];

    private static async Task<bool> HasSelectorAsync(IPage page, string selector)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 5000 }).ConfigureAwait(false);
            return await page.Locator(selector).CountAsync().ConfigureAwait(false) > 0;
        }
        catch (PlaywrightException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string DescribePages(IReadOnlyList<IPage> pages)
        => pages.Count == 0
            ? "(none)"
            : string.Join(", ", pages.Select(static page => string.IsNullOrWhiteSpace(page.Url) ? "(blank)" : page.Url));
}
