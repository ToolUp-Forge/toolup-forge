// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AssistantFactScopeTests

// ─── Phase 990.C — an assistant chat turn's fact tools read the request's scope ─
//
// The fact tools (`query_facts` and its two siblings) read only the
// `ResolvedScope` the platform minted for the request (Phase 797). A chat
// turn does not run its agent loop on the request: `SubmitMessage` builds a
// BACKGROUND context and the tools execute against that. So the question is
// whether the background context carries the request's resolved scope — if
// it does not, every fact tool in conversation reads the anonymous shard and
// the assistant cannot see the caller's Facts at all, whatever team they
// are in.
//
// The test drives a real turn through `SubmitMessage` with a scripted model
// that calls `query_facts`, under a request whose scope the platform
// resolved to one team, with a second team's Fact under the same subject and
// metric beside it. The tool's answer must be that team's Fact, and only it.

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private queryArgs =
    """{"subject_hierarchy":"brand","subject_path":"acme","metric":"revenue"}"""

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

let private assertOk (store: IFactStore) (scopeId: string) (draft: FactDraft) : Fact =
    match store.Assert(scopeId, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// The model: one `query_facts` call, then an answer. It records the
/// result the tool returned, which is what the test reads.
type private QueryingModel() =
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
            ProviderName = "scripted-fact-reader"
            Model = "scripted-fact-reader-model"
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
                                Name = "query_facts"
                                Arguments = queryArgs
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | toolResults ->
                toolResults |> List.iter (fun r -> results.Enqueue r.Content)

                return
                    Ok {
                        Content = "Read."
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

/// A deployment with the fact tier composed by its one knob and the
/// assistant's per-request services, as a composition root registers them.
let private servicesWith (model: IAIProvider) : ServiceProvider =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                }
        }
        |> FactsCompose.withFactStore

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IAIProviderFactory>(ScriptedFactory model) |> ignore

    let registry = AIToolRegistry.AIToolRegistry()

    registry.RegisterAll(
        app.AITools
        |> List.map (fun (definition, execute) -> AIToolRegistry.createTool definition execute)
    )

    services.AddSingleton<AIToolRegistry.AIToolRegistry>(registry) |> ignore

    services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(
        ClientToolDispatch.ClientToolDispatchRegistry()
    )
    |> ignore

    services.AddSingleton<AICancellationRegistry.AICancellationRegistry>(
        AICancellationRegistry.AICancellationRegistry()
    )
    |> ignore

    let services =
        match app.Extensions.ServiceConfig with
        | Some configure -> configure services
        | None -> services

    services.BuildServiceProvider()

let private storageFor (teamId: string) : StorageScope = {
    ScopeId = teamId
    Container = $"team-{teamId}"
    Persist = true
}

/// The request as the scope-resolution middleware leaves it: the storage
/// scope, the user, and the `ResolvedScope` minted from the storage scope.
let private requestFor (sp: IServiceProvider) (teamId: string) (userId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- sp
    let storage = storageFor teamId
    ctx.Items["ToolUp.StorageScope"] <- box storage
    ScopeResolution.remember ctx storage |> ignore
    ctx.Items["ToolUp.UserId"] <- box userId
    ctx :> HttpContext

/// Poll `GetTaskStatus` until the task is terminal — bounded, so a hang
/// fails the test instead of the run.
let private awaitTerminal (api: AIAssistantApi) (taskId: Guid) = async {
    let mutable result = None
    let mutable attempts = 0

    while result.IsNone && attempts < 400 do
        match! api.GetTaskStatus taskId with
        | Some task when
            (match task.Status with
             | AITaskCompleted
             | AITaskFailed _ -> true
             | _ -> false)
            ->
            result <- Some task
        | _ ->
            attempts <- attempts + 1
            do! Async.Sleep 50

    return result
}

/// Submit one chat turn on `ctx` and wait for it to finish.
let private chatTurn (ctx: HttpContext) =
    let api = fst (aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx)

    async {
        let! submitted =
            api.SubmitMessage {
                ConversationId = Guid.NewGuid()
                Content = "What was Acme's revenue in September?"
                ActiveModule = None
                ActivePage = None
                ActivePageNarrative = None
                OverrideProviderLabel = None
                Surface = FullPage
                RetrievalFilters = None
            }

        match! awaitTerminal api submitted.TaskId with
        | Some task ->
            match task.Status with
            | AITaskFailed reason -> return failtestf "the turn failed: %A" reason
            | _ -> return ()
        | None -> return failtest "the turn did not finish"
    }
    |> Async.RunSynchronously

/// The fact ids a `query_facts` result returned.
let private factIdsIn (result: string) : string list =
    match (JsonDocument.Parse result).RootElement.TryGetProperty "facts" with
    | true, facts ->
        facts.EnumerateArray()
        |> Seq.map (fun fact -> fact.GetProperty("factId").GetString())
        |> List.ofSeq
    | _ -> failtestf "query_facts returned no facts array: %s" result

let tests =
    testList "Phase 990.C — an assistant chat turn's fact tools read the request's scope" [

        test "query_facts in a chat turn reads the team the request resolved to, and no other team" {
            let model = QueryingModel()
            let sp = servicesWith model
            let store = sp.GetRequiredService<IFactStore>()
            let teamA = "team-" + Guid.NewGuid().ToString "N"
            let teamB = "team-" + Guid.NewGuid().ToString "N"

            let ours = assertOk store teamA (revenue 1250m)
            let theirs = assertOk store teamB (revenue 9999m)

            chatTurn (requestFor sp teamA "alice")

            match model.ToolResults with
            | [ result ] ->
                let ids = factIdsIn result
                Expect.equal ids [ ours.FactId ] "the tool read the requesting team's Fact"
                Expect.isFalse (List.contains theirs.FactId ids) "and never another team's"
            | other -> failtestf "expected exactly one query_facts result, got %A" other
        }

        test "a chat turn on a request the platform resolved no scope for reads the anonymous shard — nothing" {
            let model = QueryingModel()
            let sp = servicesWith model
            let store = sp.GetRequiredService<IFactStore>()
            let team = "team-" + Guid.NewGuid().ToString "N"
            assertOk store team (revenue 1250m) |> ignore

            // The storage scope is on the request, but no `ResolvedScope` is:
            // the fact door must not promote the one into the other.
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- sp
            ctx.Items["ToolUp.StorageScope"] <- box (storageFor team)
            ctx.Items["ToolUp.UserId"] <- box "alice"

            chatTurn (ctx :> HttpContext)

            match model.ToolResults with
            | [ result ] -> Expect.isEmpty (factIdsIn result) "an unresolved request reads no team's Facts"
            | other -> failtestf "expected exactly one query_facts result, got %A" other
        }
    ]