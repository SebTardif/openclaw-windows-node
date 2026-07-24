using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Real Discord channel parity proof: configures the real gateway's
/// Discord plugin with the SUT bot's SecretRef token and a strict
/// allowlist scoped to the driver bot, mention requirement, and selected
/// guild/channel, then drives an inbound mention -&gt; agent -&gt; outbound
/// reply round trip using two distinct bot identities. The driver token
/// is used only from this Windows test process (via
/// <see cref="DiscordRestClient"/>) and is never staged into WSL/the
/// gateway; only the SUT token (and the live model profile's provider API
/// key, needed for the agent to actually answer) are staged.
/// </summary>
internal static class RealChannelDiscordProof
{
    public static async Task RunAsync(E2ESetupFixture fixture, LiveModelProfile modelProfile, RealChannelProfile channelProfile)
    {
        var secrets = new LiveParitySecretRegistry();
        var modelApiKey = LiveParityCredentialResolver.ResolveRequiredSecret(modelProfile.ApiKeyEnvVar, "live model provider API key");
        var driverToken = LiveParityCredentialResolver.ResolveRequiredSecret(channelProfile.Driver.TokenEnvVar, "Discord driver bot token");
        var sutToken = LiveParityCredentialResolver.ResolveRequiredSecret(channelProfile.Sut.TokenEnvVar, "Discord SUT bot token");
        secrets.Register(modelApiKey);
        secrets.Register(driverToken);
        secrets.Register(sutToken);

        await GatewayReadiness.AssertReadyAsync(fixture, "before real channel proof");

        var discordConfigJson = DiscordGatewayConfigJsonBuilder.Build(channelProfile);
        var configureScript =
            $"""
            openclaw config set {ShellScriptingHelpers.SingleQuote("channels.discord.token")} --ref-provider default --ref-source env --ref-id {channelProfile.Sut.TokenEnvVar}
            openclaw config set {ShellScriptingHelpers.SingleQuote("channels.discord")} {ShellScriptingHelpers.SingleQuote(discordConfigJson)} --strict-json --merge
            openclaw config set {ShellScriptingHelpers.SingleQuote($"models.providers.{modelProfile.Provider}.apiKey")} --ref-provider default --ref-source env --ref-id {modelProfile.ApiKeyEnvVar}
            openclaw config set {ShellScriptingHelpers.SingleQuote("agents.defaults.model.primary")} {ShellScriptingHelpers.SingleQuote($"{modelProfile.Provider}/{modelProfile.Model}")}
            """;

        // Only the SUT token and the model provider key are staged into the
        // WSL gateway service. The driver token is deliberately excluded:
        // it stays in this Windows test process for use by DiscordRestClient
        // below and must never reach WSL/the gateway.
        var secretEnvironment = new Dictionary<string, string>
        {
            [channelProfile.Sut.TokenEnvVar] = sutToken,
            [modelProfile.ApiKeyEnvVar] = modelApiKey,
        };

        await using (await WslSecretGatewayScope.StageAsync(fixture, secrets, secretEnvironment, configureScript))
        {
            await GatewayReadiness.AssertReadyAsync(fixture, "after real channel gateway configuration");

            var nonce = Guid.NewGuid().ToString("N");
            var mentionMessage = $"<@{channelProfile.Sut.UserId}> reply with only this exact marker: {nonce}";

            using var driverClient = new DiscordRestClient(driverToken);
            using var sutClient = new DiscordRestClient(sutToken);

            string? driverMessageId = null;
            string? sutReplyMessageId = null;
            try
            {
                driverMessageId = await driverClient.PostMessageAsync(channelProfile.ChannelId, mentionMessage);

                var reply = await BoundedPoller.PollAsync<DiscordMessage>(
                    async () =>
                    {
                        var messages = await driverClient.ListMessagesAfterAsync(channelProfile.ChannelId, driverMessageId);
                        return messages.FirstOrDefault(m =>
                            string.Equals(m.AuthorId, channelProfile.Sut.UserId, StringComparison.Ordinal) &&
                            m.Content.Contains(nonce, StringComparison.Ordinal));
                    },
                    channelProfile.PollTimeout,
                    TimeSpan.FromSeconds(2),
                    () => "No message authored by the SUT bot and containing the nonce was observed in the " +
                        $"channel within {channelProfile.PollTimeout.TotalSeconds}s.");

                sutReplyMessageId = reply.Id;

                Console.WriteLine(
                    "[E2E] real channel roundtrip passed: driverPosted=true; sutReplyObserved=true; " +
                    "pollBounded=true.");
            }
            finally
            {
                // Best-effort only: cleanup failures here must never mask
                // the round-trip assertion's own pass/fail outcome.
                if (driverMessageId is not null)
                    await driverClient.TryDeleteMessageAsync(channelProfile.ChannelId, driverMessageId);
                if (sutReplyMessageId is not null)
                    await sutClient.TryDeleteMessageAsync(channelProfile.ChannelId, sutReplyMessageId);
            }

            await GatewayReadiness.AssertReadyAsync(fixture, "after real channel roundtrip");
        }

        await GatewayReadiness.AssertReadyAsync(fixture, "after real channel gateway configuration restore");
    }
}
