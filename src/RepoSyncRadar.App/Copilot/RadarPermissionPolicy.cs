using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.App.Copilot;

#pragma warning disable GHCP001 // SDK 1.0.1 exposes permission decisions through experimental RPC types.

/// <summary>
/// Implements <see cref="PermissionRequestHandler"/> for RepoSyncRadar. The policy is
/// intentionally restrictive — see <c>docs/DESIGN.md §8.1</c> and the Step 11 entry of
/// <c>docs/IMPLEMENTATION_PLAN.md</c>:
/// <list type="bullet">
///   <item><description><c>custom-tool</c> on the local allow-list is approved without prompting; everything else is prompted (Step 14).</description></item>
///   <item><description><c>read</c> is approved without prompting.</description></item>
///   <item><description><c>url</c> is approved if the host is on <see cref="UrlAllowList"/>; otherwise the UI is asked.</description></item>
///   <item><description><c>write</c> always goes through the UI prompt.</description></item>
///   <item><description><c>shell</c> is denied by rule — no shell allow-list is defined yet.</description></item>
///   <item><description>Everything else (e.g. <c>mcp</c>, <c>memory</c>, <c>hook</c>) is denied by rule.</description></item>
/// </list>
/// </summary>
public sealed partial class RadarPermissionPolicy
{
    /// <summary>
    /// Custom-tool names that are pre-approved without prompting. The read-only tools
    /// are harmless, and Morning Triage may write scoring rows plus low-score
    /// auto-rejected review rows automatically. Final non-low-score review decisions
    /// remain user-owned.
    /// </summary>
    internal static readonly IReadOnlySet<string> AutoApprovedToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "radar_list_commits",
        "radar_get_diff",
        "radar_resolve_url",
        "radar_fetch_rendered",
        "radar_score_commit",
    };

    private readonly UrlAllowList _urlAllowList;
    private readonly IPermissionPrompt _prompt;
    private readonly ILogger<RadarPermissionPolicy> _logger;

    public RadarPermissionPolicy(
        UrlAllowList urlAllowList,
        IPermissionPrompt prompt,
        ILogger<RadarPermissionPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(urlAllowList);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(logger);

        _urlAllowList = urlAllowList;
        _prompt = prompt;
        _logger = logger;
    }

    /// <summary>
    /// Adapts the policy to the SDK delegate shape. Pass <c>policy.HandleAsync</c> as the
    /// <see cref="SessionConfig.OnPermissionRequest"/> value.
    /// </summary>
    public async Task<PermissionDecision> HandleAsync(
        PermissionRequest request,
        PermissionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionId = invocation?.SessionId ?? "<unknown>";

        switch (request)
        {
            case PermissionRequestCustomTool customTool:
                if (customTool.ToolName is not null && AutoApprovedToolNames.Contains(customTool.ToolName))
                {
                    LogApprovingCustomTool(_logger, customTool.ToolName, sessionId);
                    return Approved();
                }

                LogPromptingForCustomTool(_logger, customTool.ToolName, sessionId);
                return await ConfirmAsync(customTool).ConfigureAwait(false);

            case PermissionRequestRead read:
                LogApprovingRead(_logger, read.Path, sessionId);
                return Approved();

            case PermissionRequestUrl url:
                if (_urlAllowList.IsAllowed(url.Url))
                {
                    LogApprovingAllowListedUrl(_logger, url.Url, sessionId);
                    return Approved();
                }

                LogPromptingForUrl(_logger, url.Url, sessionId);
                return await ConfirmAsync(url).ConfigureAwait(false);

            case PermissionRequestWrite write:
                LogPromptingForWrite(_logger, write.FileName, sessionId);
                return await ConfirmAsync(write).ConfigureAwait(false);

            case PermissionRequestShell shell:
                // No shell allow-list is defined yet; deny outright. Shell support will be
                // re-evaluated when (and if) a future step introduces an allowed-command list.
                LogDenyingShell(_logger, shell.FullCommandText, sessionId);
                return DeniedByRules();

            default:
                LogDenyingUnknownKind(_logger, request.Kind, sessionId);
                return DeniedByRules();
        }
    }

    private async Task<PermissionDecision> ConfirmAsync(PermissionRequest request)
    {
        var approved = await _prompt.ConfirmAsync(request).ConfigureAwait(false);
        return approved ? Approved() : DeniedByUser();
    }

    private static PermissionDecision Approved() =>
        PermissionDecision.ApproveOnce();

    private static PermissionDecision DeniedByUser() =>
        PermissionDecision.Reject("Rejected by user.");

    private static PermissionDecision DeniedByRules() =>
        PermissionDecision.UserNotAvailable();

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Approving custom tool {ToolName} (session={SessionId})")]
    private static partial void LogApprovingCustomTool(ILogger logger, string? toolName, string sessionId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Custom tool {ToolName} is not on the read-only allow-list; prompting user (session={SessionId})")]
    private static partial void LogPromptingForCustomTool(ILogger logger, string? toolName, string sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Approving read of {Path} (session={SessionId})")]
    private static partial void LogApprovingRead(ILogger logger, string? path, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Approving allow-listed URL {Url} (session={SessionId})")]
    private static partial void LogApprovingAllowListedUrl(ILogger logger, string? url, string sessionId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "URL {Url} is not on the allow-list; prompting user (session={SessionId})")]
    private static partial void LogPromptingForUrl(ILogger logger, string? url, string sessionId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Write to {FileName} requires confirmation (session={SessionId})")]
    private static partial void LogPromptingForWrite(ILogger logger, string? fileName, string sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Denying shell command {Command} by rule (session={SessionId})")]
    private static partial void LogDenyingShell(ILogger logger, string? command, string sessionId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Denying unsupported permission kind {Kind} by rule (session={SessionId})")]
    private static partial void LogDenyingUnknownKind(ILogger logger, string? kind, string sessionId);
}
#pragma warning restore GHCP001
