module ToolUp.Platform.Tests.Contracts.IDataSourceContract

open System.Text
open Expecto
open ToolUp.Platform

// ─── IDataSource contract pack ────────────────────────────────────
//
// Parametrised tests for any `IDataSource` implementation. The
// factory takes a seed callback so tests can pre-populate the
// connector with `(sourceId, table) → bytes` content before
// exercising the contract methods.
//
// Coverage targets the interface contract — Kind discriminator,
// Connect/ListTables/GetSchema/Query round-trips, error shapes for
// missing content, and the declared capability record (Phase 1095). Connector-specific behaviour (BigQuery dialect,
// Redshift connection-string parsing) is tested in each connector's
// own integration tests.

type Seeder = DataSourceId -> string -> byte[] -> unit

// ─── Phase 1095 — the capability record ────────────────────────────
//
// A connector's abilities beyond the five members are its declared
// `Capabilities` record, never its runtime type. The laws: a declared
// capability is honoured (the reader hands back exactly the declared
// instance); an undeclared one asked for is refused by name (the
// connector's `Kind` and the capability); the record is a constant of the
// connector; and a decorator forwarding the record declares exactly its
// inner connector's capabilities, whatever the decorator's own type is.

/// A connector forwarding every member, its record included, to `inner`.
let private forwarding (inner: IDataSource) =
    { new IDataSource with
        member _.Kind = inner.Kind
        member _.Capabilities = inner.Capabilities
        member _.Connect ctx = inner.Connect ctx
        member _.ListTables ctx = inner.ListTables ctx
        member _.GetSchema(ctx, t) = inner.GetSchema(ctx, t)
        member _.Query(ctx, sql) = inner.Query(ctx, sql)
    }

let private same (declared: 'T option) (read: Result<'T, CapabilityNotDeclared>) =
    match declared, read with
    | Some d, Ok r -> obj.ReferenceEquals(d, r)
    | None, Error _ -> true
    | _ -> false

/// The capability laws for the connector `factory` builds.
let capabilityLaws (name: string) (factory: unit -> IDataSource) =
    testList $"{name} — IDataSource capabilities (Phase 1095)" [

        test "a declared capability is honoured; an undeclared one asked for is refused by name" {
            let source = factory ()

            for capability in ConnectorCapability.all do
                match ConnectorCapabilities.require capability source with
                | Ok() ->
                    Expect.isTrue
                        (ConnectorCapabilities.declares capability source.Capabilities)
                        $"{ConnectorCapability.name capability} is granted only when declared"
                | Error refusal ->
                    Expect.equal
                        refusal
                        {
                            Kind = source.Kind
                            Capability = capability
                        }
                        "the refusal names the connector and the capability"

                    let sentence = CapabilityNotDeclared.describe refusal
                    Expect.stringContains sentence source.Kind "the sentence names the connector"
                    Expect.stringContains sentence (ConnectorCapability.name capability) "and the capability"

            Expect.isTrue
                (same source.Capabilities.Watchable (ConnectorCapabilities.watchable source))
                "the watch read is the declared watch"

            Expect.isTrue
                (same source.Capabilities.Native (ConnectorCapabilities.native source))
                "the native read is the declared emission"
        }

        test "the record is a constant of the connector" {
            let source = factory ()

            Expect.equal
                (ConnectorCapabilities.declared source.Capabilities)
                (ConnectorCapabilities.declared source.Capabilities)
                "two reads declare the same capabilities"
        }

        test "a decorator forwarding the record declares exactly its inner connector's capabilities" {
            let source = factory ()
            let decorated = forwarding source

            for capability in ConnectorCapability.all do
                Expect.equal
                    (ConnectorCapabilities.require capability decorated |> Result.isOk)
                    (ConnectorCapabilities.require capability source |> Result.isOk)
                    $"{ConnectorCapability.name capability} reads through the decorator"
        }
    ]

/// The capability laws, plus the pin that the connector declares exactly
/// `expected` — what a shipped connector's probes answered before the record.
let capabilities (name: string) (expected: ConnectorCapability list) (factory: unit -> IDataSource) =
    testList name [
        test "declares exactly what it implements" {
            Expect.equal (ConnectorCapabilities.declared (factory ()).Capabilities) expected "the declared capabilities"
        }
        capabilityLaws name factory
    ]

let tests (name: string) (factory: unit -> IDataSource * Seeder) =

    let mkConfig sourceId : DataSourceConfig = {
        Id = sourceId
        Name = $"Test source {sourceId}"
        Kind = "InMemory"
        ConnectionScope = Map.empty
        CredentialKey = "test-credential"
        Tables = None
        Tags = Map.empty
    }

    let mkContext sourceId : DataSourceCallContext = {
        ScopeId = "test-scope"
        Config = mkConfig sourceId
        Credential = None
    }

    testList $"{name} — IDataSource contract" [

        capabilityLaws name (factory >> fst)

        test "Kind is non-empty" {
            let source, _ = factory ()
            Expect.isNonEmpty source.Kind "Kind discriminator must not be empty"
        }

        testCaseAsync "Connect succeeds for valid context"
        <| async {
            let source, _ = factory ()
            let ctx = mkContext "src-1"

            match! source.Connect ctx with
            | Ok() -> ()
            | Error err -> failtestf "Connect failed: %A" err
        }

        testCaseAsync "ListTables returns the seeded table names"
        <| async {
            let source, seed = factory ()
            let sourceId = "src-list"
            seed sourceId "table-a" (Encoding.UTF8.GetBytes "a-content")
            seed sourceId "table-b" (Encoding.UTF8.GetBytes "b-content")

            let ctx = mkContext sourceId

            match! source.ListTables ctx with
            | Ok tables ->
                let asSet = Set.ofList tables
                Expect.isTrue (asSet.Contains "table-a") "table-a present"
                Expect.isTrue (asSet.Contains "table-b") "table-b present"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "ListTables is scoped to the requested source id"
        <| async {
            let source, seed = factory ()
            seed "src-A" "table-only-on-A" (Encoding.UTF8.GetBytes "x")
            seed "src-B" "table-only-on-B" (Encoding.UTF8.GetBytes "y")

            let ctxA = mkContext "src-A"

            match! source.ListTables ctxA with
            | Ok tables ->
                Expect.contains tables "table-only-on-A" "src-A's table is listed"
                Expect.isFalse (List.contains "table-only-on-B" tables) "src-B's table is NOT listed under src-A"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "GetSchema returns published schema or empty columns"
        <| async {
            let source, seed = factory ()
            seed "src-schema" "tbl" (Encoding.UTF8.GetBytes "x")
            let ctx = mkContext "src-schema"

            match! source.GetSchema(ctx, "tbl") with
            | Ok schema -> Expect.equal schema.TableName "tbl" "schema.TableName matches request"
            // The InMemoryDataSource's auto-Seed-overload uses an
            // empty column list; impls that publish columns will
            // assert non-empty in their own integration tests.
            | Error err -> failtestf "GetSchema failed: %A" err
        }

        testCaseAsync "Query returns the seeded bytes for a known table"
        <| async {
            let source, seed = factory ()
            let payload = Encoding.UTF8.GetBytes "{\"v\":42}"
            seed "src-query" "rollup" payload
            let ctx = mkContext "src-query"

            match! source.Query(ctx, "rollup") with
            | Ok bytes -> Expect.equal (Encoding.UTF8.GetString bytes) "{\"v\":42}" "bytes round-trip"
            | Error err -> failtestf "Query failed: %A" err
        }

        testCaseAsync "Query for unknown table returns Error"
        <| async {
            let source, _ = factory ()
            let ctx = mkContext "src-empty"

            match! source.Query(ctx, "no-such-table") with
            | Ok _ -> failtest "Expected Error for unknown table; got Ok"
            | Error _ -> ()
        }
    ]