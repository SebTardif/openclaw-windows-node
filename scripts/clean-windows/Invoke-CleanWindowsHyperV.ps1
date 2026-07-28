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

function Get-RepoTransferItems {
    return @(
        ".config",
        ".editorconfig",
        ".gitattributes",
        ".gitignore",
        "Directory.Build.props",
        "GitVersion.yml",
        "NuGet.Config",
        "build.ps1",
        "global.json",
        "installer.iss",
        "openclaw-windows-node.slnx",
        "package-lock.json",
        "package.json",
        "scripts",
        "src",
        "tests"
    )
}

function Copy-RepoToGuest {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    $guestRepoRoot = Get-GuestRepoRoot
    Write-Step "Copying repository into the guest"

    Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Resetting guest repo root" -TimeoutSec 300 -ScriptBlock {
        param($RemoteRepoRoot)
        if (Test-Path -LiteralPath $RemoteRepoRoot) {
            Remove-Item -LiteralPath $RemoteRepoRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $RemoteRepoRoot | Out-Null
    } -ArgumentList @($guestRepoRoot) | Out-Null

    foreach ($item in Get-RepoTransferItems) {
        $sourcePath = Join-Path $script:RepoRoot $item
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Repository transfer item was not found: $sourcePath"
        }

        Copy-Item -LiteralPath $sourcePath -Destination $guestRepoRoot -ToSession $Session -Recurse -Force
    }

    Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Initializing guest git repo" -TimeoutSec 600 -ScriptBlock {
        param($RemoteRepoRoot)

        $git = Get-Command git -ErrorAction SilentlyContinue
        if ($null -eq $git) {
            throw "Git is not available in the guest."
        }

        Set-Location $RemoteRepoRoot
        if (Test-Path -LiteralPath (Join-Path $RemoteRepoRoot ".git")) {
            Remove-Item -LiteralPath (Join-Path $RemoteRepoRoot ".git") -Recurse -Force
        }

        & git init | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "git init failed with exit code $LASTEXITCODE."
        }

        & git branch -M main 2>$null | Out-Null
        & git config user.name "OpenClaw Clean Windows Runner"
        if ($LASTEXITCODE -ne 0) {
            throw "git config user.name failed with exit code $LASTEXITCODE."
        }

        & git config user.email "openclaw-clean-windows@example.invalid"
        if ($LASTEXITCODE -ne 0) {
            throw "git config user.email failed with exit code $LASTEXITCODE."
        }

        & git add -A
        if ($LASTEXITCODE -ne 0) {
            throw "git add failed with exit code $LASTEXITCODE."
        }

        $status = @(& git status --short)
        if ($status.Count -gt 0) {
            & git commit -m "Guest staging" --quiet
            if ($LASTEXITCODE -ne 0) {
                throw "git commit failed with exit code $LASTEXITCODE."
            }
        }
    } -ArgumentList @($guestRepoRoot) | Out-Null
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
                [ValidateSet("Version", "SourceExportWinget", "SourceProbeGit")]
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
                if (-not $process.WaitForExit(60000)) {
                    try {
                        $process.Kill()
                        $process.WaitForExit()
                    } catch {
                    }
                    throw "Trusted winget operation '$Operation' timed out after 60 seconds."
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
                throw "Trusted winget operation '$Operation' failed ($failureType)."
            }
            if ($null -eq $result) {
                throw "Trusted winget operation '$Operation' returned no result."
            }
            return $result
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
                throw "The exact winget source could not resolve the pinned Git.Git package noninteractively."
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
                    Assert-OpenClawWingetCli `
                        -WingetPath $directWinget `
                        -TemporaryRoot $temporaryRoot
                    $result = [pscustomobject][ordered]@{
                        Stage = "winget-bootstrap"
                        AlreadyInstalled = $true
                        Version = "v1.29.280"
                    }
                } else {
                    $assetPaths = @{}
                    $downloadDeadlineUtc = [DateTime]::UtcNow.AddSeconds(1800)
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
                    Assert-OpenClawWingetCli `
                        -WingetPath $directWinget `
                        -TemporaryRoot $temporaryRoot
                    $result = [pscustomobject][ordered]@{
                        Stage = "winget-bootstrap"
                        AlreadyInstalled = $false
                        Version = "v1.29.280"
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
    Invoke-GuestCommandWithTimeout `
        -Session $Session `
        -OperationName "Bootstrapping pinned guest WinGet" `
        -TimeoutSec 3000 `
        -ScriptBlock (Get-GuestWingetBootstrapScriptBlock) | Out-Null
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

function Ensure-GuestPowerShell7Installed {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    Write-Step "Ensuring guest PowerShell 7 is installed"
    Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Installing guest PowerShell 7" -TimeoutSec 1800 -ScriptBlock {
        if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) {
            return
        }

        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw "winget is not available in the guest. Install App Installer, then retry Prepare."
        }

        & winget install --id Microsoft.PowerShell -e --scope machine --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity
        if ($LASTEXITCODE -ne 0) {
            throw "winget failed to install PowerShell 7 with exit code $LASTEXITCODE."
        }

        $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
        if (-not (Get-Command pwsh.exe -ErrorAction SilentlyContinue)) {
            throw "PowerShell 7 was installed but pwsh.exe is not available on PATH in the guest session."
        }
    } | Out-Null
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

        Ensure-GuestWingetAvailable -Session $activeSession
        Ensure-GuestGitInstalled -Session $activeSession
        Ensure-GuestPowerShell7Installed -Session $activeSession
        Copy-RepoToGuest -Session $activeSession

        $guestRepoRoot = Get-GuestRepoRoot
        Invoke-GuestCommandWithTimeout -Session $activeSession -OperationName "Running guest setup-dev" -TimeoutSec $GuestCommandTimeoutSec -ScriptBlock {
            param($RemoteRepoRoot)
            Set-Location $RemoteRepoRoot
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RemoteRepoRoot "scripts\setup-dev.ps1")
            if ($LASTEXITCODE -ne 0) {
                throw "setup-dev.ps1 failed with exit code $LASTEXITCODE."
            }
        } -ArgumentList @($guestRepoRoot) | Out-Null

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

function Resolve-HostArtifactPath {
    if (-not [string]::IsNullOrWhiteSpace($HostArtifactRoot)) {
        if ([IO.Path]::IsPathRooted($HostArtifactRoot)) {
            return (Resolve-FullPath -Path $HostArtifactRoot)
        }

        return (Resolve-FullPath -Path (Join-Path $script:RepoRoot $HostArtifactRoot))
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    return (Join-Path $script:RepoRoot ("TestResults\CleanWindowsHyperV\{0}\{1}" -f $VMName, $timestamp))
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
        $verifyResult = Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Verifying guest readiness" -TimeoutSec $GuestCommandTimeoutSec -ScriptBlock {
            $windowsSdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
            if (-not (Test-Path -LiteralPath $windowsSdkPath)) {
                throw "Windows SDK is not present in the guest."
            }

            [ordered]@{
                computerName = $env:COMPUTERNAME
                userName = [Environment]::UserName
                gitVersion = (& git --version 2>&1 | Out-String).Trim()
                dotnetVersion = (& dotnet --version 2>&1 | Out-String).Trim()
                nodeVersion = (& node --version 2>&1 | Out-String).Trim()
                npmVersion = (& npm --version 2>&1 | Out-String).Trim()
                windowsSdkPresent = (Test-Path -LiteralPath $windowsSdkPath)
            } | ConvertTo-Json -Depth 5
        }

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
    $hostArtifacts = Resolve-HostArtifactPath
    New-Item -ItemType Directory -Force -Path $hostArtifacts | Out-Null

    Assert-OwnedVM -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId | Out-Null
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

        $smokeFailed = $true
        try {
            Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Running $ValidationLane validation lane" -TimeoutSec $GuestCommandTimeoutSec -ScriptBlock {
                param(
                    $RemoteRepoRoot,
                    $RemoteArtifactRoot,
                    $RemoteValidationLane,
                    $RemotePreviousRelease,
                    $RemotePreviousInstallerSha256
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
                    $validationEngine = "powershell.exe"
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

                & $validationEngine @validationArguments
                if ($LASTEXITCODE -ne 0) {
                    throw "$RemoteValidationLane validation lane failed with exit code $LASTEXITCODE."
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
                $PreviousInstallerSha256.ToLowerInvariant()
            ) | Out-Null

            $smokeFailed = $false
        } finally {
            $artifactRetrievalError = $null
            try {
                $exists = Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Checking guest artifact path" -TimeoutSec 120 -ScriptBlock {
                    param($RemoteArtifactRoot)
                    return (Test-Path -LiteralPath $RemoteArtifactRoot)
                } -ArgumentList @($guestArtifacts)

                if ($exists -contains $true) {
                    Copy-Item -Path $guestArtifacts -Destination $hostArtifacts -FromSession $Session -Recurse -Force
                }
            } catch {
                Write-InfoLine "Artifact retrieval did not complete: $($_.Exception.Message)"
                if (-not $smokeFailed) {
                    $smokeFailed = $true
                    $artifactRetrievalError = $_
                }
            }

            $manifest = [ordered]@{
                command = "Smoke"
                validationLane = $ValidationLane
                previousRelease = if ($ValidationLane -eq "Upgrade") { $PreviousRelease } else { "" }
                previousInstallerSha256 = if ($ValidationLane -eq "Upgrade") { $PreviousInstallerSha256.ToLowerInvariant() } else { "" }
                vmName = $VMName
                ownerId = $OwnerId
                guestArtifactRoot = $guestArtifacts
                hostArtifactRoot = $hostArtifacts
                succeeded = (-not $smokeFailed)
                timestampUtc = [DateTime]::UtcNow.ToString("o")
            }
            $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $hostArtifacts "host-smoke-manifest.json") -Encoding UTF8
            if ($null -ne $artifactRetrievalError) {
                throw $artifactRetrievalError
            }
        }
    }

    Write-InfoLine "Smoke artifacts: $hostArtifacts"
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
