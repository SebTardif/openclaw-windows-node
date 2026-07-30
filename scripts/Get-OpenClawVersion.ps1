<#
.SYNOPSIS
    Prints the GitVersion-derived OpenClaw version.

.DESCRIPTION
    Uses the repository-local GitVersion.Tool manifest so local scripts and CI
    derive versions from the same GitVersion.yml/tag history as release builds.
#>

[CmdletBinding()]
param(
    [ValidateSet("SemVer", "MajorMinorPatch")]
    [string]$Variable = "SemVer",

    [switch]$NoRestore,

    [ValidateRange(1, 1800)]
    [int]$NativeTimeoutSec = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-SafeVersionDiagnostic {
    param(
        [AllowNull()]
        [string]$Text,
        [ValidateRange(32, 4096)]
        [int]$MaximumLength = 2048
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return "<empty>"
    }
    $safe = $Text -replace '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', '?'
    $safe = $safe -replace '(?i)\b(authorization|password|passwd|pwd|secret|token|api[-_]?key)\s*[:=]\s*\S+', '$1=<redacted>'
    $safe = $safe -replace '(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/\-=]+', '$1 <redacted>'
    $safe = $safe -replace '(https?://[^?\s]+)\?[^\s]+', '$1?<redacted>'
    $safe = ($safe -replace '\s+', ' ').Trim()
    if ($safe.Length -gt $MaximumLength) {
        return $safe.Substring(0, $MaximumLength) + "...<truncated>"
    }
    return $safe
}

function Invoke-OpenClawVersionDotNet {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Restore", "GitVersion")]
        [string]$Operation,
        [Parameter(Mandatory = $true)]
        [string]$DotNetPath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [string]$CaptureRoot
    )

    $arguments = if ($Operation -eq "Restore") {
        [string[]]@("tool", "restore")
    } else {
        [string[]]@("tool", "run", "dotnet-gitversion", "--", "/output", "json")
    }
    $stdoutPath = Join-Path $CaptureRoot "$Operation.stdout.txt"
    $stderrPath = Join-Path $CaptureRoot "$Operation.stderr.txt"
    $process = Start-Process `
        -FilePath $DotNetPath `
        -WorkingDirectory $WorkingDirectory `
        -ArgumentList ($arguments -join " ") `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru `
        -WindowStyle Hidden `
        -ErrorAction Stop
    try {
        # Windows PowerShell 5.1 requires opening the handle before exit to retain ExitCode.
        $null = $process.Handle
        if (-not $process.WaitForExit($NativeTimeoutSec * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            [void]$process.WaitForExit(5000)
            throw "dotnet.exe $Operation timed out after $NativeTimeoutSec seconds."
        }
        $process.WaitForExit()
        $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
            [IO.File]::ReadAllText($stdoutPath)
        } else {
            ""
        }
        $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
            [IO.File]::ReadAllText($stderrPath)
        } else {
            ""
        }
        $safeStdout = ConvertTo-SafeVersionDiagnostic -Text $stdout
        $safeStderr = ConvertTo-SafeVersionDiagnostic -Text $stderr
        if ([int]$process.ExitCode -ne 0) {
            throw (
                "dotnet.exe {0} failed with exit code {1}. stdout='{2}' stderr='{3}'" -f
                    $Operation,
                    $process.ExitCode,
                    $safeStdout,
                    $safeStderr)
        }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Verbose "dotnet.exe $Operation stderr: $safeStderr"
        }
        return [pscustomobject][ordered]@{
            operation = $Operation
            exitCode = [int]$process.ExitCode
            stdout = $stdout
            stderr = $safeStderr
        }
    } finally {
        $process.Dispose()
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$dotnetCommands = @(
    Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
)
if ($dotnetCommands.Count -eq 0) {
    throw "dotnet.exe was not found as a Windows Application."
}
$dotnetPath = [string]$dotnetCommands[0].Source
$captureRoot = Join-Path $env:TEMP (
    "openclaw-version-{0}" -f [Guid]::NewGuid().ToString("N"))
$environmentNames = @(
    "DOTNET_NOLOGO",
    "DOTNET_CLI_TELEMETRY_OPTOUT",
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE")
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [pscustomobject]@{
        exists = Test-Path -LiteralPath "Env:$name"
        value = [Environment]::GetEnvironmentVariable($name, "Process")
    }
}
$primaryError = $null
$cleanupError = $null
$versionValue = $null
try {
    New-Item -ItemType Directory -Path $captureRoot -ErrorAction Stop | Out-Null
    $env:DOTNET_NOLOGO = "1"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    if (-not $NoRestore) {
        [void](Invoke-OpenClawVersionDotNet `
            -Operation "Restore" `
            -DotNetPath $dotnetPath `
            -WorkingDirectory $repoRoot `
            -CaptureRoot $captureRoot)
    }

    $gitVersionResult = Invoke-OpenClawVersionDotNet `
        -Operation "GitVersion" `
        -DotNetPath $dotnetPath `
        -WorkingDirectory $repoRoot `
        -CaptureRoot $captureRoot
    $jsonText = [string]$gitVersionResult.stdout
    $trimmedJson = $jsonText.Trim()
    if (
        -not $trimmedJson.StartsWith("{", [StringComparison]::Ordinal) -or
        -not $trimmedJson.EndsWith("}", [StringComparison]::Ordinal)
    ) {
        throw "GitVersion stdout was not one JSON object."
    }
    try {
        $gitVersion = $trimmedJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $safeJsonError = ConvertTo-SafeVersionDiagnostic -Text $_.Exception.Message
        throw "GitVersion stdout was not valid JSON: $safeJsonError"
    }
    if ($null -eq $gitVersion -or $gitVersion -is [Array]) {
        throw "GitVersion stdout was not one JSON object."
    }
    $property = $gitVersion.PSObject.Properties[$Variable]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "GitVersion did not return '$Variable'."
    }
    $versionValue = [string]$property.Value
} catch {
    $primaryError = $_
} finally {
    foreach ($name in $environmentNames) {
        $previous = $previousEnvironment[$name]
        if ([bool]$previous.exists) {
            [Environment]::SetEnvironmentVariable($name, [string]$previous.value, "Process")
        } else {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $captureRoot) {
        try {
            Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction Stop
        } catch {
            $cleanupError = $_
        }
    }
}

if ($null -ne $primaryError -and $null -ne $cleanupError) {
    $safePrimary = ConvertTo-SafeVersionDiagnostic -Text $primaryError.Exception.Message
    $safeCleanup = ConvertTo-SafeVersionDiagnostic -Text $cleanupError.Exception.Message
    throw "Version discovery failed: $safePrimary Capture cleanup also failed: $safeCleanup"
}
if ($null -ne $primaryError) {
    throw $primaryError
}
if ($null -ne $cleanupError) {
    $safeCleanup = ConvertTo-SafeVersionDiagnostic -Text $cleanupError.Exception.Message
    throw "Version capture cleanup failed: $safeCleanup"
}
Write-Output $versionValue
