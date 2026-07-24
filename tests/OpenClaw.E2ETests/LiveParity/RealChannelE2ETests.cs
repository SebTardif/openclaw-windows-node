using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

[Collection("E2E Real Channel Setup")]
public sealed class RealChannelE2ETests
{
    private readonly E2ESetupFixture _fixture;

    public RealChannelE2ETests(RealChannelE2ESetupFixture fixture)
    {
        _fixture = fixture.Inner;

        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    /// <summary>
    /// Loads the strict live model profile (for agent execution) and the
    /// strict real channel profile (OPENCLAW_REAL_CHANNEL_PROFILE), configures
    /// the real published WSL gateway's Discord plugin with the SUT bot's
    /// SecretRef token and a strict allowlist, then drives an inbound
    /// mention -&gt; agent -&gt; outbound reply round trip using two distinct
    /// Discord bot identities (driver, SUT). Never skips for a missing
    /// profile or credential once the gate is enabled; LiveParityProfileLoader
    /// /LiveParityCredentialResolver fail closed instead, naming only the
    /// offending environment variable.
    /// </summary>
    [RealChannelE2EFact]
    public Task RealDiscordChannel_MentionAgent_OutboundReply_Roundtrip()
    {
        var modelProfile = LiveParityProfileLoader.LoadLiveModelProfile();
        var channelProfile = LiveParityProfileLoader.LoadRealChannelProfile();
        return RealChannelDiscordProof.RunAsync(_fixture, modelProfile, channelProfile);
    }
}
