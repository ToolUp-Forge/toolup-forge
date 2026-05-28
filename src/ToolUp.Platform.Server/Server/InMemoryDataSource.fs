module ToolUp.Platform.InMemoryDataSource

open System.Collections.Concurrent
open System.Text
open ToolUp.Platform

// ─── InMemoryDataSource ──────────────────────────────────────────
//
// Test / dev `IDataSource` implementation that holds preconfigured
// `(table → bytes)` content in memory. Used by:
//   - The `IDataSourceContract` test pack (verifies the SDK
//     interface contract without any external deps).
//   - The `TestHarness` for module-development workflows.
//   - Deployments that want a stub-source for staging or for
//     demoing the data-ingestion UI without real cloud integration.
//
// **`Kind` discriminator: `"InMemory"`.** Matches `DataSourceConfig.Kind`
// for routing through `IDataIngestor`.
//
// The content map is `(sourceId, table) → bytes` so one in-memory
// source can serve multiple configured `DataSourceConfig` records
// with disjoint table namespaces. Test factories typically construct
// a single source per test and seed it with one table.
//
// Connect / ListTables / GetSchema / Query never touch `ISecretStore`
// — there are no credentials to resolve. Tests that exercise the
// credential-missing failure mode must use a connector that does.

type InMemoryDataSource() =

    let content = ConcurrentDictionary<string * string, byte[] * TableSchema>()

    /// Seed a (sourceId, table) → (bytes, schema) entry. Tests call
    /// this before invoking the SDK's ingestion path.
    member _.Seed(sourceId: DataSourceId, table: string, bytes: byte[], schema: TableSchema) =
        content[(sourceId, table)] <- (bytes, schema)

    /// Convenience overload — schema defaults to empty columns when
    /// the test only cares about the bytes round-trip.
    member this.Seed(sourceId: DataSourceId, table: string, bytes: byte[]) =
        this.Seed(sourceId, table, bytes, { TableName = table; Columns = [] })

    /// Convenience: seed string content as UTF-8 bytes.
    member this.SeedText(sourceId: DataSourceId, table: string, text: string) =
        this.Seed(sourceId, table, Encoding.UTF8.GetBytes text)

    interface IDataSource with
        member _.Kind = "InMemory"

        member _.Connect(_) = async { return Ok() }

        member _.ListTables(ctx) = async {
            let tables =
                content.Keys
                |> Seq.filter (fun (s, _) -> s = ctx.Config.Id)
                |> Seq.map snd
                |> Seq.distinct
                |> List.ofSeq

            return Ok tables
        }

        member _.GetSchema(ctx, table) = async {
            match content.TryGetValue((ctx.Config.Id, table)) with
            | true, (_, schema) -> return Ok schema
            | false, _ -> return Ok { TableName = table; Columns = [] }
        }

        member _.Query(ctx, sql) = async {
            // Treat `sql` as the table name for the in-memory source —
            // connector docs make this explicit (each connector is
            // free to interpret the dialect; in-memory has no SQL
            // engine). Credentials are ignored — the in-memory
            // source doesn't authenticate.
            match content.TryGetValue((ctx.Config.Id, sql)) with
            | true, (bytes, _) -> return Ok bytes
            | false, _ ->
                return Error(SchemaMismatch $"InMemoryDataSource: no content seeded for ({ctx.Config.Id}, {sql})")
        }

let create () = InMemoryDataSource()