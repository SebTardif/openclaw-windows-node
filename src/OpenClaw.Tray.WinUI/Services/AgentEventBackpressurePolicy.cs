using System;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class AgentEventBackpressurePolicy
{
    /// <summary>
    /// Returns true only for high-volume state updates that a newer event can
    /// supersede in the diagnostic ring. Start, terminal, error, approval, and
    /// unknown protocol shapes fail closed and remain control events.
    /// </summary>
    public static bool IsDisposableUpdate(AgentEventInfo evt)
    {
        var stream = evt.Stream;
        if (string.Equals(stream, "assistant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stream, "reasoning", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stream, "command_output", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (evt.Data.ValueKind != JsonValueKind.Object)
            return false;

        if (string.Equals(stream, "item", StringComparison.OrdinalIgnoreCase))
            return HasValue(evt.Data, "phase", "update");

        if (string.Equals(stream, "tool", StringComparison.OrdinalIgnoreCase))
            return HasAnyValue(evt.Data, "phase", "update", "progress", "output");

        if (string.Equals(stream, "job", StringComparison.OrdinalIgnoreCase))
            return HasAnyValue(evt.Data, "state", "update", "running", "progress", "output");

        return false;
    }

    private static bool HasValue(JsonElement data, string propertyName, string expected) =>
        data.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool HasAnyValue(
        JsonElement data,
        string propertyName,
        params string[] expectedValues)
    {
        if (!data.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var actual = value.GetString();
        foreach (var expected in expectedValues)
        {
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
