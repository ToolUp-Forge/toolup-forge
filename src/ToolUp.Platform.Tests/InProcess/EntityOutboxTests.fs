module ToolUp.Platform.Tests.InProcess.EntityOutboxTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Phase 599 — entity-write outbox ─────────────────────────────────
//
// `SaveWithEvents` couples an entity save to its `IEventStore` events
// via a write-ahead intent + version witness. This pack drives the
// happy path, the crash shapes (publish deferred, save-never-committed
// ghost prevention), and the relay's witness discrimination. "Crashes"
// are simulated by staging state directly and running the relay over a
// fresh `OutboxEntityStore` instance — the blob substrate is the only
// state that survives a real process death.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

type LedgerEntry = {
    Id: EntityId
    Type: string
    Version: int
    Note: string
}

[<Literal>]
let private LedgerType = "OutboxLedgerEntry"

let private mkEntry (id: EntityId) (note: string) : LedgerEntry = {
    Id = id
    Type = LedgerType
    Version = 0
    Note = note
}

/// Event store whose `Write` always throws — simulates the event store
/// being down at save time. Reads return empty.
type private FaultingEventStore() =
    interface IEventStore with
        member _.Write(_evt) = async { return failwith "simulated event-store outage" }
        member _.ReadAll(_scopeId) = async { return [] }
        member _.ReadByType(_scopeId, _eventType) = async { return [] }
        member _.ReadBySource(_scopeId, _sourceModule) = async { return [] }
        member _.ListScopes() = async { return [] }

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = async {
            return Ok(Unchecked.defaultof<ErasureSummary>)
        }

/// Shared substrate: LocalFileStorage root + entity store + registry.
/// `mkOutbox` binds a (possibly different) event store + relay windows
/// over the same substrate — the "restarted process" in crash tests.
let private fixture () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-entity-outbox-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<LedgerEntry>(EntityRegistration.create<LedgerEntry> LedgerType)
    let entityStore = BlobEntityStore(dos, blob, registry, None) :> IEntityStore

    let mkOutbox (eventStore: IEventStore) (settle: TimeSpan) (abandon: TimeSpan) =
        EntityOutbox.OutboxEntityStore(
            entityStore,
            eventStore,
            blob,
            silentLogger,
            settleWindow = settle,
            abandonWindow = abandon
        )

    blob, entityStore, mkOutbox

let private scopeFor () =
    "outbox-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let private eventFor (scope: string) (note: string) =
    Events.create scope "test.ledger" "LedgerMoved" $"{{\"note\":\"{note}\"}}"

[<Tests>]
let tests =
    testList "Phase 599 — entity-write outbox" [

        testCaseAsync "happy path: entity saved, events published, no intent left behind"
        <| async {
            let _, entityStore, mkOutbox = fixture ()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let outbox =
                mkOutbox events EntityOutbox.defaultSettleWindow EntityOutbox.defaultAbandonWindow

            let scope = scopeFor ()

            let! result =
                outbox.SaveWithEvents(scope, LedgerType, "e-1", mkEntry "e-1" "first", [ eventFor scope "first" ])

            match result with
            | Ok entityRef -> Expect.equal entityRef.Version 1 "first save gets version 1"
            | Error e -> failtestf "save failed: %A" e

            let! saved = entityStore.Get<LedgerEntry>(scope, LedgerType, "e-1")
            Expect.isOk saved "entity readable through the plain store"

            let! published = events.ReadByType(scope, "LedgerMoved")
            Expect.equal published.Length 1 "event published on the happy path"

            let! pending = outbox.PendingCount()
            Expect.equal pending 0 "intent cleared after publish"
        }

        testCaseAsync "event-store outage at save time: entity commits, relay publishes on recovery"
        <| async {
            let _, entityStore, mkOutbox = fixture ()
            let scope = scopeFor ()

            // Save while the event store is down.
            let downOutbox =
                mkOutbox (FaultingEventStore() :> IEventStore) TimeSpan.Zero EntityOutbox.defaultAbandonWindow

            let! result =
                downOutbox.SaveWithEvents(
                    scope,
                    LedgerType,
                    "e-2",
                    mkEntry "e-2" "spilled",
                    [ eventFor scope "spilled" ]
                )

            Expect.isOk result "the entity mutation commits despite the event-store outage"
            let! pending = downOutbox.PendingCount()
            Expect.equal pending 1 "the intent survives for the relay"

            // "Restart" — a fresh outbox over the same substrate with the
            // store recovered.
            let recovered = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let relayOutbox = mkOutbox recovered TimeSpan.Zero EntityOutbox.defaultAbandonWindow
            let! published = relayOutbox.RelayOnce 100
            Expect.equal published 1 "relay published the deferred intent"

            let! landed = recovered.ReadByType(scope, "LedgerMoved")
            Expect.equal landed.Length 1 "the staged event reached the recovered store"

            let! remaining = relayOutbox.PendingCount()
            Expect.equal remaining 0 "intent cleared after relay"

            // The witness held: the entity is present at version 1.
            let! versions = entityStore.ListVersions<LedgerEntry>(scope, LedgerType, "e-2")
            Expect.equal (versions |> List.map _.Version) [ 1 ] "entity committed exactly once"
        }

        testCaseAsync "a failed save withdraws its intent and publishes nothing"
        <| async {
            let _, _, mkOutbox = fixture ()
            let scope = scopeFor ()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            // The entity record's Type field lies, so `BlobEntityStore`
            // rejects the save AFTER the intent was staged —
            // `SaveWithEvents` must withdraw the intent and publish
            // nothing. (The crash variant of a never-committed save —
            // process death between staging and save — is the abandoned-
            // intent test below.)
            let outbox = mkOutbox events TimeSpan.Zero TimeSpan.Zero

            let! result =
                outbox.SaveWithEvents(
                    scope,
                    LedgerType,
                    "e-3",
                    {
                        mkEntry "e-3" "bad" with
                            Type = "WrongType"
                    },
                    [ eventFor scope "ghost" ]
                )

            Expect.isError result "the lying save fails"
            let! pendingAfterWithdraw = outbox.PendingCount()
            Expect.equal pendingAfterWithdraw 0 "failed save withdraws its intent immediately"

            let! published = events.ReadByType(scope, "LedgerMoved")
            Expect.isEmpty published "no ghost events for a mutation that never happened"
        }

        testCaseAsync "relay discards an abandoned unwitnessed intent without publishing"
        <| async {
            let blob, _, mkOutbox = fixture ()
            let scope = scopeFor ()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            // Hand-stage an intent for a save that never happened — the
            // crash-between-staging-and-save window. Backdate CreatedAt
            // past the abandon window.
            let intent: EntityOutbox.OutboxIntent = {
                IntentId = Guid.NewGuid()
                ScopeId = scope
                EntityType = LedgerType
                EntityId = "e-never"
                MinVersionAfterSave = 1
                CreatedAt = DateTime.UtcNow.AddMinutes -30.0
                Events = [ eventFor scope "ghost" ]
            }

            let json =
                System.Text.Json.JsonSerializer.Serialize(
                    intent,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                )

            let blobName =
                $"entity-outbox/{scope}/{intent.CreatedAt.Ticks:D19}-{intent.IntentId:N}.json"

            let! _ = blob.Upload("_platform", blobName, System.Text.Encoding.UTF8.GetBytes json)

            let outbox = mkOutbox events TimeSpan.Zero (TimeSpan.FromMinutes 10.0)
            let! published = outbox.RelayOnce 100
            Expect.equal published 0 "nothing published for the unwitnessed intent"

            let! pending = outbox.PendingCount()
            Expect.equal pending 0 "abandoned intent discarded"

            let! landed = events.ReadByType(scope, "LedgerMoved")
            Expect.isEmpty landed "the log carries no ghost events"
        }

        testCaseAsync "relay publishes a crash-orphaned intent once the save is version-witnessed"
        <| async {
            let blob, entityStore, mkOutbox = fixture ()
            let scope = scopeFor ()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            // The crash-between-save-and-publish window: entity saved,
            // intent still staged, publish never ran.
            let! saved = entityStore.Save<LedgerEntry>(scope, mkEntry "e-4" "orphan")
            Expect.isOk saved "direct save succeeds"

            let intent: EntityOutbox.OutboxIntent = {
                IntentId = Guid.NewGuid()
                ScopeId = scope
                EntityType = LedgerType
                EntityId = "e-4"
                MinVersionAfterSave = 1
                CreatedAt = DateTime.UtcNow.AddMinutes -1.0
                Events = [ eventFor scope "orphan" ]
            }

            let json =
                System.Text.Json.JsonSerializer.Serialize(
                    intent,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                )

            let blobName =
                $"entity-outbox/{scope}/{intent.CreatedAt.Ticks:D19}-{intent.IntentId:N}.json"

            let! _ = blob.Upload("_platform", blobName, System.Text.Encoding.UTF8.GetBytes json)

            let outbox = mkOutbox events TimeSpan.Zero (TimeSpan.FromMinutes 10.0)
            let! published = outbox.RelayOnce 100
            Expect.equal published 1 "witnessed intent published"

            let! landed = events.ReadByType(scope, "LedgerMoved")
            Expect.equal landed.Length 1 "the orphaned event reached the store"

            let! pending = outbox.PendingCount()
            Expect.equal pending 0 "intent cleared"
        }
    ]