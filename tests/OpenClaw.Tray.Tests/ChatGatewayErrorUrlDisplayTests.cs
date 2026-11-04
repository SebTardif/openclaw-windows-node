namespace OpenClaw.Tray.Tests;

public class ChatGatewayErrorUrlDisplayTests
{
    [Fact]
    public void ConnectionErrorsSanitizeTheGatewayUrl()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ChatPage.xaml.cs"));

        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(credential.GatewayUrl)", source);
        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)", source);
        Assert.DoesNotContain("ChatPage_CannotConnectToGateway\"), credential.GatewayUrl)", source);
        Assert.DoesNotContain("ChatPage_InvalidGatewayUrl\"), gatewayUrl)", source);
    }
}
