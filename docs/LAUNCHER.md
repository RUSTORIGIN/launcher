# Rustorigin Launcher - reference

How the launcher works, in detail: download, verification, client updates, self-update, the security
model, runtime files, every `launcher.cfg` key and per-user pref, installers, packaging and the release
checklist. Day-to-day commands, conventions and gotchas for working on the code are in
[`CLAUDE.md`](../CLAUDE.md); UI conventions in [`STYLING.md`](STYLING.md).

> "Rust" here is the **game**. This project is written in **C#/.NET Framework 4.x (WPF)** - it
> is not a Rust-language project. There is no `cargo`, no `src/api/`, and no `.rs` files.

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
- A `launcher.cfg` placed **next to the exe** overrides the embedded defaults at runtime - in a **release
  build only when started with `--dev-config`** (`LoadConfig`; otherwise it is ignored and logged), because
  players run the exe from Downloads, where any web page can drop a file. A dev build (`build.bat`, nothing
  embedded) always reads it. Even with `--dev-config`, a release build takes `AcSessionUrl` and `AcClientFile`
  only from its embedded config, so the real launcher can't be told to vouch for modified game files.
  `LaunchExe` must be a relative `.exe` path inside the install folder and
  `UpdateRepo` must be `owner/repo`, whatever the source.

## Download behavior (implemented)

- **Resumable**: written to `%LOCALAPPDATA%\RustOrigin\RustClient.zip.part` via HTTP Range
  requests; auto-retries a dropped connection (up to 30 times, 5s apart) from the last byte. A file
  the server does not have (the GET answers 404, 410 or 403) is not retried - it fails at once
  (`RemoteFileMissingException`). A failed HEAD is still tolerated: some hosts refuse HEAD.
- **Pause / resume from the UI**: the hero download button is INSTALL, then **PAUSE** while a
  download runs (calls `CancelDownload` -> keeps the `.part`), then **RESUME** to continue via Range
  from the saved byte. One handler, `OnDownloadButton`, dispatches on `busy`; `RefreshState` sets the
  label/glyph. The **PLAY** button is hidden unless the client is installed (or our client is running).
- **Piece repair** (`PieceHashes`, `FindBadPieces`): `config/piece-hashes.txt`, embedded in the exe (never read
  from a file next to it), lists the SHA-256 of every 32 MB piece of the hosted zip and pack, keyed by the file's
  full SHA-256 + size. When the file being fetched is listed, each piece is checked as it arrives and a damaged
  one is fetched again at once (8 damaged arrivals of one piece stop the download, saying security software or the drive may be interfering;
  two different pieces that each arrive twice with the same wrong SHA-256 - or one piece that arrives 8 times with
  the same wrong SHA-256 - mean the list is wrong, so piece checks turn off and the full check decides). If
  the finished file still fails its full SHA-256 check, the launcher re-reads it, finds the damaged pieces and
  re-downloads only those, for up to 3 rounds. The full SHA-256 check is still the only thing that accepts a
  download; the piece hashes only decide what to fetch again. A file that is not listed (or a stale list) just
  downloads without piece repair. After a failed download the next one uses a single connection
  (`SafeDownload` pref), until a download verifies.
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
  `DownloadUrl` / `PatchUrl` must be `https://` (`IsHttpsUrl`: INSTALL refuses an http client URL, an http
  pack is skipped), and a server redirect to a non-https address fails the download at once
  (`RequireHttps` / `InsecureRedirectException`, not retried). The self-update requests get the same check.
- **Size pin** (`ClientSize` / `PatchSize`, optional, printed by the packaging scripts): a server announcing
  another size is refused before anything is downloaded, the single stream stops at that size, and the
  parallel download reserves no more than it. Without a pin the server's size is still a hard stop, and the
  free space is checked before anything is reserved (+512 MB).
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
  3. `ClientPatch.Stage` also writes a manifest of every staged file (size + SHA-256), the files to delete and
     the version to `%LOCALAPPDATA%\Rustorigin\update-manifest.txt`, which other accounts can't write.
     `ClientPatch.Commit(..., manifest, rehash)` moves only staged files the manifest lists with the right size
     - and when the commit is a resumed one (after a restart: the next UPDATE click), re-hashes each one
     first; any mismatch, unlisted file or link discards the staging and the update is staged again from the
     kept pack. The version and delete list come from the manifest, not from the staging folder.
  4. `ClientPatch.Commit` moves the staged files into place, deletes the files the new build
     dropped, writes the marker with the new `client=`, and only then removes the `ready` flag.
     Commit is idempotent: a swap cut short by a crash is finished by the next UPDATE
     click (on the download thread, after the re-hash above), and removing the leftover staging folder is
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

On launch (background thread), the launcher checks `UpdateRepo`'s **latest GitHub Release** and compares
the tag to its own version. If newer, it downloads the release's `SHA256SUMS.txt` and
`SHA256SUMS.txt.sig` and checks the **signature** (`UpdateParsing.VerifyRelease`) against `UpdatePublicKey`
from its embedded config - all *before* showing the player anything. Only a release that verifies brings
up the (mandatory) update prompt; the player presses Update, the launcher downloads
`RustoriginLauncher.exe`, checks it against the hash from the **signed** sums, and self-replaces (rename
running exe -> `.old`, drop the new exe in, relaunch). See `UpdateCheckWorker` / `PerformUpdate` in
`src/WpfLauncher.cs`.

- **Signature:** RSA (>= 3072-bit) PKCS#1 v1.5 over SHA-256 of
  `"rustorigin-launcher-release\n" + <tag> + "\n" + <exact bytes of SHA256SUMS.txt>`, base64 in the asset
  `SHA256SUMS.txt.sig`. The tag is part of the message, so an old signed release can't be re-published as
  a newer version. A missing, invalid or wrongly-keyed signature is logged and the release is **ignored**
  (it can't block anyone behind the update prompt).
- **Keys:** `scripts\new_signing_key.ps1 -OutFile <outside the repo>` makes the RSA-3072 key pair once; the
  private key stays offline (locked to the maintainer's account), the printed `UpdatePublicKey=` line goes
  into `config/launcher.cfg`. `UpdatePublicKey` is taken only from the embedded config (like the anti-cheat
  keys, even with `--dev-config`). Blank = the launcher never updates itself (fail closed).
- **Releasing:** CI only creates a **draft** (`release.yml`); `scripts\sign_release.ps1 -Tag vX.Y.Z -KeyFile
  <key> [-Publish]` downloads the draft, checks every asset against `SHA256SUMS.txt` (and that nothing
  unlisted is attached), signs, re-verifies with the committed public key, uploads the `.sig` and publishes.
  `/releases/latest` never returns drafts, so players only ever see signed releases.
- **Rotation:** a lost key means no more updates (players reinstall a launcher with a new key); a leaked
  key means shipping a launcher with a new key, signed with the old one, at once.
- Needs a **public** repo (unauthenticated GitHub API); on a private repo the check 404s and is silently
  skipped. `UpdateRepo=` blank also disables it.
- If the install folder is read-only, the swap fails gracefully and opens the releases page.
- Launchers before this change verify only the (unsigned) `SHA256SUMS.txt`; they still update normally
  to a signed release and are protected from then on.

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
exe is the trust anchor), and can be overridden by a `launcher.cfg` next to the exe for testing (release build: start it with `--dev-config`).

The download path still does **NOT**:

- fetch, parse, or verify a **signed release manifest** (no embedded public key, no signature check)
- pin or verify server certificates beyond the OS default TLS chain

**If you add manifest signing**, that is a genuine security-sensitive change: verify the signature
with an embedded public key, take the expected hash from the *signed* manifest rather than plain
config, refuse to extract/launch on mismatch, add tests, and update this document. Keep the private
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
| `update-manifest.txt` | What a staged client update contains (sizes + SHA-256s, deletes, version), kept here - not in the install folder - so `ClientPatch.Commit` can refuse staged files someone swapped. Deleted when the update is committed. |
| `launcher.log` | Timestamped diagnostics of every download/verify step - ask players for this when an install misbehaves. |
| `installdir.txt` | The install folder the player picked on first INSTALL (`ChooseInstallDir`), remembered across runs; cleared by Uninstall client so a reinstall asks again. |
| `prefs.cfg` | Per-user settings (see below). |

**Install folder permissions - fail closed** (`SecureInstallDir`, `RequirePrivateInstallDir`): a folder made at
a drive root inherits "Authenticated Users: Modify", so any account on the PC could swap the game or launcher
exe that this player - or an admin - later runs. The launcher therefore only installs, updates (incl. finishing
a staged update) and PLAYs from a **private** install folder (`FolderExposure` returns null): on NTFS/ReFS,
owned by this user, SYSTEM or Administrators, and writable by no one else.
- A new install folder is **created** private (`Directory.CreateDirectory(dir, PrivateSecurity)`): this user,
  SYSTEM and Administrators full control, Users and OWNER RIGHTS read + execute, nothing inherited.
- A folder this user owns but others can write (an install by an older launcher) is **locked** (`LockFolder`)
  - in the background on launch, and again at INSTALL/UPDATE/REPAIR/PLAY. `ResetTree` then walks everything
  inside: every entry gets what it inherits from the folder, and entries another account owns, any
  link/junction, or a file with more than one name (a **hard link**, whose other name may be outside the
  folder) are deleted (a junction or hard link is removed as a name, never its target). If there were any,
  the install marker is dropped, so the game must be repaired (a fresh verified extract, which replaces files
  rather than writing into them) before PLAY. The `_download\*.verified` records are dropped too.
- A folder an admin made (an install by the **old per-machine NSIS setup**) or another account made can't be
  re-permissioned by the player. `RequirePrivateInstallDir` offers a one-time fix that starts the launcher
  elevated (`--secure-folder "<dir>" <player SID>` -> `SecureFolderElevated`): it only touches a folder that
  is exposed right now and looks like a Rustorigin install, never names Everyone/Users as the player, takes
  over untrusted entries for Administrators instead of deleting them (links and hard links are still removed),
  and drops the marker if there were any.
  Declined or impossible (a FAT/exFAT drive, a folder that isn't an install) -> PLAY/INSTALL are refused with
  the reason; a fresh install forgets the folder so the next INSTALL asks for another.
- **Links are never followed** (`src/FolderPin.cs`): the ACL calls find a folder by name and would follow a
  junction or symlink, re-permissioning its target (e.g. another account's profile). So an install folder that
  is, or sits under, a junction/symlink/mount point is refused - by `SecureInstallDir`, by `LooksLikeInstallFolder`
  (no elevated fix is offered) and by `SecureFolderElevated`. While a folder is locked it and every entry
  `ResetTree` touches are held open without delete sharing (`FolderPin.Pin`), so nobody can rename one or swap
  a link in its place mid-lock; the folder's real path (`GetFinalPathNameByHandle`) must equal its configured
  path, so a subst drive or network path is refused too. Cloud/dedup reparse points (OneDrive placeholders) are
  not links and are allowed. Tested by `scripts\test_folder_pin.ps1`.
- **An entry the lock can't secure keeps the folder refused** (unreadable, or held open by another program):
  `LockFolder` puts a fresh `.rustorigin-lock-incomplete` into the folder before making the folder itself
  private, and again after a walk that left entries unsecured (`NewRecord`). Each one has the private
  permissions from the start (created in the open folder, it would otherwise get that folder's, and another
  account could open it with rights to change or delete it). When nothing is at the name it is created right
  there; otherwise it is created under a temporary name nobody can guess (`.rustorigin-lock-incomplete.<random>.new`,
  so nothing can be planted there first) and renamed over the file or link at the name in one step
  (`FolderPin.RenameOver`). Whatever another account prepared at the name - a file with its own permissions, a
  handle it holds open - goes with the file it replaces; an existing record is never kept. If the name is held
  open without delete sharing, the replacement fails and what is there stays: before the lock that aborts the
  lock (the folder stays exposed, so refused) and counts as *busy*, like a game file open in another program
  (`FolderPin.InUse`: a sharing violation, or access denied on the replace or move when an open of our own shows
  the name is held - or, for a move, that we may add a folder there, so only an open handle is left to explain
  it) - so the player is told to close the program, and not offered the administrator fix, which the same open
  handle would refuse. A refusal that could be a permission stays one, and the fix is offered. When a failed lock leaves nothing at the name, a record is created
  there directly as a last resort. A *folder* (or folder link) planted at the name before the lock can't be
  replaced that way, so it is first moved aside in one step through a handle on it (`FolderPin.MoveAside`, never
  over anything) to `.rustorigin-lock-incomplete.<random>.old`; if that is refused (its planter denied the player
  delete on it, or holds it or something in it open) the lock aborts and the folder stays exposed (held open:
  busy; denied: the fix is offered). Moved, it is an ordinary entry of the walk: if it can't be
  secured (as the player: deleted), the record of ours at the name keeps the folder refused - not the planter's
  folder, which its planter could remove after the lock. The record stays open, shared with no one,
  until the folder is private and the record is set to inherit from it. Leftover temporary records (a lock cut
  short between the two steps) and moved-aside folders are removed by the next lock that secures everything. The record is removed only
  once every entry was secured - so a walk that failed or was cut short
  (launcher closed, crash) leaves it behind. While anything by that name exists, `FolderExposure` reports
  the folder as not secured, so the next check doesn't pass just because the folder's own permissions look
  private; the next lock walks it again. A folder planted at that name is moved aside and handled by the walk
  like any other entry (as the player deleted when another account owns it, otherwise secured and only removed
  once everything is); a file planted there is replaced. Uninstall keeps the record and any moved-aside folder. When the player's
  own lock was blocked only by files open in another program, no administrator fix is offered (close the game,
  then try again); for any other unsecured entry the fix is offered. After the elevated
  fix, `RequirePrivateInstallDir` also requires the helper's exit code 0 (1 = entries it could not secure,
  3 = all of those were only open in another program: reported as busy, 2 = refused).
  Tested end to end through the built exe by `scripts\test_folder_lock.ps1`.
- **Permissions are set one object at a time, through the pinned handles** (`FolderPin.GetSecurity`/`SetSecurity`
  = `Get`/`SetKernelObjectSecurity`), never by name: `SetNamedSecurityInfo` (.NET `SetAccessControl`) and even
  handle-based `SetSecurityInfo` pass a folder's new permissions on to every entry already in it, so a hard link
  planted before the lock would hand them to the file it shares outside the folder. Instead `LockFolder` sets
  the folder alone, and `ResetTree` sets each entry to what it inherits (`FolderPin.InheritedAcl`) before listing
  what is inside it; a file whose link count (`FolderPin.LinkCount`) is above 1 is never re-permissioned, only
  that name is removed (`FolderPin.DeleteName`, which leaves the other name's attributes alone too). An entry
  whose name ends in a dot or space (only creatable through a `\\?\` path; Windows trims it when opening) can't
  be pinned as itself, so it counts as not secured.
- **Untrusted folders are taken apart without following links** (`FolderPin.DeleteTree`): the player's own
  lock deletes a folder another account owns (a planted `.rustorigin-lock-incomplete` folder too, once moved aside), and that
  account keeps its rights inside it meanwhile - enough to empty a subfolder and turn it into a junction to one
  of the player's folders. So every folder is pinned while it is emptied, and every entry is opened and deleted
  only after its real path is checked to be its pinned folder's real path plus its name (`InFolder`); a recursive
  `Directory.Delete(path, true)` would follow such a junction. Read-only entries are deleted as they are
  (`DeleteName`, `FILE_DISPOSITION_IGNORE_READONLY`; on Windows before 10 1809 a read-only one stays, and the
  folder stays refused). Uninstall keeps a folder moved aside from the record's name whole, like the record.
- Limits: it can't tell that a file another account *modified in place* before the lock was changed (only the
  `AcClientFile` hashes and a repair can), and nothing protects against malware running as the player.

Inside the install folder: `.rustorigin-installed` (the install marker: line 1 the install time,
then `client=<ClientVersion>`), only while an update is being applied, `_update\` (staged
rebuilt files + `ready` flag; removed by the commit), and, only while a folder lock is unfinished,
`.rustorigin-lock-incomplete` (see above).

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

There is no `cargo` or clippy here. Six test scripts exist (five `Add-Type`-based, one driving the built exe):
`scripts\test_updater_parsing.ps1` (compiles `src\UpdateParsing.cs`, unit-tests the self-update
JSON/`SHA256SUMS`/version parsing), `scripts\test_a2s_parsing.ps1` (compiles `src\A2S.cs`,
unit-tests the A2S reply parser and runs one end-to-end query against a loopback UDP responder) and
`scripts\test_client_patch.ps1` (compiles `src\ClientPatch.cs` + the pack builder, builds a pack from
two synthetic client folders, applies it, checks the result byte-for-byte, and checks every way an
update must be refused: modified install, missing source, cancel, truncated pack, unsafe recipe), and
`scripts\test_ac_session.ps1` (compiles `src\AcSession.cs`, unit-tests the anti-cheat session messages,
HMAC signing against an independent reference vector, reply parsing, the endpoint rule, and the session
client's loop against an in-process fake service; with `AC_TEST_URL` set it also runs the real HTTP client
against a running `ac_session.py`), and `scripts\test_folder_pin.ps1` (compiles `src\FolderPin.cs`, builds
junctions in a temp folder and checks that a linked install folder or parent is refused, a plain one is
accepted, and a pinned folder can't be renamed), and `scripts\test_folder_lock.ps1` (runs the release
exe's `--secure-folder` helper on temp folders: links refused, an exposed folder locked, links inside removed,
and an entry held open keeps the folder refused across runs until it can be secured).
Two GitHub Actions workflows exist: **build-check** (`.github/workflows/build-check.yml`) compiles
both builds **and runs all six tests** on every push/PR, and **release**
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
- **Per-user install, no admin/UAC** (`RequestExecutionLevel user`): default
  `%LOCALAPPDATA%\Programs\Rustorigin Launcher`, HKCU entries, this user's shortcuts - like the MSI. It used to
  be per-machine into `C:\Rustorigin` (elevated, HKLM), which left the launcher, `Uninstall.exe` and the game
  writable by every account on the PC: any of them could swap a file an admin later ran. The launcher's
  one-time elevated folder fix covers installs made that way.
- The install folder is locked to this user, SYSTEM and Administrators with `icacls` - only when the setup
  created it or it already holds the launcher, never an unrelated folder typed on the directory page. The
  uninstaller deletes a folder recursively only when it is named `Rustorigin` / `Rustorigin Launcher`;
  otherwise it removes just the launcher's own files.
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

**Whenever the hosted zip or pack changes**, also run `.\scripts\make_piece_hashes.ps1 <zip> <patchpack>` on
the exact uploaded files before step 3. It writes `config\piece-hashes.txt` (the SHA-256 of every 32 MB
piece), which the launcher embeds for piece repair. A stale or missing entry is harmless (that file downloads
without piece repair), but players whose PC damages downloads then get the old fail-and-restart behaviour.
Its `file` lines also give each file's exact size for `ClientSize=` / `PatchSize=`.

**When the client build changes** (new server build), also, before step 3:

- set a new `ClientVersion=` (and `Version=` / `Tagline=` if they name the build);
- regenerate the `AcClientFile=` lines with `.\scripts\make_client_filehashes.ps1 -ClientDir <new client>`
  (the anti-cheat session would otherwise report every player's new game files as `client:modified`);
- give the new zip a **new file name** in `DownloadUrl` (e.g. `RustClient-2021-04.zip`) rather than
  overwriting the old one - a CDN may keep serving the cached old file under the old name;
- run `.\scripts\make_client_patch.ps1` against the **same** new client folder the zip was made
  from, upload the `.patchpack`, and set `PatchUrl=`, `PatchSha256=` and `PatchProbe=` from its
  output. The pack only ever upgrades the *immediately previous* build; anyone else gets the zip;
- smoke-test the update with a `launcher.cfg` next to a dev exe (or the release exe started with `--dev-config`) against a copy of the old client
  before tagging: the launcher self-update is mandatory, so a release reaches every player at once.

If the uploaded zip and the embedded hash ever drift apart, players get a (correct) integrity
rejection and cannot install - re-run from step 1.

**Automated launcher release (GitHub Actions).** `.github/workflows/release.yml` builds the launcher
on a version tag and creates a **draft** release; you sign and publish it from your own machine:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

That builds `RustoriginLauncher.exe`, the NSIS setup `.exe`, and the MSI from the **committed**
`config/launcher.cfg` (it refuses to build a release whose config has no valid `UpdatePublicKey`),
generates `SHA256SUMS.txt`, and creates a **draft** GitHub Release with all artifacts attached. Then:

```powershell
.\scripts\sign_release.ps1 -Tag v1.0.0 -KeyFile <offline key> -Publish
```

Once it is published, update the README's release and downloads badges: `docs/readme/badge-release-*.svg`
and `docs/readme/badge-downloads-*.svg` are static images that show the version (e.g. `v1.1.2`) and the
total download count at the time they were drawn, and nothing updates them automatically. Their text is
outlined into paths, so it can't be edited as text: regenerate the dark and light file of each with the
new values, at the same height (30 px) and in the same two colour themes, and land them through a pull
request like any other change.

The workflow is least-privilege: `permissions: {}` at the top, the build job (installer tooling from
NuGet/Chocolatey, NSIS pinned to 3.12.0) can only read; only the `publish` job writes, inside the
protected `release` environment, running nothing but GitHub's own actions and `gh`. Every action is pinned
to a commit SHA. Tag + environment protection: `.github/BRANCH_PROTECTION.md`. **It does not touch the game client** (that 9 GB zip is never in CI), so
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
| `LaunchExe` | Exe the Play button runs: exactly `InstallDir\LaunchExe` (a relative `.exe` path, `SafeLaunchExe`) - never searched for, and never one next to the launcher. Default `RustClient.exe`. |
| `LaunchArgs` | Optional args for the main PLAY button, e.g. `-console +connect 127.0.0.1:28015`. |
| `ClientVersion` | Id of the client build `DownloadUrl` serves (e.g. `2021-04`), recorded in the install marker. An install recording another version - or none (installs made before 1.1) - is outdated and shows **UPDATE** instead of PLAY. Blank disables update detection. Must equal the `-To` the delta pack was built with. |
| `PatchUrl` | Direct link to the delta pack (`scripts\make_client_patch.ps1`) that upgrades the previous build in place. Blank = outdated installs download the full client. |
| `PatchSha256` | Expected hex SHA-256 of the delta pack. **Required** for the pack to be used; a mismatched pack is rejected and the full client is installed instead. |
| `PatchProbe` | `relative\path\|size` of a file in the build the pack upgrades **from** (e.g. `GameAssembly.dll\|43225944`). The pack is only tried when it matches, so other builds skip straight to the full download. Blank = always try the pack. |
| `ClientSize` / `PatchSize` | Exact byte size of the client zip / delta pack (printed by `package_client.ps1` / `make_client_patch.ps1`). Optional but recommended: a download server announcing another size is refused, and the launcher never downloads or reserves more. Must belong to the same file as `Sha256` / `PatchSha256`. |
| `Version` | Optional label shown in the launcher. |
| `Title` | Big hero title (default `RUSTORIGIN`). |
| `Tagline` | Description line under the title. |
| `Player` | Parsed into `PlayerName` but currently **not displayed** (the user pill was removed). Kept for compatibility / future use. |
| `UpdatePublicKey` | `rsa:<modulus>:<exponent>` printed by `scripts\new_signing_key.ps1`: the release key a self-update's `SHA256SUMS.txt` must be signed with. Embedded config only. Blank = no self-update (and `release.yml` refuses to build a release). |
| `UpdateRepo` | `owner/repo` checked for launcher self-updates via GitHub Releases (public repos only). Blank disables. Default `RUSTORIGIN/launcher`. |
| `DiscordAppId` | Discord application id enabling **Rich Presence** (`src/DiscordRpc.cs`) - shows "RUSTORIGIN - In the launcher / In game" on the player's Discord. Blank disables. |
| `DiscordLargeImage` | Optional Rich Presence art-asset key (uploaded in the Discord app) shown as the large image. |
| `DiscordButtonLabel` / `DiscordButtonUrl` | Optional clickable button under the presence (Discord can't make the image itself a link). e.g. `Play on RustOrigin` -> `https://rustorigin.com`. |
| `AcSessionUrl` | Base URL of the launcher anti-cheat session service (`docs/CLIENT_ANTICHEAT.md`). When set, `Play` starts an `AcSessionClient` (`src/AcSession.cs`) right after the game process starts: it opens a session for the SteamID logged in to Steam (registry `ActiveUser`), sending the PC's hardware id (`AcSession.ReadHardwareId`: a prefixed SHA-256 of the Windows MachineGuid; the session service re-keys it with a server-only secret, and the RustRCON panel uses it for alt-account links and its ban-evasion check, slice 8), and heartbeats until the game exits, on its own background thread, logging only to `launcher.log`. It never blocks or delays PLAY. Because the server can kick players without a session (OriginSession enforcement, slice 7), the launcher keeps it alive: closing the window while the game runs hides it to the tray and the launcher quits when the game exits (`closeAfterGame`; tray > Quit quits at once, `quitNow`), and `EnsureAcSession` (on the 2 s game timer) starts a session for a running game that has none (launcher restarted mid-game). **Game link:** `AcGameLog` follows the game's own `Player.log` (`%USERPROFILE%\AppData\LocalLow\Facepunch Studios LTD\Rust\Player.log`) for the one-time code the server sends to the connected game (`rustorigin-link <16 hex>`), and the session client echoes it at once (`link` in the signed heartbeat), which binds the session to that game connection; only that code is taken from the log. Must be `https`; plain `http` is accepted only for a local/private IP literal (a dev server, e.g. in WSL) - anything else is ignored with a log line. Blank (the shipped default) disables it. On the first PLAY with it set, a one-time notice (shown after the game has started; pref `AcNoticeShown`) tells the player what is sent. **The tool list is deliberately not a launcher setting:** the session service sends the rules in its session reply (`"tools"`, parsed by `AcSignals.ParseToolSpec`), and the client reports only `tool:<id>` - keep it that way, so the list stays out of this public repo, the exe and players' configs. |
| `AcClientFile` | Repeatable `relative\path\|size\|sha256` (`AcSignals.ParseClientFile`; rooted paths and `..` refused): once per launch the session hashes these game files and reports `client:modified:<file>` / `client:missing:<file>` on a mismatch. **Regenerate with `scripts\make_client_filehashes.ps1` whenever the client build changes.** A source that defines them replaces the previous list. |
| `Server` | Repeatable, up to 6: `Server=Tag\|Name\|launch args\|players\|cover` (everything after Name optional). Rendered as a **website-style card** (`ServerTile`, matching rustorigin.com): a cover-image header (`cover` is an embedded image name e.g. `main.png`, else `server-cover.png`, else a gradient) with a **status-dot badge** (top-left) + **dark tag pills** (top-right) overlaid, then a body with a live **PLAYERS ONLINE** count + **violet progress bar**, the `Name` + a `RUSTORIGIN \| tags` subtitle, the connect address, and a violet **CONNECT** chip (or **SOON** when the card has no join target). `Tag` may be **comma/slash-separated** (e.g. `Vanilla, Main, Old Recoil`) to show several pills. Cards stack vertically; clicking a joinable card (or its CONNECT chip) launches with its args. **Live status:** when the launch args contain `+connect HOST:PORT`, the launcher queries A2S (`src/A2S.cs`) and fills the count/bar (green ONLINE / grey SOON, refreshed ~60s); a `statusUrl` (6th field) web feed is used instead when set; otherwise the static `players` text shows. A source that defines `Server=` lines replaces the previous source's list. |
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
| `SafeDownload` | `false` | Set by the launcher after a download failed its integrity check: the next download uses a single connection. Cleared by the next download that verifies. |
| `DiscordRpc` | `true` | Publish Discord Rich Presence (needs `DiscordAppId` set in `launcher.cfg`). |
| `AcNoticeShown` | `false` | Set once the one-time anti-cheat session notice has been shown (after the first PLAY with `AcSessionUrl` set). |

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
│   ├── AcSession.cs         #   anti-cheat session protocol v1: build/sign messages, parse replies (tested by scripts/test_ac_session.ps1)
│   ├── app.manifest         #   Win32 manifest (csc /win32manifest)
│   └── app.ico              #   app icon
├── assets/                  # build-time embedded resources + icon sources
│   ├── 1.jpg / 2.jpg / 3.jpg           # night background screenshots (embedded + cross-faded)
│   ├── logo.png / logo-original.png / server-cover.png
│   ├── release_icon.ico     #   applied to RustoriginLauncher.exe by make_release.ps1
│   ├── app_icon_source.png
│   └── fonts/               #   bundled Montserrat (4 weights) + OFL.txt
├── config/
│   ├── launcher.cfg         # runtime config (URL, hash, install dir, servers, branding)
│   └── piece-hashes.txt     # per-32 MB-piece SHA-256 of the hosted zip + pack (make_piece_hashes.ps1)
├── scripts/
│   ├── build.bat            # dev compile check of WpfLauncher.cs via csc
│   ├── make_release.ps1     # release build: stamp version, embed resources, icon+manifest
│   ├── package_client.ps1   # zip the client into RustClient.zip for hosting
│   ├── make_client_patch.ps1 # build the delta pack (previous build -> new build) for hosting
│   ├── client_patch_builder.cs # the pack builder (content-defined chunking); compiled by the two scripts that use it, not part of the launcher
│   ├── client_filter.ps1    # which client files ship - shared by package_client.ps1 and make_client_patch.ps1
│   ├── build_msi.ps1        # build the per-user MSI installer (WiX)
│   ├── build_installer_exe.ps1 # build the NSIS setup .exe (electron-builder style, per-user)
│   ├── test_updater_parsing.ps1 # unit tests for src/UpdateParsing.cs (run in build-check CI)
│   ├── test_a2s_parsing.ps1 # unit + loopback tests for src/A2S.cs live server status (run in build-check CI)
│   ├── test_client_patch.ps1 # end-to-end tests for the client delta update (run in build-check CI)
│   ├── test_ac_session.ps1  # unit tests for src/AcSession.cs, the anti-cheat session protocol (run in build-check CI)
│   ├── make_client_filehashes.ps1 # AcClientFile= lines (size + SHA-256 of key game files) for launcher.cfg
│   ├── make_piece_hashes.ps1 # per-piece hashes of the hosted files -> config\piece-hashes.txt (download self-repair)
│   ├── new_signing_key.ps1  # once: the offline release signing key (prints UpdatePublicKey=)
│   ├── sign_release.ps1     # every release: check the draft's assets, sign SHA256SUMS.txt, publish
│   └── installer/
│       ├── RustOrigin.wxs   # WiX source for the MSI (WixUI_InstallDir wizard)
│       ├── RustOrigin.nsi   # NSIS source for the setup .exe (MUI2 wizard)
│       └── license.rtf      # MIT license shown on both installers' license page
├── docs/
│   ├── IMPLEMENTATION_PLAN.md  # design->code status record (reconciled to current code)
│   ├── LAUNCHER.md          # this reference: behavior, config keys, runtime files, release checklist
│   ├── CLIENT_ANTICHEAT.md  # launcher anti-cheat session: design, protocol v1, rollout, privacy
│   ├── AC_MODULE_SIGNAL.md  # proposal (not built): user-mode loaded-module signal for RustClient.exe
│   ├── README-PLAYERS.txt
│   ├── screenshot.png       # launcher screenshot (no longer shown in the README)
│   ├── readme/              # README images: banner, flow strip, release/downloads/license badges (dark/light SVGs)
│   └── brand-kit/           # design system (css, style guide, docs)
├── .claude/                 # Claude Code: agents/ + rules/ (shared); settings.local.json is git-ignored
├── .github/                 # workflows (build-check, release), PR template, rulesets (main, release tags), BRANCH_PROTECTION.md
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
├── README.md                # player-facing page; the authoritative reference is docs/LAUNCHER.md
└── CLAUDE.md                # Claude Code instructions (short); the reference is docs/LAUNCHER.md
```

## Version control

This is a git repository (branch `main`, changed only through pull requests), pushed to the **public**
GitHub repo `RUSTORIGIN/launcher`. `.gitignore` and `.gitattributes` are in place; line endings
are normalized to LF (CRLF for `.bat`/`.ps1`).

`.gitignore` keeps build output and the multi-GB client out of git:

- `bin/`, `obj/`, `release/` - build output (dotnet output lands in `src/bin`, `src/obj`)
- `RustClient.zip`, `RustClient.zip.sha256`, `*.patchpack`, `*.part` - packaged client (9+ GB), delta pack and download temp
- loose built exes at the repo root (`RustoriginLauncher.exe`, `RustClient.exe`)
- `.superdesign/tmp/` - design scratch
- key/cert/secret file types (`*.pem`, `*.key`, `*.pfx`, `.env`, `rclone.conf`, ...)

Do commit source (`src/*.cs`, `src/*.csproj`), scripts (`scripts/*`), `config/launcher.cfg`,
build-time `assets/` (`1.jpg`-`3.jpg`, `logo*.png`, `server-cover.png`, `fonts/`, `release_icon.ico`),
and docs. The commit history exposes the author email.

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
- **Update smoke test** (with a `launcher.cfg` next to the exe - release build started with `--dev-config` - and a copy of the previous client as
  the install): UPDATE shows instead of PLAY -> the pack downloads, verifies, "UPDATING" runs to 100%
  -> PLAY appears and the marker ends with `client=<ClientVersion>`. Wrong `PatchSha256` -> the pack
  is rejected and the full download starts. A modified install file -> the log says the pack is not
  usable and the full download starts; the install is untouched until then.

## Disclaimer

Community project, not affiliated with or endorsed by Facepunch Studios. The Rust client itself
is distributed separately and is not part of this repository.
