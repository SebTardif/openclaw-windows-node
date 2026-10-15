namespace OpenClaw.Connection;

/// <summary>
/// Dashboard launch either uses the saved endpoint, or an HTTP loopback origin
/// already bound to the current SSH process generation.
/// </summary>
public readonly record struct DashboardLaunchBinding(bool TunnelRequired, string? OwnedHttpOrigin)
{
    public static DashboardLaunchBinding Direct() => new(false, null);

    public static DashboardLaunchBinding ForTunnel(SshTunnelConfig? tunnel, string? ownedHttpOrigin)
    {
        if (tunnel is null)
            return Direct();

        return new(true, string.IsNullOrWhiteSpace(ownedHttpOrigin) ? null : ownedHttpOrigin);
    }
}
