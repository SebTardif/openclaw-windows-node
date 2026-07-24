#requires -Version 5.1
<#
.SYNOPSIS
    Plans or runs focused Crabbox Windows validation against the current checkout.

.DESCRIPTION
    Creates a new Azure Windows Crabbox lease for a combined installed smoke or
    a component-only native desktop or WSL2 probe. Captures local artifacts and
    stops only the lease created by this invocation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CrabboxPath,

    [ValidateSet("azure")]
    [string]$Provider = "azure",

    [Parameter(Mandatory = $true)]
    [ValidateSet("CombinedInstalledSmoke", "NativeDesktopComponent", "Wsl2Component")]
    [string]$Mode,

    [ValidateSet("Installed", "Upgrade")]
    [string]$ValidationLane = "Installed",

    [string]$PreviousRelease = "",

    [string]$PreviousInstallerSha256 = "",

    [string]$AzureImage = "",

    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,

    [string]$ArtifactRoot = "",

    [ValidateRange(30, 360)]
    [int]$IdleTimeoutMinutes = 90,

    [ValidateRange(30, 720)]
    [int]$TtlMinutes = 240,

    [switch]$PlanOnly,

    [switch]$RequestUiProof,

    [switch]$RequireFullInstalledSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [IO.Path]::GetFullPath($Path)
}

function Format-CommandArgument {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value -eq "") {
        return '""'
    }

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Format-CommandPreview {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [string]$StdInPath = ""
    )

    $quotedPath = Format-CommandArgument -Value $FilePath
    $quotedArgs = @($Arguments | ForEach-Object { Format-CommandArgument -Value $_ })
    $commandBody = "& $quotedPath " + ($quotedArgs -join " ")
    if ([string]::IsNullOrWhiteSpace($StdInPath)) {
        return $commandBody.Trim()
    }

    $quotedStdIn = Format-CommandArgument -Value $StdInPath
    return "Get-Content -LiteralPath $quotedStdIn -Raw | $commandBody".Trim()
}

function Get-WindowsModeValue {
    switch ($Mode) {
        "CombinedInstalledSmoke" { return "normal" }
        "NativeDesktopComponent" { return "normal" }
        "Wsl2Component" { return "wsl2" }
        default { throw "Unsupported mode '$Mode'." }
    }
}

function Get-ProofClass {
    switch ($Mode) {
        "CombinedInstalledSmoke" {
            if ($ValidationLane -eq "Upgrade") {
                return "combined-native-desktop-wsl2-upgrade-smoke"
            }
            return "combined-native-desktop-wsl2-installed-smoke"
        }
        "NativeDesktopComponent" { return "native-desktop-component-only" }
        "Wsl2Component" { return "wsl2-component-only" }
        default { throw "Unsupported mode '$Mode'." }
    }
}

function Get-ModeArtifactName {
    switch ($Mode) {
        "CombinedInstalledSmoke" { return "combined-$($ValidationLane.ToLowerInvariant())-smoke" }
        "NativeDesktopComponent" { return "native-desktop-component" }
        "Wsl2Component" { return "wsl2-component" }
        default { throw "Unsupported mode '$Mode'." }
    }
}

function Get-AzureAuthGuidance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedCrabboxPath
    )

    return @(
        "Azure authentication is required before this script can warm a Crabbox lease.",
        "Run these commands interactively, then rerun this script:",
        "  az login",
        "  & `"$ResolvedCrabboxPath`" azure login --location <approved-location>",
        "  & `"$ResolvedCrabboxPath`" doctor --provider azure --target windows"
    ) -join [Environment]::NewLine
}

function Test-LikelyAzureAuthFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    return $Text -match '(?im)(az login|azure login|login required|not logged in|authentication|unauthorized|forbidden|credential|token|subscription)'
}

function Write-Manifest {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$ManifestObject,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $ManifestObject | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-CrabboxCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedCrabboxPath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactDirectory,
        [string]$StdInText = ""
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    $stdoutPath = Join-Path $ArtifactDirectory "stdout.txt"
    $stderrPath = Join-Path $ArtifactDirectory "stderr.txt"
    $combinedPath = Join-Path $ArtifactDirectory "combined.txt"
    Remove-Item -LiteralPath $stdoutPath, $stderrPath, $combinedPath -Force -ErrorAction SilentlyContinue

    Push-Location $WorkingDirectory
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            # Windows PowerShell 5.1 promotes native stderr to ErrorRecord objects.
            # Keep those records redirected and use the native exit code explicitly.
            $ErrorActionPreference = "Continue"
            if ([string]::IsNullOrEmpty($StdInText)) {
                & $ResolvedCrabboxPath @Arguments 1> $stdoutPath 2> $stderrPath
            } else {
                $previousOutputEncoding = $OutputEncoding
                try {
                    $OutputEncoding = New-Object System.Text.UTF8Encoding -ArgumentList $false
                    $StdInText | & $ResolvedCrabboxPath @Arguments 1> $stdoutPath 2> $stderrPath
                } finally {
                    $OutputEncoding = $previousOutputEncoding
                }
            }
            $exitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    } finally {
        Pop-Location
    }

    $stdout = ""
    if (Test-Path -LiteralPath $stdoutPath) {
        $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    }

    $stderr = ""
    if (Test-Path -LiteralPath $stderrPath) {
        $stderr = Get-Content -LiteralPath $stderrPath -Raw
    }
    $combined = (($stdout, $stderr) -join [Environment]::NewLine).Trim()
    Set-Content -LiteralPath $combinedPath -Value $combined -Encoding UTF8

    return [pscustomobject]@{
        ExitCode = $exitCode
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        CombinedPath = $combinedPath
        Stdout = $stdout
        Stderr = $stderr
        Combined = $combined
        Preview = Format-CommandPreview -FilePath $ResolvedCrabboxPath -Arguments $Arguments
    }
}

function Get-LeaseIdFromWarmupOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $matches = [regex]::Matches($Text, '\bcbx_[0-9a-f]{12}\b')
    $values = @($matches | ForEach-Object { $_.Value } | Select-Object -Unique)
    if ($values.Count -ne 1) {
        throw "Expected exactly one Crabbox lease id in warmup output. Found $($values.Count)."
    }

    return $values[0]
}

function Get-OptionalLeaseIdFromWarmupOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $matches = [regex]::Matches($Text, '\bcbx_[0-9a-f]{12}\b')
    $values = @($matches | ForEach-Object { $_.Value } | Select-Object -Unique)
    if ($values.Count -gt 1) {
        throw "Warmup output contained multiple Crabbox lease ids. Refusing ambiguous cleanup."
    }

    if ($values.Count -eq 1) {
        return $values[0]
    }

    return ""
}

function Get-RunIdentifierFromText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $jsonObjects = @()
    foreach ($line in ($Text -split "(`r`n|`n|`r)")) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith("{")) {
            continue
        }

        try {
            $jsonObjects += ,($trimmed | ConvertFrom-Json -ErrorAction Stop)
        } catch {
        }
    }

    foreach ($jsonObject in $jsonObjects) {
        foreach ($propertyName in @("runId", "runID", "resultId", "resultID", "id")) {
            $property = $jsonObject.PSObject.Properties[$propertyName]
            if ($property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                return [string]$property.Value
            }
        }
    }

    $match = [regex]::Match($Text, '(?im)\b(?:run|result)[-_ ]id\b\s*[:=]\s*(?<id>[A-Za-z0-9._:-]+)')
    if ($match.Success) {
        return $match.Groups["id"].Value
    }

    return ""
}

function Get-RemoteArtifactPathFromOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $matches = [regex]::Matches($Text, '(?im)^artifactArchive=(?<path>[^\r\n]+)$')
    $values = @($matches | ForEach-Object { $_.Groups["path"].Value.Trim() } | Select-Object -Unique)
    if ($values.Count -ne 1 -or [string]::IsNullOrWhiteSpace($values[0])) {
        throw "Expected exactly one artifactArchive path in Crabbox run output. Found $($values.Count)."
    }

    return $values[0]
}

function Assert-ValidationArtifactContract {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ValidationArtifactRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Installed", "Upgrade")]
        [string]$Lane
    )

    $phaseStatusPath = Join-Path $ValidationArtifactRoot "phase-status.json"
    if (-not (Test-Path -LiteralPath $phaseStatusPath -PathType Leaf)) {
        throw "Retrieved Crabbox artifacts are missing phase-status.json."
    }
    if ($Lane -ne "Upgrade") {
        return
    }

    $phaseStatus = Get-Content -LiteralPath $phaseStatusPath -Raw | ConvertFrom-Json
    if ([int]$phaseStatus.exitCode -ne 0 -or -not [bool]$phaseStatus.cleanupCompleted) {
        throw "Retrieved upgrade phase status does not report successful cleanup."
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
        $phaseProperty = $phaseStatus.phases.PSObject.Properties[$phase]
        if ($null -eq $phaseProperty -or [string]$phaseProperty.Value -ne "passed") {
            throw "Retrieved upgrade phase '$phase' did not pass."
        }
    }

    foreach ($requiredArtifact in @(
        "upgrade-smoke.log",
        "upgrade-smoke.done",
        "inno-install-previous.log",
        "inno-install-current.log",
        "installed-runtime-proof\phase-status.json"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $ValidationArtifactRoot $requiredArtifact) -PathType Leaf)) {
            throw "Retrieved upgrade artifacts are missing $requiredArtifact."
        }
    }
    $runtimeStatus = Get-Content -LiteralPath (Join-Path $ValidationArtifactRoot "installed-runtime-proof\phase-status.json") -Raw | ConvertFrom-Json
    if ([int]$runtimeStatus.exitCode -ne 0) {
        throw "Retrieved upgrade installed-runtime-proof did not report exitCode 0."
    }
}

function New-RemoteScriptContent {
    switch ($Mode) {
        "CombinedInstalledSmoke" {
            $combinedScript = @'
$ErrorActionPreference = "Stop"
$repoRoot = (Get-Location).Path
$validationLane = "__VALIDATION_LANE__"
$artifactRoot = Join-Path $repoRoot "__ARTIFACT_ROOT__"
$env:OPENCLAW_REPO_ROOT = $repoRoot
if ($env:PROCESSOR_ARCHITECTURE -ne "AMD64") {
    throw "Combined validation requires an x64 Windows host."
}
if ($env:CRABBOX_DESKTOP -ne "1") {
    throw "Combined validation requires the native desktop capability."
}
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw "Combined image contract requires wsl.exe."
}
& wsl.exe --status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Combined image contract requires operational WSL."
}
$distros = @(& wsl.exe --list --quiet 2>$null) |
    ForEach-Object { ($_ -replace '\x00', '').Trim() } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$ubuntu = @($distros | Where-Object { $_ -match '^Ubuntu(?:-|$)' } | Select-Object -First 1)
if ($ubuntu.Count -ne 1) {
    throw "Combined image contract requires a prepared Ubuntu WSL distribution."
}
$kernel = (& wsl.exe --distribution $ubuntu[0] --exec uname -r 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $kernel -notmatch '(?i)(microsoft-standard-WSL2|WSL2)') {
    throw "Combined image contract requires a running WSL2 Ubuntu distribution."
}
Write-Output "combinedImageContract=passed"
Write-Output "combinedImageUbuntu=$($ubuntu[0])"
Write-Output "combinedImageKernel=$kernel"
if ($validationLane -eq "Upgrade") {
    $validationScript = Join-Path $repoRoot "scripts\validate-inno-upgrade-smoke.ps1"
    $validationEngine = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $validationArguments = @(
        "-NoProfile",
        "-File", $validationScript,
        "-RepoRoot", $repoRoot,
        "-ArtifactRoot", $artifactRoot,
        "-PreviousRelease", "__PREVIOUS_RELEASE__",
        "-PreviousInstallerSha256", "__PREVIOUS_INSTALLER_SHA256__",
        "-ConfirmCleanMachineReleaseIdentity"
    )
} else {
    $validationScript = Join-Path $repoRoot "scripts\validate-installed-inno-smoke.ps1"
    $validationEngine = "powershell.exe"
    $validationArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $validationScript,
        "-RepoRoot", $repoRoot,
        "-ArtifactRoot", $artifactRoot
    )
}
if (-not (Test-Path -LiteralPath $validationScript -PathType Leaf)) {
    throw "$validationLane validation script does not exist: $validationScript"
}
& $validationEngine @validationArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
$phaseStatus = Join-Path $artifactRoot "phase-status.json"
if (-not (Test-Path -LiteralPath $phaseStatus)) {
    throw "$validationLane validation did not produce phase-status.json."
}
if ($validationLane -eq "Upgrade") {
    $status = Get-Content -LiteralPath $phaseStatus -Raw | ConvertFrom-Json
    if ([int]$status.exitCode -ne 0 -or -not [bool]$status.cleanupCompleted) {
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
        $phaseProperty = $status.phases.PSObject.Properties[$phase]
        if ($null -eq $phaseProperty -or [string]$phaseProperty.Value -ne "passed") {
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
        if (-not (Test-Path -LiteralPath (Join-Path $artifactRoot $requiredArtifact) -PathType Leaf)) {
            throw "Upgrade artifacts are missing $requiredArtifact."
        }
    }
    $runtimeStatus = Get-Content -LiteralPath (Join-Path $artifactRoot "installed-runtime-proof\phase-status.json") -Raw | ConvertFrom-Json
    if ([int]$runtimeStatus.exitCode -ne 0) {
        throw "Upgrade installed-runtime-proof did not report exitCode 0."
    }
}
$archivePath = Join-Path $repoRoot "__ARCHIVE_NAME__"
Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $artifactRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $archivePath)) {
    throw "$validationLane artifact archive was not created."
}
Write-Output "artifactArchive=$archivePath"
'@
            $artifactDirectory = if ($ValidationLane -eq "Upgrade") {
                "TestResults\CrabboxCombinedUpgradeSmoke"
            } else {
                "TestResults\CrabboxCombinedInstalledSmoke"
            }
            $archiveName = "openclaw-$($ValidationLane.ToLowerInvariant())-smoke-artifacts.zip"
            $combinedScript = $combinedScript.Replace("__VALIDATION_LANE__", $ValidationLane)
            $combinedScript = $combinedScript.Replace("__ARTIFACT_ROOT__", $artifactDirectory)
            $combinedScript = $combinedScript.Replace("__PREVIOUS_RELEASE__", $PreviousRelease)
            $combinedScript = $combinedScript.Replace("__PREVIOUS_INSTALLER_SHA256__", $PreviousInstallerSha256.ToLowerInvariant())
            $combinedScript = $combinedScript.Replace("__ARCHIVE_NAME__", $archiveName)
            return $combinedScript
        }
        "NativeDesktopComponent" {
            return @'
$ErrorActionPreference = "Stop"
if ($env:PROCESSOR_ARCHITECTURE -ne "AMD64") {
    throw "Native desktop component proof requires an x64 Windows host."
}
if ($env:CRABBOX_DESKTOP -ne "1") {
    throw "Native desktop component proof requires the desktop capability."
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Native desktop component proof is not running on Windows."
}
Write-Output "nativeDesktopComponent=passed"
Write-Output "nativeDesktopComputer=$env:COMPUTERNAME"
'@
        }
        "Wsl2Component" {
            return @'
set -euo pipefail
osrelease="$(cat /proc/sys/kernel/osrelease)"
case "$osrelease" in
  *WSL2*|*wsl2*) ;;
  *)
    echo "Kernel release does not indicate WSL2: $osrelease" >&2
    exit 1
    ;;
esac
if [ ! -d /mnt/c/Windows/System32 ]; then
  echo "Windows host filesystem is not mounted at /mnt/c/Windows/System32" >&2
  exit 1
fi
if ! command -v wslpath >/dev/null 2>&1; then
  echo "wslpath is required inside the guest." >&2
  exit 1
fi
wslpath -w /mnt/c >/dev/null
printf 'WSL2 capability probe passed\n'
printf 'kernel=%s\n' "$osrelease"
'@
        }
        default {
            throw "Unsupported mode '$Mode'."
        }
    }
}

$resolvedRepoRoot = Resolve-FullPath -Path $RepoRoot
if (-not (Test-Path -LiteralPath $resolvedRepoRoot -PathType Container)) {
    throw "RepoRoot does not exist: $resolvedRepoRoot"
}

if ($TtlMinutes -lt $IdleTimeoutMinutes) {
    throw "TtlMinutes must be greater than or equal to IdleTimeoutMinutes."
}

if ($Mode -eq "CombinedInstalledSmoke" -and [string]::IsNullOrWhiteSpace($AzureImage)) {
    throw "CombinedInstalledSmoke requires -AzureImage for an x64 native Windows image prebaked with WSL2, Ubuntu, and OpenClaw prerequisites."
}

if ($Mode -ne "CombinedInstalledSmoke" -and -not [string]::IsNullOrWhiteSpace($AzureImage)) {
    throw "AzureImage is accepted only for CombinedInstalledSmoke."
}

if ($ValidationLane -eq "Upgrade") {
    if ($Mode -ne "CombinedInstalledSmoke") {
        throw "Upgrade validation requires CombinedInstalledSmoke on one native Windows plus WSL2 host."
    }
    if ($PreviousRelease -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Upgrade validation requires -PreviousRelease as an exact tag-like SemVer such as v0.6.12."
    }
    if ($PreviousInstallerSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Upgrade validation requires -PreviousInstallerSha256 as exactly 64 hexadecimal characters."
    }
    $upgradeScriptPath = Join-Path $resolvedRepoRoot "scripts\validate-inno-upgrade-smoke.ps1"
    if (-not $PlanOnly -and -not (Test-Path -LiteralPath $upgradeScriptPath -PathType Leaf)) {
        throw "Upgrade validation script does not exist in this checkout: $upgradeScriptPath"
    }
} elseif (
    -not [string]::IsNullOrWhiteSpace($PreviousRelease) -or
    -not [string]::IsNullOrWhiteSpace($PreviousInstallerSha256)
) {
    throw "PreviousRelease and PreviousInstallerSha256 are accepted only with -ValidationLane Upgrade."
}

if (-not [string]::IsNullOrWhiteSpace($AzureImage) -and $AzureImage -match '[\x00-\x1f]') {
    throw "AzureImage must not contain control characters."
}

if ($Mode -eq "Wsl2Component" -and $RequestUiProof) {
    throw "Wsl2Component cannot satisfy a UI proof request. Use NativeDesktopComponent or CombinedInstalledSmoke."
}

if ($RequireFullInstalledSmoke -and $Mode -ne "CombinedInstalledSmoke") {
    throw "Only CombinedInstalledSmoke can be labeled as full installed-app proof."
}

if (-not [IO.Path]::IsPathRooted($CrabboxPath)) {
    throw "CrabboxPath must be an absolute path."
}

if (Test-Path -LiteralPath $CrabboxPath) {
    $resolvedCrabboxPath = (Resolve-Path -LiteralPath $CrabboxPath).Path
} else {
    $resolvedCrabboxPath = Resolve-FullPath -Path $CrabboxPath
}
if (-not $PlanOnly -and -not (Test-Path -LiteralPath $resolvedCrabboxPath -PathType Leaf)) {
    throw "CrabboxPath does not exist: $resolvedCrabboxPath"
}

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $ArtifactRoot = Join-Path $resolvedRepoRoot ("TestResults\CleanWindows\Crabbox\{0}-{1}" -f (Get-ModeArtifactName), $timestamp)
}

$resolvedArtifactRoot = Resolve-FullPath -Path $ArtifactRoot
New-Item -ItemType Directory -Force -Path $resolvedArtifactRoot | Out-Null

$windowsMode = Get-WindowsModeValue
$proofClass = Get-ProofClass
$remoteScriptContent = New-RemoteScriptContent
if ($Mode -eq "Wsl2Component") {
    $remoteScriptContent = $remoteScriptContent -replace "`r`n", "`n"
}
switch ($Mode) {
    "CombinedInstalledSmoke" { $remoteScriptFileName = "remote-combined-$($ValidationLane.ToLowerInvariant())-smoke.ps1" }
    "NativeDesktopComponent" { $remoteScriptFileName = "remote-native-desktop-component.ps1" }
    "Wsl2Component" { $remoteScriptFileName = "remote-wsl2-component.sh" }
}
$remoteScriptPath = Join-Path $resolvedArtifactRoot $remoteScriptFileName
$remoteScriptContent | Set-Content -LiteralPath $remoteScriptPath -Encoding UTF8

$doctorArguments = @("doctor", "--provider", $Provider, "--target", "windows")
$warmupArguments = @("warmup", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "--arch", "amd64", "--azure-os-disk", "managed", "--keep", "--idle-timeout", ("{0}m" -f $IdleTimeoutMinutes), "--ttl", ("{0}m" -f $TtlMinutes), "--timing-json")
if ($Mode -in @("CombinedInstalledSmoke", "NativeDesktopComponent")) {
    $warmupArguments += "--desktop"
}

$runArtifactDirectory = Join-Path $resolvedArtifactRoot "run"
$runArguments = @("run", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "--id", "<lease-id>", "--preflight", "--timing-json", "--capture-stdout", (Join-Path $runArtifactDirectory "remote-stdout.txt"), "--capture-stderr", (Join-Path $runArtifactDirectory "remote-stderr.txt"), "--script-stdin", "--")
if ($Mode -in @("CombinedInstalledSmoke", "NativeDesktopComponent")) {
    $runArguments = @("run", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "--desktop", "--id", "<lease-id>", "--preflight", "--timing-json", "--capture-stdout", (Join-Path $runArtifactDirectory "remote-stdout.txt"), "--capture-stderr", (Join-Path $runArtifactDirectory "remote-stderr.txt"), "--script-stdin", "--")
}
$stopArguments = @("stop", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "<lease-id>")
$listArguments = @("list", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "--json")
$resultsArguments = @("results", "<run-id>")
$validationArtifactName = "$($ValidationLane.ToLowerInvariant())-smoke"
$copyArguments = @("cp", "--provider", $Provider, "--id", "<lease-id>", "SANDBOX:<remote-artifact-archive>", (Join-Path $resolvedArtifactRoot "remote-artifacts\openclaw-$validationArtifactName-artifacts.zip"))

$manifestPath = Join-Path $resolvedArtifactRoot "crabbox-smoke-manifest.json"
$manifest = [ordered]@{
    createdAtUtc = [DateTime]::UtcNow.ToString("o")
    planOnly = [bool]$PlanOnly
    provider = $Provider
    target = "windows"
    mode = $Mode
    validationLane = $ValidationLane
    previousRelease = if ($ValidationLane -eq "Upgrade") { $PreviousRelease } else { "" }
    previousInstallerSha256 = if ($ValidationLane -eq "Upgrade") { $PreviousInstallerSha256.ToLowerInvariant() } else { "" }
    windowsMode = $windowsMode
    proofClass = $proofClass
    repoRoot = $resolvedRepoRoot
    artifactRoot = $resolvedArtifactRoot
    crabboxPath = $resolvedCrabboxPath
    requestUiProof = [bool]$RequestUiProof
    requireFullInstalledSmoke = [bool]$RequireFullInstalledSmoke
    acquisition = [ordered]@{
        architecture = "amd64"
        azureOsDisk = "managed"
        azureImage = $AzureImage
        combinedImageRequired = ($Mode -eq "CombinedInstalledSmoke")
    }
    remoteScriptPath = $remoteScriptPath
    commands = [ordered]@{
        doctor = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $doctorArguments
        warmup = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $warmupArguments
        run = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $runArguments -StdInPath $remoteScriptPath
        copyArtifacts = if ($Mode -eq "CombinedInstalledSmoke") { Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $copyArguments } else { "Not applicable for component-only proof." }
        stop = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $stopArguments
        listAfterStop = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $listArguments
        results = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $resultsArguments
    }
    execution = [ordered]@{
        leaseId = ""
        runId = ""
        doctor = $null
        warmup = $null
        run = $null
        copyArtifacts = $null
        results = $null
        stop = [ordered]@{
            attempted = $false
            succeeded = $false
            exitCode = $null
            stdoutPath = ""
            stderrPath = ""
            combinedPath = ""
            note = "Not attempted yet."
            verification = $null
        }
    }
}
Write-Manifest -ManifestObject $manifest -Path $manifestPath

if ($PlanOnly) {
    Write-Host "Plan only. No Crabbox commands were executed."
    Write-Host "Manifest: $manifestPath"
    Write-Host "Remote script: $remoteScriptPath"
    return
}

$exitCode = 1
$leaseId = ""
$runId = ""
$runFailed = $true
$stopFailure = $false
$originalAzureImage = $env:CRABBOX_AZURE_IMAGE
if ($Mode -eq "CombinedInstalledSmoke") {
    $env:CRABBOX_AZURE_IMAGE = $AzureImage
}

try {
    $doctorResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $doctorArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory (Join-Path $resolvedArtifactRoot "doctor")
    $manifest.execution.doctor = [ordered]@{
        exitCode = $doctorResult.ExitCode
        stdoutPath = $doctorResult.StdoutPath
        stderrPath = $doctorResult.StderrPath
        combinedPath = $doctorResult.CombinedPath
    }
    Write-Manifest -ManifestObject $manifest -Path $manifestPath
    if ($doctorResult.ExitCode -ne 0) {
        if (Test-LikelyAzureAuthFailure -Text $doctorResult.Combined) {
            throw (Get-AzureAuthGuidance -ResolvedCrabboxPath $resolvedCrabboxPath)
        }

        throw "Crabbox doctor failed. Inspect $($doctorResult.CombinedPath)."
    }

    $warmupResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $warmupArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory (Join-Path $resolvedArtifactRoot "warmup")
    $manifest.execution.warmup = [ordered]@{
        exitCode = $warmupResult.ExitCode
        stdoutPath = $warmupResult.StdoutPath
        stderrPath = $warmupResult.StderrPath
        combinedPath = $warmupResult.CombinedPath
    }
    $leaseId = Get-OptionalLeaseIdFromWarmupOutput -Text $warmupResult.Combined
    if (-not [string]::IsNullOrWhiteSpace($leaseId)) {
        $manifest.execution.leaseId = $leaseId
    }
    Write-Manifest -ManifestObject $manifest -Path $manifestPath
    if ($warmupResult.ExitCode -ne 0) {
        if (Test-LikelyAzureAuthFailure -Text $warmupResult.Combined) {
            throw (Get-AzureAuthGuidance -ResolvedCrabboxPath $resolvedCrabboxPath)
        }

        throw "Crabbox warmup failed. Inspect $($warmupResult.CombinedPath)."
    }

    $leaseId = Get-LeaseIdFromWarmupOutput -Text $warmupResult.Combined
    $manifest.execution.leaseId = $leaseId
    Write-Manifest -ManifestObject $manifest -Path $manifestPath

    $effectiveRunArguments = @()
    foreach ($argument in $runArguments) {
        if ($argument -eq "<lease-id>") {
            $effectiveRunArguments += $leaseId
        } else {
            $effectiveRunArguments += $argument
        }
    }

    $runResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $effectiveRunArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory $runArtifactDirectory -StdInText $remoteScriptContent
    $runId = Get-RunIdentifierFromText -Text $runResult.Combined
    $manifest.execution.run = [ordered]@{
        exitCode = $runResult.ExitCode
        stdoutPath = $runResult.StdoutPath
        stderrPath = $runResult.StderrPath
        combinedPath = $runResult.CombinedPath
        remoteStdoutPath = (Join-Path $runArtifactDirectory "remote-stdout.txt")
        remoteStderrPath = (Join-Path $runArtifactDirectory "remote-stderr.txt")
    }
    $manifest.execution.runId = $runId
    Write-Manifest -ManifestObject $manifest -Path $manifestPath
    if ($runResult.ExitCode -ne 0) {
        throw "Crabbox run failed. Inspect $($runResult.CombinedPath)."
    }

    $capturedRemoteStdoutPath = Join-Path $runArtifactDirectory "remote-stdout.txt"
    if (-not (Test-Path -LiteralPath $capturedRemoteStdoutPath -PathType Leaf)) {
        throw "Crabbox did not create the requested remote stdout capture: $capturedRemoteStdoutPath"
    }
    $capturedRemoteStdout = Get-Content -LiteralPath $capturedRemoteStdoutPath -Raw
    $capturedRemoteStdout = $capturedRemoteStdout -replace "`r`n", "`n"

    switch ($Mode) {
        "CombinedInstalledSmoke" {
            if ($capturedRemoteStdout -notmatch '(?m)^combinedImageContract=passed$') {
                throw "Combined image contract marker is missing from remote proof output."
            }
        $remoteArtifactPath = Get-RemoteArtifactPathFromOutput -Text $capturedRemoteStdout
        $localArchivePath = Join-Path $resolvedArtifactRoot "remote-artifacts\openclaw-$validationArtifactName-artifacts.zip"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $localArchivePath) | Out-Null

        $effectiveCopyArguments = @()
        foreach ($argument in $copyArguments) {
            switch ($argument) {
                "<lease-id>" { $effectiveCopyArguments += $leaseId }
                "SANDBOX:<remote-artifact-archive>" { $effectiveCopyArguments += "SANDBOX:$remoteArtifactPath" }
                default { $effectiveCopyArguments += $argument }
            }
        }

        $copyResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $effectiveCopyArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory (Join-Path $resolvedArtifactRoot "copy-artifacts")
        $manifest.execution.copyArtifacts = [ordered]@{
            exitCode = $copyResult.ExitCode
            stdoutPath = $copyResult.StdoutPath
            stderrPath = $copyResult.StderrPath
            combinedPath = $copyResult.CombinedPath
            remotePath = $remoteArtifactPath
            localPath = $localArchivePath
        }
        Write-Manifest -ManifestObject $manifest -Path $manifestPath
        if ($copyResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $localArchivePath -PathType Leaf)) {
            throw "Crabbox artifact retrieval failed. Inspect $($copyResult.CombinedPath)."
        }

        $expandedArtifactPath = Join-Path $resolvedArtifactRoot "remote-artifacts\$validationArtifactName"
        Expand-Archive -LiteralPath $localArchivePath -DestinationPath $expandedArtifactPath -Force
        Assert-ValidationArtifactContract -ValidationArtifactRoot $expandedArtifactPath -Lane $ValidationLane
        }
        "NativeDesktopComponent" {
            if ($capturedRemoteStdout -notmatch '(?m)^nativeDesktopComponent=passed$') {
                throw "Native desktop component proof marker is missing."
            }
        }
        "Wsl2Component" {
            if ($capturedRemoteStdout -notmatch '(?m)^WSL2 capability probe passed$') {
                throw "WSL2 component proof marker is missing."
            }
        }
    }

    $runFailed = $false

    if (-not [string]::IsNullOrWhiteSpace($runId)) {
        $effectiveResultsArguments = @()
        foreach ($argument in $resultsArguments) {
            if ($argument -eq "<run-id>") {
                $effectiveResultsArguments += $runId
            } else {
                $effectiveResultsArguments += $argument
            }
        }

        $resultsResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $effectiveResultsArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory (Join-Path $resolvedArtifactRoot "results")
        if ($resultsResult.ExitCode -eq 0) {
            $resultsNote = "Results collected."
        } else {
            $resultsNote = "Results were unavailable from this provider path. Direct Azure may not expose coordinator-only results."
        }

        $manifest.execution.results = [ordered]@{
            exitCode = $resultsResult.ExitCode
            stdoutPath = $resultsResult.StdoutPath
            stderrPath = $resultsResult.StderrPath
            combinedPath = $resultsResult.CombinedPath
            note = $resultsNote
        }
        Write-Manifest -ManifestObject $manifest -Path $manifestPath
    } else {
        $manifest.execution.results = [ordered]@{
            exitCode = $null
            stdoutPath = ""
            stderrPath = ""
            combinedPath = ""
            note = "Run output did not expose a result identifier."
        }
        Write-Manifest -ManifestObject $manifest -Path $manifestPath
    }

    $exitCode = 0
} catch {
    $message = $_.Exception.Message
    Set-Content -LiteralPath (Join-Path $resolvedArtifactRoot "failure.txt") -Value $message -Encoding UTF8
    throw
} finally {
    if (-not [string]::IsNullOrWhiteSpace($leaseId)) {
        $manifest.execution.stop.attempted = $true

        try {
            $effectiveStopArguments = @()
            foreach ($argument in $stopArguments) {
                if ($argument -eq "<lease-id>") {
                    $effectiveStopArguments += $leaseId
                } else {
                    $effectiveStopArguments += $argument
                }
            }

            $stopResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $effectiveStopArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory (Join-Path $resolvedArtifactRoot "stop")
            if ($stopResult.ExitCode -eq 0) {
                $stopNote = "Lease stop completed."
            } else {
                $stopNote = "Lease stop failed. Inspect the stop artifacts."
            }

            $manifest.execution.stop = [ordered]@{
                attempted = $true
                succeeded = ($stopResult.ExitCode -eq 0)
                exitCode = $stopResult.ExitCode
                stdoutPath = $stopResult.StdoutPath
                stderrPath = $stopResult.StderrPath
                combinedPath = $stopResult.CombinedPath
                note = $stopNote
            }

            if ($stopResult.ExitCode -ne 0) {
                $stopFailure = $true
            } else {
                $listResult = Invoke-CrabboxCommand -ResolvedCrabboxPath $resolvedCrabboxPath -Arguments $listArguments -WorkingDirectory $resolvedRepoRoot -ArtifactDirectory (Join-Path $resolvedArtifactRoot "list-after-stop")
                $leasePattern = [regex]::Escape($leaseId)
                $leaseStillListed = $listResult.Combined -match $leasePattern
                $manifest.execution.stop.verification = [ordered]@{
                    exitCode = $listResult.ExitCode
                    stdoutPath = $listResult.StdoutPath
                    stderrPath = $listResult.StderrPath
                    combinedPath = $listResult.CombinedPath
                    leaseAbsent = (-not $leaseStillListed)
                }
                if ($listResult.ExitCode -ne 0 -or $leaseStillListed) {
                    $stopFailure = $true
                    $manifest.execution.stop.succeeded = $false
                    $manifest.execution.stop.note = "Lease stop could not be verified by list."
                }
            }
        } catch {
            $stopFailure = $true
            $manifest.execution.stop = [ordered]@{
                attempted = $true
                succeeded = $false
                exitCode = $null
                stdoutPath = ""
                stderrPath = ""
                combinedPath = ""
                note = $_.Exception.Message
            }
        }
    }

    if ($null -eq $originalAzureImage) {
        Remove-Item -LiteralPath "Env:\CRABBOX_AZURE_IMAGE" -ErrorAction SilentlyContinue
    } else {
        [Environment]::SetEnvironmentVariable("CRABBOX_AZURE_IMAGE", $originalAzureImage, "Process")
    }
    Write-Manifest -ManifestObject $manifest -Path $manifestPath
}

if ($stopFailure) {
    throw "Crabbox validation finished but lease cleanup failed. Inspect $manifestPath."
}

if ($exitCode -eq 0 -and -not $runFailed) {
    Write-Host "Crabbox validation passed."
    Write-Host "Artifacts: $resolvedArtifactRoot"
    Write-Host "Manifest: $manifestPath"
}
