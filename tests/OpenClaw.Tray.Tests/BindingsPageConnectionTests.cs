namespace OpenClaw.Tray.Tests;

public sealed class BindingsPageConnectionTests
{
    [Fact]
    public void ConfigRequest_RequiresAConnectedGatewayClient()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "BindingsPage.xaml.cs"));

        AssertRequestRequiresConnection(MethodBody(source, "public void Initialize()"));
        AssertRequestRequiresConnection(MethodBody(source, "private void RefreshButton_Click"));
        var request = MethodBody(source, "private void RequestConfig(");
        Assert.Contains("RequestConfigAsync()", request, StringComparison.Ordinal);
        Assert.Contains(
            "RefreshButton.IsEnabled = CurrentApp.GatewayClient is { IsConnectedToGateway: true }",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertRequestRequiresConnection(string method)
    {
        var gate = method.IndexOf("IsConnectedToGateway: true", StringComparison.Ordinal);
        var request = method.IndexOf("RequestConfig(client)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "Expected the connected-gateway check.");
        Assert.True(request > gate, "RequestConfig must follow the connected-gateway check.");
        Assert.DoesNotContain("if (client != null)", method, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {signature}");
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        Assert.True(next > start);
        return source[start..next];
    }
}
