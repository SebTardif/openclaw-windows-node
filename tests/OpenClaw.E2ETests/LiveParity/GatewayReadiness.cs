using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Shared "app/node are Ready" assertion reused by both live-parity proofs
/// (live model, real channel), each of which needs the same check before
/// staging, after staging, and after restoring gateway configuration.
/// Mirrors <c>PublishedGatewayRoundtripProof.AssertReadyAsync</c>.
/// </summary>
internal static class GatewayReadiness
{
    public static async Task AssertReadyAsync(E2ESetupFixture fixture, string phase)
    {
        await fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        await fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        using var status = await fixture.Client!.CallToolExpectSuccessAsync("app.status").ConfigureAwait(false);
        var root = status.RootElement;
        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        var nodeConnected = root.TryGetProperty("nodeConnected", out var connected) && connected.GetBoolean();
        var nodePaired = root.TryGetProperty("nodePaired", out var paired) && paired.GetBoolean();

        if (connectionStatus is not ("Ready" or "Connected") || !nodeConnected || !nodePaired)
        {
            fixture.CaptureArtifacts();
            throw new InvalidOperationException(
                $"App/node were not Ready {phase}: status={connectionStatus}, nodeConnected={nodeConnected}, " +
                $"nodePaired={nodePaired}.");
        }
    }
}
