// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Shared fixture builders for the Phase 574 SpreadsheetML emit pack:
/// the canonical mixed-kind workbook every round-trip case reopens,
/// plus the helpers that read an emitted package back through the
/// OpenXml SDK for the facts `ToolUp.Tabular`'s reader does not
/// surface (merged ranges, column widths, number-format codes).
module ToolUp.OpenXml.Spreadsheet.Tests.Fixtures

open System
open System.IO
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet
open ToolUp.OpenXml.Spreadsheet

/// A sheet built from a name the fixtures know is valid. Fixtures
/// asserting on *invalid* names go through `SheetModel.create`
/// directly and read the `Result`.
let sheet (name: string) (rows: RowModel list) : SheetModel =
    match SheetModel.create name rows with
    | Ok built -> built
    | Error error -> failwith (SheetName.describeError name error)

/// The canonical acceptance fixture: a merged header range across
/// three columns, then a row per value kind, each carrying its own
/// number format, plus a column-width declaration.
///
///   A1:C1  "Quarterly Summary"   (merged header)
///   A2..C2 "Metric" / "Value" / "Recorded"
///   A3..C3 "Revenue" / 1234.5 (0.00)   / 2026-03-31 (date)
///   A4..C4 "Units"   / 42 (#,##0)      / 2026-06-30 (date)
///   A5..C5 "Audited" / true            / (blank)
let mixedKindWorkbook () : WorkbookModel =
    let rows = [
        RowModel.ofCells [ CellModel.text "Quarterly Summary"; CellModel.blank; CellModel.blank ]
        RowModel.ofText [ "Metric"; "Value"; "Recorded" ]
        RowModel.ofCells [
            CellModel.text "Revenue"
            CellModel.number 1234.5 |> CellModel.withFormat "0.00"
            CellModel.date (DateTime(2026, 3, 31))
        ]
        RowModel.ofCells [
            CellModel.text "Units"
            CellModel.number 42.0 |> CellModel.withFormat "#,##0"
            CellModel.date (DateTime(2026, 6, 30))
        ]
        RowModel.ofCells [ CellModel.text "Audited"; CellModel.boolean true; CellModel.blank ]
    ]

    sheet "Summary" rows
    |> SheetModel.withMergedRanges [
        {
            FirstRow = 0
            FirstColumn = 0
            LastRow = 0
            LastColumn = 2
        }
    ]
    |> SheetModel.withColumnWidths [ { ColumnIndex = 0; Width = 24.0 }; { ColumnIndex = 2; Width = 14.5 } ]
    |> List.singleton
    |> WorkbookModel.ofSheets

// ─── SDK-level readback ──────────────────────────────────────────
//
// `ToolUp.Tabular` reads the grid — values, and the date-vs-number
// classification that proves a date format survived. It does not
// surface merged ranges, column widths, or the format codes
// themselves, so those are read back through the SDK here.

/// Apply `read` to the named sheet's `Worksheet` in an emitted
/// package.
let withWorksheet (bytes: byte[]) (sheetName: string) (read: Worksheet -> 'a) : 'a =
    use stream = new MemoryStream(bytes)
    use document = Package.openRead stream
    let workbookPart = Package.workbookPart document

    let sheet =
        workbookPart.Workbook.Sheets.Elements<Sheet>()
        |> Seq.find (fun s -> s.Name.Value = sheetName)

    let worksheetPart = workbookPart.GetPartById sheet.Id.Value :?> WorksheetPart
    read worksheetPart.Worksheet

/// The `mergeCell` references on a sheet, in emitted order.
let mergedReferences (bytes: byte[]) (sheetName: string) : string list =
    withWorksheet bytes sheetName (fun worksheet ->
        worksheet.Elements<MergeCells>()
        |> Seq.collect _.Elements<MergeCell>()
        |> Seq.map _.Reference.Value
        |> List.ofSeq)

/// The `(min, max, width)` column declarations on a sheet.
let columnDeclarations (bytes: byte[]) (sheetName: string) : (uint32 * uint32 * float) list =
    withWorksheet bytes sheetName (fun worksheet ->
        worksheet.Elements<Columns>()
        |> Seq.collect _.Elements<Column>()
        |> Seq.map (fun column -> column.Min.Value, column.Max.Value, column.Width.Value)
        |> List.ofSeq)

/// Every custom number-format code in the workbook's styles part,
/// keyed by its allocated `numFmtId`.
let numberFormatCodes (bytes: byte[]) : (uint32 * string) list =
    use stream = new MemoryStream(bytes)
    use document = Package.openRead stream
    let workbookPart = Package.workbookPart document

    match workbookPart.WorkbookStylesPart with
    | null -> []
    | stylesPart ->
        match stylesPart.Stylesheet.NumberingFormats with
        | null -> []
        | formats ->
            formats.Elements<NumberingFormat>()
            |> Seq.map (fun format -> format.NumberFormatId.Value, format.FormatCode.Value)
            |> List.ofSeq

/// The workbook's sheet names, in tab order.
let sheetNames (bytes: byte[]) : string list =
    use stream = new MemoryStream(bytes)
    use document = Package.openRead stream

    (Package.workbookPart document).Workbook.Sheets.Elements<Sheet>()
    |> Seq.map _.Name.Value
    |> List.ofSeq

/// Distinct shared-string values, in emitted order.
let sharedStrings (bytes: byte[]) : string list =
    use stream = new MemoryStream(bytes)
    use document = Package.openRead stream
    let workbookPart = Package.workbookPart document

    match workbookPart.SharedStringTablePart with
    | null -> []
    | part ->
        match part.SharedStringTable with
        | null -> []
        | table -> table.Elements<SharedStringItem>() |> Seq.map _.InnerText |> List.ofSeq