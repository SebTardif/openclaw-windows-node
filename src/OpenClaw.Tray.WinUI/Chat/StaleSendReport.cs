namespace OpenClawTray.Chat;

/// <summary>
/// A send that fails after the connection generation has moved still
/// completes normally, so a reset is not reported as an error on the new
/// session. The composer port must not treat that completion as accepted.
/// </summary>
public static class StaleSendReport
{
    private static readonly AsyncLocal<bool> Superseded = new();

    public static void MarkSuperseded() => Superseded.Value = true;

    public static bool ConsumeSuperseded()
    {
        var superseded = Superseded.Value;
        Superseded.Value = false;
        return superseded;
    }
}
