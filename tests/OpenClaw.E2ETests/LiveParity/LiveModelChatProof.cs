using System.Text.Json;
using OpenClaw.E2ETests.Setup;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Live model parity proof: stages the user-selected provider/model from a
/// <see cref="LiveModelProfile"/> into the real published WSL gateway using
/// an OpenClaw SecretRef (never a literal API key), sends one chat turn
/// with a unique marker through the native tray MCP
/// app.chat.send/app.chat.snapshot surface (the same surface
/// PublishedGatewayRoundtripProof uses), and verifies an ordered User
/// marker followed by a non-empty Assistant reply with turnActive=false.
/// Never logs prompt/reply/model/provider values; diagnostics are
/// counts/coarse state only.
/// </summary>
internal static class LiveModelChatProof
{
    public static async Task RunAsync(E2ESetupFixture fixture, LiveModelProfile profile)
    {
        var secrets = new LiveParitySecretRegistry();
        var apiKey = LiveParityCredentialResolver.ResolveRequiredSecret(profile.ApiKeyEnvVar, "live model provider API key");
        secrets.Register(apiKey);

        await GatewayReadiness.AssertReadyAsync(fixture, "before live model proof");

        var configureScript =
            $"""
            openclaw config set {ShellScriptingHelpers.SingleQuote($"models.providers.{profile.Provider}.apiKey")} --ref-provider default --ref-source env --ref-id {profile.ApiKeyEnvVar}
            openclaw config set {ShellScriptingHelpers.SingleQuote("agents.defaults.model.primary")} {ShellScriptingHelpers.SingleQuote($"{profile.Provider}/{profile.Model}")}
            """;
        var secretEnvironment = new Dictionary<string, string> { [profile.ApiKeyEnvVar] = apiKey };

        await using (await WslSecretGatewayScope.StageAsync(fixture, secrets, secretEnvironment, configureScript))
        {
            await GatewayReadiness.AssertReadyAsync(fixture, "after live model gateway configuration");

            var marker = $"OPENCLAW_LIVE_MODEL_E2E_{Guid.NewGuid():N}";
            // Instructs a short reply to bound token/cost usage; the marker
            // itself (not the whole sentence) is what WaitForShortReplyAsync
            // matches against the timeline's User entry.
            var message =
                $"This is an automated OpenClaw end-to-end connectivity check. Reply with a short, one-sentence " +
                $"acknowledgment only. Marker: {marker}";

            using var send = await CallToolWithoutContentDiagnosticsAsync(
                fixture,
                "app.chat.send",
                new { message },
                "send the live model turn");
            var sendRoot = send.RootElement;
            if (!sendRoot.TryGetProperty("sent", out var sent) || !sent.GetBoolean())
                throw new InvalidOperationException("app.chat.send did not accept the synthetic marker.");

            var threadId = sendRoot.TryGetProperty("threadId", out var threadIdElement) ? threadIdElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(threadId))
                throw new InvalidOperationException("app.chat.send did not return a threadId.");

            var turn = await WaitForShortReplyAsync(fixture, threadId!, marker, profile.ReplyTimeout);
            Console.WriteLine(
                $"[E2E] live model roundtrip passed: userIndex={turn.UserIndex}; assistantIndex={turn.AssistantIndex}; " +
                $"replyLength={turn.ReplyLength}; turnActive=false.");

            await GatewayReadiness.AssertReadyAsync(fixture, "after live model roundtrip");
        }

        await GatewayReadiness.AssertReadyAsync(fixture, "after live model gateway configuration restore");
    }

    private sealed record ChatTurnResult(int UserIndex, int AssistantIndex, int ReplyLength);

    private static Task<ChatTurnResult> WaitForShortReplyAsync(
        E2ESetupFixture fixture,
        string threadId,
        string expectedUserMarker,
        TimeSpan timeout)
    {
        return BoundedPoller.PollAsync<ChatTurnResult>(
            async () =>
            {
                using var snapshot = await CallToolWithoutContentDiagnosticsAsync(
                    fixture,
                    "app.chat.snapshot",
                    new { threadId },
                    "poll live model completion");
                var root = snapshot.RootElement;
                if (!root.TryGetProperty("selectedTimeline", out var timeline) || timeline.ValueKind != JsonValueKind.Object)
                    return null;

                var turnActive = timeline.TryGetProperty("turnActive", out var active) && active.GetBoolean();
                if (turnActive)
                    return null;
                if (!timeline.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return null;

                var userMarkerIndex = -1;
                var index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    var kind = entry.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                    var text = entry.TryGetProperty("Text", out var textValue) ? textValue.GetString() : null;

                    if (userMarkerIndex < 0 &&
                        string.Equals(kind, "User", StringComparison.OrdinalIgnoreCase) &&
                        text is not null &&
                        text.Contains(expectedUserMarker, StringComparison.Ordinal))
                    {
                        userMarkerIndex = index;
                    }
                    else if (userMarkerIndex >= 0 &&
                        string.Equals(kind, "Assistant", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(text))
                    {
                        return new ChatTurnResult(userMarkerIndex, index, text.Length);
                    }

                    index++;
                }

                return null;
            },
            timeout,
            TimeSpan.FromMilliseconds(500),
            () => "app.chat.snapshot did not contain the synthetic User marker followed by a non-empty " +
                $"Assistant reply with turnActive=false within {timeout.TotalSeconds}s.");
    }

    private static async Task<JsonDocument> CallToolWithoutContentDiagnosticsAsync(
        E2ESetupFixture fixture,
        string toolName,
        object arguments,
        string phase)
    {
        try
        {
            return await fixture.Client!.CallToolExpectSuccessAsync(toolName, arguments).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"Native tray MCP failed while attempting to {phase} ({ex.GetType().Name}). " +
                "Response details were suppressed because this is a live content-bearing lane.");
        }
    }
}
