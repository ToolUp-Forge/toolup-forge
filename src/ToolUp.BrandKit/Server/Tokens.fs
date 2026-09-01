module ToolUp.BrandKit.Tokens

/// The canonical CSS custom-property NAMES the BrandKit theming contract
/// is written in. A consumer defines a value for each name in its own
/// `:root` declaration and attaches it, in its own stylesheet, to the
/// `bk-*` class hooks every BrandKit primitive and layout emits. Those
/// class hooks are the whole of the theming contract.
///
/// **The package emits no `var(--bk-…)` reference and no token-bearing
/// inline style.** These literals are documentation plus a programmatic
/// handle (`HostThemeTokens.ofBrandKitValues` projects supplied values
/// onto this set) — never a value BrandKit substitutes into markup. The
/// only inline styles emitted anywhere are the two per-call literal
/// values `docs/brandkit-tokens.md` records under "Inline-style
/// references": a wordmark's emphasis colour and a persona's optional
/// ring colour, neither of which references a token.
///
/// Phase 197's contract pack pins both directions — every hook named
/// below is asserted to be emitted by the primitive that names it, and a
/// separate case fails if a `var(--bk-` reference ever appears in any
/// rendered markup. So introducing token substitution later would be a
/// deliberate, visible change rather than a silent one.
///
/// Sane default VALUES are documented in `docs/brandkit-tokens.md`,
/// whose variable table this list mirrors. The package itself ships zero
/// opinionated styling — only structural markup + class hooks.
///
/// Token names, and the class hook(s) that carry each:
///   `--bk-font-display` — display headings / wordmark (`.bk-display`, `.bk-wordmark`)
///   `--bk-font-ui`      — body / UI text (`.bk-page`, the Phase 92 `LayoutShell` body hook)
///   `--bk-font-mono`    — data / timestamps / eyebrows (`.bk-eyebrow`, `.bk-mono`)
///   `--bk-ink`          — body text colour on light surfaces (`.bk-page`)
///   `--bk-ink-mute`     — secondary / muted text (`.bk-eyebrow-mute`)
///   `--bk-paper`        — base surface background (`.bk-page`)
///   `--bk-panel`        — raised-surface background (`.bk-card-deep`)
///   `--bk-rule`         — border / divider colour (`.bk-rule`, `.bk-card-outlined`)
///   `--bk-accent`       — brand accent / interactive colour (`.bk-eyebrow`, `.bk-tag-on`)
///   `--bk-on-dark-text` — text colour on accent / dark surfaces (`.bk-wordmark`)
///   `--bk-positive`     — semantic success (`.bk-tag-positive`)
///   `--bk-priority`     — semantic warning / alert (`.bk-tag-priority`, `.bk-tag-critical`)
///   `--bk-info`         — semantic info, typically same as accent (`.bk-tag-info`)
///   `--bk-radius-md`    — small-card / pill corner radius (`.bk-tag`, `.bk-card-tight`)
///   `--bk-radius-lg`    — large-card corner radius (`.bk-card`)
///   `--bk-shadow-card`  — card elevation (`.bk-card`)

[<Literal>]
let FontDisplayVar = "--bk-font-display"

[<Literal>]
let FontUiVar = "--bk-font-ui"

[<Literal>]
let FontMonoVar = "--bk-font-mono"

[<Literal>]
let InkVar = "--bk-ink"

[<Literal>]
let InkMuteVar = "--bk-ink-mute"

[<Literal>]
let PaperVar = "--bk-paper"

[<Literal>]
let PanelVar = "--bk-panel"

[<Literal>]
let RuleVar = "--bk-rule"

[<Literal>]
let AccentVar = "--bk-accent"

[<Literal>]
let OnDarkTextVar = "--bk-on-dark-text"

[<Literal>]
let PositiveVar = "--bk-positive"

[<Literal>]
let PriorityVar = "--bk-priority"

[<Literal>]
let InfoVar = "--bk-info"

[<Literal>]
let RadiusMdVar = "--bk-radius-md"

[<Literal>]
let RadiusLgVar = "--bk-radius-lg"

[<Literal>]
let ShadowCardVar = "--bk-shadow-card"

/// Format a token name as a CSS-variable reference, for a CONSUMER
/// building its own stylesheet rules or inline styles:
/// `cssVar "--bk-ink"` → `"var(--bk-ink)"`. No BrandKit primitive or
/// layout calls this — the package emits no `var(--bk-…)` reference of
/// its own (see the module header).
let cssVar (varName: string) : string = sprintf "var(%s)" varName