# RsyncDeck for Windows

[Download the Windows x64 installer](https://github.com/ljdong-fy/RsyncDeck/releases/tag/v1.0.0) · [简体中文](README.md)

RsyncDeck is a graphical interface for rsync. Use it to sync folders or individual files between local drives, over SSH, or through an rsync module.

## Getting started

1. Open `RsyncDeck-1.0.0-win-x64-setup.msi`, choose an installation folder, and launch RsyncDeck from the Start menu. The .NET Desktop Runtime, rsync, and OpenSSH are included. To uninstall, open **Uninstall RsyncDeck** in the installation folder. Saved sessions are retained.
2. New installations pick the bundled rsync automatically. Upgrades retain a valid custom rsync path; select **File → Use bundled rsync** to switch back. SSH connections still require non-interactive credentials and a verified host key. The included `ssh.exe` and `ssh-keygen.exe` are under `engine\usr\bin`; OpenSSH uses the Windows user's `.ssh` configuration.
3. Enter the source and destination. For example, use `D:\Assets` and `user@server.example:/data/assets`. At least one side must be local. In single-file mode, the destination remains a folder.
4. Select **Preview sync** to preview changes without writing to the destination. Use **Sync and verify** when ready. **Sync verified** means rsync succeeded and a read-only check found no remaining changes under the current sync options. This check computes checksums only when requested by the sync options. For a standalone check without writing to the destination, choose **File → Verify file contents**.

RsyncDeck first follows the Windows display language: Chinese locales default to Simplified Chinese; other locales default to English. Use **Language → English**, **简体中文**, or **Use system language** to change it. Your preference is saved, and any edits currently in the form are kept when the language changes.

## Options and sessions

- Select a saved session and click **Set as startup session** to load it next time you open the app. Sync still starts manually. Choose **Sessions → Start with a blank session** to clear the preference.
- **Reset options** restores the current form’s sync options while retaining the session name, source, and destination. Click **Save session** to persist the changes.
- Select **Open log folder** to find the latest job log. A new job overwrites `latest.log`. The app clears it on exit, or at the next start after an unexpected shutdown.

- **Basic options** include timestamp and permission preservation, file skip rules, progress, and mirror deletion. When mirror deletion is enabled, sync deletes destination-only items automatically. Use Preview sync to review planned deletions.
- **Advanced options** cover checksums, compression, backup, partial files, SSH port, and additional rsync options. For example: `--exclude=*.tmp --bwlimit=4096 --stats`. Include and exclude rules also apply to verification. Run mirror deletion and backup separately.
- **Extra options** includes single-file mode and local Windows CMD commands. Pre/post-sync commands run only when enabled for an actual sync, never during simulation or read-only verification. The post-sync command runs after transfer and before the post-sync check.
- Choose **Sessions → Migrate local Grsync sessions** to copy the current Windows user’s Grsync sessions, paths, settings, extra rsync options, and local commands with their original enabled states. Existing sessions with the same name are kept. Migration runs no syncs or commands.
- Use **Import…** for other Grsync `.ini` or RsyncDeck JSON files. This keeps command text but disables command execution and deletion. Exports contain paths and rsync options and omit local commands.

Settings are stored in `%LOCALAPPDATA%\RsyncDeck`; the temporary log is stored in `%TEMP%\RsyncDeck\logs`. The app cleans up automatically generated logs from earlier versions. Canceling stops the job but does not roll back files already transferred. Raw rsync messages or Windows dialogs may use their own language.

## Transfer progress

With **Basic options → Show transfer progress** on, **Sync and verify** runs a read-only scan before displaying per-file and overall transfer progress. The overall percentage estimates progress from expected file sizes and falls back to an unknown total when the estimate becomes unreliable. The transfer can only show 100% after a successful rsync exit; content verification is a separate stage. Simulations and read-only verification show their current stage and final result. Turning off transfer progress skips the scan for estimating totals; mirror deletion still scans first to cap the number of deletions.

## Connection & proxy

Choose SOCKS5 or HTTP CONNECT under **Connection & proxy**. Enter the address and port displayed by your proxy app (for example, `127.0.0.1:1080`), then save the session. The default is a direct connection. This setting requires the bundled rsync and applies to simulation, transfer, and verification for SSH sessions. It supports proxies without username/password authentication. It does not apply to local sync or rsync modules.

When redistributing the MSI installer, offer the companion `RsyncDeck-1.0.0-third-party-sources.zip` on the same download page. End users do not need to download that archive. See `THIRD-PARTY.md` for component provenance and versions.
