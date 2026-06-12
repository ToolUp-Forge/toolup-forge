// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.Fixtures

open System.IO
open System.Text
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet
open ToolUp.Tabular

// ─── Shared schema + golden fixtures for the test packs ─────────

/// The canonical six-column import schema the golden fixtures
/// bind against — one column per `ColumnType` case.
let productSchema =
    TableSchema.make [
        ColumnSchema.make "Sku" ColumnType.Text
        |> ColumnSchema.required
        |> ColumnSchema.withConstraints {
            ColumnConstraints.none with
                Pattern = Some "[A-Z]{2}-[0-9]{4}"
                MaxLength = Some 16
        }
        ColumnSchema.make "Name" ColumnType.Text |> ColumnSchema.required
        ColumnSchema.make "Price" ColumnType.Number
        |> ColumnSchema.required
        |> ColumnSchema.withConstraints {
            ColumnConstraints.none with
                MinValue = Some 0m
                MaxValue = Some 10000m
        }
        ColumnSchema.make "Stock" ColumnType.Integer
        |> ColumnSchema.withConstraints {
            ColumnConstraints.none with
                MinValue = Some 0m
        }
        ColumnSchema.make "Launched" ColumnType.Date
        ColumnSchema.make "Status" (ColumnType.Choice [ "Active"; "Discontinued"; "Preorder" ])
    ]

/// UTF-8 (no BOM) bytes for a CSV body.
let csvBytes (text: string) : byte[] = Encoding.UTF8.GetBytes text

let csvStream (text: string) : MemoryStream = new MemoryStream(csvBytes text)

/// A clean three-row file against `productSchema`.
let cleanCsv =
    "Sku,Name,Price,Stock,Launched,Status\n"
    + "AB-0001,Widget,9.99,120,2024-03-01,Active\n"
    + "CD-0002,\"Gadget, large\",1250.50,4,2023-11-15,Preorder\n"
    + "EF-0003,Sprocket,0.45,,,Discontinued\n"

/// Mixed-validity: row 2 clean; row 3 has a bad price + bad
/// status; row 4 has a bad SKU pattern + bad stock; row 5 clean.
let mixedCsv =
    "Sku,Name,Price,Stock,Launched,Status\n"
    + "AB-0001,Widget,9.99,120,2024-03-01,Active\n"
    + "CD-0002,Gadget,not-a-price,7,2023-11-15,Unknown\n"
    + "bad sku,Sprocket,1.00,many,2022-01-01,Active\n"
    + "GH-0004,Flange,12.00,3,2024-06-30,Preorder\n"

// ─── In-memory XLSX builder ─────────────────────────────────────

/// Cell spec for the workbook builder.
type Fx =
    /// Shared-string cell.
    | S of string
    /// Inline-string cell.
    | I of string
    /// Number cell, general style.
    | N of float
    /// Number cell carrying a date style (numFmtId 14).
    | D of float
    /// Boolean cell.
    | B of bool
    /// No cell at this position (sparse-row gap).
    | Gap

let private columnReference (index: int) =
    let mutable i = index + 1
    let sb = StringBuilder()

    while i > 0 do
        let rem = (i - 1) % 26
        sb.Insert(0, char (int 'A' + rem)) |> ignore
        i <- (i - 1) / 26

    sb.ToString()

let private buildWorkbook (stream: MemoryStream) (sheetName: string) (rows: Fx list list) : unit =
    use document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook)
    let workbookPart = document.AddWorkbookPart()
    workbookPart.Workbook <- Workbook()

    // Stylesheet: format 0 = general, format 1 = date (14).
    // Built imperatively — OpenXmlElement implements
    // IEnumerable<OpenXmlElement>, so a single-child constructor
    // call resolves to the enumerable overload and tries to
    // re-parent the child's children ("part of a tree" error).
    let stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>()
    let cellFormats = CellFormats()
    cellFormats.AppendChild(CellFormat(NumberFormatId = UInt32Value 0u)) |> ignore

    cellFormats.AppendChild(CellFormat(NumberFormatId = UInt32Value 14u, ApplyNumberFormat = BooleanValue true))
    |> ignore

    let stylesheet = Stylesheet()
    stylesheet.AppendChild cellFormats |> ignore
    stylesPart.Stylesheet <- stylesheet

    // Pool the shared strings.
    let sharedStrings =
        rows
        |> List.collect id
        |> List.choose (function
            | S text -> Some text
            | _ -> None)
        |> List.distinct

    let sharedIndex = sharedStrings |> List.mapi (fun i text -> text, i) |> dict

    if not (List.isEmpty sharedStrings) then
        let sharedPart = workbookPart.AddNewPart<SharedStringTablePart>()
        let table = SharedStringTable()

        for text in sharedStrings do
            let item = SharedStringItem()
            item.Text <- Text(text)
            table.AppendChild item |> ignore

        sharedPart.SharedStringTable <- table

    let worksheetPart = workbookPart.AddNewPart<WorksheetPart>()
    let sheetData = SheetData()

    rows
    |> List.iteri (fun rowIndex cells ->
        let row = Row(RowIndex = UInt32Value(uint32 (rowIndex + 1)))

        cells
        |> List.iteri (fun columnIndex spec ->
            let reference = sprintf "%s%d" (columnReference columnIndex) (rowIndex + 1)

            let cell =
                match spec with
                | S text ->
                    Some(
                        Cell(
                            CellReference = StringValue reference,
                            DataType = EnumValue CellValues.SharedString,
                            CellValue = CellValue(string sharedIndex[text])
                        )
                    )
                | I text ->
                    let inlineString = InlineString()
                    inlineString.Text <- Text(text)

                    Some(
                        Cell(
                            CellReference = StringValue reference,
                            DataType = EnumValue CellValues.InlineString,
                            InlineString = inlineString
                        )
                    )
                | N value ->
                    Some(
                        Cell(
                            CellReference = StringValue reference,
                            CellValue = CellValue(value.ToString System.Globalization.CultureInfo.InvariantCulture)
                        )
                    )
                | D value ->
                    Some(
                        Cell(
                            CellReference = StringValue reference,
                            StyleIndex = UInt32Value 1u,
                            CellValue = CellValue(value.ToString System.Globalization.CultureInfo.InvariantCulture)
                        )
                    )
                | B value ->
                    Some(
                        Cell(
                            CellReference = StringValue reference,
                            DataType = EnumValue CellValues.Boolean,
                            CellValue = CellValue(if value then "1" else "0")
                        )
                    )
                | Gap -> None

            cell |> Option.iter (row.AppendChild >> ignore))

        sheetData.AppendChild row |> ignore)

    let worksheet = Worksheet()
    worksheet.AppendChild sheetData |> ignore
    worksheetPart.Worksheet <- worksheet

    let sheets = workbookPart.Workbook.AppendChild(Sheets())

    sheets.AppendChild(
        Sheet(
            Id = StringValue(workbookPart.GetIdOfPart worksheetPart),
            SheetId = UInt32Value 1u,
            Name = StringValue sheetName
        )
    )
    |> ignore

    workbookPart.Workbook.Save()

/// Build a single-sheet workbook (named `sheetName`) from cell
/// specs, returning the file bytes. Shared strings are pooled
/// into a real `SharedStringTablePart`; date cells reference a
/// stylesheet cell format with the built-in date numFmtId 14.
let xlsxBytes (sheetName: string) (rows: Fx list list) : byte[] =
    use stream = new MemoryStream()
    buildWorkbook stream sheetName rows
    stream.ToArray()

/// Excel serial for 2021-01-01 (1900 date system).
let serial20210101 = 44197.0

/// The `productSchema` header row as builder specs.
let productHeader = [ S "Sku"; S "Name"; S "Price"; S "Stock"; S "Launched"; S "Status" ]