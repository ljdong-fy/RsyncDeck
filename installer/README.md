# Windows MSI

Use PowerShell 7 and the WiX CLI (tested with 5.0.2). First stage the complete Windows x64 portable distribution, including the self-contained RsyncDeck executable, `engine`, `licenses`, and the third-party notices. Then run:

```powershell
./installer/Build-Msi.ps1 -PortableDirectory ./release/RsyncDeck-1.0.0-win-x64 -OutputPath ./release/RsyncDeck-1.0.0-win-x64-setup.msi
```

The MSI embeds that payload and installs for the current Windows user. Sessions are stored separately from the application files and are retained on uninstall. When distributing the installer, offer the same third-party source archive as for the portable ZIP.
