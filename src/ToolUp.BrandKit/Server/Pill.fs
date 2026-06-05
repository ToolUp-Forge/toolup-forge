module ToolUp.BrandKit.Pill

open Giraffe.ViewEngine
open ToolUp.BrandKit

/// Pill primitives — small uppercase tags for chips / status markers /
/// priority flags. Named `pill` rather than `tag` to avoid shadowing
/// Giraffe.ViewEngine's `tag` element-builder inside consumer code.
/// Severity for `pillSeverity` — semantic colour mapping into the
/// `--bk-{info,positive,priority}` CSS vars. `Critical` shares the
/// `--bk-priority` colour by convention (priority + critical typically
/// use the same brand alert colour, distinguished by surrounding copy
/// and iconography rather than colour).
type Severity =
    | Info
    | Positive
    | Priority
    | Critical

/// Plain pill — neutral border + ink text. Compact status / category
/// chip.
let pill (text: string) : XmlNode = span [ _class "bk-tag" ] [ str text ]

/// Accent pill — `--bk-accent` border + text. Use to mark interactive
/// state (an "active" tag in a filter strip, the currently-selected
/// chip in a group).
let pillOn (text: string) : XmlNode =
    span [ _class "bk-tag bk-tag-on" ] [ str text ]

/// Pill with a leading accent dot — for status-style chips that need
/// visual prominence (e.g. "PRIORITY · WATCH", "PREMIUM · SOON"). The
/// dot is a separate span so a consumer's CSS can colour the dot
/// independently of the chip text.
let pillWithDot (text: string) : XmlNode =
    span [ _class "bk-tag bk-tag-dotted" ] [ span [ _class "bk-tag-dot"; attr "aria-hidden" "true" ] []; str text ]

/// Semantic-severity pill. Renders with class
/// `bk-tag bk-tag-{info|positive|priority|critical}`; consumer styles
/// each modifier with the right `--bk-{info,positive,priority}`
/// background / border / text colour. Use for confirmed/positive
/// movement (Positive), priority alerts (Priority), critical alarms
/// (Critical), and informational new-signal markers (Info).
let pillSeverity (severity: Severity) (text: string) : XmlNode =
    let severityClass =
        match severity with
        | Info -> "bk-tag-info"
        | Positive -> "bk-tag-positive"
        | Priority -> "bk-tag-priority"
        | Critical -> "bk-tag-critical"

    span [ _class (sprintf "bk-tag %s" severityClass) ] [ str text ]