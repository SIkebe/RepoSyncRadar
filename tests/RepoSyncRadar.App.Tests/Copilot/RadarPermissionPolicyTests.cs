using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

#pragma warning disable GHCP001 // beta.8 exposes permission decisions through experimental RPC types.

public class RadarPermissionPolicyTests
{
    private static readonly PermissionInvocation _invocation = new() { SessionId = "session-1" };
    private static readonly string _approveOnceKind = PermissionDecision.ApproveOnce().Kind;
    private static readonly string _rejectKind = PermissionDecision.Reject("test").Kind;
    private static readonly string _userNotAvailableKind = PermissionDecision.UserNotAvailable().Kind;

    private static RadarPermissionPolicy CreatePolicy(IPermissionPrompt prompt)
    {
        var options = Options.Create(new CopilotOptions
        {
            AllowedUrlHosts = ["docs.github.com", "api.github.com"],
        });
        var allowList = new UrlAllowList(options);
        return new RadarPermissionPolicy(allowList, prompt, NullLogger<RadarPermissionPolicy>.Instance);
    }

    private static PermissionRequestCustomTool NewCustomTool(string id, string name) => new()
    {
        ToolCallId = id,
        ToolName = name,
        ToolDescription = "Tool description for test.",
    };

    private static PermissionRequestRead NewRead(string id, string path) => new()
    {
        ToolCallId = id,
        Path = path,
        Intention = "Reading a fixture file for the test.",
    };

    private static PermissionRequestUrl NewUrl(string id, string url) => new()
    {
        ToolCallId = id,
        Url = url,
        Intention = "Fetching the URL for the test.",
    };

    private static PermissionRequestWrite NewWrite(string id, string fileName) => new()
    {
        ToolCallId = id,
        FileName = fileName,
        Intention = "Writing a file for the test.",
        Diff = "--- a\n+++ b\n",
        CanOfferSessionApproval = false,
    };

    private static PermissionRequestShell NewShell(string id, string command) => new()
    {
        ToolCallId = id,
        FullCommandText = command,
        Intention = "Running a shell command for the test.",
        Commands = [],
        PossiblePaths = [],
        PossibleUrls = [],
        HasWriteFileRedirection = false,
        CanOfferSessionApproval = false,
    };

    private static PermissionRequestMcp NewMcp(string id) => new()
    {
        ToolCallId = id,
        ServerName = "third-party",
        ToolName = "do_something",
        ToolTitle = "Third-party tool",
        ReadOnly = false,
    };

    [Fact]
    public async Task CustomTool_Is_Approved()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("tc-1", "radar_list_commits"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("radar_score_commit")]
    [InlineData("radar_save_review")]
    public async Task TriageWriteCustomTool_Is_Approved_Without_Prompt(string toolName)
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("tc-triage-write", toolName), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Read_Is_Approved()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewRead("tc-2", "/some/file.md"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Url_AllowListed_Is_Approved_Without_Prompt()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewUrl("tc-3", "https://docs.github.com/en/actions"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Url_NotAllowListed_Is_Approved_If_User_Confirms()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewUrl("tc-4", "https://example.com/foo"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Url_NotAllowListed_Is_DeniedByUser_If_Refused()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewUrl("tc-5", "https://example.com/foo"), _invocation);

        Assert.Equal(_rejectKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_Approved_When_User_Confirms()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewWrite("tc-6", "C:/repo/foo.md"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_DeniedByUser_When_User_Refuses()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewWrite("tc-7", "C:/repo/bar.md"), _invocation);

        Assert.Equal(_rejectKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Shell_Unknown_Is_DeniedByRules_Without_Prompt()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewShell("tc-8", "rm -rf /"), _invocation);

        Assert.Equal(_userNotAvailableKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_Kind_Is_DeniedByRules()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        // PermissionRequestMcp は本アプリでは未対応扱い。今後 MCP を許可するときに別途扱う。
        var result = await policy.HandleAsync(NewMcp("tc-9"), _invocation);

        Assert.Equal(_userNotAvailableKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }
}
#pragma warning restore GHCP001
