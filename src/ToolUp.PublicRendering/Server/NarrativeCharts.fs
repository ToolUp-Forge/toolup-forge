// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 94 — deterministic server-side inline-SVG chart renderer.
///
/// The render half of the chart projector: a pure `props -> XmlNode`
/// renderer for the `Component("chart", …)` blocks that
/// [`NarrativeFromData.chart`](NarrativeFromData.fs) emits. It is
/// registered into a `NarrativeLayout.ComponentRegistry` (the sanctioned
/// Narrative type-erasure seam from Phase 87 — no `NarrativeElement` DU
/// fork) and produces inline SVG with **no JavaScript**, so a chart
/// prerenders and hydrates byte-identically (the prerender-determinism
/// rule) and carries no charting-vendor dependency (GP 2).
///
/// Wiring (consumer side):
///
/// ```fsharp
/// open ToolUp.PublicRendering
///
/// let layout =
///     NarrativeLayout.renderBodyWith Set.empty NarrativeCharts.registry
/// // or merge NarrativeCharts.registry into a larger component registry
/// ```
///
/// The on-the-wire prop format (mirrored by `NarrativeFromData.chart`):
///   - `"chart.kind"`   — `"line"` | `"bar"` | `"area"` (default `"line"`)
///   - `"chart.title"`  — optional visible caption
///   - `"chart.points"` — `"label=value;label=value;…"`, value parsed as
///                        an `InvariantCulture` float; malformed points
///                        are skipped. Labels must not contain `;` or `=`.
///
/// A block may declare further props — the optional binding props
/// (`"chart.artifactKey"` / `"chart.datasetVintage"`, Phase 649) are the
/// shipped case. This renderer reads only the three keys above and draws
/// nothing from the rest, deliberately: a binding is a claim a reader
/// resolves, not a mark on the canvas, so declaring one leaves the SVG
/// byte-identical.
namespace ToolUp.PublicRendering

open System
open System.Globalization
open Giraffe.ViewEngine

module NarrativeCharts =

    let private inv = CultureInfo.InvariantCulture

    /// Component block name this renderer claims.
    [<Literal>]
    let ComponentName = "chart"

    [<Literal>]
    let private KeyKind = "chart.kind"

    [<Literal>]
    let private KeyTitle = "chart.title"

    [<Literal>]
    let private KeyPoints = "chart.points"

    /// Decode `"label=value;…"` into ordered `(label, value)` points,
    /// skipping malformed entries. Deterministic; no culture-sensitive
    /// parsing.
    let private decodePoints (raw: string) : (string * float) list =
        if String.IsNullOrWhiteSpace raw then
            []
        else
            raw.Split(';')
            |> Array.toList
            |> List.choose (fun seg ->
                let eq = seg.IndexOf('=')

                if eq <= 0 then
                    None
                else
                    let label = seg.Substring(0, eq).Trim()
                    let valueText = seg.Substring(eq + 1).Trim()

                    match Double.TryParse(valueText, NumberStyles.Float, inv) with
                    | true, v -> Some(label, v)
                    | _ -> None)

    // Fixed canvas — a stable viewBox keeps the SVG responsive (the
    // layout's CSS scales it) while every coordinate is computed
    // deterministically below.
    [<Literal>]
    let private W = 320.0

    [<Literal>]
    let private H = 160.0

    [<Literal>]
    let private Pad = 12.0

    let private fmt (n: float) : string =
        // One-decimal, InvariantCulture — byte-stable coordinates.
        Math.Round(n, 1).ToString("0.0", inv)

    /// Map values to plot coordinates. x is evenly spaced across the plot
    /// width; y inverts the value range into the plot height (a flat
    /// series renders at the vertical midpoint).
    let private project (points: (string * float) list) : (float * float) list =
        let n = List.length points
        let values = points |> List.map snd
        let lo = List.min values
        let hi = List.max values
        let span = hi - lo
        let plotW = W - 2.0 * Pad
        let plotH = H - 2.0 * Pad

        points
        |> List.mapi (fun i (_, v) ->
            let x =
                if n = 1 then
                    W / 2.0
                else
                    Pad + plotW * float i / float (n - 1)

            let y =
                if span = 0.0 then
                    Pad + plotH / 2.0
                else
                    Pad + plotH * (1.0 - (v - lo) / span)

            x, y)

    let private axis () =
        tag "line" [
            _class "tu-chart__axis"
            attr "x1" (fmt Pad)
            attr "y1" (fmt (H - Pad))
            attr "x2" (fmt (W - Pad))
            attr "y2" (fmt (H - Pad))
            attr "stroke" "currentColor"
        ] []

    let private renderLine (close: bool) (coords: (float * float) list) =
        let pts =
            coords
            |> List.map (fun (x, y) -> sprintf "%s,%s" (fmt x) (fmt y))
            |> String.concat " "

        if close then
            let (x0, _) = List.head coords
            let (xn, _) = List.last coords
            let baseline = fmt (H - Pad)
            let poly = sprintf "%s %s,%s %s,%s" pts (fmt xn) baseline (fmt x0) baseline
            tag "polygon" [ _class "tu-chart__area"; attr "points" poly; attr "fill" "currentColor" ] []
        else
            tag "polyline" [
                _class "tu-chart__line"
                attr "points" pts
                attr "fill" "none"
                attr "stroke" "currentColor"
            ] []

    let private renderBars (coords: (float * float) list) =
        let n = List.length coords
        let plotW = W - 2.0 * Pad
        let barW = if n = 0 then 0.0 else (plotW / float n) * 0.6
        let baseline = H - Pad

        coords
        |> List.map (fun (x, y) ->
            tag "rect" [
                _class "tu-chart__bar"
                attr "x" (fmt (x - barW / 2.0))
                attr "y" (fmt y)
                attr "width" (fmt barW)
                attr "height" (fmt (max 0.0 (baseline - y)))
                attr "fill" "currentColor"
            ] [])

    /// The pure `props -> XmlNode` renderer registered for the `"chart"`
    /// component. Empty / malformed data degrades to a labelled
    /// placeholder rather than a broken SVG.
    let renderChart (props: Map<string, string>) : XmlNode =
        let kind = props.TryFind KeyKind |> Option.defaultValue "line"
        let title = props.TryFind KeyTitle
        let points = props.TryFind KeyPoints |> Option.defaultValue "" |> decodePoints

        let titleNode =
            match title with
            | Some t when t <> "" -> [ tag "figcaption" [ _class "tu-chart__caption" ] [ encodedText t ] ]
            | _ -> []

        if List.isEmpty points then
            figure [ _class "tu-chart tu-chart--empty" ] (titleNode @ [ encodedText "No chart data." ])
        else
            let coords = project points

            let marks =
                match kind with
                | "bar" -> renderBars coords
                | "area" -> [ renderLine true coords; renderLine false coords ]
                | _ -> [ renderLine false coords ]

            let svg =
                tag
                    "svg"
                    [
                        _class (sprintf "tu-chart__svg tu-chart__svg--%s" kind)
                        attr "viewBox" (sprintf "0 0 %s %s" (fmt W) (fmt H))
                        attr "role" "img"
                        attr "preserveAspectRatio" "none"
                    ]
                    (axis () :: marks)

            figure [ _class (sprintf "tu-chart tu-chart--%s" kind) ] (svg :: titleNode)

    /// A `NarrativeLayout.ComponentRegistry` containing just the chart
    /// renderer. Merge it into a deployment's registry, or pass it
    /// straight to `NarrativeLayout.renderBodyWith` /
    /// `NarrativeLayout.richRenderOptions`.
    let registry: NarrativeLayout.ComponentRegistry =
        Map.ofList [ ComponentName, renderChart ]