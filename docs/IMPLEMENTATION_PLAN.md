# RUSTORIGIN Launcher - Design -> Code Implementation Plan

> **Reconciled 2026-09-19 against the current `WpfLauncher.cs`.** The launcher is built and
> shipping; this doc is now a *status record* of how the Superdesign composition maps to the code
> as it actually stands, not a forward plan. The UI has been intentionally **reduced** from the
> full v1 composition to a focused set (video hero + window controls + hero block + server grid +
> Settings tab), and real launcher behaviour - including **mandatory SHA-256 verification** - has
> been added on top.

**Source design (Superdesign)**
- Project: `b9f5e199-00eb-4043-9e07-e3775dd9ede7` - *RUSTORIGIN Launcher*
- Draft: `36d5d2ba-dc4a-4f28-b270-48fc5a820124` - fetched **v4** -> `.superdesign/tmp/launcher-draft.html`
- Canvas: https://superdesign.dev/teams/908d793f-f95d-45d5-9232-74f2cfa4f45b/projects/b9f5e199-00eb-4043-9e07-e3775dd9ede7?node=draft-variant-36d5d2ba-dc4a-4f28-b270-48fc5a820124

**Target codebase**
- `src/WpfLauncher.cs` - code-only WPF on .NET Framework 4.x (no XAML), compiled with `csc` via
  `scripts/make_release.ps1` (or `scripts/build.bat` for a resource-less dev compile).
- Single-file distribution: video/logo/fonts/config embedded as resources, unpacked at first run.

---

## 1. Design system (extracted from the draft `:root` + utility classes)

Still accurate - these tokens match the code today.

| Token | Value | Code (`WpfLauncher.cs`) | Status |
|---|---|---|---|
| bg | `#0B0D12` | `mainGrid.Background = B("#0B0D12")` | ✅ |
| text-hi / dim / mute | `#FFFFFF` / `#C9CCD6` / `#8A8E99` | `TextHi` / `TextDim` / `TextMute` | ✅ |
| accent / accentHi | `#D14431` / `#E9583F` | `Accent` / `AccentHi` | ✅ |
| online / offline | `#3BD16F` / `#6B7080` | `Online` (Settings status dot) | ✅ · `Offline` removed with the avatar code |
| glass fill / border | `rgba(14,16,22,.55)` / `rgba(255,255,255,.12)` | `Glass` `#8C0E1016` / `Stroke` `#1FFFFFFF` | ✅ |
| glass-hover | `rgba(30,34,44,.65)` | `GlassHi` `#A61E222C` | ✅ |
| outlined pill border | `rgba(255,255,255,.35)` | `StrokeHi` `#59FFFFFF` | ✅ |
| brand font | Montserrat 500/600/700 | `Brand` (bundled Montserrat) | ✅ |
| body font | Inter -> Segoe UI | `FontFamily("Segoe UI")` (Inter not bundled) | ⚠️ Segoe fallback (was T5) |
| radii | window 32 · card 16 · thumb 12 · pill 999 | `R=32` · card `16` · thumb `12` · `23/999` | ✅ |
| icons | Iconify `lucide:*` | Segoe MDL2 Assets glyphs | ✅ (equivalent) |

---

## 2. Layout & component mapping (as rendered today)

`BuildContent()` is the source of truth for what actually renders. It calls exactly:
`BuildTopRight` · `BuildHero` · `BuildServerGrid` · `BuildSettingsPanel`. Everything else below
is absent - the previously-dead builders (`BuildBrandMark`, `BuildFriendsRail`, `BuildBottom`,
plus the `Avatar`/`Offline` helpers) have been **removed** (section 4 T-cleanup, done).

| Design element (draft) | Code | Status today |
|---|---|---|
| Rounded 32px card, 1px stroke, shadow | window chrome + `mainGrid.Clip` + edge Border | ✅ rendered |
| Hero key-visual (CSS gradient scene) | `BuildBackground()` - cross-fading screenshot slideshow (`1.png`-`4.png`) + `BuildGradient()` overlays; gradient fallback | ✅ rendered (real screenshots, upgraded from the CSS mock) |
| Window controls: **gear (Settings) / min / close** | `BuildTopRight()` + `OpenSettings()` / `MinimizeWithFade()` | ✅ rendered (app-only; not in the web mock) |
| Hero: large OG logo | `BuildHero()` `logoBmp` image | ✅ rendered |
| Hero: wordmark (`GameTitle`) | `BuildHero()` @ 54px | ✅ rendered |
| Hero: "JANUARY UPDATE 2021" caption | `BuildHero()` accent caption | ✅ rendered |
| Hero: tagline | `BuildHero()` `Tagline` | ✅ rendered |
| Hero action 1: **PLAY** white pill | `PlayButton()` | ✅ rendered (-> `IN-GAME` while the client runs) |
| Hero action 2: **INSTALL / RESUME** + progress/status | `LinkButton()` -> `StartInstall()`, `progTrack`/`statusText` | ✅ rendered (see section 4 T1) |
| Right column: 6 server cards + DISCOVER MORE | `BuildServerGrid()` / `ServerTile()` | ✅ rendered; each card launches with its args |
| **Settings tab** (Installation/Game/Downloads/Appearance/About) | `BuildSettingsPanel()` + `Build*Page()` | ✅ rendered - **new since the original plan; not in the mock** |
| Hero "Most Played" tag | - | ❌ not rendered (removed) |
| Top-left brand mark | (was `BuildBrandMark()`) | ❌ removed |
| Left rail (icon buttons) | - | ❌ never implemented (no builder) |
| Top-right recent-games pill | `BuildTopRight()` (removed) | ❌ not rendered |
| Top-right user pill (chat/bell/avatar/name) | - | ❌ not rendered |
| Friends rail (8 avatars, status dots) | (was `BuildFriendsRail()`) | ❌ removed |
| Bottom chevron / chat icon | (was `BuildBottom()`) | ❌ removed |
| `pulse` on online dots | - | ❌ moot (no avatars rendered; was T2) |

**Net:** the live UI is the reduced composition (matching the spirit of the v4 edit), plus a full
Settings tab the mock never had. The old plan's "❌ removed in v4 -> keep them" guidance is
superseded: the reductions are the intended shipping state.

---

## 3. Real launcher behaviour (implemented, beyond the static mock)

- **Download**: resumable (`.part` + HTTP Range, up to 30 auto-retries), TLS 1.2/1.3 forced
  (`ConfigureTls`), reuse of a partial only for the same URL+ETag+size - `DownloadWorker` /
  `DownloadRange`.
- **Integrity (mandatory)**: `StartInstall` refuses to download without a configured `Sha256`;
  the finished `.part` is SHA-256-checked (`VerifyDownload` / `ComputeSha256`) **before** it is
  promoted or extracted, and rejected on mismatch. See CLAUDE.md's security section.
- **Extract**: `ExtractZip` to `InstallDir` (`C:\RustOrigin` by default) with zip-slip guards and
  per-file progress.
- **Launch**: `Play` starts `LaunchExe` (default `RustClient.exe`) with per-server or default
  args; single-instance (focuses a running client instead of launching twice).
- **Window/UX**: system-tray icon (`SetupTray`), borderless-window minimize support, optional
  minimize-on-play (`MinimizeInGame` pref, default **off**), restore-on-exit (`WatchGame` /
  `RestoreFromGame`), install-time launcher self-copy + shortcut (`InstallLauncherAndShortcut`).
- **Config**: `launcher.cfg` (embedded defaults, overridden by a copy next to the exe) drives
  URL, hash, install dir, branding, and up to 6 servers - `LoadConfig` / `ApplyConfig`.

---

## 4. Open items / tasks

- **T1 - Hero action 2 is INSTALL/RESUME (design said ADD TO FAVORITE).** *Resolved:* the launcher
  needs an install action, so INSTALL stays; it shows **RESUME** when a partial exists and is
  **hidden once installed** (there is no UPDATE - re-installing overwrites). No favourite toggle.
- **T-cleanup - Remove dead UI code.** *Done (2026-09-19):* `BuildBrandMark`, `BuildFriendsRail`,
  `BuildBottom`, the `Avatar` helper, and the `Offline` brush have been deleted (they were never
  called by `BuildContent`). `Online` is kept - the Settings status dot still uses it.
- **T5 - (Optional) Bundle Inter** for body text to match the design exactly (currently Segoe UI).
  Adds font files to the embedded resources; only worth it if the Segoe fallback bothers you.
- **T6 - Live data (future).** Server player counts and any friends/now-playing data are static
  config strings. Wire to a real source (server query / Discord / Steam) if/when available.

_Dropped from the original plan:_ **T2** (pulse the online dots) is moot - no avatars are rendered.
**T3** (confirm v4 deletions) is resolved - the reductions are the shipping design.

---

## 5. Build & verify

```powershell
# from RustLauncher\
.\scripts\make_release.ps1 -Version <x.y.z>   # embeds assets, applies icon+manifest -> release\RustOrigin.exe
```

Verify: window renders at 1440x860; hero (logo/wordmark/caption/tagline), PLAY, INSTALL, the
6-card server grid, DISCOVER MORE, and the Settings gear are all present; PLAY and server cards
launch the client; tray/minimize/shortcut work.

**Integrity smoke test:** with `Sha256` blank, clicking INSTALL must be refused ("Set Sha256...").
With a correct `Sha256`, INSTALL downloads -> verifies -> extracts. With a deliberately wrong
`Sha256`, the download must be **rejected** ("Integrity check failed...") and nothing installed.

Keep the client zip, the embedded `Sha256`, and the uploaded file in lockstep - see the
**Release / publish checklist** in CLAUDE.md (re-packaging changes the hash).

---

## 6. Status summary

The design is implemented in `WpfLauncher.cs` as an intentionally **reduced** composition (video
hero + window controls + hero block + server grid + Settings tab), plus real launcher behaviour
the mock can't express - most importantly **mandatory SHA-256 verification** of downloads.
Remaining work is optional: **T5** (bundle Inter) and **T6** (live data). **T-cleanup** (delete
dead UI methods) is done. No functional gaps.
