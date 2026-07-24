<#
.SYNOPSIS
    Safely validates clean-runner unattended answer XML and answer ISO media.

.DESCRIPTION
    Creates a high-entropy in-process test credential, generates strict answer
    XML, and optionally creates and mounts the IMAPI2 ISO. The owned validation
    root must be a new child of TestResults\CleanWindowsUnattend. Cleanup checks
    a nonce-bound marker before deleting the generated files. Output is a
    nonsecret JSON proof object.
#>

[CmdletBinding()]
param(
    [ValidateSet("ValidateXml", "ValidateMedia", "ValidateAuthenticationClassifier")]
    [string]$Command = "ValidateMedia",

    [string]$OwnedRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$validationBase = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot "TestResults\CleanWindowsUnattend")
).TrimEnd("\")
if ([string]::IsNullOrWhiteSpace($OwnedRoot)) {
    $OwnedRoot = Join-Path $validationBase ("validation-" + [Guid]::NewGuid().ToString("N"))
}
$resolvedOwnedRoot = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd("\")
if (
    $resolvedOwnedRoot -eq $validationBase -or
    -not $resolvedOwnedRoot.StartsWith(
        $validationBase + "\",
        [StringComparison]::OrdinalIgnoreCase
    )
) {
    throw "OwnedRoot must be a new child of TestResults\CleanWindowsUnattend."
}
if (Test-Path -LiteralPath $resolvedOwnedRoot) {
    throw "OwnedRoot already exists. Refusing unsafe cleanup."
}

Import-Module (Join-Path $PSScriptRoot "CleanWindowsUnattend.psm1") -Force
$validationId = [Guid]::NewGuid().ToString("N")
$markerPath = Join-Path $resolvedOwnedRoot "validation.owner.json"
$mediaRoot = Join-Path $resolvedOwnedRoot "unattend"
$stagingPath = Join-Path $mediaRoot "staging"
$answerPath = Join-Path $stagingPath "AutoUnattend.xml"
$isoPath = Join-Path $mediaRoot "openclaw-unattend.iso"
$credentialPath = Join-Path $resolvedOwnedRoot "credentials\roundtrip.clixml"
$credentialMetadataPath = Join-Path $resolvedOwnedRoot "credentials\roundtrip.owner.json"
$credential = $null
try {
    New-Item -ItemType Directory -Force -Path $resolvedOwnedRoot | Out-Null
    [ordered]@{
        schema = "openclaw.clean-windows.media-validation/v1"
        validationId = $validationId
        root = $resolvedOwnedRoot
    } | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8

    if ($Command -eq "ValidateAuthenticationClassifier") {
        $badPasswordRejected = Test-CleanWindowsCredentialAuthenticationRejection `
            -Message "Logon failure: unknown user name or bad password." `
            -FullyQualifiedErrorId "PSSessionOpenFailed" `
            -Category "OpenError"
        $accessDeniedRejected = Test-CleanWindowsCredentialAuthenticationRejection `
            -Message "Access is denied." `
            -FullyQualifiedErrorId "CreateRemoteRunspaceFailed" `
            -Category "OpenError"
        $transientRejected = Test-CleanWindowsCredentialAuthenticationRejection `
            -Message "PowerShell Direct transport is temporarily unavailable." `
            -FullyQualifiedErrorId "PSSessionOpenFailed" `
            -Category "OpenError"
        if (-not $badPasswordRejected -or -not $accessDeniedRejected -or $transientRejected) {
            throw "Credential authentication rejection classifier failed its contract."
        }
        [ordered]@{
            schema = "openclaw.clean-windows.authentication-classifier-proof/v1"
            command = $Command
            succeeded = $true
            badPasswordRejected = $true
            accessDeniedRejected = $true
            transientTransportRejected = $false
            cleanup = "owned-root-removed-in-finally"
        } | ConvertTo-Json -Depth 4 -Compress
        return
    }

    Protect-CleanWindowsOwnedDirectory `
        -Path $mediaRoot `
        -OwnedRoot $resolvedOwnedRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $stagingPath | Out-Null
    $credential = New-CleanWindowsTestCredential -UserName "OpenClaw & QA"
    Export-CleanWindowsCredential `
        -Credential $credential `
        -CredentialPath $credentialPath `
        -MetadataPath $credentialMetadataPath `
        -OwnedRoot $resolvedOwnedRoot `
        -VMName "OpenClaw-Media-Test" `
        -OwnerId "openclaw-media-validation" `
        -Kind "final" | Out-Null
    $roundtripCredential = Import-CleanWindowsCredential `
        -CredentialPath $credentialPath `
        -MetadataPath $credentialMetadataPath `
        -OwnedRoot $resolvedOwnedRoot `
        -VMName "OpenClaw-Media-Test" `
        -OwnerId "openclaw-media-validation" `
        -ExpectedKind "final"
    if ($roundtripCredential.UserName -cne $credential.UserName) {
        throw "DPAPI credential roundtrip changed the username."
    }
    New-CleanWindowsAnswerFile `
        -Path $answerPath `
        -OwnedRoot $resolvedOwnedRoot `
        -Credential $credential `
        -ComputerName "OCW-MEDIATEST" | Out-Null
    $answerProof = Test-CleanWindowsAnswerFile `
        -Path $answerPath `
        -ExpectedComputerName "OCW-MEDIATEST" `
        -Credential $credential

    $mediaProof = $null
    if ($Command -eq "ValidateMedia") {
        New-CleanWindowsAnswerIso `
            -StagingPath $stagingPath `
            -IsoPath $isoPath `
            -OwnedRoot $resolvedOwnedRoot | Out-Null
        $mediaProof = Test-CleanWindowsAnswerIsoMount `
            -IsoPath $isoPath `
            -ExpectedAnswerFilePath $answerPath `
            -Credential $credential `
            -ExpectedComputerName "OCW-MEDIATEST"
    }

    [ordered]@{
        schema = "openclaw.clean-windows.unattend-validation/v1"
        command = $Command
        succeeded = $true
        credential = [ordered]@{
            dpapiRoundtrip = $true
            restrictiveAcl = $true
            passwordPrinted = $false
        }
        answerMediaRestrictiveAcl = $true
        answerFile = [ordered]@{
            valid = [bool]$answerProof.valid
            imageIndex = [int]$answerProof.imageIndex
            imageName = [string]$answerProof.imageName
            architecture = [string]$answerProof.architecture
            locale = [string]$answerProof.locale
            autoLogon = [bool]$answerProof.autoLogon
            productKey = [bool]$answerProof.productKey
        }
        media = if ($null -eq $mediaProof) {
            $null
        } else {
            [ordered]@{
                valid = [bool]$mediaProof.valid
                mountedReadOnly = [bool]$mediaProof.mountedReadOnly
                answerFileAtRoot = [bool]$mediaProof.answerFileAtRoot
            }
        }
        cleanup = "owned-root-removed-in-finally"
    } | ConvertTo-Json -Depth 6 -Compress
} finally {
    $credential = $null
    if (Test-Path -LiteralPath $resolvedOwnedRoot) {
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw "Validation cleanup refused because its ownership marker is missing."
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if (
            $marker.schema -cne "openclaw.clean-windows.media-validation/v1" -or
            $marker.validationId -cne $validationId -or
            [IO.Path]::GetFullPath([string]$marker.root).TrimEnd("\") -cne $resolvedOwnedRoot
        ) {
            throw "Validation cleanup refused because its ownership marker does not match."
        }
        Remove-Item -LiteralPath $resolvedOwnedRoot -Recurse -Force
    }
}
