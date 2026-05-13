using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RepoSyncRadar.App.Components;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// WPF implementation of <see cref="IDeviceCodePrompt"/>. The dialog renders the user
/// code, copies it to the clipboard, opens the verification URL in the default
/// browser, and stays open until the background poll succeeds or fails.
/// </summary>
public sealed partial class WpfDeviceCodePrompt : IDeviceCodePrompt
{
    private readonly Dispatcher _dispatcher;
    private readonly IClipboard _clipboard;
    private readonly ILogger<WpfDeviceCodePrompt> _logger;
    private Window? _window;

    public WpfDeviceCodePrompt(IClipboard clipboard, ILogger<WpfDeviceCodePrompt> logger)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(logger);
        _clipboard = clipboard;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "WpfDeviceCodePrompt requires a running WPF Application.");
    }

    public Task DisplayAsync(DeviceCodeChallenge challenge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        return _dispatcher.InvokeAsync(() =>
        {
            CloseInternal();
            _window = BuildWindow(challenge);
            _window.Show();

            // Best-effort: auto-copy code & open browser so the user gets a one-click
            // experience even when the dialog is offscreen on a busy taskbar.
            _ = _clipboard.SetTextAsync(challenge.UserCode);
            TryOpenBrowser(challenge.VerificationUri);
        }).Task;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        return _dispatcher.InvokeAsync(CloseInternal).Task;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void CloseInternal()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            _window.Close();
        }
        catch (InvalidOperationException)
        {
            // Window already closed by the user via the system menu.
        }
        finally
        {
            _window = null;
        }
    }

    private Window BuildWindow(DeviceCodeChallenge challenge)
    {
        var stack = new StackPanel { Margin = new Thickness(24), MinWidth = 420 };

        stack.Children.Add(new TextBlock
        {
            Text = "GitHub にサインインしてください",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });

        stack.Children.Add(new TextBlock
        {
            Text = "下のコードはクリップボードにコピー済みです。ブラウザでサインインを完了するとアプリは自動的に続行します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        stack.Children.Add(new Border
        {
            Background = Brushes.WhiteSmoke,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = challenge.UserCode,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        });

        var urlText = new TextBlock { Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap };
        urlText.Inlines.Add("検証 URL: ");
        var hyperlink = new Hyperlink(new Run(challenge.VerificationUri.ToString())) { NavigateUri = challenge.VerificationUri };
        hyperlink.RequestNavigate += (_, e) =>
        {
            TryOpenBrowser(e.Uri);
            e.Handled = true;
        };
        urlText.Inlines.Add(hyperlink);
        stack.Children.Add(urlText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var copyButton = new Button { Content = "コードを再コピー", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
        copyButton.Click += async (_, _) => await _clipboard.SetTextAsync(challenge.UserCode).ConfigureAwait(false);
        buttons.Children.Add(copyButton);

        var openButton = new Button { Content = "ブラウザを開く", Padding = new Thickness(12, 4, 12, 4) };
        openButton.Click += (_, _) => TryOpenBrowser(challenge.VerificationUri);
        buttons.Children.Add(openButton);

        stack.Children.Add(buttons);

        stack.Children.Add(new TextBlock
        {
            Text = string.Create(
                CultureInfo.CurrentCulture,
                $"このコードは {challenge.ExpiresAt.ToLocalTime():HH:mm:ss} まで有効です。"),
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
        });

        return new Window
        {
            Title = "RepoSyncRadar — GitHub サインイン",
            Content = stack,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ShowInTaskbar = true,
            Topmost = true,
        };
    }

    private void TryOpenBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogBrowserOpenFailed(_logger, uri, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed to launch the default browser for {Uri}. Ask the user to navigate manually.")]
    private static partial void LogBrowserOpenFailed(ILogger logger, Uri uri, Exception exception);
}
