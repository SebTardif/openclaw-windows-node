using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Secretless, fixture-free contract tests for the small reusable helpers
/// shared by the live model and real channel proofs:
/// <see cref="LiveParityCredentialResolver"/>, <see cref="LiveParitySecretRegistry"/>,
/// <see cref="BoundedPoller"/>, and <see cref="ShellScriptingHelpers"/>. None
/// of these tests touch WSL, the tray, or a real gateway connection, and
/// none of them use a real credential value; planted "secret" values here
/// are synthetic strings chosen only to exercise redaction and validation
/// logic.
/// </summary>
public sealed class LiveParitySupportContractTests : IDisposable
{
    private const string TestCredentialEnvVar = "OPENCLAW_LIVE_PARITY_TEST_CREDENTIAL";
    private readonly string? _savedTestCredential;

    public LiveParitySupportContractTests()
    {
        _savedTestCredential = Environment.GetEnvironmentVariable(TestCredentialEnvVar);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestCredentialEnvVar, _savedTestCredential);
    }

    // ── LiveParityCredentialResolver ─────────────────────────────────────

    [Fact]
    public void ResolveRequiredSecret_WhenEnvVarUnset_ThrowsNamingVariable()
    {
        Environment.SetEnvironmentVariable(TestCredentialEnvVar, null);

        var ex = Assert.Throws<LiveParityConfigurationException>(
            () => LiveParityCredentialResolver.ResolveRequiredSecret(TestCredentialEnvVar, "test credential"));

        Assert.Contains(TestCredentialEnvVar, ex.Message);
    }

    [Fact]
    public void ResolveRequiredSecret_WhenEnvVarEmpty_ThrowsNamingVariable()
    {
        Environment.SetEnvironmentVariable(TestCredentialEnvVar, "");

        var ex = Assert.Throws<LiveParityConfigurationException>(
            () => LiveParityCredentialResolver.ResolveRequiredSecret(TestCredentialEnvVar, "test credential"));

        Assert.Contains(TestCredentialEnvVar, ex.Message);
    }

    [Fact]
    public void ResolveRequiredSecret_WhenValueContainsNewline_ThrowsWithoutLeakingValue()
    {
        var plantedValue = $"line-one-{Guid.NewGuid():N}\r\nline-two";
        Environment.SetEnvironmentVariable(TestCredentialEnvVar, plantedValue);

        var ex = Assert.Throws<LiveParityConfigurationException>(
            () => LiveParityCredentialResolver.ResolveRequiredSecret(TestCredentialEnvVar, "test credential"));

        Assert.Contains(TestCredentialEnvVar, ex.Message);
        Assert.DoesNotContain(plantedValue, ex.Message);
        Assert.DoesNotContain("line-one", ex.Message);
    }

    // Note: an embedded-NUL case is intentionally not tested here. Windows
    // truncates environment variable values at the first embedded NUL
    // character (verified empirically: Environment.SetEnvironmentVariable /
    // GetEnvironmentVariable round-trip a NUL-containing string as truncated
    // at the NUL), so a NUL character can never actually reach
    // ResolveRequiredSecret's value on this platform. The CRLF test above
    // exercises the same IndexOfAny(['\r','\n','\0']) validation branch that
    // the NUL check belongs to.

    [Fact]
    public void ResolveRequiredSecret_WhenValueExceedsMaxLength_ThrowsWithoutLeakingValue()
    {
        var plantedValue = new string('x', 4097);
        Environment.SetEnvironmentVariable(TestCredentialEnvVar, plantedValue);

        var ex = Assert.Throws<LiveParityConfigurationException>(
            () => LiveParityCredentialResolver.ResolveRequiredSecret(TestCredentialEnvVar, "test credential"));

        Assert.Contains(TestCredentialEnvVar, ex.Message);
        Assert.DoesNotContain(plantedValue, ex.Message);
    }

    [Fact]
    public void ResolveRequiredSecret_WhenValueIsSet_ReturnsExactValue()
    {
        var plantedValue = $"value-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(TestCredentialEnvVar, plantedValue);

        var resolved = LiveParityCredentialResolver.ResolveRequiredSecret(TestCredentialEnvVar, "test credential");

        Assert.Equal(plantedValue, resolved);
    }

    // ── LiveParitySecretRegistry ──────────────────────────────────────────

    [Fact]
    public void Register_IgnoresValuesShorterThanMinimumRedactableLength()
    {
        var registry = new LiveParitySecretRegistry();
        registry.Register("abc"); // 3 chars: below the minimum, so this is a no-op.

        var redacted = registry.Redact("the value is abc here");

        Assert.Equal("the value is abc here", redacted);
    }

    [Fact]
    public void Register_AndRedact_RemovesAShortSecretAtTheMinimumRedactableLength()
    {
        var registry = new LiveParitySecretRegistry();
        const string secret = "Zq7m"; // exactly 4 chars: the minimum length that is redacted.
        registry.Register(secret);

        var redacted = registry.Redact($"the value is {secret} here");

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Register_AndRedact_RemovesALongKnownSecret()
    {
        var registry = new LiveParitySecretRegistry();
        var secret = "sk-" + new string('a', 40);
        registry.Register(secret);

        var redacted = registry.Redact($"provider responded using {secret} without issue");

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_RemovesADiscordShapedTokenThatSanitizeForLogAloneWouldMiss()
    {
        // Three dot-separated segments, each individually under the 48-char
        // run that E2ESetupFixture.SanitizeForLog's length heuristic looks
        // for, and with no "token/authorization/secret/password" keyword
        // nearby to trip its keyword heuristic either. This demonstrates
        // exactly why LiveParitySecretRegistry exists: a real Discord bot
        // token can have this shape and slip past the generic sanitizer.
        var fakeToken = $"{new string('a', 24)}.{new string('B', 6)}.{new string('c', 27)}";
        var text = $"observed value {fakeToken} while polling";

        Assert.Equal(text, E2ESetupFixture.SanitizeForLog(text));

        var registry = new LiveParitySecretRegistry();
        registry.Register(fakeToken);
        var redacted = registry.Redact(text);

        Assert.DoesNotContain(fakeToken, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Theory]
    [InlineData(false)] // registered short-then-long
    [InlineData(true)] // registered long-then-short
    public void Redact_OrdersLongestSecretFirst_SoAShorterSecretCannotLeaveAFragmentOfALongerOneBehind(bool registerLongFirst)
    {
        const string shortSecret = "ZQ7MABCDEF";
        const string longSecret = "ZQ7MABCDEFGHIJK"; // shortSecret is a prefix of this.
        var registry = new LiveParitySecretRegistry();
        if (registerLongFirst)
        {
            registry.Register(longSecret);
            registry.Register(shortSecret);
        }
        else
        {
            registry.Register(shortSecret);
            registry.Register(longSecret);
        }

        var redacted = registry.Redact($"observed value {longSecret} in response");

        Assert.DoesNotContain(longSecret, redacted);
        Assert.DoesNotContain(shortSecret, redacted);
        Assert.DoesNotContain("GHIJK", redacted); // the tail unique to the longer secret must not survive.
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_WithNoSecretsRegistered_StillAppliesSanitizeForLogBaseline()
    {
        var registry = new LiveParitySecretRegistry();
        const string text = "authorization: hunter2hunter2hunter2hunter2";

        Assert.Equal(E2ESetupFixture.SanitizeForLog(text), registry.Redact(text));
    }

    [Fact]
    public void Redact_WithNullInput_ReturnsEmptyString()
    {
        var registry = new LiveParitySecretRegistry();

        Assert.Equal(string.Empty, registry.Redact(null));
    }

    [Fact]
    public void Redact_WithEmptyInput_ReturnsEmptyString()
    {
        var registry = new LiveParitySecretRegistry();

        Assert.Equal(string.Empty, registry.Redact(string.Empty));
    }

    [Fact]
    public void Register_IgnoresNullAndEmptyValues_WithoutThrowing()
    {
        var registry = new LiveParitySecretRegistry();

        registry.Register(null);
        registry.Register(string.Empty);

        Assert.Equal("nothing planted here", registry.Redact("nothing planted here"));
    }

    // ── BoundedPoller ─────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_ReturnsFirstNonNullResult_WithoutWaitingForTheFullTimeout()
    {
        var started = DateTime.UtcNow;

        var result = await BoundedPoller.PollAsync(
            () => Task.FromResult<string?>("ready"),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50),
            () => "should not time out");

        var elapsed = DateTime.UtcNow - started;
        Assert.Equal("ready", result);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"expected a fast return, took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task PollAsync_WhenProbeNeverSucceeds_ThrowsTimeoutExceptionWithDescribedMessage_AndStaysBounded()
    {
        var timeout = TimeSpan.FromMilliseconds(300);
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => BoundedPoller.PollAsync(
            () => Task.FromResult<string?>(null),
            timeout,
            TimeSpan.FromMilliseconds(50),
            () => "the probe never observed the expected state"));

        var elapsed = DateTime.UtcNow - started;
        Assert.Equal("the probe never observed the expected state", ex.Message);
        // Bounded: must not run away well past the requested timeout.
        Assert.True(elapsed < timeout + TimeSpan.FromSeconds(5), $"expected a bounded wait, took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task PollAsync_TimeoutMessage_CanBeFullyRedacted_EvenIfDescribeTimeoutCapturedASecret()
    {
        // Proof code must never let a secret reach describeTimeout, but this
        // demonstrates the defense-in-depth contract: even if it did, the
        // registry used to redact all other proof diagnostics also fully
        // removes it from a BoundedPoller timeout message.
        const string secret = "ZQ7MABCDEFsecretvalue0123456789";
        var registry = new LiveParitySecretRegistry();
        registry.Register(secret);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => BoundedPoller.PollAsync(
            () => Task.FromResult<string?>(null),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(50),
            () => $"probe for {secret} timed out"));

        var redactedMessage = registry.Redact(ex.Message);

        Assert.DoesNotContain(secret, redactedMessage);
        Assert.Contains("[REDACTED]", redactedMessage);
    }

    // ── ShellScriptingHelpers ─────────────────────────────────────────────

    [Fact]
    public void SingleQuote_WrapsAPlainValueInSingleQuotes()
    {
        Assert.Equal("'hello'", ShellScriptingHelpers.SingleQuote("hello"));
    }

    [Fact]
    public void SingleQuote_EscapesEmbeddedSingleQuotes()
    {
        Assert.Equal("'it'\"'\"'s a test'", ShellScriptingHelpers.SingleQuote("it's a test"));
    }

    [Fact]
    public void SingleQuote_EmptyValue_ReturnsAnEmptyQuotedPair()
    {
        Assert.Equal("''", ShellScriptingHelpers.SingleQuote(string.Empty));
    }
}
