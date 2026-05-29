using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

#pragma warning disable GHCP001 // beta.9 exposes permission decisions through experimental RPC types.

public class SessionConfigBuilderTests
{
    [Fact]
    public void Build_For_Triage_Produces_Append_Mode_Streaming_With_Configured_Model()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-5",
            Streaming = true,
            EnableSessionTelemetry = false,
            AllowedUrlHosts = ["docs.github.com"],
        };
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> handler = (_, _) =>
            Task.FromResult(PermissionDecision.ApproveOnce());

        var config = SessionConfigBuilder.Build(
            SessionPurpose.Triage,
            Options.Create(copilot),
            handler);

        Assert.Equal("gpt-5", config.Model);
        Assert.True(config.Streaming);
        Assert.False(config.EnableSessionTelemetry);
        Assert.NotNull(config.SystemMessage);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
        Assert.False(string.IsNullOrWhiteSpace(config.SystemMessage.Content));
        Assert.Same(handler, config.OnPermissionRequest);
        Assert.True(config.SkipCustomInstructions);
        Assert.True(config.CustomAgentsLocalOnly);
        Assert.False(config.CoauthorEnabled);
        Assert.False(config.ManageScheduleEnabled);
    }

    [Fact]
    public void Build_With_Tools_Registers_And_Restricts_Available_Tools()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-5",
            Streaming = true,
            AllowedUrlHosts = ["docs.github.com"],
        };
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> handler = (_, _) =>
            Task.FromResult(PermissionDecision.ApproveOnce());
        var tool = AIFunctionFactory.Create(
            () => "ok",
            new AIFunctionFactoryOptions
            {
                Name = "radar_test",
                Description = "test tool",
            });

        var config = SessionConfigBuilder.Build(
            SessionPurpose.Triage,
            Options.Create(copilot),
            handler,
            tools: [tool]);

        Assert.NotNull(config.Tools);
        Assert.Single(config.Tools);
        Assert.Equal("radar_test", config.Tools.Single().Name);
        Assert.Equal(["custom:radar_test"], config.AvailableTools);
    }
}
#pragma warning restore GHCP001
