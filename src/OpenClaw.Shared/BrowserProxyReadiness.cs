using System.Net;

namespace OpenClaw.Shared;

/// <summary>
/// Pure classification contract for the browser-control prerequisite. Callers
/// collect endpoint and HTTP facts; this type turns them into one stable state
/// and repair path without handling or exposing credential values.
/// </summary>
public static class BrowserProxyReadiness
{
    public enum Kind
    {
        Ready,
        EndpointInvalid,
        SshForwardMissing,
        HostAbsent,
        HostUnreachable,
        AuthenticationRequired,
        AuthenticationRejected,
    }

    public readonly record struct Result(
        Kind State,
        string Summary,
        string Repair,
        BrowserControlEndpoint.Provenance? EndpointProvenance = null)
    {
        public bool IsReady => State == Kind.Ready;

        public string ToError() =>
            $"Browser control preflight blocked: {Summary} {Repair} " +
            "The Gateway connection is separate and does not need to be restarted.";
    }

    public static Result EndpointFailure(
        string endpointError,
        bool sshConfigured,
        bool explicitEndpointConfigured,
        bool sshLocalEndpointConfigured) =>
        sshConfigured && !explicitEndpointConfigured && !sshLocalEndpointConfigured
            ? new Result(
                Kind.SshForwardMissing,
                endpointError,
                "Enable the tray-managed SSH browser-proxy forward, or configure a trusted explicit local browser-control port.")
            : new Result(
                Kind.EndpointInvalid,
                endpointError,
                "Configure a trusted local browser-control endpoint, or disable Browser proxy bridge in Settings.");

    public static Result HostFailure(
        int localPort,
        BrowserControlEndpoint.Provenance provenance,
        bool connectionRefused,
        int? sshRemoteGatewayPort)
    {
        var state = connectionRefused ? Kind.HostAbsent : Kind.HostUnreachable;
        var summary = connectionRefused
            ? $"no browser-control host is listening at 127.0.0.1:{localPort}."
            : $"the browser-control host at 127.0.0.1:{localPort} did not respond.";
        var repair = provenance == BrowserControlEndpoint.Provenance.ManagedSshForward ||
                     sshRemoteGatewayPort is not null
            ? BuildSshRepair(localPort, sshRemoteGatewayPort)
            : $"Start or install the OpenClaw browser-control host on local port {localPort}, then retry. " +
              $"If the Gateway is reached through SSH, also forward the browser-control port with: " +
              $"ssh -N -L {localPort}:127.0.0.1:<remote-gateway-port+2> <user>@<host>. " +
              "If browser control is not intended, disable Browser proxy bridge in Settings.";
        return new Result(state, summary, repair, provenance);
    }

    public static Result FromHttpStatus(
        HttpStatusCode statusCode,
        bool hasSharedToken,
        BrowserControlEndpoint.Provenance provenance)
    {
        if (statusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return new Result(
                Kind.Ready,
                "the browser-control host is reachable and accepted authentication.",
                "No repair is required.",
                provenance);
        }

        return hasSharedToken
            ? new Result(
                Kind.AuthenticationRejected,
                "the browser-control host rejected the saved shared gateway token.",
                "Update the Gateway Token in Settings so it matches browser-control auth, or disable Browser proxy bridge.",
                provenance)
            : new Result(
                Kind.AuthenticationRequired,
                "the browser-control host requires authentication, but this gateway has no saved shared token.",
                "Enter the matching shared Gateway Token in Settings, reconnect node mode, and retry. QR/bootstrap tokens are not the shared token.",
                provenance);
    }

    private static string BuildSshRepair(int localPort, int? sshRemoteGatewayPort)
    {
        var remotePort = sshRemoteGatewayPort is >= 1 and <= 65533
            ? (sshRemoteGatewayPort.Value + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "<remote-gateway-port+2>";
        return $"Verify the SSH companion forward maps local port {localPort} " +
               $"to remote browser-control port {remotePort}: " +
               $"ssh -N -L {localPort}:127.0.0.1:{remotePort} <user>@<host>. " +
               "Start the remote browser-control host, then retry. " +
               "If browser control is not intended, disable Browser proxy bridge in Settings.";
    }
}
