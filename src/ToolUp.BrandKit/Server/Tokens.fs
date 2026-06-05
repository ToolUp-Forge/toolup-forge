module ToolUp.BrandKit.Tokens

/// Canonical CSS custom-property names every BrandKit primitive reads from.
/// Consumers define values for these names in their `:root` declaration;
/// BrandKit emits `var(--bk-<name>)` references in inline styles where
/// CSS-class hooks alone don't suffice.
///
/// Sane defaults are documented in `docs/brandkit-tokens.md`. The
/// package itself ships zero opinionated styling — only structural
/// markup + token references.
///
/// Token classes:
///   `--bk-font-display` — display headings (serif italic by convention)
///   `--bk-font-ui`      — body / UI text
///   `--bk-font-mono`    — data / timestamps / eyebrows
///   `--bk-ink`          — body text colour on light surfaces
///   `--bk-ink-mute`     — secondary / muted text
///   `--bk-paper`        — base surface background
///   `--bk-panel`        — raised-surface background
///   `--bk-rule`         — border / divider colour
///   `--bk-accent`       — brand accent / interactive colour
///   `--bk-on-dark-text` — text colour on accent / dark surfaces
///   `--bk-positive`     — semantic success
///   `--bk-priority`     — semantic warning / alert
///   `--bk-info`         — semantic info (typically same as accent)
///   `--bk-radius-md`    — small-card / pill corner radius
///   `--bk-radius-lg`    — large-card corner radius
///   `--bk-shadow-card`  — card elevation

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

/// Format a CSS-variable reference suitable for an inline `style="..."`
/// attribute. `cssVar "--bk-ink"` → `"var(--bk-ink)"`.
let cssVar (varName: string) : string = sprintf "var(%s)" varName