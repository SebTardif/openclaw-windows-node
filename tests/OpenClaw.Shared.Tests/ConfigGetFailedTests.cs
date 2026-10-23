using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class ConfigGetFailedTests
{
    [Fact]
    public async Task RequestConfigAsync_WhenTheGatewayIsNotConnected_RaisesConfigGetFailed()
    {
        using var client = NewClient();
        IOperatorGatewayClient facade = client;
        var failed = 0;
        var updated = 0;
        facade.ConfigGetFailed += (_, _) => failed++;
        facade.ConfigUpdated += (_, _) => updated++;

        await client.RequestConfigAsync();

        Assert.Equal(1, failed);
        Assert.Equal(0, updated);
    }

    [Fact]
    public void RejectedConfigGet_RaisesConfigGetFailedAndDoesNotPublishConfig()
    {
        using var client = NewClient();
        var failed = 0;
        var updated = 0;
        client.ConfigGetFailed += (_, _) => failed++;
        client.ConfigUpdated += (_, _) => updated++;
        RegisterTracked(client, "config-get-1", "config.get");

        InvokeHandleResponse(client, """
            {
              "type": "res",
              "id": "config-get-1",
              "ok": false,
              "error": { "message": "config unavailable" }
            }
            """);

        Assert.Equal(1, failed);
        Assert.Equal(0, updated);
    }

    [Fact]
    public void RejectedOtherRequest_DoesNotRaiseConfigGetFailed()
    {
        using var client = NewClient();
        var failed = 0;
        client.ConfigGetFailed += (_, _) => failed++;
        RegisterTracked(client, "usage-1", "usage.cost");

        InvokeHandleResponse(client, """
            { "type": "res", "id": "usage-1", "ok": false, "error": { "message": "nope" } }
            """);

        Assert.Equal(0, failed);
    }

    private static OpenClawGatewayClient NewClient() =>
        new("ws://127.0.0.1:1", "test-token", new TestLogger(), identityPath: Path.Combine(Path.GetTempPath(), "openclaw-f119-" + Guid.NewGuid().ToString("n"), "device.json"));

    private static void RegisterTracked(OpenClawGatewayClient client, string requestId, string method)
    {
        var fieldInfo = typeof(OpenClawGatewayClient).GetField(
            "_pendingRequests",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(fieldInfo);
        var registry = Assert.IsType<PendingRequestRegistry>(fieldInfo!.GetValue(client));
        registry.OpenConnection();
        registry.RegisterTracked(requestId, method);
    }

    private static void InvokeHandleResponse(OpenClawGatewayClient client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(OpenClawGatewayClient).GetMethod(
            "HandleResponseForConnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(client, [doc.RootElement, 0L]);
    }
}
