# Changelog

All notable changes to the RustOrigin launcher are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are Git tags (`vX.Y.Z`).

## [1.0.2] - 2026-09-23

### Changed
- **Training Grounds** moved to the new dedicated server: the card now connects to
  `51.195.60.227:28015` (was `185.190.143.67:28015`), and its live player count queries the new host.

## [1.0.1] - 2026-09-23

### Changed
- Client download is now served from **`cdn.rustorigin.com`** (a Cloudflare R2 custom domain)
  instead of the throttled `pub-*.r2.dev` dev endpoint, so concurrent multi-GB downloads scale on
  the CDN with free egress (still resumable via HTTP Range). The client file and its SHA-256 are
  unchanged.

## [1.0.0] - 2026-09-22

Initial public release. Highlights:

### Added
- Single-file WPF launcher (`RustoriginLauncher.exe`, ~3 MB) - downloads, verifies, extracts and launches
  the Rust ("January 2021") client.
- **Mandatory SHA-256 verification** of the downloaded client (rejects any mismatch; Install is
  blocked without a configured hash).
- **Resumable** downloads (HTTP Range, auto-retry) over TLS 1.2/1.3; free-disk-space check before
  extraction. The hero button **pauses/resumes** an in-progress download (PAUSE while downloading,
  RESUME to continue from the saved partial), and **PLAY is hidden until the client is installed**.
- **Self-update** from GitHub Releases, verified against the release `SHA256SUMS.txt`.
- **Discord Rich Presence** (optional, `DiscordAppId`): shows "RUSTORIGIN - In the launcher / In game"
  on the player's Discord, via a dependency-free Discord IPC client (`src/DiscordRpc.cs`).
- **Rounded, frameless native window** via `WindowChrome`: drag from anywhere, resize, maximize,
  Aero Snap, taskbar, system menu, circular glass settings/minimize/close buttons (maximize via double-click/Aero Snap); content scales on resize. The 32px
  corners come from a rounded window region (not a layered window), so the **native Windows
  minimize/maximize/restore animations** are preserved.
- **Cross-fading night screenshot background** (three embedded JPEGs, ~7s switch) with **carousel
  dot indicators** at the bottom (click a dot to jump to a screenshot).
- **Vertical server cards** with per-server cover art, a **"click to join" hover overlay**, and
  **live A2S player counts** (green/red status dot + `players/max`, queried over Steam A2S when a
  card's args include `+connect host:port`, refreshed ~60s).
- **In-app settings panel** (caption **gear** button): a glass overlay with the per-user toggles
  (background slideshow, minimize-in-game, auto-update, Discord Rich Presence), the PLAY launch args,
  and "Open log folder" / "Re-import Rust config" actions. Closes on X / Done / backdrop / Esc; prefs
  remain editable in `prefs.cfg`.
- **Import existing Rust keybinds (first run):** on the first completed install, the launcher finds the
  player's existing Steam Rust `cfg` folder (default paths, registry Steam path, and all
  `libraryfolders.vdf` libraries) and offers to copy it in so their keybinds carry over. One-time prompt
  (`RustConfigImported` pref); graphics settings are noted as not carrying to the January-2021 build.
- **Social links** bar above the server cards: configurable `Social=platform|url` icons with
  built-in Discord / YouTube / TikTok brand marks (inline vectors, no extra assets).
- Two installers: **NSIS setup `.exe`** (Program Files) and **per-user MSI**.
- CI: build-check (compile + updater-parsing tests on every push/PR) and a tag-triggered release
  workflow (builds exe + installers, checksums, GitHub Release).
- Docs: `README`, `CLAUDE.md`, `CONTRIBUTING.md`, `SECURITY.md`, MIT `LICENSE`.

### Fixed
- The main button no longer shows **IN-GAME** for an unrelated `RustClient.exe` running elsewhere
  on the PC. "Running" is now scoped to the client this launcher started or the exe under its own
  `InstallDir`, so an un-installed launcher can't falsely report the game as running.
- **Broken/incomplete installs are caught before launch.** A structural completeness check (the
  `<exe>_Data` folder is present and non-empty and `UnityPlayer.dll` sits next to the exe, or our
  post-extract marker exists) means a half-extracted client no longer launches into a Unity error.
  Such an install shows a **REPAIR** button + a hint instead of PLAY, and clicking a server card
  won't launch a broken client. Cheap sanity check, not cryptographic verification.

### Notes
- The exe and installers are **unsigned** until a code-signing certificate is added (SmartScreen
  shows an unknown-publisher warning on first run).
- The client hash and update checksums are **not yet signed** (transport + hash trust only);
  signed-manifest verification is planned.
