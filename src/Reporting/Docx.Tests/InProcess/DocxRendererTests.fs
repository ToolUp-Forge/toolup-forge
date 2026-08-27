// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Docx.Tests.DocxRendererTests

open System.IO
open System.Text
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Validation
open Expecto
open ToolUp.OpenXml
open ToolUp.Reporting
open ToolUp.Reporting.Docx
open ToolUp.Platform.Tests.Contracts

// ─── Fixture helpers over the ToolUp.OpenXml structural model ────────

/// Wrap a textual fixture body in a minimal one-paragraph .docx — the
/// contract pack's body builder.
let private buildDocx (text: string) : byte[] =
    Emit.toBytes (DocModel.ofBlocks [ Paragraph(ParagraphModel.create [ Run.plain text ]) ])

let private buildDocxOf (blocks: Block list) : byte[] = Emit.toBytes (DocModel.ofBlocks blocks)

/// Text projection for the contract pack: re-import the rendered
/// document and flatten every block to text.
let private extractText (bytes: byte[]) : string =
    let imported = Import.fromBytes bytes

    imported.Model.Sections
    |> List.collect _.Blocks
    |> List.map Block.text
    |> String.concat "\n"

let private importBlocks (bytes: byte[]) : Block list =
    (Import.fromBytes bytes).Model.Sections |> List.collect _.Blocks

let private mkTemplate (body: byte[]) (placeholders: PlaceholderSchema list) : ReportTemplate = {
    Id = "fixture-template"
    DisplayName = "Fixture"
    Format = Docx
    Body = body
    Placeholders = placeholders
    Version = 1
}

let private textSchema (key: string) : PlaceholderSchema = {
    Key = key
    DisplayName = key
    Kind = Text
    Required = true
}

let private render template values =
    (DocxReportRenderer.create ()).Render(template, values)
    |> Async.RunSynchronously

let private expectOk result =
    match result with
    | Ok(bytes: byte[]) -> bytes
    | Error e -> failtestf "expected Ok, got %s" (RenderError.toMessage e)

let private blocksText (blocks: Block list) : string =
    blocks |> List.map Block.text |> String.concat "\n"

// ─── Narrative fixtures (Phase 575) ──────────────────────────────────
//
// The narrative DU shares case names with `PlaceholderKind` / `Block`
// (`Text`, `Image`, `Table`, `Paragraph`, `Heading`), so the fixtures
// live in a nested module whose local `open` makes the narrative cases
// win. Everything below the module keeps the file-level resolution.

module private NarrativeFixtures =

    open ToolUp.Platform.Narrative

    /// A document exercising every `NarrativeElement` case.
    let full: NarrativeDocument = {
        Title = "Quarterly Review"
        Subtitle = Some "every element kind"
        Provenance = None
        Lang = None
        CanonicalUrl = None
        Sections = [
            {
                Id = "findings"
                Heading = "Findings"
                Subheading = Some "a section subheading"
                Elements = [
                    Paragraph [
                        Text "Plain "
                        Emphasis "emphasised "
                        Strong "strong "
                        Code "inline-code"
                        Br
                        Link("https://example.org/a", [ Text "a link" ])
                        Image("https://example.org/i.png", "an inline image", None)
                    ]
                    Heading(3, [ Text "A sub-heading" ])
                    BulletList [ [ Text "first bullet" ]; [ Text "second bullet" ] ]
                    OrderedList [ [ Text "first step" ]; [ Text "second step" ] ]
                    KeyValueGrid [ "Revenue", [ Metric("Revenue", "1.2m", None) ] ]
                    Table(
                        [ "Region", Left; "Value", Right ],
                        [ [ [ Text "North" ]; [ Text "42" ] ]; [ [ Text "South" ]; [ Text "17" ] ] ]
                    )
                    Callout(Warning, [ Text "mind the gap" ])
                    CodeBlock(Some "fsharp", "let x = 1")
                    Blockquote(Some "A. Author", [ Text "quoted prose" ])
                    Divider
                    Video {
                        Sources = [
                            {
                                Src = "https://example.org/v.mp4"
                                Type = Some "video/mp4"
                            }
                        ]
                        Poster = None
                        Tracks = []
                        Caption = Some "a video caption"
                    }
                    Audio {
                        Sources = [
                            {
                                Src = "https://example.org/a.mp3"
                                Type = None
                            }
                        ]
                        Tracks = []
                        Caption = Some "an audio caption"
                    }
                    ImageGallery [
                        {
                            Src = "https://example.org/g.png"
                            Alt = "gallery image"
                            Caption = Some "gallery caption"
                            Href = None
                        }
                    ]
                    Embed {
                        Url = "https://example.org/e"
                        Title = "an embed title"
                        AspectRatio = None
                    }
                    Card {
                        Heading = Some "a card heading"
                        Image = None
                        Body = [ Paragraph [ Text "card body" ] ]
                    }
                    Accordion [ "a panel label", [ Paragraph [ Text "panel body" ] ] ]
                    Tabs [ "a tab label", [ Paragraph [ Text "tab body" ] ] ]
                    Component("chart", Map [ "chart.kind", "line"; "chart.points", "a=1;b=2" ])
                ]
            }
        ]
    }

    /// A minimal document carrying one element.
    let ofElements (elements: NarrativeElement list) : NarrativeDocument = {
        full with
            Subtitle = None
            Sections = [
                {
                    Id = "only"
                    Heading = "Only"
                    Subheading = None
                    Elements = elements
                }
            ]
    }

    let componentDocument (name: string) (props: Map<string, string>) = ofElements [ Component(name, props) ]

    let calloutOnly = ofElements [ Callout(Warning, [ Text "mind the gap" ]) ]

    let codeOnly = ofElements [ CodeBlock(Some "fsharp", "let x = 1") ]

    /// A metric quoting a classified fact — the Phase 564 export door's
    /// input shape.
    let factBearing (factRef: string) =
        ofElements [ Paragraph [ Metric("Revenue", "1.2m", Some factRef) ] ]

    let redact (denied: Map<string, string>) (document: NarrativeDocument) =
        NarrativeFacts.redactDeniedFacts denied document

    let deniedMarker (policyRef: string) =
        NarrativeFacts.notDisclosableMarker policyRef

    let plaintext (document: NarrativeDocument) = NarrativePlaintext.render document

let private noComponentRenderers: NarrativeOoxml.ComponentRenderers =
    fun _ _ -> NarrativeOoxml.Fallback

let private projectFull () =
    NarrativeOoxml.project noComponentRenderers NarrativeFixtures.full

// ─── The shared contract pack, bound through the docx container ──────

let private contractTests =
    IReportRendererContract.testsWithBody "DocxReportRenderer" DocxReportRenderer.create Docx buildDocx extractText

// ─── Format-specific fixtures ────────────────────────────────────────

let private fixtureTests =
    testList "DocxReportRenderer — docx fixtures" [
        testCase "Token split across identically-formatted runs re-joins and substitutes"
        <| fun () ->
            let body =
                buildDocxOf [
                    Paragraph(ParagraphModel.create [ Run.plain "Hello {{na"; Run.plain "me}}!" ])
                ]

            let bytes =
                render (mkTemplate body [ textSchema "name" ]) (Map [ "name", TextValue "Alice" ])
                |> expectOk

            let text = extractText bytes
            Expect.stringContains text "Hello Alice!" "split token substituted"

        testCase "Token split across a formatting boundary is left as authored"
        <| fun () ->
            let bold = {
                Run.plain "{{na" with
                    Formatting = { RunFormatting.none with Bold = true }
            }

            let body =
                buildDocxOf [ Paragraph(ParagraphModel.create [ bold; Run.plain "me}}" ]) ]

            let bytes =
                render (mkTemplate body [ textSchema "name" ]) (Map [ "name", TextValue "Alice" ])
                |> expectOk

            let text = extractText bytes
            Expect.stringContains text "{{na" "token across a formatting boundary not substituted"

        testCase "Run formatting survives substitution"
        <| fun () ->
            let bold = {
                Run.plain "Total: " with
                    Formatting = { RunFormatting.none with Bold = true }
            }

            let body =
                buildDocxOf [ Paragraph(ParagraphModel.create [ bold; Run.plain "{{n}}" ]) ]

            let schema = {
                Key = "n"
                DisplayName = "N"
                Kind = Number(Some "F0")
                Required = true
            }

            let bytes =
                render (mkTemplate body [ schema ]) (Map [ "n", NumberValue 42.0 ]) |> expectOk

            match importBlocks bytes with
            | [ Paragraph p ] ->
                match p.Runs with
                | first :: _ -> Expect.isTrue first.Formatting.Bold "leading run still bold"
                | [] -> failtest "paragraph lost its runs"

                Expect.stringContains (ParagraphModel.text p) "Total: 42" "value substituted beside bold run"
            | blocks -> failtestf "expected one paragraph, got %A" (List.length blocks)

        testCase "Whole-paragraph Table token renders a native table"
        <| fun () ->
            let body =
                buildDocxOf [ Paragraph(ParagraphModel.create [ Run.plain "{{items}}" ]) ]

            let columns = [
                {
                    Key = "item"
                    DisplayName = "Item"
                    Kind = Text
                }
                {
                    Key = "qty"
                    DisplayName = "Qty"
                    Kind = Number None
                }
            ]

            let schema = {
                Key = "items"
                DisplayName = "Items"
                Kind = Table columns
                Required = true
            }

            let rows =
                TableValue [
                    Map [ "item", TextValue "Widget"; "qty", NumberValue 3.0 ]
                    Map [ "item", TextValue "Gadget"; "qty", NumberValue 5.0 ]
                ]

            let bytes = render (mkTemplate body [ schema ]) (Map [ "items", rows ]) |> expectOk

            match importBlocks bytes with
            | [ Block.Table table ] ->
                Expect.equal (List.length table.Rows) 3 "header + two data rows"

                let headerText =
                    Block.text (
                        Block.Table {
                            table with
                                Rows = [ table.Rows.Head ]
                        }
                    )

                Expect.stringContains headerText "Item" "header row carries column display names"
                let allText = Block.text (Block.Table table)
                Expect.stringContains allText "Widget" "first data row present"
                Expect.stringContains allText "Gadget" "second data row present"
            | blocks -> failtestf "expected one table block, got %A" blocks

        testCase "Heading blocks keep their level through substitution"
        <| fun () ->
            let body =
                buildDocxOf [ Heading(2, ParagraphModel.create [ Run.plain "Report for {{name}}" ]) ]

            let bytes =
                render (mkTemplate body [ textSchema "name" ]) (Map [ "name", TextValue "Q3" ])
                |> expectOk

            match importBlocks bytes with
            | [ Heading(2, p) ] -> Expect.stringContains (ParagraphModel.text p) "Report for Q3" "heading substituted"
            | blocks -> failtestf "expected one level-2 heading, got %A" blocks

        testCase "A body that is not a .docx surfaces RendererFailure"
        <| fun () ->
            let template =
                mkTemplate (Encoding.UTF8.GetBytes "not a docx") [ textSchema "name" ]

            match render template (Map [ "name", TextValue "x" ]) with
            | Error(RendererFailure(renderer, _)) ->
                Expect.equal renderer "DocxReportRenderer" "failure names the renderer"
            | Error e -> failtestf "expected RendererFailure, got %A" e
            | Ok _ -> failtest "expected Error for a non-docx body"
    ]

// ─── Phase 575 — NarrativeDocument → OOXML projection ────────────────

let private countBlocks (predicate: Block -> bool) (blocks: Block list) =
    blocks |> List.filter predicate |> List.length

let private isTable =
    function
    | Block.Table _ -> true
    | _ -> false

let private listNumberingIds (blocks: Block list) =
    blocks
    |> List.choose (function
        | Block.ListItem(numbering, _) -> Some numbering.NumberingId
        | _ -> None)

/// The narrative-bearing template: one paragraph that is nothing but the
/// `{{body}}` token, so the narrative expands at that anchor.
let private narrativeTemplate () =
    mkTemplate (buildDocxOf [ Paragraph(ParagraphModel.create [ Run.plain "{{body}}" ]) ]) [ textSchema "body" ]

let private projectionTests =
    testList "NarrativeOoxml — projection" [
        testCase "The document title and section headings become styled heading paragraphs"
        <| fun () ->
            let blocks = projectFull ()

            match blocks with
            | Block.Heading(1, title) :: _ ->
                Expect.equal (ParagraphModel.text title) "Quarterly Review" "title is the level-1 heading"
            | other -> failtestf "expected a level-1 title heading first, got %A" (List.truncate 1 other)

            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.Heading(2, p) -> ParagraphModel.text p = "Findings"
                     | _ -> false))
                "section heading is level 2"

            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.Heading(3, p) -> ParagraphModel.text p = "A sub-heading"
                     | _ -> false))
                "an element heading clamps into the 3..6 band"

        testCase "Lists project to numbered list items, bullets and ordered on distinct instances"
        <| fun () ->
            let ids = projectFull () |> listNumberingIds
            Expect.equal (List.length ids) 4 "two bullets plus two ordered items"

            Expect.equal
                (ids |> List.distinct |> List.length)
                2
                "bullets and ordered items reference distinct numbering instances"

        testCase "Table, KeyValueGrid, Callout and an unresolved component all project to tables"
        <| fun () ->
            let blocks = projectFull ()

            Expect.isGreaterThanOrEqual
                (countBlocks isTable blocks)
                4
                "KeyValueGrid + Table + Callout + the component's data table"

            let text = blocksText blocks
            Expect.stringContains text "Region" "table header present"
            Expect.stringContains text "North" "table body present"
            Expect.stringContains text "Warning: mind the gap" "callout carries its severity label"

        testCase "Callout renders as a shaded single-cell table — the Word idiom"
        <| fun () ->
            let blocks =
                NarrativeOoxml.project noComponentRenderers NarrativeFixtures.calloutOnly

            match blocks |> List.filter isTable with
            | [ Block.Table table ] ->
                match table.Rows with
                | [ row ] ->
                    match row.Cells with
                    | [ cell ] ->
                        match cell.RawProperties with
                        | Some props -> Expect.stringContains props "w:shd" "the callout cell carries shading"
                        | None -> failtest "callout cell carries no verbatim cell properties"
                    | cells -> failtestf "expected a single cell, got %d" (List.length cells)
                | rows -> failtestf "expected a single row, got %d" (List.length rows)
            | tables -> failtestf "expected exactly one callout table, got %d" (List.length tables)

        testCase "CodeBlock projects to a monospace paragraph"
        <| fun () ->
            let blocks = NarrativeOoxml.project noComponentRenderers NarrativeFixtures.codeOnly

            let codeRun =
                blocks
                |> List.collect (function
                    | Block.Paragraph p -> p.Runs
                    | _ -> [])
                |> List.tryFind (fun run -> run.Text = "let x = 1")

            match codeRun with
            | Some run ->
                match run.Formatting.RawProperties with
                | Some props -> Expect.stringContains props "w:rFonts" "code runs carry a monospace font"
                | None -> failtest "code run carries no verbatim run properties"
            | None -> failtest "code block content missing from the projection"

        testCase "Media, embed and gallery blocks degrade to link and caption text, never dropped"
        <| fun () ->
            let text = projectFull () |> blocksText

            for expected in
                [
                    "a video caption"
                    "https://example.org/v.mp4"
                    "an audio caption"
                    "https://example.org/a.mp3"
                    "gallery image"
                    "gallery caption"
                    "an embed title"
                    "https://example.org/e"
                    "an inline image"
                    "https://example.org/a"
                    "card body"
                    "panel body"
                    "tab body"
                    "quoted prose"
                    "A. Author"
                ] do
                Expect.stringContains text expected $"'{expected}' survives the projection"

        testCase "An unregistered component renders its data-table degradation"
        <| fun () ->
            let document =
                NarrativeFixtures.componentDocument "chart" (Map [ "chart.points", "a=1;b=2" ])

            let blocks = NarrativeOoxml.project noComponentRenderers document
            let text = blocksText blocks
            Expect.stringContains text "[component: chart]" "the unresolved component is named"
            Expect.stringContains text "chart.points" "its props render as a data table"
            Expect.stringContains text "a=1;b=2" "the series data survives"
            Expect.isGreaterThanOrEqual (countBlocks isTable blocks) 1 "the degradation is a native table"

        testCase "A registered SVG component states the figure is not embedded and keeps the data"
        <| fun () ->
            // Until the OOXML figure-emit capability is composed there is
            // no block that can carry the SVG, so the projection says so
            // rather than dropping the component silently.
            let renderers: NarrativeOoxml.ComponentRenderers =
                fun _ _ -> NarrativeOoxml.Svg "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"

            let document =
                NarrativeFixtures.componentDocument "chart" (Map [ "chart.points", "a=1;b=2" ])

            let text = NarrativeOoxml.project renderers document |> blocksText
            Expect.stringContains text "[figure: chart" "the figure is named"
            Expect.stringContains text "not embedded" "the absence is stated honestly"
            Expect.stringContains text "a=1;b=2" "the data degradation still carries the series"

        testCase "A registered raster component names its MIME type in the honest degradation"
        <| fun () ->
            let renderers: NarrativeOoxml.ComponentRenderers =
                fun _ _ -> NarrativeOoxml.Image([| 1uy; 2uy |], "image/png")

            let text =
                NarrativeOoxml.project renderers (NarrativeFixtures.componentDocument "chart" Map.empty)
                |> blocksText

            Expect.stringContains text "image/png" "the raster result names its MIME type"

        testCase "The Phase 564 disclosure door redacts before projection"
        <| fun () ->
            let denied = Map [ "fact-1", "policy-A" ]

            let redacted =
                NarrativeFixtures.factBearing "fact-1" |> NarrativeFixtures.redact denied

            let text = NarrativeOoxml.project noComponentRenderers redacted |> blocksText

            Expect.stringContains
                text
                (NarrativeFixtures.deniedMarker "policy-A")
                "the projection carries the policy-naming marker"

            Expect.isFalse (text.Contains "1.2m") "the denied value never reaches the projection"

        testCase "An undenied fact-bearing metric projects its value unchanged"
        <| fun () ->
            let text =
                NarrativeFixtures.factBearing "fact-1"
                |> NarrativeFixtures.redact Map.empty
                |> NarrativeOoxml.project noComponentRenderers
                |> blocksText

            Expect.stringContains text "1.2m" "an undenied metric is untouched"
    ]

let private narrativePlaceholderTests =
    testList "DocxReportRenderer — narrative placeholders" [
        testCase "A whole-paragraph narrative token reopens as native Word structures"
        <| fun () ->
            let bytes =
                render (narrativeTemplate ()) (Map [ "body", NarrativeValue NarrativeFixtures.full ])
                |> expectOk

            let blocks = importBlocks bytes

            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.Heading(1, _) -> true
                     | _ -> false))
                "the narrative title reopens as a level-1 heading"

            Expect.isTrue
                (blocks
                 |> List.exists (function
                     | Block.Heading(2, _) -> true
                     | _ -> false))
                "the section heading reopens as a level-2 heading"

            Expect.equal (blocks |> listNumberingIds |> List.length) 4 "list items reopen carrying numbering"
            Expect.isGreaterThanOrEqual (countBlocks isTable blocks) 4 "tables reopen as native tables"

            let text = blocksText blocks
            Expect.stringContains text "first bullet" "bullet content survives the round trip"
            Expect.stringContains text "let x = 1" "code content survives the round trip"
            Expect.stringContains text "mind the gap" "callout content survives the round trip"
            Expect.isFalse (text.Contains "{{body}}") "the anchor token is consumed"

        testCase "The minted list numbering is declared in the emitted numbering part"
        <| fun () ->
            let bytes =
                render (narrativeTemplate ()) (Map [ "body", NarrativeValue NarrativeFixtures.full ])
                |> expectOk

            let imported = Import.fromBytes bytes

            let declared =
                imported.Model.Numbering.Instances |> List.map _.NumberingId |> Set.ofList

            let referenced =
                imported.Model.Sections
                |> List.collect _.Blocks
                |> listNumberingIds
                |> Set.ofList

            Expect.isTrue (referenced <> Set.empty) "the render produced list items"

            Expect.isEmpty
                (Set.difference referenced declared |> Set.toList)
                "every referenced numbering instance is declared"

        testCase "The rendered package validates against the WordprocessingML schema"
        <| fun () ->
            // The projection writes verbatim property fragments and, for
            // lists, splices numbering definitions into the numbering part.
            // Schema validation is what proves those are well-formed where
            // a reopen alone would not.
            let bytes =
                render (narrativeTemplate ()) (Map [ "body", NarrativeValue NarrativeFixtures.full ])
                |> expectOk

            use stream = new MemoryStream(bytes)
            use document = WordprocessingDocument.Open(stream, false)

            let errors =
                OpenXmlValidator(FileFormatVersions.Office2019).Validate document
                |> Seq.map (fun error -> $"{error.Path.XPath}: {error.Description}")
                |> Seq.toList

            Expect.isEmpty errors "the emitted package carries no schema-validation errors"

        testCase "A narrative-free render leaves the numbering part untouched"
        <| fun () ->
            let bytes =
                render
                    (mkTemplate (buildDocx "Hello {{name}}") [ textSchema "name" ])
                    (Map [ "name", TextValue "Alice" ])
                |> expectOk

            Expect.isNone (Import.fromBytes bytes).Model.Numbering.RawXml "no numbering part is minted (GP 11)"

        testCase "An inline narrative token takes the plaintext projection"
        <| fun () ->
            let body =
                buildDocxOf [
                    Paragraph(ParagraphModel.create [ Run.plain "Summary: {{body}} — ends here." ])
                ]

            let document = NarrativeFixtures.ofElements [] // title + section heading only

            let bytes =
                render (mkTemplate body [ textSchema "body" ]) (Map [ "body", NarrativeValue document ])
                |> expectOk

            let blocks = importBlocks bytes
            Expect.equal (List.length blocks) 1 "an inline token does not split its host paragraph"
            let text = blocksText blocks
            Expect.stringContains text "Summary: " "the surrounding sentence is preserved"
            Expect.stringContains text "ends here." "the tail of the sentence is preserved"

            Expect.stringContains
                (NarrativeFixtures.plaintext document)
                "Quarterly Review"
                "the plaintext projection carries the document title"

            Expect.stringContains text "Quarterly Review" "the inline degradation is the plaintext projection"

        testCase "A narrative supplied for a Table-kind placeholder is still a kind mismatch"
        <| fun () ->
            let schema = {
                Key = "body"
                DisplayName = "Body"
                Kind = Table []
                Required = true
            }

            match
                render
                    (mkTemplate (buildDocx "{{body}}") [ schema ])
                    (Map [ "body", NarrativeValue NarrativeFixtures.full ])
            with
            | Error(PlaceholderTypeMismatch(key, _, _)) -> Expect.equal key "body" "the mismatch names the placeholder"
            | Error e -> failtestf "expected PlaceholderTypeMismatch, got %A" e
            | Ok _ -> failtest "expected a kind mismatch for a narrative in a Table slot"

        testCase "createWith routes component blocks through the supplied registry"
        <| fun () ->
            let renderers: NarrativeOoxml.ComponentRenderers =
                fun name _ -> NarrativeOoxml.Svg $"<svg data-name=\"{name}\"/>"

            let document =
                NarrativeFixtures.componentDocument "chart" (Map [ "chart.points", "a=1" ])

            let bytes =
                (DocxReportRenderer.createWith renderers)
                    .Render(narrativeTemplate (), Map [ "body", NarrativeValue document ])
                |> Async.RunSynchronously
                |> expectOk

            let text = extractText bytes
            Expect.stringContains text "[figure: chart" "the registered renderer's result reached the projection"
            Expect.isFalse (text.Contains "[component: chart]") "a registered name does not take the unresolved path"
    ]

let tests =
    testList "DocxReportRenderer" [ contractTests; fixtureTests; projectionTests; narrativePlaceholderTests ]