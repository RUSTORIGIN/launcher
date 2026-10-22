# Rustorigin Launcher

Windows launcher for a community Rust (the *game*, April 2021 build) server: downloads, SHA-256-verifies,
extracts and launches the client; updates it in place from a delta pack; self-updates from signed GitHub
Releases. C# 5 / .NET Framework 4.x WPF, code-only (no XAML), compiled by Windows' own `csc.exe`.

## Commands (repo root, PowerShell)
- Compile check (fast, no assets embedded): `scripts\build.bat` -> `RustoriginLauncher.exe` in repo root (git-ignored)
- Release build: `.\scripts\make_release.ps1 -Version <x.y.z>` -> `release\RustoriginLauncher.exe`
  - It REWRITES `AssemblyVersion` in `src\WpfLauncher.cs`: pass the version already in that file, then `git diff` it
- Tests (one script = one suite; no per-case filter):
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test_<suite>.ps1`
  suites: `updater_parsing` (UpdateParsing.cs), `a2s_parsing` (A2S.cs), `client_patch` (ClientPatch.cs + builder),
  `ac_session` (AcSession.cs), `folder_pin` (FolderPin.cs: install-folder link checks)
- Run: `release\RustoriginLauncher.exe`; add `--dev-config` to use a `launcher.cfg` next to the exe
- Installers: `scripts\build_installer_exe.ps1 -Version x.y.z` (NSIS 3.12), `scripts\build_msi.ps1` (WiX 5.0.2, never v6+)
- No linter/formatter exists. CI (`.github/workflows/build-check.yml`) = release build + the 5 suites on PowerShell 7

## Architecture
- `src/WpfLauncher.cs` (~4k lines): `App.Main`, `Assets`, `LauncherWindow` = UI, config, downloader, install,
  play, self-update. Grep method names (`DownloadWorker`, `FetchVerified`, `ApplyConfig`, `RefreshState`,
  `SecureInstallDir`, `UpdateCheckWorker`, `Play`) instead of reading it whole
- Pure, WPF-free, unit-tested: `UpdateParsing.cs` (release JSON, SHA256SUMS, signature), `A2S.cs` (server status),
  `ClientPatch.cs` (delta pack: recipe, Stage, Commit), `AcSession.cs` (anti-cheat session protocol + client),
  `FolderPin.cs` (install-folder link checks)
- `DiscordRpc.cs`: Rich Presence over the local named pipe
- `config/launcher.cfg`: host config, embedded into the exe (the trust anchor). `prefs.cfg`: per-user `Prefs`
- `scripts/`: build, package client, make delta pack, sign release, tests; `scripts/installer/`: NSIS + WiX
- Reference docs: `docs/LAUNCHER.md` (behavior, config keys, runtime files, release checklist),
  `docs/STYLING.md` (UI), `docs/CLIENT_ANTICHEAT.md` (session protocol)
- `.claude/agents/` (launcher-explorer, build-and-test, security-reviewer) and `.claude/rules/` (loaded per path)

## Conventions
- C# 5 only: no `$""`, `?.`, `nameof`, `catch ... when`, `out var`, expression-bodied members, tuples
- No NuGet / no new assemblies. A new `src/*.cs` must be added to `build.bat`, `make_release.ps1` (and a test)
- Naming: PascalCase types/methods/config fields (`DownloadUrl`), camelCase private fields; `_camel` only in DiscordRpc
- Comments say why, above the member, in full sentences; match the surrounding density
- Non-critical paths: `try { ... } catch { }` + `Log(...)` (best effort). Integrity/security failures fail CLOSED
- Background work: `new Thread { IsBackground = true }`; UI only via `Dispatcher.BeginInvoke`; cooperative
  cancel via `cancelRequested`. `DispatcherTimer` handlers run on the UI thread: keep them cheap
- Status line: `ShowStatus(brush, text, untilNextAction)` / `ClearStatus()`; never assign `statusText.Text`
- New `launcher.cfg` key: parse in `ApplyConfig` (lowercase switch), document in `docs/LAUNCHER.md` + `README.md`.
  Per-user toggles go in `Prefs`, not `launcher.cfg`
- Testable logic goes in a WPF-free file and gets a case in its `scripts\test_*.ps1`
- Line endings: LF, but CRLF for `*.bat` / `*.ps1` (`.gitattributes`)

## Security invariants (never weaken without an explicit request)
- Client zip and delta pack are SHA-256-verified before use; no hash = no install
- Release builds ignore a `launcher.cfg` next to the exe unless `--dev-config`; `AcSessionUrl`, `AcClientFile`,
  `UpdatePublicKey` come only from the embedded config
- Install folder is fail-closed: install/update/PLAY only from a folder writable by the player, SYSTEM and
  Administrators alone (`SecureInstallDir`, `RequirePrivateInstallDir`)
- Self-update installs only releases whose `SHA256SUMS.txt` is signed with `UpdatePublicKey`; CI only makes drafts;
  `scripts\sign_release.ps1` (offline key) signs + publishes
- Downloads are https only (incl. redirects); PLAY runs exactly `InstallDir\LaunchExe`
- Changing any of this: update `SECURITY.md` + `docs/LAUNCHER.md`, and add a test

## Gotchas
- CI's PowerShell 7 (.NET 8) `Add-Type` treats warnings as errors: obsolete APIs (e.g. `RNGCryptoServiceProvider`)
  break CI while compiling fine locally. `test_ac_session.ps1` uses `-IgnoreWarnings` on purpose (`HttpWebRequest`)
- Shell tools can eat backslashes in heredocs: write `.py`/`.ps1` helper scripts with the Write tool instead
- Never test against the real install (`C:\Rustorigin`) or real cache (`%LOCALAPPDATA%\Rustorigin`): use a scratch
  `InstallDir` via `--dev-config` (+ `installdir.txt` in the cache), and clean up what you created
- Never run the NSIS uninstaller: it deletes `%LOCALAPPDATA%\Rustorigin`
- `release/`, `RustClient.zip`, `*.patchpack`, root `RustoriginLauncher.exe` are build output (git-ignored)
- Uploading to the CDN, tagging, publishing a release: outward-facing - only on an explicit request
- `main` is PR-only (ruleset); work on a branch

## Workflow
- Before saying a change is done: compile (`build.bat` or `make_release.ps1`) and run the suites for the files
  you touched; UI changes: run the exe and look
- If a change touches download, update, signature or install-folder code (`FetchVerified`, `Download*`, `ExtractZip`,
  `PieceHashes`/`FindBadPieces`, `ClientPatch.cs`, `UpdateParsing.cs`, `UpdateCheckWorker`/`PerformUpdate`,
  `SecureInstallDir`/`LockFolder`/`FolderExposure`, `sign_release.ps1`): run the `security-reviewer` agent before
  saying it's done
- Commit only when asked; never commit `release/`, keys, or the client zip
- When compacting, preserve: modified files, branch/PR, test results, open decisions, and anything not yet verified
