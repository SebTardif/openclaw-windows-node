namespace OpenClaw.Tray.Tests;

public class SetupCodePreviewUrlDisplayTests
{
    [Fact]
    public void BothSetupCodePreviewsSanitizeTheDecodedUrl()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var status = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Windows", "ConnectionStatusWindow.xaml.cs"));
        var page = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "ConnectionPage.xaml.cs"));

        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(decoded.Url)", status);
        Assert.Contains("SetupCodePreview.Text = $\"→ {previewUrl}", status);
        Assert.DoesNotContain("SetupCodePreview.Text = $\"→ {decoded.Url", status);

        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(decoded.Url)", page);
        Assert.Contains("AddSetupCodePreviewUrl.Text = string.Format(LocalizationHelper.GetString(\"ConnectionPage_PreviewGateway\"), previewUrl);", page);
        Assert.DoesNotContain("ConnectionPage_PreviewGateway\"), decoded.Url", page);
    }
}
