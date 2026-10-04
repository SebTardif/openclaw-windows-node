using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class FixtureLoopbackListenerOwnerTests
{
    [Fact]
    public void OrdinaryProcess_DoesNotOwnTheListener()
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable(GatewayFixtureIsolation.ModeEnvironmentVariable));
        Assert.False(FixtureLoopbackListenerOwner.IsOwnedByCurrentProcessParent(1));
    }
}
