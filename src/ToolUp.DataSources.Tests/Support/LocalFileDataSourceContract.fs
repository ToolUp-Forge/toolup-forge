// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Support.LocalFileDataSourceContract

open System
open System.Text
open Expecto
open ToolUp.Platform

// ─── The local-file IDataSource contract pack ─────────────────────
//
// The conformance bar for the Phase 10d file connectors (`Csv`,
// `Excel`, `Parquet`), mirroring `ToolUp.Platform.Tests`'
// `IDataSourceContract` point for point.
//
// **Why this is not simply a binding of that pack — read this before
// "fixing" the duplication.** The shipped pack seeds OPAQUE BYTES
// against a table name and asserts `Query` returns those exact bytes.
// Both halves are `InMemoryDataSource`-shaped:
//
//   • A file connector's fixture is not opaque. Seeding `"a-content"`
//     as a `.parquet` blob does not produce a Parquet file, it
//     produces a blob the reader correctly refuses. Each format needs
//     its own writer, so the seeder here takes a HEADER and ROWS and
//     the connector's arm renders them in its own format.
//   • Byte-exactness cannot hold. `Query` PARSES a format and
//     re-emits RFC 4180 CSV — that re-emission is the family's uniform
//     wire contract, and it is exactly what makes a seeded byte string
//     unrecoverable. The assertion below is therefore the honest one:
//     the header and every seeded row survive the round trip.
//
// Unlike `RemoteDataSourceContract` these arms are ALWAYS ON. There is
// no credential, no account and no network: the fixtures are written
// into an in-process `IBlobStorage` and read back through the same
// seam a deployment composes a real backend into. A fresh checkout
// runs the whole pack.

/// Everything a connector's arm supplies to the pack.
type LocalFileTarget = {
    /// The connector under test.
    Source: IDataSource
    /// Write a fixture file for `(sourceId, table)` carrying `header`
    /// and `rows`, in the connector's own format.
    Seed: string -> string -> string list -> string list list -> unit
    /// A call context addressing one source id. Distinct source ids
    /// MUST address distinct containers or prefixes — that is what
    /// makes point 4 a real isolation test rather than a tautology.
    Context: string -> DataSourceCallContext
    /// The name `GetSchema` / `Query` are given for a seeded table.
    /// Usually the table name itself; the Excel connector addresses a
    /// region inside the workbook, so it is not always.
    Address: string -> string
}

/// Split CSV bytes into rows of fields. Deliberately naive — the
/// fixtures below carry no commas, quotes or line breaks inside a
/// field, and reusing the connector's own parser here would make every
/// assertion circular.
let internal parseCsv (bytes: byte[]) : string list list =
    Encoding.UTF8.GetString bytes
    |> fun text -> text.Split([| "\r\n"; "\n" |], StringSplitOptions.None)
    |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace line))
    |> Array.map (fun line -> line.Split(',') |> Array.map (_.Trim('"')) |> List.ofArray)
    |> List.ofArray

/// The seven-point pack. `factory` is called fresh per test so no
/// case can observe another's fixtures.
let tests (name: string) (factory: unit -> LocalFileTarget) =

    let header = [ "region"; "units"; "active" ]

    let rows = [
        [ "north"; "12"; "true" ]
        [ "south"; "7"; "false" ]
        [ "east"; "31"; "true" ]
    ]

    testList $"{name} — IDataSource contract (local file)" [

        test "Kind is non-empty" {
            let target = factory ()
            Expect.isNonEmpty target.Source.Kind "Kind discriminator must not be empty"
        }

        testCaseAsync "Connect succeeds once a file is present"
        <| async {
            let target = factory ()
            target.Seed "src-connect" "sales" header rows

            match! target.Source.Connect(target.Context "src-connect") with
            | Ok() -> ()
            | Error err -> failtestf "Connect failed: %A" err
        }

        testCaseAsync "Connect fails when the configured location holds no file"
        <| async {
            let target = factory ()

            // A source pointed at an empty container is the single
            // most common misconfiguration, and it must not read as
            // a healthy connection: an admin UI's "Test connection"
            // is the last cheap place to catch a wrong prefix.
            match! target.Source.Connect(target.Context "src-empty") with
            | Ok() -> failtest "Expected Error when no file matches the configured prefix; got Ok"
            | Error _ -> ()
        }

        testCaseAsync "ListTables returns the seeded table names"
        <| async {
            let target = factory ()
            target.Seed "src-list" "table-a" header rows
            target.Seed "src-list" "table-b" header rows

            match! target.Source.ListTables(target.Context "src-list") with
            | Ok tables ->
                let asSet = Set.ofList tables
                Expect.isTrue (asSet.Contains "table-a") "table-a present"
                Expect.isTrue (asSet.Contains "table-b") "table-b present"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "ListTables is scoped to the requested source id"
        <| async {
            let target = factory ()
            target.Seed "src-A" "table-only-on-A" header rows
            target.Seed "src-B" "table-only-on-B" header rows

            match! target.Source.ListTables(target.Context "src-A") with
            | Ok tables ->
                Expect.contains tables "table-only-on-A" "src-A's table is listed"

                Expect.isFalse
                    (tables |> List.exists (fun t -> t.StartsWith "table-only-on-B"))
                    "src-B's table is NOT listed under src-A"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "GetSchema returns the requested table with its columns"
        <| async {
            let target = factory ()
            target.Seed "src-schema" "tbl" header rows
            let address = target.Address "tbl"

            match! target.Source.GetSchema(target.Context "src-schema", address) with
            | Ok schema ->
                Expect.equal schema.TableName address "schema.TableName echoes the request"

                Expect.sequenceEqual
                    (schema.Columns |> List.map _.Name)
                    header
                    "the declared columns are the file's own, in order"

                for column in schema.Columns do
                    Expect.isNonEmpty
                        column.DataType
                        "every column carries a native type name — inferred for the text formats, declared for Parquet"
            | Error err -> failtestf "GetSchema failed: %A" err
        }

        testCaseAsync "Query round-trips the seeded table as RFC 4180 CSV"
        <| async {
            let target = factory ()
            target.Seed "src-query" "rollup" header rows

            match! target.Source.Query(target.Context "src-query", target.Address "rollup") with
            | Ok bytes ->
                let parsed = parseCsv bytes
                Expect.isNonEmpty parsed "Query emits at least a header row"
                Expect.sequenceEqual (List.head parsed) header "the emitted header is the file's own"

                Expect.sequenceEqual
                    (List.tail parsed)
                    rows
                    "every seeded row survives the parse-and-re-emit round trip, in order"
            | Error err -> failtestf "Query failed: %A" err
        }

        testCaseAsync "Query for an unknown table returns Error"
        <| async {
            let target = factory ()
            target.Seed "src-missing" "present" header rows

            match! target.Source.Query(target.Context "src-missing", target.Address "no-such-table") with
            | Ok _ -> failtest "Expected Error for a table with no file; got Ok"
            | Error _ -> ()
        }
    ]