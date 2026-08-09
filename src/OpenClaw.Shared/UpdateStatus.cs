using System;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// The gateway's resolved update track. This is authoritative for the installed
/// OpenClaw runtime, rather than the companion's separate release repository.
/// </summary>
public sealed class GatewayUpdateStatus
{
    public string? EffectiveChannel { get; init; }

    public bool SuppressesCompanionUpdate => string.Equals(
        EffectiveChannel,
        "extended-stable",
        StringComparison.OrdinalIgnoreCase);
}

public static class GatewayUpdateStatusParser
{
    /// <summary>
    /// Parses the additive <c>effectiveChannel</c> field from <c>update.status</c>.
    /// Older gateways omit it, which deliberately preserves the legacy updater path.
    /// </summary>
    public static GatewayUpdateStatus Parse(JsonElement payload) => new()
    {
        EffectiveChannel = payload.ValueKind == JsonValueKind.Object &&
                           payload.TryGetProperty("effectiveChannel", out var channel) &&
                           channel.ValueKind == JsonValueKind.String
            ? channel.GetString()
            : null
    };
}
