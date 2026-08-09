// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeChartsTests

open System.Text.Json
open Expecto
open Giraffe.ViewEngine
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 94 — Narrative chart projector + inline-SVG renderer ──────
//
// The projector (NarrativeFromData.chart) emits a Component("chart", …)
// block; the renderer (NarrativeCharts.renderChart) turns it into
// deterministic inline SVG. Coverage: prop encoding, per-kind SVG marks,
// byte-stable determinism, empty-data degradation, the chartTable data
// fallback, and the end-to-end render through the component registry.
//
// ─── Phase 649 — the binding props ───────────────────────────────────
//
// A chart block declares WHICH governed results it visualises, through
// two optional props. Three things are worth pinning and one of them is
// the absence: an unbound chart must emit exactly the pre-649 bag, or
// every document that never opts in changes shape (GP 11). The other two
// are that a declared binding survives the wire — additive props ride the
// existing `Map<string, string>` serialisation, which is a claim about
// the codec and so is verified rather than assumed — and that it changes
// no drawn byte, because a binding is a claim a reader resolves, not a
// mark on the canvas.

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

let private jsonOptions = FableConverters.create ()

let private binding: NarrativeFromData.ChartBinding = {
    ArtifactKey = Some "result-8f2c"
    DatasetVintage = Some "dataset-v17"
}

let private propsOf (el: NarrativeElement) =
    match el with
    | Component("chart", props) -> props
    | other -> failtestf "expected a chart Component, got %A" other

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

        // ─── Phase 649 — binding props ───────────────────────────────

        test "an unbound chart emits exactly the pre-binding prop bag" {
            // The byte-stability pin. Stated as the WHOLE key set rather
            // than as "the binding keys are absent", so a future prop
            // added without a decision fails here too.
            let props =
                propsOf (NarrativeFromData.chart NarrativeFromData.Bar (Some "Revenue") series)

            Expect.equal
                (props |> Map.toList |> List.map fst)
                [ "chart.kind"; "chart.points"; "chart.title" ]
                "three keys, exactly as before Phase 649"

            Expect.equal
                (NarrativeFromData.chartWith NarrativeFromData.noBinding NarrativeFromData.Bar (Some "Revenue") series)
                (NarrativeFromData.chart NarrativeFromData.Bar (Some "Revenue") series)
                "chartWith noBinding is the unbound projector"
        }

        test "a bound chart declares its binding as props, recoverable by any reader" {
            let props =
                propsOf (NarrativeFromData.chartWith binding NarrativeFromData.Bar (Some "Revenue") series)

            Expect.equal (props.TryFind "chart.artifactKey") (Some "result-8f2c") "artifact key declared"
            Expect.equal (props.TryFind "chart.datasetVintage") (Some "dataset-v17") "dataset vintage declared"
            Expect.equal (props.TryFind "chart.kind") (Some "bar") "the pre-existing props are untouched"
            Expect.equal (NarrativeFromData.chartBinding props) binding "the reader recovers what was declared"
        }

        test "a half binding declares only the member it has" {
            // Half a binding is a legitimate state to DECLARE (a policy
            // that requires both is the consumer's, not the projector's);
            // what must not happen is an empty prop standing in for the
            // missing member, which would read as "bound to nothing".
            let keyOnly: NarrativeFromData.ChartBinding = {
                ArtifactKey = Some "result-8f2c"
                DatasetVintage = None
            }

            let props =
                propsOf (NarrativeFromData.chartWith keyOnly NarrativeFromData.Line None series)

            Expect.equal (props.TryFind "chart.artifactKey") (Some "result-8f2c") "the declared half rides"
            Expect.isNone (props.TryFind "chart.datasetVintage") "the absent half emits no prop at all"
            Expect.equal (NarrativeFromData.chartBinding props) keyOnly "and reads back as the same half"
        }

        test "a chart block with no binding props reads back as unbound" {
            let props = propsOf (NarrativeFromData.chart NarrativeFromData.Line None series)

            Expect.equal
                (NarrativeFromData.chartBinding props)
                NarrativeFromData.noBinding
                "absent props are 'declares no binding', not 'unknown'"
        }

        test "the binding survives the narrative wire round-trip" {
            // Additive props ride the existing Map<string, string>
            // serialisation — the claim the phase makes about the codec,
            // verified against the codec rather than assumed.
            let doc =
                docWith [
                    NarrativeFromData.chartWith binding NarrativeFromData.Bar (Some "Revenue") series
                ]

            let json = JsonSerializer.Serialize(doc, jsonOptions)
            let back = JsonSerializer.Deserialize<NarrativeDocument>(json, jsonOptions)
            Expect.equal back doc "the bound document round-trips to an equal value"

            match back.Sections[0].Elements with
            | [ Component("chart", props) ] ->
                Expect.equal (NarrativeFromData.chartBinding props) binding "the binding is recoverable after the wire"
            | other -> failtestf "expected one chart Component, got %A" other
        }

        test "an unbound chart round-trips unchanged too" {
            let doc = docWith [ NarrativeFromData.chart NarrativeFromData.Line None series ]
            let json = JsonSerializer.Serialize(doc, jsonOptions)
            let back = JsonSerializer.Deserialize<NarrativeDocument>(json, jsonOptions)
            Expect.equal back doc "the unbound document round-trips to an equal value"

            Expect.isFalse (json.Contains "chart.artifactKey") "no binding key reaches the wire when none was declared"
        }

        test "declaring a binding changes no drawn byte" {
            // The renderer reads three keys and draws from those; a
            // binding is a claim a reader resolves, not a mark. So a
            // published page is byte-identical whether or not the block
            // says where its numbers came from.
            let unbound =
                renderProps (NarrativeFromData.chart NarrativeFromData.Bar (Some "Revenue") series)

            let bound =
                renderProps (NarrativeFromData.chartWith binding NarrativeFromData.Bar (Some "Revenue") series)

            Expect.equal (norm bound) (norm unbound) "the SVG is unchanged by the declaration"
        }
    ]