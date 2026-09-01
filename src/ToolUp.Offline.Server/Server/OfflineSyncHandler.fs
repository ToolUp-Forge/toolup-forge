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
//  2. **Conflict is detected HERE, not by the store.** The phase design
//     assumed `IEntityStore.Save` surfaces `EntityError.VersionConflict`
//     for a stale write. It does not: `BlobEntityStore.Save` assigns
//     `max(existing) + 1` unconditionally and never compares against the
//     caller's version, so a naive replay would silently clobber every
//     concurrent server-side edit. The handler therefore reads the head
//     version first and compares it against `QueuedMutation.BaseVersion`
//     — that comparison IS the last-writer-wins guard, and removing it
//     removes the entire conflict story.
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

/// Per-entity-type replay adapter. Registered by the module that owns
/// the entity record, because only it knows the record's shape.
///
/// Both functions return `Result<_, string>` rather than raising: a
/// malformed offline payload is an expected outcome (the queue may
/// hold bytes written by an older client build), and it must become a
/// `Rejected` the client can drop rather than a 500 it retries forever.
type OfflineEntityReplay = {
    /// `EntityFieldsCore.Type` this adapter handles.
    EntityType: string
    /// Current server bytes + version for one id. `Ok None` when the
    /// entity does not exist.
    Current: OfflineReplayContext -> string -> Async<Result<(byte[] * int) option, string>>
    /// Deserialise `payload` and persist it via `IEntityStore.Save`.
    /// Returns the saved bytes and the version the store assigned.
    Apply: OfflineReplayContext -> byte[] -> Async<Result<byte[] * int, string>>
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
            fun ctx payload -> async {
                let parsed =
                    try
                        Ok(Json.deserialize<'T> (Encoding.UTF8.GetString payload))
                    with ex ->
                        Error(sprintf "offline payload for '%s' did not deserialise: %s" entityType ex.Message)

                match parsed with
                | Error msg -> return Error msg
                | Ok entity ->
                    match! ctx.Store.Save<'T>(ctx.ScopeId, entity) with
                    | Error err -> return Error(EntityError.message err)
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
                        | Error err -> return Error(EntityError.message err)
                        | Ok saved -> return Ok(Encoding.UTF8.GetBytes(Json.serialize<'T> saved), entityRef.Version)
            }
    }

// ─── Handler options ─────────────────────────────────────────────────

/// Composition-time options for the sync handler.
///
/// `AuditEventStore` is the audit half of the phase, and it is opt-in
/// (`None` by default) so a deployment that composes the handler pays
/// nothing for it (GP 11 + GP 13).
///
/// **Why an `IEventStore` and not the `IAuditLog` seam.** The phase
/// requires the replayed audit row to carry the mutation's ORIGINAL
/// `EnqueuedAt` as its `OccurredAt`, and the caller's user id rather
/// than `"system"`. `IAuditLog.Record(scopeId, event)` accepts neither:
/// it builds the envelope internally with `Events.create`, which stamps
/// `DateTime.UtcNow`, and `BlobEntityStore` hard-codes `UserId =
/// "system"` in its own emission. Writing the `ModuleEvent` directly —
/// with `SourceModule = AuditSourceModule.value` and the same event-type
/// name and payload shape the audit codec expects — is the only path
/// that preserves both facts, and `AuditReplicator` rebuilds the
/// envelope from `modEvt.OccurredAt`, so downstream sinks see the
/// origination time with no sink-side change.
///
/// **Known consequence, stated rather than hidden.** When the entity
/// store is ALSO composed with an `IAuditLog`, an applied replay
/// produces two lifecycle rows for the same entity version: the store's
/// (`UserId = "system"`, application time) and this one (real user,
/// origination time). They are distinguishable by `UserId` and they
/// record genuinely different facts, but they are not deduplicated.
/// Collapsing them would require a dedicated `AuditEvent` case, which
/// is a breaking union-case addition to `ToolUp.Platform.Core` and is
/// deliberately out of this phase's scope.
type OfflineSyncOptions = {
    /// Per-entity-type replay adapters. An entity type absent here is
    /// `Rejected` — the handler never guesses a record shape.
    Replays: OfflineEntityReplay list
    /// Opt-in audit emission. `None` (default) emits nothing.
    AuditEventStore: IEventStore option
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
        AuditEventStore = None
        MaxBatchSize = 50
    }

    let withReplays (replays: OfflineEntityReplay list) (options: OfflineSyncOptions) : OfflineSyncOptions = {
        options with
            Replays = replays
    }

    let withAuditEventStore (store: IEventStore) (options: OfflineSyncOptions) : OfflineSyncOptions = {
        options with
            AuditEventStore = Some store
    }

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
    let private options = FableConverters.create ()

    /// Emit one lifecycle audit row stamped with the mutation's
    /// origination time and the resolved caller.
    ///
    /// Best-effort in exactly the shape `BlobEntityStore` uses: any
    /// failure is swallowed, because a replay that succeeded must not
    /// be reported to the client as failed merely because its audit row
    /// did not land. The client would re-queue it and apply it twice.
    let emit
        (eventStore: IEventStore option)
        (scopeId: string)
        (userId: string)
        (mutation: QueuedMutation)
        (newVersion: int)
        : Async<unit> =
        async {
            match eventStore with
            | None -> return ()
            | Some store ->
                try
                    let payload: EntityLifecycleEventPayload = {
                        UserId = userId
                        EntityType = mutation.EntityType
                        EntityId = mutation.EntityId
                        Version = newVersion
                    }

                    let auditEvent =
                        match mutation.Operation with
                        | DeleteOp -> AuditEvent.EntityDeleted payload
                        | SaveOp when newVersion <= 1 -> AuditEvent.EntityCreated payload
                        | SaveOp -> AuditEvent.EntityUpdated payload

                    let moduleEvent: ModuleEvent = {
                        Id = Guid.NewGuid()
                        // THE POINT OF THE WHOLE BLOCK: origination
                        // time, not application time.
                        OccurredAt = mutation.EnqueuedAt.UtcDateTime
                        ScopeId = scopeId
                        SourceModule = AuditSourceModule.value
                        EventType = AuditEvent.eventTypeName auditEvent
                        Payload = JsonSerializer.Serialize(payload, options)
                    }

                    do! store.Write moduleEvent
                with _ ->
                    return ()
        }

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
                match! replay.Current replayCtx mutation.EntityId with
                | Error msg -> return Rejected msg
                | Ok current ->
                    let headVersion =
                        match current with
                        | Some(_, v) -> v
                        | None -> 0

                    // Guard 2 — last-writer-wins conflict detection.
                    // The head moved under the offline edit, so the
                    // user chooses. Note an entity created offline
                    // (BaseVersion = 0) conflicts only if something
                    // now exists at that id.
                    if headVersion <> mutation.BaseVersion then
                        let serverBytes =
                            match current with
                            | Some(bytes, _) -> bytes
                            | None -> Array.empty

                        return Conflict(mutation.Payload, serverBytes)
                    else
                        match mutation.Operation with
                        | DeleteOp ->
                            match!
                                replayCtx.Store.Delete(replayCtx.ScopeId, mutation.EntityType, mutation.EntityId)
                            with
                            | Error err -> return Rejected(EntityError.message err)
                            | Ok() ->
                                do!
                                    ReplayAudit.emit
                                        options.AuditEventStore
                                        replayCtx.ScopeId
                                        replayCtx.UserId
                                        mutation
                                        headVersion

                                return Applied Array.empty
                        | SaveOp ->
                            match! replay.Apply replayCtx mutation.Payload with
                            | Error msg -> return Rejected msg
                            | Ok(savedBytes, newVersion) ->
                                do!
                                    ReplayAudit.emit
                                        options.AuditEventStore
                                        replayCtx.ScopeId
                                        replayCtx.UserId
                                        mutation
                                        newVersion

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