using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UsageCostApplicationPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldApply_AcceptsUnspecifiedGatewayPeriod(int responseDays)
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays,
            selectedPeriodDays: 7));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ShouldApply_AcceptsInclusiveRangeAroundSelectedPeriod(int responseDays)
    {
        Assert.True(UsageCostApplicationPolicy.ShouldApply(
            responseDays,
            selectedPeriodDays: 7));
    }

    [Fact]
    public void ShouldApply_RejectsFirstMismatchedResponse()
    {
        Assert.False(UsageCostApplicationPolicy.ShouldApply(
            responseDays: 30,
            selectedPeriodDays: 7));
    }

    [Fact]
    public void ShouldApply_RejectsLaterMismatchedResponse()
    {
        Assert.False(UsageCostApplicationPolicy.ShouldApply(
            responseDays: 30,
            selectedPeriodDays: 7));
    }

    [Fact]
    public void UsagePage_DoesNotRequestFixed30DayCostOrMoveTheSelector()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "UsagePage.xaml.cs"));
        var start = source.IndexOf("private void RequestRefresh(", StringComparison.Ordinal);
        var end = source.IndexOf("private void OnOpenConnectionClick", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var refresh = source[start..end];
        Assert.DoesNotContain("RequestUsageAsync(", refresh, StringComparison.Ordinal);
        Assert.Contains("RequestUsageCostAsync(_currentPeriodDays)", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncSelectorToServerDays", source, StringComparison.Ordinal);
    }
}
