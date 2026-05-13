namespace RepoSyncRadar.App.Components;

/// <summary>
/// Lightweight clipboard abstraction so the Razor components can be tested without
/// hitting WPF's static <c>System.Windows.Clipboard</c>. The production implementation
/// (<see cref="WpfClipboard"/>) forwards to the WPF clipboard on the UI thread.
/// </summary>
public interface IClipboard
{
    /// <summary>Copies <paramref name="text"/> to the system clipboard.</summary>
    Task SetTextAsync(string text);
}
