# Windows MSI

Use PowerShell 7 and the WiX CLI (tested with 5.0.2). First stage the complete Windows x64 portable distribution, including the self-contained RsyncDeck executable, `engine`, `licenses`, and the third-party notices. Install the matching WiX UI extension once, then build:

```powershell
wix extension add -g WixToolset.UI.wixext/5.0.2
./installer/Build-Msi.ps1 -PortableDirectory ./release/RsyncDeck-1.0.0-win-x64 -OutputPath ./release/RsyncDeck-1.0.0-win-x64-setup.msi
```

The MSI embeds that payload and installs for the current Windows user, with a choice of installation folder. The installation folder includes an Uninstall RsyncDeck shortcut to Windows Installer. Sessions are stored separately from the application files and are retained on uninstall. When distributing the installer, offer the same third-party source archive as for the portable ZIP.
