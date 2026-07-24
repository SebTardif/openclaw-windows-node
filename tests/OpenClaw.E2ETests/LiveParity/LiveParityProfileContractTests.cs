namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Secretless, fixture-free contract tests for strict live-parity profile
/// loading, parsing, and field validation. These never touch WSL/tray/
/// gateway state and never use a real credential; every "secret-shaped"
/// value used here is a synthetic placeholder. Profile-path tests use
/// isolated temp files created per test and always clean them up. Tests
/// that set the well-known profile path environment variables restore
/// them in <see cref="Dispose"/>.
/// </summary>
public sealed class LiveParityProfileContractTests : IDisposable
{
    private readonly string? _savedLiveModelProfilePath;
    private readonly string? _savedRealChannelProfilePath;
    private readonly List<string> _tempFiles = [];

    public LiveParityProfileContractTests()
    {
        _savedLiveModelProfilePath = Environment.GetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath);
        _savedRealChannelProfilePath = Environment.GetEnvironmentVariable(LiveParityEnvVars.RealChannelProfilePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath, _savedLiveModelProfilePath);
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RealChannelProfilePath, _savedRealChannelProfilePath);

        foreach (var path in _tempFiles)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            // slopwatch-ignore: SW003 Test temp-file cleanup is best-effort and must not hide the test outcome.
            catch { /* best effort */ }
        }
    }

    private string WriteTempProfile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"live-parity-contract-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _tempFiles.Add(path);
        return path;
    }

    private const string ValidLiveModelProfileJson =
        """
        {
          "schemaVersion": 1,
          "provider": "openai",
          "model": "gpt-4o-mini",
          "apiKeyEnvVar": "TEST_LIVE_MODEL_API_KEY"
        }
        """;

    private const string ValidRealChannelProfileJson =
        """
        {
          "schemaVersion": 1,
          "guildId": "123456789012345678",
          "channelId": "234567890123456789",
          "driver": { "tokenEnvVar": "TEST_DRIVER_TOKEN", "userId": "111111111111111111" },
          "sut": { "tokenEnvVar": "TEST_SUT_TOKEN", "userId": "222222222222222222" }
        }
        """;

    // ── Profile file resolution (path env var) ──────────────────────────

    [Fact]
    public void LoadLiveModelProfile_WhenPathVariableUnset_ThrowsNamingPathVariable()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath, null);

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.LoadLiveModelProfile());

        Assert.Contains(LiveParityEnvVars.LiveModelProfilePath, ex.Message);
    }

    [Fact]
    public void LoadLiveModelProfile_WhenPathIsRelative_ThrowsRejectingNonAbsolutePath()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath, "relative\\profile.json");

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.LoadLiveModelProfile());

        Assert.Contains(LiveParityEnvVars.LiveModelProfilePath, ex.Message);
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadLiveModelProfile_WhenFileDoesNotExist_ThrowsNamingPathVariable()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"live-parity-missing-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath, missingPath);

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.LoadLiveModelProfile());

        Assert.Contains(LiveParityEnvVars.LiveModelProfilePath, ex.Message);
    }

    [Fact]
    public void LoadLiveModelProfile_WhenFileIsValid_ReturnsParsedProfileAndAppliesDefaultTimeout()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.LiveModelProfilePath, WriteTempProfile(ValidLiveModelProfileJson));

        var profile = LiveParityProfileLoader.LoadLiveModelProfile();

        Assert.Equal("openai", profile.Provider);
        Assert.Equal("gpt-4o-mini", profile.Model);
        Assert.Equal("TEST_LIVE_MODEL_API_KEY", profile.ApiKeyEnvVar);
        Assert.Equal(TimeSpan.FromSeconds(120), profile.ReplyTimeout);
    }

    [Fact]
    public void LoadRealChannelProfile_WhenPathVariableUnset_ThrowsNamingPathVariable()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RealChannelProfilePath, null);

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.LoadRealChannelProfile());

        Assert.Contains(LiveParityEnvVars.RealChannelProfilePath, ex.Message);
    }

    [Fact]
    public void LoadRealChannelProfile_WhenFileIsValid_ReturnsParsedProfileAndAppliesDefaults()
    {
        Environment.SetEnvironmentVariable(LiveParityEnvVars.RealChannelProfilePath, WriteTempProfile(ValidRealChannelProfileJson));

        var profile = LiveParityProfileLoader.LoadRealChannelProfile();

        Assert.Equal("123456789012345678", profile.GuildId);
        Assert.Equal("234567890123456789", profile.ChannelId);
        Assert.Equal("TEST_DRIVER_TOKEN", profile.Driver.TokenEnvVar);
        Assert.Equal("111111111111111111", profile.Driver.UserId);
        Assert.Equal("TEST_SUT_TOKEN", profile.Sut.TokenEnvVar);
        Assert.Equal("222222222222222222", profile.Sut.UserId);
        Assert.Equal(TimeSpan.FromSeconds(45), profile.PollTimeout);
    }

    // ── Strict schema: unknown fields ───────────────────────────────────

    [Fact]
    public void ParseLiveModelProfile_RejectsUnknownField_WithoutLeakingFieldValue()
    {
        const string plantedSecretLikeValue = "sk-should-never-appear-in-any-message-abc123";
        var json =
            $$"""
            {
              "schemaVersion": 1,
              "provider": "openai",
              "model": "gpt-4o-mini",
              "apiKeyEnvVar": "TEST_LIVE_MODEL_API_KEY",
              "apiKey": "{{plantedSecretLikeValue}}"
            }
            """;

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));

        Assert.DoesNotContain(plantedSecretLikeValue, ex.Message);
        Assert.DoesNotContain(plantedSecretLikeValue, ex.ToString());
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsUnknownField_WithoutLeakingFieldValue()
    {
        const string plantedSecretLikeValue = "should-never-appear-in-any-message";
        var json =
            $$"""
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "driver": { "tokenEnvVar": "TEST_DRIVER_TOKEN", "userId": "111111111111111111" },
              "sut": { "tokenEnvVar": "TEST_SUT_TOKEN", "userId": "222222222222222222" },
              "webhookSecret": "{{plantedSecretLikeValue}}"
            }
            """;

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));

        Assert.DoesNotContain(plantedSecretLikeValue, ex.Message);
        Assert.DoesNotContain(plantedSecretLikeValue, ex.ToString());
    }

    // ── Live model profile field validation ─────────────────────────────

    [Fact]
    public void ParseLiveModelProfile_RejectsMissingSchemaVersion()
    {
        const string json = """{ "provider": "openai", "model": "gpt-4o-mini", "apiKeyEnvVar": "X" }""";

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));
    }

    [Fact]
    public void ParseLiveModelProfile_RejectsWrongSchemaVersion()
    {
        const string json = """{ "schemaVersion": 99, "provider": "openai", "model": "gpt-4o-mini", "apiKeyEnvVar": "X" }""";

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));
    }

    [Fact]
    public void ParseLiveModelProfile_RejectsUppercaseProviderId()
    {
        const string json = """{ "schemaVersion": 1, "provider": "OpenAI", "model": "gpt-4o-mini", "apiKeyEnvVar": "X" }""";

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));
    }

    [Fact]
    public void ParseLiveModelProfile_RejectsLiteralApiKeyLookingValue_ForApiKeyEnvVarField()
    {
        const string fakeLiteralKey = "sk-test1234567890abcdefTESTKEYNOTREAL";
        var json =
            $$"""
            { "schemaVersion": 1, "provider": "openai", "model": "gpt-4o-mini", "apiKeyEnvVar": "{{fakeLiteralKey}}" }
            """;

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));

        Assert.DoesNotContain(fakeLiteralKey, ex.Message);
        Assert.Contains("environment variable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseLiveModelProfile_RejectsLowercaseEnvVarName()
    {
        const string json = """{ "schemaVersion": 1, "provider": "openai", "model": "gpt-4o-mini", "apiKeyEnvVar": "my_lowercase_var" }""";

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));
    }

    [Fact]
    public void ParseLiveModelProfile_RejectsReplyTimeoutOutOfBounds()
    {
        const string json =
            """{ "schemaVersion": 1, "provider": "openai", "model": "gpt-4o-mini", "apiKeyEnvVar": "X", "replyTimeoutSeconds": 99999 }""";

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseLiveModelProfile(json));
    }

    // ── Real channel profile field validation ───────────────────────────

    [Fact]
    public void ParseRealChannelProfile_RejectsNonNumericGuildId()
    {
        var json = ValidRealChannelProfileJson.Replace("123456789012345678", "not-a-snowflake");

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsMissingDriver()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "sut": { "tokenEnvVar": "TEST_SUT_TOKEN", "userId": "222222222222222222" }
            }
            """;

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsMissingSut()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "driver": { "tokenEnvVar": "TEST_DRIVER_TOKEN", "userId": "111111111111111111" }
            }
            """;

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsSameTokenEnvVarForDriverAndSut()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "driver": { "tokenEnvVar": "SAME_TOKEN_VAR", "userId": "111111111111111111" },
              "sut": { "tokenEnvVar": "SAME_TOKEN_VAR", "userId": "222222222222222222" }
            }
            """;

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));
        Assert.Contains("distinct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsSameUserIdForDriverAndSut()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "driver": { "tokenEnvVar": "TEST_DRIVER_TOKEN", "userId": "999999999999999999" },
              "sut": { "tokenEnvVar": "TEST_SUT_TOKEN", "userId": "999999999999999999" }
            }
            """;

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));
        Assert.Contains("distinct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsDiscordTokenShapedValue_ForTokenEnvVarField()
    {
        // Synthetic three-segment shape matching a real Discord bot token's
        // structure, but not a real credential.
        var fakeTokenShapedValue = $"{new string('a', 24)}.{new string('B', 7)}.{new string('c', 27)}";
        var json =
            $$"""
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "driver": { "tokenEnvVar": "{{fakeTokenShapedValue}}", "userId": "111111111111111111" },
              "sut": { "tokenEnvVar": "TEST_SUT_TOKEN", "userId": "222222222222222222" }
            }
            """;

        var ex = Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));

        Assert.DoesNotContain(fakeTokenShapedValue, ex.Message);
    }

    [Fact]
    public void ParseRealChannelProfile_RejectsDiscordApiBaseUrlOverride()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "guildId": "123456789012345678",
              "channelId": "234567890123456789",
              "driver": { "tokenEnvVar": "TEST_DRIVER_TOKEN", "userId": "111111111111111111" },
              "sut": { "tokenEnvVar": "TEST_SUT_TOKEN", "userId": "222222222222222222" },
              "discordApiBaseUrl": "https://example.invalid/api/v10/"
            }
            """;

        Assert.Throws<LiveParityConfigurationException>(() => LiveParityProfileLoader.ParseRealChannelProfile(json));
    }

    [Fact]
    public void ParseRealChannelProfile_AppliesDefaultPollTimeoutWhenOmitted()
    {
        var profile = LiveParityProfileLoader.ParseRealChannelProfile(ValidRealChannelProfileJson);

        Assert.Equal(TimeSpan.FromSeconds(45), profile.PollTimeout);
    }

    // ── Low-level field validators ───────────────────────────────────────

    [Theory]
    [InlineData("VALID_NAME")]
    [InlineData("_LEADING_UNDERSCORE")]
    [InlineData("AB")]
    public void EnsureEnvVarName_AcceptsValidUpperSnakeCase(string value)
    {
        Assert.Equal(value, LiveParityValidation.EnsureEnvVarName("field", value));
    }

    [Theory]
    [InlineData("lowercase_name")]
    [InlineData("Mixed_Case")]
    [InlineData("1STARTS_WITH_DIGIT")]
    [InlineData("HAS SPACE")]
    public void EnsureEnvVarName_RejectsNonUpperSnakeCase(string value)
    {
        Assert.Throws<LiveParityConfigurationException>(() => LiveParityValidation.EnsureEnvVarName("field", value));
    }

    [Theory]
    [InlineData("123456789012345")]
    [InlineData("1234567890123456789012345")]
    public void EnsureSnowflakeId_AcceptsValidLengthDigitStrings(string value)
    {
        Assert.Equal(value, LiveParityValidation.EnsureSnowflakeId("field", value));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("123")]
    [InlineData("12345678901234567890123456")]
    public void EnsureSnowflakeId_RejectsInvalidShapes(string value)
    {
        Assert.Throws<LiveParityConfigurationException>(() => LiveParityValidation.EnsureSnowflakeId("field", value));
    }

    [Fact]
    public void EnsureBoundedInt_RejectsOutOfRange()
    {
        Assert.Throws<LiveParityConfigurationException>(
            () => LiveParityValidation.EnsureBoundedInt("field", 1000, defaultValue: 10, min: 1, max: 100));
    }

    [Fact]
    public void EnsureBoundedInt_ReturnsDefaultWhenOmitted()
    {
        Assert.Equal(10, LiveParityValidation.EnsureBoundedInt("field", null, defaultValue: 10, min: 1, max: 100));
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("AIzaSyAbCdEfGhIjKlMnOpQrStUvWxYz0123456")]
    public void LooksLikeSecretLiteral_DetectsKnownProviderPrefixes(string value)
    {
        Assert.True(LiveParityValidation.LooksLikeSecretLiteral(value));
    }

    [Theory]
    [InlineData("OPENCLAW_LIVE_MODEL_API_KEY")]
    [InlineData("TEST_SUT_TOKEN")]
    [InlineData("A")]
    public void LooksLikeSecretLiteral_AllowsPlainEnvVarNames(string value)
    {
        Assert.False(LiveParityValidation.LooksLikeSecretLiteral(value));
    }

    [Fact]
    public void LooksLikeSecretLiteral_DetectsDiscordTokenShape()
    {
        var fakeToken = $"{new string('a', 24)}.{new string('B', 7)}.{new string('c', 27)}";
        Assert.True(LiveParityValidation.LooksLikeSecretLiteral(fakeToken));
    }
}
