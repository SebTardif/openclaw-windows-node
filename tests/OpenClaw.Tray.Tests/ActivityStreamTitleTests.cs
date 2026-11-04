using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ActivityStreamTitleTests
{
    [Fact]
    public void TitleUsesToolAndPhaseAndOmitsTheCommandLabel()
    {
        var activity = new AgentActivity
        {
            SessionKey = "agent:main",
            Kind = ActivityKind.Exec,
            ToolName = "exec",
            State = "running",
            Label = "curl https://user:secret@gateway.example/path?token=stored"
        };

        var title = ActivityStreamTitle.Format(activity.SessionKey, activity);

        Assert.Equal("agent:main: exec running", title);
        Assert.DoesNotContain("curl", title);
        Assert.DoesNotContain("secret", title);
        Assert.DoesNotContain("token=", title);
    }

    [Fact]
    public void SupportCallSiteDoesNotCopyTheActivityLabel()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "GatewayService.cs"));

        Assert.Contains("ActivityStreamTitle.Format(sessionKey, activity)", source);
        Assert.DoesNotContain("activity.Label", source);
    }
}
