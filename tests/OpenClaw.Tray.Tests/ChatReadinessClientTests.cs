using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class ChatReadinessClientTests
{
    [Fact]
    public void CreateHandler_disables_the_proxy_for_loopback()
    {
        using var handler = ChatReadinessClient.CreateHandler(new Uri("http://127.0.0.1:18789/"));
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void CreateHandler_keeps_the_proxy_for_a_remote_url()
    {
        using var handler = ChatReadinessClient.CreateHandler(new Uri("https://gateway.example/"));
        Assert.True(handler.UseProxy);
    }
}
