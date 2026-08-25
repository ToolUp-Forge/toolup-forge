// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactClauseFeederTests

open System
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRagTelemetry
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI.SystemPromptBuilder
open ToolUp.Facts
open ToolUp.RAG
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 708 — the fact-clause feeder ──────────────────────────────
//
// Phase 522 built the push path and Phase 558 wired its resolver in, but
// nothing ever produced a `FactClause`: every non-test `RetrievalRequest`
// left the field `None`, so facts entered the prompt only when the model
// thought to call a tool. These cases pin the feeder that closes the
// loop, and — as importantly — pin what it does NOT change.
//
// Covered: the projection from plan steps to clauses (`UseFact` only,
// deduplicated, current-head), the prompt-path wiring (a resolving
// question pushes its fact ahead of the chunks under the verbatim
// contract), byte-identity for a non-resolving question and for a
// planner-less composition, the timeout degrading to `None` with a
// telemetry mark, the compose knob, and the "reuse, don't recompute"
// property behind the plan id.

// ── Harness ───────────────────────────────────────────────────────

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draftFor (brand: string) (metric: string) (value: FactValue) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ brand ]
    }
    Metric = MetricRef metric
    Value = value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "h1" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private registry: IMetricRegistry =
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
                Staleness = UntilSuperseded
                ProducingOperation = None
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

let private candidate (path: string list) (metric: string) : TripleCandidate = {
    SubjectHierarchy = "brand"
    SubjectPath = path
    Metric = metric
    PeriodFrom = None
    PeriodTo = None
    PeriodLabel = None
}

/// A deterministic compiler emitting fixed candidates, counting how many
/// times it was asked. The count is what proves "reused, not recomputed".
type private CountingCompiler(candidates: TripleCandidate list) =
    let mutable calls = 0
    member _.Calls = calls

    member _.Compiler: TripleCompiler =
        fun _ -> async {
            Interlocked.Increment &calls |> ignore
            return Ok candidates
        }

/// A planner over in-memory substrate, plus the store to seed and the
/// event store to read plan records back from.
let private plannerOver (compiler: TripleCompiler) : IAnswerPlanner * IFactStore * IEventStore =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    AnswerPlanner.create store gate (Some registry) events compiler, store, events

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// A stub pipeline that records the request it was handed and returns a
/// fixed match list. The recorded request is the whole point: the feeder's
/// observable output is one field on it.
type private RecordingPipeline(matches: VectorMatch list) =
    let mutable last: RetrievalRequest option = None
    member _.LastRequest = last

    interface IRetrievalPipeline with
        member _.Retrieve request _ = async {
            last <- Some request
            return matches
        }

        member _.Index _ _ _ = async { return () }
        member _.DeleteByScope _ = async { return () }

/// Telemetry stub capturing every stage name recorded.
type private RecordingTelemetry() =
    let stages = ResizeArray<string>()
    member _.Stages = List.ofSeq stages

    interface IRagTelemetry with
        member _.RecordEmbedding(_, _) = ()
        member _.RecordEnqueue(_, _, _) = ()
        member _.RecordFlush(_, _) = ()
        member _.RecordIndexLoadError _ = ()

        member _.RecordRetrievalStages stageTimings =
            stageTimings |> List.iter (fst >> stages.Add)

        member _.RecordRetrieval(_, _, _) = ()
        member _.RecordObserverFailure _ = ()
        member _.Snapshot() = (RagTelemetry.createNoOp ()).Snapshot()

/// An `IFactClausePlanner` that never returns — the shape the timeout
/// guard exists for.
type private HangingPlanner() =
    interface IFactClausePlanner with
        member _.PlanClauses(_, _, _) = async {
            do! Async.Sleep 60_000
            return PlannedFactClauses.none
        }

/// An `IFactClausePlanner` that throws — a planner fault must degrade the
/// same way a timeout does.
type private FaultingPlanner() =
    interface IFactClausePlanner with
        member _.PlanClauses(_, _, _) : Async<PlannedFactClauses> = async { return failwith "compiler exploded" }

let private chunkMatch: VectorMatch = {
    ChunkId = "c1"
    Content = "some supporting prose about the brand"
    Score = 0.4
    Scope = Team "t"
    Metadata = Map.ofList [ ChunkMetadata.OriginKey, "Narrative" ]
}

let private factMatch (factId: string) (rendering: string) : VectorMatch = {
    ChunkId = "fact:" + factId
    Content = rendering
    Score = 1.0
    Scope = Team "t"
    Metadata =
        Map.ofList [
            ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue Fact
            ChunkMetadata.FactIdKey, factId
            ChunkMetadata.FactRenderingKey, rendering
            ChunkMetadata.FactMetricKey, "revenue"
            ChunkMetadata.FactFreshnessKey, "Fresh"
        ]
}

let private contextFor (question: string) : PromptContext = {
    Access = AccessContext.unrestricted (AuthenticatedUser "u")
    ActiveModule = None
    ActivePage = None
    ActivePageNarrative = None
    ModuleContexts = Map.empty
    CurrentMessage = Some question
    ConversationHistory = []
    RetrievalFilters = None
    RetrievedSources = ref []
    ShortCircuit = ref None
    PlannedAnswerId = ref None
}

// ── The projection: plan steps → clauses (708.A) ──────────────────

let projectionTests =
    testList "Phase 708 clause projection" [

        testCaseAsync "a UseFact step becomes a clause carrying the candidate's subject, metric and period"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "revenue" ]
            let planner, store, _ = plannerOver compiler.Compiler
            let scope = newScope ()
            assertFact store scope (draftFor "acme" "revenue" (Scalar 21800m)) |> ignore

            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner
            let! planned = feeder.PlanClauses(scope, "user-1", "what was acme revenue?")

            match planned.Clauses with
            | [ clause ] ->
                Expect.equal clause.SubjectHierarchy "brand" "hierarchy carried through"
                Expect.equal clause.SubjectPath [ "acme" ] "subject path carried through"
                Expect.equal clause.Metric "revenue" "metric carried through"

                // `UseFact` means "the CURRENT head answers this", so the
                // clause must ask for the current head too — pinning the
                // plan's compile instant would ask a different question.
                Expect.isNone clause.AsOf "the clause reads the current head, not an as-of snapshot"
            | other -> failtestf "expected one clause, got %A" other

            Expect.isFalse (String.IsNullOrEmpty planned.PlanId) "the plan id names the retained plan"
        }

        testCaseAsync "a question that resolves nothing yields no clauses and no plan id (GP 9 — no nearest match)"
        <| async {
            // `sales` is not registered; the planner refuses rather than
            // reaching for `revenue`, so the feeder has nothing to push.
            let compiler = CountingCompiler [ candidate [ "acme" ] "sales" ]
            let planner, _, _ = plannerOver compiler.Compiler

            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner
            let! planned = feeder.PlanClauses(newScope (), "user-1", "what were acme sales?")

            Expect.isTrue (PlannedFactClauses.isEmpty planned) "an unresolvable question pushes nothing"
            Expect.equal planned PlannedFactClauses.none "it contributes exactly the none value"
            Expect.equal planned.PlanId "" "no plan is retained when nothing is pushable"
        }

        testCaseAsync "a metric with no stored fact plans a gap, not a clause"
        <| async {
            // Registered vocabulary, empty store: the planner resolves
            // `RequestData` (or `ComputeFact`), and a gap is not a fact.
            let compiler = CountingCompiler [ candidate [ "acme" ] "revenue" ]
            let planner, _, _ = plannerOver compiler.Compiler

            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner
            let! planned = feeder.PlanClauses(newScope (), "user-1", "what was acme revenue?")

            Expect.isTrue (PlannedFactClauses.isEmpty planned) "an unanswerable-for-data triple pushes nothing"
        }

        testCaseAsync "a repeated triple compiles to ONE clause (one clause is one retrieval read)"
        <| async {
            let compiler =
                CountingCompiler [ candidate [ "acme" ] "revenue"; candidate [ "acme" ] "revenue" ]

            let planner, store, _ = plannerOver compiler.Compiler
            let scope = newScope ()
            assertFact store scope (draftFor "acme" "revenue" (Scalar 21800m)) |> ignore

            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner
            let! planned = feeder.PlanClauses(scope, "user-1", "acme revenue and acme revenue?")

            Expect.equal planned.Clauses.Length 1 "duplicate triples collapse"
        }
    ]

// ── The prompt path (708.A / 708.C) ───────────────────────────────

let promptPathTests =
    testList "Phase 708 prompt-path wiring" [

        testCaseAsync "a resolving question populates FactClause and the fact leads the prompt, verbatim-contracted"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "revenue" ]
            let planner, store, _ = plannerOver compiler.Compiler
            let scope = newScope ()
            assertFact store scope (draftFor "acme" "revenue" (Scalar 21800m)) |> ignore

            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner
            let pipeline = RecordingPipeline [ factMatch "fact-1" "£21,800"; chunkMatch ]
            let ctx = contextFor "what was acme revenue?"

            // The prompt path derives the planner's scope from the caller,
            // so the seeded scope has to be the caller's own.
            let ctx = {
                ctx with
                    Access = AccessContext.unrestricted (AuthenticatedUser scope)
            }

            let builder =
                RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    (Some feeder)
                    FactClausePlanOptions.defaults
                    (pipeline :> IRetrievalPipeline)

            let! prompt = builder ctx

            match pipeline.LastRequest with
            | Some request ->
                match request.FactClause with
                | Some clause ->
                    Expect.equal clause.Metric "revenue" "the compiled clause reached the pipeline"
                    Expect.equal clause.SubjectPath [ "acme" ] "with the subject the question named"
                | None -> failtest "expected the planned clause on the retrieval request"
            | None -> failtest "expected the pipeline to have been called"

            Expect.stringContains prompt "quote these numbers verbatim" "the facts block carries the verbatim contract"

            Expect.stringContains prompt "[F1] revenue: £21,800" "the pushed fact renders in the facts block"

            let factIndex = prompt.IndexOf "[F1] revenue"
            let chunkIndex = prompt.IndexOf "supporting prose"
            Expect.isGreaterThan chunkIndex factIndex "the facts block precedes the chunk context"

            Expect.isSome ctx.PlannedAnswerId.Value "the plan id lands on the channel the provenance recording reads"
        }

        testCaseAsync "a non-resolving question builds the SAME request a planner-less turn does (GP 11)"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "sales" ]
            let planner, _, _ = plannerOver compiler.Compiler
            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner

            let withFeeder = RecordingPipeline [ chunkMatch ]
            let without = RecordingPipeline [ chunkMatch ]

            let build (p: IFactClausePlanner option) (pipeline: RecordingPipeline) =
                RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    p
                    FactClausePlanOptions.defaults
                    (pipeline :> IRetrievalPipeline)

            let ctxA = contextFor "what were acme sales?"
            let ctxB = contextFor "what were acme sales?"

            let! promptA = (build (Some feeder) withFeeder) ctxA
            let! promptB = (build None without) ctxB

            Expect.equal promptA promptB "the prompt is byte-identical"

            Expect.equal
                withFeeder.LastRequest
                without.LastRequest
                "the retrieval request is identical, field for field"

            Expect.isNone ctxA.PlannedAnswerId.Value "nothing planned ⇒ no plan id on the channel"
        }

        testCaseAsync "Enabled = false never consults a composed planner"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "revenue" ]
            let planner, store, _ = plannerOver compiler.Compiler
            let scope = newScope ()
            assertFact store scope (draftFor "acme" "revenue" (Scalar 21800m)) |> ignore

            let feeder = AnswerPlanClausePlanner.create planner :> IFactClausePlanner
            let pipeline = RecordingPipeline [ chunkMatch ]

            let ctx = {
                contextFor "what was acme revenue?" with
                    Access = AccessContext.unrestricted (AuthenticatedUser scope)
            }

            let builder =
                RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    (Some feeder)
                    FactClausePlanOptions.disabled
                    (pipeline :> IRetrievalPipeline)

            let! _ = builder ctx

            Expect.equal compiler.Calls 0 "the compiler was never invoked"

            match pipeline.LastRequest with
            | Some request -> Expect.isNone request.FactClause "no clause on the request"
            | None -> failtest "expected the pipeline to have been called"
        }

        testCaseAsync "the back-compat tool-aware builder is byte-identical to the planner-less planned path"
        <| async {
            let viaOld = RecordingPipeline [ chunkMatch ]
            let viaNew = RecordingPipeline [ chunkMatch ]

            let ctxA = contextFor "unrelated question"
            let ctxB = contextFor "unrelated question"

            let! promptA =
                (RAGPromptBuilder.withRetrievalToolAware
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    (viaOld :> IRetrievalPipeline))
                    ctxA

            let! promptB =
                (RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    None
                    FactClausePlanOptions.disabled
                    (viaNew :> IRetrievalPipeline))
                    ctxB

            Expect.equal promptA promptB "same prompt"
            Expect.equal viaOld.LastRequest viaNew.LastRequest "same request"
        }
    ]

// ── The bound (708.B) ─────────────────────────────────────────────

let boundTests =
    testList "Phase 708 latency guard" [

        testCaseAsync "a planner that overruns its budget degrades to no clause, with a telemetry mark"
        <| async {
            let telemetry = RecordingTelemetry()
            let pipeline = RecordingPipeline [ chunkMatch ]
            let ctx = contextFor "what was acme revenue?"

            let builder =
                RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    (Some(telemetry :> IRagTelemetry))
                    None
                    RAGPromptBuilder.ToolFraming.none
                    (Some(HangingPlanner() :> IFactClausePlanner))
                    { Enabled = true; TimeoutMs = 25 }
                    (pipeline :> IRetrievalPipeline)

            let! prompt = builder ctx

            match pipeline.LastRequest with
            | Some request -> Expect.isNone request.FactClause "the overrun contributes no clause"
            | None -> failtest "the turn must still reach retrieval"

            Expect.stringContains prompt "supporting prose" "the turn proceeds on chunks alone"

            Expect.contains telemetry.Stages "FactClausePlanTimeout" "the overrun is marked"

            Expect.isNone ctx.PlannedAnswerId.Value "nothing planned ⇒ no plan id"
        }

        testCaseAsync
            "a planner that throws degrades exactly as a timeout does — retrieval never fails because planning did"
        <| async {
            let telemetry = RecordingTelemetry()
            let pipeline = RecordingPipeline [ chunkMatch ]

            let builder =
                RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    (Some(telemetry :> IRagTelemetry))
                    None
                    RAGPromptBuilder.ToolFraming.none
                    (Some(FaultingPlanner() :> IFactClausePlanner))
                    FactClausePlanOptions.defaults
                    (pipeline :> IRetrievalPipeline)

            let! prompt = builder (contextFor "what was acme revenue?")

            Expect.stringContains prompt "supporting prose" "the turn completed"
            Expect.contains telemetry.Stages "FactClausePlanTimeout" "the fault is marked on the same channel"
        }

        testCaseAsync "a planner that ran and resolved nothing is marked distinctly from one that is off"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "sales" ]
            let planner, _, _ = plannerOver compiler.Compiler
            let telemetry = RecordingTelemetry()
            let pipeline = RecordingPipeline [ chunkMatch ]

            let builder =
                RAGPromptBuilder.withRetrievalPlanned
                    RetrievalDefaults.defaults
                    (Some(telemetry :> IRagTelemetry))
                    None
                    RAGPromptBuilder.ToolFraming.none
                    (Some(AnswerPlanClausePlanner.create planner :> IFactClausePlanner))
                    FactClausePlanOptions.defaults
                    (pipeline :> IRetrievalPipeline)

            let! _ = builder (contextFor "what were acme sales?")

            Expect.contains telemetry.Stages "FactClausePlanEmpty" "a fired-but-empty compile is visible"
        }

        test "the budget is clamped into a range Async.StartChild will accept" {
            let clamped = FactClausePlanOptions.clamp { Enabled = true; TimeoutMs = 0 }
            Expect.equal clamped.TimeoutMs 1 "a non-positive budget clamps to 1 ms"

            let wide = FactClausePlanOptions.clamp { Enabled = true; TimeoutMs = 600_000 }
            Expect.equal wide.TimeoutMs 30_000 "an unbounded budget clamps to 30 s"
        }
    ]

// ── Reuse, not recompute (708.B / 560.D) ──────────────────────────

let reuseTests =
    testList "Phase 708 plan reuse" [

        testCaseAsync "recording by plan id reuses the computed plan — the question is compiled exactly once"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "revenue" ]
            let planner, store, events = plannerOver compiler.Compiler
            let scope = newScope ()
            assertFact store scope (draftFor "acme" "revenue" (Scalar 21800m)) |> ignore

            let feeder = AnswerPlanClausePlanner.create planner
            let! planned = (feeder :> IFactClausePlanner).PlanClauses(scope, "user-1", "what was acme revenue?")

            Expect.equal compiler.Calls 1 "one compile so far"

            let! recorded = (feeder :> IPlannedAnswerRecorder).RecordPlanned(scope, "msg-1", planned.PlanId)

            Expect.isTrue recorded "the retained plan was found and recorded"
            Expect.equal compiler.Calls 1 "recording did NOT recompile the question"

            let! written = events.ReadByType(scope, PlanEvents.RecordedType)
            let planRecords = written

            Expect.equal planRecords.Length 1 "exactly one plan record"

            Expect.stringContains
                planRecords.Head.Payload
                planned.PlanId
                "the recorded plan is the SAME plan that shaped the prompt"
        }

        testCaseAsync "recording an unknown plan id reports false rather than throwing"
        <| async {
            let compiler = CountingCompiler []
            let planner, _, _ = plannerOver compiler.Compiler
            let feeder = AnswerPlanClausePlanner.create planner :> IPlannedAnswerRecorder

            let! recorded = feeder.RecordPlanned(newScope (), "msg-1", "plan-does-not-exist")

            Expect.isFalse recorded "an unrecoverable plan id degrades; the answer still stands"
        }

        testCaseAsync "retention is bounded — an evicted plan reports false, it does not grow without limit"
        <| async {
            let compiler = CountingCompiler [ candidate [ "acme" ] "revenue" ]
            let planner, store, _ = plannerOver compiler.Compiler
            let scope = newScope ()
            assertFact store scope (draftFor "acme" "revenue" (Scalar 21800m)) |> ignore

            let feeder = AnswerPlanClausePlanner.createWithRetention planner 1
            let clausePlanner = feeder :> IFactClausePlanner

            let! first = clausePlanner.PlanClauses(scope, "user-1", "what was acme revenue?")
            let! _second = clausePlanner.PlanClauses(scope, "user-1", "and again?")

            Expect.isNone (feeder.TryPlan first.PlanId) "the older plan was evicted at the bound"

            let! recorded = (feeder :> IPlannedAnswerRecorder).RecordPlanned(scope, "msg-1", first.PlanId)

            Expect.isFalse recorded "an evicted plan records as a miss, never a throw"
        }
    ]

// ── Compose (708.A / GP 13) ───────────────────────────────────────

let private composedUnder (knob: FactStoreMode) : ServiceProvider =
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

    services.AddSingleton<IMetricRegistry>(registry) |> ignore

    match app.Extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    services.BuildServiceProvider()

let composeTests =
    testList "Phase 708 compose registration" [

        test "EnabledFactStore registers the clause planner on the same knob as the store" {
            let sp = composedUnder EnabledFactStore

            Expect.isFalse
                (isNull (box (sp.GetService<IFactClausePlanner>())))
                "the feeder rides the one fact-tier knob"

            Expect.isFalse
                (isNull (box (sp.GetService<IPlannedAnswerRecorder>())))
                "so does the recorder that reuses its plans"
        }

        test "the planner and the recorder are ONE instance — a split pair would empty the retention" {
            let sp = composedUnder EnabledFactStore

            let asPlanner = sp.GetRequiredService<IFactClausePlanner>()
            let asRecorder = sp.GetRequiredService<IPlannedAnswerRecorder>()

            Expect.isTrue
                (obj.ReferenceEquals(asPlanner, asRecorder))
                "both faces resolve to the same object, so plans retained by one are visible to the other"
        }

        test "NoFactStore registers no feeder — a fact-less composition is byte-identical (GP 11 / GP 13)" {
            let sp = composedUnder NoFactStore

            Expect.isTrue (isNull (sp.GetService(typeof<IFactClausePlanner>))) "no feeder in a no-store composition"

            Expect.isTrue (isNull (sp.GetService(typeof<IPlannedAnswerRecorder>))) "and no recorder"
        }

        test "the feeder defaults ON — the push path existing but never firing is what 708 closes" {
            Expect.isTrue FactClausePlanOptions.defaults.Enabled "composing the fact tier is the opt-in"
            Expect.isFalse FactClausePlanOptions.disabled.Enabled "and the pre-708 posture stays reachable"

            Expect.equal
                FactClausePlanOptions.disabled.TimeoutMs
                FactClausePlanOptions.defaults.TimeoutMs
                "turning the feeder off does not silently retune the budget"
        }
    ]

let tests =
    testList "Phase 708 fact-clause production wiring" [
        projectionTests
        promptPathTests
        boundTests
        reuseTests
        composeTests
    ]