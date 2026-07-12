module ToolUp.Platform.Tests.InProcess.CoherenceCheckTests

open System
open System.Collections.Generic
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.HealthChecks
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Phase 563 — fact-base coherence checking ────────────────────────
//
// Covers the four checklist items:
//   563.A — comparability derived from the registry (additive RollUp +
//           registered subject hierarchy), never per-fact.
//   563.B — the pure check + typed findings, each cause class classified.
//   563.C — standing execution: audit rows under `_facts`, an
//           INotificationChannel alert, an IHealthCheck degradation.
//   563.D — coherent base ⇒ no findings; findings never mutate facts;
//           deployments that don't opt in are byte-identical.

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private t0 = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)

/// A metric definition carrying a roll-up declaration.
let private metricDef (id: string) (rollup: RollUp option) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = "C0"
    Staleness = UntilSuperseded
    ProducingOperation = Some "rollup"
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = rollup
}

let private subjectDef (id: string) (levels: string list) : SubjectDefinition = {
    Id = id
    Name = id
    Levels = levels
    Calendar = None
}

// Registry: `revenue` is additive (tolerance 1); `margin` is non-additive;
// the `product` hierarchy (brand → sku) is registered.
let private registry: IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "sales"
            Definition = metricDef "revenue" (Some(Additive 1m))
        }
        {
            Module = "sales"
            Definition = metricDef "margin" (Some NonAdditive)
        }
    ] [
        {
            Module = "sales"
            Definition = subjectDef "product" [ "brand"; "sku" ]
        }
    ]

/// A hand-built current-head fact at `path` (under the `product` hierarchy)
/// with `value` and transaction time `asOf`, metric `metric` (default
/// `revenue`). FactId is cosmetic here — the pure check never reads it.
let private factAt (metric: string) (hierarchy: string) (path: string list) (value: FactValue) (asOf: DateTime) : Fact = {
    FactId = sprintf "%s/%s@%O" hierarchy (String.concat ">" path) value
    Subject = { Hierarchy = hierarchy; Path = path }
    Metric = MetricRef metric
    Value = value
    Period = q2
    AsOf = asOf
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "h" ]
        TriggerRef = None
    }
    Confidence = None
    Supersedes = None
    Disclosure = Disclosure.Surfaceable
}

/// The common shape: a `revenue` parent `acme` over two skus, same vintage.
let private revenue (path: string list) (value: FactValue) (asOf: DateTime) : Fact =
    factAt "revenue" "product" path value asOf

let private cfg = CoherenceConfig.defaults

// ─── Recording INotificationChannel ──────────────────────────────────

type private RecordingChannel() =
    let published = List<string * Notification>()
    member _.Published = published |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId: string, n: Notification) = async { published.Add((scopeId, n)) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }

// ─── Store helpers (for the run / mutation / health tests) ────────────

let private draftWith (inputHash: string) (path: string list) (value: FactValue) : FactDraft = {
    Subject = { Hierarchy = "product"; Path = path }
    Metric = MetricRef "revenue"
    Value = value
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

let private draft (path: string list) (value: FactValue) : FactDraft = draftWith "h" path value

let private assertOk (a: Async<Result<'a, string>>) : Async<'a> = async {
    let! r = a

    return
        match r with
        | Ok v -> v
        | Error e -> failtestf "expected Ok, got Error: %s" e
}

let private jobCtx (scopeId: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
    Attempt = 1
    Trigger = Trigger.Manual
    TriggerSource = ScheduledManually "system"
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = ""
    DeadLetterDestination = None
}

let tests =
    testList "Phase 563 — fact-base coherence checking" [

        // ─── 563.A / 563.D — comparability + coherent base ────────────

        test "coherent base ⇒ no findings" {
            let facts = [
                revenue [ "acme" ] (Scalar 100m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
            ]

            Expect.isEmpty (CoherenceCheck.check (Some registry) cfg facts) "parent equals the sum of its children"
        }

        test "a discrepancy within the declared tolerance is not flagged" {
            let facts = [
                revenue [ "acme" ] (Scalar 100.5m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
            ]

            Expect.isEmpty (CoherenceCheck.check (Some registry) cfg facts) "0.5 is within the tolerance of 1"
        }

        test "no registry ⇒ no findings (GP 13 zero-weight)" {
            let facts = [
                revenue [ "acme" ] (Scalar 999m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 1m) t0
            ]

            Expect.isEmpty (CoherenceCheck.check None cfg facts) "a registry-less deployment never checks coherence"
        }

        test "a non-additive metric is excluded from checking" {
            let facts = [
                factAt "margin" "product" [ "acme" ] (Scalar 0.9m) t0
                factAt "margin" "product" [ "acme"; "widget-x" ] (Scalar 0.4m) t0
                factAt "margin" "product" [ "acme"; "widget-y" ] (Scalar 0.4m) t0
            ]

            Expect.isEmpty
                (CoherenceCheck.check (Some registry) cfg facts)
                "a ratio metric declares NonAdditive — no parent = Σ children relationship to test"
        }

        test "an unregistered subject hierarchy is excluded" {
            let facts = [
                factAt "revenue" "unregistered" [ "acme" ] (Scalar 999m) t0
                factAt "revenue" "unregistered" [ "acme"; "widget-x" ] (Scalar 1m) t0
            ]

            Expect.isEmpty
                (CoherenceCheck.check (Some registry) cfg facts)
                "comparability requires a registered subject hierarchy"
        }

        test "a leaf with no children generates no finding" {
            let facts = [ revenue [ "acme"; "widget-x" ] (Scalar 60m) t0 ]

            Expect.isEmpty
                (CoherenceCheck.check (Some registry) cfg facts)
                "a childless subject has nothing to reconcile"
        }

        // ─── 563.B — each cause class detected ────────────────────────

        test "cause: PartialLoad — an explicitly Absent child" {
            let facts = [
                revenue [ "acme" ] (Scalar 100m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Absent "no data loaded") t0
            ]

            let findings = CoherenceCheck.check (Some registry) cfg facts
            Expect.hasLength findings 1 "one finding for the acme parent"
            Expect.equal findings.[0].Cause PartialLoad "an Absent child ⇒ partial load"
            Expect.equal findings.[0].Expected 60m "the absent child contributes nothing to the sum"
            Expect.equal findings.[0].Found 100m "the parent value is carried"
            Expect.equal findings.[0].ChildCount 2 "both children (incl. the Absent one) counted"
        }

        test "cause: PartialLoad — parent exceeds the children sum (no explicit Absent)" {
            let facts = [
                revenue [ "acme" ] (Scalar 130m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
            ]

            let findings = CoherenceCheck.check (Some registry) cfg facts
            Expect.hasLength findings 1 "one finding"
            Expect.equal findings.[0].Cause PartialLoad "parent > Σ children ⇒ children under-loaded"
            Expect.equal findings.[0].Discrepancy 30m "signed discrepancy = found - expected"
        }

        test "cause: UnitSlip — a power-of-ten ratio" {
            let facts = [
                revenue [ "acme" ] (Scalar 100000m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
            ]

            let findings = CoherenceCheck.check (Some registry) cfg facts
            Expect.hasLength findings 1 "one finding"
            Expect.equal findings.[0].Cause UnitSlip "a ×1000 ratio ⇒ unit slip"
        }

        test "cause: MixedVintage — a wide AsOf spread" {
            let facts = [
                revenue [ "acme" ] (Scalar 130m) (t0.AddDays 10.0)
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
            ]

            let findings = CoherenceCheck.check (Some registry) cfg facts
            Expect.hasLength findings 1 "one finding"

            Expect.equal
                findings.[0].Cause
                MixedVintage
                "a 10-day vintage spread ⇒ mixed vintage (beats the magnitude fingerprint)"
        }

        test "cause: Unclassified — parent below the sum, same vintage, no scale signal" {
            let facts = [
                revenue [ "acme" ] (Scalar 70m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
            ]

            let findings = CoherenceCheck.check (Some registry) cfg facts
            Expect.hasLength findings 1 "one finding"
            Expect.equal findings.[0].Cause Unclassified "a discrepancy that matches no specific fingerprint"
            Expect.equal findings.[0].Discrepancy -30m "found (70) - expected (100)"
        }

        test "multi-level: each parent checked against its own direct children" {
            // brand acme = 100 (correct); sku widget-x = 60 but its own
            // children (variants) sum to 50 → one finding at widget-x only.
            let facts = [
                revenue [ "acme" ] (Scalar 100m) t0
                revenue [ "acme"; "widget-x" ] (Scalar 60m) t0
                revenue [ "acme"; "widget-y" ] (Scalar 40m) t0
                revenue [ "acme"; "widget-x"; "red" ] (Scalar 30m) t0
                revenue [ "acme"; "widget-x"; "blue" ] (Scalar 20m) t0
            ]

            let findings = CoherenceCheck.check (Some registry) cfg facts
            Expect.hasLength findings 1 "only widget-x fails to reconcile with its variants"
            Expect.equal findings.[0].Subject.Path [ "acme"; "widget-x" ] "the finding is at the widget-x level"
            Expect.equal findings.[0].Expected 50m "sum of its two variants"
            Expect.equal findings.[0].Found 60m "widget-x asserted value"
        }

        // ─── 563.C — standing execution (audit + alert) ───────────────

        testCaseAsync "run emits an audit row under _facts + a single alert; returns the findings"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create blob events
            let channel = RecordingChannel()
            let scope = newScope ()

            let! _ = store.Assert(scope, draft [ "acme" ] (Scalar 130m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-x" ] (Scalar 60m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-y" ] (Scalar 40m)) |> assertOk

            let! findings =
                CoherenceCheck.run
                    store
                    (Some registry)
                    events
                    (channel :> INotificationChannel)
                    cfg
                    (fun () -> t0)
                    scope

            Expect.hasLength findings 1 "one finding surfaced"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let coherenceRows =
                rows |> List.filter (fun e -> e.EventType = CoherenceEvents.FindingType)

            Expect.hasLength coherenceRows 1 "one coherence audit row written under _facts"

            Expect.hasLength channel.Published 1 "exactly one alert published"

            match channel.Published.[0] with
            | scopeId, SystemMessage(SystemMessageLevel.Warning, _) ->
                Expect.equal scopeId scope "alert published to the fact's scope"
            | other -> failtestf "expected a Warning SystemMessage, got %A" other
        }

        testCaseAsync "a coherent base emits no audit row and no alert"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create blob events
            let channel = RecordingChannel()
            let scope = newScope ()

            let! _ = store.Assert(scope, draft [ "acme" ] (Scalar 100m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-x" ] (Scalar 60m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-y" ] (Scalar 40m)) |> assertOk

            let! findings =
                CoherenceCheck.run
                    store
                    (Some registry)
                    events
                    (channel :> INotificationChannel)
                    cfg
                    (fun () -> t0)
                    scope

            Expect.isEmpty findings "no findings on a coherent base"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let coherenceRows =
                rows |> List.filter (fun e -> e.EventType = CoherenceEvents.FindingType)

            Expect.isEmpty coherenceRows "no coherence audit rows"
            Expect.isEmpty channel.Published "no alert"
        }

        // ─── 563.D — findings never mutate facts ──────────────────────

        testCaseAsync "run never mutates a stored fact (GP 9 — alert, never auto-correct)"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create blob events
            let channel = RecordingChannel()
            let scope = newScope ()

            let! parent = store.Assert(scope, draft [ "acme" ] (Scalar 130m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-x" ] (Scalar 60m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-y" ] (Scalar 40m)) |> assertOk

            let! before = store.Query(scope, FactQuery.all)

            let! _ =
                CoherenceCheck.run
                    store
                    (Some registry)
                    events
                    (channel :> INotificationChannel)
                    cfg
                    (fun () -> t0)
                    scope

            let! after = store.Query(scope, FactQuery.all)
            Expect.equal (List.length after) (List.length before) "no fact added or removed"
            Expect.sequenceEqual after before "the current heads are byte-identical after the check"

            let! reloaded = store.Get(scope, parent.FactId)
            Expect.equal reloaded (Some parent) "the incoherent parent stands unchanged — correction is a human act"
        }

        // ─── 563.C — the scheduled-execution surface (IJobHandler) ─────

        testCaseAsync "the job handler runs the check for its JobContext scope and reports Success"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create blob events
            let channel = RecordingChannel()
            let scope = newScope ()

            let! _ = store.Assert(scope, draft [ "acme" ] (Scalar 130m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-x" ] (Scalar 60m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-y" ] (Scalar 40m)) |> assertOk

            let handler =
                CoherenceJobHandler.create
                    store
                    (Some registry)
                    (channel :> INotificationChannel)
                    events
                    cfg
                    (fun () -> t0)
                    noopLogger

            let! result = handler.Execute(jobCtx scope)
            Expect.equal result JobResult.Success "the scheduled sweep succeeds"
            Expect.hasLength channel.Published 1 "the handler published the coherence alert"
        }

        // ─── 563.C — IHealthCheck ─────────────────────────────────────

        testCaseAsync "health check: Healthy on a coherent base, Degraded on an inconsistency"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create blob events
            let scope = newScope ()

            let probe =
                CoherenceHealthCheck.create store (Some registry) { cfg with HealthScope = scope }

            let! _ = store.Assert(scope, draft [ "acme" ] (Scalar 100m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-x" ] (Scalar 60m)) |> assertOk
            let! _ = store.Assert(scope, draft [ "acme"; "widget-y" ] (Scalar 40m)) |> assertOk

            let! healthy = probe.Check()
            Expect.equal healthy Healthy "coherent base ⇒ Healthy"

            // Introduce a cross-level inconsistency: supersede the parent
            // with a wrong roll-up over a *new* input (a changed input hash
            // is what supersedes — re-asserting the same content address is
            // an idempotent no-op).
            let! _ = store.Assert(scope, draftWith "h2" [ "acme" ] (Scalar 130m)) |> assertOk

            let! degraded = probe.Check()

            match degraded with
            | Degraded _ -> ()
            | other -> failtestf "expected Degraded after the inconsistency, got %A" other
        }

        // ─── 563.D — deployments that don't opt in are byte-identical ──

        test "withCoherenceChecks over a NoFactStore app is a no-op (byte-identical)" {
            let app = {
                ServerApp.empty with
                    Config = {
                        ServerApp.empty.Config with
                            FactStore = NoFactStore
                    }
            }

            let after = FactsCompose.withCoherenceChecks (Trigger.CronTrigger "0 3 * * *") app

            Expect.isTrue
                (Object.ReferenceEquals(after, app))
                "a NoFactStore app is returned unchanged — nothing registered, byte-identical (GP 11 / GP 13)"
        }
    ]