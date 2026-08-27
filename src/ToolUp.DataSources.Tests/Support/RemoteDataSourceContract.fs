// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Support.RemoteDataSourceContract

open System
open System.Text
open Expecto
open ToolUp.Platform

// ─── The remote-warehouse IDataSource contract pack ───────────────
//
// The env-gated conformance bar for the `src/DataSources/*`
// connectors, mirroring `ToolUp.Platform.Tests`'
// `IDataSourceContract` point for point.
//
// **Why this is not simply a binding of that pack — read this before
// "fixing" the duplication.** The shipped pack is parametrised on a
// SEEDER (`DataSourceId -> string -> byte[] -> unit`) and asserts a
// BYTE-EXACT round-trip: seed `{"v":42}` against table `rollup`, then
// `Query(ctx, "rollup")` must return those bytes. Both halves are
// `InMemoryDataSource`-shaped and neither can hold for a warehouse:
//
//   • A seeder cannot exist. Seeding a real warehouse means DDL plus
//     write privileges on a live account — which a read-only
//     ingestion connector must not require, and which no CI job
//     should be handed.
//   • Byte-exactness cannot hold. `Query` on any real connector
//     executes a statement and FORMATS the result set (this family
//     emits RFC 4180 CSV). There is no seeded byte string a formatter
//     returns unchanged, so the assertion is not merely hard to
//     satisfy — it is unsatisfiable by construction for every
//     connector that is not an in-memory byte store.
//
// So the seven points are re-expressed in terms a warehouse can
// actually honour, against a PRE-PROVISIONED, READ-ONLY table the
// operator names by environment variable. Nothing here creates,
// writes to, or drops anything.
//
// With the env vars unset each arm reports ONE `pending` case naming
// the variables it wanted — a fresh checkout is clean, and a CI job
// that was supposed to have credentials shows "skipped" rather than a
// green that proves nothing.

/// Everything a connector's env-gated arm supplies to the pack.
type RemoteTarget = {
    /// The connector under test.
    Source: IDataSource
    /// A call context pointing at the scope (dataset / schema /
    /// database) that CONTAINS `Table`.
    Context: DataSourceCallContext
    /// A call context pointing at a scope that does NOT contain
    /// `Table` — used to prove `ListTables` is scoped rather than
    /// account-wide. Pointing it at a schema that does not exist is
    /// fine: the contract accepts either an empty list or an error,
    /// and only refuses a list that leaks `Table`.
    IsolatedContext: DataSourceCallContext
    /// Name of the pre-provisioned, readable table.
    Table: string
    /// A statement returning a BOUNDED sample of `Table` — bounded so
    /// a conformance run against a production warehouse cannot scan a
    /// fact table by accident.
    SampleSql: string
    /// A statement naming a table that does not exist, which the
    /// connector must surface as `Error`.
    MissingTableSql: string
}

/// Names of the env vars that are unset or blank.
let missingEnvVars (names: string list) : string list =
    names
    |> List.filter (fun name -> String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable name))

/// Split CSV bytes back into a header row and the raw body lines.
/// Deliberately naive — it is used only to read the FIRST line, which
/// the connectors never quote for a legal column name, and reusing
/// the emitter's own escaping here would make the assertion circular.
let internal headerOf (bytes: byte[]) : string list =
    let text = Encoding.UTF8.GetString bytes

    match text.Split([| "\r\n"; "\n" |], StringSplitOptions.None) |> Array.toList with
    | [] -> []
    | first :: _ when String.IsNullOrEmpty first -> []
    | first :: _ -> first.Split(',') |> Array.map _.Trim('"') |> Array.toList

/// The seven-point pack. `envVars` are the variables whose absence
/// makes the whole arm pending; `factory` is only called once at
/// least one test runs, so a connector's client is never constructed
/// on a credential-free checkout.
let tests (name: string) (envVars: string list) (factory: unit -> RemoteTarget) =
    match missingEnvVars envVars with
    | [] ->
        testList $"{name} — IDataSource contract (remote)" [

            test "Kind is non-empty" {
                let target = factory ()
                Expect.isNonEmpty target.Source.Kind "Kind discriminator must not be empty"
            }

            testCaseAsync "Connect succeeds for valid context"
            <| async {
                let target = factory ()

                match! target.Source.Connect target.Context with
                | Ok() -> ()
                | Error err -> failtestf "Connect failed: %A" err
            }

            testCaseAsync "ListTables includes the configured table"
            <| async {
                let target = factory ()

                match! target.Source.ListTables target.Context with
                | Ok tables ->
                    Expect.isNonEmpty tables "the configured scope must expose at least one table"

                    Expect.contains
                        (tables |> List.map _.ToLowerInvariant())
                        (target.Table.ToLowerInvariant())
                        "the pre-provisioned table is listed"
                | Error err -> failtestf "ListTables failed: %A" err
            }

            testCaseAsync "ListTables is scoped, not account-wide"
            <| async {
                let target = factory ()

                // An isolated scope may legitimately answer either
                // way — an empty list, or an error naming a scope that
                // does not exist. What it must NEVER do is return the
                // configured scope's tables, which is what an
                // unscoped catalogue query would do.
                match! target.Source.ListTables target.IsolatedContext with
                | Ok tables ->
                    Expect.isFalse
                        (tables
                         |> List.exists (fun t -> t.Equals(target.Table, StringComparison.OrdinalIgnoreCase)))
                        "the configured scope's table must NOT appear under an isolated scope"
                | Error _ -> ()
            }

            testCaseAsync "GetSchema returns the requested table with columns"
            <| async {
                let target = factory ()

                match! target.Source.GetSchema(target.Context, target.Table) with
                | Ok schema ->
                    Expect.equal schema.TableName target.Table "schema.TableName echoes the request"

                    Expect.isNonEmpty
                        schema.Columns
                        "a real warehouse table introspects to at least one column (unlike the in-memory connector, which may publish none)"

                    for column in schema.Columns do
                        Expect.isNonEmpty column.Name "every column carries a name"

                        Expect.isNonEmpty
                            column.DataType
                            "every column carries its RAW native type name — the connectors store the provider spelling, not the coarse ColumnType"
                | Error err -> failtestf "GetSchema failed: %A" err
            }

            testCaseAsync "Query returns CSV whose header matches the schema"
            <| async {
                let target = factory ()

                let! schema = target.Source.GetSchema(target.Context, target.Table)
                let! queried = target.Source.Query(target.Context, target.SampleSql)

                match schema, queried with
                | Ok schema, Ok bytes ->
                    Expect.isGreaterThan bytes.Length 0 "Query returns bytes"
                    let header = headerOf bytes
                    Expect.isNonEmpty header "the CSV payload opens with a header row"

                    let declared =
                        schema.Columns |> List.map (fun c -> c.Name.ToLowerInvariant()) |> Set.ofList

                    let emitted = header |> List.map _.ToLowerInvariant() |> Set.ofList

                    Expect.isTrue
                        (Set.isSubset emitted declared)
                        $"every emitted CSV column is declared by GetSchema — emitted %A{emitted}, declared %A{declared}"
                | Error err, _ -> failtestf "GetSchema failed: %A" err
                | _, Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "Query for an unknown table returns Error"
            <| async {
                let target = factory ()

                match! target.Source.Query(target.Context, target.MissingTableSql) with
                | Ok _ -> failtest "Expected Error for a statement over a non-existent table; got Ok"
                | Error _ -> ()
            }
        ]
    | missing ->
        let named = String.Join(", ", missing)

        testList name [ ptestCase $"skipped — %s{named} not set" <| fun _ -> () ]