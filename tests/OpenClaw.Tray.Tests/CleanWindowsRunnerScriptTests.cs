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

        Assert.Contains("Set-OwnedVmSecurityConfiguration", freshCreate);
        Assert.Contains("Assert-OwnedVM", securitySeam);
        Assert.Contains("[string]$ownedVm.State -ne \"Off\"", securitySeam);
        Assert.Contains("Expected exactly one DVD drive for the verified Windows ISO", securitySeam);
        Assert.Contains("-FirstBootDevice $windowsDvdDrive", securitySeam);
        Assert.Contains("-SecureBootTemplate $script:WindowsSecureBootTemplate", securitySeam);
        Assert.True(
            securitySeam.IndexOf("if (-not $hasKeyProtector)", StringComparison.Ordinal) <
            securitySeam.IndexOf("Set-VMKeyProtector", StringComparison.Ordinal));
        Assert.Contains("Test-KeyProtectorPresent -KeyProtector $keyProtector", securitySeam);
        Assert.Contains("Hyper-V reports an unset protector as four zero bytes", script);
        Assert.True(
            securitySeam.IndexOf("if (-not [bool]$tpmEnabled)", StringComparison.Ordinal) <
            securitySeam.IndexOf("Enable-VMTPM", StringComparison.Ordinal));
        Assert.Contains("Refusing to change an unknown vTPM configuration", securitySeam);

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
            line => line.Contains(
                "$repairedPreFirstStartSecurityConfiguration = $true",
                StringComparison.Ordinal));
        Assert.Contains("-ResolvedWindowsIsoPath ([string]$state.windowsIsoPath)", resume);
        Assert.DoesNotContain("Stop-VM", resume, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperVRecoveryDocs_PreserveAndResumeTheCurrentSecureBootPartialState()
    {
        var docs = File.ReadAllText(Path.Combine(Root, "docs", "CLEAN_WINDOWS_RUNNERS.md"));
        var skill = File.ReadAllText(
            Path.Combine(Root, ".agents", "skills", "openclaw-hyperv-smoke", "SKILL.md"));
        var exactCommand =
            @".\scripts\clean-windows\Invoke-CleanWindowsHyperV.ps1 -Command Create -ResumeUnattended -VMName 'OpenClaw-Clean-Windows' -OwnerId 'openclaw-clean-runner-bkudiess' -VhdPath 'D:\Hyper-V\OpenClaw-Clean-Windows\os.vhdx' -ConfirmOwnedAction";

        foreach (var guidance in new[] { docs, skill })
        {
            Assert.Contains("`MicrosoftWindows`", guidance);
            Assert.Contains("whitespace/case-normalized", guidance);
            Assert.Contains("non-empty GUID", guidance);
            Assert.Contains(exactCommand, guidance);
            Assert.Contains("Do not use `-CleanupUnattend` for this state", guidance);
            Assert.Contains("owned VM is Off", guidance);
            Assert.Contains("Ordinary resume paths do not inject keys", guidance);
            Assert.Contains("configuration was repaired", guidance);
            Assert.Contains("reverified", guidance);
            Assert.Contains("VM, VHD", guidance);
            Assert.Contains("media", guidance);
            Assert.Contains("credentials", guidance);
        }
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
        Assert.Contains("Detach-OwnedInstallationMedia -State $State", controller);
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
