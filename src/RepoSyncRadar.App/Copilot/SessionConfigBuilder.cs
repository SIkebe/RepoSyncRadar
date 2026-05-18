using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Builds <see cref="SessionConfig"/> values for a given <see cref="SessionPurpose"/>.
/// Pulled out of <see cref="CopilotSessionFactory"/> so the SDK-free wiring can be unit
/// tested without ever spawning a <see cref="CopilotClient"/> process.
/// </summary>
internal static class SessionConfigBuilder
{
    private const string ClientName = "RepoSyncRadar";

    public static SessionConfig Build(
        SessionPurpose purpose,
        IOptions<CopilotOptions> options,
        PermissionRequestHandler permissionHandler,
        ToolAuditHook? auditHook = null,
        IReadOnlyList<AIFunction>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionHandler);

        var copilot = options.Value;

        var config = new SessionConfig
        {
            ClientName = ClientName,
            Model = copilot.DefaultModel,
            Streaming = copilot.Streaming,
            SystemMessage = new SystemMessageConfig
            {
                // SystemMessageMode.Append keeps the SDK guard-rails and only adds our
                // operating rules on top. Never use Replace — see DESIGN.md §8.3.
                Mode = SystemMessageMode.Append,
                Content = SystemPromptFor(purpose),
            },
            OnPermissionRequest = permissionHandler,
        };

        if (tools is { Count: > 0 })
        {
            config.Tools = tools.ToList();
            config.AvailableTools = tools.Select(static tool => tool.Name).ToList();
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

    private static string SystemPromptFor(SessionPurpose purpose) => purpose switch
    {
        SessionPurpose.Triage =>
            "You are the RepoSyncRadar morning triage agent. Score newly synced commits, "
            + "summarize them in Japanese, and pick the top 5 must-reads. Only call radar_* tools.",
        SessionPurpose.Adoption =>
            "You are the RepoSyncRadar focused-commit explainer and writer. Explain the diff in Japanese, "
            + "then produce shareable drafts for the chosen commit. Stay factual.",
        SessionPurpose.Ask =>
            "You are the RepoSyncRadar query assistant. Answer using only radar_query results. "
            + "Refuse anything that requires writes or shell access.",
        SessionPurpose.Maintenance =>
            "You are the RepoSyncRadar maintenance reviewer. Propose ignore / boost rules "
            + "based on the recent review history. Do not apply changes without approval.",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported session purpose."),
    };
}
