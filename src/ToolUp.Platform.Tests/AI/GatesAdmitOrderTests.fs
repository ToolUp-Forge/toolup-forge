// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.GatesAdmitOrderTests

// ─── The order a tool call passes its gates, at every door ───────────
//
// Seven gates stand between a model's tool call and the data it reaches:
// g1 the caller's Read on the module, g2 that grant's liveness, g3 the
// deployment's effect ceiling, g4 the client-resident allowlist, g5 the
// human approval, g6 the module's AI-queryability opt-in and g7 the user's
// consent to the read. Each row below sets SEVERAL gates failing at once
// and asserts which refusal the caller sees, so a reordering at any door
// turns a row red.
//
// The doors: the agent loop's dispatch, the four `_platform.ai.*` reach
// tools, and the three POST handlers that complete a parked call.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose
open ToolUp.Platform.Jobs
open ToolUp.Platform.Scope
open ToolUp.Platform.Tests.Contracts
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open DataManagementTypes

let private Read = ModulePermission.Read
let private jsonOptions = PlatformJson.options

let private scope: StorageScope = {
    ScopeId = "t1"
    Container = "team-t1"
    Persist = true
}

/// A grant on `Payroll` recorded but awaiting the subject's consent: the
/// permission entry is present, the authority behind it is not live.
let private pendingPayroll: ModuleGrantRecord = {
    State = GrantState.PendingConsent
    SatisfiedPolicy = GrantPolicy.RequiresSubjectConsent
    Justification = "order test"
    ConsentedBy = None
}

/// The gate states a row sets.
type private GateState = {
    /// g1 — the caller holds Read on `Payroll`.
    Readable: bool
    /// g2 — that grant is live.
    GrantLive: bool
    /// g3 — the tool's effects sit within the ceiling.
    WithinCeiling: bool
    /// g4 — the client-resident allowlist admits the call.
    AllowlistAdmits: bool
    /// g5 — no approval is required.
    ApprovalFree: bool
    /// g6 — `Payroll` opted into the AI surface.
    Queryable: bool
    /// g7 — the user has not refused the read.
    Consented: bool
}

let private allOpen = {
    Readable = true
    GrantLive = true
    WithinCeiling = true
    AllowlistAdmits = true
    ApprovalFree = true
    Queryable = true
    Consented = true
}

let private allShut = {
    Readable = false
    GrantLive = false
    WithinCeiling = false
    AllowlistAdmits = false
    ApprovalFree = false
    Queryable = false
    Consented = false
}

let private grantStamps
    (state: GateState)
    (services: ServiceCollection)
    (items: Collections.Generic.IDictionary<obj, obj>)
    =
    services.AddSingleton<GrantPolicyGuard.ModuleGrantPolicyRegistry>(
        GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [ "Payroll", GrantPolicy.RequiresSubjectConsent ]
    )
    |> ignore

    let record =
        if state.GrantLive then
            {
                pendingPayroll with
                    State = GrantState.Active
                    ConsentedBy = Some "alice"
            }
        else
            pendingPayroll

    items[GrantPolicyGuard.ModuleGrantsItemsKey] <- box (Map.ofList [ "Payroll", record ])

    if state.GrantLive then
        items[GrantConsentStore.ModuleGrantConsentsItemsKey] <- box (Map.ofList [ "Payroll", Ok() ])

// ─── The loop's dispatch arm ─────────────────────────────────────────

/// Names one tool call on its first turn and ends the conversation on the
/// second, whatever it was offered: the dispatch re-check is the door.
type private OneCallProvider(toolName: string) =
    let mutable calls = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "order-test"
            Model = "order-test-model"
        }

        member _.SendMessage(_, _, _, _, _) = async {
            calls <- calls + 1

            if calls = 1 then
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = toolName
                                Arguments = "{}"
                            }
                        ]
                        StopReason = StopReason.ToolUse
                        Usage = None
                    }
            else
                return
                    Ok {
                        Content = "done"
                        ToolCalls = []
                        StopReason = StopReason.EndTurn
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

type private AllowlistOf(admits: bool) =
    interface IClientToolAuthorizer with
        member _.Authorize(_, _, _, _) =
            if admits then
                ClientToolAuthDecision.Allow
            else
                ClientToolAuthDecision.Deny "order test: the allowlist refuses"

/// Run one call of `payroll.export` through the loop with `state`, and
/// return what the model was shown for it.
let private loopDoor (state: GateState) : string =
    let effects =
        if state.WithinCeiling then
            ToolEffectDeclaration.declare [ ToolEffect.ReadFacts; ToolEffect.WriteState "payroll" ]
        else
            ToolEffectDeclaration.declare [ ToolEffect.ReadFacts; ToolEffect.Egress "payroll.example" ]

    let def: AIToolDefinition = {
        Name = "payroll.export"
        Description = "order test tool"
        Parameters = []
        SourceModule = "Payroll"
        EmitsActions = None
        // Client-resident so the allowlist is consulted; a call every gate
        // admits then waits for the browser, which the row reads as admitted.
        Location = ClientResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = effects
    }

    let policy =
        let bounded =
            ToolPolicy.bounded [ ToolEffectClass.ReadFacts; ToolEffectClass.WriteState ]

        if state.ApprovalFree then
            bounded
        else
            ToolPolicy.withApprovalFor [ ToolEffectClass.WriteState; ToolEffectClass.Egress ] bounded

    let registry = AIToolRegistry(policy)
    registry.RegisterAll [ createTool def (fun _ _ -> async { return "ran" }) ]

    let services = ServiceCollection()

    services.AddSingleton<IClientToolAuthorizer>(AllowlistOf state.AllowlistAdmits)
    |> ignore

    let ctx = DefaultHttpContext()
    AccessContexts.stampUserId ctx.Items "alice"
    AccessContexts.stampScope ctx.Items scope

    AccessContexts.stampPermissions
        ctx.Items
        (if state.Readable then
             [ "Payroll", [ Read ] ]
         else
             [ "Other", [ Read ] ])

    grantStamps state services ctx.Items
    ctx.RequestServices <- services.BuildServiceProvider()

    let shown = ResizeArray<string>()

    let onEvent (evt: AIStreamEvent) =
        match evt with
        | ToolCallCompleted(_, _, content) -> lock shown (fun () -> shown.Add content)
        | _ -> ()

    // A call every gate admits waits for the browser; a short cancel ends
    // that wait, and the row reads "admitted" off the absence of a refusal.
    use cancel = new CancellationTokenSource(TimeSpan.FromSeconds 2.0)

    try
        AIAgentEngine.runAgentLoopWithInput
            (OneCallProvider "payroll.export")
            registry
            (ClientToolDispatch.ClientToolDispatchRegistry())
            ctx
            (Guid.NewGuid())
            (Guid.NewGuid())
            AISurface.FullPage
            None
            None
            cancel.Token
            [ AIProviderMessage.text "user" "export payroll" ]
            ModelInput.empty
            onEvent
        |> Async.RunSynchronously
        |> ignore
    with _ ->
        ()

    lock shown (fun () -> String.concat "\n" shown)

/// What a loop row expects the model to be shown: the gate whose refusal
/// it is, by a fragment of that gate's own sentence.
let private loopRefusal (shown: string) : string =
    if shown.Contains "requires Read on module" then
        "g1"
    elif shown.Contains "requires a live grant" then
        "g2"
    elif shown.Contains "outside this deployment's tool policy ceiling" then
        "g3"
    elif shown.Contains "the allowlist refuses" then
        "g4"
    elif shown.Contains "requires a person's approval" then
        "g5"
    elif shown.Contains "was denied" then
        "denied:" + shown
    else
        "admitted"

// ─── The four reach tools ────────────────────────────────────────────

type private OneTypeCatalog() =
    interface IDataCatalog with
        member _.ListTypes() = async {
            return [
                {
                    Id = "salary"
                    DisplayName = "salary"
                    Schema = None
                }
            ]
        }

        member _.GetSchema _ = async { return None }

        member _.GetProducers typeId = async { return if typeId = "salary" then [ "Payroll" ] else [] }
        member _.ListObjects(_, _) = async { return [] }
        member _.CountObjects(_, _) = async { return 0 }

        member _.GetSyntheticSample(_, _, _) =
            failwith "not reachable from the reach tools"

/// The shipped bus's RBAC answer and nothing else.
type private ReadOnlyBus() =
    interface IModuleQueryBus with
        member _.Ask(context, request) = async {
            if not (AccessContext.hasPermission request.TargetModule Read context) then
                return Some(Error(PermissionDenied request.TargetModule))
            else
                return Some(Ok { Payload = """{"ok":true}""" })
        }

/// Run one reach tool with `state` and return its `error` discriminator,
/// or "admitted" when no gate refused it.
let private reachDoor (toolName: string) (args: string) (state: GateState) : string =
    let services = ServiceCollection()

    if state.Queryable then
        services.AddSingleton<ModuleAIExposureRegistry>(
            ModuleAIExposureRegistry.ofDeclarations [ "Payroll", ModuleAIExposure.Queryable ]
        )
        |> ignore

    services.AddSingleton<IDataCatalog>(OneTypeCatalog()) |> ignore
    services.AddSingleton<IModuleQueryBus>(ReadOnlyBus()) |> ignore

    let consents = AIConsentDispatch.AIConsentRegistry()
    services.AddSingleton<AIConsentDispatch.AIConsentRegistry>(consents) |> ignore

    services.AddSingleton<AIConsentMode>(RememberPerConversation) |> ignore
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    let conversationId = Guid.NewGuid()

    // A standing decision recorded for this conversation: g7 answers without
    // a prompt, so no row waits on a dialog.
    AIConsentDispatch.recordDecision
        storage
        scope.Container
        conversationId
        "Payroll"
        (if state.Consented then AllowForConversation else Denied)
    |> Async.RunSynchronously

    let ctx = DefaultHttpContext()
    AccessContexts.stampUserId ctx.Items "alice"
    AccessContexts.stampScope ctx.Items scope

    AccessContexts.stampPermissions
        ctx.Items
        (if state.Readable then
             [ "Payroll", [ Read ] ]
         else
             [ "Other", [ Read ] ])

    grantStamps state services ctx.Items
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items[AIConsentDispatch.ItemsKeys.ConversationId] <- box conversationId
    ctx.Items[AIConsentDispatch.ItemsKeys.TaskId] <- box (Guid.NewGuid())

    ctx.Items[AIConsentDispatch.ItemsKeys.StreamEmitter] <-
        box (
            {
                Emit = fun _ -> failtest "a standing decision must not prompt"
            }
            : AIConsentDispatch.StreamEmitter
        )

    let tool =
        PlatformAITools.builtIn |> List.find (fun t -> t.Definition.Name = toolName)

    let result = tool.Execute ctx args |> Async.RunSynchronously

    use doc = JsonDocument.Parse result

    match doc.RootElement.TryGetProperty "error" with
    | true, v when v.ValueKind = JsonValueKind.String ->
        match v.GetString() with
        | "PermissionDenied" -> "g1/g2"
        | "UnqueryableModule" -> "g6"
        | "UserDenied" -> "g7"
        // Past every gate: the store or bus the deployment did not compose.
        | _ -> "admitted"
    | _ -> "admitted"

// ─── The three POST handlers ─────────────────────────────────────────

let private post
    (handler: Giraffe.Core.HttpHandler)
    (services: IServiceProvider)
    (readable: string list)
    (body: string)
    =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services
    AccessContexts.stampUserId ctx.Items "alice"
    AccessContexts.stampScope ctx.Items scope
    AccessContexts.stampPermissions ctx.Items (readable |> List.map (fun m -> m, [ Read ]))
    ctx.Request.Method <- "POST"
    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    ctx.Response.Body <- new MemoryStream()

    handler (fun c -> Threading.Tasks.Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx.Response.StatusCode

/// POST a client-tool result for a call parked on `sourceModule`; the
/// status, and whether the parked call is still waiting afterwards.
let private toolResultDoor (sourceModule: string) (readable: string list) : int * bool =
    let registry = ClientToolDispatch.ClientToolDispatchRegistry()
    let services = ServiceCollection()
    services.AddSingleton(registry) |> ignore
    let id = Guid.NewGuid()
    registry.RegisterPending(id, Some sourceModule) |> ignore

    let body =
        JsonSerializer.Serialize(
            ({
                TaskId = Guid.NewGuid()
                ToolCallId = id
                ResultJson = "{}"
            }
            : ClientToolResultRequest),
            jsonOptions
        )

    let status =
        post ClientToolDispatch.clientToolResultHandler (services.BuildServiceProvider()) readable body

    status, (registry.SourceModuleOf id).IsSome

let private consentDoor (targetModule: string) (readable: string list) : int * bool =
    let registry = AIConsentDispatch.AIConsentRegistry()
    let services = ServiceCollection()
    services.AddSingleton(registry) |> ignore
    let id = Guid.NewGuid()

    registry.RegisterPending(
        id,
        {
            ConversationId = Guid.NewGuid()
            TargetModule = targetModule
            Container = scope.Container
            UserId = UserId "alice"
        }
    )
    |> ignore

    let body =
        JsonSerializer.Serialize(
            ({
                ConsentId = id
                Decision = AllowDecision.toToken AllowOnce
            }
            : AIConsentDecisionRequest),
            jsonOptions
        )

    let status =
        post AIConsentHandler.consentDecisionHandler (services.BuildServiceProvider()) readable body

    status, (registry.PendingOf id).IsSome

let private approvalDoor (sourceModule: string) (readable: string list) : int * bool =
    let registry = ToolApprovalDispatch.ToolApprovalRegistry()
    let services = ServiceCollection()
    services.AddSingleton(registry) |> ignore
    let id = Guid.NewGuid()

    registry.RegisterPending(
        id,
        {
            ConversationId = Guid.NewGuid()
            ToolName = "payroll.export"
            SourceModule = sourceModule
            ArgumentsDigest = ToolApprovalDispatch.argumentsDigest "{}"
            ArgumentsPreview = "{}"
            ScopeId = scope.ScopeId
            UserId = UserId "alice"
        }
    )
    |> ignore

    let body =
        JsonSerializer.Serialize(
            ({
                ApprovalId = id
                Decision = ToolApprovalDecision.toToken Approved
            }
            : ToolApprovalDecisionRequest),
            jsonOptions
        )

    let status =
        post ToolApprovalHandler.toolApprovalDecisionHandler (services.BuildServiceProvider()) readable body

    status, (registry.PendingOf id).IsSome

// ─── The tables ──────────────────────────────────────────────────────

/// (row name, gate state, expected refusal). Each state shuts the expected
/// gate AND every gate after it, so only the order can make the row pass.
let private loopRows = [
    "every gate shut: g1 answers", allShut, "g1"
    "Read held, the rest shut: g2 answers", { allShut with Readable = true }, "g2"
    "Read and a live grant, the rest shut: g3 answers",
    {
        allShut with
            Readable = true
            GrantLive = true
    },
    "g3"
    "within the ceiling, the allowlist and approval shut: g4 answers",
    {
        allOpen with
            AllowlistAdmits = false
            ApprovalFree = false
    },
    "g4"
    "only approval shut: g5 answers", { allOpen with ApprovalFree = false }, "g5"
    "every gate open: admitted", allOpen, "admitted"
]

/// The reach tools' rows. g3–g5 are the loop's and do not apply here.
let private reachRows = [
    "every gate shut", allShut, "g1/g2"
    "Read held, grant pending, the rest shut", { allShut with Readable = true }, "g1/g2"
    "Read and a live grant, not queryable, no consent",
    {
        allShut with
            Readable = true
            GrantLive = true
    },
    "g6"
    "queryable, the user refused", { allOpen with Consented = false }, "g7"
    "every gate open", allOpen, "admitted"
]

/// The rows a reach tool answers differently from the table, with why.
/// Each is a gate the tool does not run today.
let private reachExceptions: Map<string * string, string * string> =
    Map.ofList [
        // query_module asks the bus, which checks Read; it never asks g2.
        ("_platform.ai.query_module", "Read held, grant pending, the rest shut"), ("g6", "query_module runs no g2")
        // query_entity runs no per-module RBAC at all.
        ("_platform.ai.query_entity", "every gate shut"), ("g6", "query_entity runs no g1")
        ("_platform.ai.query_entity", "Read held, grant pending, the rest shut"), ("g6", "query_entity runs no g2")
    ]

let private reachTools = [
    "_platform.ai.query_module", """{"moduleName":"Payroll","queryKey":"latest"}"""
    "_platform.ai.query_entity", """{"entityType":"salary"}"""
    "_platform.ai.list_results", """{"moduleName":"Payroll"}"""
    "_platform.ai.get_latest_result", """{"moduleName":"Payroll","resultType":"summary"}"""
]

let tests =
    testList "the gate order, at every door" [
        testList "the loop's dispatch arm" [
            for name, state, expected in loopRows ->
                testCase name
                <| fun () -> Expect.equal (loopRefusal (loopDoor state)) expected "the refusal the model is shown"
        ]

        testList "the four reach tools" [
            for toolName, args in reachTools do
                for name, state, expected in reachRows do
                    let expected, why =
                        match reachExceptions.TryFind(toolName, name) with
                        | Some(today, why) -> today, " (" + why + ")"
                        | None -> expected, ""

                    yield
                        testCase $"{toolName}: {name}{why}"
                        <| fun () ->
                            Expect.equal (reachDoor toolName args state) expected "the refusal the tool returns"
        ]

        testList "the three POST handlers check g1 before completing" [
            for door, run in
                [
                    "/api/ai/tool-result", toolResultDoor
                    "/api/ai/consent", consentDoor
                    "/api/ai/tool-approval", approvalDoor
                ] do
                yield
                    testCase $"{door}: no Read on the recorded module refuses and leaves the call parked"
                    <| fun () -> Expect.equal (run "Payroll" [ "Other" ]) (403, true) "refused before completing"

                yield
                    testCase $"{door}: Read on the recorded module completes"
                    <| fun () -> Expect.equal (run "Payroll" [ "Payroll" ]) (200, false) "completed"
        ]
    ]