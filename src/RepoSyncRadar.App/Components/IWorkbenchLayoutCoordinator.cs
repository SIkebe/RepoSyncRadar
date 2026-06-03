namespace RepoSyncRadar.App.Components;

public interface IWorkbenchLayoutCoordinator
{
    event EventHandler<bool>? SettingsExpandedChanged;

    bool IsSettingsExpanded { get; }

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
