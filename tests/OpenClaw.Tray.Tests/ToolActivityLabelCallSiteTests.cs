namespace OpenClaw.Tray.Tests;

public class ToolActivityLabelCallSiteTests
{
    [Fact]
    public void ToolCommandQueryAndUrlAreRedactedBeforeTruncation()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Shared",
            "OpenClawGatewayClient.cs"));

        Assert.Contains("TruncateText(TokenSanitizer.RedactToolActivityLabel(firstLine), 60)", source);
        Assert.Contains("TruncateText(TokenSanitizer.RedactToolActivityLabel(query.GetString()), 60)", source);
        Assert.Contains("TruncateText(TokenSanitizer.RedactToolActivityLabel(url.GetString()), 60)", source);
    }
}
