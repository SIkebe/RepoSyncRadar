using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

#pragma warning disable MCPSDK001 // Copilot SDK permission and MCP auth decisions remain experimental.

public class SessionConfigBuilderTests
{
    [Fact]
    public async Task Build_For_Triage_Produces_Append_Mode_Streaming_With_Configured_Model()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-6-luna",
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

        Assert.Equal("gpt-6-luna", config.Model);
        Assert.Equal("high", config.Inference.ReasoningEffort);
        Assert.Equal(ContextTier.LongContext, config.Inference.ContextTier);
        Assert.True(config.Events.Streaming);
        Assert.False(config.GitHub.TelemetryEnabled);
        Assert.False(config.Filesystem.TrackFileChanges);
        Assert.False(config.State.EnableCrossSessionStore);
        Assert.False(config.Experiments.EnableExperimentalMode);
        Assert.False(config.Skills.Enabled);
        Assert.NotNull(config.Skills.IncludedBuiltinSkills);
        Assert.Empty(config.Skills.IncludedBuiltinSkills);
        Assert.NotNull(config.State.Memory);
        Assert.False(config.State.Memory!.Enabled);
        Assert.NotNull(config.Tools.Search);
        Assert.Equal(false, config.Tools.Search!.Enabled);
        Assert.NotNull(config.Tools.AvailableTools);
        Assert.Empty(config.Tools.AvailableTools);
        Assert.NotNull(config.GitHub.Capi);
        Assert.False(config.GitHub.Capi!.EnableWebSocketResponses.GetValueOrDefault());
        Assert.NotNull(config.SystemMessage);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
        Assert.False(string.IsNullOrWhiteSpace(config.SystemMessage.Content));
        Assert.Same(handler, config.Permissions.OnRequest);
        Assert.True(config.Instructions.SkipCustomInstructions);
        Assert.True(config.Agents.LocalDiscoveryOnly);
        Assert.False(config.Git.CoauthorEnabled);
        Assert.False(config.Tools.ManageScheduleEnabled);
        Assert.False(config.Hooks.EnableRepositoryHooks);
        Assert.Null(config.Filesystem.TrustWorkingDirectory);
        Assert.Null(config.Filesystem.AdditionalDirectories);
        Assert.NotNull(config.Policy.ManagedSettings);
        Assert.NotNull(config.Policy.ManagedSettings!.Permissions);
        Assert.Equal(
            DisableBypassPermissionsModes.Disable,
            config.Policy.ManagedSettings.Permissions!.DisableBypassPermissionsMode);
        Assert.True(config.Policy.ManagedSettings.Permissions.DisableAssistedPermissionsMode);
        Assert.Equal(["shell"], config.Policy.ManagedSettings.Permissions.Deny);
        Assert.Equal(McpOAuthTokenStorageMode.InMemory, config.Mcp.OauthTokenStorage);
        Assert.NotNull(config.Mcp.OnAuthRequest);
        var mcpAuthResult = await config.Mcp.OnAuthRequest!(new McpAuthContext());
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

        Assert.NotNull(config.Tools.Definitions);
        Assert.Single(config.Tools.Definitions);
        Assert.Equal("radar_test", config.Tools.Definitions!.Single().Name);
        Assert.Equal(["custom:radar_test"], config.Tools.AvailableTools);
    }

    [Theory]
    [InlineData(SessionPurpose.Triage)]
    [InlineData(SessionPurpose.Adoption)]
    [InlineData(SessionPurpose.Maintenance)]
    public void Build_Disables_All_Skills_For_Every_Purpose(SessionPurpose purpose)
    {
        var config = SessionConfigBuilder.Build(
            purpose,
            Options.Create(new CopilotOptions()),
            static (_, _) => Task.FromResult(PermissionDecision.Reject("No tools needed.")));

        Assert.False(config.Skills.Enabled);
        Assert.Empty(config.Skills.IncludedBuiltinSkills!);
        Assert.NotNull(config.Agents.IncludedBuiltInAgents);
        Assert.Empty(config.Agents.IncludedBuiltInAgents);
        Assert.Empty(config.Clone().Agents.IncludedBuiltInAgents!);
        Assert.Null(config.Skills.Provider);
        Assert.Null(config.Interaction.OnShellAuthenticationRequest);
        Assert.False(config.Clone().Skills.Enabled);
        Assert.True(config.Policy.ManagedSettings!.Permissions!.DisableAssistedPermissionsMode);
    }

    [Fact]
    public void Build_For_Adoption_Restricts_Fetching_To_Radar_Tool()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-6-luna",
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

        Assert.Equal(["custom:radar_fetch_rendered"], config.Tools.AvailableTools);
        Assert.Contains("use radar_fetch_rendered", config.SystemMessage!.Content, StringComparison.Ordinal);
        Assert.Contains("never use a built-in URL fetch tool", config.SystemMessage.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SessionPurpose.Triage)]
    [InlineData(SessionPurpose.Adoption)]
    [InlineData(SessionPurpose.Maintenance)]
    public void Build_Uses_Manual_Permissions_Without_Auto_Recovery_For_Every_Purpose(SessionPurpose purpose)
    {
        var config = SessionConfigBuilder.Build(
            purpose,
            Options.Create(new CopilotOptions()),
            static (_, _) => Task.FromResult(PermissionDecision.Reject("No tools needed.")));

        Assert.Equal(PermissionMode.Manual, config.Permissions.Mode);
        Assert.Equal(PermissionMode.Manual, config.Clone().Permissions.Mode);
        Assert.False(config.Interaction.AutoModeRecovery);
        Assert.False(config.Clone().Interaction.AutoModeRecovery);
        Assert.Null(config.Interaction.OnAutoModeSwitchRequest);
        Assert.Null(config.State.Memory!.StoreMemory);
        Assert.Null(config.Inference.AllowMultipleToolCalls);
    }

    [Theory]
    [InlineData(SessionPurpose.Triage)]
    [InlineData(SessionPurpose.Adoption)]
    [InlineData(SessionPurpose.Maintenance)]
    public void Build_Does_Not_Exclude_Managed_Policy_For_Any_Purpose(SessionPurpose purpose)
    {
        var config = SessionConfigBuilder.Build(
            purpose,
            Options.Create(new CopilotOptions()),
            static (_, _) => Task.FromResult(PermissionDecision.Reject("No tools needed.")));

        Assert.Null(config.Policy.ExcludedManagedSettings);
        Assert.Null(config.Clone().Policy.ExcludedManagedSettings);
    }
}
#pragma warning restore MCPSDK001
