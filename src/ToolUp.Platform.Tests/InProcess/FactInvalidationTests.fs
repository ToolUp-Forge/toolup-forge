module ToolUp.Platform.Tests.InProcess.FactInvalidationTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Phase 561 — reactive fact recomputation ─────────────────────────
//
// Covers the four checklist items:
//   561.A — RecomputePolicy as registry data (default Manual).
//   561.B — the invalidation walk: lineage → facts whose InputHashes cite
//           superseded inputs; derived InputsChanged (never a stored flag).
//   561.C — per-policy execution (Eager schedules, OnQuery defers, Manual
//           surfaces) + UntilUpstreamChange freshness from invalidation.
//   561.D — idempotent re-assert on unchanged output; no-policy byte-
//           identical; invalidation never mutates a stored fact.

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

/// Unwrap an `Async<Result<_, string>>` or fail the test with the error.
let private assertOk (a: Async<Result<'a, string>>) : Async<'a> = async {
    let! r = a

    return
        match r with
        | Ok v -> v
        | Error e -> failtestf "expected Ok, got Error: %s" e
}

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// A fresh blob-backed fact store + its lineage store (independent event
// stores — lineage is its own; the fact store audits to its own).
let private newStore () : IFactStore =
    BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore())

let private newLineage () : ILineageStore =
    LineageStore.EventStoreLineageStore(InMemoryEventStore.InMemoryEventStore() :> IEventStore) :> ILineageStore

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

/// A computed scalar draft over one input hash, at metric `metricId`.
let private draftFor (metricId: string) (inputHash: string) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metricId
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

let private lineageLink (fromId: string) (toId: string) : LineageLink = {
    LinkId = Guid.NewGuid()
    FromObjectId = fromId
    ToObjectId = toId
    ModuleName = "test"
    LinkType = Derived
    Timestamp = DateTime.UtcNow
}

/// A metric definition carrying a recompute policy + staleness.
let private metricDef (id: string) (staleness: StalenessPolicy) (policy: RecomputePolicy option) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = "C0"
    Staleness = staleness
    ProducingOperation = Some "rollup"
    CanonicalMethod = None
    RecomputePolicy = policy
    RollUp = None
    Context = None
}

let private registryWith (defs: MetricDefinition list) : IMetricRegistry =
    MetricRegistry.build (defs |> List.map (fun d -> { Module = "sales"; Definition = d })) []

// ─── Recording IJobScheduler (records Schedule + TriggerOnce) ─────────

type private RecordingScheduler() =
    let scheduled = ConcurrentQueue<JobRegistration>()
    let triggered = ConcurrentQueue<JobId>()

    member _.Scheduled = scheduled |> List.ofSeq
    member _.Triggered = triggered |> List.ofSeq

    interface IJobScheduler with
        member _.RegisterHandler(_, _) = ()
        member _.RegisterHandlerAsync(_, _) = async { return Ok() }

        member _.Schedule(reg: JobRegistration) = async {
            let id = Guid.NewGuid()
            scheduled.Enqueue reg
            return Ok id
        }

        member _.Cancel(_, _) = async { return () }
        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs _ = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, _) = async {
            triggered.Enqueue jobId
            return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

// A recomputer that returns whatever draft the supplied function yields.
let private recomputerReturning (f: Fact -> Result<FactDraft option, string>) : IFactRecomputer =
    { new IFactRecomputer with
        member _.Recompute(_scopeId, fact) = async { return f fact }
    }

let private jobCtx (scopeId: string) (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
    Attempt = 1
    Trigger = Trigger.Manual
    TriggerSource = ScheduledManually "system"
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

let tests =
    testList "Phase 561 — reactive fact recomputation" [

        // ─── 561.A — RecomputePolicy as registry data ─────────────────

        test "RecomputePolicy.resolve defaults None to Manual" {
            Expect.equal (RecomputePolicy.resolve None) Manual "undeclared ⇒ Manual"
            Expect.equal (RecomputePolicy.resolve (Some Eager)) Eager "declared policy passes through"
            Expect.equal (RecomputePolicy.resolve (Some OnQuery)) OnQuery "declared policy passes through"
        }

        testCaseAsync "policyFor reads the metric's declared policy; Manual for undeclared / unregistered"
        <| async {
            let store = newStore ()
            let scope = newScope ()

            let! eager = store.Assert(scope, draftFor "eager_metric" "v1" 100m) |> assertOk

            let! manual = store.Assert(scope, draftFor "undeclared_metric" "v1" 100m) |> assertOk

            let registry =
                registryWith [
                    metricDef "eager_metric" UntilSuperseded (Some Eager)
                    metricDef "undeclared_metric" UntilSuperseded None
                ]

            Expect.equal (FactInvalidation.policyFor (Some registry) eager) Eager "declared Eager read back"
            Expect.equal (FactInvalidation.policyFor (Some registry) manual) Manual "None-declared ⇒ Manual"
            Expect.equal (FactInvalidation.policyFor None eager) Manual "no registry ⇒ Manual for every fact"
        }

        // ─── 561.B — the invalidation walk ────────────────────────────

        testCaseAsync "invalidatedHeads flags a current head citing a changed input; leaves others alone"
        <| async {
            let store = newStore ()
            let lineage = newLineage ()
            let scope = newScope ()

            let! _ = store.Assert(scope, draftFor "revenue" "v1" 100m)
            let! _ = store.Assert(scope, draftFor "cost" "other" 50m)

            let! set = FactInvalidation.invalidationSet lineage scope [ "v1" ]
            let! invalidated = FactInvalidation.invalidatedHeads store scope set

            Expect.hasLength invalidated 1 "only the fact citing v1 is invalidated"
            Expect.equal invalidated.[0].Metric.Value "revenue" "the v1-citing revenue fact"
        }

        testCaseAsync "invalidation walk follows lineage descendants (derived-from is transitively invalidated)"
        <| async {
            let store = newStore ()
            let lineage = newLineage ()
            let scope = newScope ()

            // v2 was derived from v1 in lineage; a fact cites v2.
            do! lineage.Record(scope, lineageLink "v1" "v2") |> assertOk

            let! _ = store.Assert(scope, draftFor "revenue" "v2" 100m)

            let! set = FactInvalidation.invalidationSet lineage scope [ "v1" ]
            Expect.isTrue (Set.contains "v2" set) "v2 (descendant of changed v1) is in the invalidation set"

            let! invalidated = FactInvalidation.invalidatedHeads store scope set
            Expect.hasLength invalidated 1 "the v2-citing fact is invalidated via lineage"
        }

        testCaseAsync "invalidation never mutates a stored fact (law L1)"
        <| async {
            let store = newStore ()
            let lineage = newLineage ()
            let scope = newScope ()

            let! asserted = store.Assert(scope, draftFor "revenue" "v1" 100m) |> assertOk

            let! set = FactInvalidation.invalidationSet lineage scope [ "v1" ]
            let! _ = FactInvalidation.invalidatedHeads store scope set

            let! reloaded = store.Get(scope, asserted.FactId)
            Expect.equal reloaded (Some asserted) "the fact is byte-identical after invalidation — nothing was written"
        }

        // ─── 561.C — UntilUpstreamChange freshness from invalidation ───

        test "deriveFreshness: UntilUpstreamChange goes stale the moment its input changes" {
            let now = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)

            let fact =
                {
                    FactId = "f"
                    Subject = {
                        Hierarchy = "geography"
                        Path = [ "uk" ]
                    }
                    Metric = MetricRef "revenue"
                    Value = Scalar 100m
                    Period = q2
                    AsOf = now.AddDays -1.0
                    Method = Computed("rollup", "1", "p0")
                    Evidence = {
                        ResultRef = None
                        InputHashes = [ "v1" ]
                        TriggerRef = None
                    }
                    Confidence = None
                    Supersedes = None
                    Disclosure = Disclosure.Surfaceable
                }
                : Fact

            // Current + inputs changed ⇒ stale now (the real trigger).
            Expect.equal
                (FactInvalidation.deriveFreshness UntilUpstreamChange fact true true now)
                (Stale now)
                "current fact with changed inputs is stale from now"

            // Current + inputs unchanged ⇒ Stage-0 behaviour (fresh).
            Expect.equal
                (FactInvalidation.deriveFreshness UntilUpstreamChange fact true false now)
                Fresh
                "unchanged inputs ⇒ byte-for-byte the Stage-0 UntilSuperseded behaviour"

            // A different policy ignores the invalidation signal entirely.
            Expect.equal
                (FactInvalidation.deriveFreshness UntilSuperseded fact true true now)
                Fresh
                "UntilSuperseded is unaffected by the upstream-change signal"
        }

        // ─── 561.C — per-policy execution ─────────────────────────────

        testCaseAsync "Eager schedules + fires a recompute job; OnQuery defers; Manual surfaces"
        <| async {
            let store = newStore ()
            let lineage = newLineage ()
            let scheduler = RecordingScheduler()
            let scope = newScope ()

            let! _ = store.Assert(scope, draftFor "eager_m" "v1" 1m)
            let! _ = store.Assert(scope, draftFor "onquery_m" "v1" 2m)
            let! _ = store.Assert(scope, draftFor "manual_m" "v1" 3m)

            let registry =
                registryWith [
                    metricDef "eager_m" UntilUpstreamChange (Some Eager)
                    metricDef "onquery_m" UntilUpstreamChange (Some OnQuery)
                    metricDef "manual_m" UntilUpstreamChange (Some Manual)
                ]

            let! outcomes =
                RecomputeJobHandler.reactToDataChange lineage store (scheduler :> IJobScheduler) (Some registry) scope [
                    "v1"
                ]

            let enqueued =
                outcomes
                |> List.choose (function
                    | Enqueued(fid, _) -> Some fid
                    | _ -> None)

            let deferred =
                outcomes
                |> List.choose (function
                    | DeferredToQuery fid -> Some fid
                    | _ -> None)

            let surfaced =
                outcomes
                |> List.choose (function
                    | SurfacedOnly fid -> Some fid
                    | _ -> None)

            Expect.hasLength enqueued 1 "one Eager fact enqueued"
            Expect.hasLength deferred 1 "one OnQuery fact deferred"
            Expect.hasLength surfaced 1 "one Manual fact surfaced"
            Expect.hasLength scheduler.Scheduled 1 "exactly one recompute job scheduled (Eager only)"
            Expect.hasLength scheduler.Triggered 1 "the Eager job was fired (TriggerOnce)"

            let reg = scheduler.Scheduled.[0]
            Expect.equal reg.Handler RecomputeJobHandler.HandlerName "scheduled against the recompute handler"
            Expect.equal reg.ScopeId scope "scheduled in the fact's scope"
        }

        testCaseAsync "no registry ⇒ every invalidated fact is Manual (surfaced only); nothing scheduled"
        <| async {
            let store = newStore ()
            let lineage = newLineage ()
            let scheduler = RecordingScheduler()
            let scope = newScope ()

            let! _ = store.Assert(scope, draftFor "revenue" "v1" 1m)
            let! _ = store.Assert(scope, draftFor "cost" "v1" 2m)

            let! outcomes =
                RecomputeJobHandler.reactToDataChange lineage store (scheduler :> IJobScheduler) None scope [ "v1" ]

            Expect.hasLength outcomes 2 "both invalidated facts classified"

            Expect.isTrue
                (outcomes
                 |> List.forall (function
                     | SurfacedOnly _ -> true
                     | _ -> false))
                "every outcome is SurfacedOnly (Manual default)"

            Expect.isEmpty scheduler.Scheduled "no-policy deployment schedules nothing (GP 11 byte-identical)"
        }

        testCaseAsync "no invalidated facts ⇒ no outcomes, nothing scheduled (GP 13 zero-weight)"
        <| async {
            let store = newStore ()
            let lineage = newLineage ()
            let scheduler = RecordingScheduler()
            let scope = newScope ()

            let! _ = store.Assert(scope, draftFor "revenue" "v1" 1m)

            let registry = registryWith [ metricDef "revenue" UntilUpstreamChange (Some Eager) ]

            // A change to an unrelated object invalidates nothing.
            let! outcomes =
                RecomputeJobHandler.reactToDataChange lineage store (scheduler :> IJobScheduler) (Some registry) scope [
                    "unrelated"
                ]

            Expect.isEmpty outcomes "no fact cited the unrelated input"
            Expect.isEmpty scheduler.Scheduled "nothing scheduled"
        }

        // ─── 561.C / 561.D — the recompute job handler ────────────────

        testCaseAsync "handler re-asserts a changed recompute output → supersedes the stale head"
        <| async {
            let store = newStore ()
            let scope = newScope ()

            let! stale = store.Assert(scope, draftFor "revenue" "v1" 100m) |> assertOk

            // Recompute yields the same lineage (subject/metric/period/method)
            // over a NEW input version v2 with a new value → supersedes.
            let recomputer =
                recomputerReturning (fun _ -> Ok(Some(draftFor "revenue" "v2" 120m)))

            let handler = RecomputeJobHandler.create store recomputer noopLogger

            let! result = handler.Execute(jobCtx scope (RecomputeJobHandler.payloadFor stale.FactId))
            Expect.equal result JobResult.Success "recompute succeeds"

            let! heads = store.Query(scope, FactQuery.all)
            Expect.hasLength heads 1 "one current head after recompute"
            Expect.equal heads.[0].Value (Scalar 120m) "the head is the recomputed value"
            Expect.equal heads.[0].Supersedes (Some stale.FactId) "the new head supersedes the stale one (derived edge)"
        }

        testCaseAsync "handler idempotent on unchanged recompute output (content addressing)"
        <| async {
            let store = newStore ()
            let scope = newScope ()

            let! original = store.Assert(scope, draftFor "revenue" "v1" 100m) |> assertOk

            // Recompute produces the identical tuple → Assert is a no-op.
            let recomputer =
                recomputerReturning (fun _ -> Ok(Some(draftFor "revenue" "v1" 100m)))

            let handler = RecomputeJobHandler.create store recomputer noopLogger

            let! result = handler.Execute(jobCtx scope (RecomputeJobHandler.payloadFor original.FactId))
            Expect.equal result JobResult.Success "recompute succeeds"

            let! heads = store.Query(scope, FactQuery.all)
            Expect.hasLength heads 1 "still exactly one head — no churn"
            Expect.equal heads.[0].FactId original.FactId "same content-addressed id; no supersession written"
            Expect.equal heads.[0].Supersedes None "no supersession edge for an unchanged re-assert"
        }

        testCaseAsync "handler treats no-recompute-path (Ok None) as a success no-op"
        <| async {
            let store = newStore ()
            let scope = newScope ()

            let! original = store.Assert(scope, draftFor "revenue" "v1" 100m) |> assertOk

            // The default recomputer (no engine) — Ok None for every fact.
            let handler = RecomputeJobHandler.create store (NoFactRecomputer()) noopLogger

            let! result = handler.Execute(jobCtx scope (RecomputeJobHandler.payloadFor original.FactId))
            Expect.equal result JobResult.Success "no recompute path is a success no-op, not a failure"

            let! heads = store.Query(scope, FactQuery.all)
            Expect.hasLength heads 1 "the stale fact stands; nothing recomputed"
        }

        testCaseAsync "handler transient-fails when the recomputer errors"
        <| async {
            let store = newStore ()
            let scope = newScope ()

            let! original = store.Assert(scope, draftFor "revenue" "v1" 100m) |> assertOk

            let recomputer = recomputerReturning (fun _ -> Error "engine busy")
            let handler = RecomputeJobHandler.create store recomputer noopLogger

            let! result = handler.Execute(jobCtx scope (RecomputeJobHandler.payloadFor original.FactId))

            match result with
            | JobResult.TransientFailure _ -> ()
            | other -> failtestf "expected TransientFailure, got %A" other
        }

        testCaseAsync "handler no-ops (success) when the target fact is gone"
        <| async {
            let store = newStore ()
            let scope = newScope ()

            let handler = RecomputeJobHandler.create store (NoFactRecomputer()) noopLogger
            let! result = handler.Execute(jobCtx scope (RecomputeJobHandler.payloadFor "nonexistent-fact"))
            Expect.equal result JobResult.Success "a missing fact is a no-op success (retry won't help, not a failure)"
        }

        test "handler rejects a malformed payload as PermanentFailure" {
            let store = newStore ()
            let scope = newScope ()
            let handler = RecomputeJobHandler.create store (NoFactRecomputer()) noopLogger

            let result =
                handler.Execute(jobCtx scope "not json at all") |> Async.RunSynchronously

            match result with
            | JobResult.PermanentFailure _ -> ()
            | other -> failtestf "expected PermanentFailure, got %A" other
        }
    ]