using System.Text.Json;

namespace OpenClawTray.Pages;

/// <summary>
/// Fields the Diagnostics page may copy from the active gateway identity.
/// The private key is never read.
/// </summary>
public static class DiagnosticsDeviceIdentity
{
    public const string FileName = "device-key-ed25519.json";

    public readonly record struct CopyFields(string? DeviceId, string? PublicKey);

    public static string IdentityFilePath(string identityDirectory) =>
        Path.Combine(identityDirectory, FileName);

    public static CopyFields ReadCopyFields(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new(ReadString(root, "DeviceId"), ReadString(root, "PublicKeyBase64"));
    }

    public static string Shorten(string deviceId) =>
        deviceId.Length > 24
            ? string.Concat(deviceId.AsSpan(0, 16), "…", deviceId.AsSpan(deviceId.Length - 6))
            : deviceId;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;
}
