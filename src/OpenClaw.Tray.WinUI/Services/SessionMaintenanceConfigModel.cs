using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClawTray.Services;

internal sealed record SessionMaintenanceSettings(
    string Mode,
    string PruneAfter,
    long MaxEntries,
    bool KeepResetArchives,
    string ResetArchiveRetention,
    bool LimitDiskUsage,
    string MaxDiskBytes,
    string HighWaterBytes)
{
    public static SessionMaintenanceSettings Defaults { get; } = new(
        "enforce",
        "30d",
        500,
        true,
        "",
        true,
        "10gb",
        "");
}

internal static partial class SessionMaintenanceConfigModel
{
    private static readonly Regex DurationPattern = CreateDurationPattern();
    private static readonly Regex CompositeDurationTokenPattern = CreateCompositeDurationTokenPattern();
    private static readonly Regex ByteSizePattern = CreateByteSizePattern();

    public static SessionMaintenanceSettings Read(JsonElement configResponse)
    {
        var snapshot = ConfigEditorModel.CaptureSnapshot(configResponse);
        if (!snapshot.HasRoot ||
            !snapshot.Root.TryGetProperty("session", out var session) ||
            session.ValueKind != JsonValueKind.Object ||
            !session.TryGetProperty("maintenance", out var maintenance) ||
            maintenance.ValueKind != JsonValueKind.Object)
        {
            return SessionMaintenanceSettings.Defaults;
        }

        var defaults = SessionMaintenanceSettings.Defaults;
        var mode = ReadString(maintenance, "mode") ?? defaults.Mode;
        var pruneAfter = ReadScalar(maintenance, "pruneAfter") ?? defaults.PruneAfter;
        var maxEntries = ReadInt64(maintenance, "maxEntries") ?? defaults.MaxEntries;

        var keepResetArchives = true;
        var resetArchiveRetention = "";
        if (maintenance.TryGetProperty("resetArchiveRetention", out var resetValue) &&
            resetValue.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
        {
            keepResetArchives = false;
            resetArchiveRetention = ScalarText(resetValue);
        }

        var limitDiskUsage = true;
        var maxDiskBytes = defaults.MaxDiskBytes;
        if (maintenance.TryGetProperty("maxDiskBytes", out var maxDiskValue))
        {
            if (maxDiskValue.ValueKind == JsonValueKind.False)
            {
                limitDiskUsage = false;
                maxDiskBytes = "";
            }
            else if (maxDiskValue.ValueKind is not JsonValueKind.Null)
            {
                maxDiskBytes = ScalarText(maxDiskValue);
            }
        }

        var highWaterBytes = ReadScalar(maintenance, "highWaterBytes") ?? "";
        return new SessionMaintenanceSettings(
            mode,
            pruneAfter,
            maxEntries,
            keepResetArchives,
            resetArchiveRetention,
            limitDiskUsage,
            maxDiskBytes,
            highWaterBytes);
    }

    public static IReadOnlyDictionary<string, string> Validate(SessionMaintenanceSettings settings)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings.Mode is not ("enforce" or "warn"))
            errors["Mode"] = "Choose Enforce or Warn only.";
        if (!IsPositiveDuration(settings.PruneAfter))
            errors["Prune after"] = "Use a positive duration such as 30d, 12h, or 500ms.";
        if (settings.MaxEntries < 1)
            errors["Maximum entries"] = "Enter an integer of at least 1.";
        if (!settings.KeepResetArchives && !IsPositiveDuration(settings.ResetArchiveRetention))
            errors["Reset archive retention"] = "Use a positive duration such as 14d.";
        if (settings.LimitDiskUsage && !IsByteSize(settings.MaxDiskBytes, allowZero: false))
            errors["Maximum disk usage"] = "Use a positive size such as 10gb or 500mb.";
        if (!string.IsNullOrWhiteSpace(settings.HighWaterBytes) &&
            !IsByteSize(settings.HighWaterBytes, allowZero: true))
        {
            errors["High-water target"] = "Use a non-negative size such as 8gb or leave it blank for 80%.";
        }
        return errors;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadScalar(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? ScalarText(value)
            : null;

    private static long? ReadInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;

    private static string ScalarText(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : value.GetRawText();

    private static bool IsPositiveDuration(string value)
    {
        var trimmed = value.Trim();
        var match = DurationPattern.Match(trimmed);
        if (match.Success)
        {
            return decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) &&
                amount > 0;
        }

        decimal total = 0;
        var consumed = 0;
        foreach (Match token in CompositeDurationTokenPattern.Matches(trimmed))
        {
            if (token.Index != consumed ||
                !decimal.TryParse(token.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                return false;
            }
            total += amount;
            consumed += token.Length;
        }
        return consumed == trimmed.Length && consumed > 0 && total > 0;
    }

    private static bool IsByteSize(string value, bool allowZero)
    {
        var match = ByteSizePattern.Match(value.Trim());
        return match.Success &&
            decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) &&
            (allowZero ? amount >= 0 : amount > 0);
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)(ms|s|m|h|d)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateDurationPattern();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)(ms|s|m|h|d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateCompositeDurationTokenPattern();

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)(b|k|kb|m|mb|g|gb|t|tb)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateByteSizePattern();
}
