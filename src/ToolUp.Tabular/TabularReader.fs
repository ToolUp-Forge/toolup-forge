// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Tabular

open System
open System.Globalization
open System.IO
open System.Text.RegularExpressions

// ─── Phase 123 — schema validation + typed binding ──────────────
//
// The single validation engine both file legs converge on. CSV
// records and XLSX rows are normalised to one internal source
// shape; everything from header matching to per-cell type checks
// and the structured error report is shared, so the two formats
// cannot drift apart in validation behaviour.
//
// No throw-on-first-error anywhere on the happy path: every
// data-shaped problem — wrong type, constraint violation,
// malformed quoting, missing column, corrupt workbook — comes
// back as `CellError` / `RowError` data (GP 12.3). The only
// exceptions raised are for schema-authoring bugs (an invalid
// `Pattern` regex), thrown eagerly before any row is read.

/// Schema-driven tabular reads. `readCsv` / `readXlsx` (and the
/// `Bytes` overloads) materialise a `TabularReadResult`; the
/// `With` variants apply a consumer `RowBinder` to map bound rows
/// onto domain records; `streamCsv` / `streamXlsx` expose the
/// per-row outcomes lazily for consumers that must not hold the
/// table in memory (the `MaxErrors` cap is a materialiser
/// concern and does not apply to the streaming surface).
[<RequireQualifiedAccess>]
module TabularReader =

    // ── Internal source normalisation ───────────────────────────

    [<RequireQualifiedAccess>]
    type private Content =
        | Text of string
        | Number of float
        | Date of DateTime
        | Bool of bool
        | CellError of message: string

    type private SourceCell = { RawText: string; Content: Content }

    [<RequireQualifiedAccess>]
    type private SourceRow =
        /// Dense, positional cells.
        | Data of rowIndex: int * cells: SourceCell[]
        /// The parser could not produce cells for this row.
        | Broken of rowIndex: int * message: string
        /// File-level failure (not a workbook, sheet missing,
        /// corrupt part). Reported at row 0 and ends the read.
        | Fatal of message: string

    let private textCell (text: string) = {
        RawText = text
        Content = Content.Text text
    }

    let private csvSource (delimiter: char) (reader: TextReader) : seq<SourceRow> =
        Csv.parseRecords delimiter reader
        |> Seq.map (fun (rowIndex, record) ->
            match record with
            | Ok fields -> SourceRow.Data(rowIndex, fields |> Array.map textCell)
            | Error message -> SourceRow.Broken(rowIndex, message))

    let private xlsxSource (selection: SheetSelection) (stream: Stream) : seq<SourceRow> =
        Xlsx.readRows selection stream
        |> Seq.map (fun row ->
            match row with
            | Error message -> SourceRow.Fatal message
            | Ok row ->
                // Expand the sparse cell list to dense positions so
                // header matching and positional binding line up
                // with what a spreadsheet UI shows.
                let width =
                    if row.Cells.Length = 0 then
                        0
                    else
                        (row.Cells |> Array.map _.ColumnIndex |> Array.max) + 1

                let dense = Array.init width (fun _ -> textCell "")

                for cell in row.Cells do
                    let content =
                        match cell.Content with
                        | Xlsx.CellContent.Text text -> Content.Text text
                        | Xlsx.CellContent.Number value -> Content.Number value
                        | Xlsx.CellContent.Date date -> Content.Date date
                        | Xlsx.CellContent.Bool value -> Content.Bool value
                        | Xlsx.CellContent.CellError message -> Content.CellError message

                    dense[cell.ColumnIndex] <- {
                        RawText = cell.RawText
                        Content = content
                    }

                SourceRow.Data(row.RowIndex, dense))

    // ── Per-column compiled plan ────────────────────────────────

    type private ColumnPlan = {
        Schema: ColumnSchema
        /// Compiled full-match regex when `Pattern` is declared.
        Pattern: Regex option
        /// Header text this column matches on (trimmed).
        HeaderText: string
        /// Human-readable `Expected` wording for errors.
        Expected: string
    }

    let private describeColumn (column: ColumnSchema) : string =
        let constraints = column.Constraints

        let range =
            match constraints.MinValue, constraints.MaxValue with
            | Some min, Some max -> sprintf " between %M and %M" min max
            | Some min, None -> sprintf " of at least %M" min
            | None, Some max -> sprintf " of at most %M" max
            | None, None -> ""

        match column.Type with
        | ColumnType.Text ->
            let length =
                match constraints.MaxLength with
                | Some max -> sprintf " of at most %d characters" max
                | None -> ""

            let pattern =
                match constraints.Pattern with
                | Some pattern -> sprintf " matching pattern %s" pattern
                | None -> ""

            "text" + length + pattern
        | ColumnType.Number -> "a number" + range
        | ColumnType.Integer -> "a whole number" + range
        | ColumnType.Date -> "a date"
        | ColumnType.Bool -> "a boolean (true/false, yes/no, 1/0)"
        | ColumnType.Choice allowed -> "one of: " + String.concat ", " allowed

    /// Compile the schema once, eagerly — an invalid `Pattern`
    /// regex is a schema-authoring bug and throws here (with the
    /// column named) before any row is read, never mid-stream.
    let private compilePlan (schema: TableSchema) : ColumnPlan[] =
        schema.Columns
        |> List.map (fun column ->
            let pattern =
                column.Constraints.Pattern
                |> Option.map (fun pattern ->
                    try
                        Regex(sprintf "^(?:%s)$" pattern, RegexOptions.CultureInvariant)
                    with :? ArgumentException as ex ->
                        invalidArg
                            "schema"
                            (sprintf "column '%s' declares an invalid Pattern regex: %s" column.Name ex.Message))

            {
                Schema = column
                Pattern = pattern
                HeaderText = (column.Header |> Option.defaultValue column.Name).Trim()
                Expected = describeColumn column
            })
        |> List.toArray

    // ── Per-cell validation ─────────────────────────────────────

    let private cellError (rowIndex: int) (plan: ColumnPlan) (actual: string) (violation: ConstraintViolation option) = {
        RowIndex = rowIndex
        Column = plan.Schema.Name
        Expected = plan.Expected
        Actual = actual
        Violation = violation
    }

    let private isEmptyCell (cell: SourceCell) =
        match cell.Content with
        | Content.Text text -> String.IsNullOrWhiteSpace text
        | _ -> false

    /// Text form of a cell, for `Text` / `Choice` binding.
    let private textOf (cell: SourceCell) =
        match cell.Content with
        | Content.Text text -> text.Trim()
        | _ -> cell.RawText

    let private boolSpellings =
        dict [
            "true", true
            "yes", true
            "1", true
            "false", false
            "no", false
            "0", false
        ]

    let private checkRange (plan: ColumnPlan) (value: decimal) : ConstraintViolation option =
        match plan.Schema.Constraints.MinValue with
        | Some min when value < min -> Some(ConstraintViolation.BelowMinimum min)
        | _ ->
            match plan.Schema.Constraints.MaxValue with
            | Some max when value > max -> Some(ConstraintViolation.AboveMaximum max)
            | _ -> None

    let private validateCell
        (rowIndex: int)
        (plan: ColumnPlan)
        (cell: SourceCell option)
        : Result<TabularValue, CellError> =
        let column = plan.Schema

        let emptyCell () =
            if column.Required then
                Error(cellError rowIndex plan "" (Some ConstraintViolation.RequiredValueMissing))
            else
                Ok TabularValue.Empty

        match cell with
        | None -> emptyCell ()
        | Some cell when isEmptyCell cell -> emptyCell ()
        | Some cell ->
            let typeFailure () =
                Error(cellError rowIndex plan cell.RawText None)

            match column.Type with
            | ColumnType.Text ->
                let text = textOf cell

                match column.Constraints.MaxLength with
                | Some max when text.Length > max ->
                    Error(cellError rowIndex plan cell.RawText (Some(ConstraintViolation.TooLong(max, text.Length))))
                | _ ->
                    match plan.Pattern with
                    | Some regex when not (regex.IsMatch text) ->
                        Error(
                            cellError
                                rowIndex
                                plan
                                cell.RawText
                                (Some(
                                    ConstraintViolation.PatternMismatch(
                                        column.Constraints.Pattern |> Option.defaultValue ""
                                    )
                                ))
                        )
                    | _ -> Ok(TabularValue.Text text)
            | ColumnType.Number ->
                let parsed =
                    match cell.Content with
                    | Content.Number value ->
                        try
                            Some(decimal value)
                        with :? OverflowException ->
                            None
                    | Content.Text text ->
                        match Decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture) with
                        | true, value -> Some value
                        | false, _ -> None
                    | _ -> None

                match parsed with
                | None -> typeFailure ()
                | Some value ->
                    match checkRange plan value with
                    | Some violation -> Error(cellError rowIndex plan cell.RawText (Some violation))
                    | None -> Ok(TabularValue.Number value)
            | ColumnType.Integer ->
                let parsed =
                    match cell.Content with
                    | Content.Number value when
                        Double.IsFinite value
                        && Math.Truncate value = value
                        && value >= -9.2e18
                        && value <= 9.2e18
                        ->
                        Some(int64 value)
                    | Content.Text text ->
                        match
                            Int64.TryParse(
                                text.Trim(),
                                NumberStyles.Integer ||| NumberStyles.AllowThousands,
                                CultureInfo.InvariantCulture
                            )
                        with
                        | true, value -> Some value
                        | false, _ -> None
                    | _ -> None

                match parsed with
                | None -> typeFailure ()
                | Some value ->
                    match checkRange plan (decimal value) with
                    | Some violation -> Error(cellError rowIndex plan cell.RawText (Some violation))
                    | None -> Ok(TabularValue.Integer value)
            | ColumnType.Date ->
                let parsed =
                    match cell.Content with
                    | Content.Date date -> Some date
                    | Content.Number serial ->
                        // A Date column over a numeric cell whose
                        // style is not date-shaped: read as a
                        // 1900-system serial (the overwhelmingly
                        // common case; a 1904-system workbook with
                        // unstyled date serials is out of reach
                        // because the style is the only marker).
                        try
                            Some(DateTime.FromOADate serial)
                        with _ ->
                            None
                    | Content.Text text ->
                        match DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                        | true, date -> Some date
                        | false, _ -> None
                    | _ -> None

                match parsed with
                | None -> typeFailure ()
                | Some date -> Ok(TabularValue.Date date)
            | ColumnType.Bool ->
                let parsed =
                    match cell.Content with
                    | Content.Bool value -> Some value
                    | Content.Number 1.0 -> Some true
                    | Content.Number 0.0 -> Some false
                    | Content.Text text ->
                        match boolSpellings.TryGetValue(text.Trim().ToLowerInvariant()) with
                        | true, value -> Some value
                        | false, _ -> None
                    | _ -> None

                match parsed with
                | None -> typeFailure ()
                | Some value -> Ok(TabularValue.Bool value)
            | ColumnType.Choice allowed ->
                let text = textOf cell

                let canonical =
                    allowed
                    |> List.tryFind (fun candidate -> candidate.Trim().Equals(text, StringComparison.OrdinalIgnoreCase))

                match canonical with
                | Some canonical -> Ok(TabularValue.Text canonical)
                | None -> typeFailure ()

    // ── Row validation ──────────────────────────────────────────

    /// Position of each schema column in the file: `Some i` =
    /// cell index, `None` = column absent (binds `Empty` under
    /// `MissingColumnPolicy.TreatAsEmpty`).
    type private BindingPlan = {
        Positions: (ColumnPlan * int option)[]
        /// Declared width for arity checks under
        /// `ExtraColumnPolicy.Reject`.
        ExpectedWidth: int
    }

    let private validateDataRow (schema: TableSchema) (binding: BindingPlan) (rowIndex: int) (cells: SourceCell[]) =
        if
            schema.ExtraColumns = ExtraColumnPolicy.Reject
            && cells.Length > binding.ExpectedWidth
        then
            RowOutcome.Structural {
                RowIndex = rowIndex
                Kind = RowErrorKind.ArityMismatch(binding.ExpectedWidth, cells.Length)
            }
        else
            let mutable errors = []
            let mutable bound = Map.empty

            for plan, position in binding.Positions do
                let cell =
                    position
                    |> Option.bind (fun i -> if i < cells.Length then Some cells[i] else None)

                match validateCell rowIndex plan cell with
                | Ok value -> bound <- bound.Add(plan.Schema.Name, value)
                | Error error -> errors <- error :: errors

            if List.isEmpty errors then
                RowOutcome.Bound(rowIndex, bound)
            else
                RowOutcome.Invalid(List.rev errors)

    /// Match a header row against the compiled plan. Returns the
    /// binding plan plus any header-level structural errors and
    /// whether the read can continue.
    let private bindHeader (schema: TableSchema) (plans: ColumnPlan[]) (rowIndex: int) (cells: SourceCell[]) =
        let headers = cells |> Array.map (fun cell -> (textOf cell).Trim())

        let positionOf (plan: ColumnPlan) =
            headers
            |> Array.toList
            |> List.indexed
            |> List.filter (fun (_, header) -> header.Equals(plan.HeaderText, StringComparison.OrdinalIgnoreCase))
            |> List.map fst

        let matches = plans |> Array.map (fun plan -> plan, positionOf plan)

        let duplicates =
            matches
            |> Array.filter (fun (_, positions) -> List.length positions > 1)
            |> Array.map (fun (plan, _) -> {
                RowIndex = rowIndex
                Kind = RowErrorKind.DuplicateHeader plan.HeaderText
            })

        if duplicates.Length > 0 then
            Error(List.ofArray duplicates)
        else
            let missing =
                matches
                |> Array.filter (fun (_, positions) -> List.isEmpty positions)
                |> Array.map (fun (plan, _) -> {
                    RowIndex = rowIndex
                    Kind = RowErrorKind.MissingColumn plan.Schema.Name
                })

            if missing.Length > 0 && schema.MissingColumns = MissingColumnPolicy.Reject then
                Error(List.ofArray missing)
            else
                let claimed =
                    matches
                    |> Array.collect (fun (_, positions) -> List.toArray positions)
                    |> Set.ofArray

                let extras =
                    if schema.ExtraColumns = ExtraColumnPolicy.Reject then
                        headers
                        |> Array.indexed
                        |> Array.filter (fun (i, header) ->
                            not (claimed.Contains i) && not (String.IsNullOrWhiteSpace header))
                        |> Array.map (fun (_, header) -> {
                            RowIndex = rowIndex
                            Kind = RowErrorKind.ExtraColumn header
                        })
                        |> List.ofArray
                    else
                        []

                let binding = {
                    Positions = matches |> Array.map (fun (plan, positions) -> plan, List.tryHead positions)
                    ExpectedWidth = headers.Length
                }

                Ok(binding, extras)

    /// The shared validation pipeline both legs converge on.
    let private validateRows (schema: TableSchema) (source: seq<SourceRow>) : seq<RowOutcome> =
        let plans = compilePlan schema

        seq {
            let mutable binding =
                match schema.HeaderRow with
                | HeaderRowPolicy.FirstRowIsHeader -> None
                | HeaderRowPolicy.NoHeaderRow ->
                    Some {
                        Positions = plans |> Array.mapi (fun i plan -> plan, Some i)
                        ExpectedWidth = plans.Length
                    }

            let mutable stopped = false
            use rows = source.GetEnumerator()

            while not stopped && rows.MoveNext() do
                match rows.Current with
                | SourceRow.Fatal message ->
                    RowOutcome.Structural {
                        RowIndex = 0
                        Kind = RowErrorKind.UnparseableRow message
                    }

                    stopped <- true
                | SourceRow.Broken(rowIndex, message) ->
                    RowOutcome.Structural {
                        RowIndex = rowIndex
                        Kind = RowErrorKind.UnparseableRow message
                    }

                    // A broken header makes everything after it
                    // unbindable; a broken data row costs only
                    // itself.
                    if binding.IsNone then
                        stopped <- true
                | SourceRow.Data(rowIndex, cells) ->
                    // Fully-empty rows (blank CSV lines, stray
                    // formatted-but-empty XLSX rows) are skipped:
                    // they are not data, and reporting them against
                    // Required columns would flood the report.
                    if cells |> Array.forall isEmptyCell then
                        ()
                    else
                        match binding with
                        | Some plan -> validateDataRow schema plan rowIndex cells
                        | None ->
                            match bindHeader schema plans rowIndex cells with
                            | Error structural ->
                                for error in structural do
                                    RowOutcome.Structural error

                                stopped <- true
                            | Ok(plan, extras) ->
                                for error in extras do
                                    RowOutcome.Structural error

                                binding <- Some plan
        }

    // ── Materialisation ─────────────────────────────────────────

    let private materialise
        (binder: RowBinder<'Row>)
        (maxErrors: int option)
        (outcomes: seq<RowOutcome>)
        : TabularReadResult<'Row> =
        let rows = ResizeArray<'Row>()
        let cellErrors = ResizeArray<CellError>()
        let rowErrors = ResizeArray<RowError>()
        let mutable totalRows = 0
        let mutable truncated = false

        let capReached () =
            match maxErrors with
            | Some cap -> cellErrors.Count + rowErrors.Count >= cap
            | None -> false

        use enumerator = outcomes.GetEnumerator()

        while not truncated && enumerator.MoveNext() do
            match enumerator.Current with
            | RowOutcome.Bound(rowIndex, row) ->
                totalRows <- totalRows + 1

                match binder row with
                | Ok bound -> rows.Add bound
                | Error errors ->
                    // The reader owns RowIndex — binders may leave
                    // it 0 (documented on RowBinder).
                    for error in errors do
                        cellErrors.Add { error with RowIndex = rowIndex }
            | RowOutcome.Invalid errors ->
                totalRows <- totalRows + 1
                cellErrors.AddRange errors
            | RowOutcome.Structural error ->
                match error.Kind with
                | RowErrorKind.ArityMismatch _
                | RowErrorKind.UnparseableRow _ -> totalRows <- totalRows + 1
                | _ -> ()

                rowErrors.Add error

            if capReached () then
                truncated <- true

        {
            Rows = List.ofSeq rows
            CellErrors = List.ofSeq cellErrors
            RowErrors = List.ofSeq rowErrors
            TotalRows = totalRows
            Truncated = truncated
        }

    // ── Public surface ──────────────────────────────────────────

    /// Stream per-row outcomes from CSV. Lazy and row-at-a-time;
    /// the `MaxErrors` cap does not apply (it bounds materialised
    /// reports, not streams). The stream stays open for the
    /// caller to dispose.
    let streamCsv (schema: TableSchema) (options: CsvReadOptions) (stream: Stream) : seq<RowOutcome> = seq {
        use reader = Csv.openReader stream
        yield! validateRows schema (csvSource options.Delimiter reader)
    }

    /// Stream per-row outcomes from XLSX. Same contract as
    /// `streamCsv`.
    let streamXlsx (schema: TableSchema) (options: XlsxReadOptions) (stream: Stream) : seq<RowOutcome> =
        validateRows schema (xlsxSource options.Sheet stream)

    /// Read + validate CSV, mapping each bound row through the
    /// consumer's binder (Phase 123 binder seam) so domain
    /// records come out without re-parsing.
    let readCsvWith
        (binder: RowBinder<'Row>)
        (schema: TableSchema)
        (options: CsvReadOptions)
        (stream: Stream)
        : TabularReadResult<'Row> =
        streamCsv schema options stream |> materialise binder options.MaxErrors

    /// Read + validate CSV into schema-driven
    /// `Map<string, TabularValue>` rows.
    let readCsv
        (schema: TableSchema)
        (options: CsvReadOptions)
        (stream: Stream)
        : TabularReadResult<Map<string, TabularValue>> =
        readCsvWith Ok schema options stream

    /// `readCsvWith` over an in-memory byte payload (e.g. an
    /// upload already pulled from blob storage).
    let readCsvBytesWith
        (binder: RowBinder<'Row>)
        (schema: TableSchema)
        (options: CsvReadOptions)
        (bytes: byte[])
        : TabularReadResult<'Row> =
        use stream = new MemoryStream(bytes)
        readCsvWith binder schema options stream

    /// `readCsv` over an in-memory byte payload.
    let readCsvBytes
        (schema: TableSchema)
        (options: CsvReadOptions)
        (bytes: byte[])
        : TabularReadResult<Map<string, TabularValue>> =
        readCsvBytesWith Ok schema options bytes

    /// Read + validate XLSX, mapping each bound row through the
    /// consumer's binder.
    let readXlsxWith
        (binder: RowBinder<'Row>)
        (schema: TableSchema)
        (options: XlsxReadOptions)
        (stream: Stream)
        : TabularReadResult<'Row> =
        streamXlsx schema options stream |> materialise binder options.MaxErrors

    /// Read + validate XLSX into schema-driven
    /// `Map<string, TabularValue>` rows.
    let readXlsx
        (schema: TableSchema)
        (options: XlsxReadOptions)
        (stream: Stream)
        : TabularReadResult<Map<string, TabularValue>> =
        readXlsxWith Ok schema options stream

    /// `readXlsxWith` over an in-memory byte payload.
    let readXlsxBytesWith
        (binder: RowBinder<'Row>)
        (schema: TableSchema)
        (options: XlsxReadOptions)
        (bytes: byte[])
        : TabularReadResult<'Row> =
        use stream = new MemoryStream(bytes)
        readXlsxWith binder schema options stream

    /// `readXlsx` over an in-memory byte payload.
    let readXlsxBytes
        (schema: TableSchema)
        (options: XlsxReadOptions)
        (bytes: byte[])
        : TabularReadResult<Map<string, TabularValue>> =
        readXlsxBytesWith Ok schema options bytes