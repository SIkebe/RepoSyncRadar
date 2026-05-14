using RepoSyncRadar.App;
using Xunit;

namespace RepoSyncRadar.App.Tests;

public sealed class MainWindowPreviewComparisonTests
{
    [Fact]
    public void IsLocalPreviewUri_Returns_True_For_Loopback_Http()
    {
        Assert.True(MainWindow.IsLocalPreviewUri(new Uri("http://localhost:4500/en/copilot/about-copilot")));
        Assert.True(MainWindow.IsLocalPreviewUri(new Uri("http://127.0.0.1:4500/en/copilot/about-copilot")));
    }

    [Fact]
    public void IsLocalPreviewUri_Returns_False_For_Official_Docs()
    {
        Assert.False(MainWindow.IsLocalPreviewUri(new Uri("https://docs.github.com/en/copilot/about-copilot")));
    }

    [Fact]
    public void BuildOfficialComparisonUri_Maps_Localhost_Path_To_Docs_GitHub_Com()
    {
        var result = MainWindow.BuildOfficialComparisonUri(
            new Uri("http://localhost:4500/en/copilot/about-copilot?foo=bar"));

        Assert.Equal("https://docs.github.com/en/copilot/about-copilot?foo=bar", result.AbsoluteUri);
    }

    [Fact]
    public void BuildOfficialComparisonUri_Maps_Localhost_Root_To_English_Docs_Home()
    {
        var result = MainWindow.BuildOfficialComparisonUri(new Uri("http://localhost:4500/"));

        Assert.Equal("https://docs.github.com/en", result.AbsoluteUri);
    }
}