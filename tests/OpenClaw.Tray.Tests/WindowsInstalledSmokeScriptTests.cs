namespace OpenClaw.Tray.Tests;

public sealed class WindowsInstalledSmokeScriptTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void EntryPoint_HasWindowsFriendlyDefaultsAndOwnsArtifacts()
    {
        var script = ReadHelper();

        Assert.Contains("[string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)", script);
        Assert.Contains("[string]$ArtifactRoot = \"\"", script);
        Assert.Contains("TestResults\\InstalledSmoke\\$timestamp", script);
        Assert.Contains("$LogPath = Join-Path $ArtifactRoot \"installed-smoke.log\"", script);
        Assert.Contains("$DonePath = Join-Path $ArtifactRoot \"installed-smoke.done\"", script);
        Assert.Contains("$PidPath = Join-Path $ArtifactRoot \"installed-smoke.pid\"", script);
        Assert.Contains("Installed DEV Inno smoke passed.", script);
        Assert.DoesNotContain("parallels", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_RequiresInstalledPayloadAndNamedRoundtrip()
    {
        var script = ReadHelper();

        Assert.Contains("$requiredPhases = @(\"preflight\", \"build\", \"install\", \"installed-payload\", \"roundtrip\", \"cleanup\")", script);
        Assert.Contains("build-inno-local.ps1\") -Arch x64 -Dev -Fast -InstallInno", script);
        Assert.Contains("OpenClawCompanion-Dev-Setup-x64.exe", script);
        Assert.Contains("$env:OPENCLAW_E2E_TRAY_EXE = $installedTray", script);
        Assert.Contains("PublishedGatewayNativeChatTests", script);
        Assert.Contains("RealPublishedGateway_DeviceInfo_AndNativeChat_Roundtrip", script);
        Assert.Contains("if ([string]$proof.outcome -ne \"Passed\")", script);
        Assert.DoesNotContain("skipped", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Helper_ExposesProofOnlyReuseWithoutOwningInstallOrCleanup()
    {
        var script = ReadHelper();

        Assert.Contains("[switch]$ProofInstalledPayloadOnly", script);
        Assert.Contains("[string]$InstalledTrayPath = \"\"", script);
        Assert.Contains("[string]$ExpectedPayloadPath = \"\"", script);
        Assert.Contains("[ValidateSet(\"dev\", \"release\")]", script);
        Assert.Contains("$requiredPhases = @(\"preflight\", \"installed-payload\", \"roundtrip\")", script);
        Assert.Contains("Installed payload overrides are valid only with -ProofInstalledPayloadOnly.", script);
        Assert.Contains("mode = if ($ProofInstalledPayloadOnly) { \"proof-only\" }", script);
        Assert.Contains("if (-not $ProofInstalledPayloadOnly -and -not $phaseResults.Contains(\"cleanup\"))", script);
    }

    [Fact]
    public void Helper_ProtectsReleaseAndExistingDeveloperState()
    {
        var script = ReadHelper();

        Assert.Contains("{M0LTB0T-TRAY-4PP1-DEV}_is1", script);
        Assert.Contains("OpenClawTray-Dev", script);
        Assert.Contains("OpenClawGateway-Dev", script);
        Assert.Contains("Refusing to touch developer state", script);
        Assert.Equal(2, script.Split("-replace '\\x00', ''").Length - 1);
        Assert.DoesNotContain("OpenClawGateway\"", script);
        Assert.DoesNotContain("OpenClawTray\"", script);
    }

    [Fact]
    public void Helper_CleansOnlyExactInstalledProcessAndFailsOnResidue()
    {
        var script = ReadHelper();
        var nativeHelper = ReadNativeHelper();

        Assert.Contains("[string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $expectedPath", script);
        Assert.Contains("Stop-Process -Id ([int]$process.ProcessId)", script);
        Assert.DoesNotContain("Stop-Process -Name", script);
        Assert.DoesNotContain("Start-Process", script);
        Assert.Contains("Invoke-SmokeNativeProcess", script);
        Assert.Equal(2, script.Split("--disable-build-servers").Length - 1);
        Assert.Contains("/p:UseSharedCompilation=false", script);
        Assert.Contains("$processStartInfo.UseShellExecute = $false", nativeHelper);
        Assert.Contains("$processStartInfo.RedirectStandardOutput = $true", nativeHelper);
        Assert.Contains("$processStartInfo.RedirectStandardError = $true", nativeHelper);
        Assert.Contains("Stop-SmokeNativeProcessTree -RootProcessId $process.Id", nativeHelper);
        Assert.Contains("[Threading.Tasks.Task]::WaitAll(", nativeHelper);
        Assert.Contains("Stop-Process -Id $RootProcessId", nativeHelper);
        Assert.DoesNotContain("Stop-Process -Name", nativeHelper);
        Assert.Contains("DEV WSL distro still exists after cleanup", script);
        Assert.Contains("DEV uninstall registration still exists after cleanup", script);
        Assert.Contains("Installed DEV tray still exists after cleanup", script);
        Assert.Contains("Installed smoke phase '$phase' was not completed successfully.", script);
    }

    [Fact]
    public void PackagingTest_DoesNotOverwriteBoundInstallerPath()
    {
        var script = File.ReadAllText(Path.Combine(
            Root,
            "tests",
            "PackagingTests",
            "Test-InnoUninstallOrdering.ps1"));

        Assert.Contains("$script:resolvedInstallerPath = \"\"", script);
        Assert.DoesNotContain("$script:installerPath", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void E2EFixture_SuppressesFirstRunBootstrapRace()
    {
        var fixture = File.ReadAllText(Path.Combine(
            Root,
            "tests",
            "OpenClaw.E2ETests",
            "Setup",
            "E2ESetupFixture.cs"));

        Assert.Contains("merged[\"HasInjectedFirstRunBootstrap\"] = true;", fixture);
    }

    [Fact]
    public void InstalledSmokeDocs_StateSourceHarnessSetupBoundary()
    {
        var windowsTesting = File.ReadAllText(Path.Combine(Root, "docs", "WINDOWS_NODE_TESTING.md"));

        Assert.Contains("installed UI setup entrypoint itself", windowsTesting);
        Assert.Contains("source-built E2E", windowsTesting);
        Assert.Contains(".\\scripts\\validate-installed-inno-smoke.ps1", windowsTesting);
        Assert.DoesNotContain("parallels-windows-vm.sh smoke", windowsTesting);
    }

    private static string ReadHelper() =>
        File.ReadAllText(Path.Combine(Root, "scripts", "validate-installed-inno-smoke.ps1"));

    private static string ReadNativeHelper() =>
        File.ReadAllText(Path.Combine(Root, "scripts", "_smoke-native-process.ps1"));
}
