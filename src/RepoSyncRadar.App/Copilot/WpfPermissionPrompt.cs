using System.Windows;
using System.Windows.Threading;
using GitHub.Copilot.SDK;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// WPF implementation of <see cref="IPermissionPrompt"/>. Marshals the confirmation
/// dialog onto the UI thread because <see cref="RadarPermissionPolicy"/> is invoked from
/// arbitrary thread-pool threads inside the Copilot SDK.
/// </summary>
public sealed class WpfPermissionPrompt : IPermissionPrompt
{
    private readonly Dispatcher _dispatcher;

    public WpfPermissionPrompt()
    {
        _dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "WpfPermissionPrompt requires a running WPF Application.");
    }

    public Task<bool> ConfirmAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (caption, message) = FormatPrompt(request);

        return _dispatcher.InvokeAsync(() =>
        {
            var result = MessageBox.Show(
                Application.Current?.MainWindow!,
                message,
                caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }).Task;
    }

    private static (string Caption, string Message) FormatPrompt(PermissionRequest request) => request switch
    {
        PermissionRequestWrite write => (
            "RepoSyncRadar — Allow file write?",
            $"Copilot wants to write to:\n  {write.FileName}\n\nIntent: {write.Intention}\n\nAllow this write?"),
        PermissionRequestUrl url => (
            "RepoSyncRadar — Allow URL fetch?",
            $"Copilot wants to fetch:\n  {url.Url}\n\nIntent: {url.Intention}\n\nAllow this URL?"),
        PermissionRequestShell shell => (
            "RepoSyncRadar — Allow shell command?",
            $"Copilot wants to run:\n  {shell.FullCommandText}\n\nIntent: {shell.Intention}\n\nAllow this command?"),
        _ => (
            "RepoSyncRadar — Permission required",
            $"Copilot is requesting a '{request.Kind}' permission. Allow?"),
    };
}
