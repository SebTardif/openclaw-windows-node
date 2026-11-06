using OpenClawTray.Pages;

namespace OpenClaw.Tray.Tests;

public sealed class DiagnosticsDeviceIdentityTests
{
    [Fact]
    public void ReadCopyFields_UsesTheGatewayNamesAndSkipsThePrivateKey()
    {
        const string json = """
            {
              "PrivateKeyBase64": "SECRET-KEY",
              "PublicKeyBase64": "PUBLIC-KEY",
              "DeviceId": "0123456789abcdef0123456789abcdef"
            }
            """;

        var fields = DiagnosticsDeviceIdentity.ReadCopyFields(json);

        Assert.Equal("0123456789abcdef0123456789abcdef", fields.DeviceId);
        Assert.Equal("PUBLIC-KEY", fields.PublicKey);
        Assert.Equal("0123456789abcdef…abcdef", DiagnosticsDeviceIdentity.Shorten(fields.DeviceId!));
        Assert.DoesNotContain("SECRET-KEY", $"{fields.DeviceId} {fields.PublicKey}");
    }

    [Fact]
    public void ReadCopyFields_IgnoresTheLegacyLocalFileNames()
    {
        const string json = """
            {"deviceId":"legacy-id","publicKey":"legacy-key","PrivateKeyBase64":"SECRET-KEY"}
            """;

        var fields = DiagnosticsDeviceIdentity.ReadCopyFields(json);

        Assert.Null(fields.DeviceId);
        Assert.Null(fields.PublicKey);
    }

    [Fact]
    public void IdentityFilePath_IsInsideTheGatewayDirectory()
    {
        var path = DiagnosticsDeviceIdentity.IdentityFilePath(
            Path.Combine("gateways", "gateway-1"));

        Assert.Equal(
            Path.Combine("gateways", "gateway-1", "device-key-ed25519.json"),
            path);
    }

    [Fact]
    public void DebugPage_ReadsTheGatewayIdentityInsteadOfTheLocalAppDataFile()
    {
        var source = File.ReadAllText(FindPage());

        Assert.Contains("DiagnosticsDeviceIdentity.IdentityFilePath", source);
        Assert.Contains("GetIdentityDirectory", source);
        Assert.DoesNotContain("\"deviceId\"", source);
        Assert.DoesNotContain("\"publicKey\"", source);
        Assert.DoesNotContain("PrivateKeyBase64", source);
        Assert.DoesNotContain("device-key-ed25519.json", source);
    }

    private static string FindPage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "openclaw-windows-node.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("Could not find the tray solution.");
        return Path.Combine(dir.FullName, "src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
    }
}
