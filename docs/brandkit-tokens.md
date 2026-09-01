# ToolUp.BrandKit — CSS variable contract

**Phase 81; corrected at [Phase 738](migrations/738-brandkit-token-doc-contract-correction.md).** `ToolUp.BrandKit` is an app-neutral set of Giraffe.ViewEngine helpers that emit brand-shaped semantic markup. The package ships **zero opinionated styling** — only structural HTML and `bk-*` class hooks.

**The theming contract is class hooks, not emitted variable references.** BrandKit emits **no** `var(--bk-…)` reference and no token-bearing inline style anywhere. The `--bk-*` names below are the vocabulary the contract is written in: a consumer defines a value for each in its own `:root` declaration and attaches it, in its own stylesheet, to the class hook that carries it. The only inline styles the package emits are the two per-call literal values recorded under [Inline-style references](#inline-style-references) below, and neither references a token.

This doc enumerates every CSS variable BrandKit's contract names, and the class hook each is carried by. The canonical names are also encoded as `[<Literal>]` constants in `ToolUp.BrandKit.Tokens` so consumer code can reference them programmatically. [Phase 197](migrations/197-brandkit-visual-snapshot-theming-contract.md)'s contract pack makes this table executable — every row's hook is asserted to be emitted by the primitive named, a token declared with no row here fails a parity case, and a `var(--bk-` reference appearing in any rendered markup fails a case of its own.

## Variable reference

| Variable | Carried by (class hook) | Emitted by | Purpose |
|---|---|---|---|
| `--bk-font-display` | `.bk-display`, `.bk-wordmark` | Display headings, wordmark | Display font family (typically a serif italic) |
| `--bk-font-ui` | `.bk-page` | `LayoutShell` body (Phase 92) | UI / body sans-serif |
| `--bk-font-mono` | `.bk-eyebrow`, `.bk-mono` | Eyebrow, mono labels, timestamps | Monospaced family |
| `--bk-ink` | `.bk-page` | `LayoutShell` body (Phase 92) | Body text colour on light surfaces |
| `--bk-ink-mute` | `.bk-eyebrow-mute` | Eyebrow-mute, secondary labels | Secondary text colour |
| `--bk-paper` | `.bk-page` | `LayoutShell` body (Phase 92) | Base surface background |
| `--bk-panel` | `.bk-card-deep` | `cardDeep` | Raised surface background — "elevated" card variant |
| `--bk-rule` | `.bk-rule`, `.bk-card-outlined` | `hRule`, `cardOutlined`, dividers | Border / divider colour |
| `--bk-accent` | `.bk-eyebrow`, `.bk-tag-on` | `eyebrow`, `pillOn` (and consumer link rules) | Brand accent / interactive colour |
| `--bk-on-dark-text` | `.bk-wordmark` | Wordmark on-dark contexts | Text colour on accent / dark surfaces |
| `--bk-positive` | `.bk-tag-positive` | `pillSeverity Positive` | Semantic success colour |
| `--bk-priority` | `.bk-tag-priority`, `.bk-tag-critical` | `pillSeverity Priority`, `Critical` | Semantic alert / warning colour |
| `--bk-info` | `.bk-tag-info` | `pillSeverity Info` | Semantic informational colour (typically same as `--bk-accent`) |
| `--bk-radius-md` | `.bk-tag`, `.bk-card-tight` | `pill`, small cards | Small-radius corner |
| `--bk-radius-lg` | `.bk-card` | `card`, panels | Large-radius corner |
| `--bk-shadow-card` | `.bk-card` | `card` elevation | Card box-shadow |

`--bk-font-ui`, `--bk-ink` and `--bk-paper` read "(consumer body)" here until Phase 738. That was accurate at Phase 81; [Phase 92](platform/layouts.md)'s `LayoutShell` gave the document body the BrandKit-emitted `.bk-page` hook, so all three are carried by a hook the package emits rather than by a body the consumer has to class itself.

## Class hooks (the whole theming contract)

Every BrandKit element carries a class hook the consumer styles independently — this is the *only* channel through which a `--bk-*` value reaches rendered output. The class names follow a strict `bk-<primitive>[-<modifier>]` convention:

- **Page shell:** `.bk-page` (the `LayoutShell` document body, Phase 92), `.bk-skip-link`
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
  --bk-font-ui:      'Inter', system-ui, sans-serif;
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

.bk-page { font-family: var(--bk-font-ui); color: var(--bk-ink);
           background: var(--bk-paper); margin: 0; min-height: 100vh; }
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
- The Phase 82 worked sample at `samples/PublicSiteWithModules/` (canonical in-tree reference)

## Inline-style references

Two primitives apply an inline style when the value is per-call rather than per-brand. **Both carry a literal value supplied by the caller — neither references a `--bk-*` token**, and these are the only inline styles the package emits:

- **`Wordmark`** applies `style="color:<EmphasisColour>;"` on the emphasis span — emphasis colour varies per wordmark instance (on-dark variants typically swap colour), so it can't be encoded as a single class hook.
- **`Persona`** applies `style="object-fit:cover;object-position:center 12%;[border:2px solid <RingColour>;]"` on the `<img>` — the face-crop convention is universal across consumers, and the ring colour is per-call.

All other styling — surface colours, typography, spacing, radii, shadows — comes from the consumer's CSS-variable definitions + class-hook rules.
