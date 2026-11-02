// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.RagFactTierTests

// ─── Phase 986 — the fact tier on a RAG app, push door armed ─────────
//
// Composing the fact store used to arm only the tool door in a real RAG
// app. The answer planner looked for a bare `IAIProvider`; the AI tier
// registers an `IAIProviderFactory` and nothing else, so the planner fell
// back to the no-compiler refusal, no question ever compiled into a fact
// clause, and no fact was ever pushed into the prompt. And the only way to
// put the tier onto a RAG app was a lambda over the AI layer that silently
// did nothing unless `withConfig` had already switched the fact store on.
//
// What this pack pins:
//
//   1. `RagOptions.withFacts FactsCompose.withFactTier` composes the tier
//      on EITHER side of `withConfig`, and a tier composed by hand on the
//      deployment still works.
//   2. End to end, with no test-only `IAIProvider`: a fact written through
//      a DECLARED fact table reaches the prompt under the verified-facts
//      header, compiled through the AI tier's own provider factory, under
//      the planning principal's team identity.
//   3. A fact tier with no compiler warns at startup, naming the remedy;
//      one with a compiler, or a deliberate opt-out, does not.

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IRetrievalPipeline
open ToolUp.Platform.Scope.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI.SystemPromptBuilder
open ToolUp.Facts
open ToolUp.RAG
open ToolUp.RAG.RAGCompose
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Config
open ToolUp.Platform.Compose

// ── Fixtures ──────────────────────────────────────────────────────

let private brands: SubjectDefinition = {
    Id = "brand"
    Name = "Brand"
    Levels = [ "brand" ]
    Calendar = None
}

let private revenue: MetricDefinition = {
    Id = "revenue"
    Name = "revenue"
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private metrics: IMetricRegistry =
    MetricRegistry.build [
        {
            MetricRegistration.Module = "sales"
            Definition = revenue
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = brands
        }
    ]

/// The declared table the fact is written through.
let private brandSales: FactTableDefinition = {
    Id = "brand-sales"
    SchemaVersion = 1
    Hierarchy = "brand"
    Level = "brand"
    Columns = [ FactTableDefinition.column "revenue" FactTableValueShape.Scalar ]
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "brand-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private tables: IFactTableRegistry =
    FactTableRegistry.build [
        {
            FactTableRegistration.Module = "sales"
            Definition = brandSales
        }
    ] [ BindAllFactTables DefaultFactTableWriter.Destination ]

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private compiledRevenueQuestion =
    """{"triples":[{"subject_hierarchy":"brand","subject_path":["acme"],"metric":"revenue"}]}"""

/// The model behind the factory: answers the planner's structured compile
/// with a canned compilation and counts the calls.
type private CompilingProvider(content: string) =
    member val Compiles = 0 with get, set

    interface IAIProvider with
        member _.Capabilities = AIProviderCapabilities.unknown

        member _.SendMessage(_, _, _, _, _) = async { return Error(MalformedResponse "SendMessage is not used") }

        member this.SendStructuredMessage(_, _, _, _, _) = async {
            this.Compiles <- this.Compiles + 1

            return
                Ok {
                    Content = content
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

/// The AI tier's provider factory — the ONLY provider registration a
/// standard composition makes. Records the access contexts it resolved.
type private RecordingFactory(provider: IAIProvider) =
    member val Resolved: AccessContext list = [] with get, set

    interface ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None

        member this.Resolve ctx = async {
            this.Resolved <- this.Resolved @ [ ctx ]
            return Ok provider
        }

        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }
        member _.BuildPlatform(_, _, _) = None

let private stubProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private constantEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts = async { return texts |> Seq.map (fun _ -> unitVec) |> Seq.toArray }

        member _.ProviderId = "stub"
        member _.ModelId = "constant"
        member _.Dimensions = 8
    }

/// The RAG options a deployment writes, over `factory`.
let private ragApp (factory: ToolUp.AI.IAIProviderFactory) : RagOptions =
    RagOptions.create factory stubProfile constantEmbedder

/// The deployment over `storage` with `rag` composed onto it.
let private deploy (storage: IBlobStorage) (rag: RagOptions) : ServerApp =
    ServerApp.empty |> ServerApp.withStorage storage |> withRAG rag

/// Build the composed provider the way the host does: the substrate the
/// platform registers first, then the composition's own service config.
let private build (storage: IBlobStorage) (composed: ServerApp) : IServiceCollection * ServiceProvider =
    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(metrics) |> ignore
    services.AddSingleton<IFactTableRegistry>(tables) |> ignore

    let services =
        ToolUp.Platform.Tests.Support.TestSubstrate.applyServiceConfig composed services

    services, services.BuildServiceProvider()

let private compilerVerdict (services: IServiceCollection) : ConfigValidation.ValidationResult =
    let guards =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<ConfigValidation.IConfigValidator>)
        |> Seq.choose (fun d ->
            match d.ImplementationInstance with
            | :? ConfigValidation.IConfigValidator as v when v.Name = "fact-question-compiler" -> Some v
            | _ -> None)
        |> List.ofSeq

    match guards with
    | [ guard ] -> guard.Validate() |> Async.RunSynchronously
    | other -> failtestf "expected exactly one fact-question-compiler guard, found %d" other.Length

let private teamScope (teamId: string) : ResolvedScope =
    ScopeResolution.ofStorageScope {
        ScopeId = teamId
        Container = $"team-{teamId}"
        Persist = true
    }

let private contextFor (scope: ResolvedScope) (question: string) : PromptContext = {
    Access = AccessContext.unrestricted (AuthenticatedUser "ann")
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
    Scope = scope
    Services = ToolUp.AI.SystemPromptBuilder.PromptContext.noServices
}

let private writeThroughTable (sp: IServiceProvider) (teamId: string) (value: decimal) =
    let writer = sp.GetRequiredService<IFactTableWriter>()

    let run =
        writer.OpenRun(TestScope.resolved teamId, brandSales.Id)
        |> Async.RunSynchronously
        |> Result.defaultWith (fun e -> failtestf "open: %s" (FactTableWriteError.describe e))

    writer.WriteRows(
        TestScope.resolved teamId,
        run.RunId,
        [
            {
                Subject = [ "acme" ]
                Period = september
                Values = Map.ofList [ "revenue", Scalar value ]
            }
        ]
    )
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "write: %s" (FactTableWriteError.describe e))
    |> ignore

    writer.Commit(TestScope.resolved teamId, run.RunId)
    |> Async.RunSynchronously
    |> Result.defaultWith (fun e -> failtestf "commit: %s" (FactTableWriteError.describe e))
    |> ignore

/// The prompt the composed RAG layer builds for `question`: the composed
/// clause planner and the composed retrieval pipeline, through the same
/// planned prompt path the RAG system-prompt layer runs.
let private plannedPrompt (sp: IServiceProvider) (scope: ResolvedScope) (question: string) =
    let builder =
        RAGPromptBuilder.withRetrievalPlanned
            RetrievalDefaults.defaults
            None
            None
            RAGPromptBuilder.ToolFraming.none
            (Some(sp.GetRequiredService<IFactClausePlanner>()))
            FactClausePlanOptions.defaults
            (sp.GetRequiredService<IRetrievalPipeline>())

    builder (contextFor scope question) |> Async.RunSynchronously

// ── 986.A — one combinator, either order ──────────────────────────

let private config: ServerConfig = ServerConfig.defaults

let compositionTests =
    testList "Phase 986 the fact tier composes in one call" [

        test "withFacts BEFORE withConfig still composes the tier (withConfig resets the knob)" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let app =
                ragApp (RecordingFactory(CompilingProvider "{}"))
                |> RagOptions.withFacts FactsCompose.withFactTier
                |> deploy storage
                |> ServerApp.withConfig config

            Expect.equal app.Config.Stores.FactStore FeatureMode.Disabled "the later withConfig did reset the knob"

            let _, sp = build storage app

            Expect.isNotNull (box (sp.GetService<IFactClausePlanner>())) "the clause planner is composed anyway"
            Expect.isNotNull (box (sp.GetService<IFactTableWriter>())) "and so is the fact-table writer"
            Expect.isNotNull (box (sp.GetService<IFactResolver>())) "and the resolver retrieval picks up"
        }

        test "withFacts AFTER withConfig composes the same tier" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let app =
                ServerApp.empty
                |> ServerApp.withStorage storage
                |> ServerApp.withConfig config
                |> withRAG (
                    ragApp (RecordingFactory(CompilingProvider "{}"))
                    |> RagOptions.withFacts FactsCompose.withFactTier
                )

            let _, sp = build storage app

            Expect.isNotNull (box (sp.GetService<IFactClausePlanner>())) "the clause planner is composed"
            Expect.isNotNull (box (sp.GetService<IFactTableWriter>())) "and the fact-table writer"
        }

        test "a RAG app that does not call withFacts composes no fact tier (GP 13)" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let _, sp =
                build storage (deploy storage (ragApp (RecordingFactory(CompilingProvider "{}"))))

            Expect.isNull (sp.GetService(typeof<IFactClausePlanner>)) "no clause planner"
            Expect.isNull (sp.GetService(typeof<IFactTableWriter>)) "no fact-table writer"
        }

        test "a fact tier composed by hand on the deployment keeps working when the knob is set first" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let app =
                ServerApp.empty
                |> ServerApp.withStorage storage
                |> ServerApp.withConfig {
                    config with
                        Stores = {
                            config.Stores with
                                FactStore = FeatureMode.Enabled
                        }
                }
                |> FactsCompose.withFactStore
                |> FactsCompose.withFactTableWriter
                |> withRAG (ragApp (RecordingFactory(CompilingProvider "{}")))

            let _, sp = build storage app

            Expect.isNotNull (box (sp.GetService<IFactClausePlanner>())) "the hand-wired route composes"
        }
    ]

// ── 986.B / 986.D — the push door fires in a standard composition ─

let pushDoorTests =
    testList "Phase 986 the push door fires without test-only wiring" [

        test "a fact written through a declared table reaches the prompt under the verified-facts header" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let model = CompilingProvider compiledRevenueQuestion
            let factory = RecordingFactory model

            let app =
                ragApp factory
                |> RagOptions.withFacts FactsCompose.withFactTier
                |> deploy storage
                |> ServerApp.withConfig config

            let _, sp = build storage app

            Expect.isNull
                (sp.GetService(typeof<IAIProvider>))
                "no bare IAIProvider is registered — the compiler must come from the factory"

            let teamId = Guid.NewGuid().ToString "N"
            writeThroughTable sp teamId 21800m

            let prompt = plannedPrompt sp (teamScope teamId) "what was acme revenue?"

            Expect.stringContains prompt "Verified facts" "the facts block is in the prompt"

            Expect.stringContains prompt "quote these numbers verbatim" "under the verbatim-quoting contract"

            Expect.stringContains prompt "[F1] revenue" "the fact written through the table leads the block"
            Expect.stringContains prompt "[F1] revenue: 21800" "with its value, verbatim"

            Expect.equal model.Compiles 1 "the question was compiled once, through the factory's provider"

            match factory.Resolved with
            | [ ctx ] ->
                Expect.equal
                    ctx.Subject
                    (TeamMember("ann", teamId))
                    "the compile resolved its provider as the planning principal, in the plan's team"
            | other -> failtestf "expected one factory resolution, got %d" other.Length
        }

        test "a factory that resolves no provider refuses the compile, and the prompt carries no facts block" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let refusing =
                { new ToolUp.AI.IAIProviderFactory with
                    member _.Available = []
                    member _.PlatformDescriptors = []
                    member _.PlatformDescriptor = None
                    member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }
                    member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }
                    member _.BuildPlatform(_, _, _) = None
                }

            let app =
                ragApp refusing
                |> RagOptions.withFacts FactsCompose.withFactTier
                |> deploy storage

            let _, sp = build storage app
            let teamId = Guid.NewGuid().ToString "N"
            writeThroughTable sp teamId 21800m

            let planner = sp.GetRequiredService<IAnswerPlanner>()

            let plan =
                planner.Plan(teamScope teamId, "ann", "what was acme revenue?")
                |> Async.RunSynchronously

            match plan.Refusal with
            | Some(QuestionNotCompiled detail) ->
                Expect.stringContains detail "AI provider factory" "the refusal names where resolution failed"
            | other -> failtestf "expected a QuestionNotCompiled refusal, got %A" other

            let prompt = plannedPrompt sp (teamScope teamId) "what was acme revenue?"
            Expect.isFalse (prompt.Contains "Verified facts") "no fact is pushed when nothing compiled"
        }

        test "an explicit compiler outranks the factory, and is how a deployment opts out" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let model = CompilingProvider compiledRevenueQuestion
            let factory = RecordingFactory model

            let app =
                ragApp factory
                |> RagOptions.withFacts (
                    FactsCompose.withFactTier
                    >> FactsCompose.withQuestionCompiler AnswerPlanner.noQuestionCompiler
                )
                |> deploy storage

            let services, sp = build storage app
            let teamId = Guid.NewGuid().ToString "N"
            writeThroughTable sp teamId 21800m

            let prompt = plannedPrompt sp (teamScope teamId) "what was acme revenue?"

            Expect.isFalse (prompt.Contains "Verified facts") "the opted-out push door pushes nothing"
            Expect.equal model.Compiles 0 "the factory's provider was never asked to compile"
            Expect.isEmpty factory.Resolved "nor resolved"

            Expect.equal
                (compilerVerdict services)
                ConfigValidation.ValidationResult.Ok
                "a deliberate opt-out raises no startup warning"
        }
    ]

// ── 986.C — the dormant door is visible at startup ────────────────

/// The fact tier on a plain `ServerApp` — no AI tier, no provider.
let private bareFactTier (app: ServerApp) : IServiceCollection =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let services, _ = build storage (FactsCompose.withFactTier app)
    services

let warningTests =
    testList "Phase 986 a compiler-less planner warns at startup" [

        test "the fact tier with no compiler warns, naming the remedy" {
            match compilerVerdict (bareFactTier ServerApp.empty) with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "no question compiler" "it says what is missing"
                Expect.stringContains message "push door" "and which door that leaves dormant"
                Expect.stringContains message "IAIProviderFactory" "and names the AI-tier remedy"
                Expect.stringContains message "FactsCompose.withQuestionCompiler" "and the explicit one"
            | other -> failtestf "expected a Warning, got %A" other
        }

        test "the fact tier inside a RAG app (AI tier composed) does not warn" {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let app =
                ragApp (RecordingFactory(CompilingProvider "{}"))
                |> RagOptions.withFacts FactsCompose.withFactTier
                |> deploy storage

            let services, _ = build storage app

            Expect.equal
                (compilerVerdict services)
                ConfigValidation.ValidationResult.Ok
                "the factory is a compiler source"
        }

        test "an explicit compiler on a bare fact tier silences the warning" {
            let services =
                bareFactTier (
                    ServerApp.empty
                    |> FactsCompose.withQuestionCompiler AnswerPlanner.noQuestionCompiler
                )

            Expect.equal
                (compilerVerdict services)
                ConfigValidation.ValidationResult.Ok
                "a chosen compiler is not a forgotten one"
        }

        test "applying the fact tier twice registers ONE guard (preflight names must stay unique)" {
            let services = bareFactTier (FactsCompose.withFactTier ServerApp.empty)

            match compilerVerdict services with
            | ConfigValidation.ValidationResult.Warning _ -> ()
            | other -> failtestf "expected the single guard's Warning, got %A" other
        }
    ]

// ── The AI and RAG companions reach every base builder ────────────
//
// The wrapper records the two companions used to be could not reach the
// `ServerApp` builders they did not mirror, and a builder applied before
// the companion was the only one it saw. As companions they are composed
// when the deployment resolves, so a builder applied AFTER `withAI` /
// `withRAG` reaches both the resolved app and the tier composed onto it.

/// A team deployment, so the AI tier's budget schema is composed, over two
/// replicas.
let private teamConfig: ServerConfig = {
    config with
        Surfaces = Surfaces.team
        MultiInstance = {
            config.MultiInstance with
                ReplicaCount = 2
        }
}

/// Whether the resolved configuration carries the AI tier's budget schema,
/// which the tier adds only when the configuration it reads is team-scoped.
let private hasBudgetSchema (app: ServerApp) =
    (ServerApp.effectiveConfig app).ModuleConfigs
    |> List.exists (fun e -> e.ModuleKey = ToolUp.AI.AIBudgetConfigKey.value)

let reachabilityTests =
    testList "the AI and RAG companions reach the base builders applied after them" [

        test "builders applied after withAI reach the resolved app and the AI tier" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let logger = ConsoleLogger.ConsoleLogger() :> ILogger

            let app =
                ServerApp.empty
                |> ToolUp.AI.AICompose.withAI (
                    ToolUp.AI.AICompose.AIOptions.create (RecordingFactory(CompilingProvider "{}")) stubProfile
                )
                |> ServerApp.withConfig teamConfig
                |> ServerApp.withStorage storage
                |> ServerApp.withLogger logger

            let resolved = ServerApp.resolve app

            Expect.contains resolved.ComposedCompanions "ToolUp.AI" "the AI tier is composed"

            Expect.equal
                (ServerApp.effectiveConfig app).MultiInstance.ReplicaCount
                2
                "the later withConfig reaches the app"

            Expect.isTrue (obj.ReferenceEquals(resolved.Storage.Value, storage)) "and the later withStorage"
            Expect.isTrue (obj.ReferenceEquals(resolved.Logger.Value, logger)) "and the later withLogger"
            Expect.isTrue (hasBudgetSchema app) "the AI tier read the final, team-scoped configuration"
        }

        test "builders applied after withRAG reach the resolved app and both tiers" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let logger = ConsoleLogger.ConsoleLogger() :> ILogger

            let app =
                ServerApp.empty
                |> withRAG (ragApp (RecordingFactory(CompilingProvider "{}")))
                |> ServerApp.withConfig teamConfig
                |> ServerApp.withStorage storage
                |> ServerApp.withLogger logger

            let resolved = ServerApp.resolve app

            Expect.equal
                (resolved.ComposedCompanions |> List.filter ((=) "ToolUp.AI"))
                [ "ToolUp.AI" ]
                "RAG composes the AI tier, once"

            Expect.isTrue (obj.ReferenceEquals(resolved.Storage.Value, storage)) "the later withStorage reaches the app"
            Expect.isTrue (obj.ReferenceEquals(resolved.Logger.Value, logger)) "and the later withLogger"
            Expect.isTrue (hasBudgetSchema app) "the AI tier RAG composed read the final configuration"

            Expect.exists
                resolved.ConfigValidators
                (fun v -> v.Name = "rag-in-process-index-replicas")
                "the RAG tier is composed onto the same app"
        }

        test "withRAG supersedes an earlier withAI and refuses a later one, so the AI tier is never composed twice" {
            let aiOptions =
                ToolUp.AI.AICompose.AIOptions.create (RecordingFactory(CompilingProvider "{}")) stubProfile

            let rag = ragApp (RecordingFactory(CompilingProvider "{}"))

            let earlier =
                ServerApp.empty
                |> ToolUp.AI.AICompose.withAI aiOptions
                |> withRAG rag
                |> ServerApp.resolve

            Expect.equal
                (earlier.ComposedCompanions |> List.filter ((=) "ToolUp.AI"))
                [ "ToolUp.AI" ]
                "an earlier withAI is superseded by the AI tier RAG composes"

            Expect.throwsC
                (fun () ->
                    ServerApp.empty
                    |> withRAG rag
                    |> ToolUp.AI.AICompose.withAI aiOptions
                    |> ServerApp.resolve
                    |> ignore)
                (fun ex ->
                    Expect.stringContains
                        ex.Message
                        "ToolUp.AI: companion already composed"
                        "a later withAI would compose the AI tier a second time, and is refused when the app resolves")
        }
    ]

let tests =
    testList "Phase 986 RAG fact tier" [ compositionTests; pushDoorTests; warningTests; reachabilityTests ]