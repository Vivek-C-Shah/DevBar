# DevBar — style lock

Locked: 2026-07-18. Native WPF desktop strip — web asset pipeline (photos/illustrations/GSAP) not applicable; tokens + rules below are the binding contract for all UI work in this repo.

## Mood

"Instrument panel, not dashboard." Flat, dark, terminal-adjacent. The bar must disappear into the desktop when idle and read instantly when expanded. Restraint over delight: no gradients, no drop shadows, no bounce easing. Sass comes from typography (mono accents, letterspaced overlines), status dots, and the accent notch — not from decoration.

## Color tokens (dark, v1)

| Token | Hex | Use |
|---|---|---|
| `Bg` | `#0B0E14` | bar surface — fully opaque; the panel must never let desktop content show through, both for the flat "instrument panel" look and because it can sit above sensitive windows |
| `BgElevated` | `#141926` | chips, hover rows |
| `Hairline` | `#232B3B` | 1px borders only |
| `Text` | `#DCE3EC` | primary text (≈14.6:1 on Bg — AAA) |
| `Muted` | `#8B94A3` | secondary text (≈6.3:1 on Bg — AA) |
| `Accent` | runtime = Windows accent color, lightened until ≥3:1 on Bg; fallback `#5EA1FF` | notch, active dot, module glyph, highlights |
| `Good` | `#3FB950` | active/running states |
| `Warn` | `#D29922` | idle-recent states |
| `Danger` | `#F47067` | kill buttons, errors |

Contrast verified arithmetically (WCAG relative luminance): Text/Bg 14.6:1, Muted/Bg 6.3:1, fallback Accent/Bg 5.0:1. No python on this machine, so `check_contrast.py` was not run; ratios computed by hand.

## Type

- UI: `Segoe UI Variable Text, Segoe UI` — 12px body, 14px semibold titles.
- Overline labels: 10px, `+1.5` letterspacing, uppercase, Muted.
- Data (clipboard previews, ports, paths, timestamps): `Cascadia Mono, Consolas` 10.5–12px.

## Shape & motion

- Radii: 8px chips, 6px buttons, 1–2px notch. Nothing larger.
- Borders: 1px Hairline max. **No shadows, no gradients — ever.**
- Motion: expand 180ms cubic ease-out; collapse 100ms; content fade 120ms. No overshoot, no bounce. Hide is always faster than show.
- Icons: Segoe Fluent Icons glyphs only. No emoji-as-icons.

## Assets

- Logo: constructed geometric mark — accent bar over two muted lines (the strip over the desktop), dark rounded tile. `assets/logo.svg`, exported to `src/DevBar/Assets/devbar.ico`. No letter-in-a-box.
- Photography/illustrations: n/a (native utility app).
