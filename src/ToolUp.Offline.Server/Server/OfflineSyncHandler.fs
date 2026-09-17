// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.OfflineSyncHandler

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Offline
open ToolUp.Offline.OfflineSyncApi

// ─── Phase 24 — the offline replay handler ───────────────────────────
//
// Builds a per-request `IOfflineSyncApi` from the resolved
// `AccessContext` and the DI-resolved `IEntityStore`. Three properties
// are load-bearing and each is enforced here rather than trusted from
// the wire:
//
//  1. **The scope is SERVER-resolved.** `QueuedMutation.ScopeId` is
//     echoed back for the client's own bookkeeping and is never used to
//     address storage. A mutation naming a scope the caller does not
//     hold is `Rejected`, not silently redirected — a client that has
//     been offline across a team switch must be told, not quietly
//     written into whichever scope it last saw.
//
//  2. **Conflict is detected BY THE STORE, through the seam's
//     compare-and-set.** Until Phase 753 this handler read the head
//     version and compared it against `QueuedMutation.BaseVersion`
//     itself, because `IEntityStore.Save` assigns `max(existing) + 1`
//     unconditionally and never reports `VersionConflict`; that
//     hand-rolled compare had a window of its own (a server edit landing
//     between the read and the save was clobbered anyway). The replay
//     now goes through `SaveIfVersion` / `DeleteIfVersion` with the
//     mutation's `BaseVersion` as the expectation, so the head compare
//     and the write are one act inside the store, and a moved head
//     comes back typed as `EntityError.VersionConflict`. The handler's
//     job is to hand that expectation over — an adapter's `Apply` that
//     saves through the unconditional `Save` instead has opted out of
//     the conflict story, which is why `OfflineReplayError` makes the
//     conflict a distinct case rather than a message.
//
//  3. **Replay is typed through a registration, not reflected.** The
//     wire carries `byte[]`; the store's `Save<'T>` needs the real
//     record so its indexes and full-text extractors run. Only the
//     module that owns the entity knows `'T`, so it registers an
//     `OfflineEntityReplay` — the same shape, and for the same reason,
//     as `IDataMigrator.Migrate` (sanctioned erasure boundary 7). An
//     unregistered entity type is `Rejected`; it is never guessed at.

// ─── Replay registration ─────────────────────────────────────────────

/// Everything a replay function is given. All state arrives by
/// parameter — nothing is closure-captured between calls (portability
/// rule 4), so the same registration is safe under any request
/// concurrency.
type OfflineReplayContext = {
    Store: IEntityStore
    /// Server-resolved storage scope. Authoritative.
    ScopeId: string
    /// Server-resolved caller. Stamped onto the replay audit record.
    UserId: string
}

/// Why a replay adapter could not apply a mutation (Phase 753).
type OfflineReplayError =
    /// The head moved past the version the mutation was queued against
    /// — the store's `EntityError.VersionConflict`, surfaced as its own
    /// case so the handler turns it into the `Conflict` outcome the user
    /// resolves rather than a `Rejected` the client drops.
    | ReplayConflict of expected: int * actual: int
    /// Anything else — a payload that does not deserialise, an
    /// unregistered shape, a storage failure. The message reaches the
    /// client as `Rejected`.
    | ReplayRejected of string

/// Per-entity-type replay adapter. Registered by the module that owns
/// the entity record, because only it knows the record's shape.
///
/// Both functions return `Result` rather than raising: a malformed
/// offline payload is an expected outcome (the queue may hold bytes
/// written by an older client build), and it must become a `Rejected`
/// the client can drop rather than a 500 it retries forever.
type OfflineEntityReplay = {
    /// `EntityFieldsCore.Type` this adapter handles.
    EntityType: string
    /// Current server bytes + version for one id. `Ok None` when the
    /// entity does not exist. Read when a replay conflicts, so the
    /// client can show both documents.
    Current: OfflineReplayContext -> string -> Async<Result<(byte[] * int) option, string>>
    /// Deserialise `payload` and persist it via
    /// `IEntityStore.SaveIfVersion`, stating the `int` argument — the
    /// mutation's `BaseVersion` — as the expected version. Returns the saved
    /// bytes and the version the store assigned, or `ReplayConflict`
    /// when the head has moved. An adapter that persists through the
    /// unconditional `Save` instead has opted out of conflict detection
    /// for its entity type; `OfflineEntityReplay.ofJson` is the
    /// reference shape.
    Apply: OfflineReplayContext -> int -> byte[] -> Async<Result<byte[] * int, OfflineReplayError>>
}

/// Shared JSON setup — the SAME converter set `BlobEntityStore` uses to
/// serialise entities, so bytes taken off the offline queue round-trip
/// through DUs, options and Maps exactly as a live write would. Using a
/// different serialiser here would be a silent wire fork.
module private Json =
    let private options = FableConverters.create ()

    let serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

module OfflineEntityReplay =
    /// The one-liner registration for a JSON-serialised entity record.
    ///
    /// ```fsharp
    /// let replays = [ OfflineEntityReplay.ofJson<Inspection> "Inspection" ]
    /// ```
    ///
    /// `entityType` is passed explicitly rather than reflected off
    /// `typeof<'T>.Name` because the entity's registered `Type` string
    /// is a wire value that may deliberately differ from the CLR type
    /// name — inferring it would work until the first renamed record
    /// and then silently stop matching.
    let ofJson<'T> (entityType: string) : OfflineEntityReplay = {
        EntityType = entityType

        Current =
            fun ctx entityId -> async {
                match! ctx.Store.Get<'T>(ctx.ScopeId, entityType, entityId) with
                | Error(EntityError.NotFound _) -> return Ok None
                | Error err -> return Error(EntityError.message err)
                | Ok entity ->
                    match tryGetEntityFields entity with
                    | Error msg -> return Error msg
                    | Ok core ->
                        let bytes = Encoding.UTF8.GetBytes(Json.serialize<'T> entity)
                        return Ok(Some(bytes, core.Version))
            }

        Apply =
            fun ctx expectedVersion payload -> async {
                let parsed =
                    try
                        Ok(Json.deserialize<'T> (Encoding.UTF8.GetString payload))
                    with ex ->
                        Error(
                            ReplayRejected(
                                sprintf "offline payload for '%s' did not deserialise: %s" entityType ex.Message
                            )
                        )

                match parsed with
                | Error err -> return Error err
                | Ok entity ->
                    match! ctx.Store.SaveIfVersion<'T>(ctx.ScopeId, entity, expectedVersion) with
                    | Error(EntityError.VersionConflict(_, _, expected, actual)) ->
                        return Error(ReplayConflict(expected, actual))
                    | Error err -> return Error(ReplayRejected(EntityError.message err))
                    | Ok entityRef ->
                        // Re-read so the bytes handed back are exactly
                        // what the store now holds (the store rewrites
                        // the Version field on the way in, and may run
                        // registered index extractors that normalise
                        // fields). Returning the request bytes would
                        // hand the client a document that disagrees
                        // with the server on version — the precise
                        // state that manufactures the NEXT conflict.
                        match! ctx.Store.Get<'T>(ctx.ScopeId, entityType, entityRef.Id) with
                        | Error err -> return Error(ReplayRejected(EntityError.message err))
                        | Ok saved -> return Ok(Encoding.UTF8.GetBytes(Json.serialize<'T> saved), entityRef.Version)
            }
    }

// ─── Handler options ─────────────────────────────────────────────────

/// Composition-time options for the sync handler.
///
/// `AuditLog` is the audit half of the phase, and it is opt-in (`None`
/// by default) so a deployment that composes the handler pays nothing
/// for it (GP 11 + GP 13).
///
/// **The audit row an applied replay records (Phase 759).** One
/// lifecycle row per applied version — `EntityCreated` / `EntityUpdated`
/// / `EntityDeleted` — carrying the REAL user in `UserId` and, in
/// `EntityLifecycleEventPayload.Replay`, the mutation's origination time
/// (`EnqueuedAt`), the server's application time and the queue entry's
/// mutation id. It goes through `IAuditLog.Record` like every other
/// audit row, so the row's `OccurredAt` is the write time: that is what
/// keeps it visible to the audit replicator, whose cursor filters on
/// `OccurredAt` and would never deliver a row backdated behind it (the
/// Phase 24 direct-`IEventStore` emission had exactly that defect).
///
/// **Why the entity store's own row does not double it.** The store
/// emits a generic `UserId = "system"` lifecycle row from inside
/// `Save` / `Delete`, because `IEntityStore` does not carry caller
/// identity. The handler applies each mutation under
/// `EntityAuditReplayScope.run`, and the SDK-default audit log drops
/// the store's generic row for that entity while the scope is active —
/// for that call path only, never globally. **Compose the handler with
/// the same `IAuditLog` the entity store is composed with** (resolve it
/// from DI); a deployment whose store has no audit log wired still gets
/// the handler's row, and one that opts out of `AuditLog` here keeps
/// the store's generic row exactly as before.
type OfflineSyncOptions = {
    /// Per-entity-type replay adapters. An entity type absent here is
    /// `Rejected` — the handler never guesses a record shape.
    Replays: OfflineEntityReplay list
    /// Opt-in audit emission through the `IAuditLog` seam. `None`
    /// (default) emits nothing and leaves the entity store's own
    /// emission untouched.
    AuditLog: IAuditLog option
    /// Ceiling on one `ApplyBatch` call. A reconnecting client with a
    /// large backlog is drained across several batches rather than in
    /// one unbounded request — the drain is resumable by construction,
    /// so a smaller ceiling costs round trips, never correctness.
    MaxBatchSize: int
}

module OfflineSyncOptions =
    /// No replays registered, no audit, batches of 50.
    let defaults: OfflineSyncOptions = {
        Replays = []
        AuditLog = None
        MaxBatchSize = 50
    }

    let withReplays (replays: OfflineEntityReplay list) (options: OfflineSyncOptions) : OfflineSyncOptions = {
        options with
            Replays = replays
    }

    /// Record the replay's provenance-carrying lifecycle row through
    /// `log`. Pass the `IAuditLog` the entity store is composed with so
    /// the store's generic row for the same version is the one dropped.
    let withAuditLog (log: IAuditLog) (options: OfflineSyncOptions) : OfflineSyncOptions = {
        options with
            AuditLog = Some log
    }

    /// The Phase 24 shape, kept so a composition root written against
    /// it compiles unchanged: audit through an `IEventStore` directly.
    /// Since Phase 759 this is `withAuditLog` over the SDK-default
    /// `EventStoreAuditLog` on that store, with a silent logger — the
    /// row lands with the same source module, event type and payload
    /// codec `IAuditLog.GetAuditTrail` reads, and failures are swallowed
    /// exactly as the direct write's were.
    let withAuditEventStore (store: IEventStore) (options: OfflineSyncOptions) : OfflineSyncOptions =
        let silent =
            { new ILogger with
                member _.Debug _ = ()
                member _.Info _ = ()
                member _.Warn _ = ()
                member _.Error(_, _) = ()
            }

        withAuditLog (AuditLog.EventStoreAuditLog(store, silent)) options

// ─── Request-scope resolution ────────────────────────────────────────

/// Mirrors `FormApiHandler.resolveAccessContext` — the stamp left by
/// the platform's auth middleware, with a defensive fallback so a
/// misconfigured pipeline yields an anonymous context rather than a
/// null-reference deep in a write path.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.Items.TryGetValue "ToolUp.AccessContext" with
    | true, (:? AccessContext as ac) -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

let private resolveScopeId (ctx: HttpContext) (accessContext: AccessContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as scope) -> scope.ScopeId
    | _ -> accessContext.UserId

// ─── Audit ───────────────────────────────────────────────────────────

module private ReplayAudit =
    /// The lifecycle event an applied replay records: the real caller
    /// in `UserId`, and the replay provenance — origination time,
    /// application time, queue mutation id — in `Replay`.
    let event (userId: string) (mutation: QueuedMutation) (newVersion: int) (replayedAt: DateTime) : AuditEvent =
        let payload: EntityLifecycleEventPayload = {
            UserId = userId
            EntityType = mutation.EntityType
            EntityId = mutation.EntityId
            Version = newVersion
            Replay =
                Some {
                    // THE POINT OF THE WHOLE BLOCK: the origination
                    // time survives, beside the application time, on
                    // the one row that records the version.
                    OriginatedAt = mutation.EnqueuedAt.UtcDateTime
                    ReplayedAt = replayedAt
                    MutationId = mutation.Id
                }
        }

        match mutation.Operation with
        | DeleteOp -> AuditEvent.EntityDeleted payload
        | SaveOp when newVersion <= 1 -> AuditEvent.EntityCreated payload
        | SaveOp -> AuditEvent.EntityUpdated payload

    /// Record the provenance-carrying lifecycle row through the seam.
    ///
    /// Best-effort in exactly the shape `BlobEntityStore` uses: any
    /// failure is swallowed, because a replay that succeeded must not
    /// be reported to the client as failed merely because its audit row
    /// did not land. The client would re-queue it and apply it twice.
    let emit
        (auditLog: IAuditLog option)
        (scopeId: string)
        (userId: string)
        (mutation: QueuedMutation)
        (newVersion: int)
        : Async<unit> =
        async {
            match auditLog with
            | None -> return ()
            | Some log ->
                try
                    do! log.Record(scopeId, event userId mutation newVersion DateTime.UtcNow)
                with _ ->
                    return ()
        }

    /// Apply `write` as the replay of the mutation's entity: while it
    /// runs, the SDK-default audit log drops the entity store's generic
    /// `"system"` lifecycle row for that entity, so the row `emit`
    /// records afterwards is the ONLY one for the version. Entered only
    /// when the handler has an audit log to record that row through —
    /// with none, the store's own row stands exactly as before.
    let scoped (auditLog: IAuditLog option) (mutation: QueuedMutation) (write: Async<'T>) : Async<'T> =
        match auditLog with
        | None -> write
        | Some _ -> AuditLog.EntityAuditReplayScope.run mutation.EntityType mutation.EntityId write

// ─── The handler ─────────────────────────────────────────────────────

/// Apply one mutation. Pure with respect to the handler — everything it
/// needs arrives by parameter, so `ApplyBatch` is a fold over this.
let private applyOne
    (options: OfflineSyncOptions)
    (replayCtx: OfflineReplayContext)
    (mutation: QueuedMutation)
    : Async<SyncOutcome> =
    async {
        // Guard 1 — the mutation must belong to the caller's scope. An
        // empty ScopeId is treated as "the client did not say", which
        // is legitimate for a single-scope deployment.
        if mutation.ScopeId <> "" && mutation.ScopeId <> replayCtx.ScopeId then
            return
                Rejected(
                    sprintf
                        "mutation was queued against scope '%s' but the caller resolves to '%s' — re-queue it under the active team"
                        mutation.ScopeId
                        replayCtx.ScopeId
                )
        else
            match options.Replays |> List.tryFind (fun r -> r.EntityType = mutation.EntityType) with
            | None ->
                return
                    Rejected(
                        sprintf
                            "no offline replay adapter registered for entity type '%s' — register one with OfflineEntityReplay.ofJson"
                            mutation.EntityType
                    )
            | Some replay ->
                // Guard 2 — conflict detection, performed by the store:
                // the mutation's `BaseVersion` is the expectation handed
                // to the seam's compare-and-set, so the head compare and
                // the write are one act. The head moved under the offline
                // edit => the user chooses, with both documents in hand.
                // An entity created offline (BaseVersion = 0) conflicts
                // only if something now exists at that id.
                let conflict () = async {
                    match! replay.Current replayCtx mutation.EntityId with
                    | Error msg -> return Rejected msg
                    | Ok current ->
                        let serverBytes =
                            match current with
                            | Some(bytes, _) -> bytes
                            | None -> Array.empty

                        return Conflict(mutation.Payload, serverBytes)
                }

                // Phase 759 — each write runs under the replay scope
                // (ReplayAudit.scoped), so the store's own generic
                // lifecycle row for this entity is dropped and the
                // provenance-carrying row ReplayAudit.emit records is
                // the ONE row for the version. The compare-and-set is
                // untouched: the scope wraps the seam call, it does not
                // change what the seam is asked.
                match mutation.Operation with
                | DeleteOp ->
                    match!
                        ReplayAudit.scoped
                            options.AuditLog
                            mutation
                            (replayCtx.Store.DeleteIfVersion(
                                replayCtx.ScopeId,
                                mutation.EntityType,
                                mutation.EntityId,
                                mutation.BaseVersion
                            ))
                    with
                    | Error(EntityError.VersionConflict _) -> return! conflict ()
                    | Error err -> return Rejected(EntityError.message err)
                    | Ok() ->
                        do!
                            ReplayAudit.emit
                                options.AuditLog
                                replayCtx.ScopeId
                                replayCtx.UserId
                                mutation
                                mutation.BaseVersion

                        return Applied Array.empty
                | SaveOp ->
                    match!
                        ReplayAudit.scoped
                            options.AuditLog
                            mutation
                            (replay.Apply replayCtx mutation.BaseVersion mutation.Payload)
                    with
                    | Error(ReplayConflict _) -> return! conflict ()
                    | Error(ReplayRejected msg) -> return Rejected msg
                    | Ok(savedBytes, newVersion) ->
                        do! ReplayAudit.emit options.AuditLog replayCtx.ScopeId replayCtx.UserId mutation newVersion

                        return Applied savedBytes
    }

/// Per-request API record over a resolved entity store.
///
/// The `HttpContext -> IOfflineSyncApi` shape is the repo's standard
/// handler factory (see `AlgorithmCatalogApiHandler`); mount it with
/// `makeApi` and `OfflineSyncApi.routeBuilder`.
let offlineSyncApi (store: IEntityStore) (options: OfflineSyncOptions) (ctx: HttpContext) : IOfflineSyncApi =
    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId ctx accessContext

    let replayCtx: OfflineReplayContext = {
        Store = store
        ScopeId = scopeId
        UserId = accessContext.UserId
    }

    {
        Apply = fun mutation -> applyOne options replayCtx mutation

        ApplyBatch =
            fun mutations -> async {
                // Truncate rather than refuse: the client's drain loop
                // is resumable, so a batch over the ceiling costs it
                // one more round trip and nothing else. Refusing would
                // strand a client whose backlog grew past the limit.
                let admitted = mutations |> List.truncate (max 1 options.MaxBatchSize)
                let mutable results = []

                for mutation in admitted do
                    let! outcome = applyOne options replayCtx mutation

                    results <-
                        {
                            MutationId = mutation.Id
                            Outcome = outcome
                        }
                        :: results

                return List.rev results
            }

        FetchCurrent =
            fun request -> async {
                match options.Replays |> List.tryFind (fun r -> r.EntityType = request.EntityType) with
                | None -> return None
                | Some replay ->
                    match! replay.Current replayCtx request.EntityId with
                    | Ok(Some(bytes, _)) -> return Some bytes
                    | Ok None
                    | Error _ -> return None
            }
    }