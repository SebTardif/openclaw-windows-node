using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal sealed record ConfigEditorSnapshot(JsonElement Root, string? BaseHash)
{
    public static ConfigEditorSnapshot Empty { get; } = new(default, null);

    public bool HasRoot => Root.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
}

internal static class ConfigEditorModel
{
    public static ConfigEditorSnapshot CaptureSnapshot(JsonElement configResponse)
    {
        var root = ExtractConfigRoot(configResponse);
        var baseHash = ExtractBaseHash(configResponse);
        return new ConfigEditorSnapshot(root.Clone(), baseHash);
    }

    public static JsonElement ExtractConfigRoot(JsonElement configResponse)
    {
        if (configResponse.TryGetProperty("parsed", out var parsed))
            return parsed;
        if (configResponse.TryGetProperty("config", out var config))
            return config;
        return configResponse;
    }

    public static string? ExtractBaseHash(JsonElement configResponse)
    {
        if (configResponse.TryGetProperty("baseHash", out var baseHash) &&
            baseHash.ValueKind == JsonValueKind.String)
            return baseHash.GetString();

        if (configResponse.TryGetProperty("hash", out var hash) &&
            hash.ValueKind == JsonValueKind.String)
            return hash.GetString();

        if (configResponse.TryGetProperty("raw", out var raw) &&
            raw.ValueKind == JsonValueKind.String &&
            raw.GetString() is { } rawContent)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(rawContent);
            var computedHash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexStringLower(computedHash);
        }

        return null;
    }

    public static JsonElement ApplyChanges(JsonElement root, IReadOnlyDictionary<string, object?> changes)
    {
        var node = JsonNode.Parse(root.GetRawText()) ?? new JsonObject();
        foreach (var (path, value) in changes)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (value?.GetType() == typeof(object))
                continue;

            SetPath(node, path, JsonSerializer.SerializeToNode(value));
        }

        using var document = JsonDocument.Parse(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return document.RootElement.Clone();
    }

    public static Dictionary<string, object?> RelativeChangesFor(
        string sectionPath,
        IReadOnlyDictionary<string, object?> changes)
    {
        var relative = new Dictionary<string, object?>(StringComparer.Ordinal);
        var prefix = string.IsNullOrEmpty(sectionPath) ? "" : sectionPath + ".";

        foreach (var (path, value) in changes)
        {
            if (string.IsNullOrEmpty(sectionPath))
            {
                relative[path] = value;
            }
            else if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                relative[path[prefix.Length..]] = value;
            }
        }

        return relative;
    }

    public static JsonElement ApplyRelativeChanges(
        JsonElement section,
        string sectionPath,
        IReadOnlyDictionary<string, object?> changes)
    {
        var relative = RelativeChangesFor(sectionPath, changes);
        return relative.Count == 0 ? section.Clone() : ApplyChanges(section, relative);
    }

    public static object? CoerceNumber(double value, string schemaType)
    {
        if (schemaType == "integer")
        {
            if (value > long.MaxValue || value < long.MinValue)
                return value;

            return Convert.ToInt64(Math.Truncate(value), CultureInfo.InvariantCulture);
        }

        return value;
    }

    public static string? FindUneditedRedactionSentinel(
        JsonElement updated,
        IEnumerable<string> editedPaths,
        JsonElement baseDocument)
    {
        var edited = new HashSet<string>(editedPaths, StringComparer.OrdinalIgnoreCase);
        return FindBlockedRedactionSentinel(updated, baseDocument, "", edited);
    }

    public static JsonElement OmitUntouchedRedactionSentinels(
        JsonElement document,
        IEnumerable<string> editedPaths)
    {
        var node = JsonNode.Parse(document.GetRawText());
        if (node is null)
            return document.Clone();

        var edited = new HashSet<string>(editedPaths, StringComparer.OrdinalIgnoreCase);
        RemoveUntouchedRedactionSentinels(node, "", edited);
        using var rewritten = JsonDocument.Parse(node.ToJsonString());
        return rewritten.RootElement.Clone();
    }

    private static string? FindBlockedRedactionSentinel(
        JsonElement updated,
        JsonElement baseDocument,
        string path,
        HashSet<string> edited)
    {
        if (updated.ValueKind == JsonValueKind.Object)
        {
            var baseObject = baseDocument.ValueKind == JsonValueKind.Object
                ? baseDocument
                : default;
            foreach (var property in updated.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                var childBase = default(JsonElement);
                if (baseObject.ValueKind == JsonValueKind.Object)
                    baseObject.TryGetProperty(property.Name, out childBase);
                var hit = FindBlockedRedactionSentinel(property.Value, childBase, childPath, edited);
                if (hit != null)
                    return hit;
            }
        }
        else if (updated.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in updated.EnumerateArray())
            {
                var childPath = $"{path}[{index}]";
                var childBase = default(JsonElement);
                if (baseDocument.ValueKind == JsonValueKind.Array && index < baseDocument.GetArrayLength())
                    childBase = baseDocument[index];
                index++;
                var hit = FindBlockedRedactionSentinel(item, childBase, childPath, edited);
                if (hit != null)
                    return hit;
            }
        }
        else if (updated.ValueKind == JsonValueKind.String &&
                 ChannelConfigPatchBuilder.IsRedactionSentinel(updated.GetString()))
        {
            var loadedSentinel = baseDocument.ValueKind == JsonValueKind.String &&
                                 ChannelConfigPatchBuilder.IsRedactionSentinel(baseDocument.GetString());
            if (edited.Contains(path))
                return loadedSentinel ? path : null;

            if (!path.Contains('[', StringComparison.Ordinal) || !loadedSentinel || !IsCredentialPath(path))
                return null;

            return path;
        }

        return null;
    }

    private static bool IsCredentialPath(string path)
    {
        var name = path;
        var bracket = name.LastIndexOf('[');
        if (bracket >= 0 && name.EndsWith("]", StringComparison.Ordinal))
            name = name[..bracket];
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];

        return name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("webhook", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("key", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveUntouchedRedactionSentinels(
        JsonNode node,
        string path,
        HashSet<string> edited)
    {
        if (node is JsonObject obj)
        {
            var removals = new List<string>();
            foreach (var property in obj)
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    ChannelConfigPatchBuilder.IsRedactionSentinel(text) &&
                    !edited.Contains(childPath) &&
                    !childPath.Contains('[', StringComparison.Ordinal))
                {
                    removals.Add(property.Key);
                    continue;
                }

                if (property.Value is not null)
                    RemoveUntouchedRedactionSentinels(property.Value, childPath, edited);
            }

            foreach (var key in removals)
                obj.Remove(key);
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonNode item)
                    RemoveUntouchedRedactionSentinels(item, $"{path}[{index}]", edited);
            }
        }
    }

    private static void SetPath(JsonNode node, string dotPath, JsonNode? value)
    {
        var segments = dotPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return;

        var current = node;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current is not JsonObject obj)
                return;

            if (obj[segment] is not JsonObject child)
            {
                child = new JsonObject();
                obj[segment] = child;
            }

            current = child;
        }

        if (current is JsonObject target)
            target[segments[^1]] = value;
    }
}
