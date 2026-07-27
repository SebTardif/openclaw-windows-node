Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:UnattendNamespace = "urn:schemas-microsoft-com:unattend"
$script:WcmNamespace = "http://schemas.microsoft.com/WMIConfig/2002/State"
$script:CredentialSchema = "openclaw.clean-windows.credential/v1"
$script:ExpectedImageIndex = "1"
$script:ExpectedImageName = "Windows 11 Enterprise Evaluation"

if (-not ("OpenClaw.CleanWindows.ComStreamCopy" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OpenClaw.CleanWindows
{
    public static class ComStreamCopy
    {
        public static void Save(object source, string path)
        {
            var stream = (IStream)source;
            var buffer = new byte[1024 * 1024];
            var bytesReadPointer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                using (var output = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    int bytesRead;
                    do
                    {
                        Marshal.WriteInt32(bytesReadPointer, 0);
                        stream.Read(buffer, buffer.Length, bytesReadPointer);
                        bytesRead = Marshal.ReadInt32(bytesReadPointer);
                        if (bytesRead > 0)
                        {
                            output.Write(buffer, 0, bytesRead);
                        }
                    }
                    while (bytesRead > 0);
                    output.Flush(true);
                }
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
                Marshal.FreeHGlobal(bytesReadPointer);
            }
        }
    }
}
'@
}

function Test-CleanWindowsHost {
    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($isWindowsVariable) {
        return [bool]$isWindowsVariable.Value
    }

    return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
}

function Get-CleanWindowsFileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read
    )
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace("-", "")
    } finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Assert-CleanWindowsPathUnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd("\")
    if (
        -not $resolvedPath.StartsWith(
            $resolvedRoot + "\",
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw "Path must stay beneath the owned marker directory."
    }

    return $resolvedPath
}

function Get-CryptographicRandomIndex {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 255)]
        [int]$UpperBound,
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.RandomNumberGenerator]$Generator
    )

    $limit = 256 - (256 % $UpperBound)
    $buffer = New-Object byte[] 1
    do {
        $Generator.GetBytes($buffer)
        $value = [int]$buffer[0]
    } while ($value -ge $limit)

    return $value % $UpperBound
}

function New-CleanWindowsTestCredential {
    [CmdletBinding()]
    param(
        [ValidatePattern('^[^"\/\\\[\]:;|=,+*?<>@\s][^"\/\\\[\]:;|=,+*?<>@]{0,18}[^"\/\\\[\]:;|=,+*?<>@\s.]$|^[A-Za-z0-9]$')]
        [string]$UserName = "OpenClawAdmin",
        [ValidateRange(24, 128)]
        [int]$PasswordLength = 32
    )

    if (-not (Test-CleanWindowsHost)) {
        throw "Credential generation requires Windows."
    }
    if (@("Administrator", "Guest", "DefaultAccount", "WDAGUtilityAccount") -contains $UserName) {
        throw "Guest administrator name must not target a built-in Windows account."
    }

    $upper = "ABCDEFGHJKLMNPQRSTUVWXYZ"
    $lower = "abcdefghijkmnopqrstuvwxyz"
    $digits = "23456789"
    $xmlSensitiveSymbols = "&<>"
    $symbols = "!#$%&()*+,-.:;<=>?@[]^_{|}~"
    $all = $upper + $lower + $digits + $symbols
    $characters = New-Object char[] $PasswordLength
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $characters[0] = $upper[(
            Get-CryptographicRandomIndex -UpperBound $upper.Length -Generator $generator
        )]
        $characters[1] = $lower[(
            Get-CryptographicRandomIndex -UpperBound $lower.Length -Generator $generator
        )]
        $characters[2] = $digits[(
            Get-CryptographicRandomIndex -UpperBound $digits.Length -Generator $generator
        )]
        $characters[3] = $xmlSensitiveSymbols[(
            Get-CryptographicRandomIndex -UpperBound $xmlSensitiveSymbols.Length -Generator $generator
        )]
        for ($index = 4; $index -lt $characters.Length; $index++) {
            $characters[$index] = $all[(
                Get-CryptographicRandomIndex -UpperBound $all.Length -Generator $generator
            )]
        }

        for ($index = $characters.Length - 1; $index -gt 0; $index--) {
            $swapIndex = Get-CryptographicRandomIndex -UpperBound ($index + 1) -Generator $generator
            $temporary = $characters[$index]
            $characters[$index] = $characters[$swapIndex]
            $characters[$swapIndex] = $temporary
        }

        $securePassword = New-Object Security.SecureString
        foreach ($character in $characters) {
            $securePassword.AppendChar($character)
        }
        $securePassword.MakeReadOnly()
        return New-Object Management.Automation.PSCredential($UserName, $securePassword)
    } finally {
        [Array]::Clear($characters, 0, $characters.Length)
        $generator.Dispose()
    }
}

function Get-SecureStringPlainText {
    param(
        [Parameter(Mandatory = $true)]
        [Security.SecureString]$SecureString
    )

    $pointer = [IntPtr]::Zero
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    } finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

function Add-UnattendElement {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Parent,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Text,
        [switch]$AddAction
    )

    $element = $Document.CreateElement($Name, $script:UnattendNamespace)
    if ($PSBoundParameters.ContainsKey("Text") -and $null -ne $Text) {
        $element.InnerText = $Text
    }
    if ($AddAction) {
        $action = $Document.CreateAttribute("wcm", "action", $script:WcmNamespace)
        $action.Value = "add"
        [void]$element.Attributes.Append($action)
    }
    [void]$Parent.AppendChild($element)
    return $element
}

function Add-UnattendComponent {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Settings,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $component = Add-UnattendElement -Document $Document -Parent $Settings -Name "component"
    $component.SetAttribute("name", $Name)
    $component.SetAttribute("processorArchitecture", "amd64")
    $component.SetAttribute("publicKeyToken", "31bf3856ad364e35")
    $component.SetAttribute("language", "neutral")
    $component.SetAttribute("versionScope", "nonSxS")
    return $component
}

function Add-LocaleSettings {
    param(
        [Parameter(Mandatory = $true)]
        [Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)]
        [Xml.XmlElement]$Component,
        [switch]$IncludeSetupUiLanguage
    )

    if ($IncludeSetupUiLanguage) {
        $setupLanguage = Add-UnattendElement -Document $Document -Parent $Component -Name "SetupUILanguage"
        [void](Add-UnattendElement -Document $Document -Parent $setupLanguage -Name "UILanguage" -Text "en-US")
    }

    foreach ($name in @("InputLocale", "SystemLocale", "UILanguage", "UserLocale")) {
        [void](Add-UnattendElement -Document $Document -Parent $Component -Name $name -Text "en-US")
    }
}

function Add-CreatePartition {
    param(
        [Xml.XmlDocument]$Document,
        [Xml.XmlElement]$Parent,
        [string]$Order,
        [string]$Type,
        [string]$Size,
        [switch]$Extend
    )

    $partition = Add-UnattendElement -Document $Document -Parent $Parent -Name "CreatePartition" -AddAction
    [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Order" -Text $Order)
    [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Type" -Text $Type)
    if (-not [string]::IsNullOrWhiteSpace($Size)) {
        [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Size" -Text $Size)
    }
    if ($Extend) {
        [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Extend" -Text "true")
    }
}

function Add-ModifyPartition {
    param(
        [Xml.XmlDocument]$Document,
        [Xml.XmlElement]$Parent,
        [string]$Order,
        [string]$PartitionId,
        [string]$Format,
        [string]$Label,
        [string]$Letter
    )

    $partition = Add-UnattendElement -Document $Document -Parent $Parent -Name "ModifyPartition" -AddAction
    [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Order" -Text $Order)
    [void](Add-UnattendElement -Document $Document -Parent $partition -Name "PartitionID" -Text $PartitionId)
    if (-not [string]::IsNullOrWhiteSpace($Format)) {
        [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Format" -Text $Format)
    }
    if (-not [string]::IsNullOrWhiteSpace($Label)) {
        [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Label" -Text $Label)
    }
    if (-not [string]::IsNullOrWhiteSpace($Letter)) {
        [void](Add-UnattendElement -Document $Document -Parent $partition -Name "Letter" -Text $Letter)
    }
}

function New-CleanWindowsAnswerFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot,
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$Credential,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^(?!-)(?!.*--)[A-Za-z0-9-]{1,15}(?<!-)$')]
        [string]$ComputerName
    )

    $resolvedPath = Assert-CleanWindowsPathUnderRoot -Path $Path -OwnedRoot $OwnedRoot
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedPath) | Out-Null

    $document = New-Object Xml.XmlDocument
    [void]$document.AppendChild($document.CreateXmlDeclaration("1.0", "utf-8", $null))
    $root = $document.CreateElement("unattend", $script:UnattendNamespace)
    $wcmDeclaration = $document.CreateAttribute("xmlns", "wcm", "http://www.w3.org/2000/xmlns/")
    $wcmDeclaration.Value = $script:WcmNamespace
    [void]$root.Attributes.Append($wcmDeclaration)
    [void]$document.AppendChild($root)

    $windowsPe = Add-UnattendElement -Document $document -Parent $root -Name "settings"
    $windowsPe.SetAttribute("pass", "windowsPE")
    $peLocale = Add-UnattendComponent `
        -Document $document `
        -Settings $windowsPe `
        -Name "Microsoft-Windows-International-Core-WinPE"
    Add-LocaleSettings -Document $document -Component $peLocale -IncludeSetupUiLanguage

    $setup = Add-UnattendComponent `
        -Document $document `
        -Settings $windowsPe `
        -Name "Microsoft-Windows-Setup"
    $diskConfiguration = Add-UnattendElement -Document $document -Parent $setup -Name "DiskConfiguration"
    $disk = Add-UnattendElement -Document $document -Parent $diskConfiguration -Name "Disk" -AddAction
    [void](Add-UnattendElement -Document $document -Parent $disk -Name "DiskID" -Text "0")
    [void](Add-UnattendElement -Document $document -Parent $disk -Name "WillWipeDisk" -Text "true")
    $createPartitions = Add-UnattendElement -Document $document -Parent $disk -Name "CreatePartitions"
    Add-CreatePartition -Document $document -Parent $createPartitions -Order "1" -Type "EFI" -Size "260"
    Add-CreatePartition -Document $document -Parent $createPartitions -Order "2" -Type "MSR" -Size "16"
    Add-CreatePartition -Document $document -Parent $createPartitions -Order "3" -Type "Primary" -Extend
    $modifyPartitions = Add-UnattendElement -Document $document -Parent $disk -Name "ModifyPartitions"
    Add-ModifyPartition -Document $document -Parent $modifyPartitions -Order "1" -PartitionId "1" -Format "FAT32" -Label "System" -Letter ""
    Add-ModifyPartition -Document $document -Parent $modifyPartitions -Order "2" -PartitionId "2" -Format "" -Label "" -Letter ""
    Add-ModifyPartition -Document $document -Parent $modifyPartitions -Order "3" -PartitionId "3" -Format "NTFS" -Label "Windows" -Letter "C"
    [void](Add-UnattendElement -Document $document -Parent $diskConfiguration -Name "WillShowUI" -Text "OnError")

    $imageInstall = Add-UnattendElement -Document $document -Parent $setup -Name "ImageInstall"
    $osImage = Add-UnattendElement -Document $document -Parent $imageInstall -Name "OSImage"
    $installFrom = Add-UnattendElement -Document $document -Parent $osImage -Name "InstallFrom"
    $metadata = Add-UnattendElement -Document $document -Parent $installFrom -Name "MetaData" -AddAction
    [void](Add-UnattendElement -Document $document -Parent $metadata -Name "Key" -Text "/IMAGE/INDEX")
    [void](Add-UnattendElement -Document $document -Parent $metadata -Name "Value" -Text $script:ExpectedImageIndex)
    $installTo = Add-UnattendElement -Document $document -Parent $osImage -Name "InstallTo"
    [void](Add-UnattendElement -Document $document -Parent $installTo -Name "DiskID" -Text "0")
    [void](Add-UnattendElement -Document $document -Parent $installTo -Name "PartitionID" -Text "3")
    [void](Add-UnattendElement -Document $document -Parent $osImage -Name "WillShowUI" -Text "OnError")
    $userData = Add-UnattendElement -Document $document -Parent $setup -Name "UserData"
    [void](Add-UnattendElement -Document $document -Parent $userData -Name "AcceptEula" -Text "true")
    [void](Add-UnattendElement -Document $document -Parent $userData -Name "FullName" -Text "OpenClaw Test")
    [void](Add-UnattendElement -Document $document -Parent $userData -Name "Organization" -Text "OpenClaw")

    $specialize = Add-UnattendElement -Document $document -Parent $root -Name "settings"
    $specialize.SetAttribute("pass", "specialize")
    $specializeShell = Add-UnattendComponent `
        -Document $document `
        -Settings $specialize `
        -Name "Microsoft-Windows-Shell-Setup"
    [void](Add-UnattendElement -Document $document -Parent $specializeShell -Name "ComputerName" -Text $ComputerName)
    [void](Add-UnattendElement -Document $document -Parent $specializeShell -Name "RegisteredOrganization" -Text "OpenClaw")
    [void](Add-UnattendElement -Document $document -Parent $specializeShell -Name "RegisteredOwner" -Text "OpenClaw Test")

    $oobe = Add-UnattendElement -Document $document -Parent $root -Name "settings"
    $oobe.SetAttribute("pass", "oobeSystem")
    $oobeLocale = Add-UnattendComponent `
        -Document $document `
        -Settings $oobe `
        -Name "Microsoft-Windows-International-Core"
    Add-LocaleSettings -Document $document -Component $oobeLocale
    $oobeShell = Add-UnattendComponent `
        -Document $document `
        -Settings $oobe `
        -Name "Microsoft-Windows-Shell-Setup"
    $oobeOptions = Add-UnattendElement -Document $document -Parent $oobeShell -Name "OOBE"
    $oobeSettings = [ordered]@{
        HideEULAPage = "true"
        HideLocalAccountScreen = "true"
        HideOEMRegistrationScreen = "true"
        HideOnlineAccountScreens = "true"
        HideWirelessSetupInOOBE = "true"
        NetworkLocation = "Work"
        ProtectYourPC = "3"
        SkipMachineOOBE = "true"
        SkipUserOOBE = "true"
    }
    foreach ($option in $oobeSettings.GetEnumerator()) {
        [void](Add-UnattendElement -Document $document -Parent $oobeOptions -Name $option.Key -Text $option.Value)
    }

    $userAccounts = Add-UnattendElement -Document $document -Parent $oobeShell -Name "UserAccounts"
    $localAccounts = Add-UnattendElement -Document $document -Parent $userAccounts -Name "LocalAccounts"
    $localAccount = Add-UnattendElement -Document $document -Parent $localAccounts -Name "LocalAccount" -AddAction
    $password = Add-UnattendElement -Document $document -Parent $localAccount -Name "Password"
    $plainText = Get-SecureStringPlainText -SecureString $Credential.Password
    try {
        [void](Add-UnattendElement -Document $document -Parent $password -Name "Value" -Text $plainText)
    } finally {
        $plainText = $null
    }
    [void](Add-UnattendElement -Document $document -Parent $password -Name "PlainText" -Text "true")
    [void](Add-UnattendElement -Document $document -Parent $localAccount -Name "Description" -Text "Disposable OpenClaw test administrator")
    [void](Add-UnattendElement -Document $document -Parent $localAccount -Name "DisplayName" -Text $Credential.UserName)
    [void](Add-UnattendElement -Document $document -Parent $localAccount -Name "Group" -Text "Administrators")
    [void](Add-UnattendElement -Document $document -Parent $localAccount -Name "Name" -Text $Credential.UserName)
    [void](Add-UnattendElement -Document $document -Parent $oobeShell -Name "RegisteredOrganization" -Text "OpenClaw")
    [void](Add-UnattendElement -Document $document -Parent $oobeShell -Name "RegisteredOwner" -Text "OpenClaw Test")

    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = New-Object Text.UTF8Encoding($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $writer = [Xml.XmlWriter]::Create($resolvedPath, $settings)
    try {
        $document.Save($writer)
    } finally {
        $writer.Dispose()
    }

    Test-CleanWindowsAnswerFile `
        -Path $resolvedPath `
        -ExpectedComputerName $ComputerName `
        -Credential $Credential | Out-Null
    return $resolvedPath
}

function Get-RequiredXmlNode {
    param(
        [Xml.XmlNode]$Root,
        [string]$XPath,
        [Xml.XmlNamespaceManager]$NamespaceManager,
        [string]$Description
    )

    $node = $Root.SelectSingleNode($XPath, $NamespaceManager)
    if ($null -eq $node) {
        throw "Answer file is missing $Description."
    }
    return $node
}

function Assert-XmlValue {
    param(
        [Xml.XmlNode]$Root,
        [string]$XPath,
        [string]$Expected,
        [Xml.XmlNamespaceManager]$NamespaceManager,
        [string]$Description
    )

    $node = Get-RequiredXmlNode `
        -Root $Root `
        -XPath $XPath `
        -NamespaceManager $NamespaceManager `
        -Description $Description
    if ($node.InnerText -cne $Expected) {
        throw "Answer file $Description has an unexpected value."
    }
}

function Test-CleanWindowsAnswerFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedComputerName,
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$Credential
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $document = New-Object Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.Load($resolvedPath)
    if ($document.DocumentElement.NamespaceURI -cne $script:UnattendNamespace) {
        throw "Answer file root namespace is invalid."
    }

    $namespaces = New-Object Xml.XmlNamespaceManager($document.NameTable)
    $namespaces.AddNamespace("u", $script:UnattendNamespace)
    $namespaces.AddNamespace("wcm", $script:WcmNamespace)
    $setupPath = "/u:unattend/u:settings[@pass='windowsPE']/u:component[@name='Microsoft-Windows-Setup']"
    $diskPath = "$setupPath/u:DiskConfiguration/u:Disk"
    Assert-XmlValue -Root $document -XPath "$diskPath/u:DiskID" -Expected "0" -NamespaceManager $namespaces -Description "disk ID"
    Assert-XmlValue -Root $document -XPath "$diskPath/u:WillWipeDisk" -Expected "true" -NamespaceManager $namespaces -Description "fresh-disk wipe setting"

    $expectedCreatePartitions = @(
        [ordered]@{ order = "1"; type = "EFI"; size = "260"; extend = "" },
        [ordered]@{ order = "2"; type = "MSR"; size = "16"; extend = "" },
        [ordered]@{ order = "3"; type = "Primary"; size = ""; extend = "true" }
    )
    $createNodes = @($document.SelectNodes("$diskPath/u:CreatePartitions/u:CreatePartition", $namespaces))
    if ($createNodes.Count -ne $expectedCreatePartitions.Count) {
        throw "Answer file must create exactly three GPT partitions."
    }
    for ($index = 0; $index -lt $expectedCreatePartitions.Count; $index++) {
        $expected = $expectedCreatePartitions[$index]
        $actual = $createNodes[$index]
        foreach ($property in @("Order", "Type")) {
            $expectedValue = $expected[$property.ToLowerInvariant()]
            Assert-XmlValue -Root $actual -XPath "u:$property" -Expected $expectedValue -NamespaceManager $namespaces -Description "partition $($index + 1) $property"
        }
        if ($expected.size) {
            Assert-XmlValue -Root $actual -XPath "u:Size" -Expected $expected.size -NamespaceManager $namespaces -Description "partition $($index + 1) size"
        } elseif ($null -ne $actual.SelectSingleNode("u:Size", $namespaces)) {
            throw "The extending Windows partition must not have a fixed size."
        }
        if ($expected.extend) {
            Assert-XmlValue -Root $actual -XPath "u:Extend" -Expected "true" -NamespaceManager $namespaces -Description "Windows partition extension"
        }
    }

    $modifyNodes = @($document.SelectNodes("$diskPath/u:ModifyPartitions/u:ModifyPartition", $namespaces))
    if ($modifyNodes.Count -ne 3) {
        throw "Answer file must modify exactly three GPT partitions."
    }
    $modifyExpectations = @(
        [ordered]@{ order = "1"; id = "1"; format = "FAT32"; label = "System"; letter = "" },
        [ordered]@{ order = "2"; id = "2"; format = ""; label = ""; letter = "" },
        [ordered]@{ order = "3"; id = "3"; format = "NTFS"; label = "Windows"; letter = "C" }
    )
    for ($index = 0; $index -lt $modifyExpectations.Count; $index++) {
        $expected = $modifyExpectations[$index]
        $actual = $modifyNodes[$index]
        Assert-XmlValue -Root $actual -XPath "u:Order" -Expected $expected.order -NamespaceManager $namespaces -Description "modified partition order"
        Assert-XmlValue -Root $actual -XPath "u:PartitionID" -Expected $expected.id -NamespaceManager $namespaces -Description "modified partition ID"
        foreach ($property in @("Format", "Label", "Letter")) {
            $expectedValue = $expected[$property.ToLowerInvariant()]
            $node = $actual.SelectSingleNode("u:$property", $namespaces)
            if ($expectedValue) {
                if ($null -eq $node -or $node.InnerText -cne $expectedValue) {
                    throw "Answer file modified partition $($index + 1) has an invalid $property."
                }
            } elseif ($null -ne $node) {
                throw "Answer file modified partition $($index + 1) must omit $property."
            }
        }
    }

    Assert-XmlValue -Root $document -XPath "$setupPath/u:ImageInstall/u:OSImage/u:InstallTo/u:DiskID" -Expected "0" -NamespaceManager $namespaces -Description "install disk"
    Assert-XmlValue -Root $document -XPath "$setupPath/u:ImageInstall/u:OSImage/u:InstallTo/u:PartitionID" -Expected "3" -NamespaceManager $namespaces -Description "install partition"
    $metadataNodes = @($document.SelectNodes("$setupPath/u:ImageInstall/u:OSImage/u:InstallFrom/u:MetaData", $namespaces))
    if ($metadataNodes.Count -ne 1) {
        throw "Answer file must contain exactly one supported OS image selector."
    }
    $selectors = @{}
    foreach ($metadata in $metadataNodes) {
        $key = (Get-RequiredXmlNode -Root $metadata -XPath "u:Key" -NamespaceManager $namespaces -Description "image metadata key").InnerText
        $value = (Get-RequiredXmlNode -Root $metadata -XPath "u:Value" -NamespaceManager $namespaces -Description "image metadata value").InnerText
        $selectors[$key] = $value
    }
    if ($selectors["/IMAGE/INDEX"] -cne $script:ExpectedImageIndex) {
        throw "Answer file image index is not 1."
    }
    if ($selectors.ContainsKey("/IMAGE/NAME")) {
        throw "Answer file must not combine unsupported index and name selectors."
    }

    foreach ($localePath in @(
        "/u:unattend/u:settings[@pass='windowsPE']/u:component[@name='Microsoft-Windows-International-Core-WinPE']",
        "/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='Microsoft-Windows-International-Core']"
    )) {
        foreach ($localeName in @("InputLocale", "SystemLocale", "UILanguage", "UserLocale")) {
            Assert-XmlValue -Root $document -XPath "$localePath/u:$localeName" -Expected "en-US" -NamespaceManager $namespaces -Description "$localeName locale"
        }
    }

    Assert-XmlValue -Root $document -XPath "/u:unattend/u:settings[@pass='specialize']/u:component[@name='Microsoft-Windows-Shell-Setup']/u:ComputerName" -Expected $ExpectedComputerName -NamespaceManager $namespaces -Description "computer name"
    $accountPath = "/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='Microsoft-Windows-Shell-Setup']/u:UserAccounts/u:LocalAccounts/u:LocalAccount"
    Assert-XmlValue -Root $document -XPath "$accountPath/u:Name" -Expected $Credential.UserName -NamespaceManager $namespaces -Description "local account name"
    Assert-XmlValue -Root $document -XPath "$accountPath/u:Group" -Expected "Administrators" -NamespaceManager $namespaces -Description "local administrator group"
    Assert-XmlValue -Root $document -XPath "$accountPath/u:Password/u:PlainText" -Expected "true" -NamespaceManager $namespaces -Description "setup password encoding"
    $expectedPassword = Get-SecureStringPlainText -SecureString $Credential.Password
    try {
        Assert-XmlValue -Root $document -XPath "$accountPath/u:Password/u:Value" -Expected $expectedPassword -NamespaceManager $namespaces -Description "escaped setup password"
    } finally {
        $expectedPassword = $null
    }

    foreach ($forbiddenName in @(
        "AutoLogon",
        "ProductKey",
        "RunSynchronous",
        "RunAsynchronous",
        "FirstLogonCommands",
        "LogonCommands"
    )) {
        if ($null -ne $document.SelectSingleNode("//*[local-name()='$forbiddenName']")) {
            throw "Answer file must not contain $forbiddenName."
        }
    }

    foreach ($requiredOobeValue in @(
        "HideEULAPage",
        "HideLocalAccountScreen",
        "HideOEMRegistrationScreen",
        "HideOnlineAccountScreens",
        "HideWirelessSetupInOOBE",
        "SkipMachineOOBE",
        "SkipUserOOBE"
    )) {
        Assert-XmlValue `
            -Root $document `
            -XPath "/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='Microsoft-Windows-Shell-Setup']/u:OOBE/u:$requiredOobeValue" `
            -Expected "true" `
            -NamespaceManager $namespaces `
            -Description "OOBE option $requiredOobeValue"
    }

    return [pscustomobject]@{
        schema = "openclaw.clean-windows.answer-file-proof/v1"
        valid = $true
        imageIndex = [int]$script:ExpectedImageIndex
        imageName = $script:ExpectedImageName
        architecture = "amd64"
        locale = "en-US"
        autoLogon = $false
        productKey = $false
    }
}

function Release-ComObject {
    param([object]$ComObject)

    if ($null -ne $ComObject -and [Runtime.InteropServices.Marshal]::IsComObject($ComObject)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($ComObject)
    }
}

function New-CleanWindowsAnswerIso {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$StagingPath,
        [Parameter(Mandatory = $true)]
        [string]$IsoPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot
    )

    if (-not (Test-CleanWindowsHost)) {
        throw "Answer ISO generation requires Windows."
    }

    $resolvedStagingPath = (Resolve-Path -LiteralPath $StagingPath -ErrorAction Stop).Path
    [void](Assert-CleanWindowsPathUnderRoot -Path $resolvedStagingPath -OwnedRoot $OwnedRoot)
    $answerFilePath = Join-Path $resolvedStagingPath "AutoUnattend.xml"
    if (-not (Test-Path -LiteralPath $answerFilePath -PathType Leaf)) {
        throw "Staging must contain AutoUnattend.xml at its root."
    }
    $resolvedIsoPath = Assert-CleanWindowsPathUnderRoot -Path $IsoPath -OwnedRoot $OwnedRoot
    if (Test-Path -LiteralPath $resolvedIsoPath) {
        throw "Answer ISO path already exists."
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedIsoPath) | Out-Null

    $fileSystemImage = $null
    $root = $null
    $resultImage = $null
    $imageStream = $null
    try {
        $fileSystemImage = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
        $fileSystemImage.ChooseImageDefaultsForMediaType(2)
        $fileSystemImage.FileSystemsToCreate = 3
        $fileSystemImage.VolumeName = "OPENCLAW_UNATTEND"
        $root = $fileSystemImage.Root
        $root.AddTree($resolvedStagingPath, $false)
        $resultImage = $fileSystemImage.CreateResultImage()
        $imageStream = $resultImage.ImageStream
        [OpenClaw.CleanWindows.ComStreamCopy]::Save($imageStream, $resolvedIsoPath)
    } catch {
        if (Test-Path -LiteralPath $resolvedIsoPath) {
            Remove-Item -LiteralPath $resolvedIsoPath -Force
        }
        throw
    } finally {
        Release-ComObject -ComObject $imageStream
        Release-ComObject -ComObject $resultImage
        Release-ComObject -ComObject $root
        Release-ComObject -ComObject $fileSystemImage
    }

    if ((Get-Item -LiteralPath $resolvedIsoPath).Length -le 0) {
        throw "IMAPI2 produced an empty answer ISO."
    }
    return $resolvedIsoPath
}

function Test-CleanWindowsAnswerIsoMount {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$IsoPath,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedAnswerFilePath,
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$Credential,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedComputerName,
        [ValidateRange(5, 120)]
        [int]$TimeoutSec = 30
    )

    foreach ($commandName in @("Mount-DiskImage", "Dismount-DiskImage", "Get-DiskImage", "Get-Volume")) {
        if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
            throw "Required Windows disk-image command is unavailable: $commandName"
        }
    }

    $resolvedIsoPath = (Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop).Path
    $expectedHash = Get-CleanWindowsFileSha256 -Path $ExpectedAnswerFilePath
    $mounted = $false
    try {
        Mount-DiskImage -ImagePath $resolvedIsoPath -StorageType ISO -Access ReadOnly -ErrorAction Stop | Out-Null
        $mounted = $true
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
        $volume = $null
        do {
            $volume = @(
                Get-DiskImage -ImagePath $resolvedIsoPath -ErrorAction Stop |
                    Get-Volume -ErrorAction SilentlyContinue |
                    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.DriveLetter) }
            ) | Select-Object -First 1
            if ($null -eq $volume) {
                Start-Sleep -Milliseconds 250
            }
        } while ($null -eq $volume -and [DateTime]::UtcNow -lt $deadline)

        if ($null -eq $volume) {
            throw "Mounted answer ISO did not receive a drive letter within $TimeoutSec seconds."
        }
        $mountedAnswerPath = "{0}:\AutoUnattend.xml" -f $volume.DriveLetter
        if (-not (Test-Path -LiteralPath $mountedAnswerPath -PathType Leaf)) {
            throw "Mounted answer ISO does not contain AutoUnattend.xml at its root."
        }
        if ((Get-CleanWindowsFileSha256 -Path $mountedAnswerPath) -cne $expectedHash) {
            throw "Mounted answer ISO content does not match the generated answer file."
        }
        Test-CleanWindowsAnswerFile `
            -Path $mountedAnswerPath `
            -ExpectedComputerName $ExpectedComputerName `
            -Credential $Credential | Out-Null

        return [pscustomobject]@{
            schema = "openclaw.clean-windows.answer-media-proof/v1"
            valid = $true
            mountedReadOnly = $true
            answerFileAtRoot = $true
            imageIndex = [int]$script:ExpectedImageIndex
            imageName = $script:ExpectedImageName
            secretIncludedInProof = $false
        }
    } finally {
        if ($mounted) {
            Dismount-DiskImage -ImagePath $resolvedIsoPath -ErrorAction Stop | Out-Null
        }
    }
}

function Test-CleanWindowsCredentialAuthenticationRejection {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [string]$Message,
        [AllowEmptyString()]
        [string]$FullyQualifiedErrorId,
        [AllowEmptyString()]
        [string]$Category
    )

    $messageIndicatesAuthenticationRejection = $Message -match (
        "(?i)(" +
        "the user name or password is incorrect|" +
        "unknown user name or bad password|" +
        "logon failure|" +
        "invalid credentials?|" +
        "credentials? (?:is|are) invalid|" +
        "authentication (?:has )?failed|" +
        "access is denied|" +
        "unauthorized" +
        ")"
    )
    if (-not $messageIndicatesAuthenticationRejection) {
        return $false
    }

    $errorIdIndicatesSessionAuthentication =
        $FullyQualifiedErrorId -match (
            "(?i)(authentication|credential|logon|" +
            "PSSessionOpenFailed|CreateRemoteRunspaceFailed)"
        )
    $categoryIndicatesAuthentication =
        $Category -in @("AuthenticationError", "SecurityError", "PermissionDenied")
    return [bool]($errorIdIndicatesSessionAuthentication -or $categoryIndicatesAuthentication)
}

function Set-CleanWindowsRestrictiveAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$Directory
    )

    if (-not (Test-CleanWindowsHost)) {
        throw "Credential ACL protection requires Windows."
    }

    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($Directory) {
        $existingAcl = New-Object Security.AccessControl.DirectorySecurity(
            $Path,
            [Security.AccessControl.AccessControlSections]::Owner
        )
    } else {
        $existingAcl = New-Object Security.AccessControl.FileSecurity(
            $Path,
            [Security.AccessControl.AccessControlSections]::Owner
        )
    }
    $existingOwnerSid = $existingAcl.GetOwner([Security.Principal.SecurityIdentifier])
    $systemSid = New-Object Security.Principal.SecurityIdentifier("S-1-5-18")
    $administratorsSid = New-Object Security.Principal.SecurityIdentifier("S-1-5-32-544")
    if ($Directory) {
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $inheritance = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    } else {
        $acl = New-Object Security.AccessControl.FileSecurity
        $inheritance = [Security.AccessControl.InheritanceFlags]::None
    }
    $acl.SetAccessRuleProtection($true, $false)
    # Elevated UAC creation can default ownership to Administrators, so set it deliberately.
    if ($existingOwnerSid.Value -cne $currentSid.Value) {
        $acl.SetOwner($currentSid)
    }
    foreach ($sid in @($currentSid, $systemSid, $administratorsSid)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        [void]$acl.AddAccessRule($rule)
    }
    $aclExtensions = [Type]::GetType(
        "System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl",
        $false
    )
    if ($Directory) {
        if ($null -eq $aclExtensions) {
            [IO.Directory]::SetAccessControl($Path, $acl)
        } else {
            $directoryInfo = [IO.DirectoryInfo]::new($Path)
            $setDirectoryAcl = $aclExtensions.GetMethod(
                "SetAccessControl",
                [Type[]]@([IO.DirectoryInfo], [Security.AccessControl.DirectorySecurity])
            )
            $setDirectoryAclArguments = [object[]]::new(2)
            $setDirectoryAclArguments[0] = $directoryInfo
            $setDirectoryAclArguments[1] = $acl.PSObject.BaseObject
            [void]$setDirectoryAcl.Invoke($null, $setDirectoryAclArguments)
        }
    } else {
        if ($null -eq $aclExtensions) {
            [IO.File]::SetAccessControl($Path, $acl)
        } else {
            $fileInfo = [IO.FileInfo]::new($Path)
            $setFileAcl = $aclExtensions.GetMethod(
                "SetAccessControl",
                [Type[]]@([IO.FileInfo], [Security.AccessControl.FileSecurity])
            )
            $setFileAclArguments = [object[]]::new(2)
            $setFileAclArguments[0] = $fileInfo
            $setFileAclArguments[1] = $acl.PSObject.BaseObject
            [void]$setFileAcl.Invoke($null, $setFileAclArguments)
        }
    }
}

function Assert-CleanWindowsRestrictiveAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $aclExtensions = [Type]::GetType(
        "System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl",
        $false
    )
    if (Test-Path -LiteralPath $Path -PathType Container) {
        if ($null -eq $aclExtensions) {
            $acl = [IO.Directory]::GetAccessControl($Path)
        } else {
            $directoryInfo = [IO.DirectoryInfo]::new($Path)
            $getDirectoryAcl = $aclExtensions.GetMethod(
                "GetAccessControl",
                [Type[]]@([IO.DirectoryInfo])
            )
            $getDirectoryAclArguments = [object[]]::new(1)
            $getDirectoryAclArguments[0] = $directoryInfo
            $acl = $getDirectoryAcl.Invoke($null, $getDirectoryAclArguments)
        }
    } else {
        if ($null -eq $aclExtensions) {
            $acl = [IO.File]::GetAccessControl($Path)
        } else {
            $fileInfo = [IO.FileInfo]::new($Path)
            $getFileAcl = $aclExtensions.GetMethod(
                "GetAccessControl",
                [Type[]]@([IO.FileInfo])
            )
            $getFileAclArguments = [object[]]::new(1)
            $getFileAclArguments[0] = $fileInfo
            $acl = $getFileAcl.Invoke($null, $getFileAclArguments)
        }
    }
    if (-not $acl.AreAccessRulesProtected) {
        throw "Credential ACL must have inheritance disabled."
    }
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -cne $currentSid) {
        throw "Credential ACL owner must be the current Windows user."
    }
    $allowedSids = @(
        $currentSid,
        "S-1-5-18",
        "S-1-5-32-544"
    )
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 3) {
        throw "Credential ACL must contain exactly the current user, SYSTEM, and Administrators."
    }
    foreach ($rule in $rules) {
        if (
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $allowedSids -notcontains $rule.IdentityReference.Value -or
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl
        ) {
            throw "Credential ACL contains an unexpected access rule."
        }
    }
}

function Protect-CleanWindowsOwnedDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot
    )

    $resolvedPath = Assert-CleanWindowsPathUnderRoot -Path $Path -OwnedRoot $OwnedRoot
    New-Item -ItemType Directory -Force -Path $resolvedPath | Out-Null
    Set-CleanWindowsRestrictiveAcl -Path $resolvedPath -Directory
    Assert-CleanWindowsRestrictiveAcl -Path $resolvedPath
    return $resolvedPath
}

function Write-CleanWindowsOwnedJsonFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot,
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [ValidateRange(1, 32)]
        [int]$Depth = 8
    )

    $resolvedOwnedRoot = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd("\")
    $resolvedPath = Assert-CleanWindowsPathUnderRoot -Path $Path -OwnedRoot $resolvedOwnedRoot
    if (
        -not [string]::Equals(
            (Split-Path -Parent $resolvedPath),
            $resolvedOwnedRoot,
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        throw "Owned JSON marker must be written directly beneath its owned root."
    }

    New-Item -ItemType Directory -Force -Path $resolvedOwnedRoot | Out-Null
    Set-CleanWindowsRestrictiveAcl -Path $resolvedOwnedRoot -Directory
    Assert-CleanWindowsRestrictiveAcl -Path $resolvedOwnedRoot

    $temporaryPath = "{0}.{1}.tmp" -f $resolvedPath, ([Guid]::NewGuid().ToString("N"))
    try {
        $json = $Value | ConvertTo-Json -Depth $Depth
        $utf8 = New-Object Text.UTF8Encoding($true)
        [IO.File]::WriteAllText($temporaryPath, $json, $utf8)
        Set-CleanWindowsRestrictiveAcl -Path $temporaryPath
        Assert-CleanWindowsRestrictiveAcl -Path $temporaryPath

        if ([IO.File]::Exists($resolvedPath)) {
            [IO.File]::Replace($temporaryPath, $resolvedPath, [NullString]::Value)
        } else {
            [IO.File]::Move($temporaryPath, $resolvedPath)
        }
    } finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }

    return $resolvedPath
}

function Export-CleanWindowsCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.PSCredential]$Credential,
        [Parameter(Mandatory = $true)]
        [string]$CredentialPath,
        [Parameter(Mandatory = $true)]
        [string]$MetadataPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot,
        [Parameter(Mandatory = $true)]
        [string]$VMName,
        [Parameter(Mandatory = $true)]
        [string]$OwnerId,
        [Parameter(Mandatory = $true)]
        [ValidateSet("setup", "final")]
        [string]$Kind
    )

    if (-not (Test-CleanWindowsHost)) {
        throw "DPAPI credential persistence requires Windows."
    }

    $resolvedCredentialPath = Assert-CleanWindowsPathUnderRoot -Path $CredentialPath -OwnedRoot $OwnedRoot
    $resolvedMetadataPath = Assert-CleanWindowsPathUnderRoot -Path $MetadataPath -OwnedRoot $OwnedRoot
    $credentialDirectory = Split-Path -Parent $resolvedCredentialPath
    if ((Split-Path -Parent $resolvedMetadataPath) -cne $credentialDirectory) {
        throw "Credential and metadata files must share one protected directory."
    }
    New-Item -ItemType Directory -Force -Path $credentialDirectory | Out-Null
    Set-CleanWindowsRestrictiveAcl -Path $credentialDirectory -Directory

    $Credential | Export-Clixml -LiteralPath $resolvedCredentialPath -Depth 4 -Force
    Set-CleanWindowsRestrictiveAcl -Path $resolvedCredentialPath
    $metadata = [ordered]@{
        schema = $script:CredentialSchema
        kind = $Kind
        vmName = $VMName
        ownerId = $OwnerId
        userName = $Credential.UserName
        createdUtc = [DateTime]::UtcNow.ToString("o")
        protection = "current-user-dpapi-clixml"
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedMetadataPath -Encoding UTF8
    Set-CleanWindowsRestrictiveAcl -Path $resolvedMetadataPath
    Assert-CleanWindowsRestrictiveAcl -Path $credentialDirectory
    Assert-CleanWindowsRestrictiveAcl -Path $resolvedCredentialPath
    Assert-CleanWindowsRestrictiveAcl -Path $resolvedMetadataPath
    return $resolvedCredentialPath
}

function Import-CleanWindowsCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$CredentialPath,
        [Parameter(Mandatory = $true)]
        [string]$MetadataPath,
        [Parameter(Mandatory = $true)]
        [string]$OwnedRoot,
        [Parameter(Mandatory = $true)]
        [string]$VMName,
        [Parameter(Mandatory = $true)]
        [string]$OwnerId,
        [ValidateSet("setup", "final")]
        [string]$ExpectedKind
    )

    $resolvedCredentialPath = Assert-CleanWindowsPathUnderRoot -Path $CredentialPath -OwnedRoot $OwnedRoot
    $resolvedMetadataPath = Assert-CleanWindowsPathUnderRoot -Path $MetadataPath -OwnedRoot $OwnedRoot
    if (-not (Test-Path -LiteralPath $resolvedCredentialPath -PathType Leaf)) {
        throw "DPAPI credential file was not found."
    }
    if (-not (Test-Path -LiteralPath $resolvedMetadataPath -PathType Leaf)) {
        throw "Credential metadata file was not found."
    }
    Assert-CleanWindowsRestrictiveAcl -Path (Split-Path -Parent $resolvedCredentialPath)
    Assert-CleanWindowsRestrictiveAcl -Path $resolvedCredentialPath
    Assert-CleanWindowsRestrictiveAcl -Path $resolvedMetadataPath

    $metadata = Get-Content -LiteralPath $resolvedMetadataPath -Raw | ConvertFrom-Json
    if (
        $metadata.schema -cne $script:CredentialSchema -or
        -not [string]::Equals([string]$metadata.vmName, $VMName, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$metadata.ownerId, $OwnerId, [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Credential metadata does not match the owned VM."
    }
    if (
        -not [string]::IsNullOrWhiteSpace($ExpectedKind) -and
        $metadata.kind -cne $ExpectedKind
    ) {
        throw "Credential metadata kind does not match '$ExpectedKind'."
    }
    $credential = Import-Clixml -LiteralPath $resolvedCredentialPath
    if ($credential -isnot [Management.Automation.PSCredential]) {
        throw "DPAPI credential file did not contain a PSCredential."
    }
    if ($credential.UserName -cne [string]$metadata.userName) {
        throw "Credential username does not match its metadata."
    }
    return $credential
}

Export-ModuleMember -Function @(
    "Assert-CleanWindowsPathUnderRoot",
    "Export-CleanWindowsCredential",
    "Get-CleanWindowsFileSha256",
    "Import-CleanWindowsCredential",
    "New-CleanWindowsAnswerFile",
    "New-CleanWindowsAnswerIso",
    "New-CleanWindowsTestCredential",
    "Protect-CleanWindowsOwnedDirectory",
    "Test-CleanWindowsAnswerFile",
    "Test-CleanWindowsAnswerIsoMount",
    "Test-CleanWindowsCredentialAuthenticationRejection",
    "Write-CleanWindowsOwnedJsonFile"
)
