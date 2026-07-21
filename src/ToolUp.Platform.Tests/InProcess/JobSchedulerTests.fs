module ToolUp.Platform.Tests.InProcess.JobSchedulerTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── InProcessJobScheduler — IJobScheduler contract binding ──────
//
// Binds the `IJobScheduler` contract pack to the Phase 9b in-process
// scheduler over `BlobJobStore` rooted in a fresh temp directory per
// factory call. Cross-test isolation is structural — each factory
// call gets its own filesystem subtree, and two scope ids are GUID-
// suffixed.
//
// The contract pack only exercises the IJobScheduler interface
// methods (Schedule, Cancel, Disable, Enable, TriggerOnce, Get,
// ListJobs). The BackgroundService dispatch loop is NOT started —
// `BackgroundService.ExecuteAsync` is only invoked by ASP.NET Core
// hosting, and this binding never calls `StartAsync`. All Phase 9c
// portability rules are about the interface shape, not the dispatch
// runtime.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Stub `INotificationChannel` that swallows publishes — the
/// scheduler only publishes a `SystemMessage` on dead-letter, which
/// the contract pack does not exercise.
let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }

        member _.Subscribe(_, _) = async { return Guid.NewGuid() }

        member _.Unsubscribe(_) = async { return () }
    }

let private buildScheduler () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-jobsched-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore
    let config = ServerConfig.defaults

    JobScheduler.create jobStore eventStore silentChannel config silentLogger (NoOpActivitySink() :> IActivitySink)

// Phase 9b.A — telemetry interface tests. Drift detection itself lives
// in the BackgroundService loop and uses `DateTime.UtcNow` directly,
// so the missed-tick edge is exercised manually per the recipe in
// `technical-guide/06-jobs-ingestion-and-diagnostics.md`. These tests
// pin the read-side contract: a fresh scheduler reports zero, the
// snapshot's `GeneratedAt` advances per call, and the
// `IJobSchedulerTelemetry` upcast succeeds (so DI registrations against
// the interface resolve at runtime).
let private telemetryTests =
    testList "InProcessJobScheduler — IJobSchedulerTelemetry" [
        test "fresh scheduler reports zero missed ticks and no last-drift" {
            let scheduler = buildScheduler () :> IJobSchedulerTelemetry
            let snap = scheduler.Snapshot()

            Expect.equal snap.TickMissedCount60Min 0 "no missed ticks at start"
            Expect.isNone snap.LastDriftMs "no last drift recorded"
            Expect.isNone snap.LastTickMissedAt "no last miss recorded"
        }

        test "consecutive snapshots advance GeneratedAt" {
            let scheduler = buildScheduler () :> IJobSchedulerTelemetry
            let first = scheduler.Snapshot()
            System.Threading.Thread.Sleep 5
            let second = scheduler.Snapshot()

            Expect.isGreaterThanOrEqual second.GeneratedAt first.GeneratedAt "GeneratedAt monotonic across calls"
        }

        test "InProcessJobScheduler upcasts to IJobSchedulerTelemetry" {
            let scheduler = buildScheduler ()

            match (scheduler :> obj) with
            | :? IJobSchedulerTelemetry -> ()
            | _ -> failtest "scheduler does not implement IJobSchedulerTelemetry"
        }
    ]

// ─── Phase 598 — event-trigger catch-up watermark ────────────────
//
// Exercises `RunCatchUpScan` directly (the hosted-service loop only
// wraps it in cadence) plus the `JobTriggerWatermark` cursor
// semantics. The "restart" in these tests is a fresh watermark over
// the same `LocalFileStorage` root — the cursor blob is the only
// state that survives a real process death.

type private RecordingHandler() =
    let executed = System.Collections.Concurrent.ConcurrentQueue<JobContext>()

    member _.Executed = executed |> List.ofSeq

    interface IJobHandler with
        member _.Execute ctx = async {
            executed.Enqueue ctx
            return Success
        }

/// Poll `predicate` until it holds or `timeoutMs` elapses — dispatch
/// is `Async.Start` fire-and-forget, so assertions on handler
/// execution need a settle wait.
let private waitFor (timeoutMs: int) (predicate: unit -> bool) : bool =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable ok = predicate ()

    while not ok && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep 25
        ok <- predicate ()

    ok

let private buildCatchUpFixture () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-jobsched-catchup-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore

    let config = {
        ServerConfig.defaults with
            JobScheduler = InProcessJobScheduler
            EventTriggerCatchUp = true
    }

    let watermark = JobTriggerWatermark.JobTriggerWatermark(storage, silentLogger)

    let scheduler =
        JobScheduler.createWithCatchUp
            jobStore
            eventStore
            silentChannel
            config
            silentLogger
            (NoOpActivitySink() :> IActivitySink)
            watermark

    storage, eventStore, scheduler, watermark

let private scheduleOnEventJob
    (scheduler: IJobScheduler)
    (handler: IJobHandler)
    (scope: string)
    (eventType: string)
    : unit =
    scheduler.RegisterHandler("catchup-test-handler", handler)

    let registration: JobRegistration = {
        ScopeId = scope
        Handler = "catchup-test-handler"
        Payload = ""
        Trigger = OnEvent eventType
        Idempotency = None
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = None
        Precision = Minute
        CreatedBy = "test"
        Tags = Map.empty
    }

    match scheduler.Schedule registration |> Async.RunSynchronously with
    | Ok _ -> ()
    | Error e -> failtestf "schedule failed: %A" e

let private eventAt (scope: string) (eventType: string) (occurredAt: DateTime) : ModuleEvent = {
    Events.create scope "test.module" eventType "{}" with
        OccurredAt = occurredAt
}

let private freshScope () =
    "catchup-" + Guid.NewGuid().ToString("N").Substring(0, 8)

let private catchUpTests =
    testList "InProcessJobScheduler — event-trigger catch-up (Phase 598)" [
        test "cursor isAfter orders by OccurredAt with Id tie-break" {
            let t = DateTime.UtcNow
            let cursorId = Guid.NewGuid()

            let cursor: JobTriggerWatermark.JobTriggerCursor = {
                LastDispatchedAt = t
                LastDispatchedEventId = cursorId
            }

            let older = eventAt "s" "e" (t.AddSeconds -1.0)
            let newer = eventAt "s" "e" (t.AddSeconds 1.0)
            let cursorOwn = { eventAt "s" "e" t with Id = cursorId }

            Expect.isFalse (JobTriggerWatermark.JobTriggerCursor.isAfter cursor older) "older event is not after"
            Expect.isTrue (JobTriggerWatermark.JobTriggerCursor.isAfter cursor newer) "newer event is after"

            Expect.isFalse
                (JobTriggerWatermark.JobTriggerCursor.isAfter cursor cursorOwn)
                "cursor's own event is not after"
        }

        test "watermark advance is monotonic and round-trips through flush + load" {
            let storage, _, _, watermark = buildCatchUpFixture ()
            let scope = freshScope ()
            let newer = eventAt scope "e" DateTime.UtcNow
            let older = eventAt scope "e" (DateTime.UtcNow.AddMinutes -5.0)

            watermark.Advance newer
            watermark.Advance older

            match watermark.TryGet scope with
            | Some c -> Expect.equal c.LastDispatchedEventId newer.Id "kept the max under out-of-order advance"
            | None -> failtest "cursor missing after advance"

            watermark.FlushDirty() |> Async.RunSynchronously

            // Fresh instance over the same storage = restarted process.
            let reloaded = JobTriggerWatermark.JobTriggerWatermark(storage, silentLogger)

            match reloaded.LoadPersisted scope |> Async.RunSynchronously with
            | JobTriggerWatermark.Loaded c -> Expect.equal c.LastDispatchedEventId newer.Id "cursor round-tripped"
            | other -> failtestf "expected Loaded, got %A" other
        }

        test "corrupt cursor blob loads as Unreadable, never Missing" {
            let storage, _, _, watermark = buildCatchUpFixture ()
            let scope = freshScope ()

            storage.Upload("_platform", $"job-triggers/{scope}.cursor", Encoding.UTF8.GetBytes "not-json{")
            |> Async.RunSynchronously
            |> ignore

            match watermark.LoadPersisted scope |> Async.RunSynchronously with
            | JobTriggerWatermark.Unreadable _ -> ()
            | other -> failtestf "expected Unreadable, got %A" other
        }

        test "startup scan dispatches the actual missed event past the persisted cursor" {
            let _, eventStore, scheduler, watermark = buildCatchUpFixture ()
            let scope = freshScope ()
            let handler = RecordingHandler()
            scheduleOnEventJob (scheduler :> IJobScheduler) handler scope "inventory.moved"

            // A prior session's cursor, 10 minutes back.
            watermark.Advance(eventAt scope "seed" (DateTime.UtcNow.AddMinutes -10.0))
            watermark.FlushDirty() |> Async.RunSynchronously

            // Durably written, never notified — the crash window.
            let missed = eventAt scope "inventory.moved" (DateTime.UtcNow.AddMinutes -2.0)
            eventStore.Write missed |> Async.RunSynchronously

            scheduler.RunCatchUpScan true |> Async.RunSynchronously

            Expect.isTrue (waitFor 5000 (fun () -> handler.Executed.Length >= 1)) "missed trigger dispatched"

            match (handler.Executed |> List.head).TriggerSource with
            | ScheduledByEvent(et, id) ->
                Expect.equal et "inventory.moved" "replay carries the real event type"
                Expect.equal id missed.Id "replay carries the real event id"
            | other -> failtestf "expected ScheduledByEvent, got %A" other
        }

        test "first enable seeds the cursor to now and replays no history" {
            let storage, eventStore, scheduler, _ = buildCatchUpFixture ()
            let scope = freshScope ()
            let handler = RecordingHandler()
            scheduleOnEventJob (scheduler :> IJobScheduler) handler scope "inventory.moved"

            // Pre-feature history that already fired live (or never will).
            eventStore.Write(eventAt scope "inventory.moved" (DateTime.UtcNow.AddMinutes -10.0))
            |> Async.RunSynchronously

            scheduler.RunCatchUpScan true |> Async.RunSynchronously

            Expect.isFalse (waitFor 1500 (fun () -> handler.Executed.Length >= 1)) "history not replayed"

            let reloaded = JobTriggerWatermark.JobTriggerWatermark(storage, silentLogger)

            match reloaded.LoadPersisted scope |> Async.RunSynchronously with
            | JobTriggerWatermark.Loaded c ->
                Expect.isGreaterThan
                    c.LastDispatchedAt
                    (DateTime.UtcNow.AddMinutes -1.0)
                    "seed cursor persisted at (approximately) now"
            | other -> failtestf "expected a persisted seed cursor, got %A" other
        }

        test "events behind the cursor beyond the overlap window are not replayed" {
            let _, eventStore, scheduler, watermark = buildCatchUpFixture ()
            let scope = freshScope ()
            let handler = RecordingHandler()
            scheduleOnEventJob (scheduler :> IJobScheduler) handler scope "inventory.moved"

            // Already-dispatched event 10 minutes back; cursor advanced
            // 5 minutes back — the event sits well past the 30s overlap.
            eventStore.Write(eventAt scope "inventory.moved" (DateTime.UtcNow.AddMinutes -10.0))
            |> Async.RunSynchronously

            watermark.Advance(eventAt scope "seed" (DateTime.UtcNow.AddMinutes -5.0))
            watermark.FlushDirty() |> Async.RunSynchronously

            scheduler.RunCatchUpScan true |> Async.RunSynchronously

            Expect.isFalse
                (waitFor 1500 (fun () -> handler.Executed.Length >= 1))
                "already-dispatched event not replayed"
        }

        test "sweep dispatches settled events past the in-memory cursor and skips fresh ones" {
            let _, eventStore, scheduler, watermark = buildCatchUpFixture ()
            let scope = freshScope ()
            let handler = RecordingHandler()
            scheduleOnEventJob (scheduler :> IJobScheduler) handler scope "sweep.event"

            watermark.Seed(scope, JobTriggerWatermark.JobTriggerCursor.at (DateTime.UtcNow.AddMinutes -10.0), false)

            // Dropped by the live hook, older than the settle window.
            let settled = eventAt scope "sweep.event" (DateTime.UtcNow.AddMinutes -2.0)
            eventStore.Write settled |> Async.RunSynchronously

            // Too fresh — its notify may still be in flight.
            let fresh = eventAt scope "sweep.event" (DateTime.UtcNow.AddSeconds -5.0)
            eventStore.Write fresh |> Async.RunSynchronously

            scheduler.RunCatchUpScan false |> Async.RunSynchronously

            Expect.isTrue (waitFor 5000 (fun () -> handler.Executed.Length >= 1)) "settled event dispatched"
            System.Threading.Thread.Sleep 500

            let dispatchedIds =
                handler.Executed
                |> List.map (fun ctx ->
                    match ctx.TriggerSource with
                    | ScheduledByEvent(_, id) -> id
                    | other -> failtestf "expected ScheduledByEvent, got %A" other)

            Expect.equal dispatchedIds [ settled.Id ] "exactly the settled event, not the fresh one"
        }
    ]

let tests =
    let factory () =
        let scheduler = buildScheduler () :> IJobScheduler
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        scheduler, "team-a-" + suffix, "team-b-" + suffix

    testList "InProcessJobScheduler" [
        IJobSchedulerContract.tests "InProcessJobScheduler" factory
        telemetryTests
        catchUpTests
    ]