using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public class SessionConfigBuilderTests
{
    [Fact]
    public void Build_For_Triage_Produces_Append_Mode_Streaming_With_Configured_Model()
    {
        var copilot = new CopilotOptions
        {
            DefaultModel = "gpt-5",
            Streaming = true,
            AllowedUrlHosts = ["docs.github.com"],
        };
        PermissionRequestHandler handler = (_, _) =>
            Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });

        var config = SessionConfigBuilder.Build(
            SessionPurpose.Triage,
            Options.Create(copilot),
            handler);

        Assert.Equal("gpt-5", config.Model);
        Assert.True(config.Streaming);
        Assert.NotNull(config.SystemMessage);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
        Assert.False(string.IsNullOrWhiteSpace(config.SystemMessage.Content));
        Assert.Same(handler, config.OnPermissionRequest);
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
        PermissionRequestHandler handler = (_, _) =>
            Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });
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
        Assert.Equal(["radar_test"], config.AvailableTools);
    }
}
