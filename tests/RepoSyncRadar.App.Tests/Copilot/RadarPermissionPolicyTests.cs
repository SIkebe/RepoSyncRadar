using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

#pragma warning disable GHCP001 // Copilot SDK permission decisions remain experimental.

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

    private static PermissionRequestCustomTool NewCustomTool(
        string id,
        string name,
        bool? managedApprovalRequired = null) => new()
    {
        ToolCallId = id,
        ToolName = name,
        ToolDescription = "Tool description for test.",
        ManagedApprovalRequired = managedApprovalRequired,
    };

    private static PermissionRequestRead NewRead(string id, string path, bool? managedApprovalRequired = null) => new()
    {
        ToolCallId = id,
        Path = path,
        Intention = "Reading a fixture file for the test.",
        ManagedApprovalRequired = managedApprovalRequired,
    };

    private static PermissionRequestUrl NewUrl(string id, string url, bool? managedApprovalRequired = null) => new()
    {
        ToolCallId = id,
        Url = url,
        Intention = "Fetching the URL for the test.",
        ManagedApprovalRequired = managedApprovalRequired,
    };

    private static PermissionRequestWrite NewWrite(string id, string fileName) => new()
    {
        ToolCallId = id,
        FileName = fileName,
        Intention = "Writing a file for the test.",
        Diff = "--- a\n+++ b\n",
        CanOfferSessionApproval = false,
    };

    private static PermissionRequestShell NewShell(
        string id,
        string command,
        bool? managedApprovalRequired = null) => new()
    {
        ToolCallId = id,
        FullCommandText = command,
        Intention = "Running a shell command for the test.",
        Commands = [],
        PossiblePaths = [],
        PossibleUrls = [],
        HasWriteFileRedirection = false,
        CanOfferSessionApproval = false,
        ManagedApprovalRequired = managedApprovalRequired,
    };

    private static PermissionRequestMcp NewMcp(string id, bool? managedApprovalRequired = null) => new()
    {
        ToolCallId = id,
        ServerName = "third-party",
        ToolName = "do_something",
        ToolTitle = "Third-party tool",
        ReadOnly = false,
        ManagedApprovalRequired = managedApprovalRequired,
    };

    [Fact]
    public async Task CustomTool_Is_Approved()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("tc-1", "radar_list_commits"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        AssertDecisionContext(
            result,
            PermissionDecisionOutcome.AutoApproved,
            PermissionDecisionSource.HostPolicy);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScoreCommit_Is_Approved_Without_Prompt()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("tc-triage-write", "radar_score_commit"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Managed_ScoreCommit_Is_Prompted_Instead_Of_AutoApproved()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(
            NewCustomTool("tc-managed-triage-write", "radar_score_commit", managedApprovalRequired: true),
            _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveReview_Is_Prompted_Because_Final_Review_Decisions_Are_User_Owned()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewCustomTool("tc-save-review", "radar_save_review"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
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
    public async Task Managed_Read_Is_Prompted_Instead_Of_AutoApproved()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(
            NewRead("tc-managed-read", "/some/file.md", managedApprovalRequired: true),
            _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Managed_Settings_Do_Not_Force_Prompt_For_Unflagged_Read()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);
        var invocation = new PermissionInvocation
        {
            SessionId = "session-managed",
            ManagedSettingsEnabled = true,
        };

        var result = await policy.HandleAsync(NewRead("tc-managed-session", "/some/file.md"), invocation);

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
    public async Task Managed_AllowListedUrl_Is_Prompted_Instead_Of_AutoApproved()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(
            NewUrl("tc-managed-url", "https://docs.github.com/en/actions", managedApprovalRequired: true),
            _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        await prompt.Received(1).ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Url_NotAllowListed_Is_Approved_If_User_Confirms()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        prompt.ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(NewUrl("tc-4", "https://example.com/foo"), _invocation);

        Assert.Equal(_approveOnceKind, result.Kind);
        AssertDecisionContext(
            result,
            PermissionDecisionOutcome.PromptedUser,
            PermissionDecisionSource.HumanResponse);
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
        AssertDecisionContext(
            result,
            PermissionDecisionOutcome.PromptedUser,
            PermissionDecisionSource.HumanResponse);
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
        AssertDecisionContext(
            result,
            PermissionDecisionOutcome.AutopilotDenied,
            PermissionDecisionSource.HostPolicy);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Managed_Shell_Remains_DeniedByRules_Without_Prompt()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(
            NewShell("tc-managed-shell", "rm -rf /", managedApprovalRequired: true),
            _invocation);

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

    [Fact]
    public async Task Managed_Unknown_Kind_Remains_DeniedByRules()
    {
        var prompt = Substitute.For<IPermissionPrompt>();
        var policy = CreatePolicy(prompt);

        var result = await policy.HandleAsync(
            NewMcp("tc-managed-mcp", managedApprovalRequired: true),
            _invocation);

        Assert.Equal(_userNotAvailableKind, result.Kind);
        await prompt.DidNotReceive().ConfirmAsync(Arg.Any<PermissionRequest>(), Arg.Any<CancellationToken>());
    }

    private static void AssertDecisionContext(
        PermissionDecision decision,
        PermissionDecisionOutcome outcome,
        PermissionDecisionSource source)
    {
        Assert.NotNull(decision.DecisionContext);
        Assert.Equal(outcome, decision.DecisionContext.Outcome);
        Assert.Equal(source, decision.DecisionContext.Source);
        Assert.Equal(PermissionDecisionSurface.Sdk, decision.DecisionContext.Surface);
    }
}
#pragma warning restore GHCP001
