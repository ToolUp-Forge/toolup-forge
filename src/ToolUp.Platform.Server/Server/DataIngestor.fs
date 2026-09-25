module ToolUp.Platform.DataIngestor

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Constants ────────────────────────────────────────────────────

[<Literal>]
let DataIngestionSourceModule = "_platform.dataingestion"

[<Literal>]
let private RunCompletedEventType = "IngestionRunCompleted"

[<Literal>]
let private RunFailedEventType = "IngestionRunFailed"

let private maxRunsPerSource = 50

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: `_platform`.
// Run blob: `data-sources/{scopeId}/runs/{sourceId}/{ts}-{runId:N}.json`.
// Co-located with the config-store layout from `DataSourceConfigStore.fs`
// (`data-sources/{scopeId}/configs/{sourceId}.json`) so operators see
// one consistent shape under `_platform/data-sources/`.

let private platformContainer = "_platform"

let private runBlob (run: IngestionRun) =
    let ts = run.StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fffffffZ")
    $"data-sources/{run.ScopeId}/runs/{run.DataSourceId}/{ts}-{run.RunId:N}.json"

let private runsPrefix (scopeId: string) (sourceId: DataSourceId) =
    $"data-sources/{scopeId}/runs/{sourceId}/"

// ─── JSON helpers ─────────────────────────────────────────────────

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let serializeStr (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

let private downloadAll<'T> (storage: IBlobStorage) (blobNames: string list) : Async<'T list> = async {
    let! results =
        blobNames
        |> List.map (fun name -> async {
            let! result = storage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes -> Json.tryDeserialize<'T> bytes
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

// ─── Object id derivation ─────────────────────────────────────────

/// `IDataObjectStore` object id derived from `(sourceId, table)`.
/// Double-underscore separator avoids slash collision with the
/// `IDataObjectStore` blob layout (`{container}/objects/{objectId}/...`).
/// Mirrors the `ResultObjectId.make` pattern from Phase 8.
let private resultObjectId (sourceId: DataSourceId) (table: string) = $"_dataingestion__{sourceId}__{table}"

// ─── DataIngestor ─────────────────────────────────────────────────
//
// Default `IDataIngestor` implementation. Resolves the config + the
// matching connector + the credential (via `ISecretStore` thunk
// pattern), runs `Connect → GetSchema → Query`, persists the schema as its own
// content-addressed object (Phase 832), writes the resulting bytes
// through `IDataObjectStore.Save` with `Versioned` policy so each
// refresh creates a new version while preserving history (Phase 7's
// immutability guarantee).
//
// Persistence: one `IngestionRun` blob per attempt under
// `_platform/data-sources/{scopeId}/runs/{sourceId}/{ts}-{runId:N}.json`.
// ISO-ordered timestamp prefix means `GetRecentRuns(_, _, count)` is
// `List + sortDescending + truncate count + Download` — the same
// hot-path shape as Phase 9b's `IJobStore.GetRecentRuns`.
//
// Lifecycle events: `IngestionRunCompleted` / `IngestionRunFailed`
// emitted to `IEventStore` under `SourceModule = DataIngestion-
// SourceModule` ("_platform.dataingestion") so audit + webhook
// subscribers pick them up automatically.

type DataIngestor
    (
        configStore: IDataSourceConfigStore,
        secretStore: ISecretStore,
        objectStore: IDataObjectStore,
        eventStore: IEventStore,
        connectors: IDataSource list,
        storage: IBlobStorage,
        logger: ILogger
    ) =

    let connectorByKind = connectors |> List.map (fun c -> c.Kind, c) |> Map.ofList

    let recordRun (run: IngestionRun) = async {
        try
            let bytes = Json.serialize run
            let! _ = storage.Upload(platformContainer, runBlob run, bytes)
            return ()
        with ex ->
            logger.Warn $"[DataIngestor] failed to persist IngestionRun {run.RunId}: {ex.Message}"
    }

    let emitEvent (scopeId: string) (eventType: string) (run: IngestionRun) = async {
        try
            let evt =
                Events.create scopeId DataIngestionSourceModule eventType (Json.serializeStr run)

            do! eventStore.Write evt
        with ex ->
            logger.Warn $"[DataIngestor] failed to write {eventType} event for run {run.RunId}: {ex.Message}"
    }

    let runFailed scopeId sourceId table runId startedAt err jobId : IngestionRun = {
        RunId = runId
        DataSourceId = sourceId
        ScopeId = scopeId
        Table = table
        StartedAt = startedAt
        CompletedAt = Some(DateTime.UtcNow)
        Status = IngestionStatus.Failed
        RowsIngested = None
        ResultObjectId = None
        Error = Some err
        JobId = jobId
    }

    // Phase 832 — fetch the connector's schema. Never fails the run: an
    // `Error`, a throw, or a "schema not available" answer (`Columns = []`)
    // all read as "no schema", and the payload then saves exactly as it did
    // before Phase 832 (no `schema-ref`).
    let fetchSchema (connector: IDataSource) (ctx: DataSourceCallContext) (table: string) = async {
        try
            match! connector.GetSchema(ctx, table) with
            | Ok schema when not (List.isEmpty schema.Columns) -> return Some schema
            | Ok _ -> return None
            | Error err ->
                logger.Info
                    $"[DataIngestor] GetSchema failed for '{ctx.Config.Id}'/{table}; ingesting without a schema-ref: {err}"

                return None
        with ex ->
            logger.Warn
                $"[DataIngestor] GetSchema threw for '{ctx.Config.Id}'/{table}; ingesting without a schema-ref: {ex.Message}"

            return None
    }

    // Phase 832 — persist the schema as its own content-addressed object,
    // BEFORE the payload that references it (so a dangling `schema-ref`
    // cannot occur; the worst case is an unreferenced schema version). The
    // schema object for `(sourceId, table)` gains a version only when the
    // schema changed, so an unchanged schema costs a read and no write.
    // Answers `(schemaRef, drift)`; `drift` is `None` when there is no
    // earlier recorded schema to compare with. `None` overall when the
    // schema could not be persisted — the payload then carries no ref.
    let persistSchema scopeId (sourceId: DataSourceId) (table: string) (schema: TableSchema) = async {
        let schemaId = IngestedPayload.schemaObjectId sourceId table
        let bytes = IngestedPayload.serializeSchema schema

        let! previous = objectStore.Get(scopeId, schemaId)

        let save () =
            objectStore.Save(
                scopeId,
                schemaId,
                bytes,
                IngestedPayload.SchemaDataType,
                "_system",
                Map.ofList [ "source-id", sourceId; "table", table ],
                Versioned
            )

        let saved drift = async {
            match! save () with
            | Ok schemaObject -> return Some(schemaObject.ContentHash, drift)
            | Error e ->
                logger.Warn
                    $"[DataIngestor] failed to persist schema for '{sourceId}'/{table}; ingesting without a schema-ref: {e}"

                return None
        }

        match previous with
        | Ok(prior, priorBytes) when priorBytes = bytes -> return Some(prior.ContentHash, Some false)
        | Ok _ -> return! saved (Some true)
        | Error DataObjectError.NotFound -> return! saved None
        | Error e ->
            // The earlier schema is unreadable: still record this one, but
            // do not claim to know whether it drifted.
            logger.Warn $"[DataIngestor] could not read the recorded schema for '{sourceId}'/{table}: {e}"
            return! saved None
    }

    interface IDataIngestor with
        member _.RunIngestion(scopeId, sourceId, table) = async {
            let runId = Guid.NewGuid()
            let startedAt = DateTime.UtcNow

            // 1. Resolve config
            match! configStore.Get(scopeId, sourceId) with
            | None ->
                let err =
                    UnexpectedFailure $"Data source '{sourceId}' not configured in scope '{scopeId}'"

                let run = runFailed scopeId sourceId table runId startedAt err None
                do! recordRun run
                do! emitEvent scopeId RunFailedEventType run
                return Error err
            | Some config ->

                // 2. Resolve connector
                match Map.tryFind config.Kind connectorByKind with
                | None ->
                    let err = UnexpectedFailure $"No IDataSource registered for Kind '{config.Kind}'"
                    let run = runFailed scopeId sourceId table runId startedAt err None
                    do! recordRun run
                    do! emitEvent scopeId RunFailedEventType run
                    return Error err
                | Some connector ->

                    // 3. Resolve credential. `None` is permitted at this
                    // stage — connectors that don't need a credential
                    // (in-memory fakes) will not consult `ctx.Credential`.
                    let! credential = secretStore.GetSecret(scopeId, config.CredentialKey)

                    let ctx: DataSourceCallContext = {
                        ScopeId = scopeId
                        Config = config
                        Credential = credential
                    }

                    // 4. Probe the source. Connect failures terminate before
                    // any persistence side-effect.
                    match! connector.Connect ctx with
                    | Error err ->
                        let run = runFailed scopeId sourceId table runId startedAt err None
                        do! recordRun run
                        do! emitEvent scopeId RunFailedEventType run
                        return Ok run
                    | Ok() ->

                        // 5. Fetch the schema (Phase 832) — between the probe
                        // and the query; persisted only once the query has
                        // produced a payload to attach it to.
                        let! observedSchema = fetchSchema connector ctx table

                        // 6. Run the query. Connector dialect varies — for
                        // in-memory the SQL string is the table name; for
                        // BigQuery / Redshift it is real SQL.
                        match! connector.Query(ctx, table) with
                        | Error err ->
                            let run = runFailed scopeId sourceId table runId startedAt err None
                            do! recordRun run
                            do! emitEvent scopeId RunFailedEventType run
                            return Ok run
                        | Ok bytes ->

                            // 7. Persist the schema first (Phase 832), then the
                            // payload through `IDataObjectStore` with `Versioned`
                            // policy — each refresh creates a new version,
                            // preserving history per Phase 7's contract.
                            let objectId = resultObjectId sourceId table

                            let! schemaRecord =
                                match observedSchema with
                                | Some schema -> persistSchema scopeId sourceId table schema
                                | None -> async { return None }

                            let schemaKeys =
                                match schemaRecord with
                                | None -> []
                                | Some(schemaRef, None) -> [ IngestedPayload.SchemaRefKey, schemaRef ]
                                | Some(schemaRef, Some changed) ->
                                    if changed then
                                        logger.Info
                                            $"[DataIngestor] schema drift on '{sourceId}'/{table}: schema-ref is now {schemaRef}"

                                    [
                                        IngestedPayload.SchemaRefKey, schemaRef
                                        IngestedPayload.SchemaDriftKey, (if changed then "true" else "false")
                                    ]

                            let metadata =
                                Map.ofList (
                                    [
                                        "source-id", sourceId
                                        "table", table
                                        "connector-kind", config.Kind
                                        IngestedPayload.PayloadFormatKey, IngestedPayload.CurrentPayloadFormat
                                    ]
                                    @ schemaKeys
                                )

                            let! saveResult =
                                objectStore.Save(
                                    scopeId,
                                    objectId,
                                    bytes,
                                    "data-ingestion",
                                    "_system",
                                    metadata,
                                    Versioned
                                )

                            match saveResult with
                            | Error e ->
                                let err = IngestionError.StorageFailure(string e)
                                let run = runFailed scopeId sourceId table runId startedAt err None
                                do! recordRun run
                                do! emitEvent scopeId RunFailedEventType run
                                return Ok run
                            | Ok dataObject ->
                                let run = {
                                    RunId = runId
                                    DataSourceId = sourceId
                                    ScopeId = scopeId
                                    Table = table
                                    StartedAt = startedAt
                                    CompletedAt = Some(DateTime.UtcNow)
                                    Status = IngestionStatus.Succeeded
                                    RowsIngested = None
                                    ResultObjectId = Some dataObject.ObjectId
                                    Error = None
                                    JobId = None
                                }

                                do! recordRun run
                                do! emitEvent scopeId RunCompletedEventType run
                                return Ok run
        }

        member _.GetRecentRuns(scopeId, sourceId, count) = async {
            let! names = storage.List(platformContainer, runsPrefix scopeId sourceId)
            let cap = min count maxRunsPerSource
            // Blob names start with the ISO-ordered timestamp prefix,
            // so descending name order = newest first.
            let recent = names |> List.sortDescending |> List.truncate cap
            let! runs = downloadAll<IngestionRun> storage recent
            return runs |> List.sortByDescending _.StartedAt
        }

let create
    (configStore: IDataSourceConfigStore)
    (secretStore: ISecretStore)
    (objectStore: IDataObjectStore)
    (eventStore: IEventStore)
    (connectors: IDataSource list)
    (storage: IBlobStorage)
    (logger: ILogger)
    : IDataIngestor =
    DataIngestor(configStore, secretStore, objectStore, eventStore, connectors, storage, logger) :> IDataIngestor