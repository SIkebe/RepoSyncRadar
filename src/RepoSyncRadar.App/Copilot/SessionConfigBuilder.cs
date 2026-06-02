using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

#pragma warning disable GHCP001 // beta.11 exposes permission decisions through experimental RPC types.

/// <summary>
/// Builds <see cref="SessionConfig"/> values for a given <see cref="SessionPurpose"/>.
/// Pulled out of <see cref="CopilotSessionFactory"/> so the SDK-free wiring can be unit
/// tested without ever spawning a <see cref="CopilotClient"/> process.
/// </summary>
internal static class SessionConfigBuilder
{
    private const string _clientName = "RepoSyncRadar";

    public static SessionConfig Build(
        SessionPurpose purpose,
        IOptions<CopilotOptions> options,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> permissionHandler,
        ToolAuditHook? auditHook = null,
        IReadOnlyList<AIFunction>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionHandler);

        var copilot = options.Value;

        var config = new SessionConfig
        {
            ClientName = _clientName,
            Model = copilot.DefaultModel,
            ContextTier = ParseContextTier(copilot.ContextTier),
            Streaming = copilot.Streaming,
            SystemMessage = new SystemMessageConfig
            {
                // SystemMessageMode.Append keeps the SDK guard-rails and only adds our
                // operating rules on top. Never use Replace — see DESIGN.md §8.3.
                Mode = SystemMessageMode.Append,
                Content = SystemPromptFor(purpose),
            },
            OnPermissionRequest = permissionHandler,
            EnableSessionTelemetry = copilot.EnableSessionTelemetry,
            SkipCustomInstructions = true,
            CustomAgentsLocalOnly = true,
            CoauthorEnabled = false,
            ManageScheduleEnabled = false,
            McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory,
        };

        if (tools is { Count: > 0 })
        {
            config.Tools = tools.Cast<AIFunctionDeclaration>().ToList();
            var availableTools = new ToolSet();
            foreach (var tool in tools)
            {
                availableTools.AddCustom(tool.Name);
            }

            config.AvailableTools = availableTools;
        }

        if (auditHook is not null)
        {
            config.Hooks = new SessionHooks
            {
                OnPreToolUse = async (input, invocation) =>
                {
                    await auditHook.OnPreToolUseAsync(input, invocation).ConfigureAwait(false);
                    return null;
                },
                OnPostToolUse = async (input, invocation) =>
                {
                    await auditHook.OnPostToolUseAsync(input, invocation).ConfigureAwait(false);
                    return null;
                },
            };
        }

        return config;
    }

    private static ContextTier? ParseContextTier(string? contextTier)
        => contextTier?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "default" => ContextTier.Default,
            "long_context" => ContextTier.LongContext,
            _ => null,
        };

    private static string SystemPromptFor(SessionPurpose purpose) => purpose switch
    {
        SessionPurpose.Triage =>
            "You are the RepoSyncRadar morning triage agent. Score newly synced commits, "
            + "summarize them in Japanese, and leave final review decisions to the user. Only call radar_* tools.",
        SessionPurpose.Adoption =>
            "You are the RepoSyncRadar focused-commit explainer and writer. Explain the diff in Japanese, "
            + "then produce shareable drafts for the chosen commit. Stay factual.",
        SessionPurpose.Maintenance =>
            "You are the RepoSyncRadar maintenance reviewer. Propose ignore / boost rules "
            + "based on the recent review history. Do not apply changes without approval.",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported session purpose."),
    };
}
#pragma warning restore GHCP001
