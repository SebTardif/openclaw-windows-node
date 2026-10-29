namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Returns a seven-day cached snapshot for the Gateway's initial request, then
/// simulates a legacy Gateway that always returns thirty-day totals.
/// </summary>
public sealed class UsagePeriodFixture
{
    public const string Name = "usage-period-legacy";

    private int _costRequestCount;

    public object Cost()
    {
        var days = Interlocked.Increment(ref _costRequestCount) == 1 ? 7 : 30;
        var totalCost = days == 7 ? 7.00 : 30.00;
        var totalTokens = days * 100L;
        return new
        {
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            days,
            daily = new[] { new { date = "2026-10-28", totalCost } },
            totals = new
            {
                input = totalTokens,
                output = 0,
                cacheRead = 0,
                cacheWrite = 0,
                totalTokens,
                totalCost,
                missingCostEntries = 0,
            },
        };
    }
}
