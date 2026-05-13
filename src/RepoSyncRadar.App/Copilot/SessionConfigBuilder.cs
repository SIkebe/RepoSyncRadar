using GitHub.Copilot.SDK;
using Microsoft.Extensions.Options;
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
        PermissionRequestHandler permissionHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionHandler);

        var copilot = options.Value;

        return new SessionConfig
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
    }

    private static string SystemPromptFor(SessionPurpose purpose) => purpose switch
    {
        SessionPurpose.Triage =>
            "You are the RepoSyncRadar morning triage agent. Score newly synced commits, "
            + "summarize them in Japanese, and pick the top 5 must-reads. Only call radar_* tools.",
        SessionPurpose.Adoption =>
            "You are the RepoSyncRadar adoption writer. Produce three Japanese drafts "
            + "(twitter_ja, slack_ja, customer_ja) for the chosen commit. Stay factual.",
        SessionPurpose.Ask =>
            "You are the RepoSyncRadar query assistant. Answer using only radar_query results. "
            + "Refuse anything that requires writes or shell access.",
        SessionPurpose.Maintenance =>
            "You are the RepoSyncRadar maintenance reviewer. Propose ignore / boost rules "
            + "based on the recent review history. Do not apply changes without approval.",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported session purpose."),
    };
}
