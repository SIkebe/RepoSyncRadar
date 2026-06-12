using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GitHub.Copilot;
using RepoSyncRadar.App.Settings;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// WPF implementation of <see cref="IPermissionPrompt"/>. Marshals the confirmation
/// dialog onto the UI thread because <see cref="RadarPermissionPolicy"/> is invoked from
/// arbitrary thread-pool threads inside the Copilot SDK.
/// </summary>
public sealed class WpfPermissionPrompt : IPermissionPrompt
{
    private readonly Dispatcher _dispatcher;
    private readonly CopilotUrlPermissionSettingsUpdater _urlSettingsUpdater;

    public WpfPermissionPrompt(CopilotUrlPermissionSettingsUpdater urlSettingsUpdater)
    {
        ArgumentNullException.ThrowIfNull(urlSettingsUpdater);

        _dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "WpfPermissionPrompt requires a running WPF Application.");
        _urlSettingsUpdater = urlSettingsUpdater;
    }

    public async Task<bool> ConfirmAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is PermissionRequestUrl url
            && CopilotUrlPermissionSettingsUpdater.TryGetPersistableHost(url.Url, out var host))
        {
            return await ConfirmUrlAsync(url, host, cancellationToken).ConfigureAwait(false);
        }

        var (caption, message) = FormatPrompt(request);

        return await _dispatcher.InvokeAsync(() =>
        {
            var result = MessageBox.Show(
                Application.Current?.MainWindow!,
                message,
                caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }

    private async Task<bool> ConfirmUrlAsync(
        PermissionRequestUrl url,
        string host,
        CancellationToken cancellationToken)
    {
        var choice = await _dispatcher.InvokeAsync(
            () => ShowUrlPrompt(url, host),
            DispatcherPriority.Normal,
            cancellationToken).Task.ConfigureAwait(false);

        if (choice == UrlPermissionChoice.AllowOnce)
        {
            return true;
        }

        if (choice != UrlPermissionChoice.AllowHost)
        {
            return false;
        }

        try
        {
            return await _urlSettingsUpdater.AddHostFromUrlAsync(url.Url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or LocalAppSettingsValidationException)
        {
            await _dispatcher.InvokeAsync(
                () => ShowUrlSettingsUpdateFailure(host, ex),
                DispatcherPriority.Normal,
                cancellationToken).Task.ConfigureAwait(false);
            return false;
        }
    }

    private static UrlPermissionChoice ShowUrlPrompt(PermissionRequestUrl url, string host)
    {
        var choice = UrlPermissionChoice.Deny;
        var stack = new StackPanel
        {
            Margin = new Thickness(24),
            MinWidth = 460,
            MaxWidth = 620,
        };

        stack.Children.Add(new TextBlock
        {
            Text = "Copilot wants to fetch:",
            Margin = new Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(new TextBox
        {
            Text = url.Url,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 12),
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Intent: {url.Intention}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"To stop future prompts for this site, add '{host}' to Copilot.AllowedUrlHosts.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var allowOnceButton = new Button
        {
            Content = "Allow once",
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };
        var allowHostButton = new Button
        {
            Content = "Always allow host",
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };
        var denyButton = new Button
        {
            Content = "Deny",
            IsCancel = true,
            Padding = new Thickness(12, 4, 12, 4),
        };

        buttons.Children.Add(allowOnceButton);
        buttons.Children.Add(allowHostButton);
        buttons.Children.Add(denyButton);
        stack.Children.Add(buttons);

        var window = new Window
        {
            Title = "RepoSyncRadar — Allow URL fetch?",
            Content = stack,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false,
        };

        allowOnceButton.Click += (_, _) =>
        {
            choice = UrlPermissionChoice.AllowOnce;
            window.DialogResult = true;
        };
        allowHostButton.Click += (_, _) =>
        {
            choice = UrlPermissionChoice.AllowHost;
            window.DialogResult = true;
        };
        denyButton.Click += (_, _) =>
        {
            choice = UrlPermissionChoice.Deny;
            window.DialogResult = false;
        };

        window.ShowDialog();
        return choice;
    }

    private static void ShowUrlSettingsUpdateFailure(string host, Exception ex)
    {
        MessageBox.Show(
            Application.Current?.MainWindow!,
            $"Could not add '{host}' to Copilot.AllowedUrlHosts.\n\n{ex.Message}",
            "RepoSyncRadar — Could not update settings",
            MessageBoxButton.OK,
            MessageBoxImage.Error,
            MessageBoxResult.OK);
    }

    private static (string Caption, string Message) FormatPrompt(PermissionRequest request) => request switch
    {
        PermissionRequestCustomTool tool => (
            "RepoSyncRadar — Allow custom tool?",
            $"Copilot wants to run custom tool:\n  {tool.ToolName}\n\n{tool.ToolDescription}\n\nAllow this tool?"),
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

    private enum UrlPermissionChoice
    {
        Deny,
        AllowOnce,
        AllowHost,
    }
}
