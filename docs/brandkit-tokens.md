# ToolUp.BrandKit — CSS variable contract

**Phase 81.** `ToolUp.BrandKit` is an app-neutral set of Giraffe.ViewEngine helpers that emit brand-shaped semantic markup. The package ships **zero opinionated styling** — only structural HTML + class hooks + a small set of inline-style references to canonical CSS custom properties. Consumers define values for those properties in their `:root` declaration (and optionally per-class rules) to brand the output.

This doc enumerates every CSS variable BrandKit reads. The canonical names are also encoded as `[<Literal>]` constants in `ToolUp.BrandKit.Tokens` so consumer code can reference them programmatically.

## Variable reference

| Variable | Used by | Purpose |
|---|---|---|
| `--bk-font-display` | Display headings, wordmark | Display font family (typically a serif italic) |
| `--bk-font-ui` | (consumer body) | UI / body sans-serif |
| `--bk-font-mono` | Eyebrow, mono labels, timestamps | Monospaced family |
| `--bk-ink` | (consumer body) | Body text colour on light surfaces |
| `--bk-ink-mute` | Eyebrow-mute, secondary labels | Secondary text colour |
| `--bk-paper` | (consumer body) | Base surface background |
| `--bk-panel` | `cardDeep` | Raised surface background — "elevated" card variant |
| `--bk-rule` | `hRule`, `cardOutlined`, dividers | Border / divider colour |
| `--bk-accent` | `eyebrow`, `pillOn`, links | Brand accent / interactive colour |
| `--bk-on-dark-text` | Wordmark on-dark contexts | Text colour on accent / dark surfaces |
| `--bk-positive` | `pillSeverity Positive` | Semantic success colour |
| `--bk-priority` | `pillSeverity Priority`, `Critical` | Semantic alert / warning colour |
| `--bk-info` | `pillSeverity Info` | Semantic informational colour (typically same as `--bk-accent`) |
| `--bk-radius-md` | `pill`, small cards | Small-radius corner |
| `--bk-radius-lg` | `card`, panels | Large-radius corner |
| `--bk-shadow-card` | `card` elevation | Card box-shadow |

## Class hooks (no inline style)

Every BrandKit element carries a class hook the consumer styles independently. The class names follow a strict `bk-<primitive>[-<modifier>]` convention:

- **Text:** `.bk-display`, `.bk-display-lg`, `.bk-display-md`, `.bk-display-sm`, `.bk-eyebrow`, `.bk-eyebrow-mute`, `.bk-mono`, `.bk-mono-sm`, `.bk-mono-md`, `.bk-mono-lg`, `.bk-mono-body`, `.bk-rule`, `.bk-rule-soft`, `.bk-divider-v`
- **Wordmark:** `.bk-wordmark`, `.bk-wordmark-emphasis`
- **Card:** `.bk-card`, `.bk-card-tight`, `.bk-card-deep`, `.bk-card-outlined`
- **Pill:** `.bk-tag`, `.bk-tag-on`, `.bk-tag-dotted`, `.bk-tag-dot`, `.bk-tag-info`, `.bk-tag-positive`, `.bk-tag-priority`, `.bk-tag-critical`
- **Persona:** `.bk-persona`, `.bk-persona-circle`, `.bk-persona-rounded`, `.bk-persona-square`, `.bk-persona-signature`, `.bk-persona-signature-name`
- **PageChrome:** `.bk-header`, `.bk-header-monogram`, `.bk-header-nav`, `.bk-header-nav-link`, `.bk-header-right`, `.bk-footer`, `.bk-footer-copyright`, `.bk-footer-links`, `.bk-footer-link`

A minimal consumer stylesheet wiring the variables + a few hook rules:

```css
:root {
  --bk-font-display: 'Newsreader', Georgia, serif;
  --bk-font-mono:    'IBM Plex Mono', monospace;
  --bk-ink:          #2B2638;
  --bk-ink-mute:     #6C6478;
  --bk-paper:        #F3EEE4;
  --bk-panel:        #FBF8F2;
  --bk-rule:         #E4DBCB;
  --bk-accent:       #6B5FBF;
  --bk-on-dark-text: #E7E2D8;
  --bk-positive:     #6F8A6E;
  --bk-priority:     #7E4550;
  --bk-info:         #6B5FBF;
  --bk-radius-md:    12px;
  --bk-radius-lg:    16px;
  --bk-shadow-card:  0 18px 40px -28px rgba(62, 51, 112, 0.40);
}

.bk-display { font-family: var(--bk-font-display); font-style: italic; }
.bk-display-lg { font-size: 46px; line-height: 1.05; }
.bk-eyebrow { font-family: var(--bk-font-mono); text-transform: uppercase;
              letter-spacing: 0.20em; font-size: 11px; color: var(--bk-accent); }
.bk-eyebrow-mute { color: var(--bk-ink-mute); }
.bk-rule { height: 1px; background: var(--bk-rule); }
.bk-card { background: #fff; border-radius: var(--bk-radius-lg);
           padding: 26px; box-shadow: var(--bk-shadow-card); }
.bk-card-tight { padding: 18px; }
.bk-card-deep { background: var(--bk-panel); }
.bk-card-outlined { background: transparent; border: 1px solid var(--bk-rule);
                    box-shadow: none; }
.bk-tag { display: inline-block; padding: 4px 8px; border-radius: 999px;
          font-family: var(--bk-font-mono); font-size: 10px;
          text-transform: uppercase; letter-spacing: 0.10em;
          background: transparent; color: var(--bk-ink-mute);
          border: 1px solid var(--bk-rule); }
.bk-tag-on { color: var(--bk-accent); border-color: var(--bk-accent); }
.bk-tag-dotted .bk-tag-dot { display: inline-block; width: 6px; height: 6px;
                              border-radius: 50%; background: var(--bk-accent);
                              margin-right: 6px; vertical-align: middle; }
.bk-tag-priority { color: var(--bk-priority); border-color: var(--bk-priority); }
.bk-tag-positive { color: var(--bk-positive); border-color: var(--bk-positive); }
.bk-tag-info { color: var(--bk-info); border-color: var(--bk-info); }
.bk-wordmark { font-family: var(--bk-font-display); font-style: italic;
               font-weight: 500; letter-spacing: -0.02em; }
.bk-wordmark-emphasis { font-style: normal; font-weight: 600; }
.bk-persona { display: block; }
.bk-persona-circle { border-radius: 50%; }
.bk-persona-rounded { border-radius: 12px; }
.bk-persona-square { border-radius: 3px; }
.bk-persona-signature { display: inline-flex; align-items: center; gap: 8px; }
.bk-persona-signature-name { color: var(--bk-ink); font-weight: 500;
                              margin-left: 4px; }
.bk-header { display: flex; align-items: center; justify-content: space-between;
             padding: 16px 24px; border-bottom: 1px solid var(--bk-rule); }
.bk-header-nav { display: flex; gap: 24px; }
.bk-header-nav-link { color: var(--bk-ink-mute); text-decoration: none; }
.bk-footer { display: flex; align-items: center; justify-content: space-between;
             padding: 24px; border-top: 1px solid var(--bk-rule);
             color: var(--bk-ink-mute); font-size: 13px; }
.bk-footer-links { display: flex; gap: 16px; }
.bk-footer-link { color: inherit; text-decoration: none; }
```

Worked examples of the variable + class shape in practice live in:
- ScaleMastery's `wwwroot/css/brand-tokens.css` + `brand.css` (per-app hand-rolled equivalent that pre-dates this package)
- Ella-MAe's `src/Ella-MAe-Client/index.css` (Phase 10 design-system tokens; consumer of this package via the upcoming Wave 13 adoption)
- The Phase 82 worked sample at `samples/PublicSiteWithModules/` (canonical in-tree reference)

## Inline-style references

A handful of primitives apply inline styles when the value is per-call rather than per-brand:

- **`Wordmark`** applies `style="color:<EmphasisColour>;"` on the emphasis span — emphasis colour varies per wordmark instance (on-dark variants typically swap colour), so it can't be encoded as a single class hook.
- **`Persona`** applies `style="object-fit:cover;object-position:center 12%;[border:2px solid <RingColour>;]"` on the `<img>` — the face-crop convention is universal across consumers, and the ring colour is per-call.

All other styling — surface colours, typography, spacing, radii, shadows — comes from the consumer's CSS-variable definitions + class-hook rules.
