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
        Assert.Contains("finally {", script);
        Assert.Contains("Restore-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath", script);
    }

    [Theory]
    [InlineData("NativeDesktop", "normal", true)]
    [InlineData("Wsl2", "wsl2", false)]
    public void CrabboxPlan_UsesExplicitAzureWindowsContract(
        string mode,
        string windowsMode,
        bool expectsDesktop)
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"openclaw-crabbox-plan-{Guid.NewGuid():N}");
        try
        {
            var result = RunCrabboxPlan(mode, artifactRoot);

            Assert.Equal(0, result.ExitCode);
            using var manifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(artifactRoot, "crabbox-smoke-manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal("azure", root.GetProperty("provider").GetString());
            Assert.Equal("windows", root.GetProperty("target").GetString());
            Assert.Equal(windowsMode, root.GetProperty("windowsMode").GetString());

            var commands = root.GetProperty("commands");
            Assert.Contains("doctor --provider azure --target windows", commands.GetProperty("doctor").GetString());
            Assert.Contains(
                $"warmup --provider azure --target windows --windows-mode {windowsMode}",
                commands.GetProperty("warmup").GetString());
            Assert.Contains(
                $"run --provider azure --target windows --windows-mode {windowsMode}",
                commands.GetProperty("run").GetString());
            Assert.Contains(
                $"stop --provider azure --target windows --windows-mode {windowsMode}",
                commands.GetProperty("stop").GetString());
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
        Assert.Contains("$remoteScriptContent -replace \"`r`n\", \"`n\"", script);
        Assert.Contains("\"cp\", \"--provider\", $Provider, \"--id\", \"<lease-id>\"", script);
        Assert.Contains("phase-status.json", script);
        Assert.Contains("} finally {", script);
        Assert.Contains("$manifest.execution.stop.attempted = $true", script);
        Assert.Contains("if ($stopFailure)", script);
        Assert.Contains("lease cleanup failed", script);
    }

    [Fact]
    public void CrabboxController_FailsClosedForCombinedOrMislabelledProof()
    {
        var script = ReadScript("Invoke-CrabboxWindowsSmoke.ps1");

        Assert.Contains("Combined NativeDesktop and Wsl2 on one lease is not supported.", script);
        Assert.Contains("Wsl2 mode cannot satisfy a UI proof request.", script);
        Assert.Contains("Wsl2 mode cannot be labeled as native proof.", script);
        Assert.Contains("validate-installed-inno-smoke.ps1", script);
        Assert.Contains("WSL2 capability probe passed", script);
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

        Assert.Contains("separate native and WSL2 leases do not prove one end-to-end host", docs);
        Assert.Contains("fails closed", docs);
        Assert.Contains("Do not describe digest verification as a Windows code signature.", docs);
        Assert.Contains("actual provider, target, mode, raw lease", docs);
        Assert.DoesNotContain("parallels-windows-vm", docs, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(Root, "scripts", "clean-windows", name));

    private static ProcessResult RunCrabboxPlan(string mode, string artifactRoot)
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
        foreach (var argument in new[]
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
        })
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
