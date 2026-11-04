namespace OpenClaw.Tray.Tests;

public class ConnectionStatusUrlDisplayTests
{
    [Fact]
    public void SavedGatewayListSanitizesTheUrl()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "ConnectionStatusWindow.xaml.cs"));

        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(gw.Url)", source);
        Assert.DoesNotContain("$\"{statusIcon} {gw.Url}\"", source);
    }
}
