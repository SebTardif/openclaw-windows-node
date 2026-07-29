using System.Diagnostics;
using System.IO.Compression;
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
        Assert.Contains("Assert-OwnedVmDiskBinding", script);
        Assert.Contains("Get-VMHardDiskDrive -VM $VmObject", script);
        Assert.Contains("\"Get-VHD\"", script);
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
    public void HyperVController_RequiresGetVhdAndBindsOnlyTheExactOwnedVmDisk()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var prerequisites = ExtractPowerShellFunction(
            controller,
            "Assert-HyperVPrerequisites",
            "Get-OwnedMarkerRoot");
        var resolver = ExtractPowerShellFunction(
            controller,
            "Resolve-CanonicalExistingVhdPath",
            "Assert-VhdChainReachesOwnedBase");
        var ancestry = ExtractPowerShellFunction(
            controller,
            "Assert-VhdChainReachesOwnedBase",
            "Assert-OwnedVmDiskBinding");
        var diskBinding = ExtractPowerShellFunction(
            controller,
            "Assert-OwnedVmDiskBinding",
            "Assert-OwnedVM");
        var ownership = ExtractPowerShellFunction(
            controller,
            "Assert-OwnedVM",
            "Get-SingleCheckpoint");

        Assert.Contains("\"Get-VHD\"", prerequisites);
        Assert.Contains("$script:VhdChainMaxDepth = 32", controller);
        Assert.Contains("Test-Path -LiteralPath $fullPath -PathType Leaf", resolver);
        Assert.Contains("Resolve-Path -LiteralPath $fullPath -ErrorAction Stop", resolver);
        Assert.Contains("Get-VHD -Path $currentPath -ErrorAction Stop", ancestry);
        Assert.Contains("ParentPath", ancestry);
        Assert.Contains("Collections.Generic.HashSet[string]", ancestry);
        Assert.Contains("[StringComparer]::OrdinalIgnoreCase", ancestry);
        Assert.Contains("cycle detected", ancestry);
        Assert.Contains("maximum depth", ancestry);
        Assert.Contains("terminated at unrelated base", ancestry);
        Assert.Contains("Get-VMHardDiskDrive -VM $VmObject -ErrorAction Stop", diskBinding);
        Assert.Contains("$drives.Count -ne 1", diskBinding);
        Assert.Contains("exactly one active hard disk", diskBinding);
        Assert.Contains("additional data disks", diskBinding);
        Assert.Contains("Assert-VhdChainReachesOwnedBase", diskBinding);
        Assert.DoesNotContain("Get-VMHardDiskDrive -VMName", diskBinding);

        var noteMarkerIndex = ownership.IndexOf("Assert-OwnerMarkerMatches", StringComparison.Ordinal);
        var fileMarkerIndex = ownership.LastIndexOf("Assert-OwnerMarkerMatches", StringComparison.Ordinal);
        var diskBindingIndex = ownership.IndexOf("Assert-OwnedVmDiskBinding", StringComparison.Ordinal);
        Assert.True(noteMarkerIndex >= 0);
        Assert.True(fileMarkerIndex > noteMarkerIndex);
        Assert.True(diskBindingIndex > fileMarkerIndex);
        Assert.DoesNotContain("Get-PrimaryVhdPath", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_AllOwnedEntryPointsUseCheckpointAwareVmAssertion()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var entryPoints = new[]
        {
            ("Invoke-ResumeUnattendedCommand", "Invoke-CreateCommand"),
            ("Invoke-PrepareCommand", "Invoke-VerifyCommand"),
            ("Invoke-VerifyCommand", "Invoke-SmokeCommand"),
            ("Invoke-SmokeCommand", "Invoke-RestoreCommand"),
        };

        foreach (var (functionName, nextFunctionName) in entryPoints)
        {
            var entryPoint = ExtractPowerShellFunction(controller, functionName, nextFunctionName);
            Assert.Contains("Assert-OwnedVM", entryPoint);
        }

        var prepare = ExtractPowerShellFunction(
            controller,
            "Invoke-PrepareCommand",
            "Invoke-VerifyCommand");
        Assert.True(
            prepare.IndexOf("Assert-OwnedVM", StringComparison.Ordinal) <
            prepare.IndexOf("Recover-PendingOwnedCheckpoint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DirectBase", "accepted|0|1|1")]
    [InlineData("MultipleLevels", "accepted|3|4|4")]
    public void HyperVController_VhdAncestryAcceptsDirectAndMultiLevelOwnedChains(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildVhdChainProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(expectedOutput, result.Stdout);
    }

    [Fact]
    public void HyperVController_CheckpointActiveAvhdxReachesExactOwnerBase()
    {
        var proof = BuildVhdChainProof("CheckpointLeaf");
        Assert.Contains(
            @"D:\Hyper-V\OpenClaw-Clean-Windows\os_622E57AA-66AB-4904-B875-7705065AF129.avhdx",
            proof);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.Equal("accepted|1|2|2", result.Stdout);
    }

    [Theory]
    [InlineData("WrongTerminal", "terminated at unrelated base")]
    [InlineData("MissingParent", "does not exist as a file")]
    [InlineData("GetVhdError", "Get-VHD failed")]
    [InlineData("Cycle", "cycle detected")]
    [InlineData("OverMaxDepth", "maximum depth of 32")]
    [InlineData("InvalidPath", "is not an absolute path")]
    [InlineData("AmbiguousGetVhd", "returned ambiguous data")]
    public void HyperVController_VhdAncestryFailsClosed(
        string scenario,
        string expectedError)
    {
        var proof = BuildVhdChainProof(scenario);
        Assert.Contains("function Get-VHD", proof);

        var result = RunPowerShellCommand(proof);

        AssertPowerShellProofSucceeded(result);
        Assert.StartsWith("rejected|", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(expectedError, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_RefusesDuplicateActiveOsDiskDrivesBeforeChainInference()
    {
        var result = RunPowerShellCommand(BuildDuplicateActiveVhdProof());

        AssertPowerShellProofSucceeded(result);
        Assert.StartsWith("0|0|", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("exactly one active hard disk", result.Stdout);
        Assert.Contains("found 2", result.Stdout);
        Assert.Contains("additional data disks", result.Stdout);
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
    public void HyperVCurrentStateDocs_UseNormalPrepareAfterExactCleanRestore()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "CLEAN_WINDOWS_RUNNERS.md"));
        var skill = File.ReadAllText(
            Path.Combine(Root, ".agents", "skills", "openclaw-hyperv-smoke", "SKILL.md"));
        var routing = File.ReadAllText(
            Path.Combine(Root, ".agents", "skills", "windows-node-testing", "SKILL.md"));
        var exactPrepareCommand =
            @".\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Prepare -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -CredentialPath $credentialPath -ConfirmOwnedAction";
        var obsoleteRecoveryCommand =
            @".\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Prepare -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -CredentialPath $credentialPath -RecoverPendingCheckpoint -ConfirmOwnedAction";

        foreach (var guidance in new[] { docs, skill })
        {
            var normalizedGuidance = guidance.Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            Assert.Contains(exactPrepareCommand, guidance);
            Assert.DoesNotContain(obsoleteRecoveryCommand, guidance, StringComparison.Ordinal);
            Assert.Contains("exact `clean-windows` checkpoint", normalizedGuidance);
            Assert.Contains("finalized", normalizedGuidance);
            Assert.Contains("final rotated", normalizedGuidance);
            Assert.Contains("normal `Prepare`", normalizedGuidance);
            Assert.Contains("without `-RecoverPendingCheckpoint`", normalizedGuidance);
            Assert.Contains("exact", normalizedGuidance);
            Assert.Contains("Do not use `-CleanupUnattend`", normalizedGuidance);
            Assert.Contains("does not install Ubuntu", normalizedGuidance);
            Assert.Contains("app-owned distribution", normalizedGuidance);
            Assert.Contains("optional-feature stage", normalizedGuidance);
            Assert.Contains("package stage", normalizedGuidance);
            Assert.Contains("confirm", normalizedGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("zero-exit", normalizedGuidance);
            Assert.Contains("nonzero version", normalizedGuidance);
            Assert.Contains("wsl.exe --update --web-download", normalizedGuidance);
            Assert.Contains("exactly one", normalizedGuidance);
            Assert.Contains(
                "final verification",
                normalizedGuidance,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("elevated PowerShell", normalizedGuidance);
            Assert.Contains("v1.29.280", normalizedGuidance);
            Assert.Contains("current-user", normalizedGuidance);
            Assert.Contains("Add-AppxPackage", normalizedGuidance);
            Assert.Contains("License1.xml", normalizedGuidance);
            Assert.Contains("five HTTPS redirects", normalizedGuidance);
            Assert.Contains("nonce", normalizedGuidance);
            Assert.Contains("cleanup", normalizedGuidance, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not claim live confirmation", normalizedGuidance);
        }
        var normalizedRouting = routing.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        Assert.Contains("-RecoverPendingCheckpoint -ConfirmOwnedAction", normalizedRouting);
        Assert.Contains("Prepare-only", normalizedRouting);
        Assert.Contains("do not issue ad hoc checkpoint commands", normalizedRouting);
        Assert.Contains("sole active hard disk", normalizedRouting);
        Assert.Contains("Get-VHD", normalizedRouting);
        Assert.Contains("terminal", normalizedRouting);
        Assert.Contains("owner-marked base VHD", normalizedRouting);
        Assert.Contains("normal `Prepare`", normalizedRouting);
        Assert.Contains("without `-RecoverPendingCheckpoint`", normalizedRouting);
        Assert.Contains("does not install Ubuntu", normalizedRouting);
        Assert.Contains("confirm", normalizedRouting, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wsl.exe --update --web-download", normalizedRouting);
        Assert.Contains("without `-RecoverPendingCheckpoint` or cleanup", normalizedRouting);
        Assert.Contains("v1.29.280", normalizedRouting);
        Assert.Contains("current-user", normalizedRouting);
        Assert.Contains("License1.xml", normalizedRouting);
        Assert.Contains("allowlisted redirects", normalizedRouting);
        Assert.Contains("nonce", normalizedRouting);
        Assert.Contains("does not claim live confirmation", normalizedRouting);
        Assert.Contains("mocks only", normalizedRouting);
        Assert.Contains("Do not use a VM", normalizedRouting);
    }

    [Fact]
    public void HyperVController_FeatureStageEnablesOnlyDisabledFeaturesAndReturnsRestartProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var featureStage = ExtractPowerShellFunction(
            controller,
            "Get-GuestOptionalFeatureStageScriptBlock",
            "Invoke-GuestOptionalFeatureStage");

        Assert.Contains("Get-WindowsOptionalFeature", featureStage);
        Assert.Contains("Enable-WindowsOptionalFeature", featureStage);
        Assert.Contains("-Online", featureStage);
        Assert.Contains("-All", featureStage);
        Assert.Contains("-NoRestart", featureStage);
        Assert.DoesNotContain("wsl.exe", featureStage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Invoke-OpenClawTrustedWslProcess",
            featureStage,
            StringComparison.Ordinal);

        var result = RunPowerShellCommand(BuildOptionalFeatureStageProof(featuresEnabled: false));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("optional-features", proof.GetProperty("stage").GetString());
        Assert.Equal(
            "Enabled",
            proof.GetProperty("microsoftWindowsSubsystemLinuxState").GetString());
        Assert.Equal(
            "Enabled",
            proof.GetProperty("virtualMachinePlatformState").GetString());
        Assert.True(proof.GetProperty("changed").GetBoolean());
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
        Assert.Equal(2, proof.GetProperty("enableCalls").GetInt32());
    }

    [Fact]
    public void HyperVController_EnabledFeatureStageSkipsEnableAndPackageFollowsHostBoundary()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var result = RunPowerShellCommand(BuildOptionalFeatureStageProof(featuresEnabled: true));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.False(proof.GetProperty("changed").GetBoolean());
        Assert.False(proof.GetProperty("needsRestart").GetBoolean());
        Assert.Equal(0, proof.GetProperty("enableCalls").GetInt32());

        var prepare = ExtractPowerShellFunction(
            controller,
            "Prepare-GuestPrerequisites",
            "Verify-HostVmConfiguration");
        var featureIndex = prepare.IndexOf(
            "Invoke-GuestOptionalFeatureStage",
            StringComparison.Ordinal);
        var featureRestartGuardIndex = prepare.IndexOf(
            "if ([bool]$featureResult.needsRestart)",
            featureIndex,
            StringComparison.Ordinal);
        var firstRestartIndex = prepare.IndexOf(
            "Restart-GuestAndReconnect",
            featureRestartGuardIndex,
            StringComparison.Ordinal);
        var helperIndex = prepare.IndexOf(
            "Install-GuestWslNativeHelper",
            firstRestartIndex,
            StringComparison.Ordinal);
        var packageIndex = prepare.IndexOf(
            "Invoke-GuestWslPackageStage",
            helperIndex,
            StringComparison.Ordinal);

        Assert.True(featureIndex >= 0);
        Assert.True(featureRestartGuardIndex > featureIndex);
        Assert.True(firstRestartIndex > featureRestartGuardIndex);
        Assert.True(helperIndex > firstRestartIndex);
        Assert.True(packageIndex > helperIndex);
    }

    [Fact]
    public void HyperVController_EnablePendingFeatureStageRequestsRestartWithoutReenabling()
    {
        var result = RunPowerShellCommand(BuildOptionalFeatureStageProof(
            featuresEnabled: false,
            initialState: "EnablePending"));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal(
            "EnablePending",
            proof.GetProperty("microsoftWindowsSubsystemLinuxState").GetString());
        Assert.Equal(
            "EnablePending",
            proof.GetProperty("virtualMachinePlatformState").GetString());
        Assert.False(proof.GetProperty("changed").GetBoolean());
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
        Assert.Equal(0, proof.GetProperty("enableCalls").GetInt32());
    }

    [Fact]
    public void HyperVController_NativeWslHelperCapturesExpectedExitAndStderrWithoutFailingJob()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var nativeHelper = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslNativeHelperInstallerScriptBlock",
            "Install-GuestWslNativeHelper");
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"wsl-native-helper-{Guid.NewGuid():N}");
        try
        {
            var result = RunPowerShellCommand(BuildNativeWslHelperProof(ownedRoot));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.Equal("Completed", proof.GetProperty("jobState").GetString());
            Assert.Equal(50, proof.GetProperty("exitCode").GetInt32());
            Assert.Equal("", proof.GetProperty("stdout").GetString());
            Assert.Contains(
                "WSL is not installed",
                proof.GetProperty("stderr").GetString(),
                StringComparison.Ordinal);
            Assert.True(proof.GetProperty("utf8Decoded").GetBoolean());
            Assert.Equal("1", proof.GetProperty("wslUtf8AtLaunch").GetString());
            Assert.Equal("prior-value", proof.GetProperty("wslUtf8After").GetString());
            Assert.Equal(0, proof.GetProperty("remainingCaptureFiles").GetInt32());

            var normalizedNativeHelper = nativeHelper.Replace("\r", "", StringComparison.Ordinal);
            var utf8SetIndex = normalizedNativeHelper.IndexOf(
                "\"WSL_UTF8\",\n                    \"1\"",
                StringComparison.Ordinal);
            var startProcessIndex = normalizedNativeHelper.IndexOf(
                "$nativeProcess = Start-Process",
                StringComparison.Ordinal);
            var restoreIndex = normalizedNativeHelper.IndexOf(
                "$restoredWslUtf8Value",
                startProcessIndex,
                StringComparison.Ordinal);
            var cleanupIndex = normalizedNativeHelper.IndexOf(
                "Remove-Item -LiteralPath $capturePath",
                restoreIndex,
                StringComparison.Ordinal);
            Assert.True(utf8SetIndex >= 0);
            Assert.True(startProcessIndex > utf8SetIndex);
            Assert.True(restoreIndex > startProcessIndex);
            Assert.True(cleanupIndex > restoreIndex);
            Assert.Contains("[Text.Encoding]::UTF8", nativeHelper);
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
    public void HyperVController_NativeWslHelperRestoresAbsentUtf8AfterLaunchFailure()
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"wsl-native-helper-failure-{Guid.NewGuid():N}");
        try
        {
            var result = RunPowerShellCommand(BuildNativeWslHelperFailureProof(ownedRoot));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.Equal("1", proof.GetProperty("wslUtf8AtLaunch").GetString());
            Assert.True(proof.GetProperty("wslUtf8AbsentAfter").GetBoolean());
            Assert.Equal(0, proof.GetProperty("remainingCaptureFiles").GetInt32());
            Assert.Contains(
                "Trusted WSL operation 'Status' failed",
                proof.GetProperty("error").GetString(),
                StringComparison.Ordinal);
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
    public void HyperVController_NativeWslHelperCapturesVersionExitOneWithoutFailingJob()
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"wsl-native-version-{Guid.NewGuid():N}");
        try
        {
            var result = RunPowerShellCommand(BuildNativeWslHelperProof(
                ownedRoot,
                operation: "Version",
                exitCode: 1));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.Equal("Completed", proof.GetProperty("jobState").GetString());
            Assert.Equal(1, proof.GetProperty("exitCode").GetInt32());
            Assert.Equal("--version", proof.GetProperty("wslArgumentsAtLaunch").GetString());
            Assert.Contains(
                "WSL must be updated",
                proof.GetProperty("stderr").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(0, proof.GetProperty("remainingCaptureFiles").GetInt32());
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
    public void HyperVController_NativeWslHelperUsesExactWebDownloadUpdateArguments()
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"wsl-native-update-{Guid.NewGuid():N}");
        try
        {
            var result = RunPowerShellCommand(BuildNativeWslHelperProof(
                ownedRoot,
                operation: "UpdateWebDownload",
                exitCode: 3010));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.Equal("Completed", proof.GetProperty("jobState").GetString());
            Assert.Equal(3010, proof.GetProperty("exitCode").GetInt32());
            Assert.EndsWith(
                @"\System32\wsl.exe",
                proof.GetProperty("wslPathAtLaunch").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "--update --web-download",
                proof.GetProperty("wslArgumentsAtLaunch").GetString());
            Assert.True(proof.GetProperty("waitAtLaunch").GetBoolean());
            Assert.True(proof.GetProperty("passThruAtLaunch").GetBoolean());
            Assert.Equal(0, proof.GetProperty("remainingCaptureFiles").GetInt32());
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
    [InlineData(-1)]
    [InlineData(50)]
    public void HyperVController_AbsentPackageUsesExactNoDistributionInstallAndRequestsRestart(
        int expectedNotInstalledExitCode)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var nativeHelper = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslNativeHelperInstallerScriptBlock",
            "Install-GuestWslNativeHelper");
        var packageStage = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslPackageStageScriptBlock",
            "Invoke-GuestWslPackageStage");
        var trustedWslSurface = nativeHelper + packageStage;

        Assert.Contains("@(\"--install\", \"--no-distribution\")", nativeHelper);
        Assert.Contains("@(\"--update\", \"--web-download\")", nativeHelper);
        Assert.Contains("[string[]]$nativeArguments", nativeHelper);
        Assert.Contains("[ValidateSet(", nativeHelper);
        Assert.Contains("\"InstallNoDistribution\"", nativeHelper);
        Assert.Contains("\"UpdateWebDownload\"", nativeHelper);
        Assert.Contains("Start-Process", nativeHelper);
        Assert.Contains("-RedirectStandardOutput", nativeHelper);
        Assert.Contains("-RedirectStandardError", nativeHelper);
        Assert.Contains("-Wait", nativeHelper);
        Assert.Contains("-PassThru", nativeHelper);
        Assert.Contains("finally {", nativeHelper);
        Assert.Contains("Remove-Item -LiteralPath $capturePath", nativeHelper);
        Assert.Contains("-WindowStyle Hidden", nativeHelper);
        Assert.DoesNotContain("Invoke-Expression", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Read-Host", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReadKey", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TypeKey", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SendKeys", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StandardInput", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ms-windows-store", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft Store", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", trustedWslSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-ArgumentList $Operation", nativeHelper, StringComparison.Ordinal);
        Assert.Contains("$installExitCode -notin [int[]]@(0, 3010)", packageStage);
        Assert.Contains("$updateExitCode -notin [int[]]@(0, 3010)", packageStage);
        Assert.Contains("$exitCode -in [int[]]@(-1, 50) -and $notInstalled", packageStage);
        Assert.DoesNotContain("post-install", packageStage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("post-update", packageStage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$restartReported", packageStage, StringComparison.Ordinal);

        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            installExitCode: 0,
            statusExitCode: expectedNotInstalledExitCode,
            versionExitCode: expectedNotInstalledExitCode,
            installOutput: "The operation completed successfully."));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,InstallNoDistribution", proof.GetProperty("calls").GetString());
        Assert.True(proof.GetProperty("installInvoked").GetBoolean());
        Assert.Equal(0, proof.GetProperty("installExitCode").GetInt32());
        Assert.False(proof.GetProperty("updateInvoked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("updateExitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("versionExitCode").ValueKind);
        Assert.Equal("restart-required", proof.GetProperty("normalizedState").GetString());
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
    }

    [Fact]
    public void HyperVController_AcceptsCompatibilityInstallExitAndAlwaysRestarts()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            installExitCode: 3010,
            statusExitCode: 50,
            versionExitCode: 0,
            versionOutput: "WSL version 2.6.1",
            installOutput: "The requested operation is successful."));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,InstallNoDistribution", proof.GetProperty("calls").GetString());
        Assert.Equal(3010, proof.GetProperty("installExitCode").GetInt32());
        Assert.False(proof.GetProperty("updateInvoked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("versionExitCode").ValueKind);
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
    }

    [Fact]
    public void HyperVController_StatusAbsentDoesNotProbeVersion()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            installExitCode: 0,
            statusExitCode: -1,
            versionExitCode: 0,
            versionOutput: "WSL version 2.6.1",
            installOutput: "The operation completed successfully."));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,InstallNoDistribution", proof.GetProperty("calls").GetString());
        Assert.True(proof.GetProperty("installInvoked").GetBoolean());
        Assert.False(proof.GetProperty("updateInvoked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("versionExitCode").ValueKind);
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
    }

    [Fact]
    public void HyperVController_ArbitraryMinusOneWithoutAbsentSignalFailsClosed()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            statusExitCode: -1,
            statusOutput: "The request could not be completed.",
            versionExitCode: 0,
            versionOutput: "WSL version 2.6.1",
            captureFailure: true));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status", proof.GetProperty("calls").GetString());
        Assert.Contains(
            "unexpected exit code -1",
            proof.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_AbsentStatusShortCircuitsUnrelatedVersionFailure()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            statusExitCode: 50,
            statusOutput: "WSL is not installed",
            versionExitCode: 7,
            versionOutput: "The version request could not be completed."));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,InstallNoDistribution", proof.GetProperty("calls").GetString());
        Assert.True(proof.GetProperty("installInvoked").GetBoolean());
        Assert.False(proof.GetProperty("updateInvoked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("versionExitCode").ValueKind);
    }

    [Fact]
    public void HyperVController_InstallFailureIncludesDiagnosticAndDoesNotRetry()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            installExitCode: 5,
            statusExitCode: 50,
            installOutput: "The package install failed.",
            captureFailure: true));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,InstallNoDistribution", proof.GetProperty("calls").GetString());
        Assert.Contains(
            "wsl.exe --install --no-distribution returned unexpected exit code 5",
            proof.GetProperty("error").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "stdout: The package install failed.",
            proof.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3010)]
    public void HyperVController_ReadyStatusAndVersionExitOneUpdatesOnceAndAlwaysRestarts(
        int updateExitCode)
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            statusExitCode: 0,
            statusOutput: "WSL status ready",
            versionExitCode: 1,
            versionOutput:
                "Press any key to install WSL. Operation aborted. WSL must be updated. Run wsl.exe --update.",
            updateExitCode: updateExitCode));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,Version,UpdateWebDownload", proof.GetProperty("calls").GetString());
        Assert.Equal("restart-required", proof.GetProperty("normalizedState").GetString());
        Assert.False(proof.GetProperty("installInvoked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("installExitCode").ValueKind);
        Assert.True(proof.GetProperty("updateInvoked").GetBoolean());
        Assert.Equal(updateExitCode, proof.GetProperty("updateExitCode").GetInt32());
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
    }

    [Fact]
    public void HyperVController_UpdateFailureDiagnosticIsBoundedAndSanitized()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            statusExitCode: 0,
            statusOutput: "WSL status ready",
            versionExitCode: 1,
            versionOutput: "token=versionsecret; " + new string('v', 3000),
            updateExitCode: 5,
            updateOutput: "Bearer updatesecretvalue " + new string('u', 3000),
            updateError: "password=updatepassword; authorization=authvalue;",
            captureFailure: true));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        var error = proof.GetProperty("error").GetString()!;
        Assert.Equal("Status,Version,UpdateWebDownload", proof.GetProperty("calls").GetString());
        Assert.Contains(
            "wsl.exe --update --web-download returned unexpected exit code 5",
            error,
            StringComparison.Ordinal);
        Assert.Contains("Version diagnostic:", error, StringComparison.Ordinal);
        Assert.Contains("Update diagnostic:", error, StringComparison.Ordinal);
        Assert.Contains("stdout:", error, StringComparison.Ordinal);
        Assert.Contains("stderr:", error, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error, StringComparison.Ordinal);
        Assert.Contains("[truncated]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("versionsecret", error, StringComparison.Ordinal);
        Assert.DoesNotContain("updatesecretvalue", error, StringComparison.Ordinal);
        Assert.DoesNotContain("updatepassword", error, StringComparison.Ordinal);
        Assert.DoesNotContain("authvalue", error, StringComparison.Ordinal);
        Assert.True(error.Length <= 2300, $"Diagnostic length was {error.Length}.");
    }

    [Fact]
    public void HyperVController_InstalledPackageSkipsInstall()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(packageInstalled: true));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,Version", proof.GetProperty("calls").GetString());
        Assert.True(proof.GetProperty("wasInstalled").GetBoolean());
        Assert.False(proof.GetProperty("installInvoked").GetBoolean());
        Assert.False(proof.GetProperty("updateInvoked").GetBoolean());
        Assert.Equal("ready", proof.GetProperty("normalizedState").GetString());
        Assert.False(proof.GetProperty("needsRestart").GetBoolean());
    }

    [Fact]
    public void HyperVController_ReadyStatusAndContradictoryVersionFailsClosed()
    {
        var result = RunPowerShellCommand(BuildWslPackageStageProof(
            packageInstalled: false,
            statusExitCode: 0,
            statusOutput: "WSL status ready",
            versionExitCode: 0,
            versionOutput: "WSL is not installed",
            captureFailure: true));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("Status,Version", proof.GetProperty("calls").GetString());
        Assert.Contains(
            "wsl.exe --version returned exit code 0 with contradictory not-installed output",
            proof.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_FinalWslVerificationReturnsProofBeforeToolSetup()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var nativeHelper = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslNativeHelperInstallerScriptBlock",
            "Install-GuestWslNativeHelper");
        var verificationStage = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslVerificationStageScriptBlock",
            "Invoke-GuestWslVerificationStage");
        var result = RunPowerShellCommand(BuildWslVerificationStageProof());

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.Equal("wsl-package-only", proof.GetProperty("scope").GetString());
        Assert.Equal("ready", proof.GetProperty("normalizedState").GetString());
        Assert.Equal(0, proof.GetProperty("statusExitCode").GetInt32());
        Assert.Equal(0, proof.GetProperty("versionExitCode").GetInt32());
        Assert.Equal("WSL status ready", proof.GetProperty("status").GetString());
        Assert.Equal("WSL version 2.6.1", proof.GetProperty("version").GetString());
        Assert.Contains(
            "ConvertTo-OpenClawNativeDiagnostic -Text $text -MaxChars $MaxChars",
            nativeHelper);
        Assert.Contains("if ($combined.Length -gt 1024)", verificationStage);
        Assert.Contains(
            "return $combined.Substring(0, 1024) + \" [truncated]\"",
            verificationStage);

        var prepare = ExtractPowerShellFunction(
            controller,
            "Prepare-GuestPrerequisites",
            "Verify-HostVmConfiguration");
        var finalVerificationIndex = prepare.IndexOf(
            "Invoke-GuestWslVerificationStage",
            StringComparison.Ordinal);
        var gitIndex = prepare.IndexOf(
            "Ensure-GuestGitInstalled",
            finalVerificationIndex,
            StringComparison.Ordinal);
        var powershellIndex = prepare.IndexOf(
            "Ensure-GuestPowerShell7Installed",
            gitIndex,
            StringComparison.Ordinal);
        var copyIndex = prepare.IndexOf("Copy-RepoToGuest", powershellIndex, StringComparison.Ordinal);
        var setupIndex = prepare.IndexOf("Running guest setup-dev", copyIndex, StringComparison.Ordinal);

        Assert.True(finalVerificationIndex >= 0);
        Assert.True(gitIndex > finalVerificationIndex);
        Assert.True(powershellIndex > gitIndex);
        Assert.True(copyIndex > powershellIndex);
        Assert.True(setupIndex > copyIndex);
    }

    [Fact]
    public void HyperVController_WingetBootstrapPinsExactMicrosoftReleaseAndSecurityBoundary()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var bootstrap = ExtractPowerShellFunction(
            controller,
            "Get-GuestWingetBootstrapScriptBlock",
            "Ensure-GuestWingetAvailable");
        var ensure = ExtractPowerShellFunction(
            controller,
            "Ensure-GuestWingetAvailable",
            "Ensure-GuestGitInstalled");

        var exactPins = new[]
        {
            "v1.29.280",
            "https://github.com$releasePath/",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle",
            "216775738",
            "0809fa9f52e395d6e7de692331dce847ac991952675116bb4d8aae2ddcc20946",
            "DesktopAppInstaller_Dependencies.zip",
            "97760717",
            "3bbfcaa5cb011c48fac48d896d64a5c7c6898859a9f3d01555c8cd000f4e2962",
            "DesktopAppInstaller_Dependencies.json",
            "322",
            "a56ddd79cf9cd056d9546cfeb6958c2b44d20f6221f8518bf17b003717d47a7a",
            "Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx",
            "896581",
            "9c17b521f9d690a1f504da5108ed6eec5669eb3a8fd1331eef43e40d84e74283",
            "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx",
            "6757465",
            "077a3d1a5d0622bd3004dca85f5e192d6e98ec79b83d4aa06766759ea6c09c3d",
            "Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64.appx",
            "25431545",
            "a31595cc4b5aebc18466ec24e8d4b566fe0fcafb52d833b6d139b8691d0e5177",
            "AppInstaller_x64.msix",
            "62421154",
            "bdc908068f7563d89ef3405f1a30ae74df8cb0416414ed3613c4d68e2c812ff1",
            "2026.623.1704.0",
            "1.29.280.0",
        };
        foreach (var pin in exactPins)
        {
            Assert.Contains(pin, bootstrap, StringComparison.Ordinal);
        }

        var vclibsIndex = bootstrap.LastIndexOf(
            "Name = \"Microsoft.VCLibs.140.00\"",
            StringComparison.Ordinal);
        var desktopIndex = bootstrap.LastIndexOf(
            "Name = \"Microsoft.VCLibs.140.00.UWPDesktop\"",
            StringComparison.Ordinal);
        var runtimeIndex = bootstrap.LastIndexOf(
            "Name = \"Microsoft.WindowsAppRuntime.1.8\"",
            StringComparison.Ordinal);
        Assert.True(vclibsIndex >= 0);
        Assert.True(desktopIndex > vclibsIndex);
        Assert.True(runtimeIndex > desktopIndex);
        Assert.DoesNotContain("UI.Xaml", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"x86\", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"arm64\", bootstrap, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("$handler.AllowAutoRedirect = $false", bootstrap);
        Assert.Contains("$handler.UseCookies = $false", bootstrap);
        Assert.Contains("$request.Headers.Authorization = $null", bootstrap);
        Assert.Contains("[Security.Authentication.SslProtocols]::Tls12", bootstrap);
        Assert.Contains("[Net.SecurityProtocolType]::Tls12", bootstrap);
        Assert.Contains("$downloadDeadlineUtc = [DateTime]::UtcNow.AddSeconds(1800)", bootstrap);
        Assert.Contains("$cancellation.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))", bootstrap);
        Assert.Contains("-TimeoutSeconds $remainingDownloadSeconds", bootstrap);
        Assert.Contains("release-assets.githubusercontent.com", bootstrap);
        Assert.Contains("objects.githubusercontent.com", bootstrap);
        Assert.Contains("$RedirectCount -ge 5", bootstrap);
        Assert.Contains("ResponseHeadersRead", bootstrap);
        Assert.Contains("Get-AuthenticodeSignature", bootstrap);
        Assert.Contains("Add-AppxPackage -Path $BundlePath -ErrorAction Stop", bootstrap);
        Assert.Contains(
            "@(\"source\", \"export\", \"--name\", \"winget\", \"--disable-interactivity\")",
            bootstrap);
        Assert.Contains("\"SourceUpdateWinget\"", bootstrap);
        Assert.Contains("\"source\",", bootstrap);
        Assert.Contains("\"update\",", bootstrap);
        Assert.Contains("\"--name\", \"winget\"", bootstrap);
        Assert.Contains("\"--accept-source-agreements\"", bootstrap);
        Assert.Contains("\"--source\", \"winget\"", bootstrap);
        Assert.Contains("\"--id\", \"Git.Git\"", bootstrap);
        Assert.Contains("@(\"--version\")", bootstrap);
        Assert.Contains("$Operation -ceq \"SourceUpdateWinget\"", bootstrap);
        Assert.Contains("$process.WaitForExit($timeoutMilliseconds)", bootstrap);
        Assert.Contains("300000", bootstrap);
        Assert.Contains("60000", bootstrap);
        Assert.Contains("Remove-OpenClawWingetTemporaryRoot", bootstrap);
        Assert.Contains("Get-AppxPackage", bootstrap);
        Assert.Contains("Get-AppExecutionAlias", bootstrap);
        Assert.Contains("Microsoft\\WindowsApps\\winget.exe", bootstrap);
        Assert.Contains("AppxMetadata\\AppxBundleManifest.xml", bootstrap);
        Assert.Contains("ProcessorArchitecture", bootstrap);
        Assert.Contains("PackageDependency", bootstrap);
        Assert.Contains("ExecutionAlias", bootstrap);
        Assert.Contains("Id\") -ceq \"winget\"", bootstrap);
        Assert.Contains("Executable\") -cne \"winget.exe\"", bootstrap);

        Assert.DoesNotContain("aka.ms", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/latest", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.github", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-BitsTransfer", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("License1.xml", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-AppxProvisionedPackage", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-AllUsers", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source reset", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source remove", bootstrap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source add", bootstrap, StringComparison.OrdinalIgnoreCase);

        var downloadLoop = bootstrap.IndexOf(
            "Invoke-OpenClawWingetAssetDownload",
            bootstrap.IndexOf("function Invoke-OpenClawWingetBootstrap", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var topHashLoop = bootstrap.IndexOf(
            "Assert-OpenClawWingetFile",
            downloadLoop,
            StringComparison.Ordinal);
        var descriptorParse = bootstrap.IndexOf(
            "Assert-OpenClawWingetDependencyDescriptor",
            topHashLoop,
            StringComparison.Ordinal);
        var firstInstall = bootstrap.IndexOf(
            "Install-OpenClawWingetValidatedPackages",
            descriptorParse,
            StringComparison.Ordinal);
        Assert.True(downloadLoop >= 0);
        Assert.True(topHashLoop > downloadLoop);
        Assert.True(descriptorParse > topHashLoop);
        Assert.True(firstInstall > descriptorParse);

        Assert.Contains("-TimeoutSec 3000", ensure);
        Assert.Contains("-ScriptBlock (Get-GuestWingetBootstrapScriptBlock)", ensure);
        Assert.Contains("Get-RequiredGuestStageResult", ensure);
        Assert.Contains("valid source catalog acquisition", ensure);
        Assert.Contains("valid nonzero source catalog version", ensure);
        Assert.Contains("honest source catalog SHA256 evidence", ensure);
        Assert.DoesNotContain("-ArgumentList", ensure, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_WingetCatalogUsesDistinctMutableSignedTrustBoundary()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var bootstrap = ExtractPowerShellFunction(
            controller,
            "Get-GuestWingetBootstrapScriptBlock",
            "Ensure-GuestWingetAvailable");
        var catalogDownload = ExtractPowerShellFunction(
            controller,
            "Invoke-OpenClawWingetCatalogDownload",
            "Assert-OpenClawWingetFile");
        var catalogEvidence = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetCatalogFileEvidence",
            "Ensure-OpenClawWingetCatalog");
        var catalogWorkflow = ExtractPowerShellFunction(
            controller,
            "Ensure-OpenClawWingetCatalog",
            "Resolve-OpenClawWingetDirectExecutable");

        Assert.Contains(
            "https://cdn.winget.microsoft.com/cache/source2.msix",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("16777216", bootstrap, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Winget.Source", bootstrap, StringComparison.Ordinal);
        Assert.Contains("$handler.AllowAutoRedirect = $false", catalogDownload);
        Assert.Contains("$handler.UseCookies = $false", catalogDownload);
        Assert.Contains("$handler.UseDefaultCredentials = $false", catalogDownload);
        Assert.Contains("$handler.Credentials = $null", catalogDownload);
        Assert.Contains("$request.Headers.Authorization = $null", catalogDownload);
        Assert.Contains("ResponseHeadersRead", catalogDownload);
        Assert.Contains("$cancellation.CancelAfter", catalogDownload);
        Assert.Contains("$contentStream.ReadAsync(", catalogDownload);
        Assert.Contains("timed out after the bounded", catalogDownload);
        Assert.Contains("exceeded the maximum size while streaming", catalogDownload);
        Assert.Contains("Get-FileHash", catalogEvidence);
        Assert.Contains(
            "$catalogPath = Join-Path $TemporaryRoot \"source2.msix\"",
            catalogWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "exceeded the shared 1800-second timeout",
            catalogWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-TimeoutSeconds $remainingDownloadSeconds",
            catalogWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", bootstrap);
        Assert.Contains("SourceCatalogAcquisition", bootstrap);
        Assert.Contains("SourceCatalogVersion", bootstrap);
        Assert.Contains("SourceCatalogSha256", bootstrap);
        Assert.DoesNotContain("ExpectedSha256", catalogEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpectedVersion", catalogEvidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid", "accepted")]
    [InlineData("http", "exact official Microsoft HTTPS URL")]
    [InlineData("host", "exact official Microsoft HTTPS URL")]
    [InlineData("path", "exact official Microsoft HTTPS URL")]
    [InlineData("query", "exact official Microsoft HTTPS URL")]
    [InlineData("fragment", "exact official Microsoft HTTPS URL")]
    [InlineData("userinfo", "exact official Microsoft HTTPS URL")]
    [InlineData("port", "exact official Microsoft HTTPS URL")]
    public void HyperVController_WingetCatalogInitialUrlMustBeExact(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogInitialUriProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(302, "'https://cdn.winget.microsoft.com/cache/next.msix?sig=secret'", 0, "accepted|cdn.winget.microsoft.com")]
    [InlineData(302, "'/cache/next.msix'", 0, "accepted|cdn.winget.microsoft.com")]
    [InlineData(302, "'http://cdn.winget.microsoft.com/cache/next.msix'", 0, "unsafe redirect")]
    [InlineData(302, "'https://example.com/cache/next.msix'", 0, "unsafe redirect")]
    [InlineData(302, "'https://user@cdn.winget.microsoft.com/cache/next.msix'", 0, "unsafe redirect")]
    [InlineData(302, "'https://cdn.winget.microsoft.com/cache/next.msix#fragment'", 0, "unsafe redirect")]
    [InlineData(302, "'https://cdn.winget.microsoft.com:444/cache/next.msix'", 0, "unsafe redirect")]
    [InlineData(302, "$null", 0, "without a Location")]
    [InlineData(302, "'https://['", 0, "malformed Location")]
    [InlineData(302, "'https://cdn.winget.microsoft.com/cache/next.msix'", 5, "maximum of 5")]
    [InlineData(500, "$null", 0, "HTTP status 500")]
    public void HyperVController_WingetCatalogRedirectsStayOnExactMicrosoftHost(
        int statusCode,
        string locationExpression,
        int redirectCount,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogRedirectProof(
            statusCode,
            locationExpression,
            redirectCount));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=secret", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=secret", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid", "accepted")]
    [InlineData("no-header", "accepted")]
    [InlineData("zero-header", "Content-Length must be nonzero")]
    [InlineData("oversize-header", "Content-Length exceeds")]
    [InlineData("empty", "download is empty")]
    [InlineData("oversize-stream", "download exceeds")]
    [InlineData("mismatch", "does not match Content-Length")]
    public void HyperVController_WingetCatalogLengthValidationFailsClosed(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogLengthProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid", "accepted")]
    [InlineData("status", "Valid Authenticode")]
    [InlineData("publisher", "exact Microsoft signer")]
    public void HyperVController_WingetCatalogRequiresValidExactMicrosoftSignature(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogSignatureProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_WingetCatalogEvidenceUsesRuntimeHashAfterTrustChecks()
    {
        var result = RunPowerShellCommand(BuildWingetCatalogFileEvidenceProof());

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(
            "accepted|CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC|signature=1|identity=1",
            result.Stdout);
    }

    [Theory]
    [InlineData("valid", "accepted|7.8.9.10")]
    [InlineData("name", "exact name, publisher, and neutral architecture")]
    [InlineData("publisher", "exact name, publisher, and neutral architecture")]
    [InlineData("arch", "exact name, publisher, and neutral architecture")]
    [InlineData("version", "valid System.Version")]
    [InlineData("zero-version", "zero version")]
    public void HyperVController_WingetCatalogManifestRequiresExactMutableIdentity(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogManifestProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("existing", "accepted|existing|7.8.9.10")]
    [InlineData("missing", "accepted|missing|")]
    [InlineData("duplicate", "multiple Microsoft.Winget.Source")]
    [InlineData("name", "unexpected identity")]
    [InlineData("publisher", "unexpected identity")]
    [InlineData("arch", "unexpected identity")]
    [InlineData("version", "valid System.Version")]
    [InlineData("zero-version", "zero version")]
    public void HyperVController_WingetCatalogExistingRegistrationIsExactOrRefused(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogRegistrationStateProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("existing", "accepted|existing|7.8.9.10||downloads=0|installs=0")]
    [InlineData("missing", "accepted|downloaded|7.8.9.10|CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC|downloads=1|installs=1")]
    public void HyperVController_WingetCatalogSkipsValidExistingOrDownloadsValidatesAndInstalls(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogAcquisitionProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("?", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("retry", "accepted|3")]
    [InlineData("timeout", "bounded retry")]
    public void HyperVController_WingetCatalogRegistrationPollingIsBounded(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCatalogRegistrationWaitProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
        if (scenario == "timeout")
        {
            Assert.EndsWith("|3", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HyperVController_WingetCatalogPrecedesSourceOperationsInBothAppInstallerBranches()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var workflow = ExtractPowerShellRange(
            controller,
            "        function Invoke-OpenClawWingetBootstrap {",
            "        $publisher = \"CN=Microsoft Corporation");
        var firstCatalog = workflow.IndexOf(
            "Ensure-OpenClawWingetCatalog",
            StringComparison.Ordinal);
        var firstCli = workflow.IndexOf(
            "Assert-OpenClawWingetCli",
            firstCatalog,
            StringComparison.Ordinal);
        var firstEvidence = workflow.IndexOf(
            "SourceCatalogAcquisition",
            firstCli,
            StringComparison.Ordinal);
        var secondCatalog = workflow.IndexOf(
            "Ensure-OpenClawWingetCatalog",
            firstCatalog + 1,
            StringComparison.Ordinal);
        var secondCli = workflow.IndexOf(
            "Assert-OpenClawWingetCli",
            secondCatalog,
            StringComparison.Ordinal);
        var secondEvidence = workflow.IndexOf(
            "SourceCatalogAcquisition",
            secondCli,
            StringComparison.Ordinal);
        var cleanup = workflow.IndexOf(
            "Remove-OpenClawWingetTemporaryRoot -Path $temporaryRoot",
            secondEvidence,
            StringComparison.Ordinal);

        Assert.True(firstCatalog >= 0);
        Assert.True(firstCli > firstCatalog);
        Assert.True(firstEvidence > firstCli);
        Assert.True(secondCatalog > firstEvidence);
        Assert.True(secondCli > secondCatalog);
        Assert.True(secondEvidence > secondCli);
        Assert.True(cleanup > secondEvidence);
        Assert.Equal(
            2,
            workflow.Split('\n').Count(
                line => line.Contains(
                    "SourceCatalogSha256 = $catalogEvidence.Sha256",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void HyperVController_OrdersWingetAfterFinalWslAndBeforeEveryToolConsumer()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var prepare = ExtractPowerShellFunction(
            controller,
            "Prepare-GuestPrerequisites",
            "Verify-HostVmConfiguration");
        var finalWslIndex = prepare.IndexOf(
            "$wslProof = Invoke-GuestWslVerificationStage",
            StringComparison.Ordinal);
        var wingetIndex = prepare.IndexOf(
            "Ensure-GuestWingetAvailable",
            finalWslIndex,
            StringComparison.Ordinal);
        var gitIndex = prepare.IndexOf(
            "Ensure-GuestGitInstalled",
            wingetIndex,
            StringComparison.Ordinal);
        var powershellIndex = prepare.IndexOf(
            "Ensure-GuestPowerShell7Installed",
            gitIndex,
            StringComparison.Ordinal);
        var setupIndex = prepare.IndexOf(
            "Running guest setup-dev",
            powershellIndex,
            StringComparison.Ordinal);

        Assert.True(finalWslIndex >= 0);
        Assert.True(wingetIndex > finalWslIndex);
        Assert.True(gitIndex > wingetIndex);
        Assert.True(powershellIndex > gitIndex);
        Assert.True(setupIndex > powershellIndex);
        Assert.Equal(
            1,
            prepare.Split('\n').Count(
                line => line.Contains("Ensure-GuestWingetAvailable", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(
        "https://release-assets.githubusercontent.com/github-production-release-asset/file?sig=secret",
        "release-assets.githubusercontent.com")]
    [InlineData(
        "https://objects.githubusercontent.com/github-production-release-asset/file?jwt=secret",
        "objects.githubusercontent.com")]
    public void HyperVController_WingetRedirectPolicyAcceptsOnlyPinnedHttpsAssetHosts(
        string location,
        string expectedHost)
    {
        var result = RunPowerShellCommand(BuildWingetRedirectProof(
            statusCode: 302,
            locationExpression: PsQuote(location),
            redirectCount: 0));

        AssertPowerShellProofSucceeded(result);
        Assert.Equal($"accepted|{expectedHost}", result.Stdout);
        Assert.DoesNotContain("secret", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(302, "'http://release-assets.githubusercontent.com/file'", 0, "unsafe redirect")]
    [InlineData(302, "'https://example.com/file'", 0, "unsafe redirect")]
    [InlineData(302, "'/relative-stays-on-github'", 0, "unsafe redirect")]
    [InlineData(302, "$null", 0, "without a Location")]
    [InlineData(302, "'https://['", 0, "malformed Location")]
    [InlineData(302, "'https://release-assets.githubusercontent.com/file'", 5, "maximum of 5")]
    [InlineData(500, "$null", 0, "HTTP status 500")]
    public void HyperVController_WingetRedirectAndHttpFailuresFailClosed(
        int statusCode,
        string locationExpression,
        int redirectCount,
        string expectedError)
    {
        var result = RunPowerShellCommand(BuildWingetRedirectProof(
            statusCode,
            locationExpression,
            redirectCount));

        AssertPowerShellProofSucceeded(result);
        Assert.StartsWith("rejected|", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(expectedError, result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jwt=", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid", "accepted")]
    [InlineData("size", "unexpected size")]
    [InlineData("hash", "unexpected SHA256")]
    public void HyperVController_WingetFilePinRejectsSizeAndHashMismatch(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetFilePinProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "accepted|source,update,--name,winget,--accept-source-agreements,--disable-interactivity|300000")]
    [InlineData(true, "timed out after 300 seconds")]
    public void HyperVController_WingetSourceUpdateUsesTypedArgsAndBoundedTimeout(
        bool timeout,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetTrustedProcessProof(timeout));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
        if (timeout)
        {
            Assert.Contains("killed=True", result.Stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("valid", "accepted")]
    [InlineData("status", "Valid Authenticode")]
    [InlineData("publisher", "exact Microsoft signer")]
    public void HyperVController_WingetSignatureRequiresValidExactMicrosoftPublisher(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetSignatureProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid", "accepted")]
    [InlineData("reordered", "entry 0")]
    [InlineData("version", "entry 1")]
    [InlineData("extra-field", "entry 0")]
    [InlineData("extra-dependency", "dependency count")]
    [InlineData("extra-top-level", "top-level fields")]
    public void HyperVController_WingetDescriptorRequiresExactOrderedDependenciesWithNoExtras(
        string scenario,
        string expectedOutput)
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"winget-descriptor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var descriptorPath = Path.Combine(ownedRoot, "dependencies.json");
            File.WriteAllText(descriptorPath, BuildWingetDescriptorJson(scenario));
            var result = RunPowerShellCommand(BuildWingetDescriptorProof(descriptorPath));

            AssertPowerShellProofSucceeded(result);
            Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
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
    [InlineData("valid", "accepted")]
    [InlineData("identity", "exact pinned x64 identity")]
    [InlineData("dependency", "Microsoft.WindowsAppRuntime.1.8")]
    [InlineData("extra-dependency", "dependency count")]
    [InlineData("alias", "alias does not match")]
    public void HyperVController_WingetPayloadManifestRejectsIdentityDependencyAndAliasMismatch(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetPayloadManifestProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid", "accepted|AppInstaller_x64.msix")]
    [InlineData("identity", "identity does not match")]
    [InlineData("payload", "payload does not match")]
    [InlineData("duplicate", "exactly one nonstub x64")]
    public void HyperVController_WingetBundleManifestRequiresExactIdentityAndSingleX64Payload(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetBundleManifestProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("traversal", "unsafe entry path")]
    [InlineData("duplicate", "duplicate entry path")]
    [InlineData("missing", "missing pinned x64 package")]
    [InlineData("corrupt", "central directory")]
    [InlineData("existing-destination", "destination already exists")]
    public void HyperVController_WingetDependencyExtractionFailsClosed(
        string scenario,
        string expectedOutput)
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"winget-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var archivePath = Path.Combine(ownedRoot, "dependencies.zip");
            if (scenario == "corrupt")
            {
                File.WriteAllText(archivePath, "not a zip");
            }
            else
            {
                using var stream = File.Create(archivePath);
                using var archive = new System.IO.Compression.ZipArchive(
                    stream,
                    System.IO.Compression.ZipArchiveMode.Create);
                if (scenario == "traversal")
                {
                    WriteZipEntry(archive, "../escape.appx", new byte[] { 1 });
                }
                else if (scenario == "duplicate")
                {
                    WriteZipEntry(archive, "x64/pinned.appx", new byte[] { 1 });
                    WriteZipEntry(archive, @"x64\pinned.appx", new byte[] { 1 });
                }
                else if (scenario == "missing")
                {
                    WriteZipEntry(archive, "x64/other.appx", new byte[] { 1 });
                }
                else
                {
                    WriteZipEntry(archive, "x64/pinned.appx", new byte[] { 1 });
                }
            }

            var destination = Path.Combine(ownedRoot, "extracted");
            if (scenario == "existing-destination")
            {
                Directory.CreateDirectory(destination);
            }
            var result = RunPowerShellCommand(
                BuildWingetDependencyExtractionProof(archivePath, destination));

            AssertPowerShellProofSucceeded(result);
            Assert.StartsWith("rejected|", result.Stdout, StringComparison.Ordinal);
            Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
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
    public void HyperVController_WingetZipXmlReaderParsesNamespaceManifestFailClosed()
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"winget-xml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var archivePath = Path.Combine(ownedRoot, "pinned.appx");
            using (var stream = File.Create(archivePath))
            using (var archive = new System.IO.Compression.ZipArchive(
                       stream,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteZipEntry(
                    archive,
                    "AppxManifest.xml",
                    System.Text.Encoding.UTF8.GetBytes(
                        "<Package xmlns=\"urn:test\"><Identity Name=\"Pinned\" /></Package>"));
            }

            var result = RunPowerShellCommand(BuildWingetZipXmlProof(archivePath));

            AssertPowerShellProofSucceeded(result);
            Assert.Equal("Package|Pinned", result.Stdout);
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
    public void HyperVController_WingetInstallsValidatedX64DependenciesInPinnedOrderBeforeBundle()
    {
        var result = RunPowerShellCommand(BuildWingetInstallOrderProof());

        AssertPowerShellProofSucceeded(result);
        Assert.Equal(
            "dep1.appx|dep2.appx|dep3.appx|bundle.msixbundle",
            result.Stdout);
    }

    [Theory]
    [InlineData("exact", "accepted|skip")]
    [InlineData("older", "accepted|install")]
    [InlineData("missing", "accepted|install")]
    [InlineData("x86-only", "accepted|install")]
    [InlineData("newer", "newer than the reproducible pin")]
    [InlineData("publisher", "unexpected identity")]
    public void HyperVController_WingetDependencyRegistrationNeverAcceptsUnexpectedX64Package(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetDependencyStateProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "accepted")]
    [InlineData(true, "cleanup rejected")]
    public void HyperVController_WingetAlreadyInstalledSkipsDownloadsAndCleanupIsAuthoritative(
        bool cleanupFails,
        string expectedOutput)
    {
        var ownedRoot = Path.Combine(
            Root,
            "TestResults",
            $"winget-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var result = RunPowerShellCommand(
                BuildWingetAlreadyInstalledProof(ownedRoot, cleanupFails));

            AssertPowerShellProofSucceeded(result);
            Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("downloads=0", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("installs=0", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("validated=1", result.Stdout, StringComparison.Ordinal);
            if (!cleanupFails)
            {
                Assert.Contains("catalog=existing", result.Stdout, StringComparison.Ordinal);
                Assert.Contains("catalogHashNull=True", result.Stdout, StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFileSystemEntries(ownedRoot));
            }
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
    [InlineData("retry", "accepted|3")]
    [InlineData("publisher", "exact pinned identity")]
    [InlineData("newer", "newer than the reproducible pin")]
    public void HyperVController_WingetRegistrationUsesBoundedExactCurrentUserRetry(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetRegistrationProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("valid", "accepted|Version,SourceExportWinget,SourceUpdateWinget,SourceProbeGit")]
    [InlineData("version", "--version did not return exactly")]
    [InlineData("source-exit", "source export")]
    [InlineData("source-name", "exactly one source named winget")]
    [InlineData("source-json", "valid JSON")]
    [InlineData("source-update-exit", "exitCode=17; stdout=update-out; stderr=update-err")]
    [InlineData("source-probe-exit", "exitCode=23; stdout=probe-out; stderr=probe-err")]
    [InlineData("source-probe-package", "exitCode=0; stdout=Found Unexpected.Package")]
    public void HyperVController_WingetCliRequiresExactVersionAndSourceValidation(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetCliValidationProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_WingetSourceValidationExportsThenUpdatesThenProbes()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var validation = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCli",
            "Remove-OpenClawWingetTemporaryRoot");

        var exportIndex = validation.IndexOf(
            "-Operation \"SourceExportWinget\"",
            StringComparison.Ordinal);
        var updateIndex = validation.IndexOf(
            "-Operation \"SourceUpdateWinget\"",
            StringComparison.Ordinal);
        var probeIndex = validation.IndexOf(
            "-Operation \"SourceProbeGit\"",
            StringComparison.Ordinal);

        Assert.True(exportIndex >= 0);
        Assert.True(updateIndex > exportIndex);
        Assert.True(probeIndex > updateIndex);
    }

    [Fact]
    public void WingetInstallsUseOnlyTheExplicitCommunitySource()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var setupDev = File.ReadAllText(Path.Combine(Root, "scripts", "setup-dev.ps1"));
        var setupInstall = ExtractPowerShellFunction(
            setupDev,
            "Install-WingetPackage",
            "ConvertTo-GitSafeDirectoryPath");

        Assert.Contains(
            "winget install --id Git.Git -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity",
            controller,
            StringComparison.Ordinal);
        Assert.Contains(
            "$script:GuestPowerShellWingetVersion = \"7.6.4.0\"",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("\"--installer-type\", \"wix\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"--scope\", \"machine\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"--source\", \"winget\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"--source\", \"winget\"", setupInstall, StringComparison.Ordinal);
        Assert.Contains("\"--accept-source-agreements\"", setupInstall, StringComparison.Ordinal);
        Assert.Contains("\"--accept-package-agreements\"", setupInstall, StringComparison.Ordinal);
        Assert.Contains("\"--disable-interactivity\"", setupInstall, StringComparison.Ordinal);
        Assert.DoesNotContain("msstore", controller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("msstore", setupInstall, StringComparison.OrdinalIgnoreCase);
        foreach (var packageId in new[]
        {
            "Git.Git",
            "Microsoft.PowerShell",
            "Microsoft.DotNet.SDK.10",
            "OpenJS.NodeJS.LTS",
            "Microsoft.WindowsSDK.10.0.26100",
            "Microsoft.EdgeWebView2Runtime",
        })
        {
            Assert.Contains(packageId, controller + setupDev, StringComparison.Ordinal);
        }

        var executableInstallLines = Directory
            .EnumerateFiles(Path.Combine(Root, "scripts"), "*.ps1", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines)
            .Select(line => line.TrimStart())
            .Where(line =>
                line.StartsWith("winget install ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("& winget install ", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(executableInstallLines);
        Assert.All(
            executableInstallLines,
            line => Assert.Contains("--source winget", line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GuestPowerShell7InstallPinsExactMachineWixAndValidatesExactEngine()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var worker = ExtractPowerShellFunction(
            controller,
            "Get-GuestPowerShell7InstallScriptBlock",
            "Ensure-GuestPowerShell7Installed");

        Assert.Contains("\"--id\", \"Microsoft.PowerShell\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--version\", $PackageVersion", worker, StringComparison.Ordinal);
        Assert.Contains("\"--installer-type\", \"wix\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--scope\", \"machine\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--source\", \"winget\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--accept-source-agreements\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--accept-package-agreements\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--disable-interactivity\"", worker, StringComparison.Ordinal);
        Assert.Contains("PowerShell\\7\\pwsh.exe", worker, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSVersion.ToString()", worker, StringComparison.Ordinal);
        Assert.Contains("[StringComparison]::Ordinal", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("\"msix\"", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--force", worker, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("install", "InstallPinnedWix,ReadInstalledVersion", "7.6.4", null)]
    [InlineData("existing", "ReadInstalledVersion", "7.6.4", null)]
    [InlineData("wrong-version", "InstallPinnedWix,ReadInstalledVersion", "7.6.3", "expected exact version '7.6.4'")]
    [InlineData("wrong-path", "InstallPinnedWix,ReadInstalledVersion", "7.6.4", "expected trusted machine path")]
    public void GuestPowerShell7InstallValidatesInstallPathAndVersion(
        string scenario,
        string expectedCalls,
        string reportedVersion,
        string? expectedError)
    {
        var result = RunPowerShellCommand(BuildPowerShell7InstallProof(
            scenario,
            reportedVersion,
            installExitCode: 0));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var actualCalls = root.GetProperty("calls").GetString();
        var capturedError = root.GetProperty("error").GetString();
        Assert.True(
            string.Equals(expectedCalls, actualCalls, StringComparison.Ordinal),
            $"Expected calls '{expectedCalls}', got '{actualCalls}'. Error: {capturedError}");
        var arguments = root.GetProperty("installArguments").GetString() ?? string.Empty;
        if (scenario != "existing")
        {
            Assert.Contains(
                "install|--id|Microsoft.PowerShell|-e|--version|7.6.4.0|--installer-type|wix|--scope|machine|--source|winget",
                arguments,
                StringComparison.Ordinal);
            Assert.DoesNotContain("msix", arguments, StringComparison.OrdinalIgnoreCase);
        }

        var error = capturedError;
        if (expectedError is null)
        {
            Assert.True(string.IsNullOrEmpty(error), error);
        }
        else
        {
            Assert.Contains(expectedError, error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GuestPowerShell7InstallReportsKnownAppxSessionFailureWithoutRetry()
    {
        var result = RunPowerShellCommand(BuildPowerShell7InstallProof(
            "appx-failure",
            reportedVersion: "7.6.4",
            installExitCode: -2147009255));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var error = root.GetProperty("error").GetString() ?? string.Empty;
        Assert.True(
            string.Equals(
                "InstallPinnedWix",
                root.GetProperty("calls").GetString(),
                StringComparison.Ordinal),
            $"Expected one pinned Wix install call. Error: {error}");
        Assert.Contains("-2147009255", error, StringComparison.Ordinal);
        Assert.Contains("0x80073D19", error, StringComparison.Ordinal);
        Assert.Contains("AppX deployment-session/user-logged-off", error, StringComparison.Ordinal);
        Assert.Contains("must not fall back to MSIX", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GuestDeveloperPrerequisitesUseExactPinnedPackageSelectionsBeforeSourceTransfer()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var worker = ExtractPowerShellFunction(
            controller,
            "Get-GuestDeveloperPrerequisiteScriptBlock",
            "Get-GuestBootIdentity");
        var orchestration = ExtractPowerShellFunction(
            controller,
            "Ensure-GuestDeveloperPrerequisites",
            "Get-GuestSetupDevCheckScriptBlock");
        var setupCheck = ExtractPowerShellFunction(
            controller,
            "Get-GuestSetupDevCheckScriptBlock",
            "Prepare-GuestPrerequisites");
        var prepare = ExtractPowerShellFunction(
            controller,
            "Prepare-GuestPrerequisites",
            "Verify-HostVmConfiguration");

        foreach (var spec in new (string Id, string Version, string InstallerType, string? Scope)[]
        {
            ("Microsoft.DotNet.SDK.10", "10.0.302", "burn", null),
            ("OpenJS.NodeJS.LTS", "24.18.0", "wix", "machine"),
            ("Microsoft.WindowsSDK.10.0.26100", "10.0.26100.7705", "burn", "machine"),
            ("Microsoft.EdgeWebView2Runtime", "150.0.4078.83", "exe", "machine"),
        })
        {
            Assert.Contains($"id = \"{spec.Id}\"", worker, StringComparison.Ordinal);
            Assert.Contains($"version = \"{spec.Version}\"", worker, StringComparison.Ordinal);
            Assert.Contains($"installerType = \"{spec.InstallerType}\"", worker, StringComparison.Ordinal);
            Assert.Contains(
                spec.Scope is null ? "scope = $null" : $"scope = \"{spec.Scope}\"",
                worker,
                StringComparison.Ordinal);
        }
        Assert.Equal(3, CountOccurrences(worker, "scope = \"machine\""));
        Assert.Contains("if ($null -ne $scope)", worker, StringComparison.Ordinal);
        Assert.Contains("$installArguments += @(\"--scope\", $scope)", worker, StringComparison.Ordinal);
        Assert.Contains(
            "Join-Path $env:ProgramFiles \"dotnet\\dotnet.exe\"",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("[StringComparison]::OrdinalIgnoreCase", worker, StringComparison.Ordinal);
        Assert.Contains("\"--source\", \"winget\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--silent\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--accept-source-agreements\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--accept-package-agreements\"", worker, StringComparison.Ordinal);
        Assert.Contains("\"--disable-interactivity\"", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("msix", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--force", worker, StringComparison.OrdinalIgnoreCase);

        var dotnetIndex = orchestration.IndexOf("\"DotNet10\"", StringComparison.Ordinal);
        var nodeIndex = orchestration.IndexOf("\"NodeLts\"", StringComparison.Ordinal);
        var sdkIndex = orchestration.IndexOf("\"WindowsSdk26100\"", StringComparison.Ordinal);
        var webViewIndex = orchestration.IndexOf("\"WebView2\"", StringComparison.Ordinal);
        Assert.True(dotnetIndex >= 0);
        Assert.True(nodeIndex > dotnetIndex);
        Assert.True(sdkIndex > nodeIndex);
        Assert.True(webViewIndex > sdkIndex);

        var wslIndex = prepare.IndexOf("Invoke-GuestWslVerificationStage", StringComparison.Ordinal);
        var wingetIndex = prepare.IndexOf("Ensure-GuestWingetAvailable", StringComparison.Ordinal);
        var gitIndex = prepare.IndexOf("Ensure-GuestGitInstalled", StringComparison.Ordinal);
        var powerShellIndex = prepare.IndexOf("Ensure-GuestPowerShell7Installed", StringComparison.Ordinal);
        var prerequisitesIndex = prepare.IndexOf("Ensure-GuestDeveloperPrerequisites", StringComparison.Ordinal);
        var sourceIndex = prepare.IndexOf("Copy-RepoToGuest", StringComparison.Ordinal);
        var checkIndex = prepare.IndexOf("Get-GuestSetupDevCheckScriptBlock", StringComparison.Ordinal);
        Assert.True(wingetIndex > wslIndex);
        Assert.True(gitIndex > wingetIndex);
        Assert.True(powerShellIndex > gitIndex);
        Assert.True(prerequisitesIndex > powerShellIndex);
        Assert.True(sourceIndex > prerequisitesIndex);
        Assert.True(checkIndex > sourceIndex);

        Assert.Contains("\"-CheckOnly\"", setupCheck, StringComparison.Ordinal);
        Assert.Contains("setup-dev.ps1 -CheckOnly", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "& powershell.exe -NoProfile -ExecutionPolicy Bypass -File",
            prepare,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GuestDeveloperPrerequisiteOrchestrationVerifiesRebootAndNeverBlindlyReinstalls()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var ensure = ExtractPowerShellFunction(
            controller,
            "Ensure-GuestDeveloperPrerequisite",
            "Ensure-GuestDeveloperPrerequisites");
        var reconnect = ExtractPowerShellFunction(
            controller,
            "Wait-ForGuestPackageRebootAndReconnect",
            "Invoke-GuestDeveloperPrerequisiteWorker");

        Assert.Contains("Get-GuestBootIdentity", ensure, StringComparison.Ordinal);
        Assert.Contains("Probing guest session after package", ensure, StringComparison.Ordinal);
        Assert.Contains("Wait-ForGuestPackageRebootAndReconnect", ensure, StringComparison.Ordinal);
        Assert.Contains("Restart-GuestAndReconnect", ensure, StringComparison.Ordinal);
        Assert.Contains("-VerifyOnly $true", ensure, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(ensure, "-VerifyOnly $false"));
        Assert.Contains("[Int64]$currentBootTicks -gt $PreviousBootTicks", reconnect, StringComparison.Ordinal);
        Assert.Contains("Assert-OwnedVM", reconnect, StringComparison.Ordinal);
        Assert.Contains("exact owned VM", reconnect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lost its guest session without a verified", ensure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "Installing guest developer prerequisite 'DotNet10'")]
    [InlineData(true, "Verifying guest developer prerequisite 'DotNet10'")]
    public void GuestDeveloperPrerequisiteWorkerRunsActualControlPathUnderPowerShell5(
        bool verifyOnly,
        string expectedOperationName)
    {
        var result = RunPowerShellCommand(
            BuildDeveloperPrerequisiteWorkerControlProof(verifyOnly));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
        Assert.Equal(5, proof.GetProperty("powerShellMajor").GetInt32());
        Assert.Equal(expectedOperationName, proof.GetProperty("operationName").GetString());
        Assert.Equal(verifyOnly, proof.GetProperty("argumentVerifyOnly").GetBoolean());
        Assert.Equal("DotNet10", proof.GetProperty("packageKey").GetString());

        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var stagedCode = ExtractPowerShellFunction(
            controller,
            "Get-GuestDeveloperPrerequisiteScriptBlock",
            "Prepare-GuestPrerequisites");
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(
                stagedCode,
                @"-\w+\s+\(\s*(if|switch|foreach|try)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "Staged prerequisite code must not pass a statement block directly as a command argument.");
    }

    [Theory]
    [InlineData("DotNet10")]
    [InlineData("NodeLts")]
    [InlineData("WindowsSdk26100")]
    [InlineData("WebView2")]
    public void GuestDeveloperPrerequisiteAlreadyPresentSkipsInstall(string packageKey)
    {
        var result = RunPowerShellCommand(
            BuildDeveloperPrerequisiteProof(packageKey, "existing", installExitCode: 0));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
        Assert.True(proof.GetProperty("verified").GetBoolean());
        Assert.True(proof.GetProperty("alreadyInstalled").GetBoolean());
        Assert.DoesNotContain(
            "Install:",
            proof.GetProperty("calls").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DotNet10", "Microsoft.DotNet.SDK.10", null)]
    [InlineData("NodeLts", "OpenJS.NodeJS.LTS", "machine")]
    [InlineData("WindowsSdk26100", "Microsoft.WindowsSDK.10.0.26100", "machine")]
    [InlineData("WebView2", "Microsoft.EdgeWebView2Runtime", "machine")]
    public void GuestDeveloperPrerequisiteInstallsAndVerifiesOneExactPackage(
        string packageKey,
        string packageId,
        string? expectedScope)
    {
        var result = RunPowerShellCommand(
            BuildDeveloperPrerequisiteProof(packageKey, "install", installExitCode: 0));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
        Assert.True(proof.GetProperty("verified").GetBoolean());
        Assert.False(proof.GetProperty("alreadyInstalled").GetBoolean());
        Assert.Equal(1, CountOccurrences(
            proof.GetProperty("calls").GetString() ?? string.Empty,
            "Install:"));
        var arguments = proof.GetProperty("installArguments").GetString() ?? string.Empty;
        Assert.Contains($"--id|{packageId}|-e|--version", arguments, StringComparison.Ordinal);
        Assert.Contains("--source|winget|--silent", arguments, StringComparison.Ordinal);
        var scope = proof.GetProperty("scope");
        if (expectedScope is null)
        {
            Assert.DoesNotContain("--scope", arguments, StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, scope.ValueKind);
        }
        else
        {
            Assert.Contains(
                $"--scope|{expectedScope}|--source|winget|--silent",
                arguments,
                StringComparison.Ordinal);
            Assert.Equal(expectedScope, scope.GetString());
        }
        Assert.DoesNotContain("msix", arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DotNet10", "Microsoft.DotNet.SDK.10", "inherent")]
    [InlineData("NodeLts", "OpenJS.NodeJS.LTS", "machine")]
    [InlineData("WindowsSdk26100", "Microsoft.WindowsSDK.10.0.26100", "machine")]
    [InlineData("WebView2", "Microsoft.EdgeWebView2Runtime", "machine")]
    public void GuestDeveloperPrerequisiteFailureHasPackageSpecificBoundedDiagnostics(
        string packageKey,
        string packageId,
        string expectedScope)
    {
        var result = RunPowerShellCommand(
            BuildDeveloperPrerequisiteProof(packageKey, "failure", installExitCode: 23));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        var error = proof.GetProperty("error").GetString() ?? string.Empty;
        Assert.Contains(packageKey, error, StringComparison.Ordinal);
        Assert.Contains(packageId, error, StringComparison.Ordinal);
        Assert.Contains($"scope={expectedScope}", error, StringComparison.Ordinal);
        Assert.Contains("exit code 23 (0x00000017)", error, StringComparison.Ordinal);
        Assert.Contains("stdout='installer output'", error, StringComparison.Ordinal);
        Assert.Contains("stderr='installer error'", error, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(
            proof.GetProperty("calls").GetString() ?? string.Empty,
            "Install:"));
    }

    [Theory]
    [InlineData(3010, false)]
    [InlineData(-1978334967, false)]
    [InlineData(1641, true)]
    [InlineData(-1978334965, true)]
    public void GuestDeveloperPrerequisiteRebootResultDefersVerificationWithoutDuplicateInstall(
        int exitCode,
        bool rebootInitiated)
    {
        var result = RunPowerShellCommand(
            BuildDeveloperPrerequisiteProof(
                "WindowsSdk26100",
                "reboot",
                installExitCode: exitCode));

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
        Assert.True(proof.GetProperty("needsRestart").GetBoolean());
        Assert.False(proof.GetProperty("verified").GetBoolean());
        Assert.Equal(rebootInitiated, proof.GetProperty("rebootInitiated").GetBoolean());
        Assert.Equal(exitCode, proof.GetProperty("installExitCode").GetInt32());
        Assert.Equal(1, CountOccurrences(
            proof.GetProperty("calls").GetString() ?? string.Empty,
            "Install:"));
    }

    [Fact]
    public void GuestSetupDevCheckInvokesCheckOnlyThroughBoundedNativeCapture()
    {
        var guestRoot = Path.Combine(Path.GetTempPath(), $"openclaw-setup-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(guestRoot, "scripts"));
        File.WriteAllText(
            Path.Combine(guestRoot, "scripts", "setup-dev.ps1"),
            "throw 'test should use mocked native process'");
        try
        {
            var result = RunPowerShellCommand(BuildSetupDevCheckProof(guestRoot));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
            Assert.True(proof.GetProperty("checkOnly").GetBoolean());
            var arguments = proof.GetProperty("arguments").GetString() ?? string.Empty;
            Assert.Contains("-File", arguments, StringComparison.Ordinal);
            Assert.Contains("-CheckOnly", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-RunValidation", arguments, StringComparison.Ordinal);
            Assert.True(proof.GetProperty("waitWasBounded").GetBoolean());
        }
        finally
        {
            DeleteTestDirectory(guestRoot);
        }
    }

    [Theory]
    [InlineData("retry", "accepted|3")]
    [InlineData("timeout", "bounded retry")]
    [InlineData("mismatch", "outside the current-user Microsoft WindowsApps alias")]
    public void HyperVController_WingetAliasCheckRetriesAndRejectsPathMismatch(
        string scenario,
        string expectedOutput)
    {
        var result = RunPowerShellCommand(BuildWingetAliasProof(scenario));

        AssertPowerShellProofSucceeded(result);
        Assert.Contains(expectedOutput, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVController_OrdersBothConditionalRebootsAroundPackageStages()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var prepare = ExtractPowerShellFunction(
            controller,
            "Prepare-GuestPrerequisites",
            "Verify-HostVmConfiguration");
        var featureIndex = prepare.IndexOf(
            "Invoke-GuestOptionalFeatureStage",
            StringComparison.Ordinal);
        var firstGuardIndex = prepare.IndexOf(
            "if ([bool]$featureResult.needsRestart)",
            featureIndex,
            StringComparison.Ordinal);
        var firstRestartIndex = prepare.IndexOf(
            "Restart-GuestAndReconnect",
            firstGuardIndex,
            StringComparison.Ordinal);
        var packageIndex = prepare.IndexOf(
            "Invoke-GuestWslPackageStage",
            firstRestartIndex,
            StringComparison.Ordinal);
        var secondGuardIndex = prepare.IndexOf(
            "if ([bool]$packageResult.needsRestart)",
            packageIndex,
            StringComparison.Ordinal);
        var secondRestartIndex = prepare.IndexOf(
            "Restart-GuestAndReconnect",
            secondGuardIndex,
            StringComparison.Ordinal);
        var helperAfterSecondReconnectIndex = prepare.IndexOf(
            "Install-GuestWslNativeHelper",
            secondRestartIndex,
            StringComparison.Ordinal);
        var finalVerificationIndex = prepare.IndexOf(
            "Invoke-GuestWslVerificationStage",
            helperAfterSecondReconnectIndex,
            StringComparison.Ordinal);

        Assert.True(featureIndex >= 0);
        Assert.True(firstGuardIndex > featureIndex);
        Assert.True(firstRestartIndex > firstGuardIndex);
        Assert.True(packageIndex > firstRestartIndex);
        Assert.True(secondGuardIndex > packageIndex);
        Assert.True(secondRestartIndex > secondGuardIndex);
        Assert.True(helperAfterSecondReconnectIndex > secondRestartIndex);
        Assert.True(finalVerificationIndex > helperAfterSecondReconnectIndex);
        Assert.Equal(
            2,
            prepare.Split('\n').Count(
                line => line.Contains("Restart-GuestAndReconnect", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            prepare.Split('\n').Count(
                line => line.Contains(
                    "$packageResult = Invoke-GuestWslPackageStage",
                    StringComparison.Ordinal)));
        Assert.Equal(
            1,
            prepare.Split('\n').Count(
                line => line.Contains(
                    "$wslProof = Invoke-GuestWslVerificationStage",
                    StringComparison.Ordinal)));
        Assert.Contains("-ResolvedVhdPath $ResolvedVhdPath", prepare);
        Assert.Contains("-ExpectedOwnerId $ExpectedOwnerId", prepare);
    }

    [Fact]
    public void HyperVController_PrepareFailureRestoresExactCleanCheckpoint()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var prepare = ExtractPowerShellFunction(
            controller,
            "Invoke-PrepareCommand",
            "Invoke-VerifyCommand");
        var prerequisitesIndex = prepare.IndexOf(
            "$session = Prepare-GuestPrerequisites",
            StringComparison.Ordinal);
        var catchIndex = prepare.IndexOf("} catch {", prerequisitesIndex, StringComparison.Ordinal);
        var stopIndex = prepare.IndexOf(
            "Stop-VMGracefully",
            catchIndex,
            StringComparison.Ordinal);
        var restoreIndex = prepare.IndexOf(
            "Restore-OwnedCheckpoint",
            stopIndex,
            StringComparison.Ordinal);
        var cleanCheckpointIndex = prepare.IndexOf(
            "-OwnedCheckpointName $script:CleanCheckpointName",
            restoreIndex,
            StringComparison.Ordinal);
        var throwIndex = prepare.IndexOf(
            "        throw",
            cleanCheckpointIndex,
            StringComparison.Ordinal);

        Assert.True(prerequisitesIndex >= 0);
        Assert.True(catchIndex > prerequisitesIndex);
        Assert.True(stopIndex > catchIndex);
        Assert.True(restoreIndex > stopIndex);
        Assert.True(cleanCheckpointIndex > restoreIndex);
        Assert.True(throwIndex > cleanCheckpointIndex);
        var rollback = prepare[catchIndex..throwIndex];
        Assert.DoesNotContain("Recover-PendingOwnedCheckpoint", rollback, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanupUnattend", rollback, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVController_FailedGuestJobCapturesBoundedDiagnosticsBeforeRemoval()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var result = RunPowerShellCommand(BuildFailedGuestJobDiagnosticProof());

        AssertPowerShellProofSucceeded(result);
        using var document = JsonDocument.Parse(result.Stdout);
        var proof = document.RootElement;
        var diagnostic = proof.GetProperty("diagnostic").GetString()!;
        Assert.Equal("Failed", proof.GetProperty("jobState").GetString());
        Assert.True(proof.GetProperty("jobRemoved").GetBoolean());
        Assert.True(diagnostic.Length <= 3084);
        Assert.Contains("reasonType=", diagnostic, StringComparison.Ordinal);
        Assert.Contains("child-reason", diagnostic, StringComparison.Ordinal);
        Assert.Contains("errors=", diagnostic, StringComparison.Ordinal);
        Assert.Contains("child-error", diagnostic, StringComparison.Ordinal);
        Assert.Contains("output=", diagnostic, StringComparison.Ordinal);
        Assert.Contains("[truncated]", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-print", diagnostic, StringComparison.Ordinal);

        var invocation = ExtractPowerShellFunction(
            controller,
            "Invoke-GuestCommandWithTimeout",
            "Restart-GuestAndReconnect");
        var stateIndex = invocation.IndexOf(
            "if ($job.State -ne \"Completed\")",
            StringComparison.Ordinal);
        var diagnosticsIndex = invocation.IndexOf(
            "Get-FailedGuestJobDiagnostic -Job $job",
            stateIndex,
            StringComparison.Ordinal);
        var throwIndex = invocation.IndexOf(
            "Guest job diagnostics:",
            diagnosticsIndex,
            StringComparison.Ordinal);
        var removeIndex = invocation.IndexOf(
            "Remove-Job -Job $job",
            throwIndex,
            StringComparison.Ordinal);
        Assert.True(stateIndex >= 0);
        Assert.True(diagnosticsIndex > stateIndex);
        Assert.True(throwIndex > diagnosticsIndex);
        Assert.True(removeIndex > throwIndex);
    }

    [Fact]
    public void HyperVController_GuestCommandTimeoutSemanticsRemainBoundedAndRemoveJob()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var invocation = ExtractPowerShellFunction(
            controller,
            "Invoke-GuestCommandWithTimeout",
            "Restart-GuestAndReconnect");

        var waitIndex = invocation.IndexOf(
            "Wait-Job -Job $job -Timeout $TimeoutSec",
            StringComparison.Ordinal);
        var stopIndex = invocation.IndexOf(
            "Stop-Job -Job $job -Force",
            waitIndex,
            StringComparison.Ordinal);
        var timeoutIndex = invocation.IndexOf(
            "timed out after $TimeoutSec seconds.",
            stopIndex,
            StringComparison.Ordinal);
        var diagnosticsIndex = invocation.IndexOf(
            "Get-FailedGuestJobDiagnostic",
            timeoutIndex,
            StringComparison.Ordinal);
        var removeIndex = invocation.IndexOf(
            "Remove-Job -Job $job -Force",
            diagnosticsIndex,
            StringComparison.Ordinal);

        Assert.True(waitIndex >= 0);
        Assert.True(stopIndex > waitIndex);
        Assert.True(timeoutIndex > stopIndex);
        Assert.True(diagnosticsIndex > timeoutIndex);
        Assert.True(removeIndex > diagnosticsIndex);
    }

    [Fact]
    public void HyperVController_OwnsCheckpointTransportArtifactAndRestoreContract()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");

        Assert.Contains("$script:CleanCheckpointName = \"clean-windows\"", script);
        Assert.Contains("$script:PreparedCheckpointName = \"openclaw-prerequisites\"", script);
        Assert.Contains("git archive", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-LiteralPath $sourceArchive.ArchivePath", script, StringComparison.Ordinal);
        Assert.Contains("-Destination $guestArchivePath", script, StringComparison.Ordinal);
        Assert.Contains("-ToSession $Session", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-RepoTransferItems", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Copy-Item -LiteralPath $sourcePath -Destination $guestRepoRoot",
            script,
            StringComparison.Ordinal);
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
        Assert.Contains("$script:GuestPowerShellWingetVersion = \"7.6.4.0\"", script);
        Assert.Contains("\"--installer-type\", \"wix\"", script);
        Assert.Contains("\"--scope\", \"machine\"", script);
        Assert.Contains("\"--source\", \"winget\"", script);
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
    public void HyperVController_RequiresCleanHeadAndOneValidatedArchiveTransfer()
    {
        var script = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var createArchive = ExtractPowerShellFunction(
            script,
            "New-CleanWindowsSourceArchive",
            "Copy-RepoToGuest");
        var transfer = ExtractPowerShellFunction(
            script,
            "Copy-RepoToGuest",
            "Get-GuestWingetBootstrapScriptBlock");
        var staging = ExtractPowerShellFunction(
            script,
            "Get-GuestSourceStagingScriptBlock",
            "New-CleanWindowsSourceArchive");

        Assert.Contains(
            "@(\"status\", \"--porcelain=v1\", \"--untracked-files=all\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "@(\"archive\", \"--format=zip\", \"--output=$ArchivePath\", \"HEAD\")",
            createArchive,
            StringComparison.Ordinal);
        Assert.Contains("Assert-CleanCommittedSourceHead", createArchive, StringComparison.Ordinal);
        Assert.Contains("Source HEAD changed", createArchive, StringComparison.Ordinal);
        Assert.Contains("[Security.Cryptography.SHA256]::Create()", createArchive, StringComparison.Ordinal);
        Assert.Contains("$script:SourceArchiveMaximumBytes = 268435456", script, StringComparison.Ordinal);
        Assert.Contains("$script:SourceArchiveMaximumTrackedFiles = 20000", script, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit($TimeoutSec * 1000)", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $process.Id -Force", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item `", transfer, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(transfer, "-ToSession $Session"));
        Assert.DoesNotContain("-Recurse", ExtractPowerShellRange(
            transfer,
            "            Copy-Item `",
            "        } catch {"), StringComparison.Ordinal);
        Assert.Contains("SHA256 mismatch", script, StringComparison.Ordinal);
        Assert.Contains("Expand-Archive", script, StringComparison.Ordinal);
        Assert.Contains("openclaw.clean-windows.source-provenance/v1", script, StringComparison.Ordinal);
        Assert.Contains("Guest staging from $SourceHead", staging, StringComparison.Ordinal);
        Assert.Contains("@(\"config\", \"--local\", \"core.autocrlf\", \"false\")", staging, StringComparison.Ordinal);
        Assert.Contains("@(\"config\", \"--local\", \"core.safecrlf\", \"true\")", staging, StringComparison.Ordinal);
        var stagingWorkflow = ExtractPowerShellRange(
            staging,
            "            foreach ($operation in @(",
            "            $statusResult = Invoke-OpenClawGuestGitProcess");
        Assert.True(
            stagingWorkflow.IndexOf("\"ConfigAutoCrlf\"", StringComparison.Ordinal) <
            stagingWorkflow.IndexOf("\"AddAll\"", StringComparison.Ordinal));
        Assert.True(
            stagingWorkflow.IndexOf("\"ConfigSafeCrlf\"", StringComparison.Ordinal) <
            stagingWorkflow.IndexOf("\"AddAll\"", StringComparison.Ordinal));
        Assert.Contains("$beforeSha256", staging, StringComparison.Ordinal);
        Assert.Contains("$afterSha256", staging, StringComparison.Ordinal);
        Assert.Contains("Guest Git staging mutated extracted source bytes.", staging, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardOutput $stdoutPath", staging, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardError $stderrPath", staging, StringComparison.Ordinal);
        Assert.Contains("$null = $process.Handle", staging, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit($NativeTimeoutSec * 1000)", staging, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $process.Id -Force", staging, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $captureRoot -Recurse -Force", staging, StringComparison.Ordinal);
        Assert.DoesNotContain("& git", staging, StringComparison.Ordinal);
        Assert.Contains("-ScriptBlock (Get-GuestSourceStagingScriptBlock)", transfer, StringComparison.Ordinal);
        Assert.Contains("$stagingOutput[0].warningCount", transfer, StringComparison.Ordinal);
        Assert.Contains("Cleaning guest source archive transfer", transfer, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $hostTransferRoot -Recurse -Force", transfer, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanSourceArchiveUsesCommittedTreeAndExcludesIgnoredGeneratedOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-source-repo-{Guid.NewGuid():N}");
        var archiveRoot = Path.Combine(Path.GetTempPath(), $"openclaw-source-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(archiveRoot);
        try
        {
            var result = RunPowerShellCommand(BuildCleanSourceArchiveProof(
                root,
                Path.Combine(archiveRoot, "source.zip")));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.Matches("^[0-9a-f]{40}$", proof.GetProperty("sourceHead").GetString() ?? string.Empty);
            Assert.Equal(3, proof.GetProperty("trackedFileCount").GetInt32());
            Assert.True(proof.GetProperty("archiveSize").GetInt64() > 0);
            Assert.Equal(64, (proof.GetProperty("sha256").GetString() ?? string.Empty).Length);
            Assert.False(proof.GetProperty("hasGeneratedEntries").GetBoolean());
            Assert.Contains(
                "requires a clean committed HEAD",
                proof.GetProperty("dirtyError").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestDirectory(root);
            DeleteTestDirectory(archiveRoot);
        }
    }

    [Fact]
    public void SourceArchiveHashMismatchFailsBeforeExtractionAndCleansGuestArchive()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-bad-hash-{Guid.NewGuid():N}.zip");
        var destination = Path.Combine(Path.GetTempPath(), $"openclaw-bad-hash-dest-{Guid.NewGuid():N}");
        CreateSourceArchive(archivePath, ("src/app.txt", "safe", 0));
        try
        {
            var result = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                archivePath,
                destination,
                expectedSha256: new string('0', 64),
                expectedFileCount: 1,
                install: true));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var error = document.RootElement.GetProperty("error").GetString() ?? string.Empty;
            Assert.True(
                error.Contains("SHA256 mismatch", StringComparison.Ordinal),
                $"Expected SHA256 mismatch. Actual: {error}");
            Assert.False(document.RootElement.GetProperty("archiveExists").GetBoolean());
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("../escape.txt", "payload", 0, "safe Windows relative path")]
    [InlineData("C:/escape.txt", "payload", 0, "absolute")]
    [InlineData("src/bin/stale.dll", "payload", 0, "forbidden generated segment")]
    [InlineData("openclaw-source-provenance.json", "payload", 0, "reserved provenance file")]
    [InlineData("safe-link", "../escape.txt", unchecked((int)0xA1FF0000), "symbolic-link target")]
    public void SourceArchiveRejectsTraversalGeneratedAndUnsafeLinkEntries(
        string entryName,
        string content,
        int externalAttributes,
        string expectedError)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-unsafe-{Guid.NewGuid():N}.zip");
        var destination = Path.Combine(Path.GetTempPath(), $"openclaw-unsafe-dest-{Guid.NewGuid():N}");
        CreateSourceArchive(archivePath, (entryName, content, externalAttributes));
        try
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
            var result = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                archivePath,
                destination,
                hash,
                expectedFileCount: 1,
                install: true));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var error = document.RootElement.GetProperty("error").GetString() ?? string.Empty;
            Assert.True(
                error.Contains(expectedError, StringComparison.OrdinalIgnoreCase),
                $"Expected '{expectedError}'. Actual: {error}");
            Assert.False(document.RootElement.GetProperty("archiveExists").GetBoolean());
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public void SourceArchiveExtractionWritesExactHeadProvenanceAndRemovesArchive()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-safe-{Guid.NewGuid():N}.zip");
        var destination = Path.Combine(Path.GetTempPath(), $"openclaw-safe-dest-{Guid.NewGuid():N}");
        CreateSourceArchive(
            archivePath,
            ("src/app.txt", "source", 0),
            ("scripts/setup-dev.ps1", "Write-Host ready", 0));
        try
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
            var result = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                archivePath,
                destination,
                hash,
                expectedFileCount: 2,
                install: true));

            AssertPowerShellProofSucceeded(result);
            using var document = JsonDocument.Parse(result.Stdout);
            var proof = document.RootElement;
            Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
            Assert.False(proof.GetProperty("archiveExists").GetBoolean());
            Assert.True(proof.GetProperty("destinationExists").GetBoolean());
            Assert.Equal(
                "1111111111111111111111111111111111111111",
                proof.GetProperty("provenanceHead").GetString());
            Assert.Equal(hash, proof.GetProperty("provenanceSha256").GetString());
            Assert.Equal(2, proof.GetProperty("provenanceTrackedFileCount").GetInt32());
            Assert.False(proof.GetProperty("hasGeneratedDirectories").GetBoolean());
            Assert.False(proof.GetProperty("hasReparsePoints").GetBoolean());
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

        [Fact]
        public void GuestSourceStagingPreservesLfBytesWithLocalAutoCrlfDisabled()
        {
            var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-lf-{Guid.NewGuid():N}.zip");
            var destination = Path.Combine(Path.GetTempPath(), $"openclaw-lf-dest-{Guid.NewGuid():N}");
            const string lfContent = "line one\nline two\n";
            CreateSourceArchive(
                archivePath,
                ("src/lf.txt", lfContent, 0));
            try
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
                var install = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                    archivePath,
                    destination,
                    hash,
                    expectedFileCount: 1,
                    install: true));
                AssertPowerShellProofSucceeded(install);
                using (var document = JsonDocument.Parse(install.Stdout))
                {
                    Assert.True(string.IsNullOrEmpty(
                        document.RootElement.GetProperty("error").GetString()));
                }

                var result = RunPowerShellCommand(BuildGuestSourceStagingProof(destination));

                AssertPowerShellProofSucceeded(result);
                using var proofDocument = JsonDocument.Parse(result.Stdout);
                var proof = proofDocument.RootElement;
                Assert.True(proof.GetProperty("clean").GetBoolean());
                Assert.Equal(
                    proof.GetProperty("beforeSha256").GetString(),
                    proof.GetProperty("afterSha256").GetString());
                Assert.Equal("false", proof.GetProperty("autoCrlf").GetString());
                Assert.Equal("true", proof.GetProperty("safeCrlf").GetString());
                Assert.Equal(string.Empty, proof.GetProperty("status").GetString());
                Assert.Equal(lfContent, File.ReadAllText(Path.Combine(destination, "src", "lf.txt")));
                Assert.DoesNotContain(
                    (byte)'\r',
                    File.ReadAllBytes(Path.Combine(destination, "src", "lf.txt")));
            }
            finally
            {
                File.Delete(archivePath);
                DeleteTestDirectory(destination);
            }
        }

        [Fact]
        public void GuestSourceStagingTreatsExitZeroStderrAsBoundedWarning()
        {
            var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-warning-{Guid.NewGuid():N}.zip");
            var destination = Path.Combine(Path.GetTempPath(), $"openclaw-warning-dest-{Guid.NewGuid():N}");
            CreateSourceArchive(archivePath, ("src/lf.txt", "line\n", 0));
            try
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
                var install = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                    archivePath,
                    destination,
                    hash,
                    expectedFileCount: 1,
                    install: true));
                AssertPowerShellProofSucceeded(install);

                var result = RunPowerShellCommand(BuildGuestSourceStagingMockProof(
                    destination,
                    addExitCode: 0,
                    addStderr: "LF will be replaced by CRLF"));

                AssertPowerShellProofSucceeded(result);
                using var document = JsonDocument.Parse(result.Stdout);
                var proof = document.RootElement;
                Assert.True(string.IsNullOrEmpty(proof.GetProperty("error").GetString()));
                Assert.True(proof.GetProperty("clean").GetBoolean());
                Assert.Equal(1, proof.GetProperty("warningCount").GetInt32());
                Assert.Contains(
                    "AddAll: LF will be replaced by CRLF",
                    proof.GetProperty("warnings").GetString(),
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Init,BranchMain,ConfigUserName,ConfigUserEmail,ConfigAutoCrlf,ConfigSafeCrlf,AddAll,Commit,Status",
                    proof.GetProperty("calls").GetString());
            }
            finally
            {
                File.Delete(archivePath);
                DeleteTestDirectory(destination);
            }
        }

        [Fact]
        public void GuestSourceStagingDetectsRealGitFailureUnderWindowsPowerShell()
        {
            var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-real-git-fail-{Guid.NewGuid():N}.zip");
            var destination = Path.Combine(Path.GetTempPath(), $"openclaw-real-git-fail-dest-{Guid.NewGuid():N}");
            CreateSourceArchive(archivePath, ("src/lf.txt", "line\n", 0));
            try
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
                var install = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                    archivePath,
                    destination,
                    hash,
                    expectedFileCount: 1,
                    install: true));
                AssertPowerShellProofSucceeded(install);

                var result = RunPowerShellCommand(
                    BuildGuestSourceStagingRealCommitFailureProof(destination));

                AssertPowerShellProofSucceeded(result);
                using var document = JsonDocument.Parse(result.Stdout);
                var proof = document.RootElement;
                Assert.Equal(5, proof.GetProperty("powerShellMajor").GetInt32());
                Assert.Contains(
                    "Guest Git operation 'Commit' failed with exit code",
                    proof.GetProperty("error").GetString(),
                    StringComparison.Ordinal);
                Assert.Contains(
                    "stderr='",
                    proof.GetProperty("error").GetString(),
                    StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(archivePath);
                DeleteTestDirectory(destination);
            }
        }

        [Fact]
        public void GuestSourceStagingNonzeroGitExitIncludesBoundedDiagnostics()
        {
            var archivePath = Path.Combine(Path.GetTempPath(), $"openclaw-git-fail-{Guid.NewGuid():N}.zip");
            var destination = Path.Combine(Path.GetTempPath(), $"openclaw-git-fail-dest-{Guid.NewGuid():N}");
            CreateSourceArchive(archivePath, ("src/lf.txt", "line\n", 0));
            try
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)));
                var install = RunPowerShellCommand(BuildSourceArchiveInstallProof(
                    archivePath,
                    destination,
                    hash,
                    expectedFileCount: 1,
                    install: true));
                AssertPowerShellProofSucceeded(install);

                var result = RunPowerShellCommand(BuildGuestSourceStagingMockProof(
                    destination,
                    addExitCode: 23,
                    addStderr: "index write failed"));

                AssertPowerShellProofSucceeded(result);
                using var document = JsonDocument.Parse(result.Stdout);
                var error = document.RootElement.GetProperty("error").GetString() ?? string.Empty;
                Assert.Contains("Guest Git operation 'AddAll'", error, StringComparison.Ordinal);
                Assert.Contains("exit code 23", error, StringComparison.Ordinal);
                Assert.Contains("stderr='index write failed'", error, StringComparison.Ordinal);
                Assert.DoesNotContain("Commit", document.RootElement.GetProperty("calls").GetString(), StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(archivePath);
                DeleteTestDirectory(destination);
            }
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

    private static string BuildWingetRedirectProof(
        int statusCode,
        string locationExpression,
        int redirectCount)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var redirectPolicy = ExtractPowerShellFunction(
            controller,
            "Resolve-OpenClawWingetHttpResponse",
            "Invoke-OpenClawWingetAssetDownload");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            redirectPolicy,
            "\n$current = [Uri]'https://github.com/microsoft/winget-cli/releases/download/v1.29.280/asset'\n",
            "try {\n",
            "  $target = Resolve-OpenClawWingetHttpResponse ",
            "-CurrentUri $current ",
            $"-StatusCode {statusCode} ",
            $"-Location ({locationExpression}) ",
            $"-RedirectCount {redirectCount} ",
            "-AssetName 'asset'\n",
            "  $value = if ($null -eq $target) { 'final' } else { [string]$target.Host }\n",
            "  [Console]::Out.Write('accepted|' + $value)\n",
            "} catch {\n",
            "  [Console]::Out.Write('rejected|' + $_.Exception.Message)\n",
            "}\n");
    }

    private static string BuildWingetCatalogInitialUriProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var initialPolicy = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCatalogInitialUri",
            "Resolve-OpenClawWingetCatalogHttpResponse");
        var uri = scenario switch
        {
            "http" => "http://cdn.winget.microsoft.com/cache/source2.msix",
            "host" => "https://example.com/cache/source2.msix",
            "path" => "https://cdn.winget.microsoft.com/cache/other.msix",
            "query" => "https://cdn.winget.microsoft.com/cache/source2.msix?value=secret",
            "fragment" => "https://cdn.winget.microsoft.com/cache/source2.msix#fragment",
            "userinfo" => "https://user@cdn.winget.microsoft.com/cache/source2.msix",
            "port" => "https://cdn.winget.microsoft.com:444/cache/source2.msix",
            _ => "https://cdn.winget.microsoft.com/cache/source2.msix",
        };
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            initialPolicy,
            "\ntry {\n",
            $" Assert-OpenClawWingetCatalogInitialUri -Uri ([Uri]{PsQuote(uri)})\n",
            " [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogRedirectProof(
        int statusCode,
        string locationExpression,
        int redirectCount)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var redirectPolicy = ExtractPowerShellFunction(
            controller,
            "Resolve-OpenClawWingetCatalogHttpResponse",
            "Assert-OpenClawWingetCatalogDownloadLength");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            redirectPolicy,
            "\n$current = [Uri]'https://cdn.winget.microsoft.com/cache/source2.msix'\n",
            "try {\n",
            " $target=Resolve-OpenClawWingetCatalogHttpResponse ",
            "-CurrentUri $current ",
            $"-StatusCode {statusCode} ",
            $"-Location ({locationExpression}) ",
            $"-RedirectCount {redirectCount}\n",
            " $value=if($null -eq $target){'final'}else{[string]$target.Host}\n",
            " [Console]::Out.Write('accepted|' + $value)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogLengthProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var lengthPolicy = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCatalogDownloadLength",
            "Invoke-OpenClawWingetCatalogDownload");
        var contentLength = scenario switch
        {
            "no-header" => "$null",
            "zero-header" => "[Int64]0",
            "oversize-header" => "[Int64]17",
            "mismatch" => "[Int64]8",
            _ => "[Int64]7",
        };
        var actualSize = scenario switch
        {
            "empty" => 0,
            "oversize-stream" => 17,
            _ => 7,
        };
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            lengthPolicy,
            "\ntry {\n",
            " Assert-OpenClawWingetCatalogDownloadLength ",
            $"-ContentLength ({contentLength}) -ActualSize {actualSize} ",
            "-MaximumSize 16 -Completed\n",
            " [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogSignatureProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var signature = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCatalogSignature",
            "Assert-OpenClawWingetSafeZipEntryName");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var status = scenario == "status" ? "NotSigned" : "Valid";
        var subject = scenario == "publisher" ? "CN=Unexpected" : publisher;
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            signature,
            "\nfunction Get-AuthenticodeSignature { [CmdletBinding()] param([string]$LiteralPath) ",
            $"[pscustomobject]@{{ Status='{status}'; SignerCertificate=[pscustomobject]@{{ Subject={PsQuote(subject)} }} }} }}\n",
            "try {\n",
            " Assert-OpenClawWingetCatalogSignature -Path 'source2.msix' ",
            $"-ExpectedPublisher {PsQuote(publisher)}\n",
            " [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogFileEvidenceProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var evidence = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetCatalogFileEvidence",
            "Ensure-OpenClawWingetCatalog");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            evidence,
            "\n$script:signature=0; $script:identity=0\n",
            "function Test-Path { [CmdletBinding()] param($LiteralPath,$PathType) $true }\n",
            "function Get-Item { [CmdletBinding()] param($LiteralPath,[switch]$Force) [pscustomobject]@{ Length=[Int64]7 } }\n",
            "function Get-FileHash { [CmdletBinding()] param($LiteralPath,$Algorithm) [pscustomobject]@{ Hash=('c' * 64) } }\n",
            "function Assert-OpenClawWingetCatalogSignature { param($Path,$ExpectedPublisher) $script:signature++ }\n",
            "function Read-OpenClawWingetZipXml { param($ArchivePath,$EntryName,$ArchiveName) [pscustomobject]@{} }\n",
            "function Assert-OpenClawWingetCatalogManifest { param($Document,$ExpectedPublisher) $script:identity++; '7.8.9.10' }\n",
            "$proof=Get-OpenClawWingetCatalogFileEvidence -Path 'source2.msix' ",
            $"-ExpectedPublisher {PsQuote(publisher)} -MaximumSize 16\n",
            "[Console]::Out.Write(('accepted|{0}|signature={1}|identity={2}' -f ",
            "$proof.Sha256,$script:signature,$script:identity))\n");
    }

    private static string BuildWingetCatalogManifestProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var attributes = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetXmlAttribute",
            "Get-OpenClawWingetDirectXmlChildren");
        var children = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetDirectXmlChildren",
            "ConvertTo-OpenClawWingetDiagnostic");
        var versionParser = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetValidNonzeroVersion",
            "Assert-OpenClawWingetCatalogManifest");
        var manifestPolicy = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCatalogManifest",
            "Assert-OpenClawWingetBundleManifest");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var name = scenario == "name" ? "Microsoft.Unexpected.Source" : "Microsoft.Winget.Source";
        var actualPublisher = scenario == "publisher" ? "CN=Unexpected" : publisher;
        var architecture = scenario == "arch" ? "x64" : "neutral";
        var version = scenario switch
        {
            "version" => "not-a-version",
            "zero-version" => "0.0.0.0",
            _ => "7.8.9.10",
        };
        var xml = string.Concat(
            "<Package xmlns=\"urn:test\"><Identity ",
            $"Name=\"{name}\" Publisher=\"{actualPublisher}\" ",
            $"ProcessorArchitecture=\"{architecture}\" Version=\"{version}\" />",
            "</Package>");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            attributes,
            "\n",
            children,
            "\n",
            versionParser,
            "\n",
            manifestPolicy,
            $"\n[xml]$document={PsQuote(xml)}\n",
            "try {\n",
            " $version=Assert-OpenClawWingetCatalogManifest -Document $document ",
            $"-ExpectedPublisher {PsQuote(publisher)}\n",
            " [Console]::Out.Write('accepted|' + $version)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogRegistrationStateProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var versionParser = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetValidNonzeroVersion",
            "Assert-OpenClawWingetCatalogManifest");
        var identity = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCatalogRegistrationIdentity",
            "Get-OpenClawWingetCatalogRegistrationState");
        var state = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetCatalogRegistrationState",
            "Wait-OpenClawWingetCatalogRegistration");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var name = scenario == "name" ? "Microsoft.Unexpected.Source" : "Microsoft.Winget.Source";
        var actualPublisher = scenario == "publisher" ? "CN=Unexpected" : publisher;
        var architecture = scenario == "arch" ? "X64" : "Neutral";
        var version = scenario switch
        {
            "version" => "not-a-version",
            "zero-version" => "0.0.0.0",
            _ => "7.8.9.10",
        };
        var packageOutput = scenario == "missing"
            ? string.Empty
            : scenario == "duplicate"
                ? "$script:package; $script:package"
                : "$script:package";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            versionParser,
            "\n",
            identity,
            "\n",
            state,
            "\n$script:package=[pscustomobject]@{ ",
            $"Name={PsQuote(name)}; Publisher={PsQuote(actualPublisher)}; ",
            $"Architecture={PsQuote(architecture)}; Version={PsQuote(version)} }}\n",
            "function Get-AppxPackage { [CmdletBinding()] param([string]$Name) ",
            packageOutput,
            " }\ntry {\n",
            " $proof=Get-OpenClawWingetCatalogRegistrationState ",
            $"-ExpectedPublisher {PsQuote(publisher)}\n",
            " [Console]::Out.Write(('accepted|{0}|{1}' -f $proof.State,$proof.Version))\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogAcquisitionProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var acquisition = ExtractPowerShellFunction(
            controller,
            "Ensure-OpenClawWingetCatalog",
            "Resolve-OpenClawWingetDirectExecutable");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var existing = scenario == "existing" ? "$true" : "$false";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            acquisition,
            "\n$script:downloads=0; $script:installs=0\n",
            "function Get-OpenClawWingetCatalogRegistrationState { param($ExpectedPublisher) ",
            $"if({existing}){{[pscustomobject]@{{State='existing';Version='7.8.9.10'}}}}",
            "else{[pscustomobject]@{State='missing';Version=$null}} }\n",
            "function Invoke-OpenClawWingetCatalogDownload { param($InitialUri,$DestinationPath,$MaximumSize,$TimeoutSeconds) ",
            "if([string]$InitialUri.OriginalString -cne 'https://cdn.winget.microsoft.com/cache/source2.msix'){throw 'wrong URL'}; ",
            "if([string]$DestinationPath -cne 'D:\\owned\\source2.msix'){throw 'wrong path'}; ",
            "if([Int64]$MaximumSize -ne 16777216){throw 'wrong maximum'}; ",
            "if([int]$TimeoutSeconds -lt 1 -or [int]$TimeoutSeconds -gt 60){throw 'wrong timeout'}; ",
            "$script:downloads++ }\n",
            "function Get-OpenClawWingetCatalogFileEvidence { param($Path,$ExpectedPublisher,$MaximumSize) ",
            "[pscustomobject]@{Version='7.8.9.10';Sha256=('C' * 64)} }\n",
            "function Add-AppxPackage { [CmdletBinding()] param($Path) $script:installs++ }\n",
            "function Wait-OpenClawWingetCatalogRegistration { param($ExpectedPublisher,$RetryCount,$DelayMilliseconds) ",
            "[pscustomobject]@{State='existing';Version='7.8.9.10'} }\n",
            "try {\n",
            " $proof=Ensure-OpenClawWingetCatalog ",
            $"-ExpectedPublisher {PsQuote(publisher)} -TemporaryRoot 'D:\\owned' ",
            "-DownloadDeadlineUtc ([DateTime]::UtcNow.AddSeconds(60)) ",
            "-RetryCount 3 -DelayMilliseconds 0\n",
            " [Console]::Out.Write(('accepted|{0}|{1}|{2}|downloads={3}|installs={4}' -f ",
            "$proof.Acquisition,$proof.Version,$proof.Sha256,$script:downloads,$script:installs))\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCatalogRegistrationWaitProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var wait = ExtractPowerShellFunction(
            controller,
            "Wait-OpenClawWingetCatalogRegistration",
            "Get-OpenClawWingetCatalogFileEvidence");
        var ready = scenario == "retry" ? "$script:calls -ge 3" : "$false";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            wait,
            "\n$script:calls=0\n",
            "function Get-OpenClawWingetCatalogRegistrationState { param($ExpectedPublisher) ",
            "$script:calls++; ",
            $"if({ready}){{[pscustomobject]@{{State='existing';Version='7.8.9.10'}}}}",
            "else{[pscustomobject]@{State='missing';Version=$null}} }\n",
            "try {\n",
            " $null=Wait-OpenClawWingetCatalogRegistration -ExpectedPublisher 'publisher' ",
            "-RetryCount 3 -DelayMilliseconds 0\n",
            " [Console]::Out.Write('accepted|' + $script:calls)\n",
            "} catch { [Console]::Out.Write(('rejected|{0}|{1}' -f $_.Exception.Message,$script:calls)) }\n");
    }

    private static string BuildWingetFilePinProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var filePin = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetFile",
            "Assert-OpenClawWingetSignature");
        var actualSize = scenario == "size" ? 2 : 1;
        var actualHash = scenario == "hash" ? new string('b', 64) : new string('a', 64);
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            filePin,
            "\nfunction Test-Path { [CmdletBinding()] param([string]$LiteralPath, [object]$PathType) return $true }\n",
            $"function Get-Item {{ [CmdletBinding()] param([string]$LiteralPath, [switch]$Force) [pscustomobject]@{{ Length = [Int64]{actualSize} }} }}\n",
            "function Get-FileHash { [CmdletBinding()] param([string]$LiteralPath, [string]$Algorithm) ",
            $"[pscustomobject]@{{ Hash = '{actualHash}' }} }}\n",
            "try {\n",
            "  Assert-OpenClawWingetFile -Path 'pinned.appx' -ExpectedSize 1 ",
            $"-ExpectedSha256 '{new string('a', 64)}' -AssetName 'pinned.appx'\n",
            "  [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetTrustedProcessProof(bool timeout)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var process = ExtractPowerShellFunction(
            controller,
            "Invoke-OpenClawTrustedWingetProcess",
            "Get-OpenClawWingetProcessDiagnostic");
        return string.Concat(
            "$ErrorActionPreference='Stop'\n",
            "function ConvertTo-OpenClawWingetDiagnostic { param($Text,$MaxChars) [string]$Text }\n",
            "function Read-OpenClawWingetBoundedNativeText { param($Path) '' }\n",
            process,
            "\n$script:timeout=",
            timeout ? "$true" : "$false",
            "; $script:killed=$false; $script:wait=0; $script:arguments=@()\n",
            "function Test-Path { param($LiteralPath,$PathType) return ($LiteralPath -ceq 'C:\\Package\\winget.exe') }\n",
            "function Start-Process {\n",
            " param($FilePath,[string[]]$ArgumentList,$RedirectStandardOutput,$RedirectStandardError,[switch]$PassThru,$WindowStyle,$ErrorAction)\n",
            " $script:arguments=@($ArgumentList)\n",
            " $mock=[pscustomobject]@{ ExitCode=0 }\n",
            " $mock | Add-Member ScriptMethod WaitForExit { param($milliseconds) if ($null -eq $milliseconds) { return $true }; $script:wait=[int]$milliseconds; return (-not $script:timeout) }\n",
            " $mock | Add-Member ScriptMethod Kill { $script:killed=$true }\n",
            " $mock | Add-Member ScriptMethod Dispose { }\n",
            " return $mock\n",
            "}\n",
            "try {\n",
            " $null=Invoke-OpenClawTrustedWingetProcess -WingetPath 'C:\\Package\\winget.exe' -Operation SourceUpdateWinget -TemporaryRoot 'D:\\owned'\n",
            " [Console]::Out.Write(('accepted|{0}|{1}' -f ($script:arguments -join ','),$script:wait))\n",
            "} catch { [Console]::Out.Write(('rejected|{0}|killed={1}' -f $_.Exception.Message,$script:killed)) }\n");
    }

    private static string BuildWingetSignatureProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var signature = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetSignature",
            "Assert-OpenClawWingetSafeZipEntryName");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var status = scenario == "status" ? "NotSigned" : "Valid";
        var subject = scenario == "publisher" ? "CN=Unexpected" : publisher;
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            signature,
            "\nfunction Get-AuthenticodeSignature { [CmdletBinding()] param([string]$LiteralPath) ",
            $"[pscustomobject]@{{ Status = '{status}'; SignerCertificate = [pscustomobject]@{{ Subject = {PsQuote(subject)} }} }} }}\n",
            "try {\n",
            "  Assert-OpenClawWingetSignature -Path 'pinned.appx' ",
            $"-ExpectedPublisher {PsQuote(publisher)} -AssetName 'pinned.appx'\n",
            "  [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetDescriptorJson(string scenario)
    {
        var dependencies = new List<Dictionary<string, string>>
        {
            new()
            {
                ["Name"] = "Microsoft.VCLibs.140.00",
                ["Version"] = "14.0.33519.0",
            },
            new()
            {
                ["Name"] = "Microsoft.VCLibs.140.00.UWPDesktop",
                ["Version"] = "14.0.33728.0",
            },
            new()
            {
                ["Name"] = "Microsoft.WindowsAppRuntime.1.8",
                ["Version"] = "8000.616.304.0",
            },
        };
        if (scenario == "reordered")
        {
            (dependencies[0], dependencies[1]) = (dependencies[1], dependencies[0]);
        }
        else if (scenario == "version")
        {
            dependencies[1]["Version"] = "14.0.0.0";
        }
        else if (scenario == "extra-field")
        {
            dependencies[0]["Unexpected"] = "value";
        }
        else if (scenario == "extra-dependency")
        {
            dependencies.Add(new Dictionary<string, string>
            {
                ["Name"] = "Microsoft.UI.Xaml.2.8",
                ["Version"] = "8.0.0.0",
            });
        }

        var descriptor = new Dictionary<string, object>
        {
            ["Dependencies"] = dependencies,
        };
        if (scenario == "extra-top-level")
        {
            descriptor["Unexpected"] = true;
        }
        return JsonSerializer.Serialize(descriptor);
    }

    private static string BuildWingetDescriptorProof(string descriptorPath)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var descriptor = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetDependencyDescriptor",
            "Assert-OpenClawAppxManifestIdentity");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            descriptor,
            "\n$expected = @(\n",
            "  [pscustomobject]@{ Name = 'Microsoft.VCLibs.140.00'; Version = '14.0.33519.0' },\n",
            "  [pscustomobject]@{ Name = 'Microsoft.VCLibs.140.00.UWPDesktop'; Version = '14.0.33728.0' },\n",
            "  [pscustomobject]@{ Name = 'Microsoft.WindowsAppRuntime.1.8'; Version = '8000.616.304.0' }\n",
            ")\n",
            "try {\n",
            $"  Assert-OpenClawWingetDependencyDescriptor -Path {PsQuote(descriptorPath)} -ExpectedDependencies $expected\n",
            "  [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetPayloadManifestProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var attributes = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetXmlAttribute",
            "Get-OpenClawWingetDirectXmlChildren");
        var children = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetDirectXmlChildren",
            "ConvertTo-OpenClawWingetDiagnostic");
        var identity = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawAppxManifestIdentity",
            "Assert-OpenClawWingetBundleManifest");
        var payload = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetPayloadManifest",
            "Assert-OpenClawWingetCurrentPackageIdentity");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var identityName = scenario == "identity"
            ? "Microsoft.Unexpected"
            : "Microsoft.DesktopAppInstaller";
        var appRuntimeVersion = scenario == "dependency"
            ? "8000.0.0.0"
            : "8000.616.304.0";
        var alias = scenario == "alias" ? "unexpected.exe" : "winget.exe";
        var extraDependency = scenario == "extra-dependency"
            ? $"<PackageDependency Name=\"Microsoft.UI.Xaml.2.8\" MinVersion=\"8.0.0.0\" Publisher=\"{publisher}\" />"
            : string.Empty;
        var xml = string.Concat(
            "<Package xmlns=\"urn:test\" xmlns:uap=\"urn:test-uap\">",
            $"<Identity Name=\"{identityName}\" Version=\"1.29.280.0\" Publisher=\"{publisher}\" ProcessorArchitecture=\"x64\" />",
            "<Dependencies>",
            $"<PackageDependency Name=\"Microsoft.WindowsAppRuntime.1.8\" MinVersion=\"{appRuntimeVersion}\" Publisher=\"{publisher}\" />",
            $"<PackageDependency Name=\"Microsoft.VCLibs.140.00\" MinVersion=\"14.0.33519.0\" Publisher=\"{publisher}\" />",
            $"<PackageDependency Name=\"Microsoft.VCLibs.140.00.UWPDesktop\" MinVersion=\"14.0.33728.0\" Publisher=\"{publisher}\" />",
            extraDependency,
            "</Dependencies>",
            "<Applications><Application Id=\"winget\" Executable=\"winget.exe\">",
            $"<Extensions><uap:ExecutionAlias Alias=\"{alias}\" /></Extensions>",
            "</Application></Applications>",
            "</Package>");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            attributes,
            "\n",
            children,
            "\n",
            identity,
            "\n",
            payload,
            "\n$expected = @(\n",
            "  [pscustomobject]@{ Name = 'Microsoft.VCLibs.140.00'; Version = '14.0.33519.0' },\n",
            "  [pscustomobject]@{ Name = 'Microsoft.VCLibs.140.00.UWPDesktop'; Version = '14.0.33728.0' },\n",
            "  [pscustomobject]@{ Name = 'Microsoft.WindowsAppRuntime.1.8'; Version = '8000.616.304.0' }\n",
            ")\n",
            $"[xml]$document = {PsQuote(xml)}\n",
            "try {\n",
            "  Assert-OpenClawWingetPayloadManifest -Document $document ",
            $"-ExpectedPublisher {PsQuote(publisher)} -ExpectedDependencies $expected\n",
            "  [Console]::Out.Write('accepted')\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetBundleManifestProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var attributes = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetXmlAttribute",
            "Get-OpenClawWingetDirectXmlChildren");
        var children = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetDirectXmlChildren",
            "ConvertTo-OpenClawWingetDiagnostic");
        var bundle = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetBundleManifest",
            "Assert-OpenClawWingetPayloadManifest");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var identityVersion = scenario == "identity" ? "2026.1.1.0" : "2026.623.1704.0";
        var payloadVersion = scenario == "payload" ? "1.29.279.0" : "1.29.280.0";
        var duplicate = scenario == "duplicate"
            ? "<Package Type=\"application\" Architecture=\"x64\" Version=\"1.29.280.0\" FileName=\"Other_x64.msix\" />"
            : string.Empty;
        var xml = string.Concat(
            "<Bundle xmlns=\"urn:test\">",
            $"<Identity Name=\"Microsoft.DesktopAppInstaller\" Publisher=\"{publisher}\" Version=\"{identityVersion}\" />",
            "<Packages>",
            $"<Package Type=\"application\" Architecture=\"x64\" Version=\"{payloadVersion}\" FileName=\"AppInstaller_x64.msix\" />",
            "<Package Type=\"application\" Architecture=\"x64\" Version=\"1.29.280.0\" FileName=\"AppInstallerStub_x64.msix\" />",
            duplicate,
            "</Packages></Bundle>");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            attributes,
            "\n",
            children,
            "\n",
            bundle,
            $"\n[xml]$document = {PsQuote(xml)}\n",
            "try {\n",
            "  $entry = Assert-OpenClawWingetBundleManifest -Document $document ",
            $"-ExpectedPublisher {PsQuote(publisher)}\n",
            "  [Console]::Out.Write('accepted|' + $entry)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetDependencyExtractionProof(
        string archivePath,
        string destinationPath)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var safeName = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetSafeZipEntryName",
            "New-OpenClawWingetZipEntryIndex");
        var index = ExtractPowerShellFunction(
            controller,
            "New-OpenClawWingetZipEntryIndex",
            "ConvertFrom-OpenClawWingetXmlBytes");
        var extract = ExtractPowerShellFunction(
            controller,
            "Expand-OpenClawWingetDependencyPackages",
            "Expand-OpenClawWingetBundlePayload");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            safeName,
            "\n",
            index,
            "\n",
            extract,
            "\n$expected = @([pscustomobject]@{ ",
            "Name = 'Pinned.Dependency'; Version = '1.0.0.0'; ",
            "RelativePath = 'x64\\pinned.appx'; Size = [Int64]1; ",
            $"Sha256 = '{new string('a', 64)}' }})\n",
            "try {\n",
            $"  $result = @(Expand-OpenClawWingetDependencyPackages -ArchivePath {PsQuote(archivePath)} ",
            $"-DestinationRoot {PsQuote(destinationPath)} -ExpectedDependencies $expected)\n",
            "  [Console]::Out.Write('accepted|' + $result.Count)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetZipXmlProof(string archivePath)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var safeName = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetSafeZipEntryName",
            "New-OpenClawWingetZipEntryIndex");
        var index = ExtractPowerShellFunction(
            controller,
            "New-OpenClawWingetZipEntryIndex",
            "ConvertFrom-OpenClawWingetXmlBytes");
        var convert = ExtractPowerShellFunction(
            controller,
            "ConvertFrom-OpenClawWingetXmlBytes",
            "Read-OpenClawWingetZipXml");
        var read = ExtractPowerShellFunction(
            controller,
            "Read-OpenClawWingetZipXml",
            "Expand-OpenClawWingetDependencyPackages");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            safeName,
            "\n",
            index,
            "\n",
            convert,
            "\n",
            read,
            "\n$document=Read-OpenClawWingetZipXml ",
            $"-ArchivePath {PsQuote(archivePath)} -EntryName 'AppxManifest.xml' -ArchiveName 'pinned.appx'\n",
            "$identity=@($document.SelectNodes(\"//*[local-name()='Identity']\"))[0]\n",
            "[Console]::Out.Write(([string]$document.DocumentElement.LocalName + '|' + [string]$identity.GetAttribute('Name')))\n");
    }

    private static string BuildWingetInstallOrderProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var identity = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCurrentPackageIdentity",
            "Get-OpenClawWingetCurrentMainPackageState");
        var state = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetDependencyInstallState",
            "Wait-OpenClawWingetDependencyRegistration");
        var wait = ExtractPowerShellFunction(
            controller,
            "Wait-OpenClawWingetDependencyRegistration",
            "Install-OpenClawWingetValidatedPackages");
        var install = ExtractPowerShellFunction(
            controller,
            "Install-OpenClawWingetValidatedPackages",
            "Wait-OpenClawWingetMainPackageRegistration");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            identity,
            "\n",
            state,
            "\n",
            wait,
            "\n",
            install,
            "\n$deps = @(\n",
            " [pscustomobject]@{ Name='Dep.One'; Version='1.0.0.0'; LocalPath='dep1.appx' },\n",
            " [pscustomobject]@{ Name='Dep.Two'; Version='2.0.0.0'; LocalPath='dep2.appx' },\n",
            " [pscustomobject]@{ Name='Dep.Three'; Version='3.0.0.0'; LocalPath='dep3.appx' }\n",
            ")\n",
            "$script:installed = @{}\n",
            "$script:pathToDependency = @{ 'dep1.appx'=$deps[0]; 'dep2.appx'=$deps[1]; 'dep3.appx'=$deps[2] }\n",
            "$script:calls = New-Object 'Collections.Generic.List[string]'\n",
            "function Get-AppxPackage { [CmdletBinding()] param([string]$Name)\n",
            " if ($script:installed.ContainsKey($Name)) { $d=$script:installed[$Name]; ",
            $"[pscustomobject]@{{ Name=$d.Name; Publisher={PsQuote(publisher)}; Architecture='X64'; ",
            "PackageFamilyName=($d.Name + '_8wekyb3d8bbwe'); Version=$d.Version } }\n",
            "}\n",
            "function Add-AppxPackage { [CmdletBinding()] param([string]$Path)\n",
            " $script:calls.Add($Path); if ($script:pathToDependency.ContainsKey($Path)) { ",
            "$d=$script:pathToDependency[$Path]; $script:installed[$d.Name]=$d } }\n",
            "Install-OpenClawWingetValidatedPackages -ValidatedDependencies $deps ",
            $"-BundlePath 'bundle.msixbundle' -ExpectedPublisher {PsQuote(publisher)} ",
            "-RetryCount 1 -DelayMilliseconds 0\n",
            "[Console]::Out.Write(($script:calls -join '|'))\n");
    }

    private static string BuildWingetDependencyStateProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var state = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetDependencyInstallState",
            "Wait-OpenClawWingetDependencyRegistration");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var version = scenario switch
        {
            "older" => "1.9.0.0",
            "newer" => "3.0.0.0",
            _ => "2.0.0.0",
        };
        var actualPublisher = scenario == "publisher" ? "CN=Unexpected" : publisher;
        var architecture = scenario == "x86-only" ? "X86" : "X64";
        var packageOutput = scenario == "missing"
            ? string.Empty
            : string.Concat(
                "[pscustomobject]@{ Name='Pinned.Dependency'; ",
                $"Publisher={PsQuote(actualPublisher)}; Architecture='{architecture}'; ",
                "PackageFamilyName='Pinned.Dependency_8wekyb3d8bbwe'; ",
                $"Version='{version}' }}");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            state,
            "\nfunction Get-AppxPackage { [CmdletBinding()] param([string]$Name) ",
            packageOutput,
            " }\n",
            "$dependency=[pscustomobject]@{ Name='Pinned.Dependency'; Version='2.0.0.0' }\n",
            "try {\n",
            " $state=Get-OpenClawWingetDependencyInstallState -Dependency $dependency ",
            $"-ExpectedPublisher {PsQuote(publisher)}\n",
            " [Console]::Out.Write('accepted|' + $state)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetAlreadyInstalledProof(
        string ownedRoot,
        bool cleanupFails)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var workflow = ExtractPowerShellRange(
            controller,
            "        function Invoke-OpenClawWingetBootstrap {",
            "        $publisher = \"CN=Microsoft Corporation");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        const string family = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
        var cleanupFunction = cleanupFails
            ? "function Remove-OpenClawWingetTemporaryRoot { param([string]$Path) throw 'mock cleanup failure' }\n"
            : string.Concat(
                "function Remove-OpenClawWingetTemporaryRoot { param([string]$Path) ",
                "$script:cleaned=$Path; Microsoft.PowerShell.Management\\Remove-Item ",
                "-LiteralPath $Path -Recurse -Force -ErrorAction Stop }\n");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            workflow,
            "\n$script:downloads=0; $script:installs=0; $script:validated=0; $script:cleaned=$null\n",
            "function Get-OpenClawWingetCurrentMainPackageState { param($ExpectedPublisher,$ExpectedPackageFamilyName) ",
            "[pscustomobject]@{ State='exact'; Package=[pscustomobject]@{ Name='Microsoft.DesktopAppInstaller' } } }\n",
            "function Assert-OpenClawWingetCurrentPackageIdentity { param($Package,$ExpectedName,$ExpectedVersion,$ExpectedPublisher,$ExpectedPackageFamilyName) }\n",
            "function Resolve-OpenClawWingetDirectExecutable { param($Package) 'C:\\Package\\winget.exe' }\n",
            "function Wait-OpenClawWingetExecutionAlias { param($ExpectedPackageFamilyName) 'alias' }\n",
            "function Ensure-OpenClawWingetCatalog { param($ExpectedPublisher,$TemporaryRoot,$DownloadDeadlineUtc) ",
            "[pscustomobject]@{ Acquisition='existing'; Version='7.8.9.10'; Sha256=$null } }\n",
            "function Assert-OpenClawWingetCli { param($WingetPath,$TemporaryRoot) $script:validated++ }\n",
            "function Invoke-OpenClawWingetAssetDownload { $script:downloads++ }\n",
            "function Install-OpenClawWingetValidatedPackages { $script:installs++ }\n",
            cleanupFunction,
            "$top=@([pscustomobject]@{ Name='unused'; Size=1; Sha256='",
            new string('a', 64),
            "' }); $deps=@([pscustomobject]@{ Name='unused'; Version='1.0.0.0' }); ",
            "$payload=[pscustomobject]@{ Name='unused'; Size=1; Sha256='",
            new string('b', 64),
            "' }\n",
            "try {\n",
            " $proof=Invoke-OpenClawWingetBootstrap ",
            $"-Publisher {PsQuote(publisher)} -PackageFamilyName '{family}' ",
            "-ReleaseBase 'https://github.com/release/' -ReleasePath '/release' ",
            "-TopAssets $top -Dependencies $deps -Payload $payload ",
            $"-TemporaryBase {PsQuote(ownedRoot)}\n",
            " [Console]::Out.Write(('accepted|downloads={0}|installs={1}|validated={2}|cleaned={3}|catalog={4}|catalogHashNull={5}' ",
            "-f $script:downloads,$script:installs,$script:validated,(-not [string]::IsNullOrEmpty($script:cleaned)),",
            "$proof.SourceCatalogAcquisition,($null -eq $proof.SourceCatalogSha256)))\n",
            "} catch {\n",
            " [Console]::Out.Write(('cleanup rejected|downloads={0}|installs={1}|validated={2}|error={3}' ",
            "-f $script:downloads,$script:installs,$script:validated,$_.Exception.Message))\n",
            "}\n");
    }

    private static string BuildWingetRegistrationProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var identity = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCurrentPackageIdentity",
            "Get-OpenClawWingetCurrentMainPackageState");
        var wait = ExtractPowerShellFunction(
            controller,
            "Wait-OpenClawWingetMainPackageRegistration",
            "Resolve-OpenClawWingetDirectExecutable");
        const string publisher =
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        var version = scenario == "newer" ? "1.30.0.0" : "1.29.280.0";
        var actualPublisher = scenario == "publisher" ? "CN=Unexpected" : publisher;
        var readyCondition = scenario == "retry" ? "$script:calls -ge 3" : "$true";
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            identity,
            "\n",
            wait,
            "\n$script:calls=0\n",
            "function Get-AppxPackage { [CmdletBinding()] param([string]$Name) $script:calls++; ",
            $"if ({readyCondition}) {{ [pscustomobject]@{{ Name='Microsoft.DesktopAppInstaller'; ",
            $"Publisher={PsQuote(actualPublisher)}; Architecture='X64'; ",
            $"PackageFamilyName='Microsoft.DesktopAppInstaller_8wekyb3d8bbwe'; Version='{version}' }} }} }}\n",
            "try {\n",
            " $package=Wait-OpenClawWingetMainPackageRegistration ",
            $"-ExpectedPublisher {PsQuote(publisher)} ",
            "-ExpectedPackageFamilyName 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' ",
            "-RetryCount 3 -DelayMilliseconds 0\n",
            " [Console]::Out.Write('accepted|' + $script:calls)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetCliValidationProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var validation = ExtractPowerShellFunction(
            controller,
            "Assert-OpenClawWingetCli",
            "Remove-OpenClawWingetTemporaryRoot");
        var diagnostic = ExtractPowerShellFunction(
            controller,
            "Get-OpenClawWingetProcessDiagnostic",
            "Wait-OpenClawWingetExecutionAlias");
        var version = scenario == "version" ? "v1.29.279" : "v1.29.280";
        var sourceExit = scenario == "source-exit" ? 1 : 0;
        var sourceText = scenario switch
        {
            "source-name" => "{\"Name\":\"msstore\"}",
            "source-json" => "not-json",
            _ => "{\"Name\":\"winget\"}",
        };
        var sourceProbeExit = scenario == "source-probe-exit" ? 23 : 0;
        var sourceProbeText = scenario switch
        {
            "source-probe-package" => "Found Unexpected.Package",
            "source-probe-exit" => "probe-out",
            _ => "Found Git [Git.Git]",
        };
        var sourceProbeError = scenario == "source-probe-exit" ? "probe-err" : string.Empty;
        var sourceUpdateExit = scenario == "source-update-exit" ? 17 : 0;
        var sourceUpdateText = scenario == "source-update-exit" ? "update-out" : "Done";
        var sourceUpdateError = scenario == "source-update-exit" ? "update-err" : string.Empty;
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "function ConvertTo-OpenClawWingetDiagnostic { param($Text,$MaxChars) if ([string]::IsNullOrEmpty([string]$Text)) { '<empty>' } else { [string]$Text } }\n",
            diagnostic,
            "\n",
            validation,
            "\n$script:calls=New-Object 'Collections.Generic.List[string]'\n",
            "function Invoke-OpenClawTrustedWingetProcess { param($WingetPath,$Operation,$TemporaryRoot) ",
            "$script:calls.Add($Operation); if ($Operation -ceq 'Version') { ",
            $"[pscustomobject]@{{ ExitCode=0; Stdout='{version}'; Stderr='' }} }} ",
            "elseif ($Operation -ceq 'SourceExportWinget') { ",
            $"[pscustomobject]@{{ ExitCode={sourceExit}; Stdout={PsQuote(sourceText)}; Stderr='' }} }} ",
            "elseif ($Operation -ceq 'SourceUpdateWinget') { ",
            $"[pscustomobject]@{{ ExitCode={sourceUpdateExit}; Stdout={PsQuote(sourceUpdateText)}; Stderr={PsQuote(sourceUpdateError)} }} }} ",
            "else { ",
            $"[pscustomobject]@{{ ExitCode={sourceProbeExit}; Stdout={PsQuote(sourceProbeText)}; Stderr={PsQuote(sourceProbeError)} }} }} }}\n",
            "try {\n",
            " Assert-OpenClawWingetCli -WingetPath 'C:\\Package\\winget.exe' -TemporaryRoot 'D:\\owned'\n",
            " [Console]::Out.Write('accepted|' + ($script:calls -join ','))\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static string BuildWingetAliasProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var alias = ExtractPowerShellFunction(
            controller,
            "Wait-OpenClawWingetExecutionAlias",
            "Assert-OpenClawWingetCli");
        var wingetResult = scenario switch
        {
            "retry" => "if ($script:calls -ge 3) { [pscustomobject]@{ Path=$script:expected } }",
            "mismatch" => "[pscustomobject]@{ Path='C:\\Unexpected\\winget.exe' }",
            _ => "$null",
        };
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            alias,
            "\n$env:LOCALAPPDATA='C:\\OpenClawAliasTest'\n",
            "$script:expected=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Microsoft\\WindowsApps\\winget.exe'))\n",
            "$script:calls=0\n",
            "function Get-Command { [CmdletBinding()] param([Parameter(Position=0)][string]$Name,[object]$CommandType,[switch]$All) ",
            "if ($Name -ceq 'Get-AppExecutionAlias') { return $null }; ",
            "if ($Name -ceq 'winget.exe') { $script:calls++; ",
            wingetResult,
            " } }\n",
            "function Test-Path { [CmdletBinding()] param([string]$LiteralPath,[object]$PathType) ",
            "return ($script:calls -ge 3 -and $LiteralPath -ceq $script:expected) }\n",
            "try {\n",
            " $resolved=Wait-OpenClawWingetExecutionAlias ",
            "-ExpectedPackageFamilyName 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' ",
            "-RetryCount 3 -DelayMilliseconds 0\n",
            " [Console]::Out.Write('accepted|' + $script:calls)\n",
            "} catch { [Console]::Out.Write('rejected|' + $_.Exception.Message) }\n");
    }

    private static void WriteZipEntry(
        System.IO.Compression.ZipArchive archive,
        string name,
        byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string ExtractPowerShellRange(
        string script,
        string startMarker,
        string endMarker)
    {
        var start = script.IndexOf(startMarker, StringComparison.Ordinal);
        var end = script.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"PowerShell range start marker was not found: {startMarker}");
        Assert.True(end > start, $"PowerShell range end marker was not found: {endMarker}");
        return script[start..end];
    }

    private static string PsQuote(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(Root, "scripts", "clean-windows", name));

    private static string BuildVhdChainProof(string scenario)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var propertyValue = ExtractPowerShellFunction(
            controller,
            "Get-PropertyValueOrNull",
            "Assert-HyperVPrerequisites");
        var resolver = ExtractPowerShellFunction(
            controller,
            "Resolve-CanonicalExistingVhdPath",
            "Assert-VhdChainReachesOwnedBase");
        var ancestry = ExtractPowerShellFunction(
            controller,
            "Assert-VhdChainReachesOwnedBase",
            "Assert-OwnedVmDiskBinding");
        var scenarioSetup = scenario switch
        {
            "DirectBase" => "",
            "CheckpointLeaf" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\os_622E57AA-66AB-4904-B875-7705065AF129.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath 'd:\HYPER-V\OpenClaw-Clean-Windows\.\os.vhdx'
                """,
            "MultipleLevels" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\level-3.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath 'D:\Hyper-V\OpenClaw-Clean-Windows\level-2.avhdx'
                Add-FakeVhd -Path 'D:\Hyper-V\OpenClaw-Clean-Windows\level-2.avhdx' -ParentPath 'D:\Hyper-V\OpenClaw-Clean-Windows\level-1.avhdx'
                Add-FakeVhd -Path 'D:\Hyper-V\OpenClaw-Clean-Windows\level-1.avhdx' -ParentPath $basePath
                """,
            "WrongTerminal" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\wrong-leaf.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath 'D:\Hyper-V\Unrelated\data.vhdx'
                Add-FakeVhd -Path 'D:\Hyper-V\Unrelated\data.vhdx' -ParentPath $null
                """,
            "MissingParent" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\missing-parent.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath 'D:\Hyper-V\OpenClaw-Clean-Windows\missing.vhdx'
                """,
            "GetVhdError" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\get-vhd-error.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath $basePath
                $script:getVhdErrorPath = $attachedPath
                """,
            "Cycle" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\cycle-a.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath 'D:\Hyper-V\OpenClaw-Clean-Windows\cycle-b.avhdx'
                Add-FakeVhd -Path 'D:\Hyper-V\OpenClaw-Clean-Windows\cycle-b.avhdx' -ParentPath $attachedPath
                """,
            "OverMaxDepth" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\depth-00.avhdx'
                for ($index = 0; $index -lt 32; $index++) {
                    $path = 'D:\Hyper-V\OpenClaw-Clean-Windows\depth-{0:D2}.avhdx' -f $index
                    $parent = if ($index -eq 31) {
                        $basePath
                    } else {
                        'D:\Hyper-V\OpenClaw-Clean-Windows\depth-{0:D2}.avhdx' -f ($index + 1)
                    }
                    Add-FakeVhd -Path $path -ParentPath $parent
                }
                """,
            "InvalidPath" => "$attachedPath = 'relative\\invalid.avhdx'\n",
            "AmbiguousGetVhd" =>
                """
                $attachedPath = 'D:\Hyper-V\OpenClaw-Clean-Windows\ambiguous.avhdx'
                Add-FakeVhd -Path $attachedPath -ParentPath $basePath
                $script:ambiguousGetVhdPath = $attachedPath
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:VhdChainMaxDepth = 32\n",
            "$script:getVhdCalls = 0\n",
            "$script:getVhdErrorPath = ''\n",
            "$script:ambiguousGetVhdPath = ''\n",
            "$script:existingPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)\n",
            "$script:parents = @{}\n",
            "$basePath = 'D:\\Hyper-V\\OpenClaw-Clean-Windows\\os.vhdx'\n",
            "$attachedPath = $basePath\n",
            "function Add-FakeVhd {\n",
            "    param([string]$Path, [AllowNull()][string]$ParentPath)\n",
            "    $canonicalPath = [IO.Path]::GetFullPath($Path)\n",
            "    [void]$script:existingPaths.Add($canonicalPath)\n",
            "    $script:parents[$canonicalPath] = $ParentPath\n",
            "}\n",
            "function Test-Path {\n",
            "    param([string]$LiteralPath, [object]$PathType)\n",
            "    return $script:existingPaths.Contains([IO.Path]::GetFullPath($LiteralPath))\n",
            "}\n",
            "function Resolve-Path {\n",
            "    param([string]$LiteralPath, [object]$ErrorAction)\n",
            "    $canonicalPath = [IO.Path]::GetFullPath($LiteralPath)\n",
            "    if (-not $script:existingPaths.Contains($canonicalPath)) { throw 'missing fake VHD' }\n",
            "    return [pscustomobject]@{ ProviderPath = $canonicalPath; Path = $canonicalPath }\n",
            "}\n",
            "function Get-VHD {\n",
            "    [CmdletBinding()]\n",
            "    param([string]$Path)\n",
            "    $script:getVhdCalls++\n",
            "    $canonicalPath = [IO.Path]::GetFullPath($Path)\n",
            "    if ([string]::Equals($canonicalPath, $script:getVhdErrorPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'simulated Get-VHD failure' }\n",
            "    if (-not $script:parents.ContainsKey($canonicalPath)) { throw 'unknown fake VHD' }\n",
            "    $record = [pscustomobject]@{ Path = $canonicalPath; ParentPath = $script:parents[$canonicalPath] }\n",
            "    if ([string]::Equals($canonicalPath, $script:ambiguousGetVhdPath, [StringComparison]::OrdinalIgnoreCase)) { return @($record, $record) }\n",
            "    return $record\n",
            "}\n",
            propertyValue,
            "\n",
            resolver,
            "\n",
            ancestry,
            "\n",
            "Add-FakeVhd -Path $basePath -ParentPath $null\n",
            scenarioSetup,
            "\ntry {\n",
            "    $chain = Assert-VhdChainReachesOwnedBase -AttachedVhdPath $attachedPath -OwnedBaseVhdPath $basePath\n",
            "    [Console]::Out.Write(\"accepted|$($chain.Depth)|$($chain.ChainLength)|$script:getVhdCalls\")\n",
            "} catch {\n",
            "    [Console]::Out.Write(\"rejected|$($_.Exception.Message)|$script:getVhdCalls\")\n",
            "}\n");
    }

    private static string BuildDuplicateActiveVhdProof()
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
        var diskBinding = ExtractPowerShellFunction(
            controller,
            "Assert-OwnedVmDiskBinding",
            "Assert-OwnedVM");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$VMName = 'OpenClaw-Disk-Proof'\n",
            "$script:chainCalls = 0\n",
            "$script:getVhdCalls = 0\n",
            "$vm = [pscustomobject]@{ Name = $VMName; Id = [Guid]'11111111-1111-1111-1111-111111111111' }\n",
            "$drive1 = [pscustomobject]@{ Path = 'D:\\Hyper-V\\OpenClaw-Clean-Windows\\checkpoint-a.avhdx' }\n",
            "$drive2 = [pscustomobject]@{ Path = 'D:\\Hyper-V\\OpenClaw-Clean-Windows\\checkpoint-b.avhdx' }\n",
            "function Get-VMHardDiskDrive { [CmdletBinding()] param([object]$VM) return @($drive1, $drive2) }\n",
            "function Get-VHD { [CmdletBinding()] param([string]$Path) $script:getVhdCalls++ }\n",
            "function Assert-VhdChainReachesOwnedBase { param([string]$AttachedVhdPath, [string]$OwnedBaseVhdPath) $script:chainCalls++ }\n",
            stringEquals,
            "\n",
            propertyValue,
            "\n",
            diskBinding,
            "\ntry {\n",
            "    Assert-OwnedVmDiskBinding -VmObject $vm -ResolvedVhdPath 'D:\\Hyper-V\\OpenClaw-Clean-Windows\\os.vhdx' | Out-Null\n",
            "    [Console]::Out.Write('unexpected-success')\n",
            "} catch {\n",
            "    [Console]::Out.Write(\"$script:chainCalls|$script:getVhdCalls|$($_.Exception.Message)\")\n",
            "}\n");
    }

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
            "Resolve-CanonicalExistingVhdPath");
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

    private static string BuildOptionalFeatureStageProof(
        bool featuresEnabled,
        string? initialState = null)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var getter = ExtractPowerShellFunction(
            controller,
            "Get-GuestOptionalFeatureStageScriptBlock",
            "Invoke-GuestOptionalFeatureStage");
        var state = initialState ?? (featuresEnabled ? "Enabled" : "Disabled");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:featureStates = @{\n",
            "  'Microsoft-Windows-Subsystem-Linux' = '",
            state,
            "'\n",
            "  'VirtualMachinePlatform' = '",
            state,
            "'\n",
            "}\n",
            "$script:enableCalls = 0\n",
            "function Get-WindowsOptionalFeature {\n",
            "  [CmdletBinding()]\n",
            "  param([switch]$Online, [string]$FeatureName)\n",
            "  return [pscustomobject]@{ FeatureName = $FeatureName; State = $script:featureStates[$FeatureName] }\n",
            "}\n",
            "function Enable-WindowsOptionalFeature {\n",
            "  [CmdletBinding()]\n",
            "  param([switch]$Online, [string]$FeatureName, [switch]$All, [switch]$NoRestart)\n",
            "  if ($script:featureStates[$FeatureName] -notin @('Disabled', 'DisabledWithPayloadRemoved')) { throw 'Attempted to enable a non-disabled feature.' }\n",
            "  $script:enableCalls++\n",
            "  $script:featureStates[$FeatureName] = 'Enabled'\n",
            "  return [pscustomobject]@{ RestartNeeded = $false }\n",
            "}\n",
            getter,
            "\n$result = & (Get-GuestOptionalFeatureStageScriptBlock)\n",
            "$result | Add-Member -NotePropertyName enableCalls -NotePropertyValue $script:enableCalls\n",
            "[Console]::Out.Write(($result | ConvertTo-Json -Compress))\n");
    }

    private static string BuildNativeWslHelperProof(
        string ownedRoot,
        string operation = "Status",
        int exitCode = 50)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var getter = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslNativeHelperInstallerScriptBlock",
            "Install-GuestWslNativeHelper");
        var escapedOwnedRoot = ownedRoot.Replace("'", "''", StringComparison.Ordinal);
        var escapedOperation = operation.Replace("'", "''", StringComparison.Ordinal);
        var stdoutExpression = operation switch
        {
            "UpdateWebDownload" => "'Update completed.'",
            _ => "''",
        };
        var stderrExpression = operation switch
        {
            "Status" => "('WSL is not installed ' + [char]0x03A9)",
            "Version" => "'Press any key to install WSL. Operation aborted. WSL must be updated.'",
            _ => "''",
        };
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$job = Start-Job -ScriptBlock {\n",
            "  param($OwnedRoot)\n",
            "  $ErrorActionPreference = 'Stop'\n",
            getter,
            "\n  New-Item -ItemType Directory -Force -Path $OwnedRoot | Out-Null\n",
            "  $env:TEMP = $OwnedRoot\n",
            "  $env:TMP = $OwnedRoot\n",
            "  [Environment]::SetEnvironmentVariable('WSL_UTF8', 'prior-value', [EnvironmentVariableTarget]::Process)\n",
            "  $script:wslUtf8AtLaunch = ''\n",
            "  $script:wslPathAtLaunch = ''\n",
            "  $script:wslArgumentsAtLaunch = @()\n",
            "  $script:waitAtLaunch = $false\n",
            "  $script:passThruAtLaunch = $false\n",
            "  function global:Test-Path {\n",
            "    [CmdletBinding()]\n",
            "    param([string]$LiteralPath, [object]$PathType)\n",
            "    if ($LiteralPath -like '*\\System32\\wsl.exe') { return $true }\n",
            "    return Microsoft.PowerShell.Management\\Test-Path @PSBoundParameters\n",
            "  }\n",
            "  function global:Start-Process {\n",
            "    [CmdletBinding()]\n",
            "    param(\n",
            "      [string]$FilePath,\n",
            "      [string[]]$ArgumentList,\n",
            "      [string]$RedirectStandardOutput,\n",
            "      [string]$RedirectStandardError,\n",
            "      [switch]$Wait,\n",
            "      [switch]$PassThru,\n",
            "      [object]$WindowStyle\n",
            "    )\n",
            "    $script:wslUtf8AtLaunch = [Environment]::GetEnvironmentVariable('WSL_UTF8', [EnvironmentVariableTarget]::Process)\n",
            "    $script:wslPathAtLaunch = $FilePath\n",
            "    $script:wslArgumentsAtLaunch = [string[]]@($ArgumentList)\n",
            "    $script:waitAtLaunch = [bool]$Wait\n",
            "    $script:passThruAtLaunch = [bool]$PassThru\n",
            "    [IO.File]::WriteAllText($RedirectStandardOutput, ",
            stdoutExpression,
            ", [Text.Encoding]::UTF8)\n",
            "    [IO.File]::WriteAllText($RedirectStandardError, ",
            stderrExpression,
            ", [Text.Encoding]::UTF8)\n",
            "    return [pscustomobject]@{ ExitCode = ",
            exitCode,
            " }\n",
            "  }\n",
            "  & (Get-GuestWslNativeHelperInstallerScriptBlock) | Out-Null\n",
            "  $nativeResult = Invoke-OpenClawTrustedWslProcess -Operation '",
            escapedOperation,
            "'\n",
            "  [pscustomobject][ordered]@{\n",
            "    exitCode = [int]$nativeResult.exitCode\n",
            "    stdout = [string]$nativeResult.stdout\n",
            "    stderr = [string]$nativeResult.stderr\n",
            "    utf8Decoded = [string]$nativeResult.stderr -like ('*' + [char]0x03A9)\n",
            "    wslUtf8AtLaunch = $script:wslUtf8AtLaunch\n",
            "    wslUtf8After = [Environment]::GetEnvironmentVariable('WSL_UTF8', [EnvironmentVariableTarget]::Process)\n",
            "    wslPathAtLaunch = $script:wslPathAtLaunch\n",
            "    wslArgumentsAtLaunch = $script:wslArgumentsAtLaunch -join ' '\n",
            "    waitAtLaunch = $script:waitAtLaunch\n",
            "    passThruAtLaunch = $script:passThruAtLaunch\n",
            "    remainingCaptureFiles = @(Get-ChildItem -LiteralPath $OwnedRoot -Filter 'openclaw-wsl-*.txt' -File -Recurse).Count\n",
            "  }\n",
            "} -ArgumentList '",
            escapedOwnedRoot,
            "'\n",
            "Wait-Job -Job $job -Timeout 15 | Out-Null\n",
            "$jobState = [string]$job.State\n",
            "$payload = @(Receive-Job -Job $job -ErrorAction Stop) | Select-Object -Last 1\n",
            "Remove-Job -Job $job -Force\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            "  jobState = $jobState\n",
            "  exitCode = [int]$payload.exitCode\n",
            "  stdout = [string]$payload.stdout\n",
            "  stderr = [string]$payload.stderr\n",
            "  utf8Decoded = [bool]$payload.utf8Decoded\n",
            "  wslUtf8AtLaunch = [string]$payload.wslUtf8AtLaunch\n",
            "  wslUtf8After = [string]$payload.wslUtf8After\n",
            "  wslPathAtLaunch = [string]$payload.wslPathAtLaunch\n",
            "  wslArgumentsAtLaunch = [string]$payload.wslArgumentsAtLaunch\n",
            "  waitAtLaunch = [bool]$payload.waitAtLaunch\n",
            "  passThruAtLaunch = [bool]$payload.passThruAtLaunch\n",
            "  remainingCaptureFiles = [int]$payload.remainingCaptureFiles\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildNativeWslHelperFailureProof(string ownedRoot)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var getter = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslNativeHelperInstallerScriptBlock",
            "Install-GuestWslNativeHelper");
        var escapedOwnedRoot = ownedRoot.Replace("'", "''", StringComparison.Ordinal);
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$OwnedRoot = '",
            escapedOwnedRoot,
            "'\n",
            getter,
            "\nNew-Item -ItemType Directory -Force -Path $OwnedRoot | Out-Null\n",
            "$env:TEMP = $OwnedRoot\n",
            "$env:TMP = $OwnedRoot\n",
            "[Environment]::SetEnvironmentVariable('WSL_UTF8', $null, [EnvironmentVariableTarget]::Process)\n",
            "$script:wslUtf8AtLaunch = ''\n",
            "function global:Test-Path {\n",
            "  [CmdletBinding()]\n",
            "  param([string]$LiteralPath, [object]$PathType)\n",
            "  if ($LiteralPath -like '*\\System32\\wsl.exe') { return $true }\n",
            "  return Microsoft.PowerShell.Management\\Test-Path @PSBoundParameters\n",
            "}\n",
            "function global:Start-Process {\n",
            "  [CmdletBinding()]\n",
            "  param(\n",
            "    [string]$FilePath,\n",
            "    [string[]]$ArgumentList,\n",
            "    [string]$RedirectStandardOutput,\n",
            "    [string]$RedirectStandardError,\n",
            "    [switch]$Wait,\n",
            "    [switch]$PassThru,\n",
            "    [object]$WindowStyle\n",
            "  )\n",
            "  $script:wslUtf8AtLaunch = [Environment]::GetEnvironmentVariable('WSL_UTF8', [EnvironmentVariableTarget]::Process)\n",
            "  [IO.File]::WriteAllText($RedirectStandardOutput, 'partial output', [Text.Encoding]::UTF8)\n",
            "  [IO.File]::WriteAllText($RedirectStandardError, 'partial error', [Text.Encoding]::UTF8)\n",
            "  throw 'simulated native launch failure'\n",
            "}\n",
            "& (Get-GuestWslNativeHelperInstallerScriptBlock) | Out-Null\n",
            "$errorMessage = ''\n",
            "try {\n",
            "  Invoke-OpenClawTrustedWslProcess -Operation 'Status' | Out-Null\n",
            "  throw 'Expected trusted WSL launch failure.'\n",
            "} catch {\n",
            "  $errorMessage = $_.Exception.Message\n",
            "}\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            "  error = $errorMessage\n",
            "  wslUtf8AtLaunch = $script:wslUtf8AtLaunch\n",
            "  wslUtf8AbsentAfter = $null -eq [Environment]::GetEnvironmentVariable('WSL_UTF8', [EnvironmentVariableTarget]::Process)\n",
            "  remainingCaptureFiles = @(Get-ChildItem -LiteralPath $OwnedRoot -Filter 'openclaw-wsl-*.txt' -File -Recurse).Count\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildWslPackageStageProof(
        bool packageInstalled,
        int installExitCode = 0,
        int statusExitCode = 50,
        string statusOutput = "WSL is not installed",
        int versionExitCode = 50,
        string versionOutput = "WSL is not installed",
        string installOutput = "The operation completed successfully.",
        int updateExitCode = 0,
        string updateOutput = "The update completed successfully.",
        string updateError = "",
        bool captureFailure = false)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var getter = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslPackageStageScriptBlock",
            "Invoke-GuestWslPackageStage");
        var escapedStatusOutput = statusOutput.Replace("'", "''", StringComparison.Ordinal);
        var escapedVersionOutput = versionOutput.Replace("'", "''", StringComparison.Ordinal);
        var escapedInstallOutput = installOutput.Replace("'", "''", StringComparison.Ordinal);
        var escapedUpdateOutput = updateOutput.Replace("'", "''", StringComparison.Ordinal);
        var escapedUpdateError = updateError.Replace("'", "''", StringComparison.Ordinal);
        var resultBody = packageInstalled
            ? """
              return [pscustomobject]@{
                operation = $Operation
                exitCode = 0
                stdout = if ($Operation -eq 'Status') { 'WSL status ready' } else { 'WSL version 2.6.1' }
                stderr = ''
              }
              """
            : string.Concat(
                "  if ($Operation -eq 'UpdateWebDownload') {\n",
                "    return [pscustomobject]@{\n",
                "      operation = $Operation\n",
                "      exitCode = ",
                updateExitCode,
                "\n",
                "      stdout = '",
                escapedUpdateOutput,
                "'\n",
                "      stderr = '",
                escapedUpdateError,
                "'\n",
                "    }\n",
                "  }\n",
                "  if ($Operation -eq 'InstallNoDistribution') {\n",
                "    return [pscustomobject]@{\n",
                "      operation = $Operation\n",
                "      exitCode = ",
                installExitCode,
                "\n",
                "      stdout = '",
                escapedInstallOutput,
                "'\n",
                "      stderr = ''\n",
                "    }\n",
                "  }\n",
                "  if ($Operation -eq 'Status') {\n",
                "    return [pscustomobject]@{\n",
                "      operation = $Operation\n",
                "      exitCode = ",
                statusExitCode,
                "\n",
                "      stdout = ''\n",
                "      stderr = '",
                escapedStatusOutput,
                "'\n",
                "    }\n",
                "  }\n",
                "  return [pscustomobject]@{\n",
                "    operation = $Operation\n",
                "    exitCode = ",
                versionExitCode,
                "\n",
                "    stdout = '",
                escapedVersionOutput,
                "'\n",
                "    stderr = ''\n",
                "  }\n");
        var execution = captureFailure
            ? """
              try {
                & (Get-GuestWslPackageStageScriptBlock) | Out-Null
                throw 'Expected WSL package stage failure.'
              } catch {
                [Console]::Out.Write(([pscustomobject][ordered]@{
                  calls = $script:nativeCalls -join ','
                  error = $_.Exception.Message
                } | ConvertTo-Json -Compress))
              }
              """
            : """
              $result = & (Get-GuestWslPackageStageScriptBlock)
              [Console]::Out.Write(([pscustomobject][ordered]@{
                calls = $script:nativeCalls -join ','
                normalizedState = [string]$result.normalizedState
                wasInstalled = [bool]$result.wasInstalled
                installInvoked = [bool]$result.installInvoked
                installExitCode = $result.installExitCode
                updateInvoked = [bool]$result.updateInvoked
                updateExitCode = $result.updateExitCode
                versionExitCode = $result.versionExitCode
                needsRestart = [bool]$result.needsRestart
              } | ConvertTo-Json -Compress))
              """;
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:nativeCalls = New-Object 'Collections.Generic.List[string]'\n",
            "function Get-WindowsOptionalFeature {\n",
            "  [CmdletBinding()]\n",
            "  param([switch]$Online, [string]$FeatureName)\n",
            "  return [pscustomobject]@{ FeatureName = $FeatureName; State = 'Enabled' }\n",
            "}\n",
            "function global:Invoke-OpenClawTrustedWslProcess {\n",
            "  param([string]$Operation)\n",
            "  [void]$script:nativeCalls.Add($Operation)\n",
            resultBody,
            "\n}\n",
            getter,
            "\n",
            execution,
            "\n");
    }

    private static string BuildWslVerificationStageProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var getter = ExtractPowerShellFunction(
            controller,
            "Get-GuestWslVerificationStageScriptBlock",
            "Invoke-GuestWslVerificationStage");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "function Get-WindowsOptionalFeature {\n",
            "  [CmdletBinding()]\n",
            "  param([switch]$Online, [string]$FeatureName)\n",
            "  return [pscustomobject]@{ FeatureName = $FeatureName; State = 'Enabled' }\n",
            "}\n",
            "function global:Invoke-OpenClawTrustedWslProcess {\n",
            "  param([string]$Operation)\n",
            "  return [pscustomobject]@{\n",
            "    operation = $Operation\n",
            "    exitCode = 0\n",
            "    stdout = if ($Operation -eq 'Status') { 'WSL status ready' } else { 'WSL version 2.6.1' }\n",
            "    stderr = ''\n",
            "  }\n",
            "}\n",
            getter,
            "\n$result = & (Get-GuestWslVerificationStageScriptBlock)\n",
            "[Console]::Out.Write(($result | ConvertTo-Json -Compress))\n");
    }

    private static string BuildFailedGuestJobDiagnosticProof()
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var helpersStart = controller.IndexOf(
            "function ConvertTo-SafeGuestDiagnosticText",
            StringComparison.Ordinal);
        var helpersEnd = controller.IndexOf(
            "function Invoke-GuestCommandWithTimeout",
            helpersStart,
            StringComparison.Ordinal);
        Assert.True(helpersStart >= 0);
        Assert.True(helpersEnd > helpersStart);
        var helpers = controller[helpersStart..helpersEnd];
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            helpers,
            "\n$job = Start-Job -ScriptBlock {\n",
            "  Write-Output ('child-output-' + ('x' * 2000))\n",
            "  Write-Output 'token=do-not-print'\n",
            "  Write-Error 'child-error'\n",
            "  throw 'child-reason'\n",
            "}\n",
            "Wait-Job -Job $job -Timeout 15 | Out-Null\n",
            "$jobState = [string]$job.State\n",
            "$jobId = $job.Id\n",
            "$diagnostic = Get-FailedGuestJobDiagnostic -Job $job\n",
            "Remove-Job -Job $job -Force\n",
            "$jobRemoved = $null -eq (Get-Job -Id $jobId -ErrorAction SilentlyContinue)\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            "  jobState = $jobState\n",
            "  jobRemoved = $jobRemoved\n",
            "  diagnostic = $diagnostic\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildPowerShell7InstallProof(
        string scenario,
        string reportedVersion,
        int installExitCode)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var getter = ExtractPowerShellFunction(
            controller,
            "Get-GuestPowerShell7InstallScriptBlock",
            "Ensure-GuestPowerShell7Installed");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$env:ProgramFiles = 'C:\\PinnedPrograms'\n",
            "$script:expectedPwshPath = 'C:\\PinnedPrograms\\PowerShell\\7\\pwsh.exe'\n",
            "$script:installed = ",
            scenario == "existing" ? "$true" : "$false",
            "\n",
            "$script:calls = New-Object 'Collections.Generic.List[string]'\n",
            "$script:installArguments = ''\n",
            "function global:Get-Command {\n",
            "  [CmdletBinding()]\n",
            "  param([string]$Name, [object]$CommandType)\n",
            "  if ($Name -eq 'winget.exe') { return [pscustomobject]@{ Source='C:\\WindowsApps\\winget.exe' } }\n",
            "  if ($Name -eq 'pwsh.exe' -and $script:installed) {\n",
            scenario == "wrong-path"
                ? "    return [pscustomobject]@{ Source='C:\\Unexpected\\pwsh.exe' }\n"
                : "    return [pscustomobject]@{ Source=$script:expectedPwshPath }\n",
            "  }\n",
            "  return $null\n",
            "}\n",
            "function global:Test-Path {\n",
            "  [CmdletBinding()]\n",
            "  param([string]$LiteralPath, [object]$PathType)\n",
            "  if ($LiteralPath -eq $script:expectedPwshPath) { return $script:installed }\n",
            "  return Microsoft.PowerShell.Management\\Test-Path -LiteralPath $LiteralPath\n",
            "}\n",
            "function global:Start-Process {\n",
            "  [CmdletBinding()]\n",
            "  param(\n",
            "    [string]$FilePath,\n",
            "    [string[]]$ArgumentList,\n",
            "    [string]$RedirectStandardOutput,\n",
            "    [string]$RedirectStandardError,\n",
            "    [switch]$Wait,\n",
            "    [switch]$PassThru,\n",
            "    [object]$WindowStyle)\n",
            "  $operation = if ($ArgumentList[0] -eq 'install') { 'InstallPinnedWix' } else { 'ReadInstalledVersion' }\n",
            "  [void]$script:calls.Add($operation)\n",
            "  if ($operation -eq 'InstallPinnedWix') {\n",
            "    $script:installArguments = $ArgumentList -join '|'\n",
            "    $script:installed = ",
            installExitCode == 0 ? "$true" : "$false",
            "\n",
            "    Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardOutput -Value 'installer output' -NoNewline\n",
            "    Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardError -Value '' -NoNewline\n",
            "    return [pscustomobject]@{ ExitCode = ",
            installExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " }\n",
            "  }\n",
            "  Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardOutput -Value ",
            PsQuote(reportedVersion),
            " -NoNewline\n",
            "  Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardError -Value '' -NoNewline\n",
            "  return [pscustomobject]@{ ExitCode = 0 }\n",
            "}\n",
            getter,
            "\n$errorMessage = $null\n",
            "try {\n",
            "  & (Get-GuestPowerShell7InstallScriptBlock) '7.6.4.0' '7.6.4'\n",
            "} catch { $errorMessage = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            "  calls = $script:calls -join ','\n",
            "  installArguments = $script:installArguments\n",
            "  error = $errorMessage\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildDeveloperPrerequisiteProof(
        string packageKey,
        string scenario,
        int installExitCode)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var worker = ExtractPowerShellFunction(
            controller,
            "Get-GuestDeveloperPrerequisiteScriptBlock",
            "Get-GuestBootIdentity");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:installed = ",
            scenario == "existing" ? "$true" : "$false",
            "\n",
            "$script:calls = [System.Collections.Generic.List[string]]::new()\n",
            "$script:installArguments = ''\n",
            "$script:nextId = 300\n",
            "function global:Get-Command {\n",
            " param($Name, $CommandType, $ErrorAction)\n",
            " if ($Name -eq 'winget.exe') { return [pscustomobject]@{ Source = 'C:\\WindowsApps\\winget.exe' } }\n",
            " if (-not $script:installed) { return $null }\n",
            " switch ($Name) {\n",
            "  'dotnet.exe' { return [pscustomobject]@{ Source = 'C:\\Program Files\\dotnet\\dotnet.exe' } }\n",
            "  'node.exe' { return [pscustomobject]@{ Source = 'C:\\Program Files\\nodejs\\node.exe' } }\n",
            "  'npm.cmd' { return [pscustomobject]@{ Source = 'C:\\Program Files\\nodejs\\npm.cmd' } }\n",
            "  default { return $null }\n",
            " }\n",
            "}\n",
            "function global:Test-Path {\n",
            " param($LiteralPath, $PathType)\n",
            " if ([string]$LiteralPath -like '*Windows Kits\\10\\Include') { return $script:installed }\n",
            " if ([string]$LiteralPath -like 'HKLM:*' -or [string]$LiteralPath -like 'HKCU:*') { return $script:installed }\n",
            " if ($PSBoundParameters.ContainsKey('PathType')) {\n",
            "  return Microsoft.PowerShell.Management\\Test-Path -LiteralPath $LiteralPath -PathType $PathType\n",
            " }\n",
            " return Microsoft.PowerShell.Management\\Test-Path -LiteralPath $LiteralPath\n",
            "}\n",
            "function global:Get-ChildItem {\n",
            " param($LiteralPath, [switch]$Directory, $ErrorAction)\n",
            " if ([string]$LiteralPath -like '*Windows Kits\\10\\Include') {\n",
            "  if ($script:installed) { return [pscustomobject]@{ Name = '10.0.26100.0' } }\n",
            "  return\n",
            " }\n",
            " return Microsoft.PowerShell.Management\\Get-ChildItem @PSBoundParameters\n",
            "}\n",
            "function global:Get-ItemProperty {\n",
            " param($LiteralPath, $ErrorAction)\n",
            " if ($script:installed) { return [pscustomobject]@{ pv = '150.0.4078.83' } }\n",
            " return $null\n",
            "}\n",
            "function global:Start-Process {\n",
            " param($FilePath, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, ",
            "[switch]$PassThru, $WindowStyle, $ErrorAction)\n",
            " $argumentsText = @($ArgumentList) -join '|'\n",
            " $operation = if ($argumentsText -match '(^|\\|)install(\\||$)') { 'Install' } ",
            "elseif ($FilePath -like '*dotnet.exe') { 'DotNetListSdks' } ",
            "elseif ($FilePath -like '*node.exe') { 'NodeVersion' } else { 'NpmVersion' }\n",
            " [void]$script:calls.Add(\"$operation`:$argumentsText\")\n",
            " $exitCode = 0\n",
            " $stdout = ''\n",
            " $stderr = ''\n",
            " if ($operation -eq 'Install') {\n",
            "  $script:installArguments = $argumentsText\n",
            "  $exitCode = ",
            installExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\n",
            "  $stdout = 'installer output'\n",
            "  $stderr = if (",
            PsQuote(scenario),
            " -eq 'failure') { 'installer error' } else { '' }\n",
            "  if (",
            PsQuote(scenario),
            " -eq 'install') { $script:installed = $true }\n",
            " } elseif ($operation -eq 'DotNetListSdks') {\n",
            "  $stdout = '10.0.302 [C:\\Program Files\\dotnet\\sdk]'\n",
            " } elseif ($operation -eq 'NodeVersion') {\n",
            "  $stdout = 'v24.18.0'\n",
            " } else { $stdout = '11.5.0' }\n",
            " Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardOutput -Value $stdout -NoNewline\n",
            " Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardError -Value $stderr -NoNewline\n",
            " $script:nextId++\n",
            " $process = [pscustomobject]@{ Id = $script:nextId; ExitCode = $exitCode; Handle = 1 }\n",
            " $process | Add-Member -MemberType ScriptMethod -Name WaitForExit ",
            "-Value { param([int]$Milliseconds) ",
            "if ($PSBoundParameters.ContainsKey('Milliseconds')) { return $true } }\n",
            " return $process\n",
            "}\n",
            worker,
            "\n$errorMessage = $null\n",
            "$proof = $null\n",
            "try { $proof = & (Get-GuestDeveloperPrerequisiteScriptBlock) ",
            PsQuote(packageKey),
            " $false 60 } catch { $errorMessage = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " error = $errorMessage\n",
            " verified = if ($proof) { [bool]$proof.verified } else { $false }\n",
            " alreadyInstalled = if ($proof) { [bool]$proof.alreadyInstalled } else { $false }\n",
            " needsRestart = if ($proof) { [bool]$proof.needsRestart } else { $false }\n",
            " rebootInitiated = if ($proof) { [bool]$proof.rebootInitiated } else { $false }\n",
            " installExitCode = if ($proof) { $proof.installExitCode } else { $null }\n",
            " scope = if ($proof) { $proof.scope } else { $null }\n",
            " calls = $script:calls -join ','\n",
            " installArguments = $script:installArguments\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildDeveloperPrerequisiteWorkerControlProof(bool verifyOnly)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var resultValidator = ExtractPowerShellFunction(
            controller,
            "Get-RequiredGuestStageResult",
            "Get-GuestOptionalFeatureStageScriptBlock");
        var scriptBlockGetter = ExtractPowerShellFunction(
            controller,
            "Get-GuestDeveloperPrerequisiteScriptBlock",
            "Get-GuestBootIdentity");
        var worker = ExtractPowerShellFunction(
            controller,
            "Invoke-GuestDeveloperPrerequisiteWorker",
            "Ensure-GuestDeveloperPrerequisite");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:operationName = $null\n",
            "$script:argumentVerifyOnly = $null\n",
            "function Invoke-GuestCommandWithTimeout {\n",
            " param($Session, $OperationName, $TimeoutSec, $ScriptBlock, $ArgumentList)\n",
            " $script:operationName = [string]$OperationName\n",
            " $script:argumentVerifyOnly = [bool]$ArgumentList[1]\n",
            " return [pscustomobject][ordered]@{\n",
            "  stage = 'developer-prerequisite'\n",
            "  packageKey = [string]$ArgumentList[0]\n",
            "  verified = $true\n",
            "  needsRestart = $false\n",
            " }\n",
            "}\n",
            resultValidator,
            "\n",
            scriptBlockGetter,
            "\n",
            worker,
            "\n$session = [Runtime.Serialization.FormatterServices]::GetUninitializedObject(",
            "[System.Management.Automation.Runspaces.PSSession])\n",
            "$errorMessage = $null\n",
            "$proof = $null\n",
            "try { $proof = Invoke-GuestDeveloperPrerequisiteWorker ",
            "-Session $session -PackageKey 'DotNet10' -VerifyOnly $",
            verifyOnly ? "true" : "false",
            " } catch { $errorMessage = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " error = $errorMessage\n",
            " powerShellMajor = [int]$PSVersionTable.PSVersion.Major\n",
            " operationName = $script:operationName\n",
            " argumentVerifyOnly = [bool]$script:argumentVerifyOnly\n",
            " packageKey = if ($proof) { [string]$proof.packageKey } else { $null }\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildSetupDevCheckProof(string guestRoot)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var worker = ExtractPowerShellFunction(
            controller,
            "Get-GuestSetupDevCheckScriptBlock",
            "Prepare-GuestPrerequisites");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:arguments = ''\n",
            "$script:waitWasBounded = $false\n",
            "function global:Start-Process {\n",
            " param($FilePath, $ArgumentList, $WorkingDirectory, $RedirectStandardOutput, ",
            "$RedirectStandardError, [switch]$PassThru, $WindowStyle, $ErrorAction)\n",
            " $script:arguments = @($ArgumentList) -join '|'\n",
            " Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardOutput -Value 'check output' -NoNewline\n",
            " Microsoft.PowerShell.Management\\Set-Content -LiteralPath $RedirectStandardError -Value '' -NoNewline\n",
            " $process = [pscustomobject]@{ Id = 901; ExitCode = 0; Handle = 1 }\n",
            " $process | Add-Member -MemberType ScriptMethod -Name WaitForExit ",
            "-Value { param([int]$Milliseconds) ",
            "if ($PSBoundParameters.ContainsKey('Milliseconds')) { $script:waitWasBounded = $true; return $true } }\n",
            " return $process\n",
            "}\n",
            worker,
            "\n$errorMessage = $null\n",
            "$proof = $null\n",
            "try { $proof = & (Get-GuestSetupDevCheckScriptBlock) ",
            PsQuote(guestRoot),
            " 60 } catch { $errorMessage = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " error = $errorMessage\n",
            " checkOnly = if ($proof) { [bool]$proof.checkOnly } else { $false }\n",
            " arguments = $script:arguments\n",
            " waitWasBounded = $script:waitWasBounded\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildCleanSourceArchiveProof(string repositoryRoot, string archivePath)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var git = ExtractPowerShellFunction(
            controller,
            "Invoke-CleanWindowsSourceGit",
            "Assert-CleanCommittedSourceHead");
        var clean = ExtractPowerShellFunction(
            controller,
            "Assert-CleanCommittedSourceHead",
            "Get-SourceArchiveInstallScriptBlock");
        var validator = ExtractPowerShellFunction(
            controller,
            "Get-SourceArchiveInstallScriptBlock",
            "Get-GuestSourceStagingScriptBlock");
        var create = ExtractPowerShellFunction(
            controller,
            "New-CleanWindowsSourceArchive",
            "Copy-RepoToGuest");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:SourceArchiveMaximumBytes = 268435456\n",
            "$script:SourceArchiveMaximumExpandedBytes = 536870912\n",
            "$script:SourceArchiveMaximumTrackedFiles = 20000\n",
            "$script:SourceProvenanceFileName = 'openclaw-source-provenance.json'\n",
            "function ConvertTo-SafeGuestDiagnosticText { param([string]$Text) if ($Text) { $Text } else { '<empty>' } }\n",
            git,
            "\n",
            clean,
            "\n",
            validator,
            "\n",
            create,
            "\n$repo = ",
            PsQuote(repositoryRoot),
            "\n$archivePath = ",
            PsQuote(archivePath),
            "\n",
            "& git -C $repo init -q\n",
            "& git -C $repo config user.name 'Archive Test'\n",
            "& git -C $repo config user.email 'archive-test@example.invalid'\n",
            "New-Item -ItemType Directory -Path (Join-Path $repo 'src') | Out-Null\n",
            "[IO.File]::WriteAllText((Join-Path $repo '.gitignore'), \"bin/`nobj/`nTestResults/`n\")\n",
            "[IO.File]::WriteAllText((Join-Path $repo 'README.md'), 'clean source')\n",
            "[IO.File]::WriteAllText((Join-Path $repo 'src\\app.txt'), 'tracked')\n",
            "& git -C $repo add -A\n",
            "& git -C $repo -c commit.gpgSign=false commit -m 'source' --quiet\n",
            "New-Item -ItemType Directory -Path (Join-Path $repo 'src\\bin') | Out-Null\n",
            "New-Item -ItemType Directory -Path (Join-Path $repo 'tests\\obj') | Out-Null\n",
            "[IO.File]::WriteAllText((Join-Path $repo 'src\\bin\\stale.dll'), 'stale')\n",
            "[IO.File]::WriteAllText((Join-Path $repo 'tests\\obj\\stale.dll'), 'stale')\n",
            "$proof = New-CleanWindowsSourceArchive -RepositoryRoot $repo -ArchivePath $archivePath\n",
            "Add-Type -AssemblyName System.IO.Compression.FileSystem\n",
            "$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)\n",
            "try { $names = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }\n",
            "$hasGenerated = @($names | Where-Object { $_ -match '(^|/)(bin|obj|TestResults)(/|$)' }).Count -ne 0\n",
            "[IO.File]::WriteAllText((Join-Path $repo 'dirty.txt'), 'dirty')\n",
            "$dirtyError = $null\n",
            "try { [void](Assert-CleanCommittedSourceHead -RepositoryRoot $repo) } catch { $dirtyError = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " sourceHead = $proof.SourceHead\n",
            " trackedFileCount = $proof.TrackedFileCount\n",
            " archiveSize = $proof.ArchiveSize\n",
            " sha256 = $proof.ArchiveSha256\n",
            " hasGeneratedEntries = $hasGenerated\n",
            " dirtyError = $dirtyError\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildSourceArchiveInstallProof(
        string archivePath,
        string destination,
        string expectedSha256,
        int expectedFileCount,
        bool install)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var validator = ExtractPowerShellFunction(
            controller,
            "Get-SourceArchiveInstallScriptBlock",
            "Get-GuestSourceStagingScriptBlock");
        var archiveSize = new FileInfo(archivePath).Length;
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            validator,
            "\n$errorMessage = $null\n",
            "$proof = $null\n",
            "try {\n",
            " $proof = & (Get-SourceArchiveInstallScriptBlock) ",
            PsQuote(archivePath),
            " ",
            PsQuote(expectedSha256),
            " ",
            expectedFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " ",
            archiveSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " 268435456 536870912 20000 ",
            "'1111111111111111111111111111111111111111' ",
            PsQuote(destination),
            " 'openclaw-source-provenance.json' $",
            install ? "true" : "false",
            "\n",
            "} catch { $errorMessage = $_.Exception.Message }\n",
            "$provenance = $null\n",
            "$provenancePath = Join-Path ",
            PsQuote(destination),
            " 'openclaw-source-provenance.json'\n",
            "if (Test-Path -LiteralPath $provenancePath -PathType Leaf) { ",
            "$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json }\n",
            "$generated = @()\n",
            "$reparse = @()\n",
            "if (Test-Path -LiteralPath ",
            PsQuote(destination),
            ") {\n",
            " $generated = @(Get-ChildItem -LiteralPath ",
            PsQuote(destination),
            " -Directory -Recurse -Force | Where-Object { @('bin','obj','TestResults') -contains $_.Name })\n",
            " $reparse = @(Get-ChildItem -LiteralPath ",
            PsQuote(destination),
            " -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })\n",
            "}\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " error = $errorMessage\n",
            " archiveExists = Test-Path -LiteralPath ",
            PsQuote(archivePath),
            "\n",
            " destinationExists = Test-Path -LiteralPath ",
            PsQuote(destination),
            "\n",
            " provenanceHead = if ($provenance) { [string]$provenance.sourceHead } else { $null }\n",
            " provenanceSha256 = if ($provenance) { [string]$provenance.archiveSha256 } else { $null }\n",
            " provenanceTrackedFileCount = if ($provenance) { [int]$provenance.trackedFileCount } else { 0 }\n",
            " hasGeneratedDirectories = $generated.Count -ne 0\n",
            " hasReparsePoints = $reparse.Count -ne 0\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildGuestSourceStagingProof(string destination)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var staging = ExtractPowerShellFunction(
            controller,
            "Get-GuestSourceStagingScriptBlock",
            "New-CleanWindowsSourceArchive");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            staging,
            "\n$proof = & (Get-GuestSourceStagingScriptBlock) ",
            PsQuote(destination),
            " '1111111111111111111111111111111111111111' ",
            "'openclaw-source-provenance.json' 60\n",
            "$autoCrlf = (& git -C ",
            PsQuote(destination),
            " config --local core.autocrlf) -join ''\n",
            "$safeCrlf = (& git -C ",
            PsQuote(destination),
            " config --local core.safecrlf) -join ''\n",
            "$status = (& git -C ",
            PsQuote(destination),
            " status --porcelain) -join \"`n\"\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " clean = [bool]$proof.clean\n",
            " beforeSha256 = [string]$proof.beforeSha256\n",
            " afterSha256 = [string]$proof.afterSha256\n",
            " autoCrlf = [string]$autoCrlf\n",
            " safeCrlf = [string]$safeCrlf\n",
            " status = [string]$status\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildGuestSourceStagingMockProof(
        string destination,
        int addExitCode,
        string addStderr)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var staging = ExtractPowerShellFunction(
            controller,
            "Get-GuestSourceStagingScriptBlock",
            "New-CleanWindowsSourceArchive");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            "$script:calls = [System.Collections.Generic.List[string]]::new()\n",
            "$script:nextId = 100\n",
            "$script:processes = @{}\n",
            "function global:Get-Command { [pscustomobject]@{ Source = 'C:\\Program Files\\Git\\cmd\\git.exe' } }\n",
            "function global:Start-Process {\n",
            " param($FilePath, $ArgumentList, $WorkingDirectory, $RedirectStandardOutput, ",
            "$RedirectStandardError, [switch]$PassThru, [switch]$NoNewWindow, ",
            "$WindowStyle, $ErrorAction)\n",
            " $operation = switch -Regex ($ArgumentList) {\n",
            "  '^init$' { 'Init'; break }\n",
            "  '^branch -M main$' { 'BranchMain'; break }\n",
            "  '^config --local user.name' { 'ConfigUserName'; break }\n",
            "  '^config --local user.email' { 'ConfigUserEmail'; break }\n",
            "  '^config --local core.autocrlf' { 'ConfigAutoCrlf'; break }\n",
            "  '^config --local core.safecrlf' { 'ConfigSafeCrlf'; break }\n",
            "  '^add -A$' { 'AddAll'; break }\n",
            "  '^commit ' { 'Commit'; break }\n",
            "  '^status --porcelain$' { 'Status'; break }\n",
            "  default { throw \"Unexpected Git arguments: $ArgumentList\" }\n",
            " }\n",
            " [void]$script:calls.Add($operation)\n",
            " Set-Content -LiteralPath $RedirectStandardOutput -Value '' -NoNewline\n",
            " $exitCode = 0\n",
            " $stderr = ''\n",
            " if ($operation -eq 'AddAll') { $exitCode = ",
            addExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "; $stderr = ",
            PsQuote(addStderr),
            " }\n",
            " Set-Content -LiteralPath $RedirectStandardError -Value $stderr -NoNewline\n",
            " $script:nextId++\n",
            " $process = [pscustomobject]@{ Id = $script:nextId; HasExited = $true; ExitCode = $exitCode; Handle = 1 }\n",
            " $process | Add-Member -MemberType ScriptMethod -Name WaitForExit ",
            "-Value { param([int]$Milliseconds) ",
            "if ($PSBoundParameters.ContainsKey('Milliseconds')) { return $true } }\n",
            " $script:processes[$process.Id] = $process\n",
            " return $process\n",
            "}\n",
            "function global:Wait-Process { param($Id, $Timeout, $ErrorAction) }\n",
            "function global:Get-Process { param($Id, $ErrorAction) return $script:processes[$Id] }\n",
            staging,
            "\n$errorMessage = $null\n",
            "$proof = $null\n",
            "try { $proof = & (Get-GuestSourceStagingScriptBlock) ",
            PsQuote(destination),
            " '1111111111111111111111111111111111111111' ",
            "'openclaw-source-provenance.json' 60 } catch { $errorMessage = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " error = $errorMessage\n",
            " clean = if ($proof) { [bool]$proof.clean } else { $false }\n",
            " warningCount = if ($proof) { [int]$proof.warningCount } else { 0 }\n",
            " warnings = if ($proof) { @($proof.warnings) -join ' | ' } else { '' }\n",
            " calls = $script:calls -join ','\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static string BuildGuestSourceStagingRealCommitFailureProof(string destination)
    {
        var controller = ReadScript("Invoke-CleanWindowsHyperV.ps1");
        var staging = ExtractPowerShellFunction(
            controller,
            "Get-GuestSourceStagingScriptBlock",
            "New-CleanWindowsSourceArchive");
        return string.Concat(
            "$ErrorActionPreference = 'Stop'\n",
            staging,
            "\n& git -C ",
            PsQuote(destination),
            " init --quiet\n",
            "if ($LASTEXITCODE -ne 0) { throw 'Test Git initialization failed.' }\n",
            "$hookPath = Join-Path ",
            PsQuote(destination),
            " '.git\\hooks\\pre-commit'\n",
            "[IO.File]::WriteAllText($hookPath, \"#!/bin/sh`nexit 23`n\", ",
            "(New-Object Text.UTF8Encoding($false)))\n",
            "$errorMessage = $null\n",
            "try { [void](& (Get-GuestSourceStagingScriptBlock) ",
            PsQuote(destination),
            " '1111111111111111111111111111111111111111' ",
            "'openclaw-source-provenance.json' 60) } ",
            "catch { $errorMessage = $_.Exception.Message }\n",
            "[Console]::Out.Write(([pscustomobject][ordered]@{\n",
            " powerShellMajor = [int]$PSVersionTable.PSVersion.Major\n",
            " error = $errorMessage\n",
            "} | ConvertTo-Json -Compress))\n");
    }

    private static void CreateSourceArchive(
        string archivePath,
        params (string Name, string Content, int ExternalAttributes)[] entries)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
            entry.ExternalAttributes = item.ExternalAttributes;
            using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
            writer.Write(item.Content);
        }
    }

    private static int CountOccurrences(string value, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    private static void DeleteTestDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }
        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
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
