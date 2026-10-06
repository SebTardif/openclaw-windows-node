using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Connection;

/// <summary>
/// Startup credential choice after a missing per-gateway identity is checked
/// against the legacy settings identity for the same gateway URL.
/// </summary>
internal readonly record struct LegacyStartupCredentialChoice(
    GatewayCredentialResolution Resolution,
    string IdentityDirectory,
    bool Copied,
    string? CopyError);

/// <summary>
/// Keeps a stored device token ahead of a shared or bootstrap token when the
/// per-gateway identity file was never copied from the legacy settings path.
/// </summary>
internal static class LegacyStartupDeviceToken
{
    public const string IdentityFileName = "device-key-ed25519.json";

    /// <summary>
    /// Records the gateway URL a copied legacy identity was accepted for.
    /// A later URL change must not send that device token.
    /// </summary>
    public const string BoundUrlFileName = "legacy-identity-bound-url.txt";

    public static bool IsStoredDeviceCredential(GatewayCredential? credential) =>
        credential?.Source is CredentialResolver.SourceDeviceToken
            or CredentialResolver.SourceNodeDeviceToken;

    public static LegacyStartupCredentialChoice Prefer(
        GatewayCredentialResolution primary,
        string recordUrl,
        string? effectiveGatewayUrl,
        string perGatewayIdentityDirectory,
        string legacySettingsDirectory,
        Func<string, GatewayCredentialResolution> resolveFromDirectory)
    {
        ArgumentNullException.ThrowIfNull(resolveFromDirectory);

        if (IsStoredDeviceCredential(primary.Credential)
            || !UrlsMatch(recordUrl, effectiveGatewayUrl)
            || IdentityFileExists(perGatewayIdentityDirectory)
            || !IdentityFileExists(legacySettingsDirectory))
        {
            return new(primary, perGatewayIdentityDirectory, Copied: false, CopyError: null);
        }

        var legacy = resolveFromDirectory(legacySettingsDirectory);
        if (!IsStoredDeviceCredential(legacy.Credential))
            return new(primary, perGatewayIdentityDirectory, Copied: false, CopyError: null);

        var copyError = TryCopyLegacyIdentity(
            perGatewayIdentityDirectory,
            legacySettingsDirectory,
            recordUrl);
        if (copyError == null)
        {
            return new(
                resolveFromDirectory(perGatewayIdentityDirectory),
                perGatewayIdentityDirectory,
                Copied: true,
                CopyError: null);
        }

        return new(legacy, legacySettingsDirectory, Copied: false, CopyError: copyError);
    }

    private static bool UrlsMatch(string recordUrl, string? effectiveGatewayUrl) =>
        !string.IsNullOrWhiteSpace(effectiveGatewayUrl)
        && string.Equals(recordUrl, effectiveGatewayUrl, StringComparison.OrdinalIgnoreCase);

    private static bool IdentityFileExists(string directory) =>
        File.Exists(Path.Combine(directory, IdentityFileName));

    /// <summary>
    /// A stored device token is usable unless this directory was stamped for a
    /// different gateway URL. Unstamped identities keep the previous behavior.
    /// </summary>
    public static bool AllowsStoredDeviceToken(string identityDirectory, string recordUrl)
    {
        var path = Path.Combine(identityDirectory, BoundUrlFileName);
        if (!File.Exists(path))
            return true;

        var bound = File.ReadAllText(path).Trim();
        return string.Equals(bound, recordUrl?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static void StampBoundUrl(string identityDirectory, string recordUrl)
    {
        if (string.IsNullOrWhiteSpace(recordUrl))
            throw new ArgumentException("A copied legacy identity needs a gateway URL.", nameof(recordUrl));

        Directory.CreateDirectory(identityDirectory);
        File.WriteAllText(Path.Combine(identityDirectory, BoundUrlFileName), recordUrl.Trim());
    }

    /// <summary>
    /// Root identity when it is allowed for this URL. A stamped root that belongs
    /// to another URL uses a directory named only for this URL, stamped before
    /// any credential is read, so a later URL cannot reuse that pairing.
    /// </summary>
    public static string SelectIdentityDirectory(string perGatewayIdentityDirectory, string recordUrl)
    {
        if (string.IsNullOrWhiteSpace(recordUrl) ||
            AllowsStoredDeviceToken(perGatewayIdentityDirectory, recordUrl))
        {
            return perGatewayIdentityDirectory;
        }

        var realm = Path.Combine(perGatewayIdentityDirectory, "realms", RealmKey(recordUrl));
        Directory.CreateDirectory(realm);
        if (!File.Exists(Path.Combine(realm, BoundUrlFileName)))
            StampBoundUrl(realm, recordUrl);
        return realm;
    }

    private static string RealmKey(string recordUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(recordUrl.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static string? TryCopyLegacyIdentity(
        string perGatewayIdentityDirectory,
        string legacySettingsDirectory,
        string recordUrl)
    {
        var destination = Path.Combine(perGatewayIdentityDirectory, IdentityFileName);
        try
        {
            if (File.Exists(destination))
                return null;

            if (!Directory.Exists(perGatewayIdentityDirectory))
                Directory.CreateDirectory(perGatewayIdentityDirectory);

            File.Copy(
                Path.Combine(legacySettingsDirectory, IdentityFileName),
                destination,
                overwrite: false);
            StampBoundUrl(perGatewayIdentityDirectory, recordUrl);
            return null;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
            catch (IOException)
            {
            }

            return ex.Message;
        }
    }
}
