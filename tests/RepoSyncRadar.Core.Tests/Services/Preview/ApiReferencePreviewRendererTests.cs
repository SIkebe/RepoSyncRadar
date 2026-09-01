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
        Assert.Contains("`GET /orgs/{org}/teams`", markdown, StringComparison.Ordinal);
        Assert.Contains("| `org` | `path` | yes | The organization name. |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `200` | OK |", markdown, StringComparison.Ordinal);
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
                  "description": "<p>The workflow name.</p>"
                }]
              }]
            }
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/graphql/data/fpt/schema-actions.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.Contains("# GraphQL API reference: actions", markdown, StringComparison.Ordinal);
        Assert.Contains("### Workflow", markdown, StringComparison.Ordinal);
        Assert.Contains("| `name` | `String!` | The workflow name. |", markdown, StringComparison.Ordinal);
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
        Assert.Contains("| `key` | `String!` | The SPDX ID. |", markdown, StringComparison.Ordinal);
        Assert.Contains("## Mutations", markdown, StringComparison.Ordinal);
        Assert.Contains("#### Return fields", markdown, StringComparison.Ordinal);
        Assert.Contains("| `assignable` | `Assignable` | The item. |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizes_Heading_Html_From_Reference_Data()
    {
        const string json = """
            {"teams":[{"title":"<img src=x onerror=alert(1)>List teams","verb":"get","requestPath":"/teams","parameters":[],"bodyParameters":[],"statusCodes":[]}]}
            """;
        var descriptor = Assert.IsType<ApiReferencePreviewDescriptor>(
            PreviewPathMapper.MapApiReferenceData("src/rest/data/fpt-2022-11-28/teams.json"));

        var markdown = ApiReferencePreviewRenderer.BuildMarkdown(descriptor, json);

        Assert.Contains("### List teams", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror", markdown, StringComparison.Ordinal);
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
