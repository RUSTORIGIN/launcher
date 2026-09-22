# Rustorigin Launcher

A polished, self-contained Windows launcher for a private Rust server. It downloads, verifies, and
launches the game client, keeps itself up to date, and ships as a single ~4 MB `.exe` that runs on
any Windows 10/11 — no runtime or installer required.

![Rustorigin Launcher](docs/screenshot.png)

## Features

- **Single self-contained exe** — code-only WPF on .NET Framework 4.x (built into Windows 10/11).
  The UI, screenshots, logo, fonts, and default config are embedded; nothing else ships.
- **Verified, resumable downloads** — pulls your hosted `RustClient.zip` over TLS 1.2, resumes
  dropped transfers via HTTP Range, and **rejects any download whose SHA-256 doesn't match** before
  it is ever extracted or launched.
- **One-click install & play** — extracts to a per-user folder and launches the client, with live
  progress/speed and a one-time offer to import your existing Steam Rust keybinds.
- **Live server cards** — cover-image tiles with a status dot, name, and a live player-count bar
  (Steam A2S, refreshed ~60 s); click a card to connect.
- **Self-update** — on launch it checks GitHub Releases; when a newer, checksum-verified build
  exists it prompts you to update, then downloads (with a progress bar), verifies, swaps itself in,
  and restarts.
- **Native, frameless window** — rounded, draggable from anywhere, resize / maximize / Aero Snap,
  a tray icon (close hides to tray), Discord Rich Presence, and a clean settings panel.

## Install

Download from the latest [release](../../releases/latest):

| Asset | Use |
|-------|-----|
| `RustoriginLauncher.exe` | The single-file launcher — portable, just run it. |
| `RustoriginLauncher-<version>-x64.exe` | NSIS setup (Start Menu + Desktop shortcuts). |
| `RustoriginLauncher-<version>.msi` | MSI installer (per-user, no admin). |

Every asset is listed in `SHA256SUMS.txt`. The exe is unsigned, so SmartScreen may warn on first
run (*More info → Run anyway*).

## Configuration

The launcher reads a simple `Key=Value` file, `config/launcher.cfg`, embedded at build time. A
`launcher.cfg` placed **next to the exe** overrides the embedded defaults at runtime (handy for a
test server). Key settings:

| Key | Meaning |
|-----|---------|
| `DownloadUrl` | Direct link to `RustClient.zip`. **Required.** |
| `Sha256` | Expected SHA-256 of `RustClient.zip`. **Required** — installs are blocked without it. |
| `InstallDir` | Where the client installs (default `C:\RustOrigin`; blank = `.\Rust`). |
| `LaunchExe` | Client executable the Play button runs (default `RustClient.exe`). |
| `Title` / `Tagline` / `Player` | Hero title, description line, and the displayed player name. |
| `UpdateRepo` | `owner/repo` checked for launcher self-updates (public repo; blank disables). |
| `DiscordAppId` | Discord Application ID for Rich Presence (blank disables). |
| `Server` | Repeatable server card: `Tag\|Name\|launch args\|players\|cover`. A `+connect HOST:PORT` in the args enables the live A2S player count. |
| `Social` | Repeatable social link: `platform\|url` (built-in icons: `discord`, `youtube`, `tiktok`). |

See `config/launcher.cfg` for the full, commented reference.

## Hosting the client (server owners)

1. **Package** your client folder into `RustClient.zip`:
   ```powershell
   .\scripts\package_client.ps1
   ```
   It prints the SHA-256 and excludes anything that must never ship (`temp\`, `maps\`, most of
   `cfg\`, and junk files).
2. **Upload** the zip somewhere that serves a **direct** download link (Cloudflare R2, S3, your web
   server, …).
3. **Set** `DownloadUrl` and `Sha256` in `config/launcher.cfg`, then cut a release (below).

## Releasing (self-update)

Push a version tag and CI does the rest:

```bash
git tag v1.0.2 && git push origin v1.0.2
```

The **Release** workflow builds the launcher exe, the NSIS setup, and the MSI; generates
`SHA256SUMS.txt`; attests build provenance; and publishes a GitHub Release. Any launcher with
`UpdateRepo` pointed at this repo then detects the new version, prompts the player to update,
verifies the download against `SHA256SUMS.txt`, and self-replaces.

## Build from source

Uses the .NET Framework `csc` that ships with Windows 10/11 — no SDK or NuGet needed.

```powershell
.\scripts\build.bat                        # quick compile check
.\scripts\make_release.ps1 -Version 1.0.1  # -> release\RustoriginLauncher.exe (single file)
```

Building the installers is optional and requires WiX v5 and NSIS:

```powershell
.\scripts\build_installer_exe.ps1 -Version 1.0.1   # NSIS setup .exe
.\scripts\build_msi.ps1 -Version 1.0.1             # MSI
```

## Project layout

```
src/        WpfLauncher.cs (the launcher) + UpdateParsing / A2S / DiscordRpc helpers
assets/     embedded media (screenshots, logo, covers) + bundled fonts
config/     launcher.cfg
scripts/    build, release, installer, and client-packaging scripts
docs/       styling guide + notes
.github/    CI: build-check + release workflows
```

## Contributing

`src/WpfLauncher.cs` is **code-only WPF** (no XAML), kept self-contained so it compiles with `csc`.
Read the [Styling Guide](docs/STYLING.md) before changing UI. CI runs a compile check on every pull
request; releases are cut from version tags.

## License

Code is released under the [MIT License](LICENSE). Bundled fonts (Montserrat and Poppins) are under
the SIL Open Font License; see `assets/fonts/`.
