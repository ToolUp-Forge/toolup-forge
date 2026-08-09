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
            let cases = [
                "line", NarrativeFromData.Line, Some "Revenue"
                "bar", NarrativeFromData.Bar, Some "Revenue"
                "area", NarrativeFromData.Area, None
            ]

            for token, kind, title in cases do
                let spec = {
                    boundSpec with
                        Kind = token
                        Title = title
                }

                let mine = ChartArtifact.props spec

                let grammar =
                    match NarrativeFromData.chart kind title (series |> List.map (fun p -> p.Label, p.Value)) with
                    | ToolUp.Platform.Narrative.Component("chart", props) -> props
                    | other -> failtestf "expected a chart Component, got %A" other

                Expect.equal mine grammar (sprintf "prop bags agree for %s" token)
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