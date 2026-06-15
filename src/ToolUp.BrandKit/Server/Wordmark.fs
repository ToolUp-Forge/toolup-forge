module ToolUp.BrandKit.Wordmark

open Giraffe.ViewEngine

/// Wordmark — the single most important primitive for brand-prominent
/// identity. The construction is: italic-stem + upright-emphasis-
/// substring + optional italic-tail. Every brand using this primitive
/// composes its identity from these three parts (e.g. "Acme-" italic +
/// "Co" upright + "rp" italic; "Bright" italic + "Spark" upright;
/// etc.).
///
/// Display font + emphasis colour come from the consumer's CSS vars +
/// the per-call `EmphasisColour` field (the spec takes the colour
/// inline because brand emphasis colour is the most variable token
/// across consumers — they want to set it per-wordmark, not commit to
/// a single brand-wide accent).
/// The wordmark construction spec.
///   `Stem`           — the italic prefix (e.g. "Ella-")
///   `Emphasis`       — the upright emphasis substring (e.g. "MA")
///   `EmphasisColour` — the colour the emphasis substring renders in
///                      (e.g. "#6B5FBF" — applied inline so each
///                      wordmark can vary)
///   `Tail`           — optional italic suffix (e.g. "e"); None for
///                      wordmarks where the emphasis is the final glyph
type WordmarkSpec = {
    Stem: string
    Emphasis: string
    EmphasisColour: string
    Tail: string option
}

/// Render a wordmark per the spec. Markup shape:
///
///     <span class="bk-wordmark">
///       Stem<span class="bk-wordmark-emphasis" style="color:#…;">MA</span>tail
///     </span>
///
/// The outer `.bk-wordmark` class hooks the italic + display-font CSS;
/// `.bk-wordmark-emphasis` hooks the upright + weight CSS. Consumer
/// rules:
///
///     .bk-wordmark { font-family: var(--bk-font-display); font-style: italic; font-weight: 500; letter-spacing: -.02em; }
///     .bk-wordmark-emphasis { font-style: normal; font-weight: 600; }
///
/// `EmphasisColour` is applied inline so a deployment can vary it per
/// wordmark instance (e.g. on-dark variants typically swap to a warm
/// tan instead of the brand-light accent).
let wordmark (spec: WordmarkSpec) : XmlNode =
    let inlineColour = sprintf "color:%s;" spec.EmphasisColour

    let parts = [
        str spec.Stem
        span [ _class "bk-wordmark-emphasis"; attr "style" inlineColour ] [ str spec.Emphasis ]
        match spec.Tail with
        | Some tail -> str tail
        | None -> ()
    ]

    span [ _class "bk-wordmark" ] parts