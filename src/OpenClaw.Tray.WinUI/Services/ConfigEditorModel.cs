using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

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

internal enum SensitiveArrayDecision
{
    Preserve,
    Replace,
    Clear
}

/// <summary>
/// Edit session for a sensitive array whose current items must stay off the page.
/// The session stores a count and the newly typed JSON. It never receives the stored secrets.
/// </summary>
internal sealed class SensitiveArrayEditSession
{
    public SensitiveArrayEditSession(int existingCount)
    {
        if (existingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(existingCount));
        ExistingCount = existingCount;
    }

    public int ExistingCount { get; }
    public bool ReplaceOpen { get; private set; }
    public bool ClearConfirmOpen { get; private set; }
    public string Draft { get; private set; } = "";
    public string? Error { get; private set; }
    public SensitiveArrayDecision Decision { get; private set; }
    public JsonElement? Replacement { get; private set; }

    public string CountText => ExistingCount == 1
        ? "1 entry is configured. Stored values stay hidden."
        : $"{ExistingCount} entries are configured. Stored values stay hidden.";

    public static JsonElement EmptyArray()
    {
        using var document = JsonDocument.Parse("[]");
        return document.RootElement.Clone();
    }

    public void BeginReplace()
    {
        ReplaceOpen = true;
        ClearConfirmOpen = false;
        Draft = "";
        Error = null;
    }

    public void SetDraft(string? text) => Draft = text ?? "";

    public bool TryApplyReplace()
    {
        try
        {
            using var document = JsonDocument.Parse(Draft);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                Error = "Must be a JSON array.";
                return false;
            }

            Replacement = document.RootElement.Clone();
            Decision = SensitiveArrayDecision.Replace;
            Error = null;
            ReplaceOpen = false;
            Draft = "";
            return true;
        }
        catch (JsonException ex)
        {
            Error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    public void CancelReplace()
    {
        ReplaceOpen = false;
        Draft = "";
        Error = null;
    }

    public void BeginClear()
    {
        ClearConfirmOpen = true;
        ReplaceOpen = false;
    }

    public void ConfirmClear()
    {
        ClearConfirmOpen = false;
        ReplaceOpen = false;
        Draft = "";
        Error = null;
        Replacement = null;
        Decision = SensitiveArrayDecision.Clear;
    }

    public void CancelClear() => ClearConfirmOpen = false;
}
