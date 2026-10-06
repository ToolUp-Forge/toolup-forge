// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ReportSubscriptionScopeTests

// ─── Phase 990.A / 990.B — a report subscription runs under the scope that created it ─
//
// A subscription's job is scheduled when the subscription is saved and runs
// later, with no request in sight. Phase 985's grounded producer reads Facts
// through the request-path fact tools, which take only a scope the platform
// resolved (Phase 797), so a job that runs under the anonymous scope can
// produce nothing grounded. Phase 935's carrier lets a typed `Schedule` carry
// a resolved scope into the run; this pack proves the subscription API uses
// it, on the SHIPPED in-process scheduler (the carrier really seals and
// redeems — no double stands in for it):
//
//   A. a subscription created under a resolved team scope runs under THAT
//      scope; one created under the anonymous scope stays anonymous; a
//      resolved scope naming another shard is never carried onto this one;
//      and a job whose scope can no longer be re-minted fails closed and its
//      last-run outcome says why;
//   B. end to end on a composed RAG app with the fact tier: the scheduled
//      grounded report reads its team's Fact, is gated, published, certified
//      and delivered under that team's scope — and the same job wiring under
//      another team's scope reads none of it.

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Narrative
open ToolUp.Platform.Secrets
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.Tracing
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.GroundedNarrativeRun
open ToolUp.ArtefactSigning
open ToolUp.Facts
open ToolUp.RAG
open ToolUp.RAG.RAGCompose
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.JobSchedulers
open ToolUp.Platform.JobSchedulers.QuartzScheduler

// ── The subscription substrate, over the shipped scheduler ────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }
    }

let private templateId = "scheduled-report"

let private template: ReportTemplate = {
    Id = templateId
    DisplayName = "Scheduled report"
    Format = Markdown
    Body = Encoding.UTF8.GetBytes "# Scheduled report\n\n{{body}}\n"
    Placeholders = [
        {
            Key = "body"
            DisplayName = "Body"
            Kind = Text
            Required = true
        }
    ]
    Version = 1
}

/// Every scope has the one template.
let private templates =
    { new IReportTemplateStore with
        member _.List _ = async.Return [ template ]

        member _.Get(_, id) =
            async.Return(if id = templateId then Some template else None)

        member _.Save(_, _, t) = async.Return(Ok t)
        member _.Delete(_, _, _) = async.Return(Ok())
    }

/// Everyone has an address at every scope.
let private addressBook =
    { new INotificationAddressBook with
        member _.ResolveEmail(recipient, _) = async {
            return
                Some {
                    Address = $"{RecipientId.toAuditString recipient}@example.test"
                    DisplayName = None
                }
        }

        member _.ResolvePhone(_, _) = async { return None }
        member _.ResolvePushTokens(_, _) = async { return [] }
        member _.ResolveWhatsApp(_, _) = async { return None }
    }

/// A sink that records the scope of every delivery.
type private RecordingSink() =
    let sent = ConcurrentQueue<string * NotificationEnvelope>()
    member _.Sent = List.ofSeq sent

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.Email
        member _.Provider = "Recording"

        member _.Send(scopeId, envelope) = async {
            sent.Enqueue((scopeId, envelope))
            return SinkResult.Delivered(Some "test-message-id")
        }

let private retryPolicy = {
    JobRetryPolicy.defaults with
        MaxAttempts = 1
}

/// The subscription substrate as a composition root wires it: the shipped
/// store, artefact store, job handler and API handler, over the shipped
/// in-process scheduler with the subscription job handler registered.
type private World(producers: ReportProducerRegistry) =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let scheduler =
        JobScheduler.create
            (JobStore.create blobs events)
            events
            silentChannel
            ServerConfig.defaults
            silentLogger
            (NoOpActivitySink() :> IActivitySink)

    let subscriptions = ReportSubscriptionStore.create blobs
    let sink = RecordingSink()

    let jobDeps: ReportSubscriptionJobDeps = {
        Subscriptions = subscriptions
        Producers = producers
        Templates = templates
        Renderers = ReportingCompose.buildDefaultRegistry ()
        Artefacts = DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore
        AddressBook = addressBook
        Sink = sink
        Audit = fun _ -> async { return () }
        Config = ReportApiConfig.defaults
        RetryPolicy = retryPolicy
        Disclosure = None
    }

    let apiDeps: ReportSubscriptionApiHandler.ReportSubscriptionApiDeps = {
        Subscriptions = subscriptions
        Producers = producers
        Scheduler = scheduler
        RetryPolicy = retryPolicy
        Precision = JobPrecision.Minute
    }

    do
        (scheduler :> IJobScheduler)
            .RegisterHandler(ReportSubscription.JobHandlerName, ReportSubscriptionJobHandler.create jobDeps)

    member _.Scheduler = scheduler
    member _.Subscriptions = subscriptions
    member _.Sink = sink

    /// The API a request resolved to `scope` manages `scopeId` through.
    member _.ApiUnder (scope: ResolvedScope) (scopeId: string) =
        ReportSubscriptionApiHandler.createUnder apiDeps "operator" scope scopeId

    /// The API built without the request's scope.
    member _.ApiWithoutScope(scopeId: string) =
        ReportSubscriptionApiHandler.create apiDeps "operator" scopeId

    /// Phase 991 — the API factory the default composition hands out,
    /// `ReportingCompose.withReportSubscriptions`, for a request resolved to
    /// `scope` managing `scopeId`. Nothing here opts in to carrying a scope.
    member _.ApiComposed (scope: ResolvedScope) (scopeId: string) =
        let _, factory = ReportingCompose.withReportSubscriptions [] jobDeps apiDeps
        factory "operator" scope scopeId

let private storageFor (teamId: string) : StorageScope = {
    ScopeId = teamId
    Container = $"team-{teamId}"
    Persist = true
}

let private newTeam () = "team-" + Guid.NewGuid().ToString "N"

let private subscriptionTo (producerKey: string) : NewReportSubscription = {
    DisplayName = "Monday report"
    ProducerKey = producerKey
    Parameters = Map.empty
    Schedule = "0 6 * * 1"
    RecipientUserIds = [ "alice" ]
    Format = Markdown
    Enabled = true
}

/// Create a subscription through `api`, fire it now, and wait for its run
/// to be recorded. Bounded, so a run that never happens fails the test.
let private createAndRun (world: World) (api: IReportSubscriptionApi) (scopeId: string) (producerKey: string) =
    let subscription =
        match api.CreateSubscription(subscriptionTo producerKey) |> Async.RunSynchronously with
        | Ok s -> s
        | Error e -> failtestf "create failed: %A" e

    match api.RunSubscriptionNow subscription.Id |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "run-now failed: %A" e

    let deadline = DateTime.UtcNow.AddSeconds 60.0
    let mutable outcome = NeverRun

    while outcome = NeverRun && DateTime.UtcNow < deadline do
        match world.Subscriptions.Get(scopeId, subscription.Id) |> Async.RunSynchronously with
        | Some current when current.LastRun <> NeverRun -> outcome <- current.LastRun
        | _ -> Thread.Sleep 25

    if outcome = NeverRun then
        failtest "the subscription's run was never recorded"

    outcome

/// A producer that records the scope its resolve ran under. It needs no
/// scope to answer, so it shows what the job carried and nothing else.
let private probe (seen: ConcurrentQueue<ResolvedScope option>) =
    ReportProducer.create "test.scope-probe" "Scope probe" [] (fun _ _ -> async {
        seen.Enqueue(ReportProducerScope.get ())

        return
            Ok {
                TemplateId = templateId
                Values = Map [ "body", TextValue "probe" ]
                FileNameStem = None
            }
    })

/// A producer that, like the grounded one, refuses without a resolved scope.
let private needsScope (seen: ConcurrentQueue<ResolvedScope option>) =
    ReportProducer.create "test.needs-scope" "Needs a resolved scope" [] (fun _ _ -> async {
        let scope = ReportProducerScope.get ()
        seen.Enqueue scope

        match scope with
        | Some s when not s.IsAnonymous ->
            return
                Ok {
                    TemplateId = templateId
                    Values = Map [ "body", TextValue "scoped" ]
                    FileNameStem = None
                }
        | _ -> return Error "a resolved scope is required"
    })

let private worldWith (producer: ReportProducer) =
    let producers = ReportProducerRegistry()
    producers.Register producer |> ignore
    World producers

let private expectSucceeded (outcome: SubscriptionRunOutcome) =
    match outcome with
    | RunSucceeded _ -> ()
    | other -> failtestf "expected the run to succeed, got %A" other

let carriedScopeTests =
    testList "Phase 990.A — a subscription's job runs under the scope that created it" [

        test "a subscription created under a resolved team scope runs under that scope" {
            let seen = ConcurrentQueue()
            let world = worldWith (probe seen)
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)

            createAndRun world (world.ApiUnder scope team) team "test.scope-probe"
            |> expectSucceeded

            Expect.equal (List.ofSeq seen) [ Some scope ] "the producer resolved under the creating request's scope"

            Expect.equal (world.Sink.Sent |> List.map fst) [ team ] "and the report was delivered at that scope"
        }

        test "a subscription created under the anonymous scope stays anonymous — whichever constructor built the API" {
            let seen = ConcurrentQueue()
            let world = worldWith (probe seen)
            let team = newTeam ()

            createAndRun world (world.ApiUnder ResolvedScope.anonymous team) team "test.scope-probe"
            |> expectSucceeded

            createAndRun world (world.ApiWithoutScope team) team "test.scope-probe"
            |> expectSucceeded

            Expect.equal
                (List.ofSeq seen)
                [ Some ResolvedScope.anonymous; Some ResolvedScope.anonymous ]
                "both runs resolved under the anonymous scope — never a widening"
        }

        test "a resolved scope naming another shard is never carried onto this one" {
            let seen = ConcurrentQueue()
            let world = worldWith (probe seen)
            let stored = newTeam ()
            let elsewhere = ScopeResolution.ofStorageScope (storageFor (newTeam ()))

            createAndRun world (world.ApiUnder elsewhere stored) stored "test.scope-probe"
            |> expectSucceeded

            Expect.equal
                (List.ofSeq seen)
                [ Some ResolvedScope.anonymous ]
                "a subscription stored in one shard is never scheduled under another's scope"
        }

        test "a job whose scope can no longer be re-minted fails closed, and its outcome says why" {
            let seen = ConcurrentQueue()
            let world = worldWith (needsScope seen)
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)

            let api = world.ApiUnder scope team

            let subscription =
                match
                    api.CreateSubscription(subscriptionTo "test.needs-scope")
                    |> Async.RunSynchronously
                with
                | Ok s -> s
                | Error e -> failtestf "create failed: %A" e

            // The token was sealed by the scheduler's own carrier. A carrier
            // over another key ring — a deployment whose ring changed, or a
            // restart with nothing bound — no longer redeems it.
            (world.Scheduler :> IScopeCarrierBinding).BindScopeCarrier(ScopeCarrier.ephemeral ())

            match api.RunSubscriptionNow subscription.Id |> Async.RunSynchronously with
            | Ok() -> ()
            | Error e -> failtestf "run-now failed: %A" e

            let deadline = DateTime.UtcNow.AddSeconds 60.0
            let mutable outcome = NeverRun

            while outcome = NeverRun && DateTime.UtcNow < deadline do
                match world.Subscriptions.Get(team, subscription.Id) |> Async.RunSynchronously with
                | Some current when current.LastRun <> NeverRun -> outcome <- current.LastRun
                | _ -> Thread.Sleep 25

            Expect.equal (List.ofSeq seen) [ Some ResolvedScope.anonymous ] "the run fell to the anonymous scope"

            match outcome with
            | RunFailed(_, reason, terminal) ->
                Expect.stringContains reason "a resolved scope is required" "the producer refused"

                Expect.stringContains
                    reason
                    ReportSubscriptionJobHandler.ScopeNotReMinted
                    "and the outcome says the scope did not re-mint"

                Expect.isTrue terminal "a refused token does not heal on retry"
            | other -> failtestf "expected a failed run, got %A" other

            // The recipients are told the report did not arrive (the
            // terminal-failure warning); the report itself is never sent.
            Expect.all
                world.Sink.Sent
                (fun (_, envelope) ->
                    match envelope.Notification with
                    | TransactionalEmail email ->
                        email.CorrelationId |> Option.exists _.StartsWith("report-subscription-failed:")
                    | _ -> false)
                "nothing but the failure warning was sent"
        }
    ]

// ── B. The scheduled grounded report, end to end ──────────────────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys scopeId = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private revenue (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "brand"
        Path = [ "acme" ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = september
    Method = HumanAsserted "analyst"
    Evidence = {
        ResultRef = None
        InputHashes = []
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

let private metrics: IMetricRegistry =
    MetricRegistry.build [
        {
            MetricRegistration.Module = "sales"
            Definition = {
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
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = {
                Id = "brand"
                Name = "Brand"
                Levels = [ "brand" ]
                Calendar = None
            }
        }
    ]

let private answer (paragraph: string) =
    JsonSerializer.Serialize {|
        sections = [
            {|
                id = "summary"
                paragraphs = [ paragraph ]
            |}
        ]
    |}

/// The model: one `query_facts` call, then a paragraph citing the Fact it
/// read — or, when it read none, a paragraph with no figure in it. It
/// records every `query_facts` result, which is what scope isolation is
/// judged by.
type private NarratingModel() =
    let results = ConcurrentQueue<string>()
    member _.ToolResults = List.ofSeq results

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted-narrator"
            Model = "scripted-narrator-model"
        }

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            match messages |> List.collect _.ToolResults |> List.tryLast with
            | None ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "query_facts"
                                Arguments = """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | Some result ->
                results.Enqueue result.Content

                let paragraph =
                    match (JsonDocument.Parse result.Content).RootElement.TryGetProperty "facts" with
                    | true, facts when facts.GetArrayLength() > 0 ->
                        let fact = facts.EnumerateArray() |> Seq.head
                        let id = fact.GetProperty("factId").GetString()
                        let rendering = fact.GetProperty("rendering").GetString()
                        $"Acme revenue was [[Revenue|{rendering}|{id}]] in September."
                    | _ -> "No revenue figures are recorded for Acme this month."

                return
                    Ok {
                        Content = answer paragraph
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

type private ScriptedFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Error NoProviderConfigured }
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

let private summaryRun: GroundedNarrativeDefinition =
    GroundedNarrativeDefinition.create
        "monthly-brand-summary"
        "Monthly brand summary"
        "sales"
        "narrator"
        "Monthly brand summary"
        "Summarise the month's brand revenue."
        [
            {
                Id = "summary"
                Heading = "Summary"
                Guidance = "One paragraph on the month's revenue."
            }
        ]
    |> GroundedNarrativeDefinition.dependingOn [ "revenue" ]

let private groundedKey = "grounded.monthly-brand-summary"

type private Deployment = {
    Model: NarratingModel
    Store: IFactStore
    Narratives: INarrativeStore
    Events: IEventStore
    World: World
}

/// A RAG app with the fact tier and grounded narratives composed, booted,
/// and the subscription substrate wired over it with the grounded report
/// registered as a producer.
let private deploy () : Deployment =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let model = NarratingModel()

    let app =
        RAGServerApp.create (ScriptedFactory(model)) stubProfile constantEmbedder
        |> RAGServerApp.withStorage storage
        |> RAGServerApp.withFacts FactsCompose.withFactTier
        |> composeRAG
        |> GroundedNarratives.compose (GroundedNarrativeRunOptions.create NarrativeGrounding.factTools) [ summaryRun ]

    let services = ServiceCollection() :> IServiceCollection
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let narratives = InMemoryNarrativeStore() :> INarrativeStore
    let secrets = InMemorySecretStore() :> ISecretStore

    let narrativeIngestor =
        { new INarrativeIngestor with
            member _.Ingest(_, _, _) = async { return NarrativeIngested "kb-1" }
        }

    // The data-arrival trigger registers its handler on the composed
    // scheduler at boot; this pack does not exercise it.
    let bootScheduler =
        JobScheduler.create
            (JobStore.create storage events)
            events
            silentChannel
            ServerConfig.defaults
            silentLogger
            (NoOpActivitySink() :> IActivitySink)

    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IEventStore>(events) |> ignore
    services.AddSingleton<ILogger>(silentLogger) |> ignore
    services.AddSingleton<IMetricRegistry>(metrics) |> ignore
    services.AddSingleton<INarrativeStore>(narratives) |> ignore
    services.AddSingleton<INarrativeIngestor>(narrativeIngestor) |> ignore
    services.AddSingleton<IJobScheduler>(bootScheduler) |> ignore
    services.AddHttpContextAccessor() |> ignore

    services.AddSingleton<IFactTableRegistry>(
        FactTableRegistry.build [] [ BindAllFactTables DefaultFactTableWriter.Destination ]
    )
    |> ignore

    services.AddSingleton<IArtefactSigner>(
        DefaultArtefactSigner.createSystem secrets (AuditLog.NoOpAuditLog() :> IAuditLog) "grounding-v1" Ed25519
    )
    |> ignore

    services.AddSingleton<IDataObjectStore>(DataObjectStore.DataObjectStore(storage, silentLogger) :> IDataObjectStore)
    |> ignore

    let services =
        match app.Extensions.ServiceConfig with
        | Some configure -> configure services
        | None -> services

    let sp = services.BuildServiceProvider()

    for hosted in sp.GetServices<IHostedService>() do
        hosted.StartAsync(CancellationToken.None)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    let producers = ReportProducerRegistry()

    producers.Register(
        GroundedNarrativeProducer.create
            groundedKey
            "Monthly brand summary"
            (sp.GetRequiredService<IGroundedNarrativeRun>())
            summaryRun.Key
            templateId
            "body"
    )
    |> ignore

    {
        Model = model
        Store = sp.GetRequiredService<IFactStore>()
        Narratives = narratives
        Events = events
        World = World producers
    }

let private assertOk (store: IFactStore) (scopeId: string) (draft: FactDraft) : Fact =
    match store.Assert(scopeId, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// The fact ids a `query_facts` result returned.
let private factIdsIn (result: string) : string list =
    match (JsonDocument.Parse result).RootElement.TryGetProperty "facts" with
    | true, facts ->
        facts.EnumerateArray()
        |> Seq.map (fun fact -> fact.GetProperty("factId").GetString())
        |> List.ofSeq
    | _ -> []

/// A member of a grounded-run audit row's payload, by name whatever its case.
let private member' (payload: string) (name: string) : JsonElement option =
    (JsonDocument.Parse payload).RootElement.EnumerateObject()
    |> Seq.tryFind (fun p -> String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
    |> Option.map _.Value.Clone()

let private narrativeRows (deployment: Deployment) (scopeId: string) : ModuleEvent list =
    deployment.Events.ReadBySource(scopeId, GroundedNarrativeEvents.SourceModule)
    |> Async.RunSynchronously

let groundedReportTests =
    testList "Phase 990.B — the scheduled grounded report runs under its team's scope" [

        test
            "created under a team scope, the scheduled report reads that team's Fact, and is gated, published, certified and delivered there" {
            let deployment = deploy ()
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)
            let fact = assertOk deployment.Store team (revenue 1250m)

            createAndRun deployment.World (deployment.World.ApiUnder scope team) team groundedKey
            |> expectSucceeded

            Expect.equal
                (deployment.Model.ToolResults |> List.map factIdsIn)
                [ [ fact.FactId ] ]
                "the run's query_facts read the team's Fact"

            match narrativeRows deployment team with
            | [ row ] ->
                Expect.equal row.EventType GroundedNarrativeEvents.PublishedType "the narrative passed the gate"

                Expect.equal
                    (member' row.Payload "Trigger" |> Option.map _.GetString())
                    (Some GroundedNarrativeProducer.TriggerName)
                    "recorded as a scheduled run"

                Expect.equal
                    (member' row.Payload "Citations"
                     |> Option.map (fun c -> c.EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq))
                    (Some [ fact.FactId ])
                    "citing the Fact it read"

                match member' row.Payload "CertificateDigest" with
                | Some digest when digest.ValueKind = JsonValueKind.String ->
                    Expect.isNotEmpty (digest.GetString()) "and certified"
                | other -> failtestf "expected a certificate digest, got %A" other
            | other -> failtestf "expected one grounded-run row at the team's scope, got %A" other

            Expect.equal
                (deployment.Narratives.List(team, 50) |> Async.RunSynchronously |> List.length)
                1
                "published to the team's narrative store"

            Expect.equal (deployment.World.Sink.Sent |> List.map fst) [ team ] "and delivered at the team's scope"
        }

        test "the same job under another team's scope reads none of the first team's Facts" {
            let deployment = deploy ()
            let owner = newTeam ()
            let other = newTeam ()
            let theirs = assertOk deployment.Store owner (revenue 1250m)
            let otherScope = ScopeResolution.ofStorageScope (storageFor other)

            createAndRun deployment.World (deployment.World.ApiUnder otherScope other) other groundedKey
            |> expectSucceeded

            match deployment.Model.ToolResults with
            | [ result ] ->
                Expect.isFalse
                    (factIdsIn result |> List.contains theirs.FactId)
                    "the other team's run never read the owner's Fact"

                Expect.isEmpty (factIdsIn result) "it read nothing at all — its own shard holds no Fact"
            | results -> failtestf "expected exactly one query_facts result, got %A" results

            match narrativeRows deployment other with
            | [ row ] ->
                Expect.equal
                    (member' row.Payload "Citations" |> Option.map _.GetArrayLength())
                    (Some 0)
                    "its narrative cites nothing"
            | rows -> failtestf "expected one grounded-run row at the other team's scope, got %A" rows

            Expect.isEmpty (narrativeRows deployment owner) "and nothing was written at the owner's scope"

            Expect.isEmpty (deployment.Narratives.List(owner, 50) |> Async.RunSynchronously) "or published there"
        }
    ]

// ── Phase 991 — scoped by default, and an existing job re-stamped ──
//
// 990 made a subscription carry its creator's scope only when the
// composition root chose `createUnder`; the default composition still handed
// out the anonymous `create`, and a job found again by its idempotency key
// kept the token it was first issued, so a subscription saved before 990 ran
// anonymous until it was deleted and re-created. These cases prove the
// default composition carries the scope with no opt-in, that a save re-stamps
// a pre-990 job IN PLACE (same job, no re-create) through the scheduler's
// re-issue verb, and that the verb refuses any scope that does not own the
// job's shard — on the in-process scheduler and on the Quartz companion.

/// Fire subscription `id` now through `api` and wait for a run newer than
/// the one already recorded. Bounded, so a run that never happens fails.
let private runAgain (world: World) (api: IReportSubscriptionApi) (scopeId: string) (id: SubscriptionId) =
    let lastRunOf () =
        match world.Subscriptions.Get(scopeId, id) |> Async.RunSynchronously with
        | Some current -> current.LastRun
        | None -> failtest "the subscription disappeared"

    let before = lastRunOf ()

    match api.RunSubscriptionNow id |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "run-now failed: %A" e

    let deadline = DateTime.UtcNow.AddSeconds 60.0
    let mutable outcome = before

    while outcome = before && DateTime.UtcNow < deadline do
        outcome <- lastRunOf ()

        if outcome = before then
            Thread.Sleep 25

    if outcome = before then
        failtest "the subscription's run was never recorded"

    outcome

/// The scheduler jobs backing subscription `id` at `scopeId`.
let private jobsFor (scheduler: IJobScheduler) (scopeId: string) (id: SubscriptionId) =
    scheduler.ListJobs scopeId
    |> Async.RunSynchronously
    |> List.filter (fun job -> job.Tags.TryFind "subscriptionId" = Some id)

let private createVia (api: IReportSubscriptionApi) (producerKey: string) =
    match api.CreateSubscription(subscriptionTo producerKey) |> Async.RunSynchronously with
    | Ok s -> s
    | Error e -> failtestf "create failed: %A" e

let private resave (api: IReportSubscriptionApi) (id: SubscriptionId) (producerKey: string) =
    match api.UpdateSubscription(id, subscriptionTo producerKey) |> Async.RunSynchronously with
    | Ok _ -> ()
    | Error e -> failtestf "re-save failed: %A" e

let defaultCompositionTests =
    testList "Phase 991.A / 991.C — the default composition carries the creator's scope, and a save re-stamps" [

        test "a subscription created through the default composition runs under its creator's scope" {
            let seen = ConcurrentQueue()
            let world = worldWith (probe seen)
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)

            createAndRun world (world.ApiComposed scope team) team "test.scope-probe"
            |> expectSucceeded

            Expect.equal (List.ofSeq seen) [ Some scope ] "no opt-in: the composed factory carried the request's scope"
        }

        test "a pre-990 (anonymous-token) job is re-stamped in place by a save, and then runs under its team's scope" {
            let seen = ConcurrentQueue()
            let world = worldWith (needsScope seen)
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)

            // Before 990 every subscription was scheduled through the string
            // overload — exactly what the anonymous `create` still does.
            let legacy = world.ApiWithoutScope team
            let subscription = createVia legacy "test.needs-scope"

            match runAgain world legacy team subscription.Id with
            | RunFailed(_, reason, _) ->
                Expect.stringContains reason ReportSubscriptionJobHandler.ScopeNotReMinted "it ran anonymous"
            | other -> failtestf "expected the anonymous job to refuse, got %A" other

            let before = jobsFor world.Scheduler team subscription.Id

            Expect.isFalse
                (before |> List.exists (fun job -> job.Tags.ContainsKey CarriedJobScope.TokenTag))
                "the legacy job carries no scope token"

            // The owner re-saves it, unchanged, through the default composition.
            let composed = world.ApiComposed scope team
            resave composed subscription.Id "test.needs-scope"

            let after = jobsFor world.Scheduler team subscription.Id

            Expect.equal
                (after |> List.map _.JobId)
                (before |> List.map _.JobId)
                "the same job, re-stamped in place — nothing was re-created"

            runAgain world composed team subscription.Id |> expectSucceeded

            Expect.equal (List.ofSeq seen |> List.last) (Some scope) "its next run resolved under the team's scope"
        }

        test "re-saving through the anonymous opt-out never re-stamps" {
            let seen = ConcurrentQueue()
            let world = worldWith (probe seen)
            let team = newTeam ()

            let legacy = world.ApiWithoutScope team
            let subscription = createVia legacy "test.scope-probe"
            resave legacy subscription.Id "test.scope-probe"

            Expect.isFalse
                (jobsFor world.Scheduler team subscription.Id
                 |> List.exists (fun job -> job.Tags.ContainsKey CarriedJobScope.TokenTag))
                "a save under no resolved scope carries none onto the job"
        }
    ]

let groundedRestampTests =
    testList "Phase 991.D — the scheduled grounded report, by default and after a re-save" [

        test "composed with only withReportSubscriptions, the scheduled grounded report reads its creator's team's Fact" {
            let deployment = deploy ()
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)
            let fact = assertOk deployment.Store team (revenue 1250m)

            createAndRun deployment.World (deployment.World.ApiComposed scope team) team groundedKey
            |> expectSucceeded

            Expect.equal
                (deployment.Model.ToolResults |> List.map factIdsIn)
                [ [ fact.FactId ] ]
                "the default composition's run read the team's Fact"

            Expect.equal (deployment.World.Sink.Sent |> List.map fst) [ team ] "and delivered it at the team's scope"
        }

        test "a pre-990 grounded subscription refuses, is re-saved, and then reads its team's Fact" {
            let deployment = deploy ()
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)
            let fact = assertOk deployment.Store team (revenue 1250m)

            let legacy = deployment.World.ApiWithoutScope team
            let subscription = createVia legacy groundedKey

            match runAgain deployment.World legacy team subscription.Id with
            | RunFailed(_, reason, _) ->
                Expect.stringContains
                    reason
                    ReportSubscriptionJobHandler.ScopeNotReMinted
                    "the anonymous job refused, as every pre-990 grounded subscription did"
            | other -> failtestf "expected the anonymous grounded run to refuse, got %A" other

            let composed = deployment.World.ApiComposed scope team
            resave composed subscription.Id groundedKey

            runAgain deployment.World composed team subscription.Id |> expectSucceeded

            Expect.equal
                (deployment.Model.ToolResults |> List.map factIdsIn |> List.tryLast)
                (Some [ fact.FactId ])
                "after the save its run read the team's Fact"

            Expect.equal
                (narrativeRows deployment team
                 |> List.filter (fun row -> row.EventType = GroundedNarrativeEvents.PublishedType)
                 |> List.length)
                1
                "and published one grounded narrative at the team's scope"
        }
    ]

// ── Phase 991.B — the re-issue verb, bound to its contract pack ────
//
// `IJobScopeReissueContract` holds what any re-issuing scheduler must do
// (re-stamp in place for the job's own shard; refuse a cross-shard or
// anonymous scope and leave the job untouched; report an unknown job). It is
// bound here three times: the in-process scheduler, the Quartz companion, and
// the quota decorator over the in-process scheduler, which must forward it.

let private inProcessScheduler () =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    JobScheduler.create
        (JobStore.create blobs events)
        events
        silentChannel
        ServerConfig.defaults
        silentLogger
        (NoOpActivitySink() :> IActivitySink)

let private bindInto (scheduler: IJobScheduler) (carrier: ScopeCarrier) =
    (scheduler :?> IScopeCarrierBinding).BindScopeCarrier carrier

let private inProcessSubject () : IJobScopeReissueContract.ReissueSubject =
    let scheduler = inProcessScheduler () :> IJobScheduler

    {
        Scheduler = scheduler
        Bind = bindInto scheduler
        Dispose = ignore
    }

let private quartzSubject () : IJobScopeReissueContract.ReissueSubject =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let quartzConfig = {
        QuartzConfig.defaults with
            SchedulerName = "toolup-reissue-" + Guid.NewGuid().ToString "N"
            StartScheduler = false
    }

    let companion =
        QuartzJobScheduler.create
            (JobStore.create blobs events)
            silentChannel
            ServerConfig.defaults
            quartzConfig
            silentLogger
        |> Async.RunSynchronously

    {
        Scheduler = companion :> IJobScheduler
        Bind = bindInto companion
        Dispose =
            fun () ->
                (companion.QuartzScheduler.Shutdown false).AsTask()
                |> Async.AwaitTask
                |> Async.RunSynchronously
    }

let private quotaGatedSubject () : IJobScopeReissueContract.ReissueSubject =
    let inner = inProcessScheduler () :> IJobScheduler

    {
        Scheduler =
            TeamQuotaPolicy.QuotaGatedJobScheduler(inner, ToolUp.Platform.Usage.NoOpTeamQuotaPolicy()) :> IJobScheduler
        Bind = bindInto inner
        Dispose = ignore
    }

let reissueTests =
    testList "Phase 991.B — re-issuing a job's carried scope" [
        IJobScopeReissueContract.tests "InProcessJobScheduler" inProcessSubject
        IJobScopeReissueContract.tests "QuartzJobScheduler" quartzSubject
        IJobScopeReissueContract.tests "QuotaGatedJobScheduler over InProcessJobScheduler" quotaGatedSubject

        test "a scheduler that does not re-issue answers Unsupported, never a silent success" {
            let team = newTeam ()
            let scope = ScopeResolution.ofStorageScope (storageFor team)

            let bare =
                { new IJobScheduler with
                    member _.RegisterHandler(_, _) = ()
                    member _.RegisterHandlerAsync(_, _) = async { return Ok() }
                    member _.Schedule(_: JobRegistration) = async { return Ok(Guid.NewGuid()) }

                    member _.Schedule(_: ResolvedScope, _: JobRegistration) = async { return Ok(Guid.NewGuid()) }

                    member _.Cancel(_, _) = async { return () }
                    member _.Disable(_, _) = async { return () }
                    member _.Enable(_, _) = async { return () }
                    member _.Get(_, _) = async { return None }
                    member _.ListJobs _ = async { return [] }
                    member _.GetRecentRuns(_, _, _) = async { return [] }
                    member _.TriggerOnce(_, _, _) = async { return Ok() }
                    member _.NotifyEventWritten(_, _, _) = async { return () }
                }

            Expect.equal
                (JobScopeReissue.reissue bare scope team (Guid.NewGuid())
                 |> Async.RunSynchronously)
                (Error ScopeReissueError.Unsupported)
                "Unsupported"
        }
    ]

let tests =
    testList "Phase 990 — report subscriptions run under the scope that created them" [
        carriedScopeTests
        groundedReportTests
        defaultCompositionTests
        groundedRestampTests
        reissueTests
    ]