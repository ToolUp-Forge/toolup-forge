// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.DataSourceFidelityContract

open System
open System.Globalization
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open DataManagementTypes

// ─── IDataSource null-and-type fidelity contract pack (Phase 836) ───
//
// `IDataSourceContract` proves a connector answers its five members.
// This pack proves it answers them WITHOUT LOSING anything: that the
// bytes `Query` emits say what the source held, and that `GetSchema`
// tells the truth about those bytes. Every property here was once
// broken silently in a shipped connector — a NULL rendered as the empty
// string reads back as a perfectly good empty string, and nothing
// downstream can tell — which is why the bar is a pack rather than a
// review habit: the next connector is written by someone who never saw
// the bug.
//
// **The fixture.** A binding seeds its backend with `Fixture.standard`
// (a small table whose rows are the cases that have actually gone wrong:
// a NULL, an empty string, a bare hyphen, a quoted comma, and one wide
// row carrying a long value with an embedded quote, comma and line
// break), then hands the pack a `FidelityTarget` addressing it. How it
// seeds is the binding's business — DDL against a local database, a file
// in the connector's own format, a recorded result set pushed through
// the connector's own cell renderer — because the pack cannot know a
// backend's write path, only what a faithful read of it must look like.
//
// **The laws** (each one a named test; `Law` holds the names):
//
//   - the payload IS the format the connector declares (Phase 834) —
//     for `Csv`, strict UTF-8 without a BOM, well-formed RFC 4180, and
//     rectangular; for `Json`, a parseable document; any other format
//     is refused as unverifiable rather than passed on trust;
//   - NULL and the empty string in the same column read back as
//     DISTINGUISHABLE values (Phase 833's convention: an unquoted empty
//     field is NULL, a quoted `""` is the empty string);
//   - a hyphen is a value, a quoted comma stays in its field, the wide
//     row is intact, and every fixture cell round-trips;
//   - `GetSchema` names every emitted column, and its `Nullable` agrees
//     with what `Query` actually emits for that column;
//   - no emitted value contradicts its column's declared type;
//   - where the connector's schema is DECLARED by the source (a catalogue,
//     a file footer) rather than inferred from sampled text, its
//     nullability and coarse type are the source's own.
//
// **The payload is read with the pack's own parser**, never the
// connector family's: a round trip read back through the writer's own
// reader agrees with itself by construction and proves nothing.
//
// **A source that cannot STATE an empty string** (a user-uploaded CSV or
// workbook, where a blank cell is all the file says) binds with
// `EmptyString = CannotState reason`: the pack then requires the blank to
// read as NULL — never as an asserted empty string — and says why in the
// test name, so the concession is visible in every run.
//
// **The go-red.** `selfTests` binds the honest fixture source AND a set of
// deliberately lossy ones, each of which must be refused by the law it
// breaks, by name. Without them the pack would only prove that the
// connectors it already agrees with agree with it.
//
// A connector that cannot be bound records WHY with `unbound`, in its
// binding list, rather than being quietly absent.
//
// This pack lives in `ToolUp.Platform.Tests` beside the others and is
// source-linked into `ToolUp.DataSources.Tests`, where the shipped
// connectors bind it — the arrangement `IDeclaresPayloadFormatContract`
// uses. It depends on nothing beyond `ToolUp.Platform`, Expecto and the
// BCL, so an external connector author can link it the same way.

// ─── The fixture (836.B) ──────────────────────────────────────────

/// One cell as the source holds it.
[<RequireQualifiedAccess>]
type Cell =
    /// The source holds NULL.
    | Null
    /// The source holds this text (possibly empty).
    | Text of string

/// One fixture column: its name, its coarse type, and whether the
/// source declares it nullable.
type FixtureColumn = {
    Name: string
    Type: ColumnType
    Nullable: bool
}

/// One fixture row, named for the case it carries so a failure names it.
type FixtureRow = { Case: string; Cells: Cell list }

/// The table a binding seeds into its backend.
type Fixture = {
    /// The table name a binding should seed it under.
    Table: string
    Columns: FixtureColumn list
    Rows: FixtureRow list
}

module Fixture =

    /// The long value in the wide row: over two thousand characters
    /// ending in an embedded quote, a comma and a line break — every
    /// character RFC 4180 has to quote, in one field. The break is a
    /// bare `\n` deliberately: XML-backed formats normalise `\r\n` on
    /// read, which is the container's rule, not a connector's loss.
    let wideText =
        String.replicate 400 "wide " + "\"quoted\", with a comma\nand a second line"

    /// The standard fidelity table. Column 0 (`id`) is the row key: the
    /// pack matches emitted rows to fixture rows by it, so a backend that
    /// returns rows in its own order is not penalised for it.
    let standard: Fixture = {
        Table = "fidelity"
        Columns = [
            {
                Name = "id"
                Type = NumberColumn
                Nullable = false
            }
            {
                Name = "label"
                Type = StringColumn
                Nullable = true
            }
            {
                Name = "amount"
                Type = NumberColumn
                Nullable = true
            }
            {
                Name = "observed_on"
                Type = DateColumn
                Nullable = true
            }
        ]
        Rows = [
            {
                Case = "plain"
                Cells = [ Cell.Text "1"; Cell.Text "plain"; Cell.Text "12.5"; Cell.Text "2026-09-26" ]
            }
            {
                Case = "null"
                Cells = [ Cell.Text "2"; Cell.Null; Cell.Null; Cell.Null ]
            }
            {
                Case = "empty"
                Cells = [ Cell.Text "3"; Cell.Text ""; Cell.Text "0"; Cell.Text "2026-01-01" ]
            }
            {
                Case = "hyphen"
                Cells = [ Cell.Text "4"; Cell.Text "-"; Cell.Text "-4.25"; Cell.Text "2025-12-31" ]
            }
            {
                Case = "quoted-comma"
                Cells = [ Cell.Text "5"; Cell.Text "Smith, J."; Cell.Text "7"; Cell.Null ]
            }
            {
                Case = "wide"
                Cells = [
                    Cell.Text "6"
                    Cell.Text wideText
                    Cell.Text "123456789.125"
                    Cell.Text "1999-12-31"
                ]
            }
        ]
    }

    /// The column names, in order.
    let header (fixture: Fixture) = fixture.Columns |> List.map _.Name

    /// A cell's text, with NULL as `null` — the shape a binding's writer
    /// usually wants (`null` for "write nothing here").
    let textOrNull (cell: Cell) : string =
        match cell with
        | Cell.Null -> null
        | Cell.Text text -> text

// ─── Reading a payload — the pack's own RFC 4180 reader ───────────

/// One parsed CSV field: its text and whether it was quoted, which is
/// the whole of what the null convention needs.
type CsvField = { Text: string; Quoted: bool }

/// Parse RFC 4180 CSV into records. Strict: an unterminated quote or
/// text after a closing quote is an error, not a guess. Accepts `\r\n`
/// and `\n` terminators; a final terminator does not start a record.
let parseCsv (text: string) : Result<CsvField list list, string> =
    let records = ResizeArray<CsvField list>()
    let fields = ResizeArray<CsvField>()
    let field = StringBuilder()
    let mutable quoted = false
    let mutable i = 0
    let mutable error = None
    let mutable recordOpen = false

    let endField () =
        fields.Add {
            Text = field.ToString()
            Quoted = quoted
        }

        field.Clear() |> ignore
        quoted <- false

    let endRecord () =
        endField ()
        records.Add(List.ofSeq fields)
        fields.Clear()
        recordOpen <- false

    while error.IsNone && i < text.Length do
        let c = text[i]

        if c = '"' && field.Length = 0 && not quoted then
            // A quoted field: read to the closing quote, un-doubling.
            quoted <- true
            recordOpen <- true
            i <- i + 1
            let mutable closed = false

            while error.IsNone && not closed do
                if i >= text.Length then
                    error <- Some $"unterminated quoted field in record %d{records.Count + 1}"
                elif text[i] = '"' then
                    if i + 1 < text.Length && text[i + 1] = '"' then
                        field.Append '"' |> ignore
                        i <- i + 2
                    else
                        closed <- true
                        i <- i + 1
                else
                    field.Append text[i] |> ignore
                    i <- i + 1

            if error.IsNone && i < text.Length then
                match text[i] with
                | ','
                | '\r'
                | '\n' -> ()
                | other -> error <- Some $"text ('%c{other}') after a closing quote in record %d{records.Count + 1}"
        elif c = '"' then
            error <- Some $"a bare quote inside an unquoted field in record %d{records.Count + 1}"
        elif c = ',' then
            endField ()
            recordOpen <- true
            i <- i + 1
        elif c = '\r' && i + 1 < text.Length && text[i + 1] = '\n' then
            endRecord ()
            i <- i + 2
        elif c = '\n' then
            endRecord ()
            i <- i + 1
        else
            field.Append c |> ignore
            recordOpen <- true
            i <- i + 1

    match error with
    | Some message -> Error message
    | None ->
        if recordOpen || field.Length > 0 || fields.Count > 0 then
            endRecord ()

        Ok(List.ofSeq records)

/// One field read under the null convention: unquoted empty is NULL, a
/// quoted empty is the empty string, anything else is its text.
let cellOfField (field: CsvField) : Cell =
    if field.Text.Length = 0 && not field.Quoted then
        Cell.Null
    else
        Cell.Text field.Text

// ─── The binding surface ──────────────────────────────────────────

/// Where a connector's `GetSchema` answer comes from.
[<RequireQualifiedAccess>]
type SchemaEvidence =
    /// The source states nullability and type (a catalogue, a file
    /// footer): the pack holds the schema to the fixture's own.
    | Declared
    /// The connector infers the schema from sampled text: the pack holds
    /// it only to consistency with the payload.
    | Inferred

/// Whether the source can state an empty string at all.
[<RequireQualifiedAccess>]
type EmptyStringSupport =
    /// The source distinguishes NULL from the empty string, so the
    /// payload must.
    | Distinguishes
    /// The source cannot state an empty string — a blank is all it
    /// holds. The payload must then read the blank as NULL, never as an
    /// asserted empty string. The reason is shown in the test name.
    | CannotState of reason: string

/// Everything a binding supplies. The factory that returns it must
/// already have seeded the backend with `Fixture.standard`.
type FidelityTarget = {
    /// The connector under test.
    Source: IDataSource
    /// The call context addressing the seeded fixture.
    Context: DataSourceCallContext
    /// The name `GetSchema` is given for the fixture table.
    Table: string
    /// What `Query` is given to read the whole fixture table.
    Statement: string
    /// The connector's own projection of a `ColumnInfo.DataType` onto
    /// the coarse `ColumnType` (each connector ships one).
    Classify: string -> ColumnType
    /// Whether the schema is declared by the source or inferred.
    Schema: SchemaEvidence
    /// Whether the source can state an empty string.
    EmptyString: EmptyStringSupport
}

/// The law names — the go-red asserts refusals BY these names.
module Law =
    [<Literal>]
    let PayloadIsDeclaredFormat = "the payload is the format the connector declares"

    [<Literal>]
    let NullAndEmptyDistinct = "NULL and the empty string are distinguishable"

    [<Literal>]
    let HyphenIsAValue = "a hyphen is a value, not a NULL"

    [<Literal>]
    let QuotedCommaIsOneField = "a quoted comma stays inside one field"

    [<Literal>]
    let WideRowIntact = "the wide row survives intact"

    [<Literal>]
    let EveryCellRoundTrips = "every fixture cell round-trips"

    [<Literal>]
    let SchemaNamesTheColumns = "GetSchema names every emitted column"

    [<Literal>]
    let NullabilityAgrees = "GetSchema's Nullable agrees with what Query emits"

    [<Literal>]
    let DeclaredNullabilityIsTheSources = "a declared nullability is the source's own"

    [<Literal>]
    let TypesNotContradicted = "no emitted value contradicts its column's declared type"

    [<Literal>]
    let DeclaredTypeIsTheSources = "a declared type is the source's own"

    let all = [
        PayloadIsDeclaredFormat
        NullAndEmptyDistinct
        HyphenIsAValue
        QuotedCommaIsOneField
        WideRowIntact
        EveryCellRoundTrips
        SchemaNamesTheColumns
        NullabilityAgrees
        DeclaredNullabilityIsTheSources
        TypesNotContradicted
        DeclaredTypeIsTheSources
    ]

// ─── Observation ──────────────────────────────────────────────────

/// What one read of a target produced.
type Observation = {
    Declared: PayloadFormat
    Schema: Result<TableSchema, string>
    Bytes: Result<byte[], string>
    /// The payload decoded to header + rows, or why it could not be.
    Table: Result<string list * Cell list list, string>
}

let private strictUtf8 = UTF8Encoding(false, true)

let private decode (format: PayloadFormat) (bytes: byte[]) : Result<string list * Cell list list, string> =
    match format with
    | PayloadFormat.Csv ->
        let text =
            try
                Ok(strictUtf8.GetString bytes)
            with ex ->
                Error $"the payload is not valid UTF-8: %s{ex.Message}"

        text
        |> Result.bind parseCsv
        |> Result.bind (fun records ->
            match records with
            | [] -> Error "the payload has no header record"
            | header :: rows -> Ok(header |> List.map _.Text, rows |> List.map (List.map cellOfField)))
    | other ->
        Error
            $"the pack reads cells from Csv payloads only; a connector declaring '%s{PayloadFormat.token other}' binds with a decoder of its own"

/// Read the target once: its declaration, its schema and its payload.
let observe (target: FidelityTarget) : Async<Observation> = async {
    let declared = PayloadFormat.declaredBy target.Source

    let! schema = target.Source.GetSchema(target.Context, target.Table)
    let! bytes = target.Source.Query(target.Context, target.Statement)

    let bytes = bytes |> Result.mapError (sprintf "Query failed: %A")

    return {
        Declared = declared
        Schema = schema |> Result.mapError (sprintf "GetSchema failed: %A")
        Bytes = bytes
        Table = bytes |> Result.bind (decode declared)
    }
}

// ─── Value comparison ─────────────────────────────────────────────

let private invariant = CultureInfo.InvariantCulture

let private tryNumber (text: string) =
    match Double.TryParse(text, NumberStyles.Float, invariant) with
    | true, value -> Some value
    | false, _ -> None

let private tryInstant (text: string) =
    match
        DateTimeOffset.TryParse(text, invariant, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal)
    with
    | true, value -> Some value.UtcDateTime
    | false, _ -> None

let private tryBoolean (text: string) =
    match text.Trim().ToLowerInvariant() with
    | "true"
    | "1" -> Some true
    | "false"
    | "0" -> Some false
    | _ -> None

/// Does `text` read as a value of `columnType`? `DateColumn` also
/// admits a time-of-day or an interval, which the coarse type folds in.
let conformsTo (columnType: ColumnType) (text: string) : bool =
    match columnType with
    | StringColumn -> true
    | NumberColumn -> (tryNumber text).IsSome
    | BooleanColumn -> (tryBoolean text).IsSome
    | DateColumn -> (tryInstant text).IsSome || fst (TimeSpan.TryParse(text, invariant))

/// Are two cells the same value under the column's type? Text compares
/// exactly; a number, date or boolean compares by value, so a backend
/// rendering `12.5` as `12.50` or a date as a round-trip timestamp is
/// not a loss.
let sameValue (columnType: ColumnType) (expected: Cell) (actual: Cell) : bool =
    match expected, actual with
    | Cell.Null, Cell.Null -> true
    | Cell.Text e, Cell.Text a ->
        e = a
        || (match columnType with
            | StringColumn -> false
            | NumberColumn ->
                match tryNumber e, tryNumber a with
                | Some x, Some y -> x = y
                | _ -> false
            | DateColumn ->
                match tryInstant e, tryInstant a with
                | Some x, Some y -> x = y
                | _ -> false
            | BooleanColumn ->
                match tryBoolean e, tryBoolean a with
                | Some x, Some y -> x = y
                | _ -> false)
    | _ -> false

let private show (cell: Cell) =
    match cell with
    | Cell.Null -> "NULL"
    | Cell.Text text when text.Length > 40 -> $"\"%s{text.Substring(0, 40)}…\" (%d{text.Length} chars)"
    | Cell.Text text -> $"\"%s{text}\""

// ─── The laws (836.A) ─────────────────────────────────────────────

/// The fixture as a binding with `support` must emit it: a source that
/// cannot state an empty string emits its blanks as NULL.
let expectedFixture (support: EmptyStringSupport) : Fixture =
    match support with
    | EmptyStringSupport.Distinguishes -> Fixture.standard
    | EmptyStringSupport.CannotState _ -> {
        Fixture.standard with
            Rows =
                Fixture.standard.Rows
                |> List.map (fun row -> {
                    row with
                        Cells =
                            row.Cells
                            |> List.map (fun cell ->
                                match cell with
                                | Cell.Text "" -> Cell.Null
                                | other -> other)
                })
      }

let private sameName (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// Match emitted rows to fixture rows by the key column. Column names
/// compare case-insensitively: a warehouse that folds unquoted
/// identifiers to upper case has lost nothing.
let private aligned (fixture: Fixture) (header: string list) (rows: Cell list list) =
    let fixtureHeader = Fixture.header fixture

    let columnMap =
        fixtureHeader
        |> List.map (fun name -> header |> List.tryFindIndex (sameName name))

    if columnMap |> List.exists Option.isNone then
        Error $"the emitted header %A{header} does not carry every fixture column %A{fixtureHeader}"
    else
        let indices = columnMap |> List.map Option.get

        let reorder (row: Cell list) =
            indices |> List.map (fun i -> if i < row.Length then row[i] else Cell.Null)

        let keyOf (cells: Cell list) =
            match List.head cells with
            | Cell.Text text -> tryNumber text
            | Cell.Null -> None

        let emitted = rows |> List.map reorder

        let pairs =
            fixture.Rows
            |> List.map (fun fixtureRow ->
                let key = keyOf fixtureRow.Cells

                fixtureRow, emitted |> List.tryFind (fun cells -> key.IsSome && keyOf cells = key))

        Ok(pairs, emitted.Length)

let private withTable (observation: Observation) (fixture: Fixture) check =
    match observation.Table with
    | Error why -> [ why ]
    | Ok(header, rows) ->
        match aligned fixture header rows with
        | Error why -> [ why ]
        | Ok(pairs, emittedCount) -> check pairs emittedCount

let private cellIn (fixture: Fixture) (caseName: string) (column: string) pairs =
    let columnIndex = fixture.Columns |> List.findIndex (fun c -> c.Name = column)

    match pairs |> List.tryFind (fun (row: FixtureRow, _) -> row.Case = caseName) with
    | Some(row, Some(cells: Cell list)) -> Ok(row.Cells[columnIndex], cells[columnIndex])
    | Some(_, None) -> Error $"the '%s{caseName}' row was not emitted"
    | None -> Error $"the fixture has no '%s{caseName}' row"

let private expectCell fixture caseName column pairs what =
    match cellIn fixture caseName column pairs with
    | Error why -> [ why ]
    | Ok(expected, actual) ->
        let columnType = (fixture.Columns |> List.find (fun c -> c.Name = column)).Type

        if sameValue columnType expected actual then
            []
        else
            [
                $"%s{what}: '%s{column}' in the '%s{caseName}' row expected %s{show expected}, read %s{show actual}"
            ]

let private schemaColumn (schema: TableSchema) (name: string) =
    schema.Columns |> List.tryFind (fun c -> sameName c.Name name)

/// Evaluate one law over an observation. An empty list is a pass.
let evaluate (target: FidelityTarget) (observation: Observation) (law: string) : string list =
    let fixture = expectedFixture target.EmptyString

    match law with
    | Law.PayloadIsDeclaredFormat ->
        match observation.Bytes with
        | Error why -> [ why ]
        | Ok bytes ->
            match observation.Declared with
            | PayloadFormat.Csv -> [
                if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
                    "the Csv payload starts with a UTF-8 BOM, which reads as a stray character in the first header cell"
                match observation.Table with
                | Error why -> $"declares Csv, but the bytes are not well-formed Csv: %s{why}"
                | Ok(header, rows) ->
                    for index, row in List.indexed rows do
                        if row.Length <> header.Length then
                            $"declares Csv, but record %d{index + 2} has %d{row.Length} fields against a %d{header.Length}-field header"
              ]
            | PayloadFormat.Json ->
                try
                    use _ = JsonDocument.Parse(ReadOnlyMemory bytes)
                    []
                with ex -> [ $"declares Json, but the bytes do not parse as JSON: %s{ex.Message}" ]
            | other -> [
                $"declares '%s{PayloadFormat.token other}', which this pack cannot verify against the bytes — it would pass on trust"
              ]

    | Law.NullAndEmptyDistinct ->
        withTable observation fixture (fun pairs _ ->
            match target.EmptyString with
            | EmptyStringSupport.Distinguishes ->
                match cellIn fixture "null" "label" pairs, cellIn fixture "empty" "label" pairs with
                | Ok(_, nullRead), Ok(_, emptyRead) -> [
                    if nullRead = emptyRead then
                        $"NULL and the empty string both read as %s{show nullRead} — the payload cannot tell them apart"
                    if nullRead <> Cell.Null then
                        $"the NULL reads as %s{show nullRead}, not as NULL"
                    if emptyRead <> Cell.Text "" then
                        $"the empty string reads as %s{show emptyRead}, not as the empty string"
                  ]
                | Error why, _
                | _, Error why -> [ why ]
            | EmptyStringSupport.CannotState _ ->
                // The source's blank must read as NULL — an asserted
                // empty string is a claim the source never made.
                expectCell fixture "null" "label" pairs "a blank the source cannot qualify"
                @ expectCell fixture "empty" "label" pairs "a blank the source cannot qualify")

    | Law.HyphenIsAValue ->
        withTable observation fixture (fun pairs _ ->
            expectCell fixture "hyphen" "label" pairs "a hyphen"
            @ expectCell fixture "hyphen" "amount" pairs "a negative number")

    | Law.QuotedCommaIsOneField ->
        withTable observation fixture (fun pairs _ ->
            expectCell fixture "quoted-comma" "label" pairs "a quoted comma"
            @ expectCell fixture "quoted-comma" "amount" pairs "the field after a quoted comma")

    | Law.WideRowIntact ->
        withTable observation fixture (fun pairs _ ->
            fixture.Columns
            |> List.collect (fun column -> expectCell fixture "wide" column.Name pairs "the wide row"))

    | Law.EveryCellRoundTrips ->
        withTable observation fixture (fun pairs emittedCount -> [
            if emittedCount <> fixture.Rows.Length then
                $"emitted %d{emittedCount} rows for a %d{fixture.Rows.Length}-row fixture"
            for row, _ in pairs do
                for column in fixture.Columns do
                    yield! expectCell fixture row.Case column.Name pairs "a cell"
        ])

    | Law.SchemaNamesTheColumns ->
        match observation.Schema, observation.Table with
        | Error why, _
        | _, Error why -> [ why ]
        | Ok schema, Ok(header, _) -> [
            if schema.Columns.IsEmpty then
                "GetSchema publishes no columns — a connector with no schema cannot state nullability or type, so it cannot meet this bar"
            else
                for name in header do
                    if (schemaColumn schema name).IsNone then
                        $"Query emits column '%s{name}', which GetSchema does not name"
          ]

    | Law.NullabilityAgrees ->
        match observation.Schema with
        | Error why -> [ why ]
        | Ok schema ->
            withTable observation fixture (fun pairs _ -> [
                for columnIndex, column in List.indexed fixture.Columns do
                    match schemaColumn schema column.Name with
                    | Some info when not info.Nullable ->
                        for row, emitted in pairs do
                            match emitted with
                            | Some cells when cells[columnIndex] = Cell.Null ->
                                $"GetSchema declares '%s{column.Name}' NOT NULL, but Query emits NULL for it in the '%s{row.Case}' row"
                            | _ -> ()
                    | _ -> ()
            ])

    | Law.DeclaredNullabilityIsTheSources ->
        match target.Schema, observation.Schema with
        | SchemaEvidence.Inferred, _ -> []
        | SchemaEvidence.Declared, Error why -> [ why ]
        | SchemaEvidence.Declared, Ok schema -> [
            for column in fixture.Columns do
                match schemaColumn schema column.Name with
                | None -> $"GetSchema does not name '%s{column.Name}'"
                | Some info when info.Nullable <> column.Nullable ->
                    $"the source declares '%s{column.Name}' nullable=%b{column.Nullable}; GetSchema says nullable=%b{info.Nullable}"
                | Some _ -> ()
          ]

    | Law.TypesNotContradicted ->
        match observation.Schema with
        | Error why -> [ why ]
        | Ok schema ->
            withTable observation fixture (fun pairs _ -> [
                for columnIndex, column in List.indexed fixture.Columns do
                    match schemaColumn schema column.Name with
                    | None -> ()
                    | Some info ->
                        let declared = target.Classify info.DataType

                        for row, emitted in pairs do
                            match emitted with
                            | Some cells ->
                                match cells[columnIndex] with
                                | Cell.Text text when not (conformsTo declared text) ->
                                    $"'%s{column.Name}' is declared '%s{info.DataType}' (%A{declared}), but the '%s{row.Case}' row emits %s{show (Cell.Text text)}"
                                | _ -> ()
                            | None -> ()
            ])

    | Law.DeclaredTypeIsTheSources ->
        match target.Schema, observation.Schema with
        | SchemaEvidence.Inferred, _ -> []
        | SchemaEvidence.Declared, Error why -> [ why ]
        | SchemaEvidence.Declared, Ok schema -> [
            for column in fixture.Columns do
                match schemaColumn schema column.Name with
                | None -> $"GetSchema does not name '%s{column.Name}'"
                | Some info ->
                    let declared = target.Classify info.DataType

                    if declared <> column.Type then
                        $"the source declares '%s{column.Name}' as %A{column.Type}; GetSchema's '%s{info.DataType}' classifies as %A{declared}"
          ]

    | other -> [ $"no such law: '%s{other}'" ]

/// Every law the target breaks, with its violations. Empty = passes.
let refusals (target: FidelityTarget) : Async<(string * string list) list> = async {
    let! observation = observe target

    return
        Law.all
        |> List.map (fun law -> law, evaluate target observation law)
        |> List.filter (fun (_, violations) -> not violations.IsEmpty)
}

// ─── The pack entry point ─────────────────────────────────────────

/// The pack. `factory` is called fresh per law and must return a target
/// whose backend already holds `Fixture.standard`.
let tests (name: string) (factory: unit -> FidelityTarget) =
    let concession =
        match (factory ()).EmptyString with
        | EmptyStringSupport.Distinguishes -> ""
        | EmptyStringSupport.CannotState reason -> $" (the source cannot state an empty string: %s{reason})"

    testList $"{name} — IDataSource fidelity contract" [
        for law in Law.all do
            let title =
                if law = Law.NullAndEmptyDistinct then
                    law + concession
                else
                    law

            testCaseAsync title
            <| async {
                let target = factory ()
                let! observation = observe target

                match evaluate target observation law with
                | [] -> ()
                | violations ->
                    failtest (
                        $"%s{name} ('%s{target.Source.Kind}') breaks '%s{law}':\n  - "
                        + String.Join("\n  - ", violations)
                    )
            }
    ]

/// Record a connector that cannot be bound, and why — shown as a
/// pending case so the gap is visible in every run, never an absence.
let unbound (name: string) (reason: string) =
    if String.IsNullOrWhiteSpace reason then
        test $"{name} — IDataSource fidelity contract" {
            failtest "an unbound connector must say why it cannot be bound"
        }
    else
        ptestCase $"{name} — IDataSource fidelity contract: NOT BOUND — %s{reason}" ignore

// ─── The fixture source and its lossy twins (836.B, 836.D) ────────

/// The pack's own RFC 4180 writer under the null convention: NULL is
/// the unquoted empty field, the empty string is `""`.
let renderFaithful (cell: Cell) : string =
    match cell with
    | Cell.Null -> ""
    | Cell.Text "" -> "\"\""
    | Cell.Text text when
        text.Contains ','
        || text.Contains '"'
        || text.Contains '\n'
        || text.Contains '\r'
        ->
        "\"" + text.Replace("\"", "\"\"") + "\""
    | Cell.Text text -> text

/// How a fixture source misbehaves — `Honest` is the reference.
[<RequireQualifiedAccess>]
type Defect =
    /// No defect: the reference connector every law accepts.
    | Honest
    /// Renders NULL as the empty string (`""`) — the loss this pack exists for.
    | NullAsEmptyString
    /// Writes fields raw, never quoting — a comma splits its field.
    | NeverQuotes
    /// Declares `label` NOT NULL while emitting a NULL in it.
    | LiesAboutNullability
    /// Declares `label` an integer while emitting text in it.
    | LiesAboutType
    /// Declares `Json` while emitting Csv.
    | LiesAboutFormat

let private nativeNameOf (columnType: ColumnType) =
    match columnType with
    | StringColumn -> "text"
    | NumberColumn -> "double"
    | DateColumn -> "date"
    | BooleanColumn -> "boolean"

/// The fixture source's own `DataType` projection.
let classifyFixtureType (dataType: string) : ColumnType =
    match dataType with
    | "double"
    | "integer" -> NumberColumn
    | "date" -> DateColumn
    | "boolean" -> BooleanColumn
    | _ -> StringColumn

/// A connector-shaped test double emitting `Fixture.standard`, honestly
/// or with one `Defect`. Everything is in-process: `Connect` is a no-op,
/// the fixture table is the only table, and `Query` answers only its name.
type FixtureSource(defect: Defect) =
    let fixture = Fixture.standard

    let render (cell: Cell) =
        match defect, cell with
        | Defect.NullAsEmptyString, Cell.Null -> "\"\""
        | Defect.NeverQuotes, Cell.Null -> ""
        | Defect.NeverQuotes, Cell.Text text -> text
        | _ -> renderFaithful cell

    let payload () =
        let lines =
            (Fixture.header fixture |> String.concat ",")
            :: (fixture.Rows
                |> List.map (fun row -> row.Cells |> List.map render |> String.concat ","))

        lines
        |> List.map (fun line -> line + "\r\n")
        |> String.concat ""
        |> Encoding.UTF8.GetBytes

    let schema () : TableSchema = {
        TableName = fixture.Table
        Columns =
            fixture.Columns
            |> List.map (fun column ->
                let nullable =
                    match defect with
                    | Defect.LiesAboutNullability when column.Name = "label" -> false
                    | _ -> column.Nullable

                let dataType =
                    match defect with
                    | Defect.LiesAboutType when column.Name = "label" -> "integer"
                    | _ -> nativeNameOf column.Type

                {
                    Name = column.Name
                    DataType = dataType
                    Nullable = nullable
                })
    }

    let kind =
        match defect with
        | Defect.Honest -> "FidelityFixture"
        | other -> $"FidelityFixture.%A{other}"

    let missing (table: string) =
        Error(SchemaMismatch $"%s{kind}: no table '%s{table}'")

    interface IDataSource with
        member _.Kind = kind
        member _.Connect _ = async { return Ok() }
        member _.ListTables _ = async { return Ok [ fixture.Table ] }

        member _.GetSchema(_, table) = async {
            return
                if table = fixture.Table then
                    Ok(schema ())
                else
                    missing table
        }

        member _.Query(_, sql) = async { return if sql = fixture.Table then Ok(payload ()) else missing sql }

    interface IDeclaresPayloadFormat with
        member _.PayloadFormat =
            match defect with
            | Defect.LiesAboutFormat -> PayloadFormat.Json
            | _ -> PayloadFormat.Csv

/// A target over a `FixtureSource` with the given defect.
let fixtureTarget (defect: Defect) () : FidelityTarget = {
    Source = FixtureSource defect :> IDataSource
    Context = {
        ScopeId = "fidelity-scope"
        Config = {
            Id = "fidelity"
            Name = "Fidelity fixture"
            Kind = "FidelityFixture"
            ConnectionScope = Map.empty
            CredentialKey = "unused"
            Tables = None
            Tags = Map.empty
        }
        Credential = None
    }
    Table = Fixture.standard.Table
    Statement = Fixture.standard.Table
    Classify = classifyFixtureType
    Schema = SchemaEvidence.Declared
    EmptyString = EmptyStringSupport.Distinguishes
}

/// The pack's proof of itself: the honest fixture source passes every
/// law, and each lossy twin is refused by the law it breaks, by name.
let selfTests =
    let refusedBy (defect: Defect) (law: string) =
        testCaseAsync $"%A{defect} is refused by '%s{law}'"
        <| async {
            let! refused = refusals (fixtureTarget defect ())
            let names = refused |> List.map fst
            Expect.contains names law $"the pack must refuse the %A{defect} fixture by name; it refused %A{names}"
        }

    testList "DataSourceFidelityContract — the pack proves itself" [
        tests "FixtureSource (honest)" (fixtureTarget Defect.Honest)

        testCaseAsync "the honest fixture source is refused by no law"
        <| async {
            let! refused = refusals (fixtureTarget Defect.Honest ())
            Expect.isEmpty refused "the reference connector passes every law"
        }

        // 836.D — the loss this pack exists to catch, refused by name.
        refusedBy Defect.NullAsEmptyString Law.NullAndEmptyDistinct
        refusedBy Defect.NullAsEmptyString Law.EveryCellRoundTrips
        refusedBy Defect.NeverQuotes Law.QuotedCommaIsOneField
        refusedBy Defect.NeverQuotes Law.PayloadIsDeclaredFormat
        refusedBy Defect.LiesAboutNullability Law.NullabilityAgrees
        refusedBy Defect.LiesAboutNullability Law.DeclaredNullabilityIsTheSources
        refusedBy Defect.LiesAboutType Law.TypesNotContradicted
        refusedBy Defect.LiesAboutType Law.DeclaredTypeIsTheSources
        refusedBy Defect.LiesAboutFormat Law.PayloadIsDeclaredFormat

        test "the fixture carries every case the laws need" {
            let cases = Fixture.standard.Rows |> List.map _.Case

            for case in [ "null"; "empty"; "hyphen"; "quoted-comma"; "wide" ] do
                Expect.contains cases case $"the fixture has a '%s{case}' row"

            Expect.isGreaterThan Fixture.wideText.Length 2000 "the wide value is wide"
        }

        test "the pack's reader honours quoting and the null convention" {
            match parseCsv "a,b,c\r\n,\"\",\"x, \"\"y\"\"\nz\"\r\n" with
            | Ok [ _; row ] ->
                Expect.equal
                    (row |> List.map cellOfField)
                    [ Cell.Null; Cell.Text ""; Cell.Text "x, \"y\"\nz" ]
                    "NULL, empty, and a quoted field with a comma, quotes and a break"
            | other -> failtestf "unexpected parse: %A" other
        }

        test "the pack's reader refuses malformed Csv rather than guessing" {
            Expect.isError (parseCsv "a\r\n\"open") "an unterminated quote"
            Expect.isError (parseCsv "a\r\n\"x\"y") "text after a closing quote"
            Expect.isError (parseCsv "a\r\nx\"y") "a bare quote in an unquoted field"
        }
    ]