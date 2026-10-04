using Microsoft.Playwright;
using Xunit;

namespace RepoSyncRadar.App.E2E.Tests;

[Trait("Category", "E2E")]
[Collection(E2ETests.Name)]
public sealed class CopilotPolicyDiagnosticsE2ETests(SeededAppHostFixture fixture)
{
    [Fact]
    public async Task Settings_Policy_Check_Is_Visible_And_Does_Not_Expose_Raw_Account_Details()
    {
        var page = await E2EPageHelpers.GetBlazorPageAsync(fixture.BlazorBrowser);
        await page.Locator("[data-testid='sidebar-settings']").ClickAsync();
        try
        {
            await page.Locator("[data-testid='settings-section-nav-automation']").ClickAsync();
            var panel = page.Locator("[data-testid='settings-copilot-policy']");
            await panel.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            var check = page.Locator("[data-testid='settings-copilot-policy-check']");
            Assert.True(await check.IsEnabledAsync());
            Assert.Equal("polite", await panel.Locator("[role='status']").GetAttributeAsync("aria-live"));
            var scope = await panel.InnerTextAsync();
            Assert.Contains("スナップショット", scope, StringComparison.Ordinal);
            Assert.Contains("権限制限は含まれず", scope, StringComparison.Ordinal);
            Assert.Empty(await panel.Locator("[role='alert']").AllAsync());
            await check.FocusAsync();
            Assert.True(await check.EvaluateAsync<bool>("el => el === document.activeElement"));
            Assert.True(await panel.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));

            await check.ClickAsync();
            await page.WaitForFunctionAsync(
                """
                () => {
                    const panel = document.querySelector('[data-testid="settings-copilot-policy"]');
                    const button = panel?.querySelector('button');
                    return button && !button.disabled
                        && (panel.querySelector('[role="alert"]')
                            || panel.querySelector('[role="status"]')?.textContent.trim());
                }
                """, null, new() { Timeout = 60000 });
            var text = await panel.InnerTextAsync();
            Assert.DoesNotContain("ghu_e2e_startup_auth_placeholder", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled method", text, StringComparison.Ordinal);
            Assert.True(await panel.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
            var artifactDirectory = Path.Combine(FindRepositoryRoot(), "artifacts", "copilot-policy");
            Directory.CreateDirectory(artifactDirectory);
            await panel.ScreenshotAsync(new() { Path = Path.Combine(artifactDirectory, "settings-policy.png") });
        }
        finally
        {
            await page.Locator("[data-testid='sidebar-settings']").ClickAsync();
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found for the policy screenshot.");
    }
}
