<#
.SYNOPSIS
    Runs the formal local live-model / real-Discord-channel parity validation path.

.DESCRIPTION
    Builds the required Windows projects, runs the secretless live-parity
    contract tests, then enables the base E2E gate plus the gate(s) selected
    by -Lane and runs the real, secret-gated live model and/or real Discord
    channel proof(s) against the published WSL gateway and native tray MCP.

    This script is intentionally stricter than the regular GitHub-hosted E2E
    shard. Unlike scripts/validate-mxc-e2e.ps1, there is no -AllowSkip escape
    hatch here: a lane you explicitly asked for must actually run and pass,
    or this script fails. A requested live lane that is reported skipped or
    missing in the TRX is always treated as a failure.

    Profile paths and credential values are never printed by this script.
    Only environment variable *names* and coarse pass/fail/skip information
    are written to the console or log files.

.PARAMETER Lane
    Which live-parity lane(s) to validate: LiveModel, RealChannel, or All.
    LiveModel configures a real, user-selected LLM provider and spends real
    API budget. RealChannel additionally exercises two real Discord bot
    accounts. There is no default: you must say which lane(s) you intend to
    spend budget on.

.PARAMETER NoBuild
    Skip build steps and run against existing outputs.

.EXAMPLE
    .\scripts\validate-live-parity-e2e.ps1 -Lane LiveModel

.EXAMPLE
    .\scripts\validate-live-parity-e2e.ps1 -Lane RealChannel

.EXAMPLE
    .\scripts\validate-live-parity-e2e.ps1 -Lane All
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("LiveModel", "RealChannel", "All")]
    [string]$Lane,

    [switch]$NoBuild,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier,

    [string]$ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $RuntimeIdentifier = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        ([System.Runtime.InteropServices.Architecture]::Arm64) { "win-arm64"; break }
        default { "win-x64" }
    }
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repoRoot "TestResults\LiveParityE2E"
}

New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Command
    )

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Read-Trx {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Expected TRX file was not created: $Path"
    }

    [xml](Get-Content -LiteralPath $Path -Raw)
}

function Get-TrxUnitTestResults {
    param([Parameter(Mandatory = $true)][xml]$Trx)

    @($Trx.SelectNodes("//*[local-name()='UnitTestResult']"))
}

function Get-TrxResultText {
    param([Parameter(Mandatory = $true)][System.Xml.XmlElement]$Result)

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($node in @($Result.SelectNodes(".//*[local-name()='Message' or local-name()='StdOut' or local-name()='StdErr']"))) {
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            $parts.Add($node.InnerText)
        }
    }

    [string]::Join("`n", $parts)
}

function Assert-LiveParityProofsPassed {
    param(
        [Parameter(Mandatory = $true)][xml]$Trx,
        [Parameter(Mandatory = $true)][string[]]$ExpectedProofs
    )

    $results = Get-TrxUnitTestResults -Trx $Trx
    $errors = New-Object System.Collections.Generic.List[string]

    foreach ($proof in $ExpectedProofs) {
        $result = @($results | Where-Object { $_.GetAttribute("testName") -like "*$proof*" }) | Select-Object -First 1
        if ($null -eq $result) {
            $errors.Add("Live parity proof was not reported in TRX: $proof")
            continue
        }

        $outcome = $result.GetAttribute("outcome")
        if ($outcome -eq "Passed") {
            Write-Host "Live parity proof passed: $proof" -ForegroundColor Green
            continue
        }

        if ($outcome -eq "NotExecuted" -or $outcome -eq "Skipped") {
            $text = Get-TrxResultText -Result $result
            $skipMessage = if ([string]::IsNullOrWhiteSpace($text)) { "no skip reason in TRX" } else { $text.Trim() }
            # A requested live lane must actually run and pass. There is no
            # -AllowSkip escape hatch: if you asked for LiveModel/RealChannel,
            # a skip here is a failure of this validation run.
            $errors.Add("Live parity proof skipped: $proof ($skipMessage)")
            continue
        }

        $errors.Add("Live parity proof '$proof' had unexpected outcome '$outcome'.")
    }

    if ($errors.Count -gt 0) {
        throw [string]::Join("`n", $errors)
    }
}

function Set-ProcessEnv {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Value
    )

    [Environment]::SetEnvironmentVariable($Name, $Value, "Process")
}

function Test-RequiredEnvVarPresent {
    param([Parameter(Mandatory = $true)][string]$Name)

    # Existence check only: this never reads the value into a variable that
    # could be logged, and never echoes it.
    $isSet = -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($Name, "Process"))
    if (-not $isSet) {
        throw "$Name is not set. See docs\LIVE_PARITY_TESTING.md for the exact profile schema and " +
            "required environment variables before running this lane."
    }
}

function Get-ProfileEnvVarReferenceNames {
    # Reads only environment-variable *name* fields (never secret values or
    # any other profile content) out of a profile JSON file, so preflight can
    # give an actionable "credential env var X is not set" message before
    # spending time on a build. This intentionally duplicates none of the
    # strict schema validation performed in-process by LiveParityProfileLoader,
    # which remains the sole source of truth for profile correctness; this is
    # a best-effort convenience check only.
    param([Parameter(Mandatory = $true)][string]$ProfilePath)

    $names = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ProfilePath)) {
        return $names
    }

    try {
        $json = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json
    } catch {
        return $names
    }
    if ($null -eq $json) {
        return $names
    }

    if ($json.PSObject.Properties.Match("apiKeyEnvVar").Count -gt 0 -and $json.apiKeyEnvVar) {
        $names.Add([string]$json.apiKeyEnvVar)
    }
    foreach ($identity in @("driver", "sut")) {
        if ($json.PSObject.Properties.Match($identity).Count -gt 0 -and $json.$identity -and
            $json.$identity.PSObject.Properties.Match("tokenEnvVar").Count -gt 0 -and $json.$identity.tokenEnvVar) {
            $names.Add([string]$json.$identity.tokenEnvVar)
        }
    }

    $names
}

function Invoke-Preflight {
    param([Parameter(Mandatory = $true)][string[]]$RequiredProfilePathVars)

    foreach ($pathVar in $RequiredProfilePathVars) {
        Test-RequiredEnvVarPresent -Name $pathVar
        $profilePath = [Environment]::GetEnvironmentVariable($pathVar, "Process")
        if (-not (Test-Path -LiteralPath $profilePath)) {
            throw "$pathVar points to a file that does not exist. See docs\LIVE_PARITY_TESTING.md."
        }

        foreach ($credentialVar in (Get-ProfileEnvVarReferenceNames -ProfilePath $profilePath)) {
            Test-RequiredEnvVarPresent -Name $credentialVar
        }
    }
}

$baseTrackedEnvVars = @(
    "OPENCLAW_REPO_ROOT",
    "OPENCLAW_RUN_E2E",
    "OPENCLAW_RUN_LIVE_MODEL_E2E",
    "OPENCLAW_RUN_REAL_CHANNEL_E2E"
)
$previousEnv = @{}
foreach ($name in $baseTrackedEnvVars) {
    $previousEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

try {
    Set-ProcessEnv -Name "OPENCLAW_REPO_ROOT" -Value $repoRoot
    Set-ProcessEnv -Name "OPENCLAW_RUN_E2E" -Value "1"

    $runLiveModel = ($Lane -eq "LiveModel") -or ($Lane -eq "All")
    $runRealChannel = ($Lane -eq "RealChannel") -or ($Lane -eq "All")

    Set-ProcessEnv -Name "OPENCLAW_RUN_LIVE_MODEL_E2E" -Value $(if ($runLiveModel) { "1" } else { $null })
    Set-ProcessEnv -Name "OPENCLAW_RUN_REAL_CHANNEL_E2E" -Value $(if ($runRealChannel) { "1" } else { $null })

    Write-Host "OpenClaw live parity validation"
    Write-Host "  Repo: $repoRoot"
    Write-Host "  Lane: $Lane"
    Write-Host "  Configuration: $Configuration"
    Write-Host "  RuntimeIdentifier: $RuntimeIdentifier"
    Write-Host "  Results: $ResultsDirectory"
    Write-Host "This lane spends real provider/Discord API budget. Profile paths and secret values are never printed by this script." -ForegroundColor Yellow

    $requiredProfileVars = New-Object System.Collections.Generic.List[string]
    if ($runLiveModel) { $requiredProfileVars.Add("OPENCLAW_LIVE_MODEL_PROFILE") }
    if ($runRealChannel) {
        $requiredProfileVars.Add("OPENCLAW_LIVE_MODEL_PROFILE")
        $requiredProfileVars.Add("OPENCLAW_REAL_CHANNEL_PROFILE")
    }
    Invoke-Preflight -RequiredProfilePathVars @($requiredProfileVars | Select-Object -Unique)

    if (-not $NoBuild) {
        Invoke-Checked -Name "Build repository" -Command {
            $powerShellExe = (Get-Process -Id $PID).Path
            & $powerShellExe -NoProfile -File (Join-Path $repoRoot "build.ps1") -Configuration $Configuration
        }

        Invoke-Checked -Name "Build tray app for $RuntimeIdentifier" -Command {
            & dotnet build ".\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj" -c $Configuration -r $RuntimeIdentifier
        }

        Invoke-Checked -Name "Build E2E tests for $RuntimeIdentifier" -Command {
            & dotnet build ".\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj" -c $Configuration -r $RuntimeIdentifier
        }
    }

    # Secretless contract tests run first and unconditionally, regardless of
    # -Lane: they never touch WSL/tray/gateway state and prove the gating,
    # profile validation, redaction, and polling contracts before spending
    # any real API/Discord budget on the live proof(s) below.
    $contractTrx = Join-Path $ResultsDirectory "OpenClaw.E2ETests.LiveParityContract.trx"
    $contractConsoleLog = Join-Path $ResultsDirectory "OpenClaw.E2ETests.LiveParityContract.console.log"
    Invoke-Checked -Name "Run secretless live-parity contract tests" -Command {
        & dotnet test ".\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj" `
            --no-build `
            --no-restore `
            -c $Configuration `
            -r $RuntimeIdentifier `
            --verbosity normal `
            --results-directory $ResultsDirectory `
            --logger "trx;LogFileName=OpenClaw.E2ETests.LiveParityContract.trx" `
            --logger "console;verbosity=normal" `
            --filter "FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityGateContractTests|FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParityProfileContractTests|FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveParitySupportContractTests" `
            2>&1 | Tee-Object -FilePath $contractConsoleLog
    }
    $contractResults = Get-TrxUnitTestResults -Trx (Read-Trx -Path $contractTrx)
    $failedContract = @($contractResults | Where-Object { $_.GetAttribute("outcome") -ne "Passed" })
    if ($failedContract.Count -gt 0) {
        throw "$($failedContract.Count) secretless live-parity contract test(s) did not pass. See $contractConsoleLog."
    }
    Write-Host "Secretless live-parity contract tests passed ($($contractResults.Count) tests)." -ForegroundColor Green

    $expectedProofs = New-Object System.Collections.Generic.List[string]
    $filterClauses = New-Object System.Collections.Generic.List[string]
    if ($runLiveModel) {
        $expectedProofs.Add("RealLiveModel_ConfiguredProvider_ChatTurn_Roundtrip")
        $filterClauses.Add("FullyQualifiedName~OpenClaw.E2ETests.LiveParity.LiveModelE2ETests")
    }
    if ($runRealChannel) {
        $expectedProofs.Add("RealDiscordChannel_MentionAgent_OutboundReply_Roundtrip")
        $filterClauses.Add("FullyQualifiedName~OpenClaw.E2ETests.LiveParity.RealChannelE2ETests")
    }
    $proofFilter = [string]::Join("|", $filterClauses)

    $proofTrx = Join-Path $ResultsDirectory "OpenClaw.E2ETests.LiveParityProof.trx"
    $proofConsoleLog = Join-Path $ResultsDirectory "OpenClaw.E2ETests.LiveParityProof.console.log"
    Invoke-Checked -Name "Run live parity proof(s) for lane $Lane" -Command {
        & dotnet test ".\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj" `
            --no-build `
            --no-restore `
            -c $Configuration `
            -r $RuntimeIdentifier `
            --verbosity normal `
            --results-directory $ResultsDirectory `
            --logger "trx;LogFileName=OpenClaw.E2ETests.LiveParityProof.trx" `
            --logger "console;verbosity=detailed" `
            --filter $proofFilter `
            2>&1 | Tee-Object -FilePath $proofConsoleLog
    }
    Assert-LiveParityProofsPassed -Trx (Read-Trx -Path $proofTrx) -ExpectedProofs @($expectedProofs)

    Write-Host ""
    Write-Host "Live parity validation completed successfully for lane: $Lane." -ForegroundColor Green
} finally {
    foreach ($name in $baseTrackedEnvVars) {
        [Environment]::SetEnvironmentVariable($name, $previousEnv[$name], "Process")
    }
}
