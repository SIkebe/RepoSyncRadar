using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Components;

public sealed class CopilotPolicyDiagnosticsPanelTests
{
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
