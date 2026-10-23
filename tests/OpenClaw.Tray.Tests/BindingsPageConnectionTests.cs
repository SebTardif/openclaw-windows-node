using OpenClawTray.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using System.Reflection;

namespace OpenClaw.Tray.Tests;

public sealed class BindingsPageConnectionTests
{
    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Error)]
    public void RecoveryThenSameClientDrop_ClearsLoadingAndShowsDisconnected(ConnectionStatus droppedStatus)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);

            gateway.SetStatus(ConnectionStatus.Connected);
            Assert.Equal(0, gateway.ConfigRequests);
            page.DispatcherQueue.Drain();
            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);
            Assert.False(page.ConnectionInfoBar.IsOpen);

            // No config response is delivered before the same client loses its socket.
            gateway.SetStatus(droppedStatus);
            page.DispatcherQueue.Drain();

            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);
            Assert.Equal("BindingsPage_GatewayDisconnected.Title", page.ConnectionInfoBar.Title);
            Assert.False(page.RefreshButton.IsEnabled);
            Assert.Equal(1, gateway.ConfigRequests);
        }
        finally
        {
            page.Unload();
            Assert.Null(gateway.StatusChanged);
            Application.Current = null;
        }
    }

    [Fact]
    public void QueuedReconnectEvents_RequestConfigOnce()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();

            // Both events are queued before the UI thread runs, so each callback sees the reconnected client.
            gateway.SetStatus(ConnectionStatus.Connecting);
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();

            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);
            Assert.False(page.ConnectionInfoBar.IsOpen);
        }
        finally
        {
            page.Unload();
            Application.Current = null;
        }
    }

    [Fact]
    public void ConfigGetRejected_ClearsTheSpinnerAndEnablesRefresh()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        gateway.FailConfigGet = true;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();

            Assert.Equal(1, gateway.ConfigRequests);
            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);
            Assert.Equal("BindingsPage_CouldNotLoadBindings", page.ConnectionInfoBar.Title);
            Assert.Equal("BindingsPage_ConfigRequestFailed.Message", page.ConnectionInfoBar.Message);
            Assert.Equal(InfoBarSeverity.Error, page.ConnectionInfoBar.Severity);
            Assert.True(page.RefreshButton.IsEnabled);

            using var config = System.Text.Json.JsonDocument.Parse("""{"bindings":[]}""");
            page.UpdateConfig(config.RootElement);
            Assert.False(page.ConnectionInfoBar.IsOpen);
            Assert.True(page.RefreshButton.IsEnabled);
        }
        finally
        {
            page.Unload();
            Assert.Null(gateway.ConfigGetFailed);
            Application.Current = null;
        }
    }

    [Fact(Timeout = 5000)]
    public async Task ConfigGetSilence_ClearsTheSpinnerWhenTheDeadlineExpires()
    {
        var previous = BindingsConfigLoadDeadline.OverrideMilliseconds;
        BindingsConfigLoadDeadline.OverrideMilliseconds = 30;
        var client = DispatchProxy.Create<IOperatorGatewayClient, BindingsGatewayProxy>();
        var gateway = (BindingsGatewayProxy)client;
        Application.Current = new OpenClawTray.App { GatewayClient = client };
        var page = new BindingsPage();
        try
        {
            page.Initialize();
            gateway.SetStatus(ConnectionStatus.Connected);
            page.DispatcherQueue.Drain();
            Assert.Equal(Visibility.Visible, page.LoadingState.Visibility);

            await Task.Delay(80);
            page.DispatcherQueue.Drain();

            Assert.Equal(Visibility.Collapsed, page.LoadingState.Visibility);
            Assert.True(page.ConnectionInfoBar.IsOpen);
            Assert.Equal("BindingsPage_ConfigRequestFailed.Message", page.ConnectionInfoBar.Message);
            Assert.True(page.RefreshButton.IsEnabled);
        }
        finally
        {
            page.Unload();
            BindingsConfigLoadDeadline.OverrideMilliseconds = previous;
            Application.Current = null;
        }
    }

    public class BindingsGatewayProxy : DispatchProxy
    {
        public bool Connected { get; private set; }
        public bool FailConfigGet { get; set; }
        public int ConfigRequests { get; private set; }
        public EventHandler<ConnectionStatus>? StatusChanged { get; private set; }
        public EventHandler? ConfigGetFailed { get; private set; }

        public void SetStatus(ConnectionStatus status)
        {
            Connected = status == ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, status);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_IsConnectedToGateway":
                    return Connected;
                case "add_StatusChanged":
                    StatusChanged += (EventHandler<ConnectionStatus>)args![0]!;
                    return null;
                case "remove_StatusChanged":
                    StatusChanged -= (EventHandler<ConnectionStatus>)args![0]!;
                    return null;
                case "add_ConfigGetFailed":
                    ConfigGetFailed += (EventHandler)args![0]!;
                    return null;
                case "remove_ConfigGetFailed":
                    ConfigGetFailed -= (EventHandler)args![0]!;
                    return null;
                case "RequestConfigAsync":
                    ConfigRequests++;
                    if (FailConfigGet)
                        ConfigGetFailed?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }
    }

    [Theory]
    [InlineData(null, 15_000)]
    [InlineData("", 15_000)]
    [InlineData("0", 15_000)]
    [InlineData("-4", 15_000)]
    [InlineData("nope", 15_000)]
    [InlineData("250", 250)]
    public void ConfigLoadDeadline_UsesAPositiveOverrideOrTheDefault(string? environmentValue, int expected)
    {
        Assert.Equal(expected, BindingsConfigLoadDeadline.ResolveMilliseconds(environmentValue));
    }

    [Theory]
    [InlineData(false, true, "Ignore")]
    [InlineData(false, false, "Ignore")]
    [InlineData(true, false, "ShowDisconnected")]
    [InlineData(true, true, "Recover")]
    public void DecideQueued_RechecksObservationAndConnection(
        bool observing,
        bool connected,
        string expected)
    {
        var decision = BindingsConnectionRecovery.DecideQueued(observing, connected);
        Assert.Equal(expected, decision.ToString());
    }

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
