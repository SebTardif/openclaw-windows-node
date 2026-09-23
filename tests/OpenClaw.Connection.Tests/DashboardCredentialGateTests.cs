namespace OpenClaw.Connection.Tests;

public sealed class DashboardCredentialGateTests
{
    [Fact]
    public void SharedToken_IsAppendedWhenItIsThePinnedCredential()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceSharedGatewayToken,
            isBootstrapToken: false,
            resolvedToken: "shared-secret",
            pinnedSharedToken: "shared-secret");

        Assert.False(decision.PinMismatch);
        Assert.True(decision.AppendToken);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, decision.CredentialSource);
        Assert.Equal("shared-secret", decision.Token);
    }

    [Fact]
    public void DeviceToken_WinsOverStoredSharedTokenForTheSamePin()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceDeviceToken,
            isBootstrapToken: false,
            resolvedToken: "device-token",
            pinnedSharedToken: "shared-secret");

        Assert.False(decision.PinMismatch);
        Assert.False(decision.AppendToken);
        Assert.Equal(CredentialResolver.SourceDeviceToken, decision.CredentialSource);
        Assert.Null(decision.Token);
    }

    [Fact]
    public void BootstrapToken_IsNotAppended()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: true,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceBootstrapToken,
            isBootstrapToken: true,
            resolvedToken: "bootstrap-token",
            pinnedSharedToken: "shared-secret");

        Assert.False(decision.AppendToken);
        Assert.Equal(CredentialResolver.SourceBootstrapToken, decision.CredentialSource);
        Assert.Null(decision.Token);
    }

    [Fact]
    public void PinMismatch_ReturnsNoTokenUrlMaterial()
    {
        var decision = DashboardCredentialGate.Decide(
            pinMatches: false,
            samePinnedRecord: true,
            tunnelAllowsSharedToken: true,
            CredentialResolver.SourceSharedGatewayToken,
            isBootstrapToken: false,
            resolvedToken: "shared-secret",
            pinnedSharedToken: "other-secret");

        Assert.True(decision.PinMismatch);
        Assert.False(decision.AppendToken);
        Assert.Null(decision.Token);
        Assert.Equal("none", decision.CredentialSource);
    }
}
