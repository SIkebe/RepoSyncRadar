using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="PreviewPathMapper"/> (IMPLEMENTATION_PLAN.md §Step 19.5).
/// Pure string-only function so the tests stay trivial and side-effect free.
/// </summary>
public sealed class PreviewPathMapperTests
{
    [Theory]
    [InlineData("content/copilot/about-copilot.md", "en", "/en/copilot/about-copilot")]
    [InlineData("content/actions/learn-github-actions/quickstart.md", "en", "/en/actions/learn-github-actions/quickstart")]
    [InlineData("content/copilot/about-copilot.md", "ja", "/ja/copilot/about-copilot")]
    public void Maps_Markdown_To_Url(string repoPath, string language, string expected)
    {
        var actual = PreviewPathMapper.Map(repoPath, language);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("content/actions/index.md", "en", "/en/actions")]
    [InlineData("content/index.md", "en", "/en")]
    public void Strips_Index_Segment(string repoPath, string language, string expected)
    {
        var actual = PreviewPathMapper.Map(repoPath, language);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("data/release-notes/3.10.md")]
    [InlineData("translations/ja-JP/content/actions/index.md")]
    [InlineData("README.md")]
    [InlineData("content/actions/style.css")]
    public void Returns_Null_For_Non_Content_Markdown(string repoPath)
    {
        var actual = PreviewPathMapper.Map(repoPath, "en");

        Assert.Null(actual);
    }

    [Fact]
    public void Defaults_Language_To_En_When_Empty()
    {
        var actual = PreviewPathMapper.Map("content/foo/bar.md", language: "");

        Assert.Equal("/en/foo/bar", actual);
    }

    [Fact]
    public void Treats_Forward_Slashes_Case_Insensitive_On_Md_Extension()
    {
        var actual = PreviewPathMapper.Map("content/foo/bar.MD", "en");

        Assert.Equal("/en/foo/bar", actual);
    }
}
