using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using RepoSyncRadar.App;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.App.Tests;

public sealed class MainWindowPreviewComparisonTests
{
    private static readonly int[] _indexOne = [1];

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

    [Theory]
    [InlineData("https://github.com/github/docs/commit/abc", true)]
    [InlineData("https://github.com/copilot", true)]
    [InlineData("https://docs.github.com/en/copilot", false)]
    [InlineData("https://example.com/page", false)]
    public void ShouldResetBeforeSinglePageNavigation_Only_Targets_GitHub(string url, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldResetBeforeSinglePageNavigation(new Uri(url)));
    }

    [Fact]
    public void IsExpectedNavigationCompletion_Ignores_Stale_WebView2_Events()
    {
        Assert.False(MainWindow.IsExpectedNavigationCompletion(42, expectedNavigationId: null));
        Assert.True(MainWindow.IsExpectedNavigationCompletion(42, expectedNavigationId: 42));
        Assert.False(MainWindow.IsExpectedNavigationCompletion(41, expectedNavigationId: 42));
    }

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionAborted, true)]
    [InlineData(CoreWebView2WebErrorStatus.OperationCanceled, true)]
    [InlineData(CoreWebView2WebErrorStatus.CannotConnect, false)]
    public void IsTransientSinglePageNavigationError_Identifies_Retryable_WebView2_Status(
        CoreWebView2WebErrorStatus status,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.IsTransientSinglePageNavigationError(status));
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
    [InlineData("https://docs.github.com/en/copilot/about-copilot", true)]
    [InlineData("https://github.com/github/docs/pull/123", true)]
    [InlineData("https://evil.example/rsr-preview-scroll", false)]
    [InlineData("http://docs.github.com/en/copilot/about-copilot", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    public void IsWebMessageSourceAllowed_Uses_Configured_Https_Hosts(
        string source,
        bool expected)
    {
        var allowList = new UrlAllowList(["docs.github.com", "github.com"]);
        var previewSession = new PreviewSession();

        Assert.Equal(
            expected,
            MainWindow.IsWebMessageSourceAllowed(source, allowList, previewSession));
    }

    [Theory]
    [InlineData("http://localhost:4500/markdown/after", true)]
    [InlineData("http://127.0.0.1:4500/markdown/after", true)]
    [InlineData("http://localhost:4501/markdown/after", false)]
    [InlineData("http://example.com:4500/markdown/after", false)]
    public void IsWebMessageSourceAllowed_Uses_Active_Local_Preview_Session(
        string source,
        bool expected)
    {
        var allowList = new UrlAllowList(["docs.github.com"]);
        var previewSession = new PreviewSession();
        previewSession.Activate(4500);

        Assert.Equal(
            expected,
            MainWindow.IsWebMessageSourceAllowed(source, allowList, previewSession));
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
    [InlineData("rsr-preview-scroll:before:0", true, 0)]
    [InlineData("rsr-preview-scroll:after:1520.75", false, 1520.75)]
    [InlineData("rsr-preview-scroll:before:-0.2", true, 0)]
    public void TryParsePreviewScrollMessage_Parses_Pane_And_Clamps_ScrollTop(
        string message,
        bool expectedBeforePane,
        double expectedScrollTop)
    {
        var parsed = MainWindow.TryParsePreviewScrollMessage(message, out var pane, out var scrollTop);

        Assert.True(parsed);
        Assert.Equal(expectedBeforePane, pane == PreviewDiffPane.Before);
        Assert.Equal(expectedScrollTop, scrollTop, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rsr-preview-scroll:left:0.5")]
    [InlineData("rsr-preview-scroll:before:not-a-number")]
    [InlineData("unrelated:before:0.5")]
    [InlineData("rsr-preview-scroll:before:0.5:120")]
    public void TryParsePreviewScrollMessage_Rejects_Invalid_Messages(string? message)
    {
        var parsed = MainWindow.TryParsePreviewScrollMessage(message, out _, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void BuildInstallSynchronizedScrollScript_Posts_Absolute_ScrollTop()
    {
        var script = MainWindow.BuildInstallSynchronizedScrollScript(PreviewDiffPane.Before);

        Assert.Contains("rsr-preview-scroll", script, StringComparison.Ordinal);
        Assert.Contains("const pane = \"before\"", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('scroll'", script, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", script, StringComparison.Ordinal);
        Assert.Contains("lastScrollTop", script, StringComparison.Ordinal);
        Assert.Contains("currentTop.toFixed(2)", script, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-scroll:${pane}:${currentTop.toFixed(2)}", script, StringComparison.Ordinal);
        Assert.Contains("event.key !== 'F7'", script, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-navigation:${direction}", script, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener('keydown', keyHandler, true)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduleCorrection", script, StringComparison.Ordinal);
        Assert.DoesNotContain("computeFingerprint", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplySynchronizedScrollScript_Uses_Absolute_ScrollTop_And_Suppresses_Feedback()
    {
        var script = MainWindow.BuildApplySynchronizedScrollScript(1520.75);

        Assert.Contains("const scrollTop = 1520.75", script, StringComparison.Ordinal);
        Assert.Contains("suppressUntil", script, StringComparison.Ordinal);
        Assert.Contains("Date.now() + 1000", script, StringComparison.Ordinal);
        Assert.Contains("top: Math.min(scrollTop, maxScrollTop)", script, StringComparison.Ordinal);
        Assert.Contains("window[stateKey].lastScrollTop = getScrollTop()", script, StringComparison.Ordinal);
        Assert.Contains("return getScrollTop()", script, StringComparison.Ordinal);
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

    [Fact]
    public void ResolvePreviewSurfaceColumnRestoreWidth_Preserves_User_Adjusted_Width()
    {
        var savedWidth = new GridLength(720);

        var result = MainWindow.ResolvePreviewSurfaceColumnRestoreWidth(savedWidth);

        Assert.Equal(GridUnitType.Pixel, result.GridUnitType);
        Assert.Equal(720, result.Value);
    }

    [Fact]
    public void ResolvePreviewSurfaceColumnRestoreWidth_Falls_Back_When_Saved_Width_Is_Collapsed()
    {
        var result = MainWindow.ResolvePreviewSurfaceColumnRestoreWidth(new GridLength(0));

        Assert.Equal(GridUnitType.Star, result.GridUnitType);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public void ResolveSplitterColumnRestoreWidth_Falls_Back_When_Saved_Width_Is_Collapsed()
    {
        var result = MainWindow.ResolveSplitterColumnRestoreWidth(new GridLength(0));

        Assert.Equal(GridUnitType.Pixel, result.GridUnitType);
        Assert.Equal(5, result.Value);
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
    public void PreviewDiffHighlighter_BuildPlan_Preserves_FormattingOnly_Changes()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "plain|markup:<span class=\"rsr-rendered-diff-changed\">plain</span>"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "plain|markup:<strong><span class=\"rsr-rendered-diff-changed\">plain</span></strong>"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal([0], plan.BeforeChangedIndexes);
        Assert.Equal([0], plan.AfterChangedIndexes);
        Assert.Single(plan.Changes);
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
        Assert.Equal(_indexOne, plan.AfterChangedIndexes);
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

        Assert.Equal(_indexOne, plan.BeforeChangedIndexes);
        Assert.Equal(_indexOne, plan.AfterChangedIndexes);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(_indexOne, change.BeforeIndexes);
        Assert.Equal(_indexOne, change.AfterIndexes);
        Assert.Equal(2, change.BeforeAnchorIndex);
        Assert.Equal(2, change.AfterAnchorIndex);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Advances_Table_Anchor_Past_Changed_Row()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Old value", "table-row-0"),
            new PreviewDiffBlock(1, "Shared value", "table-row-0"),
            new PreviewDiffBlock(2, "Next row", "table-row-1"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "New value", "table-row-0"),
            new PreviewDiffBlock(1, "Shared value", "table-row-0"),
            new PreviewDiffBlock(2, "Next row", "table-row-1"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(2, change.BeforeAnchorIndex);
        Assert.Equal(2, change.AfterAnchorIndex);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Advances_Both_Table_Anchors_For_Inserted_Cell()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "First value", "table-row-0"),
            new PreviewDiffBlock(1, "Shared value", "table-row-0"),
            new PreviewDiffBlock(2, "Next row", "table-row-1"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "First value", "table-row-0"),
            new PreviewDiffBlock(1, "Inserted value", "table-row-0"),
            new PreviewDiffBlock(2, "Shared value", "table-row-0"),
            new PreviewDiffBlock(3, "Next row", "table-row-1"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(2, change.BeforeAnchorIndex);
        Assert.Equal(3, change.AfterAnchorIndex);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Advances_Anchor_Past_Rowspan_Group()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Old spanning value", "table-0-row-group-0"),
            new PreviewDiffBlock(1, "Shared second row", "table-0-row-group-0"),
            new PreviewDiffBlock(2, "Next visual row", "table-0-row-group-1"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "New spanning value", "table-0-row-group-0"),
            new PreviewDiffBlock(1, "Shared second row", "table-0-row-group-0"),
            new PreviewDiffBlock(2, "Next visual row", "table-0-row-group-1"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(2, change.BeforeAnchorIndex);
        Assert.Equal(2, change.AfterAnchorIndex);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Groups_Adjacent_Changed_Blocks_For_Navigation()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "Old first paragraph"),
            new PreviewDiffBlock(2, "Old second paragraph"),
            new PreviewDiffBlock(3, "Shared ending"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "New paragraph"),
            new PreviewDiffBlock(2, "Shared ending"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        var change = Assert.Single(plan.Changes);
        Assert.Equal([1, 2], change.BeforeIndexes);
        Assert.Equal(_indexOne, change.AfterIndexes);
        Assert.Equal(2, plan.BeforeChangedIndexes.Count);
        Assert.Single(plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Keeps_OneSided_Insertion_Aligned_Before_Later_Replacement()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "Shared separator"),
            new PreviewDiffBlock(2, "Old paragraph"),
            new PreviewDiffBlock(3, "Ending"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "Intro"),
            new PreviewDiffBlock(1, "Added guidance"),
            new PreviewDiffBlock(2, "Shared separator"),
            new PreviewDiffBlock(3, "New paragraph"),
            new PreviewDiffBlock(4, "Ending"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(2, plan.Changes.Count);
        Assert.Empty(plan.Changes[0].BeforeIndexes);
        Assert.Equal(_indexOne, plan.Changes[0].AfterIndexes);
        Assert.Equal(1, plan.Changes[0].BeforeAnchorIndex);
        Assert.Equal(2, plan.Changes[0].AfterAnchorIndex);
        Assert.Equal([2], plan.Changes[1].BeforeIndexes);
        Assert.Equal([3], plan.Changes[1].AfterIndexes);
        Assert.Equal(3, plan.Changes[1].BeforeAnchorIndex);
        Assert.Equal(4, plan.Changes[1].AfterAnchorIndex);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Aligns_Inserted_Code_Lines_Inside_Block()
    {
        var beforeBlocks = new[]
        {
            new PreviewDiffBlock(0, "case completed:"),
            new PreviewDiffBlock(1, "break;"),
            new PreviewDiffBlock(2, "case failed:"),
        };
        var afterBlocks = new[]
        {
            new PreviewDiffBlock(0, "case completed:"),
            new PreviewDiffBlock(1, "log duration"),
            new PreviewDiffBlock(2, "log tokens"),
            new PreviewDiffBlock(3, "break;"),
            new PreviewDiffBlock(4, "case failed:"),
        };

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        var change = Assert.Single(plan.Changes);
        Assert.Empty(change.BeforeIndexes);
        Assert.Equal([1, 2], change.AfterIndexes);
        Assert.Equal(1, change.BeforeAnchorIndex);
        Assert.Equal(3, change.AfterAnchorIndex);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildExtractBlocksScript_Can_Collapse_Code_Lines()
    {
        var granularScript = PreviewDiffHighlighter.BuildExtractBlocksScript(extractCodeLines: true);
        var coarseScript = PreviewDiffHighlighter.BuildExtractBlocksScript(extractCodeLines: false);

        Assert.EndsWith("})(true);", granularScript, StringComparison.Ordinal);
        Assert.EndsWith("})(false);", coarseScript, StringComparison.Ordinal);
        Assert.Contains(
            "element.matches(`pre,${structuralContainerSelector}`)",
            coarseScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("__RSR_EXTRACT_CODE_LINES__", coarseScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Preserves_Separate_Hunks_Within_Detailed_Limit()
    {
        var beforeBlocks = Enumerable.Range(0, 1000)
            .Select(index => new PreviewDiffBlock(index, $"Shared block {index}"))
            .ToArray();
        var afterBlocks = beforeBlocks
            .Take(400)
            .Append(new PreviewDiffBlock(1000, "Inserted block"))
            .Concat(beforeBlocks.Skip(400))
            .Select((block, index) => block with { Index = index })
            .ToArray();
        afterBlocks[801] = new PreviewDiffBlock(801, "Changed block");

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(2, plan.Changes.Count);
        Assert.Empty(plan.Changes[0].BeforeIndexes);
        Assert.Equal([400], plan.Changes[0].AfterIndexes);
        Assert.Equal([800], plan.Changes[1].BeforeIndexes);
        Assert.Equal([801], plan.Changes[1].AfterIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Preserves_Separate_Changes_In_Very_Large_Comparisons()
    {
        var beforeBlocks = Enumerable.Range(0, 2100)
            .Select(index => new PreviewDiffBlock(index, $"Shared block {index}"))
            .ToArray();
        var afterBlocks = beforeBlocks
            .Select(block => block with { })
            .ToArray();
        afterBlocks[500] = new PreviewDiffBlock(500, "First changed block");
        afterBlocks[1500] = new PreviewDiffBlock(1500, "Second changed block");

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(2, plan.Changes.Count);
        Assert.Equal([500], plan.Changes[0].BeforeIndexes);
        Assert.Equal([500], plan.Changes[0].AfterIndexes);
        Assert.Equal([1500], plan.Changes[1].BeforeIndexes);
        Assert.Equal([1500], plan.Changes[1].AfterIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Handles_Many_Aligned_Hunks()
    {
        const int blockCount = 4_000;
        var beforeBlocks = Enumerable.Range(0, blockCount)
            .Select(index => new PreviewDiffBlock(
                index,
                $"Shared block {index}",
                $"row-{index}"))
            .ToArray();
        var afterBlocks = beforeBlocks
            .Select((block, index) => index % 2 == 0
                ? block with { }
                : block with { Text = $"Changed block {index}" })
            .ToArray();

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(blockCount / 2, plan.Changes.Count);
        Assert.All(plan.Changes, change =>
        {
            Assert.Single(change.BeforeIndexes);
            Assert.Single(change.AfterIndexes);
        });
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Uses_Patience_Anchors_Across_Large_Changed_Window()
    {
        var beforeBlocks = Enumerable.Range(0, 2100)
            .Select(index => new PreviewDiffBlock(index, $"Shared block {index}"))
            .ToArray();
        var afterBlocks = beforeBlocks
            .Select(block => block with { })
            .ToArray();
        afterBlocks[0] = new PreviewDiffBlock(0, "Changed first block");
        afterBlocks[^1] = new PreviewDiffBlock(2099, "Changed last block");

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal([0, 2099], plan.BeforeChangedIndexes);
        Assert.Equal([0, 2099], plan.AfterChangedIndexes);
        Assert.Equal(2, plan.Changes.Count);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Bounds_Recursive_Patience_Scans()
    {
        const int nestedAnchorCount = 3_000;
        var beforeTexts = new List<string>(3 * nestedAnchorCount);
        var afterTexts = new List<string>(3 * nestedAnchorCount);
        for (var index = 0; index < nestedAnchorCount; index++)
        {
            beforeTexts.Add($"Before noise {index}");
            afterTexts.Add($"After noise {index}");
            if (index + 1 < nestedAnchorCount)
            {
                beforeTexts.Add($"Anchor {index + 1}");
                afterTexts.Add($"Anchor {index + 1}");
            }
            beforeTexts.Add($"Anchor {index}");
            afterTexts.Add($"Anchor {index}");
        }
        beforeTexts.Add($"Anchor {nestedAnchorCount - 1}");
        afterTexts.Add($"Anchor {nestedAnchorCount - 1}");
        beforeTexts.Add("Before terminal");
        afterTexts.Add("After terminal");
        var beforeBlocks = beforeTexts
            .Select((text, index) => new PreviewDiffBlock(index, text))
            .ToArray();
        var afterBlocks = afterTexts
            .Select((text, index) => new PreviewDiffBlock(index, text))
            .ToArray();

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks, out var patienceAnchorScanCount);

        Assert.InRange(patienceAnchorScanCount, 2, 100);
        Assert.DoesNotContain(2, plan.BeforeChangedIndexes);
        Assert.DoesNotContain(2, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Preserves_Repeated_Lines_Without_Patience_Anchors()
    {
        var beforeBlocks = Enumerable.Range(0, 2100)
            .Select(index => new PreviewDiffBlock(index, "Repeated block"))
            .ToArray();
        var afterBlocks = beforeBlocks
            .Select(block => block with { })
            .ToArray();
        afterBlocks[0] = new PreviewDiffBlock(0, "Changed first block");
        afterBlocks[^1] = new PreviewDiffBlock(2099, "Changed last block");

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(2, plan.BeforeChangedIndexes.Count);
        Assert.Equal(2, plan.AfterChangedIndexes.Count);
        Assert.DoesNotContain(1000, plan.BeforeChangedIndexes);
        Assert.DoesNotContain(1000, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Bounds_Work_For_Very_Large_Repeated_Regions()
    {
        var beforeBlocks = Enumerable.Range(0, 3000)
            .Select(index => new PreviewDiffBlock(index, "Repeated block"))
            .ToArray();
        var afterBlocks = beforeBlocks
            .Select(block => block with { })
            .ToArray();
        afterBlocks[0] = new PreviewDiffBlock(0, "Changed first block");
        afterBlocks[^1] = new PreviewDiffBlock(2999, "Changed last block");

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(2, plan.BeforeChangedIndexes.Count);
        Assert.Equal(2, plan.AfterChangedIndexes.Count);
        Assert.DoesNotContain(1500, plan.BeforeChangedIndexes);
        Assert.DoesNotContain(1500, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Recovers_Shifted_Repeated_Lines_Within_Work_Budget()
    {
        var beforeBlocks = Enumerable.Range(0, 3000)
            .Select(index => new PreviewDiffBlock(index, index % 2 == 0 ? "A" : "B"))
            .ToArray();
        var afterBlocks = new[] { new PreviewDiffBlock(0, "Inserted block") }
            .Concat(beforeBlocks.Take(2999))
            .Append(new PreviewDiffBlock(3000, "Changed final block"))
            .Select((block, index) => block with { Index = index })
            .ToArray();

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Single(plan.BeforeChangedIndexes);
        Assert.Equal(2, plan.AfterChangedIndexes.Count);
        Assert.DoesNotContain(1500, plan.BeforeChangedIndexes);
        Assert.DoesNotContain(1500, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildPlan_Uses_Positional_Matches_After_Myers_Budget_Is_Exhausted()
    {
        var beforeBlocks = Enumerable.Range(0, 3000)
            .Select(index => new PreviewDiffBlock(
                index,
                index % 100 == 50 ? "Shared" : index % 2 == 0 ? "Before A" : "Before B"))
            .ToArray();
        var afterBlocks = Enumerable.Range(0, 3000)
            .Select(index => new PreviewDiffBlock(
                index,
                index % 100 == 50 ? "Shared" : index % 2 == 0 ? "After A" : "After B"))
            .ToArray();

        var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);

        Assert.Equal(2970, plan.BeforeChangedIndexes.Count);
        Assert.Equal(2970, plan.AfterChangedIndexes.Count);
        Assert.DoesNotContain(50, plan.BeforeChangedIndexes);
        Assert.DoesNotContain(1550, plan.AfterChangedIndexes);
        Assert.Contains(0, plan.BeforeChangedIndexes);
        Assert.Contains(2999, plan.AfterChangedIndexes);
    }

    [Fact]
    public void PreviewDiffHighlighter_BuildAlignmentGapPlan_Fills_Shorter_Pane_At_Each_Hunk()
    {
        var changes = new[]
        {
            new PreviewDiffChange([], [1], 1, 2),
            new PreviewDiffChange([2], [3], 3, 4),
        };

        var gaps = PreviewDiffHighlighter.BuildAlignmentGapPlan(
            changes,
            [120, 340],
            [200, 420]);

        var beforeGap = Assert.Single(gaps.Before);
        Assert.Equal(0, beforeGap.NavigationIndex);
        Assert.Equal(1, beforeGap.AnchorIndex);
        Assert.Equal(80, beforeGap.Height, precision: 3);
        Assert.Empty(gaps.After);
    }

    [Theory]
    [InlineData(720, 1180, 1180)]
    [InlineData(1180, 720, 1180)]
    [InlineData(-10, 0, 0)]
    public void PreviewDiffHighlighter_ResolveSynchronizedScrollTop_Preserves_Unclamped_Position(
        double beforeScrollTop,
        double afterScrollTop,
        double expected)
    {
        Assert.Equal(
            expected,
            PreviewDiffHighlighter.ResolveSynchronizedScrollTop(beforeScrollTop, afterScrollTop));
    }

    [Theory]
    [InlineData(1180, 1180, 1180)]
    [InlineData(1180, 720, 720)]
    [InlineData(-10, 720, 0)]
    public void PreviewDiffHighlighter_ResolveAppliedSynchronizedScrollTop_Uses_Common_Reachable_Position(
        double beforeScrollTop,
        double afterScrollTop,
        double expected)
    {
        Assert.Equal(
            expected,
            PreviewDiffHighlighter.ResolveAppliedSynchronizedScrollTop(
                beforeScrollTop,
                afterScrollTop));
    }

    [Fact]
    public void PreviewDiffHighlighter_CodeWrappingCandidates_Include_Anchor_And_Preceding_Block()
    {
        var changes = new[]
        {
            new PreviewDiffChange([], [7, 8], 6, 9),
            new PreviewDiffChange([12], [13], 14, 15),
        };

        Assert.Equal(
            [5, 6, 12, 13, 14],
            PreviewDiffHighlighter.GetCodeWrappingCandidateIndexes(
                changes,
                PreviewDiffPane.Before));
        Assert.Equal(
            [7, 8, 9, 13, 14, 15],
            PreviewDiffHighlighter.GetCodeWrappingCandidateIndexes(
                changes,
                PreviewDiffPane.After));
    }

    [Fact]
    public void PreviewDiffHighlighter_CodeWrappingCandidates_Include_Last_Block_When_Final_Anchor_Is_Missing()
    {
        var changes = new[]
        {
            new PreviewDiffChange([], [7, 8], null, null),
        };

        Assert.Equal(
            [-1],
            PreviewDiffHighlighter.GetCodeWrappingCandidateIndexes(
                changes,
                PreviewDiffPane.Before));
        Assert.Equal(
            [7, 8, -1],
            PreviewDiffHighlighter.GetCodeWrappingCandidateIndexes(
                changes,
                PreviewDiffPane.After));
    }

    [Fact]
    public void PreviewDiffHighlighter_AlignmentScripts_Measure_Anchors_And_Draw_Striped_Gaps()
    {
        var measureScript = PreviewDiffHighlighter.BuildMeasureAlignmentAnchorsScript(
            "[1,null]",
            "[1,2]");
        var applyScript = PreviewDiffHighlighter.BuildApplyAlignmentGapsScript(
            """[{"anchorIndex":1,"height":80,"navigationIndex":0}]""");

        Assert.Contains(
            "document.querySelectorAll('.rsr-preview-diff-alignment-gap-row,.rsr-preview-diff-alignment-gap')",
            measureScript,
            StringComparison.Ordinal);
        Assert.Contains("style.setProperty('display', 'none', 'important')", measureScript, StringComparison.Ordinal);
        Assert.Contains("style.removeProperty('display')", measureScript, StringComparison.Ordinal);
        Assert.Contains("scrollTop", measureScript, StringComparison.Ordinal);
        Assert.Contains("offsets", measureScript, StringComparison.Ordinal);
        Assert.Contains("getBoundingClientRect().top + window.scrollY", measureScript, StringComparison.Ordinal);
        Assert.Contains("root.getBoundingClientRect().bottom + window.scrollY", measureScript, StringComparison.Ordinal);
        Assert.Contains(": null", measureScript, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-aligned-code", measureScript, StringComparison.Ordinal);
        Assert.Contains("overflow-anchor: none", measureScript, StringComparison.Ordinal);
        Assert.Contains("target?.matches('pre') ? target : target?.closest('pre')", measureScript, StringComparison.Ordinal);
        Assert.Contains("[1,2].forEach((index)", measureScript, StringComparison.Ordinal);
        Assert.Contains(
            "Array.from(root.querySelectorAll('[data-rsr-diff-index]')).at(-1)",
            measureScript,
            StringComparison.Ordinal);
        Assert.Contains("repeating-linear-gradient", applyScript, StringComparison.Ordinal);
        Assert.Contains("td.rsr-preview-diff-alignment-gap", applyScript, StringComparison.Ordinal);
        Assert.Contains("display: table-cell", applyScript, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-alignment-gap-row", applyScript, StringComparison.Ordinal);
        Assert.Contains("gapRow.className = 'rsr-preview-diff-alignment-gap-row'", applyScript, StringComparison.Ordinal);
        Assert.Contains("const tableColumnCounts = new WeakMap()", applyScript, StringComparison.Ordinal);
        Assert.Contains("tableColumnCounts.get(table)", applyScript, StringComparison.Ordinal);
        Assert.Contains("tableColumnCounts.set(table, widestColumnCount)", applyScript, StringComparison.Ordinal);
        Assert.Contains("const sectionEndRowIndexes = new Map()", applyScript, StringComparison.Ordinal);
        Assert.Contains("const activeRowSpans = []", applyScript, StringComparison.Ordinal);
        Assert.Contains("activeRowSpans[columnIndex + offset]", applyScript, StringComparison.Ordinal);
        Assert.Contains("gapCell.colSpan = getTableColumnCount(table)", applyScript, StringComparison.Ordinal);
        Assert.Contains("width: auto !important", applyScript, StringComparison.Ordinal);
        Assert.Contains("const insertTableGapBefore =", applyScript, StringComparison.Ordinal);
        Assert.Contains("const insertTableGapAfter =", applyScript, StringComparison.Ordinal);
        Assert.Contains(
            "table.insertBefore(gapSection, rowGroup.nextSibling)",
            applyScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "terminalRow.parentElement?.matches('tfoot')",
            applyScript,
            StringComparison.Ordinal);
        Assert.Contains("insertGapAfter(table", applyScript, StringComparison.Ordinal);
        Assert.Contains(
            ".rsr-preview-diff-alignment-gap-section",
            applyScript,
            StringComparison.Ordinal);
        Assert.Contains("row.getBoundingClientRect().top - rowTopBefore", applyScript, StringComparison.Ordinal);
        Assert.Contains("setGapHeight(gapCell, renderedHeight)", applyScript, StringComparison.Ordinal);
        Assert.Contains("--rsr-preview-gap-separator", applyScript, StringComparison.Ordinal);
        Assert.Contains("background-clip: content-box", applyScript, StringComparison.Ordinal);
        Assert.Contains("padding-block-end: var(--rsr-preview-gap-separator)", applyScript, StringComparison.Ordinal);
        Assert.Contains("aria-hidden", applyScript, StringComparison.Ordinal);
        Assert.Contains("role", applyScript, StringComparison.Ordinal);
        Assert.Contains("data-rsr-diff-navigation-index", applyScript, StringComparison.Ordinal);
        Assert.Contains("height.toFixed(2)", applyScript, StringComparison.Ordinal);
        Assert.Contains("const preservedScrollTop = 0", applyScript, StringComparison.Ordinal);
        Assert.Contains("window.scrollTo", applyScript, StringComparison.Ordinal);
        Assert.Contains("__repoSyncRadarDiffNavigation?.refresh?.()", applyScript, StringComparison.Ordinal);
        Assert.Contains("const actualDisplacement =", applyScript, StringComparison.Ordinal);
        Assert.Contains("setGapHeight(gap, renderedHeight)", applyScript, StringComparison.Ordinal);
        Assert.DoesNotContain("'margin-block-end'", applyScript, StringComparison.Ordinal);
        Assert.DoesNotContain("border-block-end", applyScript, StringComparison.Ordinal);
        Assert.Contains("if (!root) {\n    return null;", applyScript, StringComparison.Ordinal);
        Assert.Contains("return window.scrollY || scrollingRoot?.scrollTop || 0", applyScript, StringComparison.Ordinal);
        Assert.Contains("anchor.matches('.rsr-code-line') ? 'span' : 'div'", applyScript, StringComparison.Ordinal);
        Assert.DoesNotContain("anchor?.closest('.ghd-", applyScript, StringComparison.Ordinal);
        Assert.Contains(
            "Array.from(root.querySelectorAll('[data-rsr-diff-index]')).at(-1)",
            applyScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "terminalElement.closest('li') ||",
            applyScript,
            StringComparison.Ordinal);
        Assert.Contains("terminalElement.closest('p,figure')", applyScript, StringComparison.Ordinal);
        Assert.Contains(
            "terminalElement.closest('picture,object')",
            applyScript,
            StringComparison.Ordinal);
        Assert.Contains("terminalContext.matches('li') ? 'li'", applyScript, StringComparison.Ordinal);
        Assert.Contains("insertGapAfter(", applyScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ApplyPlanScript_Adds_Scrollbar_Diff_Markers()
    {
        var script = PreviewDiffHighlighter.BuildApplyPlanScript("[1,2]", "\"after\"");

        Assert.Contains("window.__repoSyncRadarDiffScrollbar?.disable?.()", script, StringComparison.Ordinal);
        Assert.Contains("document.getElementById('rsr-diff-scrollbar')?.remove()", script, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-scrollbar", script, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-scrollbar-marker-after", script, StringComparison.Ordinal);
        Assert.Contains("right: 24px", script, StringComparison.Ordinal);
        Assert.Contains("width: 10px", script, StringComparison.Ordinal);
        Assert.Contains("const markerGroups = new Map()", script, StringComparison.Ordinal);
        Assert.Contains("const splitMarkerSegments = (elements)", script, StringComparison.Ordinal);
        Assert.Contains("item.rect.top > previous.bottom + 2", script, StringComparison.Ordinal);
        Assert.Contains("splitMarkerSegments(elements).forEach((segment)", script, StringComparison.Ordinal);
        Assert.Contains("const hasSubstantiveChange = substantiveTargets.length > 0", script, StringComparison.Ordinal);
        Assert.Contains(
            "const isRemoval = hasRemovedMarker !== hasAddedMarker",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const resolvedTargets = segment.elements.flatMap((element) => {",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "return element.matches(alignmentGapSelector) ? [] : [element]",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "resolvedTargets.length > 0 ? resolvedTargets : segment.elements",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "${isRemoval ? 'rsr-preview-diff-scrollbar-marker-before' : 'rsr-preview-diff-scrollbar-marker-after'}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "document.querySelectorAll('.rsr-preview-diff-target,[data-rsr-diff-navigation-index]')",
            script,
            StringComparison.Ordinal);
        Assert.Contains("`hunk-${navigationIndex}`", script, StringComparison.Ordinal);
        Assert.Contains("Math.min(...rects.map((rect) => rect.top))", script, StringComparison.Ordinal);
        Assert.Contains("Math.max(...rects.map((rect) => rect.bottom))", script, StringComparison.Ordinal);
        Assert.Contains("const documentHeight = Math.max(1, root.scrollHeight)", script, StringComparison.Ordinal);
        Assert.Contains("absoluteTop / documentHeight", script, StringComparison.Ordinal);
        Assert.Contains("(absoluteBottom - absoluteTop) / documentHeight", script, StringComparison.Ordinal);
        Assert.DoesNotContain("root.scrollHeight - window.innerHeight", script, StringComparison.Ordinal);
        Assert.Contains("window.innerHeight - height", script, StringComparison.Ordinal);
        Assert.Contains("markerTop.toFixed(1)", script, StringComparison.Ordinal);
        Assert.Contains("marker.style.top", script, StringComparison.Ordinal);
        Assert.Contains("marker.style.height", script, StringComparison.Ordinal);
        Assert.Contains(
            "window.__repoSyncRadarDiffScrollbar = { scheduleBuild: buildMarkers }",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ExtractScript_Includes_GitHub_Alert_Blocks()
    {
        var script = PreviewDiffHighlighter.ExtractBlocksScriptForTests;

        Assert.Contains(".ghd-markdown-alert", script, StringComparison.Ordinal);
        Assert.Contains(".ghd-alert,.ghd-tool", script, StringComparison.Ordinal);
        Assert.Contains("element.matches(structuralContainerSelector)", script, StringComparison.Ordinal);
        Assert.Contains("element.closest(structuralContainerSelector)", script, StringComparison.Ordinal);
        Assert.Contains("structuralContainer.querySelector(codeLineSelector)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ExtractScript_Uses_Code_Lines_Inside_Code_Tabs_And_Fences()
    {
        var script = PreviewDiffHighlighter.ExtractBlocksScriptForTests;

        Assert.Contains(
            "const codeLineSelector = 'pre > code > .rsr-code-line'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(".ghd-code-tab-label", script, StringComparison.Ordinal);
        Assert.Contains("element.matches(codeLineSelector)", script, StringComparison.Ordinal);
        Assert.Contains(
            "'.rsr-rendered-diff-added:not(.rsr-rendered-diff-gap),'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "'.rsr-rendered-diff-removed:not(.rsr-rendered-diff-gap)'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("element.matches(substantiveDiffSelector)", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "const structuralContainerSelector = '.ghd-markdown-alert,.ghd-alert,.ghd-tool,.ghd-code-tabs'",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ExtractScript_Groups_Rows_Connected_By_Rowspan()
    {
        var script = PreviewDiffHighlighter.ExtractBlocksScriptForTests;

        Assert.Contains("Array.from(root.querySelectorAll('table'))", script, StringComparison.Ordinal);
        Assert.Contains("const sectionEndRowIndexes = new Map()", script, StringComparison.Ordinal);
        Assert.Contains("sectionEndRowIndexes.get(row.parentElement)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rows.reduce(", script, StringComparison.Ordinal);
        Assert.Contains("cell.rowSpan === 0", script, StringComparison.Ordinal);
        Assert.Contains("const rowsRemainingInSection =", script, StringComparison.Ordinal);
        Assert.Contains(
            "Math.min(rowsRemainingInSection, Math.max(1, cell.rowSpan))",
            script,
            StringComparison.Ordinal);
        Assert.Contains("groupEndRowIndex", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ExtractScript_Treats_Visible_Media_As_Content()
    {
        var script = PreviewDiffHighlighter.ExtractBlocksScriptForTests;

        Assert.Contains("const mediaSelector = 'img,video,audio,iframe,object,embed'", script, StringComparison.Ordinal);
        Assert.Contains("isVisibleMedia(element)", script, StringComparison.Ordinal);
        Assert.Contains("const textContainer = element.closest(blockSelector)", script, StringComparison.Ordinal);
        Assert.Contains("element.getClientRects().length > 0", script, StringComparison.Ordinal);
        Assert.Contains("/\\/markdown-assets\\/(?:before|after)(?=\\/)/g", script, StringComparison.Ordinal);
        Assert.Contains("'/markdown-assets/shared'", script, StringComparison.Ordinal);
        Assert.Contains("const fingerprintMediaContent = (element)", script, StringComparison.Ordinal);
        Assert.Contains("context.getImageData(0, 0, canvas.width, canvas.height).data", script, StringComparison.Ordinal);
        Assert.Contains("hash = Math.imul(hash, 16777619)", script, StringComparison.Ordinal);
        Assert.Contains("const describeChangedMarkup = (element)", script, StringComparison.Ordinal);
        Assert.Contains("marker.classList.add('rsr-rendered-diff-changed')", script, StringComparison.Ordinal);
        Assert.Contains("`${text}|markup:${describeChangedMarkup(element)}`", script, StringComparison.Ordinal);
        Assert.Contains("return isVisibleMedia(element)", script, StringComparison.Ordinal);
        Assert.Contains("return `media:${element.tagName.toLowerCase()}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPreviewDiffOperationCurrent_Requires_Same_Request_And_Generation()
    {
        var request = new PreviewComparisonRequest(
            new Uri("http://localhost:4501/markdown/before"),
            new Uri("http://localhost:4501/markdown/after"),
            "Before",
            "After");
        var equalRequest = request with { };

        Assert.True(MainWindow.IsPreviewDiffOperationCurrent(request, request, 3, 3));
        Assert.False(MainWindow.IsPreviewDiffOperationCurrent(equalRequest, request, 3, 3));
        Assert.False(MainWindow.IsPreviewDiffOperationCurrent(request, request, 4, 3));
    }

    [Theory]
    [InlineData(Key.F7, ModifierKeys.None, true, 1)]
    [InlineData(Key.F7, ModifierKeys.Shift, true, -1)]
    [InlineData(Key.F7, ModifierKeys.Control, false, 0)]
    [InlineData(Key.F7, ModifierKeys.Alt, false, 0)]
    [InlineData(Key.F7, ModifierKeys.Windows, false, 0)]
    [InlineData(Key.F6, ModifierKeys.None, false, 0)]
    public void TryResolvePreviewDiffNavigationDirection_Only_Handles_Documented_Shortcuts(
        Key key,
        ModifierKeys modifiers,
        bool expectedHandled,
        int expectedDirection)
    {
        var handled = MainWindow.TryResolvePreviewDiffNavigationDirection(
            key,
            modifiers,
            out var direction);

        Assert.Equal(expectedHandled, handled);
        Assert.Equal(expectedDirection, (int)direction);
    }

    [Theory]
    [InlineData(3, 3, 7, 7, true, false, true)]
    [InlineData(3, 3, 7, 7, false, true, true)]
    [InlineData(3, 3, 7, 7, false, false, false)]
    [InlineData(3, 4, 7, 7, true, true, false)]
    [InlineData(3, 3, 7, 8, true, true, false)]
    public void CanCommitPreviewDiffNavigation_Requires_Latest_Operation_And_Found_Target(
        int expectedGeneration,
        int currentGeneration,
        int expectedOperationId,
        int currentOperationId,
        bool beforeFound,
        bool afterFound,
        bool expected)
    {
        var beforeResult = new PreviewDiffNavigationResult(beforeFound);
        var afterResult = new PreviewDiffNavigationResult(afterFound);

        Assert.Equal(
            expected,
            MainWindow.CanCommitPreviewDiffNavigation(
                expectedGeneration,
                currentGeneration,
                expectedOperationId,
                currentOperationId,
                beforeResult,
                afterResult));
    }

    [Theory]
    [InlineData(true, 720, true, 1180, 1180)]
    [InlineData(true, 720, false, 1180, 720)]
    [InlineData(false, 720, true, 1180, 1180)]
    [InlineData(false, 720, false, 1180, 0)]
    public void ResolvePreviewDiffNavigationScrollTop_Uses_Common_Absolute_Position(
        bool beforeFound,
        double beforeScrollTop,
        bool afterFound,
        double afterScrollTop,
        double expected)
    {
        var beforeResult = new PreviewDiffNavigationResult(beforeFound, beforeScrollTop);
        var afterResult = new PreviewDiffNavigationResult(afterFound, afterScrollTop);

        Assert.Equal(
            expected,
            MainWindow.ResolvePreviewDiffNavigationScrollTop(beforeResult, afterResult));
    }

    [Theory]
    [InlineData(3, 3, 7, 7, true)]
    [InlineData(3, 4, 7, 7, false)]
    [InlineData(3, 3, 7, 8, false)]
    public void IsPreviewDiffNavigationOperationCurrent_Requires_Generation_And_Operation(
        int expectedGeneration,
        int currentGeneration,
        int expectedOperationId,
        int currentOperationId,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.IsPreviewDiffNavigationOperationCurrent(
                expectedGeneration,
                currentGeneration,
                expectedOperationId,
                currentOperationId));
    }

    [Theory]
    [InlineData(3, 3, 7, 7, true)]
    [InlineData(3, 4, 7, 7, false)]
    [InlineData(3, 3, 7, 8, false)]
    public void IsPreviewAlignmentOperationCurrent_Requires_Generation_And_Operation(
        int expectedGeneration,
        int currentGeneration,
        int expectedOperationId,
        int currentOperationId,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.IsPreviewAlignmentOperationCurrent(
                expectedGeneration,
                currentGeneration,
                expectedOperationId,
                currentOperationId));
    }

    [Fact]
    public async Task ObservePreviousPreviewDiffNavigationAsync_Allows_Queue_After_Cancellation()
    {
        var canceledTask = Task.FromCanceled(new CancellationToken(canceled: true));

        await MainWindow.ObservePreviousPreviewDiffNavigationAsync(canceledTask);
        await MainWindow.ObservePreviousPreviewDiffNavigationAsync(Task.CompletedTask);
    }

    [Fact]
    public void PreviewDiffHighlighter_ApplyPlanScript_Preserves_GitHub_Alert_Layout()
    {
        var script = PreviewDiffHighlighter.BuildApplyPlanScript("[1]", "\"after\"");

        Assert.Contains(".ghd-markdown-alert.rsr-preview-diff-block", script, StringComparison.Ordinal);
        Assert.Contains("padding: 8px 0 8px 14px", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ApplyPlanScript_Does_Not_Overlay_Rendered_Inline_Diffs()
    {
        var script = PreviewDiffHighlighter.BuildApplyPlanScript("[1]", "\"after\"");

        Assert.Contains("element.classList.add('rsr-preview-diff-target')", script, StringComparison.Ordinal);
        Assert.Contains(
            "!element.matches(renderedDiffSelector) && !element.querySelector(renderedDiffSelector)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const renderedDiffSelector = '.rsr-rendered-diff-added,.rsr-rendered-diff-removed'",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ApplyPlanScript_Stamps_Navigation_Indexes()
    {
        var script = PreviewDiffHighlighter.BuildApplyPlanScript(
            "[1,2]",
            "\"after\"",
            """[{"index":1,"navigationIndex":0},{"index":2,"navigationIndex":0}]""");

        Assert.Contains("data-rsr-diff-navigation-index", script, StringComparison.Ordinal);
        Assert.Contains("navigationIndexes.get(index)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("firstChanged.scrollIntoView", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_ApplyRenderedNavigationPlanScript_Uses_Shared_Indexes()
    {
        var script = PreviewDiffHighlighter.BuildApplyRenderedNavigationPlanScript(
            """[{"index":2,"navigationIndex":1}]""");

        Assert.Contains("""{"index":2,"navigationIndex":1}""", script, StringComparison.Ordinal);
        Assert.Contains("const navigationIndexes = new Map", script, StringComparison.Ordinal);
        Assert.Contains("document.querySelectorAll('[data-rsr-diff-index]')", script, StringComparison.Ordinal);
        Assert.Contains("data-rsr-diff-navigation-index", script, StringComparison.Ordinal);
        Assert.Contains("__repoSyncRadarDiffScrollbar?.scheduleBuild?.()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_NavigateScript_Reports_Applied_Scroll_And_Refreshes_Sync_State()
    {
        var script = PreviewDiffHighlighter.BuildNavigateToDiffScript(2);

        Assert.Contains("const appliedScrollTop =", script, StringComparison.Ordinal);
        Assert.Contains("scrollSyncState.lastScrollTop = appliedScrollTop", script, StringComparison.Ordinal);
        Assert.Contains(
            "return { found: true, scrollTop: appliedScrollTop }",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDiffHighlighter_NavigateScript_Draws_One_Overlay_And_Centers_Target()
    {
        var script = PreviewDiffHighlighter.BuildNavigateToDiffScript(2);

        Assert.Contains("data-rsr-diff-navigation-index=\"2\"", script, StringComparison.Ordinal);
        Assert.Contains("rsr-preview-diff-active-overlay", script, StringComparison.Ordinal);
        Assert.Contains("--rsr-preview-diff-outline: #0969da", script, StringComparison.Ordinal);
        Assert.Contains("--rsr-preview-diff-outline: #58a6ff", script, StringComparison.Ordinal);
        Assert.Contains(
            "border: 2px solid var(--rsr-preview-diff-outline)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("pointer-events: none", script, StringComparison.Ordinal);
        Assert.Contains("overlay.setAttribute('aria-hidden', 'true')", script, StringComparison.Ordinal);
        Assert.Contains(
            "const resolvedTargets = targets.flatMap((target) => {",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "return target.matches(alignmentGapSelector) ? [] : [target]",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "return resolvedTargets.length > 0 ? resolvedTargets : targets",
            script,
            StringComparison.Ordinal);
        Assert.Contains("overlayTargets = resolveOverlayTargets()", script, StringComparison.Ordinal);
        Assert.Contains("Math.min(...rects.map((rect) => rect.left))", script, StringComparison.Ordinal);
        Assert.Contains("Math.max(...rects.map((rect) => rect.bottom))", script, StringComparison.Ordinal);
        Assert.Contains("const inlinePadding = 6", script, StringComparison.Ordinal);
        Assert.Contains(
            "getHorizontallyVisibleRect(target, inlinePadding)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "let left = Math.max(0, rect.left - inlinePadding)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "let right = Math.min(document.documentElement.clientWidth, rect.right + inlinePadding)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "left = Math.max(left, scrollRect.left + borderLeft)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "right = Math.min(right, scrollRect.right - borderRight)",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("let visibleLeft = 0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("let visibleRight = document.documentElement.clientWidth", script, StringComparison.Ordinal);
        Assert.Contains(
            "scrollTarget.addEventListener('scroll', positionOverlay, { passive: true })",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "scrollTarget.removeEventListener('scroll', positionOverlay)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const top = Math.min(...rects.map((rect) => rect.top)) + window.scrollY;",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const bottom = Math.max(...rects.map((rect) => rect.bottom)) + window.scrollY;",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("rect.top)) + window.scrollY - padding", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rect.bottom)) + window.scrollY + padding", script, StringComparison.Ordinal);
        Assert.Contains("new ResizeObserver(positionOverlay)", script, StringComparison.Ordinal);
        Assert.Contains("const refreshTargets = () =>", script, StringComparison.Ordinal);
        Assert.Contains("window.__repoSyncRadarDiffNavigation = {", script, StringComparison.Ordinal);
        Assert.Contains("refresh: refreshTargets", script, StringComparison.Ordinal);
        Assert.Contains("resizeObserver.observe(document.body)", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "targets.forEach((target) => target.classList.add('rsr-preview-diff-active'))",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Math.min(...targetRects.map((rect) => rect.top))", script, StringComparison.Ordinal);
        Assert.Contains("Math.max(...targetRects.map((rect) => rect.bottom))", script, StringComparison.Ordinal);
        Assert.Contains("targetTop - (window.innerHeight - targetHeight) / 2", script, StringComparison.Ordinal);
        Assert.Contains("window.scrollTo({ top: centeredScrollTop, behavior: 'auto' })", script, StringComparison.Ordinal);
        Assert.Contains("__repoSyncRadarPreviewScrollSync", script, StringComparison.Ordinal);
        Assert.Contains(
            "return { found: true, scrollTop: appliedScrollTop }",
            script,
            StringComparison.Ordinal);
        Assert.Contains("return { found: false }", script, StringComparison.Ordinal);
        Assert.DoesNotContain("const ratio =", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"found":true,"scrollTop":321.5}""", true, 321.5)]
    [InlineData("""{"found":false}""", false, 0)]
    [InlineData("null", false, 0)]
    [InlineData("invalid", false, 0)]
    public void PreviewDiffHighlighter_ParseNavigateResult_Handles_WebView_Result(
        string result,
        bool expectedFound,
        double expectedScrollTop)
    {
        var parsed = PreviewDiffHighlighter.ParseNavigateResult(result);

        Assert.Equal(expectedFound, parsed.Found);
        Assert.Equal(expectedScrollTop, parsed.ScrollTop);
    }

    [Theory]
    [InlineData(0, 3, "差分 1/3")]
    [InlineData(2, 3, "差分 3/3")]
    [InlineData(-1, 0, "差分なし")]
    public void BuildPreviewDiffNavigationLabel_Reports_Current_Position(
        int currentIndex,
        int count,
        string expected)
    {
        Assert.Equal(expected, MainWindow.BuildPreviewDiffNavigationLabel(currentIndex, count));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("-2", 0)]
    [InlineData("\"3\"", 0)]
    [InlineData(null, 0)]
    public void ParseScriptInteger_Handles_WebView_Script_Results(string? result, int expected)
    {
        Assert.Equal(expected, MainWindow.ParseScriptInteger(result));
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
    [InlineData("rsr-preview-diff-navigation:previous", -1)]
    [InlineData("rsr-preview-diff-navigation:next", 1)]
    public void TryParsePreviewDiffNavigationMessage_Parses_Known_Directions(
        string message,
        int expected)
    {
        Assert.True(MainWindow.TryParsePreviewDiffNavigationMessage(message, out var direction));
        Assert.Equal(expected, (int)direction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rsr-preview-diff-navigation:sideways")]
    [InlineData("rsr-preview-scroll:after:0.5")]
    public void TryParsePreviewDiffNavigationMessage_Rejects_Unknown_Messages(string? message)
    {
        Assert.False(MainWindow.TryParsePreviewDiffNavigationMessage(message, out _));
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
