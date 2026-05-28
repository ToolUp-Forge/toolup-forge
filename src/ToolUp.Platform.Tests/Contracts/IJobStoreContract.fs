module ToolUp.Platform.Tests.Contracts.IJobStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── IJobStore contract pack ─────────────────────────────────────
//
// Parametrised tests for any `IJobStore` implementation. Each test
// asks the factory for a fresh `(store, scopeA, scopeB)` triple
// where the two scopes are GUID-suffixed so concurrent test runs
// against the same shared substrate (e.g. local disk) cannot
// interfere.
//
// Coverage targets the interface contract — round-trip, scope
// isolation (GP 4), idempotency lookup, run-history newest-first
// ordering, due-job filtering. Cron-parser correctness is exercised
// separately in `CronExpressionTests` since that module is pure.

let tests (name: string) (factory: unit -> IJobStore * string * string) =

    let mkRegistration scopeId handlerName : JobDefinition = {
        JobId = Guid.NewGuid()
        ScopeId = scopeId
        Handler = handlerName
        Payload = """{"x":1}"""
        Trigger = CronTrigger "0 9 * * *"
        Idempotency = None
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = None
        Precision = Minute
        Status = Active
        CreatedAt = DateTime.UtcNow
        CreatedBy = "alice"
        NextRunAt = Some(DateTime(2026, 4, 29, 9, 0, 0))
        LastRunAt = None
        LastRunStatus = None
        LastRunError = None
        ConsecutiveFailures = 0
        Tags = Map.empty
    }

    let mkRun (job: JobDefinition) attempt status : JobRun = {
        RunId = Guid.NewGuid()
        JobId = job.JobId
        ScopeId = job.ScopeId
        Attempt = attempt
        StartedAt = DateTime.UtcNow
        CompletedAt = Some(DateTime.UtcNow)
        Status = status
        Error = None
        DurationMs = Some 100L
    }

    testList $"{name} — IJobStore contract" [

        testCaseAsync "Save then Get round-trips"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "test"
            do! store.Save job

            match! store.Get(scopeA, job.JobId) with
            | Some retrieved ->
                Expect.equal retrieved.JobId job.JobId "id round-trip"
                Expect.equal retrieved.Handler "test" "handler round-trip"
                Expect.equal retrieved.Status Active "status round-trip"
            | None -> failtest "Expected Some, got None"
        }

        testCaseAsync "Get of unknown id returns None"
        <| async {
            let store, scopeA, _ = factory ()

            match! store.Get(scopeA, Guid.NewGuid()) with
            | None -> ()
            | Some _ -> failtest "Expected None for unknown jobId"
        }

        testCaseAsync "ListJobs returns every saved job in scope"
        <| async {
            let store, scopeA, _ = factory ()
            let j1 = mkRegistration scopeA "a"
            let j2 = mkRegistration scopeA "b"
            do! store.Save j1
            do! store.Save j2

            let! jobs = store.ListJobs scopeA
            let ids = jobs |> List.map _.JobId |> Set.ofList
            Expect.equal ids (Set.ofList [ j1.JobId; j2.JobId ]) "both jobs returned"
        }

        testCaseAsync "Cross-scope isolation — scope A jobs invisible to scope B"
        <| async {
            let store, scopeA, scopeB = factory ()
            let job = mkRegistration scopeA "test"
            do! store.Save job

            let! bJobs = store.ListJobs scopeB
            Expect.isEmpty bJobs "scope B sees no scope A jobs"

            match! store.Get(scopeB, job.JobId) with
            | None -> ()
            | Some _ -> failtest "scope B should not see scope A's job by id"
        }

        testCaseAsync "Update overwrites existing record"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "test"
            do! store.Save job

            let updated = {
                job with
                    Status = Disabled
                    LastRunStatus = Some Succeeded
            }

            do! store.Update updated

            match! store.Get(scopeA, job.JobId) with
            | Some r ->
                Expect.equal r.Status Disabled "status updated"
                Expect.equal r.LastRunStatus (Some Succeeded) "lastRunStatus updated"
            | None -> failtest "expected Some after Update"
        }

        testCaseAsync "FindByIdempotencyKey returns existing live match"
        <| async {
            let store, scopeA, _ = factory ()

            let job = {
                mkRegistration scopeA "test" with
                    Idempotency =
                        Some {
                            Key = "daily-rollup"
                            TtlSeconds = 3600
                        }
            }

            do! store.Save job

            let! found = store.FindByIdempotencyKey(scopeA, "daily-rollup", 3600, DateTime.UtcNow)
            Expect.equal found (Some job.JobId) "live key match returns existing JobId"
        }

        testCaseAsync "FindByIdempotencyKey returns None outside TTL"
        <| async {
            let store, scopeA, _ = factory ()

            let job = {
                mkRegistration scopeA "test" with
                    CreatedAt = DateTime.UtcNow.AddSeconds(-100.0)
                    Idempotency = Some { Key = "expired"; TtlSeconds = 60 }
            }

            do! store.Save job

            let! found = store.FindByIdempotencyKey(scopeA, "expired", 60, DateTime.UtcNow)
            Expect.equal found None "expired key returns None — even though job exists"
        }

        testCaseAsync "FindByIdempotencyKey honours scope boundary"
        <| async {
            let store, scopeA, scopeB = factory ()

            let job = {
                mkRegistration scopeA "test" with
                    Idempotency = Some { Key = "shared"; TtlSeconds = 3600 }
            }

            do! store.Save job

            let! found = store.FindByIdempotencyKey(scopeB, "shared", 3600, DateTime.UtcNow)
            Expect.equal found None "scope B does not see scope A's idempotency key"
        }

        testCaseAsync "FindByIdempotencyKey returns None for a never-seen key"
        <| async {
            let store, scopeA, _ = factory ()

            // Populate the scope with N other definitions to make sure
            // the lookup doesn't accidentally degrade to a scan.
            for i in 1..5 do
                let job = {
                    mkRegistration scopeA $"other-{i}" with
                        Idempotency =
                            Some {
                                Key = $"other-{i}"
                                TtlSeconds = 3600
                            }
                }

                do! store.Save job

            let! found = store.FindByIdempotencyKey(scopeA, "never-seen", 3600, DateTime.UtcNow)
            Expect.equal found None "an unseen key returns None even when the scope has other entries"
        }

        testCaseAsync "DueJobs returns empty when no jobs are due"
        <| async {
            let store, scopeA, _ = factory ()
            let now = DateTime.UtcNow

            // All future or non-Active.
            for i in 1..3 do
                let job = {
                    mkRegistration scopeA $"future-{i}" with
                        NextRunAt = Some(now.AddMinutes(60.0))
                }

                do! store.Save job

            let! due = store.DueJobs(scopeA, now)
            Expect.isEmpty due "no jobs due → empty list"
        }

        testCaseAsync "RecordRun + GetRecentRuns round-trip newest-first"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "test"
            do! store.Save job

            let r1 = {
                mkRun job 1 Failed with
                    StartedAt = DateTime.UtcNow.AddMinutes(-5.0)
            }

            let r2 = {
                mkRun job 2 Failed with
                    StartedAt = DateTime.UtcNow.AddMinutes(-3.0)
            }

            let r3 = {
                mkRun job 3 Succeeded with
                    StartedAt = DateTime.UtcNow
            }

            do! store.RecordRun r1
            do! store.RecordRun r2
            do! store.RecordRun r3

            let! recent = store.GetRecentRuns(scopeA, job.JobId, 10)
            Expect.equal recent.Length 3 "three runs persisted"
            Expect.equal recent[0].Attempt 3 "newest first (attempt 3)"
            Expect.equal recent[2].Attempt 1 "oldest last (attempt 1)"
        }

        testCaseAsync "DueJobs filters Active + NextRunAt <= now"
        <| async {
            let store, scopeA, _ = factory ()
            let now = DateTime.UtcNow

            let dueActive = {
                mkRegistration scopeA "due" with
                    NextRunAt = Some(now.AddMinutes(-1.0))
            }

            let futureActive = {
                mkRegistration scopeA "future" with
                    NextRunAt = Some(now.AddMinutes(10.0))
            }

            let dueButDisabled = {
                mkRegistration scopeA "disabled" with
                    NextRunAt = Some(now.AddMinutes(-1.0))
                    Status = Disabled
            }

            do! store.Save dueActive
            do! store.Save futureActive
            do! store.Save dueButDisabled

            let! due = store.DueJobs(scopeA, now)
            let names = due |> List.map _.Handler |> Set.ofList
            Expect.equal names (Set.singleton "due") "only Active + due returned"
        }

        testCaseAsync "ListScopesWithJobs enumerates distinct scopes"
        <| async {
            let store, scopeA, scopeB = factory ()
            do! store.Save(mkRegistration scopeA "a")
            do! store.Save(mkRegistration scopeB "b")

            let! scopes = store.ListScopesWithJobs()
            let asSet = Set.ofList scopes
            Expect.isTrue (asSet.Contains scopeA) "scope A listed"
            Expect.isTrue (asSet.Contains scopeB) "scope B listed"
        }
    ]