using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RepoSyncRadar.App.Copilot.Audit;
using RepoSyncRadar.Core.Options;

namespace RepoSyncRadar.App.Copilot;

#pragma warning disable MCPSDK001 // Copilot SDK permission and MCP auth decisions remain experimental.

/// <summary>
/// Builds <see cref="CreateSessionOptions"/> values for a given <see cref="SessionPurpose"/>.
/// Pulled out of <see cref="CopilotSessionFactory"/> so the SDK-free wiring can be unit
/// tested without ever spawning a <see cref="CopilotClient"/> process.
/// </summary>
internal static class SessionConfigBuilder
{
    private const string _clientName = "RepoSyncRadar";

    public static CreateSessionOptions Build(
        SessionPurpose purpose,
        IOptions<CopilotOptions> options,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> permissionHandler,
        ToolAuditHook? auditHook = null,
        IReadOnlyList<AIFunction>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(permissionHandler);

        var copilot = options.Value;

        var config = new CreateSessionOptions
        {
            ClientName = _clientName,
            Model = copilot.DefaultModel,
            SystemMessage = new SystemMessageConfig
            {
                // SystemMessageMode.Append keeps the SDK guard-rails and only adds our
                // operating rules on top. Never use Replace — see DESIGN.md §8.3.
                Mode = SystemMessageMode.Append,
                Content = SystemPromptFor(purpose),
            },
        };

        config.Inference.ReasoningEffort = copilot.ReasoningEffort;
        config.Inference.ContextTier = ParseContextTier(copilot.ContextTier);
        config.Events.Streaming = copilot.Streaming;
        config.Permissions.OnRequest = permissionHandler;
        config.Permissions.Mode = PermissionMode.Manual;
        config.Interaction.AutoModeRecovery = false;
        config.GitHub.TelemetryEnabled = copilot.EnableSessionTelemetry;
        config.Filesystem.TrackFileChanges = false;
        config.State.EnableCrossSessionStore = false;
        config.State.Memory = new Microsoft.Copilot.MemoryConfiguration { Enabled = false };
        config.Experiments.EnableExperimentalMode = false;
        // Excluding built-ins alone does not disable ambient or provider-backed skills.
        config.Skills.Enabled = false;
        config.Skills.IncludedBuiltinSkills = [];
        config.Agents.IncludedBuiltInAgents = [];
        config.Agents.LocalDiscoveryOnly = true;
        config.Tools.Search = new ToolSearchConfig { Enabled = false };
        config.Tools.ManageScheduleEnabled = false;
        config.Instructions.SkipCustomInstructions = true;
        config.Git.CoauthorEnabled = false;
        // Repository-origin hooks run commands without per-hook approval; the radar never needs them.
        config.Hooks.EnableRepositoryHooks = false;
        config.Policy.ManagedSettings = CreateManagedSettings();
        config.Mcp.OauthTokenStorage = McpOAuthTokenStorageMode.InMemory;
        // RepoSyncRadar does not broker third-party MCP OAuth tokens. Fail closed if
        // an OAuth-protected MCP server is introduced before a user-visible flow exists.
        config.Mcp.OnAuthRequest = CancelMcpAuthRequestAsync;

        if (copilot.EnableWebSocketResponses is { } enableWebSocketResponses)
        {
            config.GitHub.Capi = new Microsoft.Copilot.CapiSessionOptions
            {
                EnableWebSocketResponses = enableWebSocketResponses,
            };
        }

        if (tools is { Count: > 0 })
        {
            config.Tools.Definitions = tools.Cast<AIFunctionDeclaration>().ToList();
            var availableTools = new ToolSet();
            foreach (var tool in tools)
            {
                availableTools.AddCustom(tool.Name);
            }

            config.Tools.AvailableTools = availableTools;
        }

        if (auditHook is not null)
        {
            config.Hooks.Handlers = new SessionHooks
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

    internal static ManagedSettings CreateManagedSettings()
        => new()
        {
            Permissions = new ManagedSettingsPermissions
            {
                DisableBypassPermissionsMode = DisableBypassPermissionsModes.Disable,
                DisableAssistedPermissionsMode = true,
                Deny = ["shell"],
            },
        };

    private static ContextTier? ParseContextTier(string? contextTier)
        => contextTier?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "default" => ContextTier.Default,
            "long_context" => ContextTier.LongContext,
            _ => null,
        };

    private static Task<McpAuthResult?> CancelMcpAuthRequestAsync(McpAuthContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult<McpAuthResult?>(McpAuthResult.Cancel());
    }

    private static string SystemPromptFor(SessionPurpose purpose) => purpose switch
    {
        SessionPurpose.Triage =>
            "You are the RepoSyncRadar morning triage agent. Score newly synced commits, "
            + "summarize them in Japanese, and leave final review decisions to the user. Only call radar_* tools.",
        SessionPurpose.Adoption =>
            "You are the RepoSyncRadar focused-commit explainer and writer. Explain the diff in Japanese, "
            + "then produce shareable drafts for the chosen commit. Stay factual. "
            + "When rendered official documentation is needed, use radar_fetch_rendered; never use a built-in URL fetch tool.",
        SessionPurpose.Maintenance =>
            "You are the RepoSyncRadar maintenance reviewer. Propose ignore / boost rules "
            + "based on the recent review history. Do not apply changes without approval.",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unsupported session purpose."),
    };
}
#pragma warning restore MCPSDK001
