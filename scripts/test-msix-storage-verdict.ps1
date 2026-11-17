Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "MsixStorageVerdict.ps1")

function Assert-Equal {
    param($Name, $Actual, $Expected)
    if ($Actual -ne $Expected) {
        throw "$Name expected $Expected but was $Actual"
    }
}

$real = [pscustomobject]@{ outcome = "Success"; exitCode = 0 }
Assert-Equal "real success" (Test-EngineUninstallSucceeded -EngineJson $real -ProcessExitCode 0) $true

$oldShape = [pscustomobject]@{ success = $true }
Assert-Equal "old success field" (Test-EngineUninstallSucceeded -EngineJson $oldShape -ProcessExitCode 0) $false

$wrongLength = [pscustomobject]@{ outcome = "Success"; exitCode = 1 }
Assert-Equal "nonzero engine exit" (Test-EngineUninstallSucceeded -EngineJson $wrongLength -ProcessExitCode 0) $false

$confirmed = [pscustomobject]@{ device_token_cleared = $true; wsl_distro_absent = $true }
Assert-Equal "confirmed path A" (Test-MsixStorageVerdictConfirmed -Verdict "PathA-OrphanRisk" -CrossCheckConsistent $true -Postconditions $confirmed) $true
Assert-Equal "missing postconditions" (Test-MsixStorageVerdictConfirmed -Verdict "PathB-CleanRemove" -CrossCheckConsistent $true -Postconditions $null) $false
Assert-Equal "cross check false" (Test-MsixStorageVerdictConfirmed -Verdict "PathA-OrphanRisk" -CrossCheckConsistent $false -Postconditions $confirmed) $false
Assert-Equal "token still present" (Test-MsixStorageVerdictConfirmed -Verdict "PathA-OrphanRisk" -CrossCheckConsistent $true -Postconditions ([pscustomobject]@{ device_token_cleared = $false; wsl_distro_absent = $true })) $false

Write-Host "MSIX storage verdict checks passed."
