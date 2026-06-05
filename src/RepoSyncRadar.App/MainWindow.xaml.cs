using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.App.Settings;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Preview;

namespace RepoSyncRadar.App;

/// <summary>
/// Color mode for the docs / preview WebView2 surfaces. Defaults to <see cref="Dark"/>
/// because the GitHub Docs dark palette matches the surrounding chrome.
/// </summary>
public enum DocsThemeMode
{
    Dark,
    Light,
}

internal readonly record struct AppChromeThemePalette(
    string HeaderBackground,
    string HeaderBorder,
    string HeaderForeground,
    string HeaderMutedForeground,
    string SplitterBackground,
    string OverlayBackground,
    string OverlayBorder,
    string OverlayForeground,
    string OverlayMutedForeground,
    string LayoutShieldBackground,
    string IconForeground,
    string IconHoverForeground,
    string IconHoverBackground,
    string IconPressedBackground);

internal readonly record struct PreviewFileNavigationState(
    bool IsVisible,
    bool CanPrevious,
    bool CanNext,
    int Ordinal,
    int Count);

internal enum WebViewHistoryNavigationDirection
{
    Back,
    Forward,
}

/// <summary>
/// Top-level shell. Hosts a BlazorWebView (UI shell) and WebView2 docs surfaces.
/// Rendering mode C from DESIGN.md §9.3.
/// </summary>
/// <remarks>
/// <para>
/// BlazorWebView and the standalone WebView2 control are two separate WebView2
/// instances inside the same process. By default both try to share the same
/// per-app user-data folder, and the one that started initializing first holds
/// an exclusive lock on it. If WebView2's XAML <c>Source</c> property kicks off
/// initialization before BlazorWebView's own <c>Loaded</c> handler runs, the
/// BlazorWebView ends up stuck on "Loading…" because its internal WebView2
/// cannot acquire the folder.
/// </para>
/// <para>
/// To make the two coexist we assign DocsView its own UserDataFolder via
/// <see cref="CoreWebView2CreationProperties"/>, and we set <c>Source</c> from
/// code-behind only after CreationProperties is configured. We then react to
/// <c>CoreWebView2InitializationCompleted</c> to attach the allow-list filter
/// or surface a fallback message when WebView2 fails to initialize.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private const int _dwmwaUseImmersiveDarkMode = 20;
    private const int _dwmwaBorderColor = 34;
    private const int _dwmwaCaptionColor = 35;
    private const int _dwmwaTextColor = 36;
    private const int _wmXbuttondown = 0x020B;
    private const int _wmAppcommand = 0x0319;
    private const int _wmInput = 0x00FF;
    private const int _xbutton1 = 0x0001;
    private const int _xbutton2 = 0x0002;
    private const int _appcommandBrowserBackward = 1;
    private const int _appcommandBrowserForward = 2;
    private const int _ridInput = 0x10000003;
    private const int _rimTypemouse = 0;
    private const int _ridevInputsink = 0x00000100;
    private const ushort _hidUsagePageGeneric = 0x01;
    private const ushort _hidUsageGenericMouse = 0x02;
    private const ushort _riMouseButton4Down = 0x0040;
    private const ushort _riMouseButton5Down = 0x0100;

    private static readonly Uri _initialDocsUri = new("https://docs.github.com/en");
    private static readonly Uri _blankNavigationUri = new("about:blank");

    /// <summary>
    /// Environment variable name. When set to a TCP port, BlazorWebView's WebView2
    /// will expose the Chrome DevTools Protocol on that port for E2E tests.
    /// </summary>
    private const string _blazorCdpPortEnv = "REPOSYNCRADAR_BLAZOR_CDP_PORT";

    /// <summary>
    /// Environment variable name. When set to a TCP port, DocsView's WebView2 will
    /// expose the Chrome DevTools Protocol on that port for E2E tests.
    /// </summary>
    private const string _docsCdpPortEnv = "REPOSYNCRADAR_DOCS_CDP_PORT";

    /// <summary>
    /// Optional root folder for standalone WebView2 user data. E2E tests set this
    /// to a unique temp path so stale WebView2 processes cannot lock the shared
    /// production folders between app launches.
    /// </summary>
    private const string _webViewUserDataRootEnv = "REPOSYNCRADAR_WEBVIEW_USER_DATA_ROOT";

    private static readonly GridLength _defaultWorkbenchColumnWidth = new(2, GridUnitType.Star);
    private static readonly GridLength _defaultPreviewSurfaceColumnWidth = new(3, GridUnitType.Star);
    private static readonly GridLength _expandedWorkbenchSplitterColumnWidth = new(5);
    // BlazorWebView hosts a native child window, which can throw when arranged at exactly 0 width.
    private static readonly GridLength _collapsedWorkbenchColumnWidth = new(1);
    private static readonly GridLength _collapsedSplitterColumnWidth = new(0);
    private static readonly TimeSpan _previewFocusLayoutShieldDuration = TimeSpan.FromMilliseconds(120);

    private readonly UrlAllowList _allowList;
    private readonly PreviewSession _previewSession;
    private readonly IPreviewNavigator _previewNavigator;
    private readonly IWorkbenchLayoutCoordinator _workbenchLayoutCoordinator;
    private readonly IAppUserSettingsStore _userSettingsStore;
    private readonly ILogger<MainWindow> _logger;
    private PreviewComparisonRequest? _activePreviewDiffRequest;
    private Uri? _activeSinglePageRequest;
    private Uri? _pendingSinglePageAfterBlank;
    private ulong? _activeSinglePageNavigationId;
    private ulong? _pendingSinglePageBlankNavigationId;
    private int _singlePageTransientRetryCount;
    private int _previewDiffGeneration;
    private bool _beforePreviewDiffReady;
    private bool _afterPreviewDiffReady;
    private ulong? _beforePreviewDiffNavigationId;
    private ulong? _afterPreviewDiffNavigationId;
    private Uri? _openOfficialDocsUri;
    private GridLength? _expandedWorkbenchColumnWidth = _defaultWorkbenchColumnWidth;
    private GridLength? _expandedPreviewSurfaceColumnWidth = _defaultPreviewSurfaceColumnWidth;
    private GridLength? _expandedWorkbenchPreviewSplitterColumnWidth = _expandedWorkbenchSplitterColumnWidth;
    private bool _isPreviewFocusMode;
    private bool _isSettingsExpandedMode;
    private bool _previewFocusToggleMouseActivated;
    private bool? _pendingPreviewFocusMode;
    private bool _previewFocusModeChangeScheduled;
    private DocsThemeMode _docsTheme = DocsThemeMode.Dark;
    private readonly DispatcherTimer _previewFocusLayoutShieldTimer;
    private HwndSource? _windowHwndSource;
    private string? _docsThemeDocumentScriptId;
    private string? _previewThemeDocumentScriptId;
    // §Step 19.9: DocsVersionSelector を code で SelectedItem を設定したときの
    // SelectionChanged をスキップするためのガード。ユーザー操作でだけ
    // navigator.RequestVersionChange を発火し、セレクション同期のループを防ぐ。
    private bool _suppressDocsVersionSelectionChanged;
    // 1 回の XButton 押下で JS postMessage / PreviewMouseDown / WM_XBUTTONDOWN /
    // WM_APPCOMMAND / WM_INPUT の複数経路が重複発火するため、view ごとに最後の
    // 履歴ナビゲーション時刻を覚えて短時間の重複コールを 1 件にまとめる。
    private static readonly TimeSpan _webViewHistoryNavigationDebounce = TimeSpan.FromMilliseconds(250);
    private DateTime _docsViewLastHistoryNavigationAt;
    private DateTime _previewViewLastHistoryNavigationAt;

    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        _previewFocusLayoutShieldTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = _previewFocusLayoutShieldDuration,
        };
        _previewFocusLayoutShieldTimer.Tick += OnPreviewFocusLayoutShieldTimerTick;

        _userSettingsStore = services.GetRequiredService<IAppUserSettingsStore>();
        _docsTheme = _userSettingsStore.Current.DefaultDocsTheme;
        _userSettingsStore.SettingsChanged += OnUserSettingsChanged;

        UpdatePreviewFocusToggleButton();
        UpdateDocsThemeToggleButton();
        ApplyAppChromeTheme(_docsTheme);
        BlazorView.Services = services;

        var webViewOptions = services.GetRequiredService<IOptions<WebViewOptions>>().Value;
        _allowList = new UrlAllowList(webViewOptions.AllowedUrlHosts);
        _previewSession = services.GetRequiredService<PreviewSession>();
        _previewNavigator = services.GetRequiredService<IPreviewNavigator>();
        _previewNavigator.Requested += OnPreviewRequested;
        _previewNavigator.ComparisonRequested += OnPreviewComparisonRequested;
        _workbenchLayoutCoordinator = services.GetRequiredService<IWorkbenchLayoutCoordinator>();
        _workbenchLayoutCoordinator.SettingsExpandedChanged += OnSettingsExpandedChanged;
        DocsView.NavigationStarting += OnDocsViewNavigationStarting;
        PreviewView.NavigationStarting += OnPreviewViewNavigationStarting;
        DocsView.NavigationCompleted += OnDocsViewNavigationCompleted;
        PreviewView.NavigationCompleted += OnPreviewViewNavigationCompleted;
        DocsView.PreviewMouseDown += OnDocsSurfacePreviewMouseDown;
        PreviewView.PreviewMouseDown += OnDocsSurfacePreviewMouseDown;
        Closed += (_, _) =>
        {
            _previewNavigator.Requested -= OnPreviewRequested;
            _previewNavigator.ComparisonRequested -= OnPreviewComparisonRequested;
            _workbenchLayoutCoordinator.SettingsExpandedChanged -= OnSettingsExpandedChanged;
            DocsView.NavigationStarting -= OnDocsViewNavigationStarting;
            PreviewView.NavigationStarting -= OnPreviewViewNavigationStarting;
            DocsView.NavigationCompleted -= OnDocsViewNavigationCompleted;
            PreviewView.NavigationCompleted -= OnPreviewViewNavigationCompleted;
            DocsView.PreviewMouseDown -= OnDocsSurfacePreviewMouseDown;
            PreviewView.PreviewMouseDown -= OnDocsSurfacePreviewMouseDown;
            _userSettingsStore.SettingsChanged -= OnUserSettingsChanged;
            _windowHwndSource?.RemoveHook(OnWindowMessage);
            _windowHwndSource = null;
            if (DocsView.CoreWebView2 is not null)
            {
                DocsView.CoreWebView2.DOMContentLoaded -= OnDocsSurfaceDomContentLoaded;
                DocsView.CoreWebView2.WebMessageReceived -= OnPreviewScrollMessageReceived;
                RemoveDocsThemeDocumentScript(DocsView);
            }
            if (PreviewView.CoreWebView2 is not null)
            {
                PreviewView.CoreWebView2.DOMContentLoaded -= OnDocsSurfaceDomContentLoaded;
                PreviewView.CoreWebView2.WebMessageReceived -= OnPreviewScrollMessageReceived;
                RemoveDocsThemeDocumentScript(PreviewView);
            }
            _previewFocusLayoutShieldTimer.Stop();
            _previewFocusLayoutShieldTimer.Tick -= OnPreviewFocusLayoutShieldTimerTick;
        };
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<MainWindow>();

        // Use a dedicated user-data folder so DocsView and BlazorWebView do not
        // contend for the same default location. See class remarks for details.
        var appDataFolder = GetWebViewUserDataRoot();
        var docsUserDataFolder = Path.Combine(appDataFolder, "DocsView");
        var previewUserDataFolder = Path.Combine(appDataFolder, "PreviewView");
        Directory.CreateDirectory(docsUserDataFolder);
        Directory.CreateDirectory(previewUserDataFolder);

        DocsView.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = docsUserDataFolder,
            // Force the English version of docs.github.com regardless of the host OS
            // locale. WebView2 forwards this value to the Accept-Language HTTP header,
            // which docs.github.com uses to redirect to the matching localized variant.
            Language = "en-US",
            // For E2E test runs the env var supplies a remote-debugging port. Production
            // builds without the env var get no CDP exposure (string.Empty leaves the
            // args clean).
            AdditionalBrowserArguments = BuildBrowserArguments(_docsCdpPortEnv),
        };
        PreviewView.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = previewUserDataFolder,
            Language = "en-US",
        };

        DocsView.CoreWebView2InitializationCompleted += OnDocsViewInitializationCompleted;
        PreviewView.CoreWebView2InitializationCompleted += OnPreviewViewInitializationCompleted;

        // Kick off DocsView initialization explicitly *after* CreationProperties
        // is set. Setting Source triggers the initialization pipeline. We also pin
        // the initial path to /en so the first navigation skips the locale redirect.
        DocsView.Source = _initialDocsUri;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeWindowChromeTheme(_docsTheme);
        _windowHwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowHwndSource?.AddHook(OnWindowMessage);
        RegisterRawMouseInput(new WindowInteropHelper(this).Handle);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (TryParseNativeMouseHistoryNavigationMessage(message, wParam, lParam, out var direction)
            && TryResolveDocsSurfaceUnderCursor(out var view))
        {
            NavigateWebViewHistory(view, direction);
            handled = true;
            return IntPtr.Zero;
        }

        if (message == _wmInput
            && TryParseRawMouseHistoryNavigationMessage(lParam, out direction)
            && TryResolveDocsSurfaceUnderCursor(out view))
        {
            NavigateWebViewHistory(view, direction);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void RegisterRawMouseInput(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = _hidUsagePageGeneric,
                usUsage = _hidUsageGenericMouse,
                dwFlags = _ridevInputsink,
                hwndTarget = handle,
            },
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            LogRawMouseInputRegistrationFailed(_logger);
        }
    }

    private void OnDocsSurfacePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WebView2CompositionControl view
            || !TryResolveMouseHistoryNavigationButton(e.ChangedButton, out var direction))
        {
            return;
        }

        NavigateWebViewHistory(view, direction);
        e.Handled = true;
    }

    /// <summary>
    /// Assigns BlazorWebView's internal WebView2 its own user-data folder and wires
    /// the same CDP-port opt-in. CDP is only active when
    /// <see cref="_blazorCdpPortEnv"/> is set, so production builds keep it closed.
    /// </summary>
    private void OnBlazorViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        var blazorUserDataFolder = Path.Combine(GetWebViewUserDataRoot(), "BlazorView");
        Directory.CreateDirectory(blazorUserDataFolder);
        e.UserDataFolder = blazorUserDataFolder;

        var args = BuildBrowserArguments(_blazorCdpPortEnv);
        if (args.Length == 0)
        {
            return;
        }

        e.EnvironmentOptions ??= new CoreWebView2EnvironmentOptions();
        var existing = e.EnvironmentOptions.AdditionalBrowserArguments;
        e.EnvironmentOptions.AdditionalBrowserArguments =
            string.IsNullOrEmpty(existing) ? args : $"{existing} {args}";
    }

    private void OnPreviewFocusTogglePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _previewFocusToggleMouseActivated = true;

    private void OnPreviewFocusTogglePreviewKeyDown(object sender, KeyEventArgs e)
        => _previewFocusToggleMouseActivated = false;

    private void OnPreviewFocusToggleClicked(object sender, RoutedEventArgs e)
    {
        var clearFocusAfterClick = _previewFocusToggleMouseActivated;
        _previewFocusToggleMouseActivated = false;

        BeginPreviewFocusModeChange(!(_pendingPreviewFocusMode ?? _isPreviewFocusMode));
        if (clearFocusAfterClick)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ClearPreviewFocusToggleKeyboardFocus());
        }
    }

    private void ClearPreviewFocusToggleKeyboardFocus()
    {
        if (ReferenceEquals(Keyboard.FocusedElement, PreviewFocusToggleButton))
        {
            Keyboard.ClearFocus();
        }
    }

    private void BeginPreviewFocusModeChange(bool isPreviewFocusMode)
    {
        if (_pendingPreviewFocusMode == isPreviewFocusMode
            || (_pendingPreviewFocusMode is null && _isPreviewFocusMode == isPreviewFocusMode))
        {
            return;
        }

        _pendingPreviewFocusMode = isPreviewFocusMode;
        PreviewFocusLayoutShield.Visibility = Visibility.Visible;
        _previewFocusLayoutShieldTimer.Stop();

        if (_previewFocusModeChangeScheduled)
        {
            return;
        }

        _previewFocusModeChangeScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _previewFocusModeChangeScheduled = false;
            if (_pendingPreviewFocusMode is not { } pendingPreviewFocusMode)
            {
                return;
            }

            _pendingPreviewFocusMode = null;
            ApplyPreviewFocusMode(pendingPreviewFocusMode);
            _previewFocusLayoutShieldTimer.Start();
        });
    }

    private void ApplyPreviewFocusMode(bool isPreviewFocusMode)
    {
        if (_isPreviewFocusMode == isPreviewFocusMode)
        {
            return;
        }

        _isPreviewFocusMode = isPreviewFocusMode;
        if (isPreviewFocusMode)
        {
            if (WorkbenchColumn.ActualWidth > 1)
            {
                _expandedWorkbenchColumnWidth = WorkbenchColumn.Width;
            }

            WorkbenchPreviewSplitter.Visibility = Visibility.Collapsed;
            WorkbenchColumn.Width = _collapsedWorkbenchColumnWidth;
            WorkbenchPreviewSplitterColumn.Width = _collapsedSplitterColumnWidth;
        }
        else
        {
            WorkbenchColumn.Width = ResolveWorkbenchColumnRestoreWidth(_expandedWorkbenchColumnWidth);
            WorkbenchPreviewSplitterColumn.Width = _expandedWorkbenchSplitterColumnWidth;
            WorkbenchPreviewSplitter.Visibility = Visibility.Visible;
        }

        UpdatePreviewFocusToggleButton();
    }

    private void OnSettingsExpandedChanged(object? sender, bool isExpanded)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplySettingsExpandedMode(isExpanded);
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => ApplySettingsExpandedMode(isExpanded));
    }

    private void ApplySettingsExpandedMode(bool isExpanded)
    {
        if (_isSettingsExpandedMode == isExpanded)
        {
            return;
        }

        if (isExpanded && _isPreviewFocusMode)
        {
            ApplyPreviewFocusMode(false);
        }

        _isSettingsExpandedMode = isExpanded;
        if (isExpanded)
        {
            _expandedPreviewSurfaceColumnWidth = ResolvePreviewSurfaceColumnSaveWidth(PreviewSurfaceColumn.Width);
            _expandedWorkbenchPreviewSplitterColumnWidth = ResolveSplitterColumnSaveWidth(WorkbenchPreviewSplitterColumn.Width);

            WorkbenchPreviewSplitter.Visibility = Visibility.Collapsed;
            WorkbenchPreviewSplitterColumn.Width = _collapsedSplitterColumnWidth;
            PreviewSurfaceHost.Visibility = Visibility.Collapsed;
            PreviewSurfaceColumn.Width = _collapsedSplitterColumnWidth;
        }
        else
        {
            PreviewSurfaceColumn.Width = ResolvePreviewSurfaceColumnRestoreWidth(_expandedPreviewSurfaceColumnWidth);
            WorkbenchPreviewSplitterColumn.Width = ResolveSplitterColumnRestoreWidth(_expandedWorkbenchPreviewSplitterColumnWidth);
            PreviewSurfaceHost.Visibility = Visibility.Visible;
            WorkbenchPreviewSplitter.Visibility = Visibility.Visible;
        }
    }

    private void OnPreviewFocusLayoutShieldTimerTick(object? sender, EventArgs e)
    {
        _previewFocusLayoutShieldTimer.Stop();
        if (_previewFocusModeChangeScheduled || _pendingPreviewFocusMode is not null)
        {
            return;
        }

        PreviewFocusLayoutShield.Visibility = Visibility.Collapsed;
    }

    private void UpdatePreviewFocusToggleButton()
    {
        PreviewFocusToggleButton.Content = BuildPreviewFocusToggleText(_isPreviewFocusMode);
        PreviewFocusToggleButton.ToolTip = BuildPreviewFocusToggleToolTip(_isPreviewFocusMode);
        AutomationProperties.SetName(
            PreviewFocusToggleButton,
            BuildPreviewFocusToggleAutomationName(_isPreviewFocusMode));
    }

    private void UpdateDocsThemeToggleButton()
    {
        DocsThemeToggleButton.Content = BuildDocsThemeToggleGlyph(_docsTheme);
        DocsThemeToggleButton.ToolTip = BuildDocsThemeToggleToolTip(_docsTheme);
        AutomationProperties.SetName(DocsThemeToggleButton, BuildDocsThemeToggleToolTip(_docsTheme));
    }

    private async void OnDocsThemeToggleClicked(object sender, RoutedEventArgs e)
    {
        var nextTheme = ToggleDocsTheme(_docsTheme);
        SetDocsTheme(nextTheme, applyToViews: true);
        try
        {
            await _userSettingsStore.SaveDefaultDocsThemeAsync(nextTheme, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDocsThemePreferenceSaveFailed(_logger, ex);
        }
    }

    private void OnUserSettingsChanged(AppUserSettings settings)
    {
        if (Dispatcher.CheckAccess())
        {
            SetDocsTheme(settings.DefaultDocsTheme, applyToViews: true);
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () => SetDocsTheme(settings.DefaultDocsTheme, applyToViews: true));
    }

    private void SetDocsTheme(DocsThemeMode theme, bool applyToViews)
    {
        if (_docsTheme == theme)
        {
            return;
        }

        _docsTheme = theme;
        UpdateDocsThemeToggleButton();
        ApplyAppChromeTheme(theme);
        ApplyWebViewThemePreference(DocsView, theme);
        ApplyWebViewThemePreference(PreviewView, theme);
        if (!applyToViews)
        {
            return;
        }

        _ = InstallDocsThemeDocumentScriptAsync(DocsView, theme);
        _ = InstallDocsThemeDocumentScriptAsync(PreviewView, theme);
        _ = ApplyDocsThemeAsync(DocsView);
        _ = ApplyDocsThemeAsync(PreviewView);
    }

    private void ApplyAppChromeTheme(DocsThemeMode theme)
    {
        var palette = ResolveAppChromeThemePalette(theme);
        var headerBackground = BrushFromHex(palette.HeaderBackground);
        var headerBorder = BrushFromHex(palette.HeaderBorder);
        var headerForeground = BrushFromHex(palette.HeaderForeground);
        var headerMutedForeground = BrushFromHex(palette.HeaderMutedForeground);
        var splitterBackground = BrushFromHex(palette.SplitterBackground);
        var overlayBackground = BrushFromHex(palette.OverlayBackground);
        var overlayBorder = BrushFromHex(palette.OverlayBorder);
        var overlayForeground = BrushFromHex(palette.OverlayForeground);
        var overlayMutedForeground = BrushFromHex(palette.OverlayMutedForeground);
        var layoutShieldBackground = BrushFromHex(palette.LayoutShieldBackground);
        var iconForeground = BrushFromHex(palette.IconForeground);
        var iconHoverForeground = BrushFromHex(palette.IconHoverForeground);
        var iconHoverBackground = BrushFromHex(palette.IconHoverBackground);
        var iconPressedBackground = BrushFromHex(palette.IconPressedBackground);

        OfficialDocsHeader.Background = headerBackground;
        OfficialDocsHeader.BorderBrush = headerBorder;
        PreviewDocsHeader.Background = headerBackground;
        PreviewDocsHeader.BorderBrush = headerBorder;
        OfficialDocsHeaderText.Foreground = headerForeground;
        PreviewDocsHeaderText.Foreground = headerForeground;
        OfficialDocsFilePathText.Foreground = headerMutedForeground;
        PreviewDocsFilePathText.Foreground = headerMutedForeground;
        WorkbenchPreviewSplitter.Background = splitterBackground;
        PreviewDocsSplitter.Background = splitterBackground;
        DocsPreviewStatusOverlay.Background = overlayBackground;
        DocsPreviewStatusOverlay.BorderBrush = overlayBorder;
        PreviewStatusOverlay.Background = overlayBackground;
        PreviewStatusOverlay.BorderBrush = overlayBorder;
        DocsPreviewStatusText.Foreground = overlayForeground;
        PreviewStatusText.Foreground = overlayForeground;
        DocsPreviewStatusDetailText.Foreground = overlayMutedForeground;
        PreviewStatusDetailText.Foreground = overlayMutedForeground;
        PreviewFocusLayoutShield.Background = layoutShieldBackground;
        Resources["PreviewChromeIconForegroundBrush"] = iconForeground;
        Resources["PreviewChromeIconHoverForegroundBrush"] = iconHoverForeground;
        Resources["PreviewChromeIconHoverBackgroundBrush"] = iconHoverBackground;
        Resources["PreviewChromeIconPressedBackgroundBrush"] = iconPressedBackground;
        DocsThemeToggleButton.Foreground = iconForeground;
        OpenOfficialDocsButton.Foreground = iconForeground;
        PreviewFocusToggleButton.Foreground = iconForeground;
        ApplyNativeWindowChromeTheme(theme);
    }

    private void ApplyNativeWindowChromeTheme(DocsThemeMode theme)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var palette = ResolveAppChromeThemePalette(theme);
        TrySetDwmWindowAttribute(handle, _dwmwaUseImmersiveDarkMode, theme == DocsThemeMode.Dark ? 1 : 0);
        TrySetDwmWindowAttribute(handle, _dwmwaCaptionColor, ToColorRef(palette.HeaderBackground));
        TrySetDwmWindowAttribute(handle, _dwmwaTextColor, ToColorRef(palette.HeaderForeground));
        TrySetDwmWindowAttribute(handle, _dwmwaBorderColor, ToColorRef(palette.HeaderBorder));
    }

    private async Task ApplyDocsThemeAsync(WebView2CompositionControl view)
    {
        try
        {
            if (view.CoreWebView2 is null)
            {
                return;
            }

            ApplyWebViewThemePreference(view, _docsTheme);
            await view.ExecuteScriptAsync(BuildDocsThemeScript(_docsTheme));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDocsThemeApplyFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Reads a port from the supplied environment variable and returns the matching
    /// Chromium <c>--remote-debugging-port=N</c> switch. Returns an empty string when
    /// the variable is unset or invalid so callers can safely concatenate.
    /// </summary>
    private static string BuildBrowserArguments(string envName)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (!int.TryParse(raw, out var port) || port is <= 0 or > 65535)
        {
            return string.Empty;
        }

        // Bind explicitly to the loopback interface so the CDP endpoint is not
        // reachable from the network even on misconfigured machines.
        return $"--remote-debugging-port={port} --remote-allow-origins=*";
    }

    private static string GetWebViewUserDataRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(_webViewUserDataRootEnv);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RepoSyncRadar");
    }

    private void OnDocsViewInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs e)
        => OnDocsSurfaceInitializationCompleted(DocsView, e);

    private void OnPreviewViewInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs e)
        => OnDocsSurfaceInitializationCompleted(PreviewView, e);

    private void OnDocsSurfaceInitializationCompleted(
        WebView2CompositionControl view,
        CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            LogWebView2InitFailed(_logger, e.InitializationException);
            ShowWebView2Fallback(e.InitializationException);
            return;
        }

        // Filter every subresource (script, image, fetch, etc.) so requests to hosts
        // outside WebViewOptions.AllowedUrlHosts are dropped before they reach the
        // network. See DESIGN.md §9.3 (mode C) and the manual smoke entry in
        // IMPLEMENTATION_PLAN.md §Step 10.
        view.CoreWebView2.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
        view.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
        view.CoreWebView2.Settings.IsWebMessageEnabled = true;
        view.CoreWebView2.DOMContentLoaded += OnDocsSurfaceDomContentLoaded;
        view.CoreWebView2.WebMessageReceived += OnPreviewScrollMessageReceived;
        ApplyWebViewThemePreference(view, _docsTheme);
        _ = InstallMouseHistoryNavigationAsync(view);
        _ = InstallDocsThemeDocumentScriptAsync(view, _docsTheme);
        _ = ApplyDocsThemeAsync(view);
    }

    private async Task InstallMouseHistoryNavigationAsync(WebView2CompositionControl view)
    {
        try
        {
            if (view.CoreWebView2 is null)
            {
                return;
            }

            var script = BuildInstallMouseHistoryNavigationScript();
            await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
            await view.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMouseHistoryNavigationInstallFailed(_logger, ex);
        }
    }

    private async Task InstallDocsThemeDocumentScriptAsync(
        WebView2CompositionControl view,
        DocsThemeMode theme)
    {
        try
        {
            if (view.CoreWebView2 is null)
            {
                return;
            }

            RemoveDocsThemeDocumentScript(view);
            var scriptId = await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                BuildDocsThemeScript(theme));
            SetDocsThemeDocumentScriptId(view, scriptId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDocsThemeApplyFailed(_logger, ex);
        }
    }

    private void RemoveDocsThemeDocumentScript(WebView2CompositionControl view)
    {
        var scriptId = GetDocsThemeDocumentScriptId(view);
        if (scriptId is null || view.CoreWebView2 is null)
        {
            return;
        }

        view.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(scriptId);
        SetDocsThemeDocumentScriptId(view, null);
    }

    private string? GetDocsThemeDocumentScriptId(WebView2CompositionControl view)
        => ReferenceEquals(view, DocsView)
            ? _docsThemeDocumentScriptId
            : ReferenceEquals(view, PreviewView)
                ? _previewThemeDocumentScriptId
                : null;

    private void SetDocsThemeDocumentScriptId(WebView2CompositionControl view, string? scriptId)
    {
        if (ReferenceEquals(view, DocsView))
        {
            _docsThemeDocumentScriptId = scriptId;
            return;
        }
        if (ReferenceEquals(view, PreviewView))
        {
            _previewThemeDocumentScriptId = scriptId;
        }
    }

    private static void ApplyWebViewThemePreference(WebView2CompositionControl view, DocsThemeMode theme)
    {
        if (view.CoreWebView2 is null)
        {
            return;
        }

        view.CoreWebView2.Profile.PreferredColorScheme = BuildPreferredColorScheme(theme);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_allowList.IsAllowed(e.Request.Uri))
        {
            return;
        }
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && _previewSession.IsAllowed(uri))
        {
            return;
        }

        LogBlockedRequest(_logger, e.Request.Uri);
        var coreWebView = sender as CoreWebView2;
        var environment = coreWebView?.Environment
            ?? DocsView.CoreWebView2?.Environment
            ?? PreviewView.CoreWebView2?.Environment;
        if (environment is null)
        {
            return;
        }
        e.Response = environment.CreateWebResourceResponse(
            Content: Stream.Null,
            StatusCode: 403,
            ReasonPhrase: "Blocked by RepoSyncRadar allow-list",
            Headers: "Content-Type: text/plain");
    }

    private void ShowWebView2Fallback(Exception ex)
    {
        DocsView.Visibility = Visibility.Collapsed;
        DocsFallback.Visibility = Visibility.Visible;
        DocsFallbackMessage.Text =
            $"WebView2 を初期化できませんでした。Edge WebView2 ランタイムを確認してください。\n\n詳細: {ex.Message}";
    }

    /// <summary>
    /// Updates <see cref="DocsView"/>.Source to <paramref name="url"/> on the UI thread.
    /// Raised by <see cref="IPreviewNavigator"/> when the Razor "ローカルプレビュー" button
    /// has finished preparing a new preview (IMPLEMENTATION_PLAN.md §Step 19.5). The
    /// companion <see cref="PreviewSession"/> is updated by <c>PreviewCoordinator</c>
    /// before this fires, so the resource filter will already allow
    /// <c>http://localhost:{port}/*</c> through.
    /// </summary>
    private void OnPreviewRequested(object? sender, Uri url)
    {
        if (Dispatcher.CheckAccess())
        {
            NavigatePreviewRequest(url);
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => NavigatePreviewRequest(url));
        }
    }

    private void OnPreviewComparisonRequested(object? sender, PreviewComparisonRequest request)
    {
        if (Dispatcher.CheckAccess())
        {
            NavigatePreviewComparisonRequest(request);
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => NavigatePreviewComparisonRequest(request));
        }
    }

    private void NavigatePreviewRequest(Uri url)
    {
        if (IsLocalPreviewUri(url))
        {
            var comparisonRequest = new PreviewComparisonRequest(
                BuildOfficialComparisonUri(url),
                url,
                "公式 docs.github.com",
                "PR HEAD localhost");
            StartPreviewDiffTracking(comparisonRequest);
            ShowComparisonMode(
                comparisonRequest.BeforeLabel,
                comparisonRequest.AfterLabel,
                comparisonRequest.FilePath,
                comparisonRequest.FileOrdinal,
                comparisonRequest.FileCount);
            ShowInitialComparisonLoadingStatus(comparisonRequest);
            NavigatePreviewPane(DocsView, comparisonRequest.BeforeUrl);
            NavigatePreviewPane(PreviewView, url);
            return;
        }

        StopPreviewDiffTracking();
        NavigateSinglePageRequest(url);
    }

    private void NavigateSinglePageRequest(Uri url)
    {
        _activeSinglePageRequest = url;
        _pendingSinglePageAfterBlank = null;
        _activeSinglePageNavigationId = null;
        _pendingSinglePageBlankNavigationId = null;
        _singlePageTransientRetryCount = 0;
        ShowSinglePageMode(BuildSinglePageHeaderLabel(url));
        ShowPreviewPaneStatus(
            isBeforePane: true,
            text: "ページを読み込み中…",
            detail: url.AbsoluteUri);

        if (ShouldResetBeforeSinglePageNavigation(url))
        {
            _pendingSinglePageAfterBlank = url;
            NavigatePreviewPane(DocsView, _blankNavigationUri);
            return;
        }

        NavigatePreviewPane(DocsView, url);
    }

    private void NavigatePreviewComparisonRequest(PreviewComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        StartPreviewDiffTracking(request);
        ShowComparisonMode(
            request.BeforeLabel,
            request.AfterLabel,
            request.FilePath,
            request.FileOrdinal,
            request.FileCount);
        ShowInitialComparisonLoadingStatus(request);
        UpdateDocsVersionSelector(request);
        NavigatePreviewPane(DocsView, request.BeforeUrl);
        NavigatePreviewPane(PreviewView, request.AfterUrl);
    }

    private static void NavigatePreviewPane(WebView2CompositionControl view, Uri url)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(url);

        if (view.CoreWebView2 is { } core)
        {
            core.Navigate(url.AbsoluteUri);
            return;
        }

        view.Source = url;
    }

    internal static bool ShouldResetBeforeSinglePageNavigation(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §Step 19.9: ヘッダーの Version ComboBox を request.CurrentVersion / AffectedVersions に
    /// 同期させる。Markdown プレビュー (= CurrentVersion もう) のときにのみ
    /// 可視にし、単一 URL 表示時は隠す。
    /// </summary>
    private void UpdateDocsVersionSelector(PreviewComparisonRequest request)
    {
        if (request.CurrentVersion is null)
        {
            DocsVersionSelector.Visibility = Visibility.Collapsed;
            return;
        }

        _suppressDocsVersionSelectionChanged = true;
        try
        {
            if (DocsVersionSelector.ItemsSource is null)
            {
                DocsVersionSelector.ItemsSource = DocsVersionCatalog.All;
            }
            DocsVersionSelector.SelectedItem = DocsVersionCatalog.All
                .FirstOrDefault(v => v == request.CurrentVersion) ?? DocsVersionCatalog.Default;
        }
        finally
        {
            _suppressDocsVersionSelectionChanged = false;
        }

        DocsVersionSelector.ToolTip = BuildDocsVersionSelectorTooltip(request);
        DocsVersionSelector.Visibility = Visibility.Visible;
    }

    private static string BuildDocsVersionSelectorTooltip(PreviewComparisonRequest request)
    {
        var affected = request.AffectedVersions;
        if (affected is null || affected.Count == 0)
        {
            return "この PR ではどの版にも差分はありません。プレビューを表示する版を選んでください。";
        }
        var labels = string.Join(", ", affected.Select(v => v.DisplayLabel));
        return $"この PR は {affected.Count} 版に影響: {labels}。プレビューを表示する版を選んでください。";
    }

    private void OnDocsVersionSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressDocsVersionSelectionChanged)
        {
            return;
        }
        if (DocsVersionSelector.SelectedItem is DocsVersion version)
        {
            _previewNavigator.RequestVersionChange(version);
        }
    }

    private void OnPreviousPreviewFileClicked(object sender, RoutedEventArgs e)
        => RequestPreviewFileNavigation(PreviewFileNavigationDirection.Previous);

    private void OnNextPreviewFileClicked(object sender, RoutedEventArgs e)
        => RequestPreviewFileNavigation(PreviewFileNavigationDirection.Next);

    private void RequestPreviewFileNavigation(PreviewFileNavigationDirection direction)
    {
        if (_activePreviewDiffRequest is not { FileOrdinal: > 0, FileCount: > 1 })
        {
            return;
        }

        _previewNavigator.RequestFileNavigation(direction);
    }

    private void StartPreviewDiffTracking(PreviewComparisonRequest request)
    {
        _activePreviewDiffRequest = request;
        _previewDiffGeneration++;
        _beforePreviewDiffReady = false;
        _afterPreviewDiffReady = false;
        _beforePreviewDiffNavigationId = null;
        _afterPreviewDiffNavigationId = null;
    }

    private void StopPreviewDiffTracking()
    {
        _activePreviewDiffRequest = null;
        _previewDiffGeneration++;
        _beforePreviewDiffReady = false;
        _afterPreviewDiffReady = false;
        _beforePreviewDiffNavigationId = null;
        _afterPreviewDiffNavigationId = null;
        HidePreviewPaneStatus(isBeforePane: true);
        HidePreviewPaneStatus(isBeforePane: false);
    }

    private void OnDocsViewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        TrackSinglePageNavigationStarting(e.Uri, e.NavigationId);
        OnPreviewDiffPaneNavigationStarting(isBeforePane: true, e.Uri, e.NavigationId);
    }

    private void OnPreviewViewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        => OnPreviewDiffPaneNavigationStarting(isBeforePane: false, e.Uri, e.NavigationId);

    private void TrackSinglePageNavigationStarting(string navigationUri, ulong navigationId)
    {
        if (_activePreviewDiffRequest is not null
            || !Uri.TryCreate(navigationUri, UriKind.Absolute, out var actualUrl))
        {
            return;
        }

        if (_pendingSinglePageAfterBlank is not null && IsSameNavigationTarget(actualUrl, _blankNavigationUri))
        {
            _pendingSinglePageBlankNavigationId = navigationId;
            return;
        }

        if (_activeSinglePageRequest is { } request && IsSameNavigationTarget(actualUrl, request))
        {
            _activeSinglePageNavigationId = navigationId;
        }
    }

    private void OnPreviewDiffPaneNavigationStarting(bool isBeforePane, string navigationUri, ulong navigationId)
    {
        if (_activePreviewDiffRequest is not { } request
            || !Uri.TryCreate(navigationUri, UriKind.Absolute, out var actualUrl))
        {
            return;
        }

        var expectedUrl = isBeforePane ? request.BeforeUrl : request.AfterUrl;
        if (!IsSameNavigationTarget(actualUrl, expectedUrl))
        {
            return;
        }

        SetPreviewDiffNavigationId(isBeforePane, navigationId);

        ShowPreviewPaneStatus(
            isBeforePane,
            isBeforePane ? "変更前ページを読み込み中…" : "PR HEAD ページを読み込み中…",
            "localhost の応答と WebView2 の描画完了を待っています。初回は Markdown/Liquid の入力解決で時間がかかることがあります。");
    }

    private void OnDocsViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ = ApplyDocsThemeAsync(DocsView);
        if (HandleSinglePageNavigationCompleted(DocsView, e))
        {
            return;
        }
        OnPreviewDiffPaneNavigationCompleted(DocsView, isBeforePane: true, e);
    }

    private void OnPreviewViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ = ApplyDocsThemeAsync(PreviewView);
        OnPreviewDiffPaneNavigationCompleted(PreviewView, isBeforePane: false, e);
    }

    private void OnDocsSurfaceDomContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        if (_activePreviewDiffRequest is not null
            || _pendingSinglePageAfterBlank is not null
            || !ReferenceEquals(sender, DocsView.CoreWebView2)
            || _activeSinglePageRequest is not { } request
            || !IsSameNavigationTarget(DocsView.Source, request))
        {
            return;
        }

        if (!IsExpectedNavigationCompletion(e.NavigationId, _activeSinglePageNavigationId))
        {
            return;
        }

        HidePreviewPaneStatus(isBeforePane: true);
    }

    private bool HandleSinglePageNavigationCompleted(
        WebView2CompositionControl view,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_activePreviewDiffRequest is not null || !ReferenceEquals(view, DocsView))
        {
            return false;
        }

        if (_pendingSinglePageAfterBlank is { } pending
            && IsSameNavigationTarget(view.Source, _blankNavigationUri))
        {
            if (!IsExpectedNavigationCompletion(e.NavigationId, _pendingSinglePageBlankNavigationId))
            {
                return true;
            }

            _pendingSinglePageAfterBlank = null;
            _pendingSinglePageBlankNavigationId = null;
            NavigatePreviewPane(DocsView, pending);
            return true;
        }

        if (_activeSinglePageRequest is not { } request
            || !IsSameNavigationTarget(view.Source, request))
        {
            return false;
        }

        if (!IsExpectedNavigationCompletion(e.NavigationId, _activeSinglePageNavigationId))
        {
            return true;
        }

        if (!e.IsSuccess)
        {
            if (ShouldRetrySinglePageNavigation(request, e.WebErrorStatus))
            {
                _singlePageTransientRetryCount++;
                _pendingSinglePageAfterBlank = request;
                _activeSinglePageNavigationId = null;
                _pendingSinglePageBlankNavigationId = null;
                ShowPreviewPaneStatus(
                    isBeforePane: true,
                    text: "ページを再読み込み中…",
                    detail: $"WebView2: {e.WebErrorStatus}。GitHub ページをもう一度読み込みます。");
                NavigatePreviewPane(DocsView, _blankNavigationUri);
                return true;
            }

            ShowPreviewPaneStatus(
                isBeforePane: true,
                text: "ページの読み込みに失敗しました",
                detail: $"WebView2: {e.WebErrorStatus}");
            return true;
        }

        HidePreviewPaneStatus(isBeforePane: true);
        return true;
    }

    private void OnPreviewDiffPaneNavigationCompleted(
        WebView2CompositionControl view,
        bool isBeforePane,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_activePreviewDiffRequest is not { } request)
        {
            return;
        }

        var expectedUrl = isBeforePane ? request.BeforeUrl : request.AfterUrl;
        if (!IsSameNavigationTarget(view.Source, expectedUrl))
        {
            return;
        }

        if (!IsExpectedNavigationCompletion(e.NavigationId, GetPreviewDiffNavigationId(isBeforePane)))
        {
            return;
        }

        if (!e.IsSuccess)
        {
            ShowPreviewPaneStatus(
                isBeforePane,
                isBeforePane ? "変更前ページの読み込みに失敗しました" : "PR HEAD ページの読み込みに失敗しました",
                $"WebView2: {e.WebErrorStatus}");
            return;
        }

        if (isBeforePane)
        {
            _beforePreviewDiffReady = true;
        }
        else
        {
            _afterPreviewDiffReady = true;
        }

        ShowPreviewPaneStatus(
            isBeforePane,
            isBeforePane ? "変更前ページの読み込み完了" : "PR HEAD ページの読み込み完了",
            _beforePreviewDiffReady && _afterPreviewDiffReady
                ? "両方のページが揃いました。差分を解析します。"
                : "もう片方のページ読み込みを待っています。");

        _ = InstallPreviewScrollSynchronizationAsync(
            view,
            isBeforePane ? PreviewDiffPane.Before : PreviewDiffPane.After,
            _previewDiffGeneration);

        if (_beforePreviewDiffReady && _afterPreviewDiffReady)
        {
            var generation = _previewDiffGeneration;
            if (IsMarkdownComparisonRequest(request))
            {
                OfficialDocsHeaderText.Text = BuildDiffHeaderLabel(
                    request.BeforeLabel,
                    0,
                    request.SourceChangeCount);
                PreviewDocsHeaderText.Text = BuildDiffHeaderLabel(
                    request.AfterLabel,
                    0,
                    request.SourceChangeCount);
                HidePreviewPaneStatus(isBeforePane: true);
                HidePreviewPaneStatus(isBeforePane: false);
                return;
            }

            ShowPreviewPaneStatus(isBeforePane: true, "差分を解析中…", "本文ブロックを抽出してハイライトを適用しています。");
            ShowPreviewPaneStatus(isBeforePane: false, "差分を解析中…", "本文ブロックを抽出してハイライトを適用しています。");
            _ = ApplyPreviewDiffHighlightsAsync(generation);
        }
    }

    private static bool IsMarkdownComparisonRequest(PreviewComparisonRequest request)
        => IsMarkdownPreviewUri(request.BeforeUrl) || IsMarkdownPreviewUri(request.AfterUrl);

    private void SetPreviewDiffNavigationId(bool isBeforePane, ulong navigationId)
    {
        if (isBeforePane)
        {
            _beforePreviewDiffNavigationId = navigationId;
            return;
        }

        _afterPreviewDiffNavigationId = navigationId;
    }

    private ulong? GetPreviewDiffNavigationId(bool isBeforePane)
        => isBeforePane ? _beforePreviewDiffNavigationId : _afterPreviewDiffNavigationId;

    internal static bool IsExpectedNavigationCompletion(ulong navigationId, ulong? expectedNavigationId)
        => expectedNavigationId is not null && navigationId == expectedNavigationId.Value;

    private bool ShouldRetrySinglePageNavigation(Uri request, CoreWebView2WebErrorStatus status)
        => _singlePageTransientRetryCount == 0
            && string.Equals(request.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && IsTransientSinglePageNavigationError(status);

    internal static bool IsTransientSinglePageNavigationError(CoreWebView2WebErrorStatus status)
        => status is CoreWebView2WebErrorStatus.ConnectionAborted
            or CoreWebView2WebErrorStatus.OperationCanceled;

    internal static bool IsMarkdownPreviewUri(Uri uri)
        => uri.AbsolutePath.StartsWith("/markdown/", StringComparison.Ordinal);

    private async Task InstallPreviewScrollSynchronizationAsync(
        WebView2CompositionControl view,
        PreviewDiffPane pane,
        int generation)
    {
        try
        {
            if (_activePreviewDiffRequest is null
                || generation != _previewDiffGeneration
                || view.CoreWebView2 is null)
            {
                return;
            }

            await view.ExecuteScriptAsync(BuildInstallSynchronizedScrollScript(pane));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPreviewScrollSyncFailed(_logger, ex);
        }
    }

    private void OnPreviewScrollMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (TryParseWebViewHistoryNavigationMessage(message, out var historyDirection))
        {
            HandleWebViewHistoryNavigationMessage(sender, historyDirection);
            return;
        }

        if (_activePreviewDiffRequest is null)
        {
            return;
        }

        if (TryParsePreviewVersionMessage(message, out var requestedVersion))
        {
            HandlePreviewVersionMessage(sender, requestedVersion);
            return;
        }

        if (!_beforePreviewDiffReady || !_afterPreviewDiffReady)
        {
            return;
        }

        if (!TryParsePreviewScrollMessage(
                message,
                out var sourcePane,
                out var ratio,
                out var anchorOffsetPx,
                out var anchorFingerprint))
        {
            return;
        }

        var senderCore = sender as CoreWebView2;
        if (sourcePane == PreviewDiffPane.Before && !ReferenceEquals(senderCore, DocsView.CoreWebView2))
        {
            return;
        }
        if (sourcePane == PreviewDiffPane.After && !ReferenceEquals(senderCore, PreviewView.CoreWebView2))
        {
            return;
        }

        var targetView = sourcePane == PreviewDiffPane.Before ? PreviewView : DocsView;
        _ = ApplySynchronizedScrollAsync(
            targetView,
            ratio,
            anchorOffsetPx,
            anchorFingerprint,
            _previewDiffGeneration);
    }

    private void HandlePreviewVersionMessage(object? sender, DocsVersion version)
    {
        if (_activePreviewDiffRequest is not { CurrentVersion: { } currentVersion })
        {
            return;
        }

        if (currentVersion == version)
        {
            return;
        }

        var senderCore = sender as CoreWebView2;
        if (!ReferenceEquals(senderCore, DocsView.CoreWebView2)
            && !ReferenceEquals(senderCore, PreviewView.CoreWebView2))
        {
            return;
        }

        _previewNavigator.RequestVersionChange(version);
    }

    private void HandleWebViewHistoryNavigationMessage(object? sender, WebViewHistoryNavigationDirection direction)
    {
        var senderCore = sender as CoreWebView2;
        if (ReferenceEquals(senderCore, DocsView.CoreWebView2))
        {
            NavigateWebViewHistory(DocsView, direction);
            return;
        }
        if (ReferenceEquals(senderCore, PreviewView.CoreWebView2))
        {
            NavigateWebViewHistory(PreviewView, direction);
        }
    }

    private void NavigateWebViewHistory(
        WebView2CompositionControl view,
        WebViewHistoryNavigationDirection direction)
    {
        if (view.CoreWebView2 is null)
        {
            return;
        }

        if (!TryAcceptWebViewHistoryNavigation(view))
        {
            return;
        }

        if (direction == WebViewHistoryNavigationDirection.Back && view.CanGoBack)
        {
            view.GoBack();
            return;
        }
        if (direction == WebViewHistoryNavigationDirection.Forward && view.CanGoForward)
        {
            view.GoForward();
        }
    }

    private bool TryAcceptWebViewHistoryNavigation(WebView2CompositionControl view)
    {
        var now = DateTime.UtcNow;
        if (ReferenceEquals(view, DocsView))
        {
            if (now - _docsViewLastHistoryNavigationAt < _webViewHistoryNavigationDebounce)
            {
                return false;
            }
            _docsViewLastHistoryNavigationAt = now;
            return true;
        }
        if (ReferenceEquals(view, PreviewView))
        {
            if (now - _previewViewLastHistoryNavigationAt < _webViewHistoryNavigationDebounce)
            {
                return false;
            }
            _previewViewLastHistoryNavigationAt = now;
            return true;
        }
        return true;
    }

    private bool TryResolveDocsSurfaceUnderCursor(out WebView2CompositionControl view)
    {
        view = DocsView;
        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        var screenPoint = new Point(cursor.X, cursor.Y);
        if (IsScreenPointOverElement(PreviewView, screenPoint))
        {
            view = PreviewView;
            return true;
        }
        if (IsScreenPointOverElement(DocsView, screenPoint))
        {
            view = DocsView;
            return true;
        }

        return false;
    }

    private static bool IsScreenPointOverElement(FrameworkElement element, Point screenPoint)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return screenPoint.X >= topLeft.X
            && screenPoint.X <= bottomRight.X
            && screenPoint.Y >= topLeft.Y
            && screenPoint.Y <= bottomRight.Y;
    }

    private async Task ApplySynchronizedScrollAsync(
        WebView2CompositionControl targetView,
        double ratio,
        double anchorOffsetPx,
        string? anchorFingerprint,
        int generation)
    {
        try
        {
            if (_activePreviewDiffRequest is null
                || generation != _previewDiffGeneration
                || targetView.CoreWebView2 is null)
            {
                return;
            }

            var script = BuildApplySynchronizedScrollScript(
                ratio,
                double.IsNaN(anchorOffsetPx) ? null : anchorOffsetPx,
                anchorFingerprint);
            await targetView.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPreviewScrollSyncFailed(_logger, ex);
        }
    }

    private async Task ApplyPreviewDiffHighlightsAsync(int generation)
    {
        try
        {
            var request = _activePreviewDiffRequest;
            if (request is null || generation != _previewDiffGeneration)
            {
                return;
            }

            var beforeBlocks = await PreviewDiffHighlighter.ExtractBlocksAsync(DocsView);
            var afterBlocks = await PreviewDiffHighlighter.ExtractBlocksAsync(PreviewView);
            if (!ReferenceEquals(request, _activePreviewDiffRequest) || generation != _previewDiffGeneration)
            {
                return;
            }

            var plan = PreviewDiffHighlighter.BuildPlan(beforeBlocks, afterBlocks);
            await PreviewDiffHighlighter.ApplyPlanAsync(
                DocsView,
                plan.BeforeChangedIndexes,
                PreviewDiffPane.Before);
            await PreviewDiffHighlighter.ApplyPlanAsync(
                PreviewView,
                plan.AfterChangedIndexes,
                PreviewDiffPane.After);

            if (ReferenceEquals(request, _activePreviewDiffRequest) && generation == _previewDiffGeneration)
            {
                OfficialDocsHeaderText.Text = BuildDiffHeaderLabel(
                    request.BeforeLabel,
                    plan.BeforeChangedIndexes.Count,
                    request.SourceChangeCount);
                PreviewDocsHeaderText.Text = BuildDiffHeaderLabel(
                    request.AfterLabel,
                    plan.AfterChangedIndexes.Count,
                    request.SourceChangeCount);
                HidePreviewPaneStatus(isBeforePane: true);
                HidePreviewPaneStatus(isBeforePane: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPreviewDiffFailed(_logger, ex);
            ShowPreviewPaneStatus(isBeforePane: true, "差分解析に失敗しました", ex.Message);
            ShowPreviewPaneStatus(isBeforePane: false, "差分解析に失敗しました", ex.Message);
        }
    }

    private void ShowComparisonMode(
        string leftLabel = "公式 docs.github.com",
        string rightLabel = "PR HEAD localhost",
        string? filePath = null,
        int? fileOrdinal = null,
        int? fileCount = null)
    {
        OfficialDocsHeaderText.Text = leftLabel;
        PreviewDocsHeaderText.Text = rightLabel;
        SetComparisonFilePath(filePath, fileOrdinal, fileCount);
        PreviewDocsSplitter.Visibility = Visibility.Visible;
        PreviewDocsHeader.Visibility = Visibility.Visible;
        PreviewViewHost.Visibility = Visibility.Visible;
        PreviewDocsSplitterColumn.Width = new GridLength(5);
        OfficialDocsColumn.Width = new GridLength(1, GridUnitType.Star);
        PreviewDocsColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void ShowOfficialOnlyMode()
        => ShowSinglePageMode("公式 docs.github.com");

    private void ShowSinglePageMode(string label)
    {
        OfficialDocsHeaderText.Text = label;
        PreviewDocsHeaderText.Text = "PR HEAD localhost";
        SetComparisonFilePath(null, null, null);
        HidePreviewPaneStatus(isBeforePane: true);
        HidePreviewPaneStatus(isBeforePane: false);
        PreviewDocsSplitter.Visibility = Visibility.Collapsed;
        PreviewDocsHeader.Visibility = Visibility.Collapsed;
        PreviewViewHost.Visibility = Visibility.Collapsed;
        PreviewDocsSplitterColumn.Width = new GridLength(0);
        OfficialDocsColumn.Width = new GridLength(1, GridUnitType.Star);
        PreviewDocsColumn.Width = new GridLength(0);
        // §Step 19.9: 公式 docs 単独表示では Version ComboBox を隠す。
        DocsVersionSelector.Visibility = Visibility.Collapsed;
    }

    internal static string BuildSinglePageHeaderLabel(Uri url)
        => string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && url.AbsolutePath.Contains("/pull/", StringComparison.OrdinalIgnoreCase)
                ? "GitHub PR"
                : string.Equals(url.Host, "docs.github.com", StringComparison.OrdinalIgnoreCase)
                    ? "公式 docs.github.com"
                    : url.Host;

    internal static bool IsLocalPreviewUri(Uri url)
        => string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && url.IsLoopback;

    internal static Uri BuildOfficialComparisonUri(Uri localPreviewUrl)
    {
        var pathAndQuery = localPreviewUrl.PathAndQuery;
        if (string.IsNullOrWhiteSpace(pathAndQuery) || string.Equals(pathAndQuery, "/", StringComparison.Ordinal))
        {
            pathAndQuery = "/en";
        }
        return new Uri($"https://docs.github.com{pathAndQuery}");
    }

    internal static string BuildDiffHeaderLabel(string label, int changedBlockCount, int sourceChangeCount = 0)
    {
        var text = changedBlockCount <= 0
            ? $"{label}・本文差分なし"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{label}・本文差分 {changedBlockCount}");
        return sourceChangeCount <= 0
            ? text
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{text}・ソース差分 {sourceChangeCount}");
    }

    private void ShowInitialComparisonLoadingStatus(PreviewComparisonRequest request)
    {
        var detail = "サーバ起動後、WebView2 のナビゲーションと Markdown/Liquid preview の描画完了を待っています。";
        ShowPreviewPaneStatus(isBeforePane: true, "変更前ページを準備中…", detail);
        ShowPreviewPaneStatus(isBeforePane: false, "PR HEAD ページを準備中…", detail);
    }

    private void SetComparisonFilePath(string? filePath, int? fileOrdinal, int? fileCount)
    {
        var text = BuildComparisonFilePathLabel(filePath);
        var indexText = BuildComparisonFileIndexLabel(fileOrdinal, fileCount);
        OfficialDocsFilePathText.Text = text;
        OfficialDocsFilePathText.ToolTip = text.Length == 0 ? null : text;
        PreviewDocsFilePathText.Text = text;
        PreviewDocsFilePathText.ToolTip = text.Length == 0 ? null : text;
        SetFileBadge(OfficialDocsFileBadge, OfficialDocsFileBadgeText, indexText);
        SetFileBadge(PreviewDocsFileBadge, PreviewDocsFileBadgeText, indexText);
        UpdatePreviewFileNavigationButtons(fileOrdinal, fileCount);
        UpdateOpenOfficialDocsButton(filePath);
    }

    private void UpdatePreviewFileNavigationButtons(int? fileOrdinal, int? fileCount)
    {
        var state = ResolvePreviewFileNavigationState(fileOrdinal, fileCount);
        var visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        PreviousPreviewFileButton.Visibility = visibility;
        NextPreviewFileButton.Visibility = visibility;
        PreviousPreviewFileButton.IsEnabled = state.CanPrevious;
        NextPreviewFileButton.IsEnabled = state.CanNext;
        PreviousPreviewFileButton.ToolTip = state.IsVisible
            ? BuildPreviewFileNavigationToolTip(PreviewFileNavigationDirection.Previous, state)
            : null;
        NextPreviewFileButton.ToolTip = state.IsVisible
            ? BuildPreviewFileNavigationToolTip(PreviewFileNavigationDirection.Next, state)
            : null;
        AutomationProperties.SetName(
            PreviousPreviewFileButton,
            state.IsVisible ? BuildPreviewFileNavigationToolTip(PreviewFileNavigationDirection.Previous, state) : "前のファイル差分へ");
        AutomationProperties.SetName(
            NextPreviewFileButton,
            state.IsVisible ? BuildPreviewFileNavigationToolTip(PreviewFileNavigationDirection.Next, state) : "次のファイル差分へ");
    }

    private void UpdateOpenOfficialDocsButton(string? filePath)
    {
        _openOfficialDocsUri = BuildOfficialDocsUri(filePath);
        if (_openOfficialDocsUri is null)
        {
            OpenOfficialDocsButton.Visibility = Visibility.Collapsed;
            OpenOfficialDocsButton.ToolTip = null;
            AutomationProperties.SetName(OpenOfficialDocsButton, "公式ドキュメントを開く");
        }
        else
        {
            OpenOfficialDocsButton.Visibility = Visibility.Visible;
            OpenOfficialDocsButton.ToolTip = $"公式 docs.github.com を既定ブラウザで開く: {_openOfficialDocsUri.AbsoluteUri}";
            AutomationProperties.SetName(OpenOfficialDocsButton, $"公式ドキュメントを開く: {_openOfficialDocsUri.AbsoluteUri}");
        }
    }

    private void OnOpenOfficialDocsClicked(object sender, RoutedEventArgs e)
    {
        if (_openOfficialDocsUri is null)
        {
            return;
        }
        try
        {
            using var browserProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_openOfficialDocsUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogOpenOfficialDocsFailed(_logger, ex, _openOfficialDocsUri.AbsoluteUri);
        }
    }

    /// <summary>
    /// Builds the public <c>docs.github.com</c> URL for the file currently
    /// shown in the comparison preview. Returns <c>null</c> when the file is
    /// not a publishable content Markdown page (e.g. <c>CHANGELOG.md</c>,
    /// <c>data/*.yml</c>) — the "公式を開く" affordance is hidden in that case
    /// because there is no canonical public page to navigate to.
    /// </summary>
    internal static Uri? BuildOfficialDocsUri(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }
        var path = PreviewPathMapper.Map(filePath.Trim(), "en");
        if (path is null)
        {
            return null;
        }
        return new Uri($"https://docs.github.com{path}");
    }

    internal static string BuildComparisonFilePathLabel(string? filePath)
        => string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath.Trim();

    internal static string BuildComparisonFileIndexLabel(int? fileOrdinal, int? fileCount)
        => fileOrdinal is > 0 && fileCount is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{fileOrdinal}/{fileCount}")
            : string.Empty;

    internal static PreviewFileNavigationState ResolvePreviewFileNavigationState(int? fileOrdinal, int? fileCount)
    {
        if (fileOrdinal is not > 0 || fileCount is not > 1 || fileOrdinal > fileCount)
        {
            return new PreviewFileNavigationState(false, false, false, 0, 0);
        }

        return new PreviewFileNavigationState(
            IsVisible: true,
            CanPrevious: fileOrdinal.Value > 1,
            CanNext: fileOrdinal.Value < fileCount.Value,
            Ordinal: fileOrdinal.Value,
            Count: fileCount.Value);
    }

    internal static string BuildPreviewFileNavigationToolTip(
        PreviewFileNavigationDirection direction,
        PreviewFileNavigationState state)
        => direction == PreviewFileNavigationDirection.Previous
            ? state.CanPrevious ? "前のファイル差分へ" : "最初のファイル差分です"
            : state.CanNext ? "次のファイル差分へ" : "最後のファイル差分です";

    internal static AppChromeThemePalette ResolveAppChromeThemePalette(DocsThemeMode theme)
        => theme == DocsThemeMode.Light
            ? new AppChromeThemePalette(
                HeaderBackground: "#F6F8FA",
                HeaderBorder: "#D0D7DE",
                HeaderForeground: "#24292F",
                HeaderMutedForeground: "#57606A",
                SplitterBackground: "#D8DEE4",
                OverlayBackground: "#F2FFFFFF",
                OverlayBorder: "#D0D7DE",
                OverlayForeground: "#24292F",
                OverlayMutedForeground: "#57606A",
                LayoutShieldBackground: "#F6F8FA",
                IconForeground: "#57606A",
                IconHoverForeground: "#24292F",
                IconHoverBackground: "#EAEEF2",
                IconPressedBackground: "#D8DEE4")
            : new AppChromeThemePalette(
                HeaderBackground: "#0D1117",
                HeaderBorder: "#30363D",
                HeaderForeground: "#C9D1D9",
                HeaderMutedForeground: "#8B949E",
                SplitterBackground: "#30363D",
                OverlayBackground: "#E60D1117",
                OverlayBorder: "#30363D",
                OverlayForeground: "#F0F6FC",
                OverlayMutedForeground: "#8B949E",
                LayoutShieldBackground: "#0D1117",
                IconForeground: "#8B949E",
                IconHoverForeground: "#F0F6FC",
                IconHoverBackground: "#21262D",
                IconPressedBackground: "#30363D");

    internal static int ToColorRef(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static SolidColorBrush BrushFromHex(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
        brush.Freeze();
        return brush;
    }

    private static void TrySetDwmWindowAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
#pragma warning restore SYSLIB1054

        internal static DocsThemeMode ToggleDocsTheme(DocsThemeMode current)
            => current == DocsThemeMode.Dark ? DocsThemeMode.Light : DocsThemeMode.Dark;

        internal static string BuildDocsThemeToggleGlyph(DocsThemeMode current)
            // Icon represents the mode that clicking will switch TO.
            => current == DocsThemeMode.Dark ? "☀" : "🌙";

        internal static string BuildDocsThemeToggleToolTip(DocsThemeMode current)
            => current == DocsThemeMode.Dark
                ? "ライトテーマに切り替え"
                : "ダークテーマに切り替え";

        internal static CoreWebView2PreferredColorScheme BuildPreferredColorScheme(DocsThemeMode theme)
            => theme == DocsThemeMode.Light
                ? CoreWebView2PreferredColorScheme.Light
                : CoreWebView2PreferredColorScheme.Dark;

        internal static string BuildDocsThemeScript(DocsThemeMode theme)
        {
                var modeName = theme == DocsThemeMode.Dark ? "dark" : "light";
                var modeJson = JsonSerializer.Serialize(modeName);
                return $$"""
(() => {
    const mode = {{modeJson}};
    const stateKey = '__repoSyncRadarDocsTheme';
    const existing = window[stateKey];
    if (existing?.observer) {
        existing.observer.disconnect();
    }
    const state = { observer: null, applying: false };
    window[stateKey] = state;
    try { window.localStorage?.setItem('color_mode', mode); } catch (_) {}
    try { window.localStorage?.setItem('preferred_color_mode', mode); } catch (_) {}
    try { window.localStorage?.setItem('theme', mode); } catch (_) {}
    try {
        document.cookie = 'color_mode=' + mode + '; path=/; max-age=31536000; samesite=lax';
        document.cookie = 'preferred_color_mode=' + mode + '; path=/; max-age=31536000; samesite=lax';
    } catch (_) {}

    const setAttributeIfChanged = (element, name, value) => {
        if (element && element.getAttribute(name) !== value) {
            element.setAttribute(name, value);
        }
    };
    const setColorSchemeIfChanged = (element) => {
        if (element?.style && element.style.colorScheme !== mode) {
            element.style.colorScheme = mode;
        }
    };
    const apply = () => {
        if (window[stateKey] !== state) {
            return;
        }
        if (state.applying) {
            return;
        }
        state.applying = true;
        try {
            const targets = new Set([
                document.documentElement,
                document.body,
                ...document.querySelectorAll('[data-color-mode]')
            ].filter(Boolean));
            for (const target of targets) {
                setAttributeIfChanged(target, 'data-color-mode', mode);
                setAttributeIfChanged(target, 'data-light-theme', 'light');
                setAttributeIfChanged(target, 'data-dark-theme', 'dark');
                setColorSchemeIfChanged(target);
            }
        } finally {
            state.applying = false;
        }
    };
    apply();
    if (document.documentElement && typeof MutationObserver === 'function') {
        const observer = new MutationObserver(apply);
        observer.observe(document.documentElement, {
            subtree: true,
            childList: true,
            attributes: true,
            attributeFilter: ['data-color-mode', 'data-light-theme', 'data-dark-theme']
        });
        state.observer = observer;
    }
    return true;
})();
""";
        }

        internal static string BuildInstallSynchronizedScrollScript(PreviewDiffPane pane)
        {
                var paneName = pane == PreviewDiffPane.Before ? "before" : "after";
                var paneJson = JsonSerializer.Serialize(paneName);
                return $$"""
(() => {
    const pane = {{paneJson}};
    const stateKey = '__repoSyncRadarPreviewScrollSync';
    const existing = window[stateKey];
    if (existing && existing.handler) {
        window.removeEventListener('scroll', existing.handler);
    }

    const leafSelector = 'h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,td,th';
    const blockedAncestorSelector = 'nav,header,footer,aside,[role="navigation"]';

    const findCandidateBlocks = () => {
        // Prefer blocks already stamped by PreviewDiffHighlighter when available,
        // since they are guaranteed to be the same set we run diff matching on.
        // Fall back to scanning leaf elements before the highlighter has finished.
        const stamped = document.querySelectorAll('[data-rsr-diff-index]');
        if (stamped.length > 0) {
            return Array.from(stamped);
        }
        const articleRoot =
            document.querySelector('main article') ||
            document.querySelector('article') ||
            document.querySelector('[data-testid="article-body"]') ||
            document.querySelector('main') ||
            document.body;
        if (!articleRoot) {
            return [];
        }
        return Array.from(articleRoot.querySelectorAll(leafSelector))
            .filter((el) => !el.closest(blockedAncestorSelector))
            .filter((el) => {
                const text = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
                return text.length >= 4 && !el.querySelector(leafSelector);
            });
    };

    const computeFingerprint = (el) => {
        const text = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
        if (text.length < 4) {
            return '';
        }
        const slice = text.slice(0, 96);
        try {
            return btoa(unescape(encodeURIComponent(slice)));
        } catch (_) {
            return '';
        }
    };

    const pickAnchor = () => {
        const blocks = findCandidateBlocks();
        for (const el of blocks) {
            const rect = el.getBoundingClientRect();
            // First block whose bottom edge is still on/below the viewport top.
            if (rect.bottom > 0) {
                return { el, rect };
            }
        }
        return null;
    };

    const getMaxScrollTop = () => {
        const root = document.scrollingElement || document.documentElement || document.body;
        if (!root) {
            return 0;
        }
        return Math.max(0, root.scrollHeight - window.innerHeight);
    };

    const getScrollRatio = () => {
        const root = document.scrollingElement || document.documentElement || document.body;
        const maxScrollTop = getMaxScrollTop();
        const currentTop = window.scrollY || root?.scrollTop || 0;
        if (maxScrollTop <= 0) {
            return 0;
        }
        return Math.max(0, Math.min(1, currentTop / maxScrollTop));
    };

    let frame = 0;
    const handler = () => {
        if (Date.now() < (window[stateKey]?.suppressUntil || 0) || frame !== 0) {
            return;
        }
        frame = window.requestAnimationFrame(() => {
            frame = 0;
            if (Date.now() < (window[stateKey]?.suppressUntil || 0)) {
                return;
            }
            const ratio = getScrollRatio().toFixed(6);
            const anchor = pickAnchor();
            const fingerprint = anchor ? computeFingerprint(anchor.el) : '';
            if (anchor && fingerprint) {
                const offset = anchor.rect.top.toFixed(2);
                window.chrome?.webview?.postMessage(
                    `rsr-preview-scroll:${pane}:${ratio}:${offset}:${fingerprint}`);
            } else {
                // No anchor available yet (page still loading). Fall back to the
                // legacy ratio-only message so the peer can still approximate.
                window.chrome?.webview?.postMessage(`rsr-preview-scroll:${pane}:${ratio}`);
            }
        });
    };

    window[stateKey] = {
        pane,
        handler,
        suppressUntil: existing?.suppressUntil || 0,
    };
    window.addEventListener('scroll', handler, { passive: true });
    return true;
})();
""";
        }

        internal static string BuildInstallMouseHistoryNavigationScript()
            => """
(() => {
    const stateKey = '__repoSyncRadarMouseHistoryNavigation';
    const existing = window[stateKey];
    if (existing && existing.handler) {
        window.removeEventListener('mousedown', existing.handler, true);
        window.removeEventListener('auxclick', existing.handler, true);
    }

    const handler = (event) => {
        const direction = event.button === 3 ? 'back' : event.button === 4 ? 'forward' : '';
        if (!direction) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        window.chrome?.webview?.postMessage(`rsr-webview-history:${direction}`);
    };

    window[stateKey] = { handler };
    window.addEventListener('mousedown', handler, true);
    window.addEventListener('auxclick', handler, true);
    return true;
})();
""";

        internal static string BuildApplySynchronizedScrollScript(double ratio)
            => BuildApplySynchronizedScrollScript(ratio, anchorOffsetPx: null, anchorFingerprintBase64: null);

        internal static string BuildApplySynchronizedScrollScript(
            double ratio,
            double? anchorOffsetPx,
            string? anchorFingerprintBase64)
        {
                var clampedRatio = Math.Clamp(ratio, 0, 1).ToString("R", CultureInfo.InvariantCulture);
                var hasAnchor = !string.IsNullOrEmpty(anchorFingerprintBase64)
                    && anchorOffsetPx is { } px && double.IsFinite(px);
                var anchorJson = JsonSerializer.Serialize(hasAnchor ? anchorFingerprintBase64 : string.Empty);
                var anchorOffsetLiteral = hasAnchor
                    ? anchorOffsetPx!.Value.ToString("R", CultureInfo.InvariantCulture)
                    : "0";
                return $$"""
(() => {
    const stateKey = '__repoSyncRadarPreviewScrollSync';
    const root = document.scrollingElement || document.documentElement || document.body;
    if (!root) {
        return false;
    }
    const ratio = {{clampedRatio}};
    const anchorFingerprint = {{anchorJson}};
    const anchorOffsetPx = {{anchorOffsetLiteral}};
    const leafSelector = 'h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,td,th';
    const blockedAncestorSelector = 'nav,header,footer,aside,[role="navigation"]';

    window[stateKey] = window[stateKey] || {};
    window[stateKey].suppressUntil = Date.now() + 250;

    const findCandidateBlocks = () => {
        const stamped = document.querySelectorAll('[data-rsr-diff-index]');
        if (stamped.length > 0) {
            return Array.from(stamped);
        }
        const articleRoot =
            document.querySelector('main article') ||
            document.querySelector('article') ||
            document.querySelector('[data-testid="article-body"]') ||
            document.querySelector('main') ||
            document.body;
        if (!articleRoot) {
            return [];
        }
        return Array.from(articleRoot.querySelectorAll(leafSelector))
            .filter((el) => !el.closest(blockedAncestorSelector))
            .filter((el) => {
                const text = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
                return text.length >= 4 && !el.querySelector(leafSelector);
            });
    };

    const computeFingerprint = (el) => {
        const text = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
        if (text.length < 4) {
            return '';
        }
        const slice = text.slice(0, 96);
        try {
            return btoa(unescape(encodeURIComponent(slice)));
        } catch (_) {
            return '';
        }
    };

    let scrolled = false;
    if (anchorFingerprint) {
        for (const el of findCandidateBlocks()) {
            if (computeFingerprint(el) === anchorFingerprint) {
                const targetTop = el.getBoundingClientRect().top;
                const delta = targetTop - anchorOffsetPx;
                if (Math.abs(delta) > 0.5) {
                    window.scrollBy({ left: 0, top: delta, behavior: 'auto' });
                }
                scrolled = true;
                break;
            }
        }
    }
    if (!scrolled) {
        const maxScrollTop = Math.max(0, root.scrollHeight - window.innerHeight);
        window.scrollTo({ left: window.scrollX || root.scrollLeft || 0, top: maxScrollTop * ratio, behavior: 'auto' });
    }
    return true;
})();
""";
        }

        internal static bool TryParsePreviewScrollMessage(
                string? message,
                out PreviewDiffPane pane,
                out double ratio)
            => TryParsePreviewScrollMessage(message, out pane, out ratio, out _, out _);

        internal static bool TryParsePreviewVersionMessage(string? message, out DocsVersion version)
        {
            version = DocsVersionCatalog.Default;
            const string Prefix = "rsr-preview-version:";
            if (string.IsNullOrWhiteSpace(message)
                || !message.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var slug = message[Prefix.Length..].Trim();
            foreach (var candidate in DocsVersionCatalog.All)
            {
                if (string.Equals(candidate.Slug, slug, StringComparison.OrdinalIgnoreCase))
                {
                    version = candidate;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryParseWebViewHistoryNavigationMessage(
            string? message,
            out WebViewHistoryNavigationDirection direction)
        {
            direction = default;
            const string Prefix = "rsr-webview-history:";
            if (string.IsNullOrWhiteSpace(message)
                || !message.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var value = message[Prefix.Length..].Trim();
            if (string.Equals(value, "back", StringComparison.OrdinalIgnoreCase))
            {
                direction = WebViewHistoryNavigationDirection.Back;
                return true;
            }
            if (string.Equals(value, "forward", StringComparison.OrdinalIgnoreCase))
            {
                direction = WebViewHistoryNavigationDirection.Forward;
                return true;
            }

            return false;
        }

        internal static bool TryResolveMouseHistoryNavigationButton(
            MouseButton button,
            out WebViewHistoryNavigationDirection direction)
        {
            direction = button switch
            {
                MouseButton.XButton1 => WebViewHistoryNavigationDirection.Back,
                MouseButton.XButton2 => WebViewHistoryNavigationDirection.Forward,
                _ => default,
            };
            return button is MouseButton.XButton1 or MouseButton.XButton2;
        }

        internal static bool TryParseNativeMouseHistoryNavigationMessage(
            int message,
            IntPtr wParam,
            IntPtr lParam,
            out WebViewHistoryNavigationDirection direction)
        {
            direction = default;
            if (message == _wmXbuttondown)
            {
                var button = HiWord(wParam);
                if (button == _xbutton1)
                {
                    direction = WebViewHistoryNavigationDirection.Back;
                    return true;
                }
                if (button == _xbutton2)
                {
                    direction = WebViewHistoryNavigationDirection.Forward;
                    return true;
                }
            }

            if (message == _wmAppcommand)
            {
                var command = HiWord(lParam) & ~0xF000;
                if (command == _appcommandBrowserBackward)
                {
                    direction = WebViewHistoryNavigationDirection.Back;
                    return true;
                }
                if (command == _appcommandBrowserForward)
                {
                    direction = WebViewHistoryNavigationDirection.Forward;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryResolveRawMouseButtonFlags(
            ushort buttonFlags,
            out WebViewHistoryNavigationDirection direction)
        {
            direction = default;
            if ((buttonFlags & _riMouseButton4Down) != 0)
            {
                direction = WebViewHistoryNavigationDirection.Back;
                return true;
            }
            if ((buttonFlags & _riMouseButton5Down) != 0)
            {
                direction = WebViewHistoryNavigationDirection.Forward;
                return true;
            }

            return false;
        }

        private static bool TryParseRawMouseHistoryNavigationMessage(
            IntPtr rawInputHandle,
            out WebViewHistoryNavigationDirection direction)
        {
            direction = default;
            uint size = 0;
            var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            _ = GetRawInputData(rawInputHandle, _ridInput, IntPtr.Zero, ref size, headerSize);
            if (size == 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var copied = GetRawInputData(rawInputHandle, _ridInput, buffer, ref size, headerSize);
                if (copied != size)
                {
                    return false;
                }

                var rawInput = Marshal.PtrToStructure<RAWINPUT>(buffer);
                return rawInput.header.dwType == _rimTypemouse
                    && TryResolveRawMouseButtonFlags(rawInput.mouse.usButtonFlags, out direction);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static int HiWord(IntPtr value)
            => unchecked((ushort)(((long)value >> 16) & 0xFFFF));

        internal static bool TryParsePreviewScrollMessage(
                string? message,
                out PreviewDiffPane pane,
                out double ratio,
                out double anchorOffsetPx,
                out string? anchorFingerprint)
        {
                pane = default;
                ratio = 0;
                anchorOffsetPx = double.NaN;
                anchorFingerprint = null;
                if (string.IsNullOrWhiteSpace(message))
                {
                        return false;
                }

                var parts = message.Split(':');
                // 3-part: legacy ratio-only. 5-part: ratio + anchor (offset, base64 fingerprint).
                if ((parts.Length != 3 && parts.Length != 5)
                        || !string.Equals(parts[0], "rsr-preview-scroll", StringComparison.Ordinal))
                {
                        return false;
                }

                pane = parts[1] switch
                {
                        "before" => PreviewDiffPane.Before,
                        "after" => PreviewDiffPane.After,
                        _ => default,
                };
                if (parts[1] is not ("before" or "after"))
                {
                        return false;
                }

                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRatio)
                        || !double.IsFinite(parsedRatio))
                {
                        return false;
                }

                ratio = Math.Clamp(parsedRatio, 0, 1);

                if (parts.Length == 5)
                {
                        if (double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOffset)
                                && double.IsFinite(parsedOffset)
                                && !string.IsNullOrEmpty(parts[4]))
                        {
                                anchorOffsetPx = parsedOffset;
                                anchorFingerprint = parts[4];
                        }
                }

                return true;
        }

    internal static GridLength ResolveWorkbenchColumnRestoreWidth(GridLength? savedWidth)
    {
        if (savedWidth.HasValue && savedWidth.Value.Value > 0)
        {
            return savedWidth.Value;
        }

        return _defaultWorkbenchColumnWidth;
    }

    internal static GridLength ResolvePreviewSurfaceColumnSaveWidth(GridLength currentWidth)
        => currentWidth.Value > 0 ? currentWidth : _defaultPreviewSurfaceColumnWidth;

    internal static GridLength ResolvePreviewSurfaceColumnRestoreWidth(GridLength? savedWidth)
    {
        if (savedWidth.HasValue && savedWidth.Value.Value > 0)
        {
            return savedWidth.Value;
        }

        return _defaultPreviewSurfaceColumnWidth;
    }

    internal static GridLength ResolveSplitterColumnSaveWidth(GridLength currentWidth)
        => currentWidth.Value > 0 ? currentWidth : _expandedWorkbenchSplitterColumnWidth;

    internal static GridLength ResolveSplitterColumnRestoreWidth(GridLength? savedWidth)
    {
        if (savedWidth.HasValue && savedWidth.Value.Value > 0)
        {
            return savedWidth.Value;
        }

        return _expandedWorkbenchSplitterColumnWidth;
    }

    internal static string BuildPreviewFocusToggleText(bool isPreviewFocusMode)
        => isPreviewFocusMode ? "››" : "‹‹";

    internal static string BuildPreviewFocusToggleToolTip(bool isPreviewFocusMode)
        => isPreviewFocusMode
            ? "折りたたんだ左の作業ペインを戻します"
            : "左の作業ペインを折りたたんでプレビューだけ表示します";

    internal static string BuildPreviewFocusToggleAutomationName(bool isPreviewFocusMode)
        => isPreviewFocusMode ? "作業ペインを戻す" : "プレビューだけ表示";

    private static void SetFileBadge(FrameworkElement badge, TextBlock textBlock, string value)
    {
        textBlock.Text = value;
        badge.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPreviewPaneStatus(bool isBeforePane, string text, string detail)
    {
        var overlay = isBeforePane ? DocsPreviewStatusOverlay : PreviewStatusOverlay;
        var textBlock = isBeforePane ? DocsPreviewStatusText : PreviewStatusText;
        var detailBlock = isBeforePane ? DocsPreviewStatusDetailText : PreviewStatusDetailText;
        textBlock.Text = text;
        detailBlock.Text = detail;
        overlay.Visibility = Visibility.Visible;
    }

    private void HidePreviewPaneStatus(bool isBeforePane)
    {
        var overlay = isBeforePane ? DocsPreviewStatusOverlay : PreviewStatusOverlay;
        overlay.Visibility = Visibility.Collapsed;
    }

    private static bool IsSameNavigationTarget(Uri? actualUrl, Uri expectedUrl)
        => actualUrl is not null
            && string.Equals(
                actualUrl.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                expectedUrl.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct POINT
    {
        public readonly int X;

        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;

        public ushort usUsage;

        public int dwFlags;

        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RAWINPUTHEADER
    {
        public readonly int dwType;

        public readonly int dwSize;

        public readonly IntPtr hDevice;

        public readonly IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RAWINPUT
    {
        public readonly RAWINPUTHEADER header;

        public readonly RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct RAWMOUSE
    {
        [FieldOffset(0)]
        public readonly ushort usFlags;

        [FieldOffset(4)]
        public readonly uint ulButtons;

        [FieldOffset(4)]
        public readonly ushort usButtonFlags;

        [FieldOffset(6)]
        public readonly ushort usButtonData;

        [FieldOffset(8)]
        public readonly uint ulRawButtons;

        [FieldOffset(12)]
        public readonly int lLastX;

        [FieldOffset(16)]
        public readonly int lLastY;

        [FieldOffset(20)]
        public readonly uint ulExtraInformation;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "WebView2 を初期化できませんでした。fallback メッセージを表示します。")]
    private static partial void LogWebView2InitFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Blocked WebView2 request to disallowed host: {Uri}")]
    private static partial void LogBlockedRequest(ILogger logger, string uri);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Preview diff highlight failed.")]
    private static partial void LogPreviewDiffFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Preview scroll synchronization failed.")]
    private static partial void LogPreviewScrollSyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Docs theme apply failed.")]
    private static partial void LogDocsThemeApplyFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Failed to open official docs in default browser: {Url}")]
    private static partial void LogOpenOfficialDocsFailed(ILogger logger, Exception exception, string url);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Docs theme preference save failed.")]
    private static partial void LogDocsThemePreferenceSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Debug,
        Message = "Mouse history navigation install failed.")]
    private static partial void LogMouseHistoryNavigationInstallFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Debug,
        Message = "Raw mouse input registration failed.")]
    private static partial void LogRawMouseInputRegistrationFailed(ILogger logger);
}
