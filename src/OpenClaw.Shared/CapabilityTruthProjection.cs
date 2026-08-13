using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Pure, transport-neutral projection of the facts that determine whether an
/// agent can use a Windows-node capability. The owners of settings, Windows
/// permissions, gateway lifecycle, approval, and runtime probes remain
/// unchanged; this type only makes their combined result inspectable.
/// </summary>
public static class CapabilityTruthProjection
{
    /// <summary>
    /// The declaration gate shared with the projection. Runtime and approval
    /// facts may block usability, but they never override an explicit local
    /// Settings opt-out by advertising the command anyway.
    /// </summary>
    public static bool ShouldDeclare(bool settingsEnabled) => settingsEnabled;

    public enum WindowsPermissionKind
    {
        NotRequired,
        Unknown,
        Allowed,
        Denied,
    }

    public enum ApprovalKind
    {
        NotConnected,
        Unknown,
        Approved,
        Pending,
        Rejected,
    }

    public enum RuntimeKind
    {
        Unknown,
        Ready,
        Blocked,
    }

    public sealed record Input(
        string Id,
        string DisplayName,
        IReadOnlyList<string> Commands,
        bool SettingsEnabled,
        WindowsPermissionKind WindowsPermission,
        bool GatewaySessionLive,
        ApprovalKind Approval,
        IReadOnlyCollection<string> EffectiveCommands,
        IReadOnlyCollection<string> PendingCommands,
        IReadOnlyDictionary<string, bool> GatewayPermissions,
        bool LocalMcpExposed,
        RuntimeKind Runtime,
        string? RuntimeDetail = null,
        string? RuntimeRepair = null);

    public sealed record State(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("commands")] IReadOnlyList<string> Commands,
        [property: JsonPropertyName("settingsEnabled")] bool SettingsEnabled,
        [property: JsonPropertyName("windowsPermission")] string WindowsPermission,
        [property: JsonPropertyName("gatewayDeclaration")] string GatewayDeclaration,
        [property: JsonPropertyName("approval")] string Approval,
        [property: JsonPropertyName("gatewayPathState")] string GatewayPathState,
        [property: JsonPropertyName("gatewayRepair")] string GatewayRepair,
        [property: JsonPropertyName("localMcpExposed")] bool LocalMcpExposed,
        [property: JsonPropertyName("runtimeReadiness")] string RuntimeReadiness,
        [property: JsonPropertyName("overallState")] string OverallState,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("repair")] string Repair);

    public static State Project(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var gatewayEffective = input.GatewaySessionLive && input.Commands.All(command =>
            input.EffectiveCommands.Contains(command, StringComparer.OrdinalIgnoreCase));
        var pending = input.Commands.Any(command =>
            input.PendingCommands.Contains(command, StringComparer.OrdinalIgnoreCase));
        var gatewayPermissionBlocked = input.Commands.Any(command =>
            CommandCenterDiagnostics.TryGetCommandPermission(input.GatewayPermissions, command, out var allowed) && !allowed);

        var declaration = gatewayEffective
            ? "effective"
            : pending
                ? "pending-approval"
                : !input.GatewaySessionLive
                    ? "not-connected"
                    : "not-declared";

        if (!input.SettingsEnabled)
        {
            return Build(input, declaration, "disabled",
                $"{input.DisplayName} is disabled in Windows Hub Settings.",
                $"Enable {input.DisplayName} on the Permissions page, save, and reconnect the Windows node.");
        }

        if (input.WindowsPermission == WindowsPermissionKind.Denied)
        {
            return Build(input, declaration, "blocked",
                $"{input.DisplayName} is enabled, but Windows permission is denied.",
                input.RuntimeRepair ?? "Open Windows privacy settings, grant the required desktop-app permission, and retry.");
        }

        if (input.Runtime == RuntimeKind.Blocked)
        {
            return Build(input, declaration, "blocked",
                input.RuntimeDetail ?? $"{input.DisplayName} is not runtime-ready.",
                input.RuntimeRepair ?? "Repair the runtime prerequisite and retry.");
        }

        if (input.LocalMcpExposed)
        {
            var runtime = input.Runtime == RuntimeKind.Unknown ? "unchecked" : "ready";
            var summary = input.RuntimeDetail ??
                $"{input.DisplayName} is available through local MCP independently of the Gateway declaration.";
            var repair = input.Runtime == RuntimeKind.Unknown
                ? input.RuntimeRepair ?? "Invoke a read-only or safe command to verify the Windows runtime prerequisite."
                : "No repair is required.";
            return Build(input, declaration, "ready", summary, repair, runtime);
        }

        if (!gatewayEffective && (pending || input.Approval == ApprovalKind.Rejected))
        {
            return Build(input, declaration, "pending-approval",
                $"{input.DisplayName} is waiting for gateway node command approval.",
                "Approve the pending Windows node declaration on the Gateway, then reconnect the node.");
        }

        if (gatewayPermissionBlocked)
        {
            return Build(input, declaration, "blocked",
                $"{input.DisplayName} is declared, but the Gateway policy blocks one or more commands.",
                "Review gateway.nodes allow/deny policy for the listed commands, approve the change, and reconnect the node.");
        }

        if (gatewayEffective)
        {
            var runtime = input.Runtime == RuntimeKind.Unknown ? "unchecked" : "ready";
            var summary = input.RuntimeDetail ??
                $"{input.DisplayName} is effective for the connected Gateway node.";
            var repair = input.Runtime == RuntimeKind.Unknown
                ? input.RuntimeRepair ?? "Invoke a read-only or safe command to verify the Windows runtime prerequisite."
                : "No repair is required.";
            return Build(input, declaration, "ready", summary, repair, runtime);
        }

        if (input.GatewaySessionLive)
        {
            return Build(input, declaration, "blocked",
                $"{input.DisplayName} is enabled locally but is not in the Gateway's effective Windows node declaration.",
                "Reconnect the Windows node. If the declaration changed, approve the pending command set on the Gateway.");
        }

        return Build(input, declaration, "unavailable",
            $"{input.DisplayName} is enabled, but neither local MCP nor a Gateway declaration currently exposes it.",
            "Enable Local MCP Server or connect and approve the Windows node, then retry.");
    }

    private static State Build(
        Input input,
        string declaration,
        string overall,
        string summary,
        string repair,
        string? runtime = null)
    {
        var gatewayEffective = input.GatewaySessionLive && input.Commands.All(command =>
            input.EffectiveCommands.Contains(command, StringComparer.OrdinalIgnoreCase));
        var pending = input.Commands.Any(command =>
            input.PendingCommands.Contains(command, StringComparer.OrdinalIgnoreCase));
        var gatewayPermissionBlocked = input.Commands.Any(command =>
            CommandCenterDiagnostics.TryGetCommandPermission(input.GatewayPermissions, command, out var allowed) && !allowed);
        var gatewayPathState = !input.GatewaySessionLive
            ? pending ? "pending-approval" : "not-connected"
            : gatewayPermissionBlocked
                ? "blocked"
                : gatewayEffective
                    ? "ready"
                    : pending
                        ? "pending-approval"
                        : "not-declared";
        var gatewayRepair = gatewayPathState switch
        {
            "ready" when pending => "The current declaration remains effective. Approve the pending Windows node changes, then reconnect to activate them.",
            "ready" => "No Gateway repair is required.",
            "blocked" => "Review gateway.nodes allow/deny policy for the listed commands, approve the change, and reconnect the node.",
            "pending-approval" => "Approve the pending Windows node declaration on the Gateway, then reconnect the node.",
            "not-declared" => "Reconnect the Windows node. If the declaration changed, approve the pending command set on the Gateway.",
            _ => "Connect the Windows node to inspect its Gateway declaration."
        };

        return new(
            input.Id,
            input.DisplayName,
            input.Commands,
            input.SettingsEnabled,
            ToKebabCase(input.WindowsPermission.ToString()),
            declaration,
            ToKebabCase(input.Approval.ToString()),
            gatewayPathState,
            gatewayRepair,
            input.LocalMcpExposed,
            runtime ?? ToKebabCase(input.Runtime.ToString()),
            overall,
            summary,
            repair);
    }

    private static string ToKebabCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
}
