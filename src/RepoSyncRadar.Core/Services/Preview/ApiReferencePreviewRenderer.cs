using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoSyncRadar.Core.Services.Preview;

/// <summary>
/// Projects github/docs generated REST and GraphQL JSON into the same article
/// shell and rendered-diff pipeline used by Markdown previews.
/// </summary>
internal static partial class ApiReferencePreviewRenderer
{
    private static readonly JsonSerializerOptions _indentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string[] _graphQlSections =
    [
        "queries",
        "mutations",
        "objects",
        "interfaces",
        "unions",
        "enums",
        "inputObjects",
        "scalars",
    ];

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();

    public static string RenderDocument(
        string repoPath,
        string? json,
        string sha,
        string label,
        string? diffAgainstJson,
        MarkdownPreviewRenderer.RenderedMarkdownDiffSide diffSide)
    {
        var descriptor = PreviewPathMapper.MapApiReferenceData(repoPath)
            ?? throw new InvalidOperationException($"'{repoPath}' は API reference data ではありません。");
        var changedEntries = FindChangedEntries(descriptor, json, diffAgainstJson);
        var entriesWithChangedMetadata = FindEntriesWithChangedMetadata(
            descriptor,
            json,
            diffAgainstJson,
            changedEntries);
        var markdown = BuildMarkdown(
            descriptor,
            json,
            changedEntries,
            entriesWithChangedMetadata,
            markerClass: null);
        var comparisonMarkdown = BuildMarkdown(
            descriptor,
            diffAgainstJson,
            changedEntries,
            entriesWithChangedMetadata,
            markerClass: null);
        if (CountLines(markdown) > 4_000 || CountLines(comparisonMarkdown) > 4_000)
        {
            var markerClass = diffSide == MarkdownPreviewRenderer.RenderedMarkdownDiffSide.Before
                ? "rsr-rendered-diff-removed"
                : "rsr-rendered-diff-added";
            markdown = BuildMarkdown(
                descriptor,
                json,
                changedEntries,
                entriesWithChangedMetadata,
                markerClass);
            comparisonMarkdown = null;
            diffSide = MarkdownPreviewRenderer.RenderedMarkdownDiffSide.None;
        }
        return MarkdownPreviewRenderer.RenderDocument(
            repoPath,
            markdown,
            sha,
            label,
            diffAgainstMarkdown: comparisonMarkdown,
            diffAgainstRepoPath: repoPath,
            diffSide: diffSide);
    }

    internal static string BuildMarkdown(ApiReferencePreviewDescriptor descriptor, string? json)
        => BuildMarkdown(
            descriptor,
            json,
            changedEntries: null,
            entriesWithChangedMetadata: null,
            markerClass: null);

    internal static Uri ResolveOfficialUrl(
        string repoPath,
        string? beforeJson,
        string? afterJson)
    {
        var descriptor = PreviewPathMapper.MapApiReferenceData(repoPath)
            ?? throw new InvalidOperationException($"'{repoPath}' は API reference data ではありません。");
        var versionPath = descriptor.Version switch
        {
            "fpt" => "/en",
            "ghec" => "/en/enterprise-cloud@latest",
            _ when descriptor.Version.StartsWith("ghes-", StringComparison.Ordinal)
                => "/en/enterprise-server@" + descriptor.Version["ghes-".Length..],
            _ => "/en",
        };
        if (descriptor.Kind == ApiReferenceKind.GraphQl)
        {
            return new Uri($"https://docs.github.com{versionPath}/graphql/reference/{descriptor.Category}");
        }

        var changedEntries = FindChangedEntries(descriptor, beforeJson, afterJson);
        var subcategory = FindFirstChangedRestSubcategory(afterJson, changedEntries)
            ?? FindFirstChangedRestSubcategory(beforeJson, changedEntries)
            ?? descriptor.Category;
        var path = $"https://docs.github.com{versionPath}/rest/{descriptor.Category}/{subcategory}";
        return descriptor.ApiVersion is null
            ? new Uri(path)
            : new Uri($"{path}?apiVersion={Uri.EscapeDataString(descriptor.ApiVersion)}");
    }

    private static string BuildMarkdown(
        ApiReferencePreviewDescriptor descriptor,
        string? json,
        HashSet<string>? changedEntries,
        HashSet<string>? entriesWithChangedMetadata,
        string? markerClass)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(json))
        {
            return BuildHeader(descriptor)
                .AppendLine()
                .AppendLine("_This reference page does not exist in this revision._")
                .ToString();
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("API reference data のルートは JSON object である必要があります。");
        }

        var markdown = BuildHeader(descriptor);
        if (changedEntries is not null)
        {
            markdown.AppendLine()
                .AppendLine("> Showing only API reference entries whose generated data changed.");
        }
        if (descriptor.Kind == ApiReferenceKind.Rest)
        {
            AppendRestReference(
                markdown,
                document.RootElement,
                changedEntries,
                entriesWithChangedMetadata,
                markerClass);
        }
        else
        {
            AppendGraphQlReference(
                markdown,
                document.RootElement,
                changedEntries,
                entriesWithChangedMetadata,
                markerClass);
        }
        return markdown.ToString();
    }

    private static StringBuilder BuildHeader(ApiReferencePreviewDescriptor descriptor)
    {
        var kind = descriptor.Kind == ApiReferenceKind.Rest ? "REST API" : "GraphQL API";
        var markdown = new StringBuilder()
            .Append("# ").Append(kind).Append(" reference: ").AppendLine(descriptor.Category)
            .AppendLine()
            .Append("> Local rendering of the generated github/docs reference data.")
            .AppendLine()
            .Append("- Documentation version: `").Append(descriptor.Version).AppendLine("`");
        if (descriptor.ApiVersion is not null)
        {
            markdown.Append("- REST API version: `").Append(descriptor.ApiVersion).AppendLine("`");
        }
        markdown.Append("- Official path: `").Append(descriptor.OfficialPath).AppendLine("`");
        return markdown;
    }

    private static void AppendRestReference(
        StringBuilder markdown,
        JsonElement root,
        HashSet<string>? changedEntries,
        HashSet<string>? entriesWithChangedMetadata,
        string? markerClass)
    {
        foreach (var subcategory in root.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (subcategory.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var headingWritten = false;
            foreach (var operation in subcategory.Value.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = GetString(operation, "title") ?? "Untitled operation";
                var verb = GetString(operation, "verb")?.ToUpperInvariant() ?? "HTTP";
                var requestPath = GetString(operation, "requestPath") ?? "/";
                var entryKey = BuildRestEntryKey(subcategory.Name, verb, requestPath);
                if (changedEntries is not null && !changedEntries.Contains(entryKey))
                {
                    continue;
                }

                if (!headingWritten)
                {
                    markdown.AppendLine().Append("## ")
                        .AppendLine(ToPlainText(Humanize(subcategory.Name)));
                    headingWritten = true;
                }
                AppendRestOperation(markdown, operation, title, verb, requestPath, markerClass);
                if (entriesWithChangedMetadata?.Contains(entryKey) == true)
                {
                    AppendRawMetadataFallback(markdown, operation);
                }
            }
        }
    }

    private static void AppendRestOperation(
        StringBuilder markdown,
        JsonElement operation,
        string title,
        string verb,
        string requestPath,
        string? markerClass)
    {
        markdown.AppendLine().Append("### ").AppendLine(ToPlainText(title))
            .AppendLine()
            .AppendLine(RenderCode(string.Concat(verb, " ", requestPath)));
        AppendSemanticChangeMarker(markdown, markerClass);
        AppendDescription(markdown, GetString(operation, "descriptionHTML"));
        AppendRestParameters(markdown, operation, "parameters", "Parameters");
        AppendRestParameters(markdown, operation, "bodyParameters", "Body parameters");
        AppendStatusCodes(markdown, operation);
    }

    private static void AppendRestParameters(
        StringBuilder markdown,
        JsonElement operation,
        string propertyName,
        string heading)
    {
        if (!operation.TryGetProperty(propertyName, out var parameters)
            || parameters.ValueKind != JsonValueKind.Array
            || parameters.GetArrayLength() == 0)
        {
            return;
        }

        markdown.AppendLine().Append("#### ").AppendLine(heading)
            .AppendLine()
            .AppendLine("| Name | Location / type | Required | Description |")
            .AppendLine("| --- | --- | --- | --- |");
        foreach (var parameter in parameters.EnumerateArray())
        {
            AppendRestParameter(markdown, parameter, parentName: null);
        }
    }

    private static void AppendRestParameter(StringBuilder markdown, JsonElement parameter, string? parentName)
    {
        var localName = GetString(parameter, "name") ?? string.Empty;
        var name = string.IsNullOrEmpty(parentName) ? localName : parentName + "." + localName;
        var location = GetString(parameter, "in") ?? GetString(parameter, "type") ?? GetSchemaType(parameter);
        var required = GetBoolean(parameter, "required") || GetBoolean(parameter, "isRequired") ? "yes" : "no";
        var description = GetString(parameter, "description");
        markdown.Append("| ").Append(RenderCode(name)).Append(" | ")
            .Append(RenderCode(location)).Append(" | ").Append(required).Append(" | ")
            .Append(EscapeTable(ToPlainText(description))).AppendLine(" |");

        if (parameter.TryGetProperty("childParamsGroups", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                AppendRestParameter(markdown, child, name);
            }
        }
    }

    private static void AppendStatusCodes(StringBuilder markdown, JsonElement operation)
    {
        if (!operation.TryGetProperty("statusCodes", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array
            || statuses.GetArrayLength() == 0)
        {
            return;
        }

        markdown.AppendLine().AppendLine("#### HTTP response status codes")
            .AppendLine()
            .AppendLine("| Status | Description |")
            .AppendLine("| --- | --- |");
        foreach (var status in statuses.EnumerateArray())
        {
            markdown.Append("| ")
                .Append(RenderCode(GetString(status, "httpStatusCode") ?? string.Empty))
                .Append(" | ").Append(EscapeTable(ToPlainText(GetString(status, "description"))))
                .AppendLine(" |");
        }
    }

    private static void AppendGraphQlReference(
        StringBuilder markdown,
        JsonElement root,
        HashSet<string>? changedEntries,
        HashSet<string>? entriesWithChangedMetadata,
        string? markerClass)
    {
        foreach (var sectionName in _graphQlSections)
        {
            if (!root.TryGetProperty(sectionName, out var section)
                || section.ValueKind != JsonValueKind.Array
                || section.GetArrayLength() == 0)
            {
                continue;
            }

            var headingWritten = false;
            foreach (var entity in section.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entityName = GetString(entity, "name") ?? "Unnamed type";
                var entryKey = BuildGraphQlEntryKey(sectionName, entityName);
                if (changedEntries is not null && !changedEntries.Contains(entryKey))
                {
                    continue;
                }

                if (!headingWritten)
                {
                    markdown.AppendLine().Append("## ").AppendLine(Humanize(sectionName));
                    headingWritten = true;
                }
                AppendGraphQlEntity(markdown, entity, entityName, markerClass);
                if (entriesWithChangedMetadata?.Contains(entryKey) == true)
                {
                    AppendRawMetadataFallback(markdown, entity);
                }
            }
        }
    }

    private static void AppendGraphQlEntity(
        StringBuilder markdown,
        JsonElement entity,
        string entityName,
        string? markerClass)
    {
        markdown.AppendLine().Append("### ").AppendLine(ToPlainText(entityName));
        AppendSemanticChangeMarker(markdown, markerClass);
        AppendDescription(markdown, GetString(entity, "description"));
        AppendGraphQlMembers(markdown, entity, "args", "Arguments");
        AppendGraphQlMembers(markdown, entity, "fields", "Fields");
        AppendGraphQlMembers(markdown, entity, "inputFields", "Input fields");
        AppendGraphQlMembers(markdown, entity, "returnFields", "Return fields");
        AppendGraphQlMembers(markdown, entity, "values", "Values");
        AppendGraphQlMembers(markdown, entity, "possibleTypes", "Possible types");
    }

    private static void AppendGraphQlMembers(
        StringBuilder markdown,
        JsonElement entity,
        string propertyName,
        string heading)
    {
        if (!entity.TryGetProperty(propertyName, out var members)
            || members.ValueKind != JsonValueKind.Array
            || members.GetArrayLength() == 0)
        {
            return;
        }

        markdown.AppendLine().Append("#### ").AppendLine(heading)
            .AppendLine()
            .AppendLine("| Name | Type | Description |")
            .AppendLine("| --- | --- | --- |");
        foreach (var member in members.EnumerateArray())
        {
            markdown.Append("| ").Append(RenderCode(GetString(member, "name") ?? string.Empty))
                .Append(" | ").Append(RenderCode(GetString(member, "type") ?? string.Empty))
                .Append(" | ").Append(EscapeTable(ToPlainText(GetString(member, "description"))))
                .AppendLine(" |");
        }
    }

    private static void AppendDescription(StringBuilder markdown, string? html)
    {
        var description = ToPlainText(html);
        if (description.Length > 0)
        {
            markdown.AppendLine().AppendLine(description);
        }
    }

    private static string GetSchemaType(JsonElement element)
    {
        if (!element.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type))
        {
            return string.Empty;
        }
        return type.ValueKind == JsonValueKind.Array
            ? string.Join(" | ", type.EnumerateArray().Select(static item => item.GetString()))
            : type.GetString() ?? string.Empty;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();

    private static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        var decoded = WebUtility.HtmlDecode(html);
        var withoutTags = HtmlTagRegex().Replace(decoded, " ");
        var normalized = WhiteSpaceRegex().Replace(withoutTags, " ").Trim();
        return WebUtility.HtmlEncode(normalized);
    }

    private static HashSet<string> FindEntriesWithChangedMetadata(
        ApiReferencePreviewDescriptor descriptor,
        string? currentJson,
        string? comparisonJson,
        HashSet<string> changedEntries)
    {
        var current = BuildUnrenderedMetadataMap(descriptor, currentJson);
        var comparison = BuildUnrenderedMetadataMap(descriptor, comparisonJson);
        return changedEntries
            .Where(key =>
                !string.Equals(
                    current.GetValueOrDefault(key, string.Empty),
                    comparison.GetValueOrDefault(key, string.Empty),
                    StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> BuildUnrenderedMetadataMap(
        ApiReferencePreviewDescriptor descriptor,
        string? json)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return entries;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return entries;
        }

        if (descriptor.Kind == ApiReferenceKind.Rest)
        {
            foreach (var subcategory in document.RootElement.EnumerateObject())
            {
                if (subcategory.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var operation in subcategory.Value.EnumerateArray())
                {
                    if (operation.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var verb = GetString(operation, "verb")?.ToUpperInvariant() ?? "HTTP";
                    var requestPath = GetString(operation, "requestPath") ?? "/";
                    entries[BuildRestEntryKey(subcategory.Name, verb, requestPath)] =
                        GetRestUnrenderedMetadata(operation);
                }
            }
        }
        else
        {
            foreach (var sectionName in _graphQlSections)
            {
                if (!document.RootElement.TryGetProperty(sectionName, out var section)
                    || section.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var entity in section.EnumerateArray())
                {
                    if (entity.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    entries[BuildGraphQlEntryKey(
                        sectionName,
                        GetString(entity, "name") ?? "Unnamed type")] =
                        GetGraphQlUnrenderedMetadata(entity);
                }
            }
        }
        return entries;
    }

    private static string GetRestUnrenderedMetadata(JsonElement operation)
    {
        var metadata = ParseObject(operation);
        RemoveProperties(metadata, "title", "verb", "requestPath", "descriptionHTML");
        StripObjectArray(metadata, "parameters", StripRestParameterMetadata);
        StripObjectArray(metadata, "bodyParameters", StripRestParameterMetadata);
        StripObjectArray(
            metadata,
            "statusCodes",
            static status => RemoveProperties(status, "httpStatusCode", "description"));
        return SerializeMetadata(metadata);
    }

    private static void StripRestParameterMetadata(JsonObject parameter)
    {
        RemoveProperties(parameter, "name", "in", "type", "required", "isRequired", "description");
        if (parameter["schema"] is JsonObject schema)
        {
            schema.Remove("type");
            if (schema.Count == 0)
            {
                parameter.Remove("schema");
            }
        }
        StripObjectArray(parameter, "childParamsGroups", StripRestParameterMetadata);
    }

    private static string GetGraphQlUnrenderedMetadata(JsonElement entity)
    {
        var metadata = ParseObject(entity);
        RemoveProperties(metadata, "name", "description");
        foreach (var propertyName in new[]
                 {
                     "args",
                     "fields",
                     "inputFields",
                     "returnFields",
                     "values",
                     "possibleTypes",
                 })
        {
            StripObjectArray(
                metadata,
                propertyName,
                static member => RemoveProperties(member, "name", "type", "description"));
        }
        return SerializeMetadata(metadata);
    }

    private static JsonObject ParseObject(JsonElement element)
        => JsonNode.Parse(element.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("API reference entry must be a JSON object.");

    private static void StripObjectArray(
        JsonObject parent,
        string propertyName,
        Action<JsonObject> stripRenderedProperties)
    {
        if (parent[propertyName] is not JsonArray items)
        {
            return;
        }

        var hasMetadata = false;
        foreach (var item in items)
        {
            if (item is JsonObject itemObject)
            {
                stripRenderedProperties(itemObject);
                hasMetadata |= itemObject.Count > 0;
            }
            else if (item is not null)
            {
                hasMetadata = true;
            }
        }
        if (!hasMetadata)
        {
            parent.Remove(propertyName);
        }
    }

    private static void RemoveProperties(JsonObject value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            value.Remove(propertyName);
        }
    }

    private static string SerializeMetadata(JsonObject metadata)
        => metadata.Count == 0 ? string.Empty : metadata.ToJsonString();

    private static void AppendRawMetadataFallback(StringBuilder markdown, JsonElement entry)
    {
        markdown.AppendLine()
            .AppendLine("#### Generated metadata")
            .AppendLine()
            .AppendLine("Fields outside the rendered reference changed; the generated entry is shown below.")
            .AppendLine()
            .AppendLine("```json")
            .AppendLine(JsonSerializer.Serialize(entry, _indentedJsonOptions))
            .AppendLine("```");
    }

    private static HashSet<string> FindChangedEntries(
            ApiReferencePreviewDescriptor descriptor,
            string? currentJson,
            string? comparisonJson)
        {
            var current = BuildEntryMap(descriptor, currentJson);
            var comparison = BuildEntryMap(descriptor, comparisonJson);
            var changed = new HashSet<string>(current.Keys, StringComparer.Ordinal);
            changed.UnionWith(comparison.Keys);
            changed.RemoveWhere(key =>
                current.TryGetValue(key, out var currentValue)
                && comparison.TryGetValue(key, out var comparisonValue)
                && string.Equals(currentValue, comparisonValue, StringComparison.Ordinal));
            return changed;
        }

    private static Dictionary<string, string> BuildEntryMap(
            ApiReferencePreviewDescriptor descriptor,
            string? json)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json))
            {
                return entries;
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            if (descriptor.Kind == ApiReferenceKind.Rest)
            {
                foreach (var subcategory in document.RootElement.EnumerateObject())
                {
                    if (subcategory.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var operation in subcategory.Value.EnumerateArray())
                    {
                        var verb = GetString(operation, "verb")?.ToUpperInvariant() ?? "HTTP";
                        var requestPath = GetString(operation, "requestPath") ?? "/";
                        entries[BuildRestEntryKey(subcategory.Name, verb, requestPath)] = operation.GetRawText();
                    }
                }
            }
            else
            {
                foreach (var sectionName in _graphQlSections)
                {
                    if (!document.RootElement.TryGetProperty(sectionName, out var section)
                        || section.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var entity in section.EnumerateArray())
                    {
                        entries[BuildGraphQlEntryKey(sectionName, GetString(entity, "name") ?? "Unnamed type")] =
                            entity.GetRawText();
                    }
                }
            }
            return entries;
        }

    private static string? FindFirstChangedRestSubcategory(
        string? json,
        HashSet<string> changedEntries)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var subcategory in document.RootElement
                     .EnumerateObject()
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            if (subcategory.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var operation in subcategory.Value.EnumerateArray())
            {
                var verb = GetString(operation, "verb")?.ToUpperInvariant() ?? "HTTP";
                var requestPath = GetString(operation, "requestPath") ?? "/";
                if (changedEntries.Contains(BuildRestEntryKey(subcategory.Name, verb, requestPath)))
                {
                    return subcategory.Name;
                }
            }
        }
        return null;
    }

    private static string BuildRestEntryKey(string subcategory, string verb, string requestPath)
        => string.Concat("rest:", subcategory, ":", verb, ":", requestPath);

    private static string BuildGraphQlEntryKey(string section, string name)
        => string.Concat("graphql:", section, ":", name);

    private static void AppendSemanticChangeMarker(StringBuilder markdown, string? markerClass)
    {
        if (markerClass is not null)
        {
            markdown.AppendLine()
                .Append("<span class=\"").Append(markerClass)
                .AppendLine("\">Changed API reference entry</span>");
        }
    }

    private static int CountLines(string text)
        => text.Count(static character => character == '\n') + 1;

    private static string Humanize(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append(' ');
            }
            builder.Append(character is '-' or '_' ? ' ' : character);
        }
        if (builder.Length > 0)
        {
            builder[0] = char.ToUpperInvariant(builder[0]);
        }
        return builder.ToString();
    }

    private static string EscapeInline(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

    private static string RenderCode(string value)
        => string.Concat(
            "<code>",
            WebUtility.HtmlEncode(EscapeInline(value)).Replace("|", "&#124;", StringComparison.Ordinal),
            "</code>");

    private static string EscapeTable(string value)
        => EscapeInline(value).Replace("|", "\\|", StringComparison.Ordinal);
}
