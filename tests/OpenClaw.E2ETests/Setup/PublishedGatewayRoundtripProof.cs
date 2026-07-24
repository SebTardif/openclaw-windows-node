using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

internal static class PublishedGatewayRoundtripProof
{
    private const int GatewayCliTimeoutMs = 120_000;

    public static async Task RunAsync(E2ESetupFixture fixture)
    {
        await AssertReadyAsync(fixture, "before proof");
        await AssertGatewayHealthAndDashboardAsync(fixture);
        await InvokeDeviceInfoAsync(fixture);

        var marker = $"OPENCLAW_NATIVE_CHAT_{Guid.NewGuid():N}";
        var reply = $"OPENCLAW_NATIVE_CHAT_REPLY_{Guid.NewGuid():N}";

        await using (await WslOpenAiResponsesMock.StartAsync(fixture, reply))
        {
            await AssertReadyAsync(fixture, "after gateway mock configuration");

            using var send = await fixture.Client!.CallToolExpectSuccessAsync(
                "app.chat.send",
                new { message = marker });
            var sendRoot = send.RootElement;
            Assert.True(
                sendRoot.TryGetProperty("sent", out var sent) && sent.GetBoolean(),
                $"app.chat.send did not accept the synthetic marker: {sendRoot.GetRawText()}");
            var threadId = sendRoot.GetProperty("threadId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(threadId), "app.chat.send did not return a threadId.");

            await WaitForFinalReplyAsync(fixture, threadId!, marker, reply);
            await AssertReadyAsync(fixture, "after native chat roundtrip");
        }

        await AssertReadyAsync(fixture, "after gateway configuration restore");
    }

    private static async Task AssertGatewayHealthAndDashboardAsync(E2ESetupFixture fixture)
    {
        var gateway = fixture.ReadActiveGatewayRecord();
        Assert.False(
            string.IsNullOrWhiteSpace(gateway.SharedGatewayToken),
            "The published gateway proof requires the fixture's shared gateway token.");
        var environment = new Dictionary<string, string>
        {
            ["OPENCLAW_GATEWAY_TOKEN"] = gateway.SharedGatewayToken!
        };

        var health = await fixture.RunInWslAsync(
            "openclaw health --deep --json",
            TimeSpan.FromSeconds(60),
            environment);
        EnsureSuccess(health, "run deep published gateway health");

        var dashboard = await fixture.RunInWslAsync(
            "openclaw dashboard --no-open",
            TimeSpan.FromSeconds(30),
            environment);
        EnsureSuccess(dashboard, "resolve the published gateway dashboard");
        Assert.True(
            dashboard.Stdout.Contains("http", StringComparison.OrdinalIgnoreCase),
            $"Published gateway dashboard output did not contain a URL: {SanitizeFailureOutput(dashboard.Stdout)}");

        using var dashboardDocument = await fixture.Client!.CallToolExpectSuccessAsync("app.dashboard.url");
        var dashboardPayload = dashboardDocument.RootElement;
        var dashboardUrl = dashboardPayload.GetProperty("url").GetString();
        var credentialSource = dashboardPayload.GetProperty("credentialSource").GetString();
        Assert.True(
            dashboardPayload.GetProperty("usesSharedGatewayToken").GetBoolean(),
            $"Expected the tray dashboard URL to use the shared gateway token; source={credentialSource}");
        Assert.False(
            dashboardPayload.GetProperty("hasTokenQuery").GetBoolean(),
            $"Expected the tray dashboard URL to avoid token query strings; source={credentialSource}");
        Assert.True(
            Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var dashboardUri) &&
            dashboardUri.IsLoopback &&
            dashboardUri.Port == fixture.GatewayPort,
            "Expected app.dashboard.url to return the active loopback gateway URL.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync(dashboardUri);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected the tray dashboard HTTP request to succeed, got HTTP {(int)response.StatusCode}.");
        foreach (var authError in new[] { "incorrect token", "invalid token", "unauthorized" })
        {
            Assert.False(
                body.Contains(authError, StringComparison.OrdinalIgnoreCase),
                $"Tray dashboard HTTP response contained an authentication error marker; " +
                $"status={(int)response.StatusCode}; bodyLength={body.Length}.");
        }

        Console.WriteLine(
            $"[E2E] published gateway deep health and dashboard HTTP load passed: " +
            $"status={(int)response.StatusCode}; bodyLength={body.Length}; source={credentialSource}");
    }

    private static async Task InvokeDeviceInfoAsync(E2ESetupFixture fixture)
    {
        var gateway = fixture.ReadActiveGatewayRecord();
        Assert.False(
            string.IsNullOrWhiteSpace(gateway.SharedGatewayToken),
            "The published gateway proof requires the fixture's shared gateway token.");

        var invokeParams = JsonSerializer.Serialize(new
        {
            nodeId = fixture.ReadActiveGatewayDeviceId(),
            command = "device.info",
            @params = new { },
            timeoutMs = 90_000,
            idempotencyKey = Guid.NewGuid().ToString("N")
        });
        var result = await fixture.RunInWslAsync(
            $"openclaw gateway call node.invoke --params {ShellSingleQuote(invokeParams)} --json --timeout {GatewayCliTimeoutMs}",
            TimeSpan.FromSeconds(130),
            new Dictionary<string, string>
            {
                ["OPENCLAW_GATEWAY_TOKEN"] = gateway.SharedGatewayToken!
            });
        EnsureSuccess(result, "invoke device.info through the published gateway");

        using var response = JsonDocument.Parse(ExtractJsonObject(result.Stdout));
        if (response.RootElement.TryGetProperty("ok", out var ok))
            Assert.True(ok.GetBoolean(), $"Gateway node.invoke returned ok=false: {response.RootElement.GetRawText()}");

        var payload = ReadNodeInvokePayload(response.RootElement);
        Assert.False(
            string.IsNullOrWhiteSpace(payload.GetProperty("deviceName").GetString()),
            $"device.info returned an empty deviceName: {payload.GetRawText()}");
        Assert.False(
            string.IsNullOrWhiteSpace(payload.GetProperty("systemName").GetString()),
            $"device.info returned an empty systemName: {payload.GetRawText()}");
        Console.WriteLine(
            $"[E2E] published gateway device.info passed: systemName={payload.GetProperty("systemName").GetString()}; " +
            $"appVersion={payload.GetProperty("appVersion").GetString()}");
    }

    private static async Task WaitForFinalReplyAsync(
        E2ESetupFixture fixture,
        string threadId,
        string expectedUserMarker,
        string expectedReply)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        string lastDiagnostic = "no snapshot";

        while (DateTime.UtcNow < deadline)
        {
            using var snapshot = await fixture.Client!.CallToolExpectSuccessAsync(
                "app.chat.snapshot",
                new { threadId });
            var root = snapshot.RootElement;
            if (root.TryGetProperty("selectedTimeline", out var timeline) &&
                timeline.ValueKind == JsonValueKind.Object)
            {
                var turnActive = timeline.TryGetProperty("turnActive", out var active) && active.GetBoolean();
                var entries = timeline.TryGetProperty("entries", out var value) &&
                    value.ValueKind == JsonValueKind.Array
                        ? value
                        : default;
                var entryCount = entries.ValueKind == JsonValueKind.Array ? entries.GetArrayLength() : 0;
                lastDiagnostic = $"turnActive={turnActive}, entryCount={entryCount}";

                if (!turnActive && entries.ValueKind == JsonValueKind.Array)
                {
                    var userMarkerIndex = -1;
                    var replyIndex = -1;
                    var index = 0;
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var kind = entry.TryGetProperty("kind", out var kindValue)
                            ? kindValue.GetString()
                            : null;
                        var text = entry.TryGetProperty("Text", out var textValue)
                            ? textValue.GetString()
                            : null;

                        if (userMarkerIndex < 0 &&
                            string.Equals(kind, "User", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(text, expectedUserMarker, StringComparison.Ordinal))
                        {
                            userMarkerIndex = index;
                        }
                        else if (userMarkerIndex >= 0 &&
                            string.Equals(kind, "Assistant", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(text, expectedReply, StringComparison.Ordinal))
                        {
                            replyIndex = index;
                            break;
                        }

                        index++;
                    }

                    lastDiagnostic += $", userMarkerIndex={userMarkerIndex}, replyIndex={replyIndex}";
                    if (userMarkerIndex >= 0 && replyIndex > userMarkerIndex)
                    {
                        Console.WriteLine(
                            $"[E2E] native chat roundtrip passed: threadId={threadId}; " +
                            $"userIndex={userMarkerIndex}; assistantIndex={replyIndex}; turnActive=false");
                        return;
                    }
                }
            }

            await Task.Delay(500);
        }

        fixture.CaptureArtifacts();
        throw new TimeoutException(
            $"app.chat.snapshot did not contain the exact synthetic User marker followed by the exact " +
            $"deterministic Assistant reply with turnActive=false. Thread={threadId}; " +
            $"last={lastDiagnostic}; artifacts={fixture.ArtifactDir}");
    }

    private static async Task AssertReadyAsync(E2ESetupFixture fixture, string phase)
    {
        await fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
        await fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60));
        using var status = await fixture.Client!.CallToolExpectSuccessAsync("app.status");
        var root = status.RootElement;
        var connectionStatus = root.GetProperty("connectionStatus").GetString();
        var nodeConnected = root.TryGetProperty("nodeConnected", out var connected) && connected.GetBoolean();
        var nodePaired = root.TryGetProperty("nodePaired", out var paired) && paired.GetBoolean();
        Assert.True(
            connectionStatus is "Ready" or "Connected" && nodeConnected && nodePaired,
            $"App/node were not Ready {phase}: {root.GetRawText()}");
    }

    private static JsonElement ReadNodeInvokePayload(JsonElement root)
    {
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            return payload.Clone();

        if (root.TryGetProperty("payloadJSON", out var payloadJson) &&
            payloadJson.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(payloadJson.GetString()))
        {
            using var doc = JsonDocument.Parse(payloadJson.GetString()!);
            return doc.RootElement.Clone();
        }

        throw new InvalidDataException(
            $"Gateway node.invoke response did not include a payload object: {root.GetRawText()}");
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidDataException(
                $"Expected a JSON object in gateway output: {SanitizeFailureOutput(output)}");
        }
        return output[start..(end + 1)];
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";

    private static void EnsureSuccess(CommandResult result, string phase)
    {
        Assert.False(result.TimedOut, $"Timed out while attempting to {phase}.");
        Assert.True(
            result.ExitCode == 0,
            $"Failed to {phase}: exit={result.ExitCode}; " +
            $"stdout={SanitizeFailureOutput(result.Stdout)}; stderr={SanitizeFailureOutput(result.Stderr)}");
    }

    private static string SanitizeFailureOutput(string value)
    {
        var sanitized = E2ESetupFixture.SanitizeForLog(value.Trim());
        return sanitized.Length <= 2_000 ? sanitized : sanitized[..2_000] + " [truncated]";
    }
}
