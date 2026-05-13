using RepoSyncRadar.Core.Services;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

public class PathToUrlResolverTests
{
    [Fact]
    public void Resolve_Returns_Single_Url_For_Fpt_Only()
    {
        var pageList = new Dictionary<string, IReadOnlyList<string>>
        {
            ["en/fpt"] = new[] { "/en/copilot/about-copilot", "/en/actions/learn-github-actions" },
        };

        const string frontmatter = "  fpt: '*'\n";

        var urls = PathToUrlResolver.Resolve(
            "content/copilot/about-copilot.md",
            frontmatter,
            pageList);

        Assert.Single(urls);
        Assert.Equal("/en/copilot/about-copilot", urls[0]);
    }

    [Fact]
    public void Resolve_Expands_Ghes_Range_To_All_Available_Versions()
    {
        var pageList = new Dictionary<string, IReadOnlyList<string>>
        {
            ["en/ghes-3.13"] = new[] { "/en/enterprise-server@3.13/admin/configuration" },
            ["en/ghes-3.14"] = new[] { "/en/enterprise-server@3.14/admin/configuration" },
            ["en/ghes-3.15"] = new[] { "/en/enterprise-server@3.15/admin/configuration" },
            ["en/ghes-3.16"] = new[] { "/en/enterprise-server@3.16/admin/configuration" },
        };

        const string frontmatter = "  ghes: '<= 3.15'\n";

        var urls = PathToUrlResolver.Resolve(
            "content/admin/configuration.md",
            frontmatter,
            pageList);

        Assert.Equal(3, urls.Count);
        Assert.Contains("/en/enterprise-server@3.13/admin/configuration", urls);
        Assert.Contains("/en/enterprise-server@3.14/admin/configuration", urls);
        Assert.Contains("/en/enterprise-server@3.15/admin/configuration", urls);
        Assert.DoesNotContain("/en/enterprise-server@3.16/admin/configuration", urls);
    }

    [Fact]
    public void Resolve_Returns_Empty_When_Pagelist_Does_Not_Contain_Path()
    {
        var pageList = new Dictionary<string, IReadOnlyList<string>>
        {
            ["en/fpt"] = new[] { "/en/copilot/about-copilot" },
        };

        const string frontmatter = "  fpt: '*'\n";

        var urls = PathToUrlResolver.Resolve(
            "content/copilot/something-new.md",
            frontmatter,
            pageList);

        Assert.Empty(urls);
    }

    [Fact]
    public void Resolve_Returns_Empty_For_Data_Folder()
    {
        var pageList = new Dictionary<string, IReadOnlyList<string>>
        {
            ["en/fpt"] = new[] { "/en/release-notes/3.14" },
        };

        const string frontmatter = "  fpt: '*'\n";

        var urls = PathToUrlResolver.Resolve(
            "data/release-notes/3.14.md",
            frontmatter,
            pageList);

        Assert.Empty(urls);
    }

    [Fact]
    public void Resolve_Falls_Back_To_English_When_Requested_Language_Has_No_Pagelist()
    {
        var pageList = new Dictionary<string, IReadOnlyList<string>>
        {
            ["en/fpt"] = new[] { "/en/copilot/about-copilot" },
        };

        const string frontmatter = "  fpt: '*'\n";

        var urls = PathToUrlResolver.Resolve(
            "content/copilot/about-copilot.md",
            frontmatter,
            pageList,
            language: "ja");

        Assert.Single(urls);
        Assert.Equal("/en/copilot/about-copilot", urls[0]);
    }

    [Fact]
    public void Resolve_Returns_Empty_For_Empty_Or_Malformed_Frontmatter()
    {
        var pageList = new Dictionary<string, IReadOnlyList<string>>
        {
            ["en/fpt"] = new[] { "/en/copilot/about-copilot" },
        };

        var emptyUrls = PathToUrlResolver.Resolve(
            "content/copilot/about-copilot.md",
            string.Empty,
            pageList);

        var garbledUrls = PathToUrlResolver.Resolve(
            "content/copilot/about-copilot.md",
            "this-is-not-yaml-at-all",
            pageList);

        Assert.Empty(emptyUrls);
        Assert.Empty(garbledUrls);
    }
}
