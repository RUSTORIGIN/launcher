# Changelog

All notable changes to the RustOrigin launcher are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are Git tags (`vX.Y.Z`).

## [Unreleased]

Initial launcher (not yet tagged/published). Highlights:

### Added
- Single-file WPF launcher (`RustOrigin.exe`, ~3 MB) - downloads, verifies, extracts and launches
  the Rust ("January 2021") client.
- **Mandatory SHA-256 verification** of the downloaded client (rejects any mismatch; Install is
  blocked without a configured hash).
- **Resumable** downloads (HTTP Range, auto-retry) over TLS 1.2/1.3; free-disk-space check before
  extraction.
- **Self-update** from GitHub Releases, verified against the release `SHA256SUMS.txt`.
- **Rounded, frameless native window** via `WindowChrome`: drag from anywhere, resize, maximize,
  Aero Snap, taskbar, system menu, circular glass min/max/close buttons; content scales on resize. The 32px
  corners come from a rounded window region (not a layered window), so the **native Windows
  minimize/maximize/restore animations** are preserved.
- **Cross-fading screenshot background** (four embedded JPEGs, ~7s switch) with **carousel dot
  indicators** at the bottom (click a dot to jump to a screenshot).
- **Vertical server cards** with per-server cover art, a **"click to join" hover overlay**, and
  **live A2S player counts** (green/red status dot + `players/max`, queried over Steam A2S when a
  card's args include `+connect host:port`, refreshed ~60s).
- **Social links** row under the hero PLAY/INSTALL buttons: configurable `Social=platform|url`
  icons with built-in Discord / YouTube / TikTok brand marks (inline vectors, no extra assets).
- Two installers: **NSIS setup `.exe`** (Program Files) and **per-user MSI**.
- CI: build-check (compile + updater-parsing tests on every push/PR) and a tag-triggered release
  workflow (builds exe + installers, checksums, GitHub Release).
- Docs: `README`, `CLAUDE.md`, `CONTRIBUTING.md`, `SECURITY.md`, MIT `LICENSE`.

### Fixed
- The main button no longer shows **IN-GAME** for an unrelated `RustClient.exe` running elsewhere
  on the PC. "Running" is now scoped to the client this launcher started or the exe under its own
  `InstallDir`, so an un-installed launcher can't falsely report the game as running.

### Notes
- The exe and installers are **unsigned** until a code-signing certificate is added (SmartScreen
  shows an unknown-publisher warning on first run).
- The client hash and update checksums are **not yet signed** (transport + hash trust only);
  signed-manifest verification is planned.
