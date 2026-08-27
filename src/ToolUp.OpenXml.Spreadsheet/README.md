# ToolUp.OpenXml.Spreadsheet

The **write side** of SpreadsheetML: an immutable typed workbook model, and
`Emit.toBytes` producing a valid `.xlsx` from it.

`ToolUp.Tabular` reads spreadsheets and does not write them; `ToolUp.OpenXml`
writes documents and is WordprocessingML-only. This companion is the missing
leg — building a workbook **from scratch**, rather than filling a template
somebody authored in Excel first.

- **Typed model** (`WorkbookModel`) — workbook → sheets → rows → cells, with a
  cell-value DU covering text / number / boolean / date-as-serial, per-cell
  number-format codes, column widths, and merged ranges. Immutable records
  throughout (GP 5); no live OpenXml SDK handle leaks through the model
  (GP 12 rule 1).
- **Validated names, not repaired ones** — a sheet name Excel would refuse comes
  back as a `Result` naming the rule it broke. Nothing here truncates a
  40-character name to 31 or strips a `/` behind your back: a file that opens
  and is silently not what you asked for is the worse outcome.
- **Deterministic emission** (`Emit`) — two emits of the same model are
  **byte-identical**, which makes an emitted workbook hashable, cacheable, and
  diffable in a fixture corpus.
- **Package plumbing** (`Package`) — create the package, add parts under
  caller-chosen relationship ids, normalise the finished archive.

The companion is **additive by construction** (GP 13): it references nothing in
`ToolUp.Platform.*` and nothing references it; its only dependency is the OpenXml
SDK (GP 1 — the vendor dep stays here).

## Quick start

```fsharp
open ToolUp.OpenXml.Spreadsheet

let rows = [
    RowModel.ofText [ "Region"; "Revenue"; "Reported" ]
    RowModel.ofCells [
        CellModel.text "North"
        CellModel.number 128_400.0 |> CellModel.withFormat "#,##0.00"
        CellModel.date (System.DateTime(2026, 3, 31))
    ]
    RowModel.ofCells [
        CellModel.text "South"
        CellModel.number 96_250.5 |> CellModel.withFormat "#,##0.00"
        CellModel.date (System.DateTime(2026, 3, 31))
    ]
]

let workbook =
    match SheetModel.create "Q1 Revenue" rows with
    | Ok sheet ->
        sheet
        |> SheetModel.withColumnWidths [ { ColumnIndex = 0; Width = 18.0 } ]
        |> List.singleton
        |> WorkbookModel.ofSheets
    | Error error -> failwith (SheetName.describeError "Q1 Revenue" error)

System.IO.File.WriteAllBytes("revenue.xlsx", Emit.toBytes workbook)
```

## Sheet-name validation

`SheetName.validate` is the whole of Excel's rule set, each failure a case you
can branch on rather than a message you have to parse:

| Case | Rule |
|---|---|
| `SheetNameEmpty` | empty or all-whitespace |
| `SheetNameTooLong` | longer than 31 characters |
| `SheetNameIllegalCharacters` | contains any of `: \ / ? * [ ]` |
| `SheetNameEnclosingApostrophe` | begins or ends with `'` |
| `SheetNameReserved` | matches `History` (case-insensitively) |

`WorkbookModel.validate` adds the workbook-level checks — at least one sheet,
case-insensitively unique sheet names, well-formed merged ranges and column
widths — and reports **every** problem rather than stopping at the first.
`Emit.tryToBytes` is the recoverable entry point; `Emit.toBytes` raises,
naming each failure, for the cases where an invalid model is a programming
error.

## Dates and number formats

Excel has no date type: a date is a numeric serial rendered under a date-shaped
number format. This package stores dates the same way, so a `Date` cell with no
declared format is emitted under `SpreadsheetDefaults.dateFormat`
(`yyyy-mm-dd`) — a date serial under Excel's General format renders, and reads
back, as a bare number.

Format codes are pooled into the styles part in first-appearance order and
allocated ids from 164 upward (Excel reserves everything below that for its
built-ins). A workbook using three distinct formats carries exactly three
custom formats and four `cellXf` entries — the unformatted default plus one per
format.

## Determinism

Two emits of one model produce identical bytes. That is not free, and two
things would otherwise silently break it:

- **ZIP entry timestamps.** Every archive entry carries a last-write time,
  defaulted to *now*.
- **The package-root relationship id.** `AddWorkbookPart()` mints it from a
  fresh GUID, with no id-taking overload to intercept, and it lands verbatim in
  `_rels/.rels`.

Emission assigns every other relationship id explicitly, then normalises the
finished archive: entries in ordinal name order, a fixed entry timestamp, and
root relationship ids rewritten to `rId1`… (they are referenced by no part
content, so the rewrite is a pure normalisation). Neither change is semantic —
ZIP entry order carries no meaning in OPC.

## Round-tripping

An emitted workbook reopens through `ToolUp.Tabular`'s `Xlsx` reader with its
values and its date-vs-number distinction intact — that reader is what the test
pack verifies against, deliberately, rather than a parser this package ships.

```fsharp
open ToolUp.Tabular

let reopen (bytes: byte[]) =
    use stream = new System.IO.MemoryStream(bytes)
    Xlsx.readRows (SheetSelection.Name "Q1 Revenue") stream |> Seq.toList
```

## Out of scope

Formulas, charts, conditional formatting, pivot tables, data validation, cell
fonts / fills / borders, and reading an existing workbook. The model covers the
structural facts a generated report carries; anything richer is a template-fill
job, and template fill is a different package's problem.

## See also

- `ToolUp.Tabular` — the read side (typed CSV / XLSX ingestion with per-row
  validation reporting).
- `ToolUp.OpenXml` — the WordprocessingML sibling: import, emit, and native
  tracked-change redlines.
- [`docs/companions/spreadsheet-emit.md`](../../docs/companions/spreadsheet-emit.md)
  — a worked example building a multi-sheet report end to end.
