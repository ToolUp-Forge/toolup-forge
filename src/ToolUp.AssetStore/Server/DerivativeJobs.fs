// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System
open System.Text.Json
open System.Threading
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Metrics

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

// ─── Phase 207 — dead-letter + retry observability ───────────────
//
// Phase 127 already records a terminal `StatusFailed` once a
// derivation exhausts its retry budget, so the request path answers
// a typed error rather than an eternal Pending. What it does NOT do
// is leave anything an operator can sweep: the failure lives only in
// the per-(hash, name) status blob the next successful derivation
// clears, no counter moves, and the only notification is the ready
// channel's `Outcome = "Failed"` — indistinguishable, to a
// subscriber filtering on the ready key, from a completion.
//
// This phase adds the three surfaces that make an exhausted
// derivation observable — a dead-letter record, a dedicated failure
// notification, and retry / failure counters — behind
// `DerivativeObservability`. Every field of that record is off in
// `DerivativeObservability.disabled`, which is what the Phase 127
// handler constructor supplies, so a deployment that does not opt in
// through `AssetStoreServerAppModule.withDerivativeDlq` runs the
// unchanged Phase 127 path: no extra blob write, no extra publish,
// no sink resolution (GP 11 / GP 13).

/// Persisted dead-letter record for a derivation that exhausted its
/// bounded retry budget. Lives at
/// `assets/derivative-dlq/{hash}/{name}.json` — beside, not inside,
/// the status blob, so clearing a status (the next successful
/// derivation does) never erases the operator's record of the
/// failure. Every field is by value (GP 12 rule 1): a sweep tool
/// re-drives the derivation from this record alone.
type DerivativeDeadLetterRecord = {
    ScopeContainer: string
    AssetId: string
    ContentHash: string
    DerivativeName: string
    ProfileId: string
    /// The final attempt's error message — the same text
    /// `DerivativeStatus.StatusFailed` carries.
    Error: string
    /// Attempt number the budget was exhausted on.
    Attempts: int
    FailedAt: DateTimeOffset
    /// `JobRetryPolicy.DeadLetterDestination` verbatim when the
    /// deployment declared one. `None` means the record was written
    /// locally only — the SDK never routes to a destination itself,
    /// it carries the operator's string through for a companion to
    /// interpret (GP 12 rule 3).
    Destination: string option
}

/// Payload published on the notification channel when an async
/// derivation exhausts its retry budget. Mirrors
/// `DerivativeReadyNotification` in shape and scope key, on its own
/// notification key so a subscriber can filter terminal failures
/// without parsing the ready payload's `Outcome` field.
type DerivativeFailedNotification = {
    AssetId: string
    ContentHash: string
    DerivativeName: string
    Error: string
    Attempts: int
    FailedAt: DateTimeOffset
    /// `true` when a `DerivativeDeadLetterRecord` was persisted for
    /// this failure — so a subscriber knows whether a sweep will
    /// find it.
    DeadLettered: bool
}

/// Opt-in observability posture for `DerivativeJobHandler`.
/// `DerivativeObservability.disabled` is the Phase 127 behaviour and
/// the value the six-argument handler constructor supplies.
type DerivativeObservability = {
    /// Write a `DerivativeDeadLetterRecord` on terminal failure.
    RecordDeadLetters: bool
    /// Publish a `DerivativeFailedNotification` on terminal failure.
    NotifyOnFailure: bool
    /// Sink for the retry / failure counters. `None` emits none.
    Metrics: IMetricsSink option
    /// Carried into the dead-letter record; the SDK does not route
    /// to it.
    DeadLetterDestination: string option
}

module DerivativeObservability =
    /// The Phase 127 posture: no dead-letter record, no failure
    /// notification, no counters, nothing resolved from DI.
    let disabled: DerivativeObservability = {
        RecordDeadLetters = false
        NotifyOnFailure = false
        Metrics = None
        DeadLetterDestination = None
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

    /// Phase 207 — `Notification.CustomNotification` key a terminal
    /// derivation failure is published under. Distinct from
    /// `DerivativeJobs.DerivativeReadyNotificationKey` on purpose:
    /// the ready key's payload has always carried an
    /// `Outcome = "Failed"` variant, but a subscriber wanting only
    /// terminal failures had to parse the payload to find it.
    /// Published only when the deployment opts in.
    [<Literal>]
    let DerivativeFailedNotificationKey = "AssetStore.DerivativeFailed"

    /// Counter incremented once per retryable attempt that did NOT
    /// exhaust the budget — the leading indicator, visible while
    /// derivations are still recovering on their own.
    [<Literal>]
    let RetryMetric = "assetstore.derivative.retry"

    /// Counter incremented once per derivation that exhausted its
    /// bounded retry budget or failed permanently.
    [<Literal>]
    let FailedMetric = "assetstore.derivative.failed"

    let statusKey (contentHash: string) (derivativeName: string) =
        sprintf "assets/derivative-status/%s/%s.json" contentHash derivativeName

    /// Phase 207 — dead-letter blob key for (hash, name). Kept out
    /// of the `assets/derivative-status/` prefix so a status clear
    /// never removes the operator's record.
    let deadLetterKey (contentHash: string) (derivativeName: string) =
        sprintf "assets/derivative-dlq/%s/%s.json" contentHash derivativeName

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

    /// Phase 207 — persist a dead-letter record. Called only on the
    /// opted-in path.
    let internal writeDeadLetter (blob: IBlobStorage) (record: DerivativeDeadLetterRecord) = async {
        let bytes = System.Text.Encoding.UTF8.GetBytes(DerivativeJobsJson.toJson record)

        let! _ = blob.Upload(record.ScopeContainer, deadLetterKey record.ContentHash record.DerivativeName, bytes)

        return ()
    }

    /// Read a persisted dead-letter record, if one exists. The read
    /// half of the sweep surface — an operator tool (or a test)
    /// re-drives a failed derivation from the returned record.
    let readDeadLetter
        (blob: IBlobStorage)
        (container: string)
        (hash: string)
        (name: string)
        : Async<DerivativeDeadLetterRecord option> =
        async {
            let! download = blob.Download(container, deadLetterKey hash name)

            return
                match download with
                | Error _ -> None
                | Ok bytes ->
                    DerivativeJobsJson.tryFromJson<DerivativeDeadLetterRecord> (
                        System.Text.Encoding.UTF8.GetString bytes
                    )
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
        maxAttempts: int,
        observability: DerivativeObservability
    ) =

    /// Counter emission. Swallows sink faults for the same reason
    /// `IMetricsSink` is fire-and-forget: an observability failure
    /// must never change a derivation's outcome.
    let count (metric: string) (derivativeName: string) =
        match observability.Metrics with
        | None -> ()
        | Some sink ->
            try
                sink.Increment(metric, Map.ofList [ "derivative", derivativeName ])
            with ex ->
                logger.Warn(sprintf "[AssetStore] derivative metric emission failed: %s" ex.Message)

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

    /// Phase 207 — the terminal-failure publish, on its own
    /// notification key. Reached only when the deployment opted in.
    let notifyFailed (container: string) (payload: DerivativeFailedNotification) = async {
        match notifications with
        | None -> ()
        | Some channel ->
            try
                do!
                    channel.Publish(
                        container,
                        CustomNotification(
                            DerivativeJobs.DerivativeFailedNotificationKey,
                            DerivativeJobsJson.toJson payload
                        )
                    )
            with ex ->
                logger.Warn(sprintf "[AssetStore] derivative failure notification publish failed: %s" ex.Message)
    }

    let derivativeCacheKey (hash: string) (spec: GeneralDerivativeSpec) =
        sprintf "assets/derivatives/%s/%s.%s" hash spec.Name spec.FileExtension

    /// Phase 127 shape, preserved verbatim. A deployment that has not
    /// opted into the Phase 207 surface constructs through this
    /// overload and gets `DerivativeObservability.disabled` — the
    /// unchanged handler. An explicit secondary constructor rather
    /// than an optional parameter, so the six-argument token stays in
    /// the public surface instead of folding into one widened form.
    new
        (
            blobStorage: IBlobStorage,
            profiles: DerivativeProfileRegistry,
            mimeRenderers: MimeRendererRegistry,
            notifications: INotificationChannel option,
            logger: ILogger,
            maxAttempts: int
        ) =
        DerivativeJobHandler(
            blobStorage,
            profiles,
            mimeRenderers,
            notifications,
            logger,
            maxAttempts,
            DerivativeObservability.disabled
        )

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

                /// Phase 207 — the dead-letter half, skipped entirely
                /// when not opted in. Returns whether a record was
                /// persisted, which the failure notification carries
                /// so a subscriber knows a sweep will find it. A
                /// write fault is logged and swallowed: recording the
                /// failure must not change the job's outcome.
                let tryWriteDeadLetter (message: string) (failedAt: DateTimeOffset) = async {
                    if not observability.RecordDeadLetters then
                        return false
                    else
                        try
                            do!
                                DerivativeJobs.writeDeadLetter blobStorage {
                                    ScopeContainer = container
                                    AssetId = payload.AssetId
                                    ContentHash = hash
                                    DerivativeName = name
                                    ProfileId = payload.ProfileId
                                    Error = message
                                    Attempts = ctx.Attempt
                                    FailedAt = failedAt
                                    Destination =
                                        observability.DeadLetterDestination |> Option.orElse ctx.DeadLetterDestination
                                }

                            return true
                        with ex ->
                            logger.Warn(sprintf "[AssetStore] dead-letter record write failed: %s" ex.Message)

                            return false
                }

                let recordFailure (message: string) = async {
                    let failedAt = DateTimeOffset.UtcNow

                    do!
                        DerivativeJobs.writeStatus
                            blobStorage
                            container
                            hash
                            name
                            (StatusFailed(message, ctx.Attempt, failedAt))

                    do!
                        notify container {
                            ready () with
                                Outcome = "Failed"
                                Error = Some message
                        }

                    // Phase 207 — every line below is inert under
                    // `DerivativeObservability.disabled`.
                    count DerivativeJobs.FailedMetric name

                    let! deadLettered = tryWriteDeadLetter message failedAt

                    if observability.NotifyOnFailure then
                        do!
                            notifyFailed container {
                                AssetId = payload.AssetId
                                ContentHash = hash
                                DerivativeName = name
                                Error = message
                                Attempts = ctx.Attempt
                                FailedAt = failedAt
                                DeadLettered = deadLettered
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
                    else
                        // Phase 207 — the leading indicator: budget
                        // still has room, so this attempt is a retry
                        // rather than a failure. Inert with no sink.
                        count DerivativeJobs.RetryMetric name

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