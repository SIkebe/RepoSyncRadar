using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RepoSyncRadar.App.Components;
using RepoSyncRadar.Core.Options;
using RepoSyncRadar.Core.Services;
using RepoSyncRadar.Core.Services.Preview;

namespace RepoSyncRadar.App;

/// <summary>
/// Top-level shell. Hosts a BlazorWebView (UI shell) and a WebView2 (live docs.github.com).
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
    private static readonly Uri InitialDocsUri = new("https://docs.github.com/en");

    /// <summary>
    /// Environment variable name. When set to a TCP port, BlazorWebView's WebView2
    /// will expose the Chrome DevTools Protocol on that port for E2E tests.
    /// </summary>
    private const string BlazorCdpPortEnv = "REPOSYNCRADAR_BLAZOR_CDP_PORT";

    /// <summary>
    /// Environment variable name. When set to a TCP port, DocsView's WebView2 will
    /// expose the Chrome DevTools Protocol on that port for E2E tests.
    /// </summary>
    private const string DocsCdpPortEnv = "REPOSYNCRADAR_DOCS_CDP_PORT";

    /// <summary>
    /// Optional root folder for standalone WebView2 user data. E2E tests set this
    /// to a unique temp path so stale WebView2 processes cannot lock the shared
    /// production folders between app launches.
    /// </summary>
    private const string WebViewUserDataRootEnv = "REPOSYNCRADAR_WEBVIEW_USER_DATA_ROOT";

    private readonly UrlAllowList _allowList;
    private readonly PreviewSession _previewSession;
    private readonly IPreviewNavigator _previewNavigator;
    private readonly ILogger<MainWindow> _logger;
    private PreviewComparisonRequest? _activePreviewDiffRequest;
    private int _previewDiffGeneration;
    private bool _beforePreviewDiffReady;
    private bool _afterPreviewDiffReady;

    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        InitializeComponent();
        BlazorView.Services = services;

        var copilotOptions = services.GetRequiredService<IOptions<CopilotOptions>>().Value;
        _allowList = new UrlAllowList(copilotOptions.AllowedUrlHosts);
        _previewSession = services.GetRequiredService<PreviewSession>();
        _previewNavigator = services.GetRequiredService<IPreviewNavigator>();
        _previewNavigator.Requested += OnPreviewRequested;
        _previewNavigator.ComparisonRequested += OnPreviewComparisonRequested;
        DocsView.NavigationCompleted += OnDocsViewNavigationCompleted;
        PreviewView.NavigationCompleted += OnPreviewViewNavigationCompleted;
        Closed += (_, _) =>
        {
            _previewNavigator.Requested -= OnPreviewRequested;
            _previewNavigator.ComparisonRequested -= OnPreviewComparisonRequested;
            DocsView.NavigationCompleted -= OnDocsViewNavigationCompleted;
            PreviewView.NavigationCompleted -= OnPreviewViewNavigationCompleted;
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
            AdditionalBrowserArguments = BuildBrowserArguments(DocsCdpPortEnv),
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
        DocsView.Source = InitialDocsUri;
    }

    /// <summary>
    /// Assigns BlazorWebView's internal WebView2 its own user-data folder and wires
    /// the same CDP-port opt-in. CDP is only active when
    /// <see cref="BlazorCdpPortEnv"/> is set, so production builds keep it closed.
    /// </summary>
    private void OnBlazorViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        var blazorUserDataFolder = Path.Combine(GetWebViewUserDataRoot(), "BlazorView");
        Directory.CreateDirectory(blazorUserDataFolder);
        e.UserDataFolder = blazorUserDataFolder;

        var args = BuildBrowserArguments(BlazorCdpPortEnv);
        if (args.Length == 0)
        {
            return;
        }

        e.EnvironmentOptions ??= new CoreWebView2EnvironmentOptions();
        var existing = e.EnvironmentOptions.AdditionalBrowserArguments;
        e.EnvironmentOptions.AdditionalBrowserArguments =
            string.IsNullOrEmpty(existing) ? args : $"{existing} {args}";
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
        var configuredRoot = Environment.GetEnvironmentVariable(WebViewUserDataRootEnv);
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
        WebView2 view,
        CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            LogWebView2InitFailed(_logger, e.InitializationException);
            ShowWebView2Fallback(e.InitializationException);
            return;
        }

        // Filter every subresource (script, image, fetch, etc.) so requests to hosts
        // outside CopilotOptions.AllowedUrlHosts are dropped before they reach the
        // network. See DESIGN.md §9.3 (mode C) and the manual smoke entry in
        // IMPLEMENTATION_PLAN.md §Step 10.
        view.CoreWebView2.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All);
        view.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
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
                comparisonRequest.FilePath);
            DocsView.Source = comparisonRequest.BeforeUrl;
            PreviewView.Source = url;
            return;
        }

        StopPreviewDiffTracking();
        ShowOfficialOnlyMode();
        DocsView.Source = url;
    }

    private void NavigatePreviewComparisonRequest(PreviewComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        StartPreviewDiffTracking(request);
        ShowComparisonMode(request.BeforeLabel, request.AfterLabel, request.FilePath);
        DocsView.Source = request.BeforeUrl;
        PreviewView.Source = request.AfterUrl;
    }

    private void StartPreviewDiffTracking(PreviewComparisonRequest request)
    {
        _activePreviewDiffRequest = request;
        _previewDiffGeneration++;
        _beforePreviewDiffReady = false;
        _afterPreviewDiffReady = false;
    }

    private void StopPreviewDiffTracking()
    {
        _activePreviewDiffRequest = null;
        _previewDiffGeneration++;
        _beforePreviewDiffReady = false;
        _afterPreviewDiffReady = false;
    }

    private void OnDocsViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => OnPreviewDiffPaneNavigationCompleted(DocsView, isBeforePane: true, e);

    private void OnPreviewViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => OnPreviewDiffPaneNavigationCompleted(PreviewView, isBeforePane: false, e);

    private void OnPreviewDiffPaneNavigationCompleted(
        WebView2 view,
        bool isBeforePane,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _activePreviewDiffRequest is not { } request)
        {
            return;
        }

        var expectedUrl = isBeforePane ? request.BeforeUrl : request.AfterUrl;
        if (!IsSameNavigationTarget(view.Source, expectedUrl))
        {
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

        if (_beforePreviewDiffReady && _afterPreviewDiffReady)
        {
            var generation = _previewDiffGeneration;
            _ = ApplyPreviewDiffHighlightsAsync(generation);
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
                    plan.BeforeChangedIndexes.Count);
                PreviewDocsHeaderText.Text = BuildDiffHeaderLabel(
                    request.AfterLabel,
                    plan.AfterChangedIndexes.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPreviewDiffFailed(_logger, ex);
        }
    }

    private void ShowComparisonMode(
        string leftLabel = "公式 docs.github.com",
        string rightLabel = "PR HEAD localhost",
        string? filePath = null)
    {
        OfficialDocsHeaderText.Text = leftLabel;
        PreviewDocsHeaderText.Text = rightLabel;
        SetComparisonFilePath(filePath);
        PreviewDocsSplitter.Visibility = Visibility.Visible;
        PreviewDocsHeader.Visibility = Visibility.Visible;
        PreviewView.Visibility = Visibility.Visible;
        PreviewDocsSplitterColumn.Width = new GridLength(5);
        OfficialDocsColumn.Width = new GridLength(1, GridUnitType.Star);
        PreviewDocsColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void ShowOfficialOnlyMode()
    {
        OfficialDocsHeaderText.Text = "公式 docs.github.com";
        PreviewDocsHeaderText.Text = "PR HEAD localhost";
        SetComparisonFilePath(null);
        PreviewDocsSplitter.Visibility = Visibility.Collapsed;
        PreviewDocsHeader.Visibility = Visibility.Collapsed;
        PreviewView.Visibility = Visibility.Collapsed;
        PreviewDocsSplitterColumn.Width = new GridLength(0);
        OfficialDocsColumn.Width = new GridLength(1, GridUnitType.Star);
        PreviewDocsColumn.Width = new GridLength(0);
    }

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

    internal static string BuildDiffHeaderLabel(string label, int changedBlockCount)
        => changedBlockCount <= 0
            ? $"{label}・差分なし"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{label}・差分 {changedBlockCount}");

    private void SetComparisonFilePath(string? filePath)
    {
        var text = BuildComparisonFilePathLabel(filePath);
        OfficialDocsFilePathText.Text = text;
        OfficialDocsFilePathText.ToolTip = text.Length == 0 ? null : text;
        PreviewDocsFilePathText.Text = text;
        PreviewDocsFilePathText.ToolTip = text.Length == 0 ? null : text;
    }

    internal static string BuildComparisonFilePathLabel(string? filePath)
        => string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath.Trim();

    private static bool IsSameNavigationTarget(Uri? actualUrl, Uri expectedUrl)
        => actualUrl is not null
            && string.Equals(
                actualUrl.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                expectedUrl.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped),
                StringComparison.OrdinalIgnoreCase);

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
}
