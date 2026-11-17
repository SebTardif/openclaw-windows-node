namespace OpenClaw.Tray.Tests;

public sealed class LegacyAgentFileApplyTests
{
    [Fact]
    public void IsCurrent_AcceptsTheSameAgentAndToken()
    {
        var request = new OpenClawTray.Pages.LegacyAgentFileApply.Request("main", "SOUL.md", 4);
        Assert.True(OpenClawTray.Pages.LegacyAgentFileApply.IsCurrent(request, "MAIN", 4));
    }

    [Fact]
    public void IsCurrent_RejectsADifferentAgent()
    {
        var request = new OpenClawTray.Pages.LegacyAgentFileApply.Request("main", "SOUL.md", 4);
        Assert.False(OpenClawTray.Pages.LegacyAgentFileApply.IsCurrent(request, "other", 4));
    }

    [Fact]
    public void IsCurrent_RejectsANewerLoadToken()
    {
        var request = new OpenClawTray.Pages.LegacyAgentFileApply.Request("main", "SOUL.md", 4);
        Assert.False(OpenClawTray.Pages.LegacyAgentFileApply.IsCurrent(request, "main", 5));
    }

    [Fact]
    public void IsCurrent_RejectsAMissingRequest()
    {
        Assert.False(OpenClawTray.Pages.LegacyAgentFileApply.IsCurrent(null, "main", 4));
    }

    [Fact]
    public void ReturnedNameMatches_AcceptsTheRequestPathOrItsLeaf()
    {
        Assert.True(OpenClawTray.Pages.LegacyAgentFileApply.ReturnedNameMatches(
            "agents/main/SOUL.md",
            "agents/main/SOUL.md"));
        Assert.True(OpenClawTray.Pages.LegacyAgentFileApply.ReturnedNameMatches(
            "agents/main/SOUL.md",
            "SOUL.md"));
    }

    [Fact]
    public void ReturnedNameMatches_RejectsADifferentFile()
    {
        Assert.False(OpenClawTray.Pages.LegacyAgentFileApply.ReturnedNameMatches(
            "agents/main/SOUL.md",
            "agents/other/TOOLS.md"));
        Assert.False(OpenClawTray.Pages.LegacyAgentFileApply.ReturnedNameMatches(
            "agents/main/SOUL.md",
            "agents/other/SOUL.md"));
    }
}
