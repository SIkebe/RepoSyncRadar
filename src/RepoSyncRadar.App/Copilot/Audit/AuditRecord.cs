namespace RepoSyncRadar.App.Copilot.Audit;

/// <summary>
/// Single-line audit envelope written to <c>%LOCALAPPDATA%\RepoSyncRadar\audit\YYYY-MM-DD.jsonl</c>.
/// One record is emitted for each <c>OnPreToolUse</c> and one for each <c>OnPostToolUse</c>.
/// </summary>
/// <param name="Phase">"pre" or "post".</param>
/// <param name="RowId">Auto-generated <see cref="Core.Models.CopilotToolLog.Id"/>.</param>
/// <param name="SessionId">Copilot session identifier (from <c>HookInvocation</c>).</param>
/// <param name="ToolName">The tool that was about to run / just ran.</param>
/// <param name="ArgsJson">JSON-serialized tool args. Same value is written on pre and post.</param>
/// <param name="ResultJson">JSON-serialized tool result. <see langword="null"/> on the pre row.</param>
/// <param name="StartedAt">UTC instant the pre hook recorded.</param>
/// <param name="EndedAt">UTC instant the post hook recorded. <see langword="null"/> on the pre row.</param>
public sealed record AuditRecord(
    string Phase,
    int RowId,
    string SessionId,
    string ToolName,
    string ArgsJson,
    string? ResultJson,
    DateTime StartedAt,
    DateTime? EndedAt);
