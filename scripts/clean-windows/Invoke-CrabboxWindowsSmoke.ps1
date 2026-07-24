#requires -Version 5.1
<#
.SYNOPSIS
    Plans or runs focused Crabbox Windows validation against the current checkout.

.DESCRIPTION
    Creates a new Azure Windows Crabbox lease for either NativeDesktop or Wsl2,
    runs the appropriate focused proof, captures local artifacts, and stops only
    the lease created by this invocation.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CrabboxPath,

    [ValidateSet("azure")]
    [string]$Provider = "azure",

    [Parameter(Mandatory = $true)]
    [ValidateSet("NativeDesktop", "Wsl2")]
    [string]$Mode,

    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,

    [string]$ArtifactRoot = "",

    [ValidateRange(30, 360)]
    [int]$IdleTimeoutMinutes = 90,

    [ValidateRange(30, 720)]
    [int]$TtlMinutes = 240,

    [switch]$PlanOnly,

    [switch]$RequestUiProof,

    [switch]$RequireNativeProof,

    [switch]$RequireCombinedNativeDesktopAndWsl2OnOneLease
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
        "NativeDesktop" { return "normal" }
        "Wsl2" { return "wsl2" }
        default { throw "Unsupported mode '$Mode'." }
    }
}

function Get-ProofClass {
    switch ($Mode) {
        "NativeDesktop" { return "native-desktop-installed-smoke" }
        "Wsl2" { return "wsl2-capability-probe" }
        default { throw "Unsupported mode '$Mode'." }
    }
}

function Get-ModeArtifactName {
    switch ($Mode) {
        "NativeDesktop" { return "native-desktop" }
        "Wsl2" { return "wsl2" }
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

function New-RemoteScriptContent {
    switch ($Mode) {
        "NativeDesktop" {
            return @'
$ErrorActionPreference = "Stop"
$repoRoot = (Get-Location).Path
$artifactRoot = Join-Path $repoRoot "TestResults\CrabboxNativeDesktop"
$env:OPENCLAW_REPO_ROOT = $repoRoot
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts\validate-installed-inno-smoke.ps1") -RepoRoot $repoRoot -ArtifactRoot $artifactRoot
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
$phaseStatus = Join-Path $artifactRoot "phase-status.json"
if (-not (Test-Path -LiteralPath $phaseStatus)) {
    throw "Installed smoke did not produce phase-status.json."
}
$archivePath = Join-Path $repoRoot "openclaw-installed-smoke-artifacts.zip"
Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $artifactRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $archivePath)) {
    throw "Installed smoke artifact archive was not created."
}
Write-Output "artifactArchive=$archivePath"
'@
        }
        "Wsl2" {
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

if ($RequireCombinedNativeDesktopAndWsl2OnOneLease) {
    throw "Combined NativeDesktop and Wsl2 on one lease is not supported. Crabbox leases cannot gain both capabilities after acquisition."
}

if ($Mode -eq "Wsl2" -and $RequestUiProof) {
    throw "Wsl2 mode cannot satisfy a UI proof request. Use NativeDesktop for UI proof."
}

if ($Mode -eq "Wsl2" -and $RequireNativeProof) {
    throw "Wsl2 mode cannot be labeled as native proof. Use NativeDesktop for native Windows proof."
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
if ($Mode -eq "Wsl2") {
    $remoteScriptContent = $remoteScriptContent -replace "`r`n", "`n"
}
if ($Mode -eq "NativeDesktop") {
    $remoteScriptFileName = "remote-native-desktop.ps1"
} else {
    $remoteScriptFileName = "remote-wsl2-probe.sh"
}
$remoteScriptPath = Join-Path $resolvedArtifactRoot $remoteScriptFileName
$remoteScriptContent | Set-Content -LiteralPath $remoteScriptPath -Encoding UTF8

$doctorArguments = @("doctor", "--provider", $Provider, "--target", "windows")
$warmupArguments = @("warmup", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "--keep", "--idle-timeout", ("{0}m" -f $IdleTimeoutMinutes), "--ttl", ("{0}m" -f $TtlMinutes), "--timing-json")
if ($Mode -eq "NativeDesktop") {
    $warmupArguments += "--desktop"
}

$runArtifactDirectory = Join-Path $resolvedArtifactRoot "run"
$runArguments = @("run", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "--id", "<lease-id>", "--preflight", "--timing-json", "--capture-stdout", (Join-Path $runArtifactDirectory "remote-stdout.txt"), "--capture-stderr", (Join-Path $runArtifactDirectory "remote-stderr.txt"), "--script-stdin", "--")
$stopArguments = @("stop", "--provider", $Provider, "--target", "windows", "--windows-mode", $windowsMode, "<lease-id>")
$resultsArguments = @("results", "<run-id>")
$copyArguments = @("cp", "--provider", $Provider, "--id", "<lease-id>", "SANDBOX:<remote-artifact-archive>", (Join-Path $resolvedArtifactRoot "remote-artifacts\openclaw-installed-smoke-artifacts.zip"))

$manifestPath = Join-Path $resolvedArtifactRoot "crabbox-smoke-manifest.json"
$manifest = [ordered]@{
    createdAtUtc = [DateTime]::UtcNow.ToString("o")
    planOnly = [bool]$PlanOnly
    provider = $Provider
    target = "windows"
    mode = $Mode
    windowsMode = $windowsMode
    proofClass = $proofClass
    repoRoot = $resolvedRepoRoot
    artifactRoot = $resolvedArtifactRoot
    crabboxPath = $resolvedCrabboxPath
    requestUiProof = [bool]$RequestUiProof
    requireNativeProof = [bool]$RequireNativeProof
    remoteScriptPath = $remoteScriptPath
    commands = [ordered]@{
        doctor = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $doctorArguments
        warmup = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $warmupArguments
        run = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $runArguments -StdInPath $remoteScriptPath
        copyArtifacts = if ($Mode -eq "NativeDesktop") { Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $copyArguments } else { "Not applicable for the WSL2 capability probe." }
        stop = Format-CommandPreview -FilePath $resolvedCrabboxPath -Arguments $stopArguments
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

    if ($Mode -eq "NativeDesktop") {
        $capturedRemoteStdoutPath = Join-Path $runArtifactDirectory "remote-stdout.txt"
        if (-not (Test-Path -LiteralPath $capturedRemoteStdoutPath -PathType Leaf)) {
            throw "Crabbox did not create the requested remote stdout capture: $capturedRemoteStdoutPath"
        }
        $capturedRemoteStdout = Get-Content -LiteralPath $capturedRemoteStdoutPath -Raw
        $remoteArtifactPath = Get-RemoteArtifactPathFromOutput -Text $capturedRemoteStdout
        $localArchivePath = Join-Path $resolvedArtifactRoot "remote-artifacts\openclaw-installed-smoke-artifacts.zip"
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

        $expandedArtifactPath = Join-Path $resolvedArtifactRoot "remote-artifacts\installed-smoke"
        Expand-Archive -LiteralPath $localArchivePath -DestinationPath $expandedArtifactPath -Force
        if (-not (Test-Path -LiteralPath (Join-Path $expandedArtifactPath "phase-status.json") -PathType Leaf)) {
            throw "Retrieved Crabbox artifacts are missing phase-status.json."
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
