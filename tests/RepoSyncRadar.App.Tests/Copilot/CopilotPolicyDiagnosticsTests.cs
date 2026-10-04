using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

#pragma warning disable GHCP001 // Test the public policy snapshot contract.
public sealed class CopilotPolicyDiagnosticsTests
{
    [Fact]
    public void Maps_Model_Lock_And_Failure_Without_Raw_Policy_Or_Account()
    {
        var result = JsonSerializer.Deserialize<ManagedSettingsResolveResult>("""
            {
              "account":"private-account",
              "resolved":{"deviceManaged":true,"serverManaged":true,"failClosed":true,
                          "settings":{"private":"raw-policy"}},
              "values":{"model":"gpt-5.5"},
              "meta":{"model":{"overridable":false,"source":"server"}},
              "diagnostics":[{"message":"private-policy-error","path":"secret","severity":"warning"}]
            }
            """)!;

        var snapshot = CopilotPolicyDiagnostics.FromResult(result);

        Assert.True(snapshot.DeviceManaged);
        Assert.True(snapshot.ServerManaged);
        Assert.True(snapshot.FailClosed);
        Assert.True(snapshot.HasDiagnostics);
        Assert.Equal("gpt-5.5", snapshot.Model);
        Assert.True(snapshot.ModelLocked);
        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-policy", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsafe\nmodel")]
    [InlineData("<script>")]
    [InlineData("ghp_syntheticCredential")]
    [InlineData("github_pat_syntheticCredential")]
    [InlineData("unrecognized-family")]
    public void Missing_Or_Unbounded_Values_Are_Not_Presented_As_Trusted_Model(string? model)
    {
        var result = new ManagedSettingsResolveResult { Values = new() { Model = model } };

        var snapshot = CopilotPolicyDiagnostics.FromResult(result);

        Assert.Null(snapshot.Model);
        Assert.Null(snapshot.ModelLocked);
        Assert.False(snapshot.FailClosed);
        Assert.False(snapshot.HasDiagnostics);
    }

    [Fact]
    public void Overridable_And_Long_Identifier_Are_Handled_Separately()
    {
        var result = new ManagedSettingsResolveResult
        {
            Values = new() { Model = new string('a', 97) },
            Meta = new() { Model = new() { Overridable = true } },
        };

        var snapshot = CopilotPolicyDiagnostics.FromResult(result);

        Assert.Null(snapshot.Model);
        Assert.False(snapshot.ModelLocked);
    }

    [Fact]
    public async Task SignedOut_Does_Not_Start_Auth_Or_Runtime()
    {
        var auth = Substitute.For<IGitHubAuthSession>();
        var tokens = Substitute.For<IGitHubAccessTokenProvider>();
        auth.GetStateAsync(Arg.Any<CancellationToken>()).Returns(GitHubAuthState.NotSignedIn);
        var service = new CopilotPolicyDiagnostics(auth, tokens, Options.Create(new CopilotOptions()),
            Substitute.For<IAppVersionProvider>(), NullLogger<CopilotPolicyDiagnostics>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveAsync(TestContext.Current.CancellationToken));

        await tokens.DidNotReceive().GetAccessTokenAsync(Arg.Any<CancellationToken>());
    }
}
#pragma warning restore GHCP001
