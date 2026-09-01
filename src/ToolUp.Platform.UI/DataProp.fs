// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataProp

open Feliz

// ─── Typed data-* attribute helpers ──────────────────────────────
//
// HTML's `data-*` attribute family is the standard escape hatch for
// stashing arbitrary string metadata on a DOM element. React renders
// `data-*` attributes correctly via a hardcoded exception in its
// attribute normaliser — but consumers reach them through Feliz's
// `prop.custom (name, value)`, which is the same code path that
// silently drops kebab-case SVG attributes (see `SvgProp.fs`).
//
// The hazard: nothing in `prop.custom` tells the caller that
// `"data-foo"` is correctly preserved while `"stroke-width"` is
// dropped. The two look identical at the call site. A future React
// release that tightens the kebab-case heuristic would silently
// break every `prop.custom ("data-…", …)` site in the SDK + every
// consumer module. Cheap to insulate against today.
//
// The companion hazard — already real — is the role/aria sibling
// family living in `AriaProp.fs`. Same `prop.custom` shape, same
// React-exception protection, same "looks identical to the
// silently-dropped form" defect mode. The two helper modules ship
// as a pair so any future audit (see
// `DomAttrCustomAuditTests.fs`) can ratchet against both with one
// rule: no `prop.custom ("data-*"|"aria-*"|"role", _)` outside
// these helper modules' own bodies.
//
// **Never use raw `prop.custom` with a `data-*` attribute name in
// new code.** Either add a typed helper here (preferred — the
// helper name documents the intent and survives editor refactors)
// or call `dataProp.custom rawName value` (the generic fallback —
// keeps the kebab-case name verbatim because that's what
// `data-*` actually expects in the DOM, but routes through the
// helper module so audits can see the intent).

/// Wrappers around `prop.custom` keyed by HTML `data-*` attribute
/// names. Module name is lowercase to match the call-site shape of
/// Feliz's own `prop.*` API.
///
/// React's attribute normaliser preserves `data-*` names verbatim
/// (kebab-case is correct here — the DOM attribute is literally
/// `data-foo`, not `dataFoo`). The helpers exist for symmetry with
/// `svgProp` / `ariaProp` and to give audits a single seam to ratchet
/// against `prop.custom ("data-…", …)`.
[<RequireQualifiedAccess>]
module dataProp =

    /// `data-ad-client` — AdSense publisher id (`ca-pub-…`). Read by
    /// the `adsbygoogle.push({})` script when the `<ins>` element is
    /// hydrated.
    let adClient (v: string) : IReactProperty = prop.custom ("data-ad-client", v)

    /// `data-ad-slot` — AdSense slot id within the publisher account.
    let adSlot (v: string) : IReactProperty = prop.custom ("data-ad-slot", v)

    /// `data-ad-format` — AdSense format hint (`auto`, `rectangle`,
    /// `vertical`, `horizontal`, `fluid`).
    let adFormat (v: string) : IReactProperty = prop.custom ("data-ad-format", v)

    /// `data-ad-layout-key` — AdSense layout key for fluid format
    /// slots (e.g. `"-fb+5w+4e-db+86"`).
    let adLayoutKey (v: string) : IReactProperty = prop.custom ("data-ad-layout-key", v)

    /// `data-ai-name` — UIToolkit `Forms.aiNamed` marker. Lets a
    /// companion's pulse-highlight (Phase 6g.B `set_field` /
    /// `click_button` decoder) target an element by name when the AI
    /// drives the matching field.
    let aiName (v: string) : IReactProperty = prop.custom ("data-ai-name", v)

    /// `data-area` — UIToolkit `Layout.Dashboard` area marker. Names
    /// the dashboard tile so deployment-side CSS / queries can address
    /// individual areas without coupling to render order.
    let area (v: string) : IReactProperty = prop.custom ("data-area", v)

    /// `data-cite-idx` — AI ConversationPanel citation-source index.
    /// Pins a retrieved-source list item to its position so a citation
    /// scroll-target can locate the matching DOM node.
    let citeIdx (v: int) : IReactProperty = prop.custom ("data-cite-idx", string v)

    /// `data-fact` — the content-addressed id of the fact a narrative
    /// number (`InlineSpan.Metric`) was quoted from (Phase 521), so a
    /// rendered metric can be traced back to the fact it references.
    /// Read by fact-aware UI surfaces; inert otherwise.
    let fact (v: string) : IReactProperty = prop.custom ("data-fact", v)

    /// `data-testid` — the de-facto convention for test selectors
    /// (Playwright / React Testing Library / Cypress). Not currently
    /// consumed by any forge code path but offered for downstream
    /// consumers that want a typed pin in their views.
    let testid (v: string) : IReactProperty = prop.custom ("data-testid", v)

    /// Generic `data-*` escape hatch. `name` is passed verbatim as
    /// the DOM attribute name; callers should include the `data-`
    /// prefix. Use this for one-off `data-*` attributes that don't
    /// merit a typed helper, while still routing through this module
    /// so audits can see the intent.
    let custom (name: string) (v: string) : IReactProperty = prop.custom (name, v)