// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AggregateMetricToolTests

// ─── `aggregate_metric` ─────────────────────────────────────────────
//
// A roll-up the answer verifier can check, and the fact tools' results in the
// verifier's scope. Covered:
//
//   1. The declaration, and its place beside the other fact tools.
//   2. The executor: a two-site total citing its inputs; each refusal by
//      name (non-additive, undeclared roll-up, unknown level, period
//      disagreement, competing values, a member with no single value); a
//      group with a withheld member reported withheld, never summed; a group
//      whose every member is withheld counted and never named.
//   3. Tool results in the verifier's scope: a figure quoted from a
//      `query_metric_population` result verifies, a withheld member's value
//      does not, and a total quoted from an `aggregate_metric` result
//      verifies against the facts it names.
//   4. End to end: a chat turn whose model calls `aggregate_metric` and
//      quotes the total keeps it under `Strict`, and its verdict names the
//      two facts.

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open ToolUp.AI.AnswerVerifier
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Fixtures ────────────────────────────────────────────────────────

let private july: TemporalExtent = {
    From = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-07"
}

let private august: TemporalExtent = {
    From = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-08"
}

let private metric (id: string) (format: string) (rollUp: RollUp option) : MetricRegistration = {
    Module = "TestModule"
    Definition = {
        Id = id
        Name = id
        Unit = "GBP"
        Dimensionality = "currency"
        Direction = HigherIsBetter
        DisplayFormat = format
        Staleness = UntilSuperseded
        ProducingOperation = None
        CanonicalMethod = None
        RecomputePolicy = None
        RollUp = rollUp
        Context = None
    }
}

/// Revenue sums (within one unit), a share does not, and units declare
/// nothing. Stores nest categories under sites.
let private registry: IMetricRegistry =
    MetricRegistry.build [
        metric "revenue" "N0" (Some(Additive 1m))
        metric "share" "P1" (Some NonAdditive)
        metric "units" "N0" None
    ] [
        {
            Module = "TestModule"
            Definition = {
                Id = "store"
                Name = "Store"
                Levels = [ "site"; "category" ]
                Calendar = None
            }
        }
    ]

let private draft
    (metricId: string)
    (path: string list)
    (value: FactValue)
    (period: TemporalExtent)
    (disclosure: Disclosure)
    (operation: string)
    : FactDraft =
    {
        Subject = { Hierarchy = "store"; Path = path }
        Metric = MetricRef metricId
        Value = value
        Period = period
        Method = Computed(operation, "1", "p0")
        Evidence = {
            ResultRef = None
            InputHashes = [ String.concat "/" path ]
            TriggerRef = None
        }
        Confidence = None
        Disclosure = disclosure
    }

let private assertFact (store: IFactStore) (scope: string) (d: FactDraft) : Fact =
    match store.Assert(TestScope.resolved scope, d) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" (FactStoreError.describe e)

/// One July revenue figure, disclosable.
let private revenue (store: IFactStore) (scope: string) (path: string list) (value: decimal) : Fact =
    assertFact store scope (draft "revenue" path (Scalar value) july Surfaceable "sales")

let private newScope () =
    "aggregate-" + Guid.NewGuid().ToString("N")

/// The fact tier, composed by its one knob.
let private factsApp () : ServerApp =
    {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    FactStore = EnabledFactStore
            }
    }
    |> FactsCompose.withFactStore

let private composed () : ServerApp * ServiceProvider =
    let app = factsApp ()

    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(registry) |> ignore

    match ServerApp.serviceConfig app with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    app, services.BuildServiceProvider()

/// Run `aggregate_metric` over the composed store, gate and registry.
let private aggregate (sp: IServiceProvider) (scope: string) (argsJson: string) : string =
    AggregateMetricTool.executeWith
        (sp.GetRequiredService<IFactStore>())
        (sp.GetRequiredService<IFactDisclosureGate>())
        (Some registry)
        (TestScope.resolved scope)
        "user-1"
        argsJson
    |> Async.RunSynchronously

let private parse (json: string) : JsonElement =
    (JsonDocument.Parse json).RootElement.Clone()

let private items (el: JsonElement) (name: string) : JsonElement list =
    el.GetProperty(name).EnumerateArray() |> Seq.map _.Clone() |> List.ofSeq

let private str (el: JsonElement) (name: string) : string = el.GetProperty(name).GetString()

let private errorOf (json: string) : string =
    match (parse json).TryGetProperty "error" with
    | true, e -> e.GetString()
    | _ -> failtestf "expected a refusal, got %s" json

let private byCategory =
    """{"metric":"revenue","subject_hierarchy":"store","group_by_level":"category"}"""

/// Two sites, two categories each.
let private seedTwoSites (store: IFactStore) (scope: string) =
    let a = revenue store scope [ "site-a"; "bakery" ] 1200m
    let b = revenue store scope [ "site-b"; "bakery" ] 800m
    revenue store scope [ "site-a"; "dairy" ] 300m |> ignore
    revenue store scope [ "site-b"; "dairy" ] 150m |> ignore
    a, b

// ─── 1. The declaration ──────────────────────────────────────────────

let private declarationTests =
    testList "the declaration" [
        test "server-resident, reads facts, and the three required arguments" {
            let d = AggregateMetricTool.definition
            Expect.equal d.Name "aggregate_metric" "its name"
            Expect.equal d.Location ServerResident "runs on the server"
            Expect.equal d.SourceModule FactEvents.SourceModule "the fact store's reserved source"
            Expect.equal d.Effects ToolEffectDeclaration.readFacts "declares it reads facts"

            Expect.equal
                (d.Parameters |> List.filter _.Required |> List.map _.Name)
                [ "metric"; "subject_hierarchy"; "group_by_level" ]
                "the metric, the hierarchy and the level"
        }

        test "composed beside the other fact tools, and in the grounded narrative's set" {
            let app, _ = composed ()
            let names = app.AITools |> List.map (fun (d, _) -> d.Name)
            Expect.contains names "aggregate_metric" "the fact store declares it"

            Expect.contains
                (NarrativeGrounding.factTools |> List.map (fun (d, _) -> d.Name))
                "aggregate_metric"
                "and the grounded narrative offers it"
        }
    ]

// ─── 2. The executor ─────────────────────────────────────────────────

let private executorTests =
    testList "the executor" [
        test "a two-site total for an additive metric cites its input facts" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            let a, b = seedTwoSites store scope

            let el = parse (aggregate sp scope byCategory)
            let totals = items el "totals"

            Expect.equal
                (totals |> List.map (fun t -> str t "group", str t "rendering"))
                [ "bakery", "2,000"; "dairy", "450" ]
                "each category summed across both sites, largest first"

            let bakery = List.head totals

            Expect.equal
                (items bakery "inputFactIds" |> List.map _.GetString())
                [ a.FactId; b.FactId ]
                "the total names the facts it was summed from"

            Expect.equal
                (items bakery "inputs" |> List.map (fun i -> str i "rendering"))
                [ "1,200"; "800" ]
                "and carries each input's rendering"

            Expect.equal (bakery.GetProperty("memberCount").GetInt32()) 2 "two members"
            Expect.equal (str bakery "periodLabel") "2026-07" "the one period they cover"
            Expect.isFalse (el.GetProperty("truncated").GetBoolean()) "nothing left out"
        }

        test "path_prefix narrows the population to one site" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            seedTwoSites store scope |> ignore

            let el =
                parse (
                    aggregate
                        sp
                        scope
                        """{"metric":"revenue","subject_hierarchy":"store","group_by_level":"category","path_prefix":"site-b"}"""
                )

            Expect.equal
                (items el "totals" |> List.map (fun t -> str t "group", str t "rendering"))
                [ "bakery", "800"; "dairy", "150" ]
                "site B alone"
        }

        test "a non-additive metric is refused by name, with its declared roll-up" {
            let _, sp = composed ()
            let scope = newScope ()

            let refusal =
                errorOf (
                    aggregate sp scope """{"metric":"share","subject_hierarchy":"store","group_by_level":"category"}"""
                )

            Expect.stringContains refusal "'share'" "names the metric"
            Expect.stringContains refusal "NonAdditive" "and its declared roll-up"
        }

        test "a metric that declares no roll-up is refused, never summed" {
            let _, sp = composed ()
            let scope = newScope ()

            let refusal =
                errorOf (
                    aggregate sp scope """{"metric":"units","subject_hierarchy":"store","group_by_level":"category"}"""
                )

            Expect.stringContains refusal "'units'" "names the metric"
            Expect.stringContains refusal "undeclared" "and that it declares nothing"
        }

        test "an unknown level is refused with the hierarchy's levels" {
            let _, sp = composed ()
            let scope = newScope ()

            let refusal =
                errorOf (
                    aggregate sp scope """{"metric":"revenue","subject_hierarchy":"store","group_by_level":"region"}"""
                )

            Expect.stringContains refusal "'region' is not a level" "names the level"
            Expect.stringContains refusal "site, category" "and lists the declared ones"
        }

        test "a group whose members cover different periods is refused" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            revenue store scope [ "site-a"; "bakery" ] 1200m |> ignore

            assertFact store scope (draft "revenue" [ "site-b"; "bakery" ] (Scalar 800m) august Surfaceable "sales")
            |> ignore

            let el = parse (aggregate sp scope byCategory)
            Expect.isEmpty (items el "totals") "no total"
            let refused = items el "refused" |> List.exactlyOne
            Expect.equal (str refused "group") "bakery" "the group"
            Expect.stringContains (str refused "reason") "2 different periods" "and why"
        }

        test "a subject holding two competing values is refused rather than counted twice" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            revenue store scope [ "site-a"; "bakery" ] 1200m |> ignore

            assertFact store scope (draft "revenue" [ "site-a"; "bakery" ] (Scalar 1250m) july Surfaceable "audit")
            |> ignore

            let el = parse (aggregate sp scope byCategory)
            Expect.isEmpty (items el "totals") "no total"

            Expect.stringContains
                (str (items el "refused" |> List.exactlyOne) "reason")
                "more than one value"
                "names the competing values"
        }

        test "a member with no single value refuses the whole request" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            revenue store scope [ "site-a"; "bakery" ] 1200m |> ignore

            assertFact
                store
                scope
                (draft "revenue" [ "site-b"; "bakery" ] (Interval(700m, 900m)) july Surfaceable "sales")
            |> ignore

            Expect.stringContains (errorOf (aggregate sp scope byCategory)) "no single value" "never partly summed"
        }

        test "a group with a withheld member is reported withheld with its count, never summed" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            revenue store scope [ "site-a"; "bakery" ] 1200m |> ignore

            assertFact
                store
                scope
                (draft "revenue" [ "site-b"; "bakery" ] (Scalar 834m) july (Restricted "licence-x") "sales")
            |> ignore

            revenue store scope [ "site-a"; "dairy" ] 300m |> ignore

            let json = aggregate sp scope byCategory
            let el = parse json

            Expect.equal
                (items el "totals" |> List.map (fun t -> str t "group"))
                [ "dairy" ]
                "only the wholly disclosed group has a total"

            let withheld = items el "withheld" |> List.exactlyOne
            Expect.equal (str withheld "group") "bakery" "the withheld group"
            Expect.equal (withheld.GetProperty("memberCount").GetInt32()) 2 "with its member count"
            Expect.isFalse (json.Contains "1,200") "the disclosed remainder is not reported as its sum"
            Expect.isFalse (json.Contains "\"834\"") "and the withheld value appears nowhere"

            Expect.equal
                (items el "withheldByPolicy" |> List.map (fun w -> str w "policyRef"))
                [ "licence-x" ]
                "counted by policy"
        }

        test "a group whose every member is withheld is counted, never named" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            revenue store scope [ "site-a"; "dairy" ] 300m |> ignore

            for site in [ "site-a"; "site-b" ] do
                assertFact
                    store
                    scope
                    (draft "revenue" [ site; "cellar" ] (Scalar 999m) july (Restricted "licence-x") "sales")
                |> ignore

            let json = aggregate sp scope byCategory
            let el = parse json
            Expect.equal (el.GetProperty("hiddenGroupCount").GetInt32()) 1 "one group, counted"
            Expect.isFalse (json.Contains "cellar") "and never named"
        }
    ]

// ─── 3. Tool results in the verifier's scope ─────────────────────────

let private verify (answer: string) (toolResult: string) =
    NumericFidelity.verify answer (toolResultFacts toolResult) (Some registry) "Strict"

let private scopeTests =
    testList "tool results in the verifier's scope" [
        test "a figure quoted from a query_metric_population result verifies" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            let a, _ = seedTwoSites store scope

            let result =
                PopulationQueryTool.executeWith
                    store
                    (sp.GetRequiredService<IFactDisclosureGate>())
                    (Some registry)
                    (fun () -> DateTime.UtcNow)
                    (TestScope.resolved scope)
                    "user-1"
                    """{"metric":"revenue","subject_hierarchy":"store","level":2,"ordering":"descending"}"""
                |> Async.RunSynchronously

            let v = verify "Site A's bakery took 1,200." result
            Expect.equal v.Verified 1 "the quoted figure is a fact the tool returned"
            Expect.equal (v.Numbers |> List.choose _.MatchedFactId) [ a.FactId ] "and names it"
        }

        test "a withheld member's value never verifies" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            revenue store scope [ "site-a"; "bakery" ] 1200m |> ignore

            assertFact
                store
                scope
                (draft "revenue" [ "site-b"; "bakery" ] (Scalar 834m) july (Restricted "licence-x") "sales")
            |> ignore

            let result =
                PopulationQueryTool.executeWith
                    store
                    (sp.GetRequiredService<IFactDisclosureGate>())
                    (Some registry)
                    (fun () -> DateTime.UtcNow)
                    (TestScope.resolved scope)
                    "user-1"
                    """{"metric":"revenue","subject_hierarchy":"store","ordering":"descending"}"""
                |> Async.RunSynchronously

            let v = verify "Site B's bakery took 834." result
            Expect.equal v.Unmatched 1 "the withheld value is not in scope"
        }

        test "a total quoted from an aggregate_metric result verifies against the facts it names" {
            let _, sp = composed ()
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            let a, b = seedTwoSites store scope

            let v =
                verify "Bakery took 2,000 across both sites." (aggregate sp scope byCategory)

            Expect.equal v.Verified 1 "the total verifies"

            Expect.equal
                (List.exactlyOne v.Numbers).DerivedFrom
                (Some [ a.FactId; b.FactId ])
                "and its verdict names the two facts"
        }
    ]

// ─── 4. End to end ───────────────────────────────────────────────────

/// The model: one `aggregate_metric` call, then an answer quoting the
/// bakery total exactly as the tool rendered it.
type private SummingModel() =
    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted-aggregate"
            Model = "scripted-aggregate-model"
        }

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            match messages |> List.collect _.ToolResults with
            | [] ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "aggregate_metric"
                                Arguments = byCategory
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | results ->
                let bakery =
                    items (parse (List.last results).Content) "totals"
                    |> List.find (fun t -> str t "group" = "bakery")

                return
                    Ok {
                        Content = sprintf "Bakery took £%s across both sites." (str bakery "rendering")
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
        member _.TryResolveByLabel(_, _) = async { return Ok provider }
        member _.BuildPlatform(_, _, _) = None

/// The fact tier composed by its knob, the assistant's per-request
/// services, the registry, and the numeric gate in `Strict`.
let private chatServices (model: IAIProvider) : ServiceProvider =
    let app = factsApp ()
    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(registry) |> ignore
    services.AddSingleton<IAIProviderFactory>(ScriptedFactory model) |> ignore

    services.AddSingleton<AnswerGate>(
        {
            Mode = AnswerGateStrict
            Verifier = NumericFidelityVerifier()
        }
    )
    |> ignore

    let tools = AIToolRegistry.AIToolRegistry()

    tools.RegisterAll(
        app.AITools
        |> List.map (fun (definition, execute) -> AIToolRegistry.createTool definition execute)
    )

    services.AddSingleton<AIToolRegistry.AIToolRegistry>(tools) |> ignore

    services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(
        ClientToolDispatch.ClientToolDispatchRegistry()
    )
    |> ignore

    services.AddSingleton<AICancellationRegistry.AICancellationRegistry>(
        AICancellationRegistry.AICancellationRegistry()
    )
    |> ignore

    let services =
        match ServerApp.serviceConfig app with
        | Some configure -> configure services
        | None -> services

    services.BuildServiceProvider()

let private requestFor (sp: IServiceProvider) (scopeId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp

    let storage: StorageScope = {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

    ctx.Items["ToolUp.StorageScope"] <- box storage
    ScopeResolution.remember ctx storage |> ignore
    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx :> HttpContext

/// Submit one chat turn and return the assistant's reply as persisted.
let private chatTurn (ctx: HttpContext) : ConversationMessage =
    let api = fst (aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx)
    let conversationId = Guid.NewGuid()

    async {
        let! submitted =
            api.SubmitMessage {
                ConversationId = conversationId
                Content = "What did each category take across both sites in July?"
                ActiveModule = None
                ActivePage = None
                ActivePageNarrative = None
                OverrideProviderLabel = None
                Surface = FullPage
                RetrievalFilters = None
            }

        let mutable finished = false
        let mutable attempts = 0

        while not finished && attempts < 400 do
            match! api.GetTaskStatus submitted.TaskId with
            | Some task ->
                match task.Status with
                | AITaskFailed reason -> failtestf "the turn failed: %A" reason
                | AITaskCompleted -> finished <- true
                | _ ->
                    attempts <- attempts + 1
                    do! Async.Sleep 50
            | None ->
                attempts <- attempts + 1
                do! Async.Sleep 50

        if not finished then
            failtest "the turn did not finish"

        let! messages = api.GetConversation conversationId

        return
            messages
            |> List.filter (fun m -> m.Participant = AIAssistant)
            |> List.tryLast
            |> Option.defaultWith (fun () -> failtest "no assistant reply was persisted")
    }
    |> Async.RunSynchronously

let private endToEndTests =
    testList "end to end" [
        test "a turn that calls aggregate_metric keeps its quoted total under Strict" {
            let sp = chatServices (SummingModel())
            let store = sp.GetRequiredService<IFactStore>()
            let scope = newScope ()
            let a, b = seedTwoSites store scope

            let reply = chatTurn (requestFor sp scope)

            Expect.equal reply.Content "Bakery took £2,000 across both sites." "the total is not withheld"

            match reply.Verification with
            | Some v ->
                Expect.equal v.Mode "Strict" "the gate ran in Strict"
                Expect.equal v.Verified 1 "the total verified"
                Expect.equal v.Unmatched 0 "nothing unmatched"

                Expect.equal
                    (List.exactlyOne v.Numbers).DerivedFrom
                    (Some [ a.FactId; b.FactId ])
                    "against the two facts the tool summed"
            | None -> failtest "the gate produced no verdict"
        }
    ]

let tests =
    testList "aggregate_metric — a roll-up the answer verifier can check" [
        declarationTests
        executorTests
        scopeTests
        endToEndTests
    ]