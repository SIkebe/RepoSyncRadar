using GitHub.Copilot.SDK;
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
}
