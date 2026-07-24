using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Strict live model profile: provider/model selection plus the name of the
/// environment variable holding the provider API key. Never contains a
/// literal credential.
/// </summary>
internal sealed record LiveModelProfile(
    string Provider,
    string Model,
    string ApiKeyEnvVar,
    TimeSpan ReplyTimeout);

/// <summary>
/// One Discord bot identity referenced by environment-variable name (token)
/// plus its numeric snowflake user/application id. Never contains a literal
/// token.
/// </summary>
internal sealed record DiscordBotIdentity(string TokenEnvVar, string UserId);

/// <summary>
/// Strict real Discord channel profile: guild/channel selection plus two
/// distinct bot identities (driver, SUT). Never contains a literal token.
/// </summary>
internal sealed record RealChannelProfile(
    string GuildId,
    string ChannelId,
    DiscordBotIdentity Driver,
    DiscordBotIdentity Sut,
    TimeSpan PollTimeout);

/// <summary>
/// Raw JSON shape accepted for the live model profile file. Unknown fields
/// are rejected at deserialization time (see <see cref="LiveParityProfileLoader"/>).
/// </summary>
internal sealed class LiveModelProfileDto
{
    public int? SchemaVersion { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? ApiKeyEnvVar { get; init; }
    public int? ReplyTimeoutSeconds { get; init; }
}

/// <summary>
/// Raw JSON shape accepted for one Discord bot identity nested in the real
/// channel profile file. Unknown fields are rejected at deserialization
/// time (see <see cref="LiveParityProfileLoader"/>).
/// </summary>
internal sealed class DiscordBotIdentityDto
{
    public string? TokenEnvVar { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Raw JSON shape accepted for the real channel profile file. Unknown
/// fields are rejected at deserialization time (see
/// <see cref="LiveParityProfileLoader"/>).
/// </summary>
internal sealed class RealChannelProfileDto
{
    public int? SchemaVersion { get; init; }
    public string? GuildId { get; init; }
    public string? ChannelId { get; init; }
    public DiscordBotIdentityDto? Driver { get; init; }
    public DiscordBotIdentityDto? Sut { get; init; }
    public int? PollTimeoutSeconds { get; init; }
}

/// <summary>
/// Loads and strictly validates the live-parity profile files named by
/// <see cref="LiveParityEnvVars.LiveModelProfilePath"/> and
/// <see cref="LiveParityEnvVars.RealChannelProfilePath"/>. Never reads any
/// other host/user configuration file. Rejects unknown JSON fields so a
/// profile cannot silently carry an unreviewed extra property (including an
/// accidental literal secret under a made-up field name). All failures
/// throw <see cref="LiveParityConfigurationException"/> naming only the
/// offending field or environment variable, never a value.
/// </summary>
internal static class LiveParityProfileLoader
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static LiveModelProfile LoadLiveModelProfile() =>
        ParseLiveModelProfile(ReadProfileFile(LiveParityEnvVars.LiveModelProfilePath));

    public static RealChannelProfile LoadRealChannelProfile() =>
        ParseRealChannelProfile(ReadProfileFile(LiveParityEnvVars.RealChannelProfilePath));

    internal static string ReadProfileFile(string pathEnvVar)
    {
        var path = Environment.GetEnvironmentVariable(pathEnvVar);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LiveParityConfigurationException(
                $"{pathEnvVar} is not set. Set it to the absolute path of a live-parity profile JSON file " +
                "(see docs/LIVE_PARITY_TESTING.md) before enabling this lane.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new LiveParityConfigurationException(
                $"{pathEnvVar} must be an absolute path to a profile JSON file. This lane does not read " +
                "arbitrary host/user configuration by relative path or search.");
        }

        if (!File.Exists(path))
        {
            throw new LiveParityConfigurationException(
                $"{pathEnvVar} points to a file that does not exist: no profile was found at the configured path.");
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LiveParityConfigurationException(
                $"{pathEnvVar} could not be read ({ex.GetType().Name}).", ex);
        }
    }

    internal static LiveModelProfile ParseLiveModelProfile(string json)
    {
        var dto = Deserialize<LiveModelProfileDto>(json);

        LiveParityValidation.EnsureSchemaVersion("schemaVersion", dto.SchemaVersion);
        var provider = LiveParityValidation.EnsureProviderId("provider", dto.Provider);
        var model = LiveParityValidation.EnsureModelId("model", dto.Model);
        var apiKeyEnvVar = LiveParityValidation.EnsureEnvVarName("apiKeyEnvVar", dto.ApiKeyEnvVar);
        var replyTimeoutSeconds = LiveParityValidation.EnsureBoundedInt(
            "replyTimeoutSeconds", dto.ReplyTimeoutSeconds, defaultValue: 120, min: 5, max: 600);

        return new LiveModelProfile(provider, model, apiKeyEnvVar, TimeSpan.FromSeconds(replyTimeoutSeconds));
    }

    internal static RealChannelProfile ParseRealChannelProfile(string json)
    {
        var dto = Deserialize<RealChannelProfileDto>(json);

        LiveParityValidation.EnsureSchemaVersion("schemaVersion", dto.SchemaVersion);
        var guildId = LiveParityValidation.EnsureSnowflakeId("guildId", dto.GuildId);
        var channelId = LiveParityValidation.EnsureSnowflakeId("channelId", dto.ChannelId);

        if (dto.Driver is null)
            throw new LiveParityConfigurationException("driver is required.");
        if (dto.Sut is null)
            throw new LiveParityConfigurationException("sut is required.");

        var driverTokenEnvVar = LiveParityValidation.EnsureEnvVarName("driver.tokenEnvVar", dto.Driver.TokenEnvVar);
        var driverUserId = LiveParityValidation.EnsureSnowflakeId("driver.userId", dto.Driver.UserId);
        var sutTokenEnvVar = LiveParityValidation.EnsureEnvVarName("sut.tokenEnvVar", dto.Sut.TokenEnvVar);
        var sutUserId = LiveParityValidation.EnsureSnowflakeId("sut.userId", dto.Sut.UserId);

        if (string.Equals(driverTokenEnvVar, sutTokenEnvVar, StringComparison.Ordinal))
        {
            throw new LiveParityConfigurationException(
                "driver.tokenEnvVar and sut.tokenEnvVar must name two distinct environment variables: the " +
                "real channel lane requires two distinct Discord bot credentials.");
        }

        if (string.Equals(driverUserId, sutUserId, StringComparison.Ordinal))
        {
            throw new LiveParityConfigurationException(
                "driver.userId and sut.userId must be two distinct Discord bot identities.");
        }

        var pollTimeoutSeconds = LiveParityValidation.EnsureBoundedInt(
            "pollTimeoutSeconds", dto.PollTimeoutSeconds, defaultValue: 45, min: 5, max: 120);
        return new RealChannelProfile(
            guildId,
            channelId,
            new DiscordBotIdentity(driverTokenEnvVar, driverUserId),
            new DiscordBotIdentity(sutTokenEnvVar, sutUserId),
            TimeSpan.FromSeconds(pollTimeoutSeconds));
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ParseOptions)
                ?? throw new LiveParityConfigurationException("Profile JSON parsed to null.");
        }
        catch (JsonException ex)
        {
            throw new LiveParityConfigurationException(
                "Profile JSON failed strict parsing: unknown field, malformed JSON, or a value of the wrong " +
                $"type ({ex.GetType().Name}). Unknown fields are rejected intentionally; see the exact schema " +
                "in docs/LIVE_PARITY_TESTING.md.",
                ex);
        }
    }
}
