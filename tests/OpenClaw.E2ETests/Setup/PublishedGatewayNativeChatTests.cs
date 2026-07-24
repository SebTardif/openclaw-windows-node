namespace OpenClaw.E2ETests.Setup;

[Collection("E2E Setup")]
public sealed class PublishedGatewayNativeChatTests
{
    private readonly E2ESetupFixture _fixture;

    public PublishedGatewayNativeChatTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
        if (_fixture.Client is null)
            throw new InvalidOperationException("E2E fixture MCP client not initialized");
    }

    [E2EFact]
    public Task RealPublishedGateway_DeviceInfo_AndNativeChat_Roundtrip() =>
        PublishedGatewayRoundtripProof.RunAsync(_fixture);
}
