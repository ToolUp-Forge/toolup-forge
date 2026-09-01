// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SvgProp

open Feliz

// ─── Typed SVG attribute helpers ─────────────────────────────────
//
// Feliz's typed `Svg.*` API requires an `ISvgAttribute list` parameter
// shape that doesn't compose with the `Html.svg` / `Html.line` /
// `Html.path` / `Html.rect` / `Html.text` / `Html.g` pattern used
// throughout this SDK and downstream consumers. The escape hatch is
// `prop.custom`, but that has its own trap: React requires **camelCase**
// for SVG attribute names. Kebab-case names (`stroke-width`,
// `fill-opacity`, etc.) are silently dropped from the DOM with a
// dev-only `Invalid DOM property '<name>'. Did you mean '<camelName>'?`
// warning; the production-build React drops the attribute with no
// warning at all and the chart renders unstyled.
//
// This module exposes typed helpers wrapping `prop.custom` with the
// React-compatible camelCase name + a properly-typed F# parameter, so
// downstream consumer modules can render SVG without hand-rolling
// kebab-case attribute strings and without surrendering type safety.
//
// **Never use raw `prop.custom` with a kebab-case attribute name in
// SVG contexts.** Either add a helper here or pass a camelCase string
// to `prop.custom` directly.
//
// **Single-word SVG attributes are React-safe lowercase and need no
// helper.** The following are safe to write as Feliz `prop.*` (where
// Feliz exposes them) or as direct attributes on an `Html.svg` /
// `Html.path` / etc. element: `x`, `y`, `cx`, `cy`, `r`, `d`, `fill`,
// `stroke`, `width`, `height`, `viewBox`, `transform`, `opacity`,
// `points`, `rx`, `ry`. The helpers below cover the multi-word
// attributes that would otherwise tempt a kebab-case `prop.custom`.

/// Wrappers around `prop.custom` keyed by React-compatible camelCase
/// SVG attribute names. Module name is lowercase to match the call-site
/// shape of Feliz's own `prop.*` API.
[<RequireQualifiedAccess>]
module svgProp =

    /// `stroke-width` — width in user units of the stroke painted on
    /// the perimeter of the shape.
    let strokeWidth (v: float) : IReactProperty =
        prop.custom ("strokeWidth", sprintf "%g" v)

    /// `stroke-dasharray` — comma- or space-separated list of lengths
    /// alternating dash and gap, e.g. `"5,5"` or `"10 4 2 4"`.
    let strokeDasharray (v: string) : IReactProperty = prop.custom ("strokeDasharray", v)

    /// `stroke-opacity` — opacity of the painted stroke. Range 0.0–1.0.
    let strokeOpacity (v: float) : IReactProperty =
        prop.custom ("strokeOpacity", sprintf "%g" v)

    /// `stroke-linecap` — shape at the ends of open subpaths.
    /// Valid values: `"butt"`, `"round"`, `"square"`.
    let strokeLinecap (v: string) : IReactProperty = prop.custom ("strokeLinecap", v)

    /// `stroke-linejoin` — shape used at the corners of paths.
    /// Valid values: `"miter"`, `"round"`, `"bevel"`, `"arcs"`, `"miter-clip"`.
    let strokeLinejoin (v: string) : IReactProperty = prop.custom ("strokeLinejoin", v)

    /// `fill-opacity` — opacity of the painted fill. Range 0.0–1.0.
    let fillOpacity (v: float) : IReactProperty =
        prop.custom ("fillOpacity", sprintf "%g" v)

    /// `text-anchor` — horizontal alignment of `<text>` content
    /// relative to its `x` coordinate.
    /// Valid values: `"start"`, `"middle"`, `"end"`.
    let textAnchor (v: string) : IReactProperty = prop.custom ("textAnchor", v)

    /// `dominant-baseline` — baseline alignment of `<text>` content
    /// relative to its `y` coordinate.
    /// Common values: `"auto"`, `"middle"`, `"central"`, `"hanging"`,
    /// `"text-before-edge"`, `"text-after-edge"`.
    let dominantBaseline (v: string) : IReactProperty = prop.custom ("dominantBaseline", v)

    /// `alignment-baseline` — alternative baseline alignment property.
    /// Prefer `dominantBaseline` for new code; this helper is provided
    /// for consumers that need to override an inherited value.
    let alignmentBaseline (v: string) : IReactProperty = prop.custom ("alignmentBaseline", v)

    /// `font-size` — font size in user units (px in most contexts).
    let fontSize (v: float) : IReactProperty =
        prop.custom ("fontSize", sprintf "%g" v)

    /// `font-weight` — weight of the font face.
    /// Common values: `"normal"`, `"bold"`, `"100"`–`"900"`.
    let fontWeight (v: string) : IReactProperty = prop.custom ("fontWeight", v)

    /// `font-family` — font family name (or comma-separated fallback list).
    let fontFamily (v: string) : IReactProperty = prop.custom ("fontFamily", v)

    /// `pointer-events` — whether the element responds to pointer input.
    /// Common values: `"none"`, `"auto"`, `"visiblePainted"`, `"all"`.
    let pointerEvents (v: string) : IReactProperty = prop.custom ("pointerEvents", v)

    /// `clip-path` — clipping reference, typically `"url(#clipId)"` or
    /// a CSS basic-shape value.
    let clipPath (v: string) : IReactProperty = prop.custom ("clipPath", v)

    /// `class` — provided here for symmetry with `prop.className` so a
    /// consumer rendering an SVG element with this module's helpers has
    /// one canonical surface. Functionally identical to `prop.className`.
    let className (v: string) : IReactProperty = prop.custom ("className", v)

    /// `for` — provided here for symmetry with `prop.htmlFor`.
    /// Functionally identical to `prop.htmlFor`.
    let htmlFor (v: string) : IReactProperty = prop.custom ("htmlFor", v)