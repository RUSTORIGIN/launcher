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
  Aero Snap, taskbar, system menu, custom min/max/close buttons; content scales on resize.
- **Cross-fading screenshot background** (four embedded JPEGs, ~7s switch).
- **Vertical server cards** with per-server cover art, and **live A2S player counts** (green/red
  status dot + `players/max`, queried over Steam A2S when a card's args include `+connect host:port`,
  refreshed ~60s).
- Two installers: **NSIS setup `.exe`** (Program Files) and **per-user MSI**.
- CI: build-check (compile + updater-parsing tests on every push/PR) and a tag-triggered release
  workflow (builds exe + installers, checksums, GitHub Release).
- Docs: `README`, `CLAUDE.md`, `CONTRIBUTING.md`, `SECURITY.md`, MIT `LICENSE`.

### Notes
- The exe and installers are **unsigned** until a code-signing certificate is added (SmartScreen
  shows an unknown-publisher warning on first run).
- The client hash and update checksums are **not yet signed** (transport + hash trust only);
  signed-manifest verification is planned.
