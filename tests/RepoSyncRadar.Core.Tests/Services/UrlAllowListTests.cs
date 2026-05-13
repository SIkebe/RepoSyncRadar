using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

/// <summary>
/// Parametrized tests for <see cref="UrlAllowList"/>. The allow-list backs WebView2's
/// <c>WebResourceRequested</c> filter (DESIGN.md §9.3, mode C). The contract is
/// host-exact, HTTPS-only, and tolerant of garbage input.
/// </summary>
public class UrlAllowListTests
{
    private static readonly string[] DocsGithubHosts = ["docs.github.com"];

    [Theory]
    [InlineData("https://docs.github.com/foo", true)]
    [InlineData("https://docs.github.com/en/copilot/about-copilot", true)]
    [InlineData("https://example.com/foo", false)]
    [InlineData("https://foo.docs.github.com/", false)]
    [InlineData("http://docs.github.com/foo", false)]
    [InlineData("not a url", false)]
    public void IsAllowed_Returns_Expected_For_DocsGithub(string url, bool expected)
    {
        var list = new UrlAllowList(DocsGithubHosts);

        Assert.Equal(expected, list.IsAllowed(url));
    }
}
