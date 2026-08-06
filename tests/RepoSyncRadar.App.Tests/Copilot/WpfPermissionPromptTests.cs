using GitHub.Copilot;
using RepoSyncRadar.App.Copilot;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class WpfPermissionPromptTests
{
    [Fact]
    public void AllowsPersistentUrlApproval_ForManagedUrl_ReturnsFalse()
    {
        var request = new PermissionRequestUrl
        {
            ToolCallId = "tc-managed-url",
            Url = "https://docs.github.com/en/copilot",
            Intention = "Read the documentation before summarizing it.",
            ManagedApprovalRequired = true,
        };

        Assert.False(WpfPermissionPrompt.AllowsPersistentUrlApproval(request));
    }

    [Fact]
    public void FormatPrompt_ForManagedRead_ShowsPathAndIntention()
    {
        var request = new PermissionRequestRead
        {
            ToolCallId = "tc-managed-read",
            Path = "C:/repo/docs/example.md",
            Intention = "Read the documentation before summarizing it.",
            ManagedApprovalRequired = true,
        };

        var (caption, message) = WpfPermissionPrompt.FormatPrompt(request);

        Assert.Equal("RepoSyncRadar — Allow file read?", caption);
        Assert.Equal(
            "Copilot wants to read:\n  C:/repo/docs/example.md\n\n"
            + "Intent: Read the documentation before summarizing it.\n\nAllow this read?",
            message);
    }
}
