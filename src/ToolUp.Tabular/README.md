# ToolUp.Tabular

Typed CSV / XLSX ingestion for ToolUp.Platform consumers: parse a tabular file into
typed rows against a declared column schema, and get back a **structured per-row /
per-cell error report** — row index, column, what was expected, what the file
actually said — instead of an exception on the first bad cell.

Built for the "upload your data as a spreadsheet" surface every data-shaped consumer
app eventually grows: bulk submission import, reference-data loads, batch updates.
One read call returns both the rows that bound cleanly and a report the operator can
act on without reopening the file.

- **CSV leg** — zero third-party dependencies. A BCL-only RFC 4180 parser: quoting,
  embedded delimiters and newlines, `""` escapes, configurable delimiter, BOM-first
  encoding detection (UTF-8 / UTF-16 / UTF-32; BOM-less input decodes as UTF-8).
- **XLSX / XLSM leg** — reads via `DocumentFormat.OpenXml` (the package's only
  third-party dependency, isolated here per GP 1 — `ToolUp.Platform.*` never
  references it). Shared strings, inline strings, number-format-aware date
  detection, sheet selection by name or index. Macro-enabled `.xlsm` workbooks read
  through the same call and produce identical results — **macros are never
  executed**, see below.
- **Additive by construction** (GP 13) — the companion references nothing in
  `ToolUp.Platform.*` and nothing references it; deployments that don't compose it
  pay zero cost.

## Quick start

```fsharp skip=fragment
open ToolUp.Tabular

let schema =
    TableSchema.make [
        ColumnSchema.make "Sku" ColumnType.Text
        |> ColumnSchema.required
        |> ColumnSchema.withConstraints {
            ColumnConstraints.none with
                Pattern = Some "[A-Z]{2}-[0-9]{4}"
        }
        ColumnSchema.make "Name" ColumnType.Text |> ColumnSchema.required
        ColumnSchema.make "Price" ColumnType.Number
        |> ColumnSchema.required
        |> ColumnSchema.withConstraints {
            ColumnConstraints.none with
                MinValue = Some 0m
        }
        ColumnSchema.make "Stock" ColumnType.Integer
        ColumnSchema.make "Launched" ColumnType.Date
        ColumnSchema.make "Status" (ColumnType.Choice [ "Active"; "Discontinued" ])
    ]

let result = TabularReader.readCsvBytes schema CsvReadOptions.defaults uploadedBytes

// result.Rows       : Map<string, TabularValue> list — rows where every cell bound
// result.CellErrors : CellError list — every failing cell (row, column, expected, actual)
// result.RowErrors  : RowError list  — structural problems (missing column, bad quoting, …)
```

For XLSX the call is symmetric — same schema, same result shape:

```fsharp skip=fragment
let result = TabularReader.readXlsxBytes schema XlsxReadOptions.defaults uploadedBytes
```

### Binding to domain records

Rows come back as `Map<string, TabularValue>` by default. The binder seam maps them
onto domain records without re-parsing — cells arrive already typed:

```fsharp skip=fragment
type Product = { Sku: string; Price: decimal }

let bindProduct (row: Map<string, TabularValue>) : Result<Product, CellError list> =
    match row["Sku"], row["Price"] with
    | TabularValue.Text sku, TabularValue.Number price -> Ok { Sku = sku; Price = price }
    | _ -> Error []   // unreachable when the schema declares these columns

let typed = TabularReader.readCsvBytesWith bindProduct schema CsvReadOptions.defaults bytes
// typed.Rows : Product list
```

A binder may also return domain-rule errors (`Error [cellError]`); the reader stamps
the correct `RowIndex` onto them, so binders can leave it `0`.

## Error model

No exception escapes for data-shaped problems — wrong types, constraint violations,
malformed quoting, a missing column, a corrupt workbook all come back as data:

| Problem | Where it lands |
|---|---|
| Cell fails its type parse or constraint | `CellErrors` (`RowIndex`, `Column`, `Expected`, `Actual`, `Violation`) |
| Row has malformed CSV quoting / unreadable XLSX row | `RowErrors` (`UnparseableRow`) |
| Declared column absent (`MissingColumnPolicy.Reject`) | `RowErrors` (`MissingColumn`), read aborts |
| Undeclared column present (`ExtraColumnPolicy.Reject`) | `RowErrors` (`ExtraColumn`), rows still bind |
| Same header twice | `RowErrors` (`DuplicateHeader`), read aborts |
| Not a workbook / sheet not found | one `RowErrors` entry at `RowIndex = 0` |

A row binds only when **every** declared cell binds; all of a failing row's cell
errors are reported together, not just the first. Row numbers are 1-based as a
spreadsheet UI shows them (header = row 1; XLSX rows keep the sheet's own numbering).
Fully-empty rows are skipped, not reported. The only exceptions thrown are for
schema-authoring bugs (an invalid `Pattern` regex), raised eagerly before any row is
read.

`MaxErrors` (default 1000) caps the materialised report: when the cap fires the
result is marked `Truncated` and enumeration stops, so a pathological file (wrong
delimiter, binary bytes) can't produce a hundred-thousand-entry report.

## Memory contract (large files)

`readCsv` / `readXlsx` materialise only the **result** (bound rows + the capped
error report) — never the file's full raw cell grid:

- The CSV parser is a single-pass character state machine; nothing is buffered
  beyond the current record.
- The XLSX leg walks rows with `OpenXmlReader` — one `Row` element is materialised
  at a time, never the worksheet DOM. The one deliberately-resident structure is the
  workbook's **shared-string table** (a workbook-level lookup any row may
  reference); its size is proportional to the file's distinct strings, not its row
  count.

Consumers that must not hold even the bound rows in memory use the streaming
surface and fold per-row outcomes themselves:

```fsharp skip=fragment
TabularReader.streamCsv schema CsvReadOptions.defaults stream
|> Seq.iter (function
    | RowOutcome.Bound(rowIndex, row) -> ingest rowIndex row
    | RowOutcome.Invalid errors -> reportCells errors
    | RowOutcome.Structural error -> reportRow error)
```

The streams are lazy end-to-end: taking the first N outcomes of a 100k-row file
reads only those rows' bytes (the test pack proves this with a byte-counting
stream). `MaxErrors` does not apply to the streaming surface — the cap bounds
materialised reports; stream consumers decide their own stopping rule.

## Dates in XLSX

Real-world XLSX stores dates as numeric serials whose **style** (numFmtId) marks
them as dates. The reader detects the built-in date formats and date-shaped custom
format codes, honours the 1904 date system (`workbookPr date1904`), and converts
serials via the OLE-automation epoch. Two documented corners:

- A `Date` column over a numeric cell whose style is *not* date-shaped is read as a
  1900-system serial (the style is the only 1904 marker, so unstyled serials in a
  1904 workbook are out of reach).
- Serials below 61 (dates before 1900-03-01) inherit the historical Excel
  leap-year-1900 off-by-one; dates from 1900-03-01 onward are exact.

Formula cells are read as their cached results (the stored value), not evaluated.

## Macro-enabled workbooks (`.xlsm`) — macros are never executed

Macro-enabled workbooks are read by the same `readXlsx*` / `streamXlsx` calls, with
the same options and the same `XlsxReadOptions`. There is no separate API and no
flag to set: a `.xlsm` and the equivalent `.xlsx` produce byte-for-byte the same
rows, the same cell values and the same error report. The container differs only in
its `[Content_Types].xml` declaration and in carrying an extra `xl/vbaProject.bin`
part; neither is part of the sheet grid.

**The macro part is ignored — never executed, never evaluated, never extracted.**
That is structural, not a promise: the reader resolves exactly four parts — the
workbook part, the shared-string table, the workbook stylesheet, and the selected
worksheet — and `vbaProject.bin` is not among them. Nothing here resolves its
relationship, opens its stream or reads a byte of it, and the package contains no
interpreter, formula evaluator or VBA host that could run it. Enumerating the
container to locate the workbook part reads part *names*, not part *contents*.

Two consequences worth stating to your own users:

- An upload surface that accepts `.xlsm` can honestly tell an uploader "macros in
  this workbook are ignored" — not "we scanned them", not "we ran them safely".
- Accepting `.xlsm` is still accepting a file that carries executable content **for
  other software**. If your deployment stores originals and serves them back, the
  macro travels with the file; that is a storage-and-egress decision, independent of
  this reader. The upload-validation seam (`SniffingUploadValidator` with
  `MimeSniffOptions.withSpreadsheetPackages`) corroborates that a payload declared as
  a workbook really is one, which is a different question again.

Running a workbook — evaluating macros or recalculating formulas — is a different
feature with a different threat model and is deliberately out of scope.

## Out of scope

XLSX/XLSM *writing*, the legacy `.xls` binary format, **macro execution or
extraction**, formula evaluation, and schema *inference* (schemas are declared, not
guessed).
