# RustOrigin - Design System

The visual language of the RustOrigin launcher, exported so a website can match it.
Everything below is in [`rustorigin.css`](./rustorigin.css) as CSS variables + utility classes.
See [`style-guide.html`](./style-guide.html) for a live reference page.

## The idea in one line
**Frosted glass floating over cinematic game art, with a rust-red accent and a
Montserrat wordmark.** Dark, moody, premium - panels are translucent and blurred
so the background bleeds through; type is crisp; motion is soft and eased.

## Color

| Token | Hex | Use |
|---|---|---|
| `--bg` | `#0B0D12` | App / page background |
| `--bg-deep` | `#0A0C11` | Deepest surface, scrim base |
| `--accent` | `#D14431` | Brand rust-red - primary accent |
| `--accent-hi` | `#E9583F` | Accent hover / gradient end |
| `--accent-glow` | `#F0613F` | Captions, small accent text, glows |
| `--text-hi` | `#FFFFFF` | Headings, primary text |
| `--text-dim` | `#C9CCD6` | Body copy |
| `--text-mute` | `#8A8E99` | Labels, captions |
| `--online` | `#3BD16F` | Online / ready status |
| `--offline` | `#6B7080` | Offline status |

Accent is used sparingly - one red thing per view (the caption, an active nav item,
a danger action). Never large red fills.

## Glass (the signature)
Two recipes, both real `backdrop-filter` blur:

- **`.glass`** - bright frosted panel: `linear-gradient(155deg, rgba(255,255,255,.09), rgba(255,255,255,.03))`, `blur(26px) saturate(150%)`, `1px rgba(255,255,255,.16)` border, layered shadow with a **bright inset top edge** (`inset 0 1px 0 rgba(255,255,255,.22)`). Use for rails, pills, nav capsules.
- **`.glass-dark`** - content cards that hold text: darker fill `rgba(16,18,26,.52)`, softer border. Use for settings cards, server tiles.

Every glass surface gets: a **light-catching top edge**, a **soft drop shadow** for float,
and generous rounding. Over bright imagery, dark cards keep text legible.

> WPF note: the launcher fakes `backdrop-filter` (which WPF lacks) by sampling a
> half-res blurred copy of the video behind each panel. On the web you get it free
> with `backdrop-filter` - the tokens above reproduce the same look.

## Typography
- **Brand / display:** Montserrat - 800 (wordmark), 700 (headings, buttons), 600 (labels). Wordmark is uppercase, tracked `.04em`.
- **Body:** Inter - 400/500/600.
- **Micro-labels:** Montserrat 600, UPPERCASE, tracked `.12em`, color `--text-mute`.
- **Tags:** uppercase, tracked `.28em`.

Scale (px): wordmark 54-76 · section heading 30-32 · card stat 26 · body 16.5 · caption 13 · label 11-12.

## Radii
`--r-pill 999` · `--r-xl 24` (rails/nav) · `--r-lg 18` (cards/tiles) · `--r-md 16` · `--r-sm 12` (buttons).

## Spacing & layout
- **Symmetric `--gutter: 64px`** side padding - left and right margins match.
- Card padding 24px. Nav item height 52px. Grid gaps 16px.
- Hero is vertically centered; content columns share the 64px gutter.

## Components (classes in the CSS)
- `.glass`, `.glass-dark` - panels
- `.tag` - outlined pill label
- `.btn-play` - primary solid-white pill · `.btn-ghost` - glass/outline · `.btn-danger` - red · `.icon-btn` (`.close`) - round window/action buttons
- `.nav-item` / `.nav-item.active` - settings sidebar
- `.danger-zone` - destructive card
- `.bar > .fill` - progress / disk usage (accent gradient)
- `.dot.online` / `.dot.offline`, `.label`

## Motion
- Easing `--ease: cubic-bezier(.22,.61,.36,1)` (CubicEase ease-out, matches the launcher's tab transitions), `--dur: 240ms`.
- Views slide up ~18-34px + cross-fade in; hovers fade (never snap).
- `.fade-up` utility for entrances. Honors `prefers-reduced-motion`.

## Cinematic background
Full-bleed image/video in `.hero-bg`, then `.hero-scrim` adds the dim + vignette
that keeps glass and text readable. Then float `.glass` panels on top.

## Fonts to load
```html
<link href="https://fonts.googleapis.com/css2?family=Montserrat:wght@500;600;700;800&family=Inter:wght@400;500;600&display=swap" rel="stylesheet">
```
