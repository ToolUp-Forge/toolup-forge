// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeckExportSeamTests

open System.Text
open Expecto
open Giraffe.ViewEngine
open ToolUp.PublicRendering
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Reporting.RendererRegistry
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 647 — the deck-export seam ────────────────────────────────
//
// Two halves, and this pack holds each to a different bar.
//
// **The routing half** is a refusal, so what is worth asserting is not
// that it fails but HOW. `NoRendererForFormat` and
// `FormatServedByDeckTier` are both failures and both typed, and only one
// of them tells the truth: the first sends a consumer looking for a
// package that deliberately does not exist. The pack therefore pins the
// case, pins that a registered deck renderer does not change it (routing
// away is unconditional — "never token-filled" is control flow here, not
// a convention), and pins that the message says where decks come from.
//
// **The handoff half** is a promise of determinism, and a determinism
// test that renders through a stub proves the stub. So the artifact tests
// drive the REAL chart grammar — the same `NarrativeCharts.renderChart`
// published pages draw through — and the conformance case pins
// `ChartArtifact.props` against the grammar's OWN projector
// (`NarrativeFromData.chart`), which is what keeps the reproduced prop
// encoding a shared surface rather than a second grammar that agrees
// today.

// ── the real chart grammar, wired as a composition would wire it ─────

/// The composition-supplied bridge from the handoff contract to the
/// deployment's own deterministic chart renderer. Verbatim the shape the
/// `ChartArtifact` module header documents.
let private svgRenderer: ChartArtifactRenderer = {
    MediaType = "image/svg+xml"
    Render =
        fun props ->
            NarrativeCharts.renderChart props
            |> RenderView.AsString.htmlNode
            |> Encoding.UTF8.GetBytes
}

let private series = [
    { Label = "Jan"; Value = 10.0 }
    { Label = "Feb"; Value = 20.0 }
    { Label = "Mar"; Value = 15.0 }
]

let private boundSpec: ChartArtifactSpec = {
    Kind = "bar"
    Title = Some "Revenue"
    Points = series
    Binding = {
        ArtifactKey = Some "result-8f2c"
        DatasetVintage = Some "dataset-v17"
    }
}

let private unboundSpec: ChartArtifactSpec = {
    boundSpec with
        Binding = {
            ArtifactKey = None
            DatasetVintage = None
        }
}

// ── Phase 649 — the same three bindings, said in both vocabularies ───
//
// The two surfaces reproduce the grammar rather than share a type (GP 1),
// so a conformance case has to state each binding twice — which is the
// point: if the halves ever disagree about what a binding IS, the pairing
// below is where it shows.

let private keyOnlyBinding: ChartBinding = {
    ArtifactKey = Some "result-8f2c"
    DatasetVintage = None
}

let private projectorBound: NarrativeFromData.ChartBinding = {
    ArtifactKey = Some "result-8f2c"
    DatasetVintage = Some "dataset-v17"
}

let private projectorKeyOnly: NarrativeFromData.ChartBinding = {
    ArtifactKey = Some "result-8f2c"
    DatasetVintage = None
}

// ── a template store carrying exactly one template ───────────────────

let private storeWith (template: ReportTemplate) =
    { new IReportTemplateStore with
        member _.List _ = async.Return [ template ]

        member _.Get(_, id) =
            async.Return(if id = template.Id then Some template else None)

        member _.Save(_, t) = async.Return(Ok t)
        member _.Delete(_, _) = async.Return(Ok())
    }

let private templateOf (format: TemplateFormat) : ReportTemplate = {
    Id = "tpl-647"
    DisplayName = "Quarterly deck"
    Format = format
    Body = Encoding.UTF8.GetBytes "{{title}}"
    Placeholders = []
    Version = 1
}

/// A renderer that claims `Pptx` — the "somebody wired a deck renderer
/// anyway" fixture. Its body is never reached; that is the assertion.
let private deckRendererClaimingPptx =
    { new IReportRenderer with
        member _.SupportedFormats = [ Pptx ]
        member _.Name = "ThirdPartyDeckRenderer"

        member _.Render(_, _) =
            async.Return(Ok(Encoding.UTF8.GetBytes "token-filled deck"))
    }

let private renderThrough (registry: RendererRegistry) (format: TemplateFormat) = async {
    let api =
        ReportApiHandler.create
            (storeWith (templateOf format))
            registry
            (fun _ _ _ -> async.Return(Ok("blob", 1)))
            (fun _ -> async.Return())
            ReportApiConfig.defaults
            "scope-647"

    return! api.Render("tpl-647", Map.empty)
}

let tests =
    testList "Phase 647 Deck export seam" [

        // ── routing posture ──────────────────────────────────────────

        test "Pptx is declared deck-tier-served and is the only such format" {
            Expect.isTrue (DeckExport.isServed Pptx) "Pptx routes to the deck tier"
            Expect.equal DeckExport.servedFormats [ Pptx ] "exactly one deck-tier format today"

            for format in [ Markdown; Html; Pdf; Docx; Xlsx ] do
                Expect.isFalse (DeckExport.isServed format) (sprintf "%A is an ordinary renderer format" format)
        }

        test "the default registry routes Pptx to the deck tier, not to a missing package" {
            let registry = ReportingCompose.buildDefaultRegistry ()

            match registry.Route Pptx with
            | Error(FormatServedByDeckTier fmt) -> Expect.equal fmt Pptx "the refusal names the requested format"
            | Error other -> failtestf "expected FormatServedByDeckTier, got %A" other
            | Ok renderer -> failtestf "expected a refusal, got renderer %s" renderer.Name
        }

        test "a registered Pptx renderer does not change the routing" {
            // The load-bearing case. If `Route` consulted the registry
            // first, this would resolve `ThirdPartyDeckRenderer` and the
            // Track-A path would quietly become a token-fill deck
            // renderer — the outcome the whole posture exists to refuse.
            let registry =
                ReportingCompose.buildDefaultRegistry ()
                |> ReportingCompose.withRenderer deckRendererClaimingPptx

            Expect.isSome (registry.TryResolve Pptx) "fixture precondition: the renderer IS registered"

            match registry.Route Pptx with
            | Error(FormatServedByDeckTier _) -> ()
            | Error other -> failtestf "expected FormatServedByDeckTier, got %A" other
            | Ok renderer -> failtestf "routing resolved a deck renderer (%s) — it must not" renderer.Name
        }

        test "an ordinary unregistered format still refuses NoRendererForFormat" {
            // The other side of the distinction: routing away is scoped
            // to the deck tier and has not swallowed the ordinary
            // missing-PackageReference signal.
            let registry = ReportingCompose.buildDefaultRegistry ()

            match registry.Route Docx with
            | Error(NoRendererForFormat fmt) -> Expect.equal fmt Docx "names the unresolved format"
            | Error other -> failtestf "expected NoRendererForFormat, got %A" other
            | Ok renderer -> failtestf "expected a refusal, got renderer %s" renderer.Name
        }

        test "the deck-tier refusal message points at the deck tier, not at a package" {
            let message = RenderError.toMessage (FormatServedByDeckTier Pptx)

            Expect.stringContains message "deck" "the message names deck output"

            Expect.isFalse
                (message.Contains "No renderer registered")
                "must not read as a missing-PackageReference failure"
        }

        test "rendering a Pptx template through the report API refuses at the routing step" {
            let outcome =
                renderThrough (ReportingCompose.buildDefaultRegistry ()) Pptx
                |> Async.RunSynchronously

            match outcome with
            | Error(Renderer(FormatServedByDeckTier fmt)) -> Expect.equal fmt Pptx "refusal carries the format"
            | other -> failtestf "expected Renderer(FormatServedByDeckTier Pptx), got %A" other
        }

        test "rendering an ordinary template is untouched by the routing change" {
            // GP 11 — the pre-647 render path still renders.
            let outcome =
                renderThrough (ReportingCompose.buildDefaultRegistry ()) Markdown
                |> Async.RunSynchronously

            match outcome with
            | Ok(RenderedInline(bytes, _)) -> Expect.isGreaterThan bytes.Length 0 "produced bytes"
            | other -> failtestf "expected an inline render, got %A" other
        }

        // ── chart-artifact handoff ───────────────────────────────────

        test "props agree with the chart grammar's own projector" {
            // The conformance pin. `ChartArtifact.props` reproduces the
            // grammar's prop keys and point encoding (companion isolation
            // — GP 1); this asserts the reproduction rather than trusting
            // the comment that says so.
            //
            // Phase 649 widened it across the binding axis, because the
            // binding props are exactly where two reproductions of one
            // grammar drift: a half binding, or an empty prop standing in
            // for an absent one, agrees on the unbound case and disagrees
            // on every real document.
            let kinds = [
                "line", NarrativeFromData.Line, Some "Revenue"
                "bar", NarrativeFromData.Bar, Some "Revenue"
                "area", NarrativeFromData.Area, None
            ]

            let bindings = [
                "bound", boundSpec.Binding, projectorBound
                "key-only", keyOnlyBinding, projectorKeyOnly
                "unbound", unboundSpec.Binding, NarrativeFromData.noBinding
            ]

            for token, kind, title in kinds do
                for label, mineBinding, grammarBinding in bindings do
                    let spec = {
                        boundSpec with
                            Kind = token
                            Title = title
                            Binding = mineBinding
                    }

                    let mine = ChartArtifact.props spec

                    let grammar =
                        match
                            NarrativeFromData.chartWith
                                grammarBinding
                                kind
                                title
                                (series |> List.map (fun p -> p.Label, p.Value))
                        with
                        | ToolUp.Platform.Narrative.Component("chart", props) -> props
                        | other -> failtestf "expected a chart Component, got %A" other

                    Expect.equal mine grammar (sprintf "prop bags agree for %s / %s" token label)

                    Expect.equal
                        (ChartArtifact.bindingOf mine)
                        spec.Binding
                        (sprintf "the artifact side reads its own props back for %s / %s" token label)
        }

        test "an unbound spec's props are byte-identical to the pre-binding bag" {
            // GP 11 from the artifact side: a consumer that never fills a
            // binding hands the renderer exactly the bag it handed before.
            let props = ChartArtifact.props unboundSpec

            Expect.equal
                (props |> Map.toList |> List.map fst)
                [ ChartArtifact.KindProp; ChartArtifact.PointsProp; ChartArtifact.TitleProp ]
                "three keys, exactly as before Phase 649"
        }

        test "a chart renders deterministically — same spec, byte-identical artifact" {
            let first = ChartArtifact.render svgRenderer boundSpec
            let second = ChartArtifact.render svgRenderer boundSpec

            Expect.isGreaterThan first.Content.Length 0 "the grammar produced bytes"
            Expect.equal first.Content second.Content "two renders are byte-identical"
            Expect.equal first.ContentHash second.ContentHash "and hash identically"
            Expect.stringStarts first.ContentHash "sha256:" "content-address shape"
            Expect.equal first.ContentHash (ChartArtifact.contentHash first.Content) "the hash is over its own bytes"
        }

        test "a changed series changes the artifact hash" {
            // The falsifier for the determinism case above: identical
            // hashes are only evidence if a different input produces a
            // different one.
            let changed = {
                boundSpec with
                    Points = { Label = "Jan"; Value = 11.0 } :: List.tail series
            }

            let a = ChartArtifact.render svgRenderer boundSpec
            let b = ChartArtifact.render svgRenderer changed
            Expect.notEqual a.ContentHash b.ContentHash "a different series is a different artifact"
        }

        test "a bound chart's metadata carries its grammar identity and both provenance refs" {
            let artifact = ChartArtifact.render svgRenderer boundSpec
            let meta = artifact.Metadata

            Expect.equal artifact.MediaType "image/svg+xml" "the renderer's declared media type rides along"
            Expect.equal meta.Grammar ChartArtifact.Grammar "grammar identity stamped"
            Expect.equal meta.GrammarVersion ChartArtifact.GrammarVersion "prop-format version stamped"
            Expect.equal meta.Kind "bar" "kind carried"
            Expect.equal meta.Title (Some "Revenue") "title carried"
            Expect.equal meta.PointCount 3 "point count carried"
            Expect.equal meta.ArtifactKey (Some "result-8f2c") "artifact key carried"
            Expect.equal meta.DatasetVintage (Some "dataset-v17") "dataset vintage carried"
            Expect.isTrue (ChartArtifact.isBound meta) "a fully-bound chart reports bound"
        }

        test "an unbound or half-bound chart does not report bound" {
            let unbound = (ChartArtifact.render svgRenderer unboundSpec).Metadata
            Expect.isFalse (ChartArtifact.isBound unbound) "no binding is not a binding"

            let keyOnly = {
                unbound with
                    ArtifactKey = Some "result-8f2c"
            }

            Expect.isFalse (ChartArtifact.isBound keyOnly) "a key without a vintage is half an answer"

            let vintageOnly = {
                unbound with
                    DatasetVintage = Some "dataset-v17"
            }

            Expect.isFalse (ChartArtifact.isBound vintageOnly) "a vintage without a key is the other half"
        }

        test "metadata is derivable without rendering" {
            // So an export tier can gate on the binding before paying for
            // the render.
            let meta = ChartArtifact.metadata boundSpec
            Expect.equal meta (ChartArtifact.render svgRenderer boundSpec).Metadata "same metadata either way"
        }
    ]

// ─── Phase 650 — the chart export bundle ─────────────────────────────
//
// 647 could render one chart a tier already had a spec for; 649 put the
// binding on the block. The bundle is the pairing: a document plus a
// block-keyed collection of its rendered artifacts, in one call.
//
// Three things are worth pinning and they are not the same kind of claim.
//
// **The keying rule** is a contract with a consumer that will recompute
// it, so the pack fixes the walk order explicitly — including the two
// cases a reimplementation gets wrong: a chart nested in a container
// (depth-first at the point the container appears, not appended after its
// siblings) and two IDENTICAL chart blocks (distinguishable only because
// the key is positional).
//
// **Partiality** is a promise about failure, so it is tested by failing:
// a block with no series and a renderer that raises on one block both
// leave the other artifacts in the bundle and name themselves typed.
//
// **The disclosure discipline** is the claim that a bundle is an export
// surface rather than a side door. What proves it is not that a gate
// exists but that the SAME door ran — so the gate stub records the
// surface it was asked about, and the denied value's marker is asserted
// in the bundle's own document.

let private points = series |> List.map (fun p -> p.Label, p.Value)

let private chartBlockBound =
    NarrativeFromData.chartWith projectorBound NarrativeFromData.Bar (Some "Revenue") points

let private chartBlockUnbound =
    NarrativeFromData.chart NarrativeFromData.Line (Some "Sessions") points

let private chartBlockNoSeries =
    NarrativeFromData.chart NarrativeFromData.Area (Some "Pending") []

let private propsOf (element: NarrativeElement) : Map<string, string> =
    match element with
    | Component("chart", props) -> props
    | other -> failtestf "expected a chart Component, got %A" other

let private sectionOf (id: string) (elements: NarrativeElement list) : NarrativeSection = {
    Id = id
    Heading = id
    Subheading = None
    Elements = elements
}

let private documentOf (sections: NarrativeSection list) : NarrativeDocument = {
    Title = "Quarterly review"
    Subtitle = None
    Sections = sections
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

/// Three chart blocks: one at section top level, one nested inside a
/// card, and one byte-identical to the first. The nesting and the
/// duplicate are the two cases the keying rule has to answer.
let private bundleDocument =
    documentOf [
        sectionOf "intro" [ Paragraph [ Text "Revenue rose." ]; chartBlockBound ]
        sectionOf "detail" [
            Card {
                Heading = Some "Nested"
                Image = None
                Body = [ chartBlockUnbound ]
            }
            chartBlockBound
        ]
    ]

/// A renderer that raises on bar charts — the "one block fails" fixture.
/// Everything else renders through the real grammar, so what the partial
/// bundle keeps is genuine output rather than a stub's.
let private failsOnBars: ChartArtifactRenderer = {
    MediaType = svgRenderer.MediaType
    Render =
        fun props ->
            if props.TryFind ChartArtifact.KindProp = Some "bar" then
                failwith "this deployment draws no bars"
            else
                svgRenderer.Render props
}

/// A document whose prose quotes a fact — the disclosure fixture.
let private factDocument =
    documentOf [
        sectionOf "figures" [
            Paragraph [ Metric("Revenue", "1200000", Some "fact-restricted") ]
            chartBlockBound
        ]
    ]

/// A gate with preset verdicts that records which egress surface it was
/// asked about — the recording is the assertion, since "a gate ran" is
/// weaker than "the export door ran".
type private RecordingGate(verdicts: Map<string, FactDisclosureVerdict>) =
    let mutable surfaces: FactEgressSurface list = []

    member _.Surfaces = List.rev surfaces

    interface IFactDisclosureGate with
        member _.Check(_, _, surface, factIds) = async {
            surfaces <- surface :: surfaces

            return
                factIds
                |> List.distinct
                |> List.map (fun id ->
                    id, (verdicts.TryFind id |> Option.defaultValue (FactNotDisclosable "unknown-fact")))
                |> Map.ofList
        }

let private metricValues (document: NarrativeDocument) : string list =
    document.Sections
    |> List.collect (fun section ->
        section.Elements
        |> List.collect (function
            | Paragraph spans -> spans
            | _ -> []))
    |> List.choose (function
        | Metric(_, value, _) -> Some value
        | _ -> None)

let bundleTests =
    testList "Phase 650 Chart export bundle" [

        // ── the keying rule ──────────────────────────────────────────

        test "blocks are keyed by document-order position, descending into containers" {
            let blocks = ChartExportBundle.blocks bundleDocument

            Expect.equal
                (blocks |> List.map _.Key)
                [ "chart:0"; "chart:1"; "chart:2" ]
                "positional keys in document order"

            Expect.equal
                (blocks |> List.map _.Ordinal |> List.map ChartExportBundle.keyFor)
                (blocks |> List.map _.Key)
                "keyFor is the function the walk keys with — a consumer needs no other"

            // The nested chart sits at ordinal 1, i.e. BEFORE the
            // top-level chart that follows its card. A walk that appended
            // container bodies after their siblings would key these two
            // the other way round and every artifact would pair with the
            // wrong block.
            Expect.equal
                (blocks[1] |> _.Props)
                (propsOf chartBlockUnbound)
                "the card's chart is walked where the card sits"

            Expect.equal
                (blocks |> List.map _.SectionId)
                [ "intro"; "detail"; "detail" ]
                "each block reports the section it renders under, nested or not"
        }

        test "two identical chart blocks are distinguishable" {
            // The reason the key is positional rather than derived from
            // content: a document may legitimately draw the same chart
            // twice, and a content-derived key would collapse them into
            // one entry.
            let blocks = ChartExportBundle.blocks bundleDocument

            Expect.equal blocks[0].Props blocks[2].Props "fixture precondition: the two blocks ARE identical"
            Expect.notEqual blocks[0].Key blocks[2].Key "and they still key apart"
        }

        test "a reconstructed spec round-trips to the block's own prop bag" {
            // The conformance pin for the read half. `specOf` decodes what
            // `ChartArtifact.props` encodes, so a canonically projected
            // block must survive the round trip byte-for-byte — otherwise
            // the artifact would be rendered from a bag that is not the
            // one the page draws from, which is the second-grammar failure
            // the whole seam is arranged to avoid.
            for block in ChartExportBundle.blocks bundleDocument do
                Expect.equal
                    (ChartArtifact.props (ChartExportBundle.specOf block.Props))
                    block.Props
                    (sprintf "%s round-trips" block.Key)

            let empty = propsOf chartBlockNoSeries

            Expect.equal (ChartArtifact.props (ChartExportBundle.specOf empty)) empty "an empty series round-trips too"

            // And the binding is read by the 649 reader, not a second one.
            Expect.equal
                (ChartExportBundle.specOf (propsOf chartBlockBound)).Binding
                (ChartArtifact.bindingOf (propsOf chartBlockBound))
                "one binding reader, not two"
        }

        // ── the pairing ──────────────────────────────────────────────

        test "one call pairs the document with an artifact per chart block" {
            let bundle = ChartExportBundle.ofDocument svgRenderer bundleDocument

            Expect.equal bundle.Document bundleDocument "the document rides along unchanged"

            Expect.equal
                (bundle.Charts |> Map.toList |> List.map fst)
                [ "chart:0"; "chart:1"; "chart:2" ]
                "every chart block is keyed into the collection"

            Expect.isEmpty bundle.Gaps "nothing refused"
            Expect.isTrue (ChartExportBundle.isComplete bundle) "so the bundle is complete"

            for key, artifact in Map.toList bundle.Charts do
                Expect.isGreaterThan artifact.Content.Length 0 (sprintf "%s produced bytes" key)
                Expect.equal artifact.MediaType "image/svg+xml" (sprintf "%s carries the renderer's media type" key)
                Expect.equal artifact.Metadata.Grammar ChartArtifact.Grammar (sprintf "%s stamps the grammar" key)
        }

        test "each artifact carries the binding its block declared" {
            // Phase 649's props are what the bundle carries through: a
            // tier reading the artifact recovers the same two identifiers
            // the block declares, without holding the document open.
            let bundle = ChartExportBundle.ofDocument svgRenderer bundleDocument
            let bound = bundle.Charts["chart:0"].Metadata
            let unbound = bundle.Charts["chart:1"].Metadata

            Expect.equal bound.Kind "bar" "kind read off the block"
            Expect.equal bound.Title (Some "Revenue") "title read off the block"
            Expect.equal bound.PointCount 3 "series decoded"
            Expect.equal bound.ArtifactKey (Some "result-8f2c") "artifact key carried through"
            Expect.equal bound.DatasetVintage (Some "dataset-v17") "dataset vintage carried through"
            Expect.isTrue (ChartArtifact.isBound bound) "a bound block yields a bound artifact"

            Expect.equal unbound.ArtifactKey None "an unbound block declares nothing"
            Expect.equal unbound.DatasetVintage None "in both fields"
            Expect.isFalse (ChartArtifact.isBound unbound) "and does not report bound"
        }

        // ── determinism ──────────────────────────────────────────────

        test "the same document yields a byte-identical bundle" {
            let first = ChartExportBundle.ofDocument svgRenderer bundleDocument
            let second = ChartExportBundle.ofDocument svgRenderer bundleDocument

            Expect.equal first second "two bundlings of one document are equal"

            Expect.equal
                (first.Charts |> Map.map (fun _ a -> a.ContentHash))
                (second.Charts |> Map.map (fun _ a -> a.ContentHash))
                "every artifact hashes identically"

            // The two identical blocks draw identical bytes — determinism
            // is a property of the spec, not of the position.
            Expect.equal
                first.Charts["chart:0"].ContentHash
                first.Charts["chart:2"].ContentHash
                "identical blocks render identically"
        }

        test "a changed series changes exactly the artifact it belongs to" {
            // The falsifier. Identical bundles are only evidence if a
            // different document produces a different one — and the change
            // must be local, or the "hash" is a document stamp rather than
            // a content address.
            let changedSeries = [ "Jan", 11.0; "Feb", 20.0; "Mar", 15.0 ]

            let changed =
                documentOf [
                    sectionOf "intro" [
                        Paragraph [ Text "Revenue rose." ]
                        NarrativeFromData.chartWith projectorBound NarrativeFromData.Bar (Some "Revenue") changedSeries
                    ]
                    sectionOf "detail" [
                        Card {
                            Heading = Some "Nested"
                            Image = None
                            Body = [ chartBlockUnbound ]
                        }
                        chartBlockBound
                    ]
                ]

            let before = ChartExportBundle.ofDocument svgRenderer bundleDocument
            let after = ChartExportBundle.ofDocument svgRenderer changed

            Expect.notEqual before after "a different document is a different bundle"

            Expect.notEqual
                before.Charts["chart:0"].ContentHash
                after.Charts["chart:0"].ContentHash
                "the changed block's artifact changed"

            Expect.equal
                before.Charts["chart:1"].ContentHash
                after.Charts["chart:1"].ContentHash
                "and no other block's did"
        }

        // ── partiality ───────────────────────────────────────────────

        test "a block with no series is a named gap, not a failed bundle" {
            let document =
                documentOf [
                    sectionOf "intro" [ chartBlockBound ]
                    sectionOf "empty" [ chartBlockNoSeries ]
                ]

            let bundle = ChartExportBundle.ofDocument svgRenderer document

            Expect.equal
                (bundle.Charts |> Map.toList |> List.map fst)
                [ "chart:0" ]
                "the renderable block still rendered"

            match bundle.Gaps with
            | [ gap ] ->
                Expect.equal gap.Key "chart:1" "the gap names the block"
                Expect.equal gap.Ordinal 1 "and its ordinal"
                Expect.equal gap.SectionId "empty" "and where to find it"
                Expect.equal gap.Refusal ChartBlockHasNoSeries "typed as the refusal it is"
            | other -> failtestf "expected exactly one gap, got %A" other

            Expect.isFalse (ChartExportBundle.isComplete bundle) "an incomplete bundle says so"
            Expect.equal bundle.Document document "and still carries the whole document"
        }

        test "a renderer that raises on one block does not lose the others" {
            // The load-bearing partiality case: the renderer belongs to
            // the deployment, so one failing chart must not deny an export
            // tier the ten that worked.
            let bundle = ChartExportBundle.ofDocument failsOnBars bundleDocument

            Expect.equal (bundle.Charts |> Map.toList |> List.map fst) [ "chart:1" ] "the line chart survived"

            Expect.equal
                (bundle.Gaps |> List.map _.Key)
                [ "chart:0"; "chart:2" ]
                "both bar blocks are gaps, in document order"

            for gap in bundle.Gaps do
                match gap.Refusal with
                | ChartRendererFailed reason ->
                    Expect.stringContains reason "draws no bars" "the renderer's own message is carried, not invented"
                | other -> failtestf "expected ChartRendererFailed, got %A" other
        }

        // ── disclosure discipline ────────────────────────────────────

        test "the gated bundle runs the same FactExport door the render path runs" {
            let gate =
                RecordingGate(Map.ofList [ "fact-restricted", FactNotDisclosable "restricted-policy" ])

            let bundle =
                NarrativeExportBundle.createWithDisclosureGate gate "principal-650" svgRenderer "scope-650" factDocument
                |> Async.RunSynchronously

            Expect.equal gate.Surfaces [ FactExport ] "checked once, at the export surface — not a bundle-specific door"

            Expect.contains
                (metricValues bundle.Document)
                (NarrativeFacts.notDisclosableMarker "restricted-policy")
                "the denied value is redacted in the bundle's own document"

            Expect.isTrue
                (bundle.Document.Sections
                 |> List.exists (fun section -> section.Id = "withheld-values"))
                "and the withheld-values note travels with it"

            Expect.equal (bundle.Charts |> Map.toList |> List.map fst) [ "chart:0" ] "the pairing survives the door"

            Expect.equal
                (ChartExportBundle.blocks bundle.Document |> List.map _.Key)
                (bundle.Charts |> Map.toList |> List.map fst)
                "and the keys index the DISCLOSED document, which is the one the bundle carries"
        }

        test "a gate that permits everything leaves the document unchanged" {
            // The falsifier for the case above — and GP 11 from the
            // disclosure side: a deployment whose facts are all
            // disclosable gets the ungated bundle exactly.
            let gate = RecordingGate(Map.ofList [ "fact-restricted", FactDisclosable ])

            let gated =
                NarrativeExportBundle.createWithDisclosureGate gate "principal-650" svgRenderer "scope-650" factDocument
                |> Async.RunSynchronously

            Expect.equal
                gated
                (NarrativeExportBundle.create svgRenderer factDocument)
                "an all-disclosable document bundles identically"
        }

        test "the ungated entry point is the honest no-fact-tier posture" {
            // GP 13 — no gate composed means no door to consult, not a
            // door quietly skipped.
            let bundle = NarrativeExportBundle.create svgRenderer factDocument

            Expect.equal bundle.Document factDocument "the document is exactly the one handed in"

            Expect.equal
                bundle
                (ChartExportBundle.ofDocument svgRenderer factDocument)
                "and the server entry point adds nothing else"
        }
    ]