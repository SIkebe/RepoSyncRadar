using System.Windows;

namespace RepoSyncRadar.App;

/// <summary>
/// Top-level shell. Hosts a BlazorWebView (UI shell) and a WebView2 (live docs.github.com).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        BlazorView.Services = services;
    }
}
