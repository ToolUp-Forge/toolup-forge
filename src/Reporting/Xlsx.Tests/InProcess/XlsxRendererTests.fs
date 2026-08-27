// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Xlsx.Tests.XlsxRendererTests

open System
open System.Globalization
open System.IO
open System.Text
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet
open Expecto
open ToolUp.Reporting
open ToolUp.Reporting.Xlsx
open ToolUp.Platform.Tests.Contracts

// ─── Fixture builders over the OpenXml SDK ───────────────────────────

/// Build an InlineString / SharedStringItem via explicit AppendChild —
/// passing the Text element to the constructor can bind the
/// IEnumerable overload and enumerate its children instead.
let private inlineStringOf (text: string) : InlineString =
    let inlineString = InlineString()

    inlineString.AppendChild(Spreadsheet.Text(Text = text, Space = SpaceProcessingModeValues.Preserve))
    |> ignore

    inlineString

let private sharedStringItemOf (text: string) : SharedStringItem =
    let item = SharedStringItem()

    item.AppendChild(Spreadsheet.Text(Text = text, Space = SpaceProcessingModeValues.Preserve))
    |> ignore

    item

/// One fixture cell: A1-style reference, its 1-based row, and content.
type private FixtureCell =
    | InlineText of reference: string * row: uint32 * text: string
    | SharedText of reference: string * row: uint32 * sstIndex: int
    | StyledInline of reference: string * row: uint32 * text: string * styleIndex: uint32
    | Formula of reference: string * row: uint32 * formula: string

let private buildWorkbook (sharedStrings: string list) (sheets: (string * FixtureCell list) list) : byte[] =
    use ms = new MemoryStream()

    do
        use doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook)
        let workbookPart = doc.AddWorkbookPart()
        workbookPart.Workbook <- Workbook()

        if not (List.isEmpty sharedStrings) then
            let sstPart = workbookPart.AddNewPart<SharedStringTablePart>()
            sstPart.SharedStringTable <- SharedStringTable()

            for value in sharedStrings do
                sstPart.SharedStringTable.AppendChild(sharedStringItemOf value) |> ignore

            sstPart.SharedStringTable.Save()

        let sheetsElement = workbookPart.Workbook.AppendChild(Sheets())
        let mutable sheetId = 1u

        for sheetName, cells in sheets do
            let worksheetPart = workbookPart.AddNewPart<WorksheetPart>()
            let sheetData = SheetData()

            let byRow =
                cells
                |> List.groupBy (fun c ->
                    match c with
                    | InlineText(_, row, _)
                    | SharedText(_, row, _)
                    | StyledInline(_, row, _, _)
                    | Formula(_, row, _) -> row)

            for rowIndex, rowCells in byRow |> List.sortBy fst do
                let row = Row(RowIndex = UInt32Value rowIndex)

                for cell in rowCells do
                    let element =
                        match cell with
                        | InlineText(reference, _, text) ->
                            Cell(
                                CellReference = StringValue reference,
                                DataType = EnumValue CellValues.InlineString,
                                InlineString = inlineStringOf text
                            )
                        | SharedText(reference, _, index) ->
                            Cell(
                                CellReference = StringValue reference,
                                DataType = EnumValue CellValues.SharedString,
                                CellValue = CellValue(string index)
                            )
                        | StyledInline(reference, _, text, styleIndex) ->
                            Cell(
                                CellReference = StringValue reference,
                                DataType = EnumValue CellValues.InlineString,
                                StyleIndex = UInt32Value styleIndex,
                                InlineString = inlineStringOf text
                            )
                        | Formula(reference, _, formula) ->
                            Cell(CellReference = StringValue reference, CellFormula = CellFormula(Text = formula))

                    row.AppendChild element |> ignore

                sheetData.AppendChild row |> ignore

            let worksheet = Worksheet()
            worksheet.AppendChild sheetData |> ignore
            worksheetPart.Worksheet <- worksheet

            sheetsElement.AppendChild(
                Sheet(
                    Name = StringValue sheetName,
                    SheetId = UInt32Value sheetId,
                    Id = StringValue(workbookPart.GetIdOfPart worksheetPart)
                )
            )
            |> ignore

            sheetId <- sheetId + 1u

        workbookPart.Workbook.Save()

    ms.ToArray()

/// The contract pack's body builder: the fixture body text in cell A1
/// of a single-sheet workbook.
let private buildXlsx (text: string) : byte[] =
    buildWorkbook [] [ "Sheet1", [ InlineText("A1", 1u, text) ] ]

let private openRead (bytes: byte[]) =
    SpreadsheetDocument.Open(new MemoryStream(bytes), false)

let private resolveCellText (workbookPart: WorkbookPart) (cell: Cell) : string option =
    let dataType =
        Option.ofObj (box cell.DataType) |> Option.map (fun _ -> cell.DataType.Value)

    match dataType with
    | Some t when t = CellValues.SharedString ->
        match cell.CellValue with
        | null -> None
        | v ->
            match workbookPart.SharedStringTablePart with
            | null -> None
            | sst ->
                sst.SharedStringTable.Elements<SharedStringItem>()
                |> Seq.tryItem (Int32.Parse(v.Text, CultureInfo.InvariantCulture))
                |> Option.map _.InnerText
    | Some t when t = CellValues.InlineString -> Option.ofObj cell.InlineString |> Option.map _.InnerText
    | _ -> Option.ofObj cell.CellValue |> Option.map _.Text

/// Text projection for the contract pack: every cell's resolved text,
/// joined.
let private extractText (bytes: byte[]) : string =
    use doc = openRead bytes
    let workbookPart = doc.WorkbookPart

    [
        for worksheetPart in workbookPart.WorksheetParts do
            for cell in worksheetPart.Worksheet.Descendants<Cell>() do
                match resolveCellText workbookPart cell with
                | Some text -> yield text
                | None -> ()
    ]
    |> String.concat " "

let private findCell (bytes: byte[]) (sheetName: string) (reference: string) : (Cell * WorkbookPart) option =
    let doc = openRead bytes
    let workbookPart = doc.WorkbookPart

    workbookPart.Workbook.Descendants<Sheet>()
    |> Seq.tryFind (fun s -> s.Name.Value = sheetName)
    |> Option.bind (fun sheet ->
        let worksheetPart = workbookPart.GetPartById sheet.Id.Value :?> WorksheetPart

        worksheetPart.Worksheet.Descendants<Cell>()
        |> Seq.tryFind (fun c ->
            not (isNull (box c.CellReference))
            && String.Equals(c.CellReference.Value, reference, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun c -> c, workbookPart))

let private mkTemplate (body: byte[]) (placeholders: PlaceholderSchema list) : ReportTemplate = {
    Id = "fixture-template"
    DisplayName = "Fixture"
    Format = Xlsx
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
    (XlsxReportRenderer.create ()).Render(template, values)
    |> Async.RunSynchronously

let private expectOk result =
    match result with
    | Ok(bytes: byte[]) -> bytes
    | Error e -> failtestf "expected Ok, got %s" (RenderError.toMessage e)

// ─── CellAddress parsing ─────────────────────────────────────────────

let private cellAddressTests =
    testList "CellAddress.tryParse" [
        testCase "Plain sheet-qualified reference parses"
        <| fun () ->
            match CellAddress.tryParse "Sheet1!B7" with
            | Some parsed ->
                Expect.equal parsed.Sheet "Sheet1" "sheet"
                Expect.equal parsed.Cell "B7" "cell"
                Expect.equal parsed.Column "B" "column"
                Expect.equal parsed.RowIndex 7u "row"
            | None -> failtest "expected a parse"

        testCase "Quoted sheet name with embedded quote parses"
        <| fun () ->
            match CellAddress.tryParse "'Bob''s Sheet'!AA10" with
            | Some parsed ->
                Expect.equal parsed.Sheet "Bob's Sheet" "unquoted sheet name"
                Expect.equal parsed.Cell "AA10" "cell"
            | None -> failtest "expected a parse"

        testCase "Keys that merely contain a bang stay ordinary tokens"
        <| fun () ->
            Expect.isNone (CellAddress.tryParse "urgent!flag") "not an A1 reference"
            Expect.isNone (CellAddress.tryParse "Sheet1!") "empty cell part"
            Expect.isNone (CellAddress.tryParse "!B7") "empty sheet part"
            Expect.isNone (CellAddress.tryParse "Sheet1!B") "no row digits"
            Expect.isNone (CellAddress.tryParse "plain_key") "no bang at all"

        testCase "Column letters convert to 1-based numbers"
        <| fun () ->
            Expect.equal (CellAddress.columnNumber "A") 1 "A"
            Expect.equal (CellAddress.columnNumber "Z") 26 "Z"
            Expect.equal (CellAddress.columnNumber "AA") 27 "AA"
    ]

// ─── The shared contract pack, bound through the xlsx container ──────

let private contractTests =
    IReportRendererContract.testsWithBody "XlsxReportRenderer" XlsxReportRenderer.create Xlsx buildXlsx extractText

// ─── Format-specific fixtures ────────────────────────────────────────

let private fixtureTests =
    testList "XlsxReportRenderer — xlsx fixtures" [
        testCase "Cell-address write preserves the target cell's style index"
        <| fun () ->
            let body = buildWorkbook [] [ "Sheet1", [ StyledInline("B2", 2u, "old", 5u) ] ]

            let bytes =
                render (mkTemplate body []) (Map.ofList [ "Sheet1!B2", NumberValue 42.0 ])
                |> expectOk

            match findCell bytes "Sheet1" "B2" with
            | Some(cell, _) ->
                Expect.isFalse (isNull (box cell.StyleIndex)) "style index still present"
                Expect.equal cell.StyleIndex.Value 5u "style index untouched"
                Expect.isTrue (isNull (box cell.DataType)) "numeric cell has no string data type"
                Expect.equal cell.CellValue.Text "42" "numeric value written"
            | None -> failtest "cell B2 not found"

        testCase "Cell-address write creates a missing cell in row/column order"
        <| fun () ->
            let body = buildWorkbook [] [ "Sheet1", [ InlineText("A1", 1u, "anchor") ] ]

            let bytes =
                render (mkTemplate body []) (Map.ofList [ "Sheet1!C3", TextValue "created" ])
                |> expectOk

            match findCell bytes "Sheet1" "C3" with
            | Some(cell, workbookPart) ->
                Expect.equal (resolveCellText workbookPart cell) (Some "created") "value written into new cell"
            | None -> failtest "cell C3 was not created"

        testCase "Quoted sheet names resolve for cell writes"
        <| fun () ->
            let body = buildWorkbook [] [ "My Sheet", [ InlineText("A1", 1u, "anchor") ] ]

            let bytes =
                render (mkTemplate body []) (Map.ofList [ "'My Sheet'!B2", TextValue "hello" ])
                |> expectOk

            match findCell bytes "My Sheet" "B2" with
            | Some(cell, workbookPart) ->
                Expect.equal (resolveCellText workbookPart cell) (Some "hello") "value written"
            | None -> failtest "cell B2 not found on the quoted sheet"

        testCase "Cell write naming an absent sheet fails the render, naming the sheet"
        <| fun () ->
            let body = buildWorkbook [] [ "Sheet1", [ InlineText("A1", 1u, "anchor") ] ]

            match render (mkTemplate body []) (Map.ofList [ "Nope!A1", TextValue "x" ]) with
            | Error(RendererFailure(_, reason)) -> Expect.stringContains reason "Nope" "failure names the sheet"
            | Error e -> failtestf "expected RendererFailure, got %A" e
            | Ok _ -> failtest "expected Error for an absent sheet"

        testCase "Whole-cell unhinted Number token becomes a native numeric cell"
        <| fun () ->
            let body = buildWorkbook [] [ "Sheet1", [ InlineText("A1", 1u, "{{n}}") ] ]

            let schema = {
                Key = "n"
                DisplayName = "N"
                Kind = Number None
                Required = true
            }

            let bytes =
                render (mkTemplate body [ schema ]) (Map.ofList [ "n", NumberValue 42.5 ])
                |> expectOk

            match findCell bytes "Sheet1" "A1" with
            | Some(cell, _) ->
                Expect.isTrue (isNull (box cell.DataType)) "no string data type"
                Expect.equal cell.CellValue.Text "42.5" "raw numeric value"
            | None -> failtest "cell A1 not found"

        testCase "Shared-string tokens substitute"
        <| fun () ->
            let body =
                buildWorkbook [ "Hello {{name}}" ] [ "Sheet1", [ SharedText("A1", 1u, 0) ] ]

            let bytes =
                render (mkTemplate body [ textSchema "name" ]) (Map.ofList [ "name", TextValue "World" ])
                |> expectOk

            Expect.stringContains (extractText bytes) "Hello World" "substituted through the shared string"

        testCase "Formula cells are untouched and the workbook is marked for recalculation"
        <| fun () ->
            let body =
                buildWorkbook [] [ "Sheet1", [ InlineText("A1", 1u, "{{n}}"); Formula("B1", 1u, "A1*2") ] ]

            let schema = {
                Key = "n"
                DisplayName = "N"
                Kind = Number None
                Required = true
            }

            let bytes =
                render (mkTemplate body [ schema ]) (Map.ofList [ "n", NumberValue 21.0 ])
                |> expectOk

            match findCell bytes "Sheet1" "B1" with
            | Some(cell, workbookPart) ->
                Expect.isFalse (isNull (box cell.CellFormula)) "formula survives"
                Expect.equal cell.CellFormula.Text "A1*2" "formula text unchanged"

                let calcProps = workbookPart.Workbook.CalculationProperties
                Expect.isFalse (isNull (box calcProps)) "calculation properties present"
                Expect.isTrue calcProps.FullCalculationOnLoad.Value "full recalculation on load"
            | None -> failtest "formula cell B1 not found"

        testCase "A body that is not an .xlsx surfaces RendererFailure"
        <| fun () ->
            let template =
                mkTemplate (Encoding.UTF8.GetBytes "not an xlsx") [ textSchema "name" ]

            match render template (Map.ofList [ "name", TextValue "x" ]) with
            | Error(RendererFailure(renderer, _)) ->
                Expect.equal renderer "XlsxReportRenderer" "failure names the renderer"
            | Error e -> failtestf "expected RendererFailure, got %A" e
            | Ok _ -> failtest "expected Error for a non-xlsx body"
    ]

let tests =
    testList "XlsxReportRenderer" [ cellAddressTests; contractTests; fixtureTests ]