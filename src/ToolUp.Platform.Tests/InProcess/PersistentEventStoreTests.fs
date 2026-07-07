module ToolUp.Platform.Tests.InProcess.PersistentEventStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

/// Per-test temp directory for the backing `LocalFileStorage`. Uses
/// `Path.GetTempPath()` so each OS places it in the conventional
/// location; the GUID suffix keeps parallel test runs isolated.
let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-persistent-event-store-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

/// Build a fresh `PersistentEventStore` over a temp-backed
/// `LocalFileStorage`. Each test gets its own isolated store — the
/// contract test pack asserts scope isolation within a store, and a
/// per-test factory avoids cross-test bleed regardless.
let private makeStore (retention: EventRetentionPolicy) : IEventStore =
    let dir = tempDir ()
    let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    PersistentEventStore.PersistentEventStore(blobStorage, retention) :> IEventStore

let private uniqueScope () =
    "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let private makeEvent
    (scopeId: string)
    (eventType: string)
    (sourceModule: string)
    (offsetMinutes: float)
    : ModuleEvent =
    {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow.AddMinutes offsetMinutes
        ScopeId = scopeId
        SourceModule = sourceModule
        EventType = eventType
        Payload = "{}"
    }

/// Contract tests — delegate to the shared pack. Retention is
/// `unlimited` so nothing the contract writes gets swept out from
/// under it.
let contractTests =
    let factory () =
        makeStore EventRetentionPolicy.unlimited

    IEventStoreContract.tests "PersistentEventStore" factory

/// Retention- and persistence-specific tests that the generic
/// `IEventStoreContract` doesn't cover.
let retentionTests =
    testList "PersistentEventStore — retention and persistence" [

        testCaseAsync "Events survive recreating the store over the same blob storage"
        <| async {
            // Simulate a server restart: same backing folder, new store instance.
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let scope = uniqueScope ()
            let event = makeEvent scope "AuditEvent" "platform" 0.0

            let first =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited) :> IEventStore

            do! first.Write event

            // New instance, same on-disk data — event must still be there.
            let second =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited) :> IEventStore

            let! events = second.ReadAll scope

            Expect.hasLength events 1 "event survived the store recreation"
            Expect.equal events.Head.Id event.Id "same event back from disk"
        }

        testCaseAsync "PruneScope removes events older than MaxAge"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            // 1-hour retention.
            let store =
                PersistentEventStore.PersistentEventStore(
                    blobStorage,
                    EventRetentionPolicy.byAge (TimeSpan.FromHours 1.0)
                )

            let scope = uniqueScope ()
            // 2 hours ago — eligible for pruning.
            let stale = makeEvent scope "Stale" "m" -120.0
            // Just now — must survive.
            let fresh = makeEvent scope "Fresh" "m" 0.0

            do! (store :> IEventStore).Write stale
            do! (store :> IEventStore).Write fresh

            let! pruned = store.PruneScope scope

            Expect.equal pruned 1 "one stale event pruned"

            let! remaining = (store :> IEventStore).ReadAll scope
            Expect.hasLength remaining 1 "only the fresh event remains"
            Expect.equal remaining.Head.EventType "Fresh" "fresh event is what survived"
        }

        testCaseAsync "PruneScope removes oldest events over MaxCountPerScope"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            // Keep only 2 events per scope.
            let store =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.byCount 2)

            let scope = uniqueScope ()
            let oldest = makeEvent scope "A" "m" -30.0
            let middle = makeEvent scope "B" "m" -20.0
            let newest = makeEvent scope "C" "m" -10.0

            do! (store :> IEventStore).Write oldest
            do! (store :> IEventStore).Write middle
            do! (store :> IEventStore).Write newest

            let! pruned = store.PruneScope scope
            Expect.equal pruned 1 "one over-cap event pruned"

            let! remaining = (store :> IEventStore).ReadAll scope
            let types = remaining |> List.map _.EventType |> List.sort

            Expect.equal types [ "B"; "C" ] "only the newest two events remain"
        }

        testCaseAsync "PruneScope is a no-op when retention policy is unlimited"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited)

            let scope = uniqueScope ()
            do! (store :> IEventStore).Write(makeEvent scope "A" "m" -999.0)
            do! (store :> IEventStore).Write(makeEvent scope "B" "m" -999.0)

            let! pruned = store.PruneScope scope
            Expect.equal pruned 0 "unlimited retention never prunes"

            let! remaining = (store :> IEventStore).ReadAll scope
            Expect.hasLength remaining 2 "both events preserved"
        }

        testCaseAsync "PruneScope does not touch other scopes"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(
                    blobStorage,
                    EventRetentionPolicy.byAge (TimeSpan.FromMinutes 1.0)
                )

            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()
            // Both stale under the 1-minute policy.
            do! (store :> IEventStore).Write(makeEvent scopeA "A" "m" -10.0)
            do! (store :> IEventStore).Write(makeEvent scopeB "B" "m" -10.0)

            // Pruning scope A must not touch scope B — scope isolation
            // is load-bearing for multi-tenant deployments.
            let! prunedA = store.PruneScope scopeA
            Expect.equal prunedA 1 "scope A pruned"

            let! remainingB = (store :> IEventStore).ReadAll scopeB
            Expect.hasLength remainingB 1 "scope B untouched by scope A pruning"
        }

        testCaseAsync "PruneScopes prunes many scopes in one call"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(
                    blobStorage,
                    EventRetentionPolicy.byAge (TimeSpan.FromMinutes 1.0)
                )

            let scopes = [ uniqueScope (); uniqueScope (); uniqueScope () ]

            for scope in scopes do
                do! (store :> IEventStore).Write(makeEvent scope "Stale" "m" -10.0)

            let! results = store.PruneScopes scopes
            Expect.equal results.Count 3 "a count per scope"

            for scope in scopes do
                Expect.equal results[scope] 1 $"scope {scope} pruned one"
        }
    ]

/// Small helper for `EventReplay` tests below — lets us compile the
/// tests alongside without a separate binding file.
module private ReplayScenario =
    type Counter = { Total: int; ByType: Map<string, int> }

    let empty = { Total = 0; ByType = Map.empty }

    let fold state (e: ModuleEvent) =
        let byType =
            state.ByType
            |> Map.change e.EventType (function
                | None -> Some 1
                | Some n -> Some(n + 1))

        {
            Total = state.Total + 1
            ByType = byType
        }

let replayTests =
    testList "EventReplay.foldScope" [

        testCaseAsync "foldScope visits events in chronological order"
        <| async {
            let store = makeStore EventRetentionPolicy.unlimited
            let scope = uniqueScope ()

            // Write out of order — ReadAll returns reverse-chronological,
            // foldScope must re-sort to chronological before folding.
            do! store.Write(makeEvent scope "A" "m" -30.0)
            do! store.Write(makeEvent scope "C" "m" -10.0)
            do! store.Write(makeEvent scope "B" "m" -20.0)

            let collector (acc: string list) (e: ModuleEvent) = acc @ [ e.EventType ]

            let! ordered = EventReplay.foldScope store scope [] collector

            Expect.equal ordered [ "A"; "B"; "C" ] "chronological fold"
        }

        testCaseAsync "foldScope aggregates state across a mixed event history"
        <| async {
            let store = makeStore EventRetentionPolicy.unlimited
            let scope = uniqueScope ()

            do! store.Write(makeEvent scope "Click" "m" -30.0)
            do! store.Write(makeEvent scope "View" "m" -20.0)
            do! store.Write(makeEvent scope "Click" "m" -10.0)

            let! totals = EventReplay.foldScope store scope ReplayScenario.empty ReplayScenario.fold

            Expect.equal totals.Total 3 "three events folded"
            Expect.equal (totals.ByType |> Map.find "Click") 2 "two clicks"
            Expect.equal (totals.ByType |> Map.find "View") 1 "one view"
        }

        testCaseAsync "foldScopeOfType filters at the store and folds chronologically"
        <| async {
            let store = makeStore EventRetentionPolicy.unlimited
            let scope = uniqueScope ()

            do! store.Write(makeEvent scope "Click" "m" -30.0)
            do! store.Write(makeEvent scope "View" "m" -20.0)
            do! store.Write(makeEvent scope "Click" "m" -10.0)

            let! clickCount = EventReplay.foldScopeOfType store scope "Click" 0 (fun acc _ -> acc + 1)

            Expect.equal clickCount 2 "only clicks counted"
        }
    ]

/// Phase 9f index-consistency + Rebuild round-trip tests. The
/// store maintains by-type and by-source indexes; deleting an
/// index ref leaves canonical authoritative; `ReadByType` resolves
/// via the index so a missing ref is invisible until `Rebuild`
/// repopulates it.
let indexTests =
    testList "PersistentEventStore — secondary indexes (Phase 9f)" [

        testCaseAsync "ReadByType matches a small subset over a large scope"
        <| async {
            // 100 events of mixed types; only 5 of type "Match".
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited)

            let storeI = store :> IEventStore
            let scope = uniqueScope ()

            for i in 1..95 do
                do! storeI.Write(makeEvent scope "Other" "m" (float -i))

            for i in 1..5 do
                do! storeI.Write(makeEvent scope "Match" "m" (float -i))

            let! matches = storeI.ReadByType(scope, "Match")
            Expect.hasLength matches 5 "ReadByType returns only the matching subset"
        }

        testCaseAsync "Index drift: deleting an index ref hides the event from ReadByType"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited)

            let storeI = store :> IEventStore
            let scope = uniqueScope ()

            let event = makeEvent scope "Drifty" "m" 0.0
            do! storeI.Write event

            let! beforeDrift = storeI.ReadByType(scope, "Drifty")
            Expect.hasLength beforeDrift 1 "indexed read finds the event before drift"

            // Delete the index ref directly; canonical stays.
            let refName = $"events/{scope}/_by-type/Drifty/{event.Id:N}.ref"
            let! _ = blobStorage.Delete("_platform", refName)

            let! afterDrift = storeI.ReadByType(scope, "Drifty")
            Expect.isEmpty afterDrift "ReadByType misses the event after the index ref is gone"

            // Canonical still works.
            let! readAll = storeI.ReadAll scope
            Expect.hasLength readAll 1 "canonical event is still on disk"
        }

        testCaseAsync "Rebuild restores indexes from canonical state"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited)

            let storeI = store :> IEventStore
            let scope = uniqueScope ()

            let events = [
                makeEvent scope "Alpha" "m1" -3.0
                makeEvent scope "Beta" "m2" -2.0
                makeEvent scope "Alpha" "m1" -1.0
            ]

            for e in events do
                do! storeI.Write e

            // Wipe both index trees.
            let! refs = blobStorage.List("_platform", $"events/{scope}/_by-type/")

            for r in refs do
                let! _ = blobStorage.Delete("_platform", r)
                ()

            let! sourceRefs = blobStorage.List("_platform", $"events/{scope}/_by-source/")

            for r in sourceRefs do
                let! _ = blobStorage.Delete("_platform", r)
                ()

            // Pre-rebuild — drifted reads return empty.
            let! preAlpha = storeI.ReadByType(scope, "Alpha")
            Expect.isEmpty preAlpha "Alpha lookups miss after wiping by-type"

            // Rebuild from canonical.
            let! count = store.Rebuild scope
            Expect.equal count 3 "rebuild processed three events"

            // Post-rebuild — both indexes serve correct results.
            let! postAlpha = storeI.ReadByType(scope, "Alpha")
            Expect.hasLength postAlpha 2 "Alpha lookups recover after rebuild"

            let! m1 = storeI.ReadBySource(scope, "m1")
            Expect.hasLength m1 2 "by-source rebuilt too"
        }

        testCaseAsync "Rebuild is idempotent — index state stays correct after double-run"
        <| async {
            // Note: each Rebuild emits an `IndexRebuilt` audit event
            // through the same `IEventStore`, so the canonical-event
            // count grows by 1 per Rebuild call. What matters for
            // idempotency is the *index state* — re-running Rebuild
            // doesn't duplicate or corrupt index entries for the
            // events that already existed.
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(blobStorage, EventRetentionPolicy.unlimited)

            let storeI = store :> IEventStore
            let scope = uniqueScope ()

            do! storeI.Write(makeEvent scope "X" "m" 0.0)
            do! storeI.Write(makeEvent scope "X" "m" 0.5)

            let! _ = store.Rebuild scope
            let! _ = store.Rebuild scope

            let! results = storeI.ReadByType(scope, "X")
            Expect.hasLength results 2 "no duplicates after double rebuild"
        }

        testCaseAsync "PruneScope removes index refs alongside canonical"
        <| async {
            let dir = tempDir ()
            let blobStorage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                PersistentEventStore.PersistentEventStore(
                    blobStorage,
                    EventRetentionPolicy.byAge (TimeSpan.FromMinutes 1.0)
                )

            let storeI = store :> IEventStore
            let scope = uniqueScope ()

            do! storeI.Write(makeEvent scope "Stale" "m" -10.0)

            let! pruned = store.PruneScope scope
            Expect.equal pruned 1 "stale event pruned"

            // Index ref must be gone too — otherwise ReadByType
            // would return a stale ref pointing at a vanished blob.
            let! typeRefs = blobStorage.List("_platform", $"events/{scope}/_by-type/")
            Expect.isEmpty typeRefs "by-type index ref removed alongside canonical"
        }
    ]

/// Exposed for Program.fs — combines contract + retention + replay
/// + Phase 9f index tests under one testList so the runner sees a
/// single entry.
let tests =
    testList "PersistentEventStore — all" [ contractTests; retentionTests; replayTests; indexTests ]