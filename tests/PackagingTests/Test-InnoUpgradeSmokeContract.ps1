<#
.SYNOPSIS
    Verifies the Windows Inno upgrade smoke's static and fail-closed contracts.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$OutputDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "TestResults\Packaging\InnoUpgradeContract"
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$upgradeScript = Join-Path $RepoRoot "scripts\validate-inno-upgrade-smoke.ps1"
$installedScript = Join-Path $RepoRoot "scripts\validate-installed-inno-smoke.ps1"
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($upgradeScript, [ref]$null, [ref]$errors)
if ($errors.Count -ne 0) {
    throw "Upgrade smoke PowerShell parser errors: $($errors.Message -join '; ')"
}

$source = Get-Content -LiteralPath $upgradeScript -Raw
$installedSource = Get-Content -LiteralPath $installedScript -Raw
foreach ($required in @(
    "ConfirmCleanMachineReleaseIdentity",
    "PreviousInstallerSha256",
    "github.com/openclaw/openclaw-windows-node/releases/download/",
    "Get-AuthenticodeSignature",
    "Assert-PreservationState",
    "validate-installed-inno-smoke.ps1",
    "ProofInstalledPayloadOnly",
    "Assert-PostCleanup"
)) {
    if (-not $source.Contains($required)) {
        throw "Upgrade smoke is missing required contract '$required'."
    }
}
if (-not $installedSource.Contains("[switch]`$ProofInstalledPayloadOnly")) {
    throw "Installed smoke does not expose the proof-only reuse contract."
}
if ($source.Contains("http://")) {
    throw "Upgrade smoke contains a non-HTTPS acquisition URL."
}

$negativeArtifacts = Join-Path $OutputDir "missing-confirmation"
$powerShell = (Get-Command pwsh -ErrorAction Stop).Source
$process = Start-Process -FilePath $powerShell -ArgumentList @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$upgradeScript`"",
    "-RepoRoot", "`"$RepoRoot`"",
    "-ArtifactRoot", "`"$negativeArtifacts`"",
    "-SafetyPreflightOnly"
) -Wait -PassThru
if ($process.ExitCode -eq 0) {
    throw "Upgrade smoke accepted -SafetyPreflightOnly without clean-machine confirmation."
}

$negativeLog = Join-Path $negativeArtifacts "upgrade-smoke.log"
if (-not (Test-Path -LiteralPath $negativeLog -PathType Leaf)) {
    throw "Negative guard run did not produce upgrade-smoke.log."
}
$negativeText = Get-Content -LiteralPath $negativeLog -Raw
if (-not $negativeText.Contains("Pass -ConfirmCleanMachineReleaseIdentity")) {
    throw "Negative guard run did not report the clean-machine confirmation blocker."
}

[ordered]@{
    passed = $true
    parserErrors = 0
    negativeGuardExitCode = $process.ExitCode
    negativeGuardArtifactRoot = $negativeArtifacts
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDir "result.json") -Encoding UTF8

Write-Host "Inno upgrade smoke packaging contract passed."
