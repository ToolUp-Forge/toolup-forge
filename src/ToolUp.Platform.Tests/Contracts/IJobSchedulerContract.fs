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
    ]