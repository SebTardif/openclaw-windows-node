function Test-EngineUninstallSucceeded {
    param(
        $EngineJson,
        [int]$ProcessExitCode
    )

    if ($null -eq $EngineJson -or $ProcessExitCode -ne 0) {
        return $false
    }

    $outcomeProperty = $EngineJson.PSObject.Properties["outcome"]
    $exitProperty = $EngineJson.PSObject.Properties["exitCode"]
    if ($null -eq $outcomeProperty -or $null -eq $exitProperty) {
        return $false
    }

    $declaredExit = 0
    if (-not [int]::TryParse([string]$exitProperty.Value, [ref]$declaredExit)) {
        return $false
    }

    return ([string]$outcomeProperty.Value) -eq "Success" -and $declaredExit -eq 0
}

function Test-MsixStorageVerdictConfirmed {
    param(
        [string]$Verdict,
        $CrossCheckConsistent,
        $Postconditions
    )

    if ($Verdict -ne "PathA-OrphanRisk" -and $Verdict -ne "PathB-CleanRemove") {
        return $false
    }
    if ($CrossCheckConsistent -ne $true) {
        return $false
    }
    if ($null -eq $Postconditions) {
        return $false
    }

    $token = $Postconditions.PSObject.Properties["device_token_cleared"]
    $distro = $Postconditions.PSObject.Properties["wsl_distro_absent"]
    if ($null -eq $token -or $null -eq $distro) {
        return $false
    }

    return [bool]$token.Value -and [bool]$distro.Value
}
