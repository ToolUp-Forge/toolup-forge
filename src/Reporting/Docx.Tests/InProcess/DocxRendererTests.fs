// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Docx.Tests.DocxRendererTests

open System.Text
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

let tests = testList "DocxReportRenderer" [ contractTests; fixtureTests ]