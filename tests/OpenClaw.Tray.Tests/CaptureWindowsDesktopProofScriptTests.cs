namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-contract checks for scripts\capture-windows-desktop-proof.ps1,
/// following the same reading-the-raw-script pattern as
/// WindowsInstalledSmokeScriptTests. See
/// CaptureWindowsDesktopProofRunnerTests for the executable dry-run coverage.
/// </summary>
public sealed class CaptureWindowsDesktopProofScriptTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void Script_NeverTouchesRealAppData()
    {
        var script = ReadScript();

        Assert.Contains("Refusing to write proof artifacts under real app data", script);
        Assert.Contains("Test-PathIsUnderRoot -Candidate $ArtifactRoot -Root $guardedRoot", script);
        Assert.Contains("Join-Path $env:APPDATA \"OpenClawTray\"", script);
        Assert.Contains("Join-Path $env:LOCALAPPDATA \"OpenClawTray\"", script);
        Assert.DoesNotContain("OPENCLAW_TRAY_DATA_DIR =", script);
        Assert.Contains("this script never reads or writes %APPDATA% or %LOCALAPPDATA% OpenClaw state", script);
    }

    [Fact]
    public void Script_DefinesSchemaVersion1ManifestWithTypedArtifacts()
    {
        var script = ReadScript();

        Assert.Contains("schemaVersion   = 1", script);
        Assert.Contains("type = \"screenshot\"", script);
        Assert.Contains("type = \"proof-text\"", script);
        Assert.Contains("type = \"trx\"", script);
        Assert.Contains("type = \"motion\"", script);
        Assert.Contains("outcome         = $Outcome", script);
        Assert.Contains("failure         = $Failure", script);
        Assert.Contains("environment     = $Environment", script);
    }

    [Fact]
    public void Script_FailsClosedOnMissingRequiredArtifacts()
    {
        var script = ReadScript();

        Assert.Contains("Required witness artifact missing: screenshot.png was not produced", script);
        Assert.Contains("Required artifact missing: proof.txt was not produced", script);
        Assert.Contains("Required artifact missing: $TrxFileName was not produced", script);
        Assert.Contains("Required witness artifact empty: screenshot.png", script);
        Assert.Contains("Required artifact empty: proof.txt", script);
        Assert.Contains("did not pass. Outcome:", script);
    }

    [Fact]
    public void Script_SeparatesOracleFromWitnessAcrossThreeFailurePhases()
    {
        var script = ReadScript();

        // The deterministic UI automation assertion is the oracle; screenshot
        // capture is a witness only and must never be relabeled as an app
        // regression. See .agents/skills/windows-computer-use-proof/SKILL.md.
        Assert.Contains("phase   = \"oracle-failed\"", script);
        Assert.Contains("phase   = \"artifact-missing\"", script);
        Assert.Contains("phase   = \"environment-non-interactive\"", script);
        Assert.Contains("The deterministic UI automation oracle failed.", script);
        Assert.Contains("The deterministic UI automation oracle passed", script);
        Assert.Contains("$oracleFailures", script);
        Assert.Contains("$artifactFailures", script);
        Assert.DoesNotContain("phase   = \"verify\"", script);
    }

    [Fact]
    public void Script_HasInteractiveDesktopGuardThatRunsBeforeBuild()
    {
        var script = ReadScript();

        Assert.Contains("function Test-InteractiveDesktopAvailable", script);
        Assert.Contains("$sessionId -ne 0 -and $userInteractive", script);
        Assert.Contains("no interactive desktop session", script);

        // Structural guard: the environment check must run, and the
        // non-interactive short circuit must appear, before the first build
        // invocation, so a Session 0 / non-interactive host never reaches
        // dotnet build or dotnet test.
        var environmentCheckIndex = script.IndexOf("$environmentInfo = Test-InteractiveDesktopAvailable", StringComparison.Ordinal);
        var nonInteractiveGuardIndex = script.IndexOf("if (-not $DryRun -and -not $environmentInfo.available)", StringComparison.Ordinal);
        var firstBuildIndex = script.IndexOf("& dotnet build", StringComparison.Ordinal);
        Assert.True(environmentCheckIndex >= 0, "Expected a Test-InteractiveDesktopAvailable call.");
        Assert.True(nonInteractiveGuardIndex >= 0, "Expected a non-interactive short-circuit guard.");
        Assert.True(firstBuildIndex >= 0, "Expected a 'dotnet build' invocation.");
        Assert.True(environmentCheckIndex < nonInteractiveGuardIndex, "The environment check must run before the non-interactive guard.");
        Assert.True(nonInteractiveGuardIndex < firstBuildIndex, "The non-interactive guard must run before any dotnet build invocation.");
    }

    [Fact]
    public void Script_TreatsMotionAsOptionalAndFailsClosedWhenUnsupportedButRequested()
    {
        var script = ReadScript();

        Assert.Contains("Motion capture was requested with -IncludeMotion, but no native screen/video recording primitive exists yet", script);
        Assert.Contains("status = \"unavailable\"", script);
        Assert.DoesNotContain("status = \"captured\"; type = \"motion\"", script);
    }

    [Fact]
    public void Script_DryRunNeverBuildsOrTestsAndReportsNotRun()
    {
        var script = ReadScript();

        Assert.Contains("[switch]$DryRun", script);
        Assert.Contains("Mode              = \"dry-run\"", script);
        Assert.Contains("ExitCode          = 2", script);

        // Structural guard: the DryRun short-circuit (and its manifest write +
        // exit) must appear before the first build/test invocation, so a dry
        // run can never fall through into a real build or test run.
        var dryRunGuardIndex = script.IndexOf("if ($DryRun) {", StringComparison.Ordinal);
        var firstBuildIndex = script.IndexOf("& dotnet build", StringComparison.Ordinal);
        Assert.True(dryRunGuardIndex >= 0, "Expected an 'if ($DryRun)' short-circuit block.");
        Assert.True(firstBuildIndex >= 0, "Expected a 'dotnet build' invocation.");
        Assert.True(dryRunGuardIndex < firstBuildIndex, "The DryRun short-circuit must run before any dotnet build invocation.");
    }

    [Fact]
    public void Script_DrivesTheDeterministicConnectionRoute()
    {
        var script = ReadScript();

        Assert.Contains("$ProofPageTag = \"connection\"", script);
        Assert.Contains("$ProofPageMarker = \"ConnectionPageMarker\"", script);
        Assert.Contains("FullyQualifiedName~WindowsDesktopProofTests.ConnectionPage_IsReachableAndScreenshotable", script);
    }

    [Fact]
    public void Script_ValidatesRepoRootBeforeDoingAnyWork()
    {
        var script = ReadScript();

        Assert.Contains("Repository root does not exist:", script);
        Assert.Contains("openclaw-windows-node.slnx", script);
    }

    private static string ReadScript() =>
        File.ReadAllText(Path.Combine(Root, "scripts", "capture-windows-desktop-proof.ps1"));
}
