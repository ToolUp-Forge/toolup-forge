module ToolUp.Platform.Tests.Contracts.IJobSchedulerContract

open System
open Expecto
open ToolUp.Platform

// ─── IJobScheduler contract pack ──────────────────────────────────
//
// Parametrised tests for any `IJobScheduler` implementation. Each test
// asks the factory for a fresh `(scheduler, scopeA, scopeB)` triple
// and a `registerHandler` callback so tests can pre-register the
// handler names they Schedule against. Scopes are GUID-suffixed so
// concurrent runs against a shared substrate (filesystem, future
// distributed companion) cannot interfere.
//
// Coverage targets the interface contract — Schedule validation
// chain, idempotency, status transitions, manual trigger, scope
// isolation. Dispatch-loop behaviour (cron tick → handler execution
// → retry / dead-letter) is exercised separately in the in-process
// binding's smoke tests where the BackgroundService can be driven
// against a fast clock — that's not a portable contract concern.

// Phase 9c.F added the two registration-side assertions Phase 9c deferred
// "until a second implementation exists" (`JobRetryPolicy`, `ShardKey`).
// The rule-4 RESTART arm lives in `restartTests` below rather than here,
// because it is the one portability rule that cannot be asked of a single
// live object: statelessness between invocations only means something
// across an invocation boundary the handler did not survive.

/// What `restartTests` needs that the plain `factory` cannot give it: a
/// scheduler that can be shut down and a second one opened over the same
/// durable store, which is how a pack simulates a process restart.
///
/// `Open` must return an instance ready to DISPATCH — an implementation
/// whose dispatch needs its `IHostedService` started starts it here, and
/// `Close` is where it stops it again. An implementation with no separate
/// start does nothing in either.
type RestartableScheduler = {
    /// Open a scheduler, ready to dispatch, over this binding's durable
    /// store. Called more than once; every call must reach the same
    /// persisted definitions and run history.
    Open: unit -> IJobScheduler
    /// Shut one down. This is the simulated process death: after it the
    /// instance is not used again.
    Close: IJobScheduler -> unit
    /// A scope id unique to this binding.
    ScopeId: string
}

let tests (name: string) (factory: unit -> IJobScheduler * string * string) =

    let mkRegistration scopeId handler trigger : JobRegistration = {
        ScopeId = scopeId
        Handler = handler
        Payload = """{}"""
        Trigger = trigger
        Idempotency = None
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = None
        Precision = Minute
        CreatedBy = "alice"
        Tags = Map.empty
    }

    /// A no-op handler the tests register so `Schedule` validation
    /// passes the "handler is registered" gate. The scheduler's
    /// dispatch loop is not exercised here — these tests only call
    /// the IJobScheduler interface methods directly.
    let nullHandler =
        { new IJobHandler with
            member _.Execute(_) = async { return JobResult.Success }
        }

    let okOrFail label result =
        match result with
        | Ok v -> v
        | Error err -> failtestf "%s: expected Ok, got %A" label err

    testList $"{name} — IJobScheduler contract" [

        // ─── Schedule validation chain ────────────────────────

        testCaseAsync "Schedule rejects InvalidCron"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration = mkRegistration scopeA "h" (CronTrigger "not a cron expression")

            match! scheduler.Schedule registration with
            | Error(InvalidCron(expr, _)) -> Expect.equal expr "not a cron expression" "round-trips the bad expression"
            | other -> failtestf "Expected InvalidCron, got %A" other
        }

        testCaseAsync "Schedule rejects HandlerNotRegistered"
        <| async {
            let scheduler, scopeA, _ = factory ()
            // Note: handler never registered

            let registration = mkRegistration scopeA "missing-handler" (CronTrigger "* * * * *")

            match! scheduler.Schedule registration with
            | Error(HandlerNotRegistered name) -> Expect.equal name "missing-handler" "names the missing handler"
            | other -> failtestf "Expected HandlerNotRegistered, got %A" other
        }

        testCaseAsync "Schedule rejects PrecisionUnsupported(Second)"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration = {
                mkRegistration scopeA "h" (CronTrigger "* * * * *") with
                    Precision = Second
            }

            match! scheduler.Schedule registration with
            | Error(PrecisionUnsupported(supplied, _)) ->
                Expect.equal supplied Second "echoes the unsupported precision"
            | other -> failtestf "Expected PrecisionUnsupported, got %A" other
        }

        testCaseAsync "Schedule succeeds with valid CronTrigger + registered handler + Minute precision"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration = mkRegistration scopeA "h" (CronTrigger "0 9 * * *")

            let jobId =
                okOrFail "Schedule" (scheduler.Schedule registration |> Async.RunSynchronously)

            Expect.notEqual jobId Guid.Empty "non-empty JobId returned"
        }

        testCaseAsync "Schedule succeeds with Manual trigger (no cron parsing required)"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration = mkRegistration scopeA "h" Manual

            let _ =
                okOrFail "Schedule (Manual)" (scheduler.Schedule registration |> Async.RunSynchronously)

            ()
        }

        // ─── Idempotency ──────────────────────────────────────

        testCaseAsync "Idempotency: re-Schedule same key inside TTL returns existing JobId"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration = {
                mkRegistration scopeA "h" Manual with
                    Idempotency =
                        Some {
                            Key = "daily-rollup"
                            TtlSeconds = 3600
                        }
            }

            let firstId =
                okOrFail "first Schedule" (scheduler.Schedule registration |> Async.RunSynchronously)

            let secondId =
                okOrFail "second Schedule" (scheduler.Schedule registration |> Async.RunSynchronously)

            Expect.equal secondId firstId "same key inside TTL returns same JobId"
        }

        testCaseAsync "Idempotency: different key gets a fresh JobId"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let r1 = {
                mkRegistration scopeA "h" Manual with
                    Idempotency = Some { Key = "k1"; TtlSeconds = 3600 }
            }

            let r2 = {
                r1 with
                    Idempotency = Some { Key = "k2"; TtlSeconds = 3600 }
            }

            let id1 = okOrFail "Schedule k1" (scheduler.Schedule r1 |> Async.RunSynchronously)
            let id2 = okOrFail "Schedule k2" (scheduler.Schedule r2 |> Async.RunSynchronously)

            Expect.notEqual id1 id2 "different keys → different JobIds"
        }

        testCaseAsync "Idempotency: scope-bounded — same key in different scopes are independent"
        <| async {
            let scheduler, scopeA, scopeB = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let registration = {
                mkRegistration scopeA "h" Manual with
                    Idempotency =
                        Some {
                            Key = "shared-key"
                            TtlSeconds = 3600
                        }
            }

            let aId =
                okOrFail "Schedule A" (scheduler.Schedule registration |> Async.RunSynchronously)

            let bId =
                okOrFail
                    "Schedule B"
                    (scheduler.Schedule { registration with ScopeId = scopeB }
                     |> Async.RunSynchronously)

            Expect.notEqual aId bId "same key in different scopes → different JobIds"
        }

        // ─── Status transitions ───────────────────────────────

        testCaseAsync "Cancel sets Status = Cancelled"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)
            let r = mkRegistration scopeA "h" Manual
            let jobId = okOrFail "Schedule" (scheduler.Schedule r |> Async.RunSynchronously)

            do! scheduler.Cancel(scopeA, jobId)

            match! scheduler.Get(scopeA, jobId) with
            | Some j -> Expect.equal j.Status Cancelled "status flipped to Cancelled"
            | None -> failtest "expected job to exist after Cancel"
        }

        testCaseAsync "Cancel is idempotent — second call is a no-op"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)
            let r = mkRegistration scopeA "h" Manual
            let jobId = okOrFail "Schedule" (scheduler.Schedule r |> Async.RunSynchronously)

            do! scheduler.Cancel(scopeA, jobId)
            do! scheduler.Cancel(scopeA, jobId) // second call must not throw
        }

        testCaseAsync "Disable then Enable restores Active"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)
            let r = mkRegistration scopeA "h" (CronTrigger "0 9 * * *")
            let jobId = okOrFail "Schedule" (scheduler.Schedule r |> Async.RunSynchronously)

            do! scheduler.Disable(scopeA, jobId)

            match! scheduler.Get(scopeA, jobId) with
            | Some j -> Expect.equal j.Status Disabled "Disable sets Disabled"
            | None -> failtest "expected job to exist after Disable"

            do! scheduler.Enable(scopeA, jobId)

            match! scheduler.Get(scopeA, jobId) with
            | Some j ->
                Expect.equal j.Status Active "Enable restores Active"
                Expect.isSome j.NextRunAt "Enable recomputes NextRunAt for cron triggers"
            | None -> failtest "expected job to exist after Enable"
        }

        // ─── TriggerOnce ─────────────────────────────────────

        testCaseAsync "TriggerOnce on unknown job returns Error"
        <| async {
            let scheduler, scopeA, _ = factory ()

            match! scheduler.TriggerOnce(scopeA, Guid.NewGuid(), "alice") with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error for unknown JobId"
        }

        testCaseAsync "TriggerOnce on cancelled job returns Error"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)
            let r = mkRegistration scopeA "h" Manual
            let jobId = okOrFail "Schedule" (scheduler.Schedule r |> Async.RunSynchronously)
            do! scheduler.Cancel(scopeA, jobId)

            match! scheduler.TriggerOnce(scopeA, jobId, "alice") with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error when triggering a cancelled job"
        }

        // ─── Read paths ──────────────────────────────────────

        testCaseAsync "Get of unknown job returns None"
        <| async {
            let scheduler, scopeA, _ = factory ()

            match! scheduler.Get(scopeA, Guid.NewGuid()) with
            | None -> ()
            | Some _ -> failtest "expected None for unknown JobId"
        }

        testCaseAsync "ListJobs returns every Schedule call's job"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            let r1 = mkRegistration scopeA "h" Manual
            let id1 = okOrFail "Schedule 1" (scheduler.Schedule r1 |> Async.RunSynchronously)
            let id2 = okOrFail "Schedule 2" (scheduler.Schedule r1 |> Async.RunSynchronously)

            let! jobs = scheduler.ListJobs scopeA
            let ids = jobs |> List.map _.JobId |> Set.ofList
            Expect.isTrue (ids.Contains id1) "first job listed"
            Expect.isTrue (ids.Contains id2) "second job listed"
        }

        testCaseAsync "ListJobs is scope-isolated"
        <| async {
            let scheduler, scopeA, scopeB = factory ()
            scheduler.RegisterHandler("h", nullHandler)
            let r = mkRegistration scopeA "h" Manual
            let _ = okOrFail "Schedule" (scheduler.Schedule r |> Async.RunSynchronously)

            let! bJobs = scheduler.ListJobs scopeB
            Expect.isEmpty bJobs "scope B sees no scope A jobs"
        }

        // ─── Phase 9c.F — the deferred registration-side assertions ──
        //
        // `mkRegistration` above has always written
        // `JobRetryPolicy.defaults` and `ShardKey = None` and asserted
        // nothing about either — Phase 9c deferred that until a second
        // implementation existed to disagree. One does now (Phase 9c.E).
        // Both fields are ones the SCHEDULER never reads on the paths
        // this pack exercises, which is exactly why an implementation
        // can drop them and pass everything above.

        testCaseAsync "Phase 9c.F — rule 3: the retry policy a caller registers is the one the scheduler reports"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            // Every field differs from `JobRetryPolicy.defaults`
            // (3 / 30s / 30min / None), so an implementation that
            // reconstructed the policy from defaults fails on all four
            // rather than passing by coincidence.
            let policy: JobRetryPolicy = {
                MaxAttempts = 7
                InitialBackoff = TimeSpan.FromSeconds 45.0
                MaxBackoff = TimeSpan.FromMinutes 17.0
                DeadLetterDestination = Some "dead-letters/contract-pack"
            }

            let registration = {
                mkRegistration scopeA "h" (CronTrigger "0 9 * * *") with
                    RetryPolicy = policy
            }

            let jobId =
                okOrFail "Schedule" (scheduler.Schedule registration |> Async.RunSynchronously)

            match! scheduler.Get(scopeA, jobId) with
            | Some job ->
                Expect.equal job.RetryPolicy.MaxAttempts policy.MaxAttempts "MaxAttempts"
                Expect.equal job.RetryPolicy.InitialBackoff policy.InitialBackoff "InitialBackoff"
                Expect.equal job.RetryPolicy.MaxBackoff policy.MaxBackoff "MaxBackoff"

                Expect.equal
                    job.RetryPolicy.DeadLetterDestination
                    policy.DeadLetterDestination
                    "DeadLetterDestination — the field a companion maps onto its own dead-letter target"
            | None -> failtest "expected the scheduled job"

            // Rule 3 is about retry expressed as DATA, so the record has
            // to survive the read path an admin UI uses too, not only
            // the single-job lookup.
            let! listed = scheduler.ListJobs scopeA

            match listed |> List.tryFind (fun j -> j.JobId = jobId) with
            | Some job -> Expect.equal job.RetryPolicy policy "ListJobs agrees with Get"
            | None -> failtest "expected the scheduled job in ListJobs"
        }

        testCaseAsync "Phase 9c.F — rule 5: ShardKey survives registration, and two jobs sharing one still share it"
        <| async {
            let scheduler, scopeA, _ = factory ()
            scheduler.RegisterHandler("h", nullHandler)

            // What rule 5 makes assertable HERE is the affinity INPUT,
            // not the routing outcome. Neither shipped implementation
            // shards — both run every job in one process — so "two
            // registrations with the same key run on the same worker
            // identity" is a claim about a node topology there is
            // nothing to observe, and no SDK interface exposes a worker
            // identity to observe it with. What a sharded implementation
            // WOULD partition on is the key arriving intact, by value
            // (rule 1), from the caller's registration through to every
            // read path — which an implementation can lose, and which is
            // therefore what is pinned.
            let shared = "tenant-ledger"

            let keyed key = {
                mkRegistration scopeA "h" Manual with
                    ShardKey = key
            }

            let aId =
                okOrFail "Schedule A" (scheduler.Schedule(keyed (Some shared)) |> Async.RunSynchronously)

            let bId =
                okOrFail "Schedule B" (scheduler.Schedule(keyed (Some shared)) |> Async.RunSynchronously)

            let otherId =
                okOrFail "Schedule other" (scheduler.Schedule(keyed (Some "tenant-reports")) |> Async.RunSynchronously)

            let unkeyedId =
                okOrFail "Schedule unkeyed" (scheduler.Schedule(keyed None) |> Async.RunSynchronously)

            let! listed = scheduler.ListJobs scopeA

            let keyOf jobId =
                match listed |> List.tryFind (fun j -> j.JobId = jobId) with
                | Some job -> job.ShardKey
                | None -> failtestf "job %A missing from ListJobs" jobId

            Expect.equal (keyOf aId) (Some shared) "the key the caller registered arrives verbatim"
            Expect.equal (keyOf bId) (keyOf aId) "two registrations under one key still share it"
            Expect.equal (keyOf otherId) (Some "tenant-reports") "a different key stays different"
            Expect.isNone (keyOf unkeyedId) "no key means no key — not the empty string, not a default"

            match! scheduler.Get(scopeA, aId) with
            | Some job -> Expect.equal job.ShardKey (Some shared) "Get agrees with ListJobs"
            | None -> failtest "expected the scheduled job"
        }
    ]

/// The rule-4 arm: handlers are stateless between invocations, asserted
/// across the boundary that makes the rule mean anything — a restart.
///
/// Separate from `tests` because it needs a `RestartableScheduler`
/// rather than a single instance, and because it is the one part of
/// this pack that DISPATCHES: rule 4 is about what a handler receives
/// when it runs, so a pack that never runs one cannot speak to it.
///
/// Every wait below is on the production path's own observable state
/// (the recorded context, the run row), never on a wall-clock guess. The
/// timeout is a failure path a green run never spends, which is why it
/// is generous.
let restartTests (name: string) (factory: unit -> RestartableScheduler) =

    /// A handler that keeps every context it was given — one instance
    /// per simulated process, so "did the SECOND process's handler see
    /// the first one's state" is a question about two different objects.
    let recorder () =
        let seen = System.Collections.Concurrent.ConcurrentQueue<JobContext>()

        let handler =
            { new IJobHandler with
                member _.Execute ctx = async {
                    seen.Enqueue ctx
                    return JobResult.Success
                }
            }

        seen, handler

    let waitFor (timeoutMs: int) (predicate: unit -> bool) : bool =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        let mutable ok = predicate ()

        while not ok && DateTime.UtcNow < deadline do
            System.Threading.Thread.Sleep 25
            ok <- predicate ()

        ok

    testList $"{name} — IJobScheduler restart statelessness (Phase 9c.F)" [

        testCaseAsync "rule 4: a fresh scheduler over the same store runs the job again, carrying nothing across"
        <| async {
            let binding = factory ()
            let scope = binding.ScopeId
            let payload = """{"tenant":"acme","window":"nightly"}"""

            let firstSeen, firstHandler = recorder ()
            let first = binding.Open()
            first.RegisterHandler("restartable", firstHandler)

            let registration: JobRegistration = {
                ScopeId = scope
                Handler = "restartable"
                Payload = payload
                Trigger = Manual
                Idempotency = None
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                CreatedBy = "alice"
                Tags = Map.empty
            }

            let jobId =
                match first.Schedule registration |> Async.RunSynchronously with
                | Ok id -> id
                | Error e -> failtestf "Schedule: %A" e

            match! first.TriggerOnce(scope, jobId, "alice") with
            | Ok() -> ()
            | Error e -> failtestf "TriggerOnce (before restart): %s" e

            Expect.isTrue (waitFor 30_000 (fun () -> not firstSeen.IsEmpty)) "the handler ran before the restart"

            let succeededRuns (scheduler: IJobScheduler) () =
                scheduler.GetRecentRuns(scope, jobId, 10)
                |> Async.RunSynchronously
                |> List.filter (fun run -> run.Status = Succeeded)
                |> List.length

            Expect.isTrue
                (waitFor 30_000 (fun () -> succeededRuns first () >= 1))
                "and the first run reached a terminal row"

            // ─── the simulated process death ─────────────────────
            binding.Close first

            let secondSeen, secondHandler = recorder ()
            let second = binding.Open()

            // A DIFFERENT handler object, registered under the same
            // name. Anything the first one accumulated is unreachable
            // from here by construction — which is the point: if the
            // scheduler needed it, this is where it would fail.
            second.RegisterHandler("restartable", secondHandler)

            match! second.Get(scope, jobId) with
            | Some job ->
                Expect.equal job.Payload payload "the definition, and its payload, outlived the first process"
                Expect.equal job.LastRunStatus (Some Succeeded) "and so did the first run's outcome"
            | None -> failtest "the definition did not survive the restart"

            match! second.TriggerOnce(scope, jobId, "bob") with
            | Ok() -> ()
            | Error e -> failtestf "TriggerOnce (after restart): %s" e

            Expect.isTrue
                (waitFor 30_000 (fun () -> not secondSeen.IsEmpty))
                "the job ran once more under the fresh scheduler"

            Expect.equal
                firstSeen.Count
                1
                "and the pre-restart handler instance was never consulted again — no live handle survived"

            let ctx = secondSeen |> Seq.head

            Expect.equal ctx.ScopeId scope "the context carried the scope"

            Expect.equal
                ctx.Payload
                payload
                "the payload arrived on the parameter list, re-read from the store rather than remembered"

            Expect.equal
                ctx.Attempt
                1
                "the attempt counter is the new dispatch's own — no attempt state was carried across the restart"

            match ctx.TriggerSource with
            | ScheduledManually user ->
                Expect.equal user "bob" "the trigger source is this dispatch's, not the previous one's"
            | other -> failtestf "expected ScheduledManually, got %A" other

            Expect.isTrue
                (waitFor 30_000 (fun () -> succeededRuns second () >= 2))
                "two runs in the history — the STORE is what carried state across the restart, not the handler"

            binding.Close second
        }
    ]