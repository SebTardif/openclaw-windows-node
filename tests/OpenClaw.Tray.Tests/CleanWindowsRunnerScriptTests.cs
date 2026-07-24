using System.Diagnostics;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class CleanWindowsRunnerScriptTests
{
    private static readonly string Root = TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void HyperVController_RequiresOwnedResourcesAndNestedVirtualization()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");

        Assert.Contains("[switch]$ConfirmOwnedAction", script);
        Assert.Contains("matching ownership markers", script);
        Assert.Contains("VM '$VMName' is unowned. Refusing to modify it.", script);
        Assert.Contains("Assert-OwnedCheckpoint", script);
        Assert.Contains("snapshotId", script);
        Assert.Contains("snapshotCreationTimeUtc", script);
        Assert.Contains("if ((Normalize-ComparisonPath $Marker.vhdPath) -ne", script);
        Assert.Contains("if ((Normalize-ComparisonPath $actualVhdPath) -ne", script);
        Assert.Contains("-Generation 2", script);
        Assert.Contains("-ExposeVirtualizationExtensions $true", script);
        Assert.Contains("-EnableSecureBoot On", script);
        Assert.Contains("[string]::Equals([string]$secureBootValue, \"On\"", script);
        Assert.Contains("Enable-VMTPM", script);
        Assert.Contains("-AutomaticCheckpointsEnabled $false", script);
        Assert.DoesNotContain("Remove-VM ", script);
        Assert.DoesNotContain("Remove-VHD", script);
    }

    [Fact]
    public void HyperVController_OwnsCheckpointTransportArtifactAndRestoreContract()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");

        Assert.Contains("$script:CleanCheckpointName = \"clean-windows\"", script);
        Assert.Contains("$script:PreparedCheckpointName = \"openclaw-prerequisites\"", script);
        Assert.Contains("Copy-Item -LiteralPath $sourcePath -Destination $guestRepoRoot -ToSession $Session", script);
        Assert.Contains("Copy-Item -Path $guestArtifacts -Destination $hostArtifacts -FromSession $Session", script);
        Assert.Contains("Wait-Job -Job $job -Timeout $TimeoutSec", script);
        Assert.Contains("LastBootUpTime.ToUniversalTime().Ticks", script);
        Assert.Contains("if ([Int64]$currentBootTicks -gt [Int64]$previousBootTicks)", script);
        Assert.Contains("validate-installed-inno-smoke.ps1", script);
        Assert.Contains("[ValidateSet(\"Installed\", \"Upgrade\")]", script);
        Assert.Contains("validate-inno-upgrade-smoke.ps1", script);
        Assert.Contains("\"-PreviousRelease\", $RemotePreviousRelease", script);
        Assert.Contains("\"-PreviousInstallerSha256\", $RemotePreviousInstallerSha256", script);
        Assert.Contains("\"-ConfirmCleanMachineReleaseIdentity\"", script);
        Assert.Contains("$validationArguments", script);
        Assert.Contains("function Ensure-GuestPowerShell7Installed", script);
        Assert.Contains("winget install --id Microsoft.PowerShell -e --scope machine", script);
        Assert.Contains("Ensure-GuestPowerShell7Installed -Session $activeSession", script);
        Assert.True(
            script.IndexOf("Ensure-GuestPowerShell7Installed -Session $activeSession", StringComparison.Ordinal) <
            script.IndexOf("Running guest setup-dev", StringComparison.Ordinal),
            "Prepare must install PowerShell 7 before setup-dev creates the reusable prerequisites checkpoint.");
        Assert.Contains("installed-runtime-proof\\phase-status.json", script);
        Assert.Contains("\"upgrade-smoke.log\"", script);
        Assert.Contains("\"upgrade-smoke.done\"", script);
        Assert.Contains("\"inno-install-previous.log\"", script);
        Assert.Contains("\"inno-install-current.log\"", script);
        Assert.Contains("cleanupCompleted", script);
        Assert.Contains("finally {", script);
        Assert.Contains("Restore-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath", script);
    }

    [Theory]
    [InlineData("CombinedInstalledSmoke", "normal", true, "combined-native-desktop-wsl2-installed-smoke", true)]
    [InlineData("NativeDesktopComponent", "normal", true, "native-desktop-component-only", false)]
    [InlineData("Wsl2Component", "wsl2", false, "wsl2-component-only", false)]
    public void CrabboxPlan_UsesExplicitAzureWindowsContract(
        string mode,
        string windowsMode,
        bool expectsDesktop,
        string proofClass,
        bool expectsImage)
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"openclaw-crabbox-plan-{Guid.NewGuid():N}");
        try
        {
            var azureImage = expectsImage ? "publisher:offer:sku:version" : null;
            var result = RunCrabboxPlan(mode, artifactRoot, azureImage);

            Assert.Equal(0, result.ExitCode);
            using var manifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(artifactRoot, "crabbox-smoke-manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal("azure", root.GetProperty("provider").GetString());
            Assert.Equal("windows", root.GetProperty("target").GetString());
            Assert.Equal(windowsMode, root.GetProperty("windowsMode").GetString());
            Assert.Equal(proofClass, root.GetProperty("proofClass").GetString());
            Assert.Equal("amd64", root.GetProperty("acquisition").GetProperty("architecture").GetString());
            Assert.Equal("managed", root.GetProperty("acquisition").GetProperty("azureOsDisk").GetString());
            Assert.Equal(azureImage ?? "", root.GetProperty("acquisition").GetProperty("azureImage").GetString());

            var commands = root.GetProperty("commands");
            Assert.Contains("doctor --provider azure --target windows", commands.GetProperty("doctor").GetString());
            Assert.Contains(
                $"warmup --provider azure --target windows --windows-mode {windowsMode}",
                commands.GetProperty("warmup").GetString());
            Assert.Contains("--arch amd64 --azure-os-disk managed", commands.GetProperty("warmup").GetString());
            Assert.Contains(
                $"run --provider azure --target windows --windows-mode {windowsMode}",
                commands.GetProperty("run").GetString());
            Assert.Contains(
                $"stop --provider azure --target windows --windows-mode {windowsMode}",
                commands.GetProperty("stop").GetString());
            Assert.Contains(
                $"list --provider azure --target windows --windows-mode {windowsMode} --json",
                commands.GetProperty("listAfterStop").GetString());
            Assert.Equal(
                expectsDesktop,
                commands.GetProperty("warmup").GetString()!.Contains("--desktop", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Fact]
    public void CrabboxController_CapturesLeaseArtifactsAndStopsFromFinally()
    {
        var script = ReadScript("Invoke-CrabboxWindowsSmoke.ps1");

        Assert.Contains(@"'\bcbx_[0-9a-f]{12}\b'", script);
        Assert.Contains("Expected exactly one Crabbox lease id", script);
        Assert.Contains("$ErrorActionPreference = \"Continue\"", script);
        Assert.Contains("$leaseId = Get-OptionalLeaseIdFromWarmupOutput", script);
        Assert.Contains("Get-RemoteArtifactPathFromOutput", script);
        Assert.Contains("Get-Content -LiteralPath $capturedRemoteStdoutPath -Raw", script);
        Assert.Contains("$capturedRemoteStdout -replace \"`r`n\", \"`n\"", script);
        Assert.Contains("$remoteScriptContent -replace \"`r`n\", \"`n\"", script);
        Assert.Contains("\"cp\", \"--provider\", $Provider, \"--id\", \"<lease-id>\"", script);
        Assert.Contains("phase-status.json", script);
        Assert.Contains("} finally {", script);
        Assert.Contains("$manifest.execution.stop.attempted = $true", script);
        Assert.Contains("leaseAbsent = (-not $leaseStillListed)", script);
        Assert.Contains("Remove-Item -LiteralPath \"Env:\\CRABBOX_AZURE_IMAGE\"", script);
        Assert.Contains("if ($stopFailure)", script);
        Assert.Contains("lease cleanup failed", script);
    }

    [Fact]
    public void CrabboxController_FailsClosedForCombinedOrMislabelledProof()
    {
        var script = ReadScript("Invoke-CrabboxWindowsSmoke.ps1");

        Assert.Contains("CombinedInstalledSmoke requires -AzureImage", script);
        Assert.Contains("Only CombinedInstalledSmoke can be labeled as full installed-app proof.", script);
        Assert.Contains("combinedImageContract=passed", script);
        Assert.Contains("requires a running WSL2 Ubuntu distribution", script);
        Assert.Contains("Wsl2Component cannot satisfy a UI proof request.", script);
        Assert.Contains("validate-installed-inno-smoke.ps1", script);
        Assert.Contains("WSL2 capability probe passed", script);
    }

    [Fact]
    public void CrabboxUpgradePlan_UsesTypedCombinedLaneAndExactArtifactContract()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"openclaw-crabbox-upgrade-plan-{Guid.NewGuid():N}");
        var sha256 = new string('a', 64);
        try
        {
            var result = RunCrabboxPlan(
                "CombinedInstalledSmoke",
                artifactRoot,
                "publisher:offer:sku:version",
                "Upgrade",
                "v0.6.12",
                sha256);

            Assert.Equal(0, result.ExitCode);
            using var manifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(artifactRoot, "crabbox-smoke-manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal("Upgrade", root.GetProperty("validationLane").GetString());
            Assert.Equal("v0.6.12", root.GetProperty("previousRelease").GetString());
            Assert.Equal(sha256, root.GetProperty("previousInstallerSha256").GetString());
            Assert.Equal(
                "combined-native-desktop-wsl2-upgrade-smoke",
                root.GetProperty("proofClass").GetString());

            var remoteScript = File.ReadAllText(root.GetProperty("remoteScriptPath").GetString()!);
            Assert.Contains("validate-inno-upgrade-smoke.ps1", remoteScript);
            Assert.Contains("Get-Command pwsh.exe", remoteScript);
            Assert.Contains("\"-PreviousRelease\", \"v0.6.12\"", remoteScript);
            Assert.Contains($"\"-PreviousInstallerSha256\", \"{sha256}\"", remoteScript);
            Assert.Contains("\"-ConfirmCleanMachineReleaseIdentity\"", remoteScript);
            Assert.Contains("cleanupCompleted", remoteScript);
            Assert.Contains("\"acquire-previous\"", remoteScript);
            Assert.Contains("\"state-preservation\"", remoteScript);
            Assert.Contains("installed-runtime-proof\\phase-status.json", remoteScript);
            Assert.Contains("\"upgrade-smoke.log\"", remoteScript);
            Assert.Contains("\"upgrade-smoke.done\"", remoteScript);
            Assert.Contains("\"inno-install-previous.log\"", remoteScript);
            Assert.Contains("\"inno-install-current.log\"", remoteScript);
            Assert.DoesNotContain("PreviousInstallerPath", remoteScript);
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("CombinedInstalledSmoke", "", "", "requires -PreviousRelease")]
    [InlineData("CombinedInstalledSmoke", "v0.6.12", "bad-sha", "requires -PreviousInstallerSha256")]
    [InlineData("NativeDesktopComponent", "v0.6.12", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "requires CombinedInstalledSmoke")]
    [InlineData("Wsl2Component", "v0.6.12", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "requires CombinedInstalledSmoke")]
    public void CrabboxUpgradePlan_FailsClosedForMissingInputsOrComponentMode(
        string mode,
        string previousRelease,
        string previousInstallerSha256,
        string expectedError)
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"openclaw-crabbox-invalid-upgrade-{Guid.NewGuid():N}");
        try
        {
            var azureImage = mode == "CombinedInstalledSmoke" ? "publisher:offer:sku:version" : null;
            var result = RunCrabboxPlan(
                mode,
                artifactRoot,
                azureImage,
                "Upgrade",
                previousRelease,
                previousInstallerSha256);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expectedError, result.Stderr);
            Assert.False(File.Exists(Path.Combine(artifactRoot, "crabbox-smoke-manifest.json")));
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CrabboxCombinedPlan_RequiresExplicitManagedImage()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"openclaw-crabbox-plan-{Guid.NewGuid():N}");
        try
        {
            var result = RunCrabboxPlan("CombinedInstalledSmoke", artifactRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("requires -AzureImage", result.Stderr);
            Assert.False(File.Exists(Path.Combine(artifactRoot, "crabbox-smoke-manifest.json")));
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CrabboxInstaller_UsesOfficialImmutableUserLocalIntegrityContract()
    {
        var script = ReadScript("Install-Crabbox.ps1");

        Assert.Contains("https://api.github.com/repos/openclaw/crabbox", script);
        Assert.Contains("InstallRoot must stay under LOCALAPPDATA", script);
        Assert.Contains("Refusing to install a non-immutable release.", script);
        Assert.Contains("checksums.txt", script);
        Assert.Contains("provenance.json", script);
        Assert.Contains("Get-AuthenticodeSignature", script);
        Assert.Contains("without changing PATH or execution policy", script);
    }

    [Fact]
    public void Documentation_DoesNotMergeWsl2AndNativeProof()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "CLEAN_WINDOWS_RUNNERS.md"));

        Assert.Contains("Separate native and WSL2 component leases do not prove one end-to-end host", docs);
        Assert.Contains("CombinedInstalledSmoke", docs);
        Assert.Contains("same host", docs);
        Assert.Contains("Azure Windows ARM64 WSL2 is unsupported", docs);
        Assert.Contains("Do not describe digest verification as a Windows code signature.", docs);
        Assert.Contains("actual provider, target, proof mode", docs);
        Assert.Contains("long-lived static Crabbox Windows host is not", docs);
        Assert.Contains("config.cmd --ephemeral --disableupdate", docs);
        Assert.Contains("pre-registration Hyper-V checkpoint or", docs);
        Assert.Contains("Always finalize by destroying the VM and its credentials", docs);
        Assert.Contains("Retry only classified", docs);
        Assert.Contains("primary acceptance consumer", docs);
        Assert.Contains(@"HKCU:\Software\Classes\openclaw", docs);
        Assert.Contains("Never delete or pre-clean", docs);
        Assert.Contains("-ValidationLane Upgrade", docs);
        Assert.Contains("-ConfirmCleanMachineReleaseIdentity", docs);
        Assert.Contains("installed-runtime-proof\\phase-status.json", docs);
        Assert.Contains("does not expose", docs);
        Assert.DoesNotContain("parallels-windows-vm", docs, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(Root, "scripts", "clean-windows", name));

    private static ProcessResult RunCrabboxPlan(
        string mode,
        string artifactRoot,
        string? azureImage = null,
        string? validationLane = null,
        string? previousRelease = null,
        string? previousInstallerSha256 = null)
    {
        Directory.CreateDirectory(artifactRoot);
        var script = Path.Combine(Root, "scripts", "clean-windows", "Invoke-CrabboxWindowsSmoke.ps1");
        var fakeCrabbox = Path.Combine(Path.GetPathRoot(Root)!, "not-installed", "crabbox.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            script,
            "-CrabboxPath",
            fakeCrabbox,
            "-Mode",
            mode,
            "-RepoRoot",
            Root,
            "-ArtifactRoot",
            artifactRoot,
            "-PlanOnly",
        };
        if (azureImage is not null)
        {
            arguments.Add("-AzureImage");
            arguments.Add(azureImage);
        }
        if (validationLane is not null)
        {
            arguments.Add("-ValidationLane");
            arguments.Add(validationLane);
        }
        if (previousRelease is not null)
        {
            arguments.Add("-PreviousRelease");
            arguments.Add(previousRelease);
        }
        if (previousInstallerSha256 is not null)
        {
            arguments.Add("-PreviousInstallerSha256");
            arguments.Add(previousInstallerSha256);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
