using System.Text;
using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

internal sealed class WslOpenAiResponsesMock : IAsyncDisposable
{
    private const int Port = 44080;
    private readonly E2ESetupFixture _fixture;
    private readonly string _runId;
    private readonly string _serverPath;
    private readonly string _unitName;
    private readonly string _configPath;
    private readonly string _backupPath;
    private bool _configured;
    private bool _started;

    private WslOpenAiResponsesMock(E2ESetupFixture fixture, string runId)
    {
        _fixture = fixture;
        _runId = runId;
        _serverPath = $"/tmp/openclaw-e2e-responses-{runId}.js";
        _unitName = $"openclaw-e2e-responses-{runId}.service";
        _configPath = "/home/openclaw/.openclaw/openclaw.json";
        _backupPath = $"/home/openclaw/.openclaw/openclaw.json.e2e-{runId}.backup";
    }

    public static async Task<WslOpenAiResponsesMock> StartAsync(
        E2ESetupFixture fixture,
        string deterministicReply)
    {
        var instance = new WslOpenAiResponsesMock(fixture, Guid.NewGuid().ToString("N"));
        try
        {
            await instance.StartServerAsync(deterministicReply);
            await instance.ConfigureGatewayAsync();
            return instance;
        }
        catch (Exception startError)
        {
            try
            {
                await instance.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Published gateway mock startup and cleanup both failed.",
                    startError,
                    cleanupError);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exception? cleanupError = null;

        if (_configured)
        {
            try
            {
                var restore = await _fixture.RunInWslAsync(
                    $"""
                    set -eu
                    test -f {ShellSingleQuote(_backupPath)}
                    cp -p {ShellSingleQuote(_backupPath)} {ShellSingleQuote(_configPath)}
                    rm -f {ShellSingleQuote(_backupPath)}
                    openclaw gateway restart >/dev/null 2>&1 || systemctl --user restart openclaw-gateway.service
                    """,
                    TimeSpan.FromSeconds(120),
                    inputViaStdin: true);
                EnsureSuccess(restore, "restore published gateway configuration");
                await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
                await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60));
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }
        }

        await CaptureLogsAsync();

        if (_started)
        {
            try
            {
                var stop = await _fixture.RunInWslAsync(
                    $"""
                    set -eu
                    unit={ShellSingleQuote(_unitName)}
                    if systemctl --user is-active --quiet "$unit"; then
                      systemctl --user stop "$unit"
                    fi
                    if systemctl --user is-active --quiet "$unit"; then
                      echo "mock server unit $unit did not stop" >&2
                      exit 1
                    fi
                    systemctl --user reset-failed "$unit" >/dev/null 2>&1 || true
                    rm -f {ShellSingleQuote(_serverPath)}
                    """,
                    TimeSpan.FromSeconds(20),
                    inputViaStdin: true);
                EnsureSuccess(stop, "stop exact WSL Responses mock process");
            }
            catch (Exception ex)
            {
                cleanupError = cleanupError is null
                    ? ex
                    : new AggregateException(cleanupError, ex);
            }
        }

        _fixture.CaptureArtifacts();
        if (cleanupError is not null)
            throw cleanupError;
    }

    private async Task StartServerAsync(string deterministicReply)
    {
        var script = BuildServerScript(deterministicReply);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var start = await _fixture.RunInWslAsync(
            $"""
            set -eu
            printf '%s' {ShellSingleQuote(encoded)} | base64 -d > {ShellSingleQuote(_serverPath)}
            chmod 600 {ShellSingleQuote(_serverPath)}
            node_path="$(find /home/openclaw/.openclaw/tools -path '*/bin/node' -type f | sort -V | tail -n 1)"
            test -n "$node_path"
            "$node_path" --check {ShellSingleQuote(_serverPath)}
            systemd-run --user --unit={ShellSingleQuote(_unitName)} --collect "$node_path" {ShellSingleQuote(_serverPath)}
            """,
            TimeSpan.FromSeconds(10),
            inputViaStdin: true);
        EnsureSuccess(start, "launch WSL Responses mock");
        _started = true;

        var ready = await _fixture.RunInWslAsync(
            $"""
            set -eu
            unit={ShellSingleQuote(_unitName)}
            for attempt in 1 2 3 4 5 6 7 8 9 10; do
              if curl --silent --fail http://127.0.0.1:{Port}/healthz >/dev/null; then
                exit 0
              fi
              if ! systemctl --user is-active --quiet "$unit"; then
                journalctl --user -u "$unit" --no-pager -n 100 >&2 || true
                exit 1
              fi
              sleep 1
            done
            echo "mock server did not become ready" >&2
            exit 1
            """,
            TimeSpan.FromSeconds(20),
            inputViaStdin: true);
        EnsureSuccess(ready, "wait for WSL Responses mock readiness");
    }

    private async Task ConfigureGatewayAsync()
    {
        var provider = JsonSerializer.Serialize(new
        {
            baseUrl = $"http://127.0.0.1:{Port}/v1",
            apiKey = "test",
            api = "openai-responses",
            request = new { allowPrivateNetwork = true },
            models = new[]
            {
                new
                {
                    id = "gpt-5.6-luna",
                    name = "OpenClaw E2E deterministic model",
                    api = "openai-responses",
                    reasoning = false,
                    input = new[] { "text" },
                    contextWindow = 128_000,
                    maxTokens = 4_096
                }
            }
        });

        var backup = await _fixture.RunInWslAsync(
            $"""
            set -eu
            config={ShellSingleQuote(_configPath)}
            backup={ShellSingleQuote(_backupPath)}
            test -f "$config"
            test ! -e "$backup"
            cp -p "$config" "$backup"
            """,
            TimeSpan.FromSeconds(15),
            inputViaStdin: true);
        EnsureSuccess(backup, "back up published gateway configuration");
        _configured = true;

        var configure = await _fixture.RunInWslAsync(
            $"""
            set -eu
            openclaw config set models.providers.openclaw-e2e {ShellSingleQuote(provider)} --strict-json --merge
            openclaw config set agents.defaults.model.primary openclaw-e2e/gpt-5.6-luna
            openclaw config set agents.defaults.model.fallbacks '[]' --strict-json
            openclaw config set agents.defaults.skipBootstrap true
            openclaw config set agents.defaults.contextInjection never
            openclaw gateway restart >/dev/null 2>&1 || systemctl --user restart openclaw-gateway.service
            """,
            TimeSpan.FromSeconds(150),
            inputViaStdin: true);
        EnsureSuccess(configure, "configure published gateway for deterministic Responses mock");

        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120));
        await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60));
        await CaptureGatewayStatusAsync();
    }

    private async Task CaptureLogsAsync()
    {
        if (!_started)
            return;

        try
        {
            var result = await _fixture.RunInWslAsync(
                $"journalctl --user -u {ShellSingleQuote(_unitName)} --no-pager -n 200 || true",
                TimeSpan.FromSeconds(10));
            File.WriteAllText(
                Path.Combine(_fixture.ArtifactDir, "published-gateway-mock.log"),
                result.Stdout);
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(_fixture.ArtifactDir, "published-gateway-mock-capture-error.log"),
                ex.Message);
        }
    }

    private async Task CaptureGatewayStatusAsync()
    {
        var result = await _fixture.RunInWslAsync(
            "systemctl --user show openclaw-gateway.service --property=ActiveState --property=SubState --property=ExecMainStatus --property=NRestarts",
            TimeSpan.FromSeconds(15));
        File.WriteAllText(
            Path.Combine(_fixture.ArtifactDir, "published-gateway-service-status.log"),
            $"exitCode={result.ExitCode}{Environment.NewLine}{result.Stdout}{result.Stderr}");
    }

    private static void EnsureSuccess(CommandResult result, string phase)
    {
        if (result.ExitCode == 0 && !result.TimedOut)
            return;

        throw new InvalidOperationException(
            $"Failed to {phase}: exit={result.ExitCode}, timedOut={result.TimedOut}, " +
            $"stderr={SanitizeFailureOutput(result.Stderr)}");
    }

    private static string SanitizeFailureOutput(string value)
    {
        var sanitized = E2ESetupFixture.SanitizeForLog(value.Trim());
        return sanitized.Length <= 2_000 ? sanitized : sanitized[..2_000] + " [truncated]";
    }

    private static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string BuildServerScript(string deterministicReply)
    {
        var replyJson = JsonSerializer.Serialize(deterministicReply);
        return $$"""
            "use strict";
            const http = require("node:http");
            const reply = {{replyJson}};
            let requestCount = 0;

            function writeJson(response, statusCode, value) {
              const body = JSON.stringify(value);
              response.writeHead(statusCode, {
                "content-type": "application/json",
                "content-length": Buffer.byteLength(body)
              });
              response.end(body);
            }

            function readJson(request, response, callback) {
              let body = "";
              request.setEncoding("utf8");
              request.on("data", chunk => {
                body += chunk;
                if (Buffer.byteLength(body) > 1024 * 1024) request.destroy();
              });
              request.on("end", () => {
                try {
                  callback(JSON.parse(body), Buffer.byteLength(body));
                } catch {
                  writeJson(response, 400, { error: { message: "invalid json", type: "invalid_request_error" } });
                }
              });
            }

            function completedResponse(responseId, messageId) {
              return {
                id: responseId,
                object: "response",
                created_at: Math.floor(Date.now() / 1000),
                status: "completed",
                error: null,
                incomplete_details: null,
                instructions: null,
                max_output_tokens: null,
                model: "gpt-5.6-luna",
                output: [{
                  id: messageId,
                  type: "message",
                  status: "completed",
                  role: "assistant",
                  content: [{ type: "output_text", text: reply, annotations: [] }]
                }],
                parallel_tool_calls: true,
                previous_response_id: null,
                reasoning: { effort: null, summary: null },
                store: false,
                temperature: 0,
                text: { format: { type: "text" } },
                tool_choice: "auto",
                tools: [],
                top_p: 1,
                truncation: "disabled",
                usage: {
                  input_tokens: 1,
                  input_tokens_details: { cached_tokens: 0 },
                  output_tokens: 1,
                  output_tokens_details: { reasoning_tokens: 0 },
                  total_tokens: 2
                },
                user: null,
                metadata: {}
              };
            }

            const server = http.createServer((request, response) => {
              if (request.method === "GET" && request.url === "/healthz") {
                writeJson(response, 200, { ok: true, status: "live" });
                return;
              }
              if (request.method === "GET" && request.url === "/v1/models") {
                writeJson(response, 200, {
                  object: "list",
                  data: [{ id: "gpt-5.6-luna", object: "model", created: 0, owned_by: "openclaw-e2e" }]
                });
                return;
              }
              if (request.method === "POST" && request.url === "/v1/embeddings") {
                readJson(request, response, (payload, bodyBytes) => {
                  requestCount += 1;
                  const inputs = Array.isArray(payload.input) ? payload.input : [payload.input];
                  console.log(JSON.stringify({
                    ts: new Date().toISOString(),
                    method: request.method,
                    path: request.url,
                    status: 200,
                    bodyBytes,
                    modelId: typeof payload.model === "string" ? payload.model : "unknown",
                    inputItemCount: inputs.length,
                    outputEventCount: 1,
                    syntheticMarker: "OPENCLAW_E2E_EMBEDDING",
                    requestCount
                  }));
                  writeJson(response, 200, {
                    object: "list",
                    data: inputs.map((_, index) => ({
                      object: "embedding",
                      embedding: [0, 0, 0, 0],
                      index
                    })),
                    model: typeof payload.model === "string" ? payload.model : "text-embedding-3-small",
                    usage: { prompt_tokens: inputs.length, total_tokens: inputs.length }
                  });
                });
                return;
              }
              if (request.method !== "POST" || request.url !== "/v1/responses") {
                writeJson(response, 404, { error: { message: "not found", type: "invalid_request_error" } });
                return;
              }

              readJson(request, response, (payload, bodyBytes) => {
                requestCount += 1;
                const responseId = `resp_openclaw_e2e_${requestCount}`;
                const messageId = `msg_openclaw_e2e_${requestCount}`;
                const complete = completedResponse(responseId, messageId);
                const stream = payload.stream !== false;
                const events = [
                  {
                    type: "response.output_item.added",
                    output_index: 0,
                    item: { id: messageId, type: "message", status: "in_progress", role: "assistant", content: [] }
                  },
                  {
                    type: "response.output_item.done",
                    output_index: 0,
                    item: complete.output[0]
                  },
                  { type: "response.completed", response: complete }
                ];

                console.log(JSON.stringify({
                  ts: new Date().toISOString(),
                  method: request.method,
                  path: request.url,
                  status: 200,
                  bodyBytes,
                  stream,
                  modelId: typeof payload.model === "string" ? payload.model : "unknown",
                  inputItemCount: Array.isArray(payload.input) ? payload.input.length : 0,
                  outputEventCount: stream ? events.length : 1,
                  syntheticMarker: reply,
                  requestCount
                }));

                if (!stream) {
                  writeJson(response, 200, complete);
                  return;
                }

                response.writeHead(200, {
                  "content-type": "text/event-stream",
                  "cache-control": "no-cache",
                  connection: "keep-alive"
                });
                for (const event of events) response.write(`data: ${JSON.stringify(event)}\n\n`);
                response.end("data: [DONE]\n\n");
              });
            });

            server.listen({ host: "127.0.0.1", port: {{Port}} }, () => {
              console.log(JSON.stringify({
                ts: new Date().toISOString(),
                status: "ready",
                port: {{Port}},
                syntheticMarker: reply
              }));
            });

            function shutdown() {
              server.close(error => process.exit(error ? 1 : 0));
            }
            process.on("SIGTERM", shutdown);
            process.on("SIGINT", shutdown);
            """;
    }
}
