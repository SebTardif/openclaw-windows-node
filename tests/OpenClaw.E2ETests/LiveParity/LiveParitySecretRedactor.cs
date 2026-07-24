using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Instance-scoped registry of resolved secret values for a single live
/// model or real channel proof run. <see cref="E2ESetupFixture.SanitizeForLog"/>
/// already redacts common token/secret/password key=value patterns and any
/// standalone run of 48+ alphanumeric characters, but it cannot catch every
/// credential shape used here: a real Discord bot token is three
/// dot-separated segments that can each be under 48 characters on their
/// own, so it can slip past that length heuristic. Registering the exact
/// resolved value here guarantees it is stripped verbatim from any text
/// this lane logs, regardless of its shape or length.
///
/// This is deliberately an instance, not a static/shared registry, so one
/// test's registered secret can never affect another test's redaction
/// assertions.
/// </summary>
internal sealed class LiveParitySecretRegistry
{
    private const int MinimumRedactedLength = 4;
    private readonly List<string> _secrets = [];

    /// <summary>
    /// Registers a resolved secret value for redaction. No-ops for null,
    /// empty, or very short values (too short to usefully redact without
    /// risking collateral redaction of ordinary text).
    /// </summary>
    public void Register(string? secretValue)
    {
        if (string.IsNullOrEmpty(secretValue) || secretValue.Length < MinimumRedactedLength)
            return;
        if (!_secrets.Contains(secretValue))
            _secrets.Add(secretValue);
    }

    /// <summary>
    /// Replaces every registered secret value found in <paramref name="text"/>
    /// with a fixed redaction marker, then delegates to
    /// <see cref="E2ESetupFixture.SanitizeForLog"/> for defense in depth
    /// against the common token/secret/password shapes it already knows.
    /// Longest secrets are redacted first so a short secret that happens to
    /// be a substring of a longer one cannot leave a partial fragment behind.
    /// </summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var sanitized = text;
        foreach (var secret in _secrets.OrderByDescending(value => value.Length))
        {
            if (sanitized.Contains(secret, StringComparison.Ordinal))
                sanitized = sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return E2ESetupFixture.SanitizeForLog(sanitized);
    }
}
