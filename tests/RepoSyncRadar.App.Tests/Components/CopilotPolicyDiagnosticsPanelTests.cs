using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Copilot;
using GitHub.Copilot.Rpc;
using System.Text.Json;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class CopilotPolicyDiagnosticsPanelTests
{
    [Fact]
    public void Inventory_Renders_Semantic_Localized_Tables_And_Separate_App_Restrictions()
    {
        var service = Substitute.For<ICopilotPolicyDiagnostics>();
        service.ResolveAsync(Arg.Any<CancellationToken>()).Returns(CreateInventorySample());
        using var provider = BuildServices(service);
        using var ctx = new BunitContext();
        var cut = ctx.Render<CopilotPolicyDiagnosticsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(provider).Add(x => x.SignedIn, true));

        cut.Find("[data-testid=\"settings-copilot-policy-check\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(7, cut.FindAll("table").Count);
            Assert.All(cut.FindAll("table"), table =>
            {
                Assert.NotEmpty(table.QuerySelector("caption")!.TextContent);
                Assert.Equal(4, table.QuerySelectorAll("thead th[scope='col']").Length);
                Assert.NotEmpty(table.QuerySelectorAll("tbody th[scope='row']"));
            });
            Assert.Contains("intelligence", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("交差適用", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("ポリシー未確定による隔離", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("未報告", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("対象外", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("内容は非表示", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("実セッションでの適用確認ではありません", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Copilot.Policy.", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("private", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<table", cut.Find("[role='status']").InnerHtml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task App_Restrictions_Are_Visible_Without_Policy_Network_Or_SignIn()
    {
        var service = Substitute.For<ICopilotPolicyDiagnostics>();
        using var provider = BuildServices(service);
        using var ctx = new BunitContext();
        var cut = ctx.Render<CopilotPolicyDiagnosticsPanel>(p => p.AddCascadingValue<IServiceProvider>(provider));
        var app = cut.Find("[data-testid=\"settings-policy-app-restrictions\"]");
        Assert.Contains("shell", app.TextContent, StringComparison.Ordinal);
        Assert.Contains("権限確認の迂回禁止", app.TextContent, StringComparison.Ordinal);
        await service.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
    }

#pragma warning disable GHCP001 // Synthetic SDK result exercises the real safe mapper, never an account policy.
    public static CopilotPolicySnapshot CreateInventorySample()
        => CopilotPolicyDiagnostics.FromResult(JsonSerializer.Deserialize<ManagedSettingsResolveResult>("""
            {
              "resolved":{
                "deviceManaged":true,"serverManaged":true,"failClosed":false,"bypassPermissionsDisabled":true,
                "permissionsAllowIntersected":true,"sandboxEnabledByUndeterminedPolicy":false,
                "clientManaged":false,"policyHelperManaged":false,"source":"mixed",
                "managedKeys":["model","autoTier","effortLevel","permissions","sandbox","allowedMcpServers","deniedMcpServers",
                    "strictPluginOnlyCustomization","allowManagedMcpServersOnly","allowManagedHooksOnly","features","enabledPlugins",
                    "extraKnownMarketplaces","strictKnownMarketplaces","remoteControl","forceLoginOrgs","forceRemoteSettingsRefresh",
                    "policyHelper","policyHelperFailureMode","telemetry"],
                "settings":{
                  "model":"gpt-5.5","autoTier":"intelligence","effortLevel":"high",
                  "permissions":{"deny":["shell","read:/private/file"],"ask":["write"],"disableBypassPermissionsMode":"disable"},
                  "sandbox":{"enabled":true,"allowBypass":false,"auth":{"git":true,"gh":false},"userPolicy":{"filesystem":{"private":"private"}}},
                  "allowedMcpServers":[{"serverUrl":"https://private.example"}],"deniedMcpServers":[],
                  "strictPluginOnlyCustomization":["skills","hooks"],"allowManagedMcpServersOnly":true,
                  "allowManagedHooksOnly":false,"features":{"private-feature":false},"enabledPlugins":{"private-plugin":true},
                  "extraKnownMarketplaces":{"private-market":{"source":{"source":"git","url":"https://private.example"},"autoUpdate":true}},
                  "strictKnownMarketplaces":[],
                  "remoteControl":{"mode":"requireSSO","githubDotComOrganizations":["private-org"]},
                  "forceLoginOrgs":["private-org"],"forceRemoteSettingsRefresh":true,
                  "policyHelper":{"path":"C:\\private\\helper.exe","args":["private-token"],"timeoutMs":5000},
                  "policyHelperFailureMode":"failClosed",
                  "telemetry":{"enabled":false,"protocol":"grpc","headers":{"Authorization":"private-token"},
                      "capture":{"identity":false,"prompts":false,"responses":false,"toolArguments":false,"toolOutput":false,"policyDetail":false}}
                }
              },
              "values":{"model":"gpt-5.5","autoTier":"intelligence"},
              "meta":{"model":{"overridable":true,"source":"server"},"autoTier":{"overridable":false,"source":"device"}},
              "layers":[
                  {"source":"server","settings":{"permissions":{"allow":["read","write"]}}},
                  {"source":"device","settings":{"permissions":{"allow":["read"],"deny":["shell"]}}}
              ],
              "diagnostics":[{"message":"private refresh served from cache","path":"permissions.allow","severity":"warning"}]
            }
            """)!);
#pragma warning restore GHCP001

    [Fact]
    public async Task Check_Is_OnDemand_And_Shows_Locked_Model_And_Snapshot_Scope()
    {
        var service = Substitute.For<ICopilotPolicyDiagnostics>();
        service.ResolveAsync(Arg.Any<CancellationToken>()).Returns(
            new CopilotPolicySnapshot(true, true, true, true, "gpt-5.5", true));
        using var provider = BuildServices(service);
        using var ctx = new BunitContext();
        var cut = ctx.Render<CopilotPolicyDiagnosticsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(provider).Add(x => x.SignedIn, true));
        await service.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());

        cut.Find("[data-testid=\"settings-copilot-policy-check\"]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("gpt-5.5", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("ポリシーで固定", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("fail-closed", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("セッションに注入する権限制限は含まれず", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("キャッシュ", cut.Markup, StringComparison.Ordinal);
            Assert.Equal("polite", cut.Find("[role=\"status\"]").GetAttribute("aria-live"));
        });
        await service.Received(1).ResolveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignedOut_Disables_Check_Without_Calling_Service()
    {
        var service = Substitute.For<ICopilotPolicyDiagnostics>();
        using var provider = BuildServices(service);
        using var ctx = new BunitContext();
        var cut = ctx.Render<CopilotPolicyDiagnosticsPanel>(p => p.AddCascadingValue<IServiceProvider>(provider));

        Assert.True(cut.Find("button").HasAttribute("disabled"));
        Assert.Contains("サインイン", cut.Markup, StringComparison.Ordinal);
        await service.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Failure_Has_Recovery_And_Does_Not_Expose_Sdk_Details()
    {
        var service = Substitute.For<ICopilotPolicyDiagnostics>();
        service.ResolveAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CopilotPolicySnapshot>(new InvalidOperationException("private-account-policy-token")));
        using var provider = BuildServices(service);
        using var ctx = new BunitContext();
        var cut = ctx.Render<CopilotPolicyDiagnosticsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(provider).Add(x => x.SignedIn, true));

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("再試行", cut.Find("[role=\"alert\"]").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("private-account", cut.Markup, StringComparison.Ordinal);
            Assert.False(cut.Find("button").HasAttribute("disabled"));
        });
    }

    [Fact]
    public void Account_Change_Cancels_And_Discards_InFlight_Result()
    {
        var service = Substitute.For<ICopilotPolicyDiagnostics>();
        var pending = new TaskCompletionSource<CopilotPolicySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        service.ResolveAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            return pending.Task;
        });
        using var provider = BuildServices(service);
        using var ctx = new BunitContext();
        var cut = ctx.Render<CopilotPolicyDiagnosticsPanel>(p => p
            .AddCascadingValue<IServiceProvider>(provider).Add(x => x.SignedIn, true));
        cut.Find("button").Click();
        Assert.True(cut.Find("button").HasAttribute("disabled"));

        cut.Render(p => p.Add(x => x.AuthStateVersion, 1));
        pending.SetResult(new(false, true, false, false, "stale-model", true));

        cut.WaitForAssertion(() =>
        {
            Assert.True(observed.IsCancellationRequested);
            Assert.DoesNotContain("stale-model", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[role=\"alert\"]"));
            Assert.False(cut.Find("button").HasAttribute("disabled"));
        });
    }

    private static ServiceProvider BuildServices(ICopilotPolicyDiagnostics service)
        => new ServiceCollection().AddSingleton(service).AddLogging()
            .AddLocalization(o => o.ResourcesPath = "Resources").BuildServiceProvider();
}
