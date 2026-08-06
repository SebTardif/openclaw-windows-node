<#
.SYNOPSIS
    Controls a clean Windows 11 Hyper-V runner lifecycle for local validation.

.DESCRIPTION
    Creates and manages one owned Gen2 Hyper-V VM with two owned checkpoints:
    clean-windows and openclaw-prerequisites.

    Safety rules are strict:
    - Existing VMs and checkpoints are never modified unless -ConfirmOwnedAction
      is present.
    - Existing VMs and checkpoints must carry matching ownership markers.
    - Unowned or mismatched resources are refused.
    - VMs and VHDs are never deleted by this script.

    Commands:
    - Create: verify the Microsoft ISO, create a new Gen2 VM, and perform a
      strict unattended install by default
    - Prepare: capture clean-windows, install guest prerequisites, capture
      openclaw-prerequisites
    - Verify: restore openclaw-prerequisites and verify host plus guest readiness
    - Smoke: restore openclaw-prerequisites, copy the repo, run the typed
      Installed or Upgrade validation lane, retrieve artifacts, then restore
    - Restore: restore a named owned checkpoint
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Create", "Prepare", "Verify", "Smoke", "Restore")]
    [string]$Command,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$VMName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OwnerId,

    [string]$IsoPath,

    [string]$VhdPath,

    [System.Management.Automation.PSCredential]$Credential,

    [string]$CredentialPath,

    [switch]$ConfirmOwnedAction,

    [ValidateSet("Unattended", "Manual")]
    [string]$CreateMode = "Unattended",

    [switch]$GenerateCredential,

    [switch]$ResumeUnattended,

    [switch]$CleanupUnattend,

    [switch]$RecoverPendingCheckpoint,

    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedIsoSha256 = "A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9",

    [ValidatePattern('^[^"\/\\\[\]:;|=,+*?<>@\s][^"\/\\\[\]:;|=,+*?<>@]{0,18}[^"\/\\\[\]:;|=,+*?<>@\s.]$|^[A-Za-z0-9]$')]
    [string]$GuestAdministratorName = "OpenClawAdmin",

    [ValidateSet("clean-windows", "openclaw-prerequisites")]
    [string]$CheckpointName = "openclaw-prerequisites",

    [string]$SwitchName,

    [string]$GuestRoot = "C:\OpenClawRunner",

    [string]$HostArtifactRoot,

    [ValidateSet("Installed", "Upgrade")]
    [string]$ValidationLane = "Installed",

    [string]$PreviousRelease = "",

    [string]$PreviousInstallerSha256 = "",

    [ValidateRange(2, 64)]
    [int]$ProcessorCount = 4,

    [ValidateRange(4, 64)]
    [int]$StartupMemoryGB = 8,

    [ValidateRange(64, 1024)]
    [int]$VhdSizeGB = 80,

    [ValidateRange(60, 7200)]
    [int]$PowerShellDirectTimeoutSec = 900,

    [ValidateRange(900, 14400)]
    [int]$UnattendedInstallTimeoutSec = 7200,

    [ValidateRange(60, 14400)]
    [int]$GuestCommandTimeoutSec = 7200,

    [ValidateRange(30, 1800)]
    [int]$GuestShutdownTimeoutSec = 300,

    [ValidateRange(60, 3600)]
    [int]$GuestRestartTimeoutSec = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$script:VmNotePrefix = "OPENCLAW-CLEAN-WINDOWS:"
$script:MarkerSchema = "openclaw.clean-windows.owner/v1"
$script:CheckpointMarkerSchema = "openclaw.clean-windows.checkpoint-owner/v2"
$script:CleanCheckpointName = "clean-windows"
$script:PreparedCheckpointName = "openclaw-prerequisites"
$script:UnattendMarkerSchema = "openclaw.clean-windows.unattend/v1"
$script:OfficialIsoSha256 = "A61ADEAB895EF5A4DB436E0A7011C92A2FF17BB0357F58B13BBC4062E535E7B9"
$script:ExpectedWindowsImageName = "Windows 11 Enterprise Evaluation"
$script:ExpectedWindowsImageIndex = 1
$script:WindowsSecureBootTemplate = "MicrosoftWindows"
$script:OpticalBootKeyWindowSec = 7
$script:OpticalBootInitialDelayMilliseconds = 750
$script:OpticalBootPulseIntervalMilliseconds = 750
$script:OpticalBootMaxPulseCount = 9
$script:InstallationMediaDetachTimeoutSec = 5
$script:InstallationMediaDetachPollIntervalMilliseconds = 250
$script:CheckpointObservationTimeoutSec = 60
$script:CheckpointObservationPollIntervalMilliseconds = 500
$script:CheckpointCreationWindowSec = 900
$script:LegacyCheckpointRecoveryWindowSec = 21600
$script:CheckpointRecoveryClockSkewSec = 300
$script:VhdChainMaxDepth = 32
$script:GuestPowerShellWingetVersion = "7.6.4.0"
$script:GuestPowerShellVersion = "7.6.4"
$script:SourceArchiveMaximumBytes = 268435456
$script:SourceArchiveMaximumExpandedBytes = 536870912
$script:SourceArchiveMaximumTrackedFiles = 20000
$script:SourceProvenanceFileName = "openclaw-source-provenance.json"
$script:SmokeArtifactArchiveMaximumBytes = 536870912
$script:SmokeArtifactExpandedMaximumBytes = 1073741824
$script:SmokeArtifactMaximumFiles = 20000

Import-Module (Join-Path $PSScriptRoot "CleanWindowsUnattend.psm1") -Force

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-InfoLine {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Gray
}

function Test-WindowsHost {
    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($isWindowsVariable) {
        return [bool]$isWindowsVariable.Value
    }

    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-RequiredParameters {
    if ([string]::IsNullOrWhiteSpace($VhdPath)) {
        throw "VhdPath is required for command '$Command'."
    }

    if ($RecoverPendingCheckpoint -and $Command -ne "Prepare") {
        throw "RecoverPendingCheckpoint is accepted only with -Command Prepare."
    }
    if ($RecoverPendingCheckpoint -and -not $ConfirmOwnedAction) {
        throw "RecoverPendingCheckpoint requires -ConfirmOwnedAction."
    }

    switch ($Command) {
        "Create" {
            if ($ResumeUnattended -and $CleanupUnattend) {
                throw "ResumeUnattended and CleanupUnattend are mutually exclusive."
            }
            if (($ResumeUnattended -or $CleanupUnattend) -and $CreateMode -ne "Unattended") {
                throw "ResumeUnattended and CleanupUnattend require -CreateMode Unattended."
            }
            if (($ResumeUnattended -or $CleanupUnattend) -and $GenerateCredential) {
                throw "GenerateCredential is accepted only for a fresh unattended Create."
            }
            if (($ResumeUnattended -or $CleanupUnattend)) {
                Assert-ConfirmationForOwnedAction -Action "Resuming or cleaning an existing unattended installation"
            } elseif ([string]::IsNullOrWhiteSpace($IsoPath)) {
                throw "IsoPath is required for Create."
            }
            if ($CreateMode -eq "Manual" -and $GenerateCredential) {
                throw "GenerateCredential is accepted only for unattended Create."
            }
            if (
                $CreateMode -eq "Unattended" -and
                $ExpectedIsoSha256.ToUpperInvariant() -cne $script:OfficialIsoSha256
            ) {
                throw "Unattended Create requires the pinned Windows 11 Enterprise Evaluation ISO SHA256."
            }
            if (
                $CreateMode -eq "Unattended" -and
                -not $ResumeUnattended -and
                -not $CleanupUnattend -and
                -not $GenerateCredential
            ) {
                throw "Fresh unattended Create requires -GenerateCredential as explicit credential-generation consent."
            }
            if ($CreateMode -eq "Unattended" -and $null -ne $Credential) {
                throw "Unattended Create generates a per-VM credential. Do not pass -Credential."
            }
            if (-not [string]::IsNullOrWhiteSpace($CredentialPath)) {
                throw "CredentialPath is an output of unattended Create and is not accepted as a Create input."
            }
        }
        "Prepare" {
            if ($null -eq $Credential -and [string]::IsNullOrWhiteSpace($CredentialPath)) {
                throw "Credential or CredentialPath is required for Prepare."
            }
        }
        "Verify" {
            if ($null -eq $Credential -and [string]::IsNullOrWhiteSpace($CredentialPath)) {
                throw "Credential or CredentialPath is required for Verify."
            }
        }
        "Smoke" {
            if ($null -eq $Credential -and [string]::IsNullOrWhiteSpace($CredentialPath)) {
                throw "Credential or CredentialPath is required for Smoke."
            }

            if ($ValidationLane -eq "Upgrade") {
                if ($PreviousRelease -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
                    throw "Upgrade validation requires -PreviousRelease as an exact tag-like SemVer such as v0.6.12."
                }
                if ($PreviousInstallerSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
                    throw "Upgrade validation requires -PreviousInstallerSha256 as exactly 64 hexadecimal characters."
                }
            } elseif (
                -not [string]::IsNullOrWhiteSpace($PreviousRelease) -or
                -not [string]::IsNullOrWhiteSpace($PreviousInstallerSha256)
            ) {
                throw "PreviousRelease and PreviousInstallerSha256 are accepted only with -ValidationLane Upgrade."
            }
        }
    }

    if ($null -ne $Credential -and -not [string]::IsNullOrWhiteSpace($CredentialPath)) {
        throw "Credential and CredentialPath are mutually exclusive."
    }
}

function Assert-ConfirmationForOwnedAction {
    param([string]$Action)

    if (-not $ConfirmOwnedAction) {
        throw "$Action requires -ConfirmOwnedAction and matching ownership markers."
    }
}

function Resolve-ExistingLiteralPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label does not exist: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [IO.Path]::GetFullPath($Path)
}

function Normalize-ComparisonPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    return ([IO.Path]::GetFullPath($Path)).TrimEnd("\").ToLowerInvariant()
}

function Test-StringEquals {
    param(
        [string]$Left,
        [string]$Right
    )

    return [string]::Equals($Left, $Right, [StringComparison]::OrdinalIgnoreCase)
}

function Normalize-SecureBootTemplate {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    $withoutWhitespace = [regex]::Replace([string]$Value, "\s", "")
    return $withoutWhitespace.ToLowerInvariant()
}

function Test-KeyProtectorPresent {
    param([object]$KeyProtector)

    if ($null -eq $KeyProtector) {
        return $false
    }

    $keyProtectorBytes = @($KeyProtector)
    # Hyper-V can report an unset protector as a four-byte host sentinel.
    # A valid local key protector must contain a substantive blob beyond it.
    if ($keyProtectorBytes.Count -le 4) {
        return $false
    }

    return $true
}

function Get-PropertyValueOrNull {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-HyperVPrerequisites {
    Write-Step "Checking Hyper-V prerequisites"

    if (-not (Test-WindowsHost)) {
        throw "This controller requires Windows."
    }

    if (-not (Test-Administrator)) {
        throw "Run this controller from an elevated PowerShell session."
    }

    $requiredCmdlets = @(
        "Get-VM",
        "New-VM",
        "Set-VM",
        "Set-VMProcessor",
        "Set-VMFirmware",
        "Set-VMKeyProtector",
        "Get-VMKeyProtector",
        "Enable-VMTPM",
        "Checkpoint-VM",
        "Get-VMSnapshot",
        "Restore-VMSnapshot",
        "Remove-VMSnapshot",
        "Get-VMHardDiskDrive",
        "Get-VHD",
        "Get-VMProcessor",
        "Get-VMFirmware",
        "Get-VMSecurity",
        "Get-VMDvdDrive",
        "Get-CimInstance",
        "Get-CimAssociatedInstance",
        "Invoke-CimMethod",
        "Start-VM",
        "Stop-VM",
        "Get-VMSwitch",
        "Add-VMDvdDrive",
        "Set-VMDvdDrive"
    )

    foreach ($cmdletName in $requiredCmdlets) {
        if (-not (Get-Command $cmdletName -ErrorAction SilentlyContinue)) {
            throw "Required Hyper-V cmdlet is unavailable: $cmdletName"
        }
    }

    $featureNames = @(
        "Microsoft-Hyper-V-All",
        "Microsoft-Hyper-V-Hypervisor"
    )

    foreach ($featureName in $featureNames) {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName $featureName -ErrorAction SilentlyContinue
        if ($null -eq $feature -or $feature.State -ne "Enabled") {
            throw "Required Windows feature is not enabled: $featureName"
        }
    }

    $vmms = Get-Service -Name vmms -ErrorAction Stop
    if ($vmms.Status -ne "Running") {
        throw "Hyper-V Virtual Machine Management service is not running."
    }

    Get-VMHost | Out-Null
    Write-InfoLine "Hyper-V host checks passed."
}

function Get-OwnedMarkerRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedVmName
    )

    $vhdDirectory = Split-Path -Parent $ResolvedVhdPath
    return Join-Path (Join-Path $vhdDirectory ".openclaw-clean-windows") $OwnedVmName
}

function Get-VMMarkerPath {
    param([string]$ResolvedVhdPath, [string]$OwnedVmName)
    return Join-Path (Get-OwnedMarkerRoot -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $OwnedVmName) "vm.owner.json"
}

function Get-CheckpointMarkerPath {
    param(
        [string]$ResolvedVhdPath,
        [string]$OwnedVmName,
        [string]$OwnedCheckpointName
    )

    return Join-Path (Get-OwnedMarkerRoot -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $OwnedVmName) ("checkpoint.{0}.owner.json" -f $OwnedCheckpointName)
}

function Get-UnattendPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath
    )

    $ownedRoot = Get-OwnedMarkerRoot -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName
    $unattendRoot = Join-Path $ownedRoot "unattend"
    $credentialRoot = Join-Path $ownedRoot "credentials"
    return [pscustomobject]@{
        OwnedRoot = $ownedRoot
        StatePath = Join-Path $ownedRoot "unattend.owner.json"
        UnattendRoot = $unattendRoot
        StagingPath = Join-Path $unattendRoot "staging"
        AnswerFilePath = Join-Path (Join-Path $unattendRoot "staging") "AutoUnattend.xml"
        AnswerIsoPath = Join-Path $unattendRoot "openclaw-unattend.iso"
        SetupCredentialPath = Join-Path $credentialRoot "setup.clixml"
        SetupCredentialMetadataPath = Join-Path $credentialRoot "setup.owner.json"
        FinalCredentialPath = Join-Path $credentialRoot "guest.clixml"
        FinalCredentialMetadataPath = Join-Path $credentialRoot "guest.owner.json"
    }
}

function Get-UnattendedComputerName {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($VMName))
        $suffix = ([BitConverter]::ToString($hash, 0, 4)).Replace("-", "")
        return "OCW-$suffix"
    } finally {
        $sha256.Dispose()
    }
}

function Write-UnattendState {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [Parameter(Mandatory = $true)]
        [object]$Paths
    )

    $State.updatedUtc = [DateTime]::UtcNow.ToString("o")
    Write-CleanWindowsOwnedJsonFile `
        -Path $Paths.StatePath `
        -OwnedRoot $Paths.OwnedRoot `
        -Value $State `
        -Depth 6 | Out-Null
}

function Read-OwnedUnattendState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [object]$Paths
    )

    if (-not (Test-Path -LiteralPath $Paths.StatePath -PathType Leaf)) {
        throw "Owned unattended-install marker is missing."
    }
    $state = Get-Content -LiteralPath $Paths.StatePath -Raw | ConvertFrom-Json
    if (
        $state.schema -cne $script:UnattendMarkerSchema -or
        -not (Test-StringEquals -Left ([string]$state.vmName) -Right $VMName) -or
        -not (Test-StringEquals -Left ([string]$state.ownerId) -Right $OwnerId) -or
        (Normalize-ComparisonPath ([string]$state.vhdPath)) -ne
            (Normalize-ComparisonPath $ResolvedVhdPath) -or
        (Normalize-ComparisonPath ([string]$state.answerIsoPath)) -ne
            (Normalize-ComparisonPath $Paths.AnswerIsoPath) -or
        (Normalize-ComparisonPath ([string]$state.ownedRoot)) -ne
            (Normalize-ComparisonPath $Paths.OwnedRoot) -or
        (Normalize-ComparisonPath ([string]$state.setupCredentialPath)) -ne
            (Normalize-ComparisonPath $Paths.SetupCredentialPath) -or
        (Normalize-ComparisonPath ([string]$state.finalCredentialPath)) -ne
            (Normalize-ComparisonPath $Paths.FinalCredentialPath) -or
        [int]$state.imageIndex -ne $script:ExpectedWindowsImageIndex -or
        [string]$state.imageName -cne $script:ExpectedWindowsImageName -or
        [string]$state.computerName -cne (Get-UnattendedComputerName)
    ) {
        throw "Owned unattended-install marker does not match this VM, owner, VHD, or media path."
    }
    if ([string]$state.expectedIsoSha256 -cne $ExpectedIsoSha256.ToUpperInvariant()) {
        throw "Owned unattended-install marker ISO hash does not match ExpectedIsoSha256."
    }
    return $state
}

function Assert-UnattendStateMatchesVmMarker {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [Parameter(Mandatory = $true)]
        [object]$VmObject
    )

    $vmMarker = Get-VMNoteMarker -VmObject $VmObject
    if (
        $null -eq $vmMarker -or
        -not $vmMarker.PSObject.Properties["isoPath"] -or
        (Normalize-ComparisonPath ([string]$vmMarker.isoPath)) -ne
            (Normalize-ComparisonPath ([string]$State.windowsIsoPath))
    ) {
        throw "Owned unattended-install marker Windows ISO does not match the VM ownership marker."
    }
}

function New-UnattendState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedIsoPath,
        [Parameter(Mandatory = $true)]
        [object]$Paths,
        [Parameter(Mandatory = $true)]
        [string]$ComputerName
    )

    return [pscustomobject][ordered]@{
        schema = $script:UnattendMarkerSchema
        vmName = $VMName
        ownerId = $OwnerId
        vhdPath = $ResolvedVhdPath
        ownedRoot = $Paths.OwnedRoot
        windowsIsoPath = $ResolvedIsoPath
        expectedIsoSha256 = $ExpectedIsoSha256.ToUpperInvariant()
        answerIsoPath = $Paths.AnswerIsoPath
        setupCredentialPath = $Paths.SetupCredentialPath
        finalCredentialPath = $Paths.FinalCredentialPath
        guestAdministratorName = $GuestAdministratorName
        computerName = $ComputerName
        imageIndex = $script:ExpectedWindowsImageIndex
        imageName = $script:ExpectedWindowsImageName
        status = "initializing"
        createdUtc = [DateTime]::UtcNow.ToString("o")
        updatedUtc = [DateTime]::UtcNow.ToString("o")
    }
}

function Set-UnattendStateStatus {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [Parameter(Mandatory = $true)]
        [object]$Paths,
        [Parameter(Mandatory = $true)]
        [string]$Status
    )

    if ($Status -ceq "complete") {
        $completedUtc = if ([string]$State.status -ceq "complete") {
            $legacyCompletion = Get-UnattendedCompletionProof `
                -State $State `
                -StatePath $Paths.StatePath
            $legacyCompletion.CompletionUtc.ToString("o")
        } else {
            [DateTime]::UtcNow.ToString("o")
        }
        if ($State.PSObject.Properties["completedUtc"]) {
            if ([string]::IsNullOrWhiteSpace([string]$State.completedUtc)) {
                $State.completedUtc = $completedUtc
            }
        } else {
            $State | Add-Member -NotePropertyName "completedUtc" -NotePropertyValue $completedUtc
        }
    }
    $State.status = $Status
    Write-UnattendState -State $State -Paths $Paths
}

function Assert-WindowsIsoHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedIsoPath
    )

    Write-Step "Verifying the official Windows ISO SHA256"
    $actualHash = (Get-CleanWindowsFileSha256 -Path $ResolvedIsoPath).ToUpperInvariant()
    $expectedHash = $ExpectedIsoSha256.ToUpperInvariant()
    if ($actualHash -cne $expectedHash) {
        throw "Windows ISO SHA256 does not match ExpectedIsoSha256. Refusing to create the VM."
    }
    Write-InfoLine "Windows ISO SHA256 verified before VM creation."
}

function Resolve-OperationCredential {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath
    )

    if ($null -ne $Credential) {
        return $Credential
    }
    $paths = Get-UnattendPaths -ResolvedVhdPath $ResolvedVhdPath
    $resolvedCredentialPath = [IO.Path]::GetFullPath($CredentialPath)
    $metadataPath = [IO.Path]::ChangeExtension($resolvedCredentialPath, "owner.json")
    return Import-CleanWindowsCredential `
        -CredentialPath $resolvedCredentialPath `
        -MetadataPath $metadataPath `
        -OwnedRoot $paths.OwnedRoot `
        -VMName $VMName `
        -OwnerId $OwnerId `
        -ExpectedKind "final"
}

function New-OwnerMarkerObject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResourceType,
        [Parameter(Mandatory = $true)]
        [string]$ResourceName,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedVmName,
        [Parameter(Mandatory = $true)]
        [string]$OwnedOwnerId,
        [string]$ResolvedIsoPath,
        [object]$VmObject,
        [object]$SnapshotObject
    )

    $marker = [ordered]@{
        schema = $script:MarkerSchema
        resourceType = $ResourceType
        resourceName = $ResourceName
        ownerId = $OwnedOwnerId
        vmName = $OwnedVmName
        vhdPath = $ResolvedVhdPath
        createdUtc = [DateTime]::UtcNow.ToString("o")
    }

    if (-not [string]::IsNullOrWhiteSpace($ResolvedIsoPath)) {
        $marker.isoPath = $ResolvedIsoPath
    }

    if ($null -ne $VmObject) {
        $vmId = Get-PropertyValueOrNull -Object $VmObject -Name "Id"
        if ($null -ne $vmId) {
            $marker.vmId = [string]$vmId
        }
    }

    if ($null -ne $SnapshotObject) {
        $snapshotId = Get-PropertyValueOrNull -Object $SnapshotObject -Name "Id"
        if ($null -ne $snapshotId) {
            $marker.snapshotId = [string]$snapshotId
        }

        $creationTime = Get-PropertyValueOrNull -Object $SnapshotObject -Name "CreationTime"
        if ($null -ne $creationTime) {
            $marker.snapshotCreationTimeUtc = ([DateTime]$creationTime).ToUniversalTime().ToString("o")
        }
    }

    return $marker
}

function ConvertTo-CheckpointUtc {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    try {
        if ($Value -is [DateTime]) {
            return ([DateTime]$Value).ToUniversalTime()
        }
        return ([DateTimeOffset]::Parse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal
        )).UtcDateTime
    } catch {
        throw "$Label is missing or is not a valid UTC timestamp."
    }
}

function Get-RequiredGuidString {
    param(
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $parsed = [Guid]::Empty
    if (
        $null -eq $Value -or
        -not [Guid]::TryParse([string]$Value, [ref]$parsed) -or
        $parsed -eq [Guid]::Empty
    ) {
        throw "$Label is missing or is not a non-empty GUID."
    }
    return $parsed.ToString("D")
}

function New-PendingCheckpointMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName,
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [DateTime]$CreationStartedUtc = [DateTime]::UtcNow,
        [string]$OperationNonce = ([Guid]::NewGuid().ToString("D"))
    )

    if ($OwnedCheckpointName -cnotin @($script:CleanCheckpointName, $script:PreparedCheckpointName)) {
        throw "Checkpoint intent name '$OwnedCheckpointName' is not one of the fixed owned checkpoint names."
    }
    $vmId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $VmObject -Name "Id") `
        -Label "Owned VM id"
    $nonce = Get-RequiredGuidString -Value $OperationNonce -Label "Checkpoint operation nonce"
    $startedUtc = $CreationStartedUtc.ToUniversalTime().ToString("o")

    return [pscustomobject][ordered]@{
        schema = $script:CheckpointMarkerSchema
        status = "pending"
        resourceType = "checkpoint"
        resourceName = $OwnedCheckpointName
        checkpointName = $OwnedCheckpointName
        ownerId = $ExpectedOwnerId
        vmName = $VMName
        vmId = $vmId
        vhdPath = [IO.Path]::GetFullPath($ResolvedVhdPath)
        operationNonce = $nonce
        creationStartedUtc = $startedUtc
        createdUtc = $startedUtc
    }
}

function New-CompletedCheckpointMarker {
    param(
        [Parameter(Mandatory = $true)]
        [object]$PendingMarker,
        [Parameter(Mandatory = $true)]
        [object]$SnapshotObject
    )

    $pendingSchema = [string](Get-PropertyValueOrNull -Object $PendingMarker -Name "schema")
    $pendingStatus = [string](Get-PropertyValueOrNull -Object $PendingMarker -Name "status")
    if (
        $pendingSchema -cne $script:CheckpointMarkerSchema -or
        $pendingStatus -cne "pending"
    ) {
        throw "Only a version 2 pending checkpoint intent can be finalized."
    }

    $snapshotId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $SnapshotObject -Name "Id") `
        -Label "Observed checkpoint id"
    $snapshotCreationTimeUtc = ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $SnapshotObject -Name "CreationTime") `
        -Label "Observed checkpoint creation time"
    $completed = [ordered]@{}
    foreach ($property in $PendingMarker.PSObject.Properties) {
        $completed[$property.Name] = $property.Value
    }
    $completed.status = "complete"
    $completed.snapshotId = $snapshotId
    $completed.snapshotCreationTimeUtc = $snapshotCreationTimeUtc.ToString("o")
    $completed.finalizedUtc = [DateTime]::UtcNow.ToString("o")
    return [pscustomobject]$completed
}

function Write-MarkerFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Marker
    )

    $ownedRoot = Split-Path -Parent $Path
    Write-CleanWindowsOwnedJsonFile `
        -Path $Path `
        -OwnedRoot $ownedRoot `
        -Value $Marker `
        -Depth 8 | Out-Null
}

function Read-MarkerFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Set-VMOwnershipMarker {
    param(
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedOwnerId,
        [string]$ResolvedIsoPath
    )

    $marker = New-OwnerMarkerObject `
        -ResourceType "vm" `
        -ResourceName $VmObject.Name `
        -ResolvedVhdPath $ResolvedVhdPath `
        -OwnedVmName $VmObject.Name `
        -OwnedOwnerId $OwnedOwnerId `
        -ResolvedIsoPath $ResolvedIsoPath `
        -VmObject $VmObject

    $notePayload = ($marker | ConvertTo-Json -Compress)
    Set-VM -VMName $VmObject.Name -Notes ($script:VmNotePrefix + $notePayload) | Out-Null

    $markerPath = Get-VMMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VmObject.Name
    Write-MarkerFile -Path $markerPath -Marker $marker
    return $marker
}

function Get-VMNoteMarker {
    param(
        [Parameter(Mandatory = $true)]
        [object]$VmObject
    )

    $notes = [string](Get-PropertyValueOrNull -Object $VmObject -Name "Notes")
    if ([string]::IsNullOrWhiteSpace($notes) -or -not $notes.StartsWith($script:VmNotePrefix, [StringComparison]::Ordinal)) {
        return $null
    }

    $json = $notes.Substring($script:VmNotePrefix.Length)
    if ([string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    return ($json | ConvertFrom-Json)
}

function Assert-OwnerMarkerMatches {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Marker,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedResourceType,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedResourceName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVmName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVhdPath,
        [object]$VmObject,
        [object]$SnapshotObject
    )

    if ($null -eq $Marker) {
        throw "Ownership marker is missing."
    }

    if ($Marker.schema -ne $script:MarkerSchema) {
        throw "Ownership marker schema is not recognized."
    }

    if ($Marker.resourceType -ne $ExpectedResourceType) {
        throw "Ownership marker resource type '$($Marker.resourceType)' does not match '$ExpectedResourceType'."
    }

    if (-not (Test-StringEquals -Left $Marker.resourceName -Right $ExpectedResourceName)) {
        throw "Ownership marker resource name '$($Marker.resourceName)' does not match '$ExpectedResourceName'."
    }

    if (-not (Test-StringEquals -Left $Marker.ownerId -Right $ExpectedOwnerId)) {
        throw "Ownership marker ownerId '$($Marker.ownerId)' does not match '$ExpectedOwnerId'."
    }

    if (-not (Test-StringEquals -Left $Marker.vmName -Right $ExpectedVmName)) {
        throw "Ownership marker vmName '$($Marker.vmName)' does not match '$ExpectedVmName'."
    }

    if ((Normalize-ComparisonPath $Marker.vhdPath) -ne (Normalize-ComparisonPath $ExpectedVhdPath)) {
        throw "Ownership marker VHD path '$($Marker.vhdPath)' does not match '$ExpectedVhdPath'."
    }

    if ($null -ne $VmObject) {
        $vmId = Get-PropertyValueOrNull -Object $VmObject -Name "Id"
        if ($null -ne $vmId -and $Marker.PSObject.Properties["vmId"] -and [string]$Marker.vmId -ne [string]$vmId) {
            throw "Ownership marker VM id does not match the current VM."
        }
    }

    if ($null -ne $SnapshotObject) {
        $snapshotId = Get-PropertyValueOrNull -Object $SnapshotObject -Name "Id"
        if ($null -ne $snapshotId -and $Marker.PSObject.Properties["snapshotId"] -and [string]$Marker.snapshotId -ne [string]$snapshotId) {
            throw "Ownership marker snapshot id does not match the current checkpoint."
        }

        $creationTime = Get-PropertyValueOrNull -Object $SnapshotObject -Name "CreationTime"
        if ($null -ne $creationTime -and $Marker.PSObject.Properties["snapshotCreationTimeUtc"]) {
            $expectedTime = ([DateTime]$Marker.snapshotCreationTimeUtc).ToUniversalTime()
            $actualTime = ([DateTime]$creationTime).ToUniversalTime()
            if ($expectedTime -ne $actualTime) {
                throw "Ownership marker checkpoint timestamp does not match the current checkpoint."
            }
        }
    }
}

function Assert-CheckpointSnapshotBelongsToVm {
    param(
        [Parameter(Mandatory = $true)]
        [object]$SnapshotObject,
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    if (
        [string](Get-PropertyValueOrNull -Object $SnapshotObject -Name "Name") -cne
            $OwnedCheckpointName
    ) {
        throw "Checkpoint recovery candidate does not have the exact fixed name '$OwnedCheckpointName'."
    }

    $expectedVmId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $VmObject -Name "Id") `
        -Label "Owned VM id"
    $snapshotVmId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $SnapshotObject -Name "VMId") `
        -Label "Checkpoint VM id"
    if ($snapshotVmId -cne $expectedVmId) {
        throw "Checkpoint recovery candidate does not belong to the exact owned VM id."
    }

    $snapshotVmName = Get-PropertyValueOrNull -Object $SnapshotObject -Name "VMName"
    if (
        $null -ne $snapshotVmName -and
        -not (Test-StringEquals -Left ([string]$snapshotVmName) -Right $VMName)
    ) {
        throw "Checkpoint recovery candidate does not belong to VM '$VMName'."
    }
}

function Assert-Version2CheckpointMarkerIntentMatches {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Marker,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedStatus,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName,
        [Parameter(Mandatory = $true)]
        [object]$VmObject
    )

    $markerSchema = [string](Get-PropertyValueOrNull -Object $Marker -Name "schema")
    $markerStatus = [string](Get-PropertyValueOrNull -Object $Marker -Name "status")
    $markerResourceType = [string](Get-PropertyValueOrNull -Object $Marker -Name "resourceType")
    $markerResourceName = [string](Get-PropertyValueOrNull -Object $Marker -Name "resourceName")
    $markerCheckpointName = [string](Get-PropertyValueOrNull -Object $Marker -Name "checkpointName")
    $markerOwnerId = [string](Get-PropertyValueOrNull -Object $Marker -Name "ownerId")
    $markerVmName = [string](Get-PropertyValueOrNull -Object $Marker -Name "vmName")
    $markerVhdPath = [string](Get-PropertyValueOrNull -Object $Marker -Name "vhdPath")
    if ($markerSchema -cne $script:CheckpointMarkerSchema) {
        throw "Checkpoint marker schema is not recognized as a transactional marker."
    }
    if ($markerStatus -cne $ExpectedStatus) {
        throw "Checkpoint marker status '$markerStatus' is not '$ExpectedStatus'."
    }
    if ($markerResourceType -cne "checkpoint") {
        throw "Checkpoint marker resource type is not 'checkpoint'."
    }
    if (
        $markerResourceName -cne $OwnedCheckpointName -or
        $markerCheckpointName -cne $OwnedCheckpointName
    ) {
        throw "Checkpoint marker does not bind the exact fixed name '$OwnedCheckpointName'."
    }
    if (
        -not (Test-StringEquals -Left $markerOwnerId -Right $ExpectedOwnerId) -or
        -not (Test-StringEquals -Left $markerVmName -Right $VMName)
    ) {
        throw "Checkpoint marker does not match this owner or VM."
    }
    if ((Normalize-ComparisonPath $markerVhdPath) -ne (Normalize-ComparisonPath $ExpectedVhdPath)) {
        throw "Checkpoint marker VHD path does not match the exact owned VHD."
    }

    $expectedVmId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $VmObject -Name "Id") `
        -Label "Owned VM id"
    $markerVmId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "vmId") `
        -Label "Checkpoint marker VM id"
    if ($markerVmId -cne $expectedVmId) {
        throw "Checkpoint marker VM id does not match the exact owned VM."
    }
    Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "operationNonce") `
        -Label "Checkpoint operation nonce" | Out-Null
    ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "creationStartedUtc") `
        -Label "Checkpoint creation-start time" | Out-Null
}

function Assert-FinalizedCheckpointMarkerMatches {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Marker,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName,
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [Parameter(Mandatory = $true)]
        [object]$SnapshotObject
    )

    Assert-CheckpointSnapshotBelongsToVm `
        -SnapshotObject $SnapshotObject `
        -VmObject $VmObject `
        -OwnedCheckpointName $OwnedCheckpointName

    if (
        [string](Get-PropertyValueOrNull -Object $Marker -Name "schema") -ceq
            $script:MarkerSchema
    ) {
        if ($Marker.PSObject.Properties["status"] -and [string]$Marker.status -cne "complete") {
            throw "Legacy checkpoint marker is not finalized."
        }
        foreach ($requiredProperty in @("vmId", "snapshotId", "snapshotCreationTimeUtc")) {
            if (-not $Marker.PSObject.Properties[$requiredProperty]) {
                throw "Legacy checkpoint marker is missing required final identity '$requiredProperty'."
            }
        }
        Assert-OwnerMarkerMatches `
            -Marker $Marker `
            -ExpectedResourceType "checkpoint" `
            -ExpectedResourceName $OwnedCheckpointName `
            -ExpectedOwnerId $ExpectedOwnerId `
            -ExpectedVmName $VMName `
            -ExpectedVhdPath $ExpectedVhdPath `
            -VmObject $VmObject `
            -SnapshotObject $SnapshotObject
        return
    }

    Assert-Version2CheckpointMarkerIntentMatches `
        -Marker $Marker `
        -ExpectedStatus "complete" `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVhdPath $ExpectedVhdPath `
        -OwnedCheckpointName $OwnedCheckpointName `
        -VmObject $VmObject

    $expectedSnapshotId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $SnapshotObject -Name "Id") `
        -Label "Observed checkpoint id"
    $markerSnapshotId = Get-RequiredGuidString `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "snapshotId") `
        -Label "Checkpoint marker snapshot id"
    if ($markerSnapshotId -cne $expectedSnapshotId) {
        throw "Checkpoint marker snapshot id does not match the current checkpoint."
    }

    $expectedCreationTime = ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $SnapshotObject -Name "CreationTime") `
        -Label "Observed checkpoint creation time"
    $markerCreationTime = ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "snapshotCreationTimeUtc") `
        -Label "Checkpoint marker snapshot creation time"
    if ($markerCreationTime.Ticks -ne $expectedCreationTime.Ticks) {
        throw "Checkpoint marker creation time does not match the current checkpoint."
    }
    $creationStartedUtc = ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "creationStartedUtc") `
        -Label "Checkpoint creation-start time"
    if ($expectedCreationTime -lt $creationStartedUtc) {
        throw "Checkpoint creation time predates its owned pending intent."
    }
    if ($expectedCreationTime -gt $creationStartedUtc.AddSeconds($script:CheckpointCreationWindowSec)) {
        throw "Checkpoint creation time is outside its owned pending operation window."
    }
}

function Resolve-CanonicalExistingVhdPath {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [int]$Level,
        [Parameter(Mandatory = $true)]
        [string]$Role
    )

    $levelDescription = if ($Level -ge 0) { " at level $Level" } else { "" }
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Role path$levelDescription is empty."
    }

    if (-not [IO.Path]::IsPathRooted($Path)) {
        throw "$Role path$levelDescription is not an absolute path: '$Path'."
    }

    try {
        $fullPath = [IO.Path]::GetFullPath($Path)
    } catch {
        throw "$Role path$levelDescription cannot be canonicalized: '$Path'."
    }

    try {
        $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
    } catch {
        throw "$Role path$levelDescription could not be checked for existence: '$fullPath'."
    }
    if (-not $exists) {
        throw "$Role path$levelDescription does not exist as a file: '$fullPath'."
    }

    try {
        $resolvedPaths = @(Resolve-Path -LiteralPath $fullPath -ErrorAction Stop)
    } catch {
        throw "$Role path$levelDescription could not be resolved: '$fullPath'."
    }
    if ($resolvedPaths.Count -ne 1) {
        throw "$Role path$levelDescription resolved ambiguously: '$fullPath'."
    }

    $providerPath = Get-PropertyValueOrNull -Object $resolvedPaths[0] -Name "ProviderPath"
    if ([string]::IsNullOrWhiteSpace([string]$providerPath)) {
        $providerPath = Get-PropertyValueOrNull -Object $resolvedPaths[0] -Name "Path"
    }
    if ([string]::IsNullOrWhiteSpace([string]$providerPath)) {
        throw "$Role path$levelDescription did not resolve to a canonical file-system path: '$fullPath'."
    }

    try {
        $canonicalPath = [IO.Path]::GetFullPath([string]$providerPath)
    } catch {
        throw "$Role path$levelDescription returned an invalid canonical path: '$fullPath'."
    }
    if (-not [IO.Path]::IsPathRooted($canonicalPath)) {
        throw "$Role path$levelDescription returned a non-absolute canonical path: '$canonicalPath'."
    }

    return $canonicalPath
}

function Assert-VhdChainReachesOwnedBase {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$AttachedVhdPath,
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$OwnedBaseVhdPath,
        [ValidateRange(1, 128)]
        [int]$MaxDepth = $script:VhdChainMaxDepth
    )

    $canonicalAttachedLeafPath = Resolve-CanonicalExistingVhdPath `
        -Path $AttachedVhdPath `
        -Level 0 `
        -Role "Attached VHD leaf"
    $canonicalOwnedBasePath = Resolve-CanonicalExistingVhdPath `
        -Path $OwnedBaseVhdPath `
        -Level -1 `
        -Role "Owner-marked base VHD"
    $currentPath = $canonicalAttachedLeafPath
    $visited = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )

    for ($level = 0; $level -lt $MaxDepth; $level++) {
        if (-not $visited.Add($currentPath)) {
            throw "VHD chain cycle detected at level $($level): '$currentPath'."
        }

        try {
            $vhdRecords = @(Get-VHD -Path $currentPath -ErrorAction Stop)
        } catch {
            throw "Get-VHD failed for VHD chain path at level $($level): '$currentPath'."
        }
        if ($vhdRecords.Count -ne 1 -or $null -eq $vhdRecords[0]) {
            throw "Get-VHD returned ambiguous data for VHD chain path at level $($level): '$currentPath'."
        }
        $vhdRecord = $vhdRecords[0]

        $reportedPathProperty = $vhdRecord.PSObject.Properties["Path"]
        if (
            $null -eq $reportedPathProperty -or
            $null -eq $reportedPathProperty.Value -or
            -not ($reportedPathProperty.Value -is [string])
        ) {
            throw "Get-VHD did not report one string Path for VHD chain level $($level): '$currentPath'."
        }
        $reportedPath = Resolve-CanonicalExistingVhdPath `
            -Path ([string]$reportedPathProperty.Value) `
            -Level $level `
            -Role "Get-VHD reported VHD"
        if (-not [string]::Equals(
            $reportedPath,
            $currentPath,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw "Get-VHD reported a different path for VHD chain level $level. Queried '$currentPath'; reported '$reportedPath'."
        }

        $parentPathProperty = $vhdRecord.PSObject.Properties["ParentPath"]
        if ($null -eq $parentPathProperty) {
            throw "Get-VHD did not report ParentPath data for VHD chain level $($level): '$currentPath'."
        }
        if (
            $null -ne $parentPathProperty.Value -and
            -not ($parentPathProperty.Value -is [string])
        ) {
            throw "Get-VHD reported ambiguous ParentPath data for VHD chain level $($level): '$currentPath'."
        }

        $parentPath = [string]$parentPathProperty.Value
        $isOwnedBase = [string]::Equals(
            $currentPath,
            $canonicalOwnedBasePath,
            [StringComparison]::OrdinalIgnoreCase
        )
        if ([string]::IsNullOrWhiteSpace($parentPath)) {
            if (-not $isOwnedBase) {
                throw "VHD chain terminated at unrelated base at level $($level): '$currentPath'. Expected '$canonicalOwnedBasePath'."
            }

            return [pscustomobject]@{
                LeafPath = $canonicalAttachedLeafPath
                BasePath = $canonicalOwnedBasePath
                Depth = $level
                ChainLength = $level + 1
            }
        }

        if ($isOwnedBase) {
            throw "Owner-marked base VHD is not terminal at level $($level): '$currentPath'."
        }

        $canonicalParentPath = Resolve-CanonicalExistingVhdPath `
            -Path $parentPath `
            -Level ($level + 1) `
            -Role "VHD parent"
        if ($visited.Contains($canonicalParentPath)) {
            throw "VHD chain cycle detected at level $($level + 1): '$canonicalParentPath'."
        }
        if (($level + 1) -ge $MaxDepth) {
            throw "VHD chain exceeded the maximum depth of $MaxDepth after level $($level): '$canonicalParentPath'."
        }

        $currentPath = $canonicalParentPath
    }

    throw "VHD chain exceeded the maximum depth of $MaxDepth."
}

function Assert-OwnedVmDiskBinding {
    param(
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath
    )

    $ownedVmName = [string](Get-PropertyValueOrNull -Object $VmObject -Name "Name")
    if (-not (Test-StringEquals -Left $ownedVmName -Right $VMName)) {
        throw "Hard disk inspection was not given the exact VM object for '$VMName'."
    }

    try {
        $drives = @(Get-VMHardDiskDrive -VM $VmObject -ErrorAction Stop)
    } catch {
        throw "Active hard disks could not be read from exact VM '$VMName'."
    }
    if ($drives.Count -ne 1) {
        throw "VM '$VMName' must have exactly one active hard disk for this controller, but found $($drives.Count). Refusing to infer an OS disk or accept additional data disks."
    }

    $drivePathProperty = $drives[0].PSObject.Properties["Path"]
    if (
        $null -eq $drivePathProperty -or
        $null -eq $drivePathProperty.Value -or
        -not ($drivePathProperty.Value -is [string])
    ) {
        throw "The active hard disk on exact VM '$VMName' does not have one unambiguous string path."
    }

    return Assert-VhdChainReachesOwnedBase `
        -AttachedVhdPath ([string]$drivePathProperty.Value) `
        -OwnedBaseVhdPath $ResolvedVhdPath
}

function Assert-OwnedVM {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId
    )

    $vmMatches = @(Get-VM -Name $VMName -ErrorAction SilentlyContinue)
    if ($vmMatches.Count -eq 0) {
        throw "VM '$VMName' was not found."
    }
    if ($vmMatches.Count -ne 1) {
        throw "VM lookup for '$VMName' returned ambiguous results."
    }
    $vm = $vmMatches[0]
    if (-not (Test-StringEquals `
        -Left ([string](Get-PropertyValueOrNull -Object $vm -Name "Name")) `
        -Right $VMName
    )) {
        throw "VM lookup did not return the exact named VM '$VMName'."
    }

    $noteMarker = Get-VMNoteMarker -VmObject $vm
    if ($null -eq $noteMarker) {
        throw "VM '$VMName' is unowned. Refusing to modify it."
    }

    Assert-OwnerMarkerMatches `
        -Marker $noteMarker `
        -ExpectedResourceType "vm" `
        -ExpectedResourceName $VMName `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVmName $VMName `
        -ExpectedVhdPath $ResolvedVhdPath `
        -VmObject $vm

    $vmMarkerPath = Get-VMMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName
    $fileMarker = Read-MarkerFile -Path $vmMarkerPath
    if ($null -eq $fileMarker) {
        throw "VM '$VMName' is missing its ownership marker file: $vmMarkerPath"
    }

    Assert-OwnerMarkerMatches `
        -Marker $fileMarker `
        -ExpectedResourceType "vm" `
        -ExpectedResourceName $VMName `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVmName $VMName `
        -ExpectedVhdPath $ResolvedVhdPath `
        -VmObject $vm

    Assert-OwnedVmDiskBinding `
        -VmObject $vm `
        -ResolvedVhdPath $ResolvedVhdPath | Out-Null

    return $vm
}

function Get-SingleCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    $snapshots = @(
        Get-VMSnapshot -VMName $VMName -ErrorAction Stop |
            Where-Object {
                [string]$_.Name -ceq $OwnedCheckpointName
            }
    )
    if ($snapshots.Count -eq 0) {
        return $null
    }

    if ($snapshots.Count -ne 1) {
        throw "VM '$VMName' has multiple checkpoints named '$OwnedCheckpointName'."
    }

    return $snapshots[0]
}

function Wait-ForOwnedCheckpointObservation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName,
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [ValidateRange(1, 300)]
        [int]$TimeoutSec = $script:CheckpointObservationTimeoutSec,
        [ValidateRange(10, 5000)]
        [int]$PollIntervalMilliseconds = $script:CheckpointObservationPollIntervalMilliseconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    do {
        $matches = @(
            Get-VMSnapshot -VMName $VMName -ErrorAction Stop |
                Where-Object {
                    [string]$_.Name -ceq $OwnedCheckpointName
                }
        )
        if ($matches.Count -gt 1) {
            throw "VM '$VMName' has multiple checkpoints named '$OwnedCheckpointName'."
        }
        if ($matches.Count -eq 1) {
            Assert-CheckpointSnapshotBelongsToVm `
                -SnapshotObject $matches[0] `
                -VmObject $VmObject `
                -OwnedCheckpointName $OwnedCheckpointName
            return $matches[0]
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            break
        }
        Start-Sleep -Milliseconds $PollIntervalMilliseconds
    } while ($true)

    throw "Timed out after $TimeoutSec seconds waiting for exact checkpoint '$OwnedCheckpointName' to appear for VM '$VMName'."
}

function Assert-OwnedCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    $snapshot = Get-SingleCheckpoint -OwnedCheckpointName $OwnedCheckpointName
    if ($null -eq $snapshot) {
        throw "Owned checkpoint '$OwnedCheckpointName' was not found for VM '$VMName'."
    }

    $checkpointMarkerPath = Get-CheckpointMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName -OwnedCheckpointName $OwnedCheckpointName
    $marker = Read-MarkerFile -Path $checkpointMarkerPath
    if ($null -eq $marker) {
        throw "Checkpoint '$OwnedCheckpointName' is missing its ownership marker file: $checkpointMarkerPath"
    }

    $vm = Get-VM -Name $VMName -ErrorAction Stop
    Assert-FinalizedCheckpointMarkerMatches `
        -Marker $marker `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVhdPath $ResolvedVhdPath `
        -OwnedCheckpointName $OwnedCheckpointName `
        -VmObject $vm `
        -SnapshotObject $snapshot

    return $snapshot
}

function Remove-OwnedCheckpointIfPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    $snapshot = Get-SingleCheckpoint -OwnedCheckpointName $OwnedCheckpointName
    if ($null -eq $snapshot) {
        $checkpointMarkerPath = Get-CheckpointMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName -OwnedCheckpointName $OwnedCheckpointName
        if (Test-Path -LiteralPath $checkpointMarkerPath -PathType Leaf) {
            throw "Checkpoint marker exists while '$OwnedCheckpointName' is not observable. Refusing to remove, overwrite, or treat pending state as finalized: $checkpointMarkerPath"
        }
        return
    }

    Assert-ConfirmationForOwnedAction -Action "Removing checkpoint '$OwnedCheckpointName'"
    Assert-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $OwnedCheckpointName | Out-Null

    Write-Step "Removing owned checkpoint $OwnedCheckpointName"
    Remove-VMSnapshot -VMName $VMName -Name $OwnedCheckpointName -Confirm:$false

    $checkpointMarkerPath = Get-CheckpointMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName -OwnedCheckpointName $OwnedCheckpointName
    Remove-Item -LiteralPath $checkpointMarkerPath -Force -ErrorAction SilentlyContinue
}

function New-OwnedCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    Assert-ConfirmationForOwnedAction -Action "Creating checkpoint '$OwnedCheckpointName'"
    $vm = Assert-OwnedVM -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId
    Remove-OwnedCheckpointIfPresent -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $OwnedCheckpointName

    $markerPath = Get-CheckpointMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName -OwnedCheckpointName $OwnedCheckpointName
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        throw "Checkpoint marker still exists after the owned replacement check. Refusing to overwrite pending or mismatched state: $markerPath"
    }
    if ($null -ne (Get-SingleCheckpoint -OwnedCheckpointName $OwnedCheckpointName)) {
        throw "Checkpoint '$OwnedCheckpointName' is still present without a finalized marker. Refusing to create a duplicate."
    }

    $pendingMarker = New-PendingCheckpointMarker `
        -ResolvedVhdPath $ResolvedVhdPath `
        -ExpectedOwnerId $ExpectedOwnerId `
        -OwnedCheckpointName $OwnedCheckpointName `
        -VmObject $vm
    Write-MarkerFile -Path $markerPath -Marker $pendingMarker

    Write-Step "Creating checkpoint $OwnedCheckpointName"
    try {
        Checkpoint-VM -VMName $VMName -SnapshotName $OwnedCheckpointName -Confirm:$false | Out-Null
        $snapshot = Wait-ForOwnedCheckpointObservation `
            -OwnedCheckpointName $OwnedCheckpointName `
            -VmObject $vm
        $completedMarker = New-CompletedCheckpointMarker `
            -PendingMarker $pendingMarker `
            -SnapshotObject $snapshot
        Assert-FinalizedCheckpointMarkerMatches `
            -Marker $completedMarker `
            -ExpectedOwnerId $ExpectedOwnerId `
            -ExpectedVhdPath $ResolvedVhdPath `
            -OwnedCheckpointName $OwnedCheckpointName `
            -VmObject $vm `
            -SnapshotObject $snapshot
        Write-MarkerFile -Path $markerPath -Marker $completedMarker
        return
    } catch {
        throw "Checkpoint '$OwnedCheckpointName' did not reach a finalized owned state. The pending intent remains at '$markerPath' for explicit recovery. $($_.Exception.Message)"
    }
}

function Get-UnattendedCompletionProof {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [Parameter(Mandatory = $true)]
        [string]$StatePath
    )

    if (
        [string](Get-PropertyValueOrNull -Object $State -Name "status") -cne
            "complete"
    ) {
        throw "Checkpoint recovery requires unattended state status 'complete'."
    }
    if (-not $State.PSObject.Properties["updatedUtc"]) {
        throw "Completed unattended state is missing its update timestamp."
    }

    $updatedUtc = ConvertTo-CheckpointUtc `
        -Value $State.updatedUtc `
        -Label "Unattended state update time"
    if (
        $State.PSObject.Properties["completedUtc"] -and
        -not [string]::IsNullOrWhiteSpace([string]$State.completedUtc)
    ) {
        $completedUtc = ConvertTo-CheckpointUtc `
            -Value $State.completedUtc `
            -Label "Unattended completion time"
        if ($updatedUtc -lt $completedUtc.AddSeconds(-$script:CheckpointRecoveryClockSkewSec)) {
            throw "Unattended update time predates completion beyond the allowed consistency bound."
        }
        return [pscustomobject]@{
            CompletionUtc = $completedUtc
            Source = "completedUtc"
            LegacyFallback = $false
        }
    }

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "Legacy unattended completion proof requires the exact owned state file."
    }
    $stateFileUtc = (Get-Item -LiteralPath $StatePath -ErrorAction Stop).LastWriteTimeUtc
    if ([Math]::Abs(($stateFileUtc - $updatedUtc).TotalSeconds) -gt $script:CheckpointRecoveryClockSkewSec) {
        throw "Legacy unattended marker and file timestamps are outside the allowed consistency bound."
    }
    $completionUtc = if ($stateFileUtc -gt $updatedUtc) { $stateFileUtc } else { $updatedUtc }
    return [pscustomobject]@{
        CompletionUtc = $completionUtc
        Source = "legacy-updatedUtc-and-file-time"
        LegacyFallback = $true
    }
}

function Get-RecoverableCheckpointCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Snapshots,
        [AllowNull()]
        [object]$Marker,
        [Parameter(Mandatory = $true)]
        [object]$VmObject,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName,
        [Parameter(Mandatory = $true)]
        [DateTime]$UnattendedCompletionUtc,
        [DateTime]$NowUtc = [DateTime]::UtcNow
    )

    $allSnapshots = @($Snapshots | Where-Object { $null -ne $_ })
    $matchingSnapshots = @(
        $allSnapshots |
            Where-Object {
                [string]$_.Name -ceq $OwnedCheckpointName
            }
    )
    if ($matchingSnapshots.Count -gt 1) {
        throw "Checkpoint recovery found duplicate checkpoints named '$OwnedCheckpointName'."
    }
    $wrongNameSnapshots = @(
        $allSnapshots |
            Where-Object {
                [string]$_.Name -cne $OwnedCheckpointName
            }
    )
    if ($wrongNameSnapshots.Count -gt 0) {
        throw "Checkpoint recovery found another checkpoint name and refuses ambiguous adoption."
    }
    if ($matchingSnapshots.Count -ne 1) {
        throw "Checkpoint recovery requires exactly one checkpoint named '$OwnedCheckpointName'."
    }

    $candidate = $matchingSnapshots[0]
    Assert-CheckpointSnapshotBelongsToVm `
        -SnapshotObject $candidate `
        -VmObject $VmObject `
        -OwnedCheckpointName $OwnedCheckpointName
    $snapshotCreationUtc = ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $candidate -Name "CreationTime") `
        -Label "Checkpoint recovery candidate creation time"
    $completionUtc = $UnattendedCompletionUtc.ToUniversalTime()
    $currentUtc = $NowUtc.ToUniversalTime()
    if ($completionUtc -gt $currentUtc.AddSeconds($script:CheckpointRecoveryClockSkewSec)) {
        throw "Unattended completion time is in the future beyond the allowed clock bound."
    }
    if ($snapshotCreationUtc -lt $completionUtc) {
        throw "Checkpoint recovery candidate predates completed unattended installation."
    }
    if ($snapshotCreationUtc -gt $currentUtc.AddSeconds($script:CheckpointRecoveryClockSkewSec)) {
        throw "Checkpoint recovery candidate creation time is in the future beyond the allowed clock bound."
    }

    if ($null -eq $Marker) {
        if ($snapshotCreationUtc -gt $completionUtc.AddSeconds($script:LegacyCheckpointRecoveryWindowSec)) {
            throw "Legacy markerless checkpoint is outside the conservative recovery window."
        }
        return $candidate
    }

    Assert-Version2CheckpointMarkerIntentMatches `
        -Marker $Marker `
        -ExpectedStatus "pending" `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVhdPath $ResolvedVhdPath `
        -OwnedCheckpointName $OwnedCheckpointName `
        -VmObject $VmObject
    $creationStartedUtc = ConvertTo-CheckpointUtc `
        -Value (Get-PropertyValueOrNull -Object $Marker -Name "creationStartedUtc") `
        -Label "Checkpoint creation-start time"
    if ($creationStartedUtc -lt $completionUtc) {
        throw "Pending checkpoint intent predates completed unattended installation."
    }
    if ($snapshotCreationUtc -lt $creationStartedUtc) {
        throw "Checkpoint recovery candidate predates the pending checkpoint intent."
    }
    if ($snapshotCreationUtc -gt $creationStartedUtc.AddSeconds($script:CheckpointCreationWindowSec)) {
        throw "Checkpoint recovery candidate is outside the pending operation window."
    }

    return $candidate
}

function Recover-PendingOwnedCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [object]$VmObject
    )

    Assert-ConfirmationForOwnedAction -Action "Recovering checkpoint '$($script:CleanCheckpointName)'"
    $paths = Get-UnattendPaths -ResolvedVhdPath $ResolvedVhdPath
    $state = Read-OwnedUnattendState -ResolvedVhdPath $ResolvedVhdPath -Paths $paths
    Assert-UnattendStateMatchesVmMarker -State $state -VmObject $VmObject
    $completionProof = Get-UnattendedCompletionProof -State $state -StatePath $paths.StatePath

    $finalCredential = Import-CleanWindowsCredential `
        -CredentialPath $paths.FinalCredentialPath `
        -MetadataPath $paths.FinalCredentialMetadataPath `
        -OwnedRoot $paths.OwnedRoot `
        -VMName $VMName `
        -OwnerId $ExpectedOwnerId `
        -ExpectedKind "final"
    if (
        -not [string]::Equals(
            $finalCredential.UserName,
            [string]$state.guestAdministratorName,
            [StringComparison]::Ordinal
        )
    ) {
        throw "Final credential username does not match completed unattended state."
    }

    $checkpointName = $script:CleanCheckpointName
    $markerPath = Get-CheckpointMarkerPath `
        -ResolvedVhdPath $ResolvedVhdPath `
        -OwnedVmName $VMName `
        -OwnedCheckpointName $checkpointName
    $marker = Read-MarkerFile -Path $markerPath
    $markerSchema = if ($null -eq $marker) {
        ""
    } else {
        [string](Get-PropertyValueOrNull -Object $marker -Name "schema")
    }
    $markerStatus = if (
        $null -ne $marker -and
        $marker.PSObject.Properties["status"]
    ) {
        [string]$marker.status
    } else {
        ""
    }
    if (
        $markerSchema -ceq $script:MarkerSchema -or
        (
            $markerSchema -ceq $script:CheckpointMarkerSchema -and
            $markerStatus -ceq "complete"
        )
    ) {
        $finalizedSnapshot = Assert-OwnedCheckpoint `
            -ResolvedVhdPath $ResolvedVhdPath `
            -ExpectedOwnerId $ExpectedOwnerId `
            -OwnedCheckpointName $checkpointName
        Write-InfoLine "Checkpoint '$checkpointName' already has a finalized ownership marker. Recovery is idempotently satisfied."
        return $finalizedSnapshot
    }

    $snapshots = @(Get-VMSnapshot -VMName $VMName -ErrorAction Stop)
    $candidate = Get-RecoverableCheckpointCandidate `
        -Snapshots $snapshots `
        -Marker $marker `
        -VmObject $VmObject `
        -ResolvedVhdPath $ResolvedVhdPath `
        -ExpectedOwnerId $ExpectedOwnerId `
        -OwnedCheckpointName $checkpointName `
        -UnattendedCompletionUtc $completionProof.CompletionUtc

    if ($null -eq $marker) {
        $legacySnapshotCreationUtc = ConvertTo-CheckpointUtc `
            -Value (Get-PropertyValueOrNull -Object $candidate -Name "CreationTime") `
            -Label "Legacy markerless checkpoint creation time"
        $marker = New-PendingCheckpointMarker `
            -ResolvedVhdPath $ResolvedVhdPath `
            -ExpectedOwnerId $ExpectedOwnerId `
            -OwnedCheckpointName $checkpointName `
            -VmObject $VmObject `
            -CreationStartedUtc $legacySnapshotCreationUtc
        $marker | Add-Member `
            -NotePropertyName "recoveryKind" `
            -NotePropertyValue "legacy-markerless-completed-unattended"
        $marker | Add-Member `
            -NotePropertyName "recoveryCompletionLowerBoundUtc" `
            -NotePropertyValue $completionProof.CompletionUtc.ToString("o")
        $marker | Add-Member `
            -NotePropertyName "recoveryCompletionSource" `
            -NotePropertyValue ([string]$completionProof.Source)
    }

    $completedMarker = New-CompletedCheckpointMarker `
        -PendingMarker $marker `
        -SnapshotObject $candidate
    Assert-FinalizedCheckpointMarkerMatches `
        -Marker $completedMarker `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVhdPath $ResolvedVhdPath `
        -OwnedCheckpointName $checkpointName `
        -VmObject $VmObject `
        -SnapshotObject $candidate
    Write-MarkerFile -Path $markerPath -Marker $completedMarker
    Write-InfoLine "Recovered exact checkpoint '$checkpointName' without deleting or recreating it."
    return $candidate
}

function Wait-ForVmState {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$DesiredStates,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSec
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $vm = Get-VM -Name $VMName -ErrorAction Stop
        $state = [string]$vm.State
        if ($DesiredStates -contains $state) {
            return $state
        }

        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "VM '$VMName' did not reach one of these states within $TimeoutSec seconds: $($DesiredStates -join ', ')."
}

function Ensure-VMRunning {
    $vm = Get-VM -Name $VMName -ErrorAction Stop
    if ([string]$vm.State -eq "Running") {
        return
    }

    Assert-ConfirmationForOwnedAction -Action "Starting VM '$VMName'"
    Start-VM -Name $VMName -Confirm:$false | Out-Null
    Wait-ForVmState -DesiredStates @("Running") -TimeoutSec $PowerShellDirectTimeoutSec | Out-Null
}

function Stop-VMGracefully {
    param(
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [int]$TimeoutSec
    )

    $vm = Get-VM -Name $VMName -ErrorAction Stop
    if ([string]$vm.State -eq "Off") {
        return
    }

    Assert-ConfirmationForOwnedAction -Action "Stopping VM '$VMName'"

    if ($null -ne $Session) {
        try {
            Invoke-Command -Session $Session -ScriptBlock {
                Start-Process -FilePath shutdown.exe -ArgumentList "/s", "/t", "0", "/f" -WindowStyle Hidden
            } -ErrorAction Stop | Out-Null
        } catch {
            Write-InfoLine "Guest shutdown request did not complete cleanly. Waiting for the VM to turn off."
        }
    }

    try {
        Wait-ForVmState -DesiredStates @("Off") -TimeoutSec $TimeoutSec | Out-Null
    } catch {
        Stop-VM -Name $VMName -TurnOff -Force -Confirm:$false | Out-Null
        Wait-ForVmState -DesiredStates @("Off") -TimeoutSec 120 | Out-Null
    }
}

function Restore-OwnedCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    Assert-ConfirmationForOwnedAction -Action "Restoring checkpoint '$OwnedCheckpointName'"
    Assert-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $OwnedCheckpointName | Out-Null
    Stop-VMGracefully -Session $null -TimeoutSec $GuestShutdownTimeoutSec

    Write-Step "Restoring owned checkpoint $OwnedCheckpointName"
    Restore-VMSnapshot -VMName $VMName -Name $OwnedCheckpointName -Confirm:$false
}

function Open-GuestSession {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSec
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastError = $null

    do {
        try {
            return (New-PSSession -VMName $VMName -Credential $GuestCredential -ErrorAction Stop)
        } catch {
            $lastError = $_
            Start-Sleep -Seconds 5
        }
    } while ((Get-Date) -lt $deadline)

    if ($null -ne $lastError) {
        throw "PowerShell Direct to VM '$VMName' did not become ready within $TimeoutSec seconds. Last error: $($lastError.Exception.Message)"
    }

    throw "PowerShell Direct to VM '$VMName' did not become ready within $TimeoutSec seconds."
}

function Invoke-UnattendedOpticalBootKey {
    param(
        [Parameter(Mandatory = $true)]
        [object]$VmObject
    )

    $vmId = ([Guid]$VmObject.Id).ToString("D")
    $deadline = [DateTime]::UtcNow.AddSeconds($script:OpticalBootKeyWindowSec)
    $computerSystem = $null
    $keyboard = $null
    $pulseCount = 0
    $successfulDeliveries = 0
    $lastCimError = $null
    $lastDiagnostic = "The Hyper-V synthetic keyboard was not available."
    Start-Sleep -Milliseconds $script:OpticalBootInitialDelayMilliseconds
    do {
        $pulseCount++
        try {
            if ($null -eq $computerSystem) {
                $computerSystem = @(
                    Get-CimInstance `
                        -Namespace "root\virtualization\v2" `
                        -ClassName "Msvm_ComputerSystem" `
                        -Filter ("Name = '{0}'" -f $vmId) `
                        -OperationTimeoutSec 1 `
                        -ErrorAction Stop
                ) | Select-Object -First 1
            }
            if ($null -eq $computerSystem) {
                $lastDiagnostic = "The VM CIM computer system was not available."
            } else {
                if ($null -eq $keyboard) {
                    $keyboard = @(
                        Get-CimAssociatedInstance `
                            -InputObject $computerSystem `
                            -Association "Msvm_SystemDevice" `
                            -ResultClassName "Msvm_Keyboard" `
                            -OperationTimeoutSec 1 `
                            -ErrorAction Stop
                    ) | Select-Object -First 1
                }
                if ($null -ne $keyboard) {
                    $typeKeyResult = Invoke-CimMethod `
                        -InputObject $keyboard `
                        -MethodName "TypeKey" `
                        -Arguments @{ keyCode = [uint32]0x20 } `
                        -OperationTimeoutSec 1 `
                        -ErrorAction Stop
                    $returnValue = [uint32]$typeKeyResult.ReturnValue
                    if ($returnValue -eq 0) {
                        $successfulDeliveries++
                    } else {
                        $lastDiagnostic = "TypeKey returned Hyper-V status code '$returnValue'."
                    }
                } else {
                    $lastDiagnostic = "The Hyper-V synthetic keyboard was not available."
                }
            }
        } catch {
            $lastCimError = $_
            $errorType = $_.Exception.GetType().Name
            $errorCategory = [string]$_.CategoryInfo.Category
            $errorId = [string]$_.FullyQualifiedErrorId
            $lastDiagnostic = "CIM error type '$errorType', category '$errorCategory', id '$errorId'."
            $computerSystem = $null
            $keyboard = $null
        }

        if ($pulseCount -lt $script:OpticalBootMaxPulseCount) {
            $remainingMilliseconds = [int][Math]::Floor(($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if ($remainingMilliseconds -lt $script:OpticalBootPulseIntervalMilliseconds) {
                break
            }
            Start-Sleep -Milliseconds $script:OpticalBootPulseIntervalMilliseconds
        }
    } while (
        $pulseCount -lt $script:OpticalBootMaxPulseCount -and
        [DateTime]::UtcNow -lt $deadline
    )

    if ($successfulDeliveries -eq 0) {
        $safeLastError = if ($null -eq $lastCimError) {
            "No CIM exception was recorded."
        } else {
            "type '{0}', category '{1}', id '{2}'." -f
                $lastCimError.Exception.GetType().Name,
                [string]$lastCimError.CategoryInfo.Category,
                [string]$lastCimError.FullyQualifiedErrorId
        }
        throw (
            "Hyper-V CIM did not accept any optical boot space-key pulse within the fixed boot window. " +
            "Safe diagnostic: $lastDiagnostic Last CIM error: $safeLastError"
        )
    }

    Write-InfoLine (
        "Delivered {0} of {1} optical boot space-key pulses through Microsoft Hyper-V CIM within the fixed boot window." -f
        $successfulDeliveries,
        $pulseCount
    )
}

function Detach-OwnedInstallationMedia {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [switch]$RequireBoth,
        [switch]$AllowAlreadyDetachedAfterPowerShellDirectReady,
        [ValidateRange(1, 30)]
        [int]$TimeoutSec = $script:InstallationMediaDetachTimeoutSec,
        [ValidateRange(10, 1000)]
        [int]$PollIntervalMilliseconds = $script:InstallationMediaDetachPollIntervalMilliseconds
    )

    if ($RequireBoth -and $AllowAlreadyDetachedAfterPowerShellDirectReady) {
        throw "RequireBoth and AllowAlreadyDetachedAfterPowerShellDirectReady are mutually exclusive."
    }
    if (
        $AllowAlreadyDetachedAfterPowerShellDirectReady -and
        [string]$State.status -cne "powershell-direct-ready"
    ) {
        throw "Already-detached recovery requires unattended status 'powershell-direct-ready'."
    }

    $expectedMedia = @(
        [pscustomobject]@{
            Label = "Windows ISO"
            Path = Normalize-ComparisonPath ([string]$State.windowsIsoPath)
        },
        [pscustomobject]@{
            Label = "answer ISO"
            Path = Normalize-ComparisonPath ([string]$State.answerIsoPath)
        }
    )
    $initialDrives = @(Get-VMDvdDrive -VMName $VMName -ErrorAction Stop)
    $initialMatches = @(
        foreach ($drive in $initialDrives) {
            $drivePath = Normalize-ComparisonPath ([string]$drive.Path)
            if ($expectedMedia.Path -contains $drivePath) {
                $drive
            }
        }
    )
    $initialAttachedPaths = @($initialMatches | ForEach-Object {
        Normalize-ComparisonPath ([string]$_.Path)
    })

    if ($RequireBoth) {
        foreach ($expected in $expectedMedia) {
            if ($initialAttachedPaths -notcontains $expected.Path) {
                throw "Expected owned $($expected.Label) was not attached. Refusing to issue a partial detach."
            }
        }
    }
    if ($AllowAlreadyDetachedAfterPowerShellDirectReady) {
        if ($initialMatches.Count -eq 0) {
            return
        }
        foreach ($expected in $expectedMedia) {
            if ($initialAttachedPaths -notcontains $expected.Path) {
                throw "Already-detached recovery found only part of the expected owned installation media. Refusing to issue a partial detach."
            }
        }
    }

    foreach ($drive in $initialMatches) {
        $drivePath = Normalize-ComparisonPath ([string]$drive.Path)
        if ($expectedMedia.Path -contains $drivePath) {
            Set-VMDvdDrive `
                -VMName $VMName `
                -ControllerNumber $drive.ControllerNumber `
                -ControllerLocation $drive.ControllerLocation `
                -Path $null | Out-Null
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    do {
        $remaining = @(
            foreach ($drive in @(Get-VMDvdDrive -VMName $VMName -ErrorAction Stop)) {
                $drivePath = Normalize-ComparisonPath ([string]$drive.Path)
                if ($expectedMedia.Path -contains $drivePath) {
                    $expected = @($expectedMedia | Where-Object { $_.Path -eq $drivePath })[0]
                    [pscustomobject]@{
                        Label = $expected.Label
                        ControllerNumber = [int]$drive.ControllerNumber
                        ControllerLocation = [int]$drive.ControllerLocation
                    }
                }
            }
        )
        if ($remaining.Count -eq 0) {
            return
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            break
        }
        Start-Sleep -Milliseconds $PollIntervalMilliseconds
    } while ($true)

    $diagnostics = @(
        $remaining |
            Sort-Object ControllerNumber, ControllerLocation, Label |
            ForEach-Object {
                "{0} remains attached at controller {1}, location {2}" -f
                    $_.Label,
                    $_.ControllerNumber,
                    $_.ControllerLocation
            }
    ) -join "; "
    throw "Timed out waiting for Hyper-V to report owned installation media detached after $TimeoutSec seconds. $diagnostics."
}

function Assert-BothOwnedInstallationMediaAttached {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State
    )

    $attachedPaths = @(
        Get-VMDvdDrive -VMName $VMName -ErrorAction Stop |
            ForEach-Object { Normalize-ComparisonPath ([string]$_.Path) }
    )
    foreach ($expected in @(
        [pscustomobject]@{
            Label = "Windows ISO"
            Path = Normalize-ComparisonPath ([string]$State.windowsIsoPath)
        },
        [pscustomobject]@{
            Label = "answer ISO"
            Path = Normalize-ComparisonPath ([string]$State.answerIsoPath)
        }
    )) {
        if ($attachedPaths -notcontains $expected.Path) {
            throw "Expected owned $($expected.Label) was not attached before the PowerShell Direct readiness transition."
        }
    }
}

function Remove-OwnedUnattendMedia {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Paths
    )

    [void](Assert-CleanWindowsPathUnderRoot -Path $Paths.UnattendRoot -OwnedRoot $Paths.OwnedRoot)
    [void](Assert-CleanWindowsPathUnderRoot -Path $Paths.AnswerIsoPath -OwnedRoot $Paths.OwnedRoot)
    [void](Assert-CleanWindowsPathUnderRoot -Path $Paths.AnswerFilePath -OwnedRoot $Paths.OwnedRoot)
    if (Test-Path -LiteralPath $Paths.UnattendRoot) {
        Remove-Item -LiteralPath $Paths.UnattendRoot -Recurse -Force
    }
}

function Remove-SetupCredentialMaterial {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Paths
    )

    foreach ($path in @($Paths.SetupCredentialPath, $Paths.SetupCredentialMetadataPath)) {
        [void](Assert-CleanWindowsPathUnderRoot -Path $path -OwnedRoot $Paths.OwnedRoot)
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

function Invoke-GuestInstallationVerification {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [string]$LocalAccountName
    )

    return Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Verifying unattended Windows installation" `
        -TimeoutSec 300 `
        -ScriptBlock {
            param($ExpectedLocalAccountName)

            $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            $currentVersion = Get-ItemProperty `
                -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" `
                -ErrorAction Stop
            $setup = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\Setup" -ErrorAction Stop
            $setupState = Get-ItemProperty `
                -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State" `
                -ErrorAction Stop

            if ([string]$operatingSystem.Caption -notlike "*Windows 11 Enterprise Evaluation") {
                throw "Installed OS caption is not Windows 11 Enterprise Evaluation."
            }
            if ([string]$currentVersion.EditionID -cne "EnterpriseEval") {
                throw "Installed Windows EditionID is not EnterpriseEval."
            }
            $buildNumber = 0
            if (
                -not [int]::TryParse(
                    [string]$operatingSystem.BuildNumber,
                    [ref]$buildNumber
                ) -or
                $buildNumber -lt 22000
            ) {
                throw "Installed Windows build is not a valid Windows 11 build."
            }
            if (
                -not [Environment]::Is64BitOperatingSystem -or
                [Environment]::GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") -cne "AMD64" -or
                [string]$operatingSystem.OSArchitecture -notmatch "64"
            ) {
                throw "Installed Windows architecture is not x64/AMD64."
            }

            foreach ($propertyName in @(
                "SystemSetupInProgress",
                "OOBEInProgress",
                "SetupPhase",
                "SetupType"
            )) {
                $property = $setup.PSObject.Properties[$propertyName]
                if ($null -eq $property -or [int]$property.Value -ne 0) {
                    throw "Windows setup property '$propertyName' is missing or not complete."
                }
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$setup.CmdLine)) {
                throw "Windows setup still has a pending setup command."
            }
            if ([string]$setupState.ImageState -cne "IMAGE_STATE_COMPLETE") {
                throw "Windows image state is not complete."
            }

            $localUser = Get-LocalUser -Name $ExpectedLocalAccountName -ErrorAction Stop
            if (-not $localUser.Enabled) {
                throw "The unattended local administrator is disabled."
            }
            $administratorsSid = New-Object Security.Principal.SecurityIdentifier("S-1-5-32-544")
            $administratorsGroup = Get-LocalGroup -SID $administratorsSid -ErrorAction Stop
            $administratorMembers = @(Get-LocalGroupMember -Group $administratorsGroup -ErrorAction Stop)
            if (
                -not @(
                    $administratorMembers |
                        Where-Object { $_.SID.Value -eq $localUser.SID.Value }
                )
            ) {
                throw "The unattended local account is not a member of Administrators."
            }

            $winlogon = Get-ItemProperty `
                -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" `
                -ErrorAction Stop
            if (
                [string]$winlogon.AutoAdminLogon -eq "1" -or
                $null -ne $winlogon.PSObject.Properties["DefaultPassword"]
            ) {
                throw "Automatic logon is enabled or a default logon password is stored."
            }

            [pscustomobject]@{
                caption = [string]$operatingSystem.Caption
                productName = [string]$currentVersion.ProductName
                editionId = [string]$currentVersion.EditionID
                displayVersion = [string]$currentVersion.DisplayVersion
                buildNumber = [string]$operatingSystem.BuildNumber
                architecture = "AMD64"
                powershellDirect = $true
                localAdministrator = $ExpectedLocalAccountName
                setupComplete = $true
                oobeComplete = $true
                autoLogon = $false
            }
        } `
        -ArgumentList @($LocalAccountName)
}

function Remove-GuestUnattendCache {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Runspaces.PSSession]$Session
    )

    Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Removing cached unattended setup material" `
        -TimeoutSec 300 `
        -ScriptBlock {
            $cachedPaths = @(
                "C:\Windows\Panther\unattend.xml",
                "C:\Windows\Panther\Unattend",
                "C:\Windows\System32\Sysprep\unattend.xml",
                "C:\Windows\System32\Sysprep\Panther\unattend.xml"
            )
            foreach ($path in $cachedPaths) {
                if (Test-Path -LiteralPath $path) {
                    Remove-Item -LiteralPath $path -Recurse -Force
                }
            }
            foreach ($path in $cachedPaths) {
                if (Test-Path -LiteralPath $path) {
                    throw "Cached unattended setup material remains in the guest."
                }
            }
        } | Out-Null
}

function Set-GuestCredential {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [string]$LocalAccountName,
        [Parameter(Mandatory = $true)]
        [Security.SecureString]$NewPassword
    )

    Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Rotating the guest administrator credential" `
        -TimeoutSec 120 `
        -ScriptBlock {
            param($ExpectedLocalAccountName, $FinalSecurePassword)
            if ($FinalSecurePassword -isnot [Security.SecureString]) {
                throw "Final credential was not transferred as a SecureString."
            }
            $localUser = Get-LocalUser -Name $ExpectedLocalAccountName -ErrorAction Stop
            Set-LocalUser -InputObject $localUser -Password $FinalSecurePassword -ErrorAction Stop
        } `
        -ArgumentList @($LocalAccountName, $NewPassword) | Out-Null
}

function Assert-OldCredentialRejected {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$OldCredential,
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$FinalCredential,
        [Parameter(Mandatory = $true)]
        [Management.Automation.Runspaces.PSSession]$VerifiedFinalSession,
        [ValidateRange(10, 120)]
        [int]$TimeoutSec = 30
    )

    if ($OldCredential.UserName -cne $FinalCredential.UserName) {
        throw "Old and final credentials must target the same guest account."
    }
    if ([string]$VerifiedFinalSession.State -cne "Opened") {
        throw "Final credential PowerShell Direct session is not open."
    }
    $verifiedUserName = Invoke-GuestCommandWithTimeout `
        -Session $VerifiedFinalSession `
        -OperationName "Confirming final credential PowerShell Direct availability" `
        -TimeoutSec 30 `
        -ScriptBlock { [Environment]::UserName }
    if (
        -not [string]::Equals(
            [string]($verifiedUserName | Select-Object -Last 1),
            $FinalCredential.UserName,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw "Final credential PowerShell Direct session does not match the guest account."
    }

    $probeScript = {
        param($TargetVmName, $CandidateCredential)
        $candidateSession = $null
        try {
            $candidateSession = New-PSSession `
                -VMName $TargetVmName `
                -Credential $CandidateCredential `
                -ErrorAction Stop
            [pscustomobject]@{
                connected = $true
                message = ""
                fullyQualifiedErrorId = ""
                category = ""
            }
        } catch {
            $messages = @(
                [string]$_.Exception.Message,
                [string]$_.ErrorDetails.Message
            )
            $innerException = $_.Exception.InnerException
            while ($null -ne $innerException) {
                $messages += [string]$innerException.Message
                $innerException = $innerException.InnerException
            }
            [pscustomobject]@{
                connected = $false
                message = ($messages -join " ")
                fullyQualifiedErrorId = [string]$_.FullyQualifiedErrorId
                category = [string]$_.CategoryInfo.Category
            }
        } finally {
            if ($null -ne $candidateSession) {
                Remove-PSSession -Session $candidateSession -ErrorAction SilentlyContinue
            }
        }
    }
    $probe = [Management.Automation.PowerShell]::Create()
    [void]$probe.AddScript($probeScript.ToString())
    [void]$probe.AddArgument($VMName)
    [void]$probe.AddArgument($OldCredential)
    $asyncResult = $null
    try {
        $asyncResult = $probe.BeginInvoke()
        if (-not $asyncResult.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSec))) {
            $probe.Stop()
            throw "Old credential verification timed out without authentication-rejection proof."
        }
        $result = @($probe.EndInvoke($asyncResult)) | Select-Object -Last 1
        if ($null -eq $result) {
            throw "Old credential verification returned no result."
        }
        if ([bool]$result.connected) {
            throw "The setup credential remained valid after credential rotation."
        }
        $isAuthenticationRejection = Test-CleanWindowsCredentialAuthenticationRejection `
            -Message ([string]$result.message) `
            -FullyQualifiedErrorId ([string]$result.fullyQualifiedErrorId) `
            -Category ([string]$result.category)
        if (-not $isAuthenticationRejection) {
            throw "Old credential attempt failed for a non-authentication reason. Refusing to treat it as rotation proof."
        }
    } finally {
        if ($null -ne $asyncResult) {
            $asyncResult.AsyncWaitHandle.Close()
        }
        $probe.Dispose()
    }
}

function Write-UnattendedResult {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Paths,
        [Parameter(Mandatory = $true)]
        [object]$Verification,
        [Parameter(Mandatory = $true)]
        [string]$Status
    )

    return [pscustomobject][ordered]@{
        command = "Create"
        createMode = "Unattended"
        status = $Status
        vmName = $VMName
        ownerId = $OwnerId
        CredentialPath = $Paths.FinalCredentialPath
        edition = [string]$Verification.editionId
        osName = [string]$Verification.caption
        build = [string]$Verification.buildNumber
        architecture = [string]$Verification.architecture
        powershellDirect = [bool]$Verification.powershellDirect
        localAdministrator = [string]$Verification.localAdministrator
        setupComplete = [bool]$Verification.setupComplete
        oobeComplete = [bool]$Verification.oobeComplete
        installationMediaAttached = $false
        autoLogon = $false
    }
}

function Complete-UnattendedInstallation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [object]$Paths,
        [Parameter(Mandatory = $true)]
        [object]$State,
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$SetupCredential,
        [switch]$RequireAttachedMedia,
        [switch]$AllowAlreadyDetachedAfterPowerShellDirectReady
    )

    if ($RequireAttachedMedia -and $AllowAlreadyDetachedAfterPowerShellDirectReady) {
        throw "RequireAttachedMedia and AllowAlreadyDetachedAfterPowerShellDirectReady are mutually exclusive."
    }
    if (
        $AllowAlreadyDetachedAfterPowerShellDirectReady -and
        [string]$State.status -cne "powershell-direct-ready"
    ) {
        throw "Already-detached resume requires unattended status 'powershell-direct-ready'."
    }
    if (
        -not [string]::Equals(
            $SetupCredential.UserName,
            [string]$State.guestAdministratorName,
            [StringComparison]::Ordinal
        )
    ) {
        throw "Setup credential username does not match the owned unattended-install marker."
    }

    Write-Step "Waiting for unattended Windows setup and PowerShell Direct"
    $setupSession = Open-GuestSession `
        -GuestCredential $SetupCredential `
        -TimeoutSec $UnattendedInstallTimeoutSec
    try {
        if ($RequireAttachedMedia) {
            Assert-BothOwnedInstallationMediaAttached -State $State
        }
        Set-UnattendStateStatus -State $State -Paths $Paths -Status "powershell-direct-ready"
        Write-Step "Detaching owned installation media"
        Detach-OwnedInstallationMedia `
            -State $State `
            -RequireBoth:$RequireAttachedMedia `
            -AllowAlreadyDetachedAfterPowerShellDirectReady:$AllowAlreadyDetachedAfterPowerShellDirectReady
        Remove-OwnedUnattendMedia -Paths $Paths
        Set-UnattendStateStatus -State $State -Paths $Paths -Status "installation-media-removed"

        $verification = Invoke-GuestInstallationVerification `
            -Session $setupSession `
            -LocalAccountName ([string]$State.guestAdministratorName)
        Remove-GuestUnattendCache -Session $setupSession
        Set-UnattendStateStatus -State $State -Paths $Paths -Status "guest-verified"

        $finalCredential = New-CleanWindowsTestCredential `
            -UserName ([string]$State.guestAdministratorName)
        Export-CleanWindowsCredential `
            -Credential $finalCredential `
            -CredentialPath $Paths.FinalCredentialPath `
            -MetadataPath $Paths.FinalCredentialMetadataPath `
            -OwnedRoot $Paths.OwnedRoot `
            -VMName $VMName `
            -OwnerId $OwnerId `
            -Kind "final" | Out-Null
        Set-UnattendStateStatus -State $State -Paths $Paths -Status "final-credential-persisted"

        Set-GuestCredential `
            -Session $setupSession `
            -LocalAccountName ([string]$State.guestAdministratorName) `
            -NewPassword $finalCredential.Password
        Set-UnattendStateStatus -State $State -Paths $Paths -Status "credential-rotated"
    } finally {
        if ($null -ne $setupSession) {
            Remove-PSSession -Session $setupSession -ErrorAction SilentlyContinue
        }
    }

    $finalSession = Open-GuestSession -GuestCredential $finalCredential -TimeoutSec 120
    try {
        $verification = Invoke-GuestInstallationVerification `
            -Session $finalSession `
            -LocalAccountName ([string]$State.guestAdministratorName)
        Assert-OldCredentialRejected `
            -OldCredential $SetupCredential `
            -FinalCredential $finalCredential `
            -VerifiedFinalSession $finalSession `
            -TimeoutSec 30
    } finally {
        Remove-PSSession -Session $finalSession -ErrorAction SilentlyContinue
    }
    Remove-SetupCredentialMaterial -Paths $Paths
    Set-UnattendStateStatus -State $State -Paths $Paths -Status "complete"
    return Write-UnattendedResult -Paths $Paths -Verification $verification -Status "complete"
}

function ConvertTo-SafeGuestDiagnosticText {
    param(
        [AllowNull()]
        [object]$Value,
        [ValidateRange(32, 4096)]
        [int]$MaxChars = 512
    )

    if ($null -eq $Value) {
        return ""
    }

    if (
        $Value -is [System.Management.Automation.PSCredential] -or
        $Value -is [Security.SecureString] -or
        $Value -is [byte[]]
    ) {
        return "<sensitive value omitted>"
    }

    $rawText = $null
    if ($Value -is [System.Management.Automation.ErrorRecord]) {
        $exception = $Value.Exception
        if ($null -eq $exception) {
            $rawText = "<error record without an exception>"
        } else {
            $rawText = "[{0}] {1}" -f $exception.GetType().FullName, $exception.Message
        }
    } elseif ($Value -is [Exception]) {
        $rawText = "[{0}] {1}" -f $Value.GetType().FullName, $Value.Message
    } elseif (
        $Value -is [string] -or
        $Value -is [char] -or
        $Value -is [bool] -or
        $Value -is [byte] -or
        $Value -is [sbyte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64] -or
        $Value -is [uint64] -or
        $Value -is [single] -or
        $Value -is [double] -or
        $Value -is [decimal] -or
        $Value -is [DateTime] -or
        $Value -is [DateTimeOffset] -or
        $Value -is [Guid] -or
        $Value.GetType().IsEnum
    ) {
        $rawText = [string]$Value
    } else {
        return "<{0} object omitted>" -f $Value.GetType().FullName
    }

    if ($rawText.Length -gt ($MaxChars * 2)) {
        $rawText = $rawText.Substring(0, $MaxChars * 2)
    }

    $safeText = [regex]::Replace(
        $rawText,
        "[\x00-\x1F\x7F]+",
        " "
    )
    $safeText = [regex]::Replace(
        $safeText,
        "(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
        "Bearer [redacted]"
    )
    $safeText = [regex]::Replace(
        $safeText,
        "(?i)\b(password|passwd|pwd|token|credential|secret|authorization)\b\s*[:=]\s*[^;,|]*",
        '$1=[redacted]'
    )
    $safeText = [regex]::Replace(
        $safeText,
        "(?i)(?:--?)(password|passwd|pwd|token|credential|secret|authorization)\s+(?:'[^']*'|`"[^`"]*`"|[^\s;,|]+)",
        '--$1 [redacted]'
    )
    $safeText = [regex]::Replace($safeText, "\s+", " ").Trim()

    if ($safeText.Length -gt $MaxChars) {
        return $safeText.Substring(0, $MaxChars) + " [truncated]"
    }
    return $safeText
}

function ConvertTo-BoundedGuestDiagnosticRecords {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [AllowEmptyCollection()]
        [object[]]$Records = @(),
        [ValidateRange(1, 16)]
        [int]$MaxRecords = 4
    )

    $items = New-Object "Collections.Generic.List[string]"
    foreach ($record in @($Records | Select-Object -First $MaxRecords)) {
        $safeRecord = ConvertTo-SafeGuestDiagnosticText -Value $record -MaxChars 384
        if (-not [string]::IsNullOrWhiteSpace($safeRecord)) {
            $items.Add($safeRecord)
        }
    }

    if ($items.Count -eq 0) {
        return ""
    }
    return "{0}=[{1}]" -f $Label, ($items -join " | ")
}

function Get-FailedGuestJobDiagnostic {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Job
    )

    $diagnostics = New-Object "Collections.Generic.List[string]"
    $reason = $Job.JobStateInfo.Reason
    if ($null -ne $reason) {
        $diagnostics.Add(
            "reasonType={0}" -f (ConvertTo-SafeGuestDiagnosticText -Value $reason.GetType().FullName -MaxChars 256)
        )
        $diagnostics.Add(
            "reason={0}" -f (ConvertTo-SafeGuestDiagnosticText -Value $reason -MaxChars 512)
        )
    }

    $receiveErrors = @()
    $receivedOutput = @()
    try {
        $receivedOutput = @(
            Receive-Job `
                -Job $Job `
                -Keep `
                -ErrorAction SilentlyContinue `
                -ErrorVariable receiveErrors
        )
    } catch {
        $diagnostics.Add(
            "receiveFailure={0}" -f (ConvertTo-SafeGuestDiagnosticText -Value $_ -MaxChars 512)
        )
    }

    $receivedText = ConvertTo-BoundedGuestDiagnosticRecords `
        -Label "receivedOutput" `
        -Records $receivedOutput
    if (-not [string]::IsNullOrWhiteSpace($receivedText)) {
        $diagnostics.Add($receivedText)
    }
    $receiveErrorText = ConvertTo-BoundedGuestDiagnosticRecords `
        -Label "receiveErrors" `
        -Records $receiveErrors
    if (-not [string]::IsNullOrWhiteSpace($receiveErrorText)) {
        $diagnostics.Add($receiveErrorText)
    }

    $childIndex = 0
    foreach ($childJob in @($Job.ChildJobs | Select-Object -First 4)) {
        if ($null -eq $childJob) {
            continue
        }

        $childParts = New-Object "Collections.Generic.List[string]"
        $childParts.Add(
            "state={0}" -f (ConvertTo-SafeGuestDiagnosticText -Value $childJob.State -MaxChars 64)
        )
        $childReason = $childJob.JobStateInfo.Reason
        if ($null -ne $childReason) {
            $childParts.Add(
                "reasonType={0}" -f (ConvertTo-SafeGuestDiagnosticText -Value $childReason.GetType().FullName -MaxChars 256)
            )
            $childParts.Add(
                "reason={0}" -f (ConvertTo-SafeGuestDiagnosticText -Value $childReason -MaxChars 384)
            )
        }

        $childErrorText = ConvertTo-BoundedGuestDiagnosticRecords `
            -Label "errors" `
            -Records @($childJob.Error)
        if (-not [string]::IsNullOrWhiteSpace($childErrorText)) {
            $childParts.Add($childErrorText)
        }
        $childOutputText = ConvertTo-BoundedGuestDiagnosticRecords `
            -Label "output" `
            -Records @($childJob.Output)
        if (-not [string]::IsNullOrWhiteSpace($childOutputText)) {
            $childParts.Add($childOutputText)
        }

        $diagnostics.Add(("child[{0}]({1})" -f $childIndex, ($childParts -join "; ")))
        $childIndex++
    }

    if ($diagnostics.Count -eq 0) {
        return "no bounded diagnostics were available"
    }

    return ConvertTo-SafeGuestDiagnosticText `
        -Value ($diagnostics -join "; ") `
        -MaxChars 3072
}

function Invoke-GuestCommandWithTimeout {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [string]$OperationName,
        [Parameter(Mandatory = $true)]
        [scriptblock]$ScriptBlock,
        [object[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSec
    )

    $job = Invoke-Command -Session $Session -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList -AsJob
    try {
        if (-not (Wait-Job -Job $job -Timeout $TimeoutSec)) {
            Stop-Job -Job $job -Force -ErrorAction SilentlyContinue | Out-Null
            throw "$OperationName timed out after $TimeoutSec seconds."
        }

        if ($job.State -ne "Completed") {
            $failedState = [string]$job.State
            $diagnostic = Get-FailedGuestJobDiagnostic -Job $job
            throw "$OperationName ended in state '$failedState'. Guest job diagnostics: $diagnostic"
        }

        $completedReceiveErrors = @()
        $completedOutput = @(
            Receive-Job `
                -Job $job `
                -ErrorAction SilentlyContinue `
                -ErrorVariable completedReceiveErrors
        )
        if ($completedReceiveErrors.Count -gt 0) {
            $completedErrorDiagnostic = ConvertTo-BoundedGuestDiagnosticRecords `
                -Label "receiveErrors" `
                -Records $completedReceiveErrors
            throw "$OperationName completed with guest error records. Guest job diagnostics: $completedErrorDiagnostic"
        }
        return $completedOutput
    } finally {
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue | Out-Null
    }
}

function Restart-GuestAndReconnect {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId
    )

    $ownedVm = Assert-OwnedVM `
        -ResolvedVhdPath $ResolvedVhdPath `
        -ExpectedOwnerId $ExpectedOwnerId
    if ([string]$ownedVm.State -cne "Running") {
        throw "Exact owned VM '$VMName' must be running before a guest restart."
    }
    $ownedVmId = ([Guid]$ownedVm.Id).ToString("D")

    $previousBootOutput = @(
        Invoke-GuestCommandWithTimeout `
            -Session $Session `
            -OperationName "Reading guest boot identity before restart" `
            -TimeoutSec 60 `
            -ScriptBlock {
                return (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().Ticks
            }
    )
    if ($previousBootOutput.Count -ne 1) {
        throw "Guest boot identity probe before restart returned $($previousBootOutput.Count) values; expected one."
    }
    $previousBootTicks = [Int64]$previousBootOutput[0]

    Assert-ConfirmationForOwnedAction -Action "Restarting exact owned VM '$VMName'"
    Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Requesting guest restart" `
        -TimeoutSec 30 `
        -ScriptBlock {
            $restartProcess = Start-Process `
                -FilePath (Join-Path $env:SystemRoot "System32\shutdown.exe") `
                -ArgumentList ([string[]]@("/r", "/t", "5", "/f")) `
                -Wait `
                -PassThru `
                -WindowStyle Hidden
            if ([int]$restartProcess.ExitCode -ne 0) {
                throw "shutdown.exe rejected the bounded restart request with exit code $($restartProcess.ExitCode)."
            }
        } | Out-Null

    Remove-PSSession -Session $Session -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds($GuestRestartTimeoutSec)
    $lastError = $null
    do {
        $candidateSession = $null
        try {
            $candidateSession = New-PSSession -VMName $VMName -Credential $GuestCredential -ErrorAction Stop
            $currentBootOutput = @(
                Invoke-GuestCommandWithTimeout `
                    -Session $candidateSession `
                    -OperationName "Reading guest boot identity after restart" `
                    -TimeoutSec 60 `
                    -ScriptBlock {
                        return (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().Ticks
                    }
            )
            if ($currentBootOutput.Count -ne 1) {
                throw "Guest boot identity probe after restart returned $($currentBootOutput.Count) values; expected one."
            }
            $currentBootTicks = [Int64]$currentBootOutput[0]
            if ([Int64]$currentBootTicks -gt [Int64]$previousBootTicks) {
                $reconnectedVm = Assert-OwnedVM `
                    -ResolvedVhdPath $ResolvedVhdPath `
                    -ExpectedOwnerId $ExpectedOwnerId
                if (([Guid]$reconnectedVm.Id).ToString("D") -cne $ownedVmId) {
                    throw "PowerShell Direct reconnected after restart, but the exact owned VM identity changed."
                }
                return $candidateSession
            }
        } catch {
            $lastError = $_
        }

        if ($null -ne $candidateSession) {
            Remove-PSSession -Session $candidateSession -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    if ($null -ne $lastError) {
        $safeLastError = ConvertTo-SafeGuestDiagnosticText -Value $lastError -MaxChars 512
        throw "Guest '$VMName' did not complete a fresh reboot within $GuestRestartTimeoutSec seconds. Last error: $safeLastError"
    }
    throw "Guest '$VMName' did not report a newer boot time within $GuestRestartTimeoutSec seconds."
}

function Get-RequiredGuestStageResult {
    param(
        [AllowEmptyCollection()]
        [object[]]$Output = @(),
        [Parameter(Mandatory = $true)]
        [string]$ExpectedStage
    )

    $results = @($Output | Where-Object { $null -ne $_ })
    if ($results.Count -ne 1) {
        throw "Guest stage '$ExpectedStage' returned $($results.Count) result objects; expected exactly one."
    }

    $stageProperty = $results[0].PSObject.Properties["stage"]
    if ($null -eq $stageProperty -or [string]$stageProperty.Value -cne $ExpectedStage) {
        throw "Guest stage '$ExpectedStage' did not return its required structured stage identity."
    }
    return $results[0]
}

function Get-GuestOptionalFeatureStageScriptBlock {
    return {
        $featureNames = [string[]]@(
            "Microsoft-Windows-Subsystem-Linux",
            "VirtualMachinePlatform"
        )
        $changedFeatures = New-Object "Collections.Generic.List[string]"
        $featureStates = [ordered]@{}
        $restartReported = $false

        foreach ($featureName in $featureNames) {
            $feature = Get-WindowsOptionalFeature `
                -Online `
                -FeatureName $featureName `
                -ErrorAction Stop
            $initialState = [string]$feature.State
            if ($initialState -ceq "Enabled") {
                $featureStates[$featureName] = $initialState
                continue
            }
            if ($initialState -ceq "EnablePending") {
                $restartReported = $true
                $featureStates[$featureName] = $initialState
                continue
            }
            if (
                $initialState -cne "Disabled" -and
                $initialState -cne "DisabledWithPayloadRemoved"
            ) {
                throw "Feature '$featureName' is in unsupported state '$initialState'."
            }

            $enableResult = Enable-WindowsOptionalFeature `
                -Online `
                -FeatureName $featureName `
                -All `
                -NoRestart `
                -ErrorAction Stop
            $changedFeatures.Add($featureName)
            if ([bool]$enableResult.RestartNeeded) {
                $restartReported = $true
            }

            $resultingFeature = Get-WindowsOptionalFeature `
                -Online `
                -FeatureName $featureName `
                -ErrorAction Stop
            $resultingState = [string]$resultingFeature.State
            if ($resultingState -cne "Enabled" -and $resultingState -cne "EnablePending") {
                throw "Feature '$featureName' ended in unexpected state '$resultingState' after enablement."
            }
            if ($resultingState -ceq "EnablePending") {
                $restartReported = $true
            }
            $featureStates[$featureName] = $resultingState
        }

        $changed = $changedFeatures.Count -gt 0
        $needsRestart = $changed -or $restartReported
        if (-not $needsRestart) {
            foreach ($featureName in $featureNames) {
                if ([string]$featureStates[$featureName] -cne "Enabled") {
                    throw "Feature '$featureName' is not enabled and no restart was requested."
                }
            }
        }

        [pscustomobject][ordered]@{
            stage = "optional-features"
            microsoftWindowsSubsystemLinuxState = [string]$featureStates["Microsoft-Windows-Subsystem-Linux"]
            virtualMachinePlatformState = [string]$featureStates["VirtualMachinePlatform"]
            changed = $changed
            changedFeatures = [string[]]@($changedFeatures)
            restartReported = $restartReported
            needsRestart = $needsRestart
        }
    }
}

function Invoke-GuestOptionalFeatureStage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    $output = Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Preparing guest optional features" `
        -TimeoutSec 1800 `
        -ScriptBlock (Get-GuestOptionalFeatureStageScriptBlock)
    return Get-RequiredGuestStageResult -Output @($output) -ExpectedStage "optional-features"
}

function Get-GuestWslNativeHelperInstallerScriptBlock {
    return {
        function global:Invoke-OpenClawTrustedWslProcess {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)]
                [ValidateSet(
                    "Status",
                    "Version",
                    "InstallNoDistribution",
                    "UpdateWebDownload"
                )]
                [string]$Operation
            )

            function ConvertTo-OpenClawNativeDiagnostic {
                param(
                    [AllowNull()]
                    [string]$Text,
                    [ValidateRange(64, 8192)]
                    [int]$MaxChars = 4096
                )

                if ([string]::IsNullOrEmpty($Text)) {
                    return ""
                }
                $sanitized = [regex]::Replace(
                    $Text,
                    "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]+",
                    ""
                )
                $sanitized = [regex]::Replace(
                    $sanitized,
                    "(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
                    "Bearer [redacted]"
                )
                $sanitized = [regex]::Replace(
                    $sanitized,
                    "(?i)\b(password|passwd|pwd|token|credential|secret|authorization)\b\s*[:=]\s*[^;,|`r`n]*",
                    '$1=[redacted]'
                )
                $sanitized = [regex]::Replace(
                    $sanitized,
                    "(?i)(?:--?)(password|passwd|pwd|token|credential|secret|authorization)\s+(?:'[^']*'|`"[^`"]*`"|[^\s;,|]+)",
                    '--$1 [redacted]'
                )
                $sanitized = $sanitized.Trim()
                if ($sanitized.Length -gt $MaxChars) {
                    return $sanitized.Substring(0, $MaxChars) + " [truncated]"
                }
                return $sanitized
            }

            function Read-OpenClawBoundedNativeText {
                param(
                    [Parameter(Mandatory = $true)]
                    [string]$Path,
                    [ValidateRange(64, 8192)]
                    [int]$MaxChars = 4096
                )

                if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                    return ""
                }

                $stream = $null
                $reader = $null
                try {
                    $stream = [IO.File]::Open(
                        $Path,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::Read,
                        [IO.FileShare]::ReadWrite
                    )
                    $reader = [IO.StreamReader]::new(
                        $stream,
                        [Text.Encoding]::UTF8,
                        $true,
                        1024,
                        $false
                    )
                    $buffer = New-Object "char[]" ($MaxChars + 1)
                    $readCount = $reader.ReadBlock($buffer, 0, $buffer.Length)
                    $boundedCount = [Math]::Min($readCount, $MaxChars)
                    $text = if ($boundedCount -eq 0) {
                        ""
                    } else {
                        -join $buffer[0..($boundedCount - 1)]
                    }
                    if ($readCount -gt $MaxChars) {
                        $text += " [truncated]"
                    }
                    return ConvertTo-OpenClawNativeDiagnostic -Text $text -MaxChars $MaxChars
                } finally {
                    if ($null -ne $reader) {
                        $reader.Dispose()
                    } elseif ($null -ne $stream) {
                        $stream.Dispose()
                    }
                }
            }

            $wslPath = [IO.Path]::GetFullPath(
                (Join-Path $env:SystemRoot "System32\wsl.exe")
            )
            if (-not (Test-Path -LiteralPath $wslPath -PathType Leaf)) {
                throw "The trusted System32 wsl.exe path is not present."
            }

            [string[]]$nativeArguments = switch ($Operation) {
                "Status" {
                    @("--status")
                    break
                }
                "Version" {
                    @("--version")
                    break
                }
                "InstallNoDistribution" {
                    @("--install", "--no-distribution")
                    break
                }
                "UpdateWebDownload" {
                    @("--update", "--web-download")
                    break
                }
                default {
                    throw "Unsupported trusted WSL operation '$Operation'."
                }
            }

            $nonce = [Guid]::NewGuid().ToString("N")
            $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
            $stdoutPath = Join-Path $temporaryRoot ("openclaw-wsl-{0}.stdout.txt" -f $nonce)
            $stderrPath = Join-Path $temporaryRoot ("openclaw-wsl-{0}.stderr.txt" -f $nonce)
            $invocationFailure = $null
            $environmentRestoreFailure = $null
            $result = $null
            $cleanupFailures = New-Object "Collections.Generic.List[string]"
            $priorWslUtf8Value = [Environment]::GetEnvironmentVariable(
                "WSL_UTF8",
                [EnvironmentVariableTarget]::Process
            )
            $wslUtf8WasPresent = $null -ne $priorWslUtf8Value
            $wslUtf8WasSetForLaunch = $false
            try {
                [Environment]::SetEnvironmentVariable(
                    "WSL_UTF8",
                    "1",
                    [EnvironmentVariableTarget]::Process
                )
                $wslUtf8WasSetForLaunch = $true
                $nativeProcess = Start-Process `
                    -FilePath $wslPath `
                    -ArgumentList $nativeArguments `
                    -RedirectStandardOutput $stdoutPath `
                    -RedirectStandardError $stderrPath `
                    -Wait `
                    -PassThru `
                    -WindowStyle Hidden `
                    -ErrorAction Stop
                $result = [pscustomobject][ordered]@{
                    operation = $Operation
                    arguments = [string[]]@($nativeArguments)
                    exitCode = [int]$nativeProcess.ExitCode
                    stdout = Read-OpenClawBoundedNativeText -Path $stdoutPath
                    stderr = Read-OpenClawBoundedNativeText -Path $stderrPath
                }
            } catch {
                $invocationFailure = $_
            } finally {
                if ($wslUtf8WasSetForLaunch) {
                    try {
                        $restoredWslUtf8Value = if ($wslUtf8WasPresent) {
                            $priorWslUtf8Value
                        } else {
                            $null
                        }
                        [Environment]::SetEnvironmentVariable(
                            "WSL_UTF8",
                            $restoredWslUtf8Value,
                            [EnvironmentVariableTarget]::Process
                        )
                    } catch {
                        $environmentRestoreFailure = $_
                    }
                }
                foreach ($capturePath in [string[]]@($stdoutPath, $stderrPath)) {
                    if (Test-Path -LiteralPath $capturePath) {
                        try {
                            Remove-Item -LiteralPath $capturePath -Force -ErrorAction Stop
                        } catch {
                            $cleanupFailures.Add(
                                (ConvertTo-OpenClawNativeDiagnostic -Text $_.Exception.Message -MaxChars 256)
                            )
                        }
                    }
                }
            }

            if ($null -ne $environmentRestoreFailure) {
                $restoreFailureType = $environmentRestoreFailure.Exception.GetType().FullName
                $restoreFailureMessage = ConvertTo-OpenClawNativeDiagnostic `
                    -Text $environmentRestoreFailure.Exception.Message `
                    -MaxChars 512
                throw "Trusted WSL operation '$Operation' could not restore WSL_UTF8 ($restoreFailureType): $restoreFailureMessage"
            }
            if ($null -ne $invocationFailure) {
                $failureType = $invocationFailure.Exception.GetType().FullName
                $failureMessage = ConvertTo-OpenClawNativeDiagnostic `
                    -Text $invocationFailure.Exception.Message `
                    -MaxChars 512
                throw "Trusted WSL operation '$Operation' failed ($failureType): $failureMessage"
            }
            if ($cleanupFailures.Count -gt 0) {
                throw "Trusted WSL operation '$Operation' could not remove its temporary capture files: $($cleanupFailures -join ' | ')"
            }
            if ($null -eq $result) {
                throw "Trusted WSL operation '$Operation' returned no process result."
            }
            return $result
        }

        [pscustomobject][ordered]@{
            stage = "native-helper"
            helperReady = $true
            allowedOperations = [string[]]@(
                "Status",
                "Version",
                "InstallNoDistribution",
                "UpdateWebDownload"
            )
        }
    }
}

function Install-GuestWslNativeHelper {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    $output = Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Installing trusted guest WSL process helper" `
        -TimeoutSec 60 `
        -ScriptBlock (Get-GuestWslNativeHelperInstallerScriptBlock)
    $result = Get-RequiredGuestStageResult -Output @($output) -ExpectedStage "native-helper"
    if (-not [bool]$result.helperReady) {
        throw "Trusted guest WSL process helper did not report ready."
    }
}

function Get-GuestWslPackageStageScriptBlock {
    return {
        function Get-OpenClawWslResultDiagnostic {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Result
            )

            $parts = foreach ($field in ([ordered]@{
                stdout = [string]$Result.stdout
                stderr = [string]$Result.stderr
            }).GetEnumerator()) {
                $diagnostic = [string]$field.Value
                if ($diagnostic.Length -gt 2048) {
                    $diagnostic = $diagnostic.Substring(0, 2048) + " [truncated input]"
                }
                $diagnostic = [regex]::Replace(
                    $diagnostic,
                    "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]+",
                    ""
                )
                $diagnostic = [regex]::Replace(
                    $diagnostic,
                    "(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
                    "******"
                )
                $diagnostic = [regex]::Replace(
                    $diagnostic,
                    "(?i)\b(password|passwd|pwd|token|credential|secret|authorization)\b\s*[:=]\s*[^;,|`r`n]*",
                    '$1=[redacted]'
                )
                $diagnostic = [regex]::Replace(
                    $diagnostic,
                    "(?i)(?:--?)(password|passwd|pwd|token|credential|secret|authorization)\s+(?:'[^']*'|`"[^`"]*`"|[^\s;,|]+)",
                    '--$1 [redacted]'
                )
                $diagnostic = [regex]::Replace($diagnostic, "\s+", " ").Trim()
                if ($diagnostic.Length -gt 448) {
                    $diagnostic = $diagnostic.Substring(0, 448) + " [truncated]"
                }
                if ([string]::IsNullOrEmpty($diagnostic)) {
                    $diagnostic = "<empty>"
                }
                "{0}: {1}" -f [string]$field.Key, $diagnostic
            }
            return ($parts -join " | ")
        }

        function Get-OpenClawWslResultState {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Result,
                [Parameter(Mandatory = $true)]
                [string]$Label
            )

            $exitCode = [int]$Result.exitCode
            $diagnostic = Get-OpenClawWslResultDiagnostic -Result $Result
            $notInstalled = $diagnostic -match (
                "(?i)(?:\bWSL\b|Windows Subsystem for Linux).{0,80}\b(?:is\s+)?not installed\b"
            )
            if ($exitCode -eq 0) {
                if ($notInstalled) {
                    throw "$Label returned exit code 0 with contradictory not-installed output."
                }
                return "ready"
            }
            if ($exitCode -in [int[]]@(-1, 50) -and $notInstalled) {
                return "not-installed"
            }
            throw "$Label returned unexpected exit code $exitCode. Diagnostic: $diagnostic"
        }

        foreach ($featureName in [string[]]@(
            "Microsoft-Windows-Subsystem-Linux",
            "VirtualMachinePlatform"
        )) {
            $feature = Get-WindowsOptionalFeature `
                -Online `
                -FeatureName $featureName `
                -ErrorAction Stop
            if ([string]$feature.State -cne "Enabled") {
                throw "Package stage requires feature '$featureName' to be Enabled; found '$($feature.State)'."
            }
        }

        $statusResult = Invoke-OpenClawTrustedWslProcess -Operation "Status"
        $statusState = Get-OpenClawWslResultState -Result $statusResult -Label "wsl.exe --status"
        if ($statusState -ceq "not-installed") {
            $installResult = Invoke-OpenClawTrustedWslProcess -Operation "InstallNoDistribution"
            $installExitCode = [int]$installResult.exitCode
            if ($installExitCode -notin [int[]]@(0, 3010)) {
                $installDiagnostic = Get-OpenClawWslResultDiagnostic -Result $installResult
                throw "wsl.exe --install --no-distribution returned unexpected exit code $installExitCode. Diagnostic: $installDiagnostic"
            }

            return [pscustomobject][ordered]@{
                stage = "wsl-package"
                scope = "wsl-package-only"
                normalizedState = "restart-required"
                wasInstalled = $false
                installInvoked = $true
                installExitCode = $installExitCode
                updateInvoked = $false
                updateExitCode = $null
                statusExitCode = [int]$statusResult.exitCode
                versionExitCode = $null
                needsRestart = $true
            }
        }
        if ($statusState -cne "ready") {
            throw "wsl.exe --status returned unsupported normalized state '$statusState'."
        }

        $versionResult = Invoke-OpenClawTrustedWslProcess -Operation "Version"
        $versionExitCode = [int]$versionResult.exitCode
        $versionDiagnostic = Get-OpenClawWslResultDiagnostic -Result $versionResult
        if ($versionExitCode -ne 0) {
            $updateResult = Invoke-OpenClawTrustedWslProcess -Operation "UpdateWebDownload"
            $updateExitCode = [int]$updateResult.exitCode
            if ($updateExitCode -notin [int[]]@(0, 3010)) {
                $updateDiagnostic = Get-OpenClawWslResultDiagnostic -Result $updateResult
                throw "wsl.exe --update --web-download returned unexpected exit code $updateExitCode. Version diagnostic: $versionDiagnostic Update diagnostic: $updateDiagnostic"
            }

            return [pscustomobject][ordered]@{
                stage = "wsl-package"
                scope = "wsl-package-only"
                normalizedState = "restart-required"
                wasInstalled = $true
                installInvoked = $false
                installExitCode = $null
                updateInvoked = $true
                updateExitCode = $updateExitCode
                statusExitCode = [int]$statusResult.exitCode
                versionExitCode = $versionExitCode
                needsRestart = $true
            }
        }

        $versionState = Get-OpenClawWslResultState `
            -Result $versionResult `
            -Label "wsl.exe --version"
        if ($versionState -cne "ready") {
            throw "wsl.exe --version returned unsupported normalized state '$versionState'."
        }

        [pscustomobject][ordered]@{
            stage = "wsl-package"
            scope = "wsl-package-only"
            normalizedState = "ready"
            wasInstalled = $true
            installInvoked = $false
            installExitCode = $null
            updateInvoked = $false
            updateExitCode = $null
            statusExitCode = [int]$statusResult.exitCode
            versionExitCode = $versionExitCode
            needsRestart = $false
        }
    }
}

function Invoke-GuestWslPackageStage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    $output = Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Preparing guest WSL package" `
        -TimeoutSec 1800 `
        -ScriptBlock (Get-GuestWslPackageStageScriptBlock)
    $result = Get-RequiredGuestStageResult -Output @($output) -ExpectedStage "wsl-package"
    if ([string]$result.normalizedState -notin [string[]]@("ready", "restart-required")) {
        throw "Guest WSL package stage returned unsupported normalized state '$($result.normalizedState)'."
    }
    return $result
}

function Get-GuestWslVerificationStageScriptBlock {
    return {
        function Get-OpenClawWslVerificationText {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Result
            )

            $combined = ("{0}`n{1}" -f [string]$Result.stdout, [string]$Result.stderr).Trim()
            $combined = [regex]::Replace($combined, "\s+", " ")
            if ($combined.Length -gt 1024) {
                return $combined.Substring(0, 1024) + " [truncated]"
            }
            return $combined
        }

        $featureStates = [ordered]@{}
        foreach ($featureName in [string[]]@(
            "Microsoft-Windows-Subsystem-Linux",
            "VirtualMachinePlatform"
        )) {
            $feature = Get-WindowsOptionalFeature `
                -Online `
                -FeatureName $featureName `
                -ErrorAction Stop
            $featureStates[$featureName] = [string]$feature.State
            if ([string]$feature.State -cne "Enabled") {
                throw "Final WSL verification requires feature '$featureName' to be Enabled; found '$($feature.State)'."
            }
        }

        $statusResult = Invoke-OpenClawTrustedWslProcess -Operation "Status"
        $versionResult = Invoke-OpenClawTrustedWslProcess -Operation "Version"
        $statusText = Get-OpenClawWslVerificationText -Result $statusResult
        $versionText = Get-OpenClawWslVerificationText -Result $versionResult
        if ([int]$statusResult.exitCode -ne 0) {
            throw "Final wsl.exe --status verification failed with exit code $($statusResult.exitCode). Diagnostic: $statusText"
        }
        if ([int]$versionResult.exitCode -ne 0) {
            throw "Final wsl.exe --version verification failed with exit code $($versionResult.exitCode). Diagnostic: $versionText"
        }

        [pscustomobject][ordered]@{
            stage = "wsl-verification"
            scope = "wsl-package-only"
            normalizedState = "ready"
            microsoftWindowsSubsystemLinuxState = [string]$featureStates["Microsoft-Windows-Subsystem-Linux"]
            virtualMachinePlatformState = [string]$featureStates["VirtualMachinePlatform"]
            statusExitCode = [int]$statusResult.exitCode
            versionExitCode = [int]$versionResult.exitCode
            status = $statusText
            version = $versionText
        }
    }
}

function Invoke-GuestWslVerificationStage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    $output = Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Verifying guest WSL package" `
        -TimeoutSec 300 `
        -ScriptBlock (Get-GuestWslVerificationStageScriptBlock)
    $result = Get-RequiredGuestStageResult -Output @($output) -ExpectedStage "wsl-verification"
    if (
        [string]$result.normalizedState -cne "ready" -or
        [int]$result.statusExitCode -ne 0 -or
        [int]$result.versionExitCode -ne 0
    ) {
        throw "Guest WSL verification did not return a ready zero-exit proof."
    }
    return $result
}

function Get-GuestRepoRoot {
    return (Join-Path $GuestRoot "repo")
}

function Invoke-CleanWindowsSourceGit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$OperationName,
        [ValidateRange(1, 600)]
        [int]$TimeoutSec = 300
    )

    $gitCommands = @(
        Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue
    )
    if ($gitCommands.Count -eq 0) {
        throw "Git is required on the host to create the clean source archive."
    }
    $gitPath = [string]$gitCommands[0].Source

    $captureRoot = Join-Path $env:TEMP (
        "openclaw-source-git-{0}" -f [Guid]::NewGuid().ToString("N"))
    $stdoutPath = Join-Path $captureRoot "stdout.txt"
    $stderrPath = Join-Path $captureRoot "stderr.txt"
    $cleanupError = $null
    try {
        New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
        $quotedArguments = @(
            $Arguments | ForEach-Object {
                $argument = [string]$_
                if ($argument.IndexOf('"') -ge 0) {
                    throw "$OperationName received an unsupported quote in a fixed Git argument."
                }
                if ($argument -match '\s') {
                    '"' + $argument + '"'
                } else {
                    $argument
                }
            }
        )
        $process = Start-Process `
            -FilePath $gitPath `
            -WorkingDirectory $RepositoryRoot `
            -ArgumentList ($quotedArguments -join " ") `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -PassThru `
            -WindowStyle Hidden `
            -ErrorAction Stop
        # Windows PowerShell 5.1 requires opening the handle before exit to retain ExitCode.
        $null = $process.Handle
        if (-not $process.WaitForExit($TimeoutSec * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw "$OperationName timed out after $TimeoutSec seconds."
        }
        $process.WaitForExit()
        $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
            [IO.File]::ReadAllText($stdoutPath)
        } else {
            ""
        }
        $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
            [IO.File]::ReadAllText($stderrPath)
        } else {
            ""
        }
        if ([int]$process.ExitCode -ne 0) {
            $diagnostic = ConvertTo-SafeGuestDiagnosticText -Text (
                "stdout='$stdout' stderr='$stderr'")
            throw "$OperationName failed with exit code $($process.ExitCode). Diagnostic: $diagnostic"
        }
        return @(
            $stdout -split '\r?\n' |
                Where-Object { -not [string]::IsNullOrEmpty($_) }
        )
    } finally {
        if (Test-Path -LiteralPath $captureRoot) {
            try {
                Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
            } catch {
                $cleanupError = $_.Exception.Message
            }
        }
        if ($cleanupError) {
            throw "Source Git capture cleanup failed: $cleanupError"
        }
    }
}

function Assert-CleanCommittedSourceHead {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $status = @(
        Invoke-CleanWindowsSourceGit `
            -RepositoryRoot $RepositoryRoot `
            -Arguments @("status", "--porcelain=v1", "--untracked-files=all") `
            -OperationName "Reading source repository status"
    )
    $dirtyEntries = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dirtyEntries.Count -ne 0) {
        throw (
            "Clean-machine source transfer requires a clean committed HEAD. " +
            "The source repository has $($dirtyEntries.Count) staged, unstaged, or untracked entries.")
    }

    $headOutput = @(
        Invoke-CleanWindowsSourceGit `
            -RepositoryRoot $RepositoryRoot `
            -Arguments @("rev-parse", "--verify", "HEAD") `
            -OperationName "Resolving source HEAD"
    )
    if ($headOutput.Count -ne 1 -or $headOutput[0] -notmatch '^[0-9A-Fa-f]{40,64}$') {
        throw "Source HEAD did not resolve to one full Git object ID."
    }
    return $headOutput[0].ToLowerInvariant()
}

function Get-SourceArchiveInstallScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [string]$ArchivePath,
            [Parameter(Mandatory = $true)]
            [string]$ExpectedSha256,
            [Parameter(Mandatory = $true)]
            [int]$ExpectedTrackedFileCount,
            [Parameter(Mandatory = $true)]
            [Int64]$ExpectedArchiveSize,
            [Parameter(Mandatory = $true)]
            [Int64]$MaximumArchiveSize,
            [Parameter(Mandatory = $true)]
            [Int64]$MaximumExpandedSize,
            [Parameter(Mandatory = $true)]
            [int]$MaximumTrackedFileCount,
            [Parameter(Mandatory = $true)]
            [string]$SourceHead,
            [AllowEmptyString()]
            [string]$DestinationRoot,
            [Parameter(Mandatory = $true)]
            [string]$ProvenanceFileName,
            [Parameter(Mandatory = $true)]
            [bool]$Install
        )

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = "Stop"
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        function Assert-OpenClawSourceArchivePath {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [Parameter(Mandatory = $true)]
                [string]$PathKind,
                [switch]$AllowProvenance
            )

            if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOf([char]0) -ge 0) {
                throw "Source archive $PathKind is empty or contains a null character."
            }
            $normalized = $Path.Replace("\", "/")
            if (
                $normalized.StartsWith("/", [StringComparison]::Ordinal) -or
                $normalized.StartsWith("//", [StringComparison]::Ordinal) -or
                $normalized -match '^[A-Za-z]:' -or
                [IO.Path]::IsPathRooted($normalized)
            ) {
                throw "Source archive $PathKind '$Path' is absolute."
            }

            $trimmed = $normalized.TrimEnd("/")
            if ([string]::IsNullOrWhiteSpace($trimmed)) {
                throw "Source archive $PathKind '$Path' has no relative path segments."
            }
            $segments = @($trimmed.Split("/"))
            $forbiddenSegments = @(".git", "bin", "obj", "TestResults")
            foreach ($segment in $segments) {
                if (
                    [string]::IsNullOrWhiteSpace($segment) -or
                    $segment -ceq "." -or
                    $segment -ceq ".." -or
                    $segment.IndexOf(":") -ge 0 -or
                    $segment -match '[<>:"|?*\x00-\x1F]' -or
                    $segment.EndsWith(".", [StringComparison]::Ordinal) -or
                    $segment.EndsWith(" ", [StringComparison]::Ordinal)
                ) {
                    throw "Source archive $PathKind '$Path' is not a safe Windows relative path."
                }
                if ($forbiddenSegments -contains $segment) {
                    throw "Source archive $PathKind '$Path' contains forbidden generated segment '$segment'."
                }
                $deviceName = ($segment.Split(".")[0]).ToUpperInvariant()
                if (
                    $deviceName -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'
                ) {
                    throw "Source archive $PathKind '$Path' contains reserved Windows name '$segment'."
                }
            }

            if (
                -not $AllowProvenance -and
                [string]::Equals(
                    $trimmed,
                    $ProvenanceFileName,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw "Source archive already contains reserved provenance file '$ProvenanceFileName'."
            }
            return $trimmed
        }

        function Get-OpenClawSourceArchiveSha256 {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path
            )

            $stream = [IO.File]::OpenRead($Path)
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $bytes = $algorithm.ComputeHash($stream)
                return (($bytes | ForEach-Object { $_.ToString("X2") }) -join "")
            } finally {
                $algorithm.Dispose()
                $stream.Dispose()
            }
        }

        function Read-OpenClawSourceArchiveLinkTarget {
            param(
                [Parameter(Mandatory = $true)]
                [IO.Compression.ZipArchiveEntry]$Entry
            )

            if ($Entry.Length -le 0 -or $Entry.Length -gt 4096) {
                throw "Source archive symbolic-link entry '$($Entry.FullName)' has an invalid target length."
            }
            $entryStream = $Entry.Open()
            try {
                $memory = New-Object IO.MemoryStream
                try {
                    $entryStream.CopyTo($memory)
                    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
                    $target = $strictUtf8.GetString($memory.ToArray())
                } finally {
                    $memory.Dispose()
                }
            } finally {
                $entryStream.Dispose()
            }
            [void](Assert-OpenClawSourceArchivePath `
                -Path $target `
                -PathKind "symbolic-link target" `
                -AllowProvenance)
            return $target
        }

        function Assert-OpenClawSourceArchive {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path
            )

            $seenPaths = New-Object 'Collections.Generic.HashSet[string]' (
                [StringComparer]::OrdinalIgnoreCase)
            $fileCount = 0
            $entryCount = 0
            $linkCount = 0
            [Int64]$expandedBytes = 0
            $archive = [IO.Compression.ZipFile]::OpenRead($Path)
            try {
                foreach ($entry in $archive.Entries) {
                    $entryCount++
                    if ($entryCount -gt ($MaximumTrackedFileCount * 2)) {
                        throw "Source archive entry count exceeds the bounded maximum."
                    }
                    $isDirectory = $entry.FullName.EndsWith("/", [StringComparison]::Ordinal)
                    $safePath = Assert-OpenClawSourceArchivePath `
                        -Path $entry.FullName `
                        -PathKind "entry"
                    if (-not $seenPaths.Add($safePath)) {
                        throw "Source archive has duplicate case-insensitive path '$safePath'."
                    }

                    $attributeBits = [BitConverter]::ToUInt32(
                        [BitConverter]::GetBytes([int]$entry.ExternalAttributes),
                        0)
                    $unixType = [int](($attributeBits -shr 16) -band 0xF000)
                    if ($isDirectory) {
                        if ($unixType -ne 0 -and $unixType -ne 0x4000) {
                            throw "Source archive directory '$safePath' has an unexpected entry type."
                        }
                        continue
                    }
                    if ($unixType -ne 0 -and $unixType -ne 0x8000 -and $unixType -ne 0xA000) {
                        throw "Source archive file '$safePath' has an unexpected entry type."
                    }

                    $fileCount++
                    if ($fileCount -gt $MaximumTrackedFileCount) {
                        throw "Source archive tracked file count exceeds the bounded maximum."
                    }
                    $expandedBytes += [Int64]$entry.Length
                    if ($expandedBytes -gt $MaximumExpandedSize) {
                        throw "Source archive expanded size exceeds the bounded maximum."
                    }
                    if ($unixType -eq 0xA000) {
                        [void](Read-OpenClawSourceArchiveLinkTarget -Entry $entry)
                        $linkCount++
                    }
                }
            } finally {
                $archive.Dispose()
            }

            if ($fileCount -ne $ExpectedTrackedFileCount) {
                throw (
                    "Source archive contains $fileCount files; " +
                    "expected exactly $ExpectedTrackedFileCount tracked files.")
            }
            return [pscustomobject][ordered]@{
                entryCount = $entryCount
                fileCount = $fileCount
                expandedBytes = $expandedBytes
                symbolicLinkCount = $linkCount
            }
        }

        $cleanupError = $null
        try {
            if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
                throw "Source archive was not found at the expected path."
            }
            $archiveItem = Get-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
            if (
                [Int64]$archiveItem.Length -ne $ExpectedArchiveSize -or
                [Int64]$archiveItem.Length -le 0 -or
                [Int64]$archiveItem.Length -gt $MaximumArchiveSize
            ) {
                throw (
                    "Source archive size is $($archiveItem.Length) bytes; " +
                    "expected $ExpectedArchiveSize within the bounded maximum.")
            }
            $actualSha256 = Get-OpenClawSourceArchiveSha256 -Path $ArchivePath
            if (-not [string]::Equals(
                    $actualSha256,
                    $ExpectedSha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Source archive SHA256 mismatch."
            }
            if ($SourceHead -notmatch '^[0-9a-f]{40,64}$') {
                throw "Source archive provenance HEAD is not a full lowercase Git object ID."
            }

            $archiveProof = Assert-OpenClawSourceArchive -Path $ArchivePath
            if ($Install) {
                if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
                    throw "Guest source archive destination root is required."
                }
                $canonicalDestination = [IO.Path]::GetFullPath($DestinationRoot)
                if (
                    [string]::Equals(
                        $canonicalDestination.TrimEnd("\"),
                        [IO.Path]::GetPathRoot($canonicalDestination).TrimEnd("\"),
                        [StringComparison]::OrdinalIgnoreCase)
                ) {
                    throw "Guest source archive destination cannot be a drive root."
                }

                if (Test-Path -LiteralPath $canonicalDestination) {
                    Remove-Item -LiteralPath $canonicalDestination -Recurse -Force -ErrorAction Stop
                }
                New-Item -ItemType Directory -Path $canonicalDestination -ErrorAction Stop | Out-Null
                Expand-Archive `
                    -LiteralPath $ArchivePath `
                    -DestinationPath $canonicalDestination `
                    -Force `
                    -ErrorAction Stop

                $reparseItems = @(
                    Get-ChildItem -LiteralPath $canonicalDestination -Recurse -Force -ErrorAction Stop |
                        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
                )
                if ($reparseItems.Count -ne 0) {
                    throw "Extracted source contains $($reparseItems.Count) reparse points."
                }
                $generatedDirectories = @(
                    Get-ChildItem -LiteralPath $canonicalDestination -Directory -Recurse -Force -ErrorAction Stop |
                        Where-Object { @("bin", "obj", "TestResults") -contains $_.Name }
                )
                if ($generatedDirectories.Count -ne 0) {
                    throw "Extracted source contains forbidden generated directories."
                }
                $extractedFiles = @(
                    Get-ChildItem -LiteralPath $canonicalDestination -File -Recurse -Force -ErrorAction Stop
                )
                if ($extractedFiles.Count -ne $ExpectedTrackedFileCount) {
                    throw (
                        "Extracted source contains $($extractedFiles.Count) files; " +
                        "expected exactly $ExpectedTrackedFileCount.")
                }

                $provenance = [pscustomobject][ordered]@{
                    schema = "openclaw.clean-windows.source-provenance/v1"
                    sourceHead = $SourceHead
                    trackedFileCount = $ExpectedTrackedFileCount
                    archiveSize = $ExpectedArchiveSize
                    archiveSha256 = $ExpectedSha256.ToUpperInvariant()
                    archiveEntryCount = [int]$archiveProof.entryCount
                    expandedBytes = [Int64]$archiveProof.expandedBytes
                }
                $provenancePath = Join-Path $canonicalDestination $ProvenanceFileName
                $utf8NoBom = New-Object Text.UTF8Encoding($false)
                [IO.File]::WriteAllText(
                    $provenancePath,
                    ($provenance | ConvertTo-Json -Depth 4),
                    $utf8NoBom)
            }

            return [pscustomobject][ordered]@{
                sourceHead = $SourceHead
                sha256 = $actualSha256
                archiveSize = [Int64]$archiveItem.Length
                trackedFileCount = [int]$archiveProof.fileCount
                archiveEntryCount = [int]$archiveProof.entryCount
                expandedBytes = [Int64]$archiveProof.expandedBytes
                symbolicLinkCount = [int]$archiveProof.symbolicLinkCount
                installed = $Install
            }
        } finally {
            if ($Install -and (Test-Path -LiteralPath $ArchivePath)) {
                try {
                    Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
                } catch {
                    $cleanupError = $_.Exception.Message
                }
            }
            if ($cleanupError) {
                throw "Guest source archive cleanup failed: $cleanupError"
            }
        }
    }
}

function Get-GuestSourceStagingScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [string]$RepositoryRoot,
            [Parameter(Mandatory = $true)]
            [string]$SourceHead,
            [Parameter(Mandatory = $true)]
            [string]$ProvenanceFileName,
            [ValidateRange(1, 300)]
            [int]$NativeTimeoutSec
        )

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = "Stop"

        function ConvertTo-OpenClawGuestGitDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [int]$MaximumLength = 1024
            )

            if ([string]::IsNullOrWhiteSpace($Text)) {
                return "<empty>"
            }
            $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
            $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
            $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
            $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
            $safe = ($safe -replace '\s+', ' ').Trim()
            if ($safe.Length -gt $MaximumLength) {
                return $safe.Substring(0, $MaximumLength) + "...<truncated>"
            }
            return $safe
        }

        function Get-OpenClawGuestSourceFileSha256 {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path
            )

            $stream = [IO.File]::OpenRead($Path)
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                return (($algorithm.ComputeHash($stream) |
                    ForEach-Object { $_.ToString("X2") }) -join "")
            } finally {
                $algorithm.Dispose()
                $stream.Dispose()
            }
        }

        function Get-OpenClawGuestSourceTreeSha256 {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Root
            )

            $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd("\")
            $rootPrefix = $canonicalRoot + "\"
            $records = New-Object 'Collections.Generic.List[string]'
            foreach ($file in Get-ChildItem -LiteralPath $canonicalRoot -File -Recurse -Force -ErrorAction Stop) {
                $fullPath = [IO.Path]::GetFullPath([string]$file.FullName)
                if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Guest source digest encountered a file outside the repository root."
                }
                $relativePath = $fullPath.Substring($rootPrefix.Length).Replace("\", "/")
                if (
                    $relativePath.StartsWith(".git/", [StringComparison]::OrdinalIgnoreCase) -or
                    [string]::Equals($relativePath, ".git", [StringComparison]::OrdinalIgnoreCase)
                ) {
                    continue
                }
                $fileHash = Get-OpenClawGuestSourceFileSha256 -Path $fullPath
                [void]$records.Add("$relativePath|$fileHash")
            }
            if ($records.Count -eq 0) {
                throw "Guest source digest found no repository files."
            }
            $orderedRecords = $records.ToArray()
            [Array]::Sort($orderedRecords, [StringComparer]::Ordinal)
            $manifestBytes = (New-Object Text.UTF8Encoding($false)).GetBytes(
                ($orderedRecords -join "`n"))
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                return (($algorithm.ComputeHash($manifestBytes) |
                    ForEach-Object { $_.ToString("X2") }) -join "")
            } finally {
                $algorithm.Dispose()
            }
        }

        function Set-OpenClawGuestSourceOwner {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Root,
                [Parameter(Mandatory = $true)]
                [int]$ExpectedTrackedFileCount,
                [switch]$IncludeGitMetadata
            )

            if ($ExpectedTrackedFileCount -lt 1 -or $ExpectedTrackedFileCount -gt 20000) {
                throw "Guest source ownership received an invalid tracked-file count."
            }
            $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd("\")
            $rootPrefix = $canonicalRoot + "\"
            $maximumEntries = if ($IncludeGitMetadata) {
                ($ExpectedTrackedFileCount * 8) + 2048
            } else {
                ($ExpectedTrackedFileCount * 4) + 2
            }
            $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
            if ($null -eq $currentSid) {
                throw "Guest source ownership could not resolve the current Windows user SID."
            }
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $entries = New-Object 'Collections.Generic.List[object]'
            $pendingDirectories = New-Object 'Collections.Generic.Queue[string]'
            $rootItem = Get-Item -LiteralPath $canonicalRoot -Force -ErrorAction Stop
            if (
                -not $rootItem.PSIsContainer -or
                ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "Guest source repository root is missing, not a directory, or is a reparse point."
            }
            [void]$entries.Add($rootItem)
            $pendingDirectories.Enqueue($canonicalRoot)
            while ($pendingDirectories.Count -gt 0) {
                if ($stopwatch.Elapsed.TotalSeconds -gt $NativeTimeoutSec) {
                    throw "Guest source ownership enumeration timed out after $NativeTimeoutSec seconds."
                }
                $directory = $pendingDirectories.Dequeue()
                foreach ($child in @(
                    Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop
                )) {
                    $fullPath = [IO.Path]::GetFullPath([string]$child.FullName)
                    if (-not $fullPath.StartsWith(
                        $rootPrefix,
                        [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Guest source ownership encountered an entry outside the repository root."
                    }
                    if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw "Guest source ownership refuses reparse point '$fullPath'."
                    }
                    [void]$entries.Add($child)
                    if ($entries.Count -gt $maximumEntries) {
                        throw "Guest source ownership entry count exceeds the bounded maximum."
                    }
                    if ($child.PSIsContainer) {
                        $pendingDirectories.Enqueue($fullPath)
                    }
                }
            }

            $fileCount = @($entries | Where-Object { -not $_.PSIsContainer }).Count
            if (
                -not $IncludeGitMetadata -and
                $fileCount -ne ($ExpectedTrackedFileCount + 1)
            ) {
                throw (
                    "Guest source ownership found $fileCount files; expected tracked files plus provenance.")
            }
            if ($IncludeGitMetadata) {
                $gitRoot = Join-Path $canonicalRoot ".git"
                if (-not (Test-Path -LiteralPath $gitRoot -PathType Container)) {
                    throw "Guest source ownership expected generated Git metadata."
                }
                $gitRootItem = Get-Item -LiteralPath $gitRoot -Force -ErrorAction Stop
                if (($gitRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Guest source ownership refuses reparse point '$gitRoot'."
                }
                if ($fileCount -le ($ExpectedTrackedFileCount + 1)) {
                    throw "Guest source ownership found no generated Git metadata files."
                }
            }
            foreach ($entry in $entries) {
                if ($stopwatch.Elapsed.TotalSeconds -gt $NativeTimeoutSec) {
                    throw "Guest source ownership normalization timed out after $NativeTimeoutSec seconds."
                }
                $currentItem = Get-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
                if (($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Guest source ownership refuses reparse point '$($currentItem.FullName)'."
                }
                $aclSections = (
                    [Security.AccessControl.AccessControlSections]::Access -bor
                    [Security.AccessControl.AccessControlSections]::Owner -bor
                    [Security.AccessControl.AccessControlSections]::Group)
                $acl = if ($currentItem.PSIsContainer) {
                    [IO.Directory]::GetAccessControl($currentItem.FullName, $aclSections)
                } else {
                    [IO.File]::GetAccessControl($currentItem.FullName, $aclSections)
                }
                $acl.SetOwner($currentSid)
                if ($currentItem.PSIsContainer) {
                    [IO.Directory]::SetAccessControl($currentItem.FullName, $acl)
                } else {
                    [IO.File]::SetAccessControl($currentItem.FullName, $acl)
                }
            }
            foreach ($entry in $entries) {
                if ($stopwatch.Elapsed.TotalSeconds -gt $NativeTimeoutSec) {
                    throw "Guest source ownership verification timed out after $NativeTimeoutSec seconds."
                }
                $currentItem = Get-Item -LiteralPath $entry.FullName -Force -ErrorAction Stop
                if (($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Guest source ownership refuses reparse point '$($currentItem.FullName)'."
                }
                $ownerAcl = if ($currentItem.PSIsContainer) {
                    [IO.Directory]::GetAccessControl(
                        $currentItem.FullName,
                        [Security.AccessControl.AccessControlSections]::Owner)
                } else {
                    [IO.File]::GetAccessControl(
                        $currentItem.FullName,
                        [Security.AccessControl.AccessControlSections]::Owner)
                }
                $owner = $ownerAcl.GetOwner([Security.Principal.SecurityIdentifier])
                if (-not $owner.Equals($currentSid)) {
                    throw "Guest source ownership verification found an entry with the wrong owner."
                }
            }
            $stopwatch.Stop()
            return [pscustomobject][ordered]@{
                ownerSid = $currentSid.Value
                entryCount = $entries.Count
                fileCount = $fileCount
                includesGitMetadata = [bool]$IncludeGitMetadata
            }
        }

        function Invoke-OpenClawGuestGitProcess {
            param(
                [Parameter(Mandatory = $true)]
                [ValidateSet(
                    "Init",
                    "BranchMain",
                    "ConfigUserName",
                    "ConfigUserEmail",
                    "ConfigAutoCrlf",
                    "ConfigSafeCrlf",
                    "AddAll",
                    "Commit",
                    "Status")]
                [string]$Operation,
                [Parameter(Mandatory = $true)]
                [string]$GitPath,
                [Parameter(Mandatory = $true)]
                [string]$WorkingDirectory,
                [Parameter(Mandatory = $true)]
                [string]$CaptureRoot
            )

            switch ($Operation) {
                "Init" { $arguments = [string[]]@("init") }
                "BranchMain" { $arguments = [string[]]@("branch", "-M", "main") }
                "ConfigUserName" {
                    $arguments = [string[]]@(
                        "config", "--local", "user.name", "OpenClaw Clean Windows Runner")
                }
                "ConfigUserEmail" {
                    $arguments = [string[]]@(
                        "config", "--local", "user.email", "openclaw-clean-windows@example.invalid")
                }
                "ConfigAutoCrlf" {
                    $arguments = [string[]]@("config", "--local", "core.autocrlf", "false")
                }
                "ConfigSafeCrlf" {
                    $arguments = [string[]]@("config", "--local", "core.safecrlf", "true")
                }
                "AddAll" { $arguments = [string[]]@("add", "-A") }
                "Commit" {
                    $arguments = [string[]]@(
                        "commit", "-m", "Guest staging from $SourceHead", "--quiet")
                }
                "Status" { $arguments = [string[]]@("status", "--porcelain") }
            }

            $quotedArguments = @(
                $arguments | ForEach-Object {
                    $argument = [string]$_
                    if ($argument.IndexOf('"') -ge 0) {
                        throw "Guest Git operation '$Operation' contains an unsupported quote."
                    }
                    if ($argument -match '\s') {
                        '"' + $argument + '"'
                    } else {
                        $argument
                    }
                }
            )
            $stdoutPath = Join-Path $CaptureRoot "$Operation.stdout.txt"
            $stderrPath = Join-Path $CaptureRoot "$Operation.stderr.txt"
            $process = Start-Process `
                -FilePath $GitPath `
                -WorkingDirectory $WorkingDirectory `
                -ArgumentList ($quotedArguments -join " ") `
                -RedirectStandardOutput $stdoutPath `
                -RedirectStandardError $stderrPath `
                -PassThru `
                -WindowStyle Hidden `
                -ErrorAction Stop
            # Windows PowerShell 5.1 requires opening the handle before exit to retain ExitCode.
            $null = $process.Handle
            if (-not $process.WaitForExit($NativeTimeoutSec * 1000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                throw "Guest Git operation '$Operation' timed out after $NativeTimeoutSec seconds."
            }
            $process.WaitForExit()
            $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
                [IO.File]::ReadAllText($stdoutPath)
            } else {
                ""
            }
            $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
                [IO.File]::ReadAllText($stderrPath)
            } else {
                ""
            }
            $safeStdout = ConvertTo-OpenClawGuestGitDiagnostic -Text $stdout
            $safeStderr = ConvertTo-OpenClawGuestGitDiagnostic -Text $stderr
            if ([int]$process.ExitCode -ne 0) {
                throw (
                    "Guest Git operation '{0}' failed with exit code {1}. stdout='{2}' stderr='{3}'" -f
                        $Operation,
                        $process.ExitCode,
                        $safeStdout,
                        $safeStderr)
            }
            return [pscustomobject][ordered]@{
                operation = $Operation
                exitCode = [int]$process.ExitCode
                hasStdout = -not [string]::IsNullOrWhiteSpace($stdout)
                hasStderr = -not [string]::IsNullOrWhiteSpace($stderr)
                stdout = $safeStdout
                stderr = $safeStderr
            }
        }

        if ($SourceHead -notmatch '^[0-9a-f]{40,64}$') {
            throw "Guest staging source HEAD is not a full lowercase Git object ID."
        }
        $canonicalRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        $provenancePath = Join-Path $canonicalRoot $ProvenanceFileName
        $provenance = Get-Content -LiteralPath $provenancePath -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
        if (
            [string]$provenance.schema -cne "openclaw.clean-windows.source-provenance/v1" -or
            [string]$provenance.sourceHead -cne $SourceHead
        ) {
            throw "Guest source provenance does not match the exact host HEAD."
        }
        $provenanceTrackedFileCount = [int]$provenance.trackedFileCount
        $ownershipProof = Set-OpenClawGuestSourceOwner `
            -Root $canonicalRoot `
            -ExpectedTrackedFileCount $provenanceTrackedFileCount

        $gitCommands = @(
            Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue
        )
        if ($gitCommands.Count -eq 0) {
            throw "Git is not available in the guest."
        }
        $gitPath = [string]$gitCommands[0].Source
        $captureRoot = Join-Path $env:TEMP (
            "openclaw-guest-git-{0}" -f [Guid]::NewGuid().ToString("N"))
        $warnings = New-Object 'Collections.Generic.List[string]'
        $cleanupError = $null
        try {
            New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
            $beforeSha256 = Get-OpenClawGuestSourceTreeSha256 -Root $canonicalRoot
            foreach ($operation in @(
                    "Init",
                    "BranchMain",
                    "ConfigUserName",
                    "ConfigUserEmail",
                    "ConfigAutoCrlf",
                    "ConfigSafeCrlf",
                    "AddAll",
                    "Commit")) {
                $result = Invoke-OpenClawGuestGitProcess `
                    -Operation $operation `
                    -GitPath $gitPath `
                    -WorkingDirectory $canonicalRoot `
                    -CaptureRoot $captureRoot
                if ([bool]$result.hasStderr) {
                    [void]$warnings.Add("$operation`: $($result.stderr)")
                }
            }
            $ownershipProof = Set-OpenClawGuestSourceOwner `
                -Root $canonicalRoot `
                -ExpectedTrackedFileCount $provenanceTrackedFileCount `
                -IncludeGitMetadata

            $statusResult = Invoke-OpenClawGuestGitProcess `
                -Operation "Status" `
                -GitPath $gitPath `
                -WorkingDirectory $canonicalRoot `
                -CaptureRoot $captureRoot
            if ([bool]$statusResult.hasStderr) {
                [void]$warnings.Add("Status: $($statusResult.stderr)")
            }
            if ([bool]$statusResult.hasStdout) {
                throw "Guest staging repository is not clean after its provenance commit: $($statusResult.stdout)"
            }
            $afterSha256 = Get-OpenClawGuestSourceTreeSha256 -Root $canonicalRoot
            if (-not [string]::Equals(
                    $beforeSha256,
                    $afterSha256,
                    [StringComparison]::Ordinal)) {
                throw "Guest Git staging mutated extracted source bytes."
            }

            return [pscustomobject][ordered]@{
                sourceHead = $SourceHead
                beforeSha256 = $beforeSha256
                afterSha256 = $afterSha256
                warningCount = $warnings.Count
                warnings = @($warnings)
                ownerSid = [string]$ownershipProof.ownerSid
                ownedEntryCount = [int]$ownershipProof.entryCount
                clean = $true
            }
        } finally {
            if (Test-Path -LiteralPath $captureRoot) {
                try {
                    Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
                } catch {
                    $cleanupError = $_.Exception.Message
                }
            }
            if ($cleanupError) {
                throw "Guest Git capture cleanup failed: $cleanupError"
            }
        }
    }
}

function New-CleanWindowsSourceArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath
    )

    $sourceHead = Assert-CleanCommittedSourceHead -RepositoryRoot $RepositoryRoot
    $treeModes = @(
        Invoke-CleanWindowsSourceGit `
            -RepositoryRoot $RepositoryRoot `
            -Arguments @("ls-tree", "-r", "--full-tree", "--format=%(objectmode)", "HEAD") `
            -OperationName "Reading source HEAD tree"
    )
    $treeModes = @($treeModes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($treeModes.Count -le 0 -or $treeModes.Count -gt $script:SourceArchiveMaximumTrackedFiles) {
        throw "Source HEAD tracked file count '$($treeModes.Count)' is outside the bounded range."
    }
    $unexpectedModes = @(
        $treeModes | Where-Object { $_ -cne "100644" -and $_ -cne "100755" -and $_ -cne "120000" }
    )
    if ($unexpectedModes.Count -ne 0) {
        throw "Source HEAD contains unsupported Git tree entry modes."
    }

    [void](
        Invoke-CleanWindowsSourceGit `
            -RepositoryRoot $RepositoryRoot `
            -Arguments @("archive", "--format=zip", "--output=$ArchivePath", "HEAD") `
            -OperationName "Creating deterministic source archive")
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        throw "Git archive completed without creating the expected source archive."
    }
    $archiveItem = Get-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
    if (
        [Int64]$archiveItem.Length -le 0 -or
        [Int64]$archiveItem.Length -gt $script:SourceArchiveMaximumBytes
    ) {
        throw "Source archive size '$($archiveItem.Length)' is outside the bounded range."
    }
    $hashStream = [IO.File]::OpenRead($ArchivePath)
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $archiveSha256 = (
            ($hashAlgorithm.ComputeHash($hashStream) |
                ForEach-Object { $_.ToString("X2") }) -join "")
    } finally {
        $hashAlgorithm.Dispose()
        $hashStream.Dispose()
    }
    $validation = @(
        & (Get-SourceArchiveInstallScriptBlock) `
            $ArchivePath `
            $archiveSha256 `
            $treeModes.Count `
            ([Int64]$archiveItem.Length) `
            $script:SourceArchiveMaximumBytes `
            $script:SourceArchiveMaximumExpandedBytes `
            $script:SourceArchiveMaximumTrackedFiles `
            $sourceHead `
            "" `
            $script:SourceProvenanceFileName `
            $false
    )
    if ($validation.Count -ne 1) {
        throw "Source archive validation returned $($validation.Count) results; expected one."
    }

    $confirmedHead = Assert-CleanCommittedSourceHead -RepositoryRoot $RepositoryRoot
    if (-not [string]::Equals($sourceHead, $confirmedHead, [StringComparison]::Ordinal)) {
        throw "Source HEAD changed while the clean source archive was being created."
    }
    return [pscustomobject][ordered]@{
        SourceHead = $sourceHead
        ArchivePath = $ArchivePath
        ArchiveSize = [Int64]$archiveItem.Length
        ArchiveSha256 = $archiveSha256
        TrackedFileCount = $treeModes.Count
        ArchiveEntryCount = [int]$validation[0].archiveEntryCount
        ExpandedBytes = [Int64]$validation[0].expandedBytes
        SymbolicLinkCount = [int]$validation[0].symbolicLinkCount
    }
}

function Copy-RepoToGuest {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    $guestRepoRoot = Get-GuestRepoRoot
    $transferNonce = [Guid]::NewGuid().ToString("N")
    $hostTransferRoot = Join-Path $env:TEMP "openclaw-clean-source-$transferNonce"
    $hostArchivePath = Join-Path $hostTransferRoot "source.zip"
    $guestTransferRoot = Join-Path $GuestRoot "transfer"
    $guestArchivePath = Join-Path $guestTransferRoot "source-$transferNonce.zip"
    $guestCleanupError = $null
    $hostCleanupError = $null
    Write-Step "Transferring clean committed source archive into the guest"

    try {
        New-Item -ItemType Directory -Path $hostTransferRoot -ErrorAction Stop | Out-Null
        $sourceArchive = New-CleanWindowsSourceArchive `
            -RepositoryRoot $script:RepoRoot `
            -ArchivePath $hostArchivePath
        Write-InfoLine (
            "Source archive: HEAD={0} files={1} entries={2} bytes={3} SHA256={4}" -f
                $sourceArchive.SourceHead,
                $sourceArchive.TrackedFileCount,
                $sourceArchive.ArchiveEntryCount,
                $sourceArchive.ArchiveSize,
                $sourceArchive.ArchiveSha256)

        Invoke-GuestCommandWithTimeout `
            -Session $Session `
            -OperationName "Preparing guest source archive transfer" `
            -TimeoutSec 60 `
            -ScriptBlock {
                param($RemoteTransferRoot, $RemoteArchivePath)
                New-Item -ItemType Directory -Path $RemoteTransferRoot -Force -ErrorAction Stop | Out-Null
                if (Test-Path -LiteralPath $RemoteArchivePath) {
                    Remove-Item -LiteralPath $RemoteArchivePath -Force -ErrorAction Stop
                }
            } `
            -ArgumentList @($guestTransferRoot, $guestArchivePath) | Out-Null

        $transferStopwatch = [Diagnostics.Stopwatch]::StartNew()
        try {
            Copy-Item `
                -LiteralPath $sourceArchive.ArchivePath `
                -Destination $guestArchivePath `
                -ToSession $Session `
                -Force `
                -ErrorAction Stop
        } catch {
            throw (
                "Single-file source archive transfer failed after {0:N1} seconds " +
                "(bytes={1}, SHA256={2}, HEAD={3}): {4}" -f
                    $transferStopwatch.Elapsed.TotalSeconds,
                    $sourceArchive.ArchiveSize,
                    $sourceArchive.ArchiveSha256,
                    $sourceArchive.SourceHead,
                    $_.Exception.Message)
        } finally {
            $transferStopwatch.Stop()
        }

        $installOutput = @(
            Invoke-GuestCommandWithTimeout `
                -Session $Session `
                -OperationName "Verifying and extracting guest source archive" `
                -TimeoutSec 600 `
                -ScriptBlock (Get-SourceArchiveInstallScriptBlock) `
                -ArgumentList @(
                    $guestArchivePath,
                    $sourceArchive.ArchiveSha256,
                    $sourceArchive.TrackedFileCount,
                    $sourceArchive.ArchiveSize,
                    $script:SourceArchiveMaximumBytes,
                    $script:SourceArchiveMaximumExpandedBytes,
                    $script:SourceArchiveMaximumTrackedFiles,
                    $sourceArchive.SourceHead,
                    $guestRepoRoot,
                    $script:SourceProvenanceFileName,
                    $true)
        )
        if (
            $installOutput.Count -ne 1 -or
            -not [bool]$installOutput[0].installed -or
            [string]$installOutput[0].sourceHead -cne [string]$sourceArchive.SourceHead -or
            [string]$installOutput[0].sha256 -cne [string]$sourceArchive.ArchiveSha256 -or
            [int]$installOutput[0].trackedFileCount -ne [int]$sourceArchive.TrackedFileCount
        ) {
            throw "Guest source archive proof did not match the exact host archive."
        }

        $stagingOutput = @(
            Invoke-GuestCommandWithTimeout `
                -Session $Session `
                -OperationName "Initializing guest git repo" `
                -TimeoutSec 600 `
                -ScriptBlock (Get-GuestSourceStagingScriptBlock) `
                -ArgumentList @(
                    $guestRepoRoot,
                    $sourceArchive.SourceHead,
                    $script:SourceProvenanceFileName,
                    120)
        )
        if (
            $stagingOutput.Count -ne 1 -or
            -not [bool]$stagingOutput[0].clean -or
            [string]$stagingOutput[0].sourceHead -cne [string]$sourceArchive.SourceHead -or
            [string]::IsNullOrWhiteSpace([string]$stagingOutput[0].ownerSid) -or
            [int]$stagingOutput[0].ownedEntryCount -le [int]$sourceArchive.TrackedFileCount -or
            [string]$stagingOutput[0].beforeSha256 -cne [string]$stagingOutput[0].afterSha256
        ) {
            throw "Guest Git staging proof did not preserve exact extracted source bytes."
        }
        if ([int]$stagingOutput[0].warningCount -gt 0) {
            Write-InfoLine (
                "Guest Git staging completed with {0} bounded warning diagnostics: {1}" -f
                    $stagingOutput[0].warningCount,
                    (@($stagingOutput[0].warnings) -join " | "))
        }
    } finally {
        try {
            Invoke-GuestCommandWithTimeout `
                -Session $Session `
                -OperationName "Cleaning guest source archive transfer" `
                -TimeoutSec 60 `
                -ScriptBlock {
                    param($RemoteArchivePath)
                    if (Test-Path -LiteralPath $RemoteArchivePath) {
                        Remove-Item -LiteralPath $RemoteArchivePath -Force -ErrorAction Stop
                    }
                } `
                -ArgumentList @($guestArchivePath) | Out-Null
        } catch {
            $guestCleanupError = $_.Exception.Message
        }
        if (Test-Path -LiteralPath $hostTransferRoot) {
            try {
                Remove-Item -LiteralPath $hostTransferRoot -Recurse -Force -ErrorAction Stop
            } catch {
                $hostCleanupError = $_.Exception.Message
            }
        }
        if ($guestCleanupError -or $hostCleanupError) {
            throw (
                "Source archive cleanup failed. guest='{0}' host='{1}'" -f
                    $(if ($guestCleanupError) { $guestCleanupError } else { "<none>" }),
                    $(if ($hostCleanupError) { $hostCleanupError } else { "<none>" }))
        }
    }
}

function Get-GuestWingetBootstrapScriptBlock {
    return {
        $ErrorActionPreference = "Stop"
        Set-StrictMode -Version 2.0

        function Get-OpenClawWingetXmlAttribute {
            param(
                [Parameter(Mandatory = $true)]
                [System.Xml.XmlElement]$Element,
                [Parameter(Mandatory = $true)]
                [string]$Name
            )

            if (-not $Element.HasAttribute($Name)) {
                throw "XML element '$($Element.LocalName)' is missing required attribute '$Name'."
            }
            return [string]$Element.GetAttribute($Name)
        }

        function Get-OpenClawWingetDirectXmlChildren {
            param(
                [Parameter(Mandatory = $true)]
                [System.Xml.XmlNode]$Parent,
                [Parameter(Mandatory = $true)]
                [string]$LocalName
            )

            return @(
                $Parent.ChildNodes |
                    Where-Object {
                        $_ -is [System.Xml.XmlElement] -and
                        [string]$_.LocalName -ceq $LocalName
                    }
            )
        }

        function ConvertTo-OpenClawWingetDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [ValidateRange(64, 8192)]
                [int]$MaxChars = 2048
            )

            if ([string]::IsNullOrEmpty($Text)) {
                return ""
            }
            $sanitized = [regex]::Replace(
                $Text,
                "[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]+",
                ""
            )
            $sanitized = [regex]::Replace(
                $sanitized,
                "(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+",
                "******"
            )
            $sanitized = [regex]::Replace(
                $sanitized,
                "(?i)\b(password|passwd|pwd|token|credential|secret|authorization|sig|jwt)\b\s*[:=]\s*[^;,|`r`n\s]*",
                '$1=[redacted]'
            )
            $sanitized = [regex]::Replace(
                $sanitized,
                "(?i)(https://[^\s?]+)\?[^\s]+",
                '$1?[redacted]'
            )
            $sanitized = [regex]::Replace($sanitized, "\s+", " ").Trim()
            if ($sanitized.Length -gt $MaxChars) {
                return $sanitized.Substring(0, $MaxChars) + " [truncated]"
            }
            return $sanitized
        }

        function Assert-OpenClawWingetInitialAssetUri {
            param(
                [Parameter(Mandatory = $true)]
                [Uri]$Uri,
                [Parameter(Mandatory = $true)]
                [string]$AssetName,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedReleasePath
            )

            $expectedPath = "$ExpectedReleasePath/$AssetName"
            if (
                -not $Uri.IsAbsoluteUri -or
                [string]$Uri.Scheme -cne "https" -or
                -not [string]::Equals(
                    [string]$Uri.Host,
                    "github.com",
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                [int]$Uri.Port -ne 443 -or
                -not [string]::IsNullOrEmpty([string]$Uri.UserInfo) -or
                -not [string]::IsNullOrEmpty([string]$Uri.Query) -or
                -not [string]::IsNullOrEmpty([string]$Uri.Fragment) -or
                [string]$Uri.AbsolutePath -cne $expectedPath
            ) {
                throw "WinGet asset '$AssetName' does not use its exact immutable github.com HTTPS release URL."
            }
        }

        function Resolve-OpenClawWingetHttpResponse {
            param(
                [Parameter(Mandatory = $true)]
                [Uri]$CurrentUri,
                [Parameter(Mandatory = $true)]
                [int]$StatusCode,
                [AllowNull()]
                [object]$Location,
                [Parameter(Mandatory = $true)]
                [ValidateRange(0, 5)]
                [int]$RedirectCount,
                [Parameter(Mandatory = $true)]
                [string]$AssetName
            )

            if ($StatusCode -ge 200 -and $StatusCode -lt 300) {
                return $null
            }

            if ($StatusCode -notin [int[]]@(301, 302, 303, 307, 308)) {
                throw "WinGet asset '$AssetName' request failed at host '$($CurrentUri.Host)' with HTTP status $StatusCode."
            }
            if ($RedirectCount -ge 5) {
                throw "WinGet asset '$AssetName' exceeded the maximum of 5 redirects at host '$($CurrentUri.Host)'."
            }
            if ($null -eq $Location) {
                throw "WinGet asset '$AssetName' received HTTP status $StatusCode without a Location header at host '$($CurrentUri.Host)'."
            }

            $locationText = if ($Location -is [Uri]) {
                [string]$Location.OriginalString
            } else {
                [string]$Location
            }
            if ([string]::IsNullOrWhiteSpace($locationText)) {
                throw "WinGet asset '$AssetName' received an empty Location header at host '$($CurrentUri.Host)'."
            }

            try {
                $redirectUri = [Uri]::new($CurrentUri, $locationText)
            } catch {
                throw "WinGet asset '$AssetName' received a malformed Location header at host '$($CurrentUri.Host)'."
            }
            $allowedRedirectHosts = [string[]]@(
                "release-assets.githubusercontent.com",
                "objects.githubusercontent.com"
            )
            $hostAllowed = $false
            foreach ($allowedHost in $allowedRedirectHosts) {
                if (
                    [string]::Equals(
                        [string]$redirectUri.Host,
                        $allowedHost,
                        [StringComparison]::OrdinalIgnoreCase
                    )
                ) {
                    $hostAllowed = $true
                    break
                }
            }
            if (
                -not $redirectUri.IsAbsoluteUri -or
                [string]$redirectUri.Scheme -cne "https" -or
                [int]$redirectUri.Port -ne 443 -or
                -not [string]::IsNullOrEmpty([string]$redirectUri.UserInfo) -or
                -not [string]::IsNullOrEmpty([string]$redirectUri.Fragment) -or
                -not $hostAllowed
            ) {
                throw "WinGet asset '$AssetName' received an unsafe redirect target from host '$($CurrentUri.Host)'."
            }
            return $redirectUri
        }

        function Invoke-OpenClawWingetAssetDownload {
            param(
                [Parameter(Mandatory = $true)]
                [Uri]$InitialUri,
                [Parameter(Mandatory = $true)]
                [string]$AssetName,
                [Parameter(Mandatory = $true)]
                [Int64]$ExpectedSize,
                [Parameter(Mandatory = $true)]
                [string]$DestinationPath,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedReleasePath,
                [Parameter(Mandatory = $true)]
                [ValidateRange(1, 1800)]
                [int]$TimeoutSeconds
            )

            Assert-OpenClawWingetInitialAssetUri `
                -Uri $InitialUri `
                -AssetName $AssetName `
                -ExpectedReleasePath $ExpectedReleasePath
            if (Test-Path -LiteralPath $DestinationPath) {
                throw "WinGet asset '$AssetName' destination already exists."
            }

            Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
            $handler = New-Object System.Net.Http.HttpClientHandler
            $client = $null
            $cancellation = $null
            $response = $null
            $request = $null
            $contentStream = $null
            $fileStream = $null
            $downloadFailure = $null
            $restoreFailure = $null
            $priorSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
            $changedGlobalSecurityProtocol = $false
            $currentUri = $InitialUri
            $safeStatus = "none"
            try {
                $handler.AllowAutoRedirect = $false
                $handler.UseCookies = $false
                $handler.PreAuthenticate = $false
                $handler.UseDefaultCredentials = $false
                $handler.Credentials = $null
                if ($null -ne $handler.PSObject.Properties["DefaultProxyCredentials"]) {
                    $handler.DefaultProxyCredentials = $null
                }
                if ($null -ne $handler.PSObject.Properties["SslProtocols"]) {
                    $handler.SslProtocols = [Security.Authentication.SslProtocols]::Tls12
                } else {
                    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                    $changedGlobalSecurityProtocol = $true
                }

                $client = New-Object System.Net.Http.HttpClient($handler)
                $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
                $cancellation = New-Object Threading.CancellationTokenSource
                $cancellation.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))
                $redirectCount = 0
                while ($true) {
                    $request = New-Object System.Net.Http.HttpRequestMessage(
                        [System.Net.Http.HttpMethod]::Get,
                        $currentUri
                    )
                    $request.Headers.Authorization = $null
                    $response = $client.SendAsync(
                        $request,
                        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
                        $cancellation.Token
                    ).GetAwaiter().GetResult()
                    $safeStatus = [string][int]$response.StatusCode
                    $redirectUri = Resolve-OpenClawWingetHttpResponse `
                        -CurrentUri $currentUri `
                        -StatusCode ([int]$response.StatusCode) `
                        -Location $response.Headers.Location `
                        -RedirectCount $redirectCount `
                        -AssetName $AssetName
                    if ($null -ne $redirectUri) {
                        $response.Dispose()
                        $response = $null
                        $request.Dispose()
                        $request = $null
                        $currentUri = $redirectUri
                        $redirectCount++
                        continue
                    }

                    $contentLength = $response.Content.Headers.ContentLength
                    if (
                        $null -ne $contentLength -and
                        [Int64]$contentLength -ne $ExpectedSize
                    ) {
                        throw "WinGet asset '$AssetName' response length did not match the pinned size."
                    }

                    $contentStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    $fileStream = [IO.File]::Open(
                        $DestinationPath,
                        [IO.FileMode]::CreateNew,
                        [IO.FileAccess]::Write,
                        [IO.FileShare]::None
                    )
                    $buffer = New-Object "byte[]" (1024 * 1024)
                    [Int64]$totalBytes = 0
                    while (($read = $contentStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $totalBytes += [Int64]$read
                        if ($totalBytes -gt $ExpectedSize) {
                            throw "WinGet asset '$AssetName' exceeded the pinned size while streaming."
                        }
                        $fileStream.Write($buffer, 0, $read)
                    }
                    $fileStream.Flush()
                    if ($totalBytes -ne $ExpectedSize) {
                        throw "WinGet asset '$AssetName' stream length did not match the pinned size."
                    }
                    break
                }
            } catch {
                $downloadFailure = $_
            } finally {
                foreach ($disposable in @(
                    $fileStream,
                    $contentStream,
                    $response,
                    $request,
                    $cancellation,
                    $client,
                    $handler
                )) {
                    if ($null -ne $disposable) {
                        try {
                            $disposable.Dispose()
                        } catch {
                        }
                    }
                }
                if ($changedGlobalSecurityProtocol) {
                    try {
                        [Net.ServicePointManager]::SecurityProtocol = $priorSecurityProtocol
                    } catch {
                        $restoreFailure = $_
                    }
                }
            }

            if ($null -ne $restoreFailure) {
                throw "WinGet asset '$AssetName' download could not restore the process TLS policy."
            }
            if ($null -ne $downloadFailure) {
                $failureType = $downloadFailure.Exception.GetType().FullName
                throw "WinGet asset '$AssetName' download failed at host '$($currentUri.Host)' with HTTP status $safeStatus ($failureType)."
            }
        }

        function Assert-OpenClawWingetCatalogInitialUri {
            param(
                [Parameter(Mandatory = $true)]
                [Uri]$Uri
            )

            $officialUri = "https://cdn.winget.microsoft.com/cache/source2.msix"
            if (
                -not $Uri.IsAbsoluteUri -or
                [string]$Uri.OriginalString -cne $officialUri -or
                [string]$Uri.Scheme -cne "https" -or
                -not [string]::Equals(
                    [string]$Uri.Host,
                    "cdn.winget.microsoft.com",
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                [int]$Uri.Port -ne 443 -or
                -not [string]::IsNullOrEmpty([string]$Uri.UserInfo) -or
                -not [string]::IsNullOrEmpty([string]$Uri.Query) -or
                -not [string]::IsNullOrEmpty([string]$Uri.Fragment) -or
                [string]$Uri.AbsolutePath -cne "/cache/source2.msix"
            ) {
                throw "WinGet source catalog does not use the exact official Microsoft HTTPS URL."
            }
        }

        function Resolve-OpenClawWingetCatalogHttpResponse {
            param(
                [Parameter(Mandatory = $true)]
                [Uri]$CurrentUri,
                [Parameter(Mandatory = $true)]
                [int]$StatusCode,
                [AllowNull()]
                [object]$Location,
                [Parameter(Mandatory = $true)]
                [ValidateRange(0, 5)]
                [int]$RedirectCount
            )

            if ($StatusCode -ge 200 -and $StatusCode -lt 300) {
                return $null
            }
            if ($StatusCode -notin [int[]]@(301, 302, 303, 307, 308)) {
                throw "WinGet source catalog request failed at host '$($CurrentUri.Host)' with HTTP status $StatusCode."
            }
            if ($RedirectCount -ge 5) {
                throw "WinGet source catalog exceeded the maximum of 5 redirects at host '$($CurrentUri.Host)'."
            }
            if ($null -eq $Location) {
                throw "WinGet source catalog received HTTP status $StatusCode without a Location header at host '$($CurrentUri.Host)'."
            }

            $locationText = if ($Location -is [Uri]) {
                [string]$Location.OriginalString
            } else {
                [string]$Location
            }
            if ([string]::IsNullOrWhiteSpace($locationText)) {
                throw "WinGet source catalog received an empty Location header at host '$($CurrentUri.Host)'."
            }
            try {
                $redirectUri = [Uri]::new($CurrentUri, $locationText)
            } catch {
                throw "WinGet source catalog received a malformed Location header at host '$($CurrentUri.Host)'."
            }
            if (
                -not $redirectUri.IsAbsoluteUri -or
                [string]$redirectUri.Scheme -cne "https" -or
                -not [string]::Equals(
                    [string]$redirectUri.Host,
                    "cdn.winget.microsoft.com",
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                [int]$redirectUri.Port -ne 443 -or
                -not [string]::IsNullOrEmpty([string]$redirectUri.UserInfo) -or
                -not [string]::IsNullOrEmpty([string]$redirectUri.Fragment)
            ) {
                throw "WinGet source catalog received an unsafe redirect target from host '$($CurrentUri.Host)'."
            }
            return $redirectUri
        }

        function Assert-OpenClawWingetCatalogDownloadLength {
            param(
                [AllowNull()]
                [object]$ContentLength,
                [Parameter(Mandatory = $true)]
                [Int64]$ActualSize,
                [Parameter(Mandatory = $true)]
                [Int64]$MaximumSize,
                [switch]$Completed
            )

            if ($MaximumSize -le 0) {
                throw "WinGet source catalog maximum download size is invalid."
            }
            if ($null -ne $ContentLength) {
                [Int64]$declaredSize = [Int64]$ContentLength
                if ($declaredSize -le 0) {
                    throw "WinGet source catalog Content-Length must be nonzero."
                }
                if ($declaredSize -gt $MaximumSize) {
                    throw "WinGet source catalog Content-Length exceeds the maximum download size."
                }
            }
            if ($Completed) {
                if ($ActualSize -le 0) {
                    throw "WinGet source catalog download is empty."
                }
                if ($ActualSize -gt $MaximumSize) {
                    throw "WinGet source catalog download exceeds the maximum size."
                }
                if (
                    $null -ne $ContentLength -and
                    $ActualSize -ne [Int64]$ContentLength
                ) {
                    throw "WinGet source catalog stream length does not match Content-Length."
                }
            }
        }

        function Invoke-OpenClawWingetCatalogDownload {
            param(
                [Parameter(Mandatory = $true)]
                [Uri]$InitialUri,
                [Parameter(Mandatory = $true)]
                [string]$DestinationPath,
                [Parameter(Mandatory = $true)]
                [ValidateRange(1, 67108864)]
                [Int64]$MaximumSize,
                [Parameter(Mandatory = $true)]
                [ValidateRange(1, 1800)]
                [int]$TimeoutSeconds
            )

            Assert-OpenClawWingetCatalogInitialUri -Uri $InitialUri
            if (Test-Path -LiteralPath $DestinationPath) {
                throw "WinGet source catalog destination already exists."
            }

            Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
            $handler = New-Object System.Net.Http.HttpClientHandler
            $client = $null
            $cancellation = $null
            $response = $null
            $request = $null
            $contentStream = $null
            $fileStream = $null
            $downloadFailure = $null
            $restoreFailure = $null
            $downloadTimedOut = $false
            $disposeFailures = New-Object "Collections.Generic.List[string]"
            $priorSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
            $changedGlobalSecurityProtocol = $false
            $currentUri = $InitialUri
            $safeStatus = "none"
            try {
                $handler.AllowAutoRedirect = $false
                $handler.UseCookies = $false
                $handler.PreAuthenticate = $false
                $handler.UseDefaultCredentials = $false
                $handler.Credentials = $null
                if ($null -ne $handler.PSObject.Properties["DefaultProxyCredentials"]) {
                    $handler.DefaultProxyCredentials = $null
                }
                if ($null -ne $handler.PSObject.Properties["SslProtocols"]) {
                    $handler.SslProtocols = [Security.Authentication.SslProtocols]::Tls12
                } else {
                    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                    $changedGlobalSecurityProtocol = $true
                }

                $client = New-Object System.Net.Http.HttpClient($handler)
                $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
                $cancellation = New-Object Threading.CancellationTokenSource
                $cancellation.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))
                $redirectCount = 0
                while ($true) {
                    $request = New-Object System.Net.Http.HttpRequestMessage(
                        [System.Net.Http.HttpMethod]::Get,
                        $currentUri
                    )
                    $request.Headers.Authorization = $null
                    $response = $client.SendAsync(
                        $request,
                        [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
                        $cancellation.Token
                    ).GetAwaiter().GetResult()
                    $safeStatus = [string][int]$response.StatusCode
                    $redirectUri = Resolve-OpenClawWingetCatalogHttpResponse `
                        -CurrentUri $currentUri `
                        -StatusCode ([int]$response.StatusCode) `
                        -Location $response.Headers.Location `
                        -RedirectCount $redirectCount
                    if ($null -ne $redirectUri) {
                        $response.Dispose()
                        $response = $null
                        $request.Dispose()
                        $request = $null
                        $currentUri = $redirectUri
                        $redirectCount++
                        continue
                    }

                    $contentLength = $response.Content.Headers.ContentLength
                    Assert-OpenClawWingetCatalogDownloadLength `
                        -ContentLength $contentLength `
                        -ActualSize 0 `
                        -MaximumSize $MaximumSize
                    $contentStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                    $fileStream = [IO.File]::Open(
                        $DestinationPath,
                        [IO.FileMode]::CreateNew,
                        [IO.FileAccess]::Write,
                        [IO.FileShare]::None
                    )
                    $buffer = New-Object "byte[]" (1024 * 1024)
                    [Int64]$totalBytes = 0
                    while (
                        ($read = $contentStream.ReadAsync(
                            $buffer,
                            0,
                            $buffer.Length,
                            $cancellation.Token
                        ).GetAwaiter().GetResult()) -gt 0
                    ) {
                        $totalBytes += [Int64]$read
                        if ($totalBytes -gt $MaximumSize) {
                            throw "WinGet source catalog exceeded the maximum size while streaming."
                        }
                        $fileStream.Write($buffer, 0, $read)
                    }
                    $fileStream.Flush()
                    Assert-OpenClawWingetCatalogDownloadLength `
                        -ContentLength $contentLength `
                        -ActualSize $totalBytes `
                        -MaximumSize $MaximumSize `
                        -Completed
                    break
                }
            } catch {
                $downloadFailure = $_
                if (
                    $null -ne $cancellation -and
                    [bool]$cancellation.IsCancellationRequested
                ) {
                    $downloadTimedOut = $true
                }
            } finally {
                foreach ($disposable in @(
                    $fileStream,
                    $contentStream,
                    $response,
                    $request,
                    $cancellation,
                    $client,
                    $handler
                )) {
                    if ($null -ne $disposable) {
                        try {
                            $disposable.Dispose()
                        } catch {
                            $disposeFailures.Add($_.Exception.GetType().FullName)
                        }
                    }
                }
                if ($changedGlobalSecurityProtocol) {
                    try {
                        [Net.ServicePointManager]::SecurityProtocol = $priorSecurityProtocol
                    } catch {
                        $restoreFailure = $_
                    }
                }
            }

            if ($disposeFailures.Count -gt 0) {
                throw "WinGet source catalog download could not dispose its HTTP resources."
            }
            if ($null -ne $restoreFailure) {
                throw "WinGet source catalog download could not restore the process TLS policy."
            }
            if ($downloadTimedOut) {
                throw "WinGet source catalog download timed out after the bounded $TimeoutSeconds seconds at host '$($currentUri.Host)'."
            }
            if ($null -ne $downloadFailure) {
                $failureType = $downloadFailure.Exception.GetType().FullName
                throw "WinGet source catalog download failed at host '$($currentUri.Host)' with HTTP status $safeStatus ($failureType)."
            }
        }

        function Assert-OpenClawWingetFile {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [Parameter(Mandatory = $true)]
                [Int64]$ExpectedSize,
                [Parameter(Mandatory = $true)]
                [ValidatePattern("^[0-9a-f]{64}$")]
                [string]$ExpectedSha256,
                [Parameter(Mandatory = $true)]
                [string]$AssetName
            )

            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                throw "Pinned WinGet file '$AssetName' is missing."
            }
            $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
            if ([Int64]$file.Length -ne $ExpectedSize) {
                throw "Pinned WinGet file '$AssetName' has an unexpected size."
            }
            $actualHash = (
                Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop
            ).Hash.ToLowerInvariant()
            if ($actualHash -cne $ExpectedSha256) {
                throw "Pinned WinGet file '$AssetName' has an unexpected SHA256."
            }
        }

        function Assert-OpenClawWingetSignature {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [string]$AssetName
            )

            $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
            if ([string]$signature.Status -cne "Valid") {
                throw "Pinned WinGet file '$AssetName' does not have a Valid Authenticode signature."
            }
            if (
                $null -eq $signature.SignerCertificate -or
                [string]$signature.SignerCertificate.Subject -cne $ExpectedPublisher
            ) {
                throw "Pinned WinGet file '$AssetName' does not have the exact Microsoft signer subject."
            }
        }

        function Assert-OpenClawWingetCatalogSignature {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher
            )

            $signature = Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
            if ([string]$signature.Status -cne "Valid") {
                throw "WinGet source catalog does not have a Valid Authenticode signature."
            }
            if (
                $null -eq $signature.SignerCertificate -or
                [string]$signature.SignerCertificate.Subject -cne $ExpectedPublisher
            ) {
                throw "WinGet source catalog does not have the exact Microsoft signer subject."
            }
        }

        function Assert-OpenClawWingetSafeZipEntryName {
            param(
                [Parameter(Mandatory = $true)]
                [string]$EntryName,
                [Parameter(Mandatory = $true)]
                [string]$ArchiveName
            )

            if ([string]::IsNullOrWhiteSpace($EntryName)) {
                throw "Archive '$ArchiveName' contains an empty entry name."
            }
            $normalized = $EntryName.Replace("/", "\")
            $isDirectory = $normalized.EndsWith("\", [StringComparison]::Ordinal)
            $trimmed = if ($isDirectory) {
                $normalized.TrimEnd("\")
            } else {
                $normalized
            }
            if (
                [string]::IsNullOrWhiteSpace($trimmed) -or
                [IO.Path]::IsPathRooted($trimmed) -or
                $trimmed.StartsWith("\", [StringComparison]::Ordinal)
            ) {
                throw "Archive '$ArchiveName' contains a rooted or empty entry path."
            }
            $segments = [string[]]$trimmed.Split([char[]]@("\", "/"))
            foreach ($segment in $segments) {
                if (
                    [string]::IsNullOrEmpty($segment) -or
                    $segment -ceq "." -or
                    $segment -ceq ".." -or
                    $segment.Contains(":") -or
                    $segment.IndexOf([char]0) -ge 0
                ) {
                    throw "Archive '$ArchiveName' contains an unsafe entry path."
                }
            }
            return $trimmed
        }

        function New-OpenClawWingetZipEntryIndex {
            param(
                [Parameter(Mandatory = $true)]
                [System.IO.Compression.ZipArchive]$Archive,
                [Parameter(Mandatory = $true)]
                [string]$ArchiveName
            )

            $entries = [Collections.Generic.Dictionary[string, object]]::new(
                [StringComparer]::OrdinalIgnoreCase
            )
            foreach ($entry in $Archive.Entries) {
                $normalized = Assert-OpenClawWingetSafeZipEntryName `
                    -EntryName ([string]$entry.FullName) `
                    -ArchiveName $ArchiveName
                if ($entries.ContainsKey($normalized)) {
                    throw "Archive '$ArchiveName' contains a duplicate entry path."
                }
                $entries.Add($normalized, $entry)
            }
            return $entries
        }

        function ConvertFrom-OpenClawWingetXmlBytes {
            param(
                [Parameter(Mandatory = $true)]
                [byte[]]$Bytes,
                [Parameter(Mandatory = $true)]
                [string]$DocumentName
            )

            $settings = New-Object System.Xml.XmlReaderSettings
            $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $settings.MaxCharactersInDocument = 4MB
            $stream = New-Object IO.MemoryStream(, $Bytes)
            $reader = $null
            try {
                $reader = [Xml.XmlReader]::Create($stream, $settings)
                $document = New-Object Xml.XmlDocument
                $document.PreserveWhitespace = $false
                $document.XmlResolver = $null
                $document.Load($reader)
                return $document
            } catch {
                throw "Pinned WinGet XML '$DocumentName' could not be parsed safely."
            } finally {
                if ($null -ne $reader) {
                    $reader.Dispose()
                }
                $stream.Dispose()
            }
        }

        function Read-OpenClawWingetZipXml {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ArchivePath,
                [Parameter(Mandatory = $true)]
                [string]$EntryName,
                [Parameter(Mandatory = $true)]
                [string]$ArchiveName
            )

            Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
            $archiveStream = $null
            $archive = $null
            $entryStream = $null
            $memory = $null
            try {
                $archiveStream = [IO.File]::Open(
                    $ArchivePath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read
                )
                $archive = New-Object System.IO.Compression.ZipArchive(
                    $archiveStream,
                    [System.IO.Compression.ZipArchiveMode]::Read,
                    $false
                )
                $entries = New-OpenClawWingetZipEntryIndex `
                    -Archive $archive `
                    -ArchiveName $ArchiveName
                $normalizedEntryName = $EntryName.Replace("/", "\")
                if (-not $entries.ContainsKey($normalizedEntryName)) {
                    throw "Archive '$ArchiveName' is missing required entry '$EntryName'."
                }
                $entry = $entries[$normalizedEntryName]
                if (
                    ([string]$entry.FullName).Replace("/", "\") -cne
                    $normalizedEntryName
                ) {
                    throw "Archive '$ArchiveName' does not use the exact required entry path '$EntryName'."
                }
                if ([Int64]$entry.Length -gt 4MB) {
                    throw "Archive '$ArchiveName' XML entry '$EntryName' exceeds the bounded size."
                }

                $entryStream = $entry.Open()
                $memory = New-Object IO.MemoryStream
                $buffer = New-Object "byte[]" 65536
                [Int64]$totalBytes = 0
                while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $totalBytes += [Int64]$read
                    if ($totalBytes -gt 4MB) {
                        throw "Archive '$ArchiveName' XML entry '$EntryName' exceeded the bounded size while reading."
                    }
                    $memory.Write($buffer, 0, $read)
                }
                return ConvertFrom-OpenClawWingetXmlBytes `
                    -Bytes $memory.ToArray() `
                    -DocumentName "$ArchiveName/$EntryName"
            } finally {
                foreach ($disposable in @($memory, $entryStream, $archive, $archiveStream)) {
                    if ($null -ne $disposable) {
                        $disposable.Dispose()
                    }
                }
            }
        }

        function Expand-OpenClawWingetDependencyPackages {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ArchivePath,
                [Parameter(Mandatory = $true)]
                [string]$DestinationRoot,
                [Parameter(Mandatory = $true)]
                [object[]]$ExpectedDependencies
            )

            Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
            if (Test-Path -LiteralPath $DestinationRoot) {
                throw "WinGet dependency extraction destination already exists."
            }
            [IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
            $canonicalRoot = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd("\") + "\"
            $archiveStream = $null
            $archive = $null
            try {
                $archiveStream = [IO.File]::Open(
                    $ArchivePath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read
                )
                $archive = New-Object System.IO.Compression.ZipArchive(
                    $archiveStream,
                    [System.IO.Compression.ZipArchiveMode]::Read,
                    $false
                )
                $entries = New-OpenClawWingetZipEntryIndex `
                    -Archive $archive `
                    -ArchiveName "DesktopAppInstaller_Dependencies.zip"
                $results = New-Object "Collections.Generic.List[object]"
                foreach ($dependency in $ExpectedDependencies) {
                    $relativePath = ([string]$dependency.RelativePath).Replace("/", "\")
                    if (-not $entries.ContainsKey($relativePath)) {
                        throw "Dependency archive is missing pinned x64 package '$relativePath'."
                    }
                    $entry = $entries[$relativePath]
                    if (
                        ([string]$entry.FullName).Replace("/", "\") -cne
                        $relativePath
                    ) {
                        throw "Dependency archive does not use exact pinned x64 package path '$relativePath'."
                    }
                    if ([Int64]$entry.Length -ne [Int64]$dependency.Size) {
                        throw "Pinned dependency '$($dependency.Name)' has an unexpected archive entry size."
                    }
                    $destinationPath = [IO.Path]::GetFullPath(
                        (Join-Path $DestinationRoot $relativePath)
                    )
                    if (
                        -not $destinationPath.StartsWith(
                            $canonicalRoot,
                            [StringComparison]::OrdinalIgnoreCase
                        )
                    ) {
                        throw "Pinned dependency extraction escaped its nonce temporary root."
                    }
                    $destinationDirectory = Split-Path -Parent $destinationPath
                    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
                    $entryStream = $null
                    $destinationStream = $null
                    try {
                        $entryStream = $entry.Open()
                        $destinationStream = [IO.File]::Open(
                            $destinationPath,
                            [IO.FileMode]::CreateNew,
                            [IO.FileAccess]::Write,
                            [IO.FileShare]::None
                        )
                        $buffer = New-Object "byte[]" 1048576
                        [Int64]$written = 0
                        while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $written += [Int64]$read
                            if ($written -gt [Int64]$dependency.Size) {
                                throw "Pinned dependency '$($dependency.Name)' exceeded its pinned size while extracting."
                            }
                            $destinationStream.Write($buffer, 0, $read)
                        }
                        $destinationStream.Flush()
                        if ($written -ne [Int64]$dependency.Size) {
                            throw "Pinned dependency '$($dependency.Name)' extraction length did not match its pinned size."
                        }
                    } finally {
                        foreach ($disposable in @($destinationStream, $entryStream)) {
                            if ($null -ne $disposable) {
                                $disposable.Dispose()
                            }
                        }
                    }
                    $results.Add([pscustomobject][ordered]@{
                        Name = [string]$dependency.Name
                        Version = [string]$dependency.Version
                        RelativePath = [string]$dependency.RelativePath
                        Size = [Int64]$dependency.Size
                        Sha256 = [string]$dependency.Sha256
                        LocalPath = $destinationPath
                    })
                }
                return [object[]]$results.ToArray()
            } catch {
                throw
            } finally {
                foreach ($disposable in @($archive, $archiveStream)) {
                    if ($null -ne $disposable) {
                        $disposable.Dispose()
                    }
                }
            }
        }

        function Expand-OpenClawWingetBundlePayload {
            param(
                [Parameter(Mandatory = $true)]
                [string]$BundlePath,
                [Parameter(Mandatory = $true)]
                [string]$PayloadEntryName,
                [Parameter(Mandatory = $true)]
                [Int64]$ExpectedSize,
                [Parameter(Mandatory = $true)]
                [string]$DestinationPath
            )

            Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
            $archiveStream = $null
            $archive = $null
            $entryStream = $null
            $destinationStream = $null
            try {
                $archiveStream = [IO.File]::Open(
                    $BundlePath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read
                )
                $archive = New-Object System.IO.Compression.ZipArchive(
                    $archiveStream,
                    [System.IO.Compression.ZipArchiveMode]::Read,
                    $false
                )
                $entries = New-OpenClawWingetZipEntryIndex `
                    -Archive $archive `
                    -ArchiveName "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
                $normalizedEntryName = $PayloadEntryName.Replace("/", "\")
                if (-not $entries.ContainsKey($normalizedEntryName)) {
                    throw "App Installer bundle is missing the exact pinned x64 payload."
                }
                $entry = $entries[$normalizedEntryName]
                if (
                    ([string]$entry.FullName).Replace("/", "\") -cne
                    $normalizedEntryName
                ) {
                    throw "App Installer bundle does not use the exact pinned x64 payload path."
                }
                if ([Int64]$entry.Length -ne $ExpectedSize) {
                    throw "App Installer x64 payload has an unexpected bundle entry size."
                }
                if (Test-Path -LiteralPath $DestinationPath) {
                    throw "App Installer payload extraction destination already exists."
                }
                $entryStream = $entry.Open()
                $destinationStream = [IO.File]::Open(
                    $DestinationPath,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None
                )
                $buffer = New-Object "byte[]" 1048576
                [Int64]$written = 0
                while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += [Int64]$read
                    if ($written -gt $ExpectedSize) {
                        throw "App Installer x64 payload exceeded its pinned size while extracting."
                    }
                    $destinationStream.Write($buffer, 0, $read)
                }
                $destinationStream.Flush()
                if ($written -ne $ExpectedSize) {
                    throw "App Installer x64 payload extraction length did not match its pinned size."
                }
                return $DestinationPath
            } finally {
                foreach ($disposable in @(
                    $destinationStream,
                    $entryStream,
                    $archive,
                    $archiveStream
                )) {
                    if ($null -ne $disposable) {
                        $disposable.Dispose()
                    }
                }
            }
        }

        function Assert-OpenClawWingetDependencyDescriptor {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [Parameter(Mandatory = $true)]
                [object[]]$ExpectedDependencies
            )

            try {
                $descriptor = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) |
                    ConvertFrom-Json -ErrorAction Stop
            } catch {
                throw "Pinned WinGet dependency descriptor could not be parsed."
            }
            $topLevelProperties = @($descriptor.PSObject.Properties.Name)
            if (
                $topLevelProperties.Count -ne 1 -or
                [string]$topLevelProperties[0] -cne "Dependencies"
            ) {
                throw "Pinned WinGet dependency descriptor has unexpected top-level fields."
            }
            $actualDependencies = @($descriptor.Dependencies)
            if ($actualDependencies.Count -ne $ExpectedDependencies.Count) {
                throw "Pinned WinGet dependency descriptor has an unexpected dependency count."
            }
            for ($index = 0; $index -lt $ExpectedDependencies.Count; $index++) {
                $actual = $actualDependencies[$index]
                $expected = $ExpectedDependencies[$index]
                $propertyNames = @($actual.PSObject.Properties.Name)
                if (
                    $propertyNames.Count -ne 2 -or
                    -not ($propertyNames -ccontains "Name") -or
                    -not ($propertyNames -ccontains "Version") -or
                    [string]$actual.Name -cne [string]$expected.Name -or
                    [string]$actual.Version -cne [string]$expected.Version
                ) {
                    throw "Pinned WinGet dependency descriptor entry $index does not match the exact ordered dependency."
                }
            }
        }

        function Assert-OpenClawAppxManifestIdentity {
            param(
                [Parameter(Mandatory = $true)]
                [Xml.XmlDocument]$Document,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedName,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedVersion,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [string]$DocumentName
            )

            if (
                $null -eq $Document.DocumentElement -or
                [string]$Document.DocumentElement.LocalName -cne "Package"
            ) {
                throw "Appx manifest '$DocumentName' does not have a Package root."
            }
            $identities = @(
                Get-OpenClawWingetDirectXmlChildren `
                    -Parent $Document.DocumentElement `
                    -LocalName "Identity"
            )
            if ($identities.Count -ne 1) {
                throw "Appx manifest '$DocumentName' must have exactly one direct Identity."
            }
            $identity = [Xml.XmlElement]$identities[0]
            if (
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Name") -cne $ExpectedName -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Version") -cne $ExpectedVersion -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Publisher") -cne $ExpectedPublisher -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "ProcessorArchitecture") -cne "x64"
            ) {
                throw "Appx manifest '$DocumentName' does not match its exact pinned x64 identity."
            }
        }

        function Get-OpenClawWingetValidNonzeroVersion {
            param(
                [Parameter(Mandatory = $true)]
                [string]$VersionText,
                [Parameter(Mandatory = $true)]
                [string]$Subject
            )

            [Version]$parsedVersion = $null
            if (-not [Version]::TryParse($VersionText, [ref]$parsedVersion)) {
                throw "$Subject does not contain a valid System.Version."
            }
            if (
                $parsedVersion.Major -eq 0 -and
                $parsedVersion.Minor -eq 0 -and
                $parsedVersion.Build -le 0 -and
                $parsedVersion.Revision -le 0
            ) {
                throw "$Subject contains a zero version."
            }
            return $parsedVersion
        }

        function Assert-OpenClawWingetCatalogManifest {
            param(
                [Parameter(Mandatory = $true)]
                [Xml.XmlDocument]$Document,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher
            )

            if (
                $null -eq $Document.DocumentElement -or
                [string]$Document.DocumentElement.LocalName -cne "Package"
            ) {
                throw "WinGet source catalog manifest does not have a Package root."
            }
            $identities = @(
                Get-OpenClawWingetDirectXmlChildren `
                    -Parent $Document.DocumentElement `
                    -LocalName "Identity"
            )
            if ($identities.Count -ne 1) {
                throw "WinGet source catalog manifest must have exactly one direct Identity."
            }
            $identity = [Xml.XmlElement]$identities[0]
            if (
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Name") -cne "Microsoft.Winget.Source" -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Publisher") -cne $ExpectedPublisher -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "ProcessorArchitecture") -cne "neutral"
            ) {
                throw "WinGet source catalog manifest does not match the exact name, publisher, and neutral architecture."
            }
            $versionText = Get-OpenClawWingetXmlAttribute `
                -Element $identity `
                -Name "Version"
            $version = Get-OpenClawWingetValidNonzeroVersion `
                -VersionText $versionText `
                -Subject "WinGet source catalog manifest"
            return $version.ToString()
        }

        function Assert-OpenClawWingetBundleManifest {
            param(
                [Parameter(Mandatory = $true)]
                [Xml.XmlDocument]$Document,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher
            )

            if (
                $null -eq $Document.DocumentElement -or
                [string]$Document.DocumentElement.LocalName -cne "Bundle"
            ) {
                throw "App Installer bundle manifest does not have a Bundle root."
            }
            $identities = @(
                Get-OpenClawWingetDirectXmlChildren `
                    -Parent $Document.DocumentElement `
                    -LocalName "Identity"
            )
            if ($identities.Count -ne 1) {
                throw "App Installer bundle manifest must have exactly one direct Identity."
            }
            $identity = [Xml.XmlElement]$identities[0]
            if (
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Name") -cne "Microsoft.DesktopAppInstaller" -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Publisher") -cne $ExpectedPublisher -or
                (Get-OpenClawWingetXmlAttribute -Element $identity -Name "Version") -cne "2026.623.1704.0"
            ) {
                throw "App Installer bundle manifest identity does not match the exact pin."
            }

            $nonStubX64Packages = @(
                $Document.SelectNodes("//*[local-name()='Package']") |
                    Where-Object {
                        $_ -is [Xml.XmlElement] -and
                        [string]$_.GetAttribute("Architecture") -ceq "x64" -and
                        ([string]$_.GetAttribute("FileName")).IndexOf(
                            "Stub",
                            [StringComparison]::OrdinalIgnoreCase
                        ) -lt 0
                    }
            )
            if ($nonStubX64Packages.Count -ne 1) {
                throw "App Installer bundle manifest must contain exactly one nonstub x64 package."
            }
            $payload = [Xml.XmlElement]$nonStubX64Packages[0]
            if (
                (Get-OpenClawWingetXmlAttribute -Element $payload -Name "FileName") -cne "AppInstaller_x64.msix" -or
                (Get-OpenClawWingetXmlAttribute -Element $payload -Name "Version") -cne "1.29.280.0" -or
                (Get-OpenClawWingetXmlAttribute -Element $payload -Name "Architecture") -cne "x64" -or
                (Get-OpenClawWingetXmlAttribute -Element $payload -Name "Type") -cne "application"
            ) {
                throw "App Installer bundle manifest x64 payload does not match the exact pin."
            }
            return "AppInstaller_x64.msix"
        }

        function Assert-OpenClawWingetPayloadManifest {
            param(
                [Parameter(Mandatory = $true)]
                [Xml.XmlDocument]$Document,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [object[]]$ExpectedDependencies
            )

            Assert-OpenClawAppxManifestIdentity `
                -Document $Document `
                -ExpectedName "Microsoft.DesktopAppInstaller" `
                -ExpectedVersion "1.29.280.0" `
                -ExpectedPublisher $ExpectedPublisher `
                -DocumentName "AppInstaller_x64.msix"
            $dependencyNodes = @(
                $Document.SelectNodes(
                    "/*[local-name()='Package']/*[local-name()='Dependencies']/*[local-name()='PackageDependency']"
                )
            )
            if ($dependencyNodes.Count -ne $ExpectedDependencies.Count) {
                throw "App Installer payload manifest has an unexpected package dependency count."
            }
            foreach ($expected in $ExpectedDependencies) {
                $matchingNodes = @(
                    $dependencyNodes |
                        Where-Object {
                            $_ -is [Xml.XmlElement] -and
                            [string]$_.GetAttribute("Name") -ceq [string]$expected.Name
                        }
                )
                if ($matchingNodes.Count -ne 1) {
                    throw (
                        "App Installer payload manifest must contain exactly one dependency named '{0}'." -f
                        [string]$expected.Name
                    )
                }
                $node = [Xml.XmlElement]$matchingNodes[0]
                if (
                    (Get-OpenClawWingetXmlAttribute -Element $node -Name "MinVersion") -cne [string]$expected.Version -or
                    (Get-OpenClawWingetXmlAttribute -Element $node -Name "Publisher") -cne $ExpectedPublisher
                ) {
                    throw (
                        "App Installer payload manifest dependency '{0}' does not match the exact pin." -f
                        [string]$expected.Name
                    )
                }
            }

            $wingetApplications = @(
                $Document.SelectNodes("//*[local-name()='Application']") |
                    Where-Object {
                        $_ -is [Xml.XmlElement] -and
                        [string]$_.GetAttribute("Id") -ceq "winget"
                    }
            )
            if ($wingetApplications.Count -ne 1) {
                throw "App Installer payload manifest must have exactly one winget application."
            }
            $wingetApplication = [Xml.XmlElement]$wingetApplications[0]
            if (
                (Get-OpenClawWingetXmlAttribute -Element $wingetApplication -Name "Executable") -cne "winget.exe"
            ) {
                throw "App Installer winget application executable does not match the exact pin."
            }
            $aliases = @(
                $wingetApplication.SelectNodes(".//*[local-name()='ExecutionAlias']")
            )
            if (
                $aliases.Count -ne 1 -or
                (Get-OpenClawWingetXmlAttribute `
                    -Element ([Xml.XmlElement]$aliases[0]) `
                    -Name "Alias") -cne "winget.exe"
            ) {
                throw "App Installer winget application alias does not match the exact pin."
            }
        }

        function Assert-OpenClawWingetCurrentPackageIdentity {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Package,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedName,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedVersion,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPackageFamilyName
            )

            if (
                [string]$Package.Name -cne $ExpectedName -or
                [string]$Package.Publisher -cne $ExpectedPublisher -or
                -not [string]::Equals(
                    [string]$Package.Architecture,
                    "X64",
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                [string]$Package.PackageFamilyName -cne $ExpectedPackageFamilyName -or
                [string]$Package.Version -cne $ExpectedVersion
            ) {
                throw "Current-user Appx registration '$ExpectedName' does not match the exact pinned identity."
            }
        }

        function Get-OpenClawWingetCurrentMainPackageState {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPackageFamilyName
            )

            $packages = @(
                Get-AppxPackage `
                    -Name "Microsoft.DesktopAppInstaller" `
                    -ErrorAction Stop
            )
            if ($packages.Count -gt 1) {
                throw "Current user has multiple Microsoft.DesktopAppInstaller registrations."
            }
            if ($packages.Count -eq 0) {
                return [pscustomobject][ordered]@{
                    State = "missing"
                    Package = $null
                }
            }

            $package = $packages[0]
            if (
                [string]$package.Name -cne "Microsoft.DesktopAppInstaller" -or
                [string]$package.Publisher -cne $ExpectedPublisher -or
                -not [string]::Equals(
                    [string]$package.Architecture,
                    "X64",
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                [string]$package.PackageFamilyName -cne $ExpectedPackageFamilyName
            ) {
                throw "Existing current-user Microsoft.DesktopAppInstaller has an unexpected identity."
            }
            $actualVersion = [Version][string]$package.Version
            $pinnedVersion = [Version]"1.29.280.0"
            if ($actualVersion -gt $pinnedVersion) {
                throw "Existing current-user Microsoft.DesktopAppInstaller is newer than the reproducible pin."
            }
            return [pscustomobject][ordered]@{
                State = if ($actualVersion -eq $pinnedVersion) { "exact" } else { "older" }
                Package = $package
            }
        }

        function Get-OpenClawWingetDependencyInstallState {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Dependency,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher
            )

            $packages = @(
                Get-AppxPackage -Name ([string]$Dependency.Name) -ErrorAction Stop
            )
            $x64Packages = @(
                $packages |
                    Where-Object {
                        [string]::Equals(
                            [string]$_.Architecture,
                            "X64",
                            [StringComparison]::OrdinalIgnoreCase
                        )
                    }
            )
            if ($x64Packages.Count -gt 1) {
                throw "Current user has multiple x64 registrations for dependency '$($Dependency.Name)'."
            }
            if ($x64Packages.Count -eq 0) {
                return "install"
            }
            $package = $x64Packages[0]
            $expectedFamily = "$($Dependency.Name)_8wekyb3d8bbwe"
            if (
                [string]$package.Name -cne [string]$Dependency.Name -or
                [string]$package.Publisher -cne $ExpectedPublisher -or
                [string]$package.PackageFamilyName -cne $expectedFamily
            ) {
                throw "Existing x64 dependency '$($Dependency.Name)' has an unexpected identity."
            }
            $actualVersion = [Version][string]$package.Version
            $pinnedVersion = [Version][string]$Dependency.Version
            if ($actualVersion -gt $pinnedVersion) {
                throw "Existing x64 dependency '$($Dependency.Name)' is newer than the reproducible pin."
            }
            if ($actualVersion -eq $pinnedVersion) {
                return "skip"
            }
            return "install"
        }

        function Wait-OpenClawWingetDependencyRegistration {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Dependency,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [ValidateRange(1, 120)]
                [int]$RetryCount = 30,
                [ValidateRange(0, 5000)]
                [int]$DelayMilliseconds = 1000
            )

            for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
                $packages = @(
                    Get-AppxPackage -Name ([string]$Dependency.Name) -ErrorAction Stop |
                        Where-Object {
                            [string]::Equals(
                                [string]$_.Architecture,
                                "X64",
                                [StringComparison]::OrdinalIgnoreCase
                            )
                        }
                )
                if ($packages.Count -gt 1) {
                    throw "Current user has multiple x64 registrations for dependency '$($Dependency.Name)'."
                }
                if ($packages.Count -eq 1) {
                    Assert-OpenClawWingetCurrentPackageIdentity `
                        -Package $packages[0] `
                        -ExpectedName ([string]$Dependency.Name) `
                        -ExpectedVersion ([string]$Dependency.Version) `
                        -ExpectedPublisher $ExpectedPublisher `
                        -ExpectedPackageFamilyName "$($Dependency.Name)_8wekyb3d8bbwe"
                    return
                }
                if ($attempt -lt $RetryCount -and $DelayMilliseconds -gt 0) {
                    Start-Sleep -Milliseconds $DelayMilliseconds
                }
            }
            throw "Pinned x64 dependency '$($Dependency.Name)' did not register for the current user within the bounded retry."
        }

        function Install-OpenClawWingetValidatedPackages {
            param(
                [Parameter(Mandatory = $true)]
                [object[]]$ValidatedDependencies,
                [Parameter(Mandatory = $true)]
                [string]$BundlePath,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [ValidateRange(1, 120)]
                [int]$RetryCount = 30,
                [ValidateRange(0, 5000)]
                [int]$DelayMilliseconds = 1000
            )

            foreach ($dependency in $ValidatedDependencies) {
                $state = Get-OpenClawWingetDependencyInstallState `
                    -Dependency $dependency `
                    -ExpectedPublisher $ExpectedPublisher
                if ([string]$state -ceq "install") {
                    Add-AppxPackage `
                        -Path ([string]$dependency.LocalPath) `
                        -ErrorAction Stop
                }
                Wait-OpenClawWingetDependencyRegistration `
                    -Dependency $dependency `
                    -ExpectedPublisher $ExpectedPublisher `
                    -RetryCount $RetryCount `
                    -DelayMilliseconds $DelayMilliseconds
            }
            Add-AppxPackage -Path $BundlePath -ErrorAction Stop
        }

        function Wait-OpenClawWingetMainPackageRegistration {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPackageFamilyName,
                [ValidateRange(1, 120)]
                [int]$RetryCount = 60,
                [ValidateRange(0, 5000)]
                [int]$DelayMilliseconds = 1000
            )

            for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
                $packages = @(
                    Get-AppxPackage `
                        -Name "Microsoft.DesktopAppInstaller" `
                        -ErrorAction Stop
                )
                if ($packages.Count -gt 1) {
                    throw "Current user has multiple Microsoft.DesktopAppInstaller registrations."
                }
                if ($packages.Count -eq 1) {
                    $package = $packages[0]
                    $version = [Version][string]$package.Version
                    if ($version -gt [Version]"1.29.280.0") {
                        throw "Current-user Microsoft.DesktopAppInstaller became newer than the reproducible pin."
                    }
                    if ($version -eq [Version]"1.29.280.0") {
                        Assert-OpenClawWingetCurrentPackageIdentity `
                            -Package $package `
                            -ExpectedName "Microsoft.DesktopAppInstaller" `
                            -ExpectedVersion "1.29.280.0" `
                            -ExpectedPublisher $ExpectedPublisher `
                            -ExpectedPackageFamilyName $ExpectedPackageFamilyName
                        return $package
                    }
                }
                if ($attempt -lt $RetryCount -and $DelayMilliseconds -gt 0) {
                    Start-Sleep -Milliseconds $DelayMilliseconds
                }
            }
            throw "Pinned Microsoft.DesktopAppInstaller did not register for the current user within the bounded retry."
        }

        function Assert-OpenClawWingetCatalogRegistrationIdentity {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Package,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher
            )

            if (
                [string]$Package.Name -cne "Microsoft.Winget.Source" -or
                [string]$Package.Publisher -cne $ExpectedPublisher -or
                -not [string]::Equals(
                    [string]$Package.Architecture,
                    "Neutral",
                    [StringComparison]::OrdinalIgnoreCase
                )
            ) {
                throw "Current-user Microsoft.Winget.Source registration has an unexpected identity."
            }
            return Get-OpenClawWingetValidNonzeroVersion `
                -VersionText ([string]$Package.Version) `
                -Subject "Current-user Microsoft.Winget.Source registration"
        }

        function Get-OpenClawWingetCatalogRegistrationState {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher
            )

            $packages = @(
                Get-AppxPackage `
                    -Name "Microsoft.Winget.Source" `
                    -ErrorAction Stop
            )
            if ($packages.Count -gt 1) {
                throw "Current user has multiple Microsoft.Winget.Source registrations."
            }
            if ($packages.Count -eq 0) {
                return [pscustomobject][ordered]@{
                    State = "missing"
                    Package = $null
                    Version = $null
                }
            }
            $version = Assert-OpenClawWingetCatalogRegistrationIdentity `
                -Package $packages[0] `
                -ExpectedPublisher $ExpectedPublisher
            return [pscustomobject][ordered]@{
                State = "existing"
                Package = $packages[0]
                Version = $version.ToString()
            }
        }

        function Wait-OpenClawWingetCatalogRegistration {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [ValidateRange(1, 120)]
                [int]$RetryCount = 60,
                [ValidateRange(0, 5000)]
                [int]$DelayMilliseconds = 1000
            )

            for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
                $state = Get-OpenClawWingetCatalogRegistrationState `
                    -ExpectedPublisher $ExpectedPublisher
                if ([string]$state.State -ceq "existing") {
                    return $state
                }
                if ($attempt -lt $RetryCount -and $DelayMilliseconds -gt 0) {
                    Start-Sleep -Milliseconds $DelayMilliseconds
                }
            }
            throw "Microsoft.Winget.Source did not register for the current user within the bounded retry."
        }

        function Get-OpenClawWingetCatalogFileEvidence {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [ValidateRange(1, 67108864)]
                [Int64]$MaximumSize
            )

            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                throw "Downloaded WinGet source catalog is missing."
            }
            $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
            if ([Int64]$file.Length -le 0) {
                throw "Downloaded WinGet source catalog is empty."
            }
            if ([Int64]$file.Length -gt $MaximumSize) {
                throw "Downloaded WinGet source catalog exceeds the maximum size."
            }
            $runtimeSha256 = (
                Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop
            ).Hash.ToUpperInvariant()
            if ($runtimeSha256 -cnotmatch "^[0-9A-F]{64}$") {
                throw "Downloaded WinGet source catalog SHA256 evidence is invalid."
            }
            Assert-OpenClawWingetCatalogSignature `
                -Path $Path `
                -ExpectedPublisher $ExpectedPublisher
            $manifest = Read-OpenClawWingetZipXml `
                -ArchivePath $Path `
                -EntryName "AppxManifest.xml" `
                -ArchiveName "source2.msix"
            $version = Assert-OpenClawWingetCatalogManifest `
                -Document $manifest `
                -ExpectedPublisher $ExpectedPublisher
            return [pscustomobject][ordered]@{
                Version = $version
                Sha256 = $runtimeSha256
            }
        }

        function Ensure-OpenClawWingetCatalog {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPublisher,
                [Parameter(Mandatory = $true)]
                [string]$TemporaryRoot,
                [Parameter(Mandatory = $true)]
                [DateTime]$DownloadDeadlineUtc,
                [ValidateRange(1, 120)]
                [int]$RetryCount = 60,
                [ValidateRange(0, 5000)]
                [int]$DelayMilliseconds = 1000
            )

            $existingState = Get-OpenClawWingetCatalogRegistrationState `
                -ExpectedPublisher $ExpectedPublisher
            if ([string]$existingState.State -ceq "existing") {
                return [pscustomobject][ordered]@{
                    Acquisition = "existing"
                    Version = [string]$existingState.Version
                    Sha256 = $null
                }
            }

            $remainingDownloadSeconds = [int][Math]::Floor(
                ($DownloadDeadlineUtc - [DateTime]::UtcNow).TotalSeconds
            )
            if ($remainingDownloadSeconds -lt 1) {
                throw "WinGet source catalog download exceeded the shared 1800-second timeout."
            }
            [Int64]$catalogMaximumSize = 16777216
            $catalogPath = Join-Path $TemporaryRoot "source2.msix"
            Invoke-OpenClawWingetCatalogDownload `
                -InitialUri ([Uri]"https://cdn.winget.microsoft.com/cache/source2.msix") `
                -DestinationPath $catalogPath `
                -MaximumSize $catalogMaximumSize `
                -TimeoutSeconds $remainingDownloadSeconds
            $fileEvidence = Get-OpenClawWingetCatalogFileEvidence `
                -Path $catalogPath `
                -ExpectedPublisher $ExpectedPublisher `
                -MaximumSize $catalogMaximumSize
            Add-AppxPackage -Path $catalogPath -ErrorAction Stop
            $registeredState = Wait-OpenClawWingetCatalogRegistration `
                -ExpectedPublisher $ExpectedPublisher `
                -RetryCount $RetryCount `
                -DelayMilliseconds $DelayMilliseconds
            if ([string]$registeredState.Version -cne [string]$fileEvidence.Version) {
                throw "Registered Microsoft.Winget.Source version does not match the validated signed catalog."
            }
            return [pscustomobject][ordered]@{
                Acquisition = "downloaded"
                Version = [string]$registeredState.Version
                Sha256 = [string]$fileEvidence.Sha256
            }
        }

        function Resolve-OpenClawWingetDirectExecutable {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Package
            )

            if ([string]::IsNullOrWhiteSpace([string]$Package.InstallLocation)) {
                throw "Current-user Microsoft.DesktopAppInstaller has no install location."
            }
            $installRoot = [IO.Path]::GetFullPath([string]$Package.InstallLocation).TrimEnd("\")
            if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
                throw "Current-user Microsoft.DesktopAppInstaller install location is missing."
            }
            $wingetPath = [IO.Path]::GetFullPath((Join-Path $installRoot "winget.exe"))
            if (
                [string](Split-Path -Parent $wingetPath).TrimEnd("\") -cne $installRoot -or
                -not (Test-Path -LiteralPath $wingetPath -PathType Leaf)
            ) {
                throw "Pinned Microsoft.DesktopAppInstaller root winget.exe is missing."
            }
            return $wingetPath
        }

        function Read-OpenClawWingetBoundedNativeText {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path,
                [ValidateRange(64, 8192)]
                [int]$MaxChars = 4096
            )

            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                return ""
            }
            $stream = $null
            $reader = $null
            try {
                $stream = [IO.File]::Open(
                    $Path,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::ReadWrite
                )
                $reader = New-Object IO.StreamReader(
                    $stream,
                    [Text.Encoding]::UTF8,
                    $true,
                    1024,
                    $false
                )
                $buffer = New-Object "char[]" ($MaxChars + 1)
                $count = $reader.ReadBlock($buffer, 0, $buffer.Length)
                $boundedCount = [Math]::Min($count, $MaxChars)
                $text = if ($boundedCount -eq 0) {
                    ""
                } else {
                    -join $buffer[0..($boundedCount - 1)]
                }
                if ($count -gt $MaxChars) {
                    $text += " [truncated]"
                }
                return ConvertTo-OpenClawWingetDiagnostic -Text $text -MaxChars $MaxChars
            } finally {
                if ($null -ne $reader) {
                    $reader.Dispose()
                } elseif ($null -ne $stream) {
                    $stream.Dispose()
                }
            }
        }

        function Invoke-OpenClawTrustedWingetProcess {
            param(
                [Parameter(Mandatory = $true)]
                [string]$WingetPath,
                [Parameter(Mandatory = $true)]
                [ValidateSet(
                    "Version",
                    "SourceExportWinget",
                    "SourceUpdateWinget",
                    "SourceProbeGit"
                )]
                [string]$Operation,
                [Parameter(Mandatory = $true)]
                [string]$TemporaryRoot
            )

            $canonicalWingetPath = [IO.Path]::GetFullPath($WingetPath)
            if (-not (Test-Path -LiteralPath $canonicalWingetPath -PathType Leaf)) {
                throw "Trusted direct winget.exe path is missing."
            }
            [string[]]$nativeArguments = switch ($Operation) {
                "Version" {
                    @("--version")
                    break
                }
                "SourceExportWinget" {
                    @("source", "export", "--name", "winget", "--disable-interactivity")
                    break
                }
                "SourceUpdateWinget" {
                    @(
                        "source",
                        "update",
                        "--name", "winget",
                        "--accept-source-agreements",
                        "--disable-interactivity"
                    )
                    break
                }
                "SourceProbeGit" {
                    @(
                        "show",
                        "--id", "Git.Git",
                        "-e",
                        "--source", "winget",
                        "--accept-source-agreements",
                        "--disable-interactivity"
                    )
                    break
                }
                default {
                    throw "Unsupported trusted winget operation '$Operation'."
                }
            }
            $timeoutMilliseconds = if ($Operation -ceq "SourceUpdateWinget") {
                300000
            } else {
                60000
            }
            $timeoutSeconds = [int]($timeoutMilliseconds / 1000)

            $nonce = [Guid]::NewGuid().ToString("N")
            $stdoutPath = Join-Path $TemporaryRoot ("winget-{0}.stdout.txt" -f $nonce)
            $stderrPath = Join-Path $TemporaryRoot ("winget-{0}.stderr.txt" -f $nonce)
            $process = $null
            $primaryFailure = $null
            $result = $null
            $cleanupFailures = New-Object "Collections.Generic.List[string]"
            try {
                $process = Start-Process `
                    -FilePath $canonicalWingetPath `
                    -ArgumentList $nativeArguments `
                    -RedirectStandardOutput $stdoutPath `
                    -RedirectStandardError $stderrPath `
                    -PassThru `
                    -WindowStyle Hidden `
                    -ErrorAction Stop
                if (-not $process.WaitForExit($timeoutMilliseconds)) {
                    try {
                        $process.Kill()
                        $process.WaitForExit()
                    } catch {
                    }
                    throw "Trusted winget operation '$Operation' timed out after $timeoutSeconds seconds."
                }
                $process.WaitForExit()
                $result = [pscustomobject][ordered]@{
                    Operation = $Operation
                    ExitCode = [int]$process.ExitCode
                    Stdout = Read-OpenClawWingetBoundedNativeText -Path $stdoutPath
                    Stderr = Read-OpenClawWingetBoundedNativeText -Path $stderrPath
                }
            } catch {
                $primaryFailure = $_
            } finally {
                if ($null -ne $process) {
                    $process.Dispose()
                }
                foreach ($capturePath in [string[]]@($stdoutPath, $stderrPath)) {
                    if (Test-Path -LiteralPath $capturePath) {
                        try {
                            Remove-Item -LiteralPath $capturePath -Force -ErrorAction Stop
                        } catch {
                            $cleanupFailures.Add(
                                (ConvertTo-OpenClawWingetDiagnostic `
                                    -Text $_.Exception.Message `
                                    -MaxChars 256)
                            )
                        }
                    }
                }
            }
            if ($cleanupFailures.Count -gt 0) {
                throw "Trusted winget operation '$Operation' could not remove its temporary capture files."
            }
            if ($null -ne $primaryFailure) {
                $failureType = $primaryFailure.Exception.GetType().FullName
                $failureDiagnostic = ConvertTo-OpenClawWingetDiagnostic `
                    -Text $primaryFailure.Exception.Message `
                    -MaxChars 512
                throw "Trusted winget operation '$Operation' failed ($failureType). Diagnostic: $failureDiagnostic"
            }
            if ($null -eq $result) {
                throw "Trusted winget operation '$Operation' returned no result."
            }
            return $result
        }

        function Get-OpenClawWingetProcessDiagnostic {
            param(
                [Parameter(Mandatory = $true)]
                [object]$Result
            )

            $stdout = ConvertTo-OpenClawWingetDiagnostic `
                -Text ([string]$Result.Stdout) `
                -MaxChars 1024
            $stderr = ConvertTo-OpenClawWingetDiagnostic `
                -Text ([string]$Result.Stderr) `
                -MaxChars 1024
            return "exitCode=$([int]$Result.ExitCode); stdout=$stdout; stderr=$stderr"
        }

        function Wait-OpenClawWingetExecutionAlias {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPackageFamilyName,
                [ValidateRange(1, 120)]
                [int]$RetryCount = 60,
                [ValidateRange(0, 5000)]
                [int]$DelayMilliseconds = 1000
            )

            $windowsAppsPath = [IO.Path]::GetFullPath(
                (Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\winget.exe")
            )
            for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
                $aliasCmdlet = Get-Command Get-AppExecutionAlias -ErrorAction SilentlyContinue
                $aliasReady = $true
                if ($null -ne $aliasCmdlet) {
                    $aliasRecords = @(
                        Get-AppExecutionAlias `
                            -Name "winget.exe" `
                            -ErrorAction SilentlyContinue
                    )
                    if ($aliasRecords.Count -gt 1) {
                        throw "Current user has multiple winget.exe AppExecutionAlias registrations."
                    }
                    if ($aliasRecords.Count -eq 0) {
                        $aliasReady = $false
                    } else {
                        $record = $aliasRecords[0]
                        if (
                            $null -ne $record.PSObject.Properties["PackageFamilyName"] -and
                            [string]$record.PackageFamilyName -cne $ExpectedPackageFamilyName
                        ) {
                            throw "Current-user winget.exe AppExecutionAlias maps to an unexpected package family."
                        }
                    }
                }

                $commands = @(
                    Get-Command `
                        winget.exe `
                        -CommandType Application `
                        -All `
                        -ErrorAction SilentlyContinue
                )
                $resolvedPaths = @(
                    $commands |
                        ForEach-Object {
                            if (-not [string]::IsNullOrWhiteSpace([string]$_.Path)) {
                                [IO.Path]::GetFullPath([string]$_.Path)
                            }
                        } |
                        Select-Object -Unique
                )
                $unexpectedPaths = @(
                    $resolvedPaths |
                        Where-Object {
                            -not [string]::Equals(
                                [string]$_,
                                $windowsAppsPath,
                                [StringComparison]::OrdinalIgnoreCase
                            )
                        }
                )
                if ($unexpectedPaths.Count -gt 0) {
                    throw "PATH resolves winget.exe outside the current-user Microsoft WindowsApps alias."
                }
                if (
                    $aliasReady -and
                    (Test-Path -LiteralPath $windowsAppsPath -PathType Leaf) -and
                    $resolvedPaths.Count -eq 1
                ) {
                    return $windowsAppsPath
                }
                if ($attempt -lt $RetryCount -and $DelayMilliseconds -gt 0) {
                    Start-Sleep -Milliseconds $DelayMilliseconds
                }
            }
            throw "Current-user winget.exe AppExecutionAlias and PATH did not become ready within the bounded retry."
        }

        function Assert-OpenClawWingetCli {
            param(
                [Parameter(Mandatory = $true)]
                [string]$WingetPath,
                [Parameter(Mandatory = $true)]
                [string]$TemporaryRoot
            )

            $versionResult = Invoke-OpenClawTrustedWingetProcess `
                -WingetPath $WingetPath `
                -Operation "Version" `
                -TemporaryRoot $TemporaryRoot
            if (
                [int]$versionResult.ExitCode -ne 0 -or
                [string]$versionResult.Stdout -cne "v1.29.280" -or
                -not [string]::IsNullOrWhiteSpace([string]$versionResult.Stderr)
            ) {
                throw "Direct pinned winget.exe --version did not return exactly v1.29.280 with exit code 0."
            }

            $sourceResult = Invoke-OpenClawTrustedWingetProcess `
                -WingetPath $WingetPath `
                -Operation "SourceExportWinget" `
                -TemporaryRoot $TemporaryRoot
            if (
                [int]$sourceResult.ExitCode -ne 0 -or
                -not [string]::IsNullOrWhiteSpace([string]$sourceResult.Stderr)
            ) {
                throw "Direct pinned winget.exe source export for the exact winget source did not return exit code 0."
            }
            try {
                $sourceRecords = @(
                    ConvertFrom-Json `
                        -InputObject ([string]$sourceResult.Stdout) `
                        -ErrorAction Stop
                )
            } catch {
                throw "Direct pinned winget.exe source export did not return valid JSON."
            }
            if (
                $sourceRecords.Count -ne 1 -or
                [string]$sourceRecords[0].Name -cne "winget"
            ) {
                throw "Direct pinned winget.exe source export did not return exactly one source named winget."
            }

            $sourceUpdate = Invoke-OpenClawTrustedWingetProcess `
                -WingetPath $WingetPath `
                -Operation "SourceUpdateWinget" `
                -TemporaryRoot $TemporaryRoot
            if ([int]$sourceUpdate.ExitCode -ne 0) {
                $sourceUpdateDiagnostic = Get-OpenClawWingetProcessDiagnostic `
                    -Result $sourceUpdate
                throw "The exact winget source update failed. Diagnostic: $sourceUpdateDiagnostic"
            }

            $sourceProbe = Invoke-OpenClawTrustedWingetProcess `
                -WingetPath $WingetPath `
                -Operation "SourceProbeGit" `
                -TemporaryRoot $TemporaryRoot
            $sourceProbeOutput = "{0} {1}" -f (
                [string]$sourceProbe.Stdout,
                [string]$sourceProbe.Stderr
            )
            if (
                [int]$sourceProbe.ExitCode -ne 0 -or
                $sourceProbeOutput -notmatch "(?i)\bGit\.Git\b"
            ) {
                $sourceProbeDiagnostic = Get-OpenClawWingetProcessDiagnostic `
                    -Result $sourceProbe
                throw "The exact winget source could not resolve the pinned Git.Git package noninteractively. Diagnostic: $sourceProbeDiagnostic"
            }
        }

        function Remove-OpenClawWingetTemporaryRoot {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path
            )

            if (Test-Path -LiteralPath $Path) {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }
            if (Test-Path -LiteralPath $Path) {
                throw "WinGet bootstrap nonce temporary root still exists after cleanup."
            }
        }

        function Invoke-OpenClawWingetBootstrap {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Publisher,
                [Parameter(Mandatory = $true)]
                [string]$PackageFamilyName,
                [Parameter(Mandatory = $true)]
                [string]$ReleaseBase,
                [Parameter(Mandatory = $true)]
                [string]$ReleasePath,
                [Parameter(Mandatory = $true)]
                [object[]]$TopAssets,
                [Parameter(Mandatory = $true)]
                [object[]]$Dependencies,
                [Parameter(Mandatory = $true)]
                [object]$Payload,
                [string]$TemporaryBase = [IO.Path]::GetTempPath()
            )

            $temporaryBase = [IO.Path]::GetFullPath($TemporaryBase).TrimEnd("\")
            $temporaryRoot = [IO.Path]::GetFullPath(
                (Join-Path $temporaryBase ("openclaw-winget-{0}" -f [Guid]::NewGuid().ToString("N")))
            )
            if (
                -not $temporaryRoot.StartsWith(
                    $temporaryBase + "\",
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                (Test-Path -LiteralPath $temporaryRoot)
            ) {
                throw "Could not allocate a new WinGet bootstrap nonce temporary root."
            }
            [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

            $primaryFailure = $null
            $cleanupFailure = $null
            $result = $null
            try {
                $downloadDeadlineUtc = [DateTime]::UtcNow.AddSeconds(1800)
                $mainState = Get-OpenClawWingetCurrentMainPackageState `
                    -ExpectedPublisher $Publisher `
                    -ExpectedPackageFamilyName $PackageFamilyName
                if ([string]$mainState.State -ceq "exact") {
                    Assert-OpenClawWingetCurrentPackageIdentity `
                        -Package $mainState.Package `
                        -ExpectedName "Microsoft.DesktopAppInstaller" `
                        -ExpectedVersion "1.29.280.0" `
                        -ExpectedPublisher $Publisher `
                        -ExpectedPackageFamilyName $PackageFamilyName
                    $directWinget = Resolve-OpenClawWingetDirectExecutable `
                        -Package $mainState.Package
                    Wait-OpenClawWingetExecutionAlias `
                        -ExpectedPackageFamilyName $PackageFamilyName | Out-Null
                    $catalogEvidence = Ensure-OpenClawWingetCatalog `
                        -ExpectedPublisher $Publisher `
                        -TemporaryRoot $temporaryRoot `
                        -DownloadDeadlineUtc $downloadDeadlineUtc
                    Assert-OpenClawWingetCli `
                        -WingetPath $directWinget `
                        -TemporaryRoot $temporaryRoot
                    $result = [pscustomobject][ordered]@{
                        Stage = "winget-bootstrap"
                        AlreadyInstalled = $true
                        Version = "v1.29.280"
                        SourceCatalogAcquisition = [string]$catalogEvidence.Acquisition
                        SourceCatalogVersion = [string]$catalogEvidence.Version
                        SourceCatalogSha256 = $catalogEvidence.Sha256
                    }
                } else {
                    $assetPaths = @{}
                    foreach ($asset in $TopAssets) {
                        $remainingDownloadSeconds = [int][Math]::Floor(
                            ($downloadDeadlineUtc - [DateTime]::UtcNow).TotalSeconds
                        )
                        if ($remainingDownloadSeconds -lt 1) {
                            throw "Pinned WinGet asset downloads exceeded the shared 1800-second timeout."
                        }
                        $assetPath = Join-Path $temporaryRoot ([string]$asset.Name)
                        $assetUri = [Uri]::new($ReleaseBase + [string]$asset.Name)
                        Invoke-OpenClawWingetAssetDownload `
                            -InitialUri $assetUri `
                            -AssetName ([string]$asset.Name) `
                            -ExpectedSize ([Int64]$asset.Size) `
                            -DestinationPath $assetPath `
                            -ExpectedReleasePath $ReleasePath `
                            -TimeoutSeconds $remainingDownloadSeconds
                        $assetPaths[[string]$asset.Name] = $assetPath
                    }

                    foreach ($asset in $TopAssets) {
                        Assert-OpenClawWingetFile `
                            -Path ([string]$assetPaths[[string]$asset.Name]) `
                            -ExpectedSize ([Int64]$asset.Size) `
                            -ExpectedSha256 ([string]$asset.Sha256) `
                            -AssetName ([string]$asset.Name)
                    }

                    $descriptorPath = [string]$assetPaths[
                        "DesktopAppInstaller_Dependencies.json"
                    ]
                    Assert-OpenClawWingetDependencyDescriptor `
                        -Path $descriptorPath `
                        -ExpectedDependencies $Dependencies

                    $dependencyArchivePath = [string]$assetPaths[
                        "DesktopAppInstaller_Dependencies.zip"
                    ]
                    $validatedDependencies = @(
                        Expand-OpenClawWingetDependencyPackages `
                            -ArchivePath $dependencyArchivePath `
                            -DestinationRoot (Join-Path $temporaryRoot "dependencies") `
                            -ExpectedDependencies $Dependencies
                    )
                    if ($validatedDependencies.Count -ne $Dependencies.Count) {
                        throw "Dependency extraction did not return every exact pinned x64 package."
                    }
                    foreach ($dependency in $validatedDependencies) {
                        Assert-OpenClawWingetFile `
                            -Path ([string]$dependency.LocalPath) `
                            -ExpectedSize ([Int64]$dependency.Size) `
                            -ExpectedSha256 ([string]$dependency.Sha256) `
                            -AssetName ([string]$dependency.RelativePath)
                        Assert-OpenClawWingetSignature `
                            -Path ([string]$dependency.LocalPath) `
                            -ExpectedPublisher $Publisher `
                            -AssetName ([string]$dependency.RelativePath)
                        $dependencyManifest = Read-OpenClawWingetZipXml `
                            -ArchivePath ([string]$dependency.LocalPath) `
                            -EntryName "AppxManifest.xml" `
                            -ArchiveName ([string]$dependency.RelativePath)
                        Assert-OpenClawAppxManifestIdentity `
                            -Document $dependencyManifest `
                            -ExpectedName ([string]$dependency.Name) `
                            -ExpectedVersion ([string]$dependency.Version) `
                            -ExpectedPublisher $Publisher `
                            -DocumentName ([string]$dependency.RelativePath)
                    }

                    $bundlePath = [string]$assetPaths[
                        "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
                    ]
                    Assert-OpenClawWingetSignature `
                        -Path $bundlePath `
                        -ExpectedPublisher $Publisher `
                        -AssetName "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
                    $bundleManifest = Read-OpenClawWingetZipXml `
                        -ArchivePath $bundlePath `
                        -EntryName "AppxMetadata\AppxBundleManifest.xml" `
                        -ArchiveName "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
                    $payloadEntryName = Assert-OpenClawWingetBundleManifest `
                        -Document $bundleManifest `
                        -ExpectedPublisher $Publisher
                    $payloadPath = Join-Path $temporaryRoot ([string]$Payload.Name)
                    Expand-OpenClawWingetBundlePayload `
                        -BundlePath $bundlePath `
                        -PayloadEntryName $payloadEntryName `
                        -ExpectedSize ([Int64]$Payload.Size) `
                        -DestinationPath $payloadPath | Out-Null
                    Assert-OpenClawWingetFile `
                        -Path $payloadPath `
                        -ExpectedSize ([Int64]$Payload.Size) `
                        -ExpectedSha256 ([string]$Payload.Sha256) `
                        -AssetName ([string]$Payload.Name)
                    Assert-OpenClawWingetSignature `
                        -Path $payloadPath `
                        -ExpectedPublisher $Publisher `
                        -AssetName ([string]$Payload.Name)
                    $payloadManifest = Read-OpenClawWingetZipXml `
                        -ArchivePath $payloadPath `
                        -EntryName "AppxManifest.xml" `
                        -ArchiveName ([string]$Payload.Name)
                    Assert-OpenClawWingetPayloadManifest `
                        -Document $payloadManifest `
                        -ExpectedPublisher $Publisher `
                        -ExpectedDependencies $Dependencies

                    Install-OpenClawWingetValidatedPackages `
                        -ValidatedDependencies $validatedDependencies `
                        -BundlePath $bundlePath `
                        -ExpectedPublisher $Publisher
                    $registeredPackage = Wait-OpenClawWingetMainPackageRegistration `
                        -ExpectedPublisher $Publisher `
                        -ExpectedPackageFamilyName $PackageFamilyName
                    $directWinget = Resolve-OpenClawWingetDirectExecutable `
                        -Package $registeredPackage
                    Wait-OpenClawWingetExecutionAlias `
                        -ExpectedPackageFamilyName $PackageFamilyName | Out-Null
                    $catalogEvidence = Ensure-OpenClawWingetCatalog `
                        -ExpectedPublisher $Publisher `
                        -TemporaryRoot $temporaryRoot `
                        -DownloadDeadlineUtc $downloadDeadlineUtc
                    Assert-OpenClawWingetCli `
                        -WingetPath $directWinget `
                        -TemporaryRoot $temporaryRoot
                    $result = [pscustomobject][ordered]@{
                        Stage = "winget-bootstrap"
                        AlreadyInstalled = $false
                        Version = "v1.29.280"
                        SourceCatalogAcquisition = [string]$catalogEvidence.Acquisition
                        SourceCatalogVersion = [string]$catalogEvidence.Version
                        SourceCatalogSha256 = $catalogEvidence.Sha256
                    }
                }
            } catch {
                $primaryFailure = $_
            } finally {
                try {
                    Remove-OpenClawWingetTemporaryRoot -Path $temporaryRoot
                } catch {
                    $cleanupFailure = $_
                }
            }

            if ($null -ne $cleanupFailure) {
                throw "WinGet bootstrap failed to clean its nonce temporary root."
            }
            if ($null -ne $primaryFailure) {
                throw $primaryFailure
            }
            if ($null -eq $result) {
                throw "WinGet bootstrap returned no result."
            }
            return $result
        }

        $publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
        $packageFamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe"
        $releaseTag = "v1.29.280"
        $releasePath = "/microsoft/winget-cli/releases/download/$releaseTag"
        $releaseBase = "https://github.com$releasePath/"
        $topAssets = [object[]]@(
            [pscustomobject][ordered]@{
                Name = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
                Size = [Int64]216775738
                Sha256 = "0809fa9f52e395d6e7de692331dce847ac991952675116bb4d8aae2ddcc20946"
            },
            [pscustomobject][ordered]@{
                Name = "DesktopAppInstaller_Dependencies.zip"
                Size = [Int64]97760717
                Sha256 = "3bbfcaa5cb011c48fac48d896d64a5c7c6898859a9f3d01555c8cd000f4e2962"
            },
            [pscustomobject][ordered]@{
                Name = "DesktopAppInstaller_Dependencies.json"
                Size = [Int64]322
                Sha256 = "a56ddd79cf9cd056d9546cfeb6958c2b44d20f6221f8518bf17b003717d47a7a"
            }
        )
        $dependencies = [object[]]@(
            [pscustomobject][ordered]@{
                Name = "Microsoft.VCLibs.140.00"
                Version = "14.0.33519.0"
                RelativePath = "x64\Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx"
                Size = [Int64]896581
                Sha256 = "9c17b521f9d690a1f504da5108ed6eec5669eb3a8fd1331eef43e40d84e74283"
            },
            [pscustomobject][ordered]@{
                Name = "Microsoft.VCLibs.140.00.UWPDesktop"
                Version = "14.0.33728.0"
                RelativePath = "x64\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx"
                Size = [Int64]6757465
                Sha256 = "077a3d1a5d0622bd3004dca85f5e192d6e98ec79b83d4aa06766759ea6c09c3d"
            },
            [pscustomobject][ordered]@{
                Name = "Microsoft.WindowsAppRuntime.1.8"
                Version = "8000.616.304.0"
                RelativePath = "x64\Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64.appx"
                Size = [Int64]25431545
                Sha256 = "a31595cc4b5aebc18466ec24e8d4b566fe0fcafb52d833b6d139b8691d0e5177"
            }
        )
        $payload = [pscustomobject][ordered]@{
            Name = "AppInstaller_x64.msix"
            Size = [Int64]62421154
            Sha256 = "bdc908068f7563d89ef3405f1a30ae74df8cb0416414ed3613c4d68e2c812ff1"
        }

        Invoke-OpenClawWingetBootstrap `
            -Publisher $publisher `
            -PackageFamilyName $packageFamilyName `
            -ReleaseBase $releaseBase `
            -ReleasePath $releasePath `
            -TopAssets $topAssets `
            -Dependencies $dependencies `
            -Payload $payload
    }
}

function Ensure-GuestWingetAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    Write-Step "Ensuring pinned guest WinGet is available"
    $bootstrapOutput = @(
        Invoke-GuestCommandWithTimeout `
            -Session $Session `
            -OperationName "Bootstrapping pinned guest WinGet" `
            -TimeoutSec 3000 `
            -ScriptBlock (Get-GuestWingetBootstrapScriptBlock)
    )
    $result = Get-RequiredGuestStageResult `
        -Output $bootstrapOutput `
        -ExpectedStage "winget-bootstrap"
    if ([string]$result.Version -cne "v1.29.280") {
        throw "Guest WinGet bootstrap result did not carry the pinned executable version."
    }
    $catalogAcquisition = [string]$result.SourceCatalogAcquisition
    if ($catalogAcquisition -cnotin [string[]]@("existing", "downloaded")) {
        throw "Guest WinGet bootstrap result did not carry a valid source catalog acquisition."
    }
    [Version]$catalogVersion = $null
    if (
        -not [Version]::TryParse(
            [string]$result.SourceCatalogVersion,
            [ref]$catalogVersion
        ) -or
        (
            $catalogVersion.Major -eq 0 -and
            $catalogVersion.Minor -eq 0 -and
            $catalogVersion.Build -le 0 -and
            $catalogVersion.Revision -le 0
        )
    ) {
        throw "Guest WinGet bootstrap result did not carry a valid nonzero source catalog version."
    }
    $catalogSha256 = $result.SourceCatalogSha256
    if (
        (
            $catalogAcquisition -ceq "existing" -and
            $null -ne $catalogSha256
        ) -or
        (
            $catalogAcquisition -ceq "downloaded" -and
            [string]$catalogSha256 -cnotmatch "^[0-9A-F]{64}$"
        )
    ) {
        throw "Guest WinGet bootstrap result did not carry honest source catalog SHA256 evidence."
    }
    return $result
}

function Ensure-GuestGitInstalled {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    Write-Step "Ensuring guest Git is installed"
    Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Installing guest Git" -TimeoutSec 1800 -ScriptBlock {
        if (Get-Command git -ErrorAction SilentlyContinue) {
            return
        }

        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw "winget is not available in the guest. Install App Installer, then retry Prepare."
        }

        & winget install --id Git.Git -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity
        if ($LASTEXITCODE -ne 0) {
            throw "winget failed to install Git with exit code $LASTEXITCODE."
        }

        $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            throw "Git was installed but is not available on PATH in the guest session."
        }
    } | Out-Null
}

function Get-GuestPowerShell7InstallScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [string]$WingetPackageVersion,
            [Parameter(Mandatory = $true)]
            [string]$ExpectedPowerShellVersion
        )

        function ConvertTo-OpenClawPowerShellToolDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [int]$MaximumLength = 1024
            )

            if ([string]::IsNullOrWhiteSpace($Text)) {
                return "<empty>"
            }

            $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
            $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
            $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
            $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
            $safe = ($safe -replace '\s+', ' ').Trim()
            if ($safe.Length -gt $MaximumLength) {
                $safe = $safe.Substring(0, $MaximumLength) + "...<truncated>"
            }
            return $safe
        }

        function Get-OpenClawNativeExitCodeHex {
            param(
                [Parameter(Mandatory = $true)]
                [int]$ExitCode
            )

            $unsignedExitCode = [BitConverter]::ToUInt32(
                [BitConverter]::GetBytes($ExitCode),
                0)
            return "0x{0:X8}" -f $unsignedExitCode
        }

        function Invoke-OpenClawPowerShellToolProcess {
            param(
                [Parameter(Mandatory = $true)]
                [ValidateSet("InstallPinnedWix", "ReadInstalledVersion")]
                [string]$Operation,
                [Parameter(Mandatory = $true)]
                [string]$ExecutablePath,
                [Parameter(Mandatory = $true)]
                [string]$PackageVersion
            )

            switch ($Operation) {
                "InstallPinnedWix" {
                    $arguments = [string[]]@(
                        "install",
                        "--id", "Microsoft.PowerShell",
                        "-e",
                        "--version", $PackageVersion,
                        "--installer-type", "wix",
                        "--scope", "machine",
                        "--source", "winget",
                        "--accept-source-agreements",
                        "--accept-package-agreements",
                        "--disable-interactivity")
                }
                "ReadInstalledVersion" {
                    $arguments = [string[]]@(
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        '$PSVersionTable.PSVersion.ToString()')
                }
            }

            $captureRoot = Join-Path $env:TEMP (
                "openclaw-powershell7-{0}" -f [Guid]::NewGuid().ToString("N"))
            $stdoutPath = Join-Path $captureRoot "stdout.txt"
            $stderrPath = Join-Path $captureRoot "stderr.txt"
            $cleanupFailure = $null
            try {
                New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
                $process = Start-Process `
                    -FilePath $ExecutablePath `
                    -ArgumentList $arguments `
                    -RedirectStandardOutput $stdoutPath `
                    -RedirectStandardError $stderrPath `
                    -Wait `
                    -PassThru `
                    -WindowStyle Hidden `
                    -ErrorAction Stop
                $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
                    Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction Stop
                } else {
                    ""
                }
                $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
                    Get-Content -LiteralPath $stderrPath -Raw -ErrorAction Stop
                } else {
                    ""
                }

                return [pscustomobject][ordered]@{
                    operation = $Operation
                    exitCode = [int]$process.ExitCode
                    stdout = ConvertTo-OpenClawPowerShellToolDiagnostic -Text $stdout
                    stderr = ConvertTo-OpenClawPowerShellToolDiagnostic -Text $stderr
                }
            } finally {
                if (Test-Path -LiteralPath $captureRoot) {
                    try {
                        Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
                    } catch {
                        $cleanupFailure = $_.Exception.Message
                    }
                }
                if ($cleanupFailure) {
                    throw "PowerShell 7 tool capture cleanup failed: $cleanupFailure"
                }
            }
        }

        function Assert-OpenClawPowerShell7 {
            param(
                [Parameter(Mandatory = $true)]
                [string]$ExpectedPath,
                [Parameter(Mandatory = $true)]
                [string]$ExpectedVersion
            )

            if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf)) {
                throw "PowerShell 7 is not installed at the expected machine path '$ExpectedPath'."
            }

            $versionResult = Invoke-OpenClawPowerShellToolProcess `
                -Operation "ReadInstalledVersion" `
                -ExecutablePath $ExpectedPath `
                -PackageVersion $WingetPackageVersion
            if ([int]$versionResult.exitCode -ne 0) {
                $versionHex = Get-OpenClawNativeExitCodeHex -ExitCode ([int]$versionResult.exitCode)
                throw (
                    (
                        "Installed pwsh.exe version check failed with exit code {0} ({1}). " +
                        "stdout='{2}' stderr='{3}'"
                    ) -f
                        $versionResult.exitCode,
                        $versionHex,
                        $versionResult.stdout,
                        $versionResult.stderr)
            }

            $reportedVersion = [string]$versionResult.stdout
            if (-not [string]::Equals(
                    $reportedVersion,
                    $ExpectedVersion,
                    [StringComparison]::Ordinal)) {
                throw (
                    "Installed pwsh.exe reported version '{0}'; expected exact version '{1}'." -f
                        $reportedVersion,
                        $ExpectedVersion)
            }

            $resolvedCommand = Get-Command pwsh.exe -CommandType Application -ErrorAction Stop
            $resolvedPath = [IO.Path]::GetFullPath([string]$resolvedCommand.Source)
            $canonicalExpectedPath = [IO.Path]::GetFullPath($ExpectedPath)
            if (-not [string]::Equals(
                    $resolvedPath,
                    $canonicalExpectedPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    "pwsh.exe resolves to '{0}'; expected trusted machine path '{1}'." -f
                        $resolvedPath,
                        $canonicalExpectedPath)
            }
        }

        $expectedPwshPath = Join-Path $env:ProgramFiles "PowerShell\7\pwsh.exe"
        if (Test-Path -LiteralPath $expectedPwshPath -PathType Leaf) {
            try {
                Assert-OpenClawPowerShell7 `
                    -ExpectedPath $expectedPwshPath `
                    -ExpectedVersion $ExpectedPowerShellVersion
                return
            } catch {
                # A wrong or incomplete existing installation is repaired only through the exact pinned Wix package.
            }
        }

        $wingetCommand = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue
        if (-not $wingetCommand) {
            throw "winget is not available in the guest. Install App Installer, then retry Prepare."
        }

        $installResult = Invoke-OpenClawPowerShellToolProcess `
            -Operation "InstallPinnedWix" `
            -ExecutablePath ([string]$wingetCommand.Source) `
            -PackageVersion $WingetPackageVersion
        if ([int]$installResult.exitCode -ne 0) {
            $installHex = Get-OpenClawNativeExitCodeHex -ExitCode ([int]$installResult.exitCode)
            $knownHint = if ($installHex -ceq "0x80073D19") {
                " This is the known AppX deployment-session/user-logged-off failure; the pinned Wix selection must not fall back to MSIX."
            } else {
                ""
            }
            throw (
                (
                    "winget failed to install pinned PowerShell {0} Wix for machine scope with exit code {1} ({2}).{3} " +
                    "stdout='{4}' stderr='{5}'"
                ) -f
                    $WingetPackageVersion,
                    $installResult.exitCode,
                    $installHex,
                    $knownHint,
                    $installResult.stdout,
                    $installResult.stderr)
        }

        $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [Environment]::GetEnvironmentVariable("Path", "User")
        Assert-OpenClawPowerShell7 `
            -ExpectedPath $expectedPwshPath `
            -ExpectedVersion $ExpectedPowerShellVersion
    }
}

function Ensure-GuestPowerShell7Installed {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    Write-Step "Ensuring guest PowerShell 7 is installed"
    Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Installing guest PowerShell 7" `
        -TimeoutSec 1800 `
        -ScriptBlock (Get-GuestPowerShell7InstallScriptBlock) `
        -ArgumentList @(
            $script:GuestPowerShellWingetVersion,
            $script:GuestPowerShellVersion) | Out-Null
}

function Get-GuestDeveloperPrerequisiteScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [ValidateSet("DotNet10", "NodeLts", "WindowsSdk26100", "WebView2", "VisualStudioBuildTools")]
            [string]$PackageKey,
            [Parameter(Mandatory = $true)]
            [bool]$VerifyOnly,
            [Parameter(Mandatory = $true)]
            [int]$NativeTimeoutSec
        )

        function ConvertTo-OpenClawPrerequisiteDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [int]$MaximumLength = 1024
            )

            if ([string]::IsNullOrWhiteSpace($Text)) {
                return "<empty>"
            }
            $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
            $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
            $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
            $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
            $safe = ($safe -replace '\s+', ' ').Trim()
            if ($safe.Length -gt $MaximumLength) {
                return $safe.Substring(0, $MaximumLength) + "...<truncated>"
            }
            return $safe
        }

        function Get-OpenClawPrerequisiteExitHex {
            param([Parameter(Mandatory = $true)][int]$ExitCode)

            $unsignedExitCode = [BitConverter]::ToUInt32(
                [BitConverter]::GetBytes($ExitCode),
                0)
            return "0x{0:X8}" -f $unsignedExitCode
        }

        function Test-OpenClawPrerequisiteReparsePoint {
            param(
                [Parameter(Mandatory = $true)]
                [string]$LiteralPath
            )

            try {
                $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
                return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
            } catch {
                return $true
            }
        }

        function Invoke-OpenClawPrerequisiteProcess {
            param(
                [Parameter(Mandatory = $true)]
                [ValidateSet("Install", "DotNetListSdks", "NodeVersion", "NpmVersion", "VsWhere")]
                [string]$Operation,
                [Parameter(Mandatory = $true)]
                [string]$ExecutablePath,
                [Parameter(Mandatory = $true)]
                [string[]]$Arguments
            )

            $captureRoot = Join-Path $env:TEMP (
                "openclaw-prerequisite-{0}-{1}" -f
                    $PackageKey.ToLowerInvariant(),
                    [Guid]::NewGuid().ToString("N"))
            $stdoutPath = Join-Path $captureRoot "stdout.txt"
            $stderrPath = Join-Path $captureRoot "stderr.txt"
            $cleanupFailure = $null
            try {
                New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
                $process = Start-Process `
                    -FilePath $ExecutablePath `
                    -ArgumentList $Arguments `
                    -RedirectStandardOutput $stdoutPath `
                    -RedirectStandardError $stderrPath `
                    -PassThru `
                    -WindowStyle Hidden `
                    -ErrorAction Stop
                # Windows PowerShell 5.1 requires opening the handle before exit to retain ExitCode.
                $null = $process.Handle
                if (-not $process.WaitForExit($NativeTimeoutSec * 1000)) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    throw (
                        "Developer prerequisite package '{0}' operation '{1}' timed out after {2} seconds." -f
                            $PackageKey,
                            $Operation,
                            $NativeTimeoutSec)
                }
                $process.WaitForExit()
                $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
                    [IO.File]::ReadAllText($stdoutPath)
                } else {
                    ""
                }
                $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
                    [IO.File]::ReadAllText($stderrPath)
                } else {
                    ""
                }
                $stdoutLines = @(
                    foreach ($line in ($stdout -split '\r?\n')) {
                        if (-not [string]::IsNullOrWhiteSpace($line)) {
                            ConvertTo-OpenClawPrerequisiteDiagnostic -Text $line
                        }
                    }
                )
                return [pscustomobject][ordered]@{
                    operation = $Operation
                    exitCode = [int]$process.ExitCode
                    stdout = ConvertTo-OpenClawPrerequisiteDiagnostic -Text $stdout
                    stdoutLines = [string[]]$stdoutLines
                    stderr = ConvertTo-OpenClawPrerequisiteDiagnostic -Text $stderr
                }
            } finally {
                if (Test-Path -LiteralPath $captureRoot) {
                    try {
                        Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
                    } catch {
                        $cleanupFailure = $_.Exception.Message
                    }
                }
                if ($cleanupFailure) {
                    throw (
                        "Developer prerequisite package '{0}' capture cleanup failed: {1}" -f
                            $PackageKey,
                            $cleanupFailure)
                }
            }
        }

        function Get-OpenClawPrerequisiteVerification {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Key
            )

            switch ($Key) {
                "DotNet10" {
                    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
                    if (-not $dotnet) {
                        return [pscustomobject]@{ present = $false; evidence = "dotnet.exe unavailable" }
                    }
                    $expectedDotNetPath = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
                    $dotnetPath = [IO.Path]::GetFullPath([string]$dotnet.Source)
                    if (-not [string]::Equals(
                        $dotnetPath,
                        $expectedDotNetPath,
                        [StringComparison]::OrdinalIgnoreCase)) {
                        return [pscustomobject]@{
                            present = $false
                            evidence = "dotnet.exe is not installed at the trusted machine path"
                        }
                    }
                    $result = Invoke-OpenClawPrerequisiteProcess `
                        -Operation "DotNetListSdks" `
                        -ExecutablePath ([string]$dotnet.Source) `
                        -Arguments ([string[]]@("--list-sdks"))
                    $versions = @(
                        [string]$result.stdout -split '\s+' |
                            Where-Object { $_ -match '^10\.\d+\.\d+$' }
                    )
                    return [pscustomobject]@{
                        present = [int]$result.exitCode -eq 0 -and $versions.Count -gt 0
                        evidence = if ($versions.Count -gt 0) {
                            ($versions -join ",")
                        } else {
                            "dotnetExit=$($result.exitCode)"
                        }
                    }
                }
                "NodeLts" {
                    $node = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue
                    $npm = Get-Command npm.cmd -CommandType Application -ErrorAction SilentlyContinue
                    if (-not $node -or -not $npm) {
                        return [pscustomobject]@{ present = $false; evidence = "node.exe or npm.cmd unavailable" }
                    }
                    $nodeResult = Invoke-OpenClawPrerequisiteProcess `
                        -Operation "NodeVersion" `
                        -ExecutablePath ([string]$node.Source) `
                        -Arguments ([string[]]@("--version"))
                    $npmResult = Invoke-OpenClawPrerequisiteProcess `
                        -Operation "NpmVersion" `
                        -ExecutablePath ([string]$npm.Source) `
                        -Arguments ([string[]]@("--version"))
                    return [pscustomobject]@{
                        present = (
                            [int]$nodeResult.exitCode -eq 0 -and
                            [int]$npmResult.exitCode -eq 0 -and
                            [string]$nodeResult.stdout -match '^v\d+\.\d+\.\d+$' -and
                            [string]$npmResult.stdout -match '^\d+\.\d+\.\d+')
                        evidence = "node=$($nodeResult.stdout);npm=$($npmResult.stdout)"
                    }
                }
                "WindowsSdk26100" {
                    $includeRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
                    $versions = @()
                    if (Test-Path -LiteralPath $includeRoot -PathType Container) {
                        $versions = @(
                            Get-ChildItem -LiteralPath $includeRoot -Directory -ErrorAction Stop |
                                Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
                                Sort-Object { [Version]$_.Name } -Descending |
                                Select-Object -ExpandProperty Name
                        )
                    }
                    return [pscustomobject]@{
                        present = $versions.Count -gt 0
                        evidence = if ($versions.Count -gt 0) { [string]$versions[0] } else { "SDK include unavailable" }
                    }
                }
                "WebView2" {
                    $keys = [string[]]@(
                        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}")
                    foreach ($key in $keys) {
                        if (Test-Path -LiteralPath $key) {
                            $version = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).pv
                            if ([string]$version -match '^\d+\.\d+\.\d+\.\d+$') {
                                return [pscustomobject]@{ present = $true; evidence = [string]$version }
                            }
                        }
                    }
                    return [pscustomobject]@{ present = $false; evidence = "WebView2 registration unavailable" }
                }
                "VisualStudioBuildTools" {
                    $redistComponentId = "Microsoft.VisualStudio.Component.VC.Redist.14.Latest"
                    $toolsComponentId = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
                    $vswherePath = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
                    if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
                        return [pscustomobject]@{
                            present = $false
                            evidence = "standard vswhere.exe path unavailable"
                        }
                    }
                    if (Test-OpenClawPrerequisiteReparsePoint -LiteralPath $vswherePath) {
                        return [pscustomobject]@{
                            present = $false
                            evidence = "standard vswhere.exe path is a reparse point"
                        }
                    }
                    $vswhereResult = Invoke-OpenClawPrerequisiteProcess `
                        -Operation "VsWhere" `
                        -ExecutablePath $vswherePath `
                        -Arguments ([string[]]@(
                            "-latest",
                            "-products", "*",
                            "-requires", $redistComponentId, $toolsComponentId,
                            "-property", "installationPath"))
                    if ([int]$vswhereResult.exitCode -ne 0) {
                        return [pscustomobject]@{
                            present = $false
                            evidence = (
                                "vswhereExit={0};stdout='{1}';stderr='{2}'" -f
                                    $vswhereResult.exitCode,
                                    $vswhereResult.stdout,
                                    $vswhereResult.stderr)
                        }
                    }
                    $installRoots = @($vswhereResult.stdoutLines)
                    if ($installRoots.Count -ne 1) {
                        return [pscustomobject]@{
                            present = $false
                            evidence = "vswhere returned $($installRoots.Count) component install roots"
                        }
                    }

                    try {
                        $installRoot = [IO.Path]::GetFullPath([string]$installRoots[0]).TrimEnd("\")
                    } catch {
                        return [pscustomobject]@{
                            present = $false
                            evidence = "vswhere returned an invalid component install root"
                        }
                    }
                    if (
                        -not (Test-Path -LiteralPath $installRoot -PathType Container) -or
                        (Test-OpenClawPrerequisiteReparsePoint -LiteralPath $installRoot)
                    ) {
                        return [pscustomobject]@{
                            present = $false
                            evidence = "vswhere component install root is unavailable or a reparse point"
                        }
                    }
                    $installRootPrefix = $installRoot + "\"
                    $redistRoot = $installRoot
                    foreach ($segment in [string[]]@("VC", "Redist", "MSVC")) {
                        $candidateRoot = [IO.Path]::GetFullPath(
                            (Join-Path $redistRoot $segment)).TrimEnd("\")
                        if (
                            -not $candidateRoot.StartsWith(
                                $installRootPrefix,
                                [StringComparison]::OrdinalIgnoreCase) -or
                            -not (Test-Path -LiteralPath $candidateRoot -PathType Container) -or
                            (Test-OpenClawPrerequisiteReparsePoint -LiteralPath $candidateRoot)
                        ) {
                            return [pscustomobject]@{
                                present = $false
                                evidence = "VC Redist path is unavailable or contains a reparse point"
                            }
                        }
                        $redistRoot = $candidateRoot
                    }

                    $versionDirectories = @(
                        Get-ChildItem -LiteralPath $redistRoot -Directory -ErrorAction Stop |
                            Where-Object {
                                $_.Name -match '^\d+\.\d+\.\d+(?:\.\d+)?$' -and
                                -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
                            } |
                            Sort-Object { [Version]$_.Name } -Descending
                    )
                    foreach ($versionDirectory in $versionDirectories) {
                        $versionPath = [IO.Path]::GetFullPath([string]$versionDirectory.FullName).TrimEnd("\")
                        if (-not $versionPath.StartsWith(
                            $installRootPrefix,
                            [StringComparison]::OrdinalIgnoreCase)) {
                            continue
                        }
                        $x64Root = [IO.Path]::GetFullPath(
                            (Join-Path $versionPath "x64")).TrimEnd("\")
                        if (
                            -not $x64Root.StartsWith(
                                $installRootPrefix,
                                [StringComparison]::OrdinalIgnoreCase) -or
                            -not (Test-Path -LiteralPath $x64Root -PathType Container) -or
                            (Test-OpenClawPrerequisiteReparsePoint -LiteralPath $x64Root)
                        ) {
                            continue
                        }
                        $crtDirectories = @(
                            Get-ChildItem -LiteralPath $x64Root -Directory -ErrorAction Stop |
                                Where-Object {
                                    $_.Name -like 'Microsoft.VC*.CRT' -and
                                    -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
                                }
                        )
                        foreach ($crtDirectory in $crtDirectories) {
                            $crtPath = [IO.Path]::GetFullPath([string]$crtDirectory.FullName).TrimEnd("\")
                            if (-not $crtPath.StartsWith(
                                $installRootPrefix,
                                [StringComparison]::OrdinalIgnoreCase)) {
                                continue
                            }
                            $runtimeFiles = @(
                                Get-ChildItem -LiteralPath $crtPath -File -ErrorAction Stop |
                                    Where-Object {
                                        $_.Length -gt 0 -and
                                        -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
                                    }
                            )
                            $vcruntime = @($runtimeFiles | Where-Object { $_.Name -like 'vcruntime140*.dll' })
                            $msvcp = @($runtimeFiles | Where-Object { $_.Name -like 'msvcp140*.dll' })
                            if ($vcruntime.Count -gt 0 -and $msvcp.Count -gt 0) {
                                return [pscustomobject]@{
                                    present = $true
                                    evidence = (
                                        "components={0},{1};installRoot={2};redistVersion={3}" -f
                                            $redistComponentId,
                                            $toolsComponentId,
                                            $installRoot,
                                            $versionDirectory.Name)
                                }
                            }
                        }
                    }
                    return [pscustomobject]@{
                        present = $false
                        evidence = "required nonempty x64 VC runtime DLLs are unavailable under the component install root"
                    }
                }
            }
        }

        $spec = switch ($PackageKey) {
            "DotNet10" {
                [pscustomobject][ordered]@{
                    displayName = ".NET 10 SDK"
                    id = "Microsoft.DotNet.SDK.10"
                    version = "10.0.302"
                    installerType = "burn"
                    scope = $null
                    customArguments = $null
                }
            }
            "NodeLts" {
                [pscustomobject][ordered]@{
                    displayName = "Node.js LTS with npm"
                    id = "OpenJS.NodeJS.LTS"
                    version = "24.18.0"
                    installerType = "wix"
                    scope = "machine"
                    customArguments = $null
                }
            }
            "WindowsSdk26100" {
                [pscustomobject][ordered]@{
                    displayName = "Windows SDK 10.0.26100"
                    id = "Microsoft.WindowsSDK.10.0.26100"
                    version = "10.0.26100.7705"
                    installerType = "burn"
                    scope = "machine"
                    customArguments = $null
                }
            }
            "WebView2" {
                [pscustomobject][ordered]@{
                    displayName = "WebView2 Runtime"
                    id = "Microsoft.EdgeWebView2Runtime"
                    version = "150.0.4078.83"
                    installerType = "exe"
                    scope = "machine"
                    customArguments = $null
                }
            }
            "VisualStudioBuildTools" {
                [pscustomobject][ordered]@{
                    displayName = "Visual Studio 2022 Build Tools VC runtime components"
                    id = "Microsoft.VisualStudio.2022.BuildTools"
                    version = "17.14.37"
                    installerType = "exe"
                    scope = "machine"
                    customArguments = (
                        "--add Microsoft.VisualStudio.Component.VC.Redist.14.Latest " +
                        "--add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --norestart")
                }
            }
        }
        $scope = if ($null -eq $spec.scope) { $null } else { [string]$spec.scope }
        $customArguments = if ($null -eq $spec.customArguments) {
            $null
        } else {
            [string]$spec.customArguments
        }

        $initialVerification = Get-OpenClawPrerequisiteVerification -Key $PackageKey
        if ([bool]$initialVerification.present) {
            return [pscustomobject][ordered]@{
                stage = "developer-prerequisite"
                packageKey = $PackageKey
                packageId = [string]$spec.id
                packageVersion = [string]$spec.version
                installerType = [string]$spec.installerType
                scope = $scope
                customArguments = $customArguments
                alreadyInstalled = $true
                installed = $false
                verified = $true
                verification = [string]$initialVerification.evidence
                needsRestart = $false
                rebootInitiated = $false
                installExitCode = $null
            }
        }
        if ($VerifyOnly) {
            throw (
                "Developer prerequisite package '{0}' ({1}) is still unavailable after reconnect. Verification: {2}" -f
                    $PackageKey,
                    $spec.id,
                    $initialVerification.evidence)
        }

        $winget = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue
        if (-not $winget) {
            throw "winget.exe is unavailable while installing developer prerequisite '$PackageKey'."
        }
        $installArguments = @(
            "install",
            "--id", [string]$spec.id,
            "-e",
            "--version", [string]$spec.version,
            "--installer-type", [string]$spec.installerType)
        if ($null -ne $scope) {
            $installArguments += @("--scope", $scope)
        }
        if ($null -ne $customArguments) {
            # Start-Process joins ArgumentList arrays under Windows PowerShell 5.1.
            # Preserve the fixed multi-word custom value as one native argv token.
            $installArguments += @("--custom", ('"{0}"' -f $customArguments))
        }
        $installArguments += @(
            "--source", "winget",
            "--silent",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--disable-interactivity")
        $installArguments = [string[]]$installArguments
        $installResult = Invoke-OpenClawPrerequisiteProcess `
            -Operation "Install" `
            -ExecutablePath ([string]$winget.Source) `
            -Arguments $installArguments
        $exitCode = [int]$installResult.exitCode
        $rebootRequiredToFinish = $exitCode -eq 3010 -or $exitCode -eq -1978334967
        $rebootInitiated = $exitCode -eq 1641 -or $exitCode -eq -1978334965
        if ($exitCode -ne 0 -and -not $rebootRequiredToFinish -and -not $rebootInitiated) {
            $exitHex = Get-OpenClawPrerequisiteExitHex -ExitCode $exitCode
            $scopeEvidence = if ($null -eq $scope) { "inherent" } else { $scope }
            throw (
                (
                    "winget failed to install developer prerequisite '{0}' ({1} {2}, installer={3}, scope={4}) " +
                    "with exit code {5} ({6}). stdout='{7}' stderr='{8}'"
                ) -f
                    $PackageKey,
                    $spec.id,
                    $spec.version,
                    $spec.installerType,
                    $scopeEvidence,
                    $exitCode,
                    $exitHex,
                    $installResult.stdout,
                    $installResult.stderr)
        }
        if ($rebootRequiredToFinish -or $rebootInitiated) {
            return [pscustomobject][ordered]@{
                stage = "developer-prerequisite"
                packageKey = $PackageKey
                packageId = [string]$spec.id
                packageVersion = [string]$spec.version
                installerType = [string]$spec.installerType
                scope = $scope
                customArguments = $customArguments
                alreadyInstalled = $false
                installed = $true
                verified = $false
                verification = "pending owned reboot verification"
                needsRestart = $true
                rebootInitiated = $rebootInitiated
                installExitCode = $exitCode
            }
        }

        $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [Environment]::GetEnvironmentVariable("Path", "User")
        $finalVerification = Get-OpenClawPrerequisiteVerification -Key $PackageKey
        if (-not [bool]$finalVerification.present) {
            throw (
                "Developer prerequisite package '{0}' ({1}) exited successfully but verification failed: {2}" -f
                    $PackageKey,
                    $spec.id,
                    $finalVerification.evidence)
        }
        return [pscustomobject][ordered]@{
            stage = "developer-prerequisite"
            packageKey = $PackageKey
            packageId = [string]$spec.id
            packageVersion = [string]$spec.version
            installerType = [string]$spec.installerType
            scope = $scope
            customArguments = $customArguments
            alreadyInstalled = $false
            installed = $true
            verified = $true
            verification = [string]$finalVerification.evidence
            needsRestart = $false
            rebootInitiated = $false
            installExitCode = $exitCode
        }
    }
}

function Get-GuestBootIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [string]$OperationName
    )

    $output = @(
        Invoke-GuestCommandWithTimeout `
            -Session $Session `
            -OperationName $OperationName `
            -TimeoutSec 60 `
            -ScriptBlock {
                return (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).
                    LastBootUpTime.ToUniversalTime().Ticks
            }
    )
    if ($output.Count -ne 1) {
        throw "$OperationName returned $($output.Count) boot identities; expected one."
    }
    return [Int64]$output[0]
}

function Wait-ForGuestPackageTransitionAndVerify {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [Int64]$PreviousBootTicks,
        [Parameter(Mandatory = $true)]
        [string]$PackageKey,
        [AllowNull()]
        [object]$OriginalInstallFailure,
        [ValidateRange(1, 3600)]
        [int]$TimeoutSec = $GuestRestartTimeoutSec,
        [ValidateRange(1, 60)]
        [int]$PollIntervalSec = 5
    )

    $ownedVm = Assert-OwnedVM `
        -ResolvedVhdPath $ResolvedVhdPath `
        -ExpectedOwnerId $ExpectedOwnerId
    $ownedVmId = ([Guid]$ownedVm.Id).ToString("D")
    Remove-PSSession -Session $Session -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastReconnectError = $null
    $lastVerificationError = $null
    $originalFailureText = if ($null -eq $OriginalInstallFailure) {
        ""
    } else {
        " Original install failure: " + (
            ConvertTo-SafeGuestDiagnosticText -Value $OriginalInstallFailure -MaxChars 1024)
    }
    do {
        $candidateVm = Assert-OwnedVM `
            -ResolvedVhdPath $ResolvedVhdPath `
            -ExpectedOwnerId $ExpectedOwnerId
        $candidateVmId = ([Guid]$candidateVm.Id).ToString("D")
        if ($candidateVmId -cne $ownedVmId) {
            throw (
                "Exact owned VM identity changed while recovering package '$PackageKey': " +
                "expected '$ownedVmId', observed '$candidateVmId'.")
        }
        if ([string]$candidateVm.State -cne "Running") {
            throw (
                "Exact owned VM '$ownedVmId' is not Running while recovering package '$PackageKey'.")
        }

        $candidateSession = $null
        $currentBootTicks = $null
        try {
            $candidateSession = New-PSSession `
                -VMName $VMName `
                -Credential $GuestCredential `
                -ErrorAction Stop
            $currentBootTicks = Get-GuestBootIdentity `
                -Session $candidateSession `
                -OperationName "Reading guest boot identity after package '$PackageKey'"
        } catch {
            $lastReconnectError = $_
        }
        if ($null -eq $currentBootTicks) {
            if ($null -ne $candidateSession) {
                Remove-PSSession -Session $candidateSession -ErrorAction SilentlyContinue
            }
            Start-Sleep -Seconds $PollIntervalSec
            continue
        }
        if ([Int64]$currentBootTicks -lt $PreviousBootTicks) {
            Remove-PSSession -Session $candidateSession -ErrorAction SilentlyContinue
            throw (
                "Guest boot identity regressed while recovering package '$PackageKey': " +
                "previous='$PreviousBootTicks', observed='$currentBootTicks'.")
        }

        try {
            $proof = Invoke-GuestDeveloperPrerequisiteWorker `
                -Session $candidateSession `
                -PackageKey $PackageKey `
                -VerifyOnly $true
        } catch {
            $lastVerificationError = $_
            Remove-PSSession -Session $candidateSession -ErrorAction SilentlyContinue
            if ([Int64]$currentBootTicks -gt $PreviousBootTicks) {
                $safeVerificationError = ConvertTo-SafeGuestDiagnosticText `
                    -Value $lastVerificationError `
                    -MaxChars 1024
                throw (
                    "Developer prerequisite package '$PackageKey' remained unavailable after a newer owned boot." +
                    $originalFailureText +
                    " Verification failure: $safeVerificationError")
            }
            Start-Sleep -Seconds $PollIntervalSec
            continue
        }

        $transition = if ([Int64]$currentBootTicks -gt $PreviousBootTicks) {
            "session-loss-reboot"
        } elseif ([Int64]$currentBootTicks -eq $PreviousBootTicks) {
            "session-recycle-same-boot"
        } else {
            throw "Guest boot identity classification failed for package '$PackageKey'."
        }
        return [pscustomobject]@{
            Session = $candidateSession
            Proof = $proof
            Transition = $transition
            BootTicks = [Int64]$currentBootTicks
        }
    } while ((Get-Date) -lt $deadline)

    $safeReconnectError = ConvertTo-SafeGuestDiagnosticText `
        -Value $lastReconnectError `
        -MaxChars 512
    $safeVerificationError = ConvertTo-SafeGuestDiagnosticText `
        -Value $lastVerificationError `
        -MaxChars 1024
    throw (
        "Developer prerequisite package '$PackageKey' did not verify after an exact owned PowerShell Direct " +
        "reconnect within $TimeoutSec seconds." +
        $originalFailureText +
        " Last reconnect failure: $safeReconnectError Last verification failure: $safeVerificationError")
}

function Invoke-GuestDeveloperPrerequisiteWorker {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [ValidateSet("DotNet10", "NodeLts", "WindowsSdk26100", "WebView2", "VisualStudioBuildTools")]
        [string]$PackageKey,
        [Parameter(Mandatory = $true)]
        [bool]$VerifyOnly
    )

    $operationName = if ($VerifyOnly) {
        "Verifying guest developer prerequisite '$PackageKey'"
    } else {
        "Installing guest developer prerequisite '$PackageKey'"
    }
    $output = @(
        Invoke-GuestCommandWithTimeout `
            -Session $Session `
            -OperationName $operationName `
            -TimeoutSec 2400 `
            -ScriptBlock (Get-GuestDeveloperPrerequisiteScriptBlock) `
            -ArgumentList @($PackageKey, $VerifyOnly, 2100)
    )
    $result = Get-RequiredGuestStageResult `
        -Output $output `
        -ExpectedStage "developer-prerequisite"
    if (
        [string]$result.packageKey -cne $PackageKey -or
        (
            -not [bool]$result.verified -and
            -not [bool]$result.needsRestart
        )
    ) {
        throw "Developer prerequisite package '$PackageKey' returned an invalid stage proof."
    }
    return $result
}

function Ensure-GuestDeveloperPrerequisite {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [ValidateSet("DotNet10", "NodeLts", "WindowsSdk26100", "WebView2", "VisualStudioBuildTools")]
        [string]$PackageKey
    )

    Write-Step "Ensuring guest developer prerequisite '$PackageKey'"
    $activeSession = $Session
    $installAttempted = $false
    $transition = "none"
    $recoveredProof = $null
    $previousBootTicks = Get-GuestBootIdentity `
        -Session $activeSession `
        -OperationName "Reading guest boot identity before package '$PackageKey'"
    try {
        try {
            $installResult = Invoke-GuestDeveloperPrerequisiteWorker `
                -Session $activeSession `
                -PackageKey $PackageKey `
                -VerifyOnly $false
            $installAttempted = [bool]$installResult.installed
        } catch {
            $installFailure = $_
            $sessionSurvivedOnSameBoot = $false
            try {
                $currentBootTicks = Get-GuestBootIdentity `
                    -Session $activeSession `
                    -OperationName "Probing guest session after package '$PackageKey' failure"
                if ([Int64]$currentBootTicks -eq $previousBootTicks) {
                    $sessionSurvivedOnSameBoot = $true
                }
            } catch {
                $sessionSurvivedOnSameBoot = $false
            }
            if ($sessionSurvivedOnSameBoot) {
                throw $installFailure
            }

            $installAttempted = $true
            $recovery = Wait-ForGuestPackageTransitionAndVerify `
                -Session $activeSession `
                -GuestCredential $GuestCredential `
                -ResolvedVhdPath $ResolvedVhdPath `
                -ExpectedOwnerId $ExpectedOwnerId `
                -PreviousBootTicks $previousBootTicks `
                -PackageKey $PackageKey `
                -OriginalInstallFailure $installFailure
            $activeSession = $recovery.Session
            $recoveredProof = $recovery.Proof
            $transition = [string]$recovery.Transition
            $installResult = $null
        }

        if ($null -ne $installResult -and [bool]$installResult.needsRestart) {
            if ([bool]$installResult.rebootInitiated) {
                $recovery = Wait-ForGuestPackageTransitionAndVerify `
                    -Session $activeSession `
                    -GuestCredential $GuestCredential `
                    -ResolvedVhdPath $ResolvedVhdPath `
                    -ExpectedOwnerId $ExpectedOwnerId `
                    -PreviousBootTicks $previousBootTicks `
                    -PackageKey $PackageKey `
                    -OriginalInstallFailure $null
                $activeSession = $recovery.Session
                $recoveredProof = $recovery.Proof
                $transition = [string]$recovery.Transition
            } else {
                $transition = "owned-required-reboot"
                $activeSession = Restart-GuestAndReconnect `
                    -Session $activeSession `
                    -GuestCredential $GuestCredential `
                    -ResolvedVhdPath $ResolvedVhdPath `
                    -ExpectedOwnerId $ExpectedOwnerId
            }
        }

        $proof = if ($null -ne $recoveredProof) {
            $recoveredProof
        } elseif (
            $null -eq $installResult -or
            [bool]$installResult.needsRestart
        ) {
            Invoke-GuestDeveloperPrerequisiteWorker `
                -Session $activeSession `
                -PackageKey $PackageKey `
                -VerifyOnly $true
        } else {
            $installResult
        }
        if (-not [bool]$proof.verified) {
            throw "Developer prerequisite package '$PackageKey' was not verified after its stage."
        }
        Write-Host ([pscustomobject][ordered]@{
            stage = [string]$proof.stage
            packageKey = [string]$proof.packageKey
            packageId = [string]$proof.packageId
            packageVersion = [string]$proof.packageVersion
            installerType = [string]$proof.installerType
            customArguments = $proof.customArguments
            alreadyInstalled = [bool]$proof.alreadyInstalled
            installedThisRun = $installAttempted
            transition = $transition
            verification = [string]$proof.verification
        } | ConvertTo-Json -Compress)
        return [pscustomobject]@{
            Session = $activeSession
            Proof = $proof
        }
    } catch {
        if (
            $null -ne $activeSession -and
            $activeSession.InstanceId -ne $Session.InstanceId
        ) {
            Remove-PSSession -Session $activeSession -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Ensure-GuestDeveloperPrerequisites {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId
    )

    $activeSession = $Session
    try {
        foreach ($packageKey in [string[]]@(
                "DotNet10",
                "NodeLts",
                "WindowsSdk26100",
                "WebView2",
                "VisualStudioBuildTools")) {
            $stageResult = Ensure-GuestDeveloperPrerequisite `
                -Session $activeSession `
                -GuestCredential $GuestCredential `
                -ResolvedVhdPath $ResolvedVhdPath `
                -ExpectedOwnerId $ExpectedOwnerId `
                -PackageKey $packageKey
            $activeSession = $stageResult.Session
        }
        return $activeSession
    } catch {
        if (
            $null -ne $activeSession -and
            $activeSession.InstanceId -ne $Session.InstanceId
        ) {
            Remove-PSSession -Session $activeSession -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Get-GuestSetupDevCheckScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [string]$RemoteRepoRoot,
            [Parameter(Mandatory = $true)]
            [int]$NativeTimeoutSec
        )

        function ConvertTo-OpenClawSetupCheckDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [int]$MaximumLength = 1024
            )

            if ([string]::IsNullOrWhiteSpace($Text)) {
                return "<empty>"
            }
            $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
            $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
            $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
            $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
            $safe = ($safe -replace '\s+', ' ').Trim()
            if ($safe.Length -gt $MaximumLength) {
                return $safe.Substring(0, $MaximumLength) + "...<truncated>"
            }
            return $safe
        }

        $setupDevPath = Join-Path $RemoteRepoRoot "scripts\setup-dev.ps1"
        if (-not (Test-Path -LiteralPath $setupDevPath -PathType Leaf)) {
            throw "Guest setup-dev.ps1 is unavailable at the expected repository path."
        }
        if ($setupDevPath.IndexOf('"') -ge 0) {
            throw "Guest setup-dev.ps1 path contains an unsupported quote."
        }
        $captureRoot = Join-Path $env:TEMP (
            "openclaw-setup-check-{0}" -f [Guid]::NewGuid().ToString("N"))
        $stdoutPath = Join-Path $captureRoot "stdout.txt"
        $stderrPath = Join-Path $captureRoot "stderr.txt"
        $cleanupFailure = $null
        try {
            New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
            $arguments = [string[]]@(
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", ('"' + $setupDevPath + '"'),
                "-CheckOnly")
            $process = Start-Process `
                -FilePath (Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe") `
                -ArgumentList $arguments `
                -WorkingDirectory $RemoteRepoRoot `
                -RedirectStandardOutput $stdoutPath `
                -RedirectStandardError $stderrPath `
                -PassThru `
                -WindowStyle Hidden `
                -ErrorAction Stop
            $null = $process.Handle
            if (-not $process.WaitForExit($NativeTimeoutSec * 1000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                throw "Guest setup-dev.ps1 -CheckOnly timed out after $NativeTimeoutSec seconds."
            }
            $process.WaitForExit()
            $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
                [IO.File]::ReadAllText($stdoutPath)
            } else {
                ""
            }
            $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
                [IO.File]::ReadAllText($stderrPath)
            } else {
                ""
            }
            if ([int]$process.ExitCode -ne 0) {
                $safeStdout = ConvertTo-OpenClawSetupCheckDiagnostic -Text $stdout
                $safeStderr = ConvertTo-OpenClawSetupCheckDiagnostic -Text $stderr
                throw (
                    "Guest setup-dev.ps1 -CheckOnly failed with exit code {0}. stdout='{1}' stderr='{2}'" -f
                        $process.ExitCode,
                        $safeStdout,
                        $safeStderr)
            }
            return [pscustomobject][ordered]@{
                stage = "setup-dev-check"
                checkOnly = $true
                exitCode = [int]$process.ExitCode
            }
        } finally {
            if (Test-Path -LiteralPath $captureRoot) {
                try {
                    Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
                } catch {
                    $cleanupFailure = $_.Exception.Message
                }
            }
            if ($cleanupFailure) {
                throw "Guest setup-dev.ps1 -CheckOnly capture cleanup failed: $cleanupFailure"
            }
        }
    }
}

function Get-GuestVerificationSummaryScriptBlock {
    return {
        function ConvertTo-OpenClawVerifyDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [int]$MaximumLength = 1024
            )

            if ([string]::IsNullOrWhiteSpace($Text)) {
                return "<empty>"
            }
            $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
            $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
            $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
            $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
            $safe = ($safe -replace '\s+', ' ').Trim()
            if ($safe.Length -gt $MaximumLength) {
                return $safe.Substring(0, $MaximumLength) + "...<truncated>"
            }
            return $safe
        }

        function Resolve-OpenClawVerifyApplication {
            param(
                [Parameter(Mandatory = $true)]
                [ValidateSet("git.exe", "dotnet.exe", "node.exe", "npm.cmd")]
                [string]$Name
            )

            $commands = @(
                Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue
            )
            if ($commands.Count -ne 1) {
                throw "Guest verification requires exactly one resolved Application named '$Name'."
            }
            $resolvedPath = [IO.Path]::GetFullPath([string]$commands[0].Source)
            if ([IO.Path]::GetFileName($resolvedPath) -cne $Name) {
                throw "Guest verification resolved '$Name' to an unexpected application path."
            }
            return $resolvedPath
        }

        function Invoke-OpenClawVerifyNativeVersion {
            param(
                [Parameter(Mandatory = $true)]
                [string]$Label,
                [Parameter(Mandatory = $true)]
                [string]$ExecutablePath,
                [Parameter(Mandatory = $true)]
                [string]$VersionPattern
            )

            $captureRoot = Join-Path $env:TEMP (
                "openclaw-verify-{0}" -f [Guid]::NewGuid().ToString("N"))
            $stdoutPath = Join-Path $captureRoot "stdout.txt"
            $stderrPath = Join-Path $captureRoot "stderr.txt"
            $cleanupFailure = $null
            try {
                New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
                $process = Start-Process `
                    -FilePath $ExecutablePath `
                    -ArgumentList ([string[]]@("--version")) `
                    -RedirectStandardOutput $stdoutPath `
                    -RedirectStandardError $stderrPath `
                    -PassThru `
                    -WindowStyle Hidden `
                    -ErrorAction Stop
                # Windows PowerShell 5.1 requires opening the handle before exit to retain ExitCode.
                $null = $process.Handle
                if (-not $process.WaitForExit(60000)) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    throw "Guest verification command '$Label' timed out after 60 seconds."
                }
                $process.WaitForExit()
                $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
                    [IO.File]::ReadAllText($stdoutPath)
                } else {
                    ""
                }
                $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
                    [IO.File]::ReadAllText($stderrPath)
                } else {
                    ""
                }
                if ([int]$process.ExitCode -ne 0) {
                    $safeStdout = ConvertTo-OpenClawVerifyDiagnostic -Text $stdout
                    $safeStderr = ConvertTo-OpenClawVerifyDiagnostic -Text $stderr
                    throw (
                        "Guest verification command '$Label' failed with exit code $($process.ExitCode). " +
                        "stdout='$safeStdout' stderr='$safeStderr'")
                }
                $version = $stdout.Trim()
                if ($version -notmatch $VersionPattern) {
                    $safeVersion = ConvertTo-OpenClawVerifyDiagnostic -Text $version
                    throw "Guest verification command '$Label' returned invalid version '$safeVersion'."
                }
                return $version
            } finally {
                if (Test-Path -LiteralPath $captureRoot) {
                    try {
                        Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
                    } catch {
                        $cleanupFailure = $_.Exception.Message
                    }
                }
                if ($cleanupFailure) {
                    throw "Guest verification capture cleanup failed for '$Label': $cleanupFailure"
                }
            }
        }

        $windowsSdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
        if (-not (Test-Path -LiteralPath $windowsSdkPath -PathType Container)) {
            throw "Windows SDK is not present in the guest."
        }

        $gitPath = Resolve-OpenClawVerifyApplication -Name "git.exe"
        $dotnetPath = Resolve-OpenClawVerifyApplication -Name "dotnet.exe"
        $nodePath = Resolve-OpenClawVerifyApplication -Name "node.exe"
        $npmPath = Resolve-OpenClawVerifyApplication -Name "npm.cmd"
        [ordered]@{
            computerName = $env:COMPUTERNAME
            userName = [Environment]::UserName
            gitVersion = Invoke-OpenClawVerifyNativeVersion `
                -Label "git.exe" `
                -ExecutablePath $gitPath `
                -VersionPattern '^git version \d+\.\d+\.\d+(?:\.[0-9A-Za-z.-]+)?$'
            dotnetVersion = Invoke-OpenClawVerifyNativeVersion `
                -Label "dotnet.exe" `
                -ExecutablePath $dotnetPath `
                -VersionPattern '^10\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'
            nodeVersion = Invoke-OpenClawVerifyNativeVersion `
                -Label "node.exe" `
                -ExecutablePath $nodePath `
                -VersionPattern '^v\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'
            npmVersion = Invoke-OpenClawVerifyNativeVersion `
                -Label "npm.cmd" `
                -ExecutablePath $npmPath `
                -VersionPattern '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$'
            windowsSdkPresent = $true
        } | ConvertTo-Json -Depth 5
    }
}

function Prepare-GuestPrerequisites {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId
    )

    Write-Step "Preparing guest prerequisites"
    $activeSession = $Session
    try {
        $featureResult = Invoke-GuestOptionalFeatureStage -Session $activeSession
        if ([bool]$featureResult.needsRestart) {
            Write-InfoLine "Guest restart is required to complete optional feature enablement."
            $activeSession = Restart-GuestAndReconnect `
                -Session $activeSession `
                -GuestCredential $GuestCredential `
                -ResolvedVhdPath $ResolvedVhdPath `
                -ExpectedOwnerId $ExpectedOwnerId
        }

        Install-GuestWslNativeHelper -Session $activeSession
        $packageResult = Invoke-GuestWslPackageStage -Session $activeSession
        if ([bool]$packageResult.needsRestart) {
            Write-InfoLine "Guest restart is required to complete WSL package preparation."
            $activeSession = Restart-GuestAndReconnect `
                -Session $activeSession `
                -GuestCredential $GuestCredential `
                -ResolvedVhdPath $ResolvedVhdPath `
                -ExpectedOwnerId $ExpectedOwnerId
            Install-GuestWslNativeHelper -Session $activeSession
        }

        $wslProof = Invoke-GuestWslVerificationStage -Session $activeSession
        Write-InfoLine "Guest WSL package verification proof:"
        Write-Host ([pscustomobject][ordered]@{
            scope = [string]$wslProof.scope
            normalizedState = [string]$wslProof.normalizedState
            statusExitCode = [int]$wslProof.statusExitCode
            versionExitCode = [int]$wslProof.versionExitCode
            status = [string]$wslProof.status
            version = [string]$wslProof.version
        } | ConvertTo-Json -Compress)

        $wingetProof = Ensure-GuestWingetAvailable -Session $activeSession
        Write-InfoLine "Guest WinGet bootstrap proof:"
        Write-Host ([pscustomobject][ordered]@{
            stage = [string]$wingetProof.Stage
            alreadyInstalled = [bool]$wingetProof.AlreadyInstalled
            version = [string]$wingetProof.Version
            sourceCatalogAcquisition = [string]$wingetProof.SourceCatalogAcquisition
            sourceCatalogVersion = [string]$wingetProof.SourceCatalogVersion
            sourceCatalogSha256 = $wingetProof.SourceCatalogSha256
        } | ConvertTo-Json -Compress)
        Ensure-GuestGitInstalled -Session $activeSession
        Ensure-GuestPowerShell7Installed -Session $activeSession
        $activeSession = Ensure-GuestDeveloperPrerequisites `
            -Session $activeSession `
            -GuestCredential $GuestCredential `
            -ResolvedVhdPath $ResolvedVhdPath `
            -ExpectedOwnerId $ExpectedOwnerId
        Copy-RepoToGuest -Session $activeSession

        $guestRepoRoot = Get-GuestRepoRoot
        $setupCheckOutput = @(
            Invoke-GuestCommandWithTimeout `
                -Session $activeSession `
                -OperationName "Running guest setup-dev.ps1 -CheckOnly" `
                -TimeoutSec $GuestCommandTimeoutSec `
                -ScriptBlock (Get-GuestSetupDevCheckScriptBlock) `
                -ArgumentList @(
                    $guestRepoRoot,
                    [Math]::Max(30, ($GuestCommandTimeoutSec - 60)))
        )
        $setupCheckResult = Get-RequiredGuestStageResult `
            -Output $setupCheckOutput `
            -ExpectedStage "setup-dev-check"
        if (-not [bool]$setupCheckResult.checkOnly -or [int]$setupCheckResult.exitCode -ne 0) {
            throw "Guest setup-dev.ps1 -CheckOnly returned an invalid stage proof."
        }

        return $activeSession
    } catch {
        if (
            $null -ne $activeSession -and
            $activeSession.InstanceId -ne $Session.InstanceId
        ) {
            Remove-PSSession -Session $activeSession -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Verify-HostVmConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [object]$VmObject
    )

    if ([int]$VmObject.Generation -ne 2) {
        throw "VM '$VMName' is not Generation 2."
    }

    $processor = Get-VMProcessor -VMName $VMName -ErrorAction Stop
    if (-not [bool](Get-PropertyValueOrNull -Object $processor -Name "ExposeVirtualizationExtensions")) {
        throw "VM '$VMName' does not expose virtualization extensions."
    }

    $firmware = Get-VMFirmware -VMName $VMName -ErrorAction Stop
    $secureBootValue = Get-PropertyValueOrNull -Object $firmware -Name "SecureBoot"
    if ($null -eq $secureBootValue) {
        $secureBootValue = Get-PropertyValueOrNull -Object $firmware -Name "EnableSecureBoot"
    }
    $secureBootEnabled = if ($secureBootValue -is [bool]) {
        [bool]$secureBootValue
    } else {
        [string]::Equals([string]$secureBootValue, "On", [StringComparison]::OrdinalIgnoreCase)
    }
    $hostReportedSecureBoot = if ($null -eq $secureBootValue) { "<null>" } else { [string]$secureBootValue }
    if (-not $secureBootEnabled) {
        throw "VM '$VMName' secure boot is not enabled for attempted canonical template identifier '$script:WindowsSecureBootTemplate'. Host-reported SecureBoot value: '$hostReportedSecureBoot'."
    }

    $secureBootTemplateValue = Get-PropertyValueOrNull -Object $firmware -Name "SecureBootTemplate"
    $hostReportedSecureBootTemplate = if ($null -eq $secureBootTemplateValue) {
        "<null>"
    } else {
        [string]$secureBootTemplateValue
    }
    if (
        (Normalize-SecureBootTemplate -Value $secureBootTemplateValue) -cne
            (Normalize-SecureBootTemplate -Value $script:WindowsSecureBootTemplate)
    ) {
        throw "VM '$VMName' secure boot template does not match attempted canonical identifier '$script:WindowsSecureBootTemplate'. Host-reported SecureBootTemplate value: '$hostReportedSecureBootTemplate'."
    }

    $secureBootTemplateIdProperty = $firmware.PSObject.Properties["SecureBootTemplateId"]
    if ($null -ne $secureBootTemplateIdProperty) {
        $secureBootTemplateIdValue = $secureBootTemplateIdProperty.Value
        $hostReportedSecureBootTemplateId = if ($null -eq $secureBootTemplateIdValue) {
            "<null>"
        } else {
            [string]$secureBootTemplateIdValue
        }
        $parsedSecureBootTemplateId = [Guid]::Empty
        if (
            -not [Guid]::TryParse(
                [string]$secureBootTemplateIdValue,
                [ref]$parsedSecureBootTemplateId
            ) -or
            $parsedSecureBootTemplateId -eq [Guid]::Empty
        ) {
            throw "VM '$VMName' secure boot template ID is invalid for attempted canonical identifier '$script:WindowsSecureBootTemplate'. Host-reported SecureBootTemplate value: '$hostReportedSecureBootTemplate'. Host-reported SecureBootTemplateId value: '$hostReportedSecureBootTemplateId'."
        }
    }

    $security = Get-VMSecurity -VMName $VMName -ErrorAction Stop
    $tpmEnabled = Get-PropertyValueOrNull -Object $security -Name "TpmEnabled"
    if ($null -eq $tpmEnabled) {
        $keyProtector = Get-VMKeyProtector -VMName $VMName -ErrorAction Stop
        $tpmEnabled = Test-KeyProtectorPresent -KeyProtector $keyProtector
    }
    if ($null -eq $tpmEnabled -or -not [bool]$tpmEnabled) {
        throw "VM '$VMName' does not have vTPM enabled."
    }

    if ([bool](Get-PropertyValueOrNull -Object $VmObject -Name "AutomaticCheckpointsEnabled")) {
        throw "VM '$VMName' has automatic checkpoints enabled."
    }
}

function Invoke-PreparedGuestOperation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Restore-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $script:PreparedCheckpointName
    Ensure-VMRunning
    $session = $null
    try {
        $session = Open-GuestSession -GuestCredential $GuestCredential -TimeoutSec $PowerShellDirectTimeoutSec
        & $Action $session
    } finally {
        if ($null -ne $session) {
            Stop-VMGracefully -Session $session -TimeoutSec $GuestShutdownTimeoutSec
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
        } else {
            Stop-VMGracefully -Session $null -TimeoutSec $GuestShutdownTimeoutSec
        }

        Restore-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $script:PreparedCheckpointName
    }
}

function Get-SmokeArtifactPackageScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [string]$ArtifactRoot,
            [Parameter(Mandatory = $true)]
            [string]$OwnedArtifactsRoot,
            [Parameter(Mandatory = $true)]
            [Int64]$MaximumArchiveBytes,
            [Parameter(Mandatory = $true)]
            [Int64]$MaximumExpandedBytes,
            [Parameter(Mandatory = $true)]
            [int]$MaximumFiles
        )

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = "Stop"
        Add-Type -AssemblyName System.IO.Compression.FileSystem

        function Assert-OpenClawSmokeArtifactRelativePath {
            param([Parameter(Mandatory = $true)][string]$Path)

            if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOf([char]0) -ge 0) {
                throw "Smoke artifact path is empty or contains a null character."
            }
            $normalized = $Path.Replace("\", "/")
            if (
                $normalized.StartsWith("/", [StringComparison]::Ordinal) -or
                $normalized.StartsWith("//", [StringComparison]::Ordinal) -or
                $normalized -match '^[A-Za-z]:' -or
                [IO.Path]::IsPathRooted($normalized)
            ) {
                throw "Smoke artifact path '$Path' is absolute."
            }
            $trimmed = $normalized.TrimEnd("/")
            foreach ($segment in @($trimmed.Split("/"))) {
                if (
                    [string]::IsNullOrWhiteSpace($segment) -or
                    $segment -ceq "." -or
                    $segment -ceq ".." -or
                    $segment.IndexOf(":") -ge 0 -or
                    $segment -match '[<>:"|?*\x00-\x1F]' -or
                    $segment.EndsWith(".", [StringComparison]::Ordinal) -or
                    $segment.EndsWith(" ", [StringComparison]::Ordinal)
                ) {
                    throw "Smoke artifact path '$Path' is not a safe Windows relative path."
                }
                $deviceName = ($segment.Split(".")[0]).ToUpperInvariant()
                if ($deviceName -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                    throw "Smoke artifact path '$Path' contains reserved Windows name '$segment'."
                }
            }
            return $trimmed
        }

        function Get-OpenClawSmokeArtifactSha256 {
            param([Parameter(Mandatory = $true)][string]$Path)

            $stream = [IO.File]::OpenRead($Path)
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                return (($algorithm.ComputeHash($stream) |
                    ForEach-Object { $_.ToString("X2") }) -join "")
            } finally {
                $algorithm.Dispose()
                $stream.Dispose()
            }
        }

        $canonicalRoot = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd("\")
        $canonicalOwnedArtifactsRoot = [IO.Path]::GetFullPath($OwnedArtifactsRoot).TrimEnd("\")
        if (-not [string]::Equals(
            (Split-Path -Parent $canonicalRoot),
            $canonicalOwnedArtifactsRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Smoke artifact root does not match the exact owned lane path."
        }
        if (@("installed-smoke", "upgrade-smoke") -cnotcontains (Split-Path -Leaf $canonicalRoot)) {
            throw "Smoke artifact root has an unexpected lane directory name."
        }
        if (-not (Test-Path -LiteralPath $canonicalRoot -PathType Container)) {
            throw "Smoke artifact root does not exist."
        }
        $rootItem = Get-Item -LiteralPath $canonicalRoot -Force -ErrorAction Stop
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Smoke artifact root is a reparse point."
        }

        $items = @(Get-ChildItem -LiteralPath $canonicalRoot -Recurse -Force -ErrorAction Stop)
        $reparseItems = @(
            $items | Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            })
        if ($reparseItems.Count -ne 0) {
            throw "Smoke artifacts contain $($reparseItems.Count) reparse points."
        }
        $files = @($items | Where-Object { -not $_.PSIsContainer })
        if ($files.Count -le 0 -or $files.Count -gt $MaximumFiles) {
            throw "Smoke artifact file count '$($files.Count)' is outside the bounded range."
        }
        [Int64]$expandedBytes = 0
        foreach ($file in $files) {
            $relativePath = $file.FullName.Substring($canonicalRoot.Length).TrimStart("\")
            [void](Assert-OpenClawSmokeArtifactRelativePath -Path $relativePath)
            $expandedBytes += [Int64]$file.Length
            if ($expandedBytes -gt $MaximumExpandedBytes) {
                throw "Smoke artifact expanded size exceeds the bounded maximum."
            }
        }

        $archiveName = ".openclaw-smoke-artifacts-{0}.zip" -f [Guid]::NewGuid().ToString("N")
        $archivePath = Join-Path (Split-Path -Parent $canonicalRoot) $archiveName
        if (Test-Path -LiteralPath $archivePath) {
            throw "Nonce smoke artifact archive path already exists."
        }
        try {
            [IO.Compression.ZipFile]::CreateFromDirectory(
                $canonicalRoot,
                $archivePath,
                [IO.Compression.CompressionLevel]::Optimal,
                $false)
            $archiveItem = Get-Item -LiteralPath $archivePath -Force -ErrorAction Stop
            if (
                [Int64]$archiveItem.Length -le 0 -or
                [Int64]$archiveItem.Length -gt $MaximumArchiveBytes
            ) {
                throw "Smoke artifact archive size is outside the bounded range."
            }
            return [pscustomobject][ordered]@{
                stage = "smoke-artifact-package"
                archivePath = $archivePath
                archiveName = $archiveName
                archiveSize = [Int64]$archiveItem.Length
                sha256 = Get-OpenClawSmokeArtifactSha256 -Path $archivePath
                fileCount = [int]$files.Count
                expandedBytes = $expandedBytes
            }
        } catch {
            if (Test-Path -LiteralPath $archivePath) {
                Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
            }
            throw
        }
    }
}

function Get-SmokeArtifactSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return (($algorithm.ComputeHash($stream) |
            ForEach-Object { $_.ToString("X2") }) -join "")
    } finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Assert-SmokeArtifactRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOf([char]0) -ge 0) {
        throw "Smoke artifact archive path is empty or contains a null character."
    }
    $normalized = $Path.Replace("\", "/")
    if (
        $normalized.StartsWith("/", [StringComparison]::Ordinal) -or
        $normalized.StartsWith("//", [StringComparison]::Ordinal) -or
        $normalized -match '^[A-Za-z]:' -or
        [IO.Path]::IsPathRooted($normalized)
    ) {
        throw "Smoke artifact archive path '$Path' is absolute."
    }
    $trimmed = $normalized.TrimEnd("/")
    foreach ($segment in @($trimmed.Split("/"))) {
        if (
            [string]::IsNullOrWhiteSpace($segment) -or
            $segment -ceq "." -or
            $segment -ceq ".." -or
            $segment.IndexOf(":") -ge 0 -or
            $segment -match '[<>:"|?*\x00-\x1F]' -or
            $segment.EndsWith(".", [StringComparison]::Ordinal) -or
            $segment.EndsWith(" ", [StringComparison]::Ordinal)
        ) {
            throw "Smoke artifact archive path '$Path' is not a safe Windows relative path."
        }
        $deviceName = ($segment.Split(".")[0]).ToUpperInvariant()
        if ($deviceName -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Smoke artifact archive path '$Path' contains reserved Windows name '$segment'."
        }
    }
    return $trimmed
}

function Expand-VerifiedSmokeArtifactArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot,
        [Parameter(Mandatory = $true)]
        [object]$PackageProof,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Installed", "Upgrade")]
        [string]$Lane
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveItem = Get-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
    if (
        ($archiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [Int64]$archiveItem.Length -ne [Int64]$PackageProof.archiveSize -or
        [Int64]$archiveItem.Length -le 0 -or
        [Int64]$archiveItem.Length -gt $script:SmokeArtifactArchiveMaximumBytes
    ) {
        throw "Smoke artifact archive size or file type does not match guest proof."
    }
    $actualSha256 = Get-SmokeArtifactSha256 -Path $ArchivePath
    if (-not [string]::Equals(
        $actualSha256,
        [string]$PackageProof.sha256,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Smoke artifact archive SHA256 mismatch."
    }

    $seenPaths = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $files = New-Object 'Collections.Generic.List[object]'
    [Int64]$expandedBytes = 0
    try {
        foreach ($entry in $archive.Entries) {
            $isDirectory = [string]::IsNullOrEmpty($entry.Name)
            $safePath = Assert-SmokeArtifactRelativePath -Path $entry.FullName
            if (-not $seenPaths.Add($safePath)) {
                throw "Smoke artifact archive contains duplicate path '$safePath'."
            }
            $attributeBits = [BitConverter]::ToUInt32(
                [BitConverter]::GetBytes([int]$entry.ExternalAttributes),
                0)
            $unixType = [int](($attributeBits -shr 16) -band 0xF000)
            if ($isDirectory) {
                if ($unixType -ne 0 -and $unixType -ne 0x4000) {
                    throw "Smoke artifact directory '$safePath' has an unexpected entry type."
                }
                continue
            }
            if ($unixType -ne 0 -and $unixType -ne 0x8000) {
                throw "Smoke artifact file '$safePath' has an unexpected reparse or archive type."
            }
            $files.Add([pscustomobject]@{ Entry = $entry; RelativePath = $safePath })
            if ($files.Count -gt $script:SmokeArtifactMaximumFiles) {
                throw "Smoke artifact archive file count exceeds the bounded maximum."
            }
            $expandedBytes += [Int64]$entry.Length
            if ($expandedBytes -gt $script:SmokeArtifactExpandedMaximumBytes) {
                throw "Smoke artifact archive expanded size exceeds the bounded maximum."
            }
        }
        if (
            $files.Count -ne [int]$PackageProof.fileCount -or
            $expandedBytes -ne [Int64]$PackageProof.expandedBytes
        ) {
            throw "Smoke artifact archive count or expanded size does not match guest proof."
        }

        $canonicalDestination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd("\")
        New-Item -ItemType Directory -Path $canonicalDestination -Force -ErrorAction Stop | Out-Null
        foreach ($file in $files) {
            $destinationPath = [IO.Path]::GetFullPath(
                (Join-Path $canonicalDestination $file.RelativePath.Replace("/", "\")))
            if (-not $destinationPath.StartsWith(
                $canonicalDestination + "\",
                [StringComparison]::OrdinalIgnoreCase)) {
                throw "Smoke artifact extraction path escaped the host artifact root."
            }
            $destinationParent = Split-Path -Parent $destinationPath
            New-Item -ItemType Directory -Path $destinationParent -Force -ErrorAction Stop | Out-Null
            $entryStream = $file.Entry.Open()
            $outputStream = [IO.File]::Open(
                $destinationPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $entryStream.CopyTo($outputStream)
            } finally {
                $outputStream.Dispose()
                $entryStream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }

    $requiredArtifacts = if ($Lane -eq "Upgrade") {
        @(
            "phase-status.json",
            "upgrade-smoke.log",
            "upgrade-smoke.done",
            "inno-install-previous.log",
            "inno-install-current.log",
            "installed-runtime-proof\phase-status.json")
    } else {
        @("phase-status.json", "installed-smoke.log")
    }
    foreach ($requiredArtifact in $requiredArtifacts) {
        if (-not (Test-Path -LiteralPath (Join-Path $DestinationRoot $requiredArtifact) -PathType Leaf)) {
            throw "Retrieved $Lane smoke artifacts are missing '$requiredArtifact'."
        }
    }
    return [pscustomobject]@{
        fileCount = [int]$files.Count
        expandedBytes = $expandedBytes
        sha256 = $actualSha256.ToUpperInvariant()
    }
}

function Resolve-HostArtifactBasePath {
    if (-not [string]::IsNullOrWhiteSpace($HostArtifactRoot)) {
        if ([IO.Path]::IsPathRooted($HostArtifactRoot)) {
            return (Resolve-FullPath -Path $HostArtifactRoot)
        }

        return (Resolve-FullPath -Path (Join-Path $script:RepoRoot $HostArtifactRoot))
    }

    return (Join-Path $script:RepoRoot ("TestResults\CleanWindowsHyperV\{0}" -f $VMName))
}

function New-SmokeArtifactRunDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [string[]]$CandidateNames
    )

    $canonicalBase = [IO.Path]::GetFullPath($BasePath).TrimEnd("\")
    if (
        [string]::Equals(
            $canonicalBase,
            [IO.Path]::GetPathRoot($canonicalBase).TrimEnd("\"),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Smoke artifact base cannot be a drive root."
    }
    if (-not (Test-Path -LiteralPath $canonicalBase)) {
        New-Item -ItemType Directory -Path $canonicalBase -ErrorAction Stop | Out-Null
    }
    $baseItem = Get-Item -LiteralPath $canonicalBase -Force -ErrorAction Stop
    if (
        -not $baseItem.PSIsContainer -or
        ($baseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Smoke artifact base is not a directory or is a reparse point."
    }

    $names = New-Object 'Collections.Generic.List[string]'
    if ($null -ne $CandidateNames -and $CandidateNames.Count -gt 0) {
        foreach ($candidateName in $CandidateNames) {
            [void]$names.Add([string]$candidateName)
        }
    } else {
        for ($attempt = 0; $attempt -lt 16; $attempt++) {
            $timestamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss-fff")
            $nonce = [Guid]::NewGuid().ToString("N").Substring(0, 8)
            [void]$names.Add("$timestamp-$nonce")
        }
    }

    foreach ($name in $names) {
        if ($name -cnotmatch '^\d{8}-\d{6}-\d{3}-[0-9a-f]{8}$') {
            throw "Smoke artifact run directory name is not a safe generated segment."
        }
        $candidate = [IO.Path]::GetFullPath((Join-Path $canonicalBase $name))
        if (-not $candidate.StartsWith(
            $canonicalBase + "\",
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Smoke artifact run directory escaped its canonical base."
        }
        if (Test-Path -LiteralPath $candidate) {
            $existing = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
            if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Smoke artifact run directory collision is a reparse point."
            }
            continue
        }
        try {
            New-Item -ItemType Directory -Path $candidate -ErrorAction Stop | Out-Null
        } catch {
            if (Test-Path -LiteralPath $candidate) {
                $racedItem = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
                if (($racedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Smoke artifact run directory collision is a reparse point."
                }
                continue
            }
            throw
        }
        $created = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        if (
            -not $created.PSIsContainer -or
            ($created.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($created.FullName).TrimEnd("\"),
                $candidate.TrimEnd("\"),
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw "Created smoke artifact run directory failed identity verification."
        }
        return $candidate
    }
    throw "Unable to allocate a unique smoke artifact run directory after $($names.Count) bounded attempts."
}

function Get-SmokeValidationCompletionProbeScriptBlock {
    return {
        param(
            [Parameter(Mandatory = $true)]
            [string]$ArtifactRoot,
            [Parameter(Mandatory = $true)]
            [string]$OwnedArtifactsRoot,
            [Parameter(Mandatory = $true)]
            [ValidateSet("Installed", "Upgrade")]
            [string]$Lane,
            [Parameter(Mandatory = $true)]
            [int]$TimeoutSec,
            [Parameter(Mandatory = $true)]
            [int]$PollIntervalMilliseconds,
            [bool]$PollOnce = $false
        )

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = "Stop"

        function ConvertTo-OpenClawSmokeCompletionDiagnostic {
            param(
                [AllowNull()]
                [string]$Text,
                [int]$MaximumLength = 4096
            )

            if ([string]::IsNullOrWhiteSpace($Text)) {
                return "<unavailable>"
            }
            $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
            $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
            $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
            $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
            $safe = ($safe -replace '\s+', ' ').Trim()
            if ($safe.Length -gt $MaximumLength) {
                return $safe.Substring(0, $MaximumLength) + "...<truncated>"
            }
            return $safe
        }

        if ($TimeoutSec -lt 1 -or $PollIntervalMilliseconds -lt 50) {
            throw "Smoke validation completion bounds are invalid."
        }
        $canonicalRoot = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd("\")
        $canonicalOwnedRoot = [IO.Path]::GetFullPath($OwnedArtifactsRoot).TrimEnd("\")
        $expectedLaneRoot = if ($Lane -eq "Upgrade") { "upgrade-smoke" } else { "installed-smoke" }
        if (
            -not [string]::Equals(
                (Split-Path -Parent $canonicalRoot),
                $canonicalOwnedRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $canonicalRoot) -cne $expectedLaneRoot
        ) {
            throw "Smoke validation completion root does not match the exact owned lane path."
        }

        $doneName = if ($Lane -eq "Upgrade") { "upgrade-smoke.done" } else { "installed-smoke.done" }
        $logName = if ($Lane -eq "Upgrade") { "upgrade-smoke.log" } else { "installed-smoke.log" }
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do {
            if (Test-Path -LiteralPath $canonicalRoot -PathType Container) {
                $rootItem = Get-Item -LiteralPath $canonicalRoot -Force -ErrorAction Stop
                if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Smoke validation completion root is a reparse point."
                }
                $donePath = Join-Path $canonicalRoot $doneName
                $phaseStatusPath = Join-Path $canonicalRoot "phase-status.json"
                if (
                    (Test-Path -LiteralPath $donePath -PathType Leaf) -and
                    (Test-Path -LiteralPath $phaseStatusPath -PathType Leaf)
                ) {
                    foreach ($requiredPath in [string[]]@($donePath, $phaseStatusPath)) {
                        $item = Get-Item -LiteralPath $requiredPath -Force -ErrorAction Stop
                        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw "Smoke validation completion evidence contains a reparse point."
                        }
                    }
                    $doneValue = (Get-Content -LiteralPath $donePath -Raw -ErrorAction Stop).Trim()
                    if ($doneValue -notmatch '^[01]$') {
                        throw "Smoke validation completion marker has invalid content."
                    }
                    $phaseDiagnostic = ConvertTo-OpenClawSmokeCompletionDiagnostic `
                        -Text (Get-Content -LiteralPath $phaseStatusPath -Raw -ErrorAction Stop)
                    $logPath = Join-Path $canonicalRoot $logName
                    $logDiagnostic = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                        ConvertTo-OpenClawSmokeCompletionDiagnostic `
                            -Text ((Get-Content -LiteralPath $logPath -Tail 40 -ErrorAction SilentlyContinue) -join "`n")
                    } else {
                        "<$logName unavailable>"
                    }
                    return [pscustomobject][ordered]@{
                        stage = "smoke-validation-completion"
                        complete = $true
                        lane = $Lane
                        exitCode = [int]$doneValue
                        phaseDiagnostic = $phaseDiagnostic
                        logDiagnostic = $logDiagnostic
                    }
                }
            }
            if ($PollOnce) {
                return [pscustomobject][ordered]@{
                    stage = "smoke-validation-completion"
                    complete = $false
                    lane = $Lane
                    exitCode = $null
                    phaseDiagnostic = $null
                    logDiagnostic = $null
                }
            }
            Start-Sleep -Milliseconds $PollIntervalMilliseconds
        } while ((Get-Date) -lt $deadline)

        throw "$Lane smoke validation did not produce closed completion evidence within $TimeoutSec seconds."
    }
}

function Get-SmokeArtifactRetrievalSession {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVmId,
        [ValidateRange(1, 120)]
        [int]$RecoveryTimeoutSec = 120
    )

    try {
        [void](Get-GuestBootIdentity `
            -Session $Session `
            -OperationName "Probing guest session for smoke artifact retrieval")
        return [pscustomobject]@{ Session = $Session; Reconnected = $false }
    } catch {
        Remove-PSSession -Session $Session -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds($RecoveryTimeoutSec)
    $lastError = $null
    do {
        $candidate = $null
        try {
            $vm = Assert-OwnedVM `
                -ResolvedVhdPath $ResolvedVhdPath `
                -ExpectedOwnerId $ExpectedOwnerId
            $candidateVmId = ([Guid]$vm.Id).ToString("D")
            if ($candidateVmId -cne $ExpectedVmId) {
                throw "Exact owned VM identity changed before smoke artifact retrieval."
            }
            if ([string]$vm.State -cne "Running") {
                throw "Exact owned VM is not Running before smoke artifact retrieval."
            }
            $candidate = Open-GuestSession `
                -GuestCredential $GuestCredential `
                -TimeoutSec ([Math]::Min(
                    30,
                    [Math]::Max(1, [int][Math]::Ceiling(($deadline - (Get-Date)).TotalSeconds))))
            [void](Get-GuestBootIdentity `
                -Session $candidate `
                -OperationName "Validating reconnected smoke artifact session")
            return [pscustomobject]@{ Session = $candidate; Reconnected = $true }
        } catch {
            $lastError = $_
            if ($null -ne $candidate) {
                Remove-PSSession -Session $candidate -ErrorAction SilentlyContinue
            }
        }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    $safeLastError = ConvertTo-SafeGuestDiagnosticText -Value $lastError -MaxChars 1024
    throw "Smoke artifact session recovery timed out after $RecoveryTimeoutSec seconds. Last error: $safeLastError"
}

function Test-SmokeValidationTransportLoss {
    param([Parameter(Mandatory = $true)][object]$ErrorRecord)

    $text = [string]$ErrorRecord
    return (
        $text.IndexOf(
            "System.Management.Automation.Remoting.PSRemotingTransportException",
            [StringComparison]::Ordinal) -ge 0 -and
        $text.IndexOf(
            "The Hyper-V socket target process has ended.",
            [StringComparison]::Ordinal) -ge 0)
}

function Wait-SmokeValidationCompletionWithRecovery {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVmId,
        [Parameter(Mandatory = $true)]
        [string]$GuestArtifactRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Installed", "Upgrade")]
        [string]$Lane,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSec
    )

    $activeSession = $Session
    $ownsActiveSession = $false
    $sessionRecovered = $false
    $recoveryAttempts = 0
    $transportLossCount = 0
    $firstTransportError = $null
    $lastTransportError = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    try {
        while ((Get-Date) -lt $deadline) {
            $remainingSeconds = [int][Math]::Ceiling(($deadline - (Get-Date)).TotalSeconds)
            if ($remainingSeconds -lt 1) {
                break
            }
            try {
                $sessionInfo = Get-SmokeArtifactRetrievalSession `
                    -Session $activeSession `
                    -GuestCredential $GuestCredential `
                    -ResolvedVhdPath $ResolvedVhdPath `
                    -ExpectedOwnerId $ExpectedOwnerId `
                    -ExpectedVmId $ExpectedVmId `
                    -RecoveryTimeoutSec ([Math]::Min(120, $remainingSeconds))
                $activeSession = $sessionInfo.Session
                if ([bool]$sessionInfo.Reconnected) {
                    $ownsActiveSession = $true
                    $sessionRecovered = $true
                    $recoveryAttempts++
                }
                $remainingSeconds = [int][Math]::Ceiling(($deadline - (Get-Date)).TotalSeconds)
                if ($remainingSeconds -lt 1) {
                    throw "$Lane smoke validation completion recovery exceeded its $TimeoutSec second deadline."
                }
                $probeOutput = @(
                    Invoke-GuestCommandWithTimeout `
                        -Session $activeSession `
                        -OperationName "Polling $Lane smoke validation completion after transport loss" `
                        -TimeoutSec ([Math]::Min(30, $remainingSeconds)) `
                        -ScriptBlock (Get-SmokeValidationCompletionProbeScriptBlock) `
                        -ArgumentList @(
                            $GuestArtifactRoot,
                            (Split-Path -Parent $GuestArtifactRoot),
                            $Lane,
                            1,
                            1000,
                            $true)
                )
                $proof = Get-RequiredGuestStageResult `
                    -Output $probeOutput `
                    -ExpectedStage "smoke-validation-completion"
                if ([bool]$proof.complete) {
                    $proof | Add-Member -NotePropertyName recoveryAttempts -NotePropertyValue $recoveryAttempts
                    $proof | Add-Member -NotePropertyName sessionRecovered -NotePropertyValue $sessionRecovered
                    return $proof
                }
                Start-Sleep -Seconds 2
            } catch {
                if (-not (Test-SmokeValidationTransportLoss -ErrorRecord $_)) {
                    throw
                }
                $transportLossCount++
                if ($null -eq $firstTransportError) {
                    $firstTransportError = $_
                }
                $lastTransportError = $_
                Remove-PSSession -Session $activeSession -ErrorAction SilentlyContinue
                $activeSession = $Session
                if ((Get-Date) -lt $deadline) {
                    Start-Sleep -Seconds 2
                }
            }
        }

        if ($transportLossCount -gt 0) {
            $safeFirst = ConvertTo-SafeGuestDiagnosticText -Value $firstTransportError -MaxChars 1024
            $safeLast = ConvertTo-SafeGuestDiagnosticText -Value $lastTransportError -MaxChars 1024
            throw (
                "$Lane smoke validation did not produce closed completion evidence within " +
                "$TimeoutSec seconds after $transportLossCount transport losses and " +
                "$recoveryAttempts exact-VM reconnects. First error: $safeFirst Last error: $safeLast")
        }
        throw "$Lane smoke validation did not produce closed completion evidence within $TimeoutSec seconds."
    } finally {
        if ($ownsActiveSession -and $null -ne $activeSession) {
            Remove-PSSession -Session $activeSession -ErrorAction SilentlyContinue
        }
    }
}

function Receive-SmokeArtifactArchive {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [string]$GuestArtifactRoot,
        [Parameter(Mandatory = $true)]
        [string]$HostArtifactRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Installed", "Upgrade")]
        [string]$Lane
    )

    $hostArchivePath = $null
    $guestArchivePath = $null
    $primaryError = $null
    $cleanupError = $null
    $retrievalProof = $null
    try {
        $packageOutput = @(
            Invoke-GuestCommandWithTimeout `
                -Session $Session `
                -OperationName "Packaging $Lane smoke artifacts" `
                -TimeoutSec 300 `
                -ScriptBlock (Get-SmokeArtifactPackageScriptBlock) `
                -ArgumentList @(
                    $GuestArtifactRoot,
                    (Split-Path -Parent $GuestArtifactRoot),
                    $script:SmokeArtifactArchiveMaximumBytes,
                    $script:SmokeArtifactExpandedMaximumBytes,
                    $script:SmokeArtifactMaximumFiles)
        )
        $packageProof = Get-RequiredGuestStageResult `
            -Output $packageOutput `
            -ExpectedStage "smoke-artifact-package"
        $guestArchivePath = [string]$packageProof.archivePath
        $expectedGuestArchiveParent = [IO.Path]::GetFullPath(
            (Split-Path -Parent $GuestArtifactRoot)).TrimEnd("\")
        $actualGuestArchiveParent = if ([string]::IsNullOrWhiteSpace($guestArchivePath)) {
            ""
        } else {
            [IO.Path]::GetFullPath((Split-Path -Parent $guestArchivePath)).TrimEnd("\")
        }
        if (
            [string]::IsNullOrWhiteSpace($guestArchivePath) -or
            -not [string]::Equals(
                $actualGuestArchiveParent,
                $expectedGuestArchiveParent,
                [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($guestArchivePath) -cne [string]$packageProof.archiveName -or
            [string]$packageProof.archiveName -notmatch '^\.openclaw-smoke-artifacts-[0-9a-f]{32}\.zip$'
        ) {
            throw "Guest smoke artifact package returned an invalid archive identity."
        }
        $hostArchivePath = Join-Path $HostArtifactRoot ([string]$packageProof.archiveName)
        Copy-Item `
            -LiteralPath $guestArchivePath `
            -Destination $hostArchivePath `
            -FromSession $Session `
            -Force `
            -ErrorAction Stop
        $retrievalProof = Expand-VerifiedSmokeArtifactArchive `
            -ArchivePath $hostArchivePath `
            -DestinationRoot $HostArtifactRoot `
            -PackageProof $packageProof `
            -Lane $Lane
    } catch {
        $primaryError = $_
    } finally {
        if (-not [string]::IsNullOrWhiteSpace($guestArchivePath)) {
            try {
                Invoke-GuestCommandWithTimeout `
                    -Session $Session `
                    -OperationName "Removing guest smoke artifact archive" `
                    -TimeoutSec 120 `
                    -ScriptBlock {
                        param($ArchivePath)
                        if (Test-Path -LiteralPath $ArchivePath -PathType Leaf) {
                            Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction Stop
                        }
                    } `
                    -ArgumentList @($guestArchivePath) | Out-Null
            } catch {
                $cleanupError = $_
            }
        }
        if (
            -not [string]::IsNullOrWhiteSpace($hostArchivePath) -and
            (Test-Path -LiteralPath $hostArchivePath)
        ) {
            try {
                Remove-Item -LiteralPath $hostArchivePath -Force -ErrorAction Stop
            } catch {
                if ($null -eq $cleanupError) {
                    $cleanupError = $_
                }
            }
        }
    }
    if ($null -ne $primaryError -and $null -ne $cleanupError) {
        $safePrimary = ConvertTo-SafeGuestDiagnosticText -Value $primaryError -MaxChars 2048
        $safeCleanup = ConvertTo-SafeGuestDiagnosticText -Value $cleanupError -MaxChars 1024
        throw "Smoke artifact retrieval failed: $safePrimary Archive cleanup also failed: $safeCleanup"
    }
    if ($null -ne $primaryError) {
        throw $primaryError
    }
    if ($null -ne $cleanupError) {
        throw $cleanupError
    }
    return $retrievalProof
}

function Remove-GuestSmokeArtifactArchiveResidue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [string]$GuestArtifactRoot
    )

    Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Removing stale guest smoke artifact archives" `
        -TimeoutSec 120 `
        -ScriptBlock {
            param($ArtifactRoot)

            Set-StrictMode -Version 2.0
            $ErrorActionPreference = "Stop"
            $ownedArtifactsRoot = [IO.Path]::GetFullPath(
                (Split-Path -Parent $ArtifactRoot)).TrimEnd("\")
            $rootItem = Get-Item -LiteralPath $ownedArtifactsRoot -Force -ErrorAction Stop
            if (
                -not $rootItem.PSIsContainer -or
                ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "Owned smoke artifact root is missing, not a directory, or is a reparse point."
            }
            foreach ($archive in @(
                Get-ChildItem `
                    -LiteralPath $ownedArtifactsRoot `
                    -Force `
                    -File `
                    -ErrorAction Stop |
                    Where-Object {
                        $_.Name -cmatch '^\.openclaw-smoke-artifacts-[0-9a-f]{32}\.zip$'
                    }
            )) {
                $archiveParent = [IO.Path]::GetFullPath($archive.DirectoryName).TrimEnd("\")
                if (
                    -not [string]::Equals(
                        $archiveParent,
                        $ownedArtifactsRoot,
                        [StringComparison]::OrdinalIgnoreCase) -or
                    ($archive.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
                ) {
                    throw "Stale smoke artifact archive identity is not safe to remove."
                }
                Remove-Item -LiteralPath $archive.FullName -Force -ErrorAction Stop
            }
        } `
        -ArgumentList @($GuestArtifactRoot) | Out-Null
}

function Receive-SmokeArtifactArchiveWithRecovery {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVmId,
        [Parameter(Mandatory = $true)]
        [string]$GuestArtifactRoot,
        [Parameter(Mandatory = $true)]
        [string]$HostArtifactRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Installed", "Upgrade")]
        [string]$Lane
    )

    $activeSession = $Session
    $ownsActiveSession = $false
    $attemptErrors = New-Object 'Collections.Generic.List[object]'
    try {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            try {
                $sessionInfo = Get-SmokeArtifactRetrievalSession `
                    -Session $activeSession `
                    -GuestCredential $GuestCredential `
                    -ResolvedVhdPath $ResolvedVhdPath `
                    -ExpectedOwnerId $ExpectedOwnerId `
                    -ExpectedVmId $ExpectedVmId
            } catch {
                if ($attemptErrors.Count -gt 0) {
                    $safeFirst = ConvertTo-SafeGuestDiagnosticText `
                        -Value $attemptErrors[0] `
                        -MaxChars 2048
                    $safeRecovery = ConvertTo-SafeGuestDiagnosticText `
                        -Value $_ `
                        -MaxChars 2048
                    throw (
                        "Smoke artifact retrieval failed before session recovery: $safeFirst " +
                        "Session recovery also failed: $safeRecovery")
                }
                throw
            }
            if ([bool]$sessionInfo.Reconnected) {
                $activeSession = $sessionInfo.Session
                $ownsActiveSession = $true
                if ($attempt -gt 1) {
                    try {
                        Remove-GuestSmokeArtifactArchiveResidue `
                            -Session $activeSession `
                            -GuestArtifactRoot $GuestArtifactRoot
                    } catch {
                        $safeFirst = ConvertTo-SafeGuestDiagnosticText `
                            -Value $attemptErrors[0] `
                            -MaxChars 2048
                        $safeCleanup = ConvertTo-SafeGuestDiagnosticText `
                            -Value $_ `
                            -MaxChars 2048
                        throw (
                            "Smoke artifact retrieval failed before session recovery: $safeFirst " +
                            "Stale archive cleanup also failed: $safeCleanup")
                    }
                }
            } elseif ($attempt -gt 1) {
                throw $attemptErrors[0]
            }

            try {
                $proof = Receive-SmokeArtifactArchive `
                    -Session $activeSession `
                    -GuestArtifactRoot $GuestArtifactRoot `
                    -HostArtifactRoot $HostArtifactRoot `
                    -Lane $Lane
                $proof | Add-Member -NotePropertyName retrievalAttempts -NotePropertyValue $attempt
                $proof | Add-Member `
                    -NotePropertyName sessionRecovered `
                    -NotePropertyValue $ownsActiveSession
                return $proof
            } catch {
                $attemptErrors.Add($_)
            }
        }

        $safeFirst = ConvertTo-SafeGuestDiagnosticText `
            -Value $attemptErrors[0] `
            -MaxChars 2048
        $safeRetry = ConvertTo-SafeGuestDiagnosticText `
            -Value $attemptErrors[1] `
            -MaxChars 2048
        throw (
            "Smoke artifact retrieval failed before session recovery: $safeFirst " +
            "Retry after exact-VM reconnect also failed: $safeRetry")
    } finally {
        if ($ownsActiveSession -and $null -ne $activeSession) {
            Remove-PSSession -Session $activeSession -ErrorAction SilentlyContinue
        }
    }
}

function Get-EffectiveSwitchName {
    if (-not [string]::IsNullOrWhiteSpace($SwitchName)) {
        return $SwitchName
    }
    $defaultSwitch = Get-VMSwitch -Name "Default Switch" -ErrorAction SilentlyContinue
    if ($null -eq $defaultSwitch) {
        throw "No Hyper-V switch was provided and 'Default Switch' was not found. Rerun with -SwitchName."
    }
    return $defaultSwitch.Name
}

function Set-OwnedVmSecurityConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedWindowsIsoPath
    )

    $ownedVm = Assert-OwnedVM `
        -ResolvedVhdPath $ResolvedVhdPath `
        -ExpectedOwnerId $ExpectedOwnerId
    if ([string]$ownedVm.State -ne "Off") {
        throw "Security configuration may be applied only while the exact owned VM '$VMName' is Off. Host-reported VM state: '$($ownedVm.State)'."
    }

    $windowsDvdDrives = @(
        Get-VMDvdDrive -VMName $VMName -ErrorAction Stop |
            Where-Object {
                (Normalize-ComparisonPath ([string]$_.Path)) -eq
                    (Normalize-ComparisonPath $ResolvedWindowsIsoPath)
            }
    )
    if ($windowsDvdDrives.Count -ne 1) {
        throw "Expected exactly one DVD drive for the verified Windows ISO on '$VMName', but found $($windowsDvdDrives.Count): $ResolvedWindowsIsoPath"
    }
    $windowsDvdDrive = $windowsDvdDrives[0]

    $keyProtector = Get-VMKeyProtector -VMName $VMName -ErrorAction Stop
    $hasKeyProtector = Test-KeyProtectorPresent -KeyProtector $keyProtector
    if (-not $hasKeyProtector) {
        Set-VMKeyProtector -VMName $VMName -NewLocalKeyProtector -ErrorAction Stop | Out-Null
        $keyProtector = Get-VMKeyProtector -VMName $VMName -ErrorAction Stop
        $hasKeyProtector = Test-KeyProtectorPresent -KeyProtector $keyProtector
        if (-not $hasKeyProtector) {
            throw "Hyper-V did not report a valid local key protector for exact owned VM '$VMName' after creating one. Refusing to configure firmware or vTPM."
        }
    }

    Set-VMFirmware `
        -VMName $VMName `
        -EnableSecureBoot On `
        -SecureBootTemplate $script:WindowsSecureBootTemplate `
        -FirstBootDevice $windowsDvdDrive | Out-Null

    $security = Get-VMSecurity -VMName $VMName -ErrorAction Stop
    $tpmEnabled = Get-PropertyValueOrNull -Object $security -Name "TpmEnabled"
    if ($null -eq $tpmEnabled) {
        throw "Host API did not report vTPM state for exact owned VM '$VMName'. Refusing to change an unknown vTPM configuration."
    }
    if (-not [bool]$tpmEnabled) {
        Enable-VMTPM -VMName $VMName | Out-Null
    }
}

function New-OwnedHyperVVm {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedIsoPath,
        [string]$AnswerIsoPath
    )

    $vmDirectory = Split-Path -Parent $ResolvedVhdPath
    New-Item -ItemType Directory -Force -Path $vmDirectory | Out-Null
    $memoryBytes = [Int64]$StartupMemoryGB * 1GB
    $vhdBytes = [Int64]$VhdSizeGB * 1GB
    $vm = New-VM `
        -Name $VMName `
        -Generation 2 `
        -MemoryStartupBytes $memoryBytes `
        -NewVHDPath $ResolvedVhdPath `
        -NewVHDSizeBytes $vhdBytes `
        -Path $vmDirectory `
        -SwitchName (Get-EffectiveSwitchName)

    Set-VMOwnershipMarker `
        -VmObject $vm `
        -ResolvedVhdPath $ResolvedVhdPath `
        -OwnedOwnerId $OwnerId `
        -ResolvedIsoPath $ResolvedIsoPath | Out-Null
    Set-VM `
        -VMName $VMName `
        -AutomaticCheckpointsEnabled $false `
        -CheckpointType Standard `
        -AutomaticStopAction ShutDown | Out-Null
    Set-VMProcessor `
        -VMName $VMName `
        -Count $ProcessorCount `
        -ExposeVirtualizationExtensions $true | Out-Null
    Add-VMDvdDrive -VMName $VMName -Path $ResolvedIsoPath | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($AnswerIsoPath)) {
        Add-VMDvdDrive -VMName $VMName -Path $AnswerIsoPath | Out-Null
    }

    if (
        -not [string]::IsNullOrWhiteSpace($AnswerIsoPath) -and
        -not @(
            Get-VMDvdDrive -VMName $VMName |
                Where-Object {
                    (Normalize-ComparisonPath ([string]$_.Path)) -eq
                        (Normalize-ComparisonPath $AnswerIsoPath)
                }
        )
    ) {
        throw "The owned answer ISO could not be attached to '$VMName'."
    }

    Set-OwnedVmSecurityConfiguration `
        -ResolvedVhdPath $ResolvedVhdPath `
        -ExpectedOwnerId $OwnerId `
        -ResolvedWindowsIsoPath $ResolvedIsoPath
    $vm = Get-VM -Name $VMName -ErrorAction Stop
    Verify-HostVmConfiguration -VmObject $vm
    return $vm
}

function Invoke-CleanupUnattendCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath
    )

    $paths = Get-UnattendPaths -ResolvedVhdPath $ResolvedVhdPath
    $state = Read-OwnedUnattendState -ResolvedVhdPath $ResolvedVhdPath -Paths $paths
    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if ($null -ne $vm) {
        $vm = Assert-OwnedVM -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $OwnerId
        Assert-UnattendStateMatchesVmMarker -State $state -VmObject $vm
        Detach-OwnedInstallationMedia -State $state
    }
    Remove-OwnedUnattendMedia -Paths $paths
    Remove-SetupCredentialMaterial -Paths $paths
    Set-UnattendStateStatus -State $state -Paths $paths -Status "unattend-cleaned"

    if (
        $null -eq $vm -and
        -not (Test-Path -LiteralPath $ResolvedVhdPath) -and
        (Test-Path -LiteralPath $paths.OwnedRoot)
    ) {
        Remove-Item -LiteralPath $paths.OwnedRoot -Recurse -Force
    }
    return [pscustomobject][ordered]@{
        command = "Create"
        createMode = "Unattended"
        status = "unattend-cleaned"
        vmName = $VMName
        ownerId = $OwnerId
        vmDeleted = $false
        vhdDeleted = $false
    }
}

function Invoke-ResumeUnattendedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath
    )

    $paths = Get-UnattendPaths -ResolvedVhdPath $ResolvedVhdPath
    $state = Read-OwnedUnattendState -ResolvedVhdPath $ResolvedVhdPath -Paths $paths
    $vm = Assert-OwnedVM -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $OwnerId
    Assert-UnattendStateMatchesVmMarker -State $state -VmObject $vm
    $allowAlreadyDetachedAfterPowerShellDirectReady =
        [string]$state.status -ceq "powershell-direct-ready"
    $repairedPreFirstStartSecurityConfiguration = $false
    $configurationVerificationError = $null
    try {
        Verify-HostVmConfiguration -VmObject $vm
    } catch {
        $configurationVerificationError = $_
    }
    if ($null -ne $configurationVerificationError) {
        $vm = Assert-OwnedVM -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $OwnerId
        if ([string]$vm.State -ne "Off") {
            throw "Owned unattended VM '$VMName' failed host configuration verification and cannot be repaired unless it is Off. Host-reported VM state: '$($vm.State)'. Verification failure: $($configurationVerificationError.Exception.Message)"
        }

        Assert-ConfirmationForOwnedAction -Action "Repairing security configuration for owned VM '$VMName'"
        Write-Step "Repairing incomplete owned VM security configuration"
        Set-OwnedVmSecurityConfiguration `
            -ResolvedVhdPath $ResolvedVhdPath `
            -ExpectedOwnerId $OwnerId `
            -ResolvedWindowsIsoPath ([string]$state.windowsIsoPath)
        $vm = Assert-OwnedVM -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $OwnerId
        Verify-HostVmConfiguration -VmObject $vm
        $repairedPreFirstStartSecurityConfiguration = $true
    }
    Ensure-VMRunning
    if ($repairedPreFirstStartSecurityConfiguration) {
        Invoke-UnattendedOpticalBootKey -VmObject $vm
    }

    if (Test-Path -LiteralPath $paths.FinalCredentialPath -PathType Leaf) {
        $finalCredential = Import-CleanWindowsCredential `
            -CredentialPath $paths.FinalCredentialPath `
            -MetadataPath $paths.FinalCredentialMetadataPath `
            -OwnedRoot $paths.OwnedRoot `
            -VMName $VMName `
            -OwnerId $OwnerId `
            -ExpectedKind "final"
        if ($finalCredential.UserName -cne [string]$state.guestAdministratorName) {
            throw "Final credential username does not match the owned unattended-install marker."
        }
        $finalSession = $null
        try {
            $finalSession = Open-GuestSession -GuestCredential $finalCredential -TimeoutSec 60
        } catch {
            $finalSession = $null
        }
        if ($null -ne $finalSession) {
            try {
                Detach-OwnedInstallationMedia -State $state
                Remove-OwnedUnattendMedia -Paths $paths
                Remove-GuestUnattendCache -Session $finalSession
                $verification = Invoke-GuestInstallationVerification `
                    -Session $finalSession `
                    -LocalAccountName ([string]$state.guestAdministratorName)
            } finally {
                Remove-PSSession -Session $finalSession -ErrorAction SilentlyContinue
            }
            Remove-SetupCredentialMaterial -Paths $paths
            Set-UnattendStateStatus -State $state -Paths $paths -Status "complete"
            return Write-UnattendedResult -Paths $paths -Verification $verification -Status "complete"
        }
    }

    $setupCredential = Import-CleanWindowsCredential `
        -CredentialPath $paths.SetupCredentialPath `
        -MetadataPath $paths.SetupCredentialMetadataPath `
        -OwnedRoot $paths.OwnedRoot `
        -VMName $VMName `
        -OwnerId $OwnerId `
        -ExpectedKind "setup"
    return Complete-UnattendedInstallation `
        -ResolvedVhdPath $ResolvedVhdPath `
        -Paths $paths `
        -State $state `
        -SetupCredential $setupCredential `
        -RequireAttachedMedia:(-not $allowAlreadyDetachedAfterPowerShellDirectReady) `
        -AllowAlreadyDetachedAfterPowerShellDirectReady:$allowAlreadyDetachedAfterPowerShellDirectReady
}

function Invoke-CreateCommand {
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath
    if ($CleanupUnattend) {
        return Invoke-CleanupUnattendCommand -ResolvedVhdPath $resolvedVhdPath
    }
    if ($ResumeUnattended) {
        return Invoke-ResumeUnattendedCommand -ResolvedVhdPath $resolvedVhdPath
    }

    $resolvedIsoPath = Resolve-ExistingLiteralPath -Path $IsoPath -Label "ISO path"
    Assert-WindowsIsoHash -ResolvedIsoPath $resolvedIsoPath
    Write-Step "Creating owned Hyper-V VM"
    if ($null -ne (Get-VM -Name $VMName -ErrorAction SilentlyContinue)) {
        throw "VM '$VMName' already exists. Fresh Create only works on a new VM. Use -ResumeUnattended or -CleanupUnattend with -ConfirmOwnedAction for an owned partial installation."
    }
    if (Test-Path -LiteralPath $resolvedVhdPath) {
        throw "VHD path already exists. Refusing to modify or delete it: $resolvedVhdPath"
    }

    if ($CreateMode -eq "Manual") {
        $vm = New-OwnedHyperVVm `
            -ResolvedVhdPath $resolvedVhdPath `
            -ResolvedIsoPath $resolvedIsoPath
        Start-VM -Name $VMName -Confirm:$false | Out-Null
        Write-InfoLine "VM '$VMName' was created and started in safe manual setup mode."
        Write-InfoLine "Complete Windows setup, then rerun Prepare with -Credential or -CredentialPath and -ConfirmOwnedAction."
        return
    }

    $paths = Get-UnattendPaths -ResolvedVhdPath $resolvedVhdPath
    if (Test-Path -LiteralPath $paths.OwnedRoot) {
        throw "Owned marker directory already exists. Use confirmed ResumeUnattended or CleanupUnattend instead of overwriting partial state."
    }
    $computerName = Get-UnattendedComputerName
    $state = New-UnattendState `
        -ResolvedVhdPath $resolvedVhdPath `
        -ResolvedIsoPath $resolvedIsoPath `
        -Paths $paths `
        -ComputerName $computerName
    Write-UnattendState -State $state -Paths $paths

    try {
        Protect-CleanWindowsOwnedDirectory `
            -Path $paths.UnattendRoot `
            -OwnedRoot $paths.OwnedRoot | Out-Null
        $setupCredential = New-CleanWindowsTestCredential -UserName $GuestAdministratorName
        Export-CleanWindowsCredential `
            -Credential $setupCredential `
            -CredentialPath $paths.SetupCredentialPath `
            -MetadataPath $paths.SetupCredentialMetadataPath `
            -OwnedRoot $paths.OwnedRoot `
            -VMName $VMName `
            -OwnerId $OwnerId `
            -Kind "setup" | Out-Null
        New-CleanWindowsAnswerFile `
            -Path $paths.AnswerFilePath `
            -OwnedRoot $paths.OwnedRoot `
            -Credential $setupCredential `
            -ComputerName $computerName | Out-Null
        New-CleanWindowsAnswerIso `
            -StagingPath $paths.StagingPath `
            -IsoPath $paths.AnswerIsoPath `
            -OwnedRoot $paths.OwnedRoot | Out-Null
        Test-CleanWindowsAnswerIsoMount `
            -IsoPath $paths.AnswerIsoPath `
            -ExpectedAnswerFilePath $paths.AnswerFilePath `
            -Credential $setupCredential `
            -ExpectedComputerName $computerName `
            -TimeoutSec 30 | Out-Null
        Set-UnattendStateStatus -State $state -Paths $paths -Status "answer-media-validated"

        $vm = New-OwnedHyperVVm `
            -ResolvedVhdPath $resolvedVhdPath `
            -ResolvedIsoPath $resolvedIsoPath `
            -AnswerIsoPath $paths.AnswerIsoPath
        Set-UnattendStateStatus -State $state -Paths $paths -Status "vm-created"
        Start-VM -Name $VMName -Confirm:$false | Out-Null
        Invoke-UnattendedOpticalBootKey -VmObject $vm
        Set-UnattendStateStatus -State $state -Paths $paths -Status "installing"
        return Complete-UnattendedInstallation `
            -ResolvedVhdPath $resolvedVhdPath `
            -Paths $paths `
            -State $state `
            -SetupCredential $setupCredential `
            -RequireAttachedMedia
    } catch {
        if (Test-Path -LiteralPath $paths.StatePath -PathType Leaf) {
            Set-UnattendStateStatus -State $state -Paths $paths -Status "partial-failure"
        }
        throw
    }
}

function Invoke-PrepareCommand {
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath
    $vm = Assert-OwnedVM -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId
    Verify-HostVmConfiguration -VmObject $vm
    $operationCredential = Resolve-OperationCredential -ResolvedVhdPath $resolvedVhdPath

    if ($RecoverPendingCheckpoint) {
        Recover-PendingOwnedCheckpoint `
            -ResolvedVhdPath $resolvedVhdPath `
            -ExpectedOwnerId $OwnerId `
            -VmObject $vm | Out-Null
    }

    $cleanCheckpoint = Get-SingleCheckpoint -OwnedCheckpointName $script:CleanCheckpointName
    if ($null -eq $cleanCheckpoint) {
        Write-Step "Capturing clean-windows checkpoint"
        Ensure-VMRunning
        $initialSession = $null
        try {
            $initialSession = Open-GuestSession -GuestCredential $operationCredential -TimeoutSec $PowerShellDirectTimeoutSec
        } finally {
            if ($null -ne $initialSession) {
                Stop-VMGracefully -Session $initialSession -TimeoutSec $GuestShutdownTimeoutSec
                Remove-PSSession -Session $initialSession -ErrorAction SilentlyContinue
            } else {
                Stop-VMGracefully -Session $null -TimeoutSec $GuestShutdownTimeoutSec
            }
        }

        New-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:CleanCheckpointName
    } else {
        Assert-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:CleanCheckpointName | Out-Null
    }

    Remove-OwnedCheckpointIfPresent -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:PreparedCheckpointName
    Restore-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:CleanCheckpointName
    Ensure-VMRunning

    $session = $null
    try {
        $session = Open-GuestSession -GuestCredential $operationCredential -TimeoutSec $PowerShellDirectTimeoutSec
        $session = Prepare-GuestPrerequisites `
            -Session $session `
            -GuestCredential $operationCredential `
            -ResolvedVhdPath $resolvedVhdPath `
            -ExpectedOwnerId $OwnerId
    } catch {
        if ($null -ne $session) {
            Stop-VMGracefully -Session $session -TimeoutSec $GuestShutdownTimeoutSec
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
            $session = $null
        } else {
            Stop-VMGracefully -Session $null -TimeoutSec $GuestShutdownTimeoutSec
        }

        Restore-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:CleanCheckpointName
        throw
    }

    try {
        Stop-VMGracefully -Session $session -TimeoutSec $GuestShutdownTimeoutSec
    } finally {
        if ($null -ne $session) {
            Remove-PSSession -Session $session -ErrorAction SilentlyContinue
        }
    }

    New-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:PreparedCheckpointName
    Write-InfoLine "Prepare completed. The VM is captured at checkpoint '$($script:PreparedCheckpointName)'."
}

function Invoke-VerifyCommand {
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath
    $vm = Assert-OwnedVM -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId
    Verify-HostVmConfiguration -VmObject $vm
    $operationCredential = Resolve-OperationCredential -ResolvedVhdPath $resolvedVhdPath
    Assert-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:CleanCheckpointName | Out-Null
    Assert-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:PreparedCheckpointName | Out-Null

    Invoke-PreparedGuestOperation `
        -ResolvedVhdPath $resolvedVhdPath `
        -ExpectedOwnerId $OwnerId `
        -GuestCredential $operationCredential `
        -Action {
        param($Session)
        Install-GuestWslNativeHelper -Session $Session
        $wslProof = Invoke-GuestWslVerificationStage -Session $Session
        $buildToolsProof = Invoke-GuestDeveloperPrerequisiteWorker `
            -Session $Session `
            -PackageKey "VisualStudioBuildTools" `
            -VerifyOnly $true
        $verifyResult = Invoke-GuestCommandWithTimeout `
            -Session $Session `
            -OperationName "Verifying guest readiness" `
            -TimeoutSec $GuestCommandTimeoutSec `
            -ScriptBlock (Get-GuestVerificationSummaryScriptBlock)

        $toolSummaryJson = $verifyResult | Select-Object -Last 1
        if ($null -eq $toolSummaryJson) {
            throw "Guest verification did not return a summary."
        }
        $toolSummary = $toolSummaryJson | ConvertFrom-Json -ErrorAction Stop
        $summary = [pscustomobject][ordered]@{
            computerName = [string]$toolSummary.computerName
            userName = [string]$toolSummary.userName
            gitVersion = [string]$toolSummary.gitVersion
            dotnetVersion = [string]$toolSummary.dotnetVersion
            nodeVersion = [string]$toolSummary.nodeVersion
            npmVersion = [string]$toolSummary.npmVersion
            windowsSdkPresent = [bool]$toolSummary.windowsSdkPresent
            visualStudioBuildTools = [pscustomobject][ordered]@{
                packageId = [string]$buildToolsProof.packageId
                packageVersion = [string]$buildToolsProof.packageVersion
                installerType = [string]$buildToolsProof.installerType
                scope = [string]$buildToolsProof.scope
                components = [string[]]@(
                    "Microsoft.VisualStudio.Component.VC.Redist.14.Latest",
                    "Microsoft.VisualStudio.Component.VC.Tools.x86.x64")
                verification = [string]$buildToolsProof.verification
            }
            wslPackageProof = [pscustomobject][ordered]@{
                scope = [string]$wslProof.scope
                normalizedState = [string]$wslProof.normalizedState
                microsoftWindowsSubsystemLinuxState = [string]$wslProof.microsoftWindowsSubsystemLinuxState
                virtualMachinePlatformState = [string]$wslProof.virtualMachinePlatformState
                statusExitCode = [int]$wslProof.statusExitCode
                versionExitCode = [int]$wslProof.versionExitCode
                status = [string]$wslProof.status
                version = [string]$wslProof.version
            }
        } | ConvertTo-Json -Depth 5

        Write-InfoLine "Guest verification summary:"
        Write-Host $summary
    }
}

function Invoke-SmokeCommand {
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath
    $hostArtifactBase = Resolve-HostArtifactBasePath
    $hostArtifacts = New-SmokeArtifactRunDirectory -BasePath $hostArtifactBase
    $hostManifestPath = Join-Path $hostArtifacts "host-smoke-manifest.json"
    Write-InfoLine "Smoke artifact run directory: $hostArtifacts"

    try {
        $ownedVm = Assert-OwnedVM -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId
        $expectedVmId = ([Guid]$ownedVm.Id).ToString("D")
        Assert-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:PreparedCheckpointName | Out-Null
        $operationCredential = Resolve-OperationCredential -ResolvedVhdPath $resolvedVhdPath

        $guestArtifactName = if ($ValidationLane -eq "Upgrade") { "upgrade-smoke" } else { "installed-smoke" }
        $guestArtifacts = Join-Path $GuestRoot "artifacts\$guestArtifactName"
        Invoke-PreparedGuestOperation `
        -ResolvedVhdPath $resolvedVhdPath `
        -ExpectedOwnerId $OwnerId `
        -GuestCredential $operationCredential `
        -Action {
        param($Session)

        Copy-RepoToGuest -Session $Session
        $guestRepoRoot = Get-GuestRepoRoot

        Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Guest smoke preflight" -TimeoutSec $GuestCommandTimeoutSec -ScriptBlock {
            param($RemoteRepoRoot, $RemoteValidationLane)
            Set-Location $RemoteRepoRoot
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RemoteRepoRoot "scripts\setup-dev.ps1") -CheckOnly
            if ($LASTEXITCODE -ne 0) {
                throw "setup-dev.ps1 -CheckOnly failed with exit code $LASTEXITCODE."
            }
            if ($RemoteValidationLane -eq "Upgrade" -and -not (Get-Command pwsh.exe -ErrorAction SilentlyContinue)) {
                throw "The Upgrade validation lane requires PowerShell 7 (pwsh.exe)."
            }
        } -ArgumentList @($guestRepoRoot, $ValidationLane) | Out-Null

        $smokeError = $null
        $validationRecoveryProof = $null
        try {
            Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Running $ValidationLane validation lane" -TimeoutSec $GuestCommandTimeoutSec -ScriptBlock {
                param(
                    $RemoteRepoRoot,
                    $RemoteArtifactRoot,
                    $RemoteValidationLane,
                    $RemotePreviousRelease,
                    $RemotePreviousInstallerSha256,
                    [pscredential]$RemoteCredential
                )
                if (Test-Path -LiteralPath $RemoteArtifactRoot) {
                    Remove-Item -LiteralPath $RemoteArtifactRoot -Recurse -Force
                }

                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $RemoteArtifactRoot) | Out-Null
                Set-Location $RemoteRepoRoot
                if ($RemoteValidationLane -eq "Upgrade") {
                    $validationScript = Join-Path $RemoteRepoRoot "scripts\validate-inno-upgrade-smoke.ps1"
                    $validationEngine = (Get-Command pwsh.exe -ErrorAction Stop).Source
                    $validationArguments = @(
                        "-NoProfile",
                        "-File", $validationScript,
                        "-RepoRoot", $RemoteRepoRoot,
                        "-ArtifactRoot", $RemoteArtifactRoot,
                        "-PreviousRelease", $RemotePreviousRelease,
                        "-PreviousInstallerSha256", $RemotePreviousInstallerSha256,
                        "-ConfirmCleanMachineReleaseIdentity"
                    )
                } else {
                    $validationScript = Join-Path $RemoteRepoRoot "scripts\validate-installed-inno-smoke.ps1"
                    $validationEngine = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
                    $validationArguments = @(
                        "-NoProfile",
                        "-ExecutionPolicy", "Bypass",
                        "-File", $validationScript,
                        "-RepoRoot", $RemoteRepoRoot,
                        "-ArtifactRoot", $RemoteArtifactRoot
                    )
                }

                if (-not (Test-Path -LiteralPath $validationScript -PathType Leaf)) {
                    throw "$RemoteValidationLane validation script does not exist: $validationScript"
                }
                if (-not (Test-Path -LiteralPath $validationEngine -PathType Leaf)) {
                    throw "$RemoteValidationLane validation engine does not exist: $validationEngine"
                }
                if (
                    $null -eq $RemoteCredential -or
                    [string]::IsNullOrWhiteSpace($RemoteCredential.UserName)
                ) {
                    throw "$RemoteValidationLane validation requires the owned guest credential."
                }

                function ConvertTo-OpenClawSmokeDiagnostic {
                    param(
                        [AllowNull()]
                        [string]$Text,
                        [int]$MaximumLength = 4096
                    )

                    if ([string]::IsNullOrWhiteSpace($Text)) {
                        return "<unavailable>"
                    }
                    $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
                    $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
                    $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
                    $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
                    $safe = ($safe -replace '\s+', ' ').Trim()
                    if ($safe.Length -gt $MaximumLength) {
                        return $safe.Substring(0, $MaximumLength) + "...<truncated>"
                    }
                    return $safe
                }

                function ConvertTo-OpenClawValidationArgument {
                    param(
                        [Parameter(Mandatory = $true)]
                        [AllowEmptyString()]
                        [string]$Value
                    )

                    $builder = [Text.StringBuilder]::new()
                    [void]$builder.Append('"')
                    $backslashes = 0
                    foreach ($character in $Value.ToCharArray()) {
                        if ($character -eq '\') {
                            $backslashes++
                            continue
                        }
                        if ($character -eq '"') {
                            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
                            [void]$builder.Append('"')
                            $backslashes = 0
                            continue
                        }
                        if ($backslashes -gt 0) {
                            [void]$builder.Append(('\' * $backslashes))
                            $backslashes = 0
                        }
                        [void]$builder.Append($character)
                    }
                    if ($backslashes -gt 0) {
                        [void]$builder.Append(('\' * ($backslashes * 2)))
                    }
                    [void]$builder.Append('"')
                    return $builder.ToString()
                }

                $validationArgumentLine = (
                    $validationArguments |
                        ForEach-Object { ConvertTo-OpenClawValidationArgument -Value ([string]$_) }
                ) -join " "
                $networkCredential = $RemoteCredential.GetNetworkCredential()
                $processStartInfo = [Diagnostics.ProcessStartInfo]::new()
                $processStartInfo.FileName = $validationEngine
                $processStartInfo.Arguments = $validationArgumentLine
                $processStartInfo.WorkingDirectory = $RemoteRepoRoot
                $processStartInfo.UseShellExecute = $false
                $processStartInfo.CreateNoWindow = $true
                $processStartInfo.LoadUserProfile = $true
                $processStartInfo.UserName = $networkCredential.UserName
                $processStartInfo.Domain = $networkCredential.Domain
                $processStartInfo.Password = $RemoteCredential.Password
                $validationProcess = [Diagnostics.Process]::Start($processStartInfo)
                if ($null -eq $validationProcess) {
                    throw "$RemoteValidationLane validation process did not start."
                }
                try {
                    $null = $validationProcess.Handle
                    $validationProcess.WaitForExit()
                    $validationProcess.WaitForExit()
                    $validationExitCode = [int]$validationProcess.ExitCode
                } finally {
                    $validationProcess.Dispose()
                }
                if ($validationExitCode -ne 0) {
                    $phaseStatusPath = Join-Path $RemoteArtifactRoot "phase-status.json"
                    $phaseDiagnostic = if (Test-Path -LiteralPath $phaseStatusPath -PathType Leaf) {
                        ConvertTo-OpenClawSmokeDiagnostic `
                            -Text (Get-Content -LiteralPath $phaseStatusPath -Raw -ErrorAction SilentlyContinue) `
                            -MaximumLength 4096
                    } else {
                        "<phase-status.json unavailable>"
                    }
                    $logName = if ($RemoteValidationLane -eq "Upgrade") {
                        "upgrade-smoke.log"
                    } else {
                        "installed-smoke.log"
                    }
                    $logPath = Join-Path $RemoteArtifactRoot $logName
                    $logDiagnostic = if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                        ConvertTo-OpenClawSmokeDiagnostic `
                            -Text ((Get-Content -LiteralPath $logPath -Tail 40 -ErrorAction SilentlyContinue) -join "`n") `
                            -MaximumLength 4096
                    } else {
                        "<$logName unavailable>"
                    }
                    throw (
                        "$RemoteValidationLane validation lane failed with exit code $validationExitCode. " +
                        "Phase status: $phaseDiagnostic Log tail: $logDiagnostic")
                }

                $phaseStatusPath = Join-Path $RemoteArtifactRoot "phase-status.json"
                if (-not (Test-Path -LiteralPath $phaseStatusPath -PathType Leaf)) {
                    throw "$RemoteValidationLane validation lane did not produce phase-status.json."
                }

                if ($RemoteValidationLane -eq "Upgrade") {
                    $phaseStatus = Get-Content -LiteralPath $phaseStatusPath -Raw | ConvertFrom-Json
                    if ([int]$phaseStatus.exitCode -ne 0 -or -not [bool]$phaseStatus.cleanupCompleted) {
                        throw "Upgrade phase-status.json does not report successful cleanup."
                    }
                    foreach ($phase in @(
                        "preflight",
                        "acquire-previous",
                        "prepare-current",
                        "install-previous",
                        "seed-state",
                        "upgrade-current",
                        "state-preservation",
                        "installed-payload",
                        "roundtrip",
                        "cleanup"
                    )) {
                        if ([string]$phaseStatus.phases.$phase -ne "passed") {
                            throw "Upgrade phase '$phase' did not pass."
                        }
                    }

                    foreach ($requiredArtifact in @(
                        "upgrade-smoke.log",
                        "upgrade-smoke.done",
                        "inno-install-previous.log",
                        "inno-install-current.log",
                        "installed-runtime-proof\phase-status.json"
                    )) {
                        if (-not (Test-Path -LiteralPath (Join-Path $RemoteArtifactRoot $requiredArtifact) -PathType Leaf)) {
                            throw "Upgrade artifacts are missing $requiredArtifact."
                        }
                    }

                    $runtimeStatusPath = Join-Path $RemoteArtifactRoot "installed-runtime-proof\phase-status.json"
                    $runtimeStatus = Get-Content -LiteralPath $runtimeStatusPath -Raw | ConvertFrom-Json
                    if ([int]$runtimeStatus.exitCode -ne 0) {
                        throw "Upgrade installed-runtime-proof did not report exitCode 0."
                    }
                }
            } -ArgumentList @(
                $guestRepoRoot,
                $guestArtifacts,
                $ValidationLane,
                $PreviousRelease,
                $PreviousInstallerSha256.ToLowerInvariant(),
                $operationCredential
            ) | Out-Null
        } catch {
            $smokeError = $_
            if (Test-SmokeValidationTransportLoss -ErrorRecord $smokeError) {
                try {
                    $validationRecoveryProof = Wait-SmokeValidationCompletionWithRecovery `
                        -Session $Session `
                        -GuestCredential $operationCredential `
                        -ResolvedVhdPath $resolvedVhdPath `
                        -ExpectedOwnerId $OwnerId `
                        -ExpectedVmId $expectedVmId `
                        -GuestArtifactRoot $guestArtifacts `
                        -Lane $ValidationLane `
                        -TimeoutSec $GuestCommandTimeoutSec
                    if ([int]$validationRecoveryProof.exitCode -eq 0) {
                        $smokeError = $null
                    } else {
                        $smokeError = (
                            "$ValidationLane smoke validation completed with exit code " +
                            "$($validationRecoveryProof.exitCode) after PowerShell Direct recovery. " +
                            "Phase status: $($validationRecoveryProof.phaseDiagnostic) " +
                            "Log tail: $($validationRecoveryProof.logDiagnostic)")
                    }
                } catch {
                    $safeTransportError = ConvertTo-SafeGuestDiagnosticText `
                        -Value $smokeError `
                        -MaxChars 2048
                    $safeRecoveryError = ConvertTo-SafeGuestDiagnosticText `
                        -Value $_ `
                        -MaxChars 2048
                    $smokeError = (
                        "$ValidationLane smoke validation lost PowerShell Direct: $safeTransportError " +
                        "Completion recovery also failed: $safeRecoveryError")
                }
            }
        }

        $artifactError = $null
        $retrievalProof = $null
        try {
            $retrievalProof = Receive-SmokeArtifactArchiveWithRecovery `
                -Session $Session `
                -GuestCredential $operationCredential `
                -ResolvedVhdPath $resolvedVhdPath `
                -ExpectedOwnerId $OwnerId `
                -ExpectedVmId $expectedVmId `
                -GuestArtifactRoot $guestArtifacts `
                -HostArtifactRoot $hostArtifacts `
                -Lane $ValidationLane
        } catch {
            $artifactError = $_
        }

        $manifest = [ordered]@{
            command = "Smoke"
            validationLane = $ValidationLane
            previousRelease = if ($ValidationLane -eq "Upgrade") { $PreviousRelease } else { "" }
            previousInstallerSha256 = if ($ValidationLane -eq "Upgrade") { $PreviousInstallerSha256.ToLowerInvariant() } else { "" }
            vmName = $VMName
            ownerId = $OwnerId
            guestArtifactRoot = $guestArtifacts
            hostArtifactBase = $hostArtifactBase
            hostArtifactRunPath = $hostArtifacts
            hostArtifactRoot = $hostArtifacts
            archiveSha256 = if ($null -ne $retrievalProof) { [string]$retrievalProof.sha256 } else { "" }
            artifactFileCount = if ($null -ne $retrievalProof) { [int]$retrievalProof.fileCount } else { 0 }
            artifactRetrievalAttempts = if ($null -ne $retrievalProof) { [int]$retrievalProof.retrievalAttempts } else { 0 }
            artifactSessionRecovered = if ($null -ne $retrievalProof) { [bool]$retrievalProof.sessionRecovered } else { $false }
            validationRecoveryAttempts = if ($null -ne $validationRecoveryProof) { [int]$validationRecoveryProof.recoveryAttempts } else { 0 }
            validationSessionRecovered = if ($null -ne $validationRecoveryProof) { [bool]$validationRecoveryProof.sessionRecovered } else { $false }
            validationRecoveredExitCode = if ($null -ne $validationRecoveryProof) { [int]$validationRecoveryProof.exitCode } else { $null }
            succeeded = ($null -eq $smokeError -and $null -eq $artifactError)
            timestampUtc = [DateTime]::UtcNow.ToString("o")
        }
        $manifest |
            ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath $hostManifestPath -Encoding UTF8
        if ($null -ne $smokeError -and $null -ne $artifactError) {
            $safeSmokeError = ConvertTo-SafeGuestDiagnosticText -Value $smokeError -MaxChars 4096
            $safeArtifactError = ConvertTo-SafeGuestDiagnosticText -Value $artifactError -MaxChars 2048
            throw (
                "$ValidationLane smoke failed: $safeSmokeError " +
                "Artifact retrieval also failed: $safeArtifactError")
        }
        if ($null -ne $smokeError) {
            throw $smokeError
        }
        if ($null -ne $artifactError) {
            throw $artifactError
        }
        }
    } catch {
        if (-not (Test-Path -LiteralPath $hostManifestPath -PathType Leaf)) {
            [ordered]@{
                command = "Smoke"
                validationLane = $ValidationLane
                vmName = $VMName
                ownerId = $OwnerId
                hostArtifactBase = $hostArtifactBase
                hostArtifactRunPath = $hostArtifacts
                hostArtifactRoot = $hostArtifacts
                succeeded = $false
                timestampUtc = [DateTime]::UtcNow.ToString("o")
                failure = ConvertTo-SafeGuestDiagnosticText -Value $_ -MaxChars 4096
            } |
                ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath $hostManifestPath -Encoding UTF8
        }
        throw
    }

    Write-InfoLine "Smoke artifacts: $hostArtifacts"
    return [pscustomobject][ordered]@{
        command = "Smoke"
        validationLane = $ValidationLane
        hostArtifactBase = $hostArtifactBase
        hostArtifactRunPath = $hostArtifacts
        manifestPath = $hostManifestPath
        succeeded = $true
    }
}

function Invoke-RestoreCommand {
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath
    Assert-OwnedVM -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId | Out-Null
    Restore-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $CheckpointName
    Write-InfoLine "VM '$VMName' was restored to checkpoint '$CheckpointName'."
}

Assert-RequiredParameters
Assert-HyperVPrerequisites

switch ($Command) {
    "Create" { Invoke-CreateCommand; break }
    "Prepare" { Invoke-PrepareCommand; break }
    "Verify" { Invoke-VerifyCommand; break }
    "Smoke" { Invoke-SmokeCommand; break }
    "Restore" { Invoke-RestoreCommand; break }
    default { throw "Unsupported command '$Command'." }
}
