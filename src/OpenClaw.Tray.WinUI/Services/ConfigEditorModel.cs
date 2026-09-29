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
        IEnumerable<string> editedPaths) =>
        OmitUntouchedRedactionSentinels(document, editedPaths, document);

    public static JsonElement OmitUntouchedRedactionSentinels(
        JsonElement document,
        IEnumerable<string> editedPaths,
        JsonElement baseDocument)
    {
        var node = JsonNode.Parse(document.GetRawText());
        if (node is null)
            return document.Clone();

        var edited = new HashSet<string>(editedPaths, StringComparer.OrdinalIgnoreCase);
        var arraysToDrop = new HashSet<string>(StringComparer.Ordinal);
        RemoveUntouchedRedactionSentinels(node, "", edited, baseDocument, arraysToDrop);
        foreach (var arrayPath in arraysToDrop.OrderBy(static path => path.Length))
            RemovePropertyAtPath(node, arrayPath);

        using var rewritten = JsonDocument.Parse(node.ToJsonString());
        return rewritten.RootElement.Clone();
    }

    private static string? FindBlockedRedactionSentinel(
        JsonElement updated,
        JsonElement baseDocument,
        string path,
        HashSet<string> edited) =>
        FindBlockedRedactionSentinel(updated, baseDocument, path, edited, baseDocument);

    private static string? FindBlockedRedactionSentinel(
        JsonElement updated,
        JsonElement baseDocument,
        string path,
        HashSet<string> edited,
        JsonElement baseRoot)
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
                var hit = FindBlockedRedactionSentinel(property.Value, childBase, childPath, edited, baseRoot);
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
                var hit = FindBlockedRedactionSentinel(item, childBase, childPath, edited, baseRoot);
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

            // Object fields and id-keyed array fields can be left out of the
            // patch. Gateway keeps the stored secret for an absent key. An array
            // whose entries lack stable ids is replaced wholesale, so an indexed
            // edit inside that array cannot drop the masked credential safely.
            if (ShouldOmitUntouchedSentinel(path))
            {
                var arrayPath = OutermostNonIdArrayPath(baseRoot, path);
                if (arrayPath != null && EditedPathInsideArray(edited, arrayPath))
                    return path;

                return null;
            }

            if (!loadedSentinel || !IsCredentialPath(path))
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
            name.Equals("privateKey", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldOmitUntouchedSentinel(string path)
    {
        if (!path.Contains('[', StringComparison.Ordinal))
            return true;

        // A masked array element is not a property we can drop. A credential
        // field on an item inside the array is.
        return IsCredentialPath(path) && !path.EndsWith(']');
    }

    private static void RemoveUntouchedRedactionSentinels(
        JsonNode node,
        string path,
        HashSet<string> edited,
        JsonElement baseRoot,
        HashSet<string> arraysToDrop)
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
                    ShouldOmitUntouchedSentinel(childPath))
                {
                    var arrayPath = OutermostNonIdArrayPath(baseRoot, childPath);
                    if (arrayPath == null)
                        removals.Add(property.Key);
                    else if (!EditedPathInsideArray(edited, arrayPath))
                        arraysToDrop.Add(arrayPath);

                    continue;
                }

                if (property.Value is not null)
                    RemoveUntouchedRedactionSentinels(property.Value, childPath, edited, baseRoot, arraysToDrop);
            }

            foreach (var key in removals)
                obj.Remove(key);
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonNode item)
                    RemoveUntouchedRedactionSentinels(item, $"{path}[{index}]", edited, baseRoot, arraysToDrop);
            }
        }
    }

    private readonly record struct ConfigPathSegment(string Name, int? Index);

    private static List<ConfigPathSegment> ParsePath(string path)
    {
        var segments = new List<ConfigPathSegment>();
        var index = 0;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                continue;
            }

            var start = index;
            while (index < path.Length && path[index] != '.' && path[index] != '[')
                index++;

            var name = path[start..index];
            int? arrayIndex = null;
            if (index < path.Length && path[index] == '[')
            {
                index++;
                var numberStart = index;
                while (index < path.Length && path[index] != ']')
                    index++;
                if (int.TryParse(path[numberStart..index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    arrayIndex = parsed;
                if (index < path.Length && path[index] == ']')
                    index++;
            }

            if (name.Length > 0)
                segments.Add(new ConfigPathSegment(name, arrayIndex));
        }

        return segments;
    }

    private static bool ArrayIsIdKeyed(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(id.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static string? OutermostNonIdArrayPath(JsonElement root, string credentialPath)
    {
        if (!credentialPath.Contains('[', StringComparison.Ordinal) ||
            root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var current = root;
        var walked = "";
        foreach (var segment in ParsePath(credentialPath))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment.Name, out var child))
            {
                return null;
            }

            walked = walked.Length == 0 ? segment.Name : $"{walked}.{segment.Name}";
            if (segment.Index is not int arrayIndex)
            {
                current = child;
                continue;
            }

            if (child.ValueKind != JsonValueKind.Array)
                return null;
            if (!ArrayIsIdKeyed(child))
                return walked;
            if ((uint)arrayIndex >= (uint)child.GetArrayLength())
                return null;

            walked = $"{walked}[{arrayIndex.ToString(CultureInfo.InvariantCulture)}]";
            current = child[arrayIndex];
        }

        return null;
    }

    private static bool EditedPathInsideArray(HashSet<string> edited, string arrayPath)
    {
        foreach (var path in edited)
        {
            if (path.Equals(arrayPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith(arrayPath + "[", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void RemovePropertyAtPath(JsonNode root, string propertyPath)
    {
        JsonNode? current = root;
        var segments = ParsePath(propertyPath);
        for (var index = 0; index < segments.Count; index++)
        {
            if (current is not JsonObject obj)
                return;

            var segment = segments[index];
            if (obj[segment.Name] is not JsonNode child)
                return;

            if (index == segments.Count - 1)
            {
                obj.Remove(segment.Name);
                return;
            }

            if (segment.Index is int arrayIndex)
            {
                if (child is not JsonArray array || (uint)arrayIndex >= (uint)array.Count)
                    return;
                current = array[arrayIndex];
                continue;
            }

            current = child;
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
