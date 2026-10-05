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
    public void Inventory_Includes_All_Policy_Groups_And_Per_Key_Metadata()
    {
        var result = JsonSerializer.Deserialize<ManagedSettingsResolveResult>("""
            {
              "resolved":{
                "deviceManaged":true,"serverManaged":true,"failClosed":false,
                "bypassPermissionsDisabled":true,"permissionsAllowIntersected":true,
                "sandboxEnabledByUndeterminedPolicy":true,"policyHelperManaged":false,
                "managedKeys":["model","autoTier","permissions","sandbox","enabledPlugins"],
                "source":"mixed",
                "settings":{
                  "model":"gpt-5.5","autoTier":"intelligence",
                  "effortLevel":"high","contextTier":"long_context",
                  "permissions":{"deny":["shell","read:/private/file"],"ask":["write"],"disableBypassPermissionsMode":"disable"},
                  "sandbox":{"enabled":true,"allowBypass":false},
                  "enabledPlugins":{"private-plugin":false},
                  "features":{"private-feature":false},
                  "remoteControl":{"mode":"disabled"},
                  "telemetry":{"enabled":false}
                }
              },
              "values":{"model":"gpt-5.5","autoTier":"intelligence"},
              "meta":{"model":{"overridable":true,"source":"server"},
                      "autoTier":{"overridable":false,"source":"device"}},
              "layers":[{"source":"server","settings":{"permissions":{"deny":["shell"]}}},
                        {"source":"device","settings":{"permissions":{"deny":["read:/private/file"]}}}],
              "diagnostics":[{"message":"private account failed refresh from cache","path":"permissions.allow","severity":"warning"}]
            }
            """)!;

        var snapshot = CopilotPolicyDiagnostics.FromResult(result);
        Assert.Equal(["Model", "Permissions", "Runtime", "Customization", "RemoteTelemetry", "Sources"],
            snapshot.Groups.Select(g => g.Key));
        var rows = snapshot.Groups.SelectMany(g => g.Entries).ToDictionary(e => e.Key);
        Assert.True(rows["model"].Overridable);
        Assert.Equal(["Server"], rows["model"].Sources);
        Assert.False(rows["autoTier"].Overridable);
        Assert.Equal(["Device"], rows["autoTier"].Sources);
        Assert.Equal("intelligence", Assert.Single(rows["autoTier"].Values).Text);
        Assert.Equal("Copilot.Policy.Value.Intersected", Assert.Single(rows["permissions.allow"].Values).ResourceKey);
        Assert.Equal(["Server", "Device"], rows["permissions.deny"].Sources);
        Assert.Equal("Copilot.Policy.Value.Yes", Assert.Single(rows["status.sandboxFallback"].Values).ResourceKey);
        Assert.Equal("Copilot.Policy.Diagnostic.Cache", Assert.Single(snapshot.Diagnostics).SummaryKey);
        Assert.Equal("permissions.allow", snapshot.Diagnostics[0].SettingKey);
        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_Distinguishes_Missing_Empty_False_And_NotApplicable()
    {
        var result = JsonSerializer.Deserialize<ManagedSettingsResolveResult>("""
            {"resolved":{"settings":{"permissions":{"allow":[],"deny":["shell"]},"sandbox":{"enabled":false},"forceLoginOrgs":[],"telemetry":{"headers":{}}}}}
            """)!;
        var rows = CopilotPolicyDiagnostics.FromResult(result).Groups.SelectMany(g => g.Entries).ToDictionary(e => e.Key);
        Assert.Equal("Copilot.Policy.Value.Unreported", Assert.Single(rows["permissions.ask"].Values).ResourceKey);
        Assert.Equal("Copilot.Policy.Value.EmptyAllow", Assert.Single(rows["permissions.allow"].Values).ResourceKey);
        Assert.Equal("Copilot.Policy.Value.No", Assert.Single(rows["sandbox"].Values).ResourceKey);
        Assert.True(rows["status.source"].IsStatus);
        Assert.Null(rows["status.client"].Values[0].Text);
        Assert.Equal("Copilot.Policy.Value.Unreported", rows["status.client"].Values[0].ResourceKey);
        Assert.Equal("Copilot.Policy.Value.None", Assert.Single(rows["forceLoginOrgs"].Values).ResourceKey);
        Assert.Equal("Copilot.Policy.Value.None", Assert.Single(rows["telemetry"].Values).ResourceKey);
    }

    [Fact]
    public void Inventory_Does_Not_Expose_Credentials_Paths_Urls_Commands_Or_Unknown_Settings()
    {
        var result = JsonSerializer.Deserialize<ManagedSettingsResolveResult>("""
            {
              "account":"private-account",
              "resolved":{"managedKeys":["model","private-key"],"settings":{
                "model":"ghp_syntheticSecret",
                "permissions":{"allow":["ghp_syntheticSecret","shell:echo private-token"],"deny":["read:/private/file"]},
                "allowedMcpServers":[{"serverUrl":"https://private.example/?token=secret"}],
                "enabledPlugins":{"ghp_syntheticSecret":true},
                "extraKnownMarketplaces":{"private-market":{"source":{"source":"git","url":"https://private.example"}}},
                "telemetry":{"headers":{"Authorization":"Bearer private-token"},"endpoint":"https://private.example","capture":{"prompts":true}},
                "policyHelper":{"path":"C:\\private\\helper.exe","args":["private-token"]},
                "forceLoginOrgs":["private-org"],
                "futureSecret":"private-value"
              }},
              "diagnostics":[{"message":"private-token","path":"/private/path","severity":"error"}]
            }
            """)!;

        var snapshot = CopilotPolicyDiagnostics.FromResult(result);
        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("syntheticSecret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("futureSecret", serialized, StringComparison.Ordinal);
        Assert.Contains("Copilot.Policy.Value.Hidden", serialized, StringComparison.Ordinal);
        Assert.Contains("Copilot.Policy.Item.UnknownSettings", serialized, StringComparison.Ordinal);
        Assert.Null(snapshot.Diagnostics[0].SettingKey);
        Assert.Equal("Error", snapshot.Diagnostics[0].Severity);
    }

    [Fact]
    public void Inventory_Bounds_Long_Lists_And_Diagnostics_Without_Hiding_Omission()
    {
        var result = new ManagedSettingsResolveResult
        {
            Resolved = new()
            {
                Settings = JsonSerializer.SerializeToElement(new
                {
                    permissions = new { deny = Enumerable.Repeat("shell", 100).ToArray() },
                }),
                ManagedKeys = Enumerable.Repeat("model", 100).ToList(),
            },
            Diagnostics = Enumerable.Range(0, 100).Select(_ => new ManagedSettingsDiagnostic
            {
                Message = "private", Path = "private", Severity = ManagedSettingsDiagnosticSeverity.Warning,
            }).ToList(),
        };

        var snapshot = CopilotPolicyDiagnostics.FromResult(result);
        var rows = snapshot.Groups.SelectMany(group => group.Entries).ToDictionary(row => row.Key);
        Assert.Equal(33, rows["permissions.deny"].Values.Count);
        Assert.Equal("Copilot.Policy.Value.Truncated", rows["permissions.deny"].Values[^1].ResourceKey);
        Assert.Equal("Copilot.Policy.Value.Truncated", rows["status.keys"].Values[^1].ResourceKey);
        Assert.Equal(32, snapshot.Diagnostics.Count);
        Assert.True(snapshot.DiagnosticsTruncated);
    }

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
