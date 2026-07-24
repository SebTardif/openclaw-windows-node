using System.Text.RegularExpressions;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Strict field validation for live-parity profiles. Every failure throws
/// <see cref="LiveParityConfigurationException"/> referencing only the
/// field path/name. Values are never echoed: env-var-name fields are
/// rejected outright when they look like a literal credential (so a pasted
/// secret cannot leak through its own validation error), and every other
/// field is validated by shape (ids, URLs, bounds) rather than echoed back,
/// so a malformed profile cannot leak anything sensitive through its own
/// error message.
/// </summary>
internal static class LiveParityValidation
{
    internal const int SupportedSchemaVersion = 1;

    private static readonly Regex EnvVarNameRegex = new("^[A-Z_][A-Z0-9_]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex ProviderIdRegex = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex ModelIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex SnowflakeIdRegex = new("^[0-9]{15,25}$", RegexOptions.Compiled);
    private static readonly Regex DiscordTokenShapeRegex =
        new(@"^[\w-]{20,}\.[\w-]{6,}\.[\w-]{20,}$", RegexOptions.Compiled);

    private static readonly string[] SecretLiteralPrefixes =
    [
        "sk-", "sk_", "xox", "ghp_", "gho_", "github_pat_", "AIza", "Bearer ", "eyJ",
    ];

    public static void EnsureSchemaVersion(string fieldPath, int? value)
    {
        if (value is null || value.Value != SupportedSchemaVersion)
        {
            throw new LiveParityConfigurationException(
                $"{fieldPath} must be {SupportedSchemaVersion}. See the current schema in docs/LIVE_PARITY_TESTING.md.");
        }
    }

    public static string EnsureRequiredString(string fieldPath, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new LiveParityConfigurationException($"{fieldPath} is required.");
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new LiveParityConfigurationException($"{fieldPath} must not contain newlines or NUL characters.");
        return value.Trim();
    }

    public static string EnsureProviderId(string fieldPath, string? value)
    {
        var trimmed = EnsureRequiredString(fieldPath, value);
        if (!ProviderIdRegex.IsMatch(trimmed))
        {
            throw new LiveParityConfigurationException(
                $"{fieldPath} must be a lowercase provider id (letters, digits, hyphens; must start with a letter).");
        }

        return trimmed;
    }

    public static string EnsureModelId(string fieldPath, string? value)
    {
        var trimmed = EnsureRequiredString(fieldPath, value);
        if (!ModelIdRegex.IsMatch(trimmed))
        {
            throw new LiveParityConfigurationException(
                $"{fieldPath} must be a valid model id (letters, digits, dot, hyphen, underscore; 1-128 characters).");
        }

        return trimmed;
    }

    public static string EnsureSnowflakeId(string fieldPath, string? value)
    {
        var trimmed = EnsureRequiredString(fieldPath, value);
        if (!SnowflakeIdRegex.IsMatch(trimmed))
        {
            throw new LiveParityConfigurationException(
                $"{fieldPath} must be a numeric Discord snowflake id (15 to 25 digits).");
        }

        return trimmed;
    }

    /// <summary>
    /// Validates that a field names an environment variable (not a literal
    /// secret). Rejects values that look like a pasted-in credential before
    /// applying the plain env-var-name shape check, so a profile author who
    /// mistakenly pastes a real key gets an actionable, value-free error
    /// instead of the literal secret being accepted and later staged.
    /// </summary>
    public static string EnsureEnvVarName(string fieldPath, string? value)
    {
        var trimmed = EnsureRequiredString(fieldPath, value);
        if (LooksLikeSecretLiteral(trimmed))
        {
            throw new LiveParityConfigurationException(
                $"{fieldPath} looks like a literal credential value, not an environment variable name. " +
                "Profiles must reference credentials by environment variable name only: put the actual " +
                $"secret value in that process environment variable and reference its NAME from {fieldPath} instead.");
        }

        if (!EnvVarNameRegex.IsMatch(trimmed))
        {
            throw new LiveParityConfigurationException(
                $"{fieldPath} must be an upper snake case environment variable name (A-Z, 0-9, underscore; " +
                "must start with a letter or underscore).");
        }

        return trimmed;
    }

    public static int EnsureBoundedInt(string fieldPath, int? value, int defaultValue, int min, int max)
    {
        if (value is null)
            return defaultValue;
        if (value.Value < min || value.Value > max)
            throw new LiveParityConfigurationException($"{fieldPath} must be between {min} and {max}.");

        return value.Value;
    }

    /// <summary>
    /// Best-effort heuristic that a string looks like a real credential
    /// rather than an environment variable name: known provider token
    /// prefixes, the three-segment Discord bot token shape, or a long
    /// mixed-case-plus-digit run without separators (which no valid
    /// environment variable name can contain).
    /// </summary>
    internal static bool LooksLikeSecretLiteral(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        foreach (var prefix in SecretLiteralPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (DiscordTokenShapeRegex.IsMatch(value))
            return true;

        if (value.Length >= 20 && value.Any(char.IsLower) && value.Any(char.IsDigit) && !value.Contains(' '))
            return true;

        return false;
    }
}
