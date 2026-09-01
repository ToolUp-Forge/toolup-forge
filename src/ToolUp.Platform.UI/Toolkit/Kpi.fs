// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

/// Headline KPI / stat tiles. A small, neutral primitive for the
/// summary numbers an analysis page leads with. Purely presentational —
/// the caller formats every string (currency, counts, ranges) — so the
/// tile carries no domain knowledge and no formatting locale of its own.
///
/// Named `Kpi` rather than `Metric` deliberately: "Metric" is a common
/// domain term that analytics modules define themselves, and a shared
/// `Metric` module would collide on every `open`.
module Kpi =

    /// Direction of a period-over-period (or any comparative) delta.
    type Direction =
        | Up
        | Down
        | Flat

    /// A pre-formatted delta shown beside a tile's value (e.g. "+12.3%").
    type Delta = { Value: string; Direction: Direction }

    /// One headline number with its label, optional delta, and optional hint line.
    type Tile = {
        Label: string
        Value: string
        Delta: Delta option
        Hint: string option
    }

    let private deltaView (delta: Delta) =
        let colour, glyph =
            match delta.Direction with
            | Up -> Tokens.Colours.success, "▲"
            | Down -> Tokens.Colours.error, "▼"
            | Flat -> Tokens.Colours.neutral, "–"

        Html.span [
            prop.className $"inline-flex items-center gap-1 text-sm font-medium {colour}"
            prop.children [ Html.span [ prop.text glyph ]; Html.span [ prop.text delta.Value ] ]
        ]

    /// A single stat tile.
    let tile (t: Tile) =
        Html.div [
            prop.className
                "flex flex-col gap-1 rounded-[var(--radius)] border border-border bg-[var(--surface)] px-5 py-4 min-w-[10rem]"
            prop.children [
                Html.span [
                    prop.className "text-xs font-medium text-[var(--muted)] uppercase tracking-wide"
                    prop.text t.Label
                ]
                Html.div [
                    prop.className "flex items-baseline gap-2"
                    prop.children [
                        Html.span [
                            prop.className "text-2xl font-semibold text-[var(--text-strong)]"
                            prop.text t.Value
                        ]
                        match t.Delta with
                        | Some d -> deltaView d
                        | None -> Html.none
                    ]
                ]
                match t.Hint with
                | Some h -> Html.span [ prop.className "text-xs text-[var(--muted)]"; prop.text h ]
                | None -> Html.none
            ]
        ]

    /// A responsive row of stat tiles, wrapping on narrow viewports.
    let row (tiles: Tile list) =
        Html.div [
            prop.className "flex flex-wrap gap-4"
            prop.children [ for t in tiles -> tile t ]
        ]