module ToolUp.Platform.EntityOutbox

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Entity-write co-located outbox (Phase 599) ──────────────────
//
// Write+event coupling for entity mutations: an entity save and its
// corresponding `IEventStore` emission are two writes with no
// transaction between them, so a crash in the gap leaves state and
// event log divergent. `OutboxEntityStore.SaveWithEvents` closes the
// gap with a **write-ahead intent**:
//
//   1. Stage an intent blob — the events + a version witness — in a
//      single blob write (the atomic step).
//   2. Save the entity through the ordinary `IEntityStore`.
//   3. Publish the events + delete the intent (best-effort — the
//      relay recovers both on a crash).
//
// The **version witness** is what keeps recovery honest. The intent
// records `MinVersionAfterSave = head + 1` observed before the save;
// on replay, the relay publishes the staged events ONLY when the
// entity's head version reached that witness (the save committed).
// An intent whose save never committed is discarded after an abandon
// window — no ghost events for mutations that never happened.
//
// **Design deviation from the phase spec** (which sketched an
// `IEntityStore` decorator staging events inside the entity payload
// blob): `BlobEntityStore` drives per-type index extractors,
// `Query<'T>` shape validation, and reflection over the entity's
// `Id`/`Type`/`Version` fields against the CALLER's record type —
// an envelope-wrapped payload breaks all three seams. The
// write-ahead intent achieves the same acceptance criterion (no
// crash point leaves a committed entity mutation without its event
// eventually published) against the store's real internals, with
// the entity payload byte-identical to a non-outbox save.
//
// **Caller contract.** The at-least-once guarantee assumes the
// application serialises writes per entity id (the store's own
// standing assumption — it has no CAS; see the blob-RMW integrity
// notes). A concurrent writer bumping the version past the witness
// while an abandoned intent waits could turn a discard into a
// publish; per-entity write serialisation removes the interleaving.

/// One staged mutation-with-events, persisted as a single blob before
/// the entity save.
type OutboxIntent = {
    IntentId: Guid
    ScopeId: string
    EntityType: string
    EntityId: EntityId
    /// Head version observed before the save, plus one. The save
    /// commits at (at least) this version — the relay's witness for
    /// "did the mutation actually land".
    MinVersionAfterSave: int
    CreatedAt: DateTime
    /// The events to publish once the save is known-committed.
    Events: ModuleEvent list
}

[<Literal>]
let private IntentContainer = "_platform"

let private intentPrefix = "entity-outbox/"

let private intentBlobName (intent: OutboxIntent) =
    $"{intentPrefix}{intent.ScopeId}/{intent.CreatedAt.Ticks:D19}-{intent.IntentId:N}.json"

let private jsonOptions = FableConverters.create ()

/// Default relay cadence support windows. `settle` keeps the relay
/// off intents whose save+publish may still be in flight; `abandon`
/// is how long an unwitnessed intent survives before it is discarded
/// as a never-committed save.
let defaultSettleWindow = TimeSpan.FromSeconds 30.0
let defaultAbandonWindow = TimeSpan.FromMinutes 10.0

/// The Phase 599 outbox surface. NOT an `IEntityStore` decorator —
/// callers keep their ordinary `IEntityStore` for plain mutations and
/// call `SaveWithEvents` for mutations whose events must not be lost.
/// Stateless over the blob substrate: compose may build more than one
/// instance (the save-path writer and the relay drain) over the same
/// stores.
type OutboxEntityStore
    (
        entityStore: IEntityStore,
        eventStore: IEventStore,
        blobStorage: IBlobStorage,
        logger: ILogger,
        ?settleWindow: TimeSpan,
        ?abandonWindow: TimeSpan
    ) =

    let settle = defaultArg settleWindow defaultSettleWindow
    let abandon = defaultArg abandonWindow defaultAbandonWindow

    let headVersion (scopeId: string) (entityType: string) (entityId: EntityId) : Async<int> = async {
        // `EntityRef<'T>` is a phantom-typed metadata record — no
        // payload materialisation, so `obj` is safe at the relay
        // boundary where the concrete type is unknown.
        let! versions = entityStore.ListVersions<obj>(scopeId, entityType, entityId)
        return versions |> List.fold (fun acc r -> max acc r.Version) 0
    }

    let publishAndClear (blobName: string) (intent: OutboxIntent) : Async<Result<unit, string>> = async {
        try
            for evt in intent.Events do
                do! eventStore.Write evt

            let! _ = blobStorage.Delete(IntentContainer, blobName)
            return Ok()
        with ex ->
            return Error ex.Message
    }

    /// Save `entity` and durably couple `events` to the mutation:
    /// once the save commits, the events are published — immediately
    /// on the happy path, by the relay after a crash or event-store
    /// outage. Events are at-least-once (a crash between publish and
    /// intent delete re-publishes; consumers dedupe by `ModuleEvent.Id`).
    /// A save that fails (or never happens) publishes nothing.
    member _.SaveWithEvents<'T>
        (scopeId: string, entityType: string, entityId: EntityId, entity: 'T, events: ModuleEvent list)
        : Async<Result<EntityRef<'T>, EntityError>> =
        async {
            if List.isEmpty events then
                // Degenerate case — no coupling needed.
                return! entityStore.Save<'T>(scopeId, entity)
            else
                // 1. Version witness, read before anything is written.
                let! versions = entityStore.ListVersions<'T>(scopeId, entityType, entityId)
                let priorHead = versions |> List.fold (fun acc r -> max acc r.Version) 0

                let intent = {
                    IntentId = Guid.NewGuid()
                    ScopeId = scopeId
                    EntityType = entityType
                    EntityId = entityId
                    MinVersionAfterSave = priorHead + 1
                    CreatedAt = DateTime.UtcNow
                    Events = events
                }

                let blobName = intentBlobName intent
                let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intent, jsonOptions))

                // 2. Stage the intent — the single atomic write the
                // guarantee rests on. Refuse the whole mutation when
                // staging fails: proceeding would re-open the exact
                // dual-write gap this surface exists to close.
                match! blobStorage.Upload(IntentContainer, blobName, bytes) with
                | Error err -> return Error(StorageFailure $"outbox intent staging failed: {err}")
                | Ok _ ->
                    // 3. The entity save.
                    match! entityStore.Save<'T>(scopeId, entity) with
                    | Error err ->
                        // Save failed — withdraw the intent so nothing
                        // publishes. Best-effort: if the delete fails,
                        // the relay's version witness discards it after
                        // the abandon window anyway.
                        let! _ = blobStorage.Delete(IntentContainer, blobName)
                        return Error err
                    | Ok entityRef ->
                        // 4. Publish + clear, best-effort.
                        match! publishAndClear blobName intent with
                        | Ok() -> ()
                        | Error msg ->
                            logger.Warn
                                $"[EntityOutbox] event=publish_deferred scope=%s{scopeId} entityType={entityType} entityId={entityId}: {msg} — relay re-publishes on recovery"

                        return Ok entityRef
        }

    /// Number of staged intents awaiting relay, across all scopes.
    member _.PendingCount() : Async<int> = async {
        let! names = blobStorage.List(IntentContainer, intentPrefix)
        return names |> List.filter (fun n -> n.EndsWith ".json") |> List.length
    }

    /// One relay pass: for each settled intent (oldest first within a
    /// scope), publish its events iff the entity's head version reached
    /// the witness; discard it (publishing NOTHING) once it outlives
    /// the abandon window without the witness — the save never
    /// committed. An event-store failure halts the pass (the store is
    /// down; later intents would fail identically). A corrupt intent
    /// is quarantined under `entity-outbox-poison/` so it can never
    /// wedge the drain. Returns the number of intents published.
    member _.RelayOnce(batchSize: int) : Async<int> = async {
        let! names = blobStorage.List(IntentContainer, intentPrefix)

        let batch =
            names
            |> List.filter (fun n -> n.EndsWith ".json")
            |> List.sort
            |> List.truncate batchSize

        let mutable published = 0
        let mutable halted = false
        let now = DateTime.UtcNow

        for blobName in batch do
            if not halted then
                let! downloaded = blobStorage.Download(IntentContainer, blobName)

                let decoded =
                    match downloaded with
                    | Error err -> Error err
                    | Ok bytes ->
                        try
                            Ok(JsonSerializer.Deserialize<OutboxIntent>(Encoding.UTF8.GetString bytes, jsonOptions))
                        with ex ->
                            Error ex.Message

                match decoded with
                | Error msg ->
                    // File-local failure — quarantine (copy then delete;
                    // blob stores have no rename) and keep draining.
                    logger.Error(
                        $"[EntityOutbox] event=intent_poison blob={blobName}: {msg} — quarantined; its events are NOT published",
                        None
                    )

                    match downloaded with
                    | Ok bytes ->
                        let quarantineName =
                            "entity-outbox-poison/" + blobName.Substring(intentPrefix.Length)

                        let! _ = blobStorage.Upload(IntentContainer, quarantineName, bytes)
                        let! _ = blobStorage.Delete(IntentContainer, blobName)
                        ()
                    | Error _ ->
                        // Could not even read the bytes — leave in place;
                        // a transient storage failure clears next pass.
                        halted <- true
                | Ok intent ->
                    let age = now - intent.CreatedAt

                    if age < settle then
                        () // may still be in flight — next pass
                    else
                        let! head = headVersion intent.ScopeId intent.EntityType intent.EntityId

                        if head >= intent.MinVersionAfterSave then
                            match! publishAndClear blobName intent with
                            | Ok() ->
                                published <- published + 1

                                logger.Info
                                    $"[EntityOutbox] event=relayed scope=%s{intent.ScopeId} entityType={intent.EntityType} entityId={intent.EntityId} events={intent.Events.Length}"
                            | Error msg ->
                                halted <- true

                                logger.Warn
                                    $"[EntityOutbox] event=relay_halted blob={blobName}: {msg} — remaining intents retry next pass"
                        elif age > abandon then
                            // The save never committed — discard without
                            // publishing so the log carries no ghosts.
                            let! _ = blobStorage.Delete(IntentContainer, blobName)

                            logger.Warn
                                $"[EntityOutbox] event=intent_abandoned scope=%s{intent.ScopeId} entityType={intent.EntityType} entityId={intent.EntityId} — save never reached version {intent.MinVersionAfterSave}; events discarded, not published"

        return published
    }