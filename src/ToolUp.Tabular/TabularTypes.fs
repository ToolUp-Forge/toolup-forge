// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Tabular

open System

// ─── Phase 123 — Tabular ingestion shared types ─────────────────
//
// Pure types: no IO, no DI, no framework references. The schema
// records declare what a consumer expects a tabular file to look
// like; the error records carry the structured per-row / per-cell
// validation report the reader returns instead of throwing.
// Everything here is an immutable record or DU (GP 5); errors are
// data, never exceptions, for data-shaped problems (GP 12.3 —
// failure expressed as data).

/// A single parsed-and-validated cell value. The reader produces
/// these from raw cell text (CSV) or typed cell payloads (XLSX)
/// after checking the owning column's `ColumnType`.
[<RequireQualifiedAccess>]
type TabularValue =
    /// Cell text for a `ColumnType.Text` or `ColumnType.Choice`
    /// column.
    | Text of string
    /// Decimal payload for a `ColumnType.Number` column. Decimal —
    /// not float — so financial-shaped imports round-trip exactly.
    | Number of decimal
    /// 64-bit integer payload for a `ColumnType.Integer` column.
    | Integer of int64
    /// Date payload for a `ColumnType.Date` column. XLSX date
    /// serials convert via the OLE-automation epoch; CSV text
    /// parses invariant-culture (ISO 8601 first).
    | Date of DateTime
    /// Boolean payload for a `ColumnType.Bool` column.
    | Bool of bool
    /// The cell was empty (and the column is not `Required`).
    /// Bound rows are total maps — every schema column has a key —
    /// so consumers pattern-match `Empty` rather than probing
    /// `Map.tryFind`.
    | Empty

/// Declared type of a column. Drives both parsing (how raw cell
/// content becomes a `TabularValue`) and the wording of the
/// `Expected` field in a `CellError`.
[<RequireQualifiedAccess>]
type ColumnType =
    /// Any text. Constraints: `MaxLength`, `Pattern`.
    | Text
    /// Decimal number. Constraints: `MinValue`, `MaxValue`.
    | Number
    /// Whole number (Int64). Constraints: `MinValue`, `MaxValue`.
    | Integer
    /// Date / date-time. XLSX numeric date serials and ISO-8601 /
    /// invariant-culture text both parse.
    | Date
    /// Boolean. Accepted text spellings (case-insensitive):
    /// true / false / yes / no / 1 / 0.
    | Bool
    /// One of an allowed set of strings (matched ordinal,
    /// case-insensitive, after trimming). Binds as
    /// `TabularValue.Text` carrying the canonical allowed value.
    | Choice of allowed: string list

/// Optional per-column value constraints, checked after the type
/// parse succeeds. Which fields apply depends on `ColumnType`:
/// `MinValue` / `MaxValue` apply to `Number` and `Integer`
/// (compared as decimal); `Pattern` and `MaxLength` apply to
/// `Text`. Inapplicable fields are ignored.
type ColumnConstraints = {
    /// Inclusive lower bound for `Number` / `Integer` columns.
    MinValue: decimal option
    /// Inclusive upper bound for `Number` / `Integer` columns.
    MaxValue: decimal option
    /// .NET regex the full cell text must match (`Text` columns).
    Pattern: string option
    /// Maximum character count (`Text` columns).
    MaxLength: int option
}

module ColumnConstraints =
    /// No constraints — the common case.
    let none: ColumnConstraints = {
        MinValue = None
        MaxValue = None
        Pattern = None
        MaxLength = None
    }

/// One declared column.
type ColumnSchema = {
    /// Canonical column name — the key in every bound row's
    /// `Map<string, TabularValue>` and the `Column` named in every
    /// `CellError`. Stable regardless of what the file's header
    /// row says.
    Name: string
    /// Header-cell text to match in the file (trimmed,
    /// case-insensitive ordinal). `None` = match on `Name`.
    /// Lets a consumer keep a clean canonical name while
    /// accepting a human-formatted header ("Unit Price (GBP)").
    Header: string option
    /// Declared type — drives parsing + the `Expected` wording in
    /// errors.
    Type: ColumnType
    /// `true` = an empty cell in this column is a `CellError`
    /// (`ConstraintViolation.RequiredValueMissing`). `false` =
    /// empty cells bind as `TabularValue.Empty`.
    Required: bool
    /// Value constraints checked after the type parse.
    Constraints: ColumnConstraints
}

module ColumnSchema =
    /// Optional non-required column of the given name + type, no
    /// constraints. Pipe-friendly starting point:
    /// `ColumnSchema.make "Sku" ColumnType.Text |> ColumnSchema.required`.
    let make (name: string) (columnType: ColumnType) : ColumnSchema = {
        Name = name
        Header = None
        Type = columnType
        Required = false
        Constraints = ColumnConstraints.none
    }

    let required (column: ColumnSchema) : ColumnSchema = { column with Required = true }

    let withHeader (header: string) (column: ColumnSchema) : ColumnSchema = { column with Header = Some header }

    let withConstraints (constraints: ColumnConstraints) (column: ColumnSchema) : ColumnSchema = {
        column with
            Constraints = constraints
    }

/// Where column positions come from.
[<RequireQualifiedAccess>]
type HeaderRowPolicy =
    /// The first row is a header; columns are located by matching
    /// header-cell text against each `ColumnSchema`'s
    /// `Header` / `Name`. Column order in the file is free.
    | FirstRowIsHeader
    /// No header row; columns bind positionally in `TableSchema`
    /// `Columns` order, starting at the first cell.
    | NoHeaderRow

/// What to do with file columns the schema doesn't declare.
[<RequireQualifiedAccess>]
type ExtraColumnPolicy =
    /// Undeclared columns are skipped silently (the default —
    /// tolerant of consumers exporting wider sheets than the
    /// import needs).
    | Ignore
    /// Each undeclared header (or each over-long row under
    /// `NoHeaderRow`) is reported as a `RowError`.
    | Reject

/// What to do when a declared column is absent from the file.
[<RequireQualifiedAccess>]
type MissingColumnPolicy =
    /// Missing declared columns abort the read structurally: the
    /// report carries one `RowErrorKind.MissingColumn` per absent
    /// column and no data rows are processed (the default — a
    /// missing column is almost always the wrong file).
    | Reject
    /// Missing columns are treated as present-but-empty. Required
    /// columns then surface per-row `RequiredValueMissing` cell
    /// errors; optional ones bind as `TabularValue.Empty`.
    | TreatAsEmpty

/// The declared shape of a tabular file.
type TableSchema = {
    /// Declared columns. Under `NoHeaderRow` the order here is the
    /// positional binding order.
    Columns: ColumnSchema list
    /// Header-row handling.
    HeaderRow: HeaderRowPolicy
    /// Policy for file columns the schema doesn't declare.
    ExtraColumns: ExtraColumnPolicy
    /// Policy for declared columns absent from the file.
    MissingColumns: MissingColumnPolicy
}

module TableSchema =
    /// Header-row schema with the tolerant defaults: extra file
    /// columns ignored, missing declared columns rejected.
    let make (columns: ColumnSchema list) : TableSchema = {
        Columns = columns
        HeaderRow = HeaderRowPolicy.FirstRowIsHeader
        ExtraColumns = ExtraColumnPolicy.Ignore
        MissingColumns = MissingColumnPolicy.Reject
    }

/// Which declared constraint a failing cell violated. `CellError`
/// carries `None` here when the failure was the type parse itself
/// (the text simply isn't a number / date / bool / allowed
/// choice).
[<RequireQualifiedAccess>]
type ConstraintViolation =
    /// Empty cell in a `Required` column.
    | RequiredValueMissing
    /// Parsed value below `ColumnConstraints.MinValue`.
    | BelowMinimum of minimum: decimal
    /// Parsed value above `ColumnConstraints.MaxValue`.
    | AboveMaximum of maximum: decimal
    /// Text failed the `ColumnConstraints.Pattern` regex.
    | PatternMismatch of pattern: string
    /// Text longer than `ColumnConstraints.MaxLength`.
    | TooLong of maxLength: int * actualLength: int

/// One failing cell. The row it belongs to is excluded from the
/// bound `Rows` (a row binds only when every cell binds); every
/// failing cell in that row is reported, not just the first.
type CellError = {
    /// 1-based row number as a user would see it in a
    /// spreadsheet UI — the header row (when present) is row 1,
    /// so the first data row is row 2. XLSX rows keep the sheet's
    /// own row numbering (gaps included).
    RowIndex: int
    /// Canonical `ColumnSchema.Name` of the failing column.
    Column: string
    /// Human-readable statement of what the schema expected
    /// ("a whole number between 1 and 999", "one of: red, green,
    /// blue").
    Expected: string
    /// The raw cell text as read from the file (pre-trim), so the
    /// report is actionable without reopening the file.
    Actual: string
    /// The specific constraint violated, or `None` when the type
    /// parse itself failed.
    Violation: ConstraintViolation option
}

/// Why a whole row (or the header) failed structurally — distinct
/// from cell-level type / constraint failures.
[<RequireQualifiedAccess>]
type RowErrorKind =
    /// Row carries more cells than the schema / header declares
    /// (reported under `ExtraColumnPolicy.Reject`).
    | ArityMismatch of expectedColumns: int * actualColumns: int
    /// The underlying parser could not produce cells for this row
    /// (e.g. CSV quoting malformed mid-row, unreadable XLSX row).
    | UnparseableRow of message: string
    /// Header row: a declared column was not found
    /// (reported under `MissingColumnPolicy.Reject`).
    | MissingColumn of column: string
    /// Header row: a file column no schema column declares
    /// (reported under `ExtraColumnPolicy.Reject`).
    | ExtraColumn of header: string
    /// Header row: the same header text matched twice — binding
    /// would be ambiguous, so the read aborts structurally.
    | DuplicateHeader of header: string

/// One structural failure, located by row.
type RowError = {
    /// 1-based row number (same numbering as `CellError`).
    /// Header-level errors carry the header row's number.
    RowIndex: int
    Kind: RowErrorKind
}

/// The complete outcome of a read: every row that bound, plus the
/// full structured report for everything that didn't. No
/// exception escapes for data-shaped errors — a caller always
/// gets this record back from a readable file.
type TabularReadResult<'Row> = {
    /// Successfully-bound rows, in file order. A row appears here
    /// only when every declared cell parsed and passed its
    /// constraints (and, for the typed surface, the consumer's
    /// binder also succeeded).
    Rows: 'Row list
    /// Every failing cell across the file (bounded by
    /// `MaxErrors`).
    CellErrors: CellError list
    /// Every structural failure — header problems, arity, rows
    /// the parser could not read.
    RowErrors: RowError list
    /// Count of data rows encountered (bound + failed), excluding
    /// the header. When `Truncated` this counts only the rows
    /// visited before the cap stopped the read.
    TotalRows: int
    /// `true` when the `MaxErrors` cap fired and enumeration
    /// stopped early — the error report is a prefix, not the
    /// whole file's.
    Truncated: bool
}

/// Consumer seam for mapping a bound row onto a domain record
/// without re-parsing (Phase 123 binder seam). Returning `Error`
/// excludes the row from `Rows` and folds the cell errors into
/// the report; the reader stamps the correct `RowIndex` onto
/// every returned `CellError`, so binders may leave it 0.
type RowBinder<'Row> = Map<string, TabularValue> -> Result<'Row, CellError list>

/// XLSX sheet selection.
[<RequireQualifiedAccess>]
type SheetSelection =
    /// 0-based position in workbook order.
    | Index of int
    /// Sheet (tab) name, matched ordinal case-insensitive.
    | Name of string

/// CSV read options.
type CsvReadOptions = {
    /// Field delimiter. `','` default; `'\t'` / `';'` for TSV and
    /// continental exports.
    Delimiter: char
    /// Error-count cap: when `CellErrors.Length + RowErrors.Length`
    /// reaches this, the read stops and the result is marked
    /// `Truncated` — keeps a pathological file (wrong delimiter,
    /// binary masquerading as CSV) from producing a
    /// 100k-entry report. `None` = unbounded.
    MaxErrors: int option
}

module CsvReadOptions =
    /// Comma delimiter, error cap 1000.
    let defaults: CsvReadOptions = {
        Delimiter = ','
        MaxErrors = Some 1000
    }

/// XLSX read options.
type XlsxReadOptions = {
    /// Which sheet to read. Default: first sheet in the workbook.
    Sheet: SheetSelection
    /// Same cap semantics as `CsvReadOptions.MaxErrors`.
    MaxErrors: int option
}

module XlsxReadOptions =
    /// First sheet, error cap 1000.
    let defaults: XlsxReadOptions = {
        Sheet = SheetSelection.Index 0
        MaxErrors = Some 1000
    }

/// Per-row outcome on the streaming surface
/// (`TabularReader.streamCsv` / `streamXlsx`). The materialising
/// `read*` functions fold this; consumers with 100k-row files can
/// fold it themselves and never hold the table in memory.
[<RequireQualifiedAccess>]
type RowOutcome =
    /// Every declared cell parsed + passed constraints. The map
    /// is total over schema column names.
    | Bound of rowIndex: int * row: Map<string, TabularValue>
    /// At least one cell failed; all of the row's failures are
    /// carried together.
    | Invalid of errors: CellError list
    /// The row (or the header, before any data row is yielded)
    /// failed structurally.
    | Structural of error: RowError