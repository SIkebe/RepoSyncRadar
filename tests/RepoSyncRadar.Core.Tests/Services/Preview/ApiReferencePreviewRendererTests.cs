using RepoSyncRadar.Core.Services.Preview;
using System.Text;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Preview;

public sealed class ApiReferencePreviewRendererTests
{
    [Fact]
    public void Renders_Rest_Operations_With_Endpoint_Parameters_And_Statuses()
    {
        const string json = """
            {
              "teams": [{
                "title": "List teams",
                "verb": "get",
                "requestPath": "/orgs/{org}/teams",
                "descriptionHTML": "<p>Lists all teams in an organization.</p>",
                "parameters": [{
                  "name": "org",
                  "in": "path",
                  "required": true,
                  "description": "The organization name.",
                  "schema": { "type": "string" }
                }],
                "bodyParameters": [],
                "statusCodes": [{
                  "httpStatusCode": "200",
                  "description": "OK"
                }]
              }]
            }
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/rest/data/fpt-2022-11-28/teams.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.Contains("# REST API reference: teams", markdown, StringComparison.Ordinal);
        Assert.Contains("<code>GET /orgs/{org}/teams</code>", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "| <code>org</code> | <code>path</code> | yes | The organization name. |",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("| <code>200</code> | OK |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_GraphQl_Types_And_Fields()
    {
        const string json = """
            {
              "objects": [{
                "name": "Workflow",
                "description": "<p>An Actions workflow.</p>",
                "fields": [{
                  "name": "name",
                  "type": "String!",
                  "description": "<p>The workflow name.</p>",
                  "arguments": [{
                    "name": "format",
                    "type": { "name": "String", "id": "string" },
                    "description": "<p>The output format.</p>"
                  }]
                }]
              }]
            }
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/graphql/data/fpt/schema-actions.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.Contains("# GraphQL API reference: actions", markdown, StringComparison.Ordinal);
        Assert.Contains("### Workflow", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "| <code>name</code> | <code>String!</code> | The workflow name. |",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("##### Arguments for <code>name</code>", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "| <code>format</code> | <code>String</code> | The output format. |",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_GraphQl_Queries_And_Mutations()
    {
        const string json = """
            {
              "queries": [{
                "name": "license",
                "type": "License",
                "description": "<p>Look up a license.</p>",
                "args": [{ "name": "key", "type": "String!", "description": "<p>The SPDX ID.</p>" }]
              }],
              "mutations": [{
                "name": "addAssignees",
                "description": "<p>Adds assignees.</p>",
                "inputFields": [{ "name": "input", "type": "AddAssigneesInput!" }],
                "returnFields": [{ "name": "assignable", "type": "Assignable", "description": "<p>The item.</p>" }]
              }]
            }
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/graphql/data/fpt/schema-licenses.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.Contains("## Queries", markdown, StringComparison.Ordinal);
        Assert.Contains("**Returns:** <code>License</code>", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "| <code>key</code> | <code>String!</code> | The SPDX ID. |",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("## Mutations", markdown, StringComparison.Ordinal);
        Assert.Contains("#### Return fields", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "| <code>assignable</code> | <code>Assignable</code> | The item. |",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizes_Heading_Html_From_Reference_Data()
    {
        const string json = """
            {"teams":[{"title":"&lt;img src=x onerror=alert(1)&gt;List teams","verb":"get","requestPath":"/teams","parameters":[],"bodyParameters":[],"statusCodes":[]}]}
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/rest/data/fpt-2022-11-28/teams.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.Contains("### List teams", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Encodes_Raw_Json_Values_Used_In_Code_Contexts()
    {
        const string json = """
            {
              "<img src=x onerror=alert(0)>teams":[{
                "title":"List teams",
                "verb":"get",
                "requestPath":"/teams`<img src=x onerror=alert(1)>",
                "parameters":[{
                  "name":"org`<img src=x onerror=alert(2)>",
                  "in":"path|query",
                  "required":true
                }],
                "statusCodes":[{"httpStatusCode":"200`<img src=x onerror=alert(3)>"}]
              }]
            }
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/rest/data/fpt-2022-11-28/teams.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.DoesNotContain("<img", markdown, StringComparison.Ordinal);
        Assert.Contains("&lt;img", markdown, StringComparison.Ordinal);
        Assert.Contains("<code>path&#124;query</code>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Encodes_GraphQl_Member_Values_Used_In_Code_Contexts()
    {
        const string json = """
            {
              "objects":[{
                "name":"Workflow",
                "fields":[{
                  "name":"name`<img src=x onerror=alert(1)>",
                  "type":"String|Int`<img src=x onerror=alert(2)>"
                }]
              }]
            }
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/graphql/data/fpt/schema-actions.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.DoesNotContain("<img", markdown, StringComparison.Ordinal);
        Assert.Contains("&lt;img", markdown, StringComparison.Ordinal);
        Assert.Contains("String&#124;Int", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Encodes_Path_Derived_Descriptor_Values()
    {
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData(
                "src/graphql/data/fpt/schema-`<img src=x onerror=alert(1)>.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, """{"objects":[]}""");

        Assert.DoesNotContain("<img", markdown, StringComparison.Ordinal);
        Assert.Contains("&lt;img", markdown, StringComparison.Ordinal);
        Assert.Contains("<code>/en/graphql/reference/", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_Rest_Subcategory_With_Unsupported_Shape()
    {
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/rest/data/fpt-2022-11-28/teams.json"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => ApiReferencePreviewRenderer.BuildMarkdown(descriptor, """{"teams":{"title":"List teams"}}"""));

        Assert.Contains("'teams' must be a JSON array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_GraphQl_Section_With_Unsupported_Shape()
    {
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/graphql/data/fpt/schema-actions.json"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => ApiReferencePreviewRenderer.BuildMarkdown(descriptor, """{"objects":{"name":"Workflow"}}"""));

        Assert.Contains("'objects' must be a JSON array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Omits_Unchanged_Rest_Subcategory_Headings()
    {
        const string beforeJson = """
            {
              "unchanged-group":[{"title":"Same","verb":"get","requestPath":"/same","descriptionHTML":"<p>Same.</p>"}],
              "changed-group":[{"title":"Changed","verb":"get","requestPath":"/changed","descriptionHTML":"<p>Old.</p>"}]
            }
            """;
        const string afterJson = """
            {
              "unchanged-group":[{"title":"Same","verb":"get","requestPath":"/same","descriptionHTML":"<p>Same.</p>"}],
              "changed-group":[{"title":"Changed","verb":"get","requestPath":"/changed","descriptionHTML":"<p>New.</p>"}]
            }
            """;

        var html = ApiReferencePreviewRenderer.RenderDocument(
            "src/rest/data/fpt-2022-11-28/teams.json",
            afterJson,
            "headsha",
            "PR HEAD API reference",
            beforeJson,
            MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("Changed group</h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Unchanged group</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Omits_Unchanged_GraphQl_Section_Headings()
    {
        const string beforeJson = """
            {
              "queries":[{"name":"same","description":"<p>Same.</p>"}],
              "objects":[{"name":"Changed","description":"<p>Old.</p>"}]
            }
            """;
        const string afterJson = """
            {
              "queries":[{"name":"same","description":"<p>Same.</p>"}],
              "objects":[{"name":"Changed","description":"<p>New.</p>"}]
            }
            """;

        var html = ApiReferencePreviewRenderer.RenderDocument(
            "src/graphql/data/fpt/schema-actions.json",
            afterJson,
            "headsha",
            "PR HEAD API reference",
            beforeJson,
            MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("Objects</h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Queries</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDocument_Shows_Raw_Fallback_When_Rendered_And_Metadata_Fields_Change()
    {
        const string beforeJson = """
            {"objects":[{"name":"Workflow","description":"<p>Old description.</p>","isDeprecated":false}]}
            """;
        const string afterJson = """
            {"objects":[{"name":"Workflow","description":"<p>New description.</p>","isDeprecated":true}]}
            """;

        var html = ApiReferencePreviewRenderer.RenderDocument(
            "src/graphql/data/fpt/schema-actions.json",
            afterJson,
            "headsha",
            "PR HEAD API reference",
            beforeJson,
            MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("Generated metadata", html, StringComparison.Ordinal);
        Assert.Contains("isDeprecated", html, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolves_Rest_Official_Url_To_Changed_Subcategory()
    {
        const string beforeJson = """
            {"enterprise-team-members":[{"title":"List teams","verb":"get","requestPath":"/teams","descriptionHTML":"<p>Old.</p>","parameters":[],"bodyParameters":[],"statusCodes":[]}]}
            """;
        const string afterJson = """
            {"enterprise-team-members":[{"title":"List teams","verb":"get","requestPath":"/teams","descriptionHTML":"<p>New.</p>","parameters":[],"bodyParameters":[],"statusCodes":[]}]}
            """;

        var url = ApiReferencePreviewRenderer.ResolveOfficialUrl(
            "src/rest/data/fpt-2022-11-28/enterprise-teams.json",
            beforeJson,
            afterJson);

        Assert.Equal(
            "https://docs.github.com/en/rest/enterprise-teams/enterprise-team-members?apiVersion=2022-11-28",
            url.AbsoluteUri);
    }

    [Fact]
    public void Resolves_GraphQl_Official_Url_With_Docs_Version()
    {
        var url = ApiReferencePreviewRenderer.ResolveOfficialUrl(
            "src/graphql/data/ghes-3.21/schema-actions.json",
            beforeJson: null,
            afterJson: """{"objects":[]}""");

        Assert.Equal(
            "https://docs.github.com/en/enterprise-server@3.21/graphql/reference/actions",
            url.AbsoluteUri);
    }

    [Fact]
    public void RenderDocument_Highlights_Api_Reference_Changes()
    {
        const string beforeJson = """
            {"teams":[{"title":"List teams","verb":"get","requestPath":"/teams","descriptionHTML":"<p>Old description.</p>","parameters":[],"bodyParameters":[],"statusCodes":[]}]}
            """;
        const string afterJson = """
            {"teams":[{"title":"List teams","verb":"get","requestPath":"/teams","descriptionHTML":"<p>New description.</p>","parameters":[],"bodyParameters":[],"statusCodes":[]}]}
            """;

        var html = ApiReferencePreviewRenderer.RenderDocument(
            "src/rest/data/fpt-2022-11-28/teams.json",
            afterJson,
            "headsha",
            "PR HEAD API reference",
            beforeJson,
            MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("New", html, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderComparison_Produces_Both_Documents_From_One_Comparison()
    {
        const string beforeJson = """
            {"teams":[{"title":"List teams","verb":"get","requestPath":"/teams","descriptionHTML":"<p>Old description.</p>"}]}
            """;
        const string afterJson = """
            {"teams":[{"title":"List teams","verb":"get","requestPath":"/teams","descriptionHTML":"<p>New description.</p>"}]}
            """;

        var comparison = ApiReferencePreviewRenderer.RenderComparison(
            "src/rest/data/fpt-2022-11-28/teams.json",
            beforeJson,
            "basesha",
            "Before API reference",
            "src/rest/data/fpt-2022-11-28/teams.json",
            afterJson,
            "headsha",
            "PR HEAD API reference");

        Assert.Contains("Old", comparison.BeforeHtml, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-removed", comparison.BeforeHtml, StringComparison.Ordinal);
        Assert.Contains("New", comparison.AfterHtml, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", comparison.AfterHtml, StringComparison.Ordinal);
        Assert.Equal(
            "https://docs.github.com/en/rest/teams/teams?apiVersion=2022-11-28",
            comparison.OfficialUrl.AbsoluteUri);
    }

    [Fact]
    public void RenderDocument_Uses_Semantic_Markers_For_Large_Reference_Diffs()
    {
        var beforeJson = BuildLargeGraphQlJson("Old");
        var afterJson = BuildLargeGraphQlJson("New");

        var html = ApiReferencePreviewRenderer.RenderDocument(
            "src/graphql/data/fpt/schema-actions.json",
            afterJson,
            "headsha",
            "PR HEAD API reference",
            beforeJson,
            MarkdownPreviewRenderer.RenderedMarkdownDiffSide.After);

        Assert.Contains("Changed API reference entry", html, StringComparison.Ordinal);
        Assert.Contains("rsr-rendered-diff-added", html, StringComparison.Ordinal);
        Assert.Contains("New description 699", html, StringComparison.Ordinal);
    }

    private static string BuildLargeGraphQlJson(string descriptionPrefix)
    {
        var json = new StringBuilder("""{"objects":[""");
        for (var index = 0; index < 700; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }
            json.Append("""{"name":"Type""")
                .Append(index)
                .Append("\",\"description\":\"<p>")
                .Append(descriptionPrefix)
                .Append(" description ")
                .Append(index)
                .Append("</p>\",\"fields\":[{\"name\":\"value\",\"type\":\"String\",\"description\":\"<p>Value ")
                .Append(index)
                .Append(".</p>\"}]}");
        }
        return json.Append("]}").ToString();
    }
}
