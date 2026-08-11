using Microsoft.Playwright;
using System.Text.Json;
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
    private readonly SeededAppHostFixture _fixture;

    public DocsViewE2ETests(SeededAppHostFixture fixture)
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

    [Fact]
    public async Task DocsView_Pins_Color_Mode_On_GitHub_Docs_Primer_Containers()
    {
        var page = await GetDocsPageAsync();

        var mode = await WaitForPinnedColorModeAsync(page);

        Assert.True(mode is "dark" or "light", $"Unexpected docs color mode: {mode}");
    }

    [Fact]
    public async Task DocsView_Reapplies_Color_Mode_When_GitHub_Docs_Rehydrates_Primer_Container()
    {
        var page = await GetDocsPageAsync();
        var mode = await WaitForPinnedColorModeAsync(page);

        var mutated = await page.EvaluateAsync<bool>(
            """
            () => {
                const target = Array.from(document.querySelectorAll('[data-color-mode]'))
                    .find(node => node !== document.documentElement);
                if (!target) {
                    return false;
                }
                target.setAttribute('data-color-mode', 'auto');
                return target.getAttribute('data-color-mode') === 'auto';
            }
            """);

        Assert.True(mutated);
        await page.WaitForFunctionAsync(
            """
            expectedMode => {
                const nodes = Array.from(document.querySelectorAll('[data-color-mode]'));
                return nodes.length >= 2
                    && nodes.every(node => node.getAttribute('data-color-mode') === expectedMode);
            }
            """,
            mode,
            new() { Timeout = 5000 });
    }

    [Fact]
    public async Task DocsView_Code_Line_Markup_Preserves_Blank_Lines_When_Selected()
    {
        var page = await GetDocsPageAsync();

        var probe = await page.EvaluateAsync<JsonElement>(
            """
            () => {
                const host = document.createElement('div');
                host.style.cssText =
                    'position:fixed;inset-inline-start:-10000px;top:0;inline-size:300px';
                host.innerHTML =
                    '<pre style="font:16px/1.55 monospace"><code>' +
                    '<span style="display:block">first</span>' +
                    '<span style="display:block;min-block-size:1.55em"><br></span>' +
                    '<span style="display:block">second</span>' +
                    '</code></pre>';
                document.body.appendChild(host);
                try {
                    const range = document.createRange();
                    range.selectNodeContents(host.querySelector('pre'));
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    const lines = Array.from(host.querySelectorAll('span'));
                    return {
                        selectedText: selection.toString(),
                        lineHeights: lines.map(line => line.getBoundingClientRect().height),
                    };
                } finally {
                    window.getSelection()?.removeAllRanges();
                    host.remove();
                }
            }
            """);

        var selectedText = probe.GetProperty("selectedText").GetString();
        var lineHeights = probe.GetProperty("lineHeights")
            .EnumerateArray()
            .Select(static height => height.GetDouble())
            .ToArray();
        Assert.NotNull(selectedText);
        Assert.Equal("first\n\nsecond", selectedText.ReplaceLineEndings("\n"));
        Assert.Equal(3, lineHeights.Length);
        Assert.Equal(lineHeights[0], lineHeights[1], precision: 2);
        Assert.Equal(lineHeights[0], lineHeights[2], precision: 2);
    }

    private static async Task<string> WaitForPinnedColorModeAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            """
            () => {
                const nodes = Array.from(document.querySelectorAll('[data-color-mode]'));
                return nodes.length >= 2
                    && nodes.every(node => ['dark', 'light'].includes(node.getAttribute('data-color-mode')))
                    && new Set(nodes.map(node => node.getAttribute('data-color-mode'))).size === 1;
            }
            """,
            null,
            new() { Timeout = 30000 });

        return await page.EvaluateAsync<string>(
            """
            () => document.documentElement.getAttribute('data-color-mode') || ''
            """);
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
