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
/// resolve against.
let private registryWith (staleness: StalenessPolicy) (producingOp: string option) : IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "TestModule"
            Definition = {
                Id = "revenue"
                Name = "Revenue"
                Unit = "GBP"
                Dimensionality = "currency"
                Direction = HigherIsBetter
                DisplayFormat = "N0"
                Staleness = staleness
                ProducingOperation = producingOp
                CanonicalMethod = None
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

let private plan (planner: IAnswerPlanner) (scope: string) (question: string) : AnswerPlan =
    planner.Plan(scope, "user-1", question) |> Async.RunSynchronously

let private stepsOf (p: AnswerPlan) : PlanStep list = p.Steps |> List.map _.Step

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

let tests =
    testList "Phase 560 grounded answer planner" [
        compilerTests
        resolutionTests
        disclosureTests
        chainTests
        composeTests
    ]