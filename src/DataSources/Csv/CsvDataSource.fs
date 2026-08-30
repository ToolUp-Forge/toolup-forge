// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Csv.CsvDataSource

open System
open System.IO
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.DataSources.Common
open DataManagementTypes

module CsvWire = ToolUp.DataSources.Common.Csv

// ─── ToolUp.DataSources.Csv ───────────────────────────────────────
//
// `IDataSource` companion for delimited files — the ingestion shape
// most consumers meet first ("we get a daily CSV").
//
// **Production-ready, and vendor-free.** The RFC 4180 reader below is
// a single-pass character state machine over BCL types only, so this
// companion adds nothing to the OSS supply-chain surface. That is a
// deliberate choice over the two alternatives: `CsvHelper` would be a
// third-party dependency for a format this repo already demonstrates
// is a ~90-line parser, and `ToolUp.Tabular` (which carries the same
// parser) would drag `DocumentFormat.OpenXml` in behind it for the
// XLSX leg a CSV connector never reaches.
//
// **Stateless between calls (portability rule 4).** Every method
// re-reads its settings from `DataSourceCallContext.Config` and
// re-acquires the file through `IBlobStorage`; the connector holds no
// parsed file, no handle, and no cached schema. A file replaced
// between two ingestion runs is simply read again.
//
// **Files are acquired through `IBlobStorage`, never `System.IO`.**
// A deployment reading off local disk composes `LocalFileStorage`; one
// reading from S3 / Azure / GCS composes the matching companion. The
// connector cannot tell them apart, which is what keeps scope
// isolation (GP 4) and encryption-at-rest intact on the file path.
//
// **`Query` re-emits, it does not echo.** The bytes returned are
// canonical RFC 4180 — comma-delimited, UTF-8, no BOM, `\r\n`
// terminated — whatever the source file's dialect was. That is the
// uniform wire format of every connector in this family, so a module
// parsing an ingested object does not have to know which connector
// produced it.

/// Parsed, validated view of one CSV source's `ConnectionScope`.
type CsvSourceSettings = {
    /// Container / prefix / extension / sample size, shared with the
    /// other file connectors.
    File: Files.FileSourceSettings
    /// Field separator in the SOURCE file. Output is always
    /// comma-delimited.
    Delimiter: char
    /// Quote character in the SOURCE file. Output always uses `"`.
    Quote: char
    /// Does the first record carry column names? When `false` the
    /// connector synthesises `column_1` … `column_n` from the widest
    /// record it sampled.
    HasHeader: bool
    /// Text encoding of the source file. A byte-order mark, when
    /// present, always wins over this setting.
    Encoding: Encoding
}

/// The `DataSourceConfig.Kind` this connector answers to.
[<Literal>]
let Kind = "Csv"

[<Literal>]
let private DefaultExtension = ".csv"

/// Encodings addressable by name, deliberately limited to what the
/// BCL carries without `System.Text.Encoding.CodePages`. Adding that
/// package for `windows-1252` would be a vendor dependency in a
/// companion whose entire point is not having one; a deployment with
/// legacy-codepage exports transcodes on the way in instead, and the
/// refusal below names the accepted set rather than failing later
/// with mojibake nobody traces back to here.
let private encodings =
    [
        "utf-8", Encoding.UTF8
        "utf8", Encoding.UTF8
        "ascii", Encoding.ASCII
        "latin1", Encoding.Latin1
        "iso-8859-1", Encoding.Latin1
        "utf-16", Encoding.Unicode
        "utf-16le", Encoding.Unicode
        "unicode", Encoding.Unicode
        "utf-16be", Encoding.BigEndianUnicode
        "utf-32", Encoding.UTF32
    ]
    |> Map.ofList

/// Read a single-character `ConnectionScope` key, accepting the few
/// spellings an operator cannot otherwise express in a flat string
/// map — a literal tab has no representation in most admin UIs.
let private readChar (scope: Map<string, string>) (key: string) (fallback: char) : Result<char, IngestionError> =
    match ConnectionScope.optional scope key with
    | None -> Ok fallback
    | Some raw ->
        match raw.Trim().ToLowerInvariant() with
        | "tab" -> Ok '\t'
        | "\\t" -> Ok '\t'
        | "pipe" -> Ok '|'
        | "semicolon" -> Ok ';'
        | "comma" -> Ok ','
        | "space" -> Ok ' '
        | _ when raw.Length = 1 -> Ok raw[0]
        | _ ->
            Error(
                SchemaMismatch
                    $"ConnectionScope key '%s{key}' must be one character, or one of [tab, pipe, semicolon, comma, space]; got '%s{raw}'"
            )

/// Read and validate one call's `ConnectionScope`. Pure.
let readSettings (scope: Map<string, string>) : Result<CsvSourceSettings, IngestionError> =
    Files.readSettings DefaultExtension scope
    |> Result.bind (fun file ->
        readChar scope "delimiter" ','
        |> Result.bind (fun delimiter ->
            readChar scope "quote" '"'
            |> Result.bind (fun quote ->
                ConnectionScope.optionalBool scope "has_header"
                |> Result.bind (fun hasHeader ->
                    let encodingName =
                        (ConnectionScope.optionalOr scope "encoding" "utf-8").Trim().ToLowerInvariant()

                    match encodings.TryFind encodingName with
                    | None ->
                        let accepted = String.Join(", ", encodings |> Map.toList |> List.map fst)

                        Error(
                            SchemaMismatch
                                $"ConnectionScope key 'encoding' must be one of [%s{accepted}]; got '%s{encodingName}'"
                        )
                    | Some encoding ->
                        if delimiter = quote then
                            Error(SchemaMismatch "ConnectionScope keys 'delimiter' and 'quote' must differ")
                        else
                            Ok {
                                File = file
                                Delimiter = delimiter
                                Quote = quote
                                HasHeader = defaultArg hasHeader true
                                Encoding = encoding
                            }))))

// ─── RFC 4180 reader ──────────────────────────────────────────────
//
// A single-pass state machine over a `TextReader`. Quoted fields may
// contain the delimiter, line breaks, and doubled quotes; a bare quote
// inside an UNQUOTED field is taken literally, which RFC 4180 forbids
// and real-world exporters emit constantly — refusing the record there
// would reject files every spreadsheet opens without complaint.
//
// Malformed quoting does not throw and does not abandon the file: the
// offending record is reported and the reader resynchronises at the
// next line break, so one bad record costs one record.

[<RequireQualifiedAccess>]
type private ReaderState =
    | FieldStart
    | Unquoted
    | Quoted
    | QuoteInQuoted

/// Parse records off a reader, yielding each as its field array.
/// Lazy — a caller taking the first `sample_rows` records of a large
/// file reads only those records' characters.
let parseRecords (delimiter: char) (quote: char) (reader: TextReader) : seq<Result<string[], string>> = seq {
    let fields = ResizeArray<string>()
    let field = StringBuilder()
    let mutable state = ReaderState.FieldStart
    let mutable malformed: string option = None
    let mutable eof = false

    let commitField () =
        fields.Add(field.ToString())
        field.Clear() |> ignore

    let takeRecord () =
        commitField ()
        let cells = fields.ToArray()
        fields.Clear()
        state <- ReaderState.FieldStart

        match malformed with
        | Some message ->
            malformed <- None
            Error message
        | None -> Ok cells

    let atLineBreak (c: char) =
        if c = '\r' then
            if reader.Peek() = int '\n' then
                reader.Read() |> ignore

            true
        else
            c = '\n'

    while not eof do
        let code = reader.Read()

        if code < 0 then
            eof <- true

            match state with
            | ReaderState.Quoted ->
                malformed <- Some "unterminated quoted field — the file ended inside an opening quote"
            | _ -> ()

            // A trailing record exists only when characters
            // contributed to it. A file ending in a clean line break
            // yields nothing extra.
            if field.Length > 0 || fields.Count > 0 || malformed.IsSome then
                takeRecord ()
        else
            let c = char code

            match state with
            | ReaderState.FieldStart ->
                if c = quote then
                    state <- ReaderState.Quoted
                elif c = delimiter then
                    commitField ()
                elif atLineBreak c then
                    takeRecord ()
                else
                    field.Append c |> ignore
                    state <- ReaderState.Unquoted
            | ReaderState.Unquoted ->
                if c = delimiter then
                    commitField ()
                    state <- ReaderState.FieldStart
                elif atLineBreak c then
                    takeRecord ()
                else
                    field.Append c |> ignore
            | ReaderState.Quoted ->
                if c = quote then
                    state <- ReaderState.QuoteInQuoted
                else
                    field.Append c |> ignore
            | ReaderState.QuoteInQuoted ->
                if c = quote then
                    field.Append quote |> ignore
                    state <- ReaderState.Quoted
                elif c = delimiter then
                    commitField ()
                    state <- ReaderState.FieldStart
                elif atLineBreak c then
                    takeRecord ()
                else
                    malformed <-
                        Some
                            $"malformed quoting — unexpected '%c{c}' after a closing quote (a quoted field must be followed by the delimiter or a line break)"

                    field.Append c |> ignore
                    state <- ReaderState.Unquoted
}

/// Open a decoding reader over the downloaded bytes. A byte-order mark
/// wins over the configured encoding: a file that says what it is in
/// its first three bytes is more trustworthy than a config key typed
/// months ago, and honouring the BOM is what stops a stray `﻿`
/// appearing inside the first header cell.
let private openReader (settings: CsvSourceSettings) (bytes: byte[]) : TextReader =
    let stream = new MemoryStream(bytes, writable = false)

    new StreamReader(stream, settings.Encoding, detectEncodingFromByteOrderMarks = true, bufferSize = 4096)
    :> TextReader

/// Synthesised header for a file declaring `has_header = false`.
let private synthesisedHeader (width: int) = [ for i in 1..width -> $"column_%d{i}" ]

/// Split the parsed records into a header row plus body rows. A
/// malformed record is a `SchemaMismatch` naming its 1-based position
/// — the connector's contract is `Result` on every path, so a bad file
/// is a typed failure, never an exception and never silently-dropped
/// rows.
let private readTable
    (settings: CsvSourceSettings)
    (bytes: byte[])
    (limit: int option)
    : Result<string list * string list list, IngestionError> =
    use reader = openReader settings bytes

    let records = parseRecords settings.Delimiter settings.Quote reader

    let mutable failure: IngestionError option = None
    let mutable header: string list option = None
    let rows = ResizeArray<string list>()
    let mutable index = 0
    let mutable go = true

    use enumerator = records.GetEnumerator()

    while go && enumerator.MoveNext() do
        index <- index + 1

        match enumerator.Current with
        | Error message ->
            failure <- Some(SchemaMismatch $"CSV record %d{index} is malformed: %s{message}")
            go <- false
        | Ok cells ->
            match header with
            | None when settings.HasHeader -> header <- Some(cells |> Array.map _.Trim() |> List.ofArray)
            | None ->
                header <- Some(synthesisedHeader cells.Length)
                rows.Add(List.ofArray cells)
            | Some _ -> rows.Add(List.ofArray cells)

            match limit with
            | Some n when rows.Count >= n -> go <- false
            | _ -> ()

    match failure with
    | Some err -> Error err
    | None ->
        match header with
        | None -> Ok([], [])
        | Some header ->
            // A header-less file whose later records are wider than
            // its first would otherwise lose the extra columns
            // silently. Widen the synthesised header to the widest
            // record actually seen.
            let widest = rows |> Seq.fold (fun acc row -> max acc row.Length) header.Length

            let header =
                if settings.HasHeader || widest <= header.Length then
                    header
                else
                    synthesisedHeader widest

            Ok(header, List.ofSeq rows)

type private CsvDataSourceImpl(storage: IBlobStorage) =

    let withSettings
        (ctx: DataSourceCallContext)
        (context: string)
        (body: CsvSourceSettings -> Async<Result<'T, IngestionError>>)
        : Async<Result<'T, IngestionError>> =
        Errors.guard context (fun () -> async {
            match readSettings ctx.Config.ConnectionScope with
            | Error err -> return Error err
            | Ok settings -> return! body settings
        })

    interface IDataSource with
        member _.Kind = Kind

        member _.Connect(ctx) =
            withSettings ctx "Csv Connect" (fun settings -> async {
                // Cheapest probe that proves both halves an operator
                // gets wrong: that the container is reachable, and
                // that the prefix addresses something. Listing does
                // not download a byte.
                match! Files.listTables storage "Csv Connect" settings.File with
                | Error err -> return Error err
                | Ok [] ->
                    return
                        Error(
                            SourceUnreachable
                                $"Csv Connect: no '%s{settings.File.Extension}' files under '%s{settings.File.Container}/%s{settings.File.Prefix}'"
                        )
                | Ok _ -> return Ok()
            })

        member _.ListTables(ctx) =
            withSettings ctx "Csv ListTables" (fun settings -> Files.listTables storage "Csv ListTables" settings.File)

        member _.GetSchema(ctx, table) =
            withSettings ctx "Csv GetSchema" (fun settings -> async {
                match! Files.download storage "Csv GetSchema" settings.File table with
                | Error err -> return Error err
                | Ok bytes ->
                    match readTable settings bytes (Some settings.File.SampleRows) with
                    | Error err -> return Error err
                    | Ok(header, rows) -> return Ok(TypeProbe.schemaOf table header rows)
            })

        member _.Query(ctx, sql) =
            withSettings ctx "Csv Query" (fun settings -> async {
                match! Files.download storage "Csv Query" settings.File sql with
                | Error err -> return Error err
                | Ok bytes ->
                    match readTable settings bytes None with
                    | Error err -> return Error err
                    | Ok(header, rows) -> return Ok(CsvWire.toBytes header (rows |> Seq.map Seq.ofList))
            })

/// Build the connector over the deployment's blob storage.
///
/// `storage` is whatever the deployment composed — `LocalFileStorage`
/// for files on disk, or any cloud companion. The connector never
/// touches `System.IO` itself, so the same `DataSourceConfig` works
/// against either without an edit.
let create (storage: IBlobStorage) : IDataSource =
    CsvDataSourceImpl(storage) :> IDataSource