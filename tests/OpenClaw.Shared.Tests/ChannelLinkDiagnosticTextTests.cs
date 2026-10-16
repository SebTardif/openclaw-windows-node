using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ChannelLinkDiagnosticTextTests
{
    private const string ImagePayload = "data:image/png;base64,pairingimagesecret";

    [Fact]
    public void Format_OmitsTheQrImageFromAFailedWait()
    {
        var parameters = "{\"currentQrDataUrl\":\"" + ImagePayload + "\",\"timeoutMs\":30000}";
        var response = "{\"qrDataUrl\":\"" + ImagePayload + "\",\"error\":\"wait failed\"}";

        var text = ChannelLinkDiagnosticText.Format(
            "web.login.wait",
            "whatsapp",
            parameters,
            "wait failed " + ImagePayload,
            response);

        Assert.Contains("Method:   web.login.wait", text, StringComparison.Ordinal);
        Assert.Contains("Channel:  whatsapp", text, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMs\":30000", text, StringComparison.Ordinal);
        Assert.Contains("\"currentQrDataUrl\":\"<omitted>\"", text, StringComparison.Ordinal);
        Assert.Contains("\"qrDataUrl\":\"<omitted>\"", text, StringComparison.Ordinal);
        Assert.Contains("wait failed <omitted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pairingimagesecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ImagePayload, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_RedactsTheImageBeforeTheResponseIsTrimmed()
    {
        var payload = "data:image/png;base64," + new string('A', 1200);
        var text = ChannelLinkDiagnosticText.Format(
            "web.login.wait",
            "whatsapp",
            "{}",
            null,
            payload);

        Assert.Contains("Response: <omitted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_KeepsAStartCallThatHasNoImage()
    {
        var text = ChannelLinkDiagnosticText.Format(
            "web.login.start",
            "whatsapp",
            "{\"force\":false,\"timeoutMs\":30000}",
            "web login provider is not available",
            "{\"ok\":false}");

        Assert.Contains("{\"force\":false,\"timeoutMs\":30000}", text, StringComparison.Ordinal);
        Assert.Contains("web login provider is not available", text, StringComparison.Ordinal);
        Assert.Contains("{\"ok\":false}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<omitted>", text, StringComparison.Ordinal);
    }
}
