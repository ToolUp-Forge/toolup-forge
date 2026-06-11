// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeChartsTests

open Expecto
open Giraffe.ViewEngine
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Phase 94 — Narrative chart projector + inline-SVG renderer ──────
//
// The projector (NarrativeFromData.chart) emits a Component("chart", …)
// block; the renderer (NarrativeCharts.renderChart) turns it into
// deterministic inline SVG. Coverage: prop encoding, per-kind SVG marks,
// byte-stable determinism, empty-data degradation, the chartTable data
// fallback, and the end-to-end render through the component registry.

let private norm (s: string) = s.Replace("\r\n", "\n")

let private docWith (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "T"
    Subtitle = None
    Sections = [
        {
            Id = "s"
            Heading = "H"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let private series = [ "Jan", 10.0; "Feb", 20.0; "Mar", 15.0 ]

let private renderProps (el: NarrativeElement) =
    match el with
    | Component("chart", props) -> RenderView.AsString.htmlNode (NarrativeCharts.renderChart props)
    | other -> failtestf "expected a chart Component, got %A" other

let tests =
    testList "Phase 94 Narrative charts" [

        test "chart projector emits a Component carrying kind + encoded points" {
            match NarrativeFromData.chart NarrativeFromData.Bar (Some "Revenue") series with
            | Component("chart", props) ->
                Expect.equal (props.TryFind "chart.kind") (Some "bar") "kind encoded"
                Expect.equal (props.TryFind "chart.title") (Some "Revenue") "title encoded"
                Expect.equal (props.TryFind "chart.points") (Some "Jan=10;Feb=20;Mar=15") "points encoded label=value;…"
            | other -> failtestf "expected a chart Component, got %A" other
        }

        test "line chart renders deterministic inline SVG (byte-identical across runs)" {
            let el = NarrativeFromData.chart NarrativeFromData.Line (Some "Revenue") series
            let h1 = norm (renderProps el)
            let h2 = norm (renderProps el)
            Expect.equal h1 h2 "two renders are byte-identical (prerender-safe)"
            Expect.stringContains h1 "<svg" "emits inline svg"
            Expect.stringContains h1 "viewBox=\"0 0 320.0 160.0\"" "fixed viewBox"
            Expect.stringContains h1 "tu-chart__line" "line mark + class hook"
            Expect.stringContains h1 "Revenue" "caption rendered"
            Expect.isFalse (h1.Contains "<script") "no JavaScript"
        }

        test "bar chart renders rect marks; area chart renders a polygon" {
            let bar = renderProps (NarrativeFromData.chart NarrativeFromData.Bar None series)
            let area = renderProps (NarrativeFromData.chart NarrativeFromData.Area None series)
            Expect.stringContains bar "tu-chart__bar" "bar marks"
            Expect.stringContains bar "<rect" "bar uses rects"
            Expect.stringContains area "tu-chart__area" "area mark"
            Expect.stringContains area "polygon" "area uses a closed polygon"
        }

        test "empty data degrades to a labelled placeholder, not a broken SVG" {
            let html =
                renderProps (NarrativeFromData.chart NarrativeFromData.Line (Some "Empty") [])

            Expect.stringContains html "No chart data." "empty-state text"
            Expect.isFalse (html.Contains "<svg") "no svg emitted for empty data"
        }

        test "sparkline is a captionless line chart" {
            match NarrativeFromData.sparkline series with
            | Component("chart", props) ->
                Expect.equal (props.TryFind "chart.kind") (Some "line") "line kind"
                Expect.isNone (props.TryFind "chart.title") "no caption"
            | other -> failtestf "expected a chart Component, got %A" other
        }

        test "chartTable gives a data-fidelity Table fallback" {
            match NarrativeFromData.chartTable "Month" "Revenue" series with
            | Table(cols, rows) ->
                Expect.equal (List.map fst cols) [ "Month"; "Revenue" ] "labelled columns"
                Expect.equal (List.length rows) 3 "one row per point"
                Expect.equal rows[0] [ [ Text "Jan" ]; [ Text "10" ] ] "label + value cell"
            | other -> failtestf "expected a Table, got %A" other
        }

        test "end-to-end: a chart renders through the component registry in a document" {
            let doc =
                docWith [ NarrativeFromData.chart NarrativeFromData.Line (Some "Trend") series ]

            let options = NarrativeLayout.richRenderOptions Set.empty NarrativeCharts.registry
            let html = NarrativeHtml.renderWith options doc
            Expect.stringContains html "tu-chart__line" "chart SVG rendered into the document body"
            Expect.stringContains html "Trend" "caption present"
        }

        test "an unregistered chart renderer degrades to the safe placeholder" {
            // No chart renderer registered → SDK placeholder, not a crash.
            let doc = docWith [ NarrativeFromData.chart NarrativeFromData.Line None series ]
            let html = NarrativeHtml.render doc
            Expect.isFalse (html.Contains "tu-chart__line") "no SVG without the registry"
            Expect.stringContains html "narrative-component--unresolved" "safe placeholder fires"
        }
    ]