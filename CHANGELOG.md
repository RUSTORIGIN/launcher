# Changelog

All notable changes to the RustOrigin launcher are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions are Git tags (`vX.Y.Z`).

## [1.1.0] - 2026-10-01

### Added
- **Client updates without re-downloading the game.** When the server moves to a newer client build
  (`ClientVersion=` in `launcher.cfg`), an installed client shows **UPDATE** instead of PLAY and is
  upgraded in place from a **delta pack**: only the data the player doesn't already have is
  downloaded (about 1.5 GB instead of 11 GB for Jan -> Apr 2021). The pack is SHA-256-verified like
  the full client (`PatchSha256=`), every rebuilt file is verified again before it replaces the old
  one, and the installed client is left untouched if anything doesn't match - the launcher then
  falls back to the full download by itself. Pausing, resuming and an interrupted update are handled.
- `scripts\make_client_patch.ps1` builds the pack from the previous and the new client folders.
- `ClientVersion`, `PatchUrl`, `PatchSha256` and `PatchProbe` keys in `launcher.cfg`.

### Fixed
- **Error messages are visible again.** "Download failed", the integrity-check failure and the
  uninstall error were wiped the instant they appeared; they now stay under the button until your
  next action. Short notices ("Launching...", "Client not installed - click Install first.") stay
  for a few seconds.
- **A leftover download of another build is never installed.** A client zip or update pack kept on
  disk by an interrupted install is reused only if it is the file the launcher currently expects;
  otherwise it is deleted and downloaded again.
- Pausing while the game is being extracted no longer discards the verified download.
- A download the server does not have (HTTP 404) now fails immediately with a clear message instead
  of retrying for several minutes.

### Changed
- **Double-clicking the launcher no longer maximizes it.** (Dragging it to the top of the screen
  still does.)
- **Reworked settings panel.** Everything is now one list of rows in two groups. **Game** shows
  which build you have and whether it is up to date, where the game is installed and how much
  space it uses (with an Open button), the minimize-while-playing switch, and Uninstall.
  **Launcher** has Discord Rich Presence and the data folder. The loose buttons and the "Done"
  button are gone - close with the X, a click outside, or Esc.
- **Server cards show their cover image sharp** (the slight blur is gone), and Training Grounds has
  a new cover.
- The launcher now names the April 2021 build ("APRIL UPDATE 2021", tagline, Discord presence).
- The install marker now records which client build is installed.
- `package_client.ps1` and the pack builder share one exclusion list (`scripts\client_filter.ps1`).
  It now also leaves out `EasyAntiCheat\`, the root `Rust.exe` and the launcher's own files, so a
  Steam depot folder or an installed client folder can be packaged as-is.

## [1.0.4] - 2026-09-29

### Added
- **Choose the install folder.** The first INSTALL opens a folder picker (pre-selecting the
  launcher's own drive, e.g. `F:\Rustorigin`) instead of always installing to `C:\Rustorigin`. The
  game goes into a `Rustorigin` subfolder of the pick, and the choice is remembered until
  "Uninstall client".

### Changed
- When the game is installed on another drive, the ~10 GB download buffer goes on that drive too
  (`<install>\_download`), so C: no longer needs the free space.

### Fixed
- **"Failed to load il2cpp".** If `GameAssembly.dll` or `global-metadata.dat` goes missing after
  install (usually antivirus quarantine), the launcher now shows REPAIR with an antivirus hint
  instead of PLAY, and logs the missing file.

## [1.0.3] - 2026-09-23

### Removed
- **Importing existing Steam Rust keybinds.** The launcher no longer offers to copy the player's
  Steam Rust `cfg` folder after an install or at startup, and the "Re-import Rust config" button is
  gone from the settings panel.

## [1.0.2] - 2026-09-23

### Added
- **Parallel downloads.** The client now downloads over 6 connections at once (32 MB HTTP Range
  chunks written into a preallocated file), which is much faster for players whose ISP throttles each
  connection (about 2x on a normal line in testing). Pause/resume and restarts keep every finished
  chunk (tracked in `RustClient.zip.part.chunks`); partials from older versions still resume. The
  full SHA-256 check is unchanged. Falls back to a single connection automatically if a server ignores
  Range. Configurable with `DownloadConnections=` (1-16) in `launcher.cfg`.

### Changed
- **Training Grounds** moved to the new dedicated server: the card now connects to
  `51.195.60.227:28015` (was `185.190.143.67:28015`), and its live player count queries the new host.

### Fixed
- **"Uninstall client" no longer deletes the launcher's own files.** The installer and the client both
  default to `C:\Rustorigin`, so removing the client also deleted the installer's `Uninstall.exe`
  (Windows then reported "cannot find C:\Rustorigin\Uninstall.exe"). It now keeps the running
  launcher, the installed `RustoriginLauncher.exe` the shortcuts use, and `Uninstall.exe`.
- **The Windows uninstaller now removes everything:** the launcher, the game client (`C:\Rustorigin`,
  even when the launcher was installed elsewhere), all shortcuts (including the Desktop one the
  launcher creates), and the settings/logs/download-cache folder `%LOCALAPPDATA%\Rustorigin`, plus
  the registry keys. It closes the launcher first, refuses to run while the game is open, and never
  wipes a drive root or system/profile folder if one was chosen as the install location (it then
  removes only its own files).

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
