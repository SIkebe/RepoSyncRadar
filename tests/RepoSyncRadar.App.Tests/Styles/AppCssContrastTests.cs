using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace RepoSyncRadar.App.Tests.Styles;

public sealed partial class AppCssContrastTests
{
    [Fact]
    public void DarkTheme_InteractiveTokens_Meet_NormalTextContrast()
    {
        var variables = ReadDarkThemeVariables();

        AssertContrast("accent button", variables["--radar-accent-fg"], variables["--radar-accent-bg"]);
        AssertContrast("accent button hover", "#FFFFFF", variables["--radar-accent-hover-bg"]);
        AssertContrast("chip", variables["--radar-chip-fg"], variables["--radar-chip-bg"]);
        AssertContrast("warning", variables["--radar-warning-fg"], variables["--radar-warning-bg"]);
        AssertContrast("danger", variables["--radar-danger-fg"], variables["--radar-danger-bg"]);
        AssertContrast("success text", variables["--radar-success-fg"], variables["--radar-panel-bg"]);
    }

    [Fact]
    public void SidebarAuthState_Uses_ThemeAware_ColorTokens()
    {
        var css = ReadAppCss();

        var signedInBlock = GetRuleBlock(css, ".sidebar-auth-state.signed-in");
        var notSignedInBlock = GetRuleBlock(css, ".sidebar-auth-state.not-signed-in");
        var notConfiguredBlock = GetRuleBlock(css, ".sidebar-auth-state.not-configured") +
            GetRuleBlock(css, ".sidebar-auth-error");

        Assert.Contains("color: var(--radar-success-fg", signedInBlock, StringComparison.Ordinal);
        Assert.Contains("color: var(--radar-warning-fg", notSignedInBlock, StringComparison.Ordinal);
        Assert.Contains("color: var(--radar-danger-fg", notConfiguredBlock, StringComparison.Ordinal);
        Assert.Contains("#7d4e00", notSignedInBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#a40e26", notConfiguredBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DarkTheme_WebViewButton_Overrides_LightBackgroundAndTextTogether()
    {
        var css = ReadAppCss();

        var normalBlock = GetRuleBlock(
            css,
            ".radar-theme-dark .radar-commit-detail .file-row .open-in-webview");
        Assert.Contains("background: var(--radar-accent-bg)", normalBlock, StringComparison.Ordinal);
        Assert.Contains("border-color: var(--radar-accent-border)", normalBlock, StringComparison.Ordinal);
        Assert.Contains("color: var(--radar-accent-fg)", normalBlock, StringComparison.Ordinal);

        var hoverBlock = GetRuleBlock(
            css,
            ".radar-theme-dark .radar-commit-detail .file-row .open-in-webview:hover:not(:disabled)");
        Assert.Contains("background: var(--radar-accent-hover-bg)", hoverBlock, StringComparison.Ordinal);
        Assert.Contains("color: #fff", hoverBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DarkTheme_DetailBadges_Override_LightThemeBackgrounds()
    {
        var css = ReadAppCss();

        var badgeBlock = GetRuleBlock(
            css,
            ".radar-theme-dark .radar-commit-detail .audience-chip");
        Assert.Contains("background: var(--radar-chip-bg)", badgeBlock, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--radar-accent-border)", badgeBlock, StringComparison.Ordinal);
        Assert.Contains("color: var(--radar-chip-fg)", badgeBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceChange_Badges_Meet_NormalTextContrast()
    {
        var css = ReadAppCss();
        var lightBadge = GetExactRuleBlock(css, ".radar-commit-detail .file-change-badge-source");
        var darkBadge = GetExactRuleBlock(css, ".radar-theme-dark .radar-commit-detail .file-change-badge-source");

        Assert.Contains("background: #fff8c5", lightBadge, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color: #633c01", lightBadge, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background: #3b2e10", darkBadge, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color: #f2cc60", darkBadge, StringComparison.OrdinalIgnoreCase);
        AssertContrast("source change badge", "#633c01", "#fff8c5");
        AssertContrast("dark source change badge", "#f2cc60", "#3b2e10");
    }

    [Fact]
    public void DarkTheme_ReusableUsagePicker_Uses_ThemeAware_LabelColor()
    {
        var pickerBlock = GetExactRuleBlock(
            ReadAppCss(),
            ".radar-theme-dark .radar-commit-detail .reusable-usage-picker");

        Assert.Contains("color: var(--radar-muted-fg)", pickerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHeader_Uses_Theme_Surface_Tokens()
    {
        var headerBlock = GetRuleBlock(ReadAppCss(), ".app-header");

        Assert.Contains("background: var(--radar-panel-bg)", headerBlock, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--radar-border-subtle)", headerBlock, StringComparison.Ordinal);
        Assert.Contains("color: var(--radar-fg)", headerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolbarIconOnly_Is_Compact_And_Centered()
    {
        var iconButtonBlock = GetExactRuleBlock(ReadAppCss(), ".toolbar-button.icon-only");

        Assert.Contains("align-items: center", iconButtonBlock, StringComparison.Ordinal);
        Assert.Contains("display: inline-flex", iconButtonBlock, StringComparison.Ordinal);
        Assert.Contains("justify-content: center", iconButtonBlock, StringComparison.Ordinal);
        Assert.Contains("padding: 0", iconButtonBlock, StringComparison.Ordinal);
        Assert.Contains("width: 1.9rem", iconButtonBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPanel_Prevents_HeaderActions_And_LongPath_Overflow()
    {
        var css = ReadAppCss();

        var buttonBlock = GetExactRuleBlock(css, ".toolbar-button");
        Assert.Contains("white-space: nowrap", buttonBlock, StringComparison.Ordinal);

        var headerContentBlock = GetRuleBlock(css, ".app-settings-header > div,") +
            GetRuleBlock(css, ".settings-subheader > div");
        Assert.Contains("min-width: 0", headerContentBlock, StringComparison.Ordinal);

        var settingsActionsBlock = GetRuleBlock(css, ".app-settings-header > .toolbar-button,") +
            GetRuleBlock(css, ".settings-actions");
        Assert.Contains("flex: 0 0 auto", settingsActionsBlock, StringComparison.Ordinal);

        var pathBlock = GetRuleBlock(css, ".settings-muted[data-testid=\"settings-local-appsettings-path\"]");
        Assert.Contains("overflow-wrap: anywhere", pathBlock, StringComparison.Ordinal);

        var workbenchBlock = GetRuleBlock(css, ".radar-workbench");
        Assert.Contains("overflow-x: hidden", workbenchBlock, StringComparison.Ordinal);

        var settingsFieldLabelBlock = GetRuleBlock(css, ".local-settings-field span,") +
            GetRuleBlock(css, ".local-settings-check span");
        Assert.Contains("overflow-wrap: anywhere", settingsFieldLabelBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalSettingsGrid_Does_Not_Force_Horizontal_Overflow_On_Narrow_Workbench()
    {
        var css = ReadAppCss();
        var panelBlock = GetRuleBlock(css, ".app-settings-panel");
        var errorBlock = GetRuleBlock(css, ".settings-error");
        var gridBlock = GetRuleBlock(css, ".local-settings-grid");
        var groupBlock = GetRuleBlock(css, ".local-settings-group");
        var copilotGroupBlock = GetExactRuleBlock(css, ".local-settings-group-copilot");
        var docsRepositoryGroupBlock = GetExactRuleBlock(css, ".local-settings-group-docs-repository");
        var contextTierBlock = GetExactRuleBlock(css, ".local-settings-field .context-tier-select");
        var webViewAllowedHostsBlock = GetExactRuleBlock(css, ".local-settings-field textarea[data-testid=\"settings-webview-allowed-hosts\"]");

        Assert.Contains("min-width: 0", panelBlock, StringComparison.Ordinal);
        Assert.Matches(@"\.settings-section\s*\{[^}]*min-width:\s*0", css);
        Assert.Matches(
            @"\.app-settings-header p,\s*\.settings-muted\s*\{[^}]*overflow-wrap:\s*anywhere",
            css);
        Assert.Contains("overflow-wrap: anywhere", errorBlock, StringComparison.Ordinal);
        Assert.Contains("repeat(12, minmax(0, 1fr))", gridBlock, StringComparison.Ordinal);
        Assert.Contains("min-width: 0", groupBlock, StringComparison.Ordinal);
        Assert.Contains("grid-column: span 5", copilotGroupBlock, StringComparison.Ordinal);
        Assert.Contains("grid-column: span 4", docsRepositoryGroupBlock, StringComparison.Ordinal);
        Assert.Matches(
            @"@media\s*\(max-width:\s*1180px\)\s*\{[\s\S]*?\.local-settings-grid\s*\{[^}]*grid-template-columns:\s*repeat\(6,\s*minmax\(0,\s*1fr\)\)",
            css);
        Assert.Contains("padding-right: 2rem", contextTierBlock, StringComparison.Ordinal);
        Assert.Contains("min-width: min(14rem, 100%)", contextTierBlock, StringComparison.Ordinal);
        Assert.Contains("min-height: 9rem", webViewAllowedHostsBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ReducedMotion_Disables_NonEssential_Animations_And_Transitions()
    {
        var css = ReadAppCss();

        Assert.Matches(
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{[\s\S]*?\.preview-spinner\s*\{[^}]*animation:\s*none",
            css);
        Assert.Matches(
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{[\s\S]*?\.radar-sidebar-resizer::before",
            css);
        Assert.Matches(
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{[\s\S]*?\.reusable-usage-picker select::picker-icon\s*\{[^}]*transition:\s*none",
            css);
        Assert.Matches(
            @"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{[\s\S]*?transition:\s*none",
            css);
    }

    [Fact]
    public void AppOwnedControls_Define_FocusVisible_And_ForcedColors_States()
    {
        var css = ReadAppCss();
        var focusBlock = GetRuleBlock(css, ".toolbar-button:focus-visible,");

        Assert.Contains(".review-button:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains(".drafts-jump-link:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("outline: 2px solid var(--radar-focus-ring", focusBlock, StringComparison.Ordinal);
        Assert.Contains("box-shadow: 0 0 0 4px var(--radar-focus-shadow", focusBlock, StringComparison.Ordinal);

        Assert.Matches(
            @"@media\s*\(forced-colors:\s*active\)\s*\{[\s\S]*?--radar-focus-ring:\s*Highlight",
            css);
        Assert.Matches(
            @"@media\s*\(forced-colors:\s*active\)\s*\{[\s\S]*?\.toolbar-button",
            css);
        Assert.Contains("background: Canvas", css, StringComparison.Ordinal);
        Assert.Contains("color: CanvasText", css, StringComparison.Ordinal);
        Assert.Contains("border-color: ButtonText", css, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ReadDarkThemeVariables()
    {
        var darkThemeBlock = GetRuleBlock(ReadAppCss(), ".radar-shell.radar-theme-dark");
        return CssVariableRegex()
            .Matches(darkThemeBlock)
            .ToDictionary(
                match => match.Groups["name"].Value,
                match => match.Groups["value"].Value,
                StringComparer.Ordinal);
    }

    private static string GetRuleBlock(string css, string selector)
    {
        var match = Regex.Match(
            css,
            $"{Regex.Escape(selector)}[^{{]*\\{{(?<block>[^}}]*)\\}}",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"CSS rule for '{selector}' was not found.");
        return match.Groups["block"].Value;
    }

    private static string GetExactRuleBlock(string css, string selector)
    {
        var match = Regex.Match(
            css,
            $"{Regex.Escape(selector)}\\s*\\{{(?<block>[^}}]*)\\}}",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"CSS rule for '{selector}' was not found.");
        return match.Groups["block"].Value;
    }

    private static void AssertContrast(string name, string foreground, string background)
    {
        const double minimumNormalTextContrast = 4.5;
        var ratio = ContrastRatio(foreground, background);
        Assert.True(
            ratio >= minimumNormalTextContrast,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name} contrast must be at least {minimumNormalTextContrast}:1, but was {ratio:0.00}:1 ({foreground} on {background})."));
    }

    private static double ContrastRatio(string foreground, string background)
    {
        var foregroundLuminance = RelativeLuminance(foreground);
        var backgroundLuminance = RelativeLuminance(background);
        var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string hexColor)
    {
        var color = hexColor.Trim().TrimStart('#');
        if (color.Length == 3)
        {
            color = string.Concat(color.Select(static character => new string(character, 2)));
        }

        var red = LinearizedColorChannel(color[..2]);
        var green = LinearizedColorChannel(color[2..4]);
        var blue = LinearizedColorChannel(color[4..6]);
        return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    }

    private static double LinearizedColorChannel(string hexChannel)
    {
        var channel = int.Parse(hexChannel, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static string ReadAppCss()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RepoSyncRadar.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "src", "RepoSyncRadar.App", "wwwroot", "css", "app.css"));
    }

    [GeneratedRegex(@"(?<name>--[a-z0-9-]+):\s*(?<value>#[0-9a-fA-F]{6})", RegexOptions.CultureInvariant)]
    private static partial Regex CssVariableRegex();
}
