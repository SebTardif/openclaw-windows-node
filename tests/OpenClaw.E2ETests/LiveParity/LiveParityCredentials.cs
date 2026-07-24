namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Resolves required secret values from named process environment
/// variables only. Never reads arbitrary host/user config or files.
/// Failure messages name the environment variable and its purpose but
/// never its value.
/// </summary>
internal static class LiveParityCredentialResolver
{
    private const int MaxSecretLength = 4096;

    public static string ResolveRequiredSecret(string envVarName, string purpose)
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(value))
        {
            throw new LiveParityConfigurationException(
                $"{envVarName} is not set. Set it to the {purpose} before enabling this lane. " +
                "See docs/LIVE_PARITY_TESTING.md.");
        }

        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new LiveParityConfigurationException(
                $"{envVarName} must not contain newline or NUL characters.");
        }

        if (value.Length > MaxSecretLength)
        {
            throw new LiveParityConfigurationException(
                $"{envVarName} exceeds the maximum accepted credential length ({MaxSecretLength} characters).");
        }

        return value;
    }
}
