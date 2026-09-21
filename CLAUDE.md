# RustOrigin Launcher

A Windows launcher for a community-hosted Rust ("January 2021" build) server. The launcher
downloads the Rust client from a URL the host controls, extracts it, and launches it. Players
download a small (~3 MB) launcher instead of the full multi-GB client up front.

> "Rust" here is the **game**. This project is written in **C#/.NET Framework 4.x (WPF)** - it
> is not a Rust-language project. There is no `cargo`, no `src/api/`, and no `.rs` files.

## Launcher sources in this repo

All three live in `src/`, each with its own `Main()` - they are *not* compiled together:

| Source | Output | Built by | Notes |
|--------|--------|----------|-------|
| `src/WpfLauncher.cs` | `RustOriginLauncher.exe` | `csc` via `scripts/build.bat` / `scripts/make_release.ps1` | **Primary / shipping build.** WPF, single-file, cross-fading screenshot background. Downloads + **SHA-256-verifies** + extracts + launches. |
| `src/Program.cs` | `RustLauncher.exe` | `src/RustLauncher.csproj` (`dotnet build`) | Minimal WinForms UI. **Find-and-launch only - it does not download or verify.** |
| `src/Launcher.cs` | (none) | not wired to any build | Older standalone WinForms downloader (`WebClient`, non-resumable, **no verification**). Reference-only; excluded from the csproj. |

The csproj excludes `WpfLauncher.cs` and `Launcher.cs` on purpose (see the `<Compile Remove>`
items) - an SDK-style project otherwise globs every `.cs` in `src/` and the three `Main()`s
collide.

Nearly all real work happens in `src/WpfLauncher.cs`. It is one self-contained code-only WPF file
(~1800 lines): window chrome, glass UI, config parsing, and the resumable, verified
downloader all live there.

## How it works

```
RustOriginLauncher.exe (launcher)
   |  HTTPS GET (resumable, TLS 1.2+)
   v
DownloadUrl from launcher.cfg  (e.g. Cloudflare R2 public bucket)
   |  RustClient.zip
   v
Extract to InstallDir  ->  launch LaunchExe (RustClient.exe)
```

- The launcher holds **no** R2/cloud credentials. `DownloadUrl` is just a public direct link.
- Everything the UI needs (the night background screenshots `1.jpg`-`3.jpg`, `logo.png`,
  `server-cover.png`, `launcher.cfg`, Montserrat fonts) is **embedded in the exe** as manifest
  resources and unpacked at first run to `%LOCALAPPDATA%\RustOrigin\assets\<version>\` (loaded from
  real files for the images and private fonts).
- The background is a **cross-fading slideshow** of the night screenshots `1.jpg`-`3.jpg` (switches every ~7s; see
  `BuildBackground` / `NextSlide` / `ShowSlide`), **softly blurred** (a `BlurEffect` on `bgHost`,
  half-res cached) under a light uniform scrim (`BuildGradient`) so the foreground UI reads cleanly -
  no vignette or side gradients. Glass panels are flat translucent. Toggle the auto-switch via the
  `BgSlideshow` pref.
- **Carousel dot indicators** at the bottom-center (`BuildSlideDots` / `UpdateSlideDots`): one dot
  per screenshot, the active one a wide accent pill; clicking a dot jumps to that slide and resets
  the auto-advance timer. Hidden when fewer than two slides load.
- A `launcher.cfg` placed **next to the exe** overrides the embedded defaults at runtime.

## Download behavior (implemented)

- **Resumable**: written to `%LOCALAPPDATA%\RustOrigin\RustClient.zip.part` via HTTP Range
  requests; auto-retries a dropped connection (up to 30 times, 5s apart) from the last byte.
- **Pause / resume from the UI**: the hero download button is INSTALL, then **PAUSE** while a
  download runs (calls `CancelDownload` -> keeps the `.part`), then **RESUME** to continue via Range
  from the saved byte. One handler, `OnDownloadButton`, dispatches on `busy`; `RefreshState` sets the
  label/glyph. The **PLAY** button is hidden unless the client is installed (or our client is running).
- **Cached-zip reuse (self-healing repair)**: `DownloadWorker` step 0 checks for an already-cached
  `RustClient.zip` (e.g. left when an extract was interrupted). If present it re-verifies the hash and
  extracts it directly - **no re-download**. On mismatch the cached zip is discarded and a fresh
  download runs. So a **REPAIR** after a broken extract reuses the ~9.5 GB zip instead of pulling it again.
- **Completeness check / REPAIR**: `IsInstalled` requires the exe AND a non-empty `<exe>_Data` folder
  plus `UnityPlayer.dll` (or the post-extract marker). A structurally-incomplete install shows a
  **REPAIR** button and is refused by `Play()`, so a half-extracted client never launches into a
  Unity error. This is a cheap sanity check, not cryptographic per-file verification.
- A saved partial is reused only for the **same** remote file (URL + ETag + size), so a new
  build is never stitched onto an old partial.
- **TLS**: `ConfigureTls()` forces TLS 1.2 (and 1.3 where the OS supports it); no HTTP fallback.
- **Integrity**: SHA-256 verification is mandatory - Install is blocked without a configured
  `Sha256`, and the finished download is verified before extraction and rejected on mismatch
  (see the security section below).
- **Logging**: every step is timestamped to `%LOCALAPPDATA%\RustOrigin\launcher.log`.

## Self-update (launcher)

On launch (background thread, gated by the `AutoUpdate` pref, default on), the launcher checks
`UpdateRepo`'s **latest GitHub Release** and compares the tag to its own version. If newer, it
prompts, downloads the release's `RustOriginLauncher.exe`, **verifies its SHA-256 against the release's
`SHA256SUMS.txt`**, and self-replaces (rename running exe -> `.old`, drop the new exe in, relaunch).
A hash mismatch is rejected - it never runs an unverified replacement, same trust model as the
client download. See `StartUpdateCheck` / `UpdateCheckWorker` in `src/WpfLauncher.cs`.

Notes / limits:
- Needs a **public** repo (unauthenticated GitHub API); on a private repo the check 404s and is
  silently skipped. `UpdateRepo=` blank also disables it.
- If the install folder is read-only (e.g. a `Program Files` install without elevation), the swap
  fails gracefully and opens the releases page for a manual update.
- The `SHA256SUMS.txt` is **not signed** - whoever controls the repo's releases controls the update
  (same caveat as the unsigned client manifest). Signing is future work.

## Discord Rich Presence

`src/DiscordRpc.cs` is a **dependency-free** Discord Rich Presence client: it talks the Discord IPC
protocol directly over the local named pipe (`\\.\pipe\discord-ipc-0..9`) using only `System.IO.Pipes`
and hand-built JSON - no NuGet, no native Discord SDK - so it stays compatible with the single-file
`csc` build. It runs on background threads, reconnects if Discord starts later, answers pings, and
silently no-ops when Discord isn't running.

Wiring in `WpfLauncher.cs`: `StartDiscord()` (constructor) starts it when `DiscordAppId` is set and
the `DiscordRpc` pref is on; `SetDiscord(state)` updates the presence to "In the launcher" on start,
"In game" when the client launches (`Play`), and back on exit (`RestoreFromGame`); `OnClosed` stops
it. Needs a **Discord Application ID** (`DiscordAppId` in `launcher.cfg`) - blank leaves the feature
dormant. An optional `DiscordLargeImage` names a Rich Presence art asset uploaded in the Discord app.

## Security model - current state (READ THIS)

**Implemented - SHA-256 verification (mandatory):** the launcher hashes the finished download and
**rejects it** (deletes it, never extracts or launches it) unless it matches the configured
`Sha256`. Verification is **required**, not optional:

- `StartInstall` refuses to begin a download when no `Sha256` is configured (`NormalizedExpectedHash`
  is empty) - nothing is downloaded until a hash is set, so a config mistake can't install an
  unverified client.
- `DownloadWorker` step 4 verifies the completed `.part` via `VerifyDownload` / `ComputeSha256`
  *before* promoting it to the real zip or extracting. `VerifyDownload` returns false on mismatch
  **and** when no hash is configured (defensive second gate).
- On a hash **mismatch** the `.part` + meta are deleted so the next attempt re-downloads cleanly.
  On the no-hash defensive path the `.part` is kept so adding `Sha256` + Resume verifies it without
  re-downloading. On cancel during hashing the `.part` is kept so Resume re-verifies.

The expected hash is normally baked into the exe via the embedded `launcher.cfg` (the open-source
exe is the trust anchor), and can be overridden by a `launcher.cfg` next to the exe for testing.

The download path still does **NOT**:

- fetch, parse, or verify a **signed release manifest** (no embedded public key, no signature check)
- pin or verify server certificates beyond the OS default TLS chain

**If you add manifest signing**, that is a genuine security-sensitive change: verify the signature
with an embedded public key, take the expected hash from the *signed* manifest rather than plain
config, refuse to extract/launch on mismatch, add tests, and update this file. Keep the private
signing key out of the repo.

**Housekeeping:** `LICENSE` (MIT) and `SECURITY.md` (private contact `rustorigin@proton.me`,
launcher-only scope) are present. Keep `SECURITY.md`'s scope and the verification claims in sync
if the security model changes.

## Windows behavior

- Targets .NET Framework 4.x, which ships with Windows 10/11 - no runtime install for players.
- Runs without administrator rights; default `InstallDir` is a top-level folder (`C:\RustOrigin`),
  not `Program Files`.
- The exe is **unsigned** until a code-signing cert is added, so SmartScreen shows the
  unknown-publisher warning on first run. The project does not attempt to bypass that.
- Does not touch Defender/SmartScreen, AV exclusions, services, persistence, or browser settings.
- **EasyAntiCheat:** the launcher only unzips the client, so it does **not** run
  `EasyAntiCheat_Setup.exe`. If a build needs EAC, either add a launch step or have players run
  `EasyAntiCheat\EasyAntiCheat_Setup.exe` once. Many private-server builds run EAC-less.

## Runtime files & state

At runtime the launcher reads/writes under **`%LOCALAPPDATA%\RustOrigin\`** (never the install
dir, so no admin rights needed):

| Path | Purpose |
|------|---------|
| `assets\<version>\` | Screenshots (`1.jpg`-`3.jpg`)/logo/fonts/`launcher.cfg` unpacked from the exe at first run. Keyed by assembly version, so a new build unpacks fresh. |
| `RustClient.zip.part` + `.part.meta` | Resumable-download buffer and its identity (URL+ETag+size) for validating a resume. |
| `RustClient.zip` | The verified download, briefly, between finalize and extract (deleted after). |
| `launcher.log` | Timestamped diagnostics of every download/verify step - ask players for this when an install misbehaves. |
| `installdir.txt` | The chosen install directory, remembered across runs. |
| `prefs.cfg` | Per-user settings (see below). |

Install also creates a **desktop shortcut** (`<name>.lnk` via `WScript.Shell` COM) and
**self-copies the launcher** into the install area - see `InstallLauncherAndShortcut()`.

## Build

The primary build uses the .NET Framework C# compiler (`csc.exe`) that is already on every
Windows 10/11 machine - no SDK/NuGet restore needed.

All scripts resolve paths against the repo root, so run them from the root regardless of the
`scripts\` location.

Quick dev compile of the launcher:

```bat
scripts\build.bat
```

`build.bat` does **not** embed resources, icon, or manifest and does not stamp the version - its
`RustOriginLauncher.exe` (written to the repo root) needs the assets beside it to run. Use it only for a
fast compile check; never ship it on its own.

Build the real single-file release exe (stamps version, embeds all resources, applies icon +
manifest - everything is baked in, nothing needs to sit beside it):

```powershell
.\scripts\make_release.ps1 -Version 1.0.0   # -> release\RustOriginLauncher.exe
```

The WinForms `RustLauncher.exe` builds via the SDK-style project (`net48`, verified working):

```powershell
dotnet build -c Release src\RustLauncher.csproj   # -> src\bin\Release\RustLauncher.exe
```

There is no `cargo` or clippy here. Two `Add-Type`-based test scripts exist:
`scripts\test_updater_parsing.ps1` (compiles `src\UpdateParsing.cs`, unit-tests the self-update
JSON/`SHA256SUMS`/version parsing) and `scripts\test_a2s_parsing.ps1` (compiles `src\A2S.cs`,
unit-tests the A2S reply parser and runs one end-to-end query against a loopback UDP responder).
Two GitHub Actions workflows exist: **build-check** (`.github/workflows/build-check.yml`) compiles
both builds **and runs both tests** on every push/PR, and **release**
(`.github/workflows/release.yml`) builds and publishes on a version tag (see the release checklist).
Do not reference commands that don't exist here. After changing `src\WpfLauncher.cs`, the fastest
correctness check is a clean `csc` compile (as `build.bat` does).

## Installers

Two optional installers are provided; both install **only** the ~3 MB launcher (the game client
is still downloaded + verified at runtime). Pick whichever fits distribution.

### A) NSIS setup `.exe` (electron-builder style) - recommended for distribution

A single self-contained setup executable, the same *kind* electron-builder's NSIS target produces
(`AppName-x.y.z-x64.exe`).

```powershell
.\scripts\build_installer_exe.ps1 -Version 1.0.0   # -> release\RustOriginLauncher-1.0.0-x64.exe
```

- **Wizard:** Welcome -> License (MIT) -> **Choose install folder** -> Install (progress) ->
  Finish (with "Launch" checkbox), plus Start Menu + Desktop shortcuts, an Add/Remove Programs
  entry, and an uninstaller.
- **Per-machine install to `Program Files` (requires admin/UAC)** - the classic per-machine
  installer behavior. This
  is the one trade-off vs the launcher's usual no-admin design; use the MSI below if you want no-admin.
- Built with **NSIS** (`scripts/installer/RustOrigin.nsi`, Modern UI 2). `build_installer_exe.ps1`
  runs `make_release.ps1` first so the setup always wraps a fresh, versioned exe.
- **NSIS prerequisite (one-time):** `winget install NSIS.NSIS`.

### B) MSI (per-user, no admin)

```powershell
.\scripts\build_msi.ps1 -Version 1.0.0   # -> release\RustOriginLauncher-1.0.0.msi
```

- **Full wizard UI** (`WixUI_InstallDir`): Welcome -> License -> **Choose install location (Browse)**
  -> Ready -> Progress -> Finish, plus Start Menu + Desktop shortcuts, an Add/Remove Programs entry,
  and clean uninstall/upgrade. The license page shows `scripts/installer/license.rtf` (MIT).
- **Per-user install, no admin/UAC:** default location `%LOCALAPPDATA%\Programs\RustOrigin Launcher\`,
  matching the launcher's no-admin design. The user can change it on the install-location page - but
  because it's a per-user (non-elevated) MSI, picking a protected folder like `Program Files` will
  fail; keep the default or a writable path. (The launcher then installs the client to `C:\RustOrigin`.)
- Built with the **WiX toolset** (`scripts/installer/RustOrigin.wxs`). `build_msi.ps1` first runs
  `make_release.ps1` so the MSI always wraps a fresh, versioned exe; the MSI version is bound from
  the exe's file version.
- **WiX prerequisites (one-time):**
  `dotnet tool install --global wix --version 5.0.2` and
  `wix extension add -g WixToolset.UI.wixext/5.0.2` (the UI extension provides the wizard).
  Use **v5** - WiX **v6+ require accepting the paid Open Source Maintenance Fee EULA to build**,
  which v5 does not. v5 uses the same `.wxs` schema, so nothing in the source changes.
- `UpgradeCode` in the `.wxs` is **stable** - never change it, or upgrades won't recognize prior builds.
- Like the exe, the MSI is **unsigned** until a code-signing cert is added (same SmartScreen note).

## Packaging & hosting the client (host-side)

```powershell
.\scripts\package_client.ps1   # zips the client folder into RustClient.zip (client files at zip root)
```

`package_client.ps1` excludes things that must never ship (e.g. `temp\`, `maps\`, most of `cfg\`,
and `*.bak`/`*.py`/`*.vdf`/`*.bat`/`*.before*`), then prints the zip's **SHA-256** and writes a
`RustClient.zip.sha256` sidecar next to it.

## Release / publish checklist (DO THIS IN ORDER)

Because verification is mandatory, the client zip, the embedded `Sha256`, and the uploaded file
must all match. Any re-package produces a **different hash** (zip ordering/metadata differ), so
these steps move together - never upload a repackaged zip without rebuilding the launcher:

1. `.\scripts\package_client.ps1` - produces `RustClient.zip` + prints/writes its SHA-256.
2. Put that hash in `config\launcher.cfg` -> `Sha256=...`, and set `DownloadUrl=` to the file's direct link.
3. `.\scripts\make_release.ps1 -Version <x.y.z>` - bakes the updated `config\launcher.cfg` (hence the
   hash) into `release\RustOriginLauncher.exe`. Confirm the hash is embedded (it appears in the exe's
   `launcher.cfg` resource).
4. Upload **that exact** `RustClient.zip` to the `DownloadUrl` host (e.g.
   `rclone copyto RustClient.zip r2:rustorigin/RustClient.zip --s3-no-check-bucket`).
5. Publish `release\RustOriginLauncher.exe` (e.g. to the R2 bucket next to the client).

If the uploaded zip and the embedded hash ever drift apart, players get a (correct) integrity
rejection and cannot install - re-run from step 1.

**Automated launcher release (GitHub Actions).** `.github/workflows/release.yml` builds and
publishes the launcher on a version tag:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

That builds `RustOriginLauncher.exe`, the NSIS setup `.exe`, and the MSI from the **committed**
`config/launcher.cfg`, generates `SHA256SUMS.txt`, and creates a GitHub Release with all
artifacts attached. **It does not touch the game client** (that 9 GB zip is never in CI), so
steps 1-2 and 4 above (package the client, set the matching `Sha256`, upload the zip to R2)
are still done by hand *before* tagging - otherwise the released launcher embeds a hash for a
zip nobody is hosting. Only tag once `config/launcher.cfg` holds the hash of the zip you uploaded.

Uploading/publishing is an outward-facing action: do it deliberately, not as part of a build.

## launcher.cfg reference

Plain `Key=Value`, `#`/`;` comments. Loaded embedded-defaults-first, then overridden by a
`launcher.cfg` next to the exe.

| Key | Meaning |
|-----|---------|
| `DownloadUrl` | Direct link to `RustClient.zip`. Required. |
| `Sha256` | Expected hex SHA-256 of `RustClient.zip`. **Required** - a mismatched download is rejected, and a blank value blocks Install entirely. Aliases: `ClientSha256`, `ExpectedSha256`. |
| `InstallDir` | Install path. Blank = `.\Rust` next to the launcher. Shipped as `C:\RustOrigin`. |
| `LaunchExe` | Exe the Play button runs (searched recursively inside `InstallDir`). Default `RustClient.exe`. |
| `LaunchArgs` | Optional args for the main PLAY button, e.g. `-console +connect 127.0.0.1:28015`. |
| `Version` | Optional label shown in the launcher. |
| `Title` | Big hero title (default `RUSTORIGIN`). |
| `Tagline` | Description line under the title. |
| `Player` | Parsed into `PlayerName` but currently **not displayed** (the user pill was removed). Kept for compatibility / future use. |
| `UpdateRepo` | `owner/repo` checked for launcher self-updates via GitHub Releases (public repos only). Blank disables. Default `RUSTORIGIN/launcher`. |
| `DiscordAppId` | Discord application id enabling **Rich Presence** (`src/DiscordRpc.cs`) - shows "RUSTORIGIN - In the launcher / In game" on the player's Discord. Blank disables. |
| `DiscordLargeImage` | Optional Rich Presence art-asset key (uploaded in the Discord app) shown as the large image. |
| `DiscordButtonLabel` / `DiscordButtonUrl` | Optional clickable button under the presence (Discord can't make the image itself a link). e.g. `Play on RustOrigin` -> `https://rustorigin.com`. |
| `Server` | Repeatable, up to 6: `Server=Tag\|Name\|launch args\|players\|cover` (everything after Name optional). `cover` is an embedded image file name (e.g. `main.jpg`) used as the card background, else the shared `server-cover.png`, else a gradient. Cards stack vertically. **Live status:** when the launch args contain `+connect HOST:PORT`, the launcher queries that endpoint over Steam A2S (`src/A2S.cs`) and shows a live green/red dot + `players/max` (refreshed ~60s), overriding the static `players` field; without a `+connect` endpoint it shows the static text. A source that defines `Server=` lines replaces the list from the previous source. |
| `Social` | Repeatable: `Social=platform\|url` -> a clickable brand icon in the bar above the server cards (opens the URL in the browser). Built-in brand marks (inline vector paths in `SocialGlyph`): `discord`, `youtube`, `tiktok`; an unknown platform key is skipped. A source that defines `Social=` lines replaces the list from the previous source. |

## Settings vs config: `launcher.cfg` (host) vs `Prefs` (per-user)

Two separate mechanisms - don't confuse them:

- **`launcher.cfg`** = host/deployment config (URL, hash, install dir, branding, servers). Shipped
  embedded in the exe, optionally overridden by a copy next to the exe. Parsed in `ApplyConfig`.
- **`Prefs`** = per-user preferences read at runtime, persisted to
  `%LOCALAPPDATA%\RustOrigin\prefs.cfg` (a plain file - **not** the registry). `static class Prefs`
  with `Get` / `GetBool` / `Set`. **There is no in-app settings UI** - the Settings tab/gear was
  removed; users change these by editing `prefs.cfg` directly (defaults below apply otherwise).

| Prefs key | Default | Effect |
|-----------|---------|--------|
| `LaunchArgs` | (from cfg) | Overrides `launcher.cfg`'s `LaunchArgs` for the PLAY button (set in `prefs.cfg`). |
| `BgSlideshow` | `true` | Auto-switch (cross-fade) between the background screenshots. Off keeps a single still image. |
| `MinimizeInGame` | `false` | Minimize the launcher while the client runs. |
| `AutoUpdate` | `true` | Check `UpdateRepo`'s GitHub Releases on launch and offer a verified self-update. |
| `DiscordRpc` | `true` | Publish Discord Rich Presence (needs `DiscordAppId` set in `launcher.cfg`). |

To add a user setting: add a `Prefs.GetBool(...)` read where it takes effect. There is no
settings screen to wire it into - users set it in `prefs.cfg`.

## Repository layout (actual)

```
.
├── src/                     # C# source (WpfLauncher.cs + UpdateParsing.cs build RustOriginLauncher.exe)
│   ├── WpfLauncher.cs       #   PRIMARY launcher (WPF, single file) -> RustOriginLauncher.exe
│   ├── UpdateParsing.cs     #   pure self-update parsers (tested by scripts/test_updater_parsing.ps1)
│   ├── A2S.cs               #   Steam A2S_INFO query for live server status (tested by scripts/test_a2s_parsing.ps1)
│   ├── DiscordRpc.cs        #   dependency-free Discord Rich Presence over the Discord IPC named pipe
│   ├── Launcher.cs          #   older standalone WinForms downloader (reference-only)
│   ├── Program.cs           #   minimal WinForms find-and-launch UI -> RustLauncher.exe
│   ├── RustLauncher.csproj  #   SDK-style project (net48); builds Program.cs only
│   ├── app.manifest         #   Win32 manifest (csc /win32manifest, csproj ApplicationManifest)
│   └── app.ico              #   WinForms app icon (csproj ApplicationIcon)
├── assets/                  # build-time embedded resources + icon sources
│   ├── 1.jpg / 2.jpg / 3.jpg           # night background screenshots (embedded + cross-faded)
│   ├── logo.png / logo-original.png / server-cover.png
│   ├── release_icon.ico     #   applied to RustOriginLauncher.exe by make_release.ps1
│   ├── app_icon_source.png
│   └── fonts/               #   bundled Montserrat (4 weights) + OFL.txt
├── config/
│   └── launcher.cfg         # runtime config (URL, hash, install dir, servers, branding)
├── scripts/
│   ├── build.bat            # dev compile check of WpfLauncher.cs via csc
│   ├── make_release.ps1     # release build: stamp version, embed resources, icon+manifest
│   ├── package_client.ps1   # zip the client into RustClient.zip for hosting
│   ├── build_msi.ps1        # build the per-user MSI installer (WiX)
│   ├── build_installer_exe.ps1 # build the NSIS setup .exe (electron-builder style, Program Files)
│   ├── test_updater_parsing.ps1 # unit tests for src/UpdateParsing.cs (run in build-check CI)
│   ├── test_a2s_parsing.ps1 # unit + loopback tests for src/A2S.cs live server status (run in build-check CI)
│   └── installer/
│       ├── RustOrigin.wxs   # WiX source for the MSI (WixUI_InstallDir wizard)
│       ├── RustOrigin.nsi   # NSIS source for the setup .exe (MUI2 wizard)
│       └── license.rtf      # MIT license shown on both installers' license page
├── docs/
│   ├── IMPLEMENTATION_PLAN.md  # design->code status record (reconciled to current code)
│   ├── README-PLAYERS.txt
│   ├── screenshot.png       # launcher screenshot used in README
│   └── brand-kit/           # design system (css, style guide, docs)
├── .github/                 # workflows (build-check, release), PR template, ruleset, BRANCH_PROTECTION.md
├── discord/                 # discord assets
├── .superdesign/            # design canvas scratch (HTML mockups)
├── release/                 # built RustOriginLauncher.exe output (git-ignored)
├── src/bin/ , src/obj/      # dotnet build output, not source of truth (git-ignored)
├── RustClient.zip(.sha256)  # packaged client + hash, ~9 GB (git-ignored; from package_client.ps1)
├── .gitignore  .gitattributes
├── LICENSE                  # MIT
├── SECURITY.md              # vulnerability-reporting policy (rustorigin@proton.me)
├── CONTRIBUTING.md          # pull-request workflow (branch -> PR -> CI -> merge)
├── CHANGELOG.md             # release notes
├── README.md                # player/host-facing docs (authoritative for behavior)
└── CLAUDE.md                # this file
```

## Version control

This is a git repository (branch `main`), pushed to a **private** GitHub repo
(`RUSTORIGIN/launcher`). `.gitignore` and `.gitattributes` are in place; line endings
are normalized to LF (CRLF for `.bat`/`.ps1`).

`.gitignore` keeps build output and the multi-GB client out of git:

- `bin/`, `obj/`, `release/` - build output (dotnet output lands in `src/bin`, `src/obj`)
- `RustClient.zip`, `RustClient.zip.sha256`, `*.part` - packaged client (9+ GB) and download temp
- loose built exes at the repo root (`RustOriginLauncher.exe`, `RustClient.exe`)
- `.superdesign/tmp/` - design scratch
- key/cert/secret file types (`*.pem`, `*.key`, `*.pfx`, `.env`, `rclone.conf`, ...)

Do commit source (`src/*.cs`, `src/*.csproj`), scripts (`scripts/*`), `config/launcher.cfg`,
build-time `assets/` (`1.jpg`-`3.jpg`, `logo*.png`, `server-cover.png`, `fonts/`, `release_icon.ico`),
and docs. When it goes public, remember the commit history exposes the author email.

## Threading model

- The download/verify/extract pipeline runs on a **background `Thread`** (`DownloadWorker`,
  started by `StartInstall`). It must never touch WPF UI objects directly.
- All UI updates from that thread marshal back via `Dispatcher.BeginInvoke` (see `SetStatus`,
  `ReportProgress`, and the completion block). Follow that pattern for any new background work.
- Cancellation is cooperative: `volatile bool cancelRequested` (+ `activeReq.Abort()` for the
  in-flight request); long loops (download, hashing) check it and throw `OperationCanceledException`.
- `DispatcherTimer`s (2s state refresh, 7s slideshow switch) run on the UI thread - keep their handlers cheap.

## Run & smoke-test

There are no automated tests; verify by running the exe:

- Launch `release\RustOriginLauncher.exe` (or a `scripts\build.bat` exe with assets beside it). The window
  opens at 1440x860 in a **rounded frameless window** with full native behaviour (drag from anywhere,
  resize, maximize, Aero Snap, taskbar) via `WindowChrome`, custom min/max/close caption buttons, the
  screenshot slideshow, PLAY, INSTALL, and the server grid. Corners flatten when maximized.
- **Native min/max animations:** the window is intentionally **not** layered (`AllowsTransparency =
  false`) - a layered window loses the native minimize/maximize/restore animations. The 32px rounded
  corners come instead from a rounded window region (`ApplyWindowRegion` -> `SetWindowRgn` with
  `CreateRoundRectRgn`), reapplied on resize/DPI-change and cleared (square) when maximized. So it
  keeps both the big radius and the real Windows animations; region-clipped corners are not
  anti-aliased, so they read very slightly harder than a layered window's.
- **Integrity smoke test:** blank `Sha256` -> INSTALL refused; correct `Sha256` -> download -> verify
  -> extract; wrong `Sha256` -> download rejected, nothing installed. (Also in docs/IMPLEMENTATION_PLAN.md section 5.)

## Conventions for edits

- Keep `src/WpfLauncher.cs` **code-only WPF** (no XAML) and self-contained - it is compiled
  directly by `csc.exe`, so it must not take a NuGet dependency.
- UI colors/brand come from `docs/brand-kit/` and the `B("#hex")` brush helpers; reuse existing
  palette brushes (`Accent`, `Glass`, `Stroke`, ...) rather than adding new literals.
- Config keys are parsed case-insensitively in `ApplyConfig`; add new keys there and document
  them in both this file and `README.md`. Per-user toggles go through `Prefs`, not `launcher.cfg`.
- After changing `src/WpfLauncher.cs`, rebuild with `scripts\build.bat` (or `scripts\make_release.ps1`)
  and confirm the exe launches; there are no automated tests to rely on.
- Asset/branding note: `assets/app_icon_source.png` -> `src/app.ico` / `assets/release_icon.ico`;
  embedded assets (`assets/1.jpg`-`3.jpg`, `assets/logo*.png`, `assets/server-cover.png`,
  `assets/fonts/`) and `config/launcher.cfg` are wired in by `scripts/make_release.ps1`.

## Disclaimer

Community project, not affiliated with or endorsed by Facepunch Studios. The Rust client itself
is distributed separately and is not part of this repository.
