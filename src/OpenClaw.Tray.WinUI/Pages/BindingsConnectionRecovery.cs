namespace OpenClawTray.Pages;

/// <summary>
/// Decides what a Bindings callback should do once it reaches the UI thread.
/// A handshake can drop, or the page can unload, between the event and the callback.
/// </summary>
internal static class BindingsConnectionRecovery
{
    internal enum Decision
    {
        Ignore,
        Recover,
        ShowDisconnected,
    }

    internal static Decision DecideQueued(bool observing, bool connected)
    {
        if (!observing)
            return Decision.Ignore;
        return connected ? Decision.Recover : Decision.ShowDisconnected;
    }
}

/// <summary>
/// How long Bindings waits for a connected <c>config.get</c> before it
/// gives up. <see cref="EnvironmentVariableName"/> overrides the default.
/// </summary>
internal static class BindingsConfigLoadDeadline
{
    internal const int DefaultMilliseconds = 15_000;
    internal const string EnvironmentVariableName = "OPENCLAW_BINDINGS_CONFIG_TIMEOUT_MS";

    /// <summary>Test seam. A positive value replaces the environment and the default.</summary>
    internal static int? OverrideMilliseconds { get; set; }

    internal static int ResolveMilliseconds(string? environmentValue)
    {
        if (int.TryParse(
                environmentValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return DefaultMilliseconds;
    }

    internal static int CurrentMilliseconds =>
        OverrideMilliseconds is int overridden && overridden > 0
            ? overridden
            : ResolveMilliseconds(Environment.GetEnvironmentVariable(EnvironmentVariableName));
}
