using System.Text;
using System.Text.Json;

namespace OpenClawTray.Controls;

/// <summary>
/// Display and save rules for a schemaless JSON object or a non-scalar array.
/// Sensitive properties are omitted from the editor text. A redaction marker
/// is never staged as the saved value. Omitted secrets are copied back from
/// the original value so a later save does not drop them.
/// </summary>
internal static class SchemaJsonEditorPolicy
{
    internal const string RedactionMarker = "[REDACTED]";

    internal static string TextForEditor(JsonElement value, bool expectObject)
    {
        if (expectObject)
        {
            if (value.ValueKind != JsonValueKind.Object)
                return "{}";
        }
        else if (value.ValueKind != JsonValueKind.Array)
        {
            return "[]";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            WriteWithoutSensitiveProperties(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static bool TryStage(string text, JsonElement original, bool expectObject, out JsonElement staged, out string? error)
    {
        staged = default;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (expectObject && root.ValueKind != JsonValueKind.Object)
            {
                error = "Must be a JSON object.";
                return false;
            }

            if (!expectObject && root.ValueKind != JsonValueKind.Array)
            {
                error = "Must be a JSON array.";
                return false;
            }

            if (ContainsRedactionMarker(root))
            {
                error = "A redaction marker cannot be saved.";
                return false;
            }

            if (!CanMergeWithoutDroppingSecrets(original, root, out error))
                return false;

            staged = MergeOmittedSensitive(original, root);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static JsonElement MergeOmittedSensitive(JsonElement original, JsonElement edited)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteMerged(writer, original, edited);

        using var merged = JsonDocument.Parse(stream.ToArray());
        return merged.RootElement.Clone();
    }

    private static void WriteMerged(Utf8JsonWriter writer, JsonElement original, JsonElement edited)
    {
        if (original.ValueKind == JsonValueKind.Object && edited.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in edited.EnumerateObject())
            {
                written.Add(property.Name);
                writer.WritePropertyName(property.Name);
                if (!IsSensitiveName(property.Name) &&
                    original.TryGetProperty(property.Name, out var originalChild))
                {
                    WriteMerged(writer, originalChild, property.Value);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            foreach (var property in original.EnumerateObject())
            {
                if (written.Contains(property.Name) || !IsSensitiveName(property.Name))
                    continue;

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
            return;
        }

        if (original.ValueKind == JsonValueKind.Array &&
            edited.ValueKind == JsonValueKind.Array &&
            original.GetArrayLength() == edited.GetArrayLength())
        {
            writer.WriteStartArray();
            var originalItems = original.EnumerateArray().ToArray();
            var index = 0;
            foreach (var item in edited.EnumerateArray())
            {
                WriteMerged(writer, originalItems[index], item);
                index++;
            }

            writer.WriteEndArray();
            return;
        }

        edited.WriteTo(writer);
    }

    private static bool CanMergeWithoutDroppingSecrets(JsonElement original, JsonElement edited, out string? error)
    {
        error = null;
        if (original.ValueKind == JsonValueKind.Object && edited.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in edited.EnumerateObject())
            {
                if (IsSensitiveName(property.Name))
                    continue;
                if (original.TryGetProperty(property.Name, out var child) &&
                    !CanMergeWithoutDroppingSecrets(child, property.Value, out error))
                {
                    return false;
                }
            }

            return true;
        }

        if (original.ValueKind == JsonValueKind.Array && edited.ValueKind == JsonValueKind.Array)
        {
            if (original.GetArrayLength() != edited.GetArrayLength())
            {
                if (ContainsSensitiveName(original))
                {
                    error = "Changing the number of items would drop a stored secret.";
                    return false;
                }

                return true;
            }

            var originalItems = original.EnumerateArray().ToArray();
            var index = 0;
            foreach (var item in edited.EnumerateArray())
            {
                if (!CanMergeWithoutDroppingSecrets(originalItems[index], item, out error))
                    return false;
                index++;
            }
        }

        return true;
    }

    private static bool ContainsSensitiveName(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (IsSensitiveName(property.Name) || ContainsSensitiveName(property.Value))
                        return true;
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ContainsSensitiveName(item))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static void WriteWithoutSensitiveProperties(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (IsSensitiveName(property.Name))
                        continue;
                    writer.WritePropertyName(property.Name);
                    WriteWithoutSensitiveProperties(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteWithoutSensitiveProperties(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool ContainsRedactionMarker(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (ContainsRedactionMarker(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (ContainsRedactionMarker(item))
                        return true;
                }
                return false;
            case JsonValueKind.String:
                var text = value.GetString();
                return text != null && text.Contains(RedactionMarker, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.ToLowerInvariant();
        return normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("api_key", StringComparison.Ordinal);
    }
}
