module ToolUp.Platform.Tests.InProcess.CronTickElectionTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── Phase 766 — the cron tick is a single-leader election ───────────
//
// Phase 16a's audit found the scheduler's cron due-job tick to be the one
// genuinely unleased cross-silo tick, and worse than unleased: every
// replica's tick reads the same due job from shared state, the contended
// loser of the per-job dispatch lease QUEUED on `acquireBlocking` rather
// than skipping, and the re-read inside the lease checked `Status` and
// outstanding external work but never whether the job was still due. So
// two `WorkerOnly` replicas over one shared lease store ran one due cron
// job twice — the loser simply ran it after the winner.
//
// Phase 766 makes the tick an election with TWO skip conditions, and this
// pack pins one arm on each:
//
//   1. the contended loser returns immediately (`TryAcquire` → `None`);
//   2. a loser whose acquire lands only AFTER the winner has released
//      re-reads the job, finds `NextRunAt` advanced past its own tick, and
//      skips rather than runs.
//
// **The pack is built the way `WebhookFailureStateLeaseTests` is built,
// for the same reason: to make the claim falsifiable.** Every arm asserts
// the OBSERVABLE EFFECT — handler invocations and persisted run rows —
// never a lock call's return value. The two-replica arms are PAIRED WITH A
// CONTROL on the identical construction whose only difference is that
// each replica holds its own lock table (which is exactly what the
// in-process default is across a process boundary): that arm MUST observe
// the double-run, or the shared-lease arms could be passing because the
// two ticks never overlapped at all.
//
// **The overlap is PLACED, not raced for.** The handler parks the winner
// inside its run until the loser has reached the instant the arm is
// about; the loser's lock, where the arm needs it, admits the acquire only
// once the winner has released. Deterministic in both directions.
//
// **Red before the fix.** Both shared-lease arms fail against the
// pre-766 code path — `acquireBlocking` for a cron source and a re-read
// without the due-ness check — with two handler invocations and two run
// rows where one is asserted. That was verified by reverting exactly those
// two edits in `JobScheduler.fs` with this file in place (the pre-fix
// build has no `RunTick`, so the revert is of the election, not of the
// whole commit): both arms red, the control and the single-replica arm
// unchanged. The commit that made them green is `16078836` —
// `feat(platform): Phase 766 — cron-tick single-leader election`. If
// either arm ever goes green with the election reverted, the overlap is
// no longer being placed and the control arm is where to look first.

// ─── Substrate doubles ───────────────────────────────────────────────

/// Records every `Info` line. The scheduler names each election outcome
/// in a structured log event (`cron_tick_skipped_contended` /
/// `cron_tick_skipped_not_due`), and those are the deterministic
/// observables for "the loser finished by SKIPPING" — a loser that ran
/// would have released a lease instead.
type private RecordingLogger() =
    let lines = ConcurrentQueue<string>()

    member _.Lines = lines |> List.ofSeq

    member _.Has(fragment: string) =
        lines |> Seq.exists (fun l -> l.Contains fragment)

    interface ILogger with
        member _.Debug _ = ()
        member _.Info m = lines.Enqueue m
        member _.Warn m = lines.Enqueue m
        member _.Error(m, _) = lines.Enqueue m

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

/// Wait for `task` or fail the test with `what` — never a bare timeout.
let private awaitOrFail (what: string) (deadline: TimeSpan) (task: Task) : Async<unit> = async {
    let! winner = Task.WhenAny(task, Task.Delay deadline) |> Async.AwaitTask

    if not (obj.ReferenceEquals(winner, task)) then
        failtestf "timed out after %O waiting for: %s" deadline what
}

/// Poll until `condition` holds or the deadline passes. Dispatch is
/// fire-and-forget inside the scheduler, so completion is observed, not
/// awaited. Returns whether the condition was reached.
let private waitFor (deadline: TimeSpan) (condition: unit -> bool) : Async<bool> = async {
    let started = DateTime.UtcNow
    let mutable met = condition ()

    while not met && DateTime.UtcNow - started < deadline do
        do! Async.Sleep 25
        met <- condition ()

    return met
}

// ─── Lock instrumentation ────────────────────────────────────────────

/// One replica's view of the lease store. Counts contended acquires and
/// releases, and — where an arm needs it — holds `TryAcquire` at the
/// door until `admitAfter` completes, so the loser's acquire can be
/// PLACED after the winner's release rather than raced against it.
type private ReplicaLock(inner: IDistributedLock, admitAfter: Task option) =
    let contended =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable releases = 0
    let mutable contentions = 0

    /// Completes the first time an acquire through this replica finds
    /// the id held elsewhere.
    member _.Contended = contended.Task

    /// Completes the first time this replica releases a lease.
    member _.ReleasedOnce = released.Task

    member _.Releases = Volatile.Read &releases
    member _.Contentions = Volatile.Read &contentions

    interface IDistributedLock with
        member _.TryAcquire(lockId, ttl) = async {
            match admitAfter with
            | Some gate -> do! gate |> Async.AwaitTask
            | None -> ()

            match! inner.TryAcquire(lockId, ttl) with
            | None ->
                Interlocked.Increment &contentions |> ignore
                contended.TrySetResult() |> ignore
                return None
            | Some lease -> return Some lease
        }

        member _.Renew lease = inner.Renew lease

        member _.Release lease = async {
            do! inner.Release lease
            Interlocked.Increment &releases |> ignore
            released.TrySetResult() |> ignore
        }

// ─── The handler ─────────────────────────────────────────────────────

/// Counts invocations across BOTH replicas, and parks every invocation
/// until `holdUntil` completes — the winner's run is held open for as
/// long as the arm needs the overlap to exist.
type private ParkingHandler(holdUntil: Task) =
    let entered = ConcurrentQueue<DateTime>()

    let firstEntry =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let secondEntry =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Invocations = entered.Count
    member _.FirstEntry = firstEntry.Task
    member _.SecondEntry = secondEntry.Task

    interface IJobHandler with
        member _.Execute _ctx = async {
            entered.Enqueue DateTime.UtcNow

            if entered.Count >= 2 then
                secondEntry.TrySetResult() |> ignore

            firstEntry.TrySetResult() |> ignore
            do! holdUntil |> Async.AwaitTask
            return Success
        }

// ─── Fixture ─────────────────────────────────────────────────────────

type private Replica = {
    Store: IJobStore
    Lock: ReplicaLock
    Logger: RecordingLogger
    Scheduler: JobScheduler.InProcessJobScheduler
}

/// One `WorkerOnly` replica over the SHARED storage root — its own
/// `IJobStore` instance (two processes each open the store), its own
/// logger, and the lock view the arm hands it.
let private replicaOver (storage: IBlobStorage) (eventStore: IEventStore) (handler: IJobHandler) (lck: ReplicaLock) =
    let store = JobStore.create storage eventStore
    let logger = RecordingLogger()

    let scheduler =
        new JobScheduler.InProcessJobScheduler(
            store,
            eventStore,
            silentChannel,
            {
                ServerConfig.defaults with
                    ProcessProfile = WorkerOnly
            },
            logger,
            NoOpActivitySink() :> IActivitySink,
            distributedLock = lck
        )

    (scheduler :> IJobScheduler).RegisterHandler("election-handler", handler)

    {
        Store = store
        Lock = lck
        Logger = logger
        Scheduler = scheduler
    }

let private freshRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-cron-election-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    root

/// Register one every-minute cron job through `replica` and make it
/// OVERDUE by two minutes, so a tick at `DateTime.UtcNow` finds it due
/// and a run advances `NextRunAt` to the next minute boundary — strictly
/// past that tick.
let private scheduleOverdueCron (replica: Replica) : string * JobId =
    let scope = "election-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    let registration: JobRegistration = {
        ScopeId = scope
        Handler = "election-handler"
        Payload = "{}"
        Trigger = CronTrigger "* * * * *"
        Idempotency = None
        RetryPolicy = {
            JobRetryPolicy.defaults with
                MaxAttempts = 1
                InitialBackoff = TimeSpan.Zero
                MaxBackoff = TimeSpan.Zero
        }
        ShardKey = None
        Precision = Minute
        CreatedBy = "test"
        Tags = Map.empty
    }

    let jobId =
        match
            (replica.Scheduler :> IJobScheduler).Schedule registration
            |> Async.RunSynchronously
        with
        | Ok id -> id
        | Error e -> failtestf "schedule failed: %A" e

    let definition =
        replica.Store.Get(scope, jobId)
        |> Async.RunSynchronously
        |> Option.defaultWith (fun () -> failtest "scheduled job not readable")

    replica.Store.Update {
        definition with
            NextRunAt = Some(DateTime.UtcNow.AddMinutes -2.0)
    }
    |> Async.RunSynchronously

    scope, jobId

let private runRows (replica: Replica) scope jobId =
    replica.Store.GetRecentRuns(scope, jobId, 20) |> Async.RunSynchronously

let private succeededRuns (replica: Replica) scope jobId =
    runRows replica scope jobId
    |> List.filter (fun r -> r.Status = Succeeded)
    |> List.length

let private tick (replica: Replica) (now: DateTime) =
    replica.Scheduler.RunTick now |> Async.RunSynchronously

let private deadline = TimeSpan.FromSeconds 20.0

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "CronTickElection (Phase 766)" [

        testCase "a single replica runs a due cron job once and a second tick at the same instant re-runs nothing"
        <| fun () ->
            let storage = LocalFileStorage.LocalFileStorage(freshRoot ()) :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let handler = ParkingHandler(Task.CompletedTask)

            let replica =
                replicaOver storage eventStore handler (ReplicaLock(InProcessDistributedLock.create (), None))

            let scope, jobId = scheduleOverdueCron replica
            let now = DateTime.UtcNow

            tick replica now

            awaitOrFail "the first tick's dispatch to reach the handler" deadline handler.FirstEntry
            |> Async.RunSynchronously

            let settled =
                waitFor deadline (fun () -> replica.Lock.Releases >= 1)
                |> Async.RunSynchronously

            Expect.isTrue settled "the dispatch should have released its lease"

            // The same tick instant again: the run advanced `NextRunAt`
            // to the next minute boundary, so nothing is due at `now`.
            tick replica now

            let advanced =
                replica.Store.Get(scope, jobId)
                |> Async.RunSynchronously
                |> Option.bind _.NextRunAt

            match advanced with
            | Some next -> Expect.isGreaterThan next now "the run should have advanced NextRunAt past the tick"
            | None -> failtest "an every-minute cron should always have a next occurrence"

            Expect.equal handler.Invocations 1 "one due job, one run"
            Expect.equal (succeededRuns replica scope jobId) 1 "one Succeeded run row"

        testCase
            "two replicas sharing one lease store: the contended loser skips the tick and the job runs exactly once"
        <| fun () ->
            // Arm 1 — skip condition 1. Replica A wins the lease and is
            // parked inside the handler; replica B's tick reads the same
            // due job and contends. Under election B returns at once.
            // Pre-766, B queued on `acquireBlocking`, won after A
            // released, re-read an `Active` job and ran it again.
            let storage = LocalFileStorage.LocalFileStorage(freshRoot ()) :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let shared = InProcessDistributedLock.create ()
            let lockA = ReplicaLock(shared, None)
            let lockB = ReplicaLock(shared, None)

            // A's run is held open until B has been refused the lease.
            let handler = ParkingHandler(lockB.Contended)
            let a = replicaOver storage eventStore handler lockA
            let b = replicaOver storage eventStore handler lockB

            let scope, jobId = scheduleOverdueCron a
            let now = DateTime.UtcNow

            tick a now

            awaitOrFail "replica A to hold the lease inside the handler" deadline handler.FirstEntry
            |> Async.RunSynchronously

            tick b now

            // B finishes by SKIPPING (the election) or by RUNNING after A
            // (the pre-766 queue). Either way it reaches a terminal
            // observable; assert on which one.
            let settled =
                waitFor deadline (fun () ->
                    lockA.Releases >= 1
                    && (b.Logger.Has "cron_tick_skipped_contended" || lockB.Releases >= 1))
                |> Async.RunSynchronously

            Expect.isTrue settled "both replicas should have finished with the tick"
            Expect.equal handler.Invocations 1 "the due job must run exactly once across both replicas"
            Expect.equal (succeededRuns a scope jobId) 1 "exactly one Succeeded run row in the shared store"
            Expect.isGreaterThanOrEqual lockB.Contentions 1 "B's acquire should have found A holding the lease"
            Expect.isTrue (b.Logger.Has "cron_tick_skipped_contended") "B should have recorded the contended skip"
            Expect.equal lockB.Releases 0 "B never held the lease, so it has nothing to release"

        testCase "two replicas sharing one lease store: a loser admitted after the winner released re-reads and skips"
        <| fun () ->
            // Arm 2 — skip condition 2. B's tick reads the due job while A
            // is still running it, but B's acquire is admitted only after
            // A has released — the window in which `TryAcquire` SUCCEEDS
            // for an occurrence that no longer exists. The in-lease
            // re-read must see `NextRunAt` advanced past the tick and
            // skip. Pre-766 (and under election alone) B ran the job.
            let storage = LocalFileStorage.LocalFileStorage(freshRoot ()) :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let shared = InProcessDistributedLock.create ()
            let lockA = ReplicaLock(shared, None)
            let lockB = ReplicaLock(shared, Some lockA.ReleasedOnce)

            // A's run is held open until B's tick has RETURNED — by which
            // point B has read the job as due and started its dispatch.
            let bTickReturned =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let handler = ParkingHandler(bTickReturned.Task)
            let a = replicaOver storage eventStore handler lockA
            let b = replicaOver storage eventStore handler lockB

            let scope, jobId = scheduleOverdueCron a
            let now = DateTime.UtcNow

            tick a now

            awaitOrFail "replica A to hold the lease inside the handler" deadline handler.FirstEntry
            |> Async.RunSynchronously

            tick b now
            bTickReturned.TrySetResult() |> ignore

            // B's acquire lands after A's release, so B always releases —
            // after skipping (766) or after running (pre-766).
            let settled =
                waitFor deadline (fun () -> lockA.Releases >= 1 && lockB.Releases >= 1)
                |> Async.RunSynchronously

            Expect.isTrue settled "both replicas should have released their lease"
            Expect.equal handler.Invocations 1 "the due job must run exactly once across both replicas"
            Expect.equal (succeededRuns a scope jobId) 1 "exactly one Succeeded run row in the shared store"
            Expect.equal lockB.Contentions 0 "B's acquire was placed after A's release, so it must not have contended"
            Expect.isTrue (b.Logger.Has "cron_tick_skipped_not_due") "B should have recorded the not-due skip"

        testCase "CONTROL — two replicas with their own lock tables double-run the job (the shape the election closes)"
        <| fun () ->
            // Identical to arm 1 except that each replica holds its own
            // lock table — the in-process default across a process
            // boundary. B's acquire succeeds, B's re-read finds the job
            // still due (A is parked, so nothing has advanced), and both
            // run. If this arm ever passes with one run, the overlap the
            // shared-lease arms rely on is not being placed.
            let storage = LocalFileStorage.LocalFileStorage(freshRoot ()) :> IBlobStorage
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let lockA = ReplicaLock(InProcessDistributedLock.create (), None)
            let lockB = ReplicaLock(InProcessDistributedLock.create (), None)

            // Each run is held open until the SECOND entry — the
            // double-run itself is what releases both.
            let holdUntilSecondEntry =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let handler = ParkingHandler(holdUntilSecondEntry.Task)
            let a = replicaOver storage eventStore handler lockA
            let b = replicaOver storage eventStore handler lockB

            let scope, jobId = scheduleOverdueCron a
            let now = DateTime.UtcNow

            tick a now

            awaitOrFail "replica A to be parked inside the handler" deadline handler.FirstEntry
            |> Async.RunSynchronously

            tick b now

            awaitOrFail "replica B to enter the handler beside A" deadline handler.SecondEntry
            |> Async.RunSynchronously

            holdUntilSecondEntry.TrySetResult() |> ignore

            let settled =
                waitFor deadline (fun () -> lockA.Releases >= 1 && lockB.Releases >= 1)
                |> Async.RunSynchronously

            Expect.isTrue settled "both replicas should have released their own lease"
            Expect.equal handler.Invocations 2 "without a shared lease the due job runs on both replicas"
            Expect.equal (succeededRuns a scope jobId) 2 "two Succeeded run rows — the double-run, observed"
    ]