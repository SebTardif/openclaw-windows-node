using OpenClaw.E2ETests.Setup;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.LiveParity;

/// <summary>
/// Generalizes the backup, stage secret(s) plus configure, restart, use,
/// restore config, unset environment, restart, delete backup lifecycle
/// shared by both live-parity lanes. Modeled on
/// <c>WslOpenAiResponsesMock</c>'s mock-provider lifecycle.
///
/// Secrets are staged into the WSL gateway service's process environment
/// via <c>systemctl --user set-environment</c>, never through
/// openclaw.json, command-line arguments visible outside the WSL VM, or
/// logs: values travel from the Windows-side <c>RunInWslAsync</c>
/// <c>environment</c> dictionary (forwarded through WSLENV) into a
/// piped-stdin bash script whose *source text* only ever references
/// <c>$NAME</c>, never embeds the literal value. The one residual exposure
/// is inherent to <c>systemctl --user set-environment NAME="$NAME"</c>
/// itself: bash must expand <c>$NAME</c> before exec, so the resolved value
/// is briefly present in that single WSL-internal process's argv. This is
/// local to the WSL VM, short-lived, and is the mechanism this lane's
/// design brief calls for; see docs/LIVE_PARITY_TESTING.md "Known
/// limitations".
///
/// <see cref="DisposeAsync"/> is strict, not best-effort: configuration and
/// environment restoration failures propagate so they cannot be silently
/// swallowed and leave the shared WSL gateway mutated for later tests.
/// </summary>
internal sealed class WslSecretGatewayScope : IAsyncDisposable
{
    private readonly E2ESetupFixture _fixture;
    private readonly LiveParitySecretRegistry _secrets;
    private readonly IReadOnlyDictionary<string, string> _secretEnvironment;
    private readonly string _configPath;
    private readonly string _backupPath;
    private bool _configured;

    private WslSecretGatewayScope(
        E2ESetupFixture fixture,
        LiveParitySecretRegistry secrets,
        IReadOnlyDictionary<string, string> secretEnvironment,
        string runId)
    {
        _fixture = fixture;
        _secrets = secrets;
        _secretEnvironment = secretEnvironment;
        _configPath = "/home/openclaw/.openclaw/openclaw.json";
        _backupPath = $"/home/openclaw/.openclaw/openclaw.json.live-parity-{runId}.backup";
    }

    /// <summary>
    /// Backs up the gateway config, exports every entry of
    /// <paramref name="secretEnvironment"/> into the gateway systemd user
    /// service's environment, runs <paramref name="configureScript"/> (a
    /// caller-supplied sequence of `openclaw config set ...` lines that must
    /// reference secrets only via SecretRef env names, never literal
    /// values), restarts the gateway, and waits for Ready.
    /// </summary>
    public static async Task<WslSecretGatewayScope> StageAsync(
        E2ESetupFixture fixture,
        LiveParitySecretRegistry secrets,
        IReadOnlyDictionary<string, string> secretEnvironment,
        string configureScript)
    {
        var scope = new WslSecretGatewayScope(fixture, secrets, secretEnvironment, Guid.NewGuid().ToString("N"));
        try
        {
            await scope.BackupAsync().ConfigureAwait(false);
            await scope.StageSecretsAndConfigureAsync(configureScript).ConfigureAwait(false);
            return scope;
        }
        catch (Exception stageError)
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Live-parity gateway staging and its cleanup both failed.",
                    stageError,
                    cleanupError);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_configured)
            return;

        var unsetLines = string.Join(
            Environment.NewLine,
            _secretEnvironment.Keys.Select(
                name => $"systemctl --user unset-environment {name} || cleanup_status=1"));

        var restore = await _fixture.RunInWslAsync(
            $"""
            set +e
            cleanup_status=0
            if test -f {ShellScriptingHelpers.SingleQuote(_backupPath)}; then
              if cp -p {ShellScriptingHelpers.SingleQuote(_backupPath)} {ShellScriptingHelpers.SingleQuote(_configPath)}; then
                rm -f {ShellScriptingHelpers.SingleQuote(_backupPath)} || cleanup_status=1
              else
                cleanup_status=1
              fi
            else
              cleanup_status=1
            fi
            {unsetLines}
            openclaw gateway restart >/dev/null 2>&1 || systemctl --user restart openclaw-gateway.service || cleanup_status=1
            exit "$cleanup_status"
            """,
            TimeSpan.FromSeconds(120),
            inputViaStdin: true,
            logCommandAndOutput: false);
        EnsureSuccess(_secrets, restore, "restore live-parity gateway configuration and unset staged secrets");
        _configured = false;

        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        _fixture.CaptureArtifacts();
    }

    private async Task BackupAsync()
    {
        var backup = await _fixture.RunInWslAsync(
            $"""
            set -eu
            config={ShellScriptingHelpers.SingleQuote(_configPath)}
            backup={ShellScriptingHelpers.SingleQuote(_backupPath)}
            test -f "$config"
            test ! -e "$backup"
            cp -p "$config" "$backup"
            """,
            TimeSpan.FromSeconds(15),
            inputViaStdin: true);
        EnsureSuccess(_secrets, backup, "back up live-parity gateway configuration");
        _configured = true;
    }

    private async Task StageSecretsAndConfigureAsync(string configureScript)
    {
        var setLines = string.Join(
            Environment.NewLine,
            _secretEnvironment.Keys.Select(name => $"systemctl --user set-environment {name}=\"${name}\""));

        var configure = await _fixture.RunInWslAsync(
            $"""
            set -eu
            {setLines}
            {configureScript}
            openclaw gateway restart >/dev/null 2>&1 || systemctl --user restart openclaw-gateway.service
            """,
            TimeSpan.FromSeconds(150),
            _secretEnvironment,
            inputViaStdin: true,
            logCommandAndOutput: false);
        EnsureSuccess(_secrets, configure, "stage secrets and configure live-parity gateway");

        await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    private static void EnsureSuccess(LiveParitySecretRegistry secrets, CommandResult result, string phase)
    {
        if (result.ExitCode == 0 && !result.TimedOut)
            return;

        throw new InvalidOperationException(
            $"Failed to {phase}: exit={result.ExitCode}, timedOut={result.TimedOut}. " +
            "Command output was suppressed because this is a secret-bearing live lane.");
    }
}
