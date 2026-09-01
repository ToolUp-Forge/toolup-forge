# Client-toolkit theming tokens

The Fable/Elmish **client toolkit** (`Toolup.UIToolkit.Tokens` + the `Kpi` / `Data` /
`StateViews` / `Forms` / `Typography` / `Layout` primitives) and the app shell render against a small
set of CSS custom properties, mirroring the SSR `ToolUp.BrandKit` `--bk-*` contract: **colours and
shape are CSS-variable-parameterised, never hardcoded.** Override the values in a `:root` *after* the
canonical `@toolup/tailwind` block to re-skin the whole client surface — no rebuild, no source change.

The token **names match the consumer-side design-kit + the six `--color-*` shell tokens**, so an app's
palette overrides flow straight through to the toolkit.

Since Phase 307 the toolkit components ship in the standalone **`ToolUp.Platform.UI`** package
(`Layout` stayed in `ToolUp.Platform.Client` — it composes the sidebar and the SDK's page-content
types). The namespace is unchanged and the client tier depends on the package, so this contract reads
the same from either side of the split.

## Tokens

| Token | Role | Default (current look) |
|---|---|---|
| `--surface` | card / input / tile background | `#ffffff` |
| `--surface-2` | inset / subtle fill | `var(--color-gray-50)` |
| `--text-strong` | headings, KPI values, body-strong | `var(--color-gray-900)` |
| `--text` | body / secondary text | `var(--color-gray-600)` |
| `--muted` | labels, captions, hints | `var(--color-gray-500)` |
| `--pos` | positive delta / success | `var(--color-green-600)` |
| `--neg` | negative delta / error | `var(--color-red-600)` |
| `--radius` | card / input / button corner | `0.5rem` (= `rounded-lg`) |
| `--radius-pill` | pill row labels | `9999px` |
| `--shadow` | card elevation | `0 0 #0000` (none) |

Defaults are referenced from Tailwind's own scale, so a deployment that overrides **nothing** renders
byte-for-byte as before this contract landed (backward-compatible — GP 11).

## Usage

```css
/* consumer index.css, AFTER the >>> end @toolup/tailwind canonical <<< marker */
:root {
  --surface:      #ffffff;
  --text-strong:  #16141f;
  --muted:        #6f6c7e;
  --pos:          #15803d;
  --neg:          #dc2626;
  --radius:       12px;
  --radius-pill:  9999px;
  --shadow:       0 1px 2px rgba(20,18,31,.05), 0 4px 12px rgba(20,18,31,.045);
}
```

Every toolkit component that reads these re-skins at runtime. The colour subset
(`--surface` aside — brand/sidebar/status) is also the surface that per-team branding
(Phases 5e / 223) injects per active team; **fonts and component shape are deliberately not
team-overridable.**

## Scope / not yet covered

- Type-scale tokens (`--fs-*`) are not yet introduced — toolkit type stays on Tailwind's `text-*`
  scale until the `Typography` follow-up.
- Pill / chip *background* fills in `Data` remain on the Tailwind gray scale (only their text is
  tokenised); a later pass can tokenise those.
- AgChart series colours are themed via the `ChartPalette` accessor (Phase 222), not these utilities,
  because AG Charts requires literal colour strings.
