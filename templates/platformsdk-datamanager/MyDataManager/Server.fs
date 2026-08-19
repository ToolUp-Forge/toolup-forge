module MyDataManager.Server

open ToolUp.Platform
open MyDataManager.SharedTypes

// ─── IDataSource skeleton ─────────────────────────────────────────
//
// One connector per `DataSourceConfig.Kind` value. `IDataIngestor`
// resolves the matching implementation by `Kind` (string equality)
// on every ingestion call and hands each method a
// `DataSourceCallContext` carrying the caller's scope, the persisted
// config, and the pre-resolved credential (when the ingestor found
// one). Connectors hold no per-request state — read credentials
// through `ctx` (or `ISecretStore` keyed by `ctx.ScopeId` +
// `ctx.Config.CredentialKey`) on every call, so rotation works
// without reconstructing the connector.
//
// Every path returns `Result<_, IngestionError>` so the ingestor can
// tell an operator-fixable failure (`CredentialMissing`) from an
// infrastructure one (`SourceUnreachable`).
//
// Register the connector with the SDK at compose time (DI), and wire
// the client module below into `ClientConfig.DataManager` with
// `ExternalDataManager` — see ClientView.fs.
type MyDataSource() =
    interface IDataSource with
        /// Must match the `Kind` of every `DataSourceConfig` this
        /// connector should serve.
        member _.Kind = "MyDataManager"

        /// Cheap reachability probe — the admin UI's "Test connection"
        /// button and the first step of every ingestion attempt.
        /// Connectors with no cheap probe may return `Ok ()`.
        member _.Connect(_ctx) = async { return Ok() }

        /// Enumerate source-side tables. Return `Ok []` when the
        /// concept is genuinely meaningless for this source.
        member _.ListTables(_ctx) = async { return Ok [] }

        /// Introspect one table's columns. `Columns = []` means
        /// "schema not available" — admin UIs render that rather than
        /// treating it as a failure.
        member _.GetSchema(_ctx, table) = async { return Ok { TableName = table; Columns = [] } }

        /// Run a query and return the source's raw bytes. The
        /// connector does NOT choose the storage format — the ingestor
        /// writes these bytes through `IDataObjectStore.Save` opaquely.
        /// `sql` is connector-specific syntax; document your dialect.
        member _.Query(_ctx, sql) = async {
            return Error(SourceUnreachable $"MyDataSource.Query not implemented (query: {sql})")
        }

let dataSource: IDataSource = MyDataSource() :> IDataSource

let private ingest (request: IngestRequest) : Async<IngestResult> = async {
    return {
        DatasetId = sprintf "%s::%s" request.SourceUri request.Format
        RowCount = 0
    }
}

let routes: MyDataManagerApi = { Ingest = ingest }