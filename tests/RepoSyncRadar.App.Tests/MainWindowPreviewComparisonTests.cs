using System.Windows;
using System.Windows.Input;
using RepoSyncRadar.App;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.App.Tests;

public sealed class MainWindowPreviewComparisonTests
{
    private static readonly int[] IndexOne = [1];

    [Fact]
    public void IsLocalPreviewUri_Returns_True_For_Loopback_Http()
    {
        Assert.True(MainWindow.IsLocalPreviewUri(new Uri("http://localhost:4500/en/copilot/about-copilot")));
        Assert.True(MainWindow.IsLocalPreviewUri(new Uri("http://127.0.0.1:4500/en/copilot/about-copilot")));
    }

    [Fact]
    public void IsLocalPreviewUri_Returns_False_For_Official_Docs()
    {
        Assert.False(MainWindow.IsLocalPreviewUri(new Uri("https://docs.github.com/en/copilot/about-copilot")));
    }

    [Fact]
    public void BuildOfficialComparisonUri_Maps_Localhost_Path_To_Docs_GitHub_Com()
    {
        var result = MainWindow.BuildOfficialComparisonUri(
            new Uri("http://localhost:4500/en/copilot/about-copilot?foo=bar"));

        Assert.Equal("https://docs.github.com/en/copilot/about-copilot?foo=bar", result.AbsoluteUri);
    }

    [Fact]
    public void BuildOfficialComparisonUri_Maps_Localhost_Root_To_English_Docs_Home()
    {
        var result = MainWindow.BuildOfficialComparisonUri(new Uri("http://localhost:4500/"));

        Assert.Equal("https://docs.github.com/en", result.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://github.com/github/docs/pull/123", "GitHub PR")]
    [InlineData("https://docs.github.com/en/copilot", "公式 docs.github.com")]
    [InlineData("https://example.com/page", "example.com")]
    public void BuildSinglePageHeaderLabel_Describes_Navigation_Target(string url, string expected)
    {
        Assert.Equal(expected, MainWindow.BuildSinglePageHeaderLabel(new Uri(url)));
    }

    [Fact]
    public void BuildInstallMouseHistoryNavigationScript_Posts_Back_And_Forward_Messages()
    {
        var script = MainWindow.BuildInstallMouseHistoryNavigationScript();

        Assert.Contains("event.button === 3", script, StringComparison.Ordinal);
        Assert.Contains("event.button === 4", script, StringComparison.Ordinal);
        Assert.Contains("rsr-webview-history:${direction}", script, StringComparison.Ordinal);
        Assert.Contains("preventDefault", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rsr-webview-history:back", "Back")]
    [InlineData("rsr-webview-history:forward", "Forward")]
    [InlineData("rsr-webview-history:BACK", "Back")]
    public void TryParseWebViewHistoryNavigationMessage_Parses_Direction(
        string message,
        string expected)
    {
        Assert.True(MainWindow.TryParseWebViewHistoryNavigationMessage(message, out var direction));
        Assert.Equal(expected, direction.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rsr-webview-history:")]
    [InlineData("rsr-webview-history:next")]
    [InlineData("rsr-preview-scroll:before:0.25")]
    public void TryParseWebViewHistoryNavigationMessage_Rejects_Invalid_Messages(string? message)
    {
        Assert.False(MainWindow.TryParseWebViewHistoryNavigationMessage(message, out _));
    }

    [Theory]
    [InlineData(MouseButton.XButton1, "Back")]
    [InlineData(MouseButton.XButton2, "Forward")]
    public void TryResolveMouseHistoryNavigationButton_Maps_XButtons(MouseButton button, string expected)
    {
        Assert.True(MainWindow.TryResolveMouseHistoryNavigationButton(button, out var direction));
        Assert.Equal(expected, direction.ToString());
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void TryResolveMouseHistoryNavigationButton_Rejects_Ordinary_Buttons(MouseButton button)
    {
        Assert.False(MainWindow.TryResolveMouseHistoryNavigationButton(button, out _));
    }

    [Theory]
    [InlineData(0x020B, 0x00010000, 0, "Back")]
    [InlineData(0x020B, 0x00020000, 0, "Forward")]
    [InlineData(0x0319, 0, 0x00010000, "Back")]
    [InlineData(0x0319, 0, 0x00020000, "Forward")]
    public void TryParseNativeMouseHistoryNavigationMessage_Maps_Windows_Messages(
        int message,
        long wParam,
        long lParam,
        string expected)
    {
        Assert.True(MainWindow.TryParseNativeMouseHistoryNavigationMessage(message, new IntPtr(wParam), new IntPtr(lParam), out var direction));
        Assert.Equal(expected, direction.ToString());
    }

    [Theory]
    [InlineData(0x020B, 0x00030000, 0)]
    [InlineData(0x0319, 0, 0x00030000)]
    [InlineData(0x020A, 0x00010000, 0)]
    public void TryParseNativeMouseHistoryNavigationMessage_Rejects_Other_Messages(
        int message,
        long wParam,
        long lParam)
    {
        Assert.False(MainWindow.TryParseNativeMouseHistoryNavigationMessage(message, new IntPtr(wParam), new IntPtr(lParam), out _));
    }

    [Theory]
    [InlineData(0x0040, "Back")]
    [InlineData(0x0100, "Forward")]
    public void TryResolveRawMouseButtonFlags_Maps_XButton_Down(ushort buttonFlags, string expected)
    {
        Assert.True(MainWindow.TryResolveRawMouseButtonFlags(buttonFlags, out var direction));
        Assert.Equal(expected, direction.ToString());
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0080)]
    [InlineData(0x0200)]
    public void TryResolveRawMouseButtonFlags_Rejects_Other_Button_Flags(ushort buttonFlags)
    {
        Assert.False(MainWindow.TryResolveRawMouseButtonFlags(buttonFlags, out _));
    }

    [Fact]
    public void BuildOfficialDocsUri_Maps_Content_Markdown_Path_To_Public_Docs_Url()
    {
        var result = MainWindow.BuildOfficialDocsUri("content/copilot/about-copilot.md");

        Assert.NotNull(result);
        Assert.Equal("https://docs.github.com/en/copilot/about-copilot", result!.AbsoluteUri);
    }

    [Fact]
    public void BuildOfficialDocsUri_Maps_Content_Index_Markdown_To_English_Home()
    {
        var result = MainWindow.BuildOfficialDocsUri("content/index.md");

        Assert.NotNull(result);
        Assert.Equal("https://docs.github.com/en", result!.AbsoluteUri);
    }

    [Fact]
    public void BuildOfficialDocsUri_Maps_Section_Index_Markdown_To_Section_Home()
    {
        var result = MainWindow.BuildOfficialDocsUri("content/copilot/index.md");

        Assert.NotNull(result);
        Assert.Equal("https://docs.github.com/en/copilot", result!.AbsoluteUri);
    }

    [Fact]
    public void BuildOfficialDocsUri_Returns_Null_For_Null_Or_Empty_Path()
    {
        Assert.Null(MainWindow.BuildOfficialDocsUri(null));
        Assert.Null(MainWindow.BuildOfficialDocsUri(string.Empty));
        Assert.Null(MainWindow.BuildOfficialDocsUri("   "));
    }

    [Fact]
    public void BuildOfficialDocsUri_Returns_Null_For_Non_Content_Markdown_Path()
    {
        // CHANGELOG.md and other root-level Markdown files have no canonical public docs URL.
        Assert.Null(MainWindow.BuildOfficialDocsUri("CHANGELOG.md"));
        Assert.Null(MainWindow.BuildOfficialDocsUri("README.md"));
    }

    [Fact]
    public void BuildOfficialDocsUri_Returns_Null_For_Non_Markdown_Content_Path()
    {
        // data/*.yml, src/*.ts etc. are not Markdown pages; nothing to publish-link to.
        Assert.Null(MainWindow.BuildOfficialDocsUri("data/release-notes.yml"));
        Assert.Null(MainWindow.BuildOfficialDocsUri("src/some-module.ts"));
    }

    [Fact]
    public void BuildOfficialDocsUri_Trims_Surrounding_Whitespace_From_Path()
    {
        var result = MainWindow.BuildOfficialDocsUri("  content/copilot/about-copilot.md  ");

        Assert.NotNull(result);
        Assert.Equal("https://docs.github.com/en/copilot/about-copilot", result!.AbsoluteUri);
    }

    [Fact]
    public void BuildDiffHeaderLabel_Shows_Changed_Block_Count()
    {
        Assert.Equal("PR HEAD localhost・本文差分 3", MainWindow.BuildDiffHeaderLabel("PR HEAD localhost", 3));
        Assert.Equal("変更前 localhost・本文差分なし", MainWindow.BuildDiffHeaderLabel("変更前 localhost", 0));
        Assert.Equal(
            "PR HEAD Markdown・本文差分なし・ソース差分 1",
            MainWindow.BuildDiffHeaderLabel("PR HEAD Markdown", 0, 1));
    }

    [Theory]
    [InlineData("http://127.0.0.1:4500/markdown/before?v=fpt&file=data%2Freusables%2Fwebhooks%2Fissue_properties.md", true)]
    [InlineData("http://127.0.0.1:4500/markdown/after?v=fpt&file=CHANGELOG.md", true)]
    [InlineData("http://127.0.0.1:4501/en/rest/using-the-rest-api/github-event-types", false)]
    public void IsMarkdownPreviewUri_Detects_Rendered_Markdown_Comparison_Urls(string url, bool expected)
    {
        Assert.Equal(expected, MainWindow.IsMarkdownPreviewUri(new Uri(url)));
    }

    [Fact]
    public void BuildComparisonFilePathLabel_Trims_Path_And_Allows_Empty()
    {
        Assert.Equal(
            "content/copilot/how-tos/configure-access-to-ai-models.md",
            MainWindow.BuildComparisonFilePathLabel(
                " content/copilot/how-tos/configure-access-to-ai-models.md "));
        Assert.Equal(string.Empty, MainWindow.BuildComparisonFilePathLabel(null));
        Assert.Equal(string.Empty, MainWindow.BuildComparisonFilePathLabel("   "));
    }

    [Fact]
    public void BuildComparisonFileIndexLabel_Shows_Ordinal_When_Available()
    {
        Assert.Equal("3/6", MainWindow.BuildComparisonFileIndexLabel(3, 6));
        Assert.Equal(string.Empty, MainWindow.BuildComparisonFileIndexLabel(null, 6));
        Assert.Equal(string.Empty, MainWindow.BuildComparisonFileIndexLabel(3, null));
        Assert.Equal(string.Empty, MainWindow.BuildComparisonFileIndexLabel(0, 6));
    }

    [Theory]
    [InlineData("rsr-preview-scroll:before:0.25", true, 0.25)]
    [InlineData("rsr-preview-scroll:after:1.5", false, 1)]
    [InlineData("rsr-preview-scroll:before:-0.2", true, 0)]
    public void TryParsePreviewScrollMessage_Parses_Pane_And_Clamps_Ratio(
        string message,
        bool expectedBeforePane,
        double expectedRatio)
    {
        var parsed = MainWindow.TryParsePreviewScrollMessage(message, out var pane, out var ratio);

        Assert.True(parsed);
        Assert.Equal(expectedBeforePane, pane == PreviewDiffPane.Before);
        Assert.Equal(expectedRatio, ratio, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rsr-preview-scroll:left:0.5")]
    [InlineData("rsr-preview-scroll:before:not-a-number")]
    [InlineData("unrelated:before:0.5")]
    [InlineData("rsr-preview-scroll:before:0.5:120")] // 4-part is malformed: must be 3 or 5
    public void TryParsePreviewScrollMessage_Rejects_Invalid_Messages(string? message)
    {
        var parsed = MainWindow.TryParsePreviewScrollMessage(message, out _, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParsePreviewScrollMessage_Parses_Anchor_Fingerprint_And_Offset()
    {
        const string fingerprint = "U2V0dGluZyB1cCBHaXRIdWIgQ29waWxvdA=="; // base64("Setting up GitHub Copilot")
        var message = $"rsr-preview-scroll:after:0.42:120.5:{fingerprint}";

        var parsed = MainWindow.TryParsePreviewScrollMessage(
            message,
            out var pane,
            out var ratio,
            out var anchorOffsetPx,
            out var anchorFingerprint);

        Assert.True(parsed);
        Assert.Equal(PreviewDiffPane.After, pane);
        Assert.Equal(0.42, ratio, precision: 6);
        Assert.Equal(120.5, anchorOffsetPx, precision: 6);
        Assert.Equal(fingerprint, anchorFingerprint);
    }

    [Fact]
    public void TryParsePreviewScrollMessage_Anchor_Outputs_Default_To_Empty_For_Legacy_Messages()
    {
        var parsed = MainWindow.TryParsePreviewScrollMessage(
            "rsr-preview-scroll:before:0.25",
            out _,
            out _,
            out var anchorOffsetPx,
            out var anchorFingerprint);

        Assert.True(parsed);
        Assert.True(double.IsNaN(anchorOffsetPx));
        Assert.Null(anchorFingerprint);
    }

    [Fact]
    public void BuildInstallSynchronizedScrollScript_Posts_Before_Pane_Ratio_Message()
    {
        var script = MainWindow.BuildInstallSynchronizedScrollScript(PreviewDiffPane.Before);

        Assert.Contains("rsr-preview-scroll", script, StringComparison.Ordinal);
        Assert.Contains("const pane = \"before\"", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('scroll'", script, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstallSynchronizedScrollScript_Sends_Anchor_Fingerprint_For_Topmost_Visible_Block()
    {
        var script = MainWindow.BuildInstallSynchronizedScrollScript(PreviewDiffPane.After);

        // The install script must look for a visible content block and include its
        // fingerprint + viewport offset in the outgoing message so the peer can do
        // content-anchored sync rather than relying on a height-ratio that diverges
        // when the two pages have different chrome.
        Assert.Contains("getBoundingClientRect", script, StringComparison.Ordinal);
        Assert.Contains("btoa", script, StringComparison.Ordinal);
        Assert.Contains("data-rsr-diff-index", script, StringComparison.Ordinal); // anchor candidates include diff blocks
    }

    [Fact]
    public void BuildApplySynchronizedScrollScript_Clamps_Ratio_And_Suppresses_Feedback()
    {
        var script = MainWindow.BuildApplySynchronizedScrollScript(2.4);

        Assert.Contains("const ratio = 1", script, StringComparison.Ordinal);
        Assert.Contains("suppressUntil", script, StringComparison.Ordinal);
        Assert.Contains("window.scrollTo", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplySynchronizedScrollScript_Uses_Anchor_When_Provided()
    {
        var script = MainWindow.BuildApplySynchronizedScrollScript(
            ratio: 0.5,
            anchorOffsetPx: 120.5,
            anchorFingerprintBase64: "U2V0dGluZyB1cCBHaXRIdWIgQ29waWxvdA==");

        Assert.Contains("U2V0dGluZyB1cCBHaXRIdWIgQ29waWxvdA==", script, StringComparison.Ordinal);
        Assert.Contains("120.5", script, StringComparison.Ordinal);
        Assert.Contains("window.scrollBy", script, StringComparison.Ordinal); // anchor branch uses scrollBy(delta)
        // Ratio fallback must still be present so anchor-miss does not freeze sync.
        Assert.Contains("const ratio = 0.5", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveWorkbenchColumnRestoreWidth_Preserves_User_Adjusted_Width()
    {
        var savedWidth = new GridLength(640);

        var result = MainWindow.ResolveWorkbenchColumnRestoreWidth(savedWidth);

        Assert.Equal(GridUnitType.Pixel, result.GridUnitType);
        Assert.Equal(640, result.Value);
    }

    [Fact]
    public void ResolveWorkbenchColumnRestoreWidth_Falls_Back_When_Saved_Width_Is_Collapsed()
    {
        var result = MainWindow.ResolveWorkbenchColumnRestoreWidth(new GridLength(0));

        Assert.Equal(GridUnitType.Star, result.GridUnitType);
        Assert.Equal(2, result.Value);
    }

    [Theory]
    [InlineData(false, "‹‹", "プレビューだけ表示", "左の作業ペインを折りたたんでプレビューだけ表示します")]
    [InlineData(true, "››", "作業ペインを戻す", "折りたたんだ左の作業ペインを戻します")]
    public void PreviewFocusToggleLabels_Describe_Current_Action(
        bool isPreviewFocusMode,
        string expectedText,
        string expectedAutomationName,
        string expectedToolTip)
    {
        Assert.Equal(expectedText, MainWindow.BuildPreviewFocusToggleText(isPreviewFocusMode));
        Assert.Equal(expectedAutomationName, MainWindow.BuildPreviewFocusToggleAutomationName(isPreviewFocusMode));
        Assert.Equal(expectedToolTip, MainWindow.BuildPreviewFocusToggleToolTip(isPreviewFocusMode));
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Ignores_Whitespace_Only_Differences()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "GitHub Copilot summary"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "GitHub\nCopilot   summary"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Empty(plan.BeforeChangedIndexes);
        Assert.Empty(plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Marks_Inserted_Block_On_After_Pane()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "Next"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "Added guidance"),
            new PreviewDiffBlock(2, "Next"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Empty(plan.BeforeChangedIndexes);
        Assert.Equal(IndexOne, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Marks_All_After_Blocks_When_Before_File_Is_Missing()
    {
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "New page title"),
            new PreviewDiffBlock(1, "Added guidance"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(Array.Empty<PreviewDiffBlock>(), afterBlocks);

        Assert.Empty(plan.BeforeChangedIndexes);
        Assert.Equal([0, 1], plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Marks_All_Before_Blocks_When_After_File_Is_Missing()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Removed page title"),
            new PreviewDiffBlock(1, "Removed guidance"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, Array.Empty<PreviewDiffBlock>());

        Assert.Equal([0, 1], plan.BeforeChangedIndexes);
        Assert.Empty(plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Marks_Replaced_Block_On_Both_Panes()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "Old paragraph"),
            new PreviewDiffBlock(2, "Next"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "New paragraph"),
            new PreviewDiffBlock(2, "Next"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(IndexOne, plan.BeforeChangedIndexes);
        Assert.Equal(IndexOne, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_ApplyPlanScript_Adds_Scrollbar_Diff_Markers()
    {
        var script = PreviewDiffHighlighter.BuildApplyPlanScript("[1,2]", "\"after\"");

        Assert.Contains("rsr-preview-diff-scrollbar", script, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-scrollbar-marker-after", script, StringComparison.Ordinal);
        Assert.Contains("right: 12px", script, StringComparison.Ordinal);
        Assert.Contains("const rect = element.getBoundingClientRect();", script, StringComparison.Ordinal);
        Assert.Contains("rect.top + window.scrollY", script, StringComparison.Ordinal);
        Assert.Contains("rect.height / maxScrollTop", script, StringComparison.Ordinal);
        Assert.Contains("marker.style.top", script, StringComparison.Ordinal);
        Assert.Contains("marker.style.height", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocsThemeScript_Dark_Sets_Data_Color_Mode_Dark()
    {
        var script = MainWindow.BuildDocsThemeScript(DocsThemeMode.Dark);

        Assert.Contains("data-color-mode", script, StringComparison.Ordinal);
        Assert.Contains("\"dark\"", script, StringComparison.Ordinal);
        // Persist via localStorage + cookie so the React shell does not flip it back on rerender.
        Assert.Contains("localStorage", script, StringComparison.Ordinal);
        Assert.Contains("color_mode", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocsThemeScript_Applies_Mode_To_Primer_Color_Mode_Containers()
    {
        var script = MainWindow.BuildDocsThemeScript(DocsThemeMode.Dark);

        // docs.github.com renders both <html data-color-mode="auto"> and an
        // inner Primer container with its own data-color-mode. Updating only
        // documentElement leaves the inner container on auto, so query all of them.
        Assert.Contains("document.querySelectorAll('[data-color-mode]')", script, StringComparison.Ordinal);
        Assert.Contains("document.body", script, StringComparison.Ordinal);
        Assert.Contains("setAttributeIfChanged(target, 'data-color-mode', mode)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocsThemeScript_Observes_Primer_Rehydration_Color_Mode_Changes()
    {
        var script = MainWindow.BuildDocsThemeScript(DocsThemeMode.Light);

        // The official docs React shell can rehydrate or replace the inner
        // color-mode node after our initial injection. Keep the selected mode pinned.
        Assert.Contains("MutationObserver", script, StringComparison.Ordinal);
        Assert.Contains("attributeFilter: ['data-color-mode', 'data-light-theme', 'data-dark-theme']", script, StringComparison.Ordinal);
        Assert.Contains("__repoSyncRadarDocsTheme", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDocsThemeScript_Light_Sets_Data_Color_Mode_Light()
    {
        var script = MainWindow.BuildDocsThemeScript(DocsThemeMode.Light);

        Assert.Contains("data-color-mode", script, StringComparison.Ordinal);
        Assert.Contains("\"light\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DocsThemeMode.Dark, Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Dark)]
    [InlineData(DocsThemeMode.Light, Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme.Light)]
    public void BuildPreferredColorScheme_Uses_Explicit_WebView2_Mode(
        DocsThemeMode theme,
        Microsoft.Web.WebView2.Core.CoreWebView2PreferredColorScheme expected)
    {
        Assert.Equal(expected, MainWindow.BuildPreferredColorScheme(theme));
    }

    [Theory]
    [InlineData(DocsThemeMode.Dark, "#0D1117", "#C9D1D9")]
    [InlineData(DocsThemeMode.Light, "#F6F8FA", "#24292F")]
    public void ResolveAppChromeThemePalette_Uses_Mode_Appropriate_Header_Colors(
        DocsThemeMode theme,
        string expectedBackground,
        string expectedForeground)
    {
        var palette = MainWindow.ResolveAppChromeThemePalette(theme);

        Assert.Equal(expectedBackground, palette.HeaderBackground);
        Assert.Equal(expectedForeground, palette.HeaderForeground);
    }

    [Theory]
    [InlineData("#0D1117", 0x0017110D)]
    [InlineData("#F6F8FA", 0x00FAF8F6)]
    [InlineData("#58A6FF", 0x00FFA658)]
    public void ToColorRef_Converts_Hex_Color_To_Windows_Colorref(string hexColor, int expectedColorRef)
    {
        Assert.Equal(expectedColorRef, MainWindow.ToColorRef(hexColor));
    }

    [Theory]
    [InlineData("rsr-preview-version:fpt", DocsPlan.Fpt, null)]
    [InlineData("rsr-preview-version:ghec", DocsPlan.Ghec, null)]
    [InlineData("rsr-preview-version:ghes-3.21", DocsPlan.Ghes, "3.21")]
    public void TryParsePreviewVersionMessage_Parses_Known_Version_Slug(
        string message,
        DocsPlan expectedPlan,
        string? expectedRelease)
    {
        var parsed = MainWindow.TryParsePreviewVersionMessage(message, out var version);

        Assert.True(parsed);
        Assert.Equal(expectedPlan, version.Plan);
        Assert.Equal(expectedRelease, version.GhesRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rsr-preview-scroll:before:0.5")]
    [InlineData("rsr-preview-version:unknown")]
    public void TryParsePreviewVersionMessage_Rejects_Non_Version_Messages(string? message)
    {
        Assert.False(MainWindow.TryParsePreviewVersionMessage(message, out _));
    }

    [Theory]
    [InlineData(1, 7, true, false, true)]
    [InlineData(4, 7, true, true, true)]
    [InlineData(7, 7, true, true, false)]
    [InlineData(1, 1, false, false, false)]
    [InlineData(8, 7, false, false, false)]
    [InlineData(0, 7, false, false, false)]
    [InlineData(null, null, false, false, false)]
    public void ResolvePreviewFileNavigationState_Enables_Header_Arrows_By_Position(
        int? ordinal,
        int? count,
        bool expectedVisible,
        bool expectedPrevious,
        bool expectedNext)
    {
        var state = MainWindow.ResolvePreviewFileNavigationState(ordinal, count);

        Assert.Equal(expectedVisible, state.IsVisible);
        Assert.Equal(expectedPrevious, state.CanPrevious);
        Assert.Equal(expectedNext, state.CanNext);
    }

    [Theory]
    [InlineData(PreviewFileNavigationDirection.Previous, true, "前のファイル差分へ")]
    [InlineData(PreviewFileNavigationDirection.Previous, false, "最初のファイル差分です")]
    [InlineData(PreviewFileNavigationDirection.Next, true, "次のファイル差分へ")]
    [InlineData(PreviewFileNavigationDirection.Next, false, "最後のファイル差分です")]
    public void BuildPreviewFileNavigationToolTip_Describes_Available_Action(
        PreviewFileNavigationDirection direction,
        bool enabled,
        string expected)
    {
        var state = direction == PreviewFileNavigationDirection.Previous
            ? new PreviewFileNavigationState(true, enabled, false, 2, 3)
            : new PreviewFileNavigationState(true, false, enabled, 2, 3);

        Assert.Equal(expected, MainWindow.BuildPreviewFileNavigationToolTip(direction, state));
    }

    [Theory]
    [InlineData(DocsThemeMode.Dark, DocsThemeMode.Light)]
    [InlineData(DocsThemeMode.Light, DocsThemeMode.Dark)]
    public void ToggleDocsTheme_Inverts_The_Current_Mode(DocsThemeMode current, DocsThemeMode expected)
    {
        Assert.Equal(expected, MainWindow.ToggleDocsTheme(current));
    }

    [Theory]
    [InlineData(DocsThemeMode.Dark, "☀")]
    [InlineData(DocsThemeMode.Light, "🌙")]
    public void BuildDocsThemeToggleGlyph_Shows_Next_Mode_Icon(DocsThemeMode current, string expectedGlyph)
    {
        // Glyph represents the mode you will switch TO when clicked.
        Assert.Equal(expectedGlyph, MainWindow.BuildDocsThemeToggleGlyph(current));
    }

    [Theory]
    [InlineData(DocsThemeMode.Dark)]
    [InlineData(DocsThemeMode.Light)]
    public void BuildDocsThemeToggleToolTip_Mentions_The_Target_Mode(DocsThemeMode current)
    {
        var toolTip = MainWindow.BuildDocsThemeToggleToolTip(current);

        Assert.False(string.IsNullOrWhiteSpace(toolTip));
        if (current == DocsThemeMode.Dark)
        {
            Assert.Contains("ライト", toolTip, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("ダーク", toolTip, StringComparison.Ordinal);
        }
    }
}
