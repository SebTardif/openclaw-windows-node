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
    - Create: create a new Gen2 VM and attach the Windows 11 ISO
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

    [switch]$ConfirmOwnedAction,

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
$script:CleanCheckpointName = "clean-windows"
$script:PreparedCheckpointName = "openclaw-prerequisites"

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

    switch ($Command) {
        "Create" {
            if ([string]::IsNullOrWhiteSpace($IsoPath)) {
                throw "IsoPath is required for Create."
            }
        }
        "Prepare" {
            if ($null -eq $Credential) {
                throw "Credential is required for Prepare."
            }
        }
        "Verify" {
            if ($null -eq $Credential) {
                throw "Credential is required for Verify."
            }
        }
        "Smoke" {
            if ($null -eq $Credential) {
                throw "Credential is required for Smoke."
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
        "Get-VMProcessor",
        "Get-VMFirmware",
        "Get-VMSecurity",
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

function Write-MarkerFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Marker
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $Marker | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
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

function Get-PrimaryVhdPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OwnedVmName
    )

    $drives = @(Get-VMHardDiskDrive -VMName $OwnedVmName -ErrorAction Stop |
        Sort-Object ControllerType, ControllerNumber, ControllerLocation)
    if ($drives.Count -eq 0) {
        throw "VM '$OwnedVmName' does not have a hard disk attached."
    }

    return (Resolve-FullPath -Path $drives[0].Path)
}

function Assert-OwnedVM {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVhdPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedOwnerId
    )

    $vm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if ($null -eq $vm) {
        throw "VM '$VMName' was not found."
    }

    $actualVhdPath = Get-PrimaryVhdPath -OwnedVmName $VMName
    if ((Normalize-ComparisonPath $actualVhdPath) -ne (Normalize-ComparisonPath $ResolvedVhdPath)) {
        throw "VM '$VMName' is attached to '$actualVhdPath', not '$ResolvedVhdPath'."
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

    return $vm
}

function Get-SingleCheckpoint {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OwnedCheckpointName
    )

    $snapshots = @(Get-VMSnapshot -VMName $VMName -Name $OwnedCheckpointName -ErrorAction SilentlyContinue)
    if ($snapshots.Count -eq 0) {
        return $null
    }

    if ($snapshots.Count -ne 1) {
        throw "VM '$VMName' has multiple checkpoints named '$OwnedCheckpointName'."
    }

    return $snapshots[0]
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

    Assert-OwnerMarkerMatches `
        -Marker $marker `
        -ExpectedResourceType "checkpoint" `
        -ExpectedResourceName $OwnedCheckpointName `
        -ExpectedOwnerId $ExpectedOwnerId `
        -ExpectedVmName $VMName `
        -ExpectedVhdPath $ResolvedVhdPath `
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
    Remove-OwnedCheckpointIfPresent -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $OwnedCheckpointName

    Write-Step "Creating checkpoint $OwnedCheckpointName"
    Checkpoint-VM -VMName $VMName -SnapshotName $OwnedCheckpointName -Confirm:$false | Out-Null

    $snapshot = Get-SingleCheckpoint -OwnedCheckpointName $OwnedCheckpointName
    if ($null -eq $snapshot) {
        throw "Checkpoint '$OwnedCheckpointName' was not created."
    }

    $vm = Get-VM -Name $VMName -ErrorAction Stop
    $marker = New-OwnerMarkerObject `
        -ResourceType "checkpoint" `
        -ResourceName $OwnedCheckpointName `
        -ResolvedVhdPath $ResolvedVhdPath `
        -OwnedVmName $VMName `
        -OwnedOwnerId $ExpectedOwnerId `
        -VmObject $vm `
        -SnapshotObject $snapshot

    $markerPath = Get-CheckpointMarkerPath -ResolvedVhdPath $ResolvedVhdPath -OwnedVmName $VMName -OwnedCheckpointName $OwnedCheckpointName
    Write-MarkerFile -Path $markerPath -Marker $marker
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
            throw "$OperationName ended in state '$($job.State)'."
        }

        return (Receive-Job -Job $job -ErrorAction Stop)
    } finally {
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue | Out-Null
    }
}

function Restart-GuestAndReconnect {
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$GuestCredential
    )

    $previousBootTicks = Invoke-Command -Session $Session -ScriptBlock {
        return (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().Ticks
    } -ErrorAction Stop

    Invoke-Command -Session $Session -ScriptBlock {
        Start-Process -FilePath shutdown.exe -ArgumentList "/r", "/t", "0", "/f" -WindowStyle Hidden
    } -ErrorAction SilentlyContinue | Out-Null

    Remove-PSSession -Session $Session -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds($GuestRestartTimeoutSec)
    $lastError = $null
    do {
        $candidateSession = $null
        try {
            $candidateSession = New-PSSession -VMName $VMName -Credential $GuestCredential -ErrorAction Stop
            $currentBootTicks = Invoke-Command -Session $candidateSession -ScriptBlock {
                return (Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime().Ticks
            } -ErrorAction Stop
            if ([Int64]$currentBootTicks -gt [Int64]$previousBootTicks) {
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
        throw "Guest '$VMName' did not complete a fresh reboot within $GuestRestartTimeoutSec seconds. Last error: $($lastError.Exception.Message)"
    }
    throw "Guest '$VMName' did not report a newer boot time within $GuestRestartTimeoutSec seconds."
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

        & winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements --disable-interactivity
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

        & winget install --id Microsoft.PowerShell -e --scope machine --accept-source-agreements --accept-package-agreements --disable-interactivity
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
        [System.Management.Automation.Runspaces.PSSession]$Session
    )

    Write-Step "Preparing guest prerequisites"

    $wslResult = Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Enabling guest WSL prerequisites" -TimeoutSec 1800 -ScriptBlock {
        $needsRestart = $false
        foreach ($featureName in @("Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform")) {
            $feature = Get-WindowsOptionalFeature -Online -FeatureName $featureName -ErrorAction Stop
            if ($feature.State -ne "Enabled") {
                Enable-WindowsOptionalFeature -Online -NoRestart -FeatureName $featureName -All | Out-Null
                $needsRestart = $true
            }
        }

        $wslOutput = & wsl.exe --status 2>&1
        $wslExitCode = $LASTEXITCODE
        if ($wslExitCode -ne 0) {
            & wsl.exe --install --no-distribution
            $installExitCode = $LASTEXITCODE
            if ($installExitCode -ne 0 -and $installExitCode -ne 3010) {
                throw "wsl --install --no-distribution failed with exit code $installExitCode. Output: $wslOutput"
            }

            $needsRestart = $true
        }

        [ordered]@{
            needsRestart = $needsRestart
        } | ConvertTo-Json -Compress
    }

    $parsedWslResult = $null
    if ($wslResult) {
        $parsedWslResult = ($wslResult | Select-Object -Last 1 | ConvertFrom-Json)
    }

    $activeSession = $Session
    if ($null -ne $parsedWslResult -and $parsedWslResult.needsRestart) {
        Write-InfoLine "Guest restart is required to complete WSL prerequisites."
        $activeSession = Restart-GuestAndReconnect -Session $Session -GuestCredential $Credential
    }

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

        & wsl.exe --status
        if ($LASTEXITCODE -ne 0) {
            throw "wsl --status failed with exit code $LASTEXITCODE."
        }
    } -ArgumentList @($guestRepoRoot) | Out-Null

    return $activeSession
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
    if (-not $secureBootEnabled) {
        throw "VM '$VMName' does not have secure boot enabled."
    }

    $secureBootTemplate = [string](Get-PropertyValueOrNull -Object $firmware -Name "SecureBootTemplate")
    if (-not [string]::IsNullOrWhiteSpace($secureBootTemplate) -and $secureBootTemplate -notmatch "Microsoft") {
        throw "VM '$VMName' secure boot template is unexpected: $secureBootTemplate"
    }

    $security = Get-VMSecurity -VMName $VMName -ErrorAction Stop
    $tpmEnabled = Get-PropertyValueOrNull -Object $security -Name "TpmEnabled"
    if ($null -eq $tpmEnabled) {
        $keyProtector = Get-VMKeyProtector -VMName $VMName -ErrorAction SilentlyContinue
        $tpmEnabled = ($null -ne $keyProtector)
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
        [scriptblock]$Action
    )

    Restore-OwnedCheckpoint -ResolvedVhdPath $ResolvedVhdPath -ExpectedOwnerId $ExpectedOwnerId -OwnedCheckpointName $script:PreparedCheckpointName
    Ensure-VMRunning
    $session = $null
    try {
        $session = Open-GuestSession -GuestCredential $Credential -TimeoutSec $PowerShellDirectTimeoutSec
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

function Invoke-CreateCommand {
    $resolvedIsoPath = Resolve-ExistingLiteralPath -Path $IsoPath -Label "ISO path"
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath

    Write-Step "Creating owned Hyper-V VM"
    $existingVm = Get-VM -Name $VMName -ErrorAction SilentlyContinue
    if ($null -ne $existingVm) {
        throw "VM '$VMName' already exists. Create only works on a new VM."
    }

    if (Test-Path -LiteralPath $resolvedVhdPath) {
        throw "VHD path already exists. Refusing to modify or delete it: $resolvedVhdPath"
    }

    $vmDirectory = Split-Path -Parent $resolvedVhdPath
    New-Item -ItemType Directory -Force -Path $vmDirectory | Out-Null

    $effectiveSwitchName = $SwitchName
    if ([string]::IsNullOrWhiteSpace($effectiveSwitchName)) {
        $defaultSwitch = Get-VMSwitch -Name "Default Switch" -ErrorAction SilentlyContinue
        if ($null -eq $defaultSwitch) {
            throw "No Hyper-V switch was provided and 'Default Switch' was not found. Rerun with -SwitchName."
        }

        $effectiveSwitchName = $defaultSwitch.Name
    }

    $memoryBytes = [Int64]$StartupMemoryGB * 1GB
    $vhdBytes = [Int64]$VhdSizeGB * 1GB

    $vm = New-VM `
        -Name $VMName `
        -Generation 2 `
        -MemoryStartupBytes $memoryBytes `
        -NewVHDPath $resolvedVhdPath `
        -NewVHDSizeBytes $vhdBytes `
        -Path $vmDirectory `
        -SwitchName $effectiveSwitchName

    Set-VM -VMName $VMName -AutomaticCheckpointsEnabled $false -CheckpointType Standard -AutomaticStopAction ShutDown | Out-Null
    Set-VMProcessor -VMName $VMName -Count $ProcessorCount -ExposeVirtualizationExtensions $true | Out-Null
    Add-VMDvdDrive -VMName $VMName -Path $resolvedIsoPath | Out-Null

    $dvdDrive = Get-VMDvdDrive -VMName $VMName | Select-Object -First 1
    if ($null -eq $dvdDrive) {
        throw "The Windows 11 ISO could not be attached to '$VMName'."
    }

    Set-VMFirmware -VMName $VMName -EnableSecureBoot On -SecureBootTemplate "Microsoft Windows" -FirstBootDevice $dvdDrive | Out-Null
    Set-VMKeyProtector -VMName $VMName -NewLocalKeyProtector | Out-Null
    Enable-VMTPM -VMName $VMName | Out-Null

    $vm = Get-VM -Name $VMName -ErrorAction Stop
    Set-VMOwnershipMarker -VmObject $vm -ResolvedVhdPath $resolvedVhdPath -OwnedOwnerId $OwnerId -ResolvedIsoPath $resolvedIsoPath | Out-Null
    Verify-HostVmConfiguration -VmObject $vm

    Start-VM -Name $VMName -Confirm:$false | Out-Null
    Write-InfoLine "VM '$VMName' was created and started."
    Write-InfoLine "Complete Windows setup in the guest, then rerun Prepare with -Credential and -ConfirmOwnedAction."
}

function Invoke-PrepareCommand {
    $resolvedVhdPath = Resolve-FullPath -Path $VhdPath
    $vm = Assert-OwnedVM -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId
    Verify-HostVmConfiguration -VmObject $vm

    $cleanCheckpoint = Get-SingleCheckpoint -OwnedCheckpointName $script:CleanCheckpointName
    if ($null -eq $cleanCheckpoint) {
        Write-Step "Capturing clean-windows checkpoint"
        Ensure-VMRunning
        $initialSession = $null
        try {
            $initialSession = Open-GuestSession -GuestCredential $Credential -TimeoutSec $PowerShellDirectTimeoutSec
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
        $session = Open-GuestSession -GuestCredential $Credential -TimeoutSec $PowerShellDirectTimeoutSec
        $session = Prepare-GuestPrerequisites -Session $session
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
    Assert-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:CleanCheckpointName | Out-Null
    Assert-OwnedCheckpoint -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -OwnedCheckpointName $script:PreparedCheckpointName | Out-Null

    Invoke-PreparedGuestOperation -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -Action {
        param($Session)
        $verifyResult = Invoke-GuestCommandWithTimeout -Session $Session -OperationName "Verifying guest readiness" -TimeoutSec $GuestCommandTimeoutSec -ScriptBlock {
            $windowsSdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
            $wslFeature = Get-WindowsOptionalFeature -Online -FeatureName "Microsoft-Windows-Subsystem-Linux"
            $vmPlatformFeature = Get-WindowsOptionalFeature -Online -FeatureName "VirtualMachinePlatform"
            $wslStatus = (& wsl.exe --status 2>&1 | Out-String).Trim()
            $wslExitCode = $LASTEXITCODE

            if ($wslExitCode -ne 0) {
                throw "wsl --status failed with exit code $wslExitCode."
            }

            if ($wslFeature.State -ne "Enabled" -or $vmPlatformFeature.State -ne "Enabled") {
                throw "WSL guest features are not fully enabled."
            }

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
                wslFeatureState = $wslFeature.State
                virtualMachinePlatformState = $vmPlatformFeature.State
                wslStatus = $wslStatus
            } | ConvertTo-Json -Depth 5
        }

        $summary = $verifyResult | Select-Object -Last 1
        if ($null -eq $summary) {
            throw "Guest verification did not return a summary."
        }

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

    $guestArtifactName = if ($ValidationLane -eq "Upgrade") { "upgrade-smoke" } else { "installed-smoke" }
    $guestArtifacts = Join-Path $GuestRoot "artifacts\$guestArtifactName"
    Invoke-PreparedGuestOperation -ResolvedVhdPath $resolvedVhdPath -ExpectedOwnerId $OwnerId -Action {
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
