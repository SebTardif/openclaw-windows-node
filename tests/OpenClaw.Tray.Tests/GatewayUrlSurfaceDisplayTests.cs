namespace OpenClaw.Tray.Tests;

public class GatewayUrlSurfaceDisplayTests
{
    [Fact]
    public void TrayDetailAndLocalAiFallbackSanitizeTheGatewayUrl()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var tray = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Presentation", "TrayMenuPresenter.cs"));
        var localAi = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Presentation", "LocalAiPageViewModel.cs"));

        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(gatewayUri.ToString())", tray);
        Assert.DoesNotContain("Detail = gatewayUri?.ToString()", tray);

        Assert.Contains("_gatewaySnapshot.GatewayUrl is null", localAi);
        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(_gatewaySnapshot.GatewayUrl)", localAi);
        Assert.DoesNotContain("GatewayName ?? _gatewaySnapshot.GatewayUrl;", localAi);
    }
}
