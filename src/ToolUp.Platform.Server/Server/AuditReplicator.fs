module ToolUp.Platform.AuditReplicator

open System
open System.Threading
open System.Threading.Channels
open Microsoft.Extensions.Hosting
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 9g — Audit replicator ────────────────────────────────────
//
// `AuditReplicator` is the SDK-default `BackgroundService` that mirrors
// every event written under `SourceModule = "_platform.audit"` to one
// or more `IAuditSink`s. It runs only when `ServerConfig.AuditSinks`
// is non-empty.
//
// Two pumps feed the same per-batch delivery code:
//
//   1. Live hook — `AuditReplicationHookedEventStore` decorator pushes
//      every audit `Write` (filtered to skip the replicator's own
//      AuditSink* events) into a per-sink bounded channel. Worker
//      drains the channel, batches events by `ScopeId`, and calls
//      `IAuditSink.Deliver`. Sub-second steady-state latency.
//
//   2. Catch-up sweep — periodic timer (default 5 min) calls
//      `IEventStore.ListScopes` and `ReadBySource` per scope, filters
//      events strictly newer than the persisted cursor, and delivers
//      missed events. Recovers events dropped on bounded-channel
//      overflow, sink-down windows that didn't trigger restart, or
//      events written by a sibling silo (Phase 9c half 2).
//
// Both pumps advance the same per-`(sinkName, scopeId)` cursor; a
// `SemaphoreSlim` per (sinkName, scopeId) serialises them so a sweep
// and a live-hook delivery for the same scope don't race.
//
// Single-instance limitation. The bounded channel and per-scope
// semaphores are in-process; running multiple silos with the same
// sink configuration would each consume the post-write hook and
// double-deliver. The cursor write is monotonic so duplicates are
// limited to a small window, but at-most-once is not guaranteed for
// distributed deployments. Phase 9c half 2's distributed lock will
// promote one replicator to leader.

let private cursorContainer = "_platform"

let private cursorJsonOptions = FableConverters.create ()

let private toCursorJson (cursor: AuditReplicatorCursor) =
    JsonSerializer.Serialize(cursor, cursorJsonOptions)

let private fromCursorJson (json: string) =
    JsonSerializer.Deserialize<AuditReplicatorCursor>(json, cursorJsonOptions)

let private cursorBlobName (sinkName: string) (scopeId: string) =
    $"audit-replicator/{sinkName}/{scopeId}.cursor"

/// Predicate matching the three replicator-emitted audit event types.
/// The hook decorator filters these out so the replicator does not
/// recursively process its own state-change events. Centralised here so
/// the contract pack (and any future test) can use the same predicate.
let isReplicatorSelfEvent (eventType: string) : bool =
    eventType = "AuditSinkDelivered"
    || eventType = "AuditSinkFailed"
    || eventType = "AuditSinkDeadLettered"

/// Predicate for events that should flow into the replicator's queue:
/// audit events that are NOT replicator-self events.
let shouldReplicate (evt: ModuleEvent) : bool =
    evt.SourceModule = AuditSourceModule.value
    && not (isReplicatorSelfEvent evt.EventType)

// ─── Cursor store ───────────────────────────────────────────────────

/// Cursor persistence over `IBlobStorage`. Cursors are tiny JSON blobs;
/// reads on missing cursors return `AuditReplicatorCursor.empty` (the
/// "no events delivered yet" state). Writes overwrite — the cursor is
/// monotonic so concurrent writers either land on the same value or the
/// later writer wins, which is correct.
type IAuditReplicatorCursorStore =
    abstract Load: sinkName: string * scopeId: string -> Async<AuditReplicatorCursor>
    abstract Save: sinkName: string * scopeId: string * cursor: AuditReplicatorCursor -> Async<unit>

type BlobAuditReplicatorCursorStore(blobStorage: IBlobStorage, logger: ILogger) =
    interface IAuditReplicatorCursorStore with
        member _.Load(sinkName, scopeId) = async {
            let blobName = cursorBlobName sinkName scopeId

            try
                let! result = blobStorage.Download(cursorContainer, blobName)

                match result with
                | Ok bytes ->
                    let json = System.Text.Encoding.UTF8.GetString bytes
                    return fromCursorJson json
                | Error _ ->
                    // First-time cursor read or transient blob error —
                    // both treated as "no events delivered yet" so the
                    // catch-up sweep delivers the full audit history.
                    return AuditReplicatorCursor.empty
            with ex ->
                logger.Warn(
                    sprintf
                        "[AuditReplicator] cursor load failed sink=%s scope=%s: %s — defaulting to empty"
                        sinkName
                        scopeId
                        ex.Message
                )

                return AuditReplicatorCursor.empty
        }

        member _.Save(sinkName, scopeId, cursor) = async {
            let blobName = cursorBlobName sinkName scopeId
            let json = toCursorJson cursor
            let bytes = System.Text.Encoding.UTF8.GetBytes json

            try
                let! _ = blobStorage.Upload(cursorContainer, blobName, bytes)
                return ()
            with ex ->
                // Cursor write failures are visible at the next sweep —
                // we'll re-deliver the batch (sink is idempotent). Log
                // at Warn so operators see drift but don't escalate.
                logger.Warn(
                    sprintf
                        "[AuditReplicator] cursor save failed sink=%s scope=%s: %s — next sweep will retry"
                        sinkName
                        scopeId
                        ex.Message
                )
        }

// ─── Hook decorator ─────────────────────────────────────────────────

/// `IEventStore` decorator that fires the replicator's enqueue function
/// after every successful `Write` of a non-self audit event. Filters via
/// `shouldReplicate` so non-audit events and the replicator's own
/// state-change events pass through silently.
///
/// Read paths are unchanged; this decorator only intercepts `Write`.
/// Multiple decorators stack in `compose` — `AuditReplicationHookedEventStore`
/// wraps `JobNotifyEventStore` wraps `WebhookHookedEventStore` wraps the
/// inner store, so audit hooks fire alongside webhook hooks.
type AuditReplicationHookedEventStore(inner: IEventStore, enqueue: ModuleEvent -> unit) =
    interface IEventStore with
        member _.Write(evt) = async {
            do! inner.Write evt

            if shouldReplicate evt then
                enqueue evt
        }

        member _.ReadAll(scopeId) = inner.ReadAll(scopeId)

        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

// ─── BackgroundService ──────────────────────────────────────────────

[<Literal>]
let private SourceModule = "_platform.audit-replicator"

/// One-batch delivery context. Internal to the worker; not exposed on
/// the public surface.
type private DeliveryItem = { Event: ModuleEvent }

/// The replicator. Constructed in `compose` only when
/// `ServerConfig.AuditSinks` is non-empty. Owns one bounded channel per
/// sink (live-hook ingress) plus one shared catch-up timer.
type AuditReplicator
    (
        sinks: IAuditSink list,
        cursorStore: IAuditReplicatorCursorStore,
        eventStore: IEventStore,
        auditLog: IAuditLog,
        options: AuditReplicatorOptions,
        samplingPolicy: AuditSamplingPolicy,
        logger: ILogger
    ) =
    inherit BackgroundService()

    // ─── Sink-level state ───────────────────────────────────────────
    //
    // Per-sink resources:
    //   * `Channel<DeliveryItem>` — bounded ingress queue from the
    //     live-hook decorator.
    //   * `Map<scopeId, SemaphoreSlim>` (lazy) — per-scope serialisation
    //     so live-hook + catch-up sweep don't race for the same cursor.
    //
    // Phase 9g / Phase 9c rule 4: handlers stateless across calls —
    // the channel and semaphores are in-process state, but the cursor
    // (which is the source of truth) lives in `IBlobStorage`. A
    // distributed companion replaces the channel with a partition-keyed
    // durable queue and the semaphores with `IDistributedLock` (Phase 9i)
    // without changing the interface.

    let channels =
        sinks
        |> List.map (fun s ->
            let ch =
                Channel.CreateBounded<DeliveryItem>(
                    BoundedChannelOptions(
                        options.BatchPolicy.QueueCapacity,
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = true
                    )
                )

            s.Name, ch)
        |> Map.ofList

    let scopeLocks =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, SemaphoreSlim>()

    let getScopeLock (sinkName: string) (scopeId: string) =
        scopeLocks.GetOrAdd((sinkName, scopeId), fun _ -> new SemaphoreSlim(1, 1))

    // ─── Audit emission ─────────────────────────────────────────────

    let emitDelivered (sinkName: string) (scopeId: string) (batchSize: int) (lastDeliveredAt: DateTime) = async {
        let payload: AuditSinkDeliveredPayload = {
            SinkName = sinkName
            BatchScopeId = scopeId
            BatchSize = batchSize
            LastDeliveredAt = lastDeliveredAt
            DeliveredAt = DateTime.UtcNow
        }
        // Emitted under `_platform` for operator-level visibility; the
        // anti-recursion guard in the hook decorator prevents this
        // event from re-entering the replicator pipeline.
        // Emitted under `_platform` for operator-level visibility.
        do! auditLog.Record("_platform", AuditSinkDelivered payload)
    }

    let emitFailed (sinkName: string) (scopeId: string) (batchSize: int) (attempt: int) (error: string) = async {
        let payload: AuditSinkFailedPayload = {
            SinkName = sinkName
            BatchScopeId = scopeId
            BatchSize = batchSize
            AttemptNumber = attempt
            Error = error
            FailedAt = DateTime.UtcNow
        }

        do! auditLog.Record("_platform", AuditSinkFailed payload)
    }

    let emitDeadLettered (sinkName: string) (scopeId: string) (batchSize: int) (attempts: int) (lastError: string) = async {
        let payload: AuditSinkDeadLetteredPayload = {
            SinkName = sinkName
            BatchScopeId = scopeId
            BatchSize = batchSize
            AttemptCount = attempts
            LastError = lastError
            DeadLetteredAt = DateTime.UtcNow
        }

        do! auditLog.Record("_platform", AuditSinkDeadLettered payload)
    }

    // ─── Per-batch delivery ─────────────────────────────────────────
    //
    // `deliverBatch` is the single delivery code path. Called by both
    // the live-hook drain loop and the catch-up sweep. Holds the
    // per-(sinkName, scopeId) semaphore for the duration of
    // delivery + cursor write — that interlocks the two pumps.

    let deliverBatch (sink: IAuditSink) (scopeId: string) (batch: ModuleEvent list) = async {
        if List.isEmpty batch then
            return ()
        else
            // Filter against the persisted cursor BEFORE delivery.
            // Without this filter, the live-hook drain loop and the
            // catch-up sweep race for the same events: the sweep
            // delivers and advances the cursor, then the live hook
            // (which received the same event from the decorator's
            // enqueue side-effect) delivers it AGAIN. The semaphore
            // serialises the two pumps but doesn't dedup their
            // payloads. Loading the cursor inside the semaphore-held
            // critical section gives us at-most-once steady-state
            // delivery while preserving at-least-once across restart
            // (because the cursor is durable in `IBlobStorage`).
            let! cursor = cursorStore.Load(sink.Name, scopeId)

            let pending = batch |> List.filter (AuditReplicatorCursor.isAfter cursor)

            if List.isEmpty pending then
                return ()
            else

                // Sort by (OccurredAt, Id) — same total order the cursor
                // uses. The sink receives events in this order; per-scope
                // ordering is preserved.
                let ordered =
                    pending
                    |> List.sortWith (fun a b ->
                        let cmp = compare a.OccurredAt b.OccurredAt
                        if cmp <> 0 then cmp else a.Id.CompareTo b.Id)

                // Decode each `ModuleEvent` back into an `AuditEvent` for
                // the sink. Decoder lives in `AuditLog.fs` as a private
                // helper; we re-derive it from `IAuditLog.GetAuditTrail`'s
                // shape here. Events that fail to decode (wire format
                // drift, corrupt blob) are skipped with a Warn — the
                // replicator preserves the audit trail's append-only
                // contract by never blocking on a single bad event.
                //
                // Gap audit #3 — track decode failures so we can emit an
                // `AuditEventDecodeFailed` audit row after the loop. Without
                // this, schema drift / corrupt payloads silently disappear
                // from the external sink trail; SOC 2 / Article 30 / SOX
                // compliance assertions become unverifiable.
                let decodeFailures = ResizeArray<Guid * string>()

                let decodedPairs =
                    ordered
                    |> List.choose (fun evt ->
                        // We cannot import the private `decodeAuditEvent`
                        // from `AuditLog.fs`; instead, recover via
                        // `IAuditLog.GetAuditTrail` would be wasteful.
                        // Inline the decode using the same JSON settings.
                        try
                            let json = evt.Payload
                            // Match every known case; defensive on the wire
                            // format. Mirrors `AuditLog.decodeAuditEvent`.
                            let decoded: AuditEvent option =
                                match evt.EventType with
                                | "UserLoggedIn" ->
                                    Some(
                                        UserLoggedIn(
                                            JsonSerializer.Deserialize<UserLoggedInPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "TeamCreated" ->
                                    Some(
                                        TeamCreated(
                                            JsonSerializer.Deserialize<TeamCreatedPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "MemberAdded" ->
                                    Some(
                                        MemberAdded(
                                            JsonSerializer.Deserialize<MemberAddedPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "MemberRemoved" ->
                                    Some(
                                        MemberRemoved(
                                            JsonSerializer.Deserialize<MemberRemovedPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "MemberRoleChanged" ->
                                    Some(
                                        MemberRoleChanged(
                                            JsonSerializer.Deserialize<MemberRoleChangedPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "FileUploaded" ->
                                    Some(
                                        FileUploaded(
                                            JsonSerializer.Deserialize<FileUploadedPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "FileDeleted" ->
                                    Some(
                                        FileDeleted(
                                            JsonSerializer.Deserialize<FileDeletedPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "AnalysisRun" ->
                                    Some(
                                        AnalysisRun(
                                            JsonSerializer.Deserialize<AnalysisRunPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "PermissionChanged" ->
                                    Some(
                                        PermissionChanged(
                                            JsonSerializer.Deserialize<PermissionChangedPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "NotificationSent" ->
                                    Some(
                                        NotificationSent(
                                            JsonSerializer.Deserialize<NotificationSentPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "NotificationDeliveryFailed" ->
                                    Some(
                                        NotificationDeliveryFailed(
                                            JsonSerializer.Deserialize<NotificationDeliveryFailedPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "HealthStateChanged" ->
                                    Some(
                                        HealthStateChanged(
                                            JsonSerializer.Deserialize<HealthStateChangedPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "EncryptionKeyCreated" ->
                                    Some(
                                        EncryptionKeyCreated(
                                            JsonSerializer.Deserialize<EncryptionKeyEventPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "EncryptionKeyRotated" ->
                                    Some(
                                        EncryptionKeyRotated(
                                            JsonSerializer.Deserialize<EncryptionKeyEventPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "EncryptionKeyDestroyed" ->
                                    Some(
                                        EncryptionKeyDestroyed(
                                            JsonSerializer.Deserialize<EncryptionKeyEventPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "EntityCreated" ->
                                    Some(
                                        EntityCreated(
                                            JsonSerializer.Deserialize<EntityLifecycleEventPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "EntityUpdated" ->
                                    Some(
                                        EntityUpdated(
                                            JsonSerializer.Deserialize<EntityLifecycleEventPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "EntityDeleted" ->
                                    Some(
                                        EntityDeleted(
                                            JsonSerializer.Deserialize<EntityLifecycleEventPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "FormSubmitted" ->
                                    Some(
                                        FormSubmitted(
                                            JsonSerializer.Deserialize<FormSubmittedPayload>(json, cursorJsonOptions)
                                        )
                                    )
                                | "FormSubmissionUpdated" ->
                                    Some(
                                        FormSubmissionUpdated(
                                            JsonSerializer.Deserialize<FormSubmissionUpdatedPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | "WorkflowTransitioned" ->
                                    Some(
                                        WorkflowTransitioned(
                                            JsonSerializer.Deserialize<WorkflowTransitionedPayload>(
                                                json,
                                                cursorJsonOptions
                                            )
                                        )
                                    )
                                | _ -> None

                            decoded |> Option.map (fun audit -> evt, audit)
                        with _ ->
                            logger.Warn(
                                sprintf
                                    "[AuditReplicator] decode failed eventId=%O eventType=%s scope=%s — skipping"
                                    evt.Id
                                    evt.EventType
                                    evt.ScopeId
                            )

                            decodeFailures.Add((evt.Id, evt.EventType))
                            None)

                // Gap audit #3 — emit a summary `AuditEventDecodeFailed`
                // audit event when the batch contained at least one
                // undecodable event. One row per batch (not per failure)
                // so a schema-drift sweep doesn't amplify into hundreds
                // of audit rows. Bounded payload (50 ids / 50 types) keeps
                // the row size predictable.
                if decodeFailures.Count > 0 then
                    let failedIds = decodeFailures |> Seq.map fst |> Seq.truncate 50 |> List.ofSeq

                    let failedTypes =
                        decodeFailures |> Seq.map snd |> Seq.distinct |> Seq.truncate 50 |> List.ofSeq

                    let payload: AuditEventDecodeFailedPayload = {
                        SinkName = sink.Name
                        BatchScopeId = scopeId
                        BatchSize = List.length ordered
                        FailedCount = decodeFailures.Count
                        FailedEventIds = failedIds
                        FailedEventTypes = failedTypes
                        FailedAt = DateTime.UtcNow
                    }

                    do! auditLog.Record("_platform", AuditEventDecodeFailed payload)

                if List.isEmpty decodedPairs then
                    // Every event failed to decode — advance cursor anyway
                    // to past the last raw event so we don't re-process.
                    let lastRaw = ordered |> List.last

                    let newCursor: AuditReplicatorCursor = {
                        LastDeliveredAt = lastRaw.OccurredAt
                        LastDeliveredEventId = lastRaw.Id
                    }

                    do! cursorStore.Save(sink.Name, scopeId, newCursor)
                    return ()
                else
                    // Phase 66 Stream B.7 — wrap each decoded event in an
                    // `AuditEnvelope` before handing it to the sink. The
                    // dispatcher does not have the originating
                    // `AccessContext.Subject` (`ModuleEvent` does not
                    // persist it today), so the subject is derived from
                    // `ScopeId` via `AuditSubject.fromScopeId`. Sinks at
                    // `SchemaVersion = LatestAuditSchemaVersion` (2) read
                    // these envelopes natively; future schema-version
                    // mismatches would be detected and translated here.
                    // Phase 66 Stream C.2 — apply the central
                    // `AuditSamplingPolicy` before delivery. The keep/skip
                    // decision is deterministic on the event id (per subject
                    // kind), so a sampled-out event stays sampled-out across
                    // the live-hook path and every catch-up re-read — it can
                    // never flicker into delivery on a sweep. Skipped events
                    // still advance the cursor below (via `ordered |>
                    // List.last`), so they are not re-attempted. The default
                    // policy (`AuditSamplingPolicy.none`, all kinds 1.0)
                    // takes the `shouldDeliver` fast path and keeps every
                    // event — byte-for-byte the pre-C.2 pipeline.
                    let envelopes =
                        decodedPairs
                        |> List.choose (fun (modEvt, audit) ->
                            let env = AuditEnvelope.fromScopeId modEvt.ScopeId modEvt.OccurredAt audit

                            if
                                AuditSamplingPolicy.shouldDeliver
                                    samplingPolicy
                                    (AuditSubject.kind env.Subject)
                                    modEvt.Id
                            then
                                Some env
                            else
                                None)

                    let batchSize = List.length envelopes

                    if List.isEmpty envelopes then
                        // Every decoded event in this batch was sampled out.
                        // Advance the cursor past the batch (so these events
                        // are not re-read) and skip both `sink.Deliver` and
                        // the `AuditSinkDelivered` emission — delivering an
                        // empty batch would emit a `batchSize = 0` row and
                        // some sinks reject empty payloads.
                        let lastEvt = ordered |> List.last

                        let newCursor: AuditReplicatorCursor = {
                            LastDeliveredAt = lastEvt.OccurredAt
                            LastDeliveredEventId = lastEvt.Id
                        }

                        do! cursorStore.Save(sink.Name, scopeId, newCursor)
                        return ()
                    else
                        // Retry loop. On `Result.Error`, emit `AuditSinkFailed`
                        // for that attempt and back off per `RetryPolicy`. After
                        // `MaxAttempts` exhausted, emit `AuditSinkDeadLettered`
                        // and advance the cursor PAST the failed batch (so
                        // subsequent events still flow). Operators investigate
                        // the dead-letter event.
                        let mutable attempt = 1
                        let mutable lastError = ""
                        let mutable succeeded = false

                        while attempt <= options.RetryPolicy.MaxAttempts && not succeeded do
                            let delay = RetryPolicy.delayFor options.RetryPolicy attempt

                            if delay > TimeSpan.Zero then
                                do! Async.Sleep delay

                            let! result = sink.Deliver envelopes

                            match result with
                            | Ok() -> succeeded <- true
                            | Error msg ->
                                lastError <- msg

                                do! emitFailed sink.Name scopeId batchSize attempt msg

                                attempt <- attempt + 1

                        // Cursor advances to the last RAW event in the batch
                        // (not the last delivered envelope) — sampled-out and
                        // delivered events alike are now behind the cursor.
                        let lastEvt = ordered |> List.last

                        let newCursor: AuditReplicatorCursor = {
                            LastDeliveredAt = lastEvt.OccurredAt
                            LastDeliveredEventId = lastEvt.Id
                        }

                        if succeeded then
                            do! cursorStore.Save(sink.Name, scopeId, newCursor)
                            do! emitDelivered sink.Name scopeId batchSize lastEvt.OccurredAt
                        else
                            // Dead-letter: advance cursor anyway so the failed
                            // batch doesn't block subsequent batches. The
                            // dead-letter audit event is the operator's signal.
                            do! cursorStore.Save(sink.Name, scopeId, newCursor)

                            do! emitDeadLettered sink.Name scopeId batchSize options.RetryPolicy.MaxAttempts lastError

                        return ()
    }

    /// Acquire the per-(sinkName, scopeId) semaphore, run `work`, release.
    /// Used by both the live-hook drain loop and the catch-up sweep so
    /// they can't double-deliver for the same scope.
    let withScopeLock (sinkName: string) (scopeId: string) (work: Async<unit>) = async {
        let sem = getScopeLock sinkName scopeId
        do! sem.WaitAsync() |> Async.AwaitTask

        try
            do! work
        finally
            sem.Release() |> ignore
    }

    // ─── Live-hook drain loop ───────────────────────────────────────

    let drainOneSink (sink: IAuditSink) (stoppingToken: CancellationToken) = task {
        let ch = channels[sink.Name]
        let lingerMs = options.BatchPolicy.LingerMs
        let maxSize = options.BatchPolicy.MaxBatchSize

        while not stoppingToken.IsCancellationRequested do
            try
                // Block until the first event arrives. After that,
                // accumulate up to `maxSize` events or until `lingerMs`
                // elapses since the first event in the batch.
                let! firstItem = ch.Reader.ReadAsync(stoppingToken).AsTask()
                let buffer = ResizeArray<ModuleEvent>()
                buffer.Add firstItem.Event

                let deadline = DateTime.UtcNow.AddMilliseconds(float lingerMs)
                let mutable keepDraining = true

                while keepDraining && buffer.Count < maxSize do
                    let now = DateTime.UtcNow

                    if now >= deadline then
                        keepDraining <- false
                    else
                        let remaining = deadline - now
                        let nextItemTask = ch.Reader.ReadAsync(stoppingToken).AsTask()
                        let timeoutTask = System.Threading.Tasks.Task.Delay(remaining, stoppingToken)
                        let! winner = System.Threading.Tasks.Task.WhenAny(nextItemTask, timeoutTask)

                        if obj.ReferenceEquals(winner, nextItemTask) then
                            buffer.Add(nextItemTask.Result.Event)
                        else
                            keepDraining <- false

                // Group accumulated events by ScopeId — different scopes
                // have different cursors and may proceed independently.
                let groupedByScope = buffer |> Seq.groupBy _.ScopeId |> List.ofSeq

                for scopeId, events in groupedByScope do
                    let batch = List.ofSeq events

                    do!
                        withScopeLock sink.Name scopeId (deliverBatch sink scopeId batch)
                        |> Async.StartAsTask
            with
            | :? OperationCanceledException -> ()
            | ex ->
                logger.Error($"[AuditReplicator] event=drain_loop_error sink={sink.Name}: unexpected error", Some ex)
    }

    // ─── Catch-up sweep ─────────────────────────────────────────────

    let catchUpSweep (stoppingToken: CancellationToken) = task {
        // Run the sweep periodically. The first sweep also serves as
        // startup recovery — events written during downtime are caught
        // up before any new live-hook events arrive (the channel may
        // already have a few queued by the time we enter the loop, but
        // the per-scope semaphore serialises them).
        while not stoppingToken.IsCancellationRequested do
            try
                let! scopes = eventStore.ListScopes() |> Async.StartAsTask

                for scopeId in scopes do
                    for sink in sinks do
                        let work = async {
                            let! cursor = cursorStore.Load(sink.Name, scopeId)
                            let! events = eventStore.ReadBySource(scopeId, AuditSourceModule.value)

                            let candidates =
                                events
                                |> List.filter (fun e -> not (isReplicatorSelfEvent e.EventType))
                                |> List.filter (AuditReplicatorCursor.isAfter cursor)

                            if not (List.isEmpty candidates) then
                                // Chunk into MaxBatchSize batches in
                                // (OccurredAt, Id) order so the cursor
                                // advances monotonically through the
                                // chunks.
                                let ordered =
                                    candidates
                                    |> List.sortWith (fun a b ->
                                        let cmp = compare a.OccurredAt b.OccurredAt
                                        if cmp <> 0 then cmp else a.Id.CompareTo b.Id)

                                let chunks = ordered |> List.chunkBySize options.BatchPolicy.MaxBatchSize

                                for chunk in chunks do
                                    do! deliverBatch sink scopeId chunk
                        }

                        do! withScopeLock sink.Name scopeId work |> Async.StartAsTask
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.Error("[AuditReplicator] event=sweep_error: unexpected error during catch-up sweep", Some ex)

            try
                if options.CatchUpSweepInterval < TimeSpan.MaxValue then
                    do! System.Threading.Tasks.Task.Delay(options.CatchUpSweepInterval, stoppingToken)
                else
                    // Sweep disabled — wait forever (until cancellation).
                    do! System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
            with :? OperationCanceledException ->
                ()
    }

    // ─── Public surface ────────────────────────────────────────────

    /// Push an event into every registered sink's queue. Called by the
    /// `AuditReplicationHookedEventStore` decorator after every audit
    /// `Write`. Drops on bounded-channel overflow with a Warn — the
    /// catch-up sweep recovers dropped events.
    member _.Enqueue(evt: ModuleEvent) =
        for sink in sinks do
            let ch = channels[sink.Name]
            let item = { Event = evt }

            if not (ch.Writer.TryWrite item) then
                logger.Warn(
                    sprintf
                        "[AuditReplicator] event=queue_full sink=%s capacity=%d eventId=%O scope=%s — relying on catch-up sweep"
                        sink.Name
                        options.BatchPolicy.QueueCapacity
                        evt.Id
                        evt.ScopeId
                )

    override this.ExecuteAsync(stoppingToken: CancellationToken) = task {
        // Spin up one drain task per sink + one catch-up sweep task.
        // All run in parallel; per-scope semaphores serialise live-hook
        // and sweep deliveries for the same scope.
        let drainTasks =
            sinks
            |> List.map (fun s -> drainOneSink s stoppingToken :> System.Threading.Tasks.Task)

        let sweepTask = catchUpSweep stoppingToken :> System.Threading.Tasks.Task

        let allTasks = drainTasks |> List.append [ sweepTask ] |> List.toArray

        try
            do! System.Threading.Tasks.Task.WhenAll(allTasks)
        with :? OperationCanceledException ->
            ()
    }