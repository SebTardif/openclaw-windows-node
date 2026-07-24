#requires -Version 5.1
<#
.SYNOPSIS
    Installs an official Crabbox Windows release into a user-local directory.

.DESCRIPTION
    Downloads release metadata from the GitHub API for openclaw/crabbox, verifies
    the selected Windows ZIP against immutable release metadata, checksums.txt,
    and provenance.json, then extracts it into a versioned folder under
    LOCALAPPDATA without changing PATH or execution policy.
#>

[CmdletBinding()]
param(
    [string]$Version = "latest",

    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "OpenClaw\Crabbox"),

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:GitHubApiRoot = "https://api.github.com/repos/openclaw/crabbox"

function Set-Tls12IfAvailable {
    if ([enum]::IsDefined([Net.SecurityProtocolType], "Tls12")) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
}

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [IO.Path]::GetFullPath($Path)
}

function Normalize-ComparisonPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return ([IO.Path]::GetFullPath($Path)).TrimEnd("\").ToLowerInvariant()
}

function Assert-InstallRootUnderLocalAppData {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw "LOCALAPPDATA is not set."
    }

    $resolvedRoot = Resolve-FullPath -Path $Path
    $resolvedLocalAppData = Resolve-FullPath -Path $env:LOCALAPPDATA
    $normalizedRoot = Normalize-ComparisonPath -Path $resolvedRoot
    $normalizedLocalAppData = Normalize-ComparisonPath -Path $resolvedLocalAppData

    if ($normalizedRoot -ne $normalizedLocalAppData -and -not $normalizedRoot.StartsWith("$normalizedLocalAppData\")) {
        throw "InstallRoot must stay under LOCALAPPDATA. Value: $resolvedRoot"
    }
}

function Invoke-GitHubJsonRequest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri
    )

    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "OpenClaw-CleanWindows-Installer"
    }

    try {
        return Invoke-RestMethod -Uri $Uri -Headers $headers -Method Get -UseBasicParsing
    } catch {
        throw "GitHub API request failed for '$Uri'. $($_.Exception.Message)"
    }
}

function Invoke-DownloadFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $headers = @{
        "User-Agent" = "OpenClaw-CleanWindows-Installer"
    }

    Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $DestinationPath -UseBasicParsing
}

function Get-NativeWindowsArchitecture {
    $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $processor) {
        throw "Unable to determine Windows processor architecture."
    }

    switch ([int]$processor.Architecture) {
        9 { return "amd64" }
        12 { return "arm64" }
        default { throw "Unsupported Windows processor architecture code '$($processor.Architecture)'." }
    }
}

function Normalize-VersionTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "Version must not be empty."
    }

    if ($trimmed -eq "latest") {
        return "latest"
    }

    if ($trimmed.StartsWith("v")) {
        return $trimmed
    }

    return "v$trimmed"
}

function Get-ReleaseMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RequestedVersion
    )

    $normalizedVersion = Normalize-VersionTag -Value $RequestedVersion
    if ($normalizedVersion -eq "latest") {
        return Invoke-GitHubJsonRequest -Uri "$script:GitHubApiRoot/releases/latest"
    }

    $encodedTag = [Uri]::EscapeDataString($normalizedVersion)
    return Invoke-GitHubJsonRequest -Uri "$script:GitHubApiRoot/releases/tags/$encodedTag"
}

function Get-AssetByName {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Assets,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $matches = @($Assets | Where-Object { $_.name -eq $Name })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one release asset named '$Name'. Found $($matches.Count)."
    }

    return $matches[0]
}

function Get-AssetDigestSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Asset
    )

    $digest = ""
    if ($Asset.PSObject.Properties["digest"]) {
        $digest = [string]$Asset.digest
    }
    if ([string]::IsNullOrWhiteSpace($digest) -or -not $digest.StartsWith("sha256:")) {
        throw "Release asset '$($Asset.name)' does not expose a SHA-256 digest."
    }

    $sha = $digest.Substring("sha256:".Length).ToLowerInvariant()
    if ($sha -notmatch "^[0-9a-f]{64}$") {
        throw "Release asset '$($Asset.name)' returned an invalid SHA-256 digest."
    }

    return $sha
}

function Assert-AssetIntegrity {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Asset,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $actualHash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = Get-AssetDigestSha256 -Asset $Asset
    if ($actualHash -ne $expectedHash) {
        throw "Downloaded asset '$($Asset.name)' did not match the GitHub asset digest."
    }

    return $actualHash
}

function Read-ChecksumMap {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $map = @{}
    $lines = Get-Content -LiteralPath $Path -ErrorAction Stop
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        $match = [regex]::Match($trimmed, "^(?<sha>[0-9A-Fa-f]{64})\s+\*?(?<name>.+)$")
        if (-not $match.Success) {
            throw "Unable to parse checksums.txt line: $trimmed"
        }

        $map[$match.Groups["name"].Value] = $match.Groups["sha"].Value.ToLowerInvariant()
    }

    return $map
}

function Assert-ProvenanceMentionsAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$AssetName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256
    )

    $provenance = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop

    $releaseAssetNames = @()
    if ($provenance.PSObject.Properties["releaseAssets"]) {
        $releaseAssetNames = @($provenance.releaseAssets)
    }

    if (-not ($releaseAssetNames -contains $AssetName)) {
        throw "provenance.json does not list asset '$AssetName'."
    }

    $payloadMatches = @()
    if ($provenance.PSObject.Properties["payloads"]) {
        $payloadMatches = @($provenance.payloads | Where-Object {
            $_.name -eq $AssetName -and
            ([string]$_.sha256) -eq $ExpectedSha256
        })
    }

    if ($payloadMatches.Count -lt 1) {
        throw "provenance.json does not include a matching payload digest for '$AssetName'."
    }
}

function Expand-ZipSafely {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ZipPath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationRoot
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $destinationRoot = Resolve-FullPath -Path $DestinationRoot
    New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
    $normalizedRoot = (Normalize-ComparisonPath -Path $destinationRoot) + "\"

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($entry in $archive.Entries) {
            $entryPath = $entry.FullName.Replace("/", "\")
            if ([string]::IsNullOrWhiteSpace($entryPath)) {
                continue
            }

            $destinationPath = [IO.Path]::GetFullPath((Join-Path $destinationRoot $entryPath))
            if (-not $destinationPath.ToLowerInvariant().StartsWith($normalizedRoot)) {
                throw "Archive entry escapes the destination root: $($entry.FullName)"
            }

            if ($entry.FullName.EndsWith("/") -or $entry.FullName.EndsWith("\")) {
                New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
                continue
            }

            $parent = Split-Path -Parent $destinationPath
            if (-not [string]::IsNullOrWhiteSpace($parent)) {
                New-Item -ItemType Directory -Force -Path $parent | Out-Null
            }

            $source = $entry.Open()
            try {
                $target = [IO.File]::Open($destinationPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $source.CopyTo($target)
                } finally {
                    $target.Dispose()
                }
            } finally {
                $source.Dispose()
            }

            if ($entry.LastWriteTime.UtcDateTime -gt [DateTime]::MinValue) {
                [IO.File]::SetLastWriteTimeUtc($destinationPath, $entry.LastWriteTime.UtcDateTime)
            }
        }
    } finally {
        $archive.Dispose()
    }
}

function Get-AuthenticodeSummary {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $signerSubject = ""
    $signerThumbprint = ""
    $timeStamperSubject = ""
    if ($signature.SignerCertificate) {
        $signerSubject = [string]$signature.SignerCertificate.Subject
        $signerThumbprint = [string]$signature.SignerCertificate.Thumbprint
    }
    if ($signature.TimeStamperCertificate) {
        $timeStamperSubject = [string]$signature.TimeStamperCertificate.Subject
    }

    return [ordered]@{
        status = [string]$signature.Status
        statusMessage = [string]$signature.StatusMessage
        isTrustedSignature = ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid)
        signerSubject = $signerSubject
        signerThumbprint = $signerThumbprint
        timeStamperSubject = $timeStamperSubject
    }
}

Set-Tls12IfAvailable
Assert-InstallRootUnderLocalAppData -Path $InstallRoot

$installRootResolved = Resolve-FullPath -Path $InstallRoot
$architecture = Get-NativeWindowsArchitecture
$release = $null
$stagingRoot = $null
$stagingDownloads = $null
$stagingExtract = $null
$versionDirectory = $null
$manifestPath = $null

try {
    $release = Get-ReleaseMetadata -RequestedVersion $Version
    if ($null -eq $release) {
        throw "Unable to retrieve Crabbox release metadata."
    }

    if ($release.draft) {
        throw "Refusing to install a draft release."
    }
    if ($release.prerelease) {
        throw "Refusing to install a prerelease."
    }
    if (-not $release.immutable) {
        throw "Refusing to install a non-immutable release."
    }

    $tagName = [string]$release.tag_name
    $versionNumber = $tagName.TrimStart("v")
    $zipAssetName = "crabbox_{0}_windows_{1}.zip" -f $versionNumber, $architecture
    $zipAsset = Get-AssetByName -Assets @($release.assets) -Name $zipAssetName
    $checksumsAsset = Get-AssetByName -Assets @($release.assets) -Name "checksums.txt"
    $provenanceAsset = Get-AssetByName -Assets @($release.assets) -Name "provenance.json"

    $versionDirectory = Join-Path $installRootResolved ("versions\{0}-windows-{1}" -f $tagName, $architecture)
    if (Test-Path -LiteralPath $versionDirectory) {
        if (-not $Force) {
            throw "Crabbox '$tagName' for '$architecture' is already installed at '$versionDirectory'. Use -Force to replace it."
        }

        Remove-Item -LiteralPath $versionDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $installRootResolved | Out-Null

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $stagingRoot = Join-Path $installRootResolved ("_staging\{0}-{1}-{2}" -f $tagName, $architecture, $timestamp)
    $stagingDownloads = Join-Path $stagingRoot "downloads"
    $stagingExtract = Join-Path $stagingRoot "extract"
    New-Item -ItemType Directory -Force -Path $stagingDownloads, $stagingExtract | Out-Null

    $zipPath = Join-Path $stagingDownloads $zipAsset.name
    $checksumsPath = Join-Path $stagingDownloads $checksumsAsset.name
    $provenancePath = Join-Path $stagingDownloads $provenanceAsset.name

    Invoke-DownloadFile -Uri $zipAsset.browser_download_url -DestinationPath $zipPath
    $zipHash = Assert-AssetIntegrity -Asset $zipAsset -FilePath $zipPath

    Invoke-DownloadFile -Uri $checksumsAsset.browser_download_url -DestinationPath $checksumsPath
    Assert-AssetIntegrity -Asset $checksumsAsset -FilePath $checksumsPath | Out-Null

    Invoke-DownloadFile -Uri $provenanceAsset.browser_download_url -DestinationPath $provenancePath
    Assert-AssetIntegrity -Asset $provenanceAsset -FilePath $provenancePath | Out-Null

    $checksums = Read-ChecksumMap -Path $checksumsPath
    if (-not $checksums.ContainsKey($zipAsset.name)) {
        throw "checksums.txt does not include '$($zipAsset.name)'."
    }
    if ($checksums[$zipAsset.name] -ne $zipHash) {
        throw "checksums.txt does not match the downloaded ZIP hash."
    }

    Assert-ProvenanceMentionsAsset -Path $provenancePath -AssetName $zipAsset.name -ExpectedSha256 $zipHash

    Expand-ZipSafely -ZipPath $zipPath -DestinationRoot $stagingExtract

    $crabboxCandidates = @(Get-ChildItem -LiteralPath $stagingExtract -Filter "crabbox.exe" -File -Recurse)
    if ($crabboxCandidates.Count -ne 1) {
        throw "Expected exactly one crabbox.exe after extraction. Found $($crabboxCandidates.Count)."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $versionDirectory) | Out-Null
    Move-Item -LiteralPath $stagingExtract -Destination $versionDirectory
    $metadataDirectory = Join-Path $versionDirectory "metadata"
    New-Item -ItemType Directory -Force -Path $metadataDirectory | Out-Null
    Copy-Item -LiteralPath $checksumsPath -Destination (Join-Path $metadataDirectory "checksums.txt")
    Copy-Item -LiteralPath $provenancePath -Destination (Join-Path $metadataDirectory "provenance.json")
    $release | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $metadataDirectory "release.json") -Encoding UTF8

    $installedCrabbox = @(Get-ChildItem -LiteralPath $versionDirectory -Filter "crabbox.exe" -File -Recurse)
    if ($installedCrabbox.Count -ne 1) {
        throw "Installed Crabbox layout is invalid. Expected exactly one crabbox.exe."
    }

    $crabboxExePath = $installedCrabbox[0].FullName
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $versionOutput = & $crabboxExePath --version 2>&1
        $versionExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($versionExitCode -ne 0) {
        throw "crabbox.exe --version failed with exit code $versionExitCode."
    }

    $signatureSummary = Get-AuthenticodeSummary -Path $crabboxExePath
    $manifestPath = Join-Path $versionDirectory "installation-manifest.json"
    $manifest = [ordered]@{
        installedAtUtc = [DateTime]::UtcNow.ToString("o")
        installRoot = $installRootResolved
        versionDirectory = $versionDirectory
        executablePath = $crabboxExePath
        executableVersionOutput = (($versionOutput | Out-String).Trim())
        release = [ordered]@{
            tag = $tagName
            version = $versionNumber
            id = [string]$release.id
            htmlUrl = [string]$release.html_url
            publishedAt = [string]$release.published_at
            immutable = [bool]$release.immutable
            draft = [bool]$release.draft
            prerelease = [bool]$release.prerelease
        }
        selectedAsset = [ordered]@{
            platform = "windows"
            architecture = $architecture
            name = $zipAsset.name
            browserDownloadUrl = $zipAsset.browser_download_url
            sha256 = $zipHash
        }
        verification = [ordered]@{
            gitHubAssetDigestMatched = $true
            checksumsMatched = $true
            provenanceMatched = $true
            integritySource = "Immutable GitHub release metadata, checksums.txt, and provenance.json"
        }
        authenticode = $signatureSummary
        metadataFiles = [ordered]@{
            checksums = (Join-Path $metadataDirectory "checksums.txt")
            provenance = (Join-Path $metadataDirectory "provenance.json")
            release = (Join-Path $metadataDirectory "release.json")
        }
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    Write-Host "Installed Crabbox: $crabboxExePath"
    Write-Host "Manifest: $manifestPath"
    if (-not $signatureSummary.isTrustedSignature) {
        Write-Host "Authenticode: $($signatureSummary.status). Integrity was verified from immutable release metadata."
    }
} finally {
    if ($stagingRoot -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
