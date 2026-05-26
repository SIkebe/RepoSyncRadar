namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Identifies what kind of work a Copilot session is being asked to perform. The
/// <see cref="SessionConfigBuilder"/> uses this to pick the system message that
/// is appended to the SDK default prompt (<see cref="GitHub.Copilot.SystemMessageMode.Append"/>).
/// </summary>
public enum SessionPurpose
{
    /// <summary>Morning Triage — scores newly synced commits and selects must-reads.</summary>
    Triage,

    /// <summary>Adoption — drafts platform-specific posts for a focused commit.</summary>
    Adoption,

    /// <summary>Weekly maintenance — suggests ignore / boost rule updates.</summary>
    Maintenance,
}
