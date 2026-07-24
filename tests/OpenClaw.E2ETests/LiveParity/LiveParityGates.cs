namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Central catalog of every environment variable name used by the live-model
/// and real-channel parity lanes. Keeping the names in one place means the
/// gates, profile loader, docs, and validation script can all agree on the
/// exact spelling without duplicating literals.
/// </summary>
internal static class LiveParityEnvVars
{
    /// <summary>Base E2E opt-in gate shared with the rest of OpenClaw.E2ETests.</summary>
    public const string RunE2E = "OPENCLAW_RUN_E2E";

    /// <summary>Opt-in gate for the live model chat-turn proof.</summary>
    public const string RunLiveModelE2E = "OPENCLAW_RUN_LIVE_MODEL_E2E";

    /// <summary>Opt-in gate for the real Discord channel round-trip proof.</summary>
    public const string RunRealChannelE2E = "OPENCLAW_RUN_REAL_CHANNEL_E2E";

    /// <summary>Absolute path to the live model profile JSON file.</summary>
    public const string LiveModelProfilePath = "OPENCLAW_LIVE_MODEL_PROFILE";

    /// <summary>Absolute path to the real channel (Discord) profile JSON file.</summary>
    public const string RealChannelProfilePath = "OPENCLAW_REAL_CHANNEL_PROFILE";

    /// <summary>
    /// True when the named environment variable is set to "1" or "true"
    /// (case-insensitive), matching the convention already used by
    /// <c>E2ETestGate</c> and <c>MxcE2ETestGate</c>.
    /// </summary>
    public static bool IsTruthy(string envVar) =>
        Environment.GetEnvironmentVariable(envVar) is { } value &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Gate for the secret-gated live model parity lane. Disabled by default;
/// when explicitly enabled it never skips for a missing profile or
/// credential; loading/staging code must fail closed instead (see
/// <see cref="LiveParityProfileLoader"/> and <see cref="LiveParityCredentialResolver"/>).
/// </summary>
internal static class LiveModelE2ETestGate
{
    public static string? SkipReason => GetSkipReason();

    private static string? GetSkipReason()
    {
        if (!LiveParityEnvVars.IsTruthy(LiveParityEnvVars.RunE2E))
        {
            return $"E2E tests disabled. Set {LiveParityEnvVars.RunE2E}=1 to enable.";
        }

        if (!LiveParityEnvVars.IsTruthy(LiveParityEnvVars.RunLiveModelE2E))
        {
            return "Live model E2E test disabled. This lane configures a real, user-selected LLM " +
                $"provider and spends real API budget. Set {LiveParityEnvVars.RunLiveModelE2E}=1 " +
                $"(in addition to {LiveParityEnvVars.RunE2E}=1) to enable. See docs/LIVE_PARITY_TESTING.md.";
        }

        // Enabled: intentionally never skip for a missing profile/secret past
        // this point. Missing configuration must fail the test, not skip it.
        return null;
    }
}

/// <summary>
/// Gate for the secret-gated real Discord channel parity lane. Disabled by
/// default; when explicitly enabled it never skips for a missing profile or
/// credential; loading/staging code must fail closed instead (see
/// <see cref="LiveParityProfileLoader"/> and <see cref="LiveParityCredentialResolver"/>).
/// </summary>
internal static class RealChannelE2ETestGate
{
    public static string? SkipReason => GetSkipReason();

    private static string? GetSkipReason()
    {
        if (!LiveParityEnvVars.IsTruthy(LiveParityEnvVars.RunE2E))
        {
            return $"E2E tests disabled. Set {LiveParityEnvVars.RunE2E}=1 to enable.";
        }

        if (!LiveParityEnvVars.IsTruthy(LiveParityEnvVars.RunRealChannelE2E))
        {
            return "Real Discord channel E2E test disabled. This lane posts and polls messages " +
                $"through two real Discord bot accounts. Set {LiveParityEnvVars.RunRealChannelE2E}=1 " +
                $"(in addition to {LiveParityEnvVars.RunE2E}=1) to enable. See docs/LIVE_PARITY_TESTING.md.";
        }

        // Enabled: intentionally never skip for a missing profile/secret past
        // this point. Missing configuration must fail the test, not skip it.
        return null;
    }
}

/// <summary>
/// Focused live model parity test must not run in the regular hosted E2E
/// shards: it configures a real LLM provider and spends real API budget.
/// Opt in locally or on protected self-hosted infrastructure only.
/// </summary>
public sealed class LiveModelE2EFactAttribute : FactAttribute
{
    public LiveModelE2EFactAttribute()
    {
        Skip = LiveModelE2ETestGate.SkipReason;
    }
}

/// <summary>
/// Focused real Discord channel parity test must not run in the regular
/// hosted E2E shards: it exercises two real Discord bot accounts. Opt in
/// locally or on protected self-hosted infrastructure only.
/// </summary>
public sealed class RealChannelE2EFactAttribute : FactAttribute
{
    public RealChannelE2EFactAttribute()
    {
        Skip = RealChannelE2ETestGate.SkipReason;
    }
}
