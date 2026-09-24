using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class CanvasGatewayAuthTests
{
    private const string TrustedOrigin = "https://gateway.example";

    [Fact]
    public void ShouldAttach_WhenDocumentAndRequestAreTrustedGatewayOrigin()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            TrustedOrigin,
            TrustedOrigin));
    }

    [Fact]
    public void ShouldAttach_WhenDocumentIsCanvasVirtualHostAndRequestIsTrustedOrigin()
    {
        Assert.True(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "https://openclaw-canvas.local/page",
            TrustedOrigin,
            TrustedOrigin));
    }

    [Fact]
    public void ShouldNotAttach_WhenDocumentIsUntrusted()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            "https://evil.example/page",
            TrustedOrigin,
            TrustedOrigin));
    }

    [Fact]
    public void ShouldNotAttach_WhenRequestIsUntrusted()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            "https://evil.example/",
            TrustedOrigin));
    }

    [Fact]
    public void ShouldNotAttach_WhenRequestIsPrefixLookalike()
    {
        Assert.False(CanvasGatewayAuth.ShouldAttachGatewayBearer(
            TrustedOrigin,
            "https://gateway.example.evil/",
            TrustedOrigin));
    }
}
