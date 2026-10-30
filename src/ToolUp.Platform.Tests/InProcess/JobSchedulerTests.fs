module ToolUp.Platform.Tests.InProcess.JobSchedulerTests

open System
open System.IO
open System.Text
open Microsoft.Extensions.DependencyInjection
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

// ─── Synchronisation, not timing ──────────────────────────────────────
//
// Dispatch is `Async.Start` fire-and-forget (`TriggerOnce`, the tick
// loop, catch-up replay, the external-retry continuation), so nothing
// is guaranteed durable at the moment a trigger call returns — the
// `Running` row, the handler execution, the terminal row, the
// `LastRunStatus` definition update, and the lease release land in
// that order, each strictly later than the last. One rule governs
// every assertion downstream of a dispatch (shared with
// `RoundOrchestratorTests.fs`, which lost the same bet in forge CI
// first): wait on the production path's own observable state — the run
// row, the definition, the lease — never on a wall-clock guess about
// how far the dispatch async has got. The timeout on such a wait is a
// FAILURE PATH, not a schedule: a green run never spends it, so making
// it generous costs nothing, and a run that exhausts it has genuinely
// lost the signal rather than merely been slow.

/// Poll `predicate` until it holds or `timeoutMs` elapses. The wait
/// ends the moment the predicate holds; the timeout is spent only by a
/// genuine failure (see the rule above).
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

// ─── Phase 319 — external hand-off + reconciliation ──────────────
//
// `JobResult.HandedOff` and the reconciliation pass that owns the run
// afterwards. The "restart" here is the same device the catch-up tests
// use — a fresh scheduler instance over the same `LocalFileStorage`
// root — because the persisted run row and its `ExternalHandle` are the
// only state that survives a real process death, and that is exactly
// what 319.D is about.
//
// Reconciliation is driven through `ReconcileAwaitingExternal()` rather
// than the tick loop, for the same reason the catch-up tests call
// `RunCatchUpScan` directly: `BackgroundService.ExecuteAsync` is only
// started by ASP.NET Core hosting, and waiting for a wall-clock minute
// boundary per assertion is not a test.

/// A programmable `IExternalComputeDispatcher`. Counts `Submit` calls,
/// hands out a fresh handle each time, and reports whatever outcome the
/// test last set.
///
/// **It deliberately does NOT deduplicate by idempotency key.** The
/// 319.E guard under test is the *scheduler's*: it must refuse to
/// re-enter a handler whose hand-off is still outstanding. A fake that
/// deduped would return the existing handle and make the submit count 1
/// whether or not the guard exists — the test would pass against a
/// scheduler with no guard at all. The dispatcher's own dedup is the
/// separate second line of defence, exercised by its own test below.
type private FakeExternalDispatcher(backend: string) =
    let submits = System.Collections.Concurrent.ConcurrentQueue<ExternalWorkSpec>()
    let mutable polls = 0
    let mutable outcome = ExternalOutcome.Pending

    member _.SubmitCount = submits.Count
    member _.Submitted = submits |> List.ofSeq
    member _.PollCount = polls

    /// What the next `Poll` reports. Terminal outcomes drive
    /// reconciliation; `Pending` / `Running` leave the run awaiting.
    member _.SetOutcome(o: ExternalOutcome) = outcome <- o

    interface IExternalComputeDispatcher with
        member _.Backend = backend

        member _.Submit(_scopeId: string, spec: ExternalWorkSpec) = async {
            submits.Enqueue spec

            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = backend
                    ScopeId = _scopeId
                    NativeRef = $"native-{submits.Count}"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll(_handle: ExternalHandle) = async {
            polls <- polls + 1
            return outcome
        }

        member _.Cancel(_handle: ExternalHandle) = async { return () }

/// A dispatcher that honours `ExternalWorkSpec.Idempotency` the way
/// Phase 318 says a backend SHOULD — returning the existing handle
/// rather than starting the work twice.
type private DedupingExternalDispatcher() =
    let byKey =
        System.Collections.Concurrent.ConcurrentDictionary<string, ExternalHandle>()

    let mutable submitCalls = 0

    member _.SubmitCallCount = submitCalls
    member _.DistinctHandles = byKey.Count

    interface IExternalComputeDispatcher with
        member _.Backend = "deduping"

        member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
            submitCalls <- submitCalls + 1

            let mint () = {
                HandleId = Guid.NewGuid()
                Backend = "deduping"
                ScopeId = scopeId
                NativeRef = Guid.NewGuid().ToString("N")
                SubmittedAt = DateTime.UtcNow
            }

            match spec.Idempotency with
            | None -> return Ok(mint ())
            | Some key -> return Ok(byKey.GetOrAdd(key, (fun _ -> mint ())))
        }

        member _.Poll(_handle) = async { return ExternalOutcome.Pending }
        member _.Cancel(_handle) = async { return () }

/// A handler that submits to the dispatcher and hands off — the whole
/// shape Phase 319 exists to make viable. Records the `Attempt` of every
/// invocation, which is how the tests prove attempts are not
/// double-counted.
type private HandOffHandler(dispatcher: IExternalComputeDispatcher, idempotencyKey: string option) =
    let attempts = System.Collections.Concurrent.ConcurrentQueue<int>()

    member _.Attempts = attempts |> List.ofSeq

    interface IJobHandler with
        member _.Execute ctx = async {
            attempts.Enqueue ctx.Attempt

            // Built through the smart constructor, never a record
            // literal — the spec gains fields over time and a literal
            // here would break on the next one.
            let spec =
                let base' = ExternalWorkSpec.create "train-forecast" ctx.Payload

                match idempotencyKey with
                | Some k -> ExternalWorkSpec.withIdempotency k base'
                | None -> base'

            match! dispatcher.Submit(ctx.ScopeId, spec) with
            | Ok handle -> return HandedOff handle
            | Error e when e.Retriable -> return TransientFailure e.Message
            | Error e -> return PermanentFailure e.Message
        }

/// Captures `SystemMessage` publishes so the dead-letter notification
/// can be asserted present (a failure) or absent (a cancellation).
type private RecordingChannel() =
    let published =
        System.Collections.Concurrent.ConcurrentQueue<string * Notification>()

    member _.Published = published |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scope, notification) = async { published.Enqueue((scope, notification)) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

type private HandoffFixture = {
    Root: string
    Store: IJobStore
    EventStore: IEventStore
    Channel: RecordingChannel
    Lock: IDistributedLock
    Scheduler: JobScheduler.InProcessJobScheduler
}

/// Construct a scheduler wired to `dispatcher` over `root`. Passing an
/// explicit `IDistributedLock` (rather than letting the scheduler default
/// to the process-wide `InProcessDistributedLock.shared`) keeps the lease
/// assertions below about THIS scheduler and nothing else in the pack.
let private buildHandoffAt (root: string) (dispatcher: IExternalComputeDispatcher) : HandoffFixture =
    Directory.CreateDirectory root |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore
    let channel = RecordingChannel()
    let lck = InProcessDistributedLock.create ()

    let scheduler =
        new JobScheduler.InProcessJobScheduler(
            jobStore,
            eventStore,
            channel,
            ServerConfig.defaults,
            silentLogger,
            NoOpActivitySink() :> IActivitySink,
            distributedLock = lck,
            externalDispatcher = dispatcher
        )

    {
        Root = root
        Store = jobStore
        EventStore = eventStore
        Channel = channel
        Lock = lck
        Scheduler = scheduler
    }

let private buildHandoff (dispatcher: IExternalComputeDispatcher) : HandoffFixture =
    buildHandoffAt
        (Path.Combine(Path.GetTempPath(), "toolup-jobsched-handoff-" + Guid.NewGuid().ToString("N")))
        dispatcher

/// Register `handler` and schedule a `Manual` job, so dispatch happens
/// exactly when the test calls `TriggerOnce` — no wall-clock dependency.
/// Zero backoff so a retry sequence does not sleep through the run.
let private scheduleManualJob (fixture: HandoffFixture) (handler: IJobHandler) (maxAttempts: int) : string * JobId =
    let scheduler = fixture.Scheduler :> IJobScheduler
    let scope = "handoff-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    scheduler.RegisterHandler("handoff-handler", handler)

    let registration: JobRegistration = {
        ScopeId = scope
        Handler = "handoff-handler"
        Payload = """{"model":"forecast"}"""
        Trigger = Manual
        Idempotency = None
        RetryPolicy = {
            JobRetryPolicy.defaults with
                MaxAttempts = maxAttempts
                InitialBackoff = TimeSpan.Zero
                MaxBackoff = TimeSpan.Zero
        }
        ShardKey = None
        Precision = Minute
        CreatedBy = "test"
        Tags = Map.empty
    }

    match scheduler.Schedule registration |> Async.RunSynchronously with
    | Ok jobId -> scope, jobId
    | Error e -> failtestf "schedule failed: %A" e

let private triggerOnce (fixture: HandoffFixture) (scope: string) (jobId: JobId) =
    match
        (fixture.Scheduler :> IJobScheduler).TriggerOnce(scope, jobId, "test")
        |> Async.RunSynchronously
    with
    | Ok() -> ()
    | Error e -> failtestf "TriggerOnce failed: %s" e

/// The newest run row for a job, or `None`.
let private latestRun (fixture: HandoffFixture) (scope: string) (jobId: JobId) : JobRun option =
    fixture.Store.GetRecentRuns(scope, jobId, 20)
    |> Async.RunSynchronously
    |> List.tryHead

let private runStatusIs (fixture: HandoffFixture) scope jobId (status: JobRunStatus) () =
    match latestRun fixture scope jobId with
    | Some r -> r.Status = status
    | None -> false

let private eventTypes (fixture: HandoffFixture) (scope: string) =
    fixture.EventStore.ReadAll scope
    |> Async.RunSynchronously
    |> List.map _.EventType

/// Hand off and settle: trigger the job, wait for the run to reach
/// `AwaitingExternal`, wait for the dispatch async to finish its tail,
/// and return the awaiting run.
///
/// The second wait is load-bearing: the awaiting ROW is the dispatch
/// async's FIRST durable write in the `HandedOff` arm, and the handle
/// registration, the `JobExternalHandedOff` event, and the
/// `LastRunStatus` definition update all land after it — so a caller
/// synchronised only on the row races everything else it might assert
/// on. The definition update is the dispatch's last store write;
/// observing it means every earlier write is durable too.
let private handOffAndSettle (fixture: HandoffFixture) scope jobId : JobRun =
    triggerOnce fixture scope jobId

    Expect.isTrue (waitFor 5000 (runStatusIs fixture scope jobId AwaitingExternal)) "the run reached AwaitingExternal"

    Expect.isTrue
        (waitFor 5000 (fun () ->
            fixture.Store.Get(scope, jobId)
            |> Async.RunSynchronously
            |> Option.exists (fun j -> j.LastRunStatus = Some AwaitingExternal)))
        "the dispatch finished settling the hand-off (definition updated)"

    (latestRun fixture scope jobId).Value

let private reconcile (fixture: HandoffFixture) =
    fixture.Scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously

let private handoffTests =
    testList "InProcessJobScheduler — external hand-off (Phase 319)" [

        test "319.A/B — HandedOff parks the run in AwaitingExternal with the handle persisted" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            let scope, jobId = scheduleManualJob fixture handler 3

            let run = handOffAndSettle fixture scope jobId

            Expect.equal run.Status AwaitingExternal "run status"
            Expect.equal run.Attempt 1 "first attempt"

            // Open, not completed. A duration stamped here would read as
            // a finished attempt that took as long as `Submit`.
            Expect.isNone run.CompletedAt "an awaiting run has not completed"
            Expect.isNone run.DurationMs "an awaiting run has no duration yet"
            Expect.isNone run.Error "a hand-off is not a failure"

            match run.ExternalHandle with
            | None -> failtest "the handle was not persisted on the run"
            | Some h ->
                Expect.equal h.Backend "gpu-pool" "handle records the accepting backend"
                Expect.equal h.ScopeId scope "handle carries the submitting scope (GP 4)"
                Expect.equal h.NativeRef "native-1" "handle carries the backend's opaque token"

            // The definition reflects the awaiting state without
            // counting it as a failure.
            let job = fixture.Store.Get(scope, jobId) |> Async.RunSynchronously |> Option.get
            Expect.equal job.LastRunStatus (Some AwaitingExternal) "definition records AwaitingExternal"
            Expect.equal job.ConsecutiveFailures 0 "a hand-off does not consume the failure budget"

            let events = eventTypes fixture scope

            Expect.contains events "JobExternalHandedOff" "the hand-off event was emitted"

            // Controls: a hand-off is neither a completion nor a
            // failure, and must not present as one.
            Expect.isFalse (events |> List.contains "JobCompleted") "no premature JobCompleted"
            Expect.isFalse (events |> List.contains "JobFailed") "no JobFailed"
            Expect.isFalse (events |> List.contains "JobDeadLettered") "no JobDeadLettered"
            Expect.isEmpty fixture.Channel.Published "no notification for a hand-off"
        }

        test "319.B — the hand-off releases the dispatch lease immediately (the slot is freed)" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            let scope, jobId = scheduleManualJob fixture handler 3

            handOffAndSettle fixture scope jobId |> ignore

            // This is the acceptance criterion made observable. The
            // scheduler's occupancy for a job IS its per-JobId dispatch
            // lease, held across the whole retry loop — so a handler that
            // blocked-and-polled (the pre-319 workaround) would hold this
            // lock for the entire remote duration. That the lock is free
            // while the run is still AwaitingExternal is precisely what
            // "frees its scheduler slot immediately" means here.
            let lockId = sprintf "toolup:job-dispatch:%O" jobId

            // The dispatch async releases the lease in its `finally`,
            // strictly AFTER the definition update `handOffAndSettle`
            // synchronised on — so a one-shot `TryAcquire` here is a
            // wall-clock bet on the last sliver of the dispatch tail.
            // Poll instead: a green run acquires within a tick or two,
            // while the defect this test exists for (a scheduler
            // holding the lease across the whole external await, the
            // pre-319 blocked-and-polled shape) never releases it and
            // spends the full failure timeout.
            let mutable acquired: Lease option = None

            Expect.isTrue
                (waitFor 5000 (fun () ->
                    acquired <-
                        fixture.Lock.TryAcquire(lockId, TimeSpan.FromSeconds 5.0)
                        |> Async.RunSynchronously

                    acquired.IsSome))
                "the dispatch lease frees once the hand-off settles"

            match acquired with
            | Some lease ->
                fixture.Lock.Release lease |> Async.RunSynchronously

                // ...and the run really is still awaiting, so the free
                // lease is not just "the work finished".
                Expect.equal
                    (latestRun fixture scope jobId |> Option.map _.Status)
                    (Some AwaitingExternal)
                    "the run is still awaiting while its slot is free"
            | None -> failtest "the dispatch lease is still held while the run awaits external compute"
        }

        test "319.C — Succeeded reconciles to JobCompleted, identically to an in-process success" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            let scope, jobId = scheduleManualJob fixture handler 3

            let awaiting = handOffAndSettle fixture scope jobId

            dispatcher.SetOutcome(ExternalOutcome.Succeeded "s3://results/run-1.parquet")
            reconcile fixture

            let run = (latestRun fixture scope jobId).Value
            Expect.equal run.RunId awaiting.RunId "the SAME run went terminal — not a new row"
            Expect.equal run.Status Succeeded "run succeeded"
            Expect.isSome run.CompletedAt "terminal run is stamped completed"
            Expect.isSome run.DurationMs "terminal run has a duration"
            Expect.isNone run.Error "success carries no error"

            Expect.equal
                (run.ExternalHandle |> Option.map _.NativeRef)
                (Some "native-1")
                "the handle is retained on the terminal row for audit"

            let job = fixture.Store.Get(scope, jobId) |> Async.RunSynchronously |> Option.get
            Expect.equal job.LastRunStatus (Some Succeeded) "definition records success"
            Expect.equal job.ConsecutiveFailures 0 "failure budget reset"
            Expect.isNone job.LastRunError "no error recorded"

            let events = eventTypes fixture scope
            Expect.contains events "JobCompleted" "the standard completion event fired"

            Expect.contains
                events
                "JobExternalReconciled"
                "the external companion event fired alongside it (carrying the result ref)"

            Expect.isEmpty fixture.Channel.Published "success publishes no notification"

            // The run left the awaiting set, so it is not re-polled
            // forever. Without this the scheduler would poll a finished
            // handle on every tick for the life of the deployment.
            let stillAwaiting =
                fixture.Store.AwaitingExternalRuns(scope, 100) |> Async.RunSynchronously

            Expect.isEmpty stillAwaiting "the reconciled run left the awaiting set"

            let pollsAfterFirst = dispatcher.PollCount
            reconcile fixture
            Expect.equal dispatcher.PollCount pollsAfterFirst "a second pass does not re-poll a resolved handle"
        }

        test "319.C — Pending/Running leaves the run awaiting and writes nothing" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            let scope, jobId = scheduleManualJob fixture handler 3

            let awaiting = handOffAndSettle fixture scope jobId

            // Default outcome is Pending; then explicitly Running.
            reconcile fixture
            dispatcher.SetOutcome(ExternalOutcome.Running(Some 0.4))
            reconcile fixture

            let run = (latestRun fixture scope jobId).Value
            Expect.equal run.Status AwaitingExternal "still awaiting after two non-terminal polls"
            Expect.equal run.RunId awaiting.RunId "same run"
            Expect.isNone run.CompletedAt "not completed"

            Expect.isTrue (dispatcher.PollCount >= 2) "the handle was actually polled (the probe is live)"

            let events = eventTypes fixture scope

            Expect.isFalse
                (events |> List.contains "JobExternalReconciled")
                "a non-terminal poll emits no reconciliation event"

            Expect.equal
                (fixture.Store.AwaitingExternalRuns(scope, 100)
                 |> Async.RunSynchronously
                 |> List.length)
                1
                "the run remains in the awaiting set for the next tick"
        }

        test "319.C — a non-retriable Failed dead-letters immediately, skipping remaining attempts" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            // Three attempts available — none of which may be used.
            let scope, jobId = scheduleManualJob fixture handler 3

            handOffAndSettle fixture scope jobId |> ignore

            dispatcher.SetOutcome(ExternalOutcome.Failed(ExternalComputeError.terminal "malformed payload"))

            reconcile fixture

            let run = (latestRun fixture scope jobId).Value
            Expect.equal run.Status DeadLettered "terminal backend failure dead-letters"
            Expect.equal run.Attempt 1 "on the first attempt — remaining retries are SKIPPED, not exhausted"

            Expect.equal
                (run.Error |> Option.map (fun e -> e.Contains "malformed payload"))
                (Some true)
                "the backend's message is preserved"

            let job = fixture.Store.Get(scope, jobId) |> Async.RunSynchronously |> Option.get
            Expect.equal job.LastRunStatus (Some DeadLettered) "definition records the dead-letter"
            Expect.equal job.ConsecutiveFailures 1 "the failure budget was consumed once, not three times"

            let events = eventTypes fixture scope
            Expect.contains events "JobDeadLettered" "the standard dead-letter event fired"

            // The `Warning` notification is part of "identical to an
            // in-process failure".
            match fixture.Channel.Published with
            | [ (s, SystemMessage(level, text)) ] ->
                Expect.equal s scope "notification published to the job's scope"
                Expect.equal level SystemMessageLevel.Warning "dead-letter notifies at Warning"
                Expect.stringContains text "handoff-handler" "the notification names the handler"
            | other -> failtestf "expected exactly one Warning SystemMessage, got %A" other

            // Control: the handler was entered ONCE. A dead-letter that
            // re-dispatched would show two.
            Expect.equal handler.Attempts [ 1 ] "the handler was not re-entered"
            Expect.equal dispatcher.SubmitCount 1 "and nothing was re-submitted"
        }

        test "319.C — a retriable Failed continues the SAME retry sequence, then dead-letters" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            let maxAttempts = 3
            let scope, jobId = scheduleManualJob fixture handler maxAttempts

            handOffAndSettle fixture scope jobId |> ignore
            Expect.equal handler.Attempts [ 1 ] "attempt 1 handed off"

            dispatcher.SetOutcome(ExternalOutcome.Failed(ExternalComputeError.retriable "backend saturated"))

            // Round 1: attempt 1 failed retriably → attempt 2 dispatched
            // and hands off again.
            reconcile fixture

            Expect.isTrue
                (waitFor 5000 (fun () -> handler.Attempts.Length >= 2))
                "the retriable failure re-dispatched the job"

            Expect.isTrue
                (waitFor 5000 (runStatusIs fixture scope jobId AwaitingExternal))
                "attempt 2 handed off in turn"

            Expect.equal handler.Attempts [ 1; 2 ] "attempt NUMBER advanced — the counter continued, it did not reset"

            // Round 2: attempt 2 fails retriably → attempt 3.
            reconcile fixture
            Expect.isTrue (waitFor 5000 (fun () -> handler.Attempts.Length >= 3)) "attempt 3 dispatched"

            Expect.isTrue (waitFor 5000 (runStatusIs fixture scope jobId AwaitingExternal)) "attempt 3 handed off"

            Expect.equal handler.Attempts [ 1; 2; 3 ] "three attempts, numbered 1..3"

            // Round 3: attempt 3 == MaxAttempts, so the budget is spent
            // and a retriable failure now dead-letters.
            reconcile fixture

            Expect.isTrue
                (waitFor 5000 (runStatusIs fixture scope jobId DeadLettered))
                "the retry budget is spent — dead-letter"

            System.Threading.Thread.Sleep 400

            // THE assertion of this test. A reconciliation that restarted
            // the retry loop at attempt 1 would keep re-submitting
            // forever and never reach a dead-letter: MaxAttempts² and
            // rising. Exactly `maxAttempts` submissions is the proof that
            // attempts are not double-counted.
            Expect.equal
                handler.Attempts
                [ 1; 2; 3 ]
                "the handler was entered exactly MaxAttempts times across the whole external retry sequence"

            Expect.equal dispatcher.SubmitCount maxAttempts "one external submission per attempt — no more"

            let job = fixture.Store.Get(scope, jobId) |> Async.RunSynchronously |> Option.get
            Expect.equal job.ConsecutiveFailures 1 "one dead-letter, so one consecutive failure"

            Expect.isEmpty
                (fixture.Store.AwaitingExternalRuns(scope, 100) |> Async.RunSynchronously)
                "nothing left awaiting"
        }

        test "319.C — a retriable Failed with no attempts left dead-letters rather than retrying" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            // One attempt only: the hand-off consumed it.
            let scope, jobId = scheduleManualJob fixture handler 1

            handOffAndSettle fixture scope jobId |> ignore

            dispatcher.SetOutcome(ExternalOutcome.Failed(ExternalComputeError.retriable "still saturated"))
            reconcile fixture

            Expect.isTrue
                (waitFor 5000 (runStatusIs fixture scope jobId DeadLettered))
                "retriable + no budget = dead-letter"

            System.Threading.Thread.Sleep 400
            Expect.equal handler.Attempts [ 1 ] "no re-dispatch beyond MaxAttempts"
            Expect.equal dispatcher.SubmitCount 1 "no second submission"
        }

        test "319.C — Cancelled is terminal but not a failure" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, None)
            let scope, jobId = scheduleManualJob fixture handler 3

            handOffAndSettle fixture scope jobId |> ignore

            dispatcher.SetOutcome ExternalOutcome.Cancelled
            reconcile fixture

            let run = (latestRun fixture scope jobId).Value
            Expect.equal run.Status ExternallyCancelled "the attempt is externally cancelled"
            Expect.isSome run.CompletedAt "terminal"
            Expect.isNone run.Error "a cancellation is not an error"

            let job = fixture.Store.Get(scope, jobId) |> Async.RunSynchronously |> Option.get
            Expect.equal job.LastRunStatus (Some ExternallyCancelled) "definition records the cancellation"

            // The distinction that matters: a cancel must not push the
            // job toward an auto-disable threshold, and must not page
            // anyone.
            Expect.equal job.ConsecutiveFailures 0 "a cancellation does NOT bump ConsecutiveFailures"
            Expect.isEmpty fixture.Channel.Published "a cancellation raises no dead-letter notification"

            let events = eventTypes fixture scope
            Expect.contains events "JobExternalReconciled" "the reconciliation event fired"
            Expect.isFalse (events |> List.contains "JobDeadLettered") "no dead-letter event"
            Expect.isFalse (events |> List.contains "JobCompleted") "and no completion event"

            Expect.isEmpty
                (fixture.Store.AwaitingExternalRuns(scope, 100) |> Async.RunSynchronously)
                "the cancelled run left the awaiting set"
        }

        test "319.D — a handle persisted before a restart is polled to completion after it" {
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-jobsched-restart-" + Guid.NewGuid().ToString("N"))

            // ─── Instance 1: submit and hand off. ───
            let dispatcher1 = FakeExternalDispatcher("gpu-pool")
            let first = buildHandoffAt root dispatcher1
            let handler1 = HandOffHandler(dispatcher1, None)
            let scope, jobId = scheduleManualJob first handler1 3
            let awaiting = handOffAndSettle first scope jobId
            let handleId = awaiting.ExternalHandle.Value.HandleId

            // ─── The restart. A brand-new scheduler, a brand-new
            // dispatcher instance, a brand-new job store — sharing only
            // the blob root, which is all a real process death leaves
            // behind. Nothing is carried over in memory: instance 1's
            // handler is never registered on instance 2, so if the
            // handle were not durable there would be nothing at all to
            // resume from. ───
            let dispatcher2 = FakeExternalDispatcher("gpu-pool")
            let second = buildHandoffAt root dispatcher2

            // The store on the new instance can see the awaiting run and
            // its handle — the precondition reconciliation depends on.
            let recovered =
                second.Store.AwaitingExternalRuns(scope, 100) |> Async.RunSynchronously

            Expect.equal recovered.Length 1 "the restarted instance finds the awaiting run"

            Expect.equal
                (recovered.Head.ExternalHandle |> Option.map _.HandleId)
                (Some handleId)
                "and the persisted handle survived the restart intact"

            dispatcher2.SetOutcome(ExternalOutcome.Succeeded "s3://results/after-restart.parquet")
            reconcile second

            Expect.isTrue (dispatcher2.PollCount >= 1) "the restarted instance actually polled the recovered handle"

            let run = (latestRun second scope jobId).Value
            Expect.equal run.RunId awaiting.RunId "the pre-restart run is the one that completed"
            Expect.equal run.Status Succeeded "resumed mid-await and reached Succeeded"

            let job = second.Store.Get(scope, jobId) |> Async.RunSynchronously |> Option.get
            Expect.equal job.LastRunStatus (Some Succeeded) "the definition reflects the completion"

            // Control: the restarted instance did NOT re-run the work. A
            // recovery implemented as "re-dispatch the handler" would
            // show a submission here — and would have launched a second
            // GPU job.
            Expect.equal dispatcher2.SubmitCount 0 "recovery polled the existing handle; it re-submitted nothing"
        }

        test "319.E — a dispatch while a hand-off is outstanding does not submit twice" {
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let fixture = buildHandoff dispatcher
            let handler = HandOffHandler(dispatcher, Some "daily-fit")
            let scope, jobId = scheduleManualJob fixture handler 3

            handOffAndSettle fixture scope jobId |> ignore
            Expect.equal dispatcher.SubmitCount 1 "one submission so far"

            // The window this guard exists for: the job is triggered
            // again (a cron interval elapsing, a restart's recovery
            // re-queue, an admin re-trigger) while the external work is
            // still running. Note the fake dispatcher does NOT dedup, so
            // a second entry into the handler WOULD produce a second
            // submission — this asserts the scheduler refused to enter it.
            triggerOnce fixture scope jobId
            triggerOnce fixture scope jobId
            System.Threading.Thread.Sleep 800

            Expect.equal dispatcher.SubmitCount 1 "still exactly one external submission"
            Expect.equal handler.Attempts [ 1 ] "the handler was not re-entered while awaiting"

            let run = (latestRun fixture scope jobId).Value
            Expect.equal run.Status AwaitingExternal "the original run is untouched"
            Expect.equal run.Attempt 1 "and its attempt number did not move"

            Expect.equal
                (fixture.Store.AwaitingExternalRuns(scope, 100)
                 |> Async.RunSynchronously
                 |> List.length)
                1
                "exactly one outstanding hand-off, not two"

            // Falsifiable control: once the hand-off RESOLVES, the guard
            // must stop blocking — otherwise it would be indistinguishable
            // from a scheduler that had simply stopped dispatching this
            // job at all, and the test above would pass for the wrong
            // reason.
            dispatcher.SetOutcome(ExternalOutcome.Succeeded "done")
            reconcile fixture
            Expect.isTrue (waitFor 5000 (runStatusIs fixture scope jobId Succeeded)) "the hand-off resolved"

            triggerOnce fixture scope jobId

            Expect.isTrue
                (waitFor 5000 (fun () -> dispatcher.SubmitCount >= 2))
                "with nothing outstanding, the job dispatches and submits again"
        }

        test "319.E — the dispatcher's own idempotency key is the second line of defence" {
            // The scheduler guard cannot cover every case: a handler
            // re-entered after its run row was lost, or a handler that
            // submits more than once itself, is only protected by the
            // key. Phase 318 says a backend SHOULD return the existing
            // handle for a repeated key — this pins that the contract is
            // the one the guard leans on.
            let dispatcher = DedupingExternalDispatcher()

            let spec =
                ExternalWorkSpec.create "train-forecast" "{}"
                |> ExternalWorkSpec.withIdempotency "same-key"

            let submit () =
                match
                    (dispatcher :> IExternalComputeDispatcher).Submit("scope-1", spec)
                    |> Async.RunSynchronously
                with
                | Ok h -> h
                | Error e -> failtestf "submit failed: %A" e

            let h1 = submit ()
            let h2 = submit ()

            Expect.equal h2.HandleId h1.HandleId "the repeated key returned the EXISTING handle"
            Expect.equal dispatcher.SubmitCallCount 2 "both calls reached the backend (the probe is live)"
            Expect.equal dispatcher.DistinctHandles 1 "but only one unit of work was ever started"

            // Control: no key means no dedup, so the assertion above is
            // about the key and not about the fake collapsing everything.
            let keyless = ExternalWorkSpec.create "train-forecast" "{}"

            let a =
                match
                    (dispatcher :> IExternalComputeDispatcher).Submit("scope-1", keyless)
                    |> Async.RunSynchronously
                with
                | Ok h -> h
                | Error e -> failtestf "submit failed: %A" e

            let b =
                match
                    (dispatcher :> IExternalComputeDispatcher).Submit("scope-1", keyless)
                    |> Async.RunSynchronously
                with
                | Ok h -> h
                | Error e -> failtestf "submit failed: %A" e

            Expect.notEqual b.HandleId a.HandleId "without a key, every submit is a fresh submission"
        }

        test "319 GP 11/13 — a scheduler with no external dispatcher reconciles nothing" {
            // The default for every existing deployment. Reconciliation
            // must be entirely inert: no store query, no behaviour change,
            // and an ordinary handler unaffected.
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-jobsched-nodispatcher-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory root |> ignore
            let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let jobStore = JobStore.create storage eventStore

            // No `externalDispatcher` argument at all — the pre-319 shape.
            let scheduler =
                JobScheduler.create
                    jobStore
                    eventStore
                    silentChannel
                    ServerConfig.defaults
                    silentLogger
                    (NoOpActivitySink() :> IActivitySink)

            let handler = RecordingHandler()
            let iface = scheduler :> IJobScheduler
            iface.RegisterHandler("plain", handler)
            let scope = "plain-" + Guid.NewGuid().ToString("N").Substring(0, 8)

            let registration: JobRegistration = {
                ScopeId = scope
                Handler = "plain"
                Payload = ""
                Trigger = Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                CreatedBy = "test"
                Tags = Map.empty
            }

            let jobId =
                match iface.Schedule registration |> Async.RunSynchronously with
                | Ok id -> id
                | Error e -> failtestf "schedule failed: %A" e

            // Reconciliation is a no-op, and safe to call.
            scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously

            // An ordinary handler still behaves exactly as before (GP 11).
            match iface.TriggerOnce(scope, jobId, "test") |> Async.RunSynchronously with
            | Ok() -> ()
            | Error e -> failtestf "TriggerOnce failed: %s" e

            Expect.isTrue (waitFor 5000 (fun () -> handler.Executed.Length >= 1)) "the in-process handler ran"

            // "The handler ran" is NOT "the run is terminal": the
            // `Succeeded` row is written by the fire-and-forget dispatch
            // async AFTER the handler returns, so asserting on the store
            // straight after the handler's own signal is a wall-clock
            // bet — the one forge CI lost (observed: `Some Running`).
            // Wait on the terminal row itself.
            let latestStatus () =
                jobStore.GetRecentRuns(scope, jobId, 10)
                |> Async.RunSynchronously
                |> List.tryHead
                |> Option.map _.Status

            Expect.isTrue
                (waitFor 5000 (fun () -> latestStatus () = Some Succeeded))
                "and succeeded, unchanged by Phase 319"

            let runs = jobStore.GetRecentRuns(scope, jobId, 10) |> Async.RunSynchronously

            Expect.isTrue (runs |> List.forall (fun r -> r.ExternalHandle = None)) "no in-process run acquires a handle"
        }

        test "319 GP 11 — a JobRun blob written before Phase 319 deserialises with ExternalHandle = None" {
            // The additive-field hazard, pinned rather than assumed.
            // `FableConverters` has bitten this estate in both
            // directions — a missing list field arriving as `null` where
            // `[]` was assumed, and `Fable.SimpleJson` throwing outright
            // on the client — so "F# None is null, therefore an absent
            // field reads as None" is exactly the kind of reasoning that
            // deserves a test rather than a comment.
            let options = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

            let modern: JobRun = {
                RunId = Guid.NewGuid()
                JobId = Guid.NewGuid()
                ScopeId = "legacy-scope"
                Attempt = 2
                StartedAt = DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
                CompletedAt = Some(DateTime(2026, 1, 2, 3, 9, 5, DateTimeKind.Utc))
                Status = Failed
                Error = Some "upstream timeout"
                DurationMs = Some 300_000L
                ExternalHandle = None
            }

            // Build a genuine pre-319 blob by serialising and stripping
            // the property, rather than hand-writing JSON that might not
            // match the converter's actual shape.
            let node =
                System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(modern, options))

            let obj = node.AsObject()

            Expect.isTrue
                (obj.Remove "ExternalHandle")
                "the property exists to be removed (if this fails the field was renamed and this test is measuring nothing)"

            let legacyJson = obj.ToJsonString()

            Expect.isFalse (legacyJson.Contains "ExternalHandle") "the legacy blob genuinely lacks the field"

            let revived =
                System.Text.Json.JsonSerializer.Deserialize<JobRun>(legacyJson, options)

            Expect.isNone revived.ExternalHandle "an absent field reads as None, not a crash and not a stub handle"

            // And the rest of the record is untouched — a converter that
            // silently reset the whole record would also produce
            // `None` here.
            Expect.equal revived.RunId modern.RunId "RunId preserved"
            Expect.equal revived.Attempt 2 "Attempt preserved"
            Expect.equal revived.Status Failed "Status preserved"
            Expect.equal revived.Error (Some "upstream timeout") "Error preserved"
            Expect.equal revived.DurationMs (Some 300_000L) "DurationMs preserved"
        }

        test "319 — the new JobRunStatus cases are appended, so historical tags do not shift" {
            // `JobRunStatus` is persisted. If a representation keys on
            // tag ordinal rather than case name, inserting a case
            // mid-union silently re-reads every historical row as its
            // neighbour. This pins the ordinals of the five pre-319
            // cases.
            let tagOf (s: JobRunStatus) =
                let case, _ =
                    Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(s, typeof<JobRunStatus>)

                case.Tag

            Expect.equal (tagOf Pending) 0 "Pending is still tag 0"
            Expect.equal (tagOf Running) 1 "Running is still tag 1"
            Expect.equal (tagOf Succeeded) 2 "Succeeded is still tag 2"
            Expect.equal (tagOf Failed) 3 "Failed is still tag 3"
            Expect.equal (tagOf DeadLettered) 4 "DeadLettered is still tag 4"
            Expect.equal (tagOf AwaitingExternal) 5 "AwaitingExternal appended at 5"
            Expect.equal (tagOf ExternallyCancelled) 6 "ExternallyCancelled appended at 6"
        }
    ]

// ─── Phase 1064 — three scheduler defects, each pinned red first ─────

/// A job store that fails `RecordRun` exactly once after `Arm`, and
/// delegates everything else. Models the process death (or the storage
/// blip) between an external outcome's handle claim and its row write.
type private FailOnceRunStore(inner: IJobStore) =
    let mutable armed = 0

    member _.Arm() = Threading.Volatile.Write(&armed, 1)

    interface IJobStore with
        member _.Save d = inner.Save d
        member _.Get(scope, jobId) = inner.Get(scope, jobId)
        member _.ListJobs scope = inner.ListJobs scope
        member _.Update d = inner.Update d

        member _.FindByIdempotencyKey(scope, key, ttl, now) =
            inner.FindByIdempotencyKey(scope, key, ttl, now)

        member _.RecordRun run = async {
            if Threading.Interlocked.Exchange(&armed, 0) = 1 then
                return raise (IOException "Phase 1064 fake: the run store blipped")
            else
                return! inner.RecordRun run
        }

        member _.GetRecentRuns(scope, jobId, count) =
            inner.GetRecentRuns(scope, jobId, count)

        member _.DueJobs(scope, now) = inner.DueJobs(scope, now)

        member _.AwaitingExternalRuns(scope, limit) =
            inner.AwaitingExternalRuns(scope, limit)

        member _.ListScopesWithJobs() = inner.ListScopesWithJobs()

/// A handler that always fails transiently, so its dispatch sleeps the
/// retry backoff between attempts — holding the per-job lease throughout.
type private AlwaysTransient() =
    let mutable attempts = 0
    member _.Attempts = Threading.Volatile.Read(&attempts)

    interface IJobHandler with
        member _.Execute _ = async {
            Threading.Interlocked.Increment(&attempts) |> ignore
            return TransientFailure "Phase 1064 fake: try again later"
        }

let private defect1064Tests =
    testList "InProcessJobScheduler — Phase 1064 defects" [

        test "defect 5 — an outcome whose claim succeeded and whose row write did not is completed by the next poll" {
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-jobsched-1064-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory root |> ignore
            let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = FailOnceRunStore(JobStore.create storage eventStore)
            let channel = RecordingChannel()
            let lck = InProcessDistributedLock.create ()
            let dispatcher = FakeExternalDispatcher("gpu-pool")
            let handleStore = InMemoryExternalHandleStore() :> IExternalHandleStore

            let scheduler =
                new JobScheduler.InProcessJobScheduler(
                    store,
                    eventStore,
                    channel,
                    ServerConfig.defaults,
                    silentLogger,
                    NoOpActivitySink() :> IActivitySink,
                    distributedLock = lck,
                    externalDispatcher = dispatcher,
                    externalHandleStore = handleStore
                )

            let fixture = {
                Root = root
                Store = store
                EventStore = eventStore
                Channel = channel
                Lock = lck
                Scheduler = scheduler
            }

            let scope, jobId = scheduleManualJob fixture (HandOffHandler(dispatcher, None)) 3
            let awaiting = handOffAndSettle fixture scope jobId

            dispatcher.SetOutcome(ExternalOutcome.Succeeded "s3://results/run-1064.parquet")
            store.Arm()

            // The first poll's row write fails. Whatever it claimed, the run
            // must still be resolvable.
            reconcile fixture

            // The next poll completes it.
            reconcile fixture

            let run = (latestRun fixture scope jobId).Value
            Expect.equal run.RunId awaiting.RunId "the SAME run went terminal"

            Expect.equal
                run.Status
                Succeeded
                "the next poll completed the run instead of reporting AlreadyResolved forever"

            let completions =
                eventTypes fixture scope
                |> List.filter (fun t -> t = "JobCompleted")
                |> List.length

            Expect.equal completions 1 "the completion was emitted exactly once"
        }

        test "defect 6 — Cancel on a job sleeping in its retry backoff returns inside the ceiling, refused as busy" {
            let fixture = buildHandoff (FakeExternalDispatcher("unused"))
            let ijs = fixture.Scheduler :> IJobScheduler
            let handler = AlwaysTransient()
            ijs.RegisterHandler("backoff-handler", handler)
            let scope = "backoff-" + Guid.NewGuid().ToString("N").Substring(0, 8)

            let registration: JobRegistration = {
                ScopeId = scope
                Handler = "backoff-handler"
                Payload = "{}"
                Trigger = Manual
                Idempotency = None
                RetryPolicy = {
                    JobRetryPolicy.defaults with
                        MaxAttempts = 2
                        InitialBackoff = TimeSpan.FromMinutes 2.0
                        MaxBackoff = TimeSpan.FromMinutes 2.0
                }
                ShardKey = None
                Precision = Minute
                CreatedBy = "test"
                Tags = Map.empty
            }

            let jobId =
                match ijs.Schedule registration |> Async.RunSynchronously with
                | Ok id -> id
                | Error e -> failtestf "schedule failed: %A" e

            triggerOnce fixture scope jobId

            // The first attempt failed and recorded its row; the dispatch is
            // now asleep in a two-minute backoff, holding the job's lease.
            Expect.isTrue (waitFor 5000 (runStatusIs fixture scope jobId Failed)) "the first attempt failed"

            let sw = Diagnostics.Stopwatch.StartNew()
            let outcome = Async.Catch(ijs.Cancel(scope, jobId)) |> Async.StartAsTask
            let returned = outcome.Wait(TimeSpan.FromSeconds 20.0)
            sw.Stop()

            Expect.isTrue returned $"Cancel returned inside the ceiling (waited %d{sw.ElapsedMilliseconds} ms)"

            match outcome.Result with
            | Choice2Of2(:? JobScheduler.JobStatusBusyException as busy) ->
                Expect.equal busy.JobId jobId "the refusal names the job"
            | other -> failtestf "expected a typed busy refusal, got %A" other

            Expect.equal handler.Attempts 1 "the backoff was not cut short"
        }

        test "defect 7 — registerProviderOAuth composes beside keyed registrations of the types it probes" {
            let services = ServiceCollection() :> IServiceCollection

            // Keyed FIRST, so a probe that does not skip keyed descriptors
            // reaches one before any instance — reading
            // `ImplementationInstance` on a keyed descriptor throws.
            services.AddKeyedSingleton<Providers.IProviderProfile>(
                "tenant-a",
                Func<IServiceProvider, obj, Providers.IProviderProfile>(fun _ _ -> failwith "never resolved at compose")
            )
            |> ignore

            services.AddKeyedSingleton<IOAuthCredentialFlow>(
                "tenant-a",
                Func<IServiceProvider, obj, IOAuthCredentialFlow>(fun _ _ -> failwith "never resolved at compose")
            )
            |> ignore

            services.AddSingleton<Providers.IProviderProfile>(
                BlobProviderProfile.create (InMemoryBlobStorage.InMemoryBlobStorage())
            )
            |> ignore

            let secretStore =
                { new Secrets.ISecretStore with
                    member _.GetSecret(_, _) = async.Return None
                    member _.SetSecret(_, _, _) = async.Return(Ok())
                    member _.DeleteSecret(_, _) = async.Return(Ok())
                    member _.ListKeys _ = async.Return []
                }

            let auditLog =
                { new IAuditLog with
                    member _.Record(_, _) = async.Return()
                    member _.GetAuditTrail(_, _, _) = async.Return []
                }

            ComposeJobs.registerProviderOAuth services None secretStore auditLog silentLogger
        }
    ]

/// Phase 9c.F — the rule-4 restart arm. One temp directory is the
/// durable substrate; every `Open` builds a brand-new scheduler over a
/// brand-new `BlobJobStore` over a brand-new `LocalFileStorage` rooted
/// there, sharing no handler registry, no dispatch state and no run
/// history in memory with the instance before it.
///
/// `Close` disposes: `InProcessJobScheduler` is a `BackgroundService`,
/// and this binding never starts the hosted service — `TriggerOnce`
/// dispatches directly — so disposal is the whole of the teardown.
let private restartBindingWith (bindRing: bool) =
    let factory () =
        let root =
            Path.Combine(Path.GetTempPath(), "toolup-jobsched-restart-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let binding: IJobSchedulerContract.RestartableScheduler = {
            Open =
                fun () ->
                    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
                    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
                    let jobStore = JobStore.create storage eventStore

                    let scheduler =
                        JobScheduler.create
                            jobStore
                            eventStore
                            silentChannel
                            ServerConfig.defaults
                            silentLogger
                            (NoOpActivitySink() :> IActivitySink)

                    // Phase 935 — what compose does: bind a carrier over the
                    // key ring the root's storage persists, so a restart
                    // (a fresh carrier over the same ring) redeems.
                    if bindRing then
                        (scheduler :> IScopeCarrierBinding)
                            .BindScopeCarrier(ScopeCarrier.ofKeyRepository (BlobXmlRepository(storage)))

                    scheduler :> IJobScheduler
            Close =
                fun scheduler ->
                    match box scheduler with
                    | :? IDisposable as disposable -> disposable.Dispose()
                    | _ -> ()
            ScopeId = "team-restart-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        }

        binding

    factory ()

let private restartBinding () = restartBindingWith true

let private restartContractTests =
    IJobSchedulerContract.restartTests "InProcessJobScheduler" restartBinding

/// Phase 818 / 935 — the platform's own scheduler RE-MINTS a
/// resolver-minted scope for the job's handler, over the same restartable
/// binding.
let private carriedScopeContractTests =
    IJobSchedulerContract.carriedScopeTests "InProcessJobScheduler" restartBinding

let private pinJobId (jobId: JobId) = jobId

/// Captured from the tree before the eleven terminal sites moved onto
/// `JobLifecycle.transition`; regenerate only for a deliberate behaviour change.
let private approvedTransitionPin =
    """== completed
job <g1>
event JobCompleted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"DurationMs":<n>}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=Succeeded error=None completed=true duration=true handle=false
definition status=Active lastRunStatus=Some Succeeded lastRunError=None failures=0 lastRunAt=true nextRunAt=false
== retriable, then completed
job <g1>
event JobCompleted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2,"DurationMs":<n>}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2,"TriggerSource":{"ScheduledManually":"test"}}
event JobFailed {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g3>","Attempt":1,"Error":"transient boom","DurationMs":<n>}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g3>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g3> attempt=1 status=Failed error=Some "transient boom" completed=true duration=true handle=false
row <g2> attempt=2 status=Succeeded error=None completed=true duration=true handle=false
definition status=Active lastRunStatus=Some Succeeded lastRunError=Some "transient boom" failures=0 lastRunAt=true nextRunAt=false
== retriable until exhausted
job <g1>
event JobDeadLettered {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Error":"transient boom","Attempts":2}
event JobFailed {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2,"Error":"transient boom","DurationMs":<n>}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2,"TriggerSource":{"ScheduledManually":"test"}}
event JobFailed {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g3>","Attempt":1,"Error":"transient boom","DurationMs":<n>}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g3>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g3> attempt=1 status=Failed error=Some "transient boom" completed=true duration=true handle=false
row <g2> attempt=2 status=Failed error=Some "transient boom" completed=true duration=true handle=false
row <g4> attempt=2 status=DeadLettered error=Some "transient boom" completed=true duration=true handle=false
definition status=Active lastRunStatus=Some DeadLettered lastRunError=Some "transient boom" failures=1 lastRunAt=true nextRunAt=false
notify SystemMessage
  (Warning,
   "Job pin-handler failed permanently after 2 attempts: transient boom")
== terminal
job <g1>
event JobDeadLettered {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Error":"permanent boom","Attempts":1}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=DeadLettered error=Some "permanent boom" completed=true duration=true handle=false
definition status=Active lastRunStatus=Some DeadLettered lastRunError=Some "permanent boom" failures=1 lastRunAt=true nextRunAt=false
notify SystemMessage
  (Warning,
   "Job pin-handler failed permanently after 3 attempts: permanent boom")
== handed off
job <g1>
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=AwaitingExternal error=None completed=false duration=false handle=true
definition status=Active lastRunStatus=Some AwaitingExternal lastRunError=None failures=0 lastRunAt=true nextRunAt=false
== external succeeded
job <g1>
event JobExternalReconciled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","Outcome":"succeeded","ResultRef":"s3://pin/result","Error":null,"Retriable":null,"AwaitedMs":<n>}
event JobCompleted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"DurationMs":<n>}
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=Succeeded error=None completed=true duration=true handle=true
definition status=Active lastRunStatus=Some Succeeded lastRunError=None failures=0 lastRunAt=true nextRunAt=false
== external retriable, attempts remain
job <g1>
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-2","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2,"TriggerSource":{"ScheduledByEvent":["_external-retry","<g4>"]}}
event JobFailed {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g4>","Attempt":1,"Error":"backend blip","DurationMs":<n>}
event JobExternalReconciled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g4>","Attempt":1,"HandleId":"<g5>","Backend":"gpu-pool","NativeRef":"native-1","Outcome":"failed","ResultRef":null,"Error":"backend blip","Retriable":true,"AwaitedMs":<n>}
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g4>","Attempt":1,"HandleId":"<g5>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g4>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g4> attempt=1 status=Failed error=Some "backend blip" completed=true duration=true handle=true
row <g2> attempt=2 status=AwaitingExternal error=None completed=false duration=false handle=true
definition status=Active lastRunStatus=Some AwaitingExternal lastRunError=None failures=0 lastRunAt=true nextRunAt=false
== external retriable, attempts spent
job <g1>
event JobDeadLettered {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Error":"backend blip","Attempts":1}
event JobExternalReconciled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","Outcome":"failed","ResultRef":null,"Error":"backend blip","Retriable":true,"AwaitedMs":<n>}
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=DeadLettered error=Some "backend blip" completed=true duration=true handle=true
definition status=Active lastRunStatus=Some DeadLettered lastRunError=Some "backend blip" failures=1 lastRunAt=true nextRunAt=false
notify SystemMessage
  (Warning, "Job pin-handler failed permanently after 1 attempts: backend blip")
== external terminal failure
job <g1>
event JobDeadLettered {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Error":"backend refused","Attempts":1}
event JobExternalReconciled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","Outcome":"failed","ResultRef":null,"Error":"backend refused","Retriable":false,"AwaitedMs":<n>}
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=DeadLettered error=Some "backend refused" completed=true duration=true handle=true
definition status=Active lastRunStatus=Some DeadLettered lastRunError=Some "backend refused" failures=1 lastRunAt=true nextRunAt=false
notify SystemMessage
  (Warning,
   "Job pin-handler failed permanently after 3 attempts: backend refused")
== external cancelled
job <g1>
event JobExternalReconciled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","Outcome":"cancelled","ResultRef":null,"Error":null,"Retriable":null,"AwaitedMs":<n>}
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=ExternallyCancelled error=None completed=true duration=true handle=true
definition status=Active lastRunStatus=Some ExternallyCancelled lastRunError=None failures=0 lastRunAt=true nextRunAt=false
== reconciliation: definition gone
job <g1>
event JobExternalHandedOff {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"HandleId":"<g3>","Backend":"gpu-pool","NativeRef":"native-1","SubmittedAt":"<t>"}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g2> attempt=1 status=DeadLettered error=Some "job definition no longer exists; external hand-off abandoned" completed=true duration=true handle=true
definition <none>
== reconciliation: handle missing
job <g1>
event JobDeadLettered {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Error":"external hand-off recorded no handle; the run cannot be polled","Attempts":2}
event JobExternalHandleMissing {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g2>","Attempt":2}
event JobCompleted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g3>","Attempt":1,"DurationMs":<n>}
event JobStarted {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","RunId":"<g3>","Attempt":1,"TriggerSource":{"ScheduledManually":"test"}}
event JobScheduled {"JobId":"<g1>","ScopeId":"<scope>","Handler":"pin-handler","Trigger":"Manual","CreatedBy":"test","NextRunAt":null}
row <g3> attempt=1 status=Succeeded error=None completed=true duration=true handle=false
row <g2> attempt=2 status=DeadLettered error=Some "external hand-off recorded no handle; the run cannot be polled" completed=true duration=true handle=false
definition status=Active lastRunStatus=Some DeadLettered lastRunError=Some "external hand-off recorded no handle; the run cannot be polled" failures=1 lastRunAt=true nextRunAt=false
notify SystemMessage
  (Warning,
   "Job pin-handler failed permanently after 3 attempts: external hand-off recorded no handle; the run cannot be polled")"""

// ─── The transition pin ──────────────────────────────────────────────
//
// Every way a run ends (the four handler results, the exhausted promotion,
// the three external outcomes with the retriable one's two exits, and the two
// reconciliation abandonments) driven through the REAL scheduler, with the
// events, rows, definition fold and notification each leaves rendered as
// normalised text. `approvedTransitionPin` was captured from the tree before
// those sites moved onto `JobLifecycle.transition`, so the refactor is pinned
// by this test staying green: "identical to an in-process failure" and
// "identical whether by push or by poll" as a test rather than a comment.
//
// Normalisation keeps only what a deployment observes and neither the clock
// nor the GUID generator decides: GUIDs become first-appearance tokens,
// durations and timestamps placeholders, the scope id `<scope>`.

/// Answers `Get` with `None` for a hidden job — a definition deleted while
/// its external work ran (the reconciliation orphan).
type private HidingJobStore(inner: IJobStore) =
    let hidden = System.Collections.Concurrent.ConcurrentDictionary<JobId, bool>()

    member _.Hide(jobId: JobId) = hidden[jobId] <- true

    interface IJobStore with
        member _.Save d = inner.Save d

        member _.Get(scope, jobId) =
            if hidden.ContainsKey jobId then
                async.Return None
            else
                inner.Get(scope, jobId)

        member _.ListJobs scope = inner.ListJobs scope
        member _.Update d = inner.Update d

        member _.FindByIdempotencyKey(scope, key, ttl, now) =
            inner.FindByIdempotencyKey(scope, key, ttl, now)

        member _.RecordRun run = inner.RecordRun run

        member _.GetRecentRuns(scope, jobId, count) =
            inner.GetRecentRuns(scope, jobId, count)

        member _.DueJobs(scope, now) = inner.DueJobs(scope, now)

        member _.AwaitingExternalRuns(scope, limit) =
            inner.AwaitingExternalRuns(scope, limit)

        member _.ListScopesWithJobs() = inner.ListScopesWithJobs()

type private PinFixture = {
    Store: HidingJobStore
    EventStore: IEventStore
    Channel: RecordingChannel
    Dispatcher: FakeExternalDispatcher
    Scheduler: JobScheduler.InProcessJobScheduler
}

let private pinStore (fixture: PinFixture) = fixture.Store :> IJobStore

let private buildPin () : PinFixture =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-jobsched-pin-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = HidingJobStore(JobStore.create storage eventStore)
    let channel = RecordingChannel()
    let dispatcher = FakeExternalDispatcher("gpu-pool")

    let scheduler =
        new JobScheduler.InProcessJobScheduler(
            store,
            eventStore,
            channel,
            ServerConfig.defaults,
            silentLogger,
            NoOpActivitySink() :> IActivitySink,
            distributedLock = InProcessDistributedLock.create (),
            externalDispatcher = dispatcher
        )

    {
        Store = store
        EventStore = eventStore
        Channel = channel
        Dispatcher = dispatcher
        Scheduler = scheduler
    }

let private pinRender (fixture: PinFixture) (scope: string) (jobId: JobId) : string =
    let tokens = System.Collections.Generic.Dictionary<string, string>()

    let normalise (text: string) =
        let text = text.Replace(scope, "<scope>")

        let text =
            RegularExpressions.Regex.Replace(
                text,
                "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
                fun m ->
                    let key = m.Value.ToLowerInvariant()

                    match tokens.TryGetValue key with
                    | true, t -> t
                    | _ ->
                        let t = sprintf "<g%d>" (tokens.Count + 1)
                        tokens[key] <- t
                        t
            )

        let text =
            RegularExpressions.Regex.Replace(text, "\"(DurationMs|AwaitedMs)\":\"?[+-]?\\d+\"?", "\"$1\":<n>")

        RegularExpressions.Regex.Replace(text, "\\d{4}-\\d{2}-\\d{2}T[0-9:.]+Z?", "<t>")

    let store = pinStore fixture
    let lines = ResizeArray<string>()
    lines.Add(normalise (sprintf "job %O" jobId))

    for e in fixture.EventStore.ReadAll scope |> Async.RunSynchronously do
        lines.Add(normalise (sprintf "event %s %s" e.EventType e.Payload))

    let runs =
        store.GetRecentRuns(scope, jobId, 50)
        |> Async.RunSynchronously
        |> List.sortBy (fun r -> r.StartedAt, r.Attempt)

    for r in runs do
        lines.Add(
            normalise (
                sprintf
                    "row %O attempt=%d status=%A error=%A completed=%b duration=%b handle=%b"
                    r.RunId
                    r.Attempt
                    r.Status
                    r.Error
                    r.CompletedAt.IsSome
                    r.DurationMs.IsSome
                    r.ExternalHandle.IsSome
            )
        )

    match store.Get(scope, jobId) |> Async.RunSynchronously with
    | None -> lines.Add "definition <none>"
    | Some j ->
        lines.Add(
            sprintf
                "definition status=%A lastRunStatus=%A lastRunError=%A failures=%d lastRunAt=%b nextRunAt=%b"
                j.Status
                j.LastRunStatus
                j.LastRunError
                j.ConsecutiveFailures
                j.LastRunAt.IsSome
                j.NextRunAt.IsSome
        )

    for (_, n) in fixture.Channel.Published do
        lines.Add(normalise (sprintf "notify %A" n))

    String.Join("\n", lines)

let private settledAt (fixture: PinFixture) scope jobId (status: JobRunStatus) =
    Expect.isTrue
        (waitFor 10000 (fun () ->
            (pinStore fixture).Get(scope, jobId)
            |> Async.RunSynchronously
            |> Option.exists (fun j -> j.LastRunStatus = Some status)))
        (sprintf "the run settled at %A" status)

    // The fold is the last write; give the lease release its tail.
    Threading.Thread.Sleep 50

/// Schedule a manual job whose attempts answer `script` (the last entry
/// repeating), trigger it, and wait until its definition records `settled`.
let private pinRun
    (fixture: PinFixture)
    (maxAttempts: int)
    (script: (JobContext -> Async<JobResult>) list)
    (settled: JobRunStatus)
    : string * JobId =
    let scheduler = fixture.Scheduler :> IJobScheduler
    let scope = "pin-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    let handler =
        { new IJobHandler with
            member _.Execute ctx =
                (script |> List.item (min (ctx.Attempt - 1) (List.length script - 1))) ctx
        }

    scheduler.RegisterHandler("pin-handler", handler)

    let registration: JobRegistration = {
        ScopeId = scope
        Handler = "pin-handler"
        Payload = "{}"
        Trigger = Manual
        Idempotency = None
        RetryPolicy = {
            JobRetryPolicy.defaults with
                MaxAttempts = maxAttempts
                InitialBackoff = TimeSpan.Zero
                MaxBackoff = TimeSpan.Zero
        }
        ShardKey = None
        Precision = Minute
        CreatedBy = "test"
        Tags = Map.empty
    }

    let jobId =
        match scheduler.Schedule registration |> Async.RunSynchronously with
        | Ok outcome -> pinJobId outcome
        | Error e -> failtestf "schedule failed: %A" e

    match scheduler.TriggerOnce(scope, jobId, "test") |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "TriggerOnce failed: %A" e

    settledAt fixture scope jobId settled
    scope, jobId

let private handOff (fixture: PinFixture) : JobContext -> Async<JobResult> =
    fun ctx -> async {
        let dispatcher = fixture.Dispatcher :> IExternalComputeDispatcher

        match! dispatcher.Submit(ctx.ScopeId, ExternalWorkSpec.create "pin" ctx.Payload) with
        | Ok handle -> return HandedOff handle
        | Error e -> return PermanentFailure e.Message
    }

let private pinScenario (name: string) (drive: PinFixture -> string * JobId) : string =
    let fixture = buildPin ()
    let scope, jobId = drive fixture
    sprintf "== %s\n%s" name (pinRender fixture scope jobId)

let private backendFailure (retriable: bool) (message: string) =
    ExternalOutcome.Failed {
        Message = message
        Retriable = retriable
    }

let private pinCapture () : string =
    let succeed (_: JobContext) = async.Return Success

    let transient (_: JobContext) =
        async.Return(TransientFailure "transient boom")

    let permanent (_: JobContext) =
        async.Return(PermanentFailure "permanent boom")

    let reconciled (outcome: ExternalOutcome) (settled: JobRunStatus) (maxAttempts: int) (fixture: PinFixture) =
        let scope, jobId = pinRun fixture maxAttempts [ handOff fixture ] AwaitingExternal
        fixture.Dispatcher.SetOutcome outcome
        fixture.Scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously
        settledAt fixture scope jobId settled
        scope, jobId

    [
        pinScenario "completed" (fun f -> pinRun f 3 [ succeed ] Succeeded)
        pinScenario "retriable, then completed" (fun f -> pinRun f 3 [ transient; succeed ] Succeeded)
        pinScenario "retriable until exhausted" (fun f -> pinRun f 2 [ transient ] DeadLettered)
        pinScenario "terminal" (fun f -> pinRun f 3 [ permanent ] DeadLettered)
        pinScenario "handed off" (fun f -> pinRun f 3 [ handOff f ] AwaitingExternal)
        pinScenario "external succeeded" (reconciled (ExternalOutcome.Succeeded "s3://pin/result") Succeeded 3)
        pinScenario "external retriable, attempts remain" (fun f ->
            // The continuation re-dispatches at attempt 2, which hands off
            // again, so the definition passes `Failed` and settles awaiting.
            let scope, jobId = pinRun f 3 [ handOff f ] AwaitingExternal
            f.Dispatcher.SetOutcome(backendFailure true "backend blip")
            f.Scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously
            f.Dispatcher.SetOutcome ExternalOutcome.Pending

            Expect.isTrue
                (waitFor 10000 (fun () ->
                    (pinStore f).GetRecentRuns(scope, jobId, 10)
                    |> Async.RunSynchronously
                    |> List.exists (fun r -> r.Attempt = 2 && r.Status = AwaitingExternal)))
                "the retry continuation handed off again at attempt 2"

            settledAt f scope jobId AwaitingExternal
            scope, jobId)
        pinScenario
            "external retriable, attempts spent"
            (reconciled (backendFailure true "backend blip") DeadLettered 1)
        pinScenario "external terminal failure" (reconciled (backendFailure false "backend refused") DeadLettered 3)
        pinScenario "external cancelled" (reconciled ExternalOutcome.Cancelled ExternallyCancelled 3)
        pinScenario "reconciliation: definition gone" (fun f ->
            let scope, jobId = pinRun f 3 [ handOff f ] AwaitingExternal
            f.Store.Hide jobId
            f.Scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously
            scope, jobId)
        pinScenario "reconciliation: handle missing" (fun f ->
            let scope, jobId = pinRun f 3 [ succeed ] Succeeded

            // An awaiting row that carries no handle: hand-edited state, or
            // a store that lost the field on a round-trip.
            (pinStore f).RecordRun {
                RunId = Guid.NewGuid()
                JobId = jobId
                ScopeId = scope
                Attempt = 2
                StartedAt = DateTime.UtcNow
                CompletedAt = None
                Status = AwaitingExternal
                Error = None
                DurationMs = None
                ExternalHandle = None
            }
            |> Async.RunSynchronously

            f.Scheduler.ReconcileAwaitingExternal() |> Async.RunSynchronously
            scope, jobId)
    ]
    |> String.concat "\n"

let private pinTests =
    testList "Phase 1066 — the transition pin" [
        test "every way a run ends emits the events, rows, fold and notification it emitted before Phase 1066" {
            let actual = pinCapture ()

            match Environment.GetEnvironmentVariable "TOOLUP_TRANSITION_PIN_OUT" with
            | null
            | "" -> ()
            | path -> File.WriteAllText(path, actual)

            Expect.equal actual approvedTransitionPin "the transition pin moved"
        }
    ]

let tests =
    let factory () =
        let scheduler = buildScheduler () :> IJobScheduler
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        scheduler, "team-a-" + suffix, "team-b-" + suffix

    testList "InProcessJobScheduler" [
        IJobSchedulerContract.tests "InProcessJobScheduler" factory
        restartContractTests
        carriedScopeContractTests
        // Phase 935 — the carrier binding, over schedulers nothing has bound.
        IScopeCarrierBindingContract.tests "InProcessJobScheduler" (fun () -> restartBindingWith false)
        telemetryTests
        catchUpTests
        handoffTests
        defect1064Tests
        pinTests
    ]