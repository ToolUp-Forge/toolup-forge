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

// ─── IngestedPayload — the schema an ingested payload carries (Phase 832) ───
//
// `DataIngestor` persists the connector's `GetSchema` answer as its own
// data object and stamps the payload's metadata with a `schema-ref` (the
// schema object's content hash) and a `payload-format` version token. This
// module is the ONE place both sides of that convention live: the writer's
// canonical serialisation and reserved object id, and the reader-side
// accessor a module calls with a `DataObject` it already holds — so no
// consumer re-derives the lookup or re-infers column types from text.
//
// A payload written before Phase 832 carries neither key, and every
// accessor answers `None` for it — the honest answer, and the one a
// reader's fallback (re-inference) keys on.

/// Phase 832 — reading and writing the schema an ingested payload carries.
module IngestedPayload =

    open System.IO
    open System.Text.Json

    /// Payload metadata key naming the content hash of the payload's
    /// schema object. Absent when the connector could not answer
    /// `GetSchema` (or answered `Columns = []`, "schema not available").
    [<Literal>]
    let SchemaRefKey = "schema-ref"

    /// Payload metadata key carrying the payload-format version token.
    /// Present on every payload written from Phase 832 on, so a later
    /// convention change has a version to condition on and payloads
    /// written before it stay readable under their own rules.
    [<Literal>]
    let PayloadFormatKey = "payload-format"

    /// Payload metadata key recording whether this run's schema differs
    /// from the schema last recorded for the same `(source, table)` —
    /// `"true"` / `"false"`. Absent when there is no schema this run or
    /// no earlier recorded schema to compare with.
    [<Literal>]
    let SchemaDriftKey = "schema-drift"

    /// The payload-format token this SDK version writes. `"1"` (Phase
    /// 832): schema-carrying payloads, empty CSV fields ambiguous
    /// between NULL and the empty string. `"2"` (Phase 833): the same,
    /// plus the null convention — an unquoted empty field is NULL, a
    /// quoted `""` is the empty string (read it with
    /// `ConnectorSupport.Csv.distinguishesNull` / `readField`).
    [<Literal>]
    let CurrentPayloadFormat = "2"

    /// `DataType` recorded on schema objects, distinct from the payload's
    /// `"data-ingestion"` so catalog sweeps can tell them apart.
    [<Literal>]
    let SchemaDataType = "data-ingestion-schema"

    /// Reserved object id holding the recorded schema history for one
    /// `(sourceId, table)`. One version per DISTINCT consecutive schema:
    /// the ingestor saves a new version only when the schema changed, so
    /// the object's version list is the table's schema-change log, and
    /// the store's content dedup keeps one blob per distinct schema. The
    /// `_dataingestion_schema__` prefix cannot collide with a payload id
    /// (`_dataingestion__{sourceId}__{table}`).
    let schemaObjectId (sourceId: DataSourceId) (table: string) =
        $"_dataingestion_schema__{sourceId}__{table}"

    /// Canonical serialisation of a `TableSchema`: a fixed member order
    /// (`tableName`, `columns[]` of `name` / `dataType` / `nullable`),
    /// no insignificant whitespace, columns in the connector's order. Two
    /// identical schemas serialise to identical bytes and therefore to one
    /// content hash — the dedup the schema object relies on.
    let serializeSchema (schema: TableSchema) : byte[] =
        use stream = new MemoryStream()

        do
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("tableName", schema.TableName)
            writer.WriteStartArray("columns")

            for column in schema.Columns do
                writer.WriteStartObject()
                writer.WriteString("name", column.Name)
                writer.WriteString("dataType", column.DataType)
                writer.WriteBoolean("nullable", column.Nullable)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()

        stream.ToArray()

    /// Parse bytes written by `serializeSchema`. `None` for anything that
    /// is not that shape — a reader never throws on a malformed blob.
    let tryParseSchema (bytes: byte[]) : TableSchema option =
        try
            use doc = JsonDocument.Parse(bytes: byte[])
            let root = doc.RootElement

            let columns =
                root.GetProperty("columns").EnumerateArray()
                |> Seq.map (fun c -> {
                    Name = c.GetProperty("name").GetString()
                    DataType = c.GetProperty("dataType").GetString()
                    Nullable = c.GetProperty("nullable").GetBoolean()
                })
                |> List.ofSeq

            Some {
                TableName = root.GetProperty("tableName").GetString()
                Columns = columns
            }
        with _ ->
            None

    /// The payload's `schema-ref`, when it carries one.
    let schemaRef (payload: DataObject) : string option =
        Map.tryFind SchemaRefKey payload.Metadata

    /// The payload's `payload-format` token. `None` for a payload written
    /// before Phase 832.
    let payloadFormat (payload: DataObject) : string option =
        Map.tryFind PayloadFormatKey payload.Metadata

    /// Whether the run that wrote this payload observed a schema different
    /// from the one last recorded for the same `(source, table)`. `None`
    /// when unknown (no schema this run, no earlier schema, or a payload
    /// written before Phase 832).
    let schemaDrift (payload: DataObject) : bool option =
        match Map.tryFind SchemaDriftKey payload.Metadata with
        | Some "true" -> Some true
        | Some "false" -> Some false
        | _ -> None

    /// The reader-side accessor: the column names, types and nullability
    /// the connector reported for this payload, read from the store by the
    /// payload's `schema-ref` — no re-inference, no live connection to the
    /// source. `None` for a payload with no `schema-ref` (written before
    /// Phase 832, or by a connector that could not answer `GetSchema`),
    /// and for a ref whose blob is gone or unreadable.
    let readSchema (store: IDataObjectStore) (payload: DataObject) : Async<TableSchema option> = async {
        match schemaRef payload with
        | None -> return None
        | Some hash ->
            match! store.GetContent(payload.ScopeId, hash) with
            | Ok bytes -> return tryParseSchema bytes
            | Error _ -> return None
    }