namespace OpenClawTray.Services;

internal static class UsageCostApplicationPolicy
{
    public static bool ShouldApply(int responseDays, int selectedPeriodDays)
    {
        if (responseDays <= 0)
            return true;

        // The gateway can compute the requested range inclusively (for example,
        // 8 for a 7-day request). Allow +/-1 so valid responses are not dropped.
        // A 30-day payload must not replace a 7-day selection, including the
        // first payload. The page asks for the selected period itself.
        return Math.Abs(responseDays - selectedPeriodDays) <= 1;
    }
}
