namespace OpenClaw.Connection;

internal readonly record struct BrowserHandoffView(
    long Id,
    bool Settled,
    int? BrowserProcessId,
    string? BrowserProcessName,
    IReadOnlySet<string>? Seen);

internal readonly record struct ForwardClientRow(string Key, int ProcessId, string? ProcessName);

internal static class BrowserHandoffConsumption
{
    internal static bool TrySelectExclusiveConsumption(
        long handoffId,
        IReadOnlyList<BrowserHandoffView> handoffs,
        IReadOnlyList<ForwardClientRow> rows,
        IReadOnlySet<string> claimed,
        out string? key)
    {
        key = null;
        BrowserHandoffView? target = null;
        foreach (var handoff in handoffs)
        {
            if (handoff.Id == handoffId && !handoff.Settled)
                target = handoff;
        }

        if (target is null)
            return false;

        foreach (var row in rows)
        {
            if (claimed.Contains(row.Key) || !Matches(target.Value, row))
                continue;

            var owners = 0;
            foreach (var handoff in handoffs)
            {
                if (!handoff.Settled && Matches(handoff, row))
                    owners++;
            }

            if (owners != 1)
                continue;

            key = row.Key;
            return true;
        }

        return false;
    }

    private static bool Matches(BrowserHandoffView handoff, ForwardClientRow row)
    {
        if (handoff.Seen is null || handoff.Seen.Contains(row.Key))
            return false;
        if (handoff.BrowserProcessId is int processId)
            return row.ProcessId == processId;
        return !string.IsNullOrEmpty(handoff.BrowserProcessName) &&
            string.Equals(handoff.BrowserProcessName, row.ProcessName, StringComparison.OrdinalIgnoreCase);
    }
}
