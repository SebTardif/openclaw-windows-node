namespace OpenClaw.Connection.Tests;

public sealed class BrowserHandoffConsumptionTests
{
    [Fact]
    public void TrySelectExclusiveConsumption_SettlesOnlyTheMatchingHandoff()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { "old" };
        var handoffs = new[]
        {
            new BrowserHandoffView(1, false, 10, null, seen),
            new BrowserHandoffView(2, false, 20, null, seen),
        };
        var rows = new[]
        {
            new ForwardClientRow("old", 10, "chrome"),
            new ForwardClientRow("new-b", 20, "chrome"),
        };

        Assert.False(BrowserHandoffConsumption.TrySelectExclusiveConsumption(
            1, handoffs, rows, new HashSet<string>(), out _));
        Assert.True(BrowserHandoffConsumption.TrySelectExclusiveConsumption(
            2, handoffs, rows, new HashSet<string>(), out var key));
        Assert.Equal("new-b", key);
    }

    [Fact]
    public void TrySelectExclusiveConsumption_DoesNotShareOneRowAcrossHandoffs()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var handoffs = new[]
        {
            new BrowserHandoffView(1, false, null, "chrome", seen),
            new BrowserHandoffView(2, false, null, "chrome", seen),
        };
        var rows = new[] { new ForwardClientRow("shared", 50, "chrome") };

        Assert.False(BrowserHandoffConsumption.TrySelectExclusiveConsumption(
            1, handoffs, rows, new HashSet<string>(), out _));
        Assert.False(BrowserHandoffConsumption.TrySelectExclusiveConsumption(
            2, handoffs, rows, new HashSet<string>(), out _));
    }

    [Fact]
    public void TrySelectExclusiveConsumption_UsesTheBrowserNameWhenTheProcessIdIsUnknown()
    {
        var handoffs = new[]
        {
            new BrowserHandoffView(4, false, null, "msedge", new HashSet<string>()),
        };
        var rows = new[]
        {
            new ForwardClientRow("probe", 8, "powershell"),
            new ForwardClientRow("edge", 9, "msedge"),
        };

        Assert.True(BrowserHandoffConsumption.TrySelectExclusiveConsumption(
            4, handoffs, rows, new HashSet<string>(), out var key));
        Assert.Equal("edge", key);
    }
}
