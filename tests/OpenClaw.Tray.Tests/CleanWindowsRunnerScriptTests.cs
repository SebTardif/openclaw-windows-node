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
    public void HyperVController_UsesCanonicalWindowsSecureBootTemplateAndValidatesEffectiveTemplate()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var normalizeStart = script.IndexOf(
            "function Normalize-SecureBootTemplate",
            StringComparison.Ordinal);
        var normalizeEnd = script.IndexOf(
            "function Get-PropertyValueOrNull",
            normalizeStart,
            StringComparison.Ordinal);
        var normalization = script[normalizeStart..normalizeEnd];
        var verifyStart = script.IndexOf(
            "function Verify-HostVmConfiguration",
            StringComparison.Ordinal);
        var verifyEnd = script.IndexOf(
            "function Invoke-PreparedGuestOperation",
            verifyStart,
            StringComparison.Ordinal);
        var verification = script[verifyStart..verifyEnd];

        Assert.Contains("$script:WindowsSecureBootTemplate = \"MicrosoftWindows\"", script);
        Assert.Contains("-SecureBootTemplate $script:WindowsSecureBootTemplate", script);
        Assert.DoesNotContain("-SecureBootTemplate \"Microsoft Windows\"", script);
        Assert.Contains("[regex]::Replace([string]$Value, \"\\s\", \"\")", normalization);
        Assert.Contains("$withoutWhitespace.ToLowerInvariant()", normalization);
        Assert.Contains(
            "Normalize-SecureBootTemplate -Value $secureBootTemplateValue",
            verification);
        Assert.Contains(
            "Normalize-SecureBootTemplate -Value $script:WindowsSecureBootTemplate",
            verification);
        Assert.Contains("attempted canonical identifier '$script:WindowsSecureBootTemplate'", verification);
        Assert.Contains("Host-reported SecureBootTemplate value", verification);
        Assert.Contains(
            "$firmware.PSObject.Properties[\"SecureBootTemplateId\"]",
            verification);
        Assert.Contains("[Guid]::TryParse(", verification);
        Assert.Contains("$parsedSecureBootTemplateId -eq [Guid]::Empty", verification);
        Assert.Contains("Host-reported SecureBootTemplateId value", verification);
        Assert.DoesNotContain("-notmatch \"Microsoft\"", verification);
    }

    [Fact]
    public void HyperVController_KeyProtectorPredicateRejectsEveryFourByteHostSentinel()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var predicateStart = script.IndexOf(
            "function Test-KeyProtectorPresent",
            StringComparison.Ordinal);
        var predicateEnd = script.IndexOf(
            "function Get-PropertyValueOrNull",
            predicateStart,
            StringComparison.Ordinal);
        var predicate = script[predicateStart..predicateEnd];
        var proofScript = string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            predicate,
            "\n$results = @(\n",
            "    Test-KeyProtectorPresent -KeyProtector ([object[]]@([byte]0, [byte]0, [byte]0, [byte]1))\n",
            "    Test-KeyProtectorPresent -KeyProtector ([byte[]]@(17, 42, 128, 255))\n",
            "    Test-KeyProtectorPresent -KeyProtector $null\n",
            "    Test-KeyProtectorPresent -KeyProtector ([byte[]]@())\n",
            "    Test-KeyProtectorPresent -KeyProtector ([byte[]]@(1, 2, 3, 4, 5))\n",
            ")\n",
            "[Console]::Out.Write(($results -join ','))\n");

        Assert.Contains("$keyProtectorBytes.Count -le 4", predicate);
        Assert.DoesNotContain("Write-", predicate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Out-", predicate, StringComparison.OrdinalIgnoreCase);

        var result = RunPowerShellCommand(proofScript);

        Assert.True(
            result.ExitCode == 0,
            $"Key protector predicate proof failed.\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        Assert.Equal("False,False,False,False,True", result.Stdout);
        Assert.DoesNotContain("0,0,0,1", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("17,42,128,255", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_RepairsOnlyConfirmedOwnedOffPartialVmBeforeStarting()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var seamStart = script.IndexOf(
            "function Set-OwnedVmSecurityConfiguration",
            StringComparison.Ordinal);
        var seamEnd = script.IndexOf("function New-OwnedHyperVVm", seamStart, StringComparison.Ordinal);
        var securitySeam = script[seamStart..seamEnd];
        var freshStart = seamEnd;
        var freshEnd = script.IndexOf(
            "function Invoke-CleanupUnattendCommand",
            freshStart,
            StringComparison.Ordinal);
        var freshCreate = script[freshStart..freshEnd];
        var resumeStart = script.IndexOf(
            "function Invoke-ResumeUnattendedCommand",
            StringComparison.Ordinal);
        var resumeEnd = script.IndexOf("function Invoke-CreateCommand", resumeStart, StringComparison.Ordinal);
        var resume = script[resumeStart..resumeEnd];
        var invokeCreateEnd = script.IndexOf(
            "function Invoke-PrepareCommand",
            resumeEnd,
            StringComparison.Ordinal);
        var invokeCreate = script[resumeEnd..invokeCreateEnd];

        Assert.Contains("Set-OwnedVmSecurityConfiguration", freshCreate);
        Assert.Contains("Assert-OwnedVM", securitySeam);
        Assert.Contains("[string]$ownedVm.State -ne \"Off\"", securitySeam);
        Assert.Contains("Expected exactly one DVD drive for the verified Windows ISO", securitySeam);
        Assert.Contains("-FirstBootDevice $windowsDvdDrive", securitySeam);
        Assert.Contains("-SecureBootTemplate $script:WindowsSecureBootTemplate", securitySeam);
        Assert.Contains("Test-KeyProtectorPresent -KeyProtector $keyProtector", securitySeam);
        Assert.Contains("Hyper-V can report an unset protector as a four-byte host sentinel", script);
        Assert.Contains("Refusing to change an unknown vTPM configuration", securitySeam);

        var getProtectorIndex = securitySeam.IndexOf("Get-VMKeyProtector", StringComparison.Ordinal);
        var testProtectorIndex = securitySeam.IndexOf(
            "Test-KeyProtectorPresent -KeyProtector $keyProtector",
            StringComparison.Ordinal);
        var missingProtectorGuardIndex = securitySeam.IndexOf(
            "if (-not $hasKeyProtector)",
            StringComparison.Ordinal);
        var setProtectorIndex = securitySeam.IndexOf("Set-VMKeyProtector", StringComparison.Ordinal);
        var rereadProtectorIndex = securitySeam.IndexOf(
            "Get-VMKeyProtector",
            setProtectorIndex,
            StringComparison.Ordinal);
        var retestProtectorIndex = securitySeam.IndexOf(
            "Test-KeyProtectorPresent -KeyProtector $keyProtector",
            rereadProtectorIndex,
            StringComparison.Ordinal);
        var invalidNewProtectorGuardIndex = securitySeam.IndexOf(
            "if (-not $hasKeyProtector)",
            retestProtectorIndex,
            StringComparison.Ordinal);
        var invalidNewProtectorFailureIndex = securitySeam.IndexOf(
            "Hyper-V did not report a valid local key protector",
            invalidNewProtectorGuardIndex,
            StringComparison.Ordinal);
        var setFirmwareIndex = securitySeam.IndexOf("Set-VMFirmware", StringComparison.Ordinal);
        var getSecurityIndex = securitySeam.IndexOf("Get-VMSecurity", StringComparison.Ordinal);
        var disabledTpmGuardIndex = securitySeam.IndexOf(
            "if (-not [bool]$tpmEnabled)",
            StringComparison.Ordinal);
        var enableTpmIndex = securitySeam.IndexOf("Enable-VMTPM", StringComparison.Ordinal);

        Assert.True(getProtectorIndex >= 0);
        Assert.True(testProtectorIndex > getProtectorIndex);
        Assert.True(missingProtectorGuardIndex > testProtectorIndex);
        Assert.True(setProtectorIndex > missingProtectorGuardIndex);
        Assert.True(rereadProtectorIndex > setProtectorIndex);
        Assert.True(retestProtectorIndex > rereadProtectorIndex);
        Assert.True(invalidNewProtectorGuardIndex > retestProtectorIndex);
        Assert.True(invalidNewProtectorFailureIndex > invalidNewProtectorGuardIndex);
        Assert.True(setFirmwareIndex > invalidNewProtectorFailureIndex);
        Assert.True(getSecurityIndex > setFirmwareIndex);
        Assert.True(disabledTpmGuardIndex > getSecurityIndex);
        Assert.True(enableTpmIndex > disabledTpmGuardIndex);
        Assert.Contains(
            """
            if (-not $hasKeyProtector) {
                    Set-VMKeyProtector -VMName $VMName -NewLocalKeyProtector -ErrorAction Stop | Out-Null
                    $keyProtector = Get-VMKeyProtector -VMName $VMName -ErrorAction Stop
                    $hasKeyProtector = Test-KeyProtectorPresent -KeyProtector $keyProtector
                    if (-not $hasKeyProtector) {
                        throw "Hyper-V did not report a valid local key protector for exact owned VM '$VMName' after creating one. Refusing to configure firmware or vTPM."
                    }
                }
            """,
            securitySeam);
        Assert.Single(
            securitySeam.Split('\n'),
            line => line.Contains("Set-VMKeyProtector", StringComparison.Ordinal));

        var freshSecurityIndex = freshCreate.IndexOf(
            "Set-OwnedVmSecurityConfiguration",
            StringComparison.Ordinal);
        var freshReverifyIndex = freshCreate.IndexOf(
            "Verify-HostVmConfiguration",
            freshSecurityIndex,
            StringComparison.Ordinal);
        var freshReturnIndex = freshCreate.IndexOf("return $vm", freshReverifyIndex, StringComparison.Ordinal);
        Assert.True(freshSecurityIndex >= 0);
        Assert.True(freshReverifyIndex > freshSecurityIndex);
        Assert.True(freshReturnIndex > freshReverifyIndex);
        Assert.Equal(2, invokeCreate.Split('\n').Count(
            line => line.Contains("$vm = New-OwnedHyperVVm", StringComparison.Ordinal)));
        Assert.Equal(2, invokeCreate.Split('\n').Count(
            line => line.Contains("Start-VM -Name $VMName", StringComparison.Ordinal)));
        Assert.All(
            invokeCreate
                .Split("$vm = New-OwnedHyperVVm", StringSplitOptions.None)
                .Skip(1),
            freshPath => Assert.True(
                freshPath.IndexOf("Start-VM -Name $VMName", StringComparison.Ordinal) >= 0,
                "Every fresh VM path must reuse New-OwnedHyperVVm, which fully verifies the shared security seam before start."));

        var stateIndex = resume.IndexOf("Read-OwnedUnattendState", StringComparison.Ordinal);
        var ownedIndex = resume.IndexOf("Assert-OwnedVM", stateIndex, StringComparison.Ordinal);
        var markerIndex = resume.IndexOf("Assert-UnattendStateMatchesVmMarker", ownedIndex, StringComparison.Ordinal);
        var initialRepairFlagIndex = resume.IndexOf(
            "$repairedPreFirstStartSecurityConfiguration = $false",
            markerIndex,
            StringComparison.Ordinal);
        var initialVerifyIndex = resume.IndexOf(
            "Verify-HostVmConfiguration",
            initialRepairFlagIndex,
            StringComparison.Ordinal);
        var failedVerificationIndex = resume.IndexOf(
            "if ($null -ne $configurationVerificationError)",
            initialVerifyIndex,
            StringComparison.Ordinal);
        var offGuardIndex = resume.IndexOf("[string]$vm.State -ne \"Off\"", failedVerificationIndex, StringComparison.Ordinal);
        var confirmationIndex = resume.IndexOf(
            "Assert-ConfirmationForOwnedAction",
            offGuardIndex,
            StringComparison.Ordinal);
        var repairIndex = resume.IndexOf(
            "Set-OwnedVmSecurityConfiguration",
            confirmationIndex,
            StringComparison.Ordinal);
        var reverifyIndex = resume.IndexOf(
            "Verify-HostVmConfiguration",
            repairIndex,
            StringComparison.Ordinal);
        var repairedFlagIndex = resume.IndexOf(
            "$repairedPreFirstStartSecurityConfiguration = $true",
            reverifyIndex,
            StringComparison.Ordinal);
        var ensureRunningIndex = resume.IndexOf("Ensure-VMRunning", repairedFlagIndex, StringComparison.Ordinal);
        var repairedConditionIndex = resume.IndexOf(
            "if ($repairedPreFirstStartSecurityConfiguration)",
            ensureRunningIndex,
            StringComparison.Ordinal);
        var opticalBootKeyIndex = resume.IndexOf(
            "Invoke-UnattendedOpticalBootKey -VmObject $vm",
            repairedConditionIndex,
            StringComparison.Ordinal);

        Assert.True(stateIndex >= 0);
        Assert.True(ownedIndex > stateIndex);
        Assert.True(markerIndex > ownedIndex);
        Assert.True(initialRepairFlagIndex > markerIndex);
        Assert.True(initialVerifyIndex > initialRepairFlagIndex);
        Assert.True(failedVerificationIndex > initialVerifyIndex);
        Assert.True(offGuardIndex > failedVerificationIndex);
        Assert.True(confirmationIndex > offGuardIndex);
        Assert.True(repairIndex > confirmationIndex);
        Assert.True(reverifyIndex > repairIndex);
        Assert.True(repairedFlagIndex > reverifyIndex);
        Assert.True(ensureRunningIndex > repairedFlagIndex);
        Assert.True(repairedConditionIndex > ensureRunningIndex);
        Assert.True(opticalBootKeyIndex > repairedConditionIndex);
        Assert.Single(
            resume.Split('\n'),
            line => line.Contains("Set-OwnedVmSecurityConfiguration", StringComparison.Ordinal));
        Assert.Single(
            resume.Split('\n'),
            line => line.Contains(
                "$repairedPreFirstStartSecurityConfiguration = $true",
                StringComparison.Ordinal));
        Assert.Contains("-ResolvedWindowsIsoPath ([string]$state.windowsIsoPath)", resume);
        Assert.DoesNotContain("Stop-VM", resume, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVRecoveryDocs_UseTypedRecoveryForCompletedMarkerlessCheckpoint()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "CLEAN_WINDOWS_RUNNERS.md"));
        var skill = File.ReadAllText(
            Path.Combine(Root, ".agents", "skills", "openclaw-hyperv-smoke", "SKILL.md"));
        var routing = File.ReadAllText(
            Path.Combine(Root, ".agents", "skills", "windows-node-testing", "SKILL.md"));
        var exactResumeCommand =
            @"$createResult = .\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Create -ResumeUnattended -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -ConfirmOwnedAction";
        var exactPrepareCommand =
            @".\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Prepare -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -CredentialPath $credentialPath -RecoverPendingCheckpoint -ConfirmOwnedAction";

        foreach (var guidance in new[] { docs, skill })
        {
            Assert.Contains(exactResumeCommand, guidance);
            Assert.Contains("$credentialPath = $createResult.CredentialPath", guidance);
            Assert.Contains(exactPrepareCommand, guidance);
            Assert.Contains("completed unattended", guidance, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("final rotated", guidance);
            Assert.Contains("marker", guidance);
            Assert.Contains("exact", guidance);
            Assert.Contains("creation time", guidance);
            Assert.Contains("without", guidance);
            Assert.Contains("delet", guidance);
            Assert.Contains("continues", guidance);
            Assert.Contains("Do not use `-CleanupUnattend`", guidance);
            Assert.Contains("reinstall", guidance);
            Assert.Contains("delete", guidance);
            Assert.Contains("ad hoc", guidance);
            Assert.Contains("snapshot", guidance);
        }
        Assert.Contains("-RecoverPendingCheckpoint -ConfirmOwnedAction", routing);
        Assert.Contains("Prepare-only", routing);
        Assert.Contains("do not issue ad hoc checkpoint commands", routing);
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

    [Fact]
    public void HyperVController_CheckpointCreationWritesPendingIntentBeforeMutationAndFinalizesAfterObservation()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var helper = ReadScript("CleanWindowsUnattend.psm1");
        var creation = ExtractPowerShellFunction(
            controller,
            "New-OwnedCheckpoint",
            "Get-UnattendedCompletionProof");

        var pendingIndex = creation.IndexOf("New-PendingCheckpointMarker", StringComparison.Ordinal);
        var pendingWriteIndex = creation.IndexOf(
            "Write-MarkerFile -Path $markerPath -Marker $pendingMarker",
            pendingIndex,
            StringComparison.Ordinal);
        var checkpointIndex = creation.IndexOf("Checkpoint-VM", StringComparison.Ordinal);
        var observationIndex = creation.IndexOf(
            "Wait-ForOwnedCheckpointObservation",
            checkpointIndex,
            StringComparison.Ordinal);
        var completeIndex = creation.IndexOf(
            "New-CompletedCheckpointMarker",
            observationIndex,
            StringComparison.Ordinal);
        var completeWriteIndex = creation.IndexOf(
            "Write-MarkerFile -Path $markerPath -Marker $completedMarker",
            completeIndex,
            StringComparison.Ordinal);

        Assert.Contains(
            "$script:CheckpointMarkerSchema = \"openclaw.clean-windows.checkpoint-owner/v2\"",
            controller);
        Assert.Contains("status = \"pending\"", controller);
        Assert.Contains("operationNonce = $nonce", controller);
        Assert.Contains("creationStartedUtc = $startedUtc", controller);
        Assert.True(pendingIndex >= 0);
        Assert.True(pendingWriteIndex > pendingIndex);
        Assert.True(checkpointIndex > pendingWriteIndex);
        Assert.True(observationIndex > checkpointIndex);
        Assert.True(completeIndex > observationIndex);
        Assert.True(completeWriteIndex > completeIndex);
        Assert.Contains("The pending intent remains", creation);
        Assert.DoesNotContain("Remove-Item", creation, StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::Replace($temporaryPath, $resolvedPath, [NullString]::Value)",
            helper);
        Assert.Contains("[IO.File]::Move($temporaryPath, $resolvedPath)", helper);
        Assert.Contains("Set-CleanWindowsRestrictiveAcl -Path $temporaryPath", helper);
        Assert.Contains("Assert-CleanWindowsRestrictiveAcl -Path $temporaryPath", helper);
        Assert.Contains("Assert-CleanWindowsRestrictiveAcl -Path $resolvedOwnedRoot", helper);
    }

    [Theory]
    [InlineData("powershell.exe", "Desktop")]
    [InlineData("pwsh.exe", "Core")]
    public void OwnedJsonWriter_ReplacesPendingMarkerWithCompleteMarkerInRealShell(
        string shell,
        string expectedEdition)
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"owned-json-writer-{Guid.NewGuid():N}");
        try
        {
            var result = RunOwnedJsonWriterProof(shell, ownedRoot);

            AssertPowerShellProofSucceeded(result);
            var jsonLine = result.Stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Last();
            using var proof = JsonDocument.Parse(jsonLine);
            var root = proof.RootElement;
            Assert.Equal(expectedEdition, root.GetProperty("edition").GetString());
            Assert.Equal("pending", root.GetProperty("firstStatus").GetString());
            Assert.Equal("complete", root.GetProperty("secondStatus").GetString());
            Assert.Equal(2, root.GetProperty("generation").GetInt32());
            Assert.True(root.GetProperty("directoryAclVerified").GetBoolean());
            Assert.True(root.GetProperty("fileAclVerified").GetBoolean());
            Assert.Equal(0, root.GetProperty("temporaryFileCount").GetInt32());
            Assert.True(root.GetProperty("targetExistedBeforeCleanup").GetBoolean());
            Assert.False(Directory.Exists(ownedRoot));
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void HyperVController_TransactionalCheckpointMarkerBindsExactObservedIdentity()
    {
        var result = RunPowerShellCommand(BuildCheckpointMarkerProof());

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var marker = document.RootElement;
        Assert.Equal("openclaw.clean-windows.checkpoint-owner/v2", marker.GetProperty("schema").GetString());
        Assert.Equal("complete", marker.GetProperty("status").GetString());
        Assert.Equal("clean-windows", marker.GetProperty("checkpointName").GetString());
        Assert.Equal("owner", marker.GetProperty("ownerId").GetString());
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            marker.GetProperty("vmId").GetString());
        Assert.Equal(
            "22222222-2222-2222-2222-222222222222",
            marker.GetProperty("snapshotId").GetString());
        Assert.True(marker.TryGetProperty("operationNonce", out _));
        Assert.True(marker.TryGetProperty("creationStartedUtc", out _));
        Assert.True(marker.TryGetProperty("snapshotCreationTimeUtc", out _));
    }

    [Theory]
    [InlineData(
        "$script:getCall++; return @($snapshot)",
        "1")]
    [InlineData(
        "$script:getCall++; if ($script:getCall -lt 3) { return @() }; return @($snapshot)",
        "3")]
    public void HyperVController_CheckpointObservationAcceptsImmediateAndDelayedAppearance(
        string getSnapshotBody,
        string expectedCalls)
    {
        var proof = BuildCheckpointObservationProof(
            getSnapshotBody,
            """
            $result = Wait-ForOwnedCheckpointObservation `
                -OwnedCheckpointName 'clean-windows' `
                -VmObject $vm `
                -TimeoutSec 1 `
                -PollIntervalMilliseconds 10
            [Console]::Out.Write("$script:getCall|$($result.Id)")
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Equal($"{expectedCalls}|22222222-2222-2222-2222-222222222222", result.Stdout);
    }

    [Fact]
    public void HyperVController_CheckpointObservationRejectsDuplicatesImmediately()
    {
        var proof = BuildCheckpointObservationProof(
            "$script:getCall++; return @($snapshot, $snapshot)",
            """
            try {
                Wait-ForOwnedCheckpointObservation `
                    -OwnedCheckpointName 'clean-windows' `
                    -VmObject $vm `
                    -TimeoutSec 1 `
                    -PollIntervalMilliseconds 10 | Out-Null
                throw 'Expected duplicate refusal.'
            } catch {
                [Console]::Out.Write("$script:getCall|$($_.Exception.Message)")
            }
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.StartsWith("1|", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("multiple checkpoints named 'clean-windows'", result.Stdout);
    }

    [Fact]
    public void HyperVController_CheckpointCreationFinalizesOnlyAfterObservedSnapshot()
    {
        var result = RunPowerShellCommand(BuildCheckpointCreationProof(observationFails: false));

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(
            "pending,write:pending,checkpoint,observe,finalize,assert:complete,write:complete|complete",
            result.Stdout);
    }

    [Fact]
    public void HyperVController_CheckpointTimeoutRetainsPendingIntent()
    {
        var result = RunPowerShellCommand(BuildCheckpointCreationProof(observationFails: true));

        AssertPowerShellProofSucceeded(result);
        Assert.StartsWith(
            "pending,write:pending,checkpoint,observe|pending|",
            result.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("pending intent remains", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("write:complete", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "finalize",
            result.Stdout.Split('|')[0],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Duplicate", "duplicate checkpoints named 'clean-windows'")]
    [InlineData("WrongName", "another checkpoint name")]
    [InlineData("WrongVm", "exact owned VM id")]
    [InlineData("BeforeCompletion", "predates completed unattended installation")]
    [InlineData("OutsideLegacyWindow", "outside the conservative recovery window")]
    [InlineData("OutsidePendingWindow", "outside the pending operation window")]
    public void HyperVController_CheckpointRecoveryCandidateRefusesAmbiguousIdentityOrTime(
        string scenario,
        string expectedError)
    {
        var result = RunPowerShellCommand(BuildCheckpointRecoveryCandidateProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedError, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_EmptyCheckpointRecoveryCollectionEmitsDomainErrorWithoutMutation()
    {
        var result = RunPowerShellCommand(BuildCheckpointRecoveryCandidateProof("Empty"));
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var candidate = ExtractPowerShellFunction(
            controller,
            "Get-RecoverableCheckpointCandidate",
            "Recover-PendingOwnedCheckpoint");

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(
            "Checkpoint recovery requires exactly one checkpoint named 'clean-windows'.|pending|unchanged|0",
            result.Stdout);
        Assert.Contains("[AllowEmptyCollection()]", candidate);
        Assert.DoesNotContain("Checkpoint-VM", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-VMSnapshot", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore-VMSnapshot", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-MarkerFile", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("$Marker.status =", candidate, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_CheckpointRecoveryRequiresAllOwnershipAndCompletionGates()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var prepare = ExtractPowerShellFunction(
            controller,
            "Invoke-PrepareCommand",
            "Invoke-VerifyCommand");
        var recovery = ExtractPowerShellFunction(
            controller,
            "Recover-PendingOwnedCheckpoint",
            "Wait-ForVmState");
        var completionProof = ExtractPowerShellFunction(
            controller,
            "Get-UnattendedCompletionProof",
            "Get-RecoverableCheckpointCandidate");

        var ownerIndex = prepare.IndexOf("Assert-OwnedVM", StringComparison.Ordinal);
        var credentialIndex = prepare.IndexOf("Resolve-OperationCredential", StringComparison.Ordinal);
        var recoveryIndex = prepare.IndexOf("Recover-PendingOwnedCheckpoint", StringComparison.Ordinal);
        var cleanLookupIndex = prepare.IndexOf(
            "Get-SingleCheckpoint -OwnedCheckpointName $script:CleanCheckpointName",
            recoveryIndex,
            StringComparison.Ordinal);
        var newCleanIndex = prepare.IndexOf(
            "New-OwnedCheckpoint",
            cleanLookupIndex,
            StringComparison.Ordinal);
        var preparedRemovalIndex = prepare.IndexOf(
            "Remove-OwnedCheckpointIfPresent",
            newCleanIndex,
            StringComparison.Ordinal);

        Assert.True(ownerIndex >= 0);
        Assert.True(credentialIndex > ownerIndex);
        Assert.True(recoveryIndex > credentialIndex);
        Assert.True(cleanLookupIndex > recoveryIndex);
        Assert.True(newCleanIndex > cleanLookupIndex);
        Assert.True(preparedRemovalIndex > newCleanIndex);
        Assert.Contains("if ($RecoverPendingCheckpoint)", prepare);
        Assert.Contains("Read-OwnedUnattendState", recovery);
        Assert.Contains("Assert-UnattendStateMatchesVmMarker", recovery);
        Assert.Contains("Get-UnattendedCompletionProof", recovery);
        Assert.Contains("-CredentialPath $paths.FinalCredentialPath", recovery);
        Assert.Contains("-ExpectedKind \"final\"", recovery);
        Assert.Contains("Get-RecoverableCheckpointCandidate", recovery);
        Assert.Contains("legacy-markerless-completed-unattended", recovery);
        Assert.Contains("already has a finalized ownership marker", recovery);
        Assert.Contains("Checkpoint recovery requires unattended state status 'complete'", completionProof);
        Assert.Contains("completedUtc", completionProof);
        Assert.Contains("LastWriteTimeUtc", completionProof);
        Assert.DoesNotContain("Checkpoint-VM", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-VMSnapshot", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-OwnedCheckpointIfPresent", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_MarkerlessRecoveryFinalizesWithoutSnapshotMutation()
    {
        var result = RunPowerShellCommand(BuildMarkerlessCheckpointRecoveryProof());

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(
            "confirm,paths,state,bind,completion,credential,read-marker,snapshots,candidate,pending,complete,assert,write,info|complete|legacy-markerless-completed-unattended",
            result.Stdout);
    }

    [Fact]
    public void HyperVController_LegacyCompletedResumePreservesOriginalCompletionBound()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var setStatus = ExtractPowerShellFunction(
            controller,
            "Set-UnattendStateStatus",
            "Assert-WindowsIsoHash");
        var proof = string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$state = [pscustomobject]@{ status = 'complete'; updatedUtc = '2026-07-27T08:00:00Z' }\n",
            "$paths = [pscustomobject]@{ StatePath = 'C:\\owned\\unattend.owner.json' }\n",
            "function Get-UnattendedCompletionProof { param([object]$State, [string]$StatePath) return [pscustomobject]@{ CompletionUtc = [DateTime]'2026-07-27T08:00:01Z' } }\n",
            "function Write-UnattendState { param([object]$State, [object]$Paths) }\n",
            setStatus,
            "\nSet-UnattendStateStatus -State $state -Paths $paths -Status 'complete'\n",
            "[Console]::Out.Write($state.completedUtc)\n");

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-27T08:00:01Z").UtcDateTime,
            DateTimeOffset.Parse(result.Stdout).UtcDateTime);
        Assert.Contains(
            "$updatedUtc -lt $completedUtc.AddSeconds(-$script:CheckpointRecoveryClockSkewSec)",
            controller);
        Assert.Contains(
            "$completionUtc = if ($stateFileUtc -gt $updatedUtc)",
            controller);
    }

    [Fact]
    public void HyperVController_PendingMarkersNeverAuthorizeCheckpointRemovalOrRestore()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var assertion = ExtractPowerShellFunction(
            controller,
            "Assert-OwnedCheckpoint",
            "Remove-OwnedCheckpointIfPresent");
        var removal = ExtractPowerShellFunction(
            controller,
            "Remove-OwnedCheckpointIfPresent",
            "New-OwnedCheckpoint");
        var restore = ExtractPowerShellFunction(
            controller,
            "Restore-OwnedCheckpoint",
            "Open-GuestSession");

        Assert.Contains("Assert-FinalizedCheckpointMarkerMatches", assertion);
        Assert.Contains("-ExpectedStatus \"complete\"", controller);
        Assert.True(
            removal.IndexOf("Assert-OwnedCheckpoint", StringComparison.Ordinal) <
            removal.IndexOf("Remove-VMSnapshot", StringComparison.Ordinal));
        Assert.True(
            restore.IndexOf("Assert-OwnedCheckpoint", StringComparison.Ordinal) <
            restore.IndexOf("Restore-VMSnapshot", StringComparison.Ordinal));
        Assert.Contains("Refusing to remove, overwrite, or treat pending state as finalized", removal);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Cleanup")]
    [InlineData("Verify")]
    [InlineData("Smoke")]
    [InlineData("Restore")]
    public void HyperVController_RejectsCheckpointRecoveryOutsidePrepare(string scenario)
    {
        var result = RunCheckpointRecoveryParameterContract(scenario, confirmOwnedAction: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "RecoverPendingCheckpoint is accepted only with -Command Prepare.",
            result.Stderr);
        Assert.DoesNotContain("Run this controller from an elevated PowerShell session", result.Stderr);
    }

    [Fact]
    public void HyperVController_RequiresConfirmationForCheckpointRecovery()
    {
        var result = RunCheckpointRecoveryParameterContract(
            "Prepare",
            confirmOwnedAction: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RecoverPendingCheckpoint requires -ConfirmOwnedAction.", result.Stderr);
        Assert.DoesNotContain("Run this controller from an elevated PowerShell session", result.Stderr);
    }

    [Fact]
    public void HyperVController_DefaultsToStrictUnattendedCreateAndVerifiedOfficialIso()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var helper = ReadScript("CleanWindowsUnattend.psm1");

        Assert.Contains("[string]$CreateMode = \"Unattended\"", controller);
        Assert.Contains("[switch]$GenerateCredential", controller);
        Assert.Contains("[switch]$ResumeUnattended", controller);
        Assert.Contains("[switch]$CleanupUnattend", controller);
        Assert.Contains("[string]$CredentialPath", controller);
        Assert.Contains(
            "A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9",
            controller);
        Assert.Contains("Get-CleanWindowsFileSha256 -Path $ResolvedIsoPath", controller);
        Assert.Contains("Refusing to create the VM", controller);
        Assert.Contains(
            "Unattended Create requires the pinned Windows 11 Enterprise Evaluation ISO SHA256.",
            controller);

        var create = controller[controller.IndexOf("function Invoke-CreateCommand", StringComparison.Ordinal)..];
        Assert.True(
            create.IndexOf("Assert-WindowsIsoHash -ResolvedIsoPath", StringComparison.Ordinal) <
            create.IndexOf("New-OwnedHyperVVm", StringComparison.Ordinal));
        Assert.Contains("Import-Module (Join-Path $PSScriptRoot \"CleanWindowsUnattend.psm1\")", controller);
        Assert.Contains("New-CleanWindowsAnswerIso", controller);
        Assert.Contains("Test-CleanWindowsAnswerIsoMount", controller);
        Assert.Contains("Add-VMDvdDrive -VMName $VMName -Path $ResolvedIsoPath", controller);
        Assert.Contains("Add-VMDvdDrive -VMName $VMName -Path $AnswerIsoPath", controller);
        Assert.Contains("-FirstBootDevice $windowsDvdDrive", controller);
        Assert.Contains("IMAPI2FS.MsftFileSystemImage", helper);
        Assert.Contains("$root.AddTree($resolvedStagingPath, $false)", helper);
        Assert.DoesNotContain("oscdimg", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_MountValidatesAnswerIsoBeforeAnyUnattendedVmOrVhdCreation()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var unattendedStart = controller.IndexOf(
            "$paths = Get-UnattendPaths -ResolvedVhdPath $resolvedVhdPath",
            StringComparison.Ordinal);
        var unattendedEnd = controller.IndexOf(
            "function Invoke-PrepareCommand",
            unattendedStart,
            StringComparison.Ordinal);
        var unattendedCreate = controller[unattendedStart..unattendedEnd];

        var generateIndex = unattendedCreate.IndexOf(
            "New-CleanWindowsAnswerIso",
            StringComparison.Ordinal);
        var validateIndex = unattendedCreate.IndexOf(
            "Test-CleanWindowsAnswerIsoMount",
            StringComparison.Ordinal);
        var createVmIndex = unattendedCreate.IndexOf(
            "$vm = New-OwnedHyperVVm",
            StringComparison.Ordinal);
        Assert.True(generateIndex >= 0);
        Assert.True(validateIndex > generateIndex);
        Assert.True(createVmIndex > validateIndex);
        Assert.Contains("-ExpectedAnswerFilePath $paths.AnswerFilePath", unattendedCreate);
        Assert.Contains("-Status \"answer-media-validated\"", unattendedCreate);
    }

    [Fact]
    public void HyperVController_ClearsGen2OpticalPromptWithBoundedVmIdCimKeyboard()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var functionStart = controller.IndexOf(
            "function Invoke-UnattendedOpticalBootKey",
            StringComparison.Ordinal);
        var functionEnd = controller.IndexOf(
            "function Detach-OwnedInstallationMedia",
            functionStart,
            StringComparison.Ordinal);
        var bootKeyFunction = controller[functionStart..functionEnd];

        Assert.Contains("$script:OpticalBootKeyWindowSec = 7", controller);
        Assert.Contains("$script:OpticalBootInitialDelayMilliseconds = 750", controller);
        Assert.Contains("$script:OpticalBootPulseIntervalMilliseconds = 750", controller);
        Assert.Contains("$script:OpticalBootMaxPulseCount = 9", controller);
        Assert.Contains("\"Get-CimInstance\"", controller);
        Assert.Contains("\"Get-CimAssociatedInstance\"", controller);
        Assert.Contains("\"Invoke-CimMethod\"", controller);
        Assert.Contains("\"root\\virtualization\\v2\"", bootKeyFunction);
        Assert.Contains("\"Msvm_ComputerSystem\"", bootKeyFunction);
        Assert.Contains("([Guid]$VmObject.Id).ToString(\"D\")", bootKeyFunction);
        Assert.Contains("-Filter (\"Name = '{0}'\" -f $vmId)", bootKeyFunction);
        Assert.Contains("\"Msvm_Keyboard\"", bootKeyFunction);
        Assert.Contains("-MethodName \"TypeKey\"", bootKeyFunction);
        Assert.Contains("@{ keyCode = [uint32]0x20 }", bootKeyFunction);
        Assert.Contains("$successfulDeliveries++", bootKeyFunction);
        Assert.Contains("$pulseCount -lt $script:OpticalBootMaxPulseCount", bootKeyFunction);
        Assert.Contains("Start-Sleep -Milliseconds $script:OpticalBootPulseIntervalMilliseconds", bootKeyFunction);
        Assert.Contains("$lastCimError = $_", bootKeyFunction);
        Assert.Contains("Safe diagnostic: $lastDiagnostic", bootKeyFunction);
        Assert.Contains("Last CIM error: $safeLastError", bootKeyFunction);
        Assert.DoesNotContain("$lastCimError.Exception.Message", bootKeyFunction, StringComparison.Ordinal);
        Assert.Contains("fixed boot window", bootKeyFunction);
        Assert.DoesNotContain(
            bootKeyFunction.Split('\n'),
            line => string.Equals(line.Trim(), "return", StringComparison.Ordinal));
        Assert.DoesNotContain("while ($true)", bootKeyFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("catch {\r\n        }", bootKeyFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("$VMName", bootKeyFunction, StringComparison.Ordinal);

        var createStart = controller.IndexOf("function Invoke-CreateCommand", StringComparison.Ordinal);
        var createEnd = controller.IndexOf("function Invoke-PrepareCommand", createStart, StringComparison.Ordinal);
        var createBody = controller[createStart..createEnd];
        var initialStartIndex = createBody.LastIndexOf(
            "Start-VM -Name $VMName -Confirm:$false",
            StringComparison.Ordinal);
        var bootKeyIndex = createBody.IndexOf(
            "Invoke-UnattendedOpticalBootKey -VmObject $vm",
            StringComparison.Ordinal);
        var directWaitIndex = createBody.IndexOf(
            "Complete-UnattendedInstallation",
            bootKeyIndex,
            StringComparison.Ordinal);
        Assert.True(initialStartIndex >= 0);
        Assert.True(bootKeyIndex > initialStartIndex);
        Assert.True(directWaitIndex > bootKeyIndex);

        var manualStart = createBody.IndexOf("if ($CreateMode -eq \"Manual\")", StringComparison.Ordinal);
        var unattendedStart = createBody.IndexOf(
            "$paths = Get-UnattendPaths",
            manualStart,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Invoke-UnattendedOpticalBootKey",
            createBody[manualStart..unattendedStart],
            StringComparison.Ordinal);
        var resumeStart = controller.IndexOf(
            "function Invoke-ResumeUnattendedCommand",
            StringComparison.Ordinal);
        var resumeEnd = controller.IndexOf("function Invoke-CreateCommand", resumeStart, StringComparison.Ordinal);
        var resume = controller[resumeStart..resumeEnd];
        var repairedResumeConditionIndex = resume.IndexOf(
            "if ($repairedPreFirstStartSecurityConfiguration)",
            StringComparison.Ordinal);
        var repairedResumeBootKeyIndex = resume.IndexOf(
            "Invoke-UnattendedOpticalBootKey -VmObject $vm",
            StringComparison.Ordinal);
        Assert.True(repairedResumeConditionIndex >= 0);
        Assert.True(repairedResumeBootKeyIndex > repairedResumeConditionIndex);
        Assert.DoesNotContain(
            "Invoke-UnattendedOpticalBootKey",
            resume[..repairedResumeConditionIndex],
            StringComparison.Ordinal);
        Assert.Single(
            resume.Split('\n'),
            line => line.Contains("Invoke-UnattendedOpticalBootKey", StringComparison.Ordinal));
    }

    [Fact]
    public void HyperVController_DetachPollingAcceptsImmediateHyperVReadback()
    {
        var proof = BuildDetachProof(
            """
            $script:getCall++
            if ($script:getCall -eq 1) { return @($windowsDrive, $answerDrive) }
            return @()
            """,
            """
            Detach-OwnedInstallationMedia -State $state -RequireBoth -TimeoutSec 1 -PollIntervalMilliseconds 10
            [Console]::Out.Write("$script:setCall,$script:getCall")
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Equal("2,2", result.Stdout);
    }

    [Fact]
    public void HyperVController_DetachPollingToleratesDelayedStaleHyperVReadback()
    {
        var proof = BuildDetachProof(
            """
            $script:getCall++
            if ($script:getCall -le 2) { return @($windowsDrive, $answerDrive) }
            if ($script:getCall -eq 3) { return @($answerDrive) }
            return @()
            """,
            """
            Detach-OwnedInstallationMedia -State $state -RequireBoth -TimeoutSec 1 -PollIntervalMilliseconds 10
            [Console]::Out.Write("$script:setCall,$script:getCall")
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Equal("2,4", result.Stdout);
    }

    [Fact]
    public void HyperVController_DetachPollingTimesOutWithOnlyControllerLocationDiagnostics()
    {
        var proof = BuildDetachProof(
            """
            $script:getCall++
            return @($windowsDrive, $answerDrive)
            """,
            """
            try {
                Detach-OwnedInstallationMedia -State $state -RequireBoth -TimeoutSec 1 -PollIntervalMilliseconds 10
                throw "Expected detach timeout."
            } catch {
                [Console]::Out.Write($_.Exception.Message)
            }
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Contains("Timed out waiting for Hyper-V", result.Stdout);
        Assert.Contains("Windows ISO remains attached at controller 0, location 1", result.Stdout);
        Assert.Contains("answer ISO remains attached at controller 0, location 2", result.Stdout);
        Assert.DoesNotContain(@"C:\owned", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".iso", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_StrictDetachRequiresBothMediaBeforeIssuingAnyDetach()
    {
        var proof = BuildDetachProof(
            """
            $script:getCall++
            return @($windowsDrive)
            """,
            """
            try {
                Detach-OwnedInstallationMedia -State $state -RequireBoth -TimeoutSec 1 -PollIntervalMilliseconds 10
                throw "Expected strict detach refusal."
            } catch {
                [Console]::Out.Write("$script:setCall|$($_.Exception.Message)")
            }
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.StartsWith("0|", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Expected owned answer ISO was not attached", result.Stdout);
        Assert.Contains("Refusing to issue a partial detach", result.Stdout);
    }

    [Fact]
    public void HyperVController_AlreadyDetachedResumeRequiresExactOwnerChecksAndReadinessStatus()
    {
        var proof = BuildDetachProof(
            """
            $script:getCall++
            return @()
            """,
            """
            Detach-OwnedInstallationMedia `
                -State $state `
                -AllowAlreadyDetachedAfterPowerShellDirectReady `
                -TimeoutSec 1 `
                -PollIntervalMilliseconds 10
            $state.status = "installing"
            try {
                Detach-OwnedInstallationMedia `
                    -State $state `
                    -AllowAlreadyDetachedAfterPowerShellDirectReady `
                    -TimeoutSec 1 `
                    -PollIntervalMilliseconds 10
                throw "Expected status refusal."
            } catch {
                [Console]::Out.Write($_.Exception.Message)
            }
            """);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Contains("requires unattended status 'powershell-direct-ready'", result.Stdout);

        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var resumeStart = controller.IndexOf("function Invoke-ResumeUnattendedCommand", StringComparison.Ordinal);
        var resumeEnd = controller.IndexOf("function Invoke-CreateCommand", resumeStart, StringComparison.Ordinal);
        var resume = controller[resumeStart..resumeEnd];
        var vmOwnerCheck = resume.IndexOf("Assert-OwnedVM", StringComparison.Ordinal);
        var markerBindingCheck = resume.IndexOf("Assert-UnattendStateMatchesVmMarker", StringComparison.Ordinal);
        var readinessGate = resume.IndexOf(
            "$allowAlreadyDetachedAfterPowerShellDirectReady =",
            StringComparison.Ordinal);
        Assert.True(vmOwnerCheck >= 0);
        Assert.True(markerBindingCheck > vmOwnerCheck);
        Assert.True(readinessGate > markerBindingCheck);
        Assert.Contains("[string]$state.status -ceq \"powershell-direct-ready\"", resume);
        Assert.Contains("-ExpectedKind \"setup\"", resume);
        Assert.Contains(
            "-AllowAlreadyDetachedAfterPowerShellDirectReady:$allowAlreadyDetachedAfterPowerShellDirectReady",
            resume);
        Assert.Contains("-RequireAttachedMedia:(-not $allowAlreadyDetachedAfterPowerShellDirectReady)", resume);

        var completionStart = controller.IndexOf(
            "function Complete-UnattendedInstallation",
            StringComparison.Ordinal);
        var completionEnd = controller.IndexOf(
            "function Invoke-GuestCommandWithTimeout",
            completionStart,
            StringComparison.Ordinal);
        var completion = controller[completionStart..completionEnd];
        foreach (var requiredContinuation in new[]
                 {
                     "Detach-OwnedInstallationMedia",
                     "Remove-OwnedUnattendMedia",
                     "Remove-GuestUnattendCache",
                     "Invoke-GuestInstallationVerification",
                     "Export-CleanWindowsCredential",
                     "Set-GuestCredential",
                     "Assert-OldCredentialRejected",
                     "Remove-SetupCredentialMaterial",
                 })
        {
            Assert.Contains(requiredContinuation, completion);
        }
    }

    [Theory]
    [InlineData("Fresh", "Fresh unattended Create requires -GenerateCredential")]
    [InlineData("Resume", "GenerateCredential is accepted only for a fresh unattended Create")]
    [InlineData("Cleanup", "GenerateCredential is accepted only for a fresh unattended Create")]
    [InlineData("Manual", "GenerateCredential is accepted only for unattended Create")]
    public void HyperVController_EnforcesGenerateCredentialConsentBeforeHyperV(
        string scenario,
        string expectedError)
    {
        var result = RunHyperVParameterContract(scenario);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Stderr);
        Assert.DoesNotContain("Run this controller from an elevated PowerShell session", result.Stderr);
    }

    [Fact]
    public void UnattendHelper_EnforcesImageDiskLocaleOobeAndNoAutologonContract()
    {
        var helper = ReadScript("CleanWindowsUnattend.psm1");

        Assert.Contains("New-Object Xml.XmlDocument", helper);
        Assert.Contains("$element.InnerText = $Text", helper);
        Assert.Contains("\"/IMAGE/INDEX\"", helper);
        Assert.Contains("exactly one supported OS image selector", helper);
        Assert.Contains("must not combine unsupported index and name selectors", helper);
        Assert.Contains("Windows 11 Enterprise Evaluation", helper);
        Assert.Contains("-Type \"EFI\" -Size \"260\"", helper);
        Assert.Contains("-Type \"MSR\" -Size \"16\"", helper);
        Assert.Contains("-Type \"Primary\" -Extend", helper);
        Assert.Contains("-Format \"FAT32\"", helper);
        Assert.Contains("-Format \"NTFS\"", helper);
        Assert.Contains("-Letter \"C\"", helper);
        Assert.Contains("\"WillWipeDisk\" -Text \"true\"", helper);
        Assert.Contains("\"en-US\"", helper);
        Assert.Contains("SkipMachineOOBE", helper);
        Assert.Contains("SkipUserOOBE", helper);
        Assert.Contains("\"Administrators\"", helper);
        Assert.Contains("\"AutoLogon\"", helper);
        Assert.Contains("\"ProductKey\"", helper);
        Assert.Contains("Answer file must not contain $forbiddenName", helper);
        Assert.DoesNotContain("<AutoLogon", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<ProductKey", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FirstLogonCommands>", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RunSynchronous>", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_ProtectsRotatesAndCleansCredentialAndAnswerMaterial()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var helper = ReadScript("CleanWindowsUnattend.psm1");

        Assert.Contains("Export-Clixml -LiteralPath $resolvedCredentialPath", helper);
        Assert.Contains("current-user-dpapi-clixml", helper);
        Assert.Contains("SetAccessRuleProtection($true, $false)", helper);
        Assert.Contains("S-1-5-18", helper);
        Assert.Contains("S-1-5-32-544", helper);
        Assert.Contains("Path must stay beneath the owned marker directory.", helper);
        Assert.Contains("Protect-CleanWindowsOwnedDirectory", controller);
        Assert.DoesNotContain("not '$Expected'", helper, StringComparison.Ordinal);
        Assert.Contains("Open-GuestSession", controller);
        Assert.Contains("$UnattendedInstallTimeoutSec", controller);
        Assert.Contains("Detach-OwnedInstallationMedia", controller);
        Assert.Contains("-State $State", controller);
        Assert.Contains("Set-VMDvdDrive", controller);
        Assert.Contains("Remove-OwnedUnattendMedia -Paths $Paths", controller);
        Assert.Contains("Remove-GuestUnattendCache -Session $setupSession", controller);
        Assert.Contains("Set-LocalUser -InputObject $localUser -Password $FinalSecurePassword", controller);
        Assert.Contains("Assert-OldCredentialRejected", controller);
        var finalSessionIndex = controller.IndexOf(
            "$finalSession = Open-GuestSession -GuestCredential $finalCredential",
            StringComparison.Ordinal);
        var oldCredentialIndex = controller.IndexOf(
            "Assert-OldCredentialRejected",
            finalSessionIndex,
            StringComparison.Ordinal);
        Assert.True(finalSessionIndex >= 0);
        Assert.True(oldCredentialIndex > finalSessionIndex);
        Assert.Contains("Test-CleanWindowsCredentialAuthenticationRejection", controller);
        Assert.Contains("Old credential attempt failed for a non-authentication reason", controller);
        Assert.Contains("$probe.BeginInvoke()", controller);
        Assert.Contains("AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSec))", controller);
        Assert.Contains("Confirming final credential PowerShell Direct availability", controller);
        Assert.Contains("-TimeoutSec 120", controller);
        Assert.Contains("CredentialPath = $Paths.FinalCredentialPath", controller);
        Assert.Contains("ResumeUnattended", controller);
        Assert.Contains("CleanupUnattend", controller);
        Assert.Contains("Assert-UnattendStateMatchesVmMarker", controller);
        Assert.Contains("Owned unattended-install marker Windows ISO does not match", controller);
        Assert.Contains("requires -ConfirmOwnedAction and matching ownership markers", controller);
        Assert.DoesNotContain("GetNetworkCredential().Password", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertFrom-SecureString -AsPlainText", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $Credential", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-VM ", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-VHD", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictiveAcl_SetsCurrentUserOwnerBeforeApplyingExactlyThreeRules()
    {
        var helper = ReadScript("CleanWindowsUnattend.psm1");
        var setterStart = helper.IndexOf("function Set-CleanWindowsRestrictiveAcl", StringComparison.Ordinal);
        var setterEnd = helper.IndexOf("function Assert-CleanWindowsRestrictiveAcl", setterStart, StringComparison.Ordinal);
        var setter = helper[setterStart..setterEnd];
        var verifier = helper[setterEnd..helper.IndexOf(
            "function Protect-CleanWindowsOwnedDirectory",
            setterEnd,
            StringComparison.Ordinal)];

        var setOwnerIndex = setter.IndexOf("$acl.SetOwner($currentSid)", StringComparison.Ordinal);
        var applyIndex = setter.IndexOf("SetAccessControl", StringComparison.Ordinal);
        Assert.True(setOwnerIndex >= 0);
        Assert.True(applyIndex > setOwnerIndex);
        Assert.Contains("New-Object Security.AccessControl.DirectorySecurity(", setter);
        Assert.Contains("New-Object Security.AccessControl.FileSecurity(", setter);
        Assert.Contains("[Security.AccessControl.AccessControlSections]::Owner", setter);
        Assert.Contains("$existingOwnerSid.Value -cne $currentSid.Value", setter);
        Assert.Contains(
            "foreach ($sid in @($currentSid, $systemSid, $administratorsSid))",
            setter);
        Assert.Single(
            setter.Split('\n'),
            line => line.Contains("$acl.AddAccessRule($rule)", StringComparison.Ordinal));
        Assert.Contains("$rules.Count -ne 3", verifier);
        Assert.Contains("$currentSid,", verifier);
        Assert.Contains("\"S-1-5-18\"", verifier);
        Assert.Contains("\"S-1-5-32-544\"", verifier);
    }

    [Fact]
    public void HyperVController_VerifiesInstalledEnterpriseEvaluationAndCompletedSetup()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");

        Assert.Contains("Windows 11 Enterprise Evaluation", controller);
        Assert.Contains("EnterpriseEval", controller);
        Assert.Contains("PROCESSOR_ARCHITECTURE", controller);
        Assert.Contains("\"AMD64\"", controller);
        Assert.Contains("SystemSetupInProgress", controller);
        Assert.Contains("OOBEInProgress", controller);
        Assert.Contains("SetupPhase", controller);
        Assert.Contains("SetupType", controller);
        Assert.Contains("IMAGE_STATE_COMPLETE", controller);
        Assert.Contains("Get-LocalUser", controller);
        Assert.Contains("Get-LocalGroup -SID", controller);
        Assert.Contains("AutoAdminLogon", controller);
        Assert.Contains("DefaultPassword", controller);
        Assert.Contains("installationMediaAttached = $false", controller);
        Assert.Contains("autoLogon = $false", controller);
    }

    [Fact]
    public void UnattendMediaValidation_GeneratesMountsVerifiesAndCleansWithoutASecretInOutput()
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            "CleanWindowsUnattend",
            $"test-{Guid.NewGuid():N}");
        try
        {
            var result = RunUnattendValidation("ValidateMedia", ownedRoot);

            Assert.True(
                result.ExitCode == 0,
                $"Media validation failed.\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
            var jsonLine = result.Stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Last();
            using var proof = JsonDocument.Parse(jsonLine);
            var root = proof.RootElement;
            Assert.True(root.GetProperty("succeeded").GetBoolean());
            Assert.True(root.GetProperty("credential").GetProperty("dpapiRoundtrip").GetBoolean());
            Assert.True(root.GetProperty("credential").GetProperty("restrictiveAcl").GetBoolean());
            Assert.True(root.GetProperty("credential").GetProperty("ownerIsCurrentUser").GetBoolean());
            Assert.False(root.GetProperty("credential").GetProperty("passwordPrinted").GetBoolean());
            Assert.True(root.GetProperty("answerMediaRestrictiveAcl").GetBoolean());
            Assert.Equal(1, root.GetProperty("answerFile").GetProperty("imageIndex").GetInt32());
            Assert.Equal(
                "Windows 11 Enterprise Evaluation",
                root.GetProperty("answerFile").GetProperty("imageName").GetString());
            Assert.False(root.GetProperty("answerFile").GetProperty("autoLogon").GetBoolean());
            Assert.False(root.GetProperty("answerFile").GetProperty("productKey").GetBoolean());
            Assert.True(root.GetProperty("media").GetProperty("mountedReadOnly").GetBoolean());
            Assert.True(root.GetProperty("media").GetProperty("answerFileAtRoot").GetBoolean());
            Assert.DoesNotContain("OpenClawAdmin", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("OpenClaw & QA", result.Stdout, StringComparison.Ordinal);
            Assert.False(Directory.Exists(ownedRoot));
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CredentialAuthenticationClassifier_RejectsTransientTransportFailures()
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            "CleanWindowsUnattend",
            $"classifier-{Guid.NewGuid():N}");
        try
        {
            var result = RunUnattendValidation("ValidateAuthenticationClassifier", ownedRoot);

            Assert.Equal(0, result.ExitCode);
            var jsonLine = result.Stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Last();
            using var proof = JsonDocument.Parse(jsonLine);
            var root = proof.RootElement;
            Assert.True(root.GetProperty("succeeded").GetBoolean());
            Assert.True(root.GetProperty("badPasswordRejected").GetBoolean());
            Assert.True(root.GetProperty("accessDeniedRejected").GetBoolean());
            Assert.False(root.GetProperty("transientTransportRejected").GetBoolean());
            Assert.False(Directory.Exists(ownedRoot));
        }
        finally
        {
            if (Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
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

    private static string BuildDetachProof(string getDvdDriveBody, string testBody)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var normalize = ExtractPowerShellFunction(
            controller,
            "Normalize-ComparisonPath",
            "Test-StringEquals");
        var detach = ExtractPowerShellFunction(
            controller,
            "Detach-OwnedInstallationMedia",
            "Assert-BothOwnedInstallationMediaAttached");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Detach-Proof'\n",
            "$script:InstallationMediaDetachTimeoutSec = 5\n",
            "$script:InstallationMediaDetachPollIntervalMilliseconds = 250\n",
            "$script:getCall = 0\n",
            "$script:setCall = 0\n",
            "$windowsDrive = [pscustomobject]@{ Path = 'C:\\owned\\windows.iso'; ControllerNumber = 0; ControllerLocation = 1 }\n",
            "$answerDrive = [pscustomobject]@{ Path = 'C:\\owned\\answer.iso'; ControllerNumber = 0; ControllerLocation = 2 }\n",
            "$state = [pscustomobject]@{ windowsIsoPath = 'C:\\owned\\windows.iso'; answerIsoPath = 'C:\\owned\\answer.iso'; status = 'powershell-direct-ready' }\n",
            "function Get-VMDvdDrive { param([string]$VMName, [object]$ErrorAction)\n",
            getDvdDriveBody,
            "\n}\n",
            "function Set-VMDvdDrive { param([string]$VMName, [int]$ControllerNumber, [int]$ControllerLocation, [AllowNull()][string]$Path) $script:setCall++ }\n",
            normalize,
            "\n",
            detach,
            "\n",
            testBody,
            "\n");
    }

    private static string BuildCheckpointObservationProof(
        string getSnapshotBody,
        string testBody)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var stringEquals = ExtractPowerShellFunction(
            controller,
            "Test-StringEquals",
            "Normalize-SecureBootTemplate");
        var propertyValue = ExtractPowerShellFunction(
            controller,
            "Get-PropertyValueOrNull",
            "Assert-HyperVPrerequisites");
        var requiredGuid = ExtractPowerShellFunction(
            controller,
            "Get-RequiredGuidString",
            "New-PendingCheckpointMarker");
        var snapshotIdentity = ExtractPowerShellFunction(
            controller,
            "Assert-CheckpointSnapshotBelongsToVm",
            "Assert-Version2CheckpointMarkerIntentMatches");
        var observation = ExtractPowerShellFunction(
            controller,
            "Wait-ForOwnedCheckpointObservation",
            "Assert-OwnedCheckpoint");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Checkpoint-Proof'\n",
            "$script:getCall = 0\n",
            "$vm = [pscustomobject]@{ Id = [Guid]'11111111-1111-1111-1111-111111111111'; Name = $VMName }\n",
            "$snapshot = [pscustomobject]@{ Name = 'clean-windows'; Id = [Guid]'22222222-2222-2222-2222-222222222222'; VMId = $vm.Id; VMName = $VMName; CreationTime = [DateTime]::UtcNow }\n",
            "function Get-VMSnapshot { param([string]$VMName, [object]$ErrorAction)\n",
            getSnapshotBody,
            "\n}\n",
            "function Start-Sleep { param([int]$Milliseconds) }\n",
            stringEquals,
            "\n",
            propertyValue,
            "\n",
            requiredGuid,
            "\n",
            snapshotIdentity,
            "\n",
            observation,
            "\n",
            testBody,
            "\n");
    }

    private static string BuildCheckpointMarkerProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var normalize = ExtractPowerShellFunction(
            controller,
            "Normalize-ComparisonPath",
            "Test-StringEquals");
        var stringEquals = ExtractPowerShellFunction(
            controller,
            "Test-StringEquals",
            "Normalize-SecureBootTemplate");
        var propertyValue = ExtractPowerShellFunction(
            controller,
            "Get-PropertyValueOrNull",
            "Assert-HyperVPrerequisites");
        var utcConversion = ExtractPowerShellFunction(
            controller,
            "ConvertTo-CheckpointUtc",
            "Get-RequiredGuidString");
        var requiredGuid = ExtractPowerShellFunction(
            controller,
            "Get-RequiredGuidString",
            "New-PendingCheckpointMarker");
        var pending = ExtractPowerShellFunction(
            controller,
            "New-PendingCheckpointMarker",
            "New-CompletedCheckpointMarker");
        var completed = ExtractPowerShellFunction(
            controller,
            "New-CompletedCheckpointMarker",
            "Write-MarkerFile");
        var snapshotIdentity = ExtractPowerShellFunction(
            controller,
            "Assert-CheckpointSnapshotBelongsToVm",
            "Assert-Version2CheckpointMarkerIntentMatches");
        var markerIdentity = ExtractPowerShellFunction(
            controller,
            "Assert-Version2CheckpointMarkerIntentMatches",
            "Assert-FinalizedCheckpointMarkerMatches");
        var finalizedIdentity = ExtractPowerShellFunction(
            controller,
            "Assert-FinalizedCheckpointMarkerMatches",
            "Get-PrimaryVhdPath");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Checkpoint-Proof'\n",
            "$script:MarkerSchema = 'openclaw.clean-windows.owner/v1'\n",
            "$script:CheckpointMarkerSchema = 'openclaw.clean-windows.checkpoint-owner/v2'\n",
            "$script:CleanCheckpointName = 'clean-windows'\n",
            "$script:PreparedCheckpointName = 'openclaw-prerequisites'\n",
            "$script:CheckpointCreationWindowSec = 900\n",
            "$vm = [pscustomobject]@{ Id = [Guid]'11111111-1111-1111-1111-111111111111'; Name = $VMName }\n",
            "$snapshot = [pscustomobject]@{ Name = 'clean-windows'; Id = [Guid]'22222222-2222-2222-2222-222222222222'; VMId = $vm.Id; VMName = $VMName; CreationTime = [DateTime]'2026-07-27T08:05:00Z' }\n",
            "function Assert-OwnerMarkerMatches { throw 'Legacy assertion must not run for a version 2 marker.' }\n",
            normalize,
            "\n",
            stringEquals,
            "\n",
            propertyValue,
            "\n",
            utcConversion,
            "\n",
            requiredGuid,
            "\n",
            pending,
            "\n",
            completed,
            "\n",
            snapshotIdentity,
            "\n",
            markerIdentity,
            "\n",
            finalizedIdentity,
            "\n",
            "$intent = New-PendingCheckpointMarker -ResolvedVhdPath 'C:\\owned\\os.vhdx' -ExpectedOwnerId 'owner' -OwnedCheckpointName 'clean-windows' -VmObject $vm -CreationStartedUtc ([DateTime]'2026-07-27T08:04:00Z') -OperationNonce '33333333-3333-3333-3333-333333333333'\n",
            "$marker = New-CompletedCheckpointMarker -PendingMarker $intent -SnapshotObject $snapshot\n",
            "Assert-FinalizedCheckpointMarkerMatches -Marker $marker -ExpectedOwnerId 'owner' -ExpectedVhdPath 'C:\\owned\\os.vhdx' -OwnedCheckpointName 'clean-windows' -VmObject $vm -SnapshotObject $snapshot\n",
            "[Console]::Out.Write(($marker | ConvertTo-Json -Compress))\n");
    }

    private static string BuildCheckpointCreationProof(bool observationFails)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var creation = ExtractPowerShellFunction(
            controller,
            "New-OwnedCheckpoint",
            "Get-UnattendedCompletionProof");
        var observationBody = observationFails
            ? "throw 'simulated observation timeout'"
            : "return $snapshot";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Checkpoint-Proof'\n",
            "$script:events = New-Object 'Collections.Generic.List[string]'\n",
            "$script:lastMarker = $null\n",
            "$vm = [pscustomobject]@{ Id = [Guid]'11111111-1111-1111-1111-111111111111'; Name = $VMName }\n",
            "$snapshot = [pscustomobject]@{ Name = 'clean-windows'; Id = [Guid]'22222222-2222-2222-2222-222222222222'; VMId = $vm.Id; VMName = $VMName; CreationTime = [DateTime]::UtcNow }\n",
            "function Assert-ConfirmationForOwnedAction { param([string]$Action) }\n",
            "function Assert-OwnedVM { param([string]$ResolvedVhdPath, [string]$ExpectedOwnerId) return $vm }\n",
            "function Remove-OwnedCheckpointIfPresent { param([string]$ResolvedVhdPath, [string]$ExpectedOwnerId, [string]$OwnedCheckpointName) }\n",
            "function Get-CheckpointMarkerPath { param([string]$ResolvedVhdPath, [string]$OwnedVmName, [string]$OwnedCheckpointName) return 'C:\\owned\\checkpoint.clean-windows.owner.json' }\n",
            "function Test-Path { param([string]$LiteralPath, [object]$PathType) return $false }\n",
            "function Get-SingleCheckpoint { param([string]$OwnedCheckpointName) return $null }\n",
            "function New-PendingCheckpointMarker { param([string]$ResolvedVhdPath, [string]$ExpectedOwnerId, [string]$OwnedCheckpointName, [object]$VmObject) [void]$script:events.Add('pending'); return [pscustomobject]@{ status = 'pending' } }\n",
            "function Write-MarkerFile { param([string]$Path, [object]$Marker) $script:lastMarker = $Marker; [void]$script:events.Add(\"write:$($Marker.status)\") }\n",
            "function Write-Step { param([string]$Message) }\n",
            "function Checkpoint-VM { param([string]$VMName, [string]$SnapshotName, [switch]$Confirm) [void]$script:events.Add('checkpoint') }\n",
            "function Wait-ForOwnedCheckpointObservation { param([string]$OwnedCheckpointName, [object]$VmObject) [void]$script:events.Add('observe'); ",
            observationBody,
            " }\n",
            "function New-CompletedCheckpointMarker { param([object]$PendingMarker, [object]$SnapshotObject) [void]$script:events.Add('finalize'); return [pscustomobject]@{ status = 'complete' } }\n",
            "function Read-MarkerFile { param([string]$Path) return $script:lastMarker }\n",
            "function Assert-FinalizedCheckpointMarkerMatches { param([object]$Marker, [string]$ExpectedOwnerId, [string]$ExpectedVhdPath, [string]$OwnedCheckpointName, [object]$VmObject, [object]$SnapshotObject) [void]$script:events.Add(\"assert:$($Marker.status)\") }\n",
            creation,
            "\n",
            "try {\n",
            "  New-OwnedCheckpoint -ResolvedVhdPath 'C:\\owned\\os.vhdx' -ExpectedOwnerId 'owner' -OwnedCheckpointName 'clean-windows' | Out-Null\n",
            "  [Console]::Out.Write(\"$($script:events -join ',')|$($script:lastMarker.status)\")\n",
            "} catch {\n",
            "  [Console]::Out.Write(\"$($script:events -join ',')|$($script:lastMarker.status)|$($_.Exception.Message)\")\n",
            "}\n");
    }

    private static string BuildCheckpointRecoveryCandidateProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var normalize = ExtractPowerShellFunction(
            controller,
            "Normalize-ComparisonPath",
            "Test-StringEquals");
        var stringEquals = ExtractPowerShellFunction(
            controller,
            "Test-StringEquals",
            "Normalize-SecureBootTemplate");
        var propertyValue = ExtractPowerShellFunction(
            controller,
            "Get-PropertyValueOrNull",
            "Assert-HyperVPrerequisites");
        var utcConversion = ExtractPowerShellFunction(
            controller,
            "ConvertTo-CheckpointUtc",
            "Get-RequiredGuidString");
        var requiredGuid = ExtractPowerShellFunction(
            controller,
            "Get-RequiredGuidString",
            "New-PendingCheckpointMarker");
        var snapshotIdentity = ExtractPowerShellFunction(
            controller,
            "Assert-CheckpointSnapshotBelongsToVm",
            "Assert-Version2CheckpointMarkerIntentMatches");
        var markerIdentity = ExtractPowerShellFunction(
            controller,
            "Assert-Version2CheckpointMarkerIntentMatches",
            "Assert-FinalizedCheckpointMarkerMatches");
        var candidate = ExtractPowerShellFunction(
            controller,
            "Get-RecoverableCheckpointCandidate",
            "Recover-PendingOwnedCheckpoint");
        var scenarioSetup = scenario switch
        {
            "Empty" =>
                "$marker = [pscustomobject]@{ status = 'pending'; sentinel = 'unchanged' }; $snapshots = @()\n",
            "Duplicate" => "$snapshots = @($snapshot, $snapshot)\n",
            "WrongName" => "$snapshot.Name = 'not-clean-windows'; $snapshots = @($snapshot)\n",
            "WrongVm" => "$snapshot.VMId = [Guid]'99999999-9999-9999-9999-999999999999'\n",
            "BeforeCompletion" => "$snapshot.CreationTime = [DateTime]'2026-07-27T07:59:00Z'\n",
            "OutsideLegacyWindow" =>
                "$snapshot.CreationTime = [DateTime]'2026-07-27T15:00:00Z'; $now = [DateTime]'2026-07-27T16:00:00Z'\n",
            "OutsidePendingWindow" =>
                "$marker = [pscustomobject]@{ schema = $script:CheckpointMarkerSchema; status = 'pending'; resourceType = 'checkpoint'; resourceName = 'clean-windows'; checkpointName = 'clean-windows'; ownerId = 'owner'; vmName = $VMName; vmId = [string]$vm.Id; vhdPath = 'C:\\owned\\os.vhdx'; operationNonce = '33333333-3333-3333-3333-333333333333'; creationStartedUtc = '2026-07-27T08:04:00Z' }; $snapshot.CreationTime = [DateTime]'2026-07-27T08:20:00Z'\n",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var failureOutput = scenario == "Empty"
            ? "[Console]::Out.Write(\"$($_.Exception.Message)|$($marker.status)|$($marker.sentinel)|$script:mutationCount\")\n"
            : "[Console]::Out.Write($_.Exception.Message)\n";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Checkpoint-Proof'\n",
            "$script:CheckpointMarkerSchema = 'openclaw.clean-windows.checkpoint-owner/v2'\n",
            "$script:CheckpointRecoveryClockSkewSec = 300\n",
            "$script:LegacyCheckpointRecoveryWindowSec = 21600\n",
            "$script:CheckpointCreationWindowSec = 900\n",
            "$vm = [pscustomobject]@{ Id = [Guid]'11111111-1111-1111-1111-111111111111'; Name = $VMName }\n",
            "$snapshot = [pscustomobject]@{ Name = 'clean-windows'; Id = [Guid]'22222222-2222-2222-2222-222222222222'; VMId = $vm.Id; VMName = $VMName; CreationTime = [DateTime]'2026-07-27T08:05:00Z' }\n",
            "$snapshots = @($snapshot)\n",
            "$marker = $null\n",
            "$completion = [DateTime]'2026-07-27T08:00:00Z'\n",
            "$now = [DateTime]'2026-07-27T09:00:00Z'\n",
            "$script:mutationCount = 0\n",
            "function Checkpoint-VM { $script:mutationCount++ }\n",
            "function Remove-VMSnapshot { $script:mutationCount++ }\n",
            "function Restore-VMSnapshot { $script:mutationCount++ }\n",
            "function Write-MarkerFile { $script:mutationCount++ }\n",
            scenarioSetup,
            normalize,
            "\n",
            stringEquals,
            "\n",
            propertyValue,
            "\n",
            utcConversion,
            "\n",
            requiredGuid,
            "\n",
            snapshotIdentity,
            "\n",
            markerIdentity,
            "\n",
            candidate,
            "\n",
            "try {\n",
            "  Get-RecoverableCheckpointCandidate -Snapshots $snapshots -Marker $marker -VmObject $vm -ResolvedVhdPath 'C:\\owned\\os.vhdx' -ExpectedOwnerId 'owner' -OwnedCheckpointName 'clean-windows' -UnattendedCompletionUtc $completion -NowUtc $now | Out-Null\n",
            "  [Console]::Out.Write('unexpected-success')\n",
            "} catch {\n",
            "  ",
            failureOutput,
            "}\n");
    }

    private static string BuildMarkerlessCheckpointRecoveryProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var recovery = ExtractPowerShellFunction(
            controller,
            "Recover-PendingOwnedCheckpoint",
            "Wait-ForVmState");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Checkpoint-Proof'\n",
            "$script:MarkerSchema = 'openclaw.clean-windows.owner/v1'\n",
            "$script:CheckpointMarkerSchema = 'openclaw.clean-windows.checkpoint-owner/v2'\n",
            "$script:CleanCheckpointName = 'clean-windows'\n",
            "$script:events = New-Object 'Collections.Generic.List[string]'\n",
            "$script:lastMarker = $null\n",
            "$vm = [pscustomobject]@{ Id = [Guid]'11111111-1111-1111-1111-111111111111'; Name = $VMName }\n",
            "$snapshot = [pscustomobject]@{ Name = 'clean-windows'; Id = [Guid]'22222222-2222-2222-2222-222222222222'; VMId = $vm.Id; VMName = $VMName; CreationTime = [DateTime]'2026-07-27T08:05:00Z' }\n",
            "function Get-PropertyValueOrNull { param([object]$Object, [string]$Name) $property = $Object.PSObject.Properties[$Name]; if ($null -eq $property) { return $null }; return $property.Value }\n",
            "function ConvertTo-CheckpointUtc { param([object]$Value, [string]$Label) return ([DateTime]$Value).ToUniversalTime() }\n",
            "function Assert-ConfirmationForOwnedAction { param([string]$Action) [void]$script:events.Add('confirm') }\n",
            "function Get-UnattendPaths { param([string]$ResolvedVhdPath) [void]$script:events.Add('paths'); return [pscustomobject]@{ StatePath = 'C:\\owned\\unattend.owner.json'; FinalCredentialPath = 'C:\\owned\\credentials\\guest.clixml'; FinalCredentialMetadataPath = 'C:\\owned\\credentials\\guest.owner.json'; OwnedRoot = 'C:\\owned' } }\n",
            "function Read-OwnedUnattendState { param([string]$ResolvedVhdPath, [object]$Paths) [void]$script:events.Add('state'); return [pscustomobject]@{ status = 'complete'; guestAdministratorName = 'OpenClawAdmin' } }\n",
            "function Assert-UnattendStateMatchesVmMarker { param([object]$State, [object]$VmObject) [void]$script:events.Add('bind') }\n",
            "function Get-UnattendedCompletionProof { param([object]$State, [string]$StatePath) [void]$script:events.Add('completion'); return [pscustomobject]@{ CompletionUtc = [DateTime]'2026-07-27T08:00:00Z' } }\n",
            "function Import-CleanWindowsCredential { param([string]$CredentialPath, [string]$MetadataPath, [string]$OwnedRoot, [string]$VMName, [string]$OwnerId, [string]$ExpectedKind) [void]$script:events.Add('credential'); return [pscustomobject]@{ UserName = 'OpenClawAdmin' } }\n",
            "function Get-CheckpointMarkerPath { param([string]$ResolvedVhdPath, [string]$OwnedVmName, [string]$OwnedCheckpointName) return 'C:\\owned\\checkpoint.clean-windows.owner.json' }\n",
            "function Read-MarkerFile { param([string]$Path) [void]$script:events.Add('read-marker'); return $null }\n",
            "function Get-VMSnapshot { param([string]$VMName, [object]$ErrorAction) [void]$script:events.Add('snapshots'); return $snapshot }\n",
            "function Get-RecoverableCheckpointCandidate { param([object[]]$Snapshots, [object]$Marker, [object]$VmObject, [string]$ResolvedVhdPath, [string]$ExpectedOwnerId, [string]$OwnedCheckpointName, [DateTime]$UnattendedCompletionUtc) [void]$script:events.Add('candidate'); return $snapshot }\n",
            "function New-PendingCheckpointMarker { param([string]$ResolvedVhdPath, [string]$ExpectedOwnerId, [string]$OwnedCheckpointName, [object]$VmObject, [DateTime]$CreationStartedUtc) [void]$script:events.Add('pending'); return [pscustomobject]@{ status = 'pending' } }\n",
            "function New-CompletedCheckpointMarker { param([object]$PendingMarker, [object]$SnapshotObject) [void]$script:events.Add('complete'); return [pscustomobject]@{ status = 'complete'; recoveryKind = $PendingMarker.recoveryKind } }\n",
            "function Assert-FinalizedCheckpointMarkerMatches { param([object]$Marker, [string]$ExpectedOwnerId, [string]$ExpectedVhdPath, [string]$OwnedCheckpointName, [object]$VmObject, [object]$SnapshotObject) [void]$script:events.Add('assert') }\n",
            "function Write-MarkerFile { param([string]$Path, [object]$Marker) $script:lastMarker = $Marker; [void]$script:events.Add('write') }\n",
            "function Write-InfoLine { param([string]$Message) [void]$script:events.Add('info') }\n",
            "function Assert-OwnedCheckpoint { throw 'Finalized-marker idempotency branch was not expected.' }\n",
            recovery,
            "\n",
            "Recover-PendingOwnedCheckpoint -ResolvedVhdPath 'C:\\owned\\os.vhdx' -ExpectedOwnerId 'owner' -VmObject $vm | Out-Null\n",
            "[Console]::Out.Write(\"$($script:events -join ',')|$($script:lastMarker.status)|$($script:lastMarker.recoveryKind)\")\n");
    }

    private static string ExtractPowerShellFunction(
        string script,
        string functionName,
        string nextFunctionName)
    {
        var start = script.IndexOf($"function {functionName}", StringComparison.Ordinal);
        var end = script.IndexOf($"function {nextFunctionName}", start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"PowerShell function {functionName} was not found.");
        Assert.True(end > start, $"PowerShell function boundary after {functionName} was not found.");
        return script[start..end];
    }

    private static void AssertPowerShellProofSucceeded(ProcessResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"PowerShell proof failed.\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
    }

    private static ProcessResult RunUnattendValidation(string command, string ownedRoot)
    {
        var script = Path.Combine(
            Root,
            "scripts",
            "clean-windows",
            "Test-CleanWindowsUnattendMedia.ps1");
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
                     "-Command",
                     command,
                     "-OwnedRoot",
                     ownedRoot,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start unattended media validation.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Unattended media validation exceeded 120 seconds.");
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessResult RunHyperVParameterContract(string scenario)
    {
        var script = Path.Combine(
            Root,
            "scripts",
            "clean-windows",
            "Invoke-CleanWindowsHyperV.ps1");
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
            "-Command",
            "Create",
            "-VMName",
            "OpenClaw-Parameter-Contract",
            "-OwnerId",
            "openclaw-parameter-contract",
            "-VhdPath",
            Path.Combine(Root, "TestResults", "parameter-contract.vhdx"),
        };
        switch (scenario)
        {
            case "Fresh":
                arguments.Add("-IsoPath");
                arguments.Add(Path.Combine(Root, "TestResults", "missing.iso"));
                break;
            case "Resume":
                arguments.Add("-ResumeUnattended");
                arguments.Add("-GenerateCredential");
                arguments.Add("-ConfirmOwnedAction");
                break;
            case "Cleanup":
                arguments.Add("-CleanupUnattend");
                arguments.Add("-GenerateCredential");
                arguments.Add("-ConfirmOwnedAction");
                break;
            case "Manual":
                arguments.Add("-CreateMode");
                arguments.Add("Manual");
                arguments.Add("-GenerateCredential");
                arguments.Add("-IsoPath");
                arguments.Add(Path.Combine(Root, "TestResults", "missing.iso"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Hyper-V parameter validation.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Hyper-V parameter validation exceeded 30 seconds.");
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessResult RunCheckpointRecoveryParameterContract(
        string scenario,
        bool confirmOwnedAction)
    {
        var script = Path.Combine(
            Root,
            "scripts",
            "clean-windows",
            "Invoke-CleanWindowsHyperV.ps1");
        var command = scenario == "Cleanup" ? "Create" : scenario;
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
            "-Command",
            command,
            "-VMName",
            "OpenClaw-Recovery-Parameter-Contract",
            "-OwnerId",
            "openclaw-recovery-parameter-contract",
            "-VhdPath",
            Path.Combine(Root, "TestResults", "recovery-parameter-contract.vhdx"),
            "-RecoverPendingCheckpoint",
        };
        if (scenario is "Prepare" or "Verify" or "Smoke")
        {
            arguments.Add("-CredentialPath");
            arguments.Add(Path.Combine(Root, "TestResults", "missing-guest.clixml"));
        }
        if (scenario == "Cleanup")
        {
            arguments.Add("-CleanupUnattend");
        }
        if (confirmOwnedAction)
        {
            arguments.Add("-ConfirmOwnedAction");
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start checkpoint recovery parameter validation.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Checkpoint recovery parameter validation exceeded 30 seconds.");
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessResult RunPowerShellCommand(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-Command", command })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell command exceeded 30 seconds.");
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessResult RunOwnedJsonWriterProof(string shell, string ownedRoot)
    {
        var modulePath = Path.Combine(
            Root,
            "scripts",
            "clean-windows",
            "CleanWindowsUnattend.psm1");
        const string command =
            """
            $ErrorActionPreference = 'Stop'
            $modulePath = $env:OPENCLAW_OWNED_JSON_MODULE
            $ownedRoot = $env:OPENCLAW_OWNED_JSON_ROOT
            $target = Join-Path $ownedRoot 'checkpoint.clean-windows.owner.json'
            try {
                Import-Module -Name $modulePath -Force -ErrorAction Stop
                $pending = [pscustomobject][ordered]@{
                    schema = 'openclaw.clean-windows.checkpoint-owner/v2'
                    status = 'pending'
                    operationNonce = '33333333-3333-3333-3333-333333333333'
                    generation = 1
                    pendingOnly = 'first-write'
                }
                $complete = [pscustomobject][ordered]@{
                    schema = 'openclaw.clean-windows.checkpoint-owner/v2'
                    status = 'complete'
                    operationNonce = '33333333-3333-3333-3333-333333333333'
                    generation = 2
                    snapshotId = '22222222-2222-2222-2222-222222222222'
                }

                Write-CleanWindowsOwnedJsonFile `
                    -Path $target `
                    -OwnedRoot $ownedRoot `
                    -Value $pending | Out-Null
                $first = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
                if ([string]$first.status -cne 'pending' -or [int]$first.generation -ne 1) {
                    throw 'First owned JSON write did not persist the pending marker.'
                }

                Write-CleanWindowsOwnedJsonFile `
                    -Path $target `
                    -OwnedRoot $ownedRoot `
                    -Value $complete | Out-Null
                $second = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
                if (
                    [string]$second.status -cne 'complete' -or
                    [int]$second.generation -ne 2 -or
                    [string]$second.snapshotId -cne '22222222-2222-2222-2222-222222222222' -or
                    $null -ne $second.PSObject.Properties['pendingOnly']
                ) {
                    throw 'Second owned JSON write did not atomically replace the pending marker.'
                }

                $loadedModule = @(
                    Get-Module |
                        Where-Object {
                            [string]::Equals(
                                [string]$_.Path,
                                $modulePath,
                                [StringComparison]::OrdinalIgnoreCase
                            )
                        }
                )[0]
                & $loadedModule {
                    param($Path)
                    Assert-CleanWindowsRestrictiveAcl -Path $Path
                } $ownedRoot
                $directoryAclVerified = $true
                & $loadedModule {
                    param($Path)
                    Assert-CleanWindowsRestrictiveAcl -Path $Path
                } $target
                $fileAclVerified = $true

                $temporaryFiles = @(
                    Get-ChildItem -LiteralPath $ownedRoot -File |
                        Where-Object {
                            $_.Name -like 'checkpoint.clean-windows.owner.json.*.tmp'
                        }
                )
                if ($temporaryFiles.Count -ne 0) {
                    throw 'Owned JSON writer left a temporary marker file behind.'
                }

                [Console]::Out.Write(([pscustomobject][ordered]@{
                    edition = [string]$PSVersionTable.PSEdition
                    firstStatus = [string]$first.status
                    secondStatus = [string]$second.status
                    generation = [int]$second.generation
                    directoryAclVerified = $directoryAclVerified
                    fileAclVerified = $fileAclVerified
                    temporaryFileCount = $temporaryFiles.Count
                    targetExistedBeforeCleanup = Test-Path -LiteralPath $target -PathType Leaf
                } | ConvertTo-Json -Compress))
            } finally {
                if (Test-Path -LiteralPath $ownedRoot) {
                    Remove-Item -LiteralPath $ownedRoot -Recurse -Force -ErrorAction Stop
                }
            }
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
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
                     "-Command",
                     command,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["OPENCLAW_OWNED_JSON_MODULE"] = modulePath;
        startInfo.Environment["OPENCLAW_OWNED_JSON_ROOT"] = ownedRoot;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start owned JSON proof in {shell}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Owned JSON proof in {shell} exceeded 30 seconds.");
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

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
