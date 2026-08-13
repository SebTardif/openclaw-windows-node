using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Reads existing owners and supplies facts to <see cref="CapabilityTruthProjection"/>.
/// It does not own settings, connection lifecycle, approvals, or runtime probes.
/// </summary>
internal static class NodeCapabilityTruthSource
{
    private static readonly string[] CameraCommands = ["camera.list", "camera.snap", "camera.clip"];
    private static readonly string[] BrowserCommands = ["browser.proxy"];
    private static readonly string[] SystemRunCommands = ["system.run", "system.run.prepare"];

    public static IReadOnlyList<CapabilityTruthProjection.State> Build(
        SettingsManager? settings,
        NodeService? nodeService,
        GatewayNodeInfo? gatewayNode,
        RoleConnectionState nodeConnectionState)
    {
        var localCommands = nodeService?.GetRegisteredCommands() ?? [];
        var mcpRunning = nodeService?.IsMcpRunning == true;
        var sessionLive = nodeConnectionState == RoleConnectionState.Connected;
        var approval = ResolveApproval(sessionLive, gatewayNode?.ApprovalState);
        var effective = gatewayNode?.Commands ?? [];
        var pending = gatewayNode?.PendingDeclaredCommands ?? [];
        var permissions = gatewayNode?.Permissions
            ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var browserReadiness = nodeService?.BrowserProxyReadiness;
        var browserRuntime = browserReadiness is { IsReady: true }
            ? CapabilityTruthProjection.RuntimeKind.Ready
            : browserReadiness is null
                ? CapabilityTruthProjection.RuntimeKind.Unknown
                : CapabilityTruthProjection.RuntimeKind.Blocked;

        return
        [
            CapabilityTruthProjection.Project(new(
                "camera",
                "Camera",
                CameraCommands,
                settings?.NodeCameraEnabled != false,
                nodeService?.CameraWindowsPermission ?? CapabilityTruthProjection.WindowsPermissionKind.Unknown,
                sessionLive,
                approval,
                effective,
                pending,
                permissions,
                mcpRunning && ContainsAll(localCommands, CameraCommands),
                ContainsAll(localCommands, CameraCommands)
                    ? CapabilityTruthProjection.RuntimeKind.Unknown
                    : CapabilityTruthProjection.RuntimeKind.Blocked,
                ContainsAll(localCommands, CameraCommands)
                    ? "Camera commands are registered. Windows camera permission and device availability are verified on invocation."
                    : "Camera commands are not registered in the current node runtime.",
                "Open Windows Camera privacy settings, allow desktop apps, confirm a camera is present, then retry camera.list or camera.snap.")),
            CapabilityTruthProjection.Project(new(
                "browser-proxy",
                "Browser proxy",
                BrowserCommands,
                settings?.NodeBrowserProxyEnabled != false,
                CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
                sessionLive,
                approval,
                effective,
                pending,
                permissions,
                mcpRunning && ContainsAll(localCommands, BrowserCommands),
                browserRuntime,
                browserReadiness?.Summary ?? (ContainsAll(localCommands, BrowserCommands)
                    ? "Browser proxy is registered; host reachability and authentication are verified by its read-only preflight."
                    : "Browser proxy is not registered because a connection, trusted endpoint, or shared gateway token prerequisite is missing."),
                browserReadiness?.Repair ?? "Check the browser readiness guidance in Command Center, repair the endpoint or shared-token prerequisite, then retry.")),
            CapabilityTruthProjection.Project(new(
                "system-run",
                "System run",
                SystemRunCommands,
                settings?.NodeSystemRunEnabled != false,
                CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
                sessionLive,
                approval,
                effective,
                pending,
                permissions,
                mcpRunning && ContainsAll(localCommands, SystemRunCommands),
                ContainsAll(localCommands, SystemRunCommands)
                    ? CapabilityTruthProjection.RuntimeKind.Unknown
                    : CapabilityTruthProjection.RuntimeKind.Blocked,
                ContainsAll(localCommands, SystemRunCommands)
                    ? "System run is registered and remains subject to local exec approvals and containment policy; runtime readiness is verified on execution."
                    : "System run commands are not registered in the current node runtime.",
                "Enable System run, review the local Exec policy, save, and reconnect the Windows node."))
        ];
    }

    private static CapabilityTruthProjection.ApprovalKind ResolveApproval(
        bool sessionLive,
        GatewayNodeApprovalState? approval) => approval switch
            {
                GatewayNodeApprovalState.Approved => CapabilityTruthProjection.ApprovalKind.Approved,
                GatewayNodeApprovalState.PendingApproval or GatewayNodeApprovalState.PendingReapproval =>
                    CapabilityTruthProjection.ApprovalKind.Pending,
                GatewayNodeApprovalState.Unapproved => CapabilityTruthProjection.ApprovalKind.Rejected,
                _ => sessionLive
                    ? CapabilityTruthProjection.ApprovalKind.Unknown
                    : CapabilityTruthProjection.ApprovalKind.NotConnected,
            };

    private static bool ContainsAll(IReadOnlyCollection<string> available, IReadOnlyList<string> required) =>
        required.All(command => available.Contains(command, StringComparer.OrdinalIgnoreCase));
}
