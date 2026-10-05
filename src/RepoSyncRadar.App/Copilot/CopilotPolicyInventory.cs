using System.Globalization;
using System.Text.Json;
using GitHub.Copilot.Rpc;

namespace RepoSyncRadar.App.Copilot;

public sealed record CopilotPolicyValue(string? Text = null, string? ResourceKey = null, string? LabelKey = null);

public sealed record CopilotPolicyEntry(
    string Key,
    string LabelKey,
    IReadOnlyList<CopilotPolicyValue> Values,
    IReadOnlyList<string> Sources,
    bool? Overridable = null,
    bool IsStatus = false);

public sealed record CopilotPolicyGroup(string Key, IReadOnlyList<CopilotPolicyEntry> Entries);

public sealed record CopilotPolicyDiagnostic(string Severity, string? SettingKey, string SummaryKey);

#pragma warning disable GHCP001 // Project only safe display values from the public managed-settings API.
internal static class CopilotPolicyInventory
{
    private const int _maxItems = 32;
    private const string _valuePrefix = "Copilot.Policy.Value.";
    private const string _itemPrefix = "Copilot.Policy.Item.";
    private static readonly string[][] _groupKeys =
    [
        ["model", "autoTier", "effortLevel", "contextTier"],
        ["permissions.allow", "permissions.ask", "permissions.deny", "permissions.disableBypassPermissionsMode"],
        ["sandbox"],
        ["allowedMcpServers", "deniedMcpServers", "strictPluginOnlyCustomization", "allowManagedMcpServersOnly",
            "allowManagedHooksOnly", "features", "enabledPlugins", "extraKnownMarketplaces", "strictKnownMarketplaces"],
        ["remoteControl", "forceLoginOrgs", "forceRemoteSettingsRefresh", "policyHelper", "policyHelperFailureMode", "telemetry"],
    ];
    private static readonly string[] _groupNames = ["Model", "Permissions", "Runtime", "Customization", "RemoteTelemetry"];
    private static readonly HashSet<string> _rootKeys = _groupKeys.SelectMany(k => k)
        .Select(k => k.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> _publicPaths = new(StringComparer.Ordinal)
    {
        "sandbox.enabled", "sandbox.failIfUnavailable", "sandbox.allowBypass", "sandbox.addCurrentWorkingDirectory",
        "sandbox.sandboxMcpServers", "sandbox.sandboxLspServers", "sandbox.auth", "sandbox.auth.git", "sandbox.auth.gh",
        "sandbox.allowDevToolAccess", "sandbox.learningMode", "sandbox.userPolicy",
        "remoteControl.mode", "remoteControl.githubDotComOrganizations", "remoteControl.githubEnterpriseCloudDomains",
        "policyHelper.path", "policyHelper.args", "policyHelper.timeoutMs",
        "telemetry.enabled", "telemetry.endpoint", "telemetry.protocol", "telemetry.headers",
        "telemetry.resourceAttributes", "telemetry.capture", "telemetry.capture.identity",
        "telemetry.capture.prompts", "telemetry.capture.responses", "telemetry.capture.toolArguments",
        "telemetry.capture.toolOutput", "telemetry.capture.policyDetail", "telemetry.captureContent",
        "telemetry.lockCaptureContent", "telemetry.serviceName",
    };
    private static readonly HashSet<string> _safeStrings = new(StringComparer.Ordinal)
    {
        "efficiency", "balance", "intelligence", "fast", "none", "minimal", "low", "medium", "high", "xhigh", "max",
        "default", "long_context", "disable", "deny", "allow", "enabled", "disabled", "requireSSO",
        "skills", "agents", "hooks", "mcp", "grpc", "http/protobuf", "http/json", "github", "git", "directory",
        "failClosed", "ignore",
    };
    private static readonly string[] _permissionKinds = ["shell", "read", "write", "url", "file", "mcp", "*"];
    private static readonly string[] _mcpMatcherKeys = ["serverUrl", "serverCommand", "serverName"];

    internal static CopilotPolicyGroup AppRestrictions
    {
        get
        {
            var permissions = SessionConfigBuilder.CreateManagedSettings().Permissions
                ?? throw new InvalidOperationException("The shared session configuration must define permission restrictions.");
            var deny = permissions.Deny
                ?? throw new InvalidOperationException("The shared session configuration must define denied tools.");
            return new("AppRestrictions",
            [
                new("permissions.disableBypassPermissionsMode", _itemPrefix + "permissions.disableBypassPermissionsMode",
                    [State(permissions.DisableBypassPermissionsMode == GitHub.Copilot.DisableBypassPermissionsModes.Disable ? "Yes" : "Unreported")],
                    ["Client"], IsStatus: true),
                new("permissions.deny", _itemPrefix + "permissions.deny",
                    deny.Select(rule => new CopilotPolicyValue(
                        Text: _permissionKinds.Contains(rule, StringComparer.Ordinal) ? rule : null,
                        ResourceKey: _permissionKinds.Contains(rule, StringComparer.Ordinal) ? null : _valuePrefix + "Hidden")).ToArray(),
                    ["Client"], IsStatus: true),
            ]);
        }
    }

    internal static IReadOnlyList<CopilotPolicyGroup> CreateGroups(ManagedSettingsResolveResult result)
    {
        var settings = result.Resolved.Settings;
        var groups = new List<CopilotPolicyGroup>();
        for (var index = 0; index < _groupKeys.Length; index++)
        {
            var rows = new List<CopilotPolicyEntry>();
            foreach (var key in _groupKeys[index])
            {
                var value = Find(settings, key);
                var meta = key switch { "model" => result.Meta?.Model, "autoTier" => result.Meta?.AutoTier, _ => null };
                var sources = meta is not null ? new[] { Source(meta.Source) } : SourcesFor(result, key);
                if (key == "model" && result.Values?.Model is { } model)
                {
                    value = JsonSerializer.SerializeToElement(model);
                }
                else if (key == "autoTier" && result.Values?.AutoTier is { } tier)
                {
                    value = JsonSerializer.SerializeToElement(tier.Value);
                }
                var row = Entry(key, value, sources) with { Overridable = meta?.Overridable };
                if (key == "permissions.allow" && result.Resolved.PermissionsAllowIntersected == true)
                {
                    // The flattened result deliberately omits intersected allowlists; never call that "none".
                    var lists = new List<CopilotPolicyValue> { State("Intersected") };
                    foreach (var layer in result.Layers.Take(_maxItems))
                    {
                        if (lists.Count >= _maxItems)
                        {
                            MarkTruncated(lists);
                            break;
                        }
                        if (Find(layer.Settings, key) is { } list)
                        {
                            AppendValue(lists, new(ResourceKey: "Copilot.Policy.Source." + Source(layer.Source)));
                            Format(key, list, lists);
                        }
                    }
                    row = row with { Values = lists };
                }
                rows.Add(row);
            }
            if (index == 1)
            {
                rows.Add(Status("status.bypass", result.Resolved.BypassPermissionsDisabled));
                rows.Add(Status("status.intersection", result.Resolved.PermissionsAllowIntersected));
            }
            else if (index == 2)
            {
                rows.Add(Status("status.sandboxFallback", result.Resolved.SandboxEnabledByUndeterminedPolicy));
            }
            else if (index == 4 && settings is { ValueKind: JsonValueKind.Object } root)
            {
                var unknown = root.EnumerateObject().Count(p => !_rootKeys.Contains(p.Name));
                if (unknown > 0)
                {
                    rows.Add(new("unknown", _itemPrefix + "UnknownSettings",
                        [new(unknown.ToString(CultureInfo.InvariantCulture), _valuePrefix + "Hidden")], []));
                }
            }
            groups.Add(new(_groupNames[index], rows));
        }
        var keys = result.Resolved.ManagedKeys.Take(_maxItems).Select(key =>
            _rootKeys.Contains(key) ? new CopilotPolicyValue(Text: key) : State("Hidden")).ToList();
        if (result.Resolved.ManagedKeys.Count > _maxItems)
        {
            MarkTruncated(keys);
        }
        var layers = result.Layers.Take(_maxItems).Select(layer => new CopilotPolicyValue(
            ResourceKey: "Copilot.Policy.Source." + Source(layer.Source))).ToList();
        if (result.Layers.Count > _maxItems)
        {
            MarkTruncated(layers);
        }
        groups.Add(new("Sources",
        [
            new("status.source", _itemPrefix + "status.source", [new(ResourceKey: "Copilot.Policy.Source." + Source(result.Resolved.Source.Value))], [], IsStatus: true),
            Status("status.device", result.Resolved.DeviceManaged),
            Status("status.server", result.Resolved.ServerManaged),
            Status("status.client", result.Resolved.ClientManaged),
            Status("status.helper", result.Resolved.PolicyHelperManaged),
            new("status.keys", _itemPrefix + "status.keys", keys.Count == 0 ? [State("None")] : keys, [], IsStatus: true),
            new("status.layers", _itemPrefix + "status.layers",
                layers.Count == 0 ? [State("None")] : layers, [], IsStatus: true),
        ]));
        return groups;
    }

    internal static IReadOnlyList<CopilotPolicyDiagnostic> CreateDiagnostics(ManagedSettingsResolveResult result)
        => result.Diagnostics.Take(_maxItems).Select(diagnostic =>
        {
            var path = diagnostic.Path;
            var knownPath = path is not null && (_publicPaths.Contains(path)
                || _groupKeys.SelectMany(k => k).Contains(path, StringComparer.Ordinal));
            var message = diagnostic.Message ?? string.Empty;
            var summary = message.Contains("cache", StringComparison.OrdinalIgnoreCase) ? "Cache"
                : message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ? "Timeout"
                : message.Contains("fetch", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("network", StringComparison.OrdinalIgnoreCase) ? "Fetch"
                : message.Contains("valid", StringComparison.OrdinalIgnoreCase) ? "Validation" : "Hidden";
            return new CopilotPolicyDiagnostic(
                diagnostic.Severity.Value switch { "warning" => "Warning", "error" => "Error", _ => "Unknown" },
                knownPath ? path : null, "Copilot.Policy.Diagnostic." + summary);
        }).ToArray();

    private static CopilotPolicyEntry Status(string key, bool? value)
        => new(key, _itemPrefix + key, [State(value switch { true => "Yes", false => "No", null => "Unreported" })], [], IsStatus: true);

    private static CopilotPolicyEntry Entry(string key, JsonElement? value, IReadOnlyList<string> sources)
    {
        var values = new List<CopilotPolicyValue>();
        Format(key, value, values);
        return new(key, _itemPrefix + key, values, sources);
    }

    private static void Format(string key, JsonElement? value, List<CopilotPolicyValue> output, string? label = null)
    {
        if (output.Count >= _maxItems)
        {
            MarkTruncated(output);
            return;
        }
        var startingCount = output.Count;
        if (value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            AppendValue(output, State("Unreported", label));
            return;
        }
        var element = value.Value;
        if (key is "features" or "enabledPlugins" && element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 0)
            {
                AppendValue(output, State("None", label));
                return;
            }
            AppendValue(output, new(properties.Count(p => p.Value.ValueKind == JsonValueKind.True).ToString(CultureInfo.InvariantCulture),
                _valuePrefix + "TrueCount"));
            AppendValue(output, new(properties.Count(p => p.Value.ValueKind == JsonValueKind.False).ToString(CultureInfo.InvariantCulture),
                _valuePrefix + "FalseCount"));
            AppendValue(output, State("IdentifiersHidden"));
            return;
        }
        if (key is "extraKnownMarketplaces" && element.ValueKind == JsonValueKind.Object)
        {
            var count = element.EnumerateObject().Count();
            AppendValue(output, new(count.ToString(CultureInfo.InvariantCulture), _valuePrefix + "EntriesHidden"));
            foreach (var marketplace in element.EnumerateObject().Take(_maxItems))
            {
                if (Find(marketplace.Value, "source.source") is { } source)
                {
                    Format("marketplace.source", source, output, _itemPrefix + "marketplace.source");
                }
                if (Find(marketplace.Value, "autoUpdate") is { } autoUpdate)
                {
                    Format("marketplace.autoUpdate", autoUpdate, output, _itemPrefix + "marketplace.autoUpdate");
                }
            }
            if (count > _maxItems)
            {
                MarkTruncated(output);
            }
            return;
        }
        if (key is "sandbox.userPolicy" or "telemetry.headers" or "telemetry.resourceAttributes"
            or "telemetry.endpoint" or "telemetry.serviceName" or "policyHelper.path" or "policyHelper.args"
            or "forceLoginOrgs" or "remoteControl.githubDotComOrganizations" or "remoteControl.githubEnterpriseCloudDomains")
        {
            var empty = element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0
                || element.ValueKind == JsonValueKind.Object && !element.EnumerateObject().Any()
                || element.ValueKind == JsonValueKind.String && element.GetString() == string.Empty;
            AppendValue(output, State(empty ? "None" : "Hidden", label));
            return;
        }
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            AppendValue(output, State(element.GetBoolean() ? "Yes" : "No", label));
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (key.StartsWith("permissions.", StringComparison.Ordinal) && key != "permissions.disableBypassPermissionsMode")
            {
                var kind = _permissionKinds.FirstOrDefault(k => text == k
                    || text.StartsWith(k + ":", StringComparison.Ordinal)
                    || text.StartsWith(k + "(", StringComparison.Ordinal));
                AppendValue(output, kind is null ? State("Hidden", label)
                    : new(kind, text == kind ? null : _valuePrefix + "ScopeHidden", label));
            }
            else if (key == "model" ? CopilotPolicyDiagnostics.IsDisplayableModel(text) : _safeStrings.Contains(text))
            {
                AppendValue(output, new(Text: text, LabelKey: label));
            }
            else
            {
                AppendValue(output, State("Hidden", label));
            }
        }
        else if (element.ValueKind == JsonValueKind.Number && key == "policyHelper.timeoutMs"
            && element.TryGetInt32(out var timeout) && timeout >= 0)
        {
            AppendValue(output, new(timeout.ToString(CultureInfo.InvariantCulture), LabelKey: label));
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() == 0)
            {
                AppendValue(output, State(key is "permissions.allow" or "allowedMcpServers" or "strictKnownMarketplaces"
                    ? "EmptyAllow" : "None", label));
            }
            else
            {
                foreach (var item in element.EnumerateArray().Take(_maxItems))
                {
                    if (key is "allowedMcpServers" or "deniedMcpServers" or "strictKnownMarketplaces")
                    {
                        var matcher = item.ValueKind == JsonValueKind.Object
                            ? _mcpMatcherKeys.FirstOrDefault(name => item.TryGetProperty(name, out _))
                            : null;
                        AppendValue(output, State("Hidden", matcher is null ? label : _itemPrefix + "mcp." + matcher));
                    }
                    else
                    {
                        Format(key, item, output, label);
                    }
                }
                if (element.GetArrayLength() > _maxItems)
                {
                    MarkTruncated(output);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (key == "autoTier" && element.TryGetProperty("overridable", out var tier))
            {
                Format(key, tier, output, label);
                return;
            }
            var unknown = false;
            foreach (var property in element.EnumerateObject())
            {
                var path = key + "." + property.Name;
                if (_publicPaths.Contains(path))
                {
                    Format(path, property.Value, output, _itemPrefix + path);
                }
                else
                {
                    unknown = true;
                }
            }
            if (unknown)
            {
                AppendValue(output, State("Hidden"));
            }
            if (output.Count == startingCount)
            {
                AppendValue(output, State("None", label));
            }
        }
        else
        {
            AppendValue(output, State("Hidden", label));
        }
    }

    private static void AppendValue(List<CopilotPolicyValue> output, CopilotPolicyValue value)
    {
        if (output.Count < _maxItems)
        {
            output.Add(value);
        }
        else
        {
            MarkTruncated(output);
        }
    }

    private static void MarkTruncated(List<CopilotPolicyValue> output)
    {
        if (output.Count == 0 || output[^1].ResourceKey != _valuePrefix + "Truncated")
        {
            output.Add(State("Truncated"));
        }
    }

    private static CopilotPolicyValue State(string state, string? label = null)
        => new(ResourceKey: _valuePrefix + state, LabelKey: label);

    private static JsonElement? Find(JsonElement? settings, string key)
    {
        var current = settings;
        foreach (var segment in key.Split('.'))
        {
            if (current is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(segment, out var child))
            {
                return null;
            }
            current = child;
        }
        return current;
    }

    private static string[] SourcesFor(ManagedSettingsResolveResult result, string key)
        => result.Layers.Where(layer => Find(layer.Settings, key) is not null)
            .Select(layer => Source(layer.Source)).Distinct(StringComparer.Ordinal).Take(_maxItems).ToArray();

    private static string Source(string? source)
        => source switch
        {
            "device" => "Device", "server" => "Server", "client" => "Client", "policyHelper" => "PolicyHelper",
            "mixed" => "Mixed", "none" => "None", _ => "Unknown",
        };
}
#pragma warning restore GHCP001
