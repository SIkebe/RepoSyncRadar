using Microsoft.Playwright;
using RepoSyncRadar.Core.Services.Preview;
using System.Reflection;
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

    [Fact]
    public async Task Preview_Diff_Extractor_Includes_Source_Metadata_But_Excludes_Other_Header_Chrome()
    {
        var appAssemblyPath = Path.ChangeExtension(AppHost.ResolveAppExePath(), ".dll");
        var appAssembly = Assembly.LoadFrom(appAssemblyPath);
        var highlighterType = appAssembly.GetType(
            "RepoSyncRadar.App.PreviewDiffHighlighter",
            throwOnError: true)!;
        var script = Assert.IsType<string>(
            highlighterType
                .GetProperty(
                    "ExtractBlocksScriptForTests",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null));
        var context = Assert.Single(_fixture.DocsBrowser.Contexts);
        var page = await context.NewPageAsync();
        try
        {
            await page.SetContentAsync(
                """
                <main>
                  <article>
                    <header>
                      <p>Ordinary preview header chrome</p>
                      <section class="rsr-source-diff">
                        <h2>Rendered source metadata</h2>
                        <p>Hidden Liquid condition details</p>
                      </section>
                    </header>
                    <p>Rendered article body</p>
                  </article>
                </main>
                """);

            var result = await page.EvaluateAsync<JsonElement>(script);
            var extractedText = result.EnumerateArray()
                .Select(static block => block.GetProperty("text").GetString())
                .ToArray();

            Assert.Contains("Rendered source metadata", extractedText);
            Assert.Contains("Hidden Liquid condition details", extractedText);
            Assert.Contains("Rendered article body", extractedText);
            Assert.DoesNotContain("Ordinary preview header chrome", extractedText);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Preview_Diff_Overlay_Unions_Targets_Clipped_By_Their_Own_Scroll_Containers()
    {
        var appAssemblyPath = Path.ChangeExtension(AppHost.ResolveAppExePath(), ".dll");
        var appAssembly = Assembly.LoadFrom(appAssemblyPath);
        var highlighterType = appAssembly.GetType(
            "RepoSyncRadar.App.PreviewDiffHighlighter",
            throwOnError: true)!;
        var script = Assert.IsType<string>(
            highlighterType
                .GetMethod(
                    "BuildNavigateToDiffScript",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [0]));
        var context = Assert.Single(_fixture.DocsBrowser.Contexts);
        var page = await context.NewPageAsync();
        try
        {
            await page.SetViewportSizeAsync(800, 600);
            await page.SetContentAsync(
                """
                <main>
                  <article>
                    <p id="outside"
                       data-rsr-diff-navigation-index="0"
                       style="margin: 20px; width: 200px">Outside target</p>
                    <div id="scroller"
                         style="border-left: 4px solid; border-right: 6px solid; margin-left: 100px; overflow-x: auto; width: 320px">
                      <div style="width: 700px">
                        <p data-rsr-diff-navigation-index="0"
                           style="margin-left: 40px; width: 500px">Nested target</p>
                      </div>
                    </div>
                  </article>
                </main>
                """);

            var navigationResult = await page.EvaluateAsync<JsonElement>(script);
            var geometry = await page.EvaluateAsync<JsonElement>(
                """
                () => {
                    const overlay = document.getElementById('rsr-preview-diff-active-overlay')
                        .getBoundingClientRect();
                    const outside = document.getElementById('outside').getBoundingClientRect();
                    const scroller = document.getElementById('scroller');
                    const scrollerRect = scroller.getBoundingClientRect();
                    const scrollerStyle = getComputedStyle(scroller);
                    const borderRight = Number.parseFloat(scrollerStyle.borderRightWidth) || 0;
                    return {
                        actualLeft: overlay.left,
                        actualRight: overlay.right,
                        expectedLeft: outside.left - 6,
                        expectedRight: scrollerRect.right - borderRight,
                    };
                }
                """);

            Assert.True(navigationResult.GetProperty("found").GetBoolean());
            Assert.Equal(
                geometry.GetProperty("expectedLeft").GetDouble(),
                geometry.GetProperty("actualLeft").GetDouble(),
                precision: 1);
            Assert.Equal(
                geometry.GetProperty("expectedRight").GetDouble(),
                geometry.GetProperty("actualRight").GetDouble(),
                precision: 1);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Preview_Diff_Scrollbar_Splits_Separated_Targets_In_One_Hunk()
    {
        var script = GetHighlighterScript(
            "BuildApplyPlanScript",
            "[0,1,2]",
            "\"after\"",
            """[{"index":0,"navigationIndex":0},{"index":1,"navigationIndex":0},{"index":2,"navigationIndex":0}]""");
        var page = await CreateDocsPageAsync();
        try
        {
            await page.SetViewportSizeAsync(800, 600);
            await page.SetContentAsync(
                """
                <main style="height: 1600px">
                  <div data-rsr-diff-index="0" style="height: 20px; width: 200px">First</div>
                  <div data-rsr-diff-index="1" style="height: 20px; width: 200px">Adjacent</div>
                  <div style="height: 300px"></div>
                  <div data-rsr-diff-index="2" style="height: 20px; width: 200px">Separated</div>
                </main>
                """);

            await page.EvaluateAsync<int>(script);
            var geometry = await page.EvaluateAsync<JsonElement>(
                """
                () => {
                    const markers = Array.from(
                        document.querySelectorAll('.rsr-preview-diff-scrollbar-marker'));
                    const root = document.scrollingElement || document.documentElement;
                    const viewport = window.innerHeight;
                    const groups = [
                        [
                            document.querySelector('[data-rsr-diff-index="0"]'),
                            document.querySelector('[data-rsr-diff-index="1"]'),
                        ],
                        [document.querySelector('[data-rsr-diff-index="2"]')],
                    ];
                    const expected = groups.map(elements => {
                        const rects = elements.map(element => element.getBoundingClientRect());
                        const top = Math.min(...rects.map(rect => rect.top)) + window.scrollY;
                        const bottom = Math.max(...rects.map(rect => rect.bottom)) + window.scrollY;
                        const height = Math.max(
                            4,
                            Math.min(viewport, ((bottom - top) / root.scrollHeight) * viewport));
                        return {
                            top: Math.max(
                                0,
                                Math.min(viewport - height, (top / root.scrollHeight) * viewport)),
                            height,
                        };
                    });
                    return {
                        markerCount: markers.length,
                        actual: markers.map(marker => {
                            const rect = marker.getBoundingClientRect();
                            return { top: rect.top, height: rect.height };
                        }),
                        expected,
                    };
                }
                """);

            Assert.Equal(2, geometry.GetProperty("markerCount").GetInt32());
            var actual = geometry.GetProperty("actual").EnumerateArray().ToArray();
            var expected = geometry.GetProperty("expected").EnumerateArray().ToArray();
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(
                    expected[index].GetProperty("top").GetDouble(),
                    actual[index].GetProperty("top").GetDouble(),
                    precision: 1);
                Assert.Equal(
                    expected[index].GetProperty("height").GetDouble(),
                    actual[index].GetProperty("height").GetDouble(),
                    precision: 1);
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [InlineData("rendered")]
    [InlineData("fallback")]
    [InlineData("one-sided")]
    public async Task Preview_Diff_Navigation_Centers_The_Visible_Change_Target(string scenario)
    {
        var script = GetHighlighterScript("BuildNavigateToDiffScript", 0);
        var markup = scenario switch
        {
            "rendered" => """
                <div data-rsr-diff-navigation-index="0"
                     style="height: 600px; position: absolute; top: 300px; width: 500px">
                  <span id="expected" class="rsr-rendered-diff-added"
                        style="display: block; height: 40px; position: absolute; top: 240px; width: 180px">Changed</span>
                </div>
                <div class="rsr-preview-diff-alignment-gap" data-rsr-diff-navigation-index="0"
                     style="height: 100px; position: absolute; top: 1100px; width: 500px"></div>
                """,
            "fallback" => """
                <div id="expected" class="rsr-preview-diff-block"
                     data-rsr-diff-navigation-index="0"
                     style="height: 40px; position: absolute; top: 700px; width: 500px">Changed</div>
                <div class="rsr-preview-diff-alignment-gap" data-rsr-diff-navigation-index="0"
                     style="height: 100px; position: absolute; top: 1100px; width: 500px"></div>
                """,
            "one-sided" => """
                <div data-rsr-diff-navigation-index="0"
                     style="height: 700px; position: absolute; top: 200px; width: 500px">Unchanged container</div>
                <div id="expected" class="rsr-preview-diff-alignment-gap"
                     data-rsr-diff-navigation-index="0"
                     style="height: 100px; position: absolute; top: 1100px; width: 500px"></div>
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var page = await CreateDocsPageAsync();
        try
        {
            await page.SetViewportSizeAsync(800, 400);
            await page.SetContentAsync(
                $"""
                <main style="height: 1800px; position: relative">
                  {markup}
                </main>
                """);

            var navigationResult = await page.EvaluateAsync<JsonElement>(script);
            var geometry = await page.EvaluateAsync<JsonElement>(
                """
                () => {
                    const expected = document.getElementById('expected').getBoundingClientRect();
                    const overlay = document.getElementById(
                        'rsr-preview-diff-active-overlay').getBoundingClientRect();
                    return {
                        expectedTop: expected.top,
                        expectedHeight: expected.height,
                        actualTop: overlay.top,
                        actualHeight: overlay.height,
                        viewportCenter: window.innerHeight / 2,
                        targetCenter: expected.top + expected.height / 2,
                    };
                }
                """);

            Assert.True(navigationResult.GetProperty("found").GetBoolean());
            Assert.Equal(
                geometry.GetProperty("expectedTop").GetDouble(),
                geometry.GetProperty("actualTop").GetDouble(),
                precision: 1);
            Assert.Equal(
                geometry.GetProperty("expectedHeight").GetDouble(),
                geometry.GetProperty("actualHeight").GetDouble(),
                precision: 1);
            Assert.InRange(
                Math.Abs(
                    geometry.GetProperty("viewportCenter").GetDouble() -
                    geometry.GetProperty("targetCenter").GetDouble()),
                0,
                0.5);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Rendered_Diff_Scrollbar_Uses_Changed_Cell_And_Point_Gap_Bounds()
    {
        const string beforeMarkdown = """
            | Model | Status |
            | --- | --- |
            | Alpha | Old |
            | Beta | Stable |
            """;
        const string afterMarkdown = """
            | Model | Status |
            | --- | --- |
            | Alpha | New |
            | Beta | Stable |
            """;
        var html = MarkdownPreviewRenderer.RenderDocument(
            "content/sample.md",
            afterMarkdown,
            "abc1234",
            "PR HEAD",
            diffAgainstMarkdown: beforeMarkdown,
            diffSide: MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);
        var page = await CreateDocsPageAsync();
        try
        {
            await page.SetViewportSizeAsync(800, 600);
            await page.SetContentAsync(html);
            var geometry = await page.EvaluateAsync<JsonElement>(
                """
                async () => {
                    const changed = document.querySelector('.rsr-rendered-diff-added');
                    const cell = changed.closest('td');
                    const table = changed.closest('table');
                    table.style.height = '600px';
                    table.setAttribute('data-rsr-diff-navigation-index', '0');
                    const point = document.createElement('div');
                    point.className = 'rsr-preview-diff-alignment-gap';
                    point.setAttribute('data-rsr-diff-navigation-index', '1');
                    point.style.height = '240px';
                    document.querySelector('article').appendChild(point);
                    window.__repoSyncRadarDiffScrollbar.scheduleBuild();
                    await new Promise(resolve => requestAnimationFrame(
                        () => requestAnimationFrame(resolve)));
                    const markers = Array.from(
                        document.querySelectorAll('.rsr-diff-scrollbar-marker'));
                    const cellRect = cell.getBoundingClientRect();
                    const tableRect = table.getBoundingClientRect();
                    const markerRect = markers[0].getBoundingClientRect();
                    const docHeight = Math.max(1, document.documentElement.scrollHeight);
                    const viewport = window.innerHeight;
                    const scrollbarSize = Math.max(
                        0,
                        window.innerWidth - document.documentElement.clientWidth);
                    const buttonSize = Math.min(scrollbarSize, viewport / 4);
                    const trackHeight = Math.max(1, viewport - buttonSize * 2);
                    const expectedCenter =
                        buttonSize +
                        ((cellRect.top + cellRect.bottom) / 2 / docHeight) * trackHeight;
                    return {
                        markerCount: markers.length,
                        cellMarkerCenter: markerRect.top + markerRect.height / 2,
                        expectedCellCenter: expectedCenter,
                        cellMarkerHeight: markerRect.height,
                        projectedTableHeight: (tableRect.height / docHeight) * trackHeight,
                        pointMarkerHeight: markers.at(-1).getBoundingClientRect().height,
                    };
                }
                """);

            Assert.Equal(2, geometry.GetProperty("markerCount").GetInt32());
            Assert.InRange(
                Math.Abs(
                    geometry.GetProperty("expectedCellCenter").GetDouble() -
                    geometry.GetProperty("cellMarkerCenter").GetDouble()),
                0,
                0.5);
            Assert.True(
                geometry.GetProperty("cellMarkerHeight").GetDouble()
                < geometry.GetProperty("projectedTableHeight").GetDouble());
            Assert.Equal(6, geometry.GetProperty("pointMarkerHeight").GetDouble());
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static string GetHighlighterScript(string methodName, params object[] arguments)
    {
        var appAssemblyPath = Path.ChangeExtension(AppHost.ResolveAppExePath(), ".dll");
        var appAssembly = Assembly.LoadFrom(appAssemblyPath);
        var highlighterType = appAssembly.GetType(
            "RepoSyncRadar.App.PreviewDiffHighlighter",
            throwOnError: true)!;
        return Assert.IsType<string>(
            highlighterType
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, arguments));
    }

    private async Task<IPage> CreateDocsPageAsync()
    {
        var context = Assert.Single(_fixture.DocsBrowser.Contexts);
        return await context.NewPageAsync();
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
