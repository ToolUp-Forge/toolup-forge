module ToolUp.Platform.Tests.InProcess.ScheduledJobDeclarationTests

open System
open System.Collections.Concurrent
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tracing
open ToolUp.Platform.ComposeJobs

// ─── Phase 9b.B — ComposeJobs.registerScheduledJobDeclarations ────────
//
// Tests the compose-time module/app-declared scheduled-job registration
// helper introduced for Phase 9b.B. The helper is invoked once at the
// end of compose, after the `IJobScheduler` singleton is built; these
// tests bind it against a real `InProcessJobScheduler` + `BlobJobStore`
// rooted in a fresh temp directory per test, plus a logger spy that
// captures the `Warn` lines emitted under `NoJobScheduler` /
// `Schedule` errors.

/// `ILogger` test double that captures every level into thread-safe
/// queues. Mirrors the pattern in adjacent tests that need to assert on
/// warn / error output without colour-coded console noise.
type private CapturingLogger() =
    let warns = ConcurrentQueue<string>()
    let errors = ConcurrentQueue<string>()

    member _.Warns = warns |> Seq.toList
    member _.Errors = errors |> Seq.toList

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn msg = warns.Enqueue(msg)

        member _.Error(msg, _) = errors.Enqueue(msg)

/// Stub `INotificationChannel` — the scheduler constructor requires
/// one; `Schedule` does not publish through it.
let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

/// Trivial `IJobHandler` that records every `Execute` call. The
/// dispatch loop is not started in these tests (we assert on the
/// store-side `JobDefinition`, not on handler invocation — Phase 9b's
/// existing `IJobSchedulerContract` covers the dispatch path), but
/// having a real handler instance exercises `RegisterHandler`.
type private RecordingHandler() =
    let calls = ConcurrentQueue<JobContext>()
    member _.Calls = calls |> Seq.toList

    interface IJobHandler with
        member _.Execute(ctx) = async {
            calls.Enqueue(ctx)
            return JobResult.Success
        }

let private freshScheduler () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-sjd-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create storage eventStore

    let silentLogger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

    let scheduler =
        JobScheduler.create
            jobStore
            eventStore
            silentChannel
            ServerConfig.defaults
            silentLogger
            (NoOpActivitySink() :> IActivitySink)
        :> IJobScheduler

    scheduler

let tests =
    testList "ComposeJobs.registerScheduledJobDeclarations (Phase 9b.B)" [

        testCase "empty declarations under NoJobScheduler is a silent no-op"
        <| fun _ ->
            let logger = CapturingLogger()
            registerScheduledJobDeclarations None [] (logger :> ILogger)

            Expect.isEmpty logger.Warns "no warn emitted for empty declarations under NoJobScheduler"
            Expect.isEmpty logger.Errors "no error emitted"

        testCase "non-empty declarations under NoJobScheduler emit a single warn"
        <| fun _ ->
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let declarations = [
                ScheduledJobDeclaration.create "test.scan-a" handler (CronTrigger "0 8 * * *")
                ScheduledJobDeclaration.create "test.scan-b" handler (CronTrigger "0 9 * * *")
            ]

            registerScheduledJobDeclarations None declarations (logger :> ILogger)

            Expect.equal logger.Warns.Length 1 "exactly one warn for the whole declaration list"

            Expect.stringContains logger.Warns[0] "Phase 9b.B" "warn cites the phase that surfaces the contract"

            Expect.stringContains logger.Warns[0] "test.scan-a" "warn names the skipped handler(s)"
            Expect.stringContains logger.Warns[0] "test.scan-b" "warn names every skipped handler"
            Expect.stringContains logger.Warns[0] "NoJobScheduler" "warn diagnoses the root cause"

        testCase "single declaration registers handler and schedules under default _platform scope"
        <| fun _ ->
            let scheduler = freshScheduler ()
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let declarations = [
                ScheduledJobDeclaration.create "test.daily-rollup" handler (CronTrigger "0 6 * * *")
            ]

            registerScheduledJobDeclarations (Some scheduler) declarations (logger :> ILogger)

            Expect.isEmpty logger.Warns "no warn emitted on the happy path"

            let jobs = scheduler.ListJobs "_platform" |> Async.RunSynchronously
            Expect.equal jobs.Length 1 "exactly one job persisted under the default scope"

            let job = jobs |> List.head
            Expect.equal job.Handler "test.daily-rollup" "handler name preserved"
            Expect.equal job.ScopeId "_platform" "scope defaulted to _platform"
            Expect.equal job.Trigger (CronTrigger "0 6 * * *") "trigger preserved"
            Expect.equal job.Status JobStatus.Active "job is Active after Schedule"

            // The auto-built idempotency key carries module-{name}-{scope}
            // so a re-register on restart returns the same JobId.
            Expect.isTrue job.Idempotency.IsSome "auto-built idempotency key present"

            Expect.equal
                job.Idempotency.Value.Key
                "module-test.daily-rollup-_platform"
                "auto-built idempotency key namespaced by handler + scope"

        testCase "Scopes list fans out per scope with distinct idempotency keys"
        <| fun _ ->
            let scheduler = freshScheduler ()
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let declaration =
                ScheduledJobDeclaration.create "test.tenant-scan" handler (CronTrigger "*/15 * * * *")
                |> ScheduledJobDeclaration.withScopes [ "team-a"; "team-b" ]

            registerScheduledJobDeclarations (Some scheduler) [ declaration ] (logger :> ILogger)

            let jobsA = scheduler.ListJobs "team-a" |> Async.RunSynchronously
            let jobsB = scheduler.ListJobs "team-b" |> Async.RunSynchronously
            let jobsPlatform = scheduler.ListJobs "_platform" |> Async.RunSynchronously

            Expect.equal jobsA.Length 1 "team-a got its scheduled copy"
            Expect.equal jobsB.Length 1 "team-b got its scheduled copy"
            Expect.equal jobsPlatform.Length 0 "_platform default does NOT apply when Scopes is explicit"

            Expect.equal jobsA[0].Idempotency.Value.Key "module-test.tenant-scan-team-a" "per-scope key for team-a"

            Expect.equal jobsB[0].Idempotency.Value.Key "module-test.tenant-scan-team-b" "per-scope key for team-b"

        testCase "re-registering the same declaration is idempotent (no duplicate JobDefinition)"
        <| fun _ ->
            let scheduler = freshScheduler ()
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let declarations = [
                ScheduledJobDeclaration.create "test.recurrent" handler (CronTrigger "0 12 * * *")
            ]

            registerScheduledJobDeclarations (Some scheduler) declarations (logger :> ILogger)
            registerScheduledJobDeclarations (Some scheduler) declarations (logger :> ILogger)

            let jobs = scheduler.ListJobs "_platform" |> Async.RunSynchronously

            Expect.equal
                jobs.Length
                1
                "second registration returns the existing JobId via idempotency rather than persisting a new definition"

        testCase "explicit Idempotency override is preserved"
        <| fun _ ->
            let scheduler = freshScheduler ()
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let customKey: IdempotencyKey = {
                Key = "custom-key-shape"
                TtlSeconds = 3600
            }

            let declaration =
                ScheduledJobDeclaration.create "test.custom-idem" handler (CronTrigger "0 0 * * *")
                |> ScheduledJobDeclaration.withIdempotency customKey

            registerScheduledJobDeclarations (Some scheduler) [ declaration ] (logger :> ILogger)

            let jobs = scheduler.ListJobs "_platform" |> Async.RunSynchronously
            Expect.equal jobs.Length 1 "single job persisted"
            Expect.equal jobs[0].Idempotency.Value.Key "custom-key-shape" "operator-supplied key wins over auto-built"
            Expect.equal jobs[0].Idempotency.Value.TtlSeconds 3600 "operator-supplied TTL preserved"

        testCase "compose-time source tag is stamped on every scheduled job"
        <| fun _ ->
            let scheduler = freshScheduler ()
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let declarations = [
                ScheduledJobDeclaration.create "test.tagged" handler (CronTrigger "0 0 * * *")
            ]

            registerScheduledJobDeclarations (Some scheduler) declarations (logger :> ILogger)

            let jobs = scheduler.ListJobs "_platform" |> Async.RunSynchronously

            Expect.equal
                (jobs[0].Tags |> Map.tryFind "source")
                (Some "compose-time")
                "auto-stamped source tag distinguishes module-declared crons from admin-UI-scheduled ones"

        testCase "invalid cron expression logs warn but does not abort the loop"
        <| fun _ ->
            let scheduler = freshScheduler ()
            let logger = CapturingLogger()
            let handler = RecordingHandler() :> IJobHandler

            let declarations = [
                ScheduledJobDeclaration.create "test.valid" handler (CronTrigger "0 0 * * *")
                ScheduledJobDeclaration.create "test.invalid" handler (CronTrigger "not a cron")
                ScheduledJobDeclaration.create "test.also-valid" handler (CronTrigger "0 12 * * *")
            ]

            registerScheduledJobDeclarations (Some scheduler) declarations (logger :> ILogger)

            // The valid declarations land; the invalid one logs a warn
            // and the loop continues — a single misconfigured cron must
            // not take down the deployment.
            let jobs = scheduler.ListJobs "_platform" |> Async.RunSynchronously

            let scheduledHandlers = jobs |> List.map _.Handler |> Set.ofList

            Expect.isTrue (Set.contains "test.valid" scheduledHandlers) "valid before-invalid still scheduled"

            Expect.isTrue (Set.contains "test.also-valid" scheduledHandlers) "valid after-invalid still scheduled"

            Expect.equal logger.Warns.Length 1 "exactly one warn for the failed schedule"
            Expect.stringContains logger.Warns[0] "test.invalid" "warn names the failing handler"
    ]