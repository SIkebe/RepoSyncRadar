using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

public sealed class MarkdownSourceDiffAnalyzerTests
{
    [Fact]
    public void Analyze_Reports_Removed_Ifversion_And_Related_Feature_Change()
    {
        using var temp = new TemporaryWorktrees();
        temp.WriteBeforeFeature("disable-ghas-button", "versions:\n  fpt: '*'\n  ghec: '*'\n  ghes: '>= 3.21'\n");
        temp.WriteAfterFeature("disable-ghas-button", "versions:\n  fpt: '*'\n  ghec: '*'\n  ghes: '>= 3.22'\n");

        const string before = """
            {% ifversion disable-ghas-button %}
            This guidance is gated.
            {% endif %}
            """;
        const string after = "This guidance is gated.";

        var summary = MarkdownSourceDiffAnalyzer.Analyze(before, after, temp.BeforePath, temp.AfterPath);

        var ifversion = Assert.Single(summary.IfversionChanges);
        Assert.Equal(DocsVersionChangeKind.Removed, ifversion.Kind);
        Assert.Equal("disable-ghas-button", ifversion.BeforeExpression);
        Assert.Null(ifversion.AfterExpression);
        Assert.Equal("This guidance is gated.", ifversion.BeforePreview);
        Assert.Null(ifversion.AfterPreview);
        var feature = Assert.Single(summary.RelatedFileChanges);
        Assert.Equal("data/features/disable-ghas-button.yml", feature.Path);
        Assert.Contains(feature.Changes, change =>
            string.Equals(change.BeforeLine, "  ghes: '>= 3.21'", StringComparison.Ordinal)
            && string.Equals(change.AfterLine, "  ghes: '>= 3.22'", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_Reports_Ifversion_Target_Section_Preview()
    {
        const string before = """
            ## Managing licenses

            {% ifversion disable-ghas-button %}
            ### Disable access

            Users without a license cannot enable Advanced Security.
            Review the billing settings before changing access.
            {% endif %}
            """;

        var summary = MarkdownSourceDiffAnalyzer.Analyze(before, "## Managing licenses");

        var ifversion = Assert.Single(summary.IfversionChanges);
        Assert.Contains("### Disable access", ifversion.BeforePreview, StringComparison.Ordinal);
        Assert.Contains("Users without a license", ifversion.BeforePreview, StringComparison.Ordinal);
        Assert.DoesNotContain("## Managing licenses", ifversion.BeforePreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_Returns_Empty_When_No_Ifversion_Changes_Exist()
    {
        var summary = MarkdownSourceDiffAnalyzer.Analyze("# Same", "# Same");

        Assert.False(summary.HasChanges);
        Assert.Empty(summary.IfversionChanges);
        Assert.Empty(summary.RelatedFileChanges);
    }

    private sealed class TemporaryWorktrees : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rsr-source-diff-" + Guid.NewGuid().ToString("N"));

        public TemporaryWorktrees()
        {
            BeforePath = Path.Combine(_root, "before");
            AfterPath = Path.Combine(_root, "after");
            Directory.CreateDirectory(BeforePath);
            Directory.CreateDirectory(AfterPath);
        }

        public string BeforePath { get; }

        public string AfterPath { get; }

        public void WriteBeforeFeature(string feature, string contents)
            => WriteFeature(BeforePath, feature, contents);

        public void WriteAfterFeature(string feature, string contents)
            => WriteFeature(AfterPath, feature, contents);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void WriteFeature(string worktreePath, string feature, string contents)
        {
            var featuresDir = Path.Combine(worktreePath, "data", "features");
            Directory.CreateDirectory(featuresDir);
            File.WriteAllText(Path.Combine(featuresDir, feature + ".yml"), contents);
        }
    }
}
