namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Reusable bounded polling helper shared by the live model chat-turn wait
/// and the real Discord channel nonce wait. Polls a probe on a fixed
/// interval until it returns a non-null result or the timeout elapses.
/// Never polls indefinitely: the deadline is checked after every probe, and
/// the final wait is clipped so the loop cannot run meaningfully longer
/// than the requested timeout.
/// </summary>
internal static class BoundedPoller
{
    public static async Task<T> PollAsync<T>(
        Func<Task<T?>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<string> describeTimeout)
        where T : class
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (true)
        {
            var result = await probe().ConfigureAwait(false);
            if (result is not null)
                return result;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            var delay = remaining < pollInterval ? remaining : pollInterval;
            // slopwatch-ignore: SW004 Bounded polling delay is intentional and clipped to the caller's timeout while waiting for external process/service state.
            await Task.Delay(delay).ConfigureAwait(false);
        }

        throw new TimeoutException(describeTimeout());
    }
}
