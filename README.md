<div align="center">

# Rustorigin Launcher

**A polished, self-contained Windows launcher for a private Rust server**. It downloads, verifies,
and launches the game client, keeps itself up to date, and ships as a single ~4 MB `.exe` with no
runtime or installer required.

[![Latest release](https://img.shields.io/github/v/release/RUSTORIGIN/launcher?sort=semver&color=7c3aed)](https://github.com/RUSTORIGIN/launcher/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/RUSTORIGIN/launcher/build-check.yml?branch=main&label=build)](https://github.com/RUSTORIGIN/launcher/actions/workflows/build-check.yml)
[![Downloads](https://img.shields.io/github/downloads/RUSTORIGIN/launcher/total?color=4ade80)](https://github.com/RUSTORIGIN/launcher/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d6)

![Rustorigin Launcher](docs/screenshot.png)

</div>

---

## Highlights

One executable, no runtime, no installer. It fetches your hosted client, **verifies it by SHA-256
before it ever touches disk**, installs and launches it, shows live server status, and **updates
itself**, behind a clean, custom WPF interface built entirely in code (no XAML).

- **Single self-contained exe**: code-only WPF on .NET Framework 4.x (built into Windows 10/11).
  UI, screenshots, logo, fonts, and default config are embedded; nothing else ships.
- **Verified, resumable downloads**: pulls your `RustClient.zip` over TLS 1.2, resumes dropped
  transfers via HTTP Range, and **rejects any download whose SHA-256 doesn't match** before extract.
- **One-click install & play**: extracts to a per-user folder and launches, with live progress
  and a one-time offer to import your existing Steam Rust keybinds.
- **Live server cards**: cover-image tiles with a status dot, name, and a live player-count bar
  (Steam A2S, refreshed ~60 s); click a card to connect.
- **Self-update**: checks GitHub Releases on launch; when a newer, checksum-verified build exists
  it prompts to update, then downloads (with a progress bar), verifies, swaps itself in, and restarts.
- **Native, frameless window**: rounded, draggable anywhere, resize / maximize / Aero Snap, tray
  integration (close hides to tray), Discord Rich Presence, and a clean settings panel.

## Install

Grab the latest [**release**](https://github.com/RUSTORIGIN/launcher/releases/latest):

| Asset | Use |
|-------|-----|
| **`RustoriginLauncher.exe`** | The single-file launcher: portable, just run it. |
| **`RustoriginLauncher-Setup.msi`** | Installer with Start Menu + Desktop shortcuts (per-user, no admin). Stable name for a permanent website download link. |

Every asset is listed in `SHA256SUMS.txt`. The exe is unsigned, so SmartScreen may warn on first
run (*More info → Run anyway*). **Requires Windows 10/11**; no .NET download needed.

## Configuration

The launcher reads a simple `Key=Value` file, `config/launcher.cfg`, embedded at build time. A
`launcher.cfg` placed **next to the exe** overrides the defaults at runtime (handy for a test
server). Key settings:

| Key | Meaning |
|-----|---------|
| `DownloadUrl` | Direct link to `RustClient.zip`. **Required.** |
| `Sha256` | Expected SHA-256 of `RustClient.zip`. **Required**: installs are blocked without it. |
| `InstallDir` | Where the client installs (default `C:\RustOrigin`; blank = `.\Rust`). |
| `LaunchExe` | Client executable the Play button runs (default `RustClient.exe`). |
| `Title` / `Tagline` / `Player` | Hero title, description line, and the displayed player name. |
| `UpdateRepo` | `owner/repo` checked for self-updates (public repo; blank disables). |
| `DiscordAppId` | Discord Application ID for Rich Presence (blank disables). |
| `Server` | Repeatable card: `Tag\|Name\|launch args\|players\|cover`. A `+connect HOST:PORT` in the args enables the live A2S count. |
| `Social` | Repeatable link: `platform\|url` (built-in icons: `discord`, `youtube`, `tiktok`). |

See [`config/launcher.cfg`](config/launcher.cfg) for the full, commented reference.

## Hosting the client

For server owners distributing the game:

1. **Package** the client folder into `RustClient.zip` (prints its SHA-256, excludes junk):
   ```powershell
   .\scripts\package_client.ps1
   ```
2. **Upload** it somewhere serving a **direct** download link (Cloudflare R2, S3, your web server…).
3. **Set** `DownloadUrl` and `Sha256` in `config/launcher.cfg`, then cut a release (below).

## Releasing & self-update

Push a version tag and CI does everything:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

The **Release** workflow builds the launcher, NSIS setup, and MSI; generates `SHA256SUMS.txt`;
attests build provenance; and publishes a GitHub Release. Any launcher with `UpdateRepo` pointed
here then detects the new version, prompts to update, verifies the download against
`SHA256SUMS.txt`, and self-replaces.

## Build from source

Uses the .NET Framework `csc` that ships with Windows 10/11 (no SDK or NuGet).

```powershell
.\scripts\build.bat                        # quick compile check
.\scripts\make_release.ps1 -Version 1.0.0  # -> release\RustoriginLauncher.exe (single file)
```

Installers are optional and need WiX v5 + NSIS:

```powershell
.\scripts\build_installer_exe.ps1 -Version 1.0.0   # NSIS setup .exe
.\scripts\build_msi.ps1 -Version 1.0.0             # MSI
```

## How it works

- **One file, no dependencies.** `WpfLauncher.cs` is a single code-only WPF window (no XAML),
  compiled directly with `csc`. Media, fonts, and `launcher.cfg` are embedded and unpacked at first
  run to `%LOCALAPPDATA%\RustOrigin\`.
- **Safety first.** Downloads are size/ETag-resumable and SHA-256-verified before extraction;
  self-updates are verified against the release checksums before the exe is swapped.
- **No servers of our own.** Live player counts come straight from Steam A2S; updates come straight
  from GitHub Releases.

```
src/        WpfLauncher.cs (the launcher) + UpdateParsing / A2S / DiscordRpc helpers
assets/     embedded media (screenshots, logo, covers) + bundled fonts
config/     launcher.cfg
scripts/    build, release, installer, and client-packaging scripts
docs/       styling guide + notes
.github/    CI: build-check + release workflows
```

## Contributing

Contributions are welcome. `src/WpfLauncher.cs` is **code-only WPF** (no XAML), kept self-contained
so it compiles with `csc`. Read the [**Styling Guide**](docs/STYLING.md) before changing UI. CI runs
a compile check and parser tests on every pull request; releases are cut from version tags.

## License

Code is released under the [MIT License](LICENSE). Bundled fonts (Montserrat and Poppins) are under
the SIL Open Font License; see [`assets/fonts/`](assets/fonts).
