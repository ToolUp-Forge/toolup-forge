// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System
open System.Text.Json
open System.Threading
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 127 — async job-backed derivation ─────────────────────
//
// Fuses the background-job pattern into the AssetStore derivative
// cache for seconds-to-minutes-class derivations: a request for an
// uncached async-mode profile entry enqueues exactly one derivation
// job per (content hash, derivative name), returns
// `DerivationPending`, and completion surfaces over the existing
// SSE notification channel; the content-hash cache serves instantly
// thereafter.
//
// Coalescing has two layers: an in-process gate in the coordinator
// (concurrent requests on one node enqueue once), and the
// scheduler's `IdempotencyKey` dedup (a duplicate `Schedule` within
// the TTL returns the existing job). A duplicate run that slips
// through anyway is harmless — the handler re-checks the cache on
// entry and no-ops (stateless + idempotent, GP 12 rule 4).

module private DerivativeJobsJson =
    let private options = FableConverters.create ()
    let toJson (v: 'T) = JsonSerializer.Serialize(v, options)

    let tryFromJson<'T> (s: string) : 'T option =
        try
            Some(JsonSerializer.Deserialize<'T>(s, options))
        with _ ->
            None

/// Job payload — everything the handler needs, by value (GP 12
/// rule 1): no record re-resolution required to start work.
type DerivativeJobPayload = {
    ScopeContainer: string
    AssetId: string
    ContentHash: string
    /// Profile key — the registry is re-resolved fresh per attempt.
    ProfileId: string
    DerivativeName: string
    InputMime: string
}

/// Persisted per-(hash, name) derivation status. Lives at
/// `assets/derivative-status/{hash}/{name}.json`; absent once the
/// derivative is cached (the cache itself is the Ready state).
type DerivativeStatus =
    | StatusPending of correlationId: string * enqueuedAt: DateTimeOffset
    | StatusFailed of message: string * attempts: int * failedAt: DateTimeOffset

/// Payload published on the notification channel when an async
/// derivation completes (or terminally fails). Scope key = the
/// asset's scope container.
type DerivativeReadyNotification = {
    AssetId: string
    ContentHash: string
    DerivativeName: string
    /// `"Ready"` or `"Failed"`.
    Outcome: string
    Error: string option
}

[<RequireQualifiedAccess>]
module DerivativeJobs =

    [<Literal>]
    let HandlerName = "assetstore-derivative"

    /// `Notification.CustomNotification` key the completion event is
    /// published under. Clients subscribe to the scope and filter on
    /// this key — same contract shape as the KB ingestion-status
    /// notification.
    [<Literal>]
    let DerivativeReadyNotificationKey = "AssetStore.DerivativeReady"

    let statusKey (contentHash: string) (derivativeName: string) =
        sprintf "assets/derivative-status/%s/%s.json" contentHash derivativeName

    let internal readStatus (blob: IBlobStorage) (container: string) (hash: string) (name: string) = async {
        let! download = blob.Download(container, statusKey hash name)

        return
            match download with
            | Error _ -> None
            | Ok bytes -> DerivativeJobsJson.tryFromJson<DerivativeStatus> (System.Text.Encoding.UTF8.GetString bytes)
    }

    let internal writeStatus (blob: IBlobStorage) (container: string) (hash: string) (name: string) (status) = async {
        let bytes =
            System.Text.Encoding.UTF8.GetBytes(DerivativeJobsJson.toJson (status: DerivativeStatus))

        let! _ = blob.Upload(container, statusKey hash name, bytes)
        return ()
    }

    let internal clearStatus (blob: IBlobStorage) (container: string) (hash: string) (name: string) = async {
        let! _ = blob.Delete(container, statusKey hash name)
        return ()
    }

/// Enqueue seam between the request path and the scheduler. One
/// instance per deployment (constructed by `AssetCompose.run` when
/// async derivation is opted in — GP 13: no opt-in, no instance,
/// no job registration, no channel traffic).
type DerivativeJobCoordinator
    (blobStorage: IBlobStorage, scheduler: IJobScheduler, retryPolicy: JobRetryPolicy, logger: ILogger) =

    // In-process coalescing gate. Multi-node coalescing rides the
    // scheduler's IdempotencyKey dedup; a rare double-trigger is
    // absorbed by the handler's idempotent cache re-check.
    let gate = new SemaphoreSlim(1, 1)

    /// TTL for the scheduler-side idempotency window. Long enough to
    /// cover a queued + running derivation; short enough that a
    /// wedged job doesn't block re-derivation forever.
    member val IdempotencyTtl = TimeSpan.FromMinutes 30.0 with get, set

    /// Ensure exactly one derivation job is queued for (hash, name).
    /// Returns the current status: an existing pending correlation,
    /// a recorded failure, or the freshly-enqueued pending state.
    member this.EnsureQueued
        (scopeContainer: string, record: AssetRecord, derivativeName: string)
        : Async<DerivativeStatus> =
        async {
            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let hash = record.ContentHash

                let! existing = DerivativeJobs.readStatus blobStorage scopeContainer hash derivativeName

                match existing with
                | Some status -> return status
                | None ->
                    let payload: DerivativeJobPayload = {
                        ScopeContainer = scopeContainer
                        AssetId = AssetId.value record.Id
                        ContentHash = hash
                        ProfileId = DerivativeProfileId.value record.DerivativeProfile
                        DerivativeName = derivativeName
                        InputMime = record.MimeType
                    }

                    let registration: JobRegistration = {
                        ScopeId = scopeContainer
                        Handler = DerivativeJobs.HandlerName
                        Payload = DerivativeJobsJson.toJson payload
                        Trigger = Manual
                        Idempotency =
                            Some {
                                Key = sprintf "%s:%s:%s" DerivativeJobs.HandlerName hash derivativeName
                                TtlSeconds = int this.IdempotencyTtl.TotalSeconds
                            }
                        RetryPolicy = retryPolicy
                        ShardKey = Some hash
                        Precision = JobPrecision.Minute
                        CreatedBy = "_assetstore"
                        Tags = Map.ofList [ "subsystem", "assetstore"; "derivative", derivativeName ]
                    }

                    match! scheduler.Schedule registration with
                    | Error scheduleError ->
                        // Surface as a pending-less failure: the
                        // request path maps a StatusFailed to
                        // RenderFailed, so the caller sees a typed
                        // error rather than an eternal Pending.
                        let message = sprintf "derivation job scheduling failed: %A" scheduleError
                        logger.Warn(sprintf "[AssetStore] %s" message)
                        return StatusFailed(message, 0, DateTimeOffset.UtcNow)
                    | Ok jobId ->
                        let correlationId = string jobId
                        let pending = StatusPending(correlationId, DateTimeOffset.UtcNow)

                        do! DerivativeJobs.writeStatus blobStorage scopeContainer hash derivativeName pending

                        match! scheduler.TriggerOnce(scopeContainer, jobId, "_assetstore") with
                        | Ok() -> ()
                        | Error message -> logger.Warn(sprintf "[AssetStore] derivation TriggerOnce failed: %s" message)

                        return pending
            finally
                gate.Release() |> ignore
        }

/// `IJobHandler` for async derivations. Stateless between
/// invocations (GP 12 rule 4): every call re-reads the payload,
/// re-checks the cache (idempotent — a duplicate or re-run job is
/// a cheap no-op), re-resolves the profile entry, renders, writes
/// the cache, clears the status blob, and publishes the completion
/// notification.
type DerivativeJobHandler
    (
        blobStorage: IBlobStorage,
        profiles: DerivativeProfileRegistry,
        mimeRenderers: MimeRendererRegistry,
        notifications: INotificationChannel option,
        logger: ILogger,
        maxAttempts: int
    ) =

    let notify (container: string) (payload: DerivativeReadyNotification) = async {
        match notifications with
        | None -> ()
        | Some channel ->
            try
                do!
                    channel.Publish(
                        container,
                        CustomNotification(
                            DerivativeJobs.DerivativeReadyNotificationKey,
                            DerivativeJobsJson.toJson payload
                        )
                    )
            with ex ->
                logger.Warn(sprintf "[AssetStore] derivative notification publish failed: %s" ex.Message)
    }

    let derivativeCacheKey (hash: string) (spec: GeneralDerivativeSpec) =
        sprintf "assets/derivatives/%s/%s.%s" hash spec.Name spec.FileExtension

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            match DerivativeJobsJson.tryFromJson<DerivativeJobPayload> ctx.Payload with
            | None -> return PermanentFailure "malformed derivative-job payload"
            | Some payload ->
                let container = payload.ScopeContainer
                let hash = payload.ContentHash
                let name = payload.DerivativeName

                let ready () = {
                    AssetId = payload.AssetId
                    ContentHash = hash
                    DerivativeName = name
                    Outcome = "Ready"
                    Error = None
                }

                let recordFailure (message: string) = async {
                    do!
                        DerivativeJobs.writeStatus
                            blobStorage
                            container
                            hash
                            name
                            (StatusFailed(message, ctx.Attempt, DateTimeOffset.UtcNow))

                    do!
                        notify container {
                            ready () with
                                Outcome = "Failed"
                                Error = Some message
                        }
                }

                /// Terminal error — retrying will not change the
                /// outcome (bad payload shape, profile drift, decode
                /// failure).
                let permanent (message: string) = async {
                    do! recordFailure message
                    return PermanentFailure message
                }

                /// Retryable error — the scheduler re-runs per the
                /// registration's RetryPolicy; the Failed status is
                /// recorded only once attempts are exhausted, so the
                /// request path keeps answering Pending while retries
                /// remain.
                let transient (message: string) = async {
                    if ctx.Attempt >= maxAttempts then
                        do! recordFailure message

                    return TransientFailure message
                }

                // Resolve the profile entry fresh on every attempt —
                // no state survives between invocations (rule 4).
                match DerivativeProfileRegistry.resolveEntry (DerivativeProfileId payload.ProfileId) name profiles with
                | None ->
                    return! permanent (sprintf "profile '%s' no longer declares derivative '%s'" payload.ProfileId name)
                | Some(ImageDerivative _) ->
                    return!
                        permanent (
                            sprintf
                                "derivative '%s' resolved to a sync image entry — async jobs serve general entries only"
                                name
                        )
                | Some(GeneralDerivative spec) ->
                    let cacheKey = derivativeCacheKey hash spec

                    // Idempotency: a duplicate or restarted job that
                    // finds the cache already written is a no-op.
                    let! cached = blobStorage.Download(container, cacheKey)

                    match cached with
                    | Ok _ ->
                        do! DerivativeJobs.clearStatus blobStorage container hash name
                        do! notify container (ready ())
                        return Success
                    | Error _ ->
                        let! original = blobStorage.Download(container, sprintf "assets/originals/%s" hash)

                        match original with
                        | Error message -> return! transient (sprintf "original download failed: %s" message)
                        | Ok originalBytes ->
                            match MimeRendererRegistry.resolve spec.RendererKey mimeRenderers with
                            | None ->
                                return!
                                    permanent (sprintf "no MIME renderer registered under key '%s'" spec.RendererKey)
                            | Some renderer ->
                                let! rendered = renderer.Render(originalBytes, payload.InputMime, spec)

                                match rendered with
                                | Error(DecodeFailed message) -> return! permanent (sprintf "decode failed: %s" message)
                                | Error(EncodeFailed(format, message)) ->
                                    return! permanent (sprintf "%s encode failed: %s" format message)
                                | Error(DerivativeRenderError.RenderFailed message) -> return! transient message
                                | Ok(bytes, _mime) ->
                                    let! cacheWrite = blobStorage.Upload(container, cacheKey, bytes)

                                    match cacheWrite with
                                    | Error message -> return! transient (sprintf "cache write failed: %s" message)
                                    | Ok _ ->
                                        do! DerivativeJobs.clearStatus blobStorage container hash name
                                        do! notify container (ready ())
                                        return Success
        }