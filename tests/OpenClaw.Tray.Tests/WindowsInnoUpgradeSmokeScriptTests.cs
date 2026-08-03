namespace OpenClaw.Tray.Tests;

public sealed class WindowsInnoUpgradeSmokeScriptTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void EntryPoint_RequiresExplicitCleanMachineAndVersionedPayloads()
    {
        var script = ReadUpgradeScript();

        Assert.StartsWith("#Requires -Version 7.0", script);
        Assert.Contains("[string]$PreviousRelease = \"\"", script);
        Assert.Contains("[string]$PreviousVersion = \"\"", script);
        Assert.Contains("[string]$PreviousInstallerPath = \"\"", script);
        Assert.Contains("[string]$PreviousInstallerSha256 = \"\"", script);
        Assert.Contains("[string]$CurrentInstallerPath = \"\"", script);
        Assert.Contains("[string]$CurrentPayloadPath = \"\"", script);
        Assert.Contains("[switch]$ConfirmCleanMachineReleaseIdentity", script);
        Assert.Contains("Pass -ConfirmCleanMachineReleaseIdentity only on a disposable clean Windows machine or VM.", script);
        Assert.Contains("A missing previous release fails closed.", script);
        Assert.Contains("must be newer than PreviousVersion", script);
        Assert.Contains("Reinstalling the same payload is not upgrade proof.", script);
        Assert.Contains("\"/VERYSILENT\"", script);
        Assert.DoesNotContain("Read-Host", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntryPoint_AcquiresOnlyExactOfficialHttpsAssetAndChecksDigestAndSignature()
    {
        var script = ReadUpgradeScript();

        Assert.Contains("https://api.github.com/repos/openclaw/openclaw-windows-node", script);
        Assert.Contains("https://github.com/openclaw/openclaw-windows-node/releases/download/", script);
        Assert.Contains("OpenClawCompanion-Setup-x64.exe", script);
        Assert.Contains("Expected exactly one official", script);
        Assert.Contains("Downloaded previous installer SHA-256 does not match the official digest.", script);
        Assert.Contains("Previous release tray signature", script);
        Assert.Contains("OpenClaw Foundation", script);
        Assert.Contains("-AllowUnsignedPreviousPayload is valid only with -PreviousInstallerPath.", script);
        Assert.Contains("Copy-Item -LiteralPath $sourceInstaller -Destination $previousInstaller", script);
        Assert.Contains("Artifact snapshot of the previous installer does not match", script);
        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntryPoint_FailsClosedOnReleaseDevRegistryDataProcessAndWslState()
    {
        var script = ReadUpgradeScript();

        foreach (var marker in new[]
        {
            "{M0LTB0T-TRAY-4PP1-D3N7}_is1",
            "{M0LTB0T-TRAY-4PP1-DEV}_is1",
            "OpenClawTray",
            "OpenClawTray-Dev",
            "OpenClawGateway",
            "OpenClawGateway-Dev",
            "OpenClaw.Tray.WinUI.exe",
            "Software\\Classes\\openclaw",
            "Software\\Classes\\openclaw-dev"
        })
        {
            Assert.Contains(marker, script);
        }

        Assert.Contains("Get-CimInstance Win32_Process -ErrorAction Stop", script);
        Assert.Contains("Refusing to stop it", script);
        Assert.Contains("Refusing to touch existing WSL state.", script);
        Assert.DoesNotContain("Stop-Process -Name", script);
    }

    [Fact]
    public void EntryPoint_ProvesUpgradeStateRuntimeAndCleanupContracts()
    {
        var script = ReadUpgradeScript();

        Assert.Contains("\"install-previous\"", script);
        Assert.Contains("\"upgrade-current\"", script);
        Assert.Contains("\"state-preservation\"", script);
        Assert.Contains("Assert-RegisteredVersion -ExpectedVersion $PreviousVersion", script);
        Assert.Contains("Assert-RegisteredVersion -ExpectedVersion $CurrentVersion", script);
        Assert.Contains("Preserved upgrade state changed unexpectedly", script);
        Assert.Contains("Previous and current installed tray hashes are identical.", script);
        Assert.Contains("validate-installed-inno-smoke.ps1", script);
        Assert.Contains("-ProofInstalledPayloadOnly", script);
        Assert.Contains("-ExpectedIdentity release", script);
        Assert.Contains("Installed published-gateway Windows node and native chat proof", script);
        Assert.Contains("Assert-PostCleanup", script);
        Assert.Contains("has a registration but no uninstaller", script);
        Assert.Contains("Refusing to recursively delete smoke-owned reparse point", script);
        Assert.Contains("Invoke-SmokeNativeProcess", script);
        Assert.DoesNotContain("Start-Process", script);
        Assert.DoesNotContain("/DIR=`\"", script);
        Assert.DoesNotContain("/LOG=`\"", script);
        Assert.DoesNotContain("-ErrorAction SilentlyContinue | Out-Null", script);
    }

    [Fact]
    public void ManualWorkflow_IsDispatchOnlyAndRequiresCleanRunnerConfirmation()
    {
        var workflow = File.ReadAllText(Path.Combine(
            Root,
            ".github",
            "workflows",
            "windows-inno-upgrade-smoke.yml"));

        Assert.Contains("workflow_dispatch:", workflow);
        Assert.DoesNotContain("pull_request:", workflow);
        Assert.DoesNotContain("push:", workflow);
        Assert.Contains("CLEAN-OPENCLAW-VM", workflow);
        Assert.Contains("CLEAN_MACHINE_CONFIRMATION: ${{ inputs.clean_machine_confirmation }}", workflow);
        Assert.Contains("PREVIOUS_RELEASE: ${{ inputs.previous_release }}", workflow);
        Assert.Contains("PREVIOUS_INSTALLER_SHA256: ${{ inputs.previous_installer_sha256 }}", workflow);
        Assert.Contains("$env:CLEAN_MACHINE_CONFIRMATION", workflow);
        Assert.Contains("-PreviousRelease $env:PREVIOUS_RELEASE", workflow);
        Assert.Contains("-PreviousInstallerSha256 $env:PREVIOUS_INSTALLER_SHA256", workflow);
        Assert.DoesNotContain("\"${{ inputs.", workflow, StringComparison.Ordinal);
        Assert.Contains("-ConfirmCleanMachineReleaseIdentity", workflow);
        Assert.Contains("-PreviousInstallerSha256", workflow);
        Assert.Contains("actions/upload-artifact", workflow);
        Assert.DoesNotContain("secrets.", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Documentation_DistinguishesSafetyPreflightFromUpgradeProof()
    {
        var documentation = File.ReadAllText(Path.Combine(Root, "docs", "WINDOWS_NODE_TESTING.md"));

        Assert.Contains(".\\scripts\\validate-inno-upgrade-smoke.ps1", documentation);
        Assert.Contains("-SafetyPreflightOnly", documentation);
        Assert.Contains("does not count as upgrade proof", documentation);
        Assert.Contains("disposable clean Windows machine or VM", documentation);
        Assert.Contains("clean Hyper-V/Crabbox Windows image", documentation);
        Assert.Contains("PowerShell 7", documentation);
        Assert.Contains("previous-release", documentation);
        Assert.Contains("does not claim product rollback", documentation);
        Assert.Contains("no updater-to-direct-install fallback", documentation);
        Assert.Contains("openclaw-cross-os-release-checks-reusable.yml", documentation);
    }

    private static string ReadUpgradeScript() =>
        File.ReadAllText(Path.Combine(Root, "scripts", "validate-inno-upgrade-smoke.ps1"));
}
