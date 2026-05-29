module ToolUp.Platform.Tests.InProcess.AuditReplicatorTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditReplicator
open ToolUp.Platform.BlobStorage

// ─── Phase 9g AuditReplicator tests ─────────────────────────────────
//
// Two layers of coverage:
//
//   1. Pure-function tests on the cursor predicate, hook decorator
//      filter, and blob cursor store round-trip — fast, deterministic.
//
//   2. End-to-end smoke test that boots the `AuditReplicator`
//      `BackgroundService`, writes audit events through the decorator,
//      and waits for delivery to land. Polling-based wait avoids
//      tight-loop dependence on the wall clock; the test passes as
//      long as the dispatcher delivers within a generous 5-second
//      window.

/// `ILogger` that swallows everything. Production-style emission would
/// inject a real logger; tests don't need to inspect log lines.
let private silentLogger =
    { new ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()
    }

/// Empty audit log — the replicator's self-emission target. Tests
/// exercising the dispatcher's audit emission would inspect a
/// capturing variant; the smoke test below only cares that batches
/// reach the sink.
type private NoOpAuditLog() =
    interface IAuditLog with
        member _.Record(_scopeId, _audit) = async { return () }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private makeAuditEvent (scopeId: string) (eventType: string) (offsetSecs: float) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow.AddSeconds offsetSecs
    ScopeId = scopeId
    SourceModule = AuditSourceModule.value
    EventType = eventType
    Payload = """{"UserId":"u1","AuthProvider":"Header"}"""
}

let private uniqueDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-audit-replicator-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let tests =
    testList "AuditReplicator" [
        // ─── Predicate tests ───────────────────────────────────────

        testCase "shouldReplicate accepts non-self audit events"
        <| fun _ ->
            let evt = makeAuditEvent "team-a" "UserLoggedIn" 0.0
            Expect.isTrue (shouldReplicate evt) "non-self audit event flows into queue"

        testCase "shouldReplicate skips non-audit events"
        <| fun _ ->
            let evt = {
                makeAuditEvent "team-a" "JobStarted" 0.0 with
                    SourceModule = "_platform.jobs"
            }

            Expect.isFalse (shouldReplicate evt) "non-audit events bypass replication"

        testCase "shouldReplicate skips replicator-self events"
        <| fun _ ->
            let delivered = makeAuditEvent "_platform" "AuditSinkDelivered" 0.0
            let failed = makeAuditEvent "_platform" "AuditSinkFailed" 0.0
            let dead = makeAuditEvent "_platform" "AuditSinkDeadLettered" 0.0

            Expect.isFalse (shouldReplicate delivered) "AuditSinkDelivered does not loop"
            Expect.isFalse (shouldReplicate failed) "AuditSinkFailed does not loop"
            Expect.isFalse (shouldReplicate dead) "AuditSinkDeadLettered does not loop"

        testCase "isReplicatorSelfEvent matches all three event-type strings"
        <| fun _ ->
            Expect.isTrue (isReplicatorSelfEvent "AuditSinkDelivered") "Delivered"
            Expect.isTrue (isReplicatorSelfEvent "AuditSinkFailed") "Failed"
            Expect.isTrue (isReplicatorSelfEvent "AuditSinkDeadLettered") "DeadLettered"
            Expect.isFalse (isReplicatorSelfEvent "UserLoggedIn") "user audit events do not match"
            Expect.isFalse (isReplicatorSelfEvent "AuditSinkSomethingNew") "fuzzy match rejected"

        // ─── Cursor predicate ─────────────────────────────────────

        testCase "AuditReplicatorCursor.isAfter strictly newer by OccurredAt"
        <| fun _ ->
            let cursor = {
                LastDeliveredAt = DateTime(2026, 5, 4, 12, 0, 0)
                LastDeliveredEventId = Guid.NewGuid()
            }

            let newer = {
                makeAuditEvent "x" "UserLoggedIn" 0.0 with
                    OccurredAt = DateTime(2026, 5, 4, 12, 0, 1)
            }

            let older = {
                makeAuditEvent "x" "UserLoggedIn" 0.0 with
                    OccurredAt = DateTime(2026, 5, 4, 11, 59, 59)
            }

            Expect.isTrue (AuditReplicatorCursor.isAfter cursor newer) "newer event passes filter"
            Expect.isFalse (AuditReplicatorCursor.isAfter cursor older) "older event is filtered out"

        testCase "AuditReplicatorCursor.isAfter tie-breaks on Guid for equal OccurredAt"
        <| fun _ ->
            let cursorGuid = Guid("00000000-0000-0000-0000-000000000000")
            let largerGuid = Guid("ffffffff-ffff-ffff-ffff-ffffffffffff")
            let ts = DateTime(2026, 5, 4, 12, 0, 0)

            let cursor = {
                LastDeliveredAt = ts
                LastDeliveredEventId = cursorGuid
            }

            let evtSameTime = {
                makeAuditEvent "x" "UserLoggedIn" 0.0 with
                    OccurredAt = ts
                    Id = largerGuid
            }

            Expect.isTrue
                (AuditReplicatorCursor.isAfter cursor evtSameTime)
                "equal OccurredAt + larger Id is strictly after the cursor"

            // Same timestamp and SAME Id — exactly at the cursor, not after
            let evtAtCursor = { evtSameTime with Id = cursorGuid }

            Expect.isFalse (AuditReplicatorCursor.isAfter cursor evtAtCursor) "cursor's own position is not 'after'"

        // ─── Phase 66 C.2 — AuditSamplingPolicy ────────────────────

        testCase "AuditSamplingPolicy.none keeps every event for every kind"
        <| fun _ ->
            let kinds = [ AnonymousAuditKind; UserAuditKind; TeamAuditKind; ClaimAuditKind ]

            for kind in kinds do
                for _ in 1..50 do
                    Expect.isTrue
                        (AuditSamplingPolicy.shouldDeliver AuditSamplingPolicy.none kind (Guid.NewGuid()))
                        $"default policy keeps every {kind} event (GP 11 backward-compat)"

        testCase "AuditSamplingPolicy rate 0.0 drops every event for that kind"
        <| fun _ ->
            let policy = {
                AuditSamplingPolicy.none with
                    Anonymous = 0.0
            }

            for _ in 1..50 do
                Expect.isFalse
                    (AuditSamplingPolicy.shouldDeliver policy AnonymousAuditKind (Guid.NewGuid()))
                    "rate 0.0 drops all anonymous events"

        testCase "AuditSamplingPolicy keep/skip decision is deterministic per event id"
        <| fun _ ->
            let policy = {
                AuditSamplingPolicy.none with
                    Anonymous = 0.5
            }

            let id = Guid.NewGuid()
            let first = AuditSamplingPolicy.shouldDeliver policy AnonymousAuditKind id

            for _ in 1..20 do
                Expect.equal
                    (AuditSamplingPolicy.shouldDeliver policy AnonymousAuditKind id)
                    first
                    "same event id yields the same decision on every call (stable across live-hook + catch-up sweep)"

        testCase "AuditSamplingPolicy rates are independent per subject kind"
        <| fun _ ->
            // Anonymous fully dropped, everything else fully kept.
            let policy = {
                AuditSamplingPolicy.none with
                    Anonymous = 0.0
            }

            for _ in 1..50 do
                let id = Guid.NewGuid()
                Expect.isFalse (AuditSamplingPolicy.shouldDeliver policy AnonymousAuditKind id) "anonymous dropped"
                Expect.isTrue (AuditSamplingPolicy.shouldDeliver policy UserAuditKind id) "user kept"
                Expect.isTrue (AuditSamplingPolicy.shouldDeliver policy TeamAuditKind id) "team kept"
                Expect.isTrue (AuditSamplingPolicy.shouldDeliver policy ClaimAuditKind id) "claim kept"

        testCase "AuditSamplingPolicy keeps approximately the configured fraction over many ids"
        <| fun _ ->
            // Validates the hash distribution: a 0.1 rate over 20k random
            // ids should keep ~10%. Generous tolerance (±3%) — this guards
            // against a degenerate hash (all-keep / all-drop / heavily
            // skewed), not a precise statistical claim.
            let policy = {
                AuditSamplingPolicy.none with
                    Anonymous = 0.1
            }

            let n = 20000

            let kept =
                seq { 1..n }
                |> Seq.filter (fun _ -> AuditSamplingPolicy.shouldDeliver policy AnonymousAuditKind (Guid.NewGuid()))
                |> Seq.length

            let fraction = float kept / float n
            Expect.isTrue (fraction > 0.07 && fraction < 0.13) $"kept fraction {fraction} is near the 0.1 target"

        // ─── BlobAuditReplicatorCursorStore round-trip ─────────────

        testCaseAsync "BlobAuditReplicatorCursorStore.Load returns empty when blob missing"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                BlobAuditReplicatorCursorStore(storage, silentLogger) :> IAuditReplicatorCursorStore

            let! cursor = store.Load("sink-x", "team-a")

            Expect.equal cursor AuditReplicatorCursor.empty "missing cursor blob returns empty"
        }

        testCaseAsync "BlobAuditReplicatorCursorStore.Save then Load round-trips the cursor"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                BlobAuditReplicatorCursorStore(storage, silentLogger) :> IAuditReplicatorCursorStore

            let original = {
                LastDeliveredAt = DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)
                LastDeliveredEventId = Guid.NewGuid()
            }

            do! store.Save("sink-x", "team-a", original)
            let! loaded = store.Load("sink-x", "team-a")

            Expect.equal loaded.LastDeliveredAt original.LastDeliveredAt "OccurredAt round-trip"
            Expect.equal loaded.LastDeliveredEventId original.LastDeliveredEventId "EventId round-trip"
        }

        testCaseAsync "BlobAuditReplicatorCursorStore isolates per-(sinkName, scopeId)"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

            let store =
                BlobAuditReplicatorCursorStore(storage, silentLogger) :> IAuditReplicatorCursorStore

            let cursorA = {
                LastDeliveredAt = DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)
                LastDeliveredEventId = Guid.NewGuid()
            }

            let cursorB = {
                LastDeliveredAt = DateTime(2026, 5, 4, 13, 0, 0, DateTimeKind.Utc)
                LastDeliveredEventId = Guid.NewGuid()
            }

            do! store.Save("sink-x", "team-a", cursorA)
            do! store.Save("sink-x", "team-b", cursorB)

            let! loadedA = store.Load("sink-x", "team-a")
            let! loadedB = store.Load("sink-x", "team-b")
            let! loadedSinkY = store.Load("sink-y", "team-a")

            Expect.equal loadedA cursorA "team-a cursor isolated"
            Expect.equal loadedB cursorB "team-b cursor isolated"
            Expect.equal loadedSinkY AuditReplicatorCursor.empty "different sink starts empty"
        }

        // ─── End-to-end smoke test ────────────────────────────────

        testCaseAsync "AuditReplicator delivers audit events to registered sink end-to-end"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            let innerStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let cursorStore =
                BlobAuditReplicatorCursorStore(storage, silentLogger) :> IAuditReplicatorCursorStore

            let auditLog = NoOpAuditLog() :> IAuditLog
            let sink = InMemoryAuditSink "smoke-test"

            // Tight LingerMs + small batch so the test completes quickly.
            let options = {
                AuditReplicatorOptions.defaults with
                    BatchPolicy = {
                        AuditReplicatorBatchPolicy.defaults with
                            LingerMs = 100
                            MaxBatchSize = 10
                    }
                    // Disable catch-up sweep — we're testing the live hook.
                    CatchUpSweepInterval = TimeSpan.MaxValue
            }

            let replicator =
                new AuditReplicator(
                    [ sink :> IAuditSink ],
                    cursorStore,
                    innerStore,
                    auditLog,
                    options,
                    AuditSamplingPolicy.none,
                    silentLogger
                )

            // Wrap inner store with the replicator's hook decorator.
            let hookedStore =
                AuditReplicationHookedEventStore(innerStore, replicator.Enqueue) :> IEventStore

            // Start the BackgroundService.
            use cts = new CancellationTokenSource()

            let svcTask =
                (replicator :> Microsoft.Extensions.Hosting.IHostedService).StartAsync(cts.Token)

            do! svcTask |> Async.AwaitTask

            // Write three audit events through the decorated store.
            let events = [
                makeAuditEvent "team-a" "UserLoggedIn" 0.0
                makeAuditEvent "team-a" "FileUploaded" 0.1
                makeAuditEvent "team-a" "FileUploaded" 0.2
            ]

            for e in events do
                do! hookedStore.Write e

            // Wait up to 5 seconds for the dispatcher to deliver.
            let deadline = DateTime.UtcNow.AddSeconds 5.0

            let mutable delivered = sink.TotalDelivered

            while delivered < 3 && DateTime.UtcNow < deadline do
                do! Async.Sleep 50
                delivered <- sink.TotalDelivered

            // Stop the service before assertion so we don't leak the
            // background loop into other tests.
            do!
                (replicator :> Microsoft.Extensions.Hosting.IHostedService).StopAsync(cts.Token)
                |> Async.AwaitTask

            Expect.equal sink.TotalDelivered 3 "all three events delivered to sink within 5 seconds"
        }

        testCaseAsync "AuditReplicator filters out replicator-self events"
        <| async {
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            let innerStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let cursorStore =
                BlobAuditReplicatorCursorStore(storage, silentLogger) :> IAuditReplicatorCursorStore

            let auditLog = NoOpAuditLog() :> IAuditLog
            let sink = InMemoryAuditSink "filter-test"

            let options = {
                AuditReplicatorOptions.defaults with
                    BatchPolicy = {
                        AuditReplicatorBatchPolicy.defaults with
                            LingerMs = 100
                    }
                    CatchUpSweepInterval = TimeSpan.MaxValue
            }

            let replicator =
                new AuditReplicator(
                    [ sink :> IAuditSink ],
                    cursorStore,
                    innerStore,
                    auditLog,
                    options,
                    AuditSamplingPolicy.none,
                    silentLogger
                )

            let hookedStore =
                AuditReplicationHookedEventStore(innerStore, replicator.Enqueue) :> IEventStore

            use cts = new CancellationTokenSource()

            do!
                (replicator :> Microsoft.Extensions.Hosting.IHostedService).StartAsync(cts.Token)
                |> Async.AwaitTask

            // Write a self-event (should NOT be replicated) and a real
            // audit event (should be replicated).
            do! hookedStore.Write(makeAuditEvent "_platform" "AuditSinkDelivered" 0.0)
            do! hookedStore.Write(makeAuditEvent "team-a" "UserLoggedIn" 0.1)

            // Wait for the real event to land.
            let deadline = DateTime.UtcNow.AddSeconds 5.0

            while sink.TotalDelivered < 1 && DateTime.UtcNow < deadline do
                do! Async.Sleep 50

            // Give a brief settle time so any leaked AuditSinkDelivered
            // would have arrived if the filter were broken.
            do! Async.Sleep 300

            do!
                (replicator :> Microsoft.Extensions.Hosting.IHostedService).StopAsync(cts.Token)
                |> Async.AwaitTask

            Expect.equal sink.TotalDelivered 1 "exactly one event delivered (the non-self UserLoggedIn)"
        }
    ]