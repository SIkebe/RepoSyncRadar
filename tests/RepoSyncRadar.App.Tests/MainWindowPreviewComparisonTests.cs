using System.Windows;
using RepoSyncRadar.App;
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

    [Fact]
    public void BuildDiffHeaderLabel_Shows_Changed_Block_Count()
    {
        Assert.Equal("PR HEAD localhost・差分 3", MainWindow.BuildDiffHeaderLabel("PR HEAD localhost", 3));
        Assert.Equal("変更前 localhost・差分なし", MainWindow.BuildDiffHeaderLabel("変更前 localhost", 0));
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
    public void BuildDocsThemeScript_Light_Sets_Data_Color_Mode_Light()
    {
        var script = MainWindow.BuildDocsThemeScript(DocsThemeMode.Light);

        Assert.Contains("data-color-mode", script, StringComparison.Ordinal);
        Assert.Contains("\"light\"", script, StringComparison.Ordinal);
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