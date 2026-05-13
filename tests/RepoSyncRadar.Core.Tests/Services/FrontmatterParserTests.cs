using RepoSyncRadar.Core.Services.Frontmatter;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services;

public class FrontmatterParserTests
{
    [Fact]
    public void Parse_Returns_Versions_Block_For_Standard_Document()
    {
        const string source =
            "---\n" +
            "title: About Copilot\n" +
            "versions:\n" +
            "  fpt: '*'\n" +
            "---\n" +
            "# Body\n" +
            "Some markdown.\n";

        var data = FrontmatterParser.Parse(source);

        Assert.NotNull(data);
        Assert.Contains("fpt: '*'", data!.VersionsRaw);
        Assert.DoesNotContain("title:", data.VersionsRaw);
    }

    [Fact]
    public void Parse_Returns_Null_When_No_Frontmatter_Present()
    {
        const string source = "# Just a heading\n\nNo fence here.\n";

        var data = FrontmatterParser.Parse(source);

        Assert.Null(data);
    }

    [Fact]
    public void Parse_Throws_FormatException_When_Closing_Fence_Missing()
    {
        const string source =
            "---\n" +
            "title: Broken\n" +
            "versions:\n" +
            "  fpt: '*'\n" +
            "# body without closing ---\n";

        Assert.Throws<FormatException>(() => FrontmatterParser.Parse(source));
    }
}
