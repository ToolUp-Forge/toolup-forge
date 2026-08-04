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
        ExternalHandle = None
    }

    /// Phase 319 — an `ExternalHandle` with every field distinct, so a
    /// round-trip test can tell a genuinely-preserved handle from one
    /// the converter reconstructed with defaults or field-swapped.
    let mkHandle (scopeId: string) (nativeRef: string) : ExternalHandle = {
        HandleId = Guid.NewGuid()
        Backend = "contract-backend"
        ScopeId = scopeId
        NativeRef = nativeRef
        SubmittedAt = DateTime(2026, 8, 4, 11, 22, 33, DateTimeKind.Utc)
    }

    /// Phase 319 — a run parked in `AwaitingExternal`: open (no
    /// `CompletedAt`, no `DurationMs`) and carrying its handle, exactly
    /// the shape `JobResult.HandedOff` produces.
    let mkAwaitingRun (job: JobDefinition) attempt (handle: ExternalHandle) : JobRun = {
        mkRun job attempt AwaitingExternal with
            CompletedAt = None
            DurationMs = None
            ExternalHandle = Some handle
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

        // ─── Phase 319 — external hand-off persistence ───────────
        //
        // Two obligations an implementation takes on: round-trip the
        // `ExternalHandle` on a `JobRun`, and answer
        // `AwaitingExternalRuns` from the awaiting set only — including
        // taking runs OUT of it as they go terminal, which is the half
        // that fails silently (a store that only ever adds looks
        // perfectly correct until the scheduler re-polls handles for
        // work that finished days ago).

        testCaseAsync "Phase 319 — ExternalHandle round-trips through RecordRun + GetRecentRuns"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "handoff"
            do! store.Save job

            let handle = mkHandle scopeA "backend-token-42"
            do! store.RecordRun(mkAwaitingRun job 1 handle)

            let! recent = store.GetRecentRuns(scopeA, job.JobId, 10)
            Expect.equal recent.Length 1 "the awaiting run persisted"
            let run = recent.Head

            Expect.equal run.Status AwaitingExternal "status round-trip"

            match run.ExternalHandle with
            | None -> failtest "the ExternalHandle was lost on the round-trip"
            | Some h ->
                // Every field asserted individually: a converter that
                // reconstructs the record with defaults, or swaps two
                // same-typed fields, passes an `isSome` check.
                Expect.equal h.HandleId handle.HandleId "HandleId round-trip"
                Expect.equal h.Backend handle.Backend "Backend round-trip"
                Expect.equal h.ScopeId handle.ScopeId "ScopeId round-trip"
                Expect.equal h.NativeRef handle.NativeRef "NativeRef (the backend's opaque token) round-trip"
                Expect.equal h.SubmittedAt handle.SubmittedAt "SubmittedAt round-trip"

            // The control: an ordinary in-process run must round-trip
            // `None`, not an empty-ish handle. Without this the test
            // above passes against an implementation that fabricates a
            // handle for every run.
            let plain = mkRun job 2 Succeeded
            do! store.RecordRun plain
            let! recent2 = store.GetRecentRuns(scopeA, job.JobId, 10)

            let plainRead = recent2 |> List.find (fun r -> r.RunId = plain.RunId)

            Expect.isNone plainRead.ExternalHandle "an in-process run carries no handle"
        }

        testCaseAsync "Phase 319 — AwaitingExternalRuns returns empty when nothing is awaiting"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "no-handoff"
            do! store.Save job

            // Populate ordinary run history, so "empty" is a real answer
            // about the awaiting set rather than an answer about an
            // empty store.
            do! store.RecordRun(mkRun job 1 Succeeded)
            do! store.RecordRun(mkRun job 2 Failed)
            do! store.RecordRun(mkRun job 3 DeadLettered)

            let! awaiting = store.AwaitingExternalRuns(scopeA, 100)
            Expect.isEmpty awaiting "no awaiting runs → empty, despite three runs in history"
        }

        testCaseAsync "Phase 319 — AwaitingExternalRuns returns awaiting runs with handles"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "handoff"
            do! store.Save job

            let h1 = mkHandle scopeA "ref-1"
            let h2 = mkHandle scopeA "ref-2"
            let a1 = mkAwaitingRun job 1 h1
            let a2 = mkAwaitingRun job 2 h2

            // A terminal run of the same job must not appear.
            do! store.RecordRun(mkRun job 3 Succeeded)
            do! store.RecordRun a1
            do! store.RecordRun a2

            let! awaiting = store.AwaitingExternalRuns(scopeA, 100)

            let ids = awaiting |> List.map _.RunId |> Set.ofList
            Expect.equal ids (Set.ofList [ a1.RunId; a2.RunId ]) "exactly the two awaiting runs"

            Expect.isTrue
                (awaiting |> List.forall (fun r -> r.Status = AwaitingExternal))
                "every returned run is AwaitingExternal"

            let refs =
                awaiting
                |> List.choose (fun r -> r.ExternalHandle |> Option.map _.NativeRef)
                |> Set.ofList

            Expect.equal refs (Set.ofList [ "ref-1"; "ref-2" ]) "each returned run carries its own handle"
        }

        testCaseAsync "Phase 319 — a run that leaves AwaitingExternal leaves the awaiting set"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "handoff"
            do! store.Save job

            let handle = mkHandle scopeA "ref-terminal"
            let awaitingRun = mkAwaitingRun job 1 handle
            do! store.RecordRun awaitingRun

            let! before = store.AwaitingExternalRuns(scopeA, 100)
            Expect.equal (before |> List.map _.RunId) [ awaitingRun.RunId ] "awaiting before reconciliation"

            // Reconciliation's write: the SAME run (same RunId, same
            // StartedAt) going terminal. This is the assertion that
            // catches an implementation which only ever adds to its
            // index — the scheduler would otherwise re-poll a finished
            // handle on every tick for the life of the deployment.
            do!
                store.RecordRun {
                    awaitingRun with
                        Status = Succeeded
                        CompletedAt = Some DateTime.UtcNow
                }

            let! after = store.AwaitingExternalRuns(scopeA, 100)
            Expect.isEmpty after "the reconciled run is no longer awaiting"

            // ...and it is still readable as a run, with its handle
            // intact. Leaving the awaiting set must not mean losing the
            // record of which backend ran it.
            let! recent = store.GetRecentRuns(scopeA, job.JobId, 10)
            let reconciled = recent |> List.find (fun r -> r.RunId = awaitingRun.RunId)
            Expect.equal reconciled.Status Succeeded "the terminal status persisted"

            Expect.equal
                (reconciled.ExternalHandle |> Option.map _.NativeRef)
                (Some "ref-terminal")
                "the handle survives reconciliation"
        }

        testCaseAsync "Phase 319 — AwaitingExternalRuns honours the scope boundary"
        <| async {
            let store, scopeA, scopeB = factory ()
            let job = mkRegistration scopeA "handoff"
            do! store.Save job
            do! store.RecordRun(mkAwaitingRun job 1 (mkHandle scopeA "ref-a"))

            let! bAwaiting = store.AwaitingExternalRuns(scopeB, 100)
            Expect.isEmpty bAwaiting "scope B sees none of scope A's awaiting runs (GP 4)"

            let! aAwaiting = store.AwaitingExternalRuns(scopeA, 100)
            Expect.equal aAwaiting.Length 1 "scope A still sees its own"
        }

        testCaseAsync "Phase 319 — AwaitingExternalRuns caps the batch at limit"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "handoff"
            do! store.Save job

            for i in 1..5 do
                do! store.RecordRun(mkAwaitingRun job i (mkHandle scopeA $"ref-{i}"))

            let! capped = store.AwaitingExternalRuns(scopeA, 2)
            Expect.equal capped.Length 2 "limit respected"

            let! all = store.AwaitingExternalRuns(scopeA, 100)
            Expect.equal all.Length 5 "the cap bounds the batch, it does not drop runs"

            let! none = store.AwaitingExternalRuns(scopeA, 0)
            Expect.isEmpty none "a non-positive limit yields nothing rather than everything"
        }

        testCaseAsync "Phase 319 — a handle-less awaiting run is still returned, not filtered"
        <| async {
            let store, scopeA, _ = factory ()
            let job = mkRegistration scopeA "handoff"
            do! store.Save job

            // Malformed: AwaitingExternal with no handle. Unreachable via
            // `JobResult.HandedOff`, but the store must surface it so the
            // scheduler can fail it — filtering it here would leave the
            // run awaiting forever with nothing anywhere reporting why.
            let malformed = {
                mkRun job 1 AwaitingExternal with
                    CompletedAt = None
                    ExternalHandle = None
            }

            do! store.RecordRun malformed

            let! awaiting = store.AwaitingExternalRuns(scopeA, 100)
            Expect.equal (awaiting |> List.map _.RunId) [ malformed.RunId ] "the malformed run is visible"
            Expect.isNone awaiting.Head.ExternalHandle "and it is honestly reported as handle-less"
        }
    ]