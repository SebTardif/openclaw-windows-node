using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class UpdateStatusTests
{
    [Fact]
    public void Parse_ExtendedStable_SuppressesCompanionUpdate()
    {
        using var document = JsonDocument.Parse("""{ "effectiveChannel": "extended-stable" }""");

        var status = GatewayUpdateStatusParser.Parse(document.RootElement);

        Assert.Equal("extended-stable", status.EffectiveChannel);
        Assert.True(status.SuppressesCompanionUpdate);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("beta")]
    [InlineData(null)]
    public void Parse_OtherOrMissingChannel_PreservesCompanionUpdate(string? channel)
    {
        using var document = JsonDocument.Parse(channel is null
            ? "{}"
            : $$"""{ "effectiveChannel": "{{channel}}" }""");

        var status = GatewayUpdateStatusParser.Parse(document.RootElement);

        Assert.False(status.SuppressesCompanionUpdate);
    }
}
