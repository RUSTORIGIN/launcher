# Launcher Styling Guide

Visual and UI conventions for **RustoriginLauncher** — a code-only WPF app whose entire
interface is built imperatively in C# in [`src/WpfLauncher.cs`](../src/WpfLauncher.cs).
No XAML, no `ResourceDictionary`, no `Style` objects. It targets .NET Framework 4.x and
compiles with the `csc.exe` that ships with Windows 10/11 (BCL + `PresentationFramework`,
`PresentationCore`, `WindowsBase`, `System.Xaml`, plus `System.Windows.Forms`/
`System.Drawing` for the tray icon). Everything — screenshots, logo, fonts, `launcher.cfg` —
ships as embedded resources unpacked to `%LOCALAPPDATA%\RustOrigin\assets\<version>\`.

This guide documents the **actual** conventions in the file so new UI stays consistent.
Line references point at the current source.

---

## 1. Principles

1. **XAML-free, on purpose.** Every visual is a `Grid`/`StackPanel`/`Border`/`TextBlock`/
   `Ellipse`/`Path` constructed in C#. This is what keeps the single-file `csc.exe` build
   shippable with no SDK or NuGet. Don't reach for markup, `Style`, or third-party controls.
2. **Fixed design canvas, scaled by a Viewbox.** The UI is authored at a fixed **1440×860**
   and a `Viewbox` (`Stretch.Uniform`) scales it to the window (`BuildContent`, line 820).
   Design in absolute pixels on that canvas — do **not** write responsive/reflow layout for
   the content; the Viewbox handles resize and maximize. The background slideshow fills
   behind it.
3. **Glass over screenshots.** The look is floating translucent panels over a blurred,
   cross-fading screenshot backdrop. Translucency is expressed as **8-digit ARGB hex**
   (`#AARRGGBB`), not opacity on solid colors. This is the core idiom — learn it (§2).
4. **Brand type + tracked capitals.** Montserrat (`Brand`) for almost all text; uppercase
   labels and CTAs get letter-spacing via the `Track()` helper (§3).
5. **Flat and restrained.** Buttons match the site: white primary pills with a **subtle**
   hover (`#F0F1F4`), glass secondaries with a faint fill swap. `DropShadowEffect` is used
   sparingly (cards, the toggle knob, hero logo/text). White is the primary-action color; the
   launcher's own accent is rust-red, and the settings panel uses the site's violet brand.
6. **Build with factory methods.** Controls come from named factories (`PrimaryPill`,
   `UtilityBtn`, `CaptionBtn`, `MakeToggle`, `ServerTile`, `Icon`, `GroupLabel`, `GroupCard`,
   `SettingRow`). Reuse them; don't hand-roll a one-off `Border` that duplicates one.

---

## 2. Color

### The `B()` helper and the named palette (lines 158–170)

All color goes through `B("#hex")`, which parses a hex string into a `SolidColorBrush`.
Hex is either `#RRGGBB` or, for glass/stroke tints, **`#AARRGGBB`** (alpha first).

```csharp
static Brush B(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }

// Brand + text
static readonly Brush Accent   = B("#D14431");   // rust red — primary accent
static readonly Brush AccentHi = B("#E9583F");   // accent hover
static readonly Brush TextHi   = B("#FFFFFF");   // primary text / white CTA fill
static readonly Brush TextDim  = B("#C9CCD6");   // secondary text
static readonly Brush TextMute = B("#8A8E99");   // labels, captions, metadata

// Glass surfaces (note the leading alpha byte)
static readonly Brush Glass    = B("#8C0E1016"); // rgba(14,16,22,.55) — panel fill
static readonly Brush GlassHi  = B("#A61E222C"); // raised glass
static readonly Brush Stroke   = B("#1FFFFFFF"); // rgba(255,255,255,.12) — hairline border
static readonly Brush StrokeHi = B("#59FFFFFF"); // rgba(255,255,255,.35) — outlined pills

static readonly Brush Online   = B("#3BD16F");   // online green
static readonly FontFamily Icons = new FontFamily("Segoe MDL2 Assets");
```

The window background is `#0B0D12` (used in `Background`, `mainGrid`; lines 252, 269).

> **`B()` does not freeze brushes** — that's the current convention, matching the file.
> If you ever want the small perf/immutability win, freeze inside `B()`
> (`var b = new SolidColorBrush(...); b.Freeze(); return b;`) — but only as a deliberate,
> one-place change, since brushes are sometimes recolored on hover by *reassigning* the
> element's `Background`/`Fill` (never by mutating a shared brush), so freezing is safe.

### Conventions

- **Named palette for brand, text, and reusable glass** (`Accent`, `TextHi`, `Stroke`, …).
  Reference the names, not the literals, for these.
- **Ephemeral tints are inline ARGB and that's accepted here** — hover fills like
  `B("#33FFFFFF")`, `B("#26FFFFFF")`, `B("#14FFFFFF")`, `B("#16FFFFFF")`, scrims like
  `B("#66000000")`, and one-off gradient stops appear inline throughout. Keep new ones in
  the same ARGB style. If a tint starts repeating across factories, promote it to a named
  brush in the palette region.
- **Status dot colors** (server badges, `QueryServerStatus`, lines 1166–1219): amber
  `#E0B341` = checking, green `#3FB950` = online, red `#F85149` = offline. Reuse these exact
  values for any new status indicator.
- **One accent.** `Accent`/`AccentHi` is for the toggle-on state, the accent eyebrow, the
  active carousel dot, the caret, the "Done" pill, and the close-button hover. The main
  PLAY/INSTALL CTAs are **white** (`TextHi`) with near-black glyphs (`#12141A`), not accent.

---

## 3. Typography

- **`Brand`** (lines 173–188): bundled Montserrat loaded from the unpacked `fonts/` dir,
  falling back to `Bahnschrift, Segoe UI`. Use `FontFamily = Brand` for essentially all
  visible UI text (titles, labels, buttons, server names).
- **Window default** is `Segoe UI` (line 256) — the base for anything that doesn't set
  `Brand`.
- **`Site`** (`MakeSite()`): bundled **Poppins** (the rustorigin.com typeface), loaded from
  `assets/fonts/Poppins-*.ttf`, falling back to installed Poppins then Segoe UI. Used by the
  website-styled **settings panel** and by the **site-styled buttons** (the white PLAY/INSTALL
  and "Done" pills, and the "DISCOVER MORE" glass pill), so those read like the site's buttons;
  the rest of the launcher uses `Brand`.
- **`Icons`** = `Segoe MDL2 Assets` for glyph icons, via the `Icon(glyph, size, brush)`
  helper (lines 889–896). Caption/social/play glyphs are MDL2 code points.

### `Track()` — letter-spacing (lines 190–194)

WPF `TextBlock` has no letter-spacing, so `Track(text, n)` inserts `n` spaces between every
character. **Use it for all uppercase labels, eyebrows, and button captions.**

```csharp
new TextBlock { Text = Track("RUSTORIGIN", 2), Foreground = Accent, FontFamily = Brand,
                FontWeight = FontWeights.SemiBold, FontSize = 10.5 }   // accent eyebrow
```

### Roles in use (sizes are inline `double`s; there is no shared scale)

| Role                 | Size | Weight    | Brush     | Notes                                  |
|----------------------|------|-----------|-----------|----------------------------------------|
| Hero title           | 54   | SemiBold  | `TextHi`  | `Brand`, `LineHeight = 54` (line 926)  |
| Settings title       | 24   | Bold      | `TextHi`  | line 454                               |
| Server name          | 19   | Bold      | `TextHi`  | `ToUpperInvariant()`, drop shadow      |
| Tagline / body       | 16.5 | Normal    | `TextDim` | wraps, `LineHeight = 25`               |
| Setting row title    | 14   | SemiBold  | `TextHi`  | line 518                               |
| Button caption       | 13–14| SemiBold  | varies    | uppercase + `Track(_, 1)`              |
| Eyebrow / CTA label  | 13   | SemiBold  | `Accent`  | e.g. "JANUARY UPDATE 2021" (line 931)  |
| Section label        | 10.5 | SemiBold  | `TextMute`| `Track(_, 2)`, uppercase (`SectionLabel`) |
| Description / caption| 11–12.5| Normal  | `TextMute`| setting descriptions, status text      |

Sizes are literals at each call site (the file's current style). Match an existing role's
size/weight rather than inventing a new one; only add a value when a genuinely new role
appears.

---

## 4. Layout, spacing & radii

- **Canvas:** author content inside the 1440×860 `homeView` grid; hero pinned left
  (`Margin = 64,0,0,0`, `Width = 470`), server column pinned right (`Margin = 0,0,64,0`).
  A `Viewbox` scales the whole thing (`BuildContent`, line 820).
- **Spacing** is inline `new Thickness(...)` at each site — there are no spacing constants.
  Keep values even and consistent with neighbors (gutters of 64; card padding like
  `30,24,30,26` for the settings card; row spacing `0,8,0,8`).
- **Corner radii in use** — reuse the one that matches the element class:

  | Element                     | Radius | Reference        |
  |-----------------------------|--------|------------------|
  | Window / edge border        | 32 (`CornerR`) | lines 154, 279 |
  | Settings card               | 22     | line 439         |
  | Pills (Play/Link/Accent)    | 23 / 21| lines 973, 567   |
  | Outline / "more" pills      | 19 / 20| lines 556, 1030  |
  | Toggle track                | 13     | line 535         |
  | Server tile (clip / edge)   | 16 / 14| lines 1047, 1136 |
  | Caption / social buttons    | 17 / 20 (circular) | lines 410, 850 |
  | Status badge                | 8      | line 1099        |

- **Z-order in `mainGrid`** (add new layers with this in mind): background slideshow
  (`bgHost`) → legibility scrim → content `Viewbox` → carousel dots → `edgeBorder` →
  caption row → `settingsOverlay` (added last, so it's on top).

---

## 5. Component patterns

Standardize on the existing factories. These are the canonical shapes.

### Pills / buttons

- **`PlayButton()` / `LinkButton()`** — the primary CTAs, styled to match the site's primary
  button ("PLAY NOW"): **white** pill (`Background = TextHi`), near-black glyph+label (`Ink`),
  **Poppins** (`Site`), uppercase + `Track(_,1)`, height 46, radius 23, with a **subtle hover**
  to `#F0F1F4`. `LinkButton` doubles as INSTALL/PAUSE.
- **`PrimaryPill`** — the settings "Done", same **white** site-primary pill as above (radius 20).
- **`UtilityBtn`** — **settings-only** compact rounded-md button (the site's copy-button style),
  covered in *Settings panel* below.
- **"DISCOVER MORE"** (`BuildServerGrid`) — the site's **secondary glass** pill: `white/6` fill,
  no border, `Ink100` label, `Site` font, full radius, hover `white/12`.
- **`CaptionBtn(glyph, onClick, closeBtn)`** — circular 34px glass button (MDL2
  glyph). Others brighten to `#33FFFFFF`; the close button hovers `Accent` (red); glyph
  lifts `TextDim → TextHi`. Marked `WindowChrome.SetIsHitTestVisibleInChrome(b, true)` so
  it's clickable inside the drag area.

All buttons set `Cursor = Cursors.Hand` and handle `MouseLeftButtonUp` with `e.Handled = true`.

### Toggle switch — `MakeToggle(on, onChange)`

A `Border` track + white `Ellipse` knob that animates its `Margin` with a 150ms `CubicEase`
`ThicknessAnimation`. This is a **settings-only** control and now follows the website switch
(track 44×24, live-green on / `white/15` off, 20px knob) — see *Settings panel* below.

### Settings panel (`BuildSettingsOverlay`, 428)

**Exception — this panel is laid out and styled like the rustorigin.com website, not the
launcher's glass system** (a deliberate divergence so launcher settings match the site). It
uses the *website design tokens* block in the palette (mirrored from the site's
`globals.css`) and mirrors the site's page structure:

- **Sheet:** a borderless flat `Ink900` card (radius 14) over a solid dark scrim
  (`#CC0B0B0C`, no frost/blur); content sits in a `ScrollViewer` so it never clips on short
  windows.
- **Header (`SectionHeading` pattern):** violet `Brand400` eyebrow → big uppercase `Ink100`
  title → a short left-aligned **gradient accent rule** (`AccentRule()`, `Brand500` →
  transparent) → an `Ink200` blurb. A flat ghost close sits top-right (hovers `Danger`).
- **Grouped rows:** each group is a `GroupLabel` eyebrow above a `GroupCard` — a raised
  `Ink850` rounded-md card whose rows are split by `white/5` hairlines (rounded-corner
  clipped). Each `SettingRow` has a faint `white/[0.03]` hover and a live-green `MakeToggle`.
- **Actions & footer:** compact rounded-md `UtilityBtn`s (faint fill; glyph+label lift to
  `Brand300` on hover, like the site's copy button), then a `white/5` divider, version text
  (`Ink400`), and the white `PrimaryPill("Done")` (the site's primary button).

The helpers `GroupLabel`, `GroupCard`, `SettingRow`, `MakeToggle`, `UtilityBtn`, `AccentRule`
are **settings-only**, so this website styling does not leak into the rest of the (glass)
launcher. `PrimaryPill` is shared with the hero CTAs (both are the site's white primary button).

> Font: the panel renders in **Poppins** — the site's typeface — via the bundled `Site`
> family (`MakeSite()`, loaded from `assets/fonts/Poppins-*.ttf`, embedded by
> `make_release.ps1` and unpacked by `Assets`). The rest of the launcher still uses
> `Brand` (Montserrat); `Site` is scoped to this panel.

### Server tile — `ServerTile(srv, c1, c2)` (1043)

Cover-image card 340×150, clipped to radius 16. Bottom gradient scrim for text legibility,
uppercase `Brand` name (bold, drop shadow) + tag, a live status badge (status dot + count,
`#B3000000` pill). On hover: cover blurs (animated `BlurEffect` 0→10) and a "CLICK TO JOIN"
overlay fades in (140ms). 1px `#1FFFFFFF` inner edge. Falls back to a `Grad(c1, c2)`
diagonal gradient when no cover image.

### Small helpers

- **`Icon(glyph, size, brush)` (889)** — MDL2 icon `TextBlock`, centered.
- **`Grad(c1, c2)` (898)** — diagonal (`0,0→1,1`) two-stop `LinearGradientBrush`.
- **`SectionLabel(t)` (506)** — tracked, uppercase, muted group label.
- **Social buttons (`SocialButton`, 843)** — circular 40px glass; brand marks are inline
  `Path` vectors (`SocialGlyph`, 24×24 viewBox, Simple Icons / CC0) that recolor on hover.
  Add a platform by adding a case to `SocialGlyph`; unknown keys return `null` (skipped).

---

## 6. Window chrome (frameless, rounded, but native)

The window is frameless **and** a real native window — deliberately **not** layered
(`AllowsTransparency = false`, line 251) so it keeps native minimize/maximize/restore
animations, Aero Snap, taskbar and the system menu.

- **Rounding is done twice**, and both must stay in sync with `CornerR` (32):
  1. `SetWindowRgn` with `CreateRoundRectRgn` clips the actual OS window in **device
     pixels** (`ApplyWindowRegion`, 643) — re-applied on size change and `OnDpiChanged`.
  2. A visual `RectangleGeometry` clip on `mainGrid` + the 1px `edgeBorder` give the
     anti-aliased corner and hairline (`ApplyRounding`, 387).
  Corners go **square when maximized** (region cleared, radius 0).
- **`WindowChrome`** (260): `CaptionHeight = 0`, `ResizeBorderThickness = 6`,
  `GlassFrameThickness = 0`, `UseAeroCaptionButtons = false`. There's no native caption —
  min/settings/close are custom `CaptionBtn`s in `BuildCaption` (394).
- **Drag from anywhere:** `MouseLeftButtonDown` sends `WM_NCLBUTTONDOWN/HTCAPTION`
  (line 293), gated by `IsInteractive` (199) — which treats any element with a **Hand
  cursor** as a control to skip. So the rule below matters:

  > **Every clickable element must set `Cursor = Cursors.Hand`.** That's how the
  > drag-from-anywhere logic knows to let the click through instead of moving the window.
  > Conversely, decorative overlays set `IsHitTestVisible = false` so they never swallow clicks.

- **Maximize** is kept inside the monitor work area via `WM_GETMINMAXINFO` (`WndProc`, 670),
  so a maximized window doesn't cover the taskbar. Double-click an empty area toggles it.

---

## 7. Interaction & state conventions

- **Hover** = reassign `Background`/`Foreground`/`Fill` in `MouseEnter`/`MouseLeave` to a
  new brush (never mutate a shared brush). Keep swap colors in the ARGB style of §2.
- **Disabled** = `SetButtonEnabled` (1003): `IsEnabled = false`, `Opacity = 0.45`,
  `Cursor = Cursors.Arrow` (drops the Hand marker so it's inert to drag logic too).
- **Animations** are short and eased: 140–150ms for hovers/toggles (`CubicEase`), ~900ms
  for the background cross-fade. Use `BeginAnimation` on the property; keep durations in
  that range for consistency.
- **Effects**: `DropShadowEffect` for lift (cards, toggle knob, hero logo/text, drop-shadowed
  labels over imagery); `BlurEffect` for glass (background `Radius = 14` at half-res via
  `BitmapCache`; settings frost `Radius = 18`; server-hover blur). Keep `RenderingBias =
  Performance` on blurs, as the existing code does.
- **Bitmaps**: set `RenderOptions.SetBitmapScalingMode(img, HighQuality)` on displayed
  images (backgrounds, covers, logo).

---

## 8. Code conventions for UI construction

- **Organized by banner comments**, not `#region`: `// ---- palette ----`,
  `// ---- ui refs ----`, `// ---------- content ----------`, etc. Add new UI next to its
  peers under the matching banner.
- **UI refs are fields** on `LauncherWindow` (`playBtn`, `progFill`, `settingsOverlay`, …) so async/state code can update them; screen-building `Build*` methods
  compose factories.
- **One factory per control shape**, named for what it produces; a screen method composes
  factories rather than building primitives inline.
- **Config-driven content**: user-facing strings/servers/socials come from `launcher.cfg`
  (`ApplyConfig`, 1244) and per-user `Prefs`, not hardcoded — keep new tunables there.

---

## 9. Do / Don't

| Do                                                        | Don't                                                    |
|-----------------------------------------------------------|----------------------------------------------------------|
| Use `B()` + named brushes for brand/text/glass            | Introduce a color system outside `B()`/ARGB hex          |
| Write translucency as `#AARRGGBB` (alpha first)           | Use `Opacity` on a solid color to fake glass             |
| Wrap uppercase labels/CTAs in `Track()`                   | Ship untracked ALL-CAPS labels                            |
| Use `FontFamily = Brand` for visible text; `Icons` for glyphs | Add a new font file (breaks the embedded-only build) |
| Author content on the 1440×860 canvas + Viewbox           | Write responsive/reflow layout for the content           |
| Reuse `AccentPill`/`OutlineBtn`/`CaptionBtn`/`MakeToggle`/`ServerTile` | Re-derive an existing control by hand         |
| Set `Cursor = Cursors.Hand` on every clickable            | Leave a control without it (breaks drag-from-anywhere)   |
| Keep white as the primary CTA, accent for one highlight   | Turn PLAY accent or add a second accent per view          |
| Keep `SetWindowRgn` radius, `ApplyRounding`, and `CornerR` in sync | Change the window radius in only one place       |
| Match hover/animation timings to 140–150ms + `CubicEase`  | Add long or unelased UI transitions                       |
| Stay XAML-free, SDK-free, NuGet-free                      | Reach for `Style`/`ResourceDictionary`/a control library |

---

*Keep this guide beside [`src/WpfLauncher.cs`](../src/WpfLauncher.cs). When you add a control,
add its factory under the right banner; if it needs a genuinely new color, add a named brush
to the palette region (lines 158–170) first, then use the name.*
