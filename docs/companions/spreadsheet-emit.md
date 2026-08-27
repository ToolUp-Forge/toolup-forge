# Building a workbook: `ToolUp.OpenXml.Spreadsheet`

A worked example — a two-sheet financial report built from scratch, emitted as a
valid `.xlsx`, and read back to prove what survived.

`ToolUp.Tabular` reads spreadsheets. `ToolUp.OpenXml` writes documents, but only
WordprocessingML. `ToolUp.OpenXml.Spreadsheet` is the SpreadsheetML write side:
a typed workbook model plus `Emit.toBytes`. It is the layer to reach for when
the workbook does not exist yet — as opposed to filling a template someone
authored in Excel, which is `ToolUp.Reporting.Xlsx`'s job.

## The model

Four record types, nested the way a spreadsheet is:

```fsharp skip=signature
module ToolUp.OpenXml.Spreadsheet =
    type WorkbookModel = { Sheets: SheetModel list }
    type SheetModel =
        { Name: string
          Rows: RowModel list
          ColumnWidths: ColumnWidth list
          MergedRanges: MergedRange list }
    type RowModel = { Cells: CellModel list }
    type CellModel = { Content: CellContent; NumberFormat: string option }
```

Two positional conventions are worth stating once, because they are what keeps
addresses stable:

- **A cell's column is its index in `Cells`**, and **a row's number is its index
  in `Rows`.** The model is a dense grid.
- **`CellContent.Blank` holds a position open.** It emits no cell element — an
  empty cell in a spreadsheet is an absence, not a value — but the cells after
  it keep their own addresses. A row of nothing but blanks is omitted entirely,
  and the rows after it keep their numbers.

## A two-sheet report

```fsharp
open System
open ToolUp.OpenXml.Spreadsheet

/// Sheet 1 — a merged title band, a header row, then the data.
let private summarySheet () =
    let rows = [
        RowModel.ofCells [
            CellModel.text "FY2026 Regional Summary"
            CellModel.blank
            CellModel.blank
        ]
        RowModel.ofText [ "Region"; "Revenue"; "Reported" ]
        RowModel.ofCells [
            CellModel.text "North"
            CellModel.number 128_400.0 |> CellModel.withFormat "#,##0.00"
            CellModel.date (DateTime(2026, 3, 31))
        ]
        RowModel.ofCells [
            CellModel.text "South"
            CellModel.number 96_250.5 |> CellModel.withFormat "#,##0.00"
            CellModel.date (DateTime(2026, 3, 31))
        ]
    ]

    SheetModel.create "Summary" rows
    |> Result.map (
        SheetModel.withMergedRanges [
            { FirstRow = 0
              FirstColumn = 0
              LastRow = 0
              LastColumn = 2 }
        ]
        >> SheetModel.withColumnWidths [
            { ColumnIndex = 0; Width = 18.0 }
            { ColumnIndex = 1; Width = 14.0 }
            { ColumnIndex = 2; Width = 14.0 }
        ]
    )

/// Sheet 2 — the ratios, as percentages, plus an audit flag.
let private ratiosSheet () =
    SheetModel.create "Ratios" [
        RowModel.ofText [ "Region"; "Share"; "Audited" ]
        RowModel.ofCells [
            CellModel.text "North"
            CellModel.number 0.5714 |> CellModel.withFormat "0.0%"
            CellModel.boolean true
        ]
        RowModel.ofCells [
            CellModel.text "South"
            CellModel.number 0.4286 |> CellModel.withFormat "0.0%"
            CellModel.boolean false
        ]
    ]

/// Both sheets, or the first naming failure.
let buildReport () : Result<WorkbookModel, SheetNameError> =
    summarySheet ()
    |> Result.bind (fun summary ->
        ratiosSheet ()
        |> Result.map (fun ratios -> WorkbookModel.ofSheets [ summary; ratios ]))

/// Emit it. Two calls with the same model produce identical bytes.
let writeReport (path: string) =
    match buildReport () with
    | Ok model -> IO.File.WriteAllBytes(path, Emit.toBytes model)
    | Error error -> failwith (SheetName.describeError "report sheet" error)
```

## Names are validated, never repaired

`SheetModel.create` returns a `Result` because Excel refuses several sheet names
outright, and the alternative to surfacing that is a file which opens and is
quietly not what you asked for:

```fsharp
let checkName (candidate: string) =
    match SheetName.validate candidate with
    | Ok name -> sprintf "'%s' is fine" name
    | Error error -> SheetName.describeError candidate error

// "Q1/Q2"                        -> contains the character '/'
// String.replicate 40 "x"        -> is 40 characters; Excel's limit is 31
// "History"                      -> matches the reserved name 'History'
```

Nothing truncates a 40-character name to 31. That is the point: a truncation is
a decision, and it is the caller's to make.

`WorkbookModel.validate` adds the workbook-level rules — at least one sheet,
case-insensitively unique names, well-formed merged ranges and column widths —
and reports **every** problem in one pass rather than stopping at the first:

```fsharp
let report (model: WorkbookModel) =
    WorkbookModel.problems model |> List.map WorkbookError.describe
```

`Emit.tryToBytes` is the recoverable entry point (`Result<byte[], WorkbookError list>`);
`Emit.toBytes` raises, naming each failure, for the cases where an invalid model
is a programming error rather than user input.

## Dates are serials under a date format

Excel has no date type. A date cell is a number under a number format that
renders as a date, and the *format* is what makes a reader classify it as one.
So a `Date` cell that declares no format is emitted under
`SpreadsheetDefaults.dateFormat` — a date serial under Excel's General format
renders, and reads back, as a bare number.

Supply your own where the default is wrong:

```fsharp
let stamped (at: DateTime) =
    CellModel.date at |> CellModel.withFormat "yyyy-mm-dd hh:mm"
```

Format codes are pooled into the styles part in first-appearance order, with ids
allocated from 164 upward (Excel reserves everything below that for its
built-ins). Three distinct formats produce three custom formats and four
`cellXf` entries — the unformatted default plus one per format.

## Reading it back

The emitted workbook reopens through `ToolUp.Tabular`'s reader, which is what
the package's own test pack verifies against — deliberately, because a writer
checked only by its author's reader has proved that the two agree, not that the
file is right.

```fsharp
open ToolUp.Tabular

let summaryRows (bytes: byte[]) =
    use stream = new IO.MemoryStream(bytes)

    Xlsx.readRows (SheetSelection.Name "Summary") stream
    |> Seq.choose (function
        | Ok row -> Some row
        | Error _ -> None)
    |> Seq.toList
```

Read back, the revenue cells arrive as `CellContent.Number`, the reported dates
as `CellContent.Date`, and the audit flags as `CellContent.Bool` — the
date-vs-number distinction being the one that proves the styles part reached the
right cells.

## Determinism, and why it needs help

Two emits of the same model produce identical bytes. Two things would otherwise
break that silently, and neither is visible until you compare the bytes:

- **ZIP entry timestamps** default to the current time, so every emit differs.
- **The package-root relationship id** is minted from a fresh GUID by
  `AddWorkbookPart()`, with no id-taking overload to intercept, and it lands
  verbatim in `_rels/.rels`.

Emission assigns every other relationship id explicitly and then normalises the
finished archive — entries in ordinal name order, one fixed entry timestamp, and
root relationship ids rewritten to `rId1`… . Root relationship ids are
referenced by no part content, and ZIP entry order carries no meaning in OPC, so
nothing semantic changes.

Byte-identical output is what makes an emitted workbook hashable for a cache
key, comparable in a golden-file corpus, and diffable in review.

## Scope

The model covers the structural facts a generated report carries: values,
number formats, column widths, merged ranges. Formulas, charts, conditional
formatting, pivot tables, data validation, and cell fonts / fills / borders are
out of scope, as is reading an existing workbook. A workbook needing those is a
template-fill job — author it in Excel and fill it with `ToolUp.Reporting.Xlsx`,
which preserves everything it does not touch.

## See also

- `src/ToolUp.OpenXml.Spreadsheet/README.md` — the package README.
- `ToolUp.Tabular` — typed CSV / XLSX ingestion, the read side.
- `ToolUp.OpenXml` — the WordprocessingML sibling.
- `ToolUp.Reporting.Xlsx` — template fill into a workbook that already exists.
