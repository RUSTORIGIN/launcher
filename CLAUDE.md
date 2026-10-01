# Rustorigin Launcher

A Windows launcher for a community-hosted Rust ("April 2021" build) server. The launcher
downloads the Rust client from a URL the host controls, extracts it, and launches it. Players
download a small (~3 MB) launcher instead of the full multi-GB client up front. When the host
moves to a newer client build, installed players **update in place from a delta pack** instead of
downloading the whole client again.

> "Rust" here is the **game**. This project is written in **C#/.NET Framework 4.x (WPF)** - it
> is not a Rust-language project. There is no `cargo`, no `src/api/`, and no `.rs` files.

## Launcher sources in this repo

All three live in `src/`, each with its own `Main()` - they are *not* compiled together:

| Source | Output | Built by | Notes |
|--------|--------|----------|-------|
| `src/WpfLauncher.cs` | `RustoriginLauncher.exe` | `csc` via `scripts/build.bat` / `scripts/make_release.ps1` | The single shipping build. WPF, single-file, cross-fading screenshot background. Downloads + **SHA-256-verifies** + extracts + launches. |
| `src/UpdateParsing.cs`, `src/A2S.cs`, `src/DiscordRpc.cs`, `src/ClientPatch.cs` | (compiled in) | same `csc` build | Self-update parsing, Steam A2S live status, Discord Rich Presence, and the client delta-update (pack recipe, stage, commit) helpers. |

Nearly all real work happens in `src/WpfLauncher.cs`. It is one self-contained code-only WPF file
(~1800 lines): window chrome, glass UI, config parsing, and the resumable, verified
downloader all live there.

## How it works

```
RustoriginLauncher.exe (launcher)
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
  no vignette or side gradients. Glass panels are flat translucent. The slideshow is **always on**
  (a core part of the look) - there is no pref or toggle to disable it.
- **Carousel dot indicators** at the bottom-center (`BuildSlideDots` / `UpdateSlideDots`): one dot
  per screenshot, the active one a wide accent pill; clicking a dot jumps to that slide and resets
  the auto-advance timer. Hidden when fewer than two slides load.
- A `launcher.cfg` placed **next to the exe** overrides the embedded defaults at runtime.

## Download behavior (implemented)

- **Resumable**: written to `%LOCALAPPDATA%\RustOrigin\RustClient.zip.part` via HTTP Range
  requests; auto-retries a dropped connection (up to 30 times, 5s apart) from the last byte. A file
  the server does not have (the GET answers 404, 410 or 403) is not retried - it fails at once
  (`RemoteFileMissingException`). A failed HEAD is still tolerated: some hosts refuse HEAD.
- **Pause / resume from the UI**: the hero download button is INSTALL, then **PAUSE** while a
  download runs (calls `CancelDownload` -> keeps the `.part`), then **RESUME** to continue via Range
  from the saved byte. One handler, `OnDownloadButton`, dispatches on `busy`; `RefreshState` sets the
  label/glyph. The **PLAY** button is hidden unless the client is installed (or our client is running).
- **Cached-zip reuse (self-healing repair)**: `DownloadWorker` step 0 checks for an already-cached
  `RustClient.zip` (e.g. left when an extract was interrupted). `FetchVerified` records the hash it
  verified in a `.verified` sidecar; `CachedDownloadUsable` reuses the file only when that record
  equals the **currently configured** hash (a file without a record, kept by an older launcher, is
  hashed once; a file of another build is deleted, never installed). A matching file is extracted
  directly - **no re-download and no re-hash**. The delta pack is reused under the same rule. If extraction fails (corrupt zip)
  it is discarded and a fresh, verified download runs. So a **REPAIR** after a broken extract reuses
  the ~9.5 GB zip instead of pulling it again. (Verification on a *fresh* download is unchanged - the
  trust anchor stays mandatory; only the redundant re-hash of the already-verified cache was removed.)
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

## Client updates (delta pack)

Moving the server to a newer client build must not cost every player a full re-download. Rust keeps
almost everything in a few multi-GB bundles that **all** change between builds, so a per-file update
saves almost nothing (Jan -> Apr 2021: 16.2 of 17.5 GB "changed") - but most of the bytes *inside*
those bundles are identical and have only moved. The delta pack ships just the bytes a player does
not already have.

- **Detecting an outdated client.** `ClientVersion=` in `launcher.cfg` names the build `DownloadUrl`
  serves. The install marker (`<InstallDir>\.rustorigin-installed`) records it as a second line,
  `client=<version>`. An install recording another version - or none, i.e. every install made before
  1.1 - is outdated (`ClientOutdated`): `RefreshState` hides **PLAY**, shows **UPDATE**, and `Play`
  refuses (the server would reject the old build anyway). Blank `ClientVersion` disables all of this.
- **The pack** (`scripts\make_client_patch.ps1` -> `RustClient-<from>-to-<to>.patchpack`, a zip):
  `recipe.txt` lists, for every changed/new file, its size + SHA-256 and how to rebuild it from
  `c <source> <offset> <length>` (copy a range of an installed file) and `d <length>` (next bytes of
  `data.bin`) ops, plus the files to delete; `data.bin` holds only the new bytes, in recipe order.
  The builder (`scripts\client_patch_builder.cs`) finds reusable data with content-defined chunking,
  so data that moved to another offset or another bundle is still found.
- **Applying it** (`TryPatchUpdate` on the download worker, logic in `src/ClientPatch.cs`):
  1. the pack is downloaded + SHA-256-verified against `PatchSha256` by the **same** `FetchVerified`
     pipeline as the full client (resumable, parallel, mandatory hash);
  2. `ClientPatch.Stage` rebuilds every changed file under `<InstallDir>\_update\files`, hashing as
     it writes, and rejects the whole update unless **each file matches its SHA-256**. The installed
     client is only read, so a failed or cancelled update leaves it exactly as it was;
  3. `ClientPatch.Commit` moves the staged files into place, deletes the files the new build
     dropped, writes the marker with the new `client=`, and only then removes the `ready` flag.
     Commit is idempotent: a swap cut short by a crash is finished on the next launch
     (constructor) or the next UPDATE click, and removing the leftover staging folder is
     best-effort (something holding it open cannot fail a finished update). Staged files are
     flushed to disk before `ready` is written.
- **Fallback.** If the pack does not fit the install (a file differs from the expected build, a
  source is missing, the pack fails its hash, or its `to` version is not `ClientVersion`), that
  is logged, the pack's hash is remembered in `patch-failed.txt` so it is not
  retried, and the worker continues straight into the normal **full download**. `PatchProbe`
  (`relative\path|size` of a file in the previous build) is checked *before* downloading the pack, so
  an install that is obviously another build skips it. A pack the host has **removed** (the GET answers 404, 410
  or 403) also falls back to the full download, without being remembered - so an old pack can be deleted from the CDN
  once most players have updated. Any other network error is not a fallback - it is
  reported like any download error and Resume continues the pack. Neither is a full disk or a locked
  file while staging (the full download would need at least as much room): that shows `Update failed`
  and keeps the verified pack, so the retry skips the download.
- **Space.** Staging needs room for the rebuilt files (~16 GB for Jan -> Apr 2021) on the install
  drive until the swap; the full-download route needs about the same for the zip + extract.
- The full zip (`DownloadUrl`/`Sha256`) must always be the **new** build: it serves new players and
  everyone the pack does not fit.

## Self-update (launcher)

On launch (background thread, gated by the `AutoUpdate` pref, default on), the launcher checks
`UpdateRepo`'s **latest GitHub Release** and compares the tag to its own version. If newer, it
prompts, downloads the release's `RustoriginLauncher.exe`, **verifies its SHA-256 against the release's
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
`Sha256`. The delta pack goes through the same gate against `PatchSha256`, and every file rebuilt
from it is verified against the SHA-256 in the pack's recipe before anything is swapped in.
Verification is **required**, not optional:

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
| `RustClient.zip.verified` / `RustClient.patchpack.verified` | The SHA-256 a kept download was verified against, so a later run reuses it only for the same configured hash. |
| `RustClient.patchpack` (+ `.part`, `.part.meta`, `.part.chunks`) | The delta pack's download buffer / verified pack, same lifecycle as the zip; deleted once the client is current. |
| `patch-failed.txt` | SHA-256 of a delta pack that did not fit this install, so it is not downloaded again (a new pack has a new hash and gets a fresh try). |
| `launcher.log` | Timestamped diagnostics of every download/verify step - ask players for this when an install misbehaves. |
| `installdir.txt` | The install folder the player picked on first INSTALL (`ChooseInstallDir`), remembered across runs; cleared by Uninstall client so a reinstall asks again. |
| `prefs.cfg` | Per-user settings (see below). |

Inside the install folder: `.rustorigin-installed` (the install marker: line 1 the install time,
then `client=<ClientVersion>`) and, only while an update is being applied, `_update\` (staged
rebuilt files + `ready` flag; removed by the commit).

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
`RustoriginLauncher.exe` (written to the repo root) needs the assets beside it to run. Use it only for a
fast compile check; never ship it on its own.

Build the real single-file release exe (stamps version, embeds all resources, applies icon +
manifest - everything is baked in, nothing needs to sit beside it):

```powershell
.\scripts\make_release.ps1 -Version 1.0.0   # -> release\RustoriginLauncher.exe
```

There is no `cargo` or clippy here. Three `Add-Type`-based test scripts exist:
`scripts\test_updater_parsing.ps1` (compiles `src\UpdateParsing.cs`, unit-tests the self-update
JSON/`SHA256SUMS`/version parsing), `scripts\test_a2s_parsing.ps1` (compiles `src\A2S.cs`,
unit-tests the A2S reply parser and runs one end-to-end query against a loopback UDP responder) and
`scripts\test_client_patch.ps1` (compiles `src\ClientPatch.cs` + the pack builder, builds a pack from
two synthetic client folders, applies it, checks the result byte-for-byte, and checks every way an
update must be refused: modified install, missing source, cancel, truncated pack, unsafe recipe).
Two GitHub Actions workflows exist: **build-check** (`.github/workflows/build-check.yml`) compiles
both builds **and runs all three tests** on every push/PR, and **release**
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
.\scripts\build_installer_exe.ps1 -Version 1.0.0   # -> release\RustoriginLauncher-1.0.0-x64.exe
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
.\scripts\build_msi.ps1 -Version 1.0.0   # -> release\RustoriginLauncher-1.0.0.msi
```

- **Full wizard UI** (`WixUI_InstallDir`): Welcome -> License -> **Choose install location (Browse)**
  -> Ready -> Progress -> Finish, plus Start Menu + Desktop shortcuts, an Add/Remove Programs entry,
  and clean uninstall/upgrade. The license page shows `scripts/installer/license.rtf` (MIT).
- **Per-user install, no admin/UAC:** default location `%LOCALAPPDATA%\Programs\Rustorigin Launcher\`,
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
`EasyAntiCheat\` + the root `Rust.exe`, the launcher's own files, and
`*.bak`/`*.py`/`*.vdf`/`*.bat`/`*.before*`), then prints the zip's **SHA-256** and writes a
`RustClient.zip.sha256` sidecar next to it. The exclusion list lives in `scripts\client_filter.ps1`
and is shared with the delta-pack builder, so the zip and the pack always ship the same file set
(and a Steam depot folder or an installed client folder can be used as a source as-is).

```powershell
# delta pack for players on the previous build (see "Client updates (delta pack)")
.\scripts\make_client_patch.ps1 -OldDir <previous client> -NewDir <new client> -From 2021-01 -To 2021-04
```

It prints the pack's **SHA-256**, the suggested `PatchProbe`, and how much players download.

## Release / publish checklist (DO THIS IN ORDER)

Because verification is mandatory, the client zip, the embedded `Sha256`, and the uploaded file
must all match. Any re-package produces a **different hash** (zip ordering/metadata differ), so
these steps move together - never upload a repackaged zip without rebuilding the launcher:

1. `.\scripts\package_client.ps1` - produces `RustClient.zip` + prints/writes its SHA-256.
2. Put that hash in `config\launcher.cfg` -> `Sha256=...`, and set `DownloadUrl=` to the file's direct link.
3. `.\scripts\make_release.ps1 -Version <x.y.z>` - bakes the updated `config\launcher.cfg` (hence the
   hash) into `release\RustoriginLauncher.exe`. Confirm the hash is embedded (it appears in the exe's
   `launcher.cfg` resource).
4. Upload **that exact** `RustClient.zip` to the `DownloadUrl` host (e.g.
   `rclone copyto RustClient.zip r2:rustorigin/RustClient.zip --s3-no-check-bucket`).
5. Publish `release\RustoriginLauncher.exe` (e.g. to the R2 bucket next to the client).

**When the client build changes** (new server build), also, before step 3:

- set a new `ClientVersion=` (and `Version=` / `Tagline=` if they name the build);
- give the new zip a **new file name** in `DownloadUrl` (e.g. `RustClient-2021-04.zip`) rather than
  overwriting the old one - a CDN may keep serving the cached old file under the old name;
- run `.\scripts\make_client_patch.ps1` against the **same** new client folder the zip was made
  from, upload the `.patchpack`, and set `PatchUrl=`, `PatchSha256=` and `PatchProbe=` from its
  output. The pack only ever upgrades the *immediately previous* build; anyone else gets the zip;
- smoke-test the update with a `launcher.cfg` next to a dev exe against a copy of the old client
  before tagging: the launcher self-update is mandatory, so a release reaches every player at once.

If the uploaded zip and the embedded hash ever drift apart, players get a (correct) integrity
rejection and cannot install - re-run from step 1.

**Automated launcher release (GitHub Actions).** `.github/workflows/release.yml` builds and
publishes the launcher on a version tag:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

That builds `RustoriginLauncher.exe`, the NSIS setup `.exe`, and the MSI from the **committed**
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
| `InstallDir` | Default/suggested install path. Blank = `.\Rust` next to the launcher. Shipped as `C:\RustOrigin`. On a fresh INSTALL the player picks the folder (`ChooseInstallDir`, pre-selecting `<launcher drive>:\Rustorigin` when the launcher is on another drive); the game always goes in a `Rustorigin` subfolder of the pick, because Uninstall deletes the whole install folder. When that folder is on another drive than `%LOCALAPPDATA%`, the download buffer goes to `<InstallDir>\_download` (`SetDownloadPaths`; not on FAT32, and a partial already in `%LOCALAPPDATA%` still resumes there). |
| `LaunchExe` | Exe the Play button runs (searched recursively inside `InstallDir`). Default `RustClient.exe`. |
| `LaunchArgs` | Optional args for the main PLAY button, e.g. `-console +connect 127.0.0.1:28015`. |
| `ClientVersion` | Id of the client build `DownloadUrl` serves (e.g. `2021-04`), recorded in the install marker. An install recording another version - or none (installs made before 1.1) - is outdated and shows **UPDATE** instead of PLAY. Blank disables update detection. Must equal the `-To` the delta pack was built with. |
| `PatchUrl` | Direct link to the delta pack (`scripts\make_client_patch.ps1`) that upgrades the previous build in place. Blank = outdated installs download the full client. |
| `PatchSha256` | Expected hex SHA-256 of the delta pack. **Required** for the pack to be used; a mismatched pack is rejected and the full client is installed instead. |
| `PatchProbe` | `relative\path\|size` of a file in the build the pack upgrades **from** (e.g. `GameAssembly.dll\|43225944`). The pack is only tried when it matches, so other builds skip straight to the full download. Blank = always try the pack. |
| `Version` | Optional label shown in the launcher. |
| `Title` | Big hero title (default `RUSTORIGIN`). |
| `Tagline` | Description line under the title. |
| `Player` | Parsed into `PlayerName` but currently **not displayed** (the user pill was removed). Kept for compatibility / future use. |
| `UpdateRepo` | `owner/repo` checked for launcher self-updates via GitHub Releases (public repos only). Blank disables. Default `RUSTORIGIN/launcher`. |
| `DiscordAppId` | Discord application id enabling **Rich Presence** (`src/DiscordRpc.cs`) - shows "RUSTORIGIN - In the launcher / In game" on the player's Discord. Blank disables. |
| `DiscordLargeImage` | Optional Rich Presence art-asset key (uploaded in the Discord app) shown as the large image. |
| `DiscordButtonLabel` / `DiscordButtonUrl` | Optional clickable button under the presence (Discord can't make the image itself a link). e.g. `Play on RustOrigin` -> `https://rustorigin.com`. |
| `Server` | Repeatable, up to 6: `Server=Tag\|Name\|launch args\|players\|cover` (everything after Name optional). Rendered as a **website-style card** (`ServerTile`, matching rustorigin.com): a cover-image header (`cover` is an embedded image name e.g. `main.jpg`, else `server-cover.png`, else a gradient) with a **status-dot badge** (top-left) + **dark tag pills** (top-right) overlaid, then a body with a live **PLAYERS ONLINE** count + **violet progress bar**, the `Name` + a `RUSTORIGIN \| tags` subtitle, the connect address, and a violet **CONNECT** chip (or **SOON** when the card has no join target). `Tag` may be **comma/slash-separated** (e.g. `Vanilla, Main, Old Recoil`) to show several pills. Cards stack vertically; clicking a joinable card (or its CONNECT chip) launches with its args. **Live status:** when the launch args contain `+connect HOST:PORT`, the launcher queries A2S (`src/A2S.cs`) and fills the count/bar (green ONLINE / grey SOON, refreshed ~60s); a `statusUrl` (6th field) web feed is used instead when set; otherwise the static `players` text shows. A source that defines `Server=` lines replaces the previous source's list. |
| `Social` | Repeatable: `Social=platform\|url` -> a clickable brand icon in the bar above the server cards (opens the URL in the browser). Built-in brand marks (inline vector paths in `SocialGlyph`): `discord`, `youtube`, `tiktok`; an unknown platform key is skipped. A source that defines `Social=` lines replaces the list from the previous source. |

## Settings vs config: `launcher.cfg` (host) vs `Prefs` (per-user)

Two separate mechanisms - don't confuse them:

- **`launcher.cfg`** = host/deployment config (URL, hash, install dir, branding, servers). Shipped
  embedded in the exe, optionally overridden by a copy next to the exe. Parsed in `ApplyConfig`.
- **`Prefs`** = per-user preferences read at runtime, persisted to
  `%LOCALAPPDATA%\RustOrigin\prefs.cfg` (a plain file - **not** the registry). `static class Prefs`
  with `Get` / `GetBool` / `Set`. A **settings panel** (the caption **gear** button, top-right) is a
  modal built in `BuildSettingsOverlay`, **styled to rustorigin.com** (Poppins `Site` font,
  `Ink*`/violet `Brand*` tokens, green switches, grouped `GroupCard`s), toggled by `ToggleSettings`
  (closes on the X / backdrop / Esc). Every item is one `InfoRow`: label + one-line description on
  the left, a single control on the right. Two groups:
  - **GAME** - *Game version* (a `StatusChip`: up to date / update required / not installed / in
    progress), *Install location* (path, size on disk, an Open button), *Minimize while in game*
    (toggle), *Uninstall game* (red button -> `UninstallClient`: deletes the installed game files +
    download cache after a confirm, keeping the launcher).
  - **LAUNCHER** - *Discord Rich Presence* (toggle), *Data folder* (Open button).

  The Game rows show live state, so `RefreshSettingsInfo` refills them each time the panel opens.
  Toggles persist immediately. There is no slideshow toggle - the background slideshow is always
  on. Users can still edit `prefs.cfg` directly.

| Prefs key | Default | Effect |
|-----------|---------|--------|
| `LaunchArgs` | (from cfg) | Overrides `launcher.cfg`'s `LaunchArgs` for the PLAY button (set in `prefs.cfg`). |
| `MinimizeInGame` | `false` | Minimize the launcher while the client runs. |
| `AutoUpdate` | `true` | Check `UpdateRepo`'s GitHub Releases on launch and offer a verified self-update. |
| `DiscordRpc` | `true` | Publish Discord Rich Presence (needs `DiscordAppId` set in `launcher.cfg`). |

To add a user setting: add a `Prefs.GetBool(...)` read where it takes effect, and (optionally) a
`SettingRow(...)` in `BuildSettingsOverlay` so it shows in the gear panel; users can also set it in
`prefs.cfg` directly.

## Repository layout (actual)

```
.
├── src/                     # C# source (WpfLauncher.cs + UpdateParsing.cs build RustoriginLauncher.exe)
│   ├── WpfLauncher.cs       #   PRIMARY launcher (WPF, single file) -> RustoriginLauncher.exe
│   ├── UpdateParsing.cs     #   pure self-update parsers (tested by scripts/test_updater_parsing.ps1)
│   ├── A2S.cs               #   Steam A2S_INFO query for live server status (tested by scripts/test_a2s_parsing.ps1)
│   ├── DiscordRpc.cs        #   dependency-free Discord Rich Presence over the Discord IPC named pipe
│   ├── ClientPatch.cs       #   client delta update: pack recipe, stage + verify, commit (tested by scripts/test_client_patch.ps1)
│   ├── app.manifest         #   Win32 manifest (csc /win32manifest)
│   └── app.ico              #   app icon
├── assets/                  # build-time embedded resources + icon sources
│   ├── 1.jpg / 2.jpg / 3.jpg           # night background screenshots (embedded + cross-faded)
│   ├── logo.png / logo-original.png / server-cover.png
│   ├── release_icon.ico     #   applied to RustoriginLauncher.exe by make_release.ps1
│   ├── app_icon_source.png
│   └── fonts/               #   bundled Montserrat (4 weights) + OFL.txt
├── config/
│   └── launcher.cfg         # runtime config (URL, hash, install dir, servers, branding)
├── scripts/
│   ├── build.bat            # dev compile check of WpfLauncher.cs via csc
│   ├── make_release.ps1     # release build: stamp version, embed resources, icon+manifest
│   ├── package_client.ps1   # zip the client into RustClient.zip for hosting
│   ├── make_client_patch.ps1 # build the delta pack (previous build -> new build) for hosting
│   ├── client_patch_builder.cs # the pack builder (content-defined chunking); compiled by the two scripts that use it, not part of the launcher
│   ├── client_filter.ps1    # which client files ship - shared by package_client.ps1 and make_client_patch.ps1
│   ├── build_msi.ps1        # build the per-user MSI installer (WiX)
│   ├── build_installer_exe.ps1 # build the NSIS setup .exe (electron-builder style, Program Files)
│   ├── test_updater_parsing.ps1 # unit tests for src/UpdateParsing.cs (run in build-check CI)
│   ├── test_a2s_parsing.ps1 # unit + loopback tests for src/A2S.cs live server status (run in build-check CI)
│   ├── test_client_patch.ps1 # end-to-end tests for the client delta update (run in build-check CI)
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
├── release/                 # built RustoriginLauncher.exe output (git-ignored)
├── src/bin/ , src/obj/      # dotnet build output, not source of truth (git-ignored)
├── RustClient.zip(.sha256)  # packaged client + hash, ~9 GB (git-ignored; from package_client.ps1)
├── *.patchpack(.sha256)     # delta pack + hash (git-ignored; from make_client_patch.ps1)
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
- `RustClient.zip`, `RustClient.zip.sha256`, `*.patchpack`, `*.part` - packaged client (9+ GB), delta pack and download temp
- loose built exes at the repo root (`RustoriginLauncher.exe`, `RustClient.exe`)
- `.superdesign/tmp/` - design scratch
- key/cert/secret file types (`*.pem`, `*.key`, `*.pfx`, `.env`, `rclone.conf`, ...)

Do commit source (`src/*.cs`, `src/*.csproj`), scripts (`scripts/*`), `config/launcher.cfg`,
build-time `assets/` (`1.jpg`-`3.jpg`, `logo*.png`, `server-cover.png`, `fonts/`, `release_icon.ico`),
and docs. When it goes public, remember the commit history exposes the author email.

## Threading model

- The download/verify/extract pipeline - and the client update (`TryPatchUpdate`: fetch the pack,
  stage, commit) - runs on a **background `Thread`** (`DownloadWorker`, started by `StartInstall`).
  It must never touch WPF UI objects directly.
- All UI updates from that thread marshal back via `Dispatcher.BeginInvoke` (see `SetStatus`,
  `ReportProgress`, and the completion block). Follow that pattern for any new background work.
- Cancellation is cooperative: `volatile bool cancelRequested` (+ `activeReq.Abort()` for the
  in-flight request); long loops (download, hashing) check it and throw `OperationCanceledException`.
- `DispatcherTimer`s (2s state refresh, 7s slideshow switch) run on the UI thread - keep their handlers cheap.

## Run & smoke-test

The automated tests cover the parsers and the delta-update logic, not the window; verify the UI by
running the exe:

- Launch `release\RustoriginLauncher.exe` (or a `scripts\build.bat` exe with assets beside it). The window
  opens at 1440x860 in a **rounded frameless window** with full native behaviour (drag from anywhere,
  resize, maximize, Aero Snap, taskbar) via `WindowChrome`, custom **settings (gear) / minimize / close**
  caption buttons (there is no maximize button - maximize via Aero Snap; a double-click does nothing), the
  screenshot slideshow, PLAY, INSTALL, and the server grid. Corners flatten when maximized.
- **Native min/max animations:** the window is intentionally **not** layered (`AllowsTransparency =
  false`) - a layered window loses the native minimize/maximize/restore animations. The 32px rounded
  corners come instead from a rounded window region (`ApplyWindowRegion` -> `SetWindowRgn` with
  `CreateRoundRectRgn`), reapplied on resize/DPI-change and cleared (square) when maximized. So it
  keeps both the big radius and the real Windows animations; region-clipped corners are not
  anti-aliased, so they read very slightly harder than a layered window's.
- **Integrity smoke test:** blank `Sha256` -> INSTALL refused; correct `Sha256` -> download -> verify
  -> extract; wrong `Sha256` -> download rejected, nothing installed. (Also in docs/IMPLEMENTATION_PLAN.md section 5.)
- **Update smoke test** (with a `launcher.cfg` next to the exe and a copy of the previous client as
  the install): UPDATE shows instead of PLAY -> the pack downloads, verifies, "UPDATING" runs to 100%
  -> PLAY appears and the marker ends with `client=<ClientVersion>`. Wrong `PatchSha256` -> the pack
  is rejected and the full download starts. A modified install file -> the log says the pack is not
  usable and the full download starts; the install is untouched until then.

## Conventions for edits

- Keep `src/WpfLauncher.cs` **code-only WPF** (no XAML) and self-contained - it is compiled
  directly by `csc.exe`, so it must not take a NuGet dependency.
- UI colors/brand come from `docs/brand-kit/` and the `B("#hex")` brush helpers; reuse existing
  palette brushes (`Accent`, `Glass`, `Stroke`, ...) rather than adding new literals.
- **Status line** (the text under the hero buttons): it is empty when idle - status lives in the
  button. Show a message with `ShowStatus(brush, text, untilNextAction)` and clear it with
  `ClearStatus()`; never assign `statusText.Text` from an action, because `RefreshState` (every 2 s
  and after every action) blanks the line unless a message is being held. Errors hold until the
  player's next action, notices (feedback for a click) for a few seconds.
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
