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
    public void AppHeader_Uses_Theme_Surface_Tokens()
    {
        var headerBlock = GetRuleBlock(ReadAppCss(), ".app-header");

        Assert.Contains("background: var(--radar-panel-bg)", headerBlock, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--radar-border-subtle)", headerBlock, StringComparison.Ordinal);
        Assert.Contains("color: var(--radar-fg)", headerBlock, StringComparison.Ordinal);
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