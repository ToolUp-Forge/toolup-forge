namespace ToolUp.Platform

// ─── IDataSource ──────────────────────────────────────────────────
//
// Server-side connector interface for one external data source —
// BigQuery, Redshift, Athena, Synapse, REST API, in-memory fake. One
// implementation per `DataSourceConfig.Kind` value. Connectors are
// registered with the SDK at compose time via DI; the
// `IDataIngestor` resolves the matching connector by `Kind` per
// ingestion call.
//
// **Stateless across calls.** Connectors hold no per-request state
// — credentials are resolved through a thunk on every call (mirrors
// `ClaudeAIProvider`'s `secretStore.GetSecret(...)` thunk pattern;
// supports rotation without provider reconstruction).
//
// **All paths return `Result<_, IngestionError>`** so the ingestor
// can distinguish operator-fixable errors (`CredentialMissing`)
// from infrastructure errors (`SourceUnreachable`) and emit
// appropriately differentiated lifecycle events.

/// Per-call context handed to every connector method. Carries the
/// caller's scope (so credential-bearing connectors can resolve
/// `secretStore.GetSecret(ScopeId, Config.CredentialKey)` per call —
/// mirrors the AI provider thunk pattern), the persisted config, and
/// the credential value when the ingestor pre-resolved it. Connectors
/// that read credentials directly from `ISecretStore` ignore the
/// `Credential` field and use `ScopeId` + `Config.CredentialKey`
/// instead.
type DataSourceCallContext = {
    /// Caller-resolved storage scope (team-scope id in Team /
    /// MultiTeam mode, user id otherwise). Connectors use it as the
    /// scope argument to `ISecretStore.GetSecret` when they read
    /// credentials directly.
    ScopeId: string
    /// Persisted config record. Read-only for connectors.
    Config: DataSourceConfig
    /// Optional pre-resolved credential. The shipped `DataIngestor`
    /// resolves the credential up-front and passes it through here so
    /// connectors that just need a credential string don't have to
    /// re-touch `ISecretStore` per call. `None` when the ingestor's
    /// pre-resolution missed (the connector may still attempt its
    /// own resolution, or fail with `CredentialMissing`).
    Credential: string option
}

type IDataSource =
    /// Kind discriminator that the ingestor matches against
    /// `DataSourceConfig.Kind` to route. E.g. `"BigQuery"`,
    /// `"InMemory"`, `"Redshift"`. Connector implementations expose
    /// this as a constant; dispatch is by string equality so a
    /// distributed registry (DI by name) can route uniformly.
    abstract Kind: string

    /// Probe the source for reachability with the supplied context.
    /// Used by admin UIs' "Test connection" button and at the start
    /// of every ingestion attempt. Returns `Ok` when the credential
    /// resolves and the source accepts a trivial query (connector-
    /// defined — e.g., BigQuery dataset metadata fetch). Connectors
    /// that have no cheap probe call may treat `Connect` as a no-op
    /// returning `Ok ()`.
    abstract Connect: ctx: DataSourceCallContext -> Async<Result<unit, IngestionError>>

    /// Enumerate available tables. Connectors that don't have a
    /// "tables" concept (a REST API) may return a synthetic single-
    /// element list naming the endpoint, or `Ok []` if the
    /// concept is genuinely meaningless.
    abstract ListTables: ctx: DataSourceCallContext -> Async<Result<string list, IngestionError>>

    /// Fetch the schema for one table. `Ok` with `Columns = []`
    /// when the connector cannot introspect schema (free-form REST
    /// response, opaque blob source). Admin UIs render an empty
    /// columns list as "schema not available" rather than treating
    /// it as a failure.
    abstract GetSchema: ctx: DataSourceCallContext * table: string -> Async<Result<TableSchema, IngestionError>>

    /// Run a query and return the raw bytes the source produced.
    /// **The connector does not choose the format** — the ingestor
    /// writes the bytes through `IDataObjectStore.Save` with
    /// `Versioned` policy, opaque to the storage layer. Modules
    /// that read those bytes back are responsible for parsing
    /// (typically as CSV, JSON, or Parquet according to the
    /// connector's documented output).
    ///
    /// `sql` is connector-specific syntax. BigQuery uses standard
    /// SQL, REST connectors may interpret it as a query parameter
    /// string, in-memory fakes may treat it as a table name lookup.
    /// Connectors document their dialect.
    abstract Query: ctx: DataSourceCallContext * sql: string -> Async<Result<byte[], IngestionError>>