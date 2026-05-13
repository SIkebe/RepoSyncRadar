using GitHub.Copilot.SDK;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// UI-facing prompt shown when a Copilot session asks for permission to perform an
/// action that is not pre-approved by <see cref="RadarPermissionPolicy"/>. The WPF
/// implementation shows a MessageBox on the dispatcher thread; tests provide a stub
/// with a pre-canned answer.
/// </summary>
public interface IPermissionPrompt
{
    /// <summary>
    /// Asks the user whether to allow the requested action. Returning <see langword="true"/>
    /// approves it; returning <see langword="false"/> denies it interactively.
    /// </summary>
    Task<bool> ConfirmAsync(PermissionRequest request, CancellationToken cancellationToken = default);
}
