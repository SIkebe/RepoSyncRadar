namespace RepoSyncRadar.App.Components;

/// <summary>
/// Lightweight pub/sub used by the Razor workbench and WPF host to coordinate
/// layout changes when Settings expands across the preview surface. Registered
/// as a singleton so the Blazor shell can publish state without holding a direct
/// reference to the window, and the host can subscribe once for the app lifetime.
/// </summary>
public interface IWorkbenchLayoutCoordinator
{
    /// <summary>Raised whenever the Settings-expanded layout state changes.</summary>
    event EventHandler<bool>? SettingsExpandedChanged;

    /// <summary>Gets whether Settings is currently expanded across the preview surface.</summary>
    bool IsSettingsExpanded { get; }

    /// <summary>Publishes the current Settings-expanded layout state.</summary>
    void SetSettingsExpanded(bool isExpanded);
}

public sealed class WorkbenchLayoutCoordinator : IWorkbenchLayoutCoordinator
{
    public event EventHandler<bool>? SettingsExpandedChanged;

    public bool IsSettingsExpanded { get; private set; }

    public void SetSettingsExpanded(bool isExpanded)
    {
        if (IsSettingsExpanded == isExpanded)
        {
            return;
        }

        IsSettingsExpanded = isExpanded;
        SettingsExpandedChanged?.Invoke(this, isExpanded);
    }
}
