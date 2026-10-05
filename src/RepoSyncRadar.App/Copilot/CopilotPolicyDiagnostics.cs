using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Auth;
using RepoSyncRadar.Core.Auth;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

public sealed record CopilotPolicySnapshot(
    bool DeviceManaged,
    bool ServerManaged,
    bool FailClosed,
    bool HasDiagnostics,
    string? Model,
    bool? ModelLocked)
{
    public IReadOnlyList<CopilotPolicyGroup> Groups { get; init; } = [];
    public IReadOnlyList<CopilotPolicyDiagnostic> Diagnostics { get; init; } = [];
    public bool DiagnosticsTruncated { get; init; }
}

public interface ICopilotPolicyDiagnostics
{
    Task<CopilotPolicySnapshot> ResolveAsync(CancellationToken cancellationToken);
}

internal sealed class CopilotPolicyDiagnostics(
    IGitHubAuthSession auth,
    IGitHubAccessTokenProvider tokens,
    IOptions<CopilotOptions> options,
    IAppVersionProvider version,
    ILogger<CopilotPolicyDiagnostics> logger) : ICopilotPolicyDiagnostics
{
#pragma warning disable GHCP001 // On-demand enterprise policy snapshots; no session-local enforcement claims.
    public async Task<CopilotPolicySnapshot> ResolveAsync(CancellationToken cancellationToken)
    {
        if (await auth.GetStateAsync(cancellationToken).ConfigureAwait(false) != GitHubAuthState.SignedIn)
        {
            throw new InvalidOperationException("Sign in before checking Copilot policy.");
        }

        var token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await using var client = new CopilotClient(
            CopilotSessionFactory.BuildClientOptions(options.Value, logger, token, version.DisplayVersion));
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.Rpc.ManagedSettings.ResolveAsync(
            gitHubToken: token,
            clientName: "RepoSyncRadar",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return FromResult(result);
    }

    internal static CopilotPolicySnapshot FromResult(ManagedSettingsResolveResult result)
    {
        var model = result.Values?.Model;
        // Unknown families and free-form policy content are not safe display identifiers.
        if (!IsDisplayableModel(model))
        {
            model = null;
        }

        return new(
            result.Resolved.DeviceManaged,
            result.Resolved.ServerManaged,
            result.Resolved.FailClosed,
            result.Diagnostics.Count != 0,
            model,
            result.Meta?.Model is { } meta ? !meta.Overridable : null)
        {
            Groups = CopilotPolicyInventory.CreateGroups(result),
            Diagnostics = CopilotPolicyInventory.CreateDiagnostics(result),
            DiagnosticsTruncated = result.Diagnostics.Count > 32,
        };
    }
#pragma warning restore GHCP001

    internal static bool IsDisplayableModel(string? model)
        => !(string.IsNullOrWhiteSpace(model) || model.Length > 96
            || !model.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || !(model == "auto"
                || model.StartsWith("gpt-", StringComparison.Ordinal)
                || model.StartsWith("claude-", StringComparison.Ordinal)
                || model.StartsWith("gemini-", StringComparison.Ordinal)
                || model.StartsWith("grok-", StringComparison.Ordinal)
                || model.StartsWith("o1", StringComparison.Ordinal)
                || model.StartsWith("o3", StringComparison.Ordinal)
                || model.StartsWith("o4", StringComparison.Ordinal)));
}
