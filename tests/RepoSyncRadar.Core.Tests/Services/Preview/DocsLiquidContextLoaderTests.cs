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

    private string WriteDataFile(string relativePath, string yaml)
    {
        var path = Path.Combine(_root, "data", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private string WriteContentFile(string relativePath, string markdown)
    {
        var path = Path.Combine(_root, "content", relativePath);
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
    public async Task LoadForMarkdownAsync_Loads_Only_Referenced_Reusables_Recursively()
    {
        WriteVariablesFile("product.yml", "prodname: GitHub");
        WriteReusablesFile(Path.Combine("copilot", "outer.md"), "Outer {% data variables.product.prodname %} {% data reusables.copilot.inner %}");
        WriteReusablesFile(Path.Combine("copilot", "inner.md"), "Inner");
        WriteReusablesFile(Path.Combine("copilot", "unused.md"), "Unused");

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/copilot/example.md",
            "Body {% data reusables.copilot.outer %}",
            TestContext.Current.CancellationToken);

        Assert.Equal("GitHub", context.Variables["product.prodname"]);
        Assert.Equal("Outer {% data variables.product.prodname %} {% data reusables.copilot.inner %}", context.Reusables["copilot.outer"]);
        Assert.Equal("Inner", context.Reusables["copilot.inner"]);
        Assert.DoesNotContain("copilot.unused", context.Reusables.Keys);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Reusables_With_Underscore_Directory()
    {
        WriteReusablesFile(Path.Combine("audit_log", "audit-log-enterprise-export-limit.md"), "Export limit details.");

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/admin/monitoring-activity-in-your-enterprise/reviewing-audit-logs-for-your-enterprise/exporting-audit-log-activity-for-your-enterprise.md",
            "## Export limits\n\n{% data reusables.audit_log.audit-log-enterprise-export-limit %}",
            TestContext.Current.CancellationToken);

        Assert.Equal("Export limit details.", context.Reusables["audit_log.audit-log-enterprise-export-limit"]);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Only_Referenced_Variable_Files()
    {
        WriteVariablesFile("product.yml", "prodname: GitHub");
        WriteVariablesFile("unused.yml", "value: Unused");

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/actions/example.md",
            "Use {% data variables.product.prodname %} and {{ site.data.variables.product.prodname }}.",
            TestContext.Current.CancellationToken);

        Assert.Equal("GitHub", context.Variables["product.prodname"]);
        Assert.DoesNotContain("unused.value", context.Variables.Keys);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Variable_Files_Referenced_By_Variable_Value()
    {
        WriteVariablesFile(
            "product.yml",
            """
            security_products: 'GitHub Code Quality ({% data variables.release-phases.public_preview %})'
            """);
        WriteVariablesFile(
            "release-phases.yml",
            """
            public_preview: public preview
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/admin/example.md",
            "{% data variables.product.security_products %}",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "GitHub Code Quality ({% data variables.release-phases.public_preview %})",
            context.Variables["product.security_products"]);
        Assert.Equal("public preview", context.Variables["release-phases.public_preview"]);
        Assert.Equal(
            "GitHub Code Quality (public preview)",
            DocsLiquidEvaluator.Evaluate(
                "{% data variables.product.security_products %}",
                context));
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Only_Referenced_Data_Sequences()
    {
        WriteDataFile(
            Path.Combine("tables", "copilot", "models-and-pricing.yml"),
            """
            - model: GPT-5
              provider: openai
              input: $1.00
            - model: Claude Sonnet
              provider: anthropic
              input: $3.00
            """);
        WriteDataFile(
            Path.Combine("tables", "copilot", "unused.yml"),
            """
            - model: Unused
              provider: unused
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/copilot/reference/models.md",
            "{% for entry in tables.copilot.models-and-pricing %}{{ entry.model }}{% endfor %}",
            TestContext.Current.CancellationToken);

        var rows = context.DataSequences["tables.copilot.models-and-pricing"];
        Assert.Equal(2, rows.Count);
        Assert.Equal("GPT-5", rows[0]["model"]);
        Assert.Equal("openai", rows[0]["provider"]);
        Assert.Equal("$3.00", rows[1]["input"]);
        Assert.DoesNotContain("tables.copilot.unused", context.DataSequences.Keys);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Referenced_Feature_Versions()
    {
        WriteDataFile(
            Path.Combine("features", "enhanced-billing-platform.yml"),
            """
            versions:
              fpt: '*'
              ghec: '*'
              ghes: '>= 3.22'
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/admin/support.md",
            "{% ifversion enhanced-billing-platform %}Enhanced billing link.{% endif %}",
            TestContext.Current.CancellationToken);

        var versions = context.Features["enhanced-billing-platform"];
        Assert.Equal("*", versions["fpt"]);
        Assert.Equal(">= 3.22", versions["ghes"]);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Feature_Versions_Referenced_By_Variable_Value()
    {
        WriteVariablesFile(
            "product.yml",
            """
            prodname_GH_cs_or_sp: '{% ifversion enhanced-billing-platform %}GitHub Secret Protection{% else %}GitHub Advanced Security{% endif %}'
            """);
        WriteDataFile(
            Path.Combine("features", "enhanced-billing-platform.yml"),
            """
            versions:
              fpt: '*'
              ghec: '*'
              ghes: '>= 3.22'
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/admin/support.md",
            "For more information, see {% data variables.product.prodname_GH_cs_or_sp %}.",
            TestContext.Current.CancellationToken);

        var versions = context.Features["enhanced-billing-platform"];
        Assert.Equal("*", versions["fpt"]);
        Assert.Equal(">= 3.22", versions["ghes"]);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Direct_Autotitle_Target()
    {
        WriteContentFile(
            Path.Combine("copilot", "concepts", "billing.md"),
            """
            ---
            title: Copilot billing
            ---

            Target.
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/copilot/reference/models.md",
            "See [AUTOTITLE](/copilot/concepts/billing).",
            TestContext.Current.CancellationToken);

        Assert.Equal("Copilot billing", context.PageTitles["copilot/concepts/billing"]);
        Assert.Equal("Copilot billing", context.PageTitles["content/copilot/concepts/billing.md"]);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Loads_Autotitle_Target_After_Conditional_Version_Prefix()
    {
        WriteContentFile(
            Path.Combine(
                "migrations",
                "using-github-enterprise-importer",
                "migrate-from-gitlab",
                "index.md"),
            """
            ---
            title: Migrating from GitLab to GitHub
            ---

            Target.
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/migrations/overview/migration-paths-to-github.md",
            "[AUTOTITLE]({% ifversion ghes %}/free-pro-team@latest{% endif %}/migrations/using-github-enterprise-importer/migrate-from-gitlab)",
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "Migrating from GitLab to GitHub",
            context.PageTitles["migrations/using-github-enterprise-importer/migrate-from-gitlab"]);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Falls_Back_To_Redirect_Autotitle_Scan()
    {
        WriteContentFile(
            Path.Combine("copilot", "concepts", "new-billing.md"),
            """
            ---
            title: New billing
            redirect_from:
              - /copilot/concepts/old-billing
            ---

            Target.
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/copilot/reference/models.md",
            "See [AUTOTITLE](/copilot/concepts/old-billing).",
            TestContext.Current.CancellationToken);

        Assert.Equal("New billing", context.PageTitles["copilot/concepts/old-billing"]);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Finds_Redirect_Autotitle_In_Route_Near_Subdirectory()
    {
        var source = new RecordingDocsFileSource();
        source.Add(
            "content/actions/reference/workflows-and-actions/expressions.md",
            """
            ---
            title: Evaluate expressions
            redirect_from:
              - /actions/reference/evaluate-expressions-in-workflows-and-actions
            ---

            Target.
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            source,
            "content/actions/reference/workflows-and-actions/workflow-cancellation.md",
            "See [AUTOTITLE](/actions/reference/evaluate-expressions-in-workflows-and-actions#cancelled).",
            TestContext.Current.CancellationToken);

        Assert.Equal("Evaluate expressions", context.PageTitles["actions/reference/evaluate-expressions-in-workflows-and-actions"]);
        Assert.Empty(source.EnumeratedDirectories);
        Assert.Equal(["content/actions/reference"], source.SearchDirectories);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Prioritizes_Redirect_FileName_Hint_In_Broad_Scan()
    {
        var source = new RecordingDocsFileSource();
        source.Add(
            "content/code-security/concepts/unrelated.md",
            """
            ---
            title: Unrelated
            ---
            """);
        source.Add(
            "content/code-security/tutorials/secure-your-organization/best-practices-for-preventing-data-leaks-in-your-organization.md",
            """
            ---
            title: Preventing data leaks
            redirect_from:
              - /code-security/getting-started/best-practices-for-preventing-data-leaks-in-your-organization
            ---
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            source,
            "content/organizations/managing-organization-settings/upgrading-to-the-github-customer-agreement.md",
            "See [AUTOTITLE](/code-security/getting-started/best-practices-for-preventing-data-leaks-in-your-organization).",
            TestContext.Current.CancellationToken);

        Assert.Equal("Preventing data leaks", context.PageTitles["code-security/getting-started/best-practices-for-preventing-data-leaks-in-your-organization"]);
        Assert.Empty(source.EnumeratedDirectories);
        Assert.Contains("content/code-security", source.SearchDirectories);
        Assert.Contains(
            "content/code-security/tutorials/secure-your-organization/best-practices-for-preventing-data-leaks-in-your-organization.md",
            source.ReadPaths);
        Assert.DoesNotContain("content/code-security/concepts/unrelated.md", source.ReadPaths);
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

    [Fact]
    public async Task LoadForMarkdownAsync_Trims_Liquid_Whitespace_Control_From_DataObject_Key()
    {
        WriteDataFile(
            Path.Combine("tables", "example.yml"),
            """
            group:
              name: Example value
            """);

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            _root,
            "content/sample.md",
            "{{ tables.example.group.name -}}",
            TestContext.Current.CancellationToken);

        Assert.True(context.DataObjects.ContainsKey("tables.example.group.name"));
        var value = Assert.IsType<DocsLiquidScalarValue>(context.DataObjects["tables.example.group.name"]);
        Assert.Equal("Example value", value.Value);
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Ignores_Invalid_EnterpriseDates_Json()
    {
        var source = new RecordingDocsFileSource();
        source.Add("src/ghes-releases/lib/enterprise-dates.json", "{ definitely invalid json");
        source.Add("src/versions/lib/enterprise-server-releases.ts", MinimalEnterpriseServerReleasesSource());

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            source,
            "content/admin/all-releases.md",
            "{% for version in enterpriseServerReleases.supported %}{{ version }}{% endfor %}",
            TestContext.Current.CancellationToken);

        Assert.True(context.DataObjects.ContainsKey("enterpriseServerReleases"));
    }

    [Fact]
    public async Task LoadForMarkdownAsync_Reads_NonString_EnterpriseDates_Json_Values()
    {
        var source = new RecordingDocsFileSource();
        source.Add(
            "src/ghes-releases/lib/enterprise-dates.json",
            """
            {
              "3.21": {
                "releaseDate": 123,
                "deprecationDate": false,
                "releaseCandidateDate": "2026-01-01",
                "generalAvailabilityDate": null
              }
            }
            """);
        source.Add("src/versions/lib/enterprise-server-releases.ts", MinimalEnterpriseServerReleasesSource());

        var context = await DocsLiquidContextLoader.LoadForMarkdownAsync(
            source,
            "content/admin/all-releases.md",
            "{{ enterpriseServerReleases.dates[version].releaseDate }}",
            TestContext.Current.CancellationToken);

        var releases = Assert.IsType<DocsLiquidObjectValue>(context.DataObjects["enterpriseServerReleases"]);
        var dates = Assert.IsType<DocsLiquidObjectValue>(releases.Properties["dates"]);
        var version = Assert.IsType<DocsLiquidObjectValue>(dates.Properties["3.21"]);
        Assert.Equal("123", Assert.IsType<DocsLiquidScalarValue>(version.Properties["releaseDate"]).Value);
        Assert.Equal("false", Assert.IsType<DocsLiquidScalarValue>(version.Properties["deprecationDate"]).Value);
        Assert.Equal(string.Empty, Assert.IsType<DocsLiquidScalarValue>(version.Properties["generalAvailabilityDate"]).Value);
    }

    private static string MinimalEnterpriseServerReleasesSource()
        => """
        export const supported = ['3.21']
        export const deprecatedWithFunctionalRedirects = []
        export const deprecated = []
        """;

    private sealed class RecordingDocsFileSource : IDocsFileSource
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> EnumeratedDirectories { get; } = [];

        public List<string> SearchDirectories { get; } = [];

        public List<string> ReadPaths { get; } = [];

        public void Add(string repoPath, string content)
        {
            _files[repoPath] = content;
        }

        public Task<string?> ReadTextAsync(string repoPath, CancellationToken cancellationToken)
        {
            ReadPaths.Add(repoPath);
            _files.TryGetValue(repoPath, out var content);
            return Task.FromResult<string?>(content);
        }

        public Task<IReadOnlyList<string>> EnumerateFilesAsync(
            string repoDirectory,
            string extension,
            CancellationToken cancellationToken)
        {
            EnumeratedDirectories.Add(repoDirectory);
            var prefix = repoDirectory.TrimEnd('/') + "/";
            IReadOnlyList<string> files = _files.Keys
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(files);
        }

        public Task<IReadOnlyList<string>> FindFilesContainingAsync(
            string repoDirectory,
            string text,
            string extension,
            CancellationToken cancellationToken)
        {
            SearchDirectories.Add(repoDirectory);
            var prefix = repoDirectory.TrimEnd('/') + "/";
            IReadOnlyList<string> files = _files
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && pair.Key.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Select(static pair => pair.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(files);
        }
    }
}
