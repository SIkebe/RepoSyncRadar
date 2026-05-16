using System.IO;
using RepoSyncRadar.Core.Services.Preview;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

/// <summary>
/// Tests for <see cref="DocsLiquidContextLoader"/> — reads
/// <c>data/variables/**/*.yml</c> and <c>data/reusables/**/*.md</c> from a
/// worktree into a <see cref="DocsLiquidContext"/>
/// (IMPLEMENTATION_PLAN.md §Step 19.8). All cases use a temp directory rooted
/// per-test so they can run in parallel without crosstalk.
/// </summary>
public sealed class DocsLiquidContextLoaderTests : IDisposable
{
    private readonly string _root;

    public DocsLiquidContextLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RsrLiquidLoader_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort; CI cleans up temp dir eventually.
        }
    }

    private string WriteVariablesFile(string name, string yaml)
    {
        var dir = Path.Combine(_root, "data", "variables");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, yaml);
        return path;
    }

    private string WriteReusablesFile(string relativePath, string markdown)
    {
        var path = Path.Combine(_root, "data", "reusables", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown);
        return path;
    }

    [Fact]
    public async Task Returns_Empty_When_Worktree_Does_Not_Exist()
    {
        var context = await DocsLiquidContextLoader.LoadAsync(
            Path.Combine(_root, "missing"),
            TestContext.Current.CancellationToken);

        Assert.Same(DocsLiquidContext.Empty, context);
    }

    [Fact]
    public async Task Returns_Empty_When_Data_Directory_Is_Missing()
    {
        var context = await DocsLiquidContextLoader.LoadAsync(_root, TestContext.Current.CancellationToken);

        Assert.Same(DocsLiquidContext.Empty, context);
    }

    [Fact]
    public async Task Flattens_Top_Level_Variables_With_Filename_Prefix()
    {
        WriteVariablesFile("product.yml",
            """
            prodname: GitHub
            prodname_copilot: GitHub Copilot
            prodname_copilot_short: Copilot
            """);

        var context = await DocsLiquidContextLoader.LoadAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal("GitHub", context.Variables["product.prodname"]);
        Assert.Equal("GitHub Copilot", context.Variables["product.prodname_copilot"]);
        Assert.Equal("Copilot", context.Variables["product.prodname_copilot_short"]);
    }

    [Fact]
    public async Task Flattens_Nested_Mapping_Variables_With_Dot_Path()
    {
        WriteVariablesFile("notices.yml",
            """
            copilot:
              chat_pro: Copilot Chat (Pro)
              chat_business: Copilot Chat (Business)
            """);

        var context = await DocsLiquidContextLoader.LoadAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal("Copilot Chat (Pro)", context.Variables["notices.copilot.chat_pro"]);
        Assert.Equal("Copilot Chat (Business)", context.Variables["notices.copilot.chat_business"]);
    }

    [Fact]
    public async Task Loads_Reusable_Markdown_With_Dot_Path_Key()
    {
        WriteReusablesFile(
            Path.Combine("copilot", "about-copilot.md"),
            "Try GitHub Copilot today.");
        WriteReusablesFile(
            Path.Combine("copilot", "billing", "intro.md"),
            "Manage billing for Copilot.");

        var context = await DocsLiquidContextLoader.LoadAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal("Try GitHub Copilot today.", context.Reusables["copilot.about-copilot"]);
        Assert.Equal("Manage billing for Copilot.", context.Reusables["copilot.billing.intro"]);
    }

    [Fact]
    public async Task Skips_Files_With_Invalid_Yaml_Without_Failing_Whole_Load()
    {
        WriteVariablesFile("good.yml", "key: value");
        WriteVariablesFile("bad.yml",
            """
            key: value
              broken: indent
            : not a valid scalar
            """);

        var context = await DocsLiquidContextLoader.LoadAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal("value", context.Variables["good.key"]);
        // bad.yml is silently skipped — no entries from it survive.
        Assert.DoesNotContain("bad.broken", context.Variables.Keys);
    }
}
