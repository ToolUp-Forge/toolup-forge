// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.CsvDataSourceTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Csv
open ToolUp.DataSources.Tests.Support
open DataManagementTypes

// ─── ToolUp.DataSources.Csv ───────────────────────────────────────
//
// Always-on: the connector needs no credential and no network, and
// the fixtures are written into an in-process `IBlobStorage`. The
// seven-point local-file contract runs here alongside the unit tests
// over the pure surfaces — `ConnectionScope` parsing, the RFC 4180
// reader's quoting rules, and the type probe.

let private container (sourceId: string) = $"team-%s{sourceId}"

[<Literal>]
let private Prefix = "exports/"

let private context (scope: (string * string) list) (sourceId: string) : DataSourceCallContext =
    TestFakes.config sourceId CsvDataSource.Kind ([ "container", container sourceId; "prefix", Prefix ] @ scope)
    |> TestFakes.context "test-scope" None

/// Render a header and rows as a delimited file.
let private render (delimiter: string) (header: string list) (rows: string list list) : byte[] =
    let line (fields: string list) = System.String.Join(delimiter, fields)

    let text =
        (line header :: (rows |> List.map line))
        |> List.map (fun l -> l + "\r\n")
        |> String.concat ""

    Encoding.UTF8.GetBytes text

let private target (scope: (string * string) list) (delimiter: string) () =
    let storage = FakeBlobStorage.InMemoryBlobStorage()

    {
        LocalFileDataSourceContract.Source = CsvDataSource.create storage
        LocalFileDataSourceContract.Seed =
            fun sourceId table header rows ->
                storage.Put(container sourceId, $"%s{Prefix}%s{table}.csv", render delimiter header rows)
        LocalFileDataSourceContract.Context = context scope
        LocalFileDataSourceContract.Address = id
    }

/// Parse a byte payload with the connector's own reader.
let private records (delimiter: char) (quote: char) (text: string) =
    use reader = new System.IO.StringReader(text)
    CsvDataSource.parseRecords delimiter quote reader |> List.ofSeq

let tests =
    testList "CsvDataSource" [

        // The contract, twice: once on the default comma dialect and
        // once on a tab dialect, because `delimiter` changes the read
        // path and a connector that honoured it only in `Query` would
        // pass the first run and fail the second.
        LocalFileDataSourceContract.tests "Csv (comma)" (target [] ",")
        LocalFileDataSourceContract.tests "Csv (tab)" (target [ "delimiter", "tab" ] "\t")

        testList "readSettings" [
            test "container is required" {
                match CsvDataSource.readSettings Map.empty with
                | Error(SchemaMismatch message) -> Expect.stringContains message "container" "names the missing key"
                | other -> failtestf "Expected SchemaMismatch naming 'container'; got %A" other
            }

            test "defaults are comma / double-quote / header / utf-8 / 1000 rows" {
                match CsvDataSource.readSettings (Map.ofList [ "container", "team-x" ]) with
                | Ok settings ->
                    Expect.equal settings.Delimiter ',' "delimiter"
                    Expect.equal settings.Quote '"' "quote"
                    Expect.isTrue settings.HasHeader "has_header"
                    Expect.equal settings.Encoding.CodePage Encoding.UTF8.CodePage "encoding"
                    Expect.equal settings.File.SampleRows 1000 "sample_rows"
                    Expect.equal settings.File.Extension ".csv" "extension"
                    Expect.equal settings.File.Prefix "" "an absent prefix is the container root"
                | Error err -> failtestf "readSettings failed: %A" err
            }

            test "a prefix is normalised to one trailing slash, separators folded" {
                let read (raw: string) =
                    match CsvDataSource.readSettings (Map.ofList [ "container", "c"; "prefix", raw ]) with
                    | Ok settings -> settings.File.Prefix
                    | Error err -> failtestf "readSettings failed: %A" err

                Expect.equal (read "exports") "exports/" "bare name"
                Expect.equal (read "/exports/") "exports/" "leading and trailing slashes"
                // Blob names are `/`-delimited on IBlobStorage whatever
                // the host OS is, so a Windows operator's backslash must
                // not silently produce a prefix matching nothing.
                Expect.equal (read "a\\b") "a/b/" "backslashes folded"
            }

            test "named delimiters are accepted where a literal cannot be typed" {
                let read (raw: string) =
                    match CsvDataSource.readSettings (Map.ofList [ "container", "c"; "delimiter", raw ]) with
                    | Ok settings -> settings.Delimiter
                    | Error err -> failtestf "readSettings failed: %A" err

                Expect.equal (read "tab") '\t' "tab"
                Expect.equal (read "pipe") '|' "pipe"
                Expect.equal (read ";") ';' "a literal character"
            }

            test "a multi-character delimiter is refused, naming the accepted spellings" {
                match CsvDataSource.readSettings (Map.ofList [ "container", "c"; "delimiter", "::" ]) with
                | Error(SchemaMismatch message) -> Expect.stringContains message "semicolon" "names the accepted set"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }

            test "an unknown encoding is refused rather than silently defaulted" {
                match CsvDataSource.readSettings (Map.ofList [ "container", "c"; "encoding", "windows-1252" ]) with
                | Error(SchemaMismatch message) -> Expect.stringContains message "utf-8" "names the accepted set"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }

            test "delimiter and quote must differ" {
                match
                    CsvDataSource.readSettings (Map.ofList [ "container", "c"; "delimiter", "\""; "quote", "\"" ])
                with
                | Error(SchemaMismatch message) -> Expect.stringContains message "differ" "says why"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }

            test "a non-positive sample_rows is refused" {
                match CsvDataSource.readSettings (Map.ofList [ "container", "c"; "sample_rows", "0" ]) with
                | Error(SchemaMismatch message) -> Expect.stringContains message "sample_rows" "names the key"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }
        ]

        testList "RFC 4180 reader" [
            test "quoted fields carry delimiters, doubled quotes and line breaks" {
                let parsed = records ',' '"' "a,\"b,c\",\"d\"\"e\",\"f\ng\"\r\n"

                match parsed with
                | [ Ok fields ] -> Expect.sequenceEqual fields [ "a"; "b,c"; "d\"e"; "f\ng" ] "fields"
                | other -> failtestf "Expected one well-formed record; got %A" other
            }

            test "a bare quote inside an unquoted field is taken literally" {
                // RFC 4180 forbids it; real exporters emit it constantly,
                // and every spreadsheet opens such a file without
                // complaint. Refusing the record would reject more real
                // files than it would catch real defects.
                match records ',' '"' "5\" pipe,2\r\n" with
                | [ Ok fields ] -> Expect.sequenceEqual fields [ "5\" pipe"; "2" ] "fields"
                | other -> failtestf "Expected one well-formed record; got %A" other
            }

            test "an unterminated quote is reported, not thrown" {
                match records ',' '"' "a,\"unterminated\r\n" with
                | [ Error message ] -> Expect.stringContains message "unterminated" "says what went wrong"
                | other -> failtestf "Expected one malformed record; got %A" other
            }

            test "one malformed record costs one record — the reader resynchronises" {
                let parsed = records ',' '"' "\"ab\"x,1\r\ngood,2\r\n"

                match parsed with
                | [ Error _; Ok fields ] -> Expect.sequenceEqual fields [ "good"; "2" ] "the next record parses"
                | other -> failtestf "Expected a malformed record then a good one; got %A" other
            }

            test "a file ending without a line break still yields its last record" {
                match records ',' '"' "a,b\r\nc,d" with
                | [ Ok _; Ok fields ] -> Expect.sequenceEqual fields [ "c"; "d" ] "trailing record"
                | other -> failtestf "Expected two records; got %A" other
            }

            test "a trailing line break does not yield a phantom empty record" {
                Expect.equal (records ',' '"' "a,b\r\n").Length 1 "one record"
            }
        ]

        testList "schema inference" [
            testCaseAsync "columns are type-probed and blanks make a column nullable"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = CsvDataSource.create storage

                storage.Put(
                    container "src",
                    $"%s{Prefix}mixed.csv",
                    render "," [ "name"; "count"; "when"; "flag" ] [
                        [ "alpha"; "1"; "2026-01-02"; "true" ]
                        [ "beta"; ""; "2026-01-03"; "false" ]
                    ]
                )

                match! source.GetSchema(context [] "src", "mixed") with
                | Ok schema ->
                    let byName = schema.Columns |> List.map (fun c -> c.Name, c) |> Map.ofList

                    Expect.equal (byName["name"].Nullable) false "a column with no blank cell is not nullable"

                    Expect.equal
                        (byName["count"].Nullable)
                        true
                        "a blank cell is the only nullability evidence a CSV carries"

                    Expect.stringContains
                        (byName["count"].DataType)
                        "inferred"
                        "the native name says the type is a guess"
                | Error err -> failtestf "GetSchema failed: %A" err
            }

            testCaseAsync "has_header = false synthesises column names and keeps the first record"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = CsvDataSource.create storage
                storage.Put(container "src", $"%s{Prefix}raw.csv", render "," [ "a"; "b" ] [ [ "c"; "d" ] ])

                let ctx = context [ "has_header", "false" ] "src"

                match! source.Query(ctx, "raw") with
                | Ok bytes ->
                    let parsed = LocalFileDataSourceContract.parseCsv bytes
                    Expect.sequenceEqual (List.head parsed) [ "column_1"; "column_2" ] "synthesised header"
                    Expect.equal (List.length parsed) 3 "both records are data, none consumed as a header"
                | Error err -> failtestf "Query failed: %A" err
            }
        ]

        testList "scope discipline" [
            testCaseAsync "a table name that would escape the prefix is refused"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = CsvDataSource.create storage
                storage.Put(container "src", "secrets.csv", render "," [ "a" ] [ [ "b" ] ])
                storage.Put(container "src", $"%s{Prefix}ok.csv", render "," [ "a" ] [ [ "b" ] ])

                // The container is scope-derived and therefore safe, but
                // the TABLE name is concatenated onto the prefix — a name
                // carrying `..` addresses a file the source was never
                // pointed at.
                match! source.Query(context [] "src", "../secrets") with
                | Ok _ -> failtest "Expected the traversal to be refused; got Ok"
                | Error(SchemaMismatch message) -> Expect.stringContains message "prefix" "explains the rule"
                | Error other -> failtestf "Expected SchemaMismatch; got %A" other
            }

            testCaseAsync "ListTables ignores nested blobs and foreign extensions"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = CsvDataSource.create storage
                let payload = render "," [ "a" ] [ [ "b" ] ]
                storage.Put(container "src", $"%s{Prefix}flat.csv", payload)
                storage.Put(container "src", $"%s{Prefix}nested/deep.csv", payload)
                storage.Put(container "src", $"%s{Prefix}manifest.json", payload)

                match! source.ListTables(context [] "src") with
                | Ok tables -> Expect.sequenceEqual tables [ "flat" ] "only sibling .csv blobs are tables"
                | Error err -> failtestf "ListTables failed: %A" err
            }
        ]

        test "Kind is the documented discriminator" { Expect.equal CsvDataSource.Kind "Csv" "DataSourceConfig.Kind" }
    ]