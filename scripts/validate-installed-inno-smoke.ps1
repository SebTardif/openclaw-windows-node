<#
.SYNOPSIS
    Builds, installs, proves, and removes the DEV Inno payload on Windows.

.DESCRIPTION
    Creates an isolated timestamped artifact directory, runs the published-gateway
    proof against the installed tray executable, and removes only state owned by
    this DEV smoke run.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "Repository root does not exist: $RepoRoot"
}
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $ArtifactRoot = Join-Path $RepoRoot "TestResults\InstalledSmoke\$timestamp"
} elseif (-not [IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $RepoRoot $ArtifactRoot
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$LogPath = Join-Path $ArtifactRoot "installed-smoke.log"
$DonePath = Join-Path $ArtifactRoot "installed-smoke.done"
$PidPath = Join-Path $ArtifactRoot "installed-smoke.pid"

$exitCode = 1
$runId = [Guid]::NewGuid().ToString("N")
$installRoot = Join-Path $env:LOCALAPPDATA "OpenClawInstalledSmoke\$runId\app"
$installerPath = Join-Path $RepoRoot "Output\OpenClawCompanion-Dev-Setup-x64.exe"
$installedTray = Join-Path $installRoot "OpenClaw.Tray.WinUI.exe"
$uninstaller = Join-Path $installRoot "unins000.exe"
$devUninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-DEV}_is1"
$devRoamingData = Join-Path $env:APPDATA "OpenClawTray-Dev"
$devLocalData = Join-Path $env:LOCALAPPDATA "OpenClawTray-Dev"
$phaseResults = [ordered]@{}
$requiredPhases = @("preflight", "build", "install", "installed-payload", "roundtrip", "cleanup")
$ownsDevInstall = $false
$originalEnvironment = @{
    OPENCLAW_RUN_E2E = $env:OPENCLAW_RUN_E2E
    OPENCLAW_REPO_ROOT = $env:OPENCLAW_REPO_ROOT
    OPENCLAW_E2E_TRAY_EXE = $env:OPENCLAW_E2E_TRAY_EXE
    OPENCLAW_E2E_ARTIFACT_ROOT = $env:OPENCLAW_E2E_ARTIFACT_ROOT
}

function Write-MainLog {
    param([string]$Message)
    $line = "[$(Get-Date -Format o)] $Message"
    $line | Add-Content -LiteralPath $LogPath -Encoding UTF8
    Write-Host $line
}

function Invoke-Phase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
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
    param(
        [Parameter(Mandatory = $true)]
        [string]$Operation
    )
    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Stop-InstalledTrayProcesses {
    if (-not (Test-Path -LiteralPath $installRoot)) {
        return
    }

    $expectedPath = [IO.Path]::GetFullPath($installedTray)
    $trayProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
        [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $expectedPath, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($process in $trayProcesses) {
        Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
    }
}

function Invoke-Cleanup {
    if (-not $ownsDevInstall) {
        return
    }

    Stop-InstalledTrayProcesses

    if (Test-Path -LiteralPath $uninstaller) {
        $uninstallLog = Join-Path $ArtifactRoot "inno-uninstall.log"
        $process = Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/LOG=`"$uninstallLog`""
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "DEV Inno uninstaller failed with exit code $($process.ExitCode)."
        }
    }

    $registeredDevDistro = @(& wsl.exe --list --quiet 2>$null) |
        ForEach-Object { ($_ -replace '\x00', '').Trim() } |
        Where-Object { $_ -eq "OpenClawGateway-Dev" }
    if ($registeredDevDistro) {
        throw "DEV WSL distro still exists after cleanup: OpenClawGateway-Dev."
    }
    if (Test-Path -LiteralPath $devUninstallKey) {
        throw "DEV uninstall registration still exists after cleanup."
    }
    if (Test-Path -LiteralPath $installedTray) {
        throw "Installed DEV tray still exists after cleanup: $installedTray."
    }
    if (Test-Path -LiteralPath $devRoamingData) {
        throw "DEV roaming data still exists after cleanup: $devRoamingData."
    }
    if (Test-Path -LiteralPath $devLocalData) {
        throw "DEV local data still exists after cleanup: $devLocalData."
    }

    Remove-Item -LiteralPath (Split-Path -Parent $installRoot) -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null
    Remove-Item -LiteralPath $LogPath, $DonePath, $PidPath -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $PidPath -Value $PID -Encoding ASCII
    Set-Location $RepoRoot
    Write-Host "OpenClaw installed DEV Inno smoke"
    Write-Host "Artifacts: $ArtifactRoot"

    Invoke-Phase "preflight" {
        if (Test-Path -LiteralPath $devUninstallKey) {
            throw "A DEV install already exists. Refusing to overwrite or uninstall developer state."
        }
        if (Test-Path -LiteralPath $devRoamingData) {
            throw "DEV roaming data already exists. Refusing to touch developer state: $devRoamingData"
        }
        if (Test-Path -LiteralPath $devLocalData) {
            throw "DEV local data already exists. Refusing to touch developer state: $devLocalData"
        }
        $registeredDevDistro = @(& wsl.exe --list --quiet 2>$null) |
            ForEach-Object { ($_ -replace '\x00', '').Trim() } |
            Where-Object { $_ -eq "OpenClawGateway-Dev" }
        if ($registeredDevDistro) {
            throw "DEV WSL distro already exists. Refusing to touch developer state: OpenClawGateway-Dev"
        }
        & (Join-Path $RepoRoot "scripts\setup-dev.ps1") -CheckOnly
        Assert-NativeSuccess "Developer prerequisite check"
    }

    Invoke-Phase "build" {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
        & (Join-Path $RepoRoot "scripts\build-inno-local.ps1") -Arch x64 -Dev -Fast -InstallInno
        Assert-NativeSuccess "DEV Inno build"
        if (-not (Test-Path -LiteralPath $installerPath)) {
            throw "DEV installer was not produced at $installerPath."
        }
    }

    Invoke-Phase "install" {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installRoot) | Out-Null
        $script:ownsDevInstall = $true
        $installLog = Join-Path $ArtifactRoot "inno-install.log"
        $process = Start-Process -FilePath $installerPath -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/DIR=`"$installRoot`"",
            "/LOG=`"$installLog`""
        ) -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "DEV Inno installer failed with exit code $($process.ExitCode)."
        }
        if (-not (Test-Path -LiteralPath $installedTray)) {
            throw "Installed tray payload is missing: $installedTray."
        }
    }

    Invoke-Phase "installed-payload" {
        $identity = (Get-Content -LiteralPath (Join-Path $installRoot "app-identity.txt") -Raw).Trim()
        if ($identity -ne "dev") {
            throw "Installed payload identity '$identity' is not 'dev'."
        }
        $publishedTray = Join-Path $RepoRoot "publish-local-x64\OpenClaw.Tray.WinUI.exe"
        if (-not (Test-Path -LiteralPath $publishedTray)) {
            throw "Published DEV tray is missing: $publishedTray."
        }
        $installedHash = (Get-FileHash -LiteralPath $installedTray -Algorithm SHA256).Hash
        $publishedHash = (Get-FileHash -LiteralPath $publishedTray -Algorithm SHA256).Hash
        if ($installedHash -ne $publishedHash) {
            throw "Installed tray hash does not match the DEV installer payload."
        }
        "installedTray=$installedTray"
        "installedSha256=$installedHash"
    }

    Invoke-Phase "roundtrip" {
        $env:OPENCLAW_RUN_E2E = "1"
        $env:OPENCLAW_REPO_ROOT = $RepoRoot
        $env:OPENCLAW_E2E_TRAY_EXE = $installedTray
        $env:OPENCLAW_E2E_ARTIFACT_ROOT = Join-Path $ArtifactRoot "e2e"

        & dotnet build (Join-Path $RepoRoot "tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj") -c Debug -r win-x64
        Assert-NativeSuccess "E2E test build"

        $trxPath = Join-Path $ArtifactRoot "installed-published-gateway.trx"
        & dotnet test (Join-Path $RepoRoot "tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj") `
            --no-build `
            -c Debug `
            -r win-x64 `
            --filter "FullyQualifiedName~OpenClaw.E2ETests.Setup.PublishedGatewayNativeChatTests" `
            --results-directory $ArtifactRoot `
            --logger "trx;LogFileName=$(Split-Path -Leaf $trxPath)" `
            --logger "console;verbosity=detailed"
        Assert-NativeSuccess "Installed published-gateway roundtrip"

        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $proofName = "RealPublishedGateway_DeviceInfo_AndNativeChat_Roundtrip"
        $proof = @($trx.TestRun.Results.UnitTestResult | Where-Object {
            $_.testName -like "*$proofName*"
        }) | Select-Object -First 1
        if ($null -eq $proof) {
            throw "Installed roundtrip did not report the named proof '$proofName'."
        }
        if ([string]$proof.outcome -ne "Passed") {
            throw "Installed roundtrip proof '$proofName' did not pass. Outcome: '$($proof.outcome)'."
        }
    }

    Invoke-Phase "cleanup" {
        Invoke-Cleanup
    }

    foreach ($phase in $requiredPhases) {
        if (-not $phaseResults.Contains($phase) -or $phaseResults[$phase] -ne "passed") {
            throw "Installed smoke phase '$phase' was not completed successfully."
        }
    }
    $exitCode = 0
} catch {
    $_ | Out-String | Add-Content -LiteralPath $LogPath -Encoding UTF8
    if (-not $phaseResults.Contains("cleanup")) {
        try {
            Invoke-Phase "cleanup" {
                Invoke-Cleanup
            }
        } catch {
            $_ | Out-String | Add-Content -LiteralPath $LogPath -Encoding UTF8
        }
    }
    $exitCode = 1
} finally {
    foreach ($entry in $originalEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }

    [ordered]@{
        runId = $runId
        exitCode = $exitCode
        installedTray = $installedTray
        artifactRoot = $ArtifactRoot
        phases = $phaseResults
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactRoot "phase-status.json") -Encoding UTF8
    Set-Content -LiteralPath $DonePath -Value $exitCode -Encoding ASCII
}

if ($exitCode -eq 0) {
    Write-Host "Installed DEV Inno smoke passed."
    Write-Host "Artifacts: $ArtifactRoot"
} else {
    Write-Warning "Installed DEV Inno smoke failed. Inspect $LogPath and phase-status.json."
}
exit $exitCode
