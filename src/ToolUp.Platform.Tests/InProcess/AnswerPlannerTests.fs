// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AnswerPlannerTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 560 — the grounded answer planner ─────────────────────────
//
// Question → (subject, metric, period) triples → typed PlanStep
// resolution → the plan recorded into the answer's provenance chain
// ("EXPLAIN for answers"). Covered here: the compiler table (resolvable
// / partial / unrecognised — refusal over fabrication, never a
// similarity fallback), per-branch resolution (fresh ⇒ UseFact, stale ⇒
// RefreshFact, computable-miss ⇒ ComputeFact, no-path ⇒ RequestData,
// recorded-absence ⇒ RequestData), disclosure interplay (a denied fact
// plans as Refuse naming the policy, never UseFact), the plan-node
// round-trip through the Phase 524 chain walk, and the one-knob
// FactsCompose registration + GP 11/13 parity.

// ── Shared harness (mirrors FactQueryToolTests) ───────────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (metric: string) (inputHashes: string list) (value: FactValue) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef metric
    Value = value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = inputHashes
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// A real (Phase 519) registry: the `revenue` metric plus the two-level
/// `brand` subject hierarchy — the vocabulary the compiler-table tests
/// resolve against. `direction` is the Phase 706 axis: it is what
/// `best_first` resolves through, and `Neutral` is what makes it refuse.
let private registryDirected
    (direction: DirectionOfBetter)
    (staleness: StalenessPolicy)
    (producingOp: string option)
    : IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "TestModule"
            Definition = {
                Id = "revenue"
                Name = "Revenue"
                Unit = "GBP"
                Dimensionality = "currency"
                Direction = direction
                DisplayFormat = "N0"
                Staleness = staleness
                ProducingOperation = producingOp
                CanonicalMethod = None
                RecomputePolicy = None
                RollUp = None
                Context = None
            }
        }
    ] [
        {
            Module = "TestModule"
            Definition = {
                Id = "brand"
                Name = "Brand"
                Levels = [ "brand"; "sku" ]
                Calendar = None
            }
        }
    ]

let private registryWith (staleness: StalenessPolicy) (producingOp: string option) : IMetricRegistry =
    registryDirected HigherIsBetter staleness producingOp

let private candidate (hierarchy: string) (path: string list) (metric: string) : TripleCandidate = {
    SubjectHierarchy = hierarchy
    SubjectPath = path
    Metric = metric
    PeriodFrom = None
    PeriodTo = None
    PeriodLabel = None
}

/// A deterministic compiler emitting fixed candidates — the seam the
/// compiler-table tests drive without an LLM.
let private compilerOf (candidates: TripleCandidate list) : TripleCompiler = fun _ -> async { return Ok candidates }

/// A fully-wired planner over in-memory substrate. Returns the planner
/// plus the store / gate / event store it composes.
let private plannerWith
    (registry: IMetricRegistry option)
    (compiler: TripleCompiler)
    (clock: unit -> DateTime)
    : IAnswerPlanner * IFactStore * IEventStore =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.createWithClock (InMemoryBlobStorage()) events clock
    let gate = FactDisclosureGate.create store events

    AnswerPlanner.createWithClock store gate registry events compiler clock, store, events

let private utcNow () = DateTime.UtcNow

// ── Phase 706 harness: populations ────────────────────────────────

/// A draft for one named brand — several distinct subjects give a
/// population several *current* heads (one lineage each), which is what a
/// ranking needs.
let private brandDraft (brand: string) (value: FactValue) : FactDraft = {
    draft "revenue" [ "h1" ] value with
        Subject = {
            Hierarchy = "brand"
            Path = [ brand ]
        }
}

/// A deterministic compiler emitting fixed population triples — the
/// Phase 706 seam driven without an LLM.
let private populationCompilerOf (populations: PopulationTriple list) : QuestionCompiler =
    fun _ -> async {
        return
            Ok {
                Triples = []
                Populations = populations
            }
    }

let private plannerCompiling
    (registry: IMetricRegistry option)
    (compiler: QuestionCompiler)
    (clock: unit -> DateTime)
    : IAnswerPlanner * IFactStore * IEventStore =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    // The registry reaches the STORE here, not only the planner: a
    // population's ordering is resolved inside `QueryPopulation` against
    // the metric's declared direction-of-better, so a registry-less store
    // refuses every `best_first` however well the planner knows the
    // vocabulary. (Which is the point of the refusal — but it is not what
    // these cases are testing.)
    let store =
        BlobFactStore.createWithRegistryAndClock (InMemoryBlobStorage()) events registry clock

    let gate = FactDisclosureGate.create store events

    AnswerPlanner.createCompilingWithClock store gate registry events compiler clock, store, events

let private plan (planner: IAnswerPlanner) (scope: string) (question: string) : AnswerPlan =
    planner.Plan(scope, "user-1", question) |> Async.RunSynchronously

let private stepsOf (p: AnswerPlan) : PlanStep list = p.Steps |> List.map _.Step

/// The single `UseAggregate` a population plan resolved to.
let private aggregateOf (p: AnswerPlan) : AggregatePlan =
    match stepsOf p with
    | [ UseAggregate aggregate ] -> aggregate
    | other -> failtestf "expected one UseAggregate step, got %A" other

// ── The compiler table (560.B) ────────────────────────────────────

let compilerTests =
    testList "Phase 560 compiler table" [

        test "a resolvable triple against a fresh fact plans UseFact citing the real fact id" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let scope = newScope ()
            let fact = assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m))

            let result = plan planner scope "what was acme revenue in Q2?"

            Expect.equal (stepsOf result) [ UseFact fact.FactId ] "one UseFact step citing the stored fact"
            Expect.isNone result.Refusal "no plan-level refusal"
            Expect.equal result.Question "what was acme revenue in Q2?" "the plan records the question"
            Expect.equal (AnswerPlan.citedFactIds result) [ fact.FactId ] "the cited-fact projection"
        }

        test "a partial compilation resolves what it can and refuses the rest, naming the unrecognised id" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, _ =
                plannerWith
                    registry
                    (compilerOf [
                        candidate "brand" [ "acme" ] "revenue"
                        candidate "brand" [ "acme" ] "share_of_voice"
                    ])
                    utcNow

            let scope = newScope ()
            let fact = assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m))

            let result = plan planner scope "revenue and share of voice for acme?"

            Expect.equal
                (stepsOf result)
                [ UseFact fact.FactId; Refuse(UnrecognisedMetric "share_of_voice") ]
                "the resolvable triple resolves; the unknown metric refuses, typed"

            Expect.equal
                (AnswerPlan.refusals result)
                [ UnrecognisedMetric "share_of_voice" ]
                "the refusal projection carries the typed reason"

            Expect.stringContains
                (PlanRefusalReason.describe (UnrecognisedMetric "share_of_voice"))
                "share_of_voice"
                "the canonical wording names what was unrecognised (GP 9)"
        }

        test "an unrecognised subject hierarchy refuses, naming the hierarchy" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, _, _ =
                plannerWith registry (compilerOf [ candidate "geography" [ "uk" ] "revenue" ]) utcNow

            let result = plan planner (newScope ()) "uk revenue?"

            Expect.equal (stepsOf result) [ Refuse(UnrecognisedSubject "geography") ] "typed subject refusal"
        }

        test "a near-miss metric id never resolves by similarity — refusal over fabrication (GP 9)" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenu" ]) utcNow

            let scope = newScope ()
            // A real revenue fact exists — the typo must still refuse.
            assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m)) |> ignore

            let result = plan planner scope "acme revenu?"

            Expect.equal
                (stepsOf result)
                [ Refuse(UnrecognisedMetric "revenu") ]
                "the typo refuses naming 'revenu' — never silently resolved to 'revenue'"
        }

        test "a subject path deeper than the declared levels refuses, typed" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, _, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme"; "widget-x"; "extra" ] "revenue" ]) utcNow

            let result = plan planner (newScope ()) "?"

            Expect.equal
                (stepsOf result)
                [ Refuse(InvalidSubjectPath("brand", [ "acme"; "widget-x"; "extra" ])) ]
                "a three-segment path under a two-level hierarchy refuses"
        }

        test "a compiler failure is a plan-level typed refusal, never an exception" {
            let failing: TripleCompiler =
                fun _ -> async { return Error "structured extraction failed: model unavailable" }

            let planner, _, _ =
                plannerWith (Some(registryWith UntilSuperseded None)) failing utcNow

            let result = plan planner (newScope ()) "anything"

            Expect.isEmpty result.Steps "no steps"

            match result.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "model unavailable" "the refusal carries the failure detail"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }

        test "an empty extraction (nothing maps to the vocabulary) is a typed unanswerable-question refusal" {
            let planner, _, _ =
                plannerWith (Some(registryWith UntilSuperseded None)) (compilerOf []) utcNow

            let result = plan planner (newScope ()) "what is the meaning of life?"

            match result.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "no registered subject or metric" "the refusal names the gap"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }

        test "with no compiler composed, every question refuses naming the missing substrate" {
            let planner, _, _ =
                plannerWith (Some(registryWith UntilSuperseded None)) AnswerPlanner.noCompiler utcNow

            let result = plan planner (newScope ()) "anything"

            match result.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "no question compiler" "names the missing compiler, typed (GP 9)"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }

        test "the vocabulary prompt enumerates the registered metric and subject ids" {
            let prompt =
                AnswerPlanner.vocabularyPrompt (Some(registryWith UntilSuperseded None))

            Expect.stringContains prompt "revenue" "metric id in the prompt"
            Expect.stringContains prompt "brand" "subject id in the prompt"
            Expect.stringContains prompt "Never invent" "the refusal-over-fabrication instruction"
        }
    ]

// ── Per-branch resolution (560.C) ─────────────────────────────────

let resolutionTests =
    testList "Phase 560 resolution branches" [

        test "a stale head plans RefreshFact with the derived stale-since instant — typed, deferred" {
            let mutable nowRef = DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)
            let window = TimeSpan.FromHours 1.0
            let registry = Some(registryWith (FreshFor window) None)

            let planner, store, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) (fun () -> nowRef)

            let scope = newScope ()
            let fact = assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m))

            // Two hours later the one-hour window has elapsed.
            nowRef <- nowRef.AddHours 2.0

            let result = plan planner scope "acme revenue?"

            Expect.equal
                (stepsOf result)
                [ RefreshFact(fact.FactId, fact.AsOf + window) ]
                "stale ⇒ RefreshFact carrying the fact id + the derived stale-since"
        }

        test "a computable miss plans ComputeFact naming the registry's producing operation" {
            let registry = Some(registryWith UntilSuperseded (Some "rollup-op"))

            let planner, _, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let result = plan planner (newScope ()) "acme revenue?"

            Expect.equal (stepsOf result) [ ComputeFact "rollup-op" ] "miss + ProducingOperation ⇒ ComputeFact"
        }

        test "a miss with no computation path plans RequestData naming the metric" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, _, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let result = plan planner (newScope ()) "acme revenue?"

            match stepsOf result with
            | [ RequestData(metricId, detail) ] ->
                Expect.equal metricId "revenue" "names the metric"
                Expect.stringContains detail "no producing operation" "names why nothing can be computed"
            | other -> failtestf "expected one RequestData, got %A" other
        }

        test "a recorded absence fact plans RequestData with the recorded gap reason — never quoted as a value" {
            let registry = Some(registryWith UntilSuperseded (Some "rollup-op"))

            let planner, store, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let scope = newScope ()

            assertFact store scope (draft "revenue" [ "h1" ] (Absent "no data loaded for this period"))
            |> ignore

            let result = plan planner scope "acme revenue?"

            Expect.equal
                (stepsOf result)
                [ RequestData("revenue", "no data loaded for this period") ]
                "the queryable absence is re-surfaced as the acquisition step, not UseFact and not ComputeFact"
        }
    ]

// ── Disclosure interplay (560.C / 560.E) ──────────────────────────

let disclosureTests =
    testList "Phase 560 disclosure interplay" [

        testCaseAsync "an Internal-only head plans Refuse naming the policy — never UseFact — and the deny is audited"
        <| async {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, events =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let scope = newScope ()

            let internalFact =
                assertFact store scope {
                    draft "revenue" [ "h1" ] (Scalar 19999m) with
                        Disclosure = Internal
                }

            let! result = planner.Plan(scope, "user-1", "acme revenue?")

            Expect.equal
                (stepsOf result)
                [ Refuse(UndisclosableFact(internalFact.FactId, "Internal")) ]
                "the denied fact refuses, naming the classification"

            Expect.equal
                (PlanRefusalReason.describe (UndisclosableFact(internalFact.FactId, "Internal")))
                (FactDisclosureVerdict.refusalText "Internal")
                "the canonical Phase 525 refusal wording — never drifts between doors"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal (List.length denies) 1 "the gate audited the plan-time deny"
            Expect.stringContains denies.Head.Payload "Retrieval" "checked at the FactRetrieval surface"
        }

        test "a Restricted head refuses naming the restricting policy ref" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let scope = newScope ()

            let restricted =
                assertFact store scope {
                    draft "revenue" [ "h1" ] (Scalar 555m) with
                        Disclosure = Restricted "licence-x"
                }

            let result = plan planner scope "acme revenue?"

            Expect.equal
                (stepsOf result)
                [ Refuse(UndisclosableFact(restricted.FactId, "licence-x")) ]
                "the refusal names the policy, never the value"
        }

        test "competing Surfaceable and Internal heads: UseFact picks the disclosable one" {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, _ =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let scope = newScope ()
            let surfaceable = assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m))

            // A competing Internal fact (different method ⇒ both current).
            assertFact store scope {
                draft "revenue" [ "h2" ] (Scalar 19999m) with
                    Method = Computed("intermediate", "1", "p1")
                    Disclosure = Internal
            }
            |> ignore

            let result = plan planner scope "acme revenue?"

            Expect.equal
                (stepsOf result)
                [ UseFact surfaceable.FactId ]
                "the disclosable competitor is planned; the Internal one never surfaces"
        }
    ]

// ── Phase 706 — the population compiler table (706.B) ─────────────

let populationCompilerTests =
    testList "Phase 706 population compiler table" [

        test "a superlative question plans one UseAggregate citing the real top-k fact ids, best-first" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            let scope = newScope ()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))
            let beta = assertFact store scope (brandDraft "beta" (Scalar 200m))
            let gamma = assertFact store scope (brandDraft "gamma" (Scalar 100m))

            let result = plan planner scope "which brand has the highest revenue?"
            let aggregate = aggregateOf result

            Expect.equal aggregate.Direction HighestFirst "higher-is-better resolves best_first to HighestFirst"

            Expect.equal
                aggregate.Ranked
                [
                    { Rank = 1; FactId = acme.FactId }
                    { Rank = 2; FactId = beta.FactId }
                    { Rank = 3; FactId = gamma.FactId }
                ]
                "the ranking cites real fact ids at their true ranks"

            Expect.equal
                (AnswerPlan.citedFactIds result)
                [ acme.FactId; beta.FactId; gamma.FactId ]
                "every ranked member is a cited fact — the chain walk reaches the ranking"

            Expect.equal aggregate.Stats.SubjectCount 3 "the summary describes what was ranked over"
            Expect.equal aggregate.Stats.Maximum (Some 300m) "nothing withheld ⇒ the magnitude block rides"
            Expect.isFalse aggregate.ValueStatisticsWithheld "no suppression on a wholly disclosable population"
            Expect.isNone aggregate.FreshnessCaveat "an UntilSuperseded population of current heads is fresh"
        }

        test "a lower-is-better metric ranks best_first ASCENDING — a registry fact, never a model judgement" {
            let registry = Some(registryDirected LowerIsBetter UntilSuperseded None)

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            let scope = newScope ()
            assertFact store scope (brandDraft "acme" (Scalar 300m)) |> ignore
            let gamma = assertFact store scope (brandDraft "gamma" (Scalar 100m))

            let aggregate = aggregateOf (plan planner scope "which brand is best on revenue?")

            Expect.equal aggregate.Direction LowestFirst "lower-is-better resolves best_first to LowestFirst"

            Expect.equal
                (aggregate.Ranked |> List.map _.FactId |> List.head)
                gamma.FactId
                "the smallest value ranks first"
        }

        test "a top-k question applies the k and reports the truncation rather than implying it saw everything" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let triple = {
                PopulationTriple.create "brand" "revenue" with
                    TopK = 2
            }

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ triple ]) utcNow

            let scope = newScope ()
            assertFact store scope (brandDraft "acme" (Scalar 300m)) |> ignore
            assertFact store scope (brandDraft "beta" (Scalar 200m)) |> ignore
            assertFact store scope (brandDraft "gamma" (Scalar 100m)) |> ignore

            let aggregate = aggregateOf (plan planner scope "top 2 brands by revenue?")

            Expect.equal (List.length aggregate.Ranked) 2 "the ranking is bounded by k"
            Expect.equal aggregate.EffectiveTopK 2 "the k the store applied"
            Expect.isFalse aggregate.TopKCapped "2 is under the ceiling, so nothing was capped"
            Expect.isTrue aggregate.Truncated "comparable members exist below the ceiling — said, not implied"
            Expect.equal aggregate.Stats.ComparableCount 3 "the summary still describes the whole matched population"
        }

        test "a count-above question filters the population BEFORE the statistics" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let triple = {
                PopulationTriple.create "brand" "revenue" with
                    ValueAtLeast = Some 150m
            }

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ triple ]) utcNow

            let scope = newScope ()
            assertFact store scope (brandDraft "acme" (Scalar 300m)) |> ignore
            assertFact store scope (brandDraft "beta" (Scalar 200m)) |> ignore
            assertFact store scope (brandDraft "gamma" (Scalar 100m)) |> ignore

            let aggregate =
                aggregateOf (plan planner scope "how many brands reach at least 150?")

            Expect.equal aggregate.Stats.FactCount 2 "'how many' is answered by the filtered population's count"
            Expect.equal aggregate.Stats.Minimum (Some 200m) "the summary describes the population the query matched"
            Expect.equal (List.length aggregate.Ranked) 2 "and the ranking is the same filtered set"
        }

        test "an unrecognised ordering token refuses, naming the gap and the vocabulary — never a guessed sort order" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let triple = {
                PopulationTriple.create "brand" "revenue" with
                    Ordering = "sideways"
            }

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ triple ]) utcNow

            let scope = newScope ()
            assertFact store scope (brandDraft "acme" (Scalar 300m)) |> ignore

            let result = plan planner scope "rank the brands sideways?"

            Expect.equal (stepsOf result) [ Refuse(UnrecognisedOrdering "sideways") ] "typed ordering refusal (GP 9)"

            let described = PlanRefusalReason.describe (UnrecognisedOrdering "sideways")
            Expect.stringContains described "sideways" "names what was unrecognised"
            Expect.stringContains described "best_first" "and enumerates the accepted vocabulary"
        }

        test "a Neutral direction-of-better refuses best_first, carrying Phase 701's own remedy verbatim" {
            let registry = Some(registryDirected Neutral UntilSuperseded None)

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            let scope = newScope ()
            assertFact store scope (brandDraft "acme" (Scalar 300m)) |> ignore

            match stepsOf (plan planner scope "which brand is best on revenue?") with
            | [ Refuse(PopulationNotOrderable(metricId, detail)) ] ->
                Expect.equal metricId "revenue" "names the metric"
                Expect.stringContains detail "Neutral" "the store's refusal reaches the plan verbatim"
                Expect.stringContains detail "Ascending" "including its remedy"

                Expect.equal
                    (PlanRefusalReason.describe (PopulationNotOrderable(metricId, detail)))
                    detail
                    "no second wording of one refusal"
            | other -> failtestf "expected PopulationNotOrderable, got %A" other
        }

        test "an explicit descending ordering ranks a Neutral metric — the caller's own choice needs no registry" {
            let registry = Some(registryDirected Neutral UntilSuperseded None)

            let triple = {
                PopulationTriple.create "brand" "revenue" with
                    Ordering = "descending"
            }

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ triple ]) utcNow

            let scope = newScope ()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))
            assertFact store scope (brandDraft "gamma" (Scalar 100m)) |> ignore

            let aggregate = aggregateOf (plan planner scope "brands by revenue, largest first")

            Expect.equal aggregate.Direction HighestFirst "explicit descending resolves without the registry"
            Expect.equal (aggregate.Ranked |> List.map _.FactId |> List.head) acme.FactId "largest first"
        }

        test "an unregistered metric or hierarchy on a population refuses exactly as a point triple does" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let planner, _, _ =
                plannerCompiling
                    registry
                    (populationCompilerOf [
                        PopulationTriple.create "brand" "share_of_voice"
                        PopulationTriple.create "geography" "revenue"
                    ])
                    utcNow

            Expect.equal
                (stepsOf (plan planner (newScope ()) "?"))
                [
                    Refuse(UnrecognisedMetric "share_of_voice")
                    Refuse(UnrecognisedSubject "geography")
                ]
                "vocabulary validation is the same deterministic step on both forms"
        }

        test "a path prefix deeper than the declared levels refuses, typed" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let triple = {
                PopulationTriple.create "brand" "revenue" with
                    PathPrefix = [ "acme"; "widget-x"; "extra" ]
            }

            let planner, _, _ =
                plannerCompiling registry (populationCompilerOf [ triple ]) utcNow

            Expect.equal
                (stepsOf (plan planner (newScope ()) "?"))
                [ Refuse(InvalidSubjectPath("brand", [ "acme"; "widget-x"; "extra" ])) ]
                "a three-segment prefix under a two-level hierarchy refuses"
        }

        test "point and population triples compile side by side in one plan" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let compiler: QuestionCompiler =
                fun _ -> async {
                    return
                        Ok {
                            Triples = [ candidate "brand" [ "acme" ] "revenue" ]
                            Populations = [ PopulationTriple.create "brand" "revenue" ]
                        }
                }

            let planner, store, _ = plannerCompiling registry compiler utcNow
            let scope = newScope ()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))

            match stepsOf (plan planner scope "acme's revenue, and who is highest?") with
            | [ UseFact factId; UseAggregate aggregate ] ->
                Expect.equal factId acme.FactId "the point triple resolves as it always did"
                Expect.equal (aggregate.Ranked |> List.map _.FactId) [ acme.FactId ] "the population resolves beside it"
            | other -> failtestf "expected a point step then an aggregate step, got %A" other
        }

        test "a point-only TripleCompiler plans byte-for-byte as it did — the 706 seam is additive (GP 11)" {
            let registry = Some(registryWith UntilSuperseded None)
            let candidates = [ candidate "brand" [ "acme" ] "revenue" ]

            let viaTriples, storeA, _ = plannerWith registry (compilerOf candidates) utcNow

            let viaQuestion, storeB, _ =
                plannerCompiling registry (QuestionCompiler.ofTriples (compilerOf candidates)) utcNow

            let scope = newScope ()
            let a = assertFact storeA scope (draft "revenue" [ "h1" ] (Scalar 21800m))
            let b = assertFact storeB scope (draft "revenue" [ "h1" ] (Scalar 21800m))
            Expect.equal a.FactId b.FactId "harness sanity: content-addressed, so the same fact id both sides"

            Expect.equal
                (stepsOf (plan viaTriples scope "acme revenue?"))
                (stepsOf (plan viaQuestion scope "acme revenue?"))
                "the lifted compiler produces the identical step list"
        }
    ]

// ── Phase 706 — population resolution branches (706.C) ────────────

let populationResolutionTests =
    testList "Phase 706 population resolution branches" [

        test "a wholly stale population keeps its ranking and carries the refresh in the RefreshFact shape" {
            let mutable nowRef = DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)
            let window = TimeSpan.FromHours 1.0
            let registry = Some(registryDirected HigherIsBetter (FreshFor window) None)

            let planner, store, _ =
                plannerCompiling
                    registry
                    (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ])
                    (fun () -> nowRef)

            let scope = newScope ()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))
            let gamma = assertFact store scope (brandDraft "gamma" (Scalar 100m))

            nowRef <- nowRef.AddHours 2.0

            let aggregate =
                aggregateOf (plan planner scope "which brand has the highest revenue?")

            Expect.equal
                (List.length aggregate.Ranked)
                2
                "the ranking survives — a stale population is quotable with a caveat"

            Expect.equal
                aggregate.Refresh
                (Some {
                    FactId = acme.FactId
                    StaleSince = min (acme.AsOf + window) (gamma.AsOf + window)
                })
                "the deferred refresh names the top-ranked member and the earliest derived stale-since"

            match aggregate.FreshnessCaveat with
            | Some caveat -> Expect.stringContains caveat "every one of the 2" "the caveat names the whole ranking"
            | None -> failtest "a wholly stale ranking must carry a caveat"
        }

        test "a partially stale population resolves with a freshness caveat and no refresh" {
            let mutable nowRef = DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)
            let window = TimeSpan.FromHours 3.0
            let registry = Some(registryDirected HigherIsBetter (FreshFor window) None)

            let planner, store, _ =
                plannerCompiling
                    registry
                    (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ])
                    (fun () -> nowRef)

            let scope = newScope ()
            assertFact store scope (brandDraft "acme" (Scalar 300m)) |> ignore

            // Four hours on, the first fact is past its window; assert the
            // second here so it is still inside its own.
            nowRef <- nowRef.AddHours 4.0
            assertFact store scope (brandDraft "gamma" (Scalar 100m)) |> ignore

            let aggregate =
                aggregateOf (plan planner scope "which brand has the highest revenue?")

            Expect.isNone aggregate.Refresh "a partly fresh ranking plans no blanket refresh"

            match aggregate.FreshnessCaveat with
            | Some caveat -> Expect.stringContains caveat "1 of the 2" "the caveat counts what is stale"
            | None -> failtest "a partly stale ranking must carry a caveat"
        }

        test "an empty population with a producing operation plans ComputeFact — 560's miss rule, unchanged" {
            let registry =
                Some(registryDirected HigherIsBetter UntilSuperseded (Some "rollup-op"))

            let planner, _, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            Expect.equal
                (stepsOf (plan planner (newScope ()) "which brand has the highest revenue?"))
                [ ComputeFact "rollup-op" ]
                "nothing recorded anywhere in the hierarchy, but the metric is computable"
        }

        test "an empty population with no computation path plans RequestData naming the hierarchy" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let planner, _, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            match stepsOf (plan planner (newScope ()) "which brand has the highest revenue?") with
            | [ RequestData(metricId, detail) ] ->
                Expect.equal metricId "revenue" "names the metric"
                Expect.stringContains detail "brand" "and the hierarchy that has nothing in it"
            | other -> failtestf "expected one RequestData, got %A" other
        }

        test "a population of non-comparable values is an aggregate answer, not a gap" {
            let registry =
                Some(registryDirected HigherIsBetter UntilSuperseded (Some "rollup-op"))

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            let scope = newScope ()

            assertFact store scope (brandDraft "acme" (Absent "no data loaded for this period"))
            |> ignore

            assertFact store scope (brandDraft "gamma" (Categorical "unavailable"))
            |> ignore

            let aggregate =
                aggregateOf (plan planner scope "which brand has the highest revenue?")

            Expect.isEmpty aggregate.Ranked "nothing carries a magnitude, so nothing is ordered (GP 9)"
            Expect.equal aggregate.Stats.NonComparableCount 2 "but both are counted — the queryable gaps stay visible"
            Expect.equal aggregate.Stats.FactCount 2 "the population matched real facts, so it is not a miss"
        }
    ]

// ── Phase 706 — disclosure interplay at population scale (706.E) ──

let populationDisclosureTests =
    testList "Phase 706 population disclosure interplay" [

        testCaseAsync
            "a restricted member is a withheld COUNT, leaves its rank as a gap, and suppresses the magnitude block"
        <| async {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let planner, store, events =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            let scope = newScope ()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))

            assertFact store scope {
                brandDraft "beta" (Scalar 200m) with
                    Disclosure = Internal
            }
            |> ignore

            let gamma = assertFact store scope (brandDraft "gamma" (Scalar 100m))

            let! result = planner.Plan(scope, "user-1", "which brand has the highest revenue?")
            let aggregate = aggregateOf result

            Expect.equal
                aggregate.Ranked
                [ { Rank = 1; FactId = acme.FactId }; { Rank = 3; FactId = gamma.FactId } ]
                "true ranks kept — the withheld member leaves a gap, never promotes the one below it"

            Expect.equal aggregate.WithheldCount 1 "existence disclosed"
            Expect.equal aggregate.WithheldByPolicy [ "Internal", 1 ] "…as a count grouped by policy, never an id"

            Expect.isTrue aggregate.ValueStatisticsWithheld "the magnitude block is gated with the members"
            Expect.isNone aggregate.Stats.Minimum "a minimum IS some member's own value"
            Expect.isNone aggregate.Stats.Maximum "so is a maximum"
            Expect.isNone aggregate.Stats.Mean "and a mean narrows it further"

            Expect.equal aggregate.Stats.SubjectCount 3 "counts are existence-level and ride regardless"
            Expect.equal aggregate.Stats.Freshness.FreshCount 3 "so is the freshness histogram"

            Expect.equal
                (AnswerPlan.citedFactIds result)
                [ acme.FactId; gamma.FactId ]
                "the withheld member is cited nowhere — it was never in the ranking"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal (List.length denies) 1 "the gate audited the plan-time deny (GP 6)"
        }

        test "a wholly restricted population discloses counts and policies — never a fact id, never a value" {
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let planner, store, _ =
                plannerCompiling registry (populationCompilerOf [ PopulationTriple.create "brand" "revenue" ]) utcNow

            let scope = newScope ()

            for brand, value in [ "acme", 300m; "gamma", 100m ] do
                assertFact store scope {
                    brandDraft brand (Scalar value) with
                        Disclosure = Restricted "licence-x"
                }
                |> ignore

            let aggregate =
                aggregateOf (plan planner scope "which brand has the highest revenue?")

            Expect.isEmpty aggregate.Ranked "nothing is quotable"
            Expect.equal aggregate.WithheldByPolicy [ "licence-x", 2 ] "both withheld under the one policy"
            Expect.isNone aggregate.Stats.Maximum "and the magnitudes go with them"
            Expect.equal aggregate.Stats.SubjectCount 2 "the population's existence is not itself a secret"
            Expect.isNone aggregate.Refresh "an empty ranking plans no refresh"
        }

        test "the planner and the query_metric_population tool run the SAME disclosure fold" {
            // Not a re-derivation: the point is that both doors reduce to
            // `PopulationDisclosure.fold`, so one store cannot disclose
            // differently depending on which one the question came through.
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let scope = newScope ()

            let ranked =
                [ "acme", 300m; "beta", 200m; "gamma", 100m ]
                |> List.map (fun (brand, value) -> assertFact store scope (brandDraft brand (Scalar value)))

            let restricted = ranked[1].FactId

            let verdictFor (factId: string) =
                if factId = restricted then
                    FactNotDisclosable "licence-x"
                else
                    FactDisclosable

            let folded = PopulationDisclosure.fold verdictFor ranked

            Expect.equal (folded.Disclosable |> List.map fst) [ 1; 3 ] "true ranks, gap preserved"
            Expect.equal folded.WithheldByPolicy [ "licence-x", 1 ] "policy-grouped count"

            Expect.isTrue
                (PopulationDisclosure.valuesWithheld folded)
                "and the magnitude gate is one predicate, not two"

            let stats =
                PopulationDisclosure.disclosedStats folded {
                    PopulationStats.empty with
                        Minimum = Some 100m
                        Maximum = Some 300m
                        Mean = Some 200m
                        SubjectCount = 3
                }

            Expect.equal (stats.Minimum, stats.Maximum, stats.Mean) (None, None, None) "magnitudes suppressed"
            Expect.equal stats.SubjectCount 3 "counts untouched"
        }
    ]

// ── Plan-node round-trip through the chain walk (560.D) ───────────

let chainTests =
    testList "Phase 560 plan in the provenance chain" [

        testCaseAsync "a recorded plan round-trips through the Phase 524 chain walk as a typed plan node"
        <| async {
            let registry = Some(registryWith UntilSuperseded None)
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events
            let gate = FactDisclosureGate.create store events

            let planner =
                AnswerPlanner.create store gate registry events (compilerOf [ candidate "brand" [ "acme" ] "revenue" ])

            let scope = newScope ()

            // A fact evidenced by result res-1, derived from object obj-1,
            // with the lineage link recorded — the full 524 island set.
            let fact =
                assertFact store scope {
                    draft "revenue" [ "obj-1" ] (Scalar 21800m) with
                        Evidence = {
                            ResultRef = Some "res-1"
                            InputHashes = [ "obj-1" ]
                            TriggerRef = None
                        }
                }

            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let! _ =
                lineage.Record(
                    scope,
                    {
                        LinkId = Guid.NewGuid()
                        FromObjectId = "obj-1"
                        ToObjectId = "res-1"
                        ModuleName = "analytics"
                        LinkType = Derived
                        Timestamp = DateTime.UtcNow
                    }
                )

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            // Compile → record → walk.
            let! compiled = planner.Plan(scope, "user-1", "acme revenue in Q2?")
            Expect.equal (stepsOf compiled) [ UseFact fact.FactId ] "harness sanity: the plan cites the fact"

            do! planner.Record(scope, "msg-1", compiled)

            // The durable record rides the _facts audit trail.
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            Expect.isSome
                (rows |> List.tryFind (fun e -> e.EventType = PlanEvents.RecordedType))
                "an AnswerPlanRecorded event joined the fact audit trail"

            // The typed plan round-trips from the record.
            let! recorded = AnswerPlanProvenance.recordedFor events scope "msg-1"

            Expect.equal
                (recorded |> List.map (fun p -> p.PlanId, p.Steps))
                [ compiled.PlanId, compiled.Steps ]
                "the recorded plan deserialises back to the same typed steps"

            // The chain walk surfaces the plan node + edges + upstream.
            let! chain = AnswerPlanProvenance.chainForMessage graph events scope "msg-1" 5

            let planNode = chain.Nodes |> List.tryFind (fun n -> n.Id = compiled.PlanId)

            match planNode with
            | Some node ->
                Expect.equal node.Kind AnswerPlanNode "the plan is a typed AnswerPlanNode"
                Expect.stringContains node.Label "acme revenue in Q2?" "the node labels the question"
            | None -> failtest "the plan node did not surface in the chain"

            let hasEdge f t k =
                chain.Edges |> List.exists (fun e -> e.From = f && e.To = t && e.Kind = k)

            Expect.equal chain.Root "msg-1" "rooted at the answer message"
            Expect.isTrue (hasEdge "msg-1" compiled.PlanId PlannedBy) "message --PlannedBy--> plan"
            Expect.isTrue (hasEdge compiled.PlanId fact.FactId CitesFact) "plan --CitesFact--> fact"
            Expect.isTrue (hasEdge "msg-1" fact.FactId CitesFact) "message --CitesFact--> fact (524 shape kept)"

            Expect.isTrue
                (chain.Nodes |> List.exists (fun n -> n.Id = "obj-1"))
                "the walk still reaches the originating data object"
        }

        testCaseAsync "with no recorded plan the chain is exactly the graph's own message chain (GP 11)"
        <| async {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore
            let graph = ProvenanceGraph.create lineage
            let scope = newScope ()

            let! composed = AnswerPlanProvenance.chainForMessage graph events scope "msg-1" 5
            let! plain = graph.GetChainForMessage(scope, "msg-1", [], 5)

            Expect.equal composed plain "no plan node, no extra edge — byte-identical chain"
        }

        testCaseAsync
            "a recorded UseAggregate round-trips the population query + gated digest, and cites its ranking in the chain"
        <| async {
            // 706.D — "how was this ranking produced?" must be answerable
            // from the record alone: the triple that ran, the direction,
            // the ceiling, the ids cited, what was withheld, and the
            // summary. And NOT the member list.
            let registry = Some(registryDirected HigherIsBetter UntilSuperseded None)

            let triple = {
                PopulationTriple.create "brand" "revenue" with
                    TopK = 2
                    ValueAtLeast = Some 50m
            }

            let planner, store, events =
                plannerCompiling registry (populationCompilerOf [ triple ]) utcNow

            let scope = newScope ()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))
            let gamma = assertFact store scope (brandDraft "gamma" (Scalar 100m))

            let! compiled = planner.Plan(scope, "user-1", "which brand has the highest revenue?")
            do! planner.Record(scope, "msg-agg", compiled)

            let! recorded = AnswerPlanProvenance.recordedFor events scope "msg-agg"

            match recorded |> List.map _.Steps with
            | [ [ { Step = UseAggregate aggregate } ] ] ->
                Expect.equal aggregate.Population triple "the population triple that ran, recorded verbatim"
                Expect.equal aggregate.Direction HighestFirst "the resolved direction survives the round-trip"
                Expect.equal aggregate.EffectiveTopK 2 "so does the ceiling that applied"

                Expect.equal
                    aggregate.Ranked
                    [ { Rank = 1; FactId = acme.FactId }; { Rank = 2; FactId = gamma.FactId } ]
                    "and the cited ranking"

                Expect.equal aggregate.Stats.SubjectCount 2 "the stats digest deserialises"
                Expect.equal aggregate.Stats.Mean (Some 200m) "decimals included"

                Expect.equal
                    (aggregate.Stats.MethodMix |> List.map fst)
                    [ "computed:rollup:1:p0" ]
                    "and the method mix — a list of tuples, which is where a wire format usually gives up"
            | other -> failtestf "expected one recorded UseAggregate step, got %A" other

            // The chain walk cites every ranked member.
            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            let! chain = AnswerPlanProvenance.chainForMessage graph events scope "msg-agg" 5

            let hasEdge f t k =
                chain.Edges |> List.exists (fun e -> e.From = f && e.To = t && e.Kind = k)

            Expect.isTrue (hasEdge "msg-agg" compiled.PlanId PlannedBy) "message --PlannedBy--> plan"
            Expect.isTrue (hasEdge compiled.PlanId acme.FactId CitesFact) "plan --CitesFact--> the top-ranked fact"
            Expect.isTrue (hasEdge compiled.PlanId gamma.FactId CitesFact) "plan --CitesFact--> the second-ranked fact"

            Expect.isTrue
                (chain.Nodes
                 |> List.exists (fun n -> n.Id = compiled.PlanId && n.Kind = AnswerPlanNode))
                "the ranking-backed plan is a typed plan node, exactly as a point plan is"
        }

        testCaseAsync "a plan recorded in one scope is unreachable from another (GP 4)"
        <| async {
            let registry = Some(registryWith UntilSuperseded None)

            let planner, store, events =
                plannerWith registry (compilerOf [ candidate "brand" [ "acme" ] "revenue" ]) utcNow

            let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

            let graph =
                ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

            let scopeA = newScope ()
            let scopeB = newScope ()

            let! compiled = planner.Plan(scopeA, "user-1", "acme revenue?")
            do! planner.Record(scopeA, "msg-1", compiled)

            let! foreign = AnswerPlanProvenance.chainForMessage graph events scopeB "msg-1" 5

            Expect.isFalse (foreign.Nodes |> List.exists (fun n -> n.Kind = AnswerPlanNode)) "no cross-scope plan node"
        }
    ]

// ── Compose registration + the 67b compiler (560.B wiring) ────────

/// A stub 67b provider: `SendStructuredMessage` returns the canned
/// content and records the system prompt it was handed.
type private StubStructuredProvider(content: string) =
    member val LastSystemPrompt: string option = None with get, set

    interface IAIProvider with
        member _.Capabilities = AIProviderCapabilities.unknown

        member _.SendMessage(_, _, _, _, _) = async { return Error(MalformedResponse "SendMessage is not used") }

        member this.SendStructuredMessage(_, _, systemPrompt, _, _) = async {
            this.LastSystemPrompt <- systemPrompt

            return
                Ok {
                    Content = content
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

/// The composed fact tier the way a deployment builds it — the
/// `FactsCompose.withFactStore` knob over a substrate-seeded collection.
let private composedUnder
    (knob: FactStoreMode)
    (registry: IMetricRegistry option)
    (provider: IAIProvider option)
    : ServerApp * ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = knob
                }
        }
        |> FactsCompose.withFactStore

    let services = ServiceCollection()

    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    registry
    |> Option.iter (fun r -> services.AddSingleton<IMetricRegistry>(r) |> ignore)

    provider
    |> Option.iter (fun p -> services.AddSingleton<IAIProvider>(p) |> ignore)

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    app, services.BuildServiceProvider()

let composeTests =
    testList "Phase 560 compose registration" [

        test "EnabledFactStore registers IAnswerPlanner on the same knob as the store" {
            let _, sp = composedUnder EnabledFactStore None None

            Expect.isFalse (isNull (box (sp.GetService<IAnswerPlanner>()))) "the planner rides the one knob"
        }

        test "EnabledFactStore registers IProvenanceGraph, so 'show the working' is one resolve" {
            let _, sp = composedUnder EnabledFactStore None None

            Expect.isFalse
                (isNull (box (sp.GetService<IProvenanceGraph>())))
                "the graph rides the same knob — no consumer has to rebuild lineage + evidence by hand"
        }

        test "NoFactStore registers no IProvenanceGraph (GP 13)" {
            let _, sp = composedUnder NoFactStore None None

            Expect.isTrue
                (isNull (sp.GetService(typeof<IProvenanceGraph>)))
                "a deployment that never composed the fact tier pays nothing for the graph"
        }

        testCaseAsync "the composed IProvenanceGraph walks the composed fact store's evidence"
        <| async {
            // Assert on what only the EVIDENCE SOURCE can produce, not on
            // the root node. A `FactRef` walk always seeds its own root —
            // with `Disclosure = None` and no edges — so "a FactNode with
            // this id exists" passes even on a graph composed with no
            // fact source at all, and pins nothing. The annotated
            // disclosure and the `DerivedFrom` edge to the fact's input
            // hash come only from `IFactEvidenceSource.GetFact`, so they
            // are what actually distinguishes a correctly-wired
            // registration. (Confirmed by probe: the root-node form
            // passed with the source cut out; these two do not.)
            let _, sp = composedUnder EnabledFactStore None None
            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            let graph = sp.GetRequiredService<IProvenanceGraph>()

            let stored = assertFact store scope (draft "revenue" [ "hash-a" ] (Scalar 100m))

            let! chain = graph.GetChain(scope, FactRef stored.FactId, Upstream, 3)

            let factNode =
                chain.Nodes
                |> List.tryFind (fun n -> n.Id = stored.FactId && n.Kind = FactNode)

            match factNode with
            | None -> failtest "the composed graph did not resolve the fact at all"
            | Some n ->
                Expect.equal
                    n.Disclosure
                    (Some "Surfaceable")
                    "the node carries the disclosure class — which only the composed evidence source supplies"

            Expect.isTrue
                (chain.Edges
                 |> List.exists (fun e -> e.From = stored.FactId && e.To = "hash-a" && e.Kind = DerivedFrom))
                "the walk reaches the fact's input hash — the evidence source is wired to the composed store"
        }

        test "NoFactStore composes byte-identically: no planner registered, the app untouched (GP 11 / GP 13)" {
            let before = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = NoFactStore
                    }
            }

            let after = FactsCompose.withFactStore before

            Expect.isTrue (obj.ReferenceEquals(before, after)) "withFactStore returns the app itself unchanged"

            let services = ServiceCollection()

            match after.Extensions.ServiceConfig with
            | Some cfg -> cfg services |> ignore
            | None -> ()

            let sp = services.BuildServiceProvider()
            Expect.isTrue (isNull (sp.GetService(typeof<IAnswerPlanner>))) "no planner in a no-store composition"
        }

        testCaseAsync
            "with no IAIProvider composed the DI planner refuses questions with the typed missing-compiler reason"
        <| async {
            let _, sp =
                composedUnder EnabledFactStore (Some(registryWith UntilSuperseded None)) None

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(newScope (), "user-1", "acme revenue?")

            match result.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "no question compiler" "typed, names the missing substrate"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }

        testCaseAsync
            "a composed IAIProvider arms the 67b structured compiler: extraction parses and resolves end-to-end"
        <| async {
            let stub =
                StubStructuredProvider(
                    """{"triples":[{"subject_hierarchy":"brand","subject_path":["acme"],"metric":"revenue","period_label":"Q2-2026"}]}"""
                )

            let _, sp =
                composedUnder EnabledFactStore (Some(registryWith UntilSuperseded None)) (Some(stub :> IAIProvider))

            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            let fact = assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m))

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(scope, "user-1", "what was acme revenue in Q2?")

            Expect.equal (stepsOf result) [ UseFact fact.FactId ] "compiled through 67b, resolved to the fact"

            Expect.equal
                (result.Steps |> List.map _.Candidate.PeriodLabel)
                [ Some "Q2-2026" ]
                "the compiler's period label is carried on the candidate"

            match stub.LastSystemPrompt with
            | Some prompt -> Expect.stringContains prompt "revenue" "the vocabulary prompt reached the provider"
            | None -> failtest "no system prompt reached the provider"
        }

        testCaseAsync "non-JSON structured output degrades to a typed refusal, never a throw"
        <| async {
            let stub = StubStructuredProvider "the revenue was probably fine"

            let _, sp =
                composedUnder EnabledFactStore (Some(registryWith UntilSuperseded None)) (Some(stub :> IAIProvider))

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(newScope (), "user-1", "acme revenue?")

            match result.Refusal with
            | Some(QuestionNotCompiled detail) -> Expect.stringContains detail "non-JSON" "the parse failure is named"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }
    ]

// ── Phase 706 — the 67b question compiler through compose ─────────

let populationComposeTests =
    testList "Phase 706 question compiler through compose" [

        testCaseAsync "a composed IAIProvider compiles a superlative question to a population, end to end"
        <| async {
            let stub =
                StubStructuredProvider(
                    """{"triples":[],"populations":[{"subject_hierarchy":"brand","metric":"revenue","ordering":"best_first","top_k":2}]}"""
                )

            let _, sp =
                composedUnder
                    EnabledFactStore
                    (Some(registryDirected HigherIsBetter UntilSuperseded None))
                    (Some(stub :> IAIProvider))

            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            let acme = assertFact store scope (brandDraft "acme" (Scalar 300m))
            assertFact store scope (brandDraft "gamma" (Scalar 100m)) |> ignore

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(scope, "user-1", "which brand has the highest revenue?")

            match result.Steps |> List.map _.Step with
            | [ UseAggregate aggregate ] ->
                Expect.equal aggregate.Population.Metric "revenue" "the compiled population triple reached the planner"
                Expect.equal aggregate.Population.TopK 2 "including its k"
                Expect.equal (aggregate.Ranked |> List.map _.FactId |> List.head) acme.FactId "and it resolved"
            | other -> failtestf "expected one UseAggregate, got %A" other

            match stub.LastSystemPrompt with
            | Some prompt ->
                Expect.stringContains
                    prompt
                    "POPULATION question"
                    "the compiler is told what a population triple is for"

                Expect.stringContains
                    prompt
                    "higher is better"
                    "and which way each metric's declared direction points — a registry fact, not a guess"
            | None -> failtest "no system prompt reached the provider"
        }

        testCaseAsync "a compiler that emits no 'populations' key compiles exactly as it did before (GP 11)"
        <| async {
            let stub =
                StubStructuredProvider(
                    """{"triples":[{"subject_hierarchy":"brand","subject_path":["acme"],"metric":"revenue"}]}"""
                )

            let _, sp =
                composedUnder EnabledFactStore (Some(registryWith UntilSuperseded None)) (Some(stub :> IAIProvider))

            let scope = newScope ()
            let store = sp.GetRequiredService<IFactStore>()
            let fact = assertFact store scope (draft "revenue" [ "h1" ] (Scalar 21800m))

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(scope, "user-1", "what was acme revenue?")

            Expect.equal (result.Steps |> List.map _.Step) [ UseFact fact.FactId ] "the pre-706 document still compiles"
        }

        testCaseAsync "a malformed 'populations' array is a typed refusal, never a partial salvage"
        <| async {
            let stub =
                StubStructuredProvider """{"triples":[],"populations":"the top ten brands"}"""

            let _, sp =
                composedUnder EnabledFactStore (Some(registryWith UntilSuperseded None)) (Some(stub :> IAIProvider))

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(newScope (), "user-1", "top brands?")

            match result.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "'populations' is not an array" "the shape violation is named"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }

        testCaseAsync "an empty compilation — neither triples nor populations — is the unanswerable-question refusal"
        <| async {
            let stub = StubStructuredProvider """{"triples":[],"populations":[]}"""

            let _, sp =
                composedUnder EnabledFactStore (Some(registryWith UntilSuperseded None)) (Some(stub :> IAIProvider))

            let planner = sp.GetRequiredService<IAnswerPlanner>()
            let! result = planner.Plan(newScope (), "user-1", "what is the meaning of life?")

            match result.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "no registered subject or metric" "the refusal names the gap"
            | other -> failtestf "expected QuestionNotCompiled, got %A" other
        }
    ]

let tests =
    testList "Phase 560 grounded answer planner" [
        compilerTests
        resolutionTests
        disclosureTests
        populationCompilerTests
        populationResolutionTests
        populationDisclosureTests
        chainTests
        composeTests
        populationComposeTests
    ]