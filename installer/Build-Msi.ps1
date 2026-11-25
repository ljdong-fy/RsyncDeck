param(
    [Parameter(Mandatory)][string]$PortableDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Wix = 'wix',
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part version.' }
$portable = (Resolve-Path -LiteralPath $PortableDirectory).Path
$icon = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../src/assets/RsyncDeck.ico')).Path
foreach ($required in @('RsyncDeck.exe','engine/usr/bin/rsync.exe','engine/usr/bin/ssh.exe','THIRD-PARTY.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $portable $required) -PathType Leaf)) { throw "Missing payload: $required" }
}
$xml = [System.Text.StringBuilder]::new()
function Add-Line([string]$text) { $null = $xml.AppendLine($text) }
function Escape([string]$text) { [Security.SecurityElement]::Escape($text) }
function Id([string]$relative) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant()))).Substring(0, 24)
}
function ComponentGuid([string]$relative) {
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(('RsyncDeck MSI payload:' + $relative.ToLowerInvariant())))
    [Guid]::new([byte[]]($bytes[0..15])).ToString()
}
function Emit-Directory([string]$path, [string]$directoryId) {
    $entries = Get-ChildItem -LiteralPath $path | Sort-Object Name
    Add-Line "<Component Id='Folder_$directoryId' Guid='*'>"
    Add-Line "<RemoveFolder Id='Remove_$directoryId' On='uninstall' />"
    Add-Line "<RegistryValue Root='HKCU' Key='Software\RsyncDeck\Installer' Name='Folder_$directoryId' Type='integer' Value='1' KeyPath='yes' />"
    Add-Line '</Component>'
    foreach ($entry in $entries) {
        $relative = $entry.FullName.Substring($portable.Length + 1)
        $hash = Id $relative
        if ($entry.PSIsContainer) {
            Add-Line "<Directory Id='D_$hash' Name='$(Escape $entry.Name)'>"
            Emit-Directory $entry.FullName "D_$hash"
            Add-Line '</Directory>'
        } else {
            Add-Line "<Component Id='C_$hash' Guid='$(ComponentGuid $relative)'>"
            Add-Line "<File Id='F_$hash' Source='$(Escape $entry.FullName)' />"
            Add-Line "<RegistryValue Root='HKCU' Key='Software\RsyncDeck\Installer' Name='C_$hash' Type='integer' Value='1' KeyPath='yes' />"
            Add-Line '</Component>'
        }
    }
}
Add-Line '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
Add-Line "<Package Name='RsyncDeck' Manufacturer='RsyncDeck' Version='$Version' UpgradeCode='9C0DFB8C-8B5F-431C-8A99-1AD7D6E3BD55' Scope='perUser' Language='1033'>"
Add-Line '<MajorUpgrade AllowSameVersionUpgrades="yes" DowngradeErrorMessage="A newer version of RsyncDeck is already installed." />'
Add-Line '<UIRef Id="RsyncDeckInstallerUI" />'
Add-Line '<MediaTemplate EmbedCab="yes" />'
Add-Line "<Icon Id='RsyncDeck.ico' SourceFile='$(Escape $icon)' />"
Add-Line '<Property Id="ARPPRODUCTICON" Value="RsyncDeck.ico" />'
Add-Line '<Property Id="ARPURLINFOABOUT" Value="https://github.com/ljdong-fy/RsyncDeck" />'
Add-Line '<StandardDirectory Id="LocalAppDataFolder"><Directory Id="INSTALLFOLDER" Name="RsyncDeck App">'
Emit-Directory $portable 'INSTALLFOLDER'
Add-Line @'
<Component Id='UninstallShortcut' Guid='*'>
  <Shortcut Id='RsyncDeckUninstall' Name='Uninstall RsyncDeck' Target='[SystemFolder]msiexec.exe' Arguments='/x [ProductCode]' Icon='RsyncDeck.ico' />
  <RegistryValue Root='HKCU' Key='Software\RsyncDeck\Installer' Name='UninstallShortcut' Type='integer' Value='1' KeyPath='yes' />
</Component>
'@
Add-Line '</Directory></StandardDirectory>'
Add-Line @"
<StandardDirectory Id='ProgramMenuFolder'>
  <Component Id='StartMenuShortcut' Guid='*'>
    <Shortcut Id='RsyncDeckShortcut' Name='RsyncDeck' Target='[INSTALLFOLDER]RsyncDeck.exe' WorkingDirectory='INSTALLFOLDER' Icon='RsyncDeck.ico' />
    <RegistryValue Root='HKCU' Key='Software\RsyncDeck\Installer' Name='StartMenuShortcut' Type='integer' Value='1' KeyPath='yes' />
  </Component>
</StandardDirectory>
<Feature Id='Application' Title='RsyncDeck' Level='1'>
  <ComponentGroupRef Id='ApplicationComponents' />
  <ComponentRef Id='StartMenuShortcut' />
  <ComponentRef Id='UninstallShortcut' />
</Feature>
<ComponentGroup Id='ApplicationComponents'>
"@
foreach ($match in [regex]::Matches($xml.ToString(), "<Component Id='((?:Folder|C)_[^']+)'")) {
    Add-Line "<ComponentRef Id='$($match.Groups[1].Value)' />"
}
Add-Line '</ComponentGroup></Package></Wix>'
$source = Join-Path ([IO.Path]::GetTempPath()) ('RsyncDeck-' + [guid]::NewGuid() + '.wxs')
try {
    [IO.File]::WriteAllText($source, $xml.ToString())
    & $Wix build -arch x64 -culture en-US -pdbtype none -ext WixToolset.UI.wixext -o $OutputPath $source (Join-Path $PSScriptRoot 'InstallerUI.wxs')
    if ($LASTEXITCODE) { throw "WiX build failed: $LASTEXITCODE" }
} finally { Remove-Item -LiteralPath $source -ErrorAction SilentlyContinue }
