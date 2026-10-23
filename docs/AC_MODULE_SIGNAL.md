# Anti-cheat session: loaded-module signal

Status: **phase A is built** on the launcher side (`AcModules` in `src/AcSession.cs`, slice 10 of
[CLIENT_ANTICHEAT.md](CLIENT_ANTICHEAT.md), which also lists the service and plugin work that remains and the
RustRCON panel contract); **phase B is built, measure-only.** It extends the evidence labels of the launcher session
(slice 5). Nothing here changes the protocol: it only adds labels to the heartbeat's `signals`.

## Goal

Many cheats run inside the game: a DLL gets loaded into `RustClient.exe` by an injector, or by a
proxy DLL dropped into the game folder (`version.dll`, `winmm.dll`, `dxgi.dll` and so on, which Windows
loads from the game folder before the system copy). The launcher already started the game, runs as the same
user, and can list the modules loaded in it without a driver or admin rights. This signal reports code that
is loaded into the game and is neither part of the hosted client build nor part of Windows, nor signed by
a trusted publisher.

Like every launcher signal, it is **evidence for staff review, never a kick or ban on its own**.

## What it cannot see

Say this plainly, in the doc and to staff:

- **External cheats** (a separate process reading game memory). The `tool:<id>` rules cover only the ones
  that have a known process name.
- **Kernel cheats and DMA hardware.** A user-mode scan can't see below itself.
- **Manually mapped code** (an injector that writes a DLL into memory without the Windows loader). It
  doesn't appear in the module list. Phase B below only hints at it.
- **Signed cheats.** A cheat signed with a trusted (stolen or bought) certificate is not reported. Nor is one
  signed with a certificate the player made themself and added to their own Windows certificate store: the check
  is Windows' own Authenticode verdict for that user account, and the player owns the account.
- **A fake launcher.** The launcher is open source, so a fake one can simply report nothing. That is the
  limit of the whole session design, not of this signal.

## What it checks

### Phase A: the module list

On the session thread, using the `Process` that `Play` started:

1. **Open the game read-only:** `OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ)` (what
   `EnumProcessModulesEx` needs) and `EnumProcessModulesEx(LIST_MODULES_ALL)` + `GetModuleFileNameExW`. The
   launcher is built `anycpu` without the 32-bit preference, so it runs as 64-bit and sees the 64-bit game's
   modules. Nothing is written, suspended or injected. A process that can't be opened at all gives one
   `module:scan-failed` label; a list that can't be read this time (a process still starting) gives nothing
   and is retried at the next heartbeat.
2. **When:** the first scan 60 s after the game starts (Unity loads plugins lazily), then at every
   heartbeat. Listing the modules takes milliseconds; only modules not seen before are classified, so a
   scan after the first one costs almost nothing.
3. **Classify each new module by where its file lives**, cached by path + size + last-write time:

| Module location | Check | Label when it fails |
|---|---|---|
| The client folder (the folder of `RustClient.exe`) | Is it on the hosted build's list of code files (`AcClientFile=`, now every `.dll`/`.exe` in the build: 14 files for the 2021-04 client, the default of `scripts\make_client_filehashes.ps1`)? Listed files are hashed by the existing file check. | `client:modified:<name>` (file check) when it's listed but differs; `client:extra-module:<name>` when it isn't listed (the proxy-DLL case). The extra-module label is only produced when the list includes `RustClient.exe` itself: an older three-file list is not a list of the build's code files, and flagging the game's own plugins would be noise |
| Windows' code folders: `%SystemRoot%\System32`, `SysWOW64`, `WinSxS`, `assembly`, `Microsoft.NET` (DriverStore is under System32) | None: writing there needs admin rights, and their files are signed through catalogs. **The rest of `%SystemRoot%` is not skipped**, and neither are the well-known user-writable folders inside System32/SysWOW64 (`Tasks`, `Tasks_Migrated`, `spool\drivers\color`, `spool\PRINTERS`, `spool\SERVERS`, `com\dmp`, `FxsTmp`, `Microsoft\Crypto\RSA\MachineKeys`): a module there is judged like any other file. | none |
| Anywhere else | Authenticode via `WinVerifyTrust`, in the file or in a system catalog (`CryptCATAdminEnumCatalogFromHash`, how Windows and many Microsoft components are signed), with no revocation check and no network fetch | `module:unsigned:<first 32 hex of SHA-256>` when there is no signature, a bad hash, or an untrusted or self-signed root |
| A path whose file is gone | None possible: a mapped DLL can be renamed while loaded, and hiding the file that way is what an injector does | `module:missing-file` (no name, no hash) |

`<name>` is the file name, lowercased and reduced to `[a-z0-9_.-]`, at most 40 characters. A file name in
the game folder says nothing about the player. Every label stays within the existing 64-character rule.

**Why the client folder is checked by hash, not by signature.** All 14 code files of the 2021-04 client
are signed by Facepunch, but they were signed **without a timestamp**, and the certificate expired on
2023-05-22. `WinVerifyTrust` (and `Get-AuthenticodeSignature`) rejects every one of them today ("not within
its validity period"). A signature rule would flag the game itself, so the client folder uses the hosted
build's hashes, which the launcher already has a mechanism for (`AcClientFile=`,
`scripts/make_client_filehashes.ps1`).

**Expired signatures elsewhere.** Older legitimate tools have the same problem. A module whose signature is
intact but whose certificate has expired counts as signed: the signer is known, and the label is meant for
code with no publisher at all.

### Phase B (built, measure-only): executable memory with no module

`AcModules.MemoryBucket` walks the game's address space with `VirtualQueryEx` (read-only; the map is walked,
nothing in the regions is read) and sums the committed, executable (`PAGE_EXECUTE*`) regions that no loaded
module backs: private or mapped executable memory, or an executable image whose allocation base is not a
loaded module (`RealAnonExecRegions`, replaceable for tests). That is where a manually mapped DLL lives.

Legitimate software makes such regions too: overlays and recorders place small executable trampolines for
their hooks, and GPU drivers may as well. Measured on the clean 2021-04 client this floor is about **0.2 MB**
across a handful of tiny regions, none over ~0.06 MB. A real manual-map injection (ExLoader), by contrast,
added a single **~1.6 MB** executable region. So the signal is sent **only when the total clears a floor**, as a
single label `mem:private-exec:large`.

**The floor comes from the server, not the client.** The launcher is open source, so a constant floor in the repo
would hand a cheat developer the exact number to map under. Instead the session service sends it in the session
reply (`memfloormb`, whole MB), exactly as it already sends the tool list, and `AcSessionClient` passes it to
`AcModules.MemFloorBytes` for that session. It lives in the service's `server.env` (`AC_MEM_FLOOR_MB`), never in
this repo, the exe or a player's config. If the server sends nothing, `MemFloorBytes` is 0 and **no memory label
is produced** - phase B is off until the operator enables it. Pick a value above the measured clean baseline
(~0.2 MB) and below a mapped image; the measurements here are guidance, the operational number stays server-side.

The signal ships **measure-only**: the RustRCON panel keeps the label on the record and shows it, but does not
open a case on it (it is deliberately left out of the panel's `isInjectionLabel`), because the false-positive
sources above are real. Let the panel act on it once the measurement phase has data.

Two limits worth stating: the scan sees a manually mapped DLL only while its memory is still executable (an
injector that maps, runs and frees is caught only if it is resident during a heartbeat), and a process that
JITs code (anything on .NET or Mono) fills private executable memory as normal operation - the Rust client is
IL2CPP (ahead-of-time), so this does not apply to it, but it is why the signal is scoped to the game process.

A refinement the first live sample suggests, for when there is more data: the injection landed as **one** 1.6 MB
region while every benign region was under 0.06 MB, so the **largest single unbacked region** separates them far
more sharply than the total does, and it is harder to trip with benign overlays (many tiny regions). The total is
harder to evade by splitting the map, though. A later version could report on both; today it uses the total only.

### Not in scope

Comparing the in-memory code of `GameAssembly.dll` with the file on disk (patch detection) is possible from
user mode, but it needs relocations applied to a 52 MB image. It is left for a later proposal, if phases A
and B prove useful.

## Expected false positives

Verify each during the measurement phase. None of these is assumed safe without data.

| Source | Expected result |
|---|---|
| Steam overlay (`gameoverlayrenderer64.dll`), Discord overlay | Signed by Valve / Discord: no label |
| NVIDIA / AMD / Intel graphics drivers | Under `%SystemRoot%\System32\DriverStore`: no label |
| Antivirus hooks, input methods (IME), audio enhancers | Usually signed or in `%SystemRoot%`: no label expected; confirm |
| OBS game capture, Medal, Overwolf, MSI Afterburner / RTSS hooks | Signing varies by product and version: **measure**; translate known hashes for staff (below) |
| ReShade and other post-processing mods | Proxy DLL in the game folder: `client:extra-module:dxgi.dll` (or similar). Legitimate, but it is exactly the cheat-loading technique, so staff need to look |
| Players' own modified client files | `client:modified:<name>`: correct, and it is what the existing file check already reports |

**Translating hashes for staff.** As with tools, the service keeps a private `ac_modules.txt`
(`<hash prefix>|<name>|benign|cheat`) and turns `module:unsigned:<hash>` into `module:benign:rtss-hooks` or
`module:cheat:<name>` in its `labels` reply; an unknown hash stays as it is. The kind matters downstream: the
RustRCON panel opens a review case for unknown and cheat modules, not for benign ones. The list stays out of
this repo, the exe and the config. The launcher only reports hashes and never receives the list.

## Privacy

What leaves the PC, in addition to today's labels:

- for game-folder files: the file name, only when it's not part of the official build;
- for any other unsigned module loaded **into the game**: the first 32 hex characters of its SHA-256;
- `module:missing-file` when a loaded module's file is no longer where it was loaded from (nothing about the file);
- `module:scan-failed` when the game process could not be opened at all.

Never sent: module paths, names of modules outside the game folder, signed modules, the module list, memory
contents (phase B sends a bucket, not addresses or bytes). Modules are inspected only inside the game
process the launcher started, never in other programs.

This **changes a promise.** Slice 5 says the launcher does no "handle scan or memory read". Listing a
process's modules and querying its memory map are reads of the game process, not of the player's files or
other programs. Even so:

- the slice-5 wording in `CLIENT_ANTICHEAT.md` changed with this slice;
- the one-time PLAY notice names it ("whether code that isn't part of the game, Windows, or a signed program
  is loaded into the game", as the file's name in the game folder, otherwise as an anonymous fingerprint);
- the notice pref was renamed to `AcNoticeShown2`, so players who saw the old text see the new one once.

A hash of an unsigned file could in theory point to someone's own homemade tool. It is still only a hash, and
only of code running inside the game.

## Rollout

1. **Measure (shadow).** Ship the labels. The session service counts **distinct hashes and how many
   distinct sessions report each**, aggregated, with no SteamIDs in its log (as today). The gate plugin logs
   labels as it already does. Run it for one to two weeks.
2. **Build the translation list** from the most common hashes. Common isn't the same as benign: a popular
   cheat is common too, so staff identify the top entries rather than allow-listing by count.
3. **Use it.** Labels go to OriginAntiCheat as review evidence, weighted by what staff found. They are never
   a kick or ban on their own.

Phase B follows the same three steps separately.

## What is built (phase A)

- `src/AcSession.cs`: the `AcModules` class with the P/Invoke calls (`psapi`, `wintrust`), the
  location/list/signature classifier and its cache (path + size + last-write time), with the module list and
  the signature check replaceable so tests can feed it a fake module list. `StartAcSession` in
  `src/WpfLauncher.cs` adds its labels to the client's `Signals` source; the game process is looked up at each
  scan, so the scan follows the game like the session does. Failures add no labels, except one
  `module:scan-failed` label when the game can't be opened at all.
- `scripts/make_client_filehashes.ps1` defaults to every `.dll`/`.exe` in the client folder (the launcher's
  own exe and the uninstaller, which the installer puts there, left out) plus the il2cpp metadata;
  `config/launcher.cfg` carries the 14 code files of the 2021-04 build.
- `scripts/test_ac_session.ps1`: the classifier against a temp folder (listed / unlisted / Windows / signed /
  unsigned / unreadable / missing modules, the cache, the first-scan delay, the incomplete-list gate,
  `scan-failed`), and a live scan of the test's own PowerShell process (module list read, a validly signed
  module recognised, no labels).
- Docs: the notice text, `CLIENT_ANTICHEAT.md` (slice 10, with the panel contract), `LAUNCHER.md`, `README.md`.

The service's `ac_modules.txt` translation and aggregated counts are built on the dev copy of `ac_session.py`;
OriginSession already prints the labels (`signals` line), and the RustRCON panel consumes them
(`CLIENT_ANTICHEAT.md` slice 10). Nothing is deployed to prod.
