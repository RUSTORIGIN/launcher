# Anti-cheat session: loaded-module signal (proposal)

Status: **proposal, not built.** It extends the evidence labels of the launcher session
([CLIENT_ANTICHEAT.md](CLIENT_ANTICHEAT.md), slice 5) and would ship as its own slice after the
enforcement switch. Nothing here changes the protocol: it only adds labels to the heartbeat's `signals`.

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
- **Signed cheats.** A cheat signed with a trusted (stolen or bought) certificate is not reported.
- **A fake launcher.** The launcher is open source, so a fake one can simply report nothing. That is the
  limit of the whole session design, not of this signal.

## What it checks

### Phase A: the module list

On the session thread, using the `Process` that `Play` started:

1. **Open the game read-only:** `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ)` and
   `EnumProcessModulesEx(LIST_MODULES_ALL)` + `GetModuleFileNameExW`. The launcher is built `anycpu` without
   the 32-bit preference, so it runs as 64-bit and sees the 64-bit game's modules. Nothing is written,
   suspended or injected.
2. **When:** the first scan 60 s after the game starts (Unity loads plugins lazily), then at every
   heartbeat. Listing the modules takes milliseconds; only modules not seen before are classified, so a
   scan after the first one costs almost nothing.
3. **Classify each new module by where its file lives**, cached by path + size + last-write time:

| Module location | Check | Label when it fails |
|---|---|---|
| The client folder (the folder of `RustClient.exe`) | Size + SHA-256 against the hosted build's list of code files (`AcClientFile=`, extended to every `.dll`/`.exe` in the build: 14 files for the 2021-04 client) | `client:modified:<name>` when it's listed but differs; `client:extra-module:<name>` when it isn't listed (the proxy-DLL case) |
| `%SystemRoot%` (System32, SysWOW64, WinSxS, DriverStore) | None: writing there needs admin rights. These are Windows and the GPU drivers. | none |
| Anywhere else | Authenticode via `WinVerifyTrust`, with no revocation check and no network fetch | `module:unsigned:<first 32 hex of SHA-256>` when there is no signature, a bad hash, or an untrusted or self-signed root |

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

### Phase B (later, measure first): executable memory with no module

`VirtualQueryEx` over the game's address space, counting regions that are executable (`PAGE_EXECUTE*`) and
either private memory (`MEM_PRIVATE`) or an image that isn't in the module list. That is where a manually
mapped DLL lives. Legitimate software makes such regions too: overlays and recorders place small executable
trampolines for their hooks, and GPU drivers may as well. So phase B reports a **size bucket only**
(`mem:private-exec:none|small|large`, thresholds set from the measured baseline) and starts in measure-only
mode. It is the weakest signal here, and it is optional.

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

**Translating hashes for staff.** As with tools, the service would keep a private `ac_modules.txt`
(`<hash prefix>|<name>|benign|cheat`) and turn `module:unsigned:<hash>` into
`module:unsigned:rtss-hooks` in its `labels` reply. The list stays out of this repo, the exe and the
config. The launcher only reports hashes and never receives the list.

## Privacy

What leaves the PC, in addition to today's labels:

- for game-folder files: the file name, only when it's not part of the official build;
- for any other unsigned module loaded **into the game**: the first 32 hex characters of its SHA-256.

Never sent: module paths, names of modules outside the game folder, signed modules, the module list, memory
contents (phase B sends a bucket, not addresses or bytes). Modules are inspected only inside the game
process the launcher started, never in other programs.

This **changes a promise.** Slice 5 says the launcher does no "handle scan or memory read". Listing a
process's modules and querying its memory map are reads of the game process, not of the player's files or
other programs. Even so:

- the slice-5 wording in `CLIENT_ANTICHEAT.md` has to change with this slice;
- the one-time PLAY notice must name it ("whether code that isn't part of the game, Windows, or a signed
  program is loaded into the game, as an anonymous fingerprint");
- the notice pref has to be renamed (e.g. `AcNoticeShown2`) so players who saw the old text see the new one.

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

## Build outline (for when it's approved)

- `src/AcSession.cs`: an `AcModules` class with the P/Invoke calls (`psapi`, `wintrust`), the
  location/hash/signature classifier and its cache, written so tests can feed it a fake module list. It joins
  the client's `Signals` source; failures add no labels, except one `module:scan-failed` label when the game
  can't be opened at all (expected only if the game was started elevated).
- `scripts/make_client_filehashes.ps1`: default to every `.dll`/`.exe` in the client folder.
- `scripts/test_ac_session.ps1`: the classifier against a temp folder (listed / modified / extra /
  unsigned / signed files), plus a live scan of the PowerShell process itself (Windows PowerShell loads
  Windows and Microsoft-signed modules, so it should produce no labels).
- Service: `ac_modules.txt` translation and the aggregated counts; plugin: nothing new (labels already flow).
- Docs: this file becomes a section of `CLIENT_ANTICHEAT.md`; update the notice text, `README.md` and
  `CLAUDE.md`.
