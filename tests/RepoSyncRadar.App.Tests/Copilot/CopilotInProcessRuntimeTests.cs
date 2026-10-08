using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot;
using RepoSyncRadar.Core.Options;
using Xunit;

namespace RepoSyncRadar.App.Tests.Copilot;

public sealed class CopilotInProcessRuntimeTests
{
    [Fact]
    public void Native_Library_Matches_The_Bundled_Runtime_Node()
    {
        var nativeDirectory = Path.Combine(
            AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        using var library = File.OpenRead(Path.Combine(nativeDirectory, "copilot_runtime.dll"));
        using var runtime = File.OpenRead(Path.Combine(nativeDirectory, "runtime.node"));

        Assert.Equal(SHA256.HashData(runtime), SHA256.HashData(library));
    }

    [Fact]
    public async Task PingAsync_Connects_To_Bundled_InProcess_Runtime()
    {
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(),
            NullLogger<CopilotSessionFactory>.Instance,
            "test-token-for-local-ping",
            "0.1.30");
        await using var client = new CopilotClient(options);

        var response = await client.PingAsync("in-process", TestContext.Current.CancellationToken);
        var status = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("pong: in-process", response.Message);
        Assert.Equal("1.0.94-2", status.Version);
    }

#pragma warning disable GHCP001 // Exercise experimental public APIs against the bundled native runtime.
    [Fact]
    public async Task App_Policy_Allows_Manual_Mode_And_Blocks_Assisted_Approval()
    {
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(), NullLogger.Instance, "offline-permission-test", "0.1.30");
        options.RequestHandler = new RejectedModelRequestHandler();
        await using var client = new CopilotClient(options);
        var config = SessionConfigBuilder.Build(SessionPurpose.Adoption,
            Options.Create(new CopilotOptions()),
            static (_, _) => Task.FromResult(PermissionDecision.Reject("Offline test denies all tools.")));
        config.AvailableTools = new ToolSet();
        config.DisabledMcpServers = ["github"];
        config.Provider = new GitHub.Copilot.ProviderConfig
        {
            Type = "openai",
            WireApi = "chat-completions",
            BaseUrl = "https://offline-model.invalid/v1",
            ApiKey = "synthetic-key",
        };
        var ct = TestContext.Current.CancellationToken;
        await using var session = await client.CreateSessionAsync(config, ct);
        var enforced = new TaskCompletionSource<SessionManagedSettingsEnforcedData>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.On<SessionManagedSettingsEnforcedEvent>(evt =>
            enforced.TrySetResult(evt.Data));

        var manual = await session.Rpc.Permissions.SetModeAsync(PermissionMode.Manual, cancellationToken: ct);
        Assert.True(manual.Success);
        Assert.Equal(PermissionMode.Manual, manual.Mode);
        var blocked = await session.Rpc.Permissions.SetModeAsync(PermissionMode.Assisted, cancellationToken: ct);
        Assert.False(blocked.Success);
        Assert.Equal(PermissionMode.Manual, blocked.Mode);
        var current = await session.Rpc.Permissions.GetModeAsync(ct);
        Assert.Equal(PermissionMode.Manual, current.Mode);
        var policyEvent = await enforced.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal("permissions.disableAssistedPermissionsMode", policyEvent.Setting);
        Assert.Equal(ManagedSettingsEnforcedEscalation.AssistedApproval, policyEvent.Escalation);
    }

    [Fact]
    public async Task ManagedSettings_Host_Boundary_RoundTrips_Empty_And_Populated_Lists()
    {
        await using var client = new CopilotClient(CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(), NullLogger.Instance, "offline-policy-test", "0.1.30"));
        var ct = TestContext.Current.CancellationToken;
        await client.StartAsync(ct);

        var validation = await client.Rpc.ManagedSettings.ValidateAsync(
            """{"permissions":{"limitTo":[]}}""", layer: "server", cancellationToken: ct);
        Assert.True(validation.Valid);
        var composed = await client.Rpc.ManagedSettings.ComposeAsync(
            [new() { Source = ManagedSettingsChannel.Server, Settings = validation.Settings }], ct);
        Assert.NotNull(composed.Resolved.Settings);
        Assert.Equal(0, composed.Resolved.Settings.Value.GetProperty("permissions").GetProperty("limitTo").GetArrayLength());

        var populated = await client.Rpc.ManagedSettings.ValidateAsync(
            """{"permissions":{"limitTo":["Domain(github.com)"]}}""",
            layer: "server", cancellationToken: ct);
        Assert.True(populated.Valid);
        var populatedResult = await client.Rpc.ManagedSettings.ComposeAsync(
            [new() { Source = ManagedSettingsChannel.Server, Settings = populated.Settings }], ct);
        Assert.NotNull(populatedResult.Resolved.Settings);
        Assert.Equal("Domain(github.com)", Assert.Single(
            populatedResult.Resolved.Settings.Value.GetProperty("permissions").GetProperty("limitTo").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task ManagedSettings_Compose_Returns_Model_And_Override_State_Without_Applying_Policy()
    {
        await using var client = new CopilotClient(CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(), NullLogger.Instance, "offline-policy-test", "0.1.30"));
        var ct = TestContext.Current.CancellationToken;
        await client.StartAsync(ct);
        var schema = await client.Rpc.ManagedSettings.SchemaAsync(ct);
        Assert.NotEmpty(schema.RuntimeVersion);
        var validation = await client.Rpc.ManagedSettings.ValidateAsync(
            """{"model":"gpt-5.5"}""", layer: "server", cancellationToken: ct);
        Assert.True(validation.Valid, string.Join("; ", validation.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        var composed = await client.Rpc.ManagedSettings.ComposeAsync(
            [new ManagedSettingsComposeLayer { Source = ManagedSettingsChannel.Server, Settings = validation.Settings }],
            ct);
        Assert.Equal("gpt-5.5", composed.Values?.Model);
        Assert.True(composed.Meta?.Model?.Overridable);
        Assert.False(composed.Resolved.FailClosed);
    }

    [Fact]
    public async Task Policy_Inventory_Covers_Native_Schema_And_Preserves_Allowlist_Intersection()
    {
        await using var client = new CopilotClient(CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(), NullLogger.Instance, "offline-policy-test", "0.1.30"));
        var ct = TestContext.Current.CancellationToken;
        await client.StartAsync(ct);
        var schema = await client.Rpc.ManagedSettings.SchemaAsync(ct);
        var server = await client.Rpc.ManagedSettings.ValidateAsync(
            """{"model":"auto","autoTier":{"overridable":"intelligence"},"permissions":{"allow":["read","write"],"deny":["shell"]},"sandbox":{"enabled":true}}""",
            layer: "server", cancellationToken: ct);
        var device = await client.Rpc.ManagedSettings.ValidateAsync(
            """{"permissions":{"allow":["read"]},"enabledPlugins":{"github":false}}""",
            layer: "device", cancellationToken: ct);
        Assert.True(server.Valid);
        Assert.True(device.Valid);
        var composed = await client.Rpc.ManagedSettings.ComposeAsync(
            [
                new() { Source = ManagedSettingsChannel.Server, Settings = server.Settings },
                new() { Source = ManagedSettingsChannel.Device, Settings = device.Settings },
            ], ct);
        var snapshot = CopilotPolicyDiagnostics.FromResult(new ManagedSettingsResolveResult
        {
            Resolved = composed.Resolved, Values = composed.Values, Meta = composed.Meta,
            Layers = composed.Layers, Diagnostics = composed.Diagnostics,
        });
        var rows = snapshot.Groups.SelectMany(group => group.Entries).ToDictionary(row => row.Key);
        var schemaKeys = schema.Schema.GetProperty("properties").EnumerateObject().Select(property => property.Name);
        var inventoryKeys = rows.Keys.Where(key => !key.StartsWith("status.", StringComparison.Ordinal))
            .Select(key => key.Split('.')[0]).Distinct(StringComparer.Ordinal);
        Assert.Equal(schemaKeys.Order(StringComparer.Ordinal), inventoryKeys.Order(StringComparer.Ordinal));
        Assert.True(composed.Resolved.PermissionsAllowIntersected);
        Assert.Equal("Copilot.Policy.Value.Intersected", rows["permissions.allow"].Values[0].ResourceKey);
        Assert.Equal(["Device", "Server"], rows["permissions.allow"].Sources.Order(StringComparer.Ordinal));
        Assert.Contains(rows["permissions.allow"].Values, value => value.Text == "write");
        Assert.True(rows["autoTier"].Overridable);
        Assert.Contains(rows["enabledPlugins"].Values, value => value.ResourceKey == "Copilot.Policy.Value.FalseCount");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_Model_Error_From_Native_Runtime_Is_Classified_Without_Real_Inference(bool structured)
    {
        var handler = new RejectedModelRequestHandler();
        var options = CopilotSessionFactory.BuildClientOptions(
            new CopilotOptions(), NullLogger.Instance, "offline-model-test", "0.1.30");
        options.RequestHandler = handler;
        await using var client = new CopilotClient(options);
        var config = SessionConfigBuilder.Build(SessionPurpose.Adoption,
            Options.Create(new CopilotOptions()),
            static (_, _) => Task.FromResult(PermissionDecision.Reject("Offline test denies all tools.")));
        config.Model = "gpt-5.5";
        config.AvailableTools = new ToolSet();
        config.DisabledMcpServers = ["github"];
        config.Provider = new GitHub.Copilot.ProviderConfig
        {
            Type = "openai",
            WireApi = "chat-completions",
            BaseUrl = "https://offline-model.invalid/v1",
            ApiKey = "synthetic-key",
        };
        var ct = TestContext.Current.CancellationToken;
        var sdkSession = await client.CreateSessionAsync(config, ct);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var subscription = sdkSession.On<SessionEvent>(evt =>
            events.Enqueue(evt switch
            {
                ModelCallFinalResultEvent final => $"{evt.Type}:{final.Data.Result.Value}",
                SessionErrorEvent error => $"{evt.Type}:{error.Data.ErrorType}",
                _ => evt.Type,
            }));
        await using var session = new SdkCopilotSession(sdkSession, SessionPurpose.Adoption, NullLogger.Instance, null);

        var failure = await Record.ExceptionAsync(async () =>
        {
            if (structured)
            {
                await session.SendStructuredDraftAsync("Offline failure probe.", TimeSpan.FromSeconds(45), ct);
            }
            else
            {
                await session.SendAsync("Offline failure probe.", TimeSpan.FromSeconds(45), ct);
            }
        });
        Assert.True(events.Contains("model.call_final_result:http_400"), string.Join(", ", events));
        Assert.True(events.Contains("session.error:query"), string.Join(", ", events));
        Assert.True(failure is CopilotModelFailureException, string.Join(", ", events));
        var error = Assert.IsType<CopilotModelFailureException>(failure);

        Assert.Equal(CopilotModelFailureKind.InvalidRequest, error.Kind);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.True(handler.RequestCount > 0);
    }

    private sealed class RejectedModelRequestHandler : CopilotRequestHandler
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CopilotRequestContext ctx)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Synthetic invalid request","type":"invalid_request_error"}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
#pragma warning restore GHCP001
}
