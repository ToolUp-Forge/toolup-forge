// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Tests.NarrativeEgressTests

open System.Text
open Expecto
open ToolUp.OpenXml
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Reporting
open ToolUp.Reporting.Docx
open ToolUp.Reporting.Tests.Fakes

// ─── Phase 575's spillover, closed (Phase 534) ───────────────────────
//
// Phase 575 shipped `NarrativeDocument → OOXML` and wired it into
// `DocxReportRenderer`, but the shipped API path never reached it: the
// Phase 564 handler resolved every top-level `NarrativeValue` to a
// `TextValue` before any renderer ran, so a narrative exported as
// `.docx` through `IReportApi.Render` arrived as flattened markdown
// prose. The renderer half was complete and reachable only by a caller
// who bypassed the handler — which is to say, by a test.
//
// These cases are the ones that could not have been written in either
// package alone, which is why this pack exists: `ToolUp.Reporting.Server`
// (the handler) and `ToolUp.Reporting.Docx` (the renderer) do not
// reference each other, and neither should.
//
// What is asserted end-to-end, THROUGH the handler:
//
//   1. the structural path is reachable — native Word headings, tables
//      and list items come out, which flattened markdown text cannot
//      produce at any indentation;
//   2. the disclosure door still runs FIRST — the redacted value and
//      the appended "Withheld values" section are what the projection
//      sees, which is the invariant Phase 564 established and the one
//      that would be worth breaking nothing to gain;
//   3. every other format is untouched (GP 11);
//   4. a renderer that does NOT declare structural expansion still
//      receives text, so keying the decision on the renderer rather
//      than on the format is not merely tidier — it is what keeps a
//      third-party `Docx` renderer working.

let private scopeId = "team-alpha"

/// A narrative with structures a markdown flattening cannot reproduce
/// in a `.docx`: a real heading, a real table, real list items.
let private structuredNarrative: NarrativeDocument = {
    Title = "Quarterly Review"
    Subtitle = None
    Provenance = None
    Lang = None
    CanonicalUrl = None
    Sections = [
        {
            Id = "findings"
            Heading = "Findings"
            Subheading = None
            // Qualified throughout: `ToolUp.OpenXml` and
            // `ToolUp.Reporting` both put a `Paragraph` / `Table` /
            // `Text` in scope, and the last `open` would otherwise
            // silently win — the same class of defect the
            // `MarkdownRenderer` / `HtmlRenderer` note in
            // `ReportingCompose` records.
            Elements = [
                NarrativeElement.Paragraph [ InlineSpan.Text "Revenue held up." ]
                BulletList [ [ InlineSpan.Text "north was flat" ]; [ InlineSpan.Text "south grew" ] ]
                NarrativeElement.Table(
                    [ "Region", Left; "Value", Right ],
                    [ [ [ InlineSpan.Text "North" ]; [ InlineSpan.Text "42" ] ] ]
                )
                NarrativeElement.Paragraph [ Metric("Revenue", "1.2m", Some "fact-revenue") ]
            ]
        }
    ]
}

/// A one-paragraph .docx whose only content is a whole-paragraph
/// narrative token — the anchor the projection expands at.
let private anchorTemplate: ReportTemplate = {
    Id = "narrative-anchor"
    DisplayName = "Narrative anchor"
    Format = Docx
    Body = Emit.toBytes (DocModel.ofBlocks [ Block.Paragraph(ParagraphModel.create [ Run.plain "{{body}}" ]) ])
    Placeholders = [
        {
            Key = "body"
            DisplayName = "Body"
            Kind = PlaceholderKind.Text
            Required = true
        }
    ]
    Version = 1
}

/// A gate that denies exactly the named facts.
type private DenyingGate(denied: (string * string) list) =
    let denied = Map.ofList denied
    let mutable surfaces = []

    /// Every `(surface, factIds)` the gate was asked about — proof the
    /// door ran, and at which surface.
    member _.Consulted = List.rev surfaces

    interface IFactDisclosureGate with
        member _.Check(_, _, surface, factIds) = async {
            surfaces <- (surface, factIds) :: surfaces

            return
                factIds
                |> List.map (fun id ->
                    match denied.TryFind id with
                    | Some policyRef -> id, FactNotDisclosable policyRef
                    | None -> id, FactDisclosable)
                |> Map.ofList
        }

/// A renderer that records the values it was handed and emits nothing
/// interesting. The control for case 4: it claims `Docx` and declares
/// no structural expansion, exactly as a third-party renderer written
/// before Phase 534 would.
type private RecordingFlatRenderer() =
    let mutable received: Map<string, PlaceholderValue> = Map.empty
    member _.Received = received

    interface IReportRenderer with
        member _.SupportedFormats = [ Docx ]
        member _.Name = "RecordingFlat"

        member _.Render(_, values) = async {
            received <- values
            return Ok(Encoding.UTF8.GetBytes "flat")
        }

let private importBlocks (bytes: byte[]) : Block list =
    (Import.fromBytes bytes).Model.Sections |> List.collect _.Blocks

let private allText (bytes: byte[]) =
    importBlocks bytes |> List.map Block.text |> String.concat "\n"

/// Build a report API over a registry containing exactly the supplied
/// renderer, with the anchor template seeded.
let private apiOver (renderer: IReportRenderer) (gate: IFactDisclosureGate option) =
    let templates = InMemoryTemplateStore()
    templates.Seed(scopeId, anchorTemplate)

    let registry =
        ReportingCompose.buildDefaultRegistry ()
        |> ReportingCompose.withRenderer renderer

    let storeBlob: ReportApiHandler.StoreBlob =
        fun _ _ _ -> async { return Ok("unused", 1) }

    let audit: ReportApiHandler.AuditOnRender = fun _ -> async { return () }

    match gate with
    | Some gate ->
        ReportApiHandler.createWithDisclosureGate
            gate
            "operator"
            templates
            registry
            storeBlob
            audit
            ReportApiConfig.defaults
            scopeId
    | None -> ReportApiHandler.create templates registry storeBlob audit ReportApiConfig.defaults scopeId

let private renderThroughApi (api: IReportApi) (values: Map<string, PlaceholderValue>) =
    match api.Render(anchorTemplate.Id, values) |> Async.RunSynchronously with
    | Ok(RenderedInline(bytes, _)) -> bytes
    | Ok other -> failtestf "expected an inline render, got %A" other
    | Error e -> failtestf "render failed: %A" e

[<Tests>]
let tests =
    testList "Phase 575 spillover — the Docx narrative egress path" [

        test "a narrative rendered THROUGH the report API produces native Word structures" {
            let api = apiOver (DocxReportRenderer.create ()) None

            let bytes =
                renderThroughApi api (Map [ "body", NarrativeValue structuredNarrative ])

            let blocks = importBlocks bytes

            // The three assertions a flattened markdown string could not
            // satisfy at any indentation: a real heading block, a real
            // table block, real list items.
            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.Heading _ -> true
                     | _ -> false))
                "the narrative's section heading became a Word heading block"

            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.Table _ -> true
                     | _ -> false))
                "and its table became a native Word table, not tab-separated text"

            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.ListItem _ -> true
                     | _ -> false))
                "and its bullets became list items"

            let text = allText bytes
            Expect.stringContains text "Revenue held up." "the prose is present"

            Expect.isFalse
                (text.Contains "## " || text.Contains "- north was flat")
                "and no markdown syntax leaked in — which is what the flattened path produced"
        }

        test "the disclosure door still runs FIRST, and its redaction reaches the structural projection" {
            let gate = DenyingGate [ "fact-revenue", "policy/confidential" ]
            let api = apiOver (DocxReportRenderer.create ()) (Some gate)

            let bytes =
                renderThroughApi api (Map [ "body", NarrativeValue structuredNarrative ])

            Expect.equal
                (gate.Consulted |> List.map fst)
                [ FactExport ]
                "the gate was consulted exactly once, at the FactExport surface"

            Expect.equal (gate.Consulted |> List.collect snd) [ "fact-revenue" ] "with the fact the narrative cites"

            let text = allText bytes

            Expect.isFalse (text.Contains "1.2m") "the denied value is not in the emitted document"

            Expect.stringContains
                text
                (NarrativeFacts.notDisclosableMarker "policy/confidential")
                "it was replaced by the policy-naming marker"

            Expect.stringContains
                text
                "Withheld values"
                "and the door's withheld-values section survived the structural projection as an ordinary section"
        }

        test "a renderer that declares no structural expansion still receives flattened text" {
            let flat = RecordingFlatRenderer()
            let api = apiOver flat None

            renderThroughApi api (Map [ "body", NarrativeValue structuredNarrative ])
            |> ignore

            match flat.Received.TryFind "body" with
            | Some(TextValue text) ->
                Expect.stringContains text "Revenue held up." "the narrative arrived as its markdown projection"
            | other ->
                failtestf
                    "a renderer declaring no structural expansion must still receive TextValue — got %A. Keying this on the FORMAT rather than the renderer would break every third-party Docx renderer on upgrade."
                    other
        }

        test "the Docx renderer declares Docx and only Docx as structurally expanded" {
            match box (DocxReportRenderer.create ()) with
            | :? IStructuralNarrativeRenderer as structural ->
                Expect.equal
                    structural.StructuralNarrativeFormats
                    [ Docx ]
                    "the declaration is the renderer's own, and matches what it actually expands"
            | _ -> failtest "DocxReportRenderer must declare IStructuralNarrativeRenderer, or the API path flattens"
        }

        test "a values map with no narrative is untouched, and non-narrative formats are unchanged" {
            let api = apiOver (DocxReportRenderer.create ()) None
            let bytes = renderThroughApi api (Map [ "body", TextValue "just text" ])

            Expect.stringContains (allText bytes) "just text" "a scalar substitution is unaffected"

            // The Markdown renderer declares no structural expansion, so
            // a narrative bound for it still flattens — the pre-534
            // behaviour, byte for byte (GP 11).
            let markdownTemplate = {
                anchorTemplate with
                    Id = "markdown-anchor"
                    Format = Markdown
                    Body = Encoding.UTF8.GetBytes "{{body}}"
            }

            let templates = InMemoryTemplateStore()
            templates.Seed(scopeId, markdownTemplate)

            let markdownApi =
                ReportApiHandler.create
                    templates
                    (ReportingCompose.buildDefaultRegistry ())
                    (fun _ _ _ -> async { return Ok("unused", 1) })
                    (fun _ -> async { return () })
                    ReportApiConfig.defaults
                    scopeId

            match
                markdownApi.Render(markdownTemplate.Id, Map [ "body", NarrativeValue structuredNarrative ])
                |> Async.RunSynchronously
            with
            | Ok(RenderedInline(bytes, _)) ->
                Expect.stringContains
                    (Encoding.UTF8.GetString bytes)
                    "Revenue held up."
                    "the markdown path still renders the narrative as text"
            | other -> failtestf "expected an inline markdown render, got %A" other
        }
    ]

// ─── The component-registry compose surface (Phase 575 deviation 2) ──
//
// `DocxReportRenderer.createWith` took a component registry and nothing
// in `ReportingCompose` offered a place to declare one, so every
// deployment reached for the no-argument `create ()` and every narrative
// `Component` block silently took its data-table degradation: the seam
// existed with nothing plugged into it.
//
// The registry could not simply be declared in `ReportingCompose` —
// Reporting must not name a rendering companion (GP 1), and the compose
// module cannot reference a sub-companion without inverting the
// dependency. So the compose surface names a FUNCTION SHAPE, and the
// sub-companion supplies a factory of that shape.

let private componentNarrative (name: string) : NarrativeDocument = {
    Title = "With a component"
    Subtitle = None
    Provenance = None
    Lang = None
    CanonicalUrl = None
    Sections = [
        {
            Id = "s"
            Heading = "Section"
            Subheading = None
            Elements = [ NarrativeElement.Component(name, Map [ "series", "a=1;b=2" ]) ]
        }
    ]
}

[<Tests>]
let componentTests =
    testList "Phase 575 spillover — the component-registry compose surface" [

        test "the adapter maps every Core result case onto the projection's own" {
            let registry: ReportComponentRegistry =
                fun name _ ->
                    match name with
                    | "svg" -> ComponentSvg "<svg/>"
                    | "image" -> ComponentImage([| 1uy; 2uy |], "image/png")
                    | _ -> ComponentFallback

            let adapted = DocxReportRenderer.ofComponentRegistry registry

            Expect.equal
                (adapted "svg" Map.empty)
                (NarrativeOoxml.ComponentResult.Svg "<svg/>")
                "an SVG result crosses the adapter unchanged"

            Expect.equal
                (adapted "image" Map.empty)
                (NarrativeOoxml.ComponentResult.Image([| 1uy; 2uy |], "image/png"))
                "and so does a raster one"

            Expect.equal
                (adapted "anything-else" Map.empty)
                NarrativeOoxml.Fallback
                "and declining maps onto the degradation, never an error"
        }

        test "the default compose registers nothing, and components degrade exactly as Phase 575 shipped" {
            let registry =
                ReportingCompose.buildRegistryWith {
                    ReportingCompose.ReportingComposeOptions.defaults with
                        ComponentAwareRenderers = [ DocxReportRenderer.createWithComponents ]
                }

            let renderer =
                match registry.Route Docx with
                | Ok renderer -> renderer
                | Error e -> failtestf "the Docx renderer was not composed: %A" e

            let bytes =
                match
                    renderer.Render(anchorTemplate, Map [ "body", NarrativeValue(componentNarrative "chart") ])
                    |> Async.RunSynchronously
                with
                | Ok bytes -> bytes
                | Error e -> failtestf "render failed: %A" e

            // The degradation, not an error and not a blank: the same
            // thing `create ()` produces.
            Expect.isNonEmpty (importBlocks bytes) "the document still has content"
            Expect.isFalse (allText bytes |> System.String.IsNullOrWhiteSpace) "and it is not blank"
        }

        test "a declared registry reaches the composed renderer" {
            let mutable asked = []

            let chartRegistry: ReportComponentRegistry =
                fun name props ->
                    asked <- (name, props) :: asked
                    ComponentSvg "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>"

            let registry =
                ReportingCompose.buildRegistryWith {
                    ReportingCompose.ReportingComposeOptions.defaults with
                        NarrativeComponents = chartRegistry
                        ComponentAwareRenderers = [ DocxReportRenderer.createWithComponents ]
                }

            let renderer =
                match registry.Route Docx with
                | Ok renderer -> renderer
                | Error e -> failtestf "the Docx renderer was not composed: %A" e

            renderer.Render(anchorTemplate, Map [ "body", NarrativeValue(componentNarrative "chart") ])
            |> Async.RunSynchronously
            |> ignore

            Expect.equal
                (asked |> List.map fst)
                [ "chart" ]
                "the composed registry was consulted for the narrative's component block"

            Expect.equal
                (asked |> List.map snd)
                [ Map [ "series", "a=1;b=2" ] ]
                "with the block's own props — this is the seam that was declared and never plugged in"
        }

        test "plain renderers and component-aware factories compose together" {
            let flat = RecordingFlatRenderer()

            let registry =
                ReportingCompose.buildRegistryWith {
                    ReportingCompose.ReportingComposeOptions.defaults with
                        Renderers = [ flat ]
                        ComponentAwareRenderers = [ DocxReportRenderer.createWithComponents ]
                }

            // Both claim Docx; last-registered wins per the registry's
            // documented rule, and the component-aware factories are
            // applied after the plain list.
            Expect.isOk (registry.Route Docx) "a Docx renderer is composed"
            Expect.isOk (registry.Route Markdown) "and the zero-dep defaults are still there"
            Expect.isOk (registry.Route Html) "both of them"
        }
    ]