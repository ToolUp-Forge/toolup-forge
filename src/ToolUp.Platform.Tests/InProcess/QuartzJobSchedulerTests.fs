module ToolUp.Platform.Tests.InProcess.QuartzJobSchedulerTests

open System
open System.IO
open System.Threading
open Expecto
open Quartz
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.JobSchedulers
open ToolUp.Platform.JobSchedulers.QuartzStore
open ToolUp.Platform.JobSchedulers.QuartzScheduler
open ToolUp.Platform.Tests.Contracts

// ─── Phase 9c.E — the Quartz companion binds BOTH contract packs ─────
//
// `IJobSchedulerContract` and `IJobStoreContract` are bound here
// UNMODIFIED against `ToolUp.JobSchedulers.Quartz`, which is the whole
// point of Phase 9c's "second companion attempted" gate: the packs were
// written while exactly one implementation existed, by the people who
// designed the interfaces, so they only prove portability once a second
// backend nobody shaped them around passes them as they stand.
//
// **Needs no external service.** Quartz's in-memory job store runs in
// this process, so unlike the Redis bindings there is no env gate and no
// `pending` arm — these cases run on every machine, in every lane, which
// is what makes the second binding a standing gate rather than an
// occasional one.
//
// Beyond the two packs, the cases at the bottom pin the things a
// contract pack structurally cannot see, because they are about what
// happens on the QUARTZ side of the seam: that a `Schedule` really did
// produce a Quartz job and trigger, that `Cancel` really did remove it,
// that a manual fire really does reach the registered handler and leave
// a run row behind, and that the `JobRetryPolicy` → `Quartz.RetryPolicy`
// map preserves the count and the backoff. Without those, every pack
// case could pass over a companion that persisted correctly and
// scheduled nothing.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

let private uniqueSuffix () =
    Guid.NewGuid().ToString("N").Substring(0, 8)

let private tempRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-quartz-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    root

/// A canonical store over `root`. A second call with the same root
/// reaches the same persisted definitions through a brand-new
/// `LocalFileStorage` — which is what the Phase 9c.F restart arms use
/// to simulate a process that died and came back.
let private jobStoreAt (root: string) =
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    JobStore.create storage eventStore

let private tempJobStore () = jobStoreAt (tempRoot ())

/// Phase 9c.F — every companion this file composes, so the teardown
/// case at the bottom of `tests` can shut them all down.
///
/// Spillover from Phase 9c.E: the two contract-pack factories compose
/// one companion per case (~35 over a run) and nothing ever shut one
/// down. An unstarted Quartz scheduler is not free — it registers in
/// Quartz's process-wide `SchedulerRepository` under its name and keeps
/// its scheduler thread parked — so the pack was leaking one per case
/// for the life of the test process. There is no per-case teardown hook
/// in Expecto to hang this on, so the bag is drained once at the end;
/// `tests` is `testSequenced` so nothing can still be using one when it
/// is drained.
let private composed =
    System.Collections.Concurrent.ConcurrentBag<QuartzJobScheduler>()

/// Compose the companion over a fresh temp-rooted `BlobJobStore`. Each
/// call gets its own filesystem subtree AND its own Quartz scheduler
/// instance (uniquely named — Quartz keys its registry on the name), so
/// cross-test isolation is structural on both sides of the seam.
///
/// `started = false` leaves the scheduler in the `Created` state: every
/// state operation the two packs exercise works there, and nothing
/// fires, which is what a pack asserting over `Schedule` / `Cancel` /
/// `Get` wants. The dispatch cases below start theirs explicitly.
let private composeOver (store: IJobStore) (started: bool) =
    let quartzConfig = {
        QuartzConfig.defaults with
            SchedulerName = "toolup-test-" + uniqueSuffix ()
            StartScheduler = started
    }

    let companion =
        QuartzJobScheduler.create store silentChannel ServerConfig.defaults quartzConfig silentLogger
        |> Async.RunSynchronously

    composed.Add companion
    companion

let private compose (started: bool) = composeOver (tempJobStore ()) started

/// Poll `predicate` until it holds or `timeoutMs` elapses. The timeout
/// is a FAILURE PATH, never a schedule: a green run never spends it, so
/// making it generous costs nothing. (Same rule, and the same reason, as
/// `JobSchedulerTests.waitFor` — dispatch is asynchronous, so an
/// assertion downstream of a fire waits on the production path's own
/// observable state rather than on a wall-clock guess.)
let private waitFor (timeoutMs: int) (predicate: unit -> bool) : bool =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable ok = predicate ()

    while not ok && DateTime.UtcNow < deadline do
        Thread.Sleep 25
        ok <- predicate ()

    ok

let private nullHandler =
    { new IJobHandler with
        member _.Execute(_) = async { return JobResult.Success }
    }

// ─── The two contract packs, bound unmodified ────────────────────────

let schedulerContractTests =
    let factory () =
        let scheduler = compose false :> IJobScheduler
        let suffix = uniqueSuffix ()
        scheduler, "team-a-" + suffix, "team-b-" + suffix

    IJobSchedulerContract.tests "QuartzJobScheduler" factory

let storeContractTests =
    let factory () =
        // The companion's own projecting store — the instance a
        // deployment registers as its `IJobStore` — not the blob store
        // underneath it. Binding the inner store would prove nothing
        // this repo did not already know.
        let store = (compose false).JobStore :> IJobStore
        let suffix = uniqueSuffix ()
        store, "team-a-" + suffix, "team-b-" + suffix

    IJobStoreContract.tests "QuartzJobStore" factory

// ─── Phase 9c.F — the restart arms, bound the second way ─────────────
//
// The Quartz side of a restart is the interesting one, and it is not
// the same question the in-process binding asks. Quartz's own store
// here is IN-MEMORY: a restart loses every job detail and trigger it
// held. What survives is the canonical `IJobStore` underneath, and the
// companion's answer is to re-project from it — so these two arms are
// where that design either holds or does not, over the same pack cases
// the durable-by-construction binding runs.

let storeRestartTests =
    let factory () =
        let root = tempRoot ()

        let binding: IJobStoreContract.ReopenableStore = {
            // The companion's own projecting store over a brand-new
            // canonical store rooted at the same place — not the inner
            // store directly, which would ask the question of
            // `BlobJobStore` a second time instead of asking it of the
            // companion.
            Open = fun () -> (composeOver (jobStoreAt root) false).JobStore :> IJobStore
            ScopeId = "team-restart-" + uniqueSuffix ()
        }

        binding

    IJobStoreContract.restartTests "QuartzJobStore" factory

let schedulerRestartTests =
    let factory () =
        let root = tempRoot ()

        let binding: IJobSchedulerContract.RestartableScheduler = {
            Open =
                fun () ->
                    let companion = composeOver (jobStoreAt root) true

                    (companion :> Microsoft.Extensions.Hosting.IHostedService).StartAsync CancellationToken.None
                    |> Async.AwaitTask
                    |> Async.RunSynchronously

                    companion :> IJobScheduler
            Close =
                fun scheduler ->
                    match box scheduler with
                    | :? Microsoft.Extensions.Hosting.IHostedService as host ->
                        host.StopAsync CancellationToken.None
                        |> Async.AwaitTask
                        |> Async.RunSynchronously
                    | _ -> ()
            ScopeId = "team-restart-" + uniqueSuffix ()
        }

        binding

    IJobSchedulerContract.restartTests "QuartzJobScheduler" factory

// ─── The Quartz side of the seam ─────────────────────────────────────

let projectionTests =
    testList "QuartzJobScheduler — Quartz-side projection" [

        testCaseAsync "Schedule projects a durable Quartz job and a cron trigger"
        <| async {
            let companion = compose false
            let scheduler = companion :> IJobScheduler
            let quartz = companion.QuartzScheduler
            let scope = "team-" + uniqueSuffix ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration: JobRegistration = {
                ScopeId = scope
                Handler = "h"
                Payload = "{}"
                Trigger = CronTrigger "0 9 * * *"
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                CreatedBy = "alice"
                Tags = Map.empty
            }

            let! result = scheduler.Schedule registration

            let jobId =
                match result with
                | Ok id -> id
                | Error e -> failtestf "Schedule: %A" e

            let! jobExists = quartz.Exists(QuartzMapping.jobKey scope jobId).AsTask() |> Async.AwaitTask
            Expect.isTrue jobExists "the Quartz job detail was created"

            let! trigger =
                quartz.GetTrigger(QuartzMapping.triggerKey scope jobId).AsTask()
                |> Async.AwaitTask

            Expect.isNotNull (box trigger) "the Quartz cron trigger was created"

            Expect.isTrue
                trigger.NextFireTimeUtc.HasValue
                "the projected trigger has a next fire time — the schedule is live on the Quartz side"
        }

        testCaseAsync "Cancel deletes the Quartz job; Disable keeps it and drops the trigger"
        <| async {
            let companion = compose false
            let scheduler = companion :> IJobScheduler
            let quartz = companion.QuartzScheduler
            let scope = "team-" + uniqueSuffix ()
            scheduler.RegisterHandler("h", nullHandler)

            let mk () : JobRegistration = {
                ScopeId = scope
                Handler = "h"
                Payload = "{}"
                Trigger = CronTrigger "0 9 * * *"
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                CreatedBy = "alice"
                Tags = Map.empty
            }

            let schedule () = async {
                match! scheduler.Schedule(mk ()) with
                | Ok id -> return id
                | Error e -> return failtestf "Schedule: %A" e
            }

            let! disabledId = schedule ()
            let! cancelledId = schedule ()

            do! scheduler.Disable(scope, disabledId)
            do! scheduler.Cancel(scope, cancelledId)

            let! disabledJobExists = quartz.Exists(QuartzMapping.jobKey scope disabledId).AsTask() |> Async.AwaitTask

            let! disabledTrigger =
                quartz.GetTrigger(QuartzMapping.triggerKey scope disabledId).AsTask()
                |> Async.AwaitTask

            Expect.isTrue disabledJobExists "a disabled job keeps its Quartz job detail — TriggerOnce can still fire it"

            Expect.isNull
                (box disabledTrigger)
                "a disabled job has no Quartz trigger — the schedule is what Disable stops"

            let! cancelledJobExists =
                quartz.Exists(QuartzMapping.jobKey scope cancelledId).AsTask()
                |> Async.AwaitTask

            Expect.isFalse cancelledJobExists "a cancelled job is removed from Quartz entirely"
        }

        testCaseAsync "Reproject rebuilds the Quartz view from the canonical store"
        <| async {
            let companion = compose false
            let scheduler = companion :> IJobScheduler
            let quartz = companion.QuartzScheduler
            let scope = "team-" + uniqueSuffix ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration: JobRegistration = {
                ScopeId = scope
                Handler = "h"
                Payload = "{}"
                Trigger = CronTrigger "*/5 * * * *"
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                CreatedBy = "alice"
                Tags = Map.empty
            }

            let! result = scheduler.Schedule registration

            let jobId =
                match result with
                | Ok id -> id
                | Error e -> failtestf "Schedule: %A" e

            // Lose the Quartz view, exactly as a restart of the
            // in-memory store would.
            let! _ = quartz.DeleteJob(QuartzMapping.jobKey scope jobId).AsTask() |> Async.AwaitTask
            let! goneExists = quartz.Exists(QuartzMapping.jobKey scope jobId).AsTask() |> Async.AwaitTask
            Expect.isFalse goneExists "precondition: the Quartz job is gone"

            let! projected = companion.JobStore.Reproject scope
            Expect.equal projected 1 "one definition reprojected"

            let! recoveredExists = quartz.Exists(QuartzMapping.jobKey scope jobId).AsTask() |> Async.AwaitTask
            Expect.isTrue recoveredExists "the Quartz job is back"
        }

        test "JobRetryPolicy maps onto Quartz's own retry policy, count and backoff preserved" {
            let policy = {
                MaxAttempts = 4
                InitialBackoff = TimeSpan.FromSeconds 30.0
                MaxBackoff = TimeSpan.FromMinutes 30.0
                DeadLetterDestination = None
            }

            match QuartzMapping.retryPolicy policy with
            | None -> failtest "a 4-attempt policy must map to a Quartz policy"
            | Some mapped ->
                // Forge counts DISPATCHES (4 attempts); Quartz counts
                // RETRIES after the first failure (3).
                Expect.equal mapped.MaxAttempts 3 "MaxAttempts - 1"

                // NOT `policy.InitialBackoff`: forge's first RETRY is
                // attempt 2, whose wait is already `InitialBackoff * 2`.
                Expect.equal
                    mapped.InitialDelay
                    (TimeSpan.FromSeconds 60.0)
                    "the first retry waits what forge's attempt 2 waits"

                Expect.equal mapped.BackoffFactor 2.0 "forge's doubling backoff"

                // The waits Quartz computes are the waits
                // `JobRetryPolicy.delayFor` documents, attempt for
                // attempt: retry N is the wait before dispatch N + 1.
                for retry in 1..3 do
                    Expect.equal
                        (mapped.DelayFor retry)
                        (JobRetryPolicy.delayFor policy (retry + 1))
                        $"retry {retry} waits what JobRetryPolicy.delayFor says"
        }

        test "a one-attempt policy maps to no Quartz retry policy" {
            let policy = {
                JobRetryPolicy.defaults with
                    MaxAttempts = 1
            }

            Expect.isNone (QuartzMapping.retryPolicy policy) "no retries asked for, none attached"
        }
    ]

let dispatchTests =
    testList "QuartzJobScheduler — dispatch" [

        testCaseAsync "a manual fire reaches the handler and records a Succeeded run"
        <| async {
            let companion = compose true
            let scheduler = companion :> IJobScheduler
            let host = companion :> Microsoft.Extensions.Hosting.IHostedService
            let scope = "team-" + uniqueSuffix ()
            let executed = System.Collections.Concurrent.ConcurrentQueue<JobContext>()

            let handler =
                { new IJobHandler with
                    member _.Execute ctx = async {
                        executed.Enqueue ctx
                        return JobResult.Success
                    }
                }

            scheduler.RegisterHandler("recording", handler)
            do! host.StartAsync CancellationToken.None |> Async.AwaitTask

            try
                let registration: JobRegistration = {
                    ScopeId = scope
                    Handler = "recording"
                    Payload = """{"x":1}"""
                    Trigger = Manual
                    Idempotency = None
                    RetryPolicy = JobRetryPolicy.defaults
                    ShardKey = None
                    Precision = Minute
                    CreatedBy = "alice"
                    Tags = Map.empty
                }

                let! result = scheduler.Schedule registration

                let jobId =
                    match result with
                    | Ok id -> id
                    | Error e -> failtestf "Schedule: %A" e

                match! scheduler.TriggerOnce(scope, jobId, "alice") with
                | Ok() -> ()
                | Error e -> failtestf "TriggerOnce: %s" e

                let ran = waitFor 15_000 (fun () -> not executed.IsEmpty)
                Expect.isTrue ran "the registered handler ran"

                let terminal () =
                    scheduler.GetRecentRuns(scope, jobId, 5)
                    |> Async.RunSynchronously
                    |> List.exists (fun run -> run.Status = Succeeded)

                Expect.isTrue (waitFor 15_000 terminal) "a Succeeded run row was recorded"

                let! definition = scheduler.Get(scope, jobId)

                match definition with
                | None -> failtest "the definition survives the run"
                | Some job ->
                    Expect.equal job.LastRunStatus (Some Succeeded) "the outcome folded back into the definition"
                    Expect.equal job.ConsecutiveFailures 0 "a success resets the failure counter"

                let ctx = executed |> Seq.head
                Expect.equal ctx.ScopeId scope "the handler ran in the job's scope"

                Expect.equal
                    ctx.Payload
                    """{"x":1}"""
                    "the payload was re-read from the definition, not from Quartz state"

                match ctx.TriggerSource with
                | ScheduledManually user -> Expect.equal user "alice" "the manual fire carried its user"
                | other -> failtestf "expected ScheduledManually, got %A" other
            finally
                host.StopAsync CancellationToken.None
                |> Async.AwaitTask
                |> Async.RunSynchronously
        }

        testCaseAsync "a permanent failure dead-letters without retrying"
        <| async {
            let companion = compose true
            let scheduler = companion :> IJobScheduler
            let host = companion :> Microsoft.Extensions.Hosting.IHostedService
            let scope = "team-" + uniqueSuffix ()
            let mutable attempts = 0

            let handler =
                { new IJobHandler with
                    member _.Execute _ = async {
                        Interlocked.Increment(&attempts) |> ignore
                        return JobResult.PermanentFailure "malformed payload"
                    }
                }

            scheduler.RegisterHandler("doomed", handler)
            do! host.StartAsync CancellationToken.None |> Async.AwaitTask

            try
                let registration: JobRegistration = {
                    ScopeId = scope
                    Handler = "doomed"
                    Payload = ""
                    Trigger = Manual
                    Idempotency = None
                    RetryPolicy = JobRetryPolicy.defaults
                    ShardKey = None
                    Precision = Minute
                    CreatedBy = "alice"
                    Tags = Map.empty
                }

                let! result = scheduler.Schedule registration

                let jobId =
                    match result with
                    | Ok id -> id
                    | Error e -> failtestf "Schedule: %A" e

                match! scheduler.TriggerOnce(scope, jobId, "alice") with
                | Ok() -> ()
                | Error e -> failtestf "TriggerOnce: %s" e

                let deadLettered () =
                    scheduler.GetRecentRuns(scope, jobId, 5)
                    |> Async.RunSynchronously
                    |> List.exists (fun run -> run.Status = DeadLettered)

                Expect.isTrue (waitFor 15_000 deadLettered) "a PermanentFailure dead-letters on the first attempt"
                Expect.equal attempts 1 "and is never retried — that is what makes it permanent"

                let! definition = scheduler.Get(scope, jobId)

                match definition with
                | None -> failtest "the definition survives the run"
                | Some job ->
                    Expect.equal job.LastRunStatus (Some DeadLettered) "the outcome folded back"
                    Expect.equal job.ConsecutiveFailures 1 "the failure counter advanced"
                    Expect.equal job.LastRunError (Some "malformed payload") "the reason was kept verbatim"
            finally
                host.StopAsync CancellationToken.None
                |> Async.AwaitTask
                |> Async.RunSynchronously
        }
    ]

let probeTests =
    testList "QuartzJobScheduler — health + preflight" [

        testCaseAsync "the health probe reports Degraded before start and Healthy after"
        <| async {
            let companion = compose true
            let probe = QuartzHealth.QuartzJobSchedulerHealth.create companion.QuartzScheduler
            let host = companion :> Microsoft.Extensions.Hosting.IHostedService

            let! beforeStart = probe.Check()

            match beforeStart with
            | HealthResult.Degraded _ -> ()
            | other -> failtestf "an unstarted scheduler is Degraded, not %A" other

            do! host.StartAsync CancellationToken.None |> Async.AwaitTask

            try
                let! afterStart = probe.Check()
                Expect.equal afterStart HealthResult.Healthy "a started scheduler is Healthy"
            finally
                host.StopAsync CancellationToken.None
                |> Async.AwaitTask
                |> Async.RunSynchronously

            let! afterStop = probe.Check()

            match afterStop with
            | HealthResult.Unhealthy _ -> ()
            | other -> failtestf "a shut-down scheduler is Unhealthy, not %A" other
        }

        testCaseAsync "the validator refuses a thread pool that cannot run a job"
        <| async {
            let companion = compose false

            let broken = {
                QuartzConfig.defaults with
                    MaxConcurrency = 0
            }

            let validator =
                QuartzValidator.create companion.QuartzScheduler broken ServerConfig.defaults

            match! validator.Validate() with
            | ConfigValidation.Error message -> Expect.stringContains message "MaxConcurrency" "names the setting"
            | other -> failtestf "expected Error, got %A" other
        }

        testCaseAsync "the validator refuses a multi-replica deployment over a non-persistent Quartz store"
        <| async {
            let companion = compose false

            let multiReplica = {
                ServerConfig.defaults with
                    ReplicaCount = 3
            }

            let validator =
                QuartzValidator.create companion.QuartzScheduler QuartzConfig.defaults multiReplica

            match! validator.Validate() with
            | ConfigValidation.Error message ->
                Expect.stringContains message "ReplicaCount" "names the count"
                Expect.stringContains message "AcceptInProcessSchedulerInMultiInstance" "names the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        testCaseAsync "the validator accepts a single-replica default composition"
        <| async {
            let companion = compose false

            let validator =
                QuartzValidator.create companion.QuartzScheduler QuartzConfig.defaults ServerConfig.defaults

            match! validator.Validate() with
            | ConfigValidation.Ok -> ()
            | other -> failtestf "expected Ok, got %A" other
        }
    ]

/// Phase 9c.F — the pack-level teardown the Phase 9c.E spillover asked
/// for. It is a test case rather than a silent `finally` so that the
/// shutdown is ASSERTED: a companion that refused to stop is a leak
/// this file is responsible for, and a leak nothing reports is the
/// state the pack was already in.
let private teardownTests =
    testList "QuartzJobScheduler — teardown" [
        testCaseAsync "every companion this file composed is shut down"
        <| async {
            let all = composed.ToArray()

            Expect.isNonEmpty
                all
                "nothing was composed, so this case proved nothing — the registry or the factories have drifted apart"

            for companion in all do
                do!
                    (companion :> Microsoft.Extensions.Hosting.IHostedService).StopAsync CancellationToken.None
                    |> Async.AwaitTask

            let mutable stillLive = 0

            for companion in all do
                let! status = companion.QuartzScheduler.GetStatus().AsTask() |> Async.AwaitTask

                if status <> SchedulerStatus.Shutdown then
                    stillLive <- stillLive + 1

            Expect.equal stillLive 0 $"%d{stillLive} of %d{all.Length} Quartz schedulers refused to shut down"
        }
    ]

// `testSequenced` is load-bearing, not decoration: the teardown case
// above shuts down every companion in the bag, so it must not overlap a
// case still using one. The pack's runner passes `Sequenced` by default
// (see `Program.fs`), but a run with `--parallel` would otherwise
// reintroduce exactly the race this file is meant to close.
let tests =
    testSequenced
    <| testList "QuartzJobScheduler — all" [
        schedulerContractTests
        storeContractTests
        storeRestartTests
        schedulerRestartTests
        projectionTests
        dispatchTests
        probeTests
        teardownTests
    ]