using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

[Collection("E2E Live Model Setup")]
public sealed class LiveModelE2ETests
{
    private readonly E2ESetupFixture _fixture;

    public LiveModelE2ETests(LiveModelE2ESetupFixture fixture)
    {
        _fixture = fixture.Inner;

        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    /// <summary>
    /// Loads the strict live model profile (OPENCLAW_LIVE_MODEL_PROFILE),
    /// stages the selected provider/model into the real published WSL
    /// gateway via a SecretRef, and verifies one bounded chat turn through
    /// the native tray MCP app.chat.send/app.chat.snapshot surface. Never
    /// skips for a missing profile or credential once the gate is enabled;
    /// LiveParityProfileLoader/LiveParityCredentialResolver fail closed
    /// instead, naming only the offending environment variable.
    /// </summary>
    [LiveModelE2EFact]
    public Task RealLiveModel_ConfiguredProvider_ChatTurn_Roundtrip()
    {
        var profile = LiveParityProfileLoader.LoadLiveModelProfile();
        return LiveModelChatProof.RunAsync(_fixture, profile);
    }
}
