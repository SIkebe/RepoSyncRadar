using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

#pragma warning disable GHCP001 // Copilot SDK permission and MCP auth decisions remain experimental.

public class SessionConfigBuilderTests
{
    [Fact]
    public async Task Build_For_Triage_Produces_Append_Mode_Streaming_With_Configured_Model()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-5.6-luna",
            ReasoningEffort = "high",
            ContextTier = "long_context",
            Streaming = true,
            EnableWebSocketResponses = false,
            EnableSessionTelemetry = false,
            AllowedUrlHosts = ["docs.github.com"],
        };
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> handler = (_, _) =>
            Task.FromResult(PermissionDecision.ApproveOnce());

        var config = SessionConfigBuilder.Build(
            SessionPurpose.Triage,
            Options.Create(copilot),
            handler);

        Assert.Equal("gpt-5.6-luna", config.Model);
        Assert.Equal("high", config.ReasoningEffort);
        Assert.Equal(ContextTier.LongContext, config.ContextTier);
        Assert.True(config.Streaming);
        Assert.False(config.EnableSessionTelemetry);
        Assert.False(config.EnableFileChangeTracking);
        Assert.False(config.EnableSessionStore);
        Assert.False(config.EnableExperimentalMode);
        Assert.NotNull(config.Memory);
        Assert.False(config.Memory!.Enabled);
        Assert.NotNull(config.ToolSearch);
        Assert.Equal(false, config.ToolSearch!.Enabled);
        Assert.NotNull(config.Capi);
        Assert.False(config.Capi!.EnableWebSocketResponses.GetValueOrDefault());
        Assert.NotNull(config.SystemMessage);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
        Assert.False(string.IsNullOrWhiteSpace(config.SystemMessage.Content));
        Assert.Same(handler, config.OnPermissionRequest);
        Assert.True(config.SkipCustomInstructions);
        Assert.True(config.CustomAgentsLocalOnly);
        Assert.False(config.CoauthorEnabled);
        Assert.False(config.ManageScheduleEnabled);
        Assert.NotNull(config.ManagedSettings);
        Assert.NotNull(config.ManagedSettings!.Permissions);
        Assert.Equal(
            DisableBypassPermissionsMode.Disable,
            config.ManagedSettings.Permissions!.DisableBypassPermissionsMode);
        Assert.Equal(["shell"], config.ManagedSettings.Permissions.Deny);
        Assert.Equal(McpOAuthTokenStorageMode.InMemory, config.McpOAuthTokenStorage);
        Assert.NotNull(config.OnMcpAuthRequest);
        var mcpAuthResult = await config.OnMcpAuthRequest!(new McpAuthContext());
        Assert.NotNull(mcpAuthResult);
        Assert.True(mcpAuthResult!.Cancelled);
        Assert.Null(mcpAuthResult.Token);
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

    [Fact]
    public void Build_For_Adoption_Restricts_Fetching_To_Radar_Tool()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-5.6-luna",
            AllowedUrlHosts = ["docs.github.com"],
        };
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> handler = (_, _) =>
            Task.FromResult(PermissionDecision.ApproveOnce());
        var tool = AIFunctionFactory.Create(
            () => "ok",
            new AIFunctionFactoryOptions
            {
                Name = "radar_fetch_rendered",
                Description = "test tool",
            });

        var config = SessionConfigBuilder.Build(
            SessionPurpose.Adoption,
            Options.Create(copilot),
            handler,
            tools: [tool]);

        Assert.Equal(["custom:radar_fetch_rendered"], config.AvailableTools);
        Assert.Contains("use radar_fetch_rendered", config.SystemMessage!.Content, StringComparison.Ordinal);
        Assert.Contains("never use a built-in URL fetch tool", config.SystemMessage.Content, StringComparison.Ordinal);
    }
}
#pragma warning restore GHCP001
