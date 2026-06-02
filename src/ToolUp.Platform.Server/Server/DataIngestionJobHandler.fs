module ToolUp.Platform.DataIngestionJobHandler

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── DataIngestionJobHandler ─────────────────────────────────────
//
// `IJobHandler` (Phase 9b) implementation that dispatches to
// `IDataIngestor.RunIngestion`. Registered with the scheduler at
// compose time under name `JobHandlerName`. The Fable.Remoting
// `IDataIngestionApi.TriggerRefresh` schedules a `Manual`-trigger
// `JobRegistration` against this handler; cron-driven refreshes
// schedule `CronTrigger` registrations the same way.
//
// **Payload shape (JSON):** `{ "sourceId": "...", "table": "..." }`.
// The handler deserialises and routes to `IDataIngestor`. Any
// deserialisation error or unknown source id surfaces as
// `JobResult.PermanentFailure` so the scheduler skips remaining
// retries and dead-letters — these errors do not recover by
// retrying.

[<Literal>]
let JobHandlerName = "_platform.dataingestion.run"

type RunPayload = { sourceId: string; table: string }

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let tryDeserialize (json: string) : RunPayload option =
        try
            Some(JsonSerializer.Deserialize<RunPayload>(json, options))
        with _ ->
            None

let payloadFor (sourceId: DataSourceId) (table: string) : string =
    Json.serialize { sourceId = sourceId; table = table }

type DataIngestionJobHandler(ingestor: IDataIngestor, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx) = async {
            match Json.tryDeserialize ctx.Payload with
            | None ->
                let msg =
                    sprintf "DataIngestionJobHandler: malformed payload for job %A — %s" ctx.JobId ctx.Payload

                logger.Warn msg
                return JobResult.PermanentFailure msg
            | Some payload ->
                let! result = ingestor.RunIngestion(ctx.ScopeId, payload.sourceId, payload.table)

                match result with
                | Error err ->
                    let msg =
                        sprintf
                            "DataIngestionJobHandler: setup error for source=%s table=%s — %A"
                            payload.sourceId
                            payload.table
                            err

                    logger.Warn msg
                    // Setup errors (config missing, connector unregistered)
                    // do not recover by retrying — escalate to dead-letter.
                    return JobResult.PermanentFailure msg
                | Ok run ->
                    match run.Status, run.Error with
                    | IngestionStatus.Succeeded, _ -> return JobResult.Success
                    | _, Some(CredentialMissing _) ->
                        // Operator must rotate the credential — retrying
                        // without intervention will not recover.
                        return JobResult.PermanentFailure(sprintf "%A" run.Error)
                    | _, Some(SchemaMismatch _) -> return JobResult.PermanentFailure(sprintf "%A" run.Error)
                    | _, Some err ->
                        // SourceUnreachable, StorageFailure,
                        // UnexpectedFailure are typically transient
                        // (rate limits, network blips, throttling).
                        return JobResult.TransientFailure(sprintf "%A" err)
                    | _ -> return JobResult.TransientFailure "ingestion run did not succeed; status unset"
        }

let create (ingestor: IDataIngestor) (logger: ILogger) : IJobHandler =
    DataIngestionJobHandler(ingestor, logger) :> IJobHandler