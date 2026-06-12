// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.Tests.InProcess.RoundTripTests

open Expecto
open ToolUp.OpenXml
open ToolUp.OpenXml.Tests

let private reimport (model: DocModel) : ImportedDocument = Import.fromBytes (Emit.toBytes model)

let tests =
    testList "RoundTrip" [
        testCase "rich fixture: import → emit → import preserves the captured structure"
        <| fun () ->
            let first = Import.fromBytes (Fixtures.buildRichDocx ())
            let second = reimport first.Model

            Expect.equal
                (ImportTests.renderModel second.Model)
                (ImportTests.renderModel first.Model)
                "structural projections equal"

            Expect.isTrue
                (ResidueReport.isEmpty second.Residue)
                (sprintf "re-imported document has no residue, got %A" second.Residue)

        testCase "rich fixture: emitted document validates against the OOXML schema"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())
            let errors = Fixtures.validate (Emit.toBytes imported.Model)

            Expect.isEmpty (errors |> List.map (fun e -> sprintf "%s: %s" e.Id e.Description)) "no validation errors"

        testCase "programmatic model: emitted document validates and round-trips"
        <| fun () ->
            let model =
                DocModel.ofBlocks [
                    Heading(
                        1,
                        {
                            ParagraphModel.create [ Run.plain "Title" ] with
                                StyleId = Some "Heading1"
                        }
                    )
                    Paragraph(
                        ParagraphModel.create [
                            Run.plain "Plain and "
                            {
                                Run.plain "bold" with
                                    Formatting = { RunFormatting.none with Bold = true }
                            }
                            Run.plain " text."
                        ]
                    )
                    ListItem({ NumberingId = 1; Level = 0 }, ParagraphModel.create [ Run.plain "Item one" ])
                    ListItem({ NumberingId = 1; Level = 0 }, ParagraphModel.create [ Run.plain "Item two" ])
                    Table {
                        Rows = [
                            {
                                Cells = [
                                    {
                                        Blocks = [ Paragraph(ParagraphModel.create [ Run.plain "A" ]) ]
                                        RawProperties = None
                                    }
                                    {
                                        Blocks = [ Paragraph(ParagraphModel.create [ Run.plain "B" ]) ]
                                        RawProperties = None
                                    }
                                ]
                                RawProperties = None
                            }
                        ]
                        RawProperties = None
                        RawGrid = None
                    }
                ]

            let errors = Fixtures.validate (Emit.toBytes model)

            Expect.isEmpty (errors |> List.map (fun e -> sprintf "%s: %s" e.Id e.Description)) "no validation errors"

            let imported = reimport model
            let blocks = imported.Model.Sections.Head.Blocks

            match blocks[0] with
            | Heading(1, p) -> Expect.equal (ParagraphModel.text p) "Title" "heading survives"
            | other -> failtestf "expected Heading, got %A" other

            match blocks[2] with
            | ListItem(numbering, _) -> Expect.equal numbering.NumberingId 1 "numbering reference survives"
            | other -> failtestf "expected ListItem, got %A" other

            Expect.isTrue
                (imported.Model.Styles.Styles |> List.exists (fun s -> s.StyleId = "Heading1"))
                "generated styles part declares the heading style"

            Expect.isTrue
                (imported.Model.Numbering.Instances |> List.exists (fun n -> n.NumberingId = 1))
                "generated numbering part declares the instance"

        testCase "opaque-carried content survives emission verbatim"
        <| fun () ->
            let first = Import.fromBytes (Fixtures.buildExoticDocx ())
            let second = reimport first.Model
            let blocks = second.Model.Sections.Head.Blocks

            match blocks[1] with
            | OpaqueBlock xml -> Expect.stringContains xml "inside control" "sdt content re-emitted"
            | other -> failtestf "expected OpaqueBlock after round trip, got %A" other

            // The re-import sees the same uncapturable element again —
            // dropped inline content from the first import is gone.
            Expect.equal
                (second.Residue.Entries |> List.map _.ElementKind)
                [ "w:sdt" ]
                "only the carried element resurfaces"

            match blocks[2] with
            | Paragraph p -> Expect.equal (ParagraphModel.text p) "link text" "flattened text retained"
            | other -> failtestf "expected paragraph, got %A" other

        testCase "multi-section models round-trip their section count"
        <| fun () ->
            let model = {
                DocModel.empty with
                    Sections = [
                        Section.create [ Paragraph(ParagraphModel.create [ Run.plain "First section" ]) ]
                        Section.create [ Paragraph(ParagraphModel.create [ Run.plain "Second section" ]) ]
                    ]
            }

            let imported = reimport model

            Expect.equal imported.Model.Sections.Length 2 "two sections"

            Expect.equal
                (imported.Model.Sections
                 |> List.map (fun s -> s.Blocks |> List.map Block.text |> String.concat "|"))
                [ "First section"; "Second section" ]
                "per-section content intact"
    ]