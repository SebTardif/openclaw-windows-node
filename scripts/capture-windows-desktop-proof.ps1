<#
.SYNOPSIS
    Captures fail-closed, current-head Windows desktop proof (screenshot +
    machine-readable manifest) for one deterministic Hub route.

.DESCRIPTION
    Builds (unless -NoBuild) and runs the isolated
    WindowsDesktopProofTests.ConnectionPage_IsReachableAndScreenshotable test in
    tests\OpenClaw.Tray.UITests, which drives the real current-head tray process
    through the same deep-link IPC path used by the Accessibility suite
    (AccessibilityAppFixture). That fixture always launches its own isolated
    OPENCLAW_TRAY_DATA_DIR under %TEMP%; this script never reads or writes real
    %APPDATA%/%LOCALAPPDATA% OpenClaw state.

    Artifacts land in a timestamped, artifact-safe directory:
      - screenshot.png       the Hub window at the proof route (required)
      - proof.txt            plain-text proof lines from the test (required)
      - desktop-proof.trx    the xunit TRX result for the proof test (required)
      - manifest.json        schemaVersion 1 manifest describing every artifact

    Motion (short video/GIF) capture is optional and, today, unsupported: no
    existing test primitive records a screen video. The manifest always
    reports an explicit "unavailable" motion artifact instead of a fake
    success. Pass -IncludeMotion to make that unsupported request fail closed
    with a clear error rather than silently producing nothing.

    This script fails closed: a missing app, a missing required artifact, or a
    failing/absent proof test result produces a non-zero exit code and a
    manifest with outcome "fail" plus a "failure" block. It never reports
    success-shaped output for a run it could not verify.

    Oracle vs. witness: the deterministic UI automation assertion (the proof
    test reaching its page marker) is the sole pass/fail oracle. The
    screenshot is a witness only and never flips the oracle outcome; a
    witness-capture failure (for example a background process denied
    SetForegroundWindow even in an interactive session) is reported as a
    distinct "artifact-missing" failure phase rather than "oracle-failed", so
    a reviewer can tell an evidence-capture gap apart from a real app
    regression. Proof completion still requires every required artifact to
    exist; see failure.phase in manifest.json for environment-non-interactive,
    oracle-failed, artifact-missing, build, app-missing, and unsupported.

    Interactive-desktop guard: before building or running anything (dry runs
    excepted), this script checks for an interactive desktop session (not
    Session 0, Environment.UserInteractive true) and fails closed with
    failure.phase "environment-non-interactive" if none is available, since a
    screenshot witness cannot exist without a desktop.

.PARAMETER RepoRoot
    Repository root. Defaults to this script's parent directory.

.PARAMETER ArtifactRoot
    Directory to write proof artifacts into. Defaults to a timestamped folder
    under TestResults\DesktopProof. Must not resolve inside %APPDATA% or
    %LOCALAPPDATA%.

.PARAMETER Configuration
    Build/test configuration. Defaults to Debug.

.PARAMETER NoBuild
    Skip building tests\OpenClaw.Tray.UITests first. Use only when you already
    built the current head; a stale build breaks the "current-head" proof
    contract.

.PARAMETER IncludeMotion
    Request short video/GIF capture in addition to the screenshot. Currently
    unsupported: this fails closed immediately with a clear reason instead of
    producing an empty or fake motion artifact.

.PARAMETER DryRun
    Validate arguments and paths, print the plan, and write a manifest with
    mode "dry-run" and outcome "not_run". Never builds or runs the proof test.
    Always exits non-zero so a dry run can never be mistaken for real proof.

.EXAMPLE
    .\scripts\capture-windows-desktop-proof.ps1

.EXAMPLE
    .\scripts\capture-windows-desktop-proof.ps1 -NoBuild -ArtifactRoot TestResults\DesktopProof\manual

.EXAMPLE
    .\scripts\capture-windows-desktop-proof.ps1 -DryRun
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),

    [string]$ArtifactRoot = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$NoBuild,

    [switch]$IncludeMotion,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProofTestProject = "tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj"
$ProofTestFilter = "FullyQualifiedName~WindowsDesktopProofTests.ConnectionPage_IsReachableAndScreenshotable"
$ProofTestName = "ConnectionPage_IsReachableAndScreenshotable"
$ProofPageTag = "connection"
$ProofPageMarker = "ConnectionPageMarker"

function Test-PathIsUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root
    )

    if ([string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd('\') + '\'
    return $normalizedCandidate.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Test-InteractiveDesktopAvailable {
    # Screenshot capture requires SetForegroundWindow, which requires an
    # interactive desktop/window station. Session 0 (services) and other
    # non-interactive window stations can never satisfy this; detect that
    # case up front so a service-context run fails fast with a clear reason
    # instead of a confusing UI automation timeout deep inside the test.
    # This is a guard against a hard blocker (no desktop at all), not a
    # guarantee that screenshot capture will succeed on an interactive
    # session; a transient foreground-focus failure on an otherwise
    # interactive session is handled separately as a witness-only failure
    # (see the oracle/artifact distinction below).
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $userInteractive = [Environment]::UserInteractive
    return [ordered]@{
        available       = ($sessionId -ne 0 -and $userInteractive)
        sessionId       = $sessionId
        userInteractive = $userInteractive
    }
}

function New-Manifest {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$Outcome,
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [string]$RunId,
        [string]$RepoName,
        [string]$Commit,
        [string]$Branch,
        [bool]$WorkingTreeDirty,
        [bool]$BuildSkipped,
        [string]$RuntimeIdentifier,
        [System.Collections.IEnumerable]$Artifacts,
        [object]$Failure,
        [object]$Environment
    )

    return [ordered]@{
        schemaVersion   = 1
        generatedAtUtc  = (Get-Date).ToUniversalTime().ToString("o")
        mode            = $Mode
        runId           = $RunId
        repo            = [ordered]@{
            name             = $RepoName
            commit           = $Commit
            branch           = $Branch
            workingTreeDirty = $WorkingTreeDirty
        }
        build           = [ordered]@{
            configuration     = $Configuration
            runtimeIdentifier = $RuntimeIdentifier
            skipped           = $BuildSkipped
        }
        route           = [ordered]@{
            pageTag    = $ProofPageTag
            pageMarker = $ProofPageMarker
        }
        isolation       = [ordered]@{
            usedRealAppData = $false
            note            = "AccessibilityAppFixture creates its own OPENCLAW_TRAY_DATA_DIR under %TEMP%\OpenClaw.Tray.Axe.<guid> per run; this script never reads or writes %APPDATA% or %LOCALAPPDATA% OpenClaw state."
        }
        environment     = $Environment
        outcome         = $Outcome
        exitCode        = $ExitCode
        failure         = $Failure
        artifacts       = $Artifacts
    }
}

function Write-ManifestAndExit {
    param(
        [Parameter(Mandatory = $true)][hashtable]$ManifestArgs,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    $manifest = New-Manifest @ManifestArgs
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
    Write-Host "Manifest: $ManifestPath"
    if ($manifest.outcome -eq "pass") {
        Write-Host "Windows desktop proof passed." -ForegroundColor Green
    } else {
        Write-Warning "Windows desktop proof did not pass (outcome: $($manifest.outcome)). See $ManifestPath."
    }
    exit $manifest.exitCode
}

# --- Resolve and validate the repo root --------------------------------------
if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "Repository root does not exist: $RepoRoot"
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot "openclaw-windows-node.slnx"))) {
    throw "Repository root does not look like the OpenClaw Windows node repo (missing openclaw-windows-node.slnx): $RepoRoot"
}

# --- Resolve and sanitize the artifact root -----------------------------------
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $ArtifactRoot = Join-Path $RepoRoot "TestResults\DesktopProof\$timestamp"
} elseif (-not [IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $RepoRoot $ArtifactRoot
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)

# Guard the real OpenClaw product data folders specifically (not all of
# %APPDATA%/%LOCALAPPDATA%, which also legitimately hosts %TEMP% and other
# unrelated app state). These are the exact folder names the tray app and its
# dev identity use for settings, gateway records, and device keys.
$guardedProductFolders = @()
if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
    $guardedProductFolders += Join-Path $env:APPDATA "OpenClawTray"
    $guardedProductFolders += Join-Path $env:APPDATA "OpenClawTray-Dev"
}
if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $guardedProductFolders += Join-Path $env:LOCALAPPDATA "OpenClawTray"
    $guardedProductFolders += Join-Path $env:LOCALAPPDATA "OpenClawTray-Dev"
}
foreach ($guardedRoot in $guardedProductFolders) {
    if (Test-PathIsUnderRoot -Candidate $ArtifactRoot -Root $guardedRoot) {
        throw "Refusing to write proof artifacts under real app data: $ArtifactRoot is inside $guardedRoot."
    }
    if (Test-PathIsUnderRoot -Candidate $guardedRoot -Root $ArtifactRoot) {
        throw "Refusing to write proof artifacts at ${ArtifactRoot}: it would contain the real app data folder $guardedRoot."
    }
}


$RunId = [Guid]::NewGuid().ToString("N")
$ManifestPath = Join-Path $ArtifactRoot "manifest.json"
$ScreenshotPath = Join-Path $ArtifactRoot "screenshot.png"
$ProofTextPath = Join-Path $ArtifactRoot "proof.txt"
$TrxFileName = "desktop-proof.trx"
$TrxPath = Join-Path $ArtifactRoot $TrxFileName

$architecture = $env:PROCESSOR_ARCHITECTURE
$runtimeIdentifier = switch ($architecture) {
    "ARM64" { "win-arm64" }
    default { "win-x64" }
}

function Invoke-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& git -C $RepoRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed while collecting proof provenance: git $($Arguments -join ' ')"
    }
    return ($output -join "`n").Trim()
}

$repoName = "openclaw-windows-node"
$commit = Invoke-GitText -Arguments @("rev-parse", "HEAD")
$branch = Invoke-GitText -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
$workingTreeStatus = Invoke-GitText -Arguments @("status", "--porcelain=v1", "--untracked-files=all")
$workingTreeDirty = -not [string]::IsNullOrWhiteSpace($workingTreeStatus)
$shortCommit = $commit.Substring(0, [Math]::Min(12, $commit.Length))

Write-Host "OpenClaw Windows desktop proof" -ForegroundColor Cyan
Write-Host "  Repo root:     $RepoRoot"
Write-Host "  Commit:        $shortCommit ($branch)"
Write-Host "  Configuration: $Configuration"
Write-Host "  Runtime:       $runtimeIdentifier"
Write-Host "  Artifacts:     $ArtifactRoot"
Write-Host "  Route:         hub/$ProofPageTag (marker: $ProofPageMarker)"

New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null

$environmentInfo = Test-InteractiveDesktopAvailable
Write-Host "  Interactive:   $($environmentInfo.available) (sessionId=$($environmentInfo.sessionId), userInteractive=$($environmentInfo.userInteractive))"

# Interactive-desktop guard: fail closed immediately in Session 0 or any
# other non-interactive window station context, before building or running
# anything. Without a desktop, screenshot capture (and likely the app itself)
# cannot work; -DryRun still reports this in its manifest but keeps its own
# "not_run"/exit-2 semantics regardless, since it never builds or runs.
if (-not $DryRun -and -not $environmentInfo.available) {
    $failure = [ordered]@{
        phase   = "environment-non-interactive"
        message = "This host has no interactive desktop session (sessionId=$($environmentInfo.sessionId), userInteractive=$($environmentInfo.userInteractive)), which is required to render and capture the tray Hub window. Run this script on a session with an interactive desktop (a real console/RDP session, or a hosted CI runner such as windows-latest), not a Session 0 service context."
    }
    $artifacts = @(
        [ordered]@{ type = "motion"; path = $null; bytes = 0; status = "unavailable"; publish = $false; reason = "Skipped: no interactive desktop session." }
    )
    Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
        Mode              = "run"
        Outcome           = "fail"
        ExitCode          = 1
        RunId             = $RunId
        RepoName          = $repoName
        Commit            = $commit
        Branch            = $branch
        WorkingTreeDirty  = $workingTreeDirty
        BuildSkipped      = [bool]$NoBuild
        RuntimeIdentifier = $runtimeIdentifier
        Artifacts         = $artifacts
        Failure           = $failure
        Environment       = $environmentInfo
    }
}

# -IncludeMotion is a static capability check: no code path in this repo
# records a screen video/GIF today. Fail closed immediately instead of
# pretending to honor the request.
if ($IncludeMotion) {
    $failure = [ordered]@{
        phase   = "unsupported"
        message = "Motion capture was requested with -IncludeMotion, but no native screen/video recording primitive exists yet (AccessibilityAppFixture only exposes a still screenshot hook). Rerun without -IncludeMotion, or add a real capture primitive before enabling this flag."
    }
    $artifacts = @(
        [ordered]@{ type = "motion"; path = $null; bytes = 0; status = "unavailable"; publish = $false; reason = $failure.message }
    )
    Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
        Mode              = $(if ($DryRun) { "dry-run" } else { "run" })
        Outcome           = "fail"
        ExitCode          = 1
        RunId             = $RunId
        RepoName          = $repoName
        Commit            = $commit
        Branch            = $branch
        WorkingTreeDirty  = $workingTreeDirty
        BuildSkipped      = [bool]$NoBuild
        RuntimeIdentifier = $runtimeIdentifier
        Artifacts         = $artifacts
        Failure           = $failure
        Environment       = $environmentInfo
    }
}

$motionArtifact = [ordered]@{
    type   = "motion"
    path   = $null
    bytes  = 0
    status = "unavailable"
    publish = $false
    reason = "No native screen/video recording primitive exists in AccessibilityAppFixture today; motion capture is optional and was not requested."
}

if ($DryRun) {
    $artifacts = @(
        [ordered]@{ type = "screenshot"; path = [IO.Path]::GetFileName($ScreenshotPath); bytes = 0; status = "not_run"; publish = $true }
        [ordered]@{ type = "proof-text"; path = [IO.Path]::GetFileName($ProofTextPath); bytes = 0; status = "not_run"; publish = $true }
        [ordered]@{ type = "trx"; path = [IO.Path]::GetFileName($TrxPath); bytes = 0; status = "not_run"; publish = $false }
        $motionArtifact
    )
    Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
        Mode              = "dry-run"
        Outcome           = "not_run"
        ExitCode          = 2
        RunId             = $RunId
        RepoName          = $repoName
        Commit            = $commit
        Branch            = $branch
        WorkingTreeDirty  = $workingTreeDirty
        BuildSkipped      = [bool]$NoBuild
        RuntimeIdentifier = $runtimeIdentifier
        Artifacts         = $artifacts
        Failure           = $null
        Environment       = $environmentInfo
    }
}

$projectPath = Join-Path $RepoRoot $ProofTestProject
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Proof test project not found: $projectPath"
}

Push-Location $RepoRoot
try {
    if (-not $NoBuild) {
        Write-Host "Building current-head proof test project..." -ForegroundColor Cyan
        & dotnet build $ProofTestProject -c $Configuration -r $runtimeIdentifier
        if ($LASTEXITCODE -ne 0) {
            $failure = [ordered]@{ phase = "build"; message = "dotnet build failed with exit code $LASTEXITCODE for $ProofTestProject." }
            Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
                Mode = "run"; Outcome = "fail"; ExitCode = 1; RunId = $RunId
                RepoName = $repoName; Commit = $commit; Branch = $branch
                WorkingTreeDirty = $workingTreeDirty
                BuildSkipped = $false; RuntimeIdentifier = $runtimeIdentifier
                Artifacts = @($motionArtifact); Failure = $failure
                Environment = $environmentInfo
            }
        }
    }

    $exePath = Join-Path $RepoRoot "src\OpenClaw.Tray.WinUI\bin\$Configuration\net10.0-windows10.0.22621.0\$runtimeIdentifier\OpenClaw.Tray.WinUI.exe"
    if (-not (Test-Path -LiteralPath $exePath)) {
        $failure = [ordered]@{ phase = "app-missing"; message = "OpenClaw.Tray.WinUI.exe was not found at its expected project build output. Build the tray app (omit -NoBuild) before capturing proof." }
        Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
            Mode = "run"; Outcome = "fail"; ExitCode = 1; RunId = $RunId
            RepoName = $repoName; Commit = $commit; Branch = $branch
            WorkingTreeDirty = $workingTreeDirty
            BuildSkipped = [bool]$NoBuild; RuntimeIdentifier = $runtimeIdentifier
            Artifacts = @($motionArtifact); Failure = $failure
            Environment = $environmentInfo
        }
    }

    $previousScreenshotPath = $env:OPENCLAW_UI_SCREENSHOT_PATH
    $previousProofPath = $env:OPENCLAW_UI_PROOF_PATH
    $previousProofHead = $env:OPENCLAW_UI_PROOF_HEAD
    Remove-Item -LiteralPath $ScreenshotPath, $ProofTextPath, $TrxPath -Force -ErrorAction SilentlyContinue

    try {
        $env:OPENCLAW_UI_SCREENSHOT_PATH = $ScreenshotPath
        $env:OPENCLAW_UI_PROOF_PATH = $ProofTextPath
        $env:OPENCLAW_UI_PROOF_HEAD = $shortCommit

        Write-Host "Running deterministic desktop proof test..." -ForegroundColor Cyan
        & dotnet test $ProofTestProject `
            --no-build `
            -c $Configuration `
            -r $runtimeIdentifier `
            --filter $ProofTestFilter `
            --results-directory $ArtifactRoot `
            --logger "trx;LogFileName=$TrxFileName" `
            --logger "console;verbosity=detailed"
        $testExitCode = $LASTEXITCODE
    } finally {
        $env:OPENCLAW_UI_SCREENSHOT_PATH = $previousScreenshotPath
        $env:OPENCLAW_UI_PROOF_PATH = $previousProofPath
        $env:OPENCLAW_UI_PROOF_HEAD = $previousProofHead
    }

    # Oracle: the deterministic UI automation assertion (dotnet test exit
    # code plus the TRX outcome for the proof test) is the sole signal for
    # whether the app itself behaved correctly. Screenshot/proof-text
    # artifacts below are witnesses only; a missing or empty witness is
    # reported as a distinct "artifact-missing" failure phase, never
    # relabeled as an app regression, and never allowed to mask a genuine
    # oracle failure.
    $oracleFailures = New-Object System.Collections.Generic.List[string]
    $artifactFailures = New-Object System.Collections.Generic.List[string]

    if ($testExitCode -ne 0) {
        $oracleFailures.Add("dotnet test exited with code $testExitCode.")
    }

    $trxOutcome = $null
    if (-not (Test-Path -LiteralPath $TrxPath)) {
        $oracleFailures.Add("Required artifact missing: $TrxFileName was not produced.")
    } else {
        try {
            [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
            $result = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like "*$ProofTestName*" }) | Select-Object -First 1
            if ($null -eq $result) {
                $oracleFailures.Add("The proof test '$ProofTestName' did not appear in the TRX results.")
            } else {
                $trxOutcome = [string]$result.outcome
                if ($trxOutcome -ne "Passed") {
                    $oracleFailures.Add("The proof test '$ProofTestName' did not pass. Outcome: '$trxOutcome'.")
                }
            }
        } catch {
            $oracleFailures.Add("Failed to parse $TrxFileName as TRX results.")
        }
    }
    $oraclePassed = ($oracleFailures.Count -eq 0)

    $screenshotStatus = "missing"
    $screenshotBytes = 0
    if (Test-Path -LiteralPath $ScreenshotPath) {
        $screenshotBytes = (Get-Item -LiteralPath $ScreenshotPath).Length
        if ($screenshotBytes -gt 0) {
            $screenshotStatus = "captured"
        } else {
            $artifactFailures.Add("Required witness artifact empty: screenshot.png has zero bytes.")
        }
    } else {
        $artifactFailures.Add("Required witness artifact missing: screenshot.png was not produced.")
    }

    $proofTextStatus = "missing"
    $proofTextBytes = 0
    if (Test-Path -LiteralPath $ProofTextPath) {
        $proofTextBytes = (Get-Item -LiteralPath $ProofTextPath).Length
        if ($proofTextBytes -gt 0) {
            $proofTextStatus = "captured"
        } else {
            $artifactFailures.Add("Required artifact empty: proof.txt has zero bytes.")
        }
    } else {
        $artifactFailures.Add("Required artifact missing: proof.txt was not produced.")
    }

    $trxStatus = if (Test-Path -LiteralPath $TrxPath) { "captured" } else { "missing" }
    $trxBytes = if (Test-Path -LiteralPath $TrxPath) { (Get-Item -LiteralPath $TrxPath).Length } else { 0 }

    $artifacts = @(
        [ordered]@{ type = "screenshot"; path = [IO.Path]::GetFileName($ScreenshotPath); bytes = $screenshotBytes; status = $screenshotStatus; publish = $true }
        [ordered]@{ type = "proof-text"; path = [IO.Path]::GetFileName($ProofTextPath); bytes = $proofTextBytes; status = $proofTextStatus; publish = $true }
        [ordered]@{ type = "trx"; path = [IO.Path]::GetFileName($TrxPath); bytes = $trxBytes; status = $trxStatus; testOutcome = $trxOutcome; publish = $false }
        $motionArtifact
    )

    if (-not $oraclePassed) {
        # Oracle failed: this is a genuine app-regression signal and takes
        # priority over any artifact-capture concerns.
        $failure = [ordered]@{
            phase   = "oracle-failed"
            message = ("The deterministic UI automation oracle failed. " + ($oracleFailures -join " "))
        }
        Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
            Mode = "run"; Outcome = "fail"; ExitCode = 1; RunId = $RunId
            RepoName = $repoName; Commit = $commit; Branch = $branch
            WorkingTreeDirty = $workingTreeDirty
            BuildSkipped = [bool]$NoBuild; RuntimeIdentifier = $runtimeIdentifier
            Artifacts = $artifacts; Failure = $failure
            Environment = $environmentInfo
        }
    }

    if ($artifactFailures.Count -gt 0) {
        # Oracle passed, but a required artifact (most commonly the
        # screenshot witness) could not be captured. Proof completion still
        # requires artifact existence, so this is a hard failure, but the
        # message makes clear the app itself behaved correctly; this is an
        # evidence-capture gap (for example a foreground-focus restriction
        # on the current session), not an app defect.
        $failure = [ordered]@{
            phase   = "artifact-missing"
            message = ("The deterministic UI automation oracle passed (outcome: '$trxOutcome'), but a required proof artifact could not be captured. " + ($artifactFailures -join " "))
        }
        Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
            Mode = "run"; Outcome = "fail"; ExitCode = 1; RunId = $RunId
            RepoName = $repoName; Commit = $commit; Branch = $branch
            WorkingTreeDirty = $workingTreeDirty
            BuildSkipped = [bool]$NoBuild; RuntimeIdentifier = $runtimeIdentifier
            Artifacts = $artifacts; Failure = $failure
            Environment = $environmentInfo
        }
    }

    Write-ManifestAndExit -ManifestPath $ManifestPath -ManifestArgs @{
        Mode = "run"; Outcome = "pass"; ExitCode = 0; RunId = $RunId
        RepoName = $repoName; Commit = $commit; Branch = $branch
        WorkingTreeDirty = $workingTreeDirty
        BuildSkipped = [bool]$NoBuild; RuntimeIdentifier = $runtimeIdentifier
        Artifacts = $artifacts; Failure = $null
        Environment = $environmentInfo
    }
} finally {
    Pop-Location
}
