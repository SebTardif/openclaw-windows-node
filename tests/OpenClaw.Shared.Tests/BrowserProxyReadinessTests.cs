using System.Net;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class BrowserProxyReadinessTests
{
    [Fact]
    public void EndpointFailure_ClassifiesMissingSshForward()
    {
        var result = BrowserProxyReadiness.EndpointFailure(
            "Browser proxy requires an explicit browser-control port or a managed SSH browser-proxy forward.",
            sshConfigured: true,
            explicitEndpointConfigured: false,
            sshLocalEndpointConfigured: false);

        Assert.Equal(BrowserProxyReadiness.Kind.SshForwardMissing, result.State);
        Assert.Contains("SSH", result.Repair, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not need to be restarted", result.ToError(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EndpointFailure_PreservesInvalidExplicitEndpointUnderSsh()
    {
        var result = BrowserProxyReadiness.EndpointFailure(
            "Configured browser-control port is outside the valid TCP port range.",
            sshConfigured: true,
            explicitEndpointConfigured: true,
            sshLocalEndpointConfigured: true);

        Assert.Equal(BrowserProxyReadiness.Kind.EndpointInvalid, result.State);
        Assert.Contains("trusted local browser-control endpoint", result.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostFailure_PreservesManagedSshEndpointProvenance()
    {
        var result = BrowserProxyReadiness.HostFailure(
            9102,
            BrowserControlEndpoint.Provenance.ManagedSshForward,
            connectionRefused: true,
            sshRemoteGatewayPort: 18789);

        Assert.Equal(BrowserProxyReadiness.Kind.HostAbsent, result.State);
        Assert.Equal(BrowserControlEndpoint.Provenance.ManagedSshForward, result.EndpointProvenance);
        Assert.Contains("managed SSH browser-control forward", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remote browser-control port 18791", result.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostFailure_DistinguishesUnreachableFromAbsent()
    {
        var result = BrowserProxyReadiness.HostFailure(
            18791,
            BrowserControlEndpoint.Provenance.GatewayPortFallback,
            connectionRefused: false,
            sshRemoteGatewayPort: null);

        Assert.Equal(BrowserProxyReadiness.Kind.HostUnreachable, result.State);
        Assert.Contains("gateway port + 2", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not respond", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disable Browser proxy bridge", result.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HttpStatus_DistinguishesMissingAndRejectedAuthentication()
    {
        var missing = BrowserProxyReadiness.FromHttpStatus(
            HttpStatusCode.Unauthorized,
            hasSharedToken: false,
            BrowserControlEndpoint.Provenance.GatewayPortFallback);
        var rejected = BrowserProxyReadiness.FromHttpStatus(
            HttpStatusCode.Unauthorized,
            hasSharedToken: true,
            BrowserControlEndpoint.Provenance.ExplicitOverride);

        Assert.Equal(BrowserProxyReadiness.Kind.AuthenticationRequired, missing.State);
        Assert.Equal(BrowserProxyReadiness.Kind.AuthenticationRejected, rejected.State);
        Assert.Contains("QR/bootstrap", missing.Repair, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("matches browser-control auth", rejected.Repair, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void HttpStatus_AuthDenialsNeverBecomeReady(HttpStatusCode statusCode)
    {
        var result = BrowserProxyReadiness.FromHttpStatus(
            statusCode,
            hasSharedToken: true,
            BrowserControlEndpoint.Provenance.ManagedSshForward);

        Assert.Equal(BrowserProxyReadiness.Kind.AuthenticationRejected, result.State);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void NonUnauthorizedHttpStatus_ProvesReadyEvenWhenHostReturnsAnHttpError()
    {
        var result = BrowserProxyReadiness.FromHttpStatus(
            HttpStatusCode.NotFound,
            hasSharedToken: true,
            BrowserControlEndpoint.Provenance.ExplicitOverride);

        Assert.True(result.IsReady);
        Assert.Equal(BrowserControlEndpoint.Provenance.ExplicitOverride, result.EndpointProvenance);
    }
}
