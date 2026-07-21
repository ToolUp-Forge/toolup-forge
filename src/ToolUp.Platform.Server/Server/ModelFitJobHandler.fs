// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 449 — fit-run envelope + IJobHandler ─────────────────────────
//
// The envelope orchestrates one fit: resolve the provider by kind, compute
// the forge-owned composite key, execute the provider, evaluate the gates,
// emit the audit trail, and return the assembled outcome. Every run is
// audited under `_platform.audit` carrying the composite key (GP 6). Gate
// failure is *data* — a `GateVerdict` in the outcome plus a
// `ModelFitGateFailed` audit row — never an exception (acceptance).
//
// Forge owns `CompositeKey` + `GateVerdicts`; the provider owns the
// artifact + diagnostics + its self-reported cost. The envelope overwrites
// whatever the provider set for the two forge-owned fields, so a fit's
// identity + gate outcome cannot be forged by a misbehaving provider.

module ModelFitEnvelope =
    /// STJ options with the full F# converter set — matches the
    /// Fable-compatible wire shape used everywhere else in the SDK, so a
    /// persisted `FitRequest` job payload round-trips faithfully (DU /
    /// Option / record / list / Map).
    let private jsonOptions = FableConverters.create ()

    /// Serialise a `FitRequest` to the persisted job-payload string.
    let serialiseRequest (request: FitRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a persisted job payload back into a `FitRequest`. `Error`
    /// carries the failure detail; the job handler treats a parse failure
    /// as a `PermanentFailure` (a malformed payload will not recover on
    /// retry).
    let tryParseRequest (payload: string) : Result<FitRequest, string> =
        try
            Ok(JsonSerializer.Deserialize<FitRequest>(payload, jsonOptions))
        with ex ->
            Error ex.Message

    /// Run one fit through the envelope. Resolves the provider, computes the
    /// forge-owned composite key, executes the provider, evaluates the
    /// requested gates, and emits `ModelFitStarted` / `ModelFitCompleted`
    /// (and `ModelFitGateFailed` when any gate fails). Returns the assembled
    /// outcome regardless of gate pass/fail — a failed gate is a verdict in
    /// the outcome, not an `Error`. `Error` is reserved for envelope-level
    /// failures (unknown kind, provider raised).
    let runFit
        (registry: ModelFitProviderRegistry)
        (audit: IAuditLog)
        (request: FitRequest)
        : Async<Result<FitOutcome, ModelFitError>> =
        async {
            match registry.TryResolve request.ProviderKind with
            | None -> return Error(ModelFitError.ProviderNotFound request.ProviderKind)
            | Some provider ->
                let key =
                    FitCompositeKey.compute
                        request.SpecRef.SpecHash
                        (DatasetVersionRef.key request.DatasetVersion)
                        request.Seed
                        provider.Kind
                        provider.ProviderVersion

                do!
                    audit.Record(
                        request.ScopeId,
                        ModelFitStarted {
                            CompositeKeyHash = key.Hash
                            SpecHash = key.SpecHash
                            DatasetVersion = key.DatasetVersion
                            Seed = key.Seed
                            ProviderId = key.ProviderId
                            ProviderVersion = key.ProviderVersion
                            ScopeId = request.ScopeId
                        }
                    )

                let! raw = async {
                    try
                        let! outcome = provider.Fit request
                        return Ok outcome
                    with ex ->
                        return Error ex.Message
                }

                match raw with
                | Error message -> return Error(ModelFitError.ProviderFailed(provider.Kind, message))
                | Ok providerOutcome ->
                    // Forge owns the composite key + gate verdicts — overwrite
                    // whatever the provider set so identity + gate outcome are
                    // authoritative regardless of provider behaviour.
                    let verdicts = Gate.evaluateAll providerOutcome.Diagnostics request.Gates

                    let outcome = {
                        providerOutcome with
                            CompositeKey = key
                            GateVerdicts = verdicts
                    }

                    let failed = verdicts |> List.filter (fun v -> not v.Passed)

                    do!
                        audit.Record(
                            request.ScopeId,
                            ModelFitCompleted {
                                CompositeKeyHash = key.Hash
                                ProviderId = key.ProviderId
                                ProviderVersion = key.ProviderVersion
                                DiagnosticCount = Map.count providerOutcome.Diagnostics
                                GatesEvaluated = List.length verdicts
                                GatesFailed = List.length failed
                                ArtifactHash = outcome.ArtifactRef.ContentHash
                                ScopeId = request.ScopeId
                            }
                        )

                    if not (List.isEmpty failed) then
                        do!
                            audit.Record(
                                request.ScopeId,
                                ModelFitGateFailed {
                                    CompositeKeyHash = key.Hash
                                    ProviderId = key.ProviderId
                                    FailedGates = failed |> List.map (fun v -> v.Name)
                                    ScopeId = request.ScopeId
                                }
                            )

                    return Ok outcome
        }

/// `IJobHandler` bound to `_platform.modelfit.run`. Deserialises a
/// `FitRequest` from the job payload and runs it through the envelope. A
/// malformed payload or an unknown provider kind is a `PermanentFailure`
/// (no retry recovers it); a provider exception is a `TransientFailure`
/// (the fit may succeed on a later attempt).
type ModelFitJobHandler(registry: ModelFitProviderRegistry, audit: IAuditLog, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match ModelFitEnvelope.tryParseRequest ctx.Payload with
            | Error e ->
                logger.Error($"ModelFitJobHandler: malformed FitRequest payload — {e}", None)
                return PermanentFailure $"malformed FitRequest payload: {e}"
            | Ok request ->
                match! ModelFitEnvelope.runFit registry audit request with
                | Ok _ -> return Success
                | Error(ModelFitError.ProviderNotFound k) ->
                    logger.Warn $"ModelFitJobHandler: no provider registered for kind '{k}'"
                    return PermanentFailure(ModelFitError.describe (ModelFitError.ProviderNotFound k))
                | Error(ModelFitError.ProviderFailed(k, m)) ->
                    logger.Warn $"ModelFitJobHandler: provider '{k}' failed — {m}"
                    return TransientFailure(ModelFitError.describe (ModelFitError.ProviderFailed(k, m)))
        }

module ModelFitJobHandler =
    /// Reserved scheduler handler name for the fit-run job.
    [<Literal>]
    let HandlerName = "_platform.modelfit.run"

    /// Construct the fit-run job handler.
    let create (registry: ModelFitProviderRegistry) (audit: IAuditLog) (logger: ILogger) : IJobHandler =
        ModelFitJobHandler(registry, audit, logger) :> IJobHandler

// ─── Phase 599 — batch fit submission ───────────────────────────────────
//
// A `FitRequestBatch` fans out to one `_platform.modelfit.batchitem` job
// per request under a single `ModelFitBatchSubmitted` audit row. Item
// execution is the unchanged Phase 449 envelope plus registry
// registration carrying the batch annotations (the item handler lives in
// `ModelFitBatchItemJobHandler.fs` — it needs `IModelRegistry`, which
// compiles later); a per-item failure fails only that item's job. The
// per-item idempotency key `{batch id}/{index}` makes a re-issued
// submission after a transient failure a no-op for the already-enqueued
// items rather than duplicate work.

/// Persisted payload of one batch item job: the batch correlation id, the
/// item's 0-based index, and its fit request.
type FitBatchItemPayload = {
    BatchId: string
    Index: int
    Request: FitRequest
}

/// The typed result of a batch submission: which items were enqueued (with
/// their `JobId`s, for per-item status polling) and which failed to
/// enqueue (reported as data — a schedule failure of one item never aborts
/// its siblings).
type FitBatchSubmission = {
    BatchId: string
    ScopeId: string
    ItemCount: int
    /// `(index, jobId)` per successfully enqueued item, in batch order.
    ScheduledJobs: (int * JobId) list
    /// `(index, reason)` per item whose enqueue failed, in batch order.
    ScheduleFailures: (int * string) list
}

module ModelFitBatch =
    /// Reserved scheduler handler name for one batch item's fit run.
    [<Literal>]
    let ItemHandlerName = "_platform.modelfit.batchitem"

    let private jsonOptions = FableConverters.create ()

    /// Serialise a batch item payload to the persisted job-payload string.
    let serialiseItem (item: FitBatchItemPayload) : string =
        JsonSerializer.Serialize(item, jsonOptions)

    /// Parse a persisted batch item payload. `Error` detail is diagnostic;
    /// the item handler treats a parse failure as a `PermanentFailure`.
    let tryParseItem (payload: string) : Result<FitBatchItemPayload, string> =
        try
            Ok(JsonSerializer.Deserialize<FitBatchItemPayload>(payload, jsonOptions))
        with ex ->
            Error ex.Message

    let private describeScheduleError (e: ScheduleError) : string =
        match e with
        | InvalidCron(expr, reason) -> $"invalid cron '{expr}': {reason}"
        | HandlerNotRegistered name -> $"handler '{name}' is not registered"
        | PrecisionUnsupported(supplied, _) -> $"precision {supplied} unsupported"
        | ScheduleError.StorageFailure m -> $"schedule storage failure: {m}"

    /// Submit a batch: validate (typed, before any work), audit the batch
    /// as a unit, then enqueue + trigger one item job per request. Per-item
    /// enqueue failures are collected as data, never thrown, and never
    /// abort sibling items. Requires `ItemHandlerName` to be registered
    /// with the scheduler — consumer-wired, like the Phase 454 scoring
    /// handler: `scheduler.RegisterHandler(ModelFitBatch.ItemHandlerName,
    /// ModelFitBatchItemJobHandler.create providers modelRegistry audit
    /// logger)`.
    let submit
        (scheduler: IJobScheduler)
        (audit: IAuditLog)
        (submittedBy: string)
        (batch: FitRequestBatch)
        : Async<Result<FitBatchSubmission, FitBatchError>> =
        async {
            match FitRequestBatch.validate batch with
            | Error e -> return Error e
            | Ok() ->
                do!
                    audit.Record(
                        batch.ScopeId,
                        ModelFitBatchSubmitted {
                            BatchId = batch.BatchId
                            ItemCount = List.length batch.Requests
                            SubmittedBy = submittedBy
                            ScopeId = batch.ScopeId
                        }
                    )

                let scheduled = ResizeArray<int * JobId>()
                let failures = ResizeArray<int * string>()

                for index, request in List.indexed batch.Requests do
                    let payload =
                        serialiseItem {
                            BatchId = batch.BatchId
                            Index = index
                            Request = request
                        }

                    let registration = {
                        ScopeId = batch.ScopeId
                        Handler = ItemHandlerName
                        Payload = payload
                        Trigger = Manual
                        Idempotency =
                            Some {
                                Key = $"modelfit.batch/{batch.BatchId}/{index}"
                                TtlSeconds = 3600
                            }
                        RetryPolicy = JobRetryPolicy.defaults
                        ShardKey = None
                        Precision = JobPrecision.Minute
                        CreatedBy = submittedBy
                        Tags = Map [ "batch.id", batch.BatchId ]
                    }

                    match! scheduler.Schedule registration with
                    | Error e -> failures.Add(index, describeScheduleError e)
                    | Ok jobId ->
                        match! scheduler.TriggerOnce(batch.ScopeId, jobId, submittedBy) with
                        | Ok() -> scheduled.Add(index, jobId)
                        | Error reason -> failures.Add(index, $"trigger failed: {reason}")

                return
                    Ok {
                        BatchId = batch.BatchId
                        ScopeId = batch.ScopeId
                        ItemCount = List.length batch.Requests
                        ScheduledJobs = List.ofSeq scheduled
                        ScheduleFailures = List.ofSeq failures
                    }
        }