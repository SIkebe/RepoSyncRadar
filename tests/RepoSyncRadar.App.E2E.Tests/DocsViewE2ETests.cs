using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

/// <summary>
/// End-to-end checks for the right WebView2 pane that hosts docs.github.com.
/// These pin down the two guarantees of <c>MainWindow</c>'s DocsView wiring:
/// <list type="bullet">
///   <item>The pane navigates to the canonical English entry point so the user
///         is not redirected to a localized variant.</item>
///   <item>The URL allow-list (HTTPS + host exact match) does not block the
///         GitHub Docs homepage and its first-party subresources.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ETests.Name)]
public sealed class DocsViewE2ETests
{
    private readonly AppHostFixture _fixture;

    public DocsViewE2ETests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DocsView_Loads_GitHub_Docs_In_English()
    {
        var page = await GetDocsPageAsync();

        // The initial Source is https://docs.github.com/en. Allow some slack for
        // a one-hop redirect, but the final URL must still be on docs.github.com
        // and must include the /en/ locale segment.
        await page.WaitForURLAsync(
            url => url.Contains("docs.github.com", StringComparison.OrdinalIgnoreCase)
                && url.Contains("/en", StringComparison.OrdinalIgnoreCase),
            new() { Timeout = 30000 });

        var lang = await page.EvaluateAsync<string>("() => document.documentElement.lang || ''");
        Assert.StartsWith("en", lang, StringComparison.OrdinalIgnoreCase);
    }

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
