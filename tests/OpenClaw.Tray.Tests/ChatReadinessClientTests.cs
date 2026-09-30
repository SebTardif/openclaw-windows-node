using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class ChatReadinessClientTests
{
    [Fact]
    public void CreateHandler_disables_the_process_proxy()
    {
        using var handler = ChatReadinessClient.CreateHandler();
        Assert.False(handler.UseProxy);
    }
}
