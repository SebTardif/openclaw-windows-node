namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-text contract tests for the live model / real Discord channel
/// parity validation script and its CI hermetic behavior. Mirrors
/// <see cref="WindowsInstalledSmokeScriptTests"/> and
/// <see cref="ReleaseSigningWorkflowTests"/>: no live fixture, no process
/// execution, just <see cref="File.ReadAllText(string)"/> plus string
/// assertions against known-important literals.
/// </summary>
public sealed class LiveParityValidationScriptTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void Script_RequiresExplicitLaneAndHasNoAllowSkipEscapeHatch()
    {
        var script = ReadScript();

        Assert.Contains("[Parameter(Mandatory = $true)]", script);
        Assert.Contains("[ValidateSet(\"LiveModel\", \"RealChannel\", \"All\")]", script);
        Assert.Contains("[string]$Lane", script);
        // No -AllowSkip parameter exists on this script (unlike validate-mxc-e2e.ps1):
        // a requested live lane must run and pass, never be waved through as skipped.
        Assert.DoesNotContain("[switch]$AllowSkip", script);
        Assert.Contains("there is no -AllowSkip escape", script);
        Assert.Contains("a lane you explicitly asked for must actually run and pass", script);
    }

    [Fact]
    public void Script_RunsSecretlessContractTestsBeforeAnyLiveProof()
    {
        var script = ReadScript();

        var contractRunIndex = script.IndexOf(
            "Run secretless live-parity contract tests", StringComparison.Ordinal);
        var proofRunIndex = script.IndexOf(
            "Run live parity proof(s) for lane $Lane", StringComparison.Ordinal);

        Assert.True(contractRunIndex >= 0, "Script must run the secretless contract tests as a named step.");
        Assert.True(proofRunIndex >= 0, "Script must run the live proof lane(s) as a named step.");
        Assert.True(
            contractRunIndex < proofRunIndex,
            "Secretless contract tests must run before any live proof lane spends real credentials/budget.");

        Assert.Contains(
            "FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityGateContractTests|" +
            "FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityProfileContractTests|" +
            "FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParitySupportContractTests",
            script);
    }

    [Fact]
    public void Script_FailsOnSkippedOrMissingRequestedProof_NeverTreatsSkipAsSuccess()
    {
        var script = ReadScript();

        Assert.Contains("RealLiveModel_ConfiguredProvider_ChatTurn_Roundtrip", script);
        Assert.Contains("RealDiscordChannel_MentionAgent_OutboundReply_Roundtrip", script);
        Assert.Contains("Live parity proof was not reported in TRX", script);
        Assert.Contains("Live parity proof skipped:", script);
        Assert.Contains(
            "A requested live lane must actually run and pass. There is no", script);
    }

    [Fact]
    public void Script_NeverPrintsProfilePathsOrCredentialValues()
    {
        var script = ReadScript();

        Assert.Contains(
            "this never reads the value into a variable that", script);
        Assert.Contains(
            "This lane spends real provider/Discord API budget. Profile paths and secret values are never printed by this script.",
            script);
        // The preflight helper only ever calls GetEnvironmentVariable to check
        // presence or to resolve a profile *path* for File-existence/JSON
        // parsing; it must never pipe a resolved value into Write-Host/Write-Output.
        Assert.DoesNotContain("Write-Host $profilePath", script);
        Assert.DoesNotContain("Write-Output $profilePath", script);
    }

    [Fact]
    public void Script_ProfilePreflight_ToleratesJsonThatParsesToNull()
    {
        var script = ReadScript();

        Assert.Contains("if ($null -eq $json)", script);
        Assert.Contains("return $names", script);
    }

    [Fact]
    public void Script_RestoresTrackedEnvironmentVariablesInFinally()
    {
        var script = ReadScript();

        var finallyIndex = script.IndexOf("} finally {", StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0, "Script must restore tracked environment variables in a finally block.");
        var finallyBlock = script[finallyIndex..];

        Assert.Contains("OPENCLAW_RUN_E2E", script);
        Assert.Contains("OPENCLAW_RUN_LIVE_MODEL_E2E", script);
        Assert.Contains("OPENCLAW_RUN_REAL_CHANNEL_E2E", script);
        Assert.Contains("[Environment]::SetEnvironmentVariable($name, $previousEnv[$name], \"Process\")", finallyBlock);
    }

    [Fact]
    public void CiWorkflow_AddsOnlySecretlessContractTestsToAnExistingE2EShard()
    {
        var workflow = ReadCiWorkflow();

        Assert.Contains("FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityGateContractTests", workflow);
        Assert.Contains("FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityProfileContractTests", workflow);
        Assert.Contains("FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParitySupportContractTests", workflow);
    }

    [Fact]
    public void CiWorkflow_RequiresEachNetworkRecoveryProofToPassAlongsideSecretlessContracts()
    {
        var workflow = ReadCiWorkflow();

        Assert.Contains("if (\"${{ matrix.name }}\" -eq \"network-recovery\")", workflow);
        Assert.Contains("GatewayStopAndStart_TrayLeavesReadyThenRecovers", workflow);
        Assert.Contains("RepeatedGatewayRestart_TrayAndNodeRecoverEachTime", workflow);
        Assert.Contains("did not report the network recovery proof", workflow);
        Assert.Contains("must pass and cannot be skipped", workflow);
    }

    [Fact]
    public void CiWorkflow_NeverRunsLiveProofClassesOrReferencesLiveParitySecretGates()
    {
        var workflow = ReadCiWorkflow();

        // The live proof classes must never appear in any CI filter: CI must
        // never attempt to run a lane that requires real credentials.
        Assert.DoesNotContain("OpenClaw.E2ETests.LiveParity.LiveModelE2ETests", workflow);
        Assert.DoesNotContain("OpenClaw.E2ETests.LiveParity.RealChannelE2ETests", workflow);
        Assert.DoesNotContain("RealLiveModel_ConfiguredProvider_ChatTurn_Roundtrip", workflow);
        Assert.DoesNotContain("RealDiscordChannel_MentionAgent_OutboundReply_Roundtrip", workflow);

        // Normal CI must never set or reference the lanes' own opt-in gates or
        // profile path variables: only OPENCLAW_RUN_E2E (shared with the rest
        // of the E2E suite) is ever set by ci.yml.
        Assert.DoesNotContain("OPENCLAW_RUN_LIVE_MODEL_E2E", workflow);
        Assert.DoesNotContain("OPENCLAW_RUN_REAL_CHANNEL_E2E", workflow);
        Assert.DoesNotContain("OPENCLAW_LIVE_MODEL_PROFILE", workflow);
        Assert.DoesNotContain("OPENCLAW_REAL_CHANNEL_PROFILE", workflow);
    }

    [Fact]
    public void Docs_ExplainNoHostedScheduledLaneRationale()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "LIVE_PARITY_TESTING.md"));

        Assert.Contains("## No hosted scheduled lane", docs);
        Assert.Contains(
            "package proof (building and testing whatever a contributor pushed, including",
            docs);
        Assert.Contains("untrusted candidate code must never run with credential access", docs);
        Assert.Contains(
            "A hosted, scheduled live-parity lane should only be added here once this",
            docs);
        Assert.Contains("a protected environment, an", docs);
        Assert.Contains(
            "actor/ref gate, ephemeral ACL-restricted secret injection (broker-leased,",
            docs);
        Assert.Contains("heartbeat/release in a `finally`", docs);
        Assert.DoesNotContain("\u2014", docs); // no em dashes in user-facing prose
    }

    [Fact]
    public void Docs_CiteMainRepoLiveTransportPriorArtWithFilePaths()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "LIVE_PARITY_TESTING.md"));

        Assert.Contains(".github/workflows/qa-live-transports-convex.yml", docs);
        Assert.Contains("environment: qa-live-shared", docs);
        Assert.Contains("authorize_actor", docs);
        Assert.Contains("validate_selected_ref", docs);
        Assert.Contains("qa/convex-credential-broker/", docs);
        Assert.Contains(
            "extensions/qa-lab/src/live-transports/shared/credential-lease.runtime.ts",
            docs);
        Assert.Contains("acquireQaCredentialLease", docs);
        Assert.Contains("scripts/test-live.mjs", docs);
        Assert.Contains("App-level encryption: not included in v1", docs);
        Assert.Contains(".agents/skills/parallels-discord-roundtrip/", docs);
        Assert.Contains("useful only as an independent manual oracle", docs);
    }

    [Fact]
    public void Docs_DoNotClaimMockLiveParityAndDoNotClaimOutboundOnly()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "LIVE_PARITY_TESTING.md"));

        Assert.Contains("not establish mock-vs-live behavioral parity", docs);
        Assert.Contains("Neither substitutes for or validates the other", docs);
        Assert.Contains("is not an outbound-send-only check", docs);
        Assert.Contains(
            "Discord's own infrastructure delivers that", docs);
        Assert.Contains("only then does the driver read that reply back", docs);
    }

    [Fact]
    public void LiveFixtures_DisableContentBearingRuntimeArtifacts()
    {
        var setupFixture = ReadRepoFile("tests", "OpenClaw.E2ETests", "Setup", "E2ESetupFixture.cs");
        var liveModelFixture = ReadRepoFile(
            "tests", "OpenClaw.E2ETests", "LiveParity", "LiveModelE2ESetupFixture.cs");
        var realChannelFixture = ReadRepoFile(
            "tests", "OpenClaw.E2ETests", "LiveParity", "RealChannelE2ESetupFixture.cs");
        var secretScope = ReadRepoFile(
            "tests", "OpenClaw.E2ETests", "LiveParity", "WslSecretGatewayScope.cs");

        Assert.Contains("captureRuntimeArtifacts: false", liveModelFixture);
        Assert.Contains("captureRuntimeArtifacts: false", realChannelFixture);
        Assert.Contains("if (!_captureRuntimeArtifacts)", setupFixture);
        Assert.Contains("DrainStreamAsync(p.StandardOutput)", setupFixture);
        Assert.Contains("DrainStreamAsync(p.StandardError)", setupFixture);
        Assert.Contains("logCommandAndOutput: false", secretScope);
    }

    [Fact]
    public void DiscordClient_HardCodesTrustedApiHost()
    {
        var client = ReadRepoFile(
            "tests", "OpenClaw.E2ETests", "LiveParity", "DiscordRestClient.cs");
        var profiles = ReadRepoFile(
            "tests", "OpenClaw.E2ETests", "LiveParity", "LiveParityProfiles.cs");

        Assert.Contains(
            "private const string DiscordApiBaseUrl = \"https://discord.com/api/v10/\";",
            client);
        Assert.DoesNotContain("discordApiBaseUrl", profiles, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadScript() =>
        File.ReadAllText(Path.Combine(Root, "scripts", "validate-live-parity-e2e.ps1"));

    private static string ReadCiWorkflow() =>
        File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

    private static string ReadRepoFile(params string[] segments) =>
        File.ReadAllText(segments.Aggregate(Root, Path.Combine));
}
