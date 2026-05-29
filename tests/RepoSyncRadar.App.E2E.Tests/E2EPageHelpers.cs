using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

internal static class E2EPageHelpers
{
    public static async Task<IPage> GetBlazorPageAsync(IBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var context = GetFirstContext(browser, "Blazor");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var page in context.Pages)
            {
                if (await HasSelectorAsync(page, "[data-testid='sidebar']").ConfigureAwait(false))
                {
                    return page;
                }
            }

            await Task.Delay(250, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Blazor page not found over CDP. Open pages: {DescribePages(context.Pages)}.");
    }

    private static IBrowserContext GetFirstContext(IBrowser browser, string label)
    {
        var contexts = browser.Contexts;
        if (contexts.Count == 0)
        {
            throw new InvalidOperationException($"{label} browser has no contexts.");
        }

        return contexts[0];
    }

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
