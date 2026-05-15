using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

/// <summary>
/// Validates that <see cref="RadarPermissionPolicy"/> still routes stronger
/// write-flavoured <see cref="PermissionRequestCustomTool"/> events through
/// <see cref="IPermissionPrompt"/> instead of auto-approving them.
/// </summary>
public sealed class PermissionFlowTests
{
    private static readonly PermissionInvocation Invocation = new() { SessionId = "session-write" };

    private static RadarPermissionPolicy CreatePolicy(IPermissionPrompt prompt)
    {
        var options = Options.Create(new CopilotOptions
        {
            AllowedUrlHosts = ["docs.github.com", "api.github.com"],
        });
        var allowList = new UrlAllowList(options);
        return new RadarPermissionPolicy(allowList, prompt, NullLogger<RadarPermissionPolicy>.Instance);
    }

    private static PermissionRequestCustomTool NewCustomTool(string toolName) => new()
    {
        ToolCallId = "tc-write",
        ToolName = toolName,
        ToolDescription = "Side-effecting tool under test.",
    };

    [Fact]
    public async Task NonTriageWriteTool_Triggers_Permission_Prompt()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("radar_post_draft"), Invocation);

        Assert.Equal(PermissionRequestResultKind.Approved, result.Kind);
        await prompt.Received(1).ConfirmAsync(
            Arg.Is<PermissionRequestCustomTool>(t => t.ToolName == "radar_post_draft"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTriageWriteTool_Denied_Returns_DeniedByUser()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("radar_post_draft"), Invocation);

        Assert.Equal(PermissionRequestResultKind.DeniedInteractivelyByUser, result.Kind);
    }
}
