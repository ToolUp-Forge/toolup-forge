module ToolUp.BrandKit.Text

open Giraffe.ViewEngine

/// Text primitives — display headings, eyebrows, mono labels, dividers.
/// Each helper emits semantic markup carrying a `bk-*` class hook and
/// nothing else — no inline style, and no reference to a `--bk-*`
/// variable (see `Tokens.fs` for the contract). Visual presentation is
/// entirely the consumer's responsibility — define the `--bk-*`
/// variables in `:root` and attach them to those class hooks.

// ─── Display headings ──────────────────────────────────────────────

/// Large display heading — display font, prominent on the page.
/// Consumer styles `.bk-display.bk-display-lg` per their brand.
let displayLarge (text: string) : XmlNode =
    div [ _class "bk-display bk-display-lg" ] [ str text ]

/// Medium display heading.
let displayMedium (text: string) : XmlNode =
    div [ _class "bk-display bk-display-md" ] [ str text ]

/// Small display heading.
let displaySmall (text: string) : XmlNode =
    span [ _class "bk-display bk-display-sm" ] [ str text ]

// ─── Eyebrows ──────────────────────────────────────────────────────

/// Eyebrow — small uppercase mono label sitting above a heading.
/// Default colour is `--bk-accent`; consumer overrides per-class.
let eyebrow (text: string) : XmlNode =
    span [ _class "bk-eyebrow" ] [ str text ]

/// Muted eyebrow — same shape, `--bk-ink-mute` colour by convention.
let eyebrowMute (text: string) : XmlNode =
    span [ _class "bk-eyebrow bk-eyebrow-mute" ] [ str text ]

// ─── Mono body / structural text ───────────────────────────────────

/// Small mono — data / timestamp / source-label text.
let monoSmall (text: string) : XmlNode =
    span [ _class "bk-mono bk-mono-sm" ] [ str text ]

/// Medium mono.
let monoMedium (text: string) : XmlNode =
    span [ _class "bk-mono bk-mono-md" ] [ str text ]

/// Large mono.
let monoLarge (text: string) : XmlNode =
    span [ _class "bk-mono bk-mono-lg" ] [ str text ]

/// Body-sized mono — for inline labels in paragraph context.
let monoBody (text: string) : XmlNode =
    span [ _class "bk-mono bk-mono-body" ] [ str text ]

// ─── Dividers ──────────────────────────────────────────────────────

/// Hairline divider — full-width 1px line in `--bk-rule` colour.
let hRule: XmlNode = div [ _class "bk-rule" ] []

/// Softer divider — same shape, lower opacity by convention.
let hRuleSoft: XmlNode = div [ _class "bk-rule bk-rule-soft" ] []

/// Vertical divider — for inline use between header items.
let vDivider: XmlNode = span [ _class "bk-divider-v"; attr "aria-hidden" "true" ] []