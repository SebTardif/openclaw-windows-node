using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class CapabilityTruthProjectionTests
{
    [Fact]
    public void Project_CameraEnabledButPermissionDenied_IsBlockedWithRepair()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.Denied,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Approved,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("blocked", state.OverallState);
        Assert.Equal("denied", state.WindowsPermission);
        Assert.Contains("privacy", state.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_PendingGatewayDeclaration_IsNotReportedReady()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.Unknown,
            effective: [],
            pending: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Pending,
            runtime: CapabilityTruthProjection.RuntimeKind.Unknown));

        Assert.Equal("pending-approval", state.OverallState);
        Assert.Equal("pending-approval", state.GatewayDeclaration);
        Assert.Contains("Approve", state.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_PendingDeclaration_RemainsVisibleBeforeSessionConnects()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.Unknown,
            sessionLive: false,
            effective: [],
            pending: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Pending,
            runtime: CapabilityTruthProjection.RuntimeKind.Unknown));

        Assert.Equal("pending-approval", state.GatewayDeclaration);
        Assert.Equal("pending-approval", state.Approval);
        Assert.Equal("pending-approval", state.OverallState);
    }

    [Fact]
    public void Project_LocalMcpOnly_IsReadyButGatewayTruthRemainsNotConnected()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            sessionLive: false,
            effective: [],
            approval: CapabilityTruthProjection.ApprovalKind.NotConnected,
            localMcp: true,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("ready", state.OverallState);
        Assert.Equal("not-connected", state.GatewayDeclaration);
        Assert.True(state.LocalMcpExposed);
        Assert.Contains("local MCP", state.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_LocalMcpReady_WinsOverMissingGatewayDeclaration()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            sessionLive: true,
            effective: [],
            approval: CapabilityTruthProjection.ApprovalKind.Approved,
            localMcp: true,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("ready", state.OverallState);
        Assert.Equal("not-declared", state.GatewayDeclaration);
        Assert.True(state.LocalMcpExposed);
    }

    [Fact]
    public void Project_LocalMcpReady_WinsOverPendingGatewayApproval()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            sessionLive: true,
            effective: [],
            pending: ["camera.list"],
            approval: CapabilityTruthProjection.ApprovalKind.Pending,
            localMcp: true,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("ready", state.OverallState);
        Assert.Equal("pending-approval", state.GatewayDeclaration);
        Assert.Equal("pending", state.Approval);
        Assert.Equal("pending-approval", state.GatewayPathState);
    }

    [Fact]
    public void Project_DisconnectedRetainedGatewayCommands_AreNotReady()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            sessionLive: false,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.NotConnected,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("unavailable", state.OverallState);
        Assert.Equal("not-connected", state.GatewayDeclaration);
    }

    [Fact]
    public void Project_GatewayPolicyBlock_WinsOverRegisteredRuntime()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Approved,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready,
            permissions: new Dictionary<string, bool> { ["camera.snap"] = false }));

        Assert.Equal("blocked", state.OverallState);
        Assert.Contains("Gateway policy", state.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_LocalMcpReady_PreservesGatewayPolicyBlockDetails()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Approved,
            localMcp: true,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready,
            permissions: new Dictionary<string, bool> { ["camera.snap"] = false }));

        Assert.Equal("ready", state.OverallState);
        Assert.Equal("blocked", state.GatewayPathState);
        Assert.Contains("allow/deny", state.GatewayRepair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_UnrelatedPendingReapproval_DoesNotBlockEffectiveCapability()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Pending,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("ready", state.OverallState);
        Assert.Equal("ready", state.GatewayPathState);
    }

    [Fact]
    public void Project_EffectiveCommandsRemainReadyDuringMatchingPendingReapproval()
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            pending: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Pending,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready));

        Assert.Equal("ready", state.OverallState);
        Assert.Equal("effective", state.GatewayDeclaration);
        Assert.Equal("pending", state.Approval);
        Assert.Equal("ready", state.GatewayPathState);
        Assert.Contains("pending", state.GatewayRepair, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("commands.camera.snap")]
    [InlineData("command:camera.snap")]
    public void Project_PrefixedGatewayPolicyBlock_IsDetected(string permissionKey)
    {
        var state = CapabilityTruthProjection.Project(Input(
            settingsEnabled: true,
            permission: CapabilityTruthProjection.WindowsPermissionKind.NotRequired,
            effective: ["camera.list", "camera.snap", "camera.clip"],
            approval: CapabilityTruthProjection.ApprovalKind.Approved,
            runtime: CapabilityTruthProjection.RuntimeKind.Ready,
            permissions: new Dictionary<string, bool> { [permissionKey] = false }));

        Assert.Equal("blocked", state.OverallState);
        Assert.Equal("blocked", state.GatewayPathState);
    }

    private static CapabilityTruthProjection.Input Input(
        bool settingsEnabled,
        CapabilityTruthProjection.WindowsPermissionKind permission,
        IReadOnlyCollection<string> effective,
        CapabilityTruthProjection.ApprovalKind approval,
        CapabilityTruthProjection.RuntimeKind runtime,
        IReadOnlyCollection<string>? pending = null,
        bool sessionLive = true,
        bool localMcp = false,
        IReadOnlyDictionary<string, bool>? permissions = null) => new(
            "camera",
            "Camera",
            ["camera.list", "camera.snap", "camera.clip"],
            settingsEnabled,
            permission,
            sessionLive,
            approval,
            effective,
            pending ?? [],
            permissions ?? new Dictionary<string, bool>(),
            localMcp,
            runtime,
            RuntimeRepair: "Open Windows privacy settings and retry.");
}
