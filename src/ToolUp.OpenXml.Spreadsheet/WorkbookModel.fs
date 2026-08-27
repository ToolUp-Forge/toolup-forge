// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.OpenXml.Spreadsheet

open System
open System.Text

// ─── Phase 574 — structural workbook model ───────────────────────
//
// Immutable value records describing a SpreadsheetML workbook at the
// altitude programmatic authoring needs: workbook → sheets → rows →
// cells, plus the presentation facts a grid carries that the values
// alone do not (per-cell number-format codes, column widths, merged
// ranges). Identity by value throughout — no live OpenXml SDK handle
// ever leaks through the model, so a model can be built, compared,
// serialised and diffed without the vendor SDK in scope.
//
// The read-side counterpart is `ToolUp.Tabular`'s `Xlsx` module,
// which streams rows out of an existing workbook. This package is the
// write side: it has no reader, and `ToolUp.Tabular` has no writer.
//
// Sheet names are the one place the model can be handed something
// Excel will refuse, so naming is validated rather than repaired:
// `SheetName.validate` returns a `Result` naming exactly what is
// wrong. Nothing here ever truncates a 40-character name to 31 or
// silently strips a `/` — a name the caller chose that the format
// cannot carry is the caller's decision to make.

/// What a cell carries. Dates are stored the way Excel itself stores
/// them — a numeric serial under a date-shaped number format — so a
/// `Date` cell is a `Number` cell whose presentation makes it a date.
/// Emission supplies `SpreadsheetDefaults.dateFormat` when a `Date`
/// cell declares no format of its own.
[<RequireQualifiedAccess>]
type CellContent =
    /// A string cell. Emission pools these into the shared-string
    /// table, so a value repeated down a column costs one entry.
    | Text of string
    | Number of value: float
    | Boolean of value: bool
    /// A date / date-time, emitted as an OLE-automation serial under a
    /// date-shaped number format.
    | Date of value: DateTime
    /// No content. Occupies its column position (so the cells after it
    /// keep their addresses) and emits no cell element.
    | Blank

/// One cell. Its column is its position in the owning row's `Cells`
/// list — a `Blank` holds a position open rather than shifting the
/// cells after it.
type CellModel = {
    Content: CellContent
    /// Excel number-format code (`"0.00"`, `"#,##0"`, `"yyyy-mm-dd"`,
    /// `"0.0%"`). `None` is Excel's General format, except on a `Date`
    /// cell where emission substitutes `SpreadsheetDefaults.dateFormat`
    /// — a date serial under General reads back as a bare number.
    NumberFormat: string option
}

module CellModel =
    let private ofContent content = {
        Content = content
        NumberFormat = None
    }

    let text (value: string) = ofContent (CellContent.Text value)
    let number (value: float) = ofContent (CellContent.Number value)
    let boolean (value: bool) = ofContent (CellContent.Boolean value)
    let date (value: DateTime) = ofContent (CellContent.Date value)
    let blank = ofContent CellContent.Blank

    /// Apply an Excel number-format code to a cell.
    let withFormat (formatCode: string) (cell: CellModel) = {
        cell with
            NumberFormat = Some formatCode
    }

/// One row. Its row number is its position in the owning sheet's
/// `Rows` list — the model is a dense grid, so a deliberately blank
/// row is a row of `Blank` cells (or an empty `Cells` list), not a gap.
type RowModel = { Cells: CellModel list }

module RowModel =
    let ofCells (cells: CellModel list) : RowModel = { Cells = cells }

    /// A row of plain text cells — the common header-row shape.
    let ofText (values: string list) : RowModel = {
        Cells = values |> List.map CellModel.text
    }

/// A column's display width, in Excel's character-width units
/// (roughly the count of `0` glyphs of the default font that fit).
/// `ColumnIndex` is zero-based.
type ColumnWidth = { ColumnIndex: int; Width: float }

/// A merged cell range, zero-based and inclusive on both ends. A
/// single-cell range (first = last) is legal but pointless; a range
/// whose last index precedes its first is refused at validation.
type MergedRange = {
    FirstRow: int
    FirstColumn: int
    LastRow: int
    LastColumn: int
}

/// One worksheet.
type SheetModel = {
    Name: string
    Rows: RowModel list
    ColumnWidths: ColumnWidth list
    MergedRanges: MergedRange list
}

/// The structural model of one `.xlsx` workbook. Sheet order is tab
/// order.
type WorkbookModel = { Sheets: SheetModel list }

// ─── Sheet-name validation ───────────────────────────────────────

/// Why Excel would refuse a sheet name. Each case names the offending
/// fact rather than a message, so a caller can render it, branch on
/// it, or repair it deliberately.
type SheetNameError =
    /// Empty or all-whitespace.
    | SheetNameEmpty
    /// Longer than Excel's 31-character limit.
    | SheetNameTooLong of length: int
    /// Contains one or more of `: \ / ? * [ ]`. The distinct offending
    /// characters are listed in the order they first appear.
    | SheetNameIllegalCharacters of characters: char list
    /// Begins or ends with an apostrophe (Excel's own sheet-reference
    /// quoting character).
    | SheetNameEnclosingApostrophe
    /// Matches a name Excel reserves (`History`, case-insensitively).
    | SheetNameReserved of reserved: string

/// Why a workbook cannot be emitted. Validation reports every problem
/// it finds rather than stopping at the first.
type WorkbookError =
    | InvalidSheetName of sheetName: string * error: SheetNameError
    /// Two sheets share a name. Excel compares sheet names
    /// case-insensitively, so `"Data"` and `"data"` collide.
    | DuplicateSheetName of sheetName: string
    /// A workbook with no sheets. Excel cannot open one.
    | EmptyWorkbook
    | InvalidMergedRange of sheetName: string * range: MergedRange * reason: string
    | InvalidColumnWidth of sheetName: string * columnIndex: int * reason: string

module SheetName =
    /// Excel's hard limit on a sheet-name length.
    [<Literal>]
    let MaxLength = 31

    /// The characters Excel refuses in a sheet name.
    let illegalCharacters = [ ':'; '\\'; '/'; '?'; '*'; '['; ']' ]

    /// Names Excel reserves for its own use.
    let reservedNames = [ "History" ]

    /// Validate a candidate sheet name against Excel's rules. The
    /// name is returned unchanged on success — this never repairs,
    /// truncates or substitutes.
    let validate (name: string) : Result<string, SheetNameError> =
        if String.IsNullOrWhiteSpace name then
            Error SheetNameEmpty
        elif name.Length > MaxLength then
            Error(SheetNameTooLong name.Length)
        else
            let offending =
                name
                |> Seq.filter (fun c -> illegalCharacters |> List.contains c)
                |> Seq.distinct
                |> List.ofSeq

            if not offending.IsEmpty then
                Error(SheetNameIllegalCharacters offending)
            elif
                name.StartsWith("'", StringComparison.Ordinal)
                || name.EndsWith("'", StringComparison.Ordinal)
            then
                Error SheetNameEnclosingApostrophe
            else
                match
                    reservedNames
                    |> List.tryFind (fun reserved -> reserved.Equals(name, StringComparison.OrdinalIgnoreCase))
                with
                | Some reserved -> Error(SheetNameReserved reserved)
                | None -> Ok name

    /// A human-readable account of a name failure, naming the rule and
    /// the offending content.
    let describeError (name: string) (error: SheetNameError) : string =
        match error with
        | SheetNameEmpty -> "sheet name is empty or whitespace"
        | SheetNameTooLong length ->
            sprintf "sheet name '%s' is %d characters; Excel's limit is %d" name length MaxLength
        | SheetNameIllegalCharacters characters ->
            sprintf
                "sheet name '%s' contains the character(s) %s; Excel refuses : \\ / ? * [ ]"
                name
                (characters |> List.map (sprintf "'%c'") |> String.concat ", ")
        | SheetNameEnclosingApostrophe -> sprintf "sheet name '%s' begins or ends with an apostrophe" name
        | SheetNameReserved reserved -> sprintf "sheet name '%s' matches the reserved name '%s'" name reserved

module WorkbookError =
    /// A human-readable account of one workbook failure.
    let describe (error: WorkbookError) : string =
        match error with
        | InvalidSheetName(name, nameError) -> SheetName.describeError name nameError
        | DuplicateSheetName name ->
            sprintf "sheet name '%s' is used more than once; Excel compares sheet names case-insensitively" name
        | EmptyWorkbook -> "workbook has no sheets; Excel cannot open a sheetless workbook"
        | InvalidMergedRange(sheetName, range, reason) ->
            sprintf
                "sheet '%s' has an invalid merged range (rows %d..%d, columns %d..%d): %s"
                sheetName
                range.FirstRow
                range.LastRow
                range.FirstColumn
                range.LastColumn
                reason
        | InvalidColumnWidth(sheetName, columnIndex, reason) ->
            sprintf "sheet '%s' has an invalid width for column %d: %s" sheetName columnIndex reason

module SheetModel =
    /// Build a sheet, validating its name. The `Result` is the whole
    /// point: a name Excel refuses is surfaced to the caller, never
    /// truncated or rewritten behind their back.
    let create (name: string) (rows: RowModel list) : Result<SheetModel, SheetNameError> =
        SheetName.validate name
        |> Result.map (fun validName -> {
            Name = validName
            Rows = rows
            ColumnWidths = []
            MergedRanges = []
        })

    let withColumnWidths (widths: ColumnWidth list) (sheet: SheetModel) = { sheet with ColumnWidths = widths }

    let withMergedRanges (ranges: MergedRange list) (sheet: SheetModel) = { sheet with MergedRanges = ranges }

    /// The sheet's widest row, in columns.
    let columnCount (sheet: SheetModel) : int =
        sheet.Rows |> List.fold (fun widest row -> max widest row.Cells.Length) 0

module WorkbookModel =
    let empty: WorkbookModel = { Sheets = [] }

    let ofSheets (sheets: SheetModel list) : WorkbookModel = { Sheets = sheets }

    /// Zero-based column index → Excel's letter column name
    /// (`0`→`"A"`, `25`→`"Z"`, `26`→`"AA"`). The inverse of
    /// `ToolUp.Tabular`'s `Xlsx.columnIndexOfReference`.
    let columnName (index: int) : string =
        if index < 0 then
            invalidArg (nameof index) (sprintf "column index must be zero or greater, got %d" index)
        else
            let builder = StringBuilder()
            let mutable remaining = index
            let mutable go = true

            while go do
                builder.Insert(0, char (int 'A' + remaining % 26)) |> ignore
                remaining <- remaining / 26 - 1

                if remaining < 0 then
                    go <- false

            builder.ToString()

    /// An `A1`-style cell reference from zero-based row / column
    /// indexes.
    let cellReference (rowIndex: int) (columnIndex: int) : string =
        sprintf "%s%d" (columnName columnIndex) (rowIndex + 1)

    let private validateSheet (sheet: SheetModel) : WorkbookError list = [
        match SheetName.validate sheet.Name with
        | Ok _ -> ()
        | Error error -> InvalidSheetName(sheet.Name, error)

        for range in sheet.MergedRanges do
            if range.FirstRow < 0 || range.FirstColumn < 0 then
                InvalidMergedRange(sheet.Name, range, "row and column indexes must be zero or greater")
            elif range.LastRow < range.FirstRow || range.LastColumn < range.FirstColumn then
                InvalidMergedRange(sheet.Name, range, "the last row / column must not precede the first")

        for width in sheet.ColumnWidths do
            if width.ColumnIndex < 0 then
                InvalidColumnWidth(sheet.Name, width.ColumnIndex, "column index must be zero or greater")
            elif width.Width <= 0.0 || Double.IsNaN width.Width || Double.IsInfinity width.Width then
                InvalidColumnWidth(
                    sheet.Name,
                    width.ColumnIndex,
                    sprintf "width must be a finite positive number, got %g" width.Width
                )
    ]

    /// Every reason the workbook could not be emitted, in sheet order.
    /// An empty list means the model is emittable.
    let problems (model: WorkbookModel) : WorkbookError list = [
        if model.Sheets.IsEmpty then
            EmptyWorkbook

        yield! model.Sheets |> List.collect validateSheet

        // Excel compares sheet names case-insensitively, so the
        // duplicate check must too. Reported once per colliding name,
        // in first-appearance order.
        yield!
            model.Sheets
            |> List.map _.Name
            |> List.countBy (fun name -> name.ToUpperInvariant())
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (upper, _) ->
                let original =
                    model.Sheets
                    |> List.map _.Name
                    |> List.find (fun name -> name.ToUpperInvariant() = upper)

                DuplicateSheetName original)
    ]

    /// The model, or every reason it cannot be emitted.
    let validate (model: WorkbookModel) : Result<WorkbookModel, WorkbookError list> =
        match problems model with
        | [] -> Ok model
        | errors -> Error errors

/// Emission defaults the model itself does not carry.
module SpreadsheetDefaults =
    /// The number format applied to a `Date` cell that declares none.
    /// A date serial under Excel's General format renders as a bare
    /// number and reads back as one, so a date cell always carries a
    /// date-shaped format.
    [<Literal>]
    let dateFormat = "yyyy-mm-dd"