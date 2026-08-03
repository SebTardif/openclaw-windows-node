#Requires -Version 7.0

<#
.SYNOPSIS
    Proves an official previous OpenClaw release upgrades to current source bits.

.DESCRIPTION
    This is a release-identity lane for a disposable Windows machine or VM only.
    It refuses to run when any release or DEV install, app data, registry, process,
    shortcut, startup task, or app-owned WSL distro already exists.

    The script downloads an exact official GitHub release asset over HTTPS or uses
    an explicit offline installer, builds or accepts the current installer, installs
    both with the production Inno identity, proves state preservation and version
    transition, delegates runtime proof to validate-installed-inno-smoke.ps1, then
    uninstalls and verifies cleanup.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactRoot = "",
    [string]$PreviousRelease = "",
    [string]$PreviousVersion = "",
    [string]$PreviousInstallerPath = "",
    [string]$PreviousInstallerSha256 = "",
    [string]$PreviousAssetName = "OpenClawCompanion-Setup-x64.exe",
    [string]$CurrentVersion = "",
    [string]$CurrentInstallerPath = "",
    [string]$CurrentPayloadPath = "",
    [switch]$AllowUnsignedPreviousPayload,
    [switch]$ConfirmCleanMachineReleaseIdentity,
    [switch]$SafetyPreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_smoke-native-process.ps1")

$officialApiRoot = "https://api.github.com/repos/openclaw/openclaw-windows-node"
$officialDownloadRoot = "https://github.com/openclaw/openclaw-windows-node/releases/download/"
$releaseUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1"
$releaseWowUninstallKey = "HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1"
$devUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-DEV}_is1"
$devWowUninstallKey = "HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-DEV}_is1"
$releaseProtocolKey = "HKCU:\Software\Classes\openclaw"
$devProtocolKey = "HKCU:\Software\Classes\openclaw-dev"
$runKey = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "Repository root does not exist: $RepoRoot"
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$runId = [Guid]::NewGuid().ToString("N")
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $ArtifactRoot = Join-Path $RepoRoot "TestResults\UpgradeSmoke\$timestamp"
} elseif (-not [IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $RepoRoot $ArtifactRoot
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$downloadRoot = Join-Path $ArtifactRoot "downloads"
$installBase = Join-Path $env:LOCALAPPDATA "OpenClawUpgradeSmoke\$runId"
$installRoot = Join-Path $installBase "app"
$installedTray = Join-Path $installRoot "OpenClaw.Tray.WinUI.exe"
$uninstaller = Join-Path $installRoot "unins000.exe"
$releaseRoamingData = Join-Path $env:APPDATA "OpenClawTray"
$releaseLocalData = Join-Path $env:LOCALAPPDATA "OpenClawTray"
$devRoamingData = Join-Path $env:APPDATA "OpenClawTray-Dev"
$devLocalData = Join-Path $env:LOCALAPPDATA "OpenClawTray-Dev"
$protectedShortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath("Programs")) "OpenClaw Companion"),
    (Join-Path ([Environment]::GetFolderPath("Programs")) "OpenClaw Companion (Dev)"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "OpenClaw Companion.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "OpenClaw Companion (Dev).lnk"),
    (Join-Path ([Environment]::GetFolderPath("Startup")) "OpenClaw Companion.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Startup")) "OpenClaw Companion (Dev).lnk")
)
$logPath = Join-Path $ArtifactRoot "upgrade-smoke.log"
$donePath = Join-Path $ArtifactRoot "upgrade-smoke.done"
$statusPath = Join-Path $ArtifactRoot "phase-status.json"
$phaseResults = [ordered]@{}
$requiredPhases = @(
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
)
if ($SafetyPreflightOnly) {
    $requiredPhases = @("preflight")
}

$exitCode = 1
$ownsInstall = $false
$ownsSeededState = $false
$cleanupCompleted = $false
$previousInstaller = ""
$currentInstaller = ""
$currentPayload = ""
$previousInstallerHash = ""
$currentInstallerHash = ""
$previousTrayHash = ""
$currentTrayHash = ""
$stateHashes = [ordered]@{}
$evidence = [ordered]@{}

function Write-MainLog {
    param([Parameter(Mandatory = $true)][string]$Message)

    $line = "[$(Get-Date -Format o)] $Message"
    $line | Add-Content -LiteralPath $logPath -Encoding UTF8
    Write-Host $line
}

function Invoke-Phase {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $phasePath = Join-Path $ArtifactRoot "phase-$Name.log"
    Write-MainLog "PHASE_START $Name"
    try {
        & $Action *> $phasePath
        $phaseResults[$Name] = "passed"
        Write-MainLog "PHASE_PASS $Name"
    } catch {
        $_ | Out-String | Add-Content -LiteralPath $phasePath -Encoding UTF8
        $phaseResults[$Name] = "failed"
        Write-MainLog "PHASE_FAIL $Name $($_.Exception.Message)"
        throw
    }
}

function Assert-NativeSuccess {
    param([Parameter(Mandatory = $true)][string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function ConvertTo-CheckedSemVer {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $normalized = $Value.Trim()
    if ($normalized.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }
    if ($normalized -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "$Label '$Value' is not an unambiguous SemVer value."
    }

    try {
        return [System.Management.Automation.SemanticVersion]$normalized
    } catch {
        throw "$Label '$Value' could not be parsed as SemVer: $($_.Exception.Message)"
    }
}

function Normalize-Sha256 {
    param(
        [string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }
    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized.StartsWith("sha256:", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring("sha256:".Length)
    }
    if ($normalized -notmatch '^[0-9a-f]{64}$') {
        throw "$Label is not a valid SHA-256 digest."
    }
    return $normalized
}

function Assert-PathAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (Test-Path -LiteralPath $Path) {
        throw "$Label already exists. Refusing to touch existing user or developer state: $Path"
    }
}

function Assert-RegistryValueAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($null -ne (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue)) {
        throw "$Label already exists. Refusing to touch existing user or developer state."
    }
}

function Get-WslDistroNames {
    $wsl = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if (-not $wsl) {
        throw "wsl.exe is required so existing app-owned WSL state can be checked."
    }

    $output = @(& $wsl.Source --list --quiet 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "wsl.exe --list --quiet failed with exit code $LASTEXITCODE. Existing WSL state is ambiguous."
    }

    return @($output |
        ForEach-Object { (($_ | Out-String) -replace '\x00', '').Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Assert-ScheduledTaskAbsent {
    param([Parameter(Mandatory = $true)][string]$Name)

    $schtasks = Join-Path $env:WINDIR "System32\schtasks.exe"
    if (-not (Test-Path -LiteralPath $schtasks -PathType Leaf)) {
        throw "schtasks.exe is required so startup task state can be checked."
    }
    & $schtasks /Query /TN $Name *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Startup task '$Name' already exists. Refusing to touch existing user or developer state."
    }
}

function Assert-CleanMachineReleaseIdentity {
    if (-not $ConfirmCleanMachineReleaseIdentity) {
        throw "Pass -ConfirmCleanMachineReleaseIdentity only on a disposable clean Windows machine or VM."
    }
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
        throw "The Inno upgrade smoke requires Windows."
    }
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        throw "The current upgrade lane supports only Windows x64."
    }

    foreach ($guard in @(
        @{ Path = $releaseUninstallKey; Label = "Release uninstall registration" },
        @{ Path = $releaseWowUninstallKey; Label = "Release WOW6432 uninstall registration" },
        @{ Path = $devUninstallKey; Label = "DEV uninstall registration" },
        @{ Path = $devWowUninstallKey; Label = "DEV WOW6432 uninstall registration" },
        @{ Path = $releaseProtocolKey; Label = "Release protocol registration" },
        @{ Path = $devProtocolKey; Label = "DEV protocol registration" },
        @{ Path = $releaseRoamingData; Label = "Release roaming data" },
        @{ Path = $releaseLocalData; Label = "Release local data or default install" },
        @{ Path = $devRoamingData; Label = "DEV roaming data" },
        @{ Path = $devLocalData; Label = "DEV local data or default install" },
        @{ Path = $installBase; Label = "Upgrade smoke install root" }
    )) {
        Assert-PathAbsent -Path $guard.Path -Label $guard.Label
    }

    Assert-RegistryValueAbsent -Path $runKey -Name "OpenClawTray" -Label "Release autostart value"
    Assert-RegistryValueAbsent -Path $runKey -Name "OpenClawTray-Dev" -Label "DEV autostart value"
    Assert-ScheduledTaskAbsent -Name "OpenClaw Companion"
    Assert-ScheduledTaskAbsent -Name "OpenClaw Companion (Dev)"

    foreach ($shortcutPath in $protectedShortcutPaths) {
        Assert-PathAbsent -Path $shortcutPath -Label "OpenClaw shortcut"
    }

    $openClawProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
        [string]::Equals(
            [IO.Path]::GetFileName($_.ExecutablePath),
            "OpenClaw.Tray.WinUI.exe",
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($openClawProcesses.Count -gt 0) {
        $paths = ($openClawProcesses | ForEach-Object ExecutablePath | Sort-Object -Unique) -join ", "
        throw "OpenClaw tray process state already exists. Refusing to stop it: $paths"
    }

    $distros = Get-WslDistroNames
    foreach ($name in @("OpenClawGateway", "OpenClawGateway-Dev")) {
        if ($distros -contains $name) {
            throw "App-owned WSL distro '$name' already exists. Refusing to touch existing WSL state."
        }
    }

    foreach ($protectedRoot in @($releaseRoamingData, $releaseLocalData, $devRoamingData, $devLocalData)) {
        $fullProtectedRoot = [IO.Path]::GetFullPath($protectedRoot).TrimEnd('\') + '\'
        if ($ArtifactRoot.StartsWith($fullProtectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "ArtifactRoot must not be under an OpenClaw release or DEV state directory."
        }
    }

    & (Join-Path $RepoRoot "scripts\setup-dev.ps1") -CheckOnly
    Assert-NativeSuccess "Developer prerequisite check"
}

function Resolve-PreviousVersion {
    if (-not [string]::IsNullOrWhiteSpace($PreviousRelease)) {
        if ($PreviousRelease -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
            throw "PreviousRelease must be an exact v-prefixed SemVer tag."
        }
        $tagVersion = $PreviousRelease.Substring(1)
        if ([string]::IsNullOrWhiteSpace($PreviousVersion)) {
            $script:PreviousVersion = $tagVersion
        } elseif (-not [string]::Equals($PreviousVersion, $tagVersion, [StringComparison]::Ordinal)) {
            throw "PreviousVersion '$PreviousVersion' does not match release tag '$PreviousRelease'."
        }
    }

    if ([string]::IsNullOrWhiteSpace($PreviousVersion)) {
        throw "Pass -PreviousRelease or an explicit -PreviousVersion with -PreviousInstallerPath."
    }
    [void](ConvertTo-CheckedSemVer -Value $PreviousVersion -Label "PreviousVersion")
}

function Resolve-CurrentVersion {
    if ([string]::IsNullOrWhiteSpace($CurrentVersion)) {
        $versionScript = Join-Path $RepoRoot "scripts\Get-OpenClawVersion.ps1"
        $script:CurrentVersion = & $versionScript -Variable SemVer
        Assert-NativeSuccess "Current version resolution"
    }
    $previousSemVer = ConvertTo-CheckedSemVer -Value $PreviousVersion -Label "PreviousVersion"
    $currentSemVer = ConvertTo-CheckedSemVer -Value $CurrentVersion -Label "CurrentVersion"
    if ($currentSemVer -le $previousSemVer) {
        throw "CurrentVersion '$CurrentVersion' must be newer than PreviousVersion '$PreviousVersion'."
    }
}

function Resolve-PreviousInstaller {
    if (-not [string]::IsNullOrWhiteSpace($PreviousInstallerPath)) {
        if (-not (Test-Path -LiteralPath $PreviousInstallerPath -PathType Leaf)) {
            throw "Previous installer override does not exist: $PreviousInstallerPath"
        }
        $sourceInstaller = (Resolve-Path -LiteralPath $PreviousInstallerPath).Path
        $sourceHash = (Get-FileHash -LiteralPath $sourceInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedHash = Normalize-Sha256 -Value $PreviousInstallerSha256 -Label "PreviousInstallerSha256"
        if ($expectedHash -and $sourceHash -ne $expectedHash) {
            throw "Previous installer SHA-256 does not match -PreviousInstallerSha256."
        }

        New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null
        $script:previousInstaller = Join-Path $downloadRoot "previous-local-OpenClawCompanion-Setup-x64.exe"
        Copy-Item -LiteralPath $sourceInstaller -Destination $previousInstaller -Force
        $script:previousInstallerHash = (Get-FileHash -LiteralPath $previousInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($previousInstallerHash -ne $sourceHash) {
            throw "Artifact snapshot of the previous installer does not match the local source SHA-256."
        }
        $script:evidence.previousAssetSource = "local"
        $script:evidence.previousLocalSource = $sourceInstaller
        return
    }

    if ($AllowUnsignedPreviousPayload) {
        throw "-AllowUnsignedPreviousPayload is valid only with -PreviousInstallerPath."
    }
    if ([string]::IsNullOrWhiteSpace($PreviousRelease)) {
        throw "A missing previous release fails closed. Pass -PreviousRelease or -PreviousInstallerPath."
    }
    if ($PreviousAssetName -ne "OpenClawCompanion-Setup-x64.exe") {
        throw "The x64 upgrade lane requires the exact official asset name OpenClawCompanion-Setup-x64.exe."
    }

    $headers = @{
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "OpenClaw-Windows-Inno-Upgrade-Smoke"
    }
    $releaseUri = "$officialApiRoot/releases/tags/$PreviousRelease"
    if (-not ([Uri]$releaseUri).Scheme.Equals("https", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Official release metadata must use HTTPS."
    }
    $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers -Method Get
    if ($release.draft -or -not [string]::Equals([string]$release.tag_name, $PreviousRelease, [StringComparison]::Ordinal)) {
        throw "Official release metadata did not resolve the exact non-draft tag '$PreviousRelease'."
    }
    if (-not ([string]$release.html_url).StartsWith(
        "https://github.com/openclaw/openclaw-windows-node/releases/tag/",
        [StringComparison]::Ordinal)) {
        throw "Release metadata did not identify the official OpenClaw repository."
    }

    $assets = @($release.assets | Where-Object {
        [string]::Equals([string]$_.name, $PreviousAssetName, [StringComparison]::Ordinal)
    })
    if ($assets.Count -ne 1) {
        throw "Expected exactly one official '$PreviousAssetName' asset for '$PreviousRelease'; found $($assets.Count)."
    }
    $asset = $assets[0]
    $downloadUri = [string]$asset.browser_download_url
    if (-not $downloadUri.StartsWith("$officialDownloadRoot$PreviousRelease/", [StringComparison]::Ordinal)) {
        throw "Release asset URL is not the exact official HTTPS release path."
    }
    if ([long]$asset.size -le 0) {
        throw "Official release asset has an invalid size."
    }

    $apiDigest = ""
    $digestProperty = $asset.PSObject.Properties["digest"]
    if ($null -ne $digestProperty) {
        $apiDigest = Normalize-Sha256 -Value ([string]$digestProperty.Value) -Label "GitHub asset digest"
    }
    $explicitDigest = Normalize-Sha256 -Value $PreviousInstallerSha256 -Label "PreviousInstallerSha256"
    if ($apiDigest -and $explicitDigest -and $apiDigest -ne $explicitDigest) {
        throw "GitHub asset digest does not match -PreviousInstallerSha256."
    }
    $expectedDigest = if ($explicitDigest) { $explicitDigest } else { $apiDigest }
    if (-not $expectedDigest) {
        throw "The official release asset has no SHA-256 digest. Pass -PreviousInstallerSha256 to fail closed."
    }

    New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null
    $script:previousInstaller = Join-Path $downloadRoot $PreviousAssetName
    Invoke-WebRequest -Uri $downloadUri -Headers $headers -OutFile $previousInstaller
    $downloaded = Get-Item -LiteralPath $previousInstaller
    if ($downloaded.Length -ne [long]$asset.size) {
        throw "Downloaded previous installer size $($downloaded.Length) does not match GitHub asset size $($asset.size)."
    }
    $script:previousInstallerHash = (Get-FileHash -LiteralPath $previousInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($previousInstallerHash -ne $expectedDigest) {
        throw "Downloaded previous installer SHA-256 does not match the official digest."
    }

    $script:evidence.previousAssetSource = "official-github-release"
    $script:evidence.previousReleaseUrl = [string]$release.html_url
    $script:evidence.previousAssetUrl = $downloadUri
    $script:evidence.previousAssetSize = [long]$asset.size
    $script:evidence.previousDigestSource = if ($explicitDigest) { "explicit-and-api" } else { "github-api" }
}

function Resolve-CurrentInstaller {
    if ([string]::IsNullOrWhiteSpace($CurrentInstallerPath)) {
        $script:currentInstaller = Join-Path $RepoRoot "Output\OpenClawCompanion-Setup-x64.exe"
        Remove-Item -LiteralPath $currentInstaller -Force -ErrorAction SilentlyContinue
        & (Join-Path $RepoRoot "scripts\build-inno-local.ps1") `
            -Arch x64 `
            -Fast `
            -InstallInno `
            -Version $CurrentVersion
        Assert-NativeSuccess "Current release-identity Inno build"
        $script:currentPayload = Join-Path $RepoRoot "publish-local-x64\OpenClaw.Tray.WinUI.exe"
        $script:evidence.currentInstallerSource = "current-source-build"
    } else {
        if (-not (Test-Path -LiteralPath $CurrentInstallerPath -PathType Leaf)) {
            throw "Current installer override does not exist: $CurrentInstallerPath"
        }
        if ([string]::IsNullOrWhiteSpace($CurrentPayloadPath)) {
            throw "-CurrentInstallerPath requires -CurrentPayloadPath for installed payload hash proof."
        }
        $script:currentInstaller = (Resolve-Path -LiteralPath $CurrentInstallerPath).Path
        $script:currentPayload = (Resolve-Path -LiteralPath $CurrentPayloadPath).Path
        $script:evidence.currentInstallerSource = "local-override"
    }

    if (-not (Test-Path -LiteralPath $currentInstaller -PathType Leaf)) {
        throw "Current installer was not produced: $currentInstaller"
    }
    if (-not (Test-Path -LiteralPath $currentPayload -PathType Leaf)) {
        throw "Current payload was not produced: $currentPayload"
    }
    $identityPath = Join-Path (Split-Path -Parent $currentPayload) "app-identity.txt"
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
        throw "Current payload is missing app-identity.txt: $identityPath"
    }
    $identity = (Get-Content -LiteralPath $identityPath -Raw).Trim()
    if ($identity -ne "release") {
        throw "Current payload identity '$identity' is not 'release'."
    }

    $script:currentInstallerHash = (Get-FileHash -LiteralPath $currentInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($currentInstallerHash -eq $previousInstallerHash) {
        throw "Previous and current installer hashes are identical. Reinstalling the same payload is not upgrade proof."
    }
}

function Invoke-InnoInstall {
    param(
        [Parameter(Mandatory = $true)][string]$Installer,
        [Parameter(Mandatory = $true)][string]$LogName
    )

    New-Item -ItemType Directory -Force -Path $installBase | Out-Null
    $captureName = "inno-{0}" -f ([IO.Path]::GetFileNameWithoutExtension($LogName).ToLowerInvariant() -replace '[^a-z0-9.-]', '-')
    $installResult = Invoke-SmokeNativeProcess `
        -Operation "Inno installer '$Installer'" `
        -FilePath $Installer `
        -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/NOCANCEL",
            "/CLOSEAPPLICATIONS",
            "/DIR=$installRoot",
            "/LOG=$(Join-Path $ArtifactRoot $LogName)"
        ) `
        -TimeoutSeconds 600 `
        -CaptureRoot $ArtifactRoot `
        -CaptureName $captureName `
        -WorkingDirectory $RepoRoot
    Write-SmokeNativeProcessOutput -Result $installResult
    Assert-SmokeNativeProcessSucceeded -Result $installResult
    if (-not (Test-Path -LiteralPath $installedTray -PathType Leaf)) {
        throw "Installed tray payload is missing after Inno install: $installedTray"
    }
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Inno uninstaller is missing after install: $uninstaller"
    }
}

function Get-ReleaseRegistration {
    if (-not (Test-Path -LiteralPath $releaseUninstallKey)) {
        throw "Release uninstall registration is missing: $releaseUninstallKey"
    }
    if (Test-Path -LiteralPath $releaseWowUninstallKey) {
        throw "Release identity is ambiguous because both native and WOW6432 uninstall registrations exist."
    }
    return Get-ItemProperty -LiteralPath $releaseUninstallKey -ErrorAction Stop
}

function Assert-RegisteredVersion {
    param([Parameter(Mandatory = $true)][string]$ExpectedVersion)

    $registration = Get-ReleaseRegistration
    if (-not [string]::Equals([string]$registration.DisplayVersion, $ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Registered DisplayVersion '$($registration.DisplayVersion)' does not match '$ExpectedVersion'."
    }
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath([string]$registration.InstallLocation).TrimEnd('\'),
        [IO.Path]::GetFullPath($installRoot).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release registration InstallLocation is not the smoke-owned install root."
    }
    return $registration
}

function Get-TrayEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    return [ordered]@{
        path = $item.FullName
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        fileVersion = [string]$item.VersionInfo.FileVersion
        productVersion = [string]$item.VersionInfo.ProductVersion
        signatureStatus = $signature.Status.ToString()
        signerSubject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
        signerThumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { "" }
    }
}

function Assert-PreviousSignature {
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary]$TrayEvidence)

    if ($AllowUnsignedPreviousPayload) {
        Write-Host "Previous payload signature requirement explicitly disabled for local/offline deterministic testing."
        return
    }
    if ($TrayEvidence.signatureStatus -ne "Valid") {
        throw "Previous release tray signature is '$($TrayEvidence.signatureStatus)', not Valid."
    }
    if ($TrayEvidence.signerSubject -notmatch "OpenClaw Foundation") {
        throw "Previous release tray signer is not the OpenClaw Foundation release identity."
    }
}

function New-PreservationState {
    $gatewayId = "11111111-2222-3333-4444-555555555555"
    $identityDir = Join-Path (Join-Path $releaseRoamingData "gateways") $gatewayId
    New-Item -ItemType Directory -Force -Path $identityDir | Out-Null

    $settingsPath = Join-Path $releaseRoamingData "settings.json"
    $gatewaysPath = Join-Path $releaseRoamingData "gateways.json"
    $identityPath = Join-Path $identityDir "device-key-ed25519.json"

    [ordered]@{
        SettingsVersion = 1
        EnableNodeMode = $true
        EnableMcpServer = $true
        AutoStart = $false
        UpgradeSmokeMarker = $runId
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

    [ordered]@{
        activeId = $gatewayId
        gateways = @(
            [ordered]@{
                id = $gatewayId
                url = "wss://upgrade-smoke.invalid"
                friendlyName = "Upgrade smoke external gateway"
                sharedGatewayToken = $null
                bootstrapToken = $null
                lastConnected = "2026-01-02T03:04:05Z"
                isLocal = $false
                requiresV2Signature = $false
                setupManagedDistroName = $null
                sshTunnel = $null
                browserControlPort = $null
            }
        )
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $gatewaysPath -Encoding UTF8

    [ordered]@{
        DeviceId = "upgrade-smoke-device"
        SchemaVersion = 1
        UpgradeSmokeMarker = $runId
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $identityPath -Encoding UTF8

    $script:ownsSeededState = $true
    foreach ($path in @($settingsPath, $gatewaysPath, $identityPath)) {
        $script:stateHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $stateHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactRoot "preservation-state.json") -Encoding UTF8
}

function Assert-PreservationState {
    foreach ($entry in $stateHashes.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Key -PathType Leaf)) {
            throw "Preserved upgrade state is missing: $($entry.Key)"
        }
        $actual = (Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Value) {
            throw "Preserved upgrade state changed unexpectedly: $($entry.Key)"
        }
    }
}

function Stop-InstalledTrayProcesses {
    if (-not (Test-Path -LiteralPath $installRoot)) {
        return
    }

    $expectedPath = [IO.Path]::GetFullPath($installedTray)
    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
        [string]::Equals(
            [IO.Path]::GetFullPath($_.ExecutablePath),
            $expectedPath,
            [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($process in $processes) {
        Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
    }
}

function Remove-OwnedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to recursively delete smoke-owned reparse point '$Path'."
    }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
}

function Assert-PostCleanup {
    foreach ($guard in @(
        @{ Path = $releaseUninstallKey; Label = "Release uninstall registration" },
        @{ Path = $releaseWowUninstallKey; Label = "Release WOW6432 uninstall registration" },
        @{ Path = $devUninstallKey; Label = "DEV uninstall registration" },
        @{ Path = $devWowUninstallKey; Label = "DEV WOW6432 uninstall registration" },
        @{ Path = $releaseProtocolKey; Label = "Release protocol registration" },
        @{ Path = $devProtocolKey; Label = "DEV protocol registration" },
        @{ Path = $installedTray; Label = "Installed tray" },
        @{ Path = $uninstaller; Label = "Inno uninstaller" },
        @{ Path = $releaseRoamingData; Label = "Smoke-owned release roaming data" },
        @{ Path = $releaseLocalData; Label = "Smoke-owned release local data" },
        @{ Path = $devRoamingData; Label = "DEV roaming data" },
        @{ Path = $devLocalData; Label = "DEV local data" },
        @{ Path = $installBase; Label = "Upgrade smoke install root" }
    )) {
        if (Test-Path -LiteralPath $guard.Path) {
            throw "$($guard.Label) remains after cleanup: $($guard.Path)"
        }
    }
    Assert-RegistryValueAbsent -Path $runKey -Name "OpenClawTray" -Label "Release autostart value"
    Assert-RegistryValueAbsent -Path $runKey -Name "OpenClawTray-Dev" -Label "DEV autostart value"
    Assert-ScheduledTaskAbsent -Name "OpenClaw Companion"
    Assert-ScheduledTaskAbsent -Name "OpenClaw Companion (Dev)"
    foreach ($shortcutPath in $protectedShortcutPaths) {
        Assert-PathAbsent -Path $shortcutPath -Label "OpenClaw shortcut"
    }
    $distros = Get-WslDistroNames
    foreach ($name in @("OpenClawGateway", "OpenClawGateway-Dev")) {
        if ($distros -contains $name) {
            throw "App-owned WSL distro remains after cleanup: $name"
        }
    }
}

function Invoke-Cleanup {
    if ($cleanupCompleted) {
        return
    }

    Stop-InstalledTrayProcesses
    if ($ownsInstall) {
        if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
            if ((Test-Path -LiteralPath $releaseUninstallKey) -or
                (Test-Path -LiteralPath $releaseWowUninstallKey)) {
                throw "Smoke-owned install has a registration but no uninstaller. Refusing to hide incomplete cleanup."
            }
            Remove-OwnedDirectory -Path $installBase
            $script:ownsInstall = $false
        } else {
            $uninstallResult = Invoke-SmokeNativeProcess `
                -Operation "Release Inno uninstaller" `
                -FilePath $uninstaller `
                -ArgumentList @(
                    "/VERYSILENT",
                    "/SUPPRESSMSGBOXES",
                    "/NORESTART",
                    "/LOG=$(Join-Path $ArtifactRoot "inno-uninstall.log")"
                ) `
                -TimeoutSeconds 300 `
                -CaptureRoot $ArtifactRoot `
                -CaptureName "inno-uninstall" `
                -WorkingDirectory $installRoot
            Write-SmokeNativeProcessOutput -Result $uninstallResult
            Assert-SmokeNativeProcessSucceeded -Result $uninstallResult
            $script:ownsInstall = $false
        }
    }

    if ($ownsSeededState) {
        Assert-PreservationState
        Remove-OwnedDirectory -Path $releaseRoamingData
        $script:ownsSeededState = $false
    }
    if (Test-Path -LiteralPath $releaseLocalData) {
        Remove-OwnedDirectory -Path $releaseLocalData
    }
    if (Test-Path -LiteralPath $installBase) {
        Remove-OwnedDirectory -Path $installBase
    }

    Assert-PostCleanup
    $script:cleanupCompleted = $true
}

try {
    New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null
    Remove-Item -LiteralPath $logPath, $donePath, $statusPath -Force -ErrorAction SilentlyContinue
    Set-Location $RepoRoot
    Write-Host "OpenClaw Windows Inno previous-release upgrade smoke"
    Write-Host "Artifacts: $ArtifactRoot"

    Invoke-Phase "preflight" {
        Assert-CleanMachineReleaseIdentity
    }

    if ($SafetyPreflightOnly) {
        Write-MainLog "Safety preflight passed. No installer was acquired, installed, upgraded, or removed."
        $exitCode = 0
    } else {
        Resolve-PreviousVersion
        Resolve-CurrentVersion

        Invoke-Phase "acquire-previous" {
            Resolve-PreviousInstaller
            "previousInstaller=$previousInstaller"
            "previousInstallerSha256=$previousInstallerHash"
        }

        Invoke-Phase "prepare-current" {
            Resolve-CurrentInstaller
            "currentInstaller=$currentInstaller"
            "currentInstallerSha256=$currentInstallerHash"
            "currentPayload=$currentPayload"
        }

        Invoke-Phase "install-previous" {
            $script:ownsInstall = $true
            Invoke-InnoInstall -Installer $previousInstaller -LogName "inno-install-previous.log"
            $registration = Assert-RegisteredVersion -ExpectedVersion $PreviousVersion
            $previousTrayEvidence = Get-TrayEvidence -Path $installedTray
            Assert-PreviousSignature -TrayEvidence $previousTrayEvidence
            $script:previousTrayHash = $previousTrayEvidence.sha256
            $script:evidence.previousRegistration = [ordered]@{
                displayName = [string]$registration.DisplayName
                displayVersion = [string]$registration.DisplayVersion
                installLocation = [string]$registration.InstallLocation
                uninstallString = [string]$registration.UninstallString
            }
            $script:evidence.previousTray = $previousTrayEvidence
        }

        Invoke-Phase "seed-state" {
            New-PreservationState
            Assert-PreservationState
        }

        Invoke-Phase "upgrade-current" {
            $priorUninstallerHash = (Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash.ToLowerInvariant()
            Invoke-InnoInstall -Installer $currentInstaller -LogName "inno-install-current.log"
            $registration = Assert-RegisteredVersion -ExpectedVersion $CurrentVersion
            $upgradedUninstallerHash = (Get-FileHash -LiteralPath $uninstaller -Algorithm SHA256).Hash.ToLowerInvariant()
            $script:evidence.currentRegistration = [ordered]@{
                displayName = [string]$registration.DisplayName
                displayVersion = [string]$registration.DisplayVersion
                installLocation = [string]$registration.InstallLocation
                uninstallString = [string]$registration.UninstallString
            }
            $script:evidence.uninstallerTransition = [ordered]@{
                previousSha256 = $priorUninstallerHash
                currentSha256 = $upgradedUninstallerHash
            }
        }

        Invoke-Phase "state-preservation" {
            Assert-PreservationState
        }

        Invoke-Phase "installed-payload" {
            $identity = (Get-Content -LiteralPath (Join-Path $installRoot "app-identity.txt") -Raw).Trim()
            if ($identity -ne "release") {
                throw "Installed current payload identity '$identity' is not 'release'."
            }
            $currentTrayEvidence = Get-TrayEvidence -Path $installedTray
            $expectedCurrentHash = (Get-FileHash -LiteralPath $currentPayload -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($currentTrayEvidence.sha256 -ne $expectedCurrentHash) {
                throw "Installed current tray hash does not match the current installer payload."
            }
            if ($currentTrayEvidence.sha256 -eq $previousTrayHash) {
                throw "Previous and current installed tray hashes are identical. Upgrade proof requires different payloads."
            }
            if (-not ([string]$currentTrayEvidence.productVersion).StartsWith(
                $CurrentVersion,
                [StringComparison]::OrdinalIgnoreCase)) {
                throw "Installed current ProductVersion '$($currentTrayEvidence.productVersion)' does not identify '$CurrentVersion'."
            }
            $script:currentTrayHash = $currentTrayEvidence.sha256
            $script:evidence.currentTray = $currentTrayEvidence
        }

        Invoke-Phase "roundtrip" {
            $proofRoot = Join-Path $ArtifactRoot "installed-runtime-proof"
            $proofScript = Join-Path $RepoRoot "scripts\validate-installed-inno-smoke.ps1"
            $windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
            & $windowsPowerShell `
                -NoProfile `
                -ExecutionPolicy Bypass `
                -File $proofScript `
                -RepoRoot $RepoRoot `
                -ArtifactRoot $proofRoot `
                -ProofInstalledPayloadOnly `
                -InstalledTrayPath $installedTray `
                -ExpectedPayloadPath $currentPayload `
                -ExpectedIdentity release
            Assert-NativeSuccess "Installed published-gateway Windows node and native chat proof"

            $proofStatusPath = Join-Path $proofRoot "phase-status.json"
            if (-not (Test-Path -LiteralPath $proofStatusPath -PathType Leaf)) {
                throw "Installed runtime proof did not produce phase-status.json."
            }
            $proofStatus = Get-Content -LiteralPath $proofStatusPath -Raw | ConvertFrom-Json
            if ([int]$proofStatus.exitCode -ne 0 -or [string]$proofStatus.mode -ne "proof-only") {
                throw "Installed runtime proof did not complete in proof-only mode."
            }
            foreach ($phase in @("preflight", "installed-payload", "roundtrip")) {
                if ([string]$proofStatus.phases.$phase -ne "passed") {
                    throw "Installed runtime proof phase '$phase' did not pass."
                }
            }
            Assert-PreservationState
        }

        Invoke-Phase "cleanup" {
            Invoke-Cleanup
        }

        foreach ($phase in $requiredPhases) {
            if (-not $phaseResults.Contains($phase) -or $phaseResults[$phase] -ne "passed") {
                throw "Upgrade smoke phase '$phase' was not completed successfully."
            }
        }
        $exitCode = 0
    }
} catch {
    $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8
    if (-not $SafetyPreflightOnly -and -not $cleanupCompleted -and ($ownsInstall -or $ownsSeededState)) {
        try {
            Invoke-Phase "cleanup" {
                Invoke-Cleanup
            }
        } catch {
            $_ | Out-String | Add-Content -LiteralPath $logPath -Encoding UTF8
        }
    }
    $exitCode = 1
} finally {
    [ordered]@{
        runId = $runId
        exitCode = $exitCode
        upgradeExecuted = -not $SafetyPreflightOnly
        artifactRoot = $ArtifactRoot
        previousRelease = $PreviousRelease
        previousVersion = $PreviousVersion
        currentVersion = $CurrentVersion
        previousInstallerSha256 = $previousInstallerHash
        currentInstallerSha256 = $currentInstallerHash
        previousTraySha256 = $previousTrayHash
        currentTraySha256 = $currentTrayHash
        cleanupCompleted = $cleanupCompleted
        phases = $phaseResults
        evidence = $evidence
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statusPath -Encoding UTF8
    Set-Content -LiteralPath $donePath -Value $exitCode -Encoding ASCII
}

if ($exitCode -eq 0) {
    if ($SafetyPreflightOnly) {
        Write-Host "Release-identity safety preflight passed. No upgrade was executed."
    } else {
        Write-Host "Windows Inno previous-release upgrade smoke passed."
    }
    Write-Host "Artifacts: $ArtifactRoot"
} else {
    Write-Warning "Windows Inno previous-release upgrade smoke failed. Inspect $logPath and $statusPath."
}
exit $exitCode
