// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ClassificationSeamTests

open System
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Audit
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.IEntityStore
open ToolUp.Platform.Compose
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.Platform.Providers

// A module's declared classifications are the whole of what a
// deployment writes: the compose chain registers the classifier and the egress
// gate, and the doors to a model consult them. Phase 1251.

type private RecordingAuditLog() =
    let recorded = ResizeArray<AuditEvent>()

    member _.Blocked =
        recorded
        |> Seq.choose (function
            | EgressBlocked p -> Some p
            | _ -> None)
        |> List.ofSeq

    interface IAuditLog with
        member _.Record(AuditScope.StreamKey _, _actor, audit) = async {
            lock recorded (fun () -> recorded.Add audit)
            return Ok()
        }

        member _.GetAuditTrail(AuditScope.StreamKey _, _, _) = async { return List.ofSeq recorded }

/// Answers every entity read with the rows it was built with.
let private entityStoreOf (rows: string list) : IEntityStore =
    let parsed = rows |> List.map (fun r -> JsonDocument.Parse(r).RootElement.Clone())

    { new IEntityStore with
        member _.Query<'T>(_, _) = async { return Ok(unbox<'T list> (box parsed)) }
        member _.Save<'T>(_, _, _: 'T) = failwith "unused"
        member _.SaveIfVersion<'T>(_, _, _: 'T, _) = failwith "unused"
        member _.Get<'T>(_, _, _) : Async<Result<'T, EntityTypes.EntityError>> = failwith "unused"
        member _.GetVersion<'T>(_, _, _, _) : Async<Result<'T, EntityTypes.EntityError>> = failwith "unused"
        member _.ListVersions<'T>(_, _, _) : Async<EntityTypes.EntityRef<'T> list> = failwith "unused"
        member _.Delete(_, _, _, _) = failwith "unused"
        member _.DeleteIfVersion(_, _, _, _, _) = failwith "unused"

        member _.FindByIndex<'T>(_, _, _, _) : Async<Result<EntityTypes.EntityRef<'T> list, EntityTypes.EntityError>> =
            failwith "unused"

        member _.Count(_, _) = failwith "unused"
        member _.ListAll<'T>(_, _, _, _) : Async<EntityTypes.EntityRef<'T> list> = failwith "unused"
    }

let private queryBusOf (payload: string) : IModuleQueryBus =
    { new IModuleQueryBus with
        member _.Ask(_, _) = async { return Some(Ok { Payload = payload }) }
    }

let private resultStoreOf (content: string) : IResultStore =
    let meta: DataObject = {
        ObjectId = "o1"
        Version = 1
        CreatedAt = DateTime.UnixEpoch
        CreatedBy = "job"
        ScopeId = "t1"
        DataType = "report"
        ContentHash = ""
        Policy = Unchecked.defaultof<_>
        Metadata = Map.empty
    }

    { new IResultStore with
        member _.GetLatest(_, _, _) = async { return Ok(meta, Text.Encoding.UTF8.GetBytes content) }

        member _.SaveResult(_, _, _, _, _, _) = failwith "unused"
        member _.ListResults(_, _, _) = failwith "unused"
        member _.CompareVersions(_, _, _, _) = failwith "unused"
        member _.DeleteResult(_, _, _) = failwith "unused"
        member _.DeleteByPrefix(_, _, _) = failwith "unused"
    }

let private ordersModule =
    ServerModule.create "Orders"
    |> ServerModule.withClassifications [ FieldClassification.create "Order" "CustomerEmail" Pii ]

/// The provider a composed app's own registrations produce, over a recording
/// audit log and whatever the door under test reads.
let private providerOf (audit: RecordingAuditLog) (substrate: IServiceCollection -> unit) (app: ServerApp) =
    let services = ServiceCollection()
    services.AddSingleton<IAuditLog>(audit) |> ignore
    substrate services

    match ServerApp.serviceConfig app with
    | Some register -> register services |> ignore
    | None -> ()

    services.BuildServiceProvider()

let private requestOver (provider: IServiceProvider) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    AccessContexts.stampUserId ctx.Items ("alice")

    AccessContexts.stampScope ctx.Items {
        ScopeId = "t1"
        Container = "team-t1"
        Persist = true
    }

    AccessContexts.stampPermissions ctx.Items []
    ctx :> HttpContext

let private composed (modules: ServerModule list) =
    ServerApp.empty |> ServerApp.addModules modules

let private builtIn (name: string) =
    PlatformAITools.builtIn |> List.find (fun t -> t.Definition.Name = name)

/// The `withheldFields` a door named at the root of its result.
let private withheldFields (json: string) : string list =
    use doc = JsonDocument.Parse json

    match doc.RootElement.TryGetProperty "withheldFields" with
    | true, names -> [ for n in names.EnumerateArray() -> n.GetString() ]
    | _ -> []

let private moduleTool (execute: string) : RegisteredTool =
    let definition: AIToolDefinition = {
        Name = "orders.read"
        Description = "Test-only tool"
        Parameters = []
        SourceModule = "Orders"
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = UndeclaredEffects
    }

    AIToolRegistry.createTool definition (fun _ _ -> async { return execute })
    |> AIToolRegistry.withClassifiedResults

let private orderRow = """{"Id":"o1","CustomerEmail":"a@b.example","Total":12}"""

/// Calls one client-resident tool once, then ends the turn.
type private OneToolCallProvider(toolName: string) =
    let mutable calls = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test"
            Model = "test-model"
        }

        member _.SendMessage(_, _, _, _, _) = async {
            calls <- calls + 1

            return
                Ok {
                    Content = if calls = 1 then "" else "done"
                    ToolCalls =
                        if calls = 1 then
                            [
                                {
                                    Id = Guid.NewGuid().ToString()
                                    Name = toolName
                                    Arguments = "{}"
                                }
                            ]
                        else
                            []
                    StopReason = if calls = 1 then StopReason.ToolUse else StopReason.EndTurn
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

type private StubProviderFactory() =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error(ProviderResolutionError.NoProviderConfigured) }
        member _.TryResolveByLabel(_, _) = async { return Error(ProviderResolutionError.NoProviderConfigured) }
        member _.BuildPlatform(_, _, _) = None

type private StubProviderProfile() =
    interface IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }

[<Tests>]
let tests =
    testList "Classification seam" [
        testCase "withClassifications concatenates, and a new module declares none"
        <| fun () ->
            let first = FieldClassification.create "Order" "CustomerEmail" Pii
            let second = FieldClassification.create "Order" "Phone" Pii

            let m =
                ServerModule.create "Orders"
                |> ServerModule.withClassifications [ first ]
                |> ServerModule.withClassifications [ second ]

            Expect.equal m.Classifications [ first; second ] "both calls are kept, in order"
            Expect.isEmpty (ServerModule.create "Other").Classifications "the default is empty"

        testCase "a composition with no declaration and no host classifier registers nothing"
        <| fun () ->
            let provider =
                composed [ ServerModule.create "Other" ]
                |> ServerApp.addServiceConfig id
                |> providerOf (RecordingAuditLog()) ignore

            Expect.isNull (box (provider.GetService<IFieldClassifier>())) "no classifier"
            Expect.isNull (box (provider.GetService<IEgressGate>())) "no gate"

        testCaseAsync "two modules' declarations fold into one registered classifier"
        <| async {
            let provider =
                composed [
                    ordersModule
                    ServerModule.create "Billing"
                    |> ServerModule.withClassifications [ FieldClassification.create "Invoice" "Iban" Spi ]
                ]
                |> providerOf (RecordingAuditLog()) ignore

            let classifier = provider.GetRequiredService<IFieldClassifier>()
            let! order = classifier.IsClassified("Order", "CustomerEmail")
            let! invoice = classifier.IsClassified("Invoice", "Iban")
            Expect.isTrue order "the first module's field"
            Expect.isTrue invoice "the second module's field"
        }

        testCase "one field declared differently by two modules is refused at compose, naming it"
        <| fun () ->
            let app =
                composed [
                    ordersModule
                    ServerModule.create "Marketing"
                    |> ServerModule.withClassifications [ FieldClassification.create "Order" "CustomerEmail" Public ]
                ]

            let refusal =
                Expect.throwsC (fun () -> ServerApp.serviceConfig app |> ignore) _.Message

            Expect.stringContains refusal "Order.CustomerEmail" "the refusal names the field"

        testCaseAsync "a declaring module is refused on the forbidden surface and admitted on the permitted one"
        <| async {
            let audit = RecordingAuditLog()

            let gate =
                (composed [ ordersModule ] |> providerOf audit ignore).GetRequiredService<IEgressGate>()

            let fields = Map.ofList [ "CustomerEmail", "a@b.example"; "Total", "12" ]

            let! toModel = gate.Apply(EgressContext.create EgressBoundary.aiToolResult "alice", "Order", fields)
            let! toResponse = gate.Apply(EgressContext.create EgressBoundary.RpcResponse "alice", "Order", fields)

            Expect.equal (toModel.TryFind "CustomerEmail") None "refused at the model boundary"
            Expect.equal (toModel.TryFind "Total") (Some "12") "an unclassified field passes"
            Expect.equal toResponse fields "admitted on a response"
            Expect.equal (audit.Blocked |> List.map _.FieldPath) [ "CustomerEmail" ] "the refusal is audited, once"
        }

        testCaseAsync "the deployment's policy replaces the default"
        <| async {
            let provider =
                composed [ ordersModule ]
                |> ServerApp.withClassificationEgressPolicy EgressGate.permissiveEgressPolicy
                |> providerOf (RecordingAuditLog()) ignore

            let fields = Map.ofList [ "CustomerEmail", "a@b.example" ]

            let! crossed =
                provider
                    .GetRequiredService<IEgressGate>()
                    .Apply(EgressContext.create EgressBoundary.aiToolResult "alice", "Order", fields)

            Expect.equal crossed fields "a permissive deployment admits it"
        }

        testCaseAsync "a host-supplied classifier is honoured over the declarations, and gets the gate"
        <| async {
            let hostClassifier =
                DefaultFieldClassifier.create [ FieldClassification.create "Order" "Total" Financial ]

            let provider =
                composed [ ordersModule ]
                |> ServerApp.addServiceConfig (fun s -> s.AddSingleton<IFieldClassifier>(hostClassifier))
                |> providerOf (RecordingAuditLog()) ignore

            Expect.isTrue
                (obj.ReferenceEquals(provider.GetRequiredService<IFieldClassifier>(), hostClassifier))
                "the host's registration stands"

            let! crossed =
                provider
                    .GetRequiredService<IEgressGate>()
                    .Apply(
                        EgressContext.create EgressBoundary.aiToolResult "alice",
                        "Order",
                        Map.ofList [ "CustomerEmail", "a@b.example"; "Total", "12" ]
                    )

            Expect.equal (crossed.TryFind "CustomerEmail") (Some "a@b.example") "the declaration is not consulted"
            Expect.equal (crossed.TryFind "Total") None "the host's classification is"
        }

        testCaseAsync "a host-registered classifier with no declaration still gets the gate"
        <| async {
            let provider =
                composed []
                |> ServerApp.addServiceConfig (fun s ->
                    s.AddSingleton<IFieldClassifier>(
                        DefaultFieldClassifier.create [ FieldClassification.create "Order" "Total" Financial ]
                    ))
                |> providerOf (RecordingAuditLog()) ignore

            Expect.isNotNull (box (provider.GetService<IEgressGate>())) "the gate stands over the host's classifier"
        }

        testCaseAsync "query_entity withholds a classified field, names it and audits it"
        <| async {
            let audit = RecordingAuditLog()

            let provider =
                composed [ ordersModule ]
                |> providerOf audit (fun s -> s.AddSingleton(entityStoreOf [ orderRow ]) |> ignore)

            let! result =
                (builtIn "_platform.ai.query_entity").Execute (requestOver provider) """{"entityType":"Order"}"""

            Expect.equal (withheldFields result) [ "Order.CustomerEmail" ] "the field path is named"
            Expect.isFalse (result.Contains "a@b.example") "the value did not cross"
            Expect.stringContains result "\"Total\":12" "the other fields did"
            Expect.equal (audit.Blocked |> List.map _.Actor) [ "alice" ] "the refusal is audited against the caller"
            Expect.equal (audit.Blocked |> List.map _.Boundary) [ "AiToolResult" ] "at the model boundary"
        }

        testCaseAsync "query_entity refuses a predicate over a classified field before it reads"
        <| async {
            let audit = RecordingAuditLog()

            let provider =
                composed [ ordersModule ]
                |> providerOf audit (fun s -> s.AddSingleton(entityStoreOf [ orderRow ]) |> ignore)

            let! result =
                (builtIn "_platform.ai.query_entity").Execute
                    (requestOver provider)
                    """{"entityType":"Order","predicateJson":"{\"op\":\"eq\",\"field\":\"CustomerEmail\",\"value\":\"a@b.example\"}"}"""

            use doc = JsonDocument.Parse result

            Expect.equal
                (doc.RootElement.GetProperty("error").GetString())
                "ClassifiedFieldInPredicate"
                "a filter on the field answers questions about it"

            Expect.equal (audit.Blocked |> List.map _.FieldPath) [ "CustomerEmail" ] "and is audited as a refusal"
        }

        testCaseAsync "query_entity admits a predicate over an unclassified field"
        <| async {
            let provider =
                composed [ ordersModule ]
                |> providerOf (RecordingAuditLog()) (fun s -> s.AddSingleton(entityStoreOf [ orderRow ]) |> ignore)

            let! result =
                (builtIn "_platform.ai.query_entity").Execute
                    (requestOver provider)
                    """{"entityType":"Order","predicateJson":"{\"op\":\"eq\",\"field\":\"Total\",\"value\":\"12\"}"}"""

            Expect.equal (withheldFields result) [ "Order.CustomerEmail" ] "it reads, and the row is still gated"
        }

        testCaseAsync "an undeclared entity's read is the executor's own text, whatever is declared elsewhere"
        <| async {
            let provider =
                composed [ ordersModule ]
                |> providerOf (RecordingAuditLog()) (fun s -> s.AddSingleton(entityStoreOf [ orderRow ]) |> ignore)

            let! result =
                (builtIn "_platform.ai.query_entity").Execute (requestOver provider) """{"entityType":"Shipment"}"""

            Expect.stringContains result "a@b.example" "the field is classified on another entity only"
            Expect.isEmpty (withheldFields result) "nothing is withheld"
        }

        testCaseAsync "a module's tool result is gated, as Execute and as Answer"
        <| async {
            let provider = composed [ ordersModule ] |> providerOf (RecordingAuditLog()) ignore
            let ctx = requestOver provider
            let tool = moduleTool """{"orders":[{"customer_email":"a@b.example","total":12}]}"""

            let! executed = tool.Execute ctx "{}"
            Expect.equal (withheldFields executed) [ "Order.CustomerEmail" ] "named, matched across spellings"
            Expect.isFalse (executed.Contains "a@b.example") "the value did not cross"

            let definition = tool.Definition

            let answering =
                AIToolRegistry.createAnsweringTool definition (fun _ _ -> async {
                    return {
                        ToolHelpers.ToolAnswer.Text = orderRow
                        ToolHelpers.ToolAnswer.Parts = []
                    }
                })
                |> AIToolRegistry.withClassifiedResults

            let! answered = answering.Answer.Value ctx "{}"
            Expect.isFalse (answered.Text.Contains "a@b.example") "the record form's text is gated too"
        }

        testCaseAsync "a tool result nothing was refused from is byte-identical, gate or no gate"
        <| async {
            let text = """{ "total" : 12 }"""
            let tool = moduleTool text

            let! gated =
                tool.Execute (requestOver (composed [ ordersModule ] |> providerOf (RecordingAuditLog()) ignore)) "{}"

            let! ungated = tool.Execute (requestOver (composed [] |> providerOf (RecordingAuditLog()) ignore)) "{}"

            Expect.equal gated text "a declaring module, an untouched result"
            Expect.equal ungated text "a composition with no classification"
        }

        testCaseAsync "query_module gates the result by the module it asked"
        <| async {
            let provider =
                composed [ ordersModule ]
                |> providerOf (RecordingAuditLog()) (fun s ->
                    s.AddSingleton(queryBusOf orderRow) |> ignore

                    s.AddSingleton(ModuleAIExposureRegistry.ofDeclarations [ "Orders", ModuleAIExposure.Queryable ])
                    |> ignore)

            let! result =
                (builtIn "_platform.ai.query_module").Execute
                    (requestOver provider)
                    """{"moduleName":"Orders","queryKey":"latest"}"""

            Expect.equal (withheldFields result) [ "Order.CustomerEmail" ] "named"
            Expect.isFalse (result.Contains "a@b.example") "the value did not cross"
        }

        testCaseAsync "get_latest_result gates the document before it caps it"
        <| async {
            let padded =
                $"""{{"CustomerEmail":"a@b.example","Pad":"{String('x', 1_000_100)}"}}"""

            let provider =
                composed [ ordersModule ]
                |> providerOf (RecordingAuditLog()) (fun s ->
                    s.AddSingleton(resultStoreOf padded) |> ignore

                    s.AddSingleton(ModuleAIExposureRegistry.ofDeclarations [ "Orders", ModuleAIExposure.Queryable ])
                    |> ignore)

            let! result =
                (builtIn "_platform.ai.get_latest_result").Execute
                    (requestOver provider)
                    """{"moduleName":"Orders","resultType":"report"}"""

            Expect.isFalse (result.Contains "a@b.example") "a capped document was still read whole by the gate"
            Expect.stringContains result "withheldFields" "and the content says what it kept"
        }

        testCaseAsync "a client-resident tool's result is gated before the model reads it"
        <| async {
            let toolName = "orders.client"
            let audit = RecordingAuditLog()
            let ctx = requestOver (composed [ ordersModule ] |> providerOf audit ignore)
            let dispatch = ClientToolDispatch.ClientToolDispatchRegistry()

            let registry = AIToolRegistry.AIToolRegistry()

            registry.RegisterAll [
                AIToolRegistry.createTool
                    {
                        (moduleTool "").Definition with
                            Name = toolName
                            Location = ClientResident
                    }
                    (fun _ _ -> failwith "a client-resident body does not run on the server")
            ]

            let completed = ResizeArray<string>()

            let onEvent (event: AIStreamEvent) =
                match event with
                | ClientToolInvoke(_, toolCallId, _, _, _, _) -> dispatch.TryComplete(toolCallId, orderRow) |> ignore
                | ToolCallCompleted(_, _, result) -> lock completed (fun () -> completed.Add result)
                | _ -> ()

            let! _ =
                AIAgentEngine.runAgentLoopWithInput
                    (OneToolCallProvider toolName)
                    registry
                    dispatch
                    ctx
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    (Some "Orders")
                    (Some "/orders")
                    CancellationToken.None
                    [ AIProviderMessage.text "user" "read the order" ]
                    ModelInput.empty
                    onEvent

            let result = Expect.wantSome (Seq.tryHead completed) "the tool call completed"
            Expect.equal (withheldFields result) [ "Order.CustomerEmail" ] "named to the model"
            Expect.isFalse (result.Contains "a@b.example") "the value did not cross"
        }

        testCaseAsync "the AI tier registers a module's declared tools through the door"
        <| async {
            let declared = (moduleTool "").Definition

            let app =
                ordersModule
                |> ServerModule.withAITools [ declared, (fun _ _ -> async { return orderRow }) ]
                |> List.singleton
                |> composed
                |> AICompose.withAI (AICompose.AIOptions.create (StubProviderFactory()) (StubProviderProfile()))

            let provider = app |> providerOf (RecordingAuditLog()) ignore
            let registry = provider.GetRequiredService<AIToolRegistry.AIToolRegistry>()

            let tool =
                registry.GetAll() |> List.find (fun t -> t.Definition.Name = declared.Name)

            let! result = tool.Execute (requestOver provider) "{}"
            Expect.equal (withheldFields result) [ "Order.CustomerEmail" ] "gated with no call beyond the declaration"
        }
    ]