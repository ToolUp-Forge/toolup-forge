// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AIToolDispatchRbacTests

// ─── Phase 36.A — AI tool-dispatch RBAC enforcement ──────────────────
//
// Before this phase the agent loop filtered its per-turn tool list by
// SURFACE only and dispatched by name, so a caller holding no `Read` on
// a module could still have that module's tools invoked on their behalf
// — the module's own RBAC gate sat one layer below, unreached.
//
// The gate has two halves and this pack proves both, plus the symmetric
// half on the client-resident return path:
//
//   1. **List time.** `AIToolRegistry.ListAccessible` drops tools the
//      caller cannot read, so the model is never told they exist.
//   2. **Dispatch time.** A tool name the model produces anyway — the
//      forged / hallucinated / replayed case, which is the only way to
//      reach here once (1) holds — is refused with a typed `Denied`
//      before `Execute` runs, and audited.
//   3. **`/api/ai/tool-result`.** The POST that completes a
//      client-resident dispatch re-checks the same permission, so the
//      round trip cannot be closed by a caller who may not read the
//      module.
//
// The non-vacuity bar for (2) is the executor: it `failtest`s if it is
// ever invoked, so a gate that silently passed would fail the test on
// the executor rather than on an assertion.
//
// GP 11 is asserted explicitly: an empty `ModulePermissions` map is the
// pre-RBAC default and must leave every surface byte-for-byte unchanged.

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

// ─── Fixtures ─────────────────────────────────────────────────────────

let private toolDef (name: string) (sourceModule: string) (location: ToolLocation) : AIToolDefinition = {
    Name = name
    Description = "Phase 36.A test tool"
    Parameters = []
    SourceModule = sourceModule
    EmitsActions = None
    Location = location
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

/// An executor that must never run. Every deny-path test registers its
/// tool with this body, so a gate that failed open is caught here rather
/// than by a missing assertion.
let private mustNotRun _ctx _argsJson : Async<string> = async {
    return failtest "tool executor must not be invoked when the caller lacks Read on its source module"
}

let private registryOf (tools: AIToolRegistry.RegisteredTool list) =
    let registry = AIToolRegistry.AIToolRegistry()
    registry.RegisterAll tools
    registry

let private accessWith (perms: (string * ModulePermission list) list) : AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "alice") with
        ModulePermissions = Map.ofList perms
}

/// Minimal provider that emits ONE tool call on its first turn and ends
/// the conversation on the second. It ignores the tool list it is handed,
/// which is exactly the forged-name shape the dispatch re-check exists
/// for: the loop never offered this tool, and the model names it anyway.
type private ForgingProvider(toolNameToCall: string) =
    let mutable callCount = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-forging"
            Model = "test-forging-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            callCount <- callCount + 1

            if callCount = 1 then
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = toolNameToCall
                                Arguments = "{}"
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            else
                return
                    Ok {
                        Content = "done"
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

/// Background-shaped `HttpContext`: the permission map arrives on
/// `HttpContext.Items` exactly as `createBackgroundContext` copies it
/// forward. Reading it from DI instead is the failure mode
/// `reconstructAccessContext` exists to avoid, so the harness
/// deliberately does NOT register an `AccessContext` singleton.
let private buildContext (eventStore: IEventStore option) (perms: (string * ModulePermission list) list) =
    let services = ServiceCollection()

    match eventStore with
    | Some store -> services.AddSingleton<IEventStore>(store) |> ignore
    | None -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "alice"

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = "alice"
            Container = "user-alice"
            Persist = true
        }

    if not (List.isEmpty perms) then
        ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList perms)

    ctx :> HttpContext

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 36.A — AI tool-dispatch RBAC" [

        // ── 1. list-time filter ──────────────────────────────────────

        testCase "ListAccessible keeps only tools whose SourceModule the caller may read"
        <| fun () ->
            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "mood.log" "MoodJournal" ServerResident) mustNotRun
                    AIToolRegistry.createTool (toolDef "sales.forecast" "SalesAnalysis" ServerResident) mustNotRun
                ]

            let access = accessWith [ "MoodJournal", [ ModulePermission.Read ] ]

            let names =
                registry.ListAccessible access |> List.map _.Definition.Name |> List.sort

            Expect.equal
                names
                [ "mood.log" ]
                "a caller with Read on MoodJournal only must see mood-journal tools — SalesAnalysis tools are not described to the model at all"

        testCase "an empty permission map is unrestricted — ListAccessible matches GetAll exactly (GP 11)"
        <| fun () ->
            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "mood.log" "MoodJournal" ServerResident) mustNotRun
                    AIToolRegistry.createTool (toolDef "sales.forecast" "SalesAnalysis" ServerResident) mustNotRun
                ]

            let access = AccessContext.unrestricted (AuthenticatedUser "alice")

            Expect.equal
                (registry.ListAccessible access |> List.map _.Definition.Name)
                (registry.GetAll() |> List.map _.Definition.Name)
                "a deployment that has never configured RBAC must see the identical list, in the identical order"

        testCase "SDK-reserved tool sources survive the filter when the deployment has not named them"
        <| fun () ->
            // The whole `_platform.ai.*` cross-module family (Phase 36.B)
            // enforces RBAC per *target* module internally. Gating it on
            // its own SourceModule would ask whether the caller may read
            // a module called "_platform.ai", which no permission map
            // names — so it would vanish the moment RBAC was configured.
            let reserved = [
                "_platform.ai.query_module", "_platform.ai"
                "_sdk.flags.read", "_sdk.FeatureFlags"
                "narrative.list", "ToolUp.Platform"
                "kb.search", "ToolUp.KnowledgeBase"
                "algo.fit", "_algorithms"
            ]

            let registry =
                registryOf [
                    for name, source in reserved ->
                        AIToolRegistry.createTool (toolDef name source ServerResident) mustNotRun
                    AIToolRegistry.createTool (toolDef "sales.forecast" "SalesAnalysis" ServerResident) mustNotRun
                ]

            let access = accessWith [ "MoodJournal", [ ModulePermission.Read ] ]

            let names =
                registry.ListAccessible access |> List.map _.Definition.Name |> List.sort

            Expect.equal
                names
                (reserved |> List.map fst |> List.sort)
                "every SDK-reserved source must pass; the consumer module the caller cannot read must not"

        testCase "an explicitly-declared reserved source is gated — the exemption is not absolute"
        <| fun () ->
            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "algo.fit" "_algorithms" ServerResident) mustNotRun
                ]

            // The deployment named `_algorithms` in the permission map and
            // granted nothing on it: that is a deliberate gate, and it is
            // honoured over the reserved-namespace exemption.
            let access =
                accessWith [ "MoodJournal", [ ModulePermission.Read ]; "_algorithms", [] ]

            Expect.isEmpty
                (registry.ListAccessible access)
                "naming a reserved source in the permission map must re-arm the gate on it"

        testCase "Write and Admin imply Read — the hierarchy is the platform's, not a second one"
        <| fun () ->
            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "w.tool" "WriteOnly" ServerResident) mustNotRun
                    AIToolRegistry.createTool (toolDef "a.tool" "AdminOnly" ServerResident) mustNotRun
                ]

            let access =
                accessWith [
                    "WriteOnly", [ ModulePermission.Write ]
                    "AdminOnly", [ ModulePermission.Admin ]
                ]

            Expect.equal
                (registry.ListAccessible access |> List.length)
                2
                "Write and Admin satisfy a Read requirement via ModulePermission.implies — the gate must not re-implement the hierarchy"

        // ── 2. dispatch-time re-check (the security boundary) ────────

        testCaseAsync "a forged tool name for an unreadable module is refused before Execute, and audited"
        <| async {
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "sales.forecast" "SalesAnalysis" ServerResident) mustNotRun
                ]

            // Read on MoodJournal only — SalesAnalysis was never offered
            // to the model this turn, and the provider names it anyway.
            let ctx =
                buildContext (Some eventStore) [ "MoodJournal", [ ModulePermission.Read ] ]

            let events = ResizeArray<AIStreamEvent>()
            let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

            let! _final =
                AIAgentEngine.runAgentLoop
                    (ForgingProvider("sales.forecast") :> IAIProvider)
                    registry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    ctx
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    [ AIProviderMessage.text "user" "forecast my sales" ]
                    None
                    onEvent

            let captured = lock events (fun () -> events.ToArray() |> Array.toList)

            // The model is told the action is not permitted (typed
            // `Denied`), not that the tool failed — an "it failed"
            // rendering invites a retry.
            let denied =
                captured
                |> List.choose (function
                    | ToolCallCompleted(_, _, content) when content.Contains "was denied:" -> Some content
                    | _ -> None)

            Expect.isNonEmpty denied "the dispatch re-check must surface a typed Denied tool-result to the model"

            Expect.stringContains
                (List.head denied)
                "SalesAnalysis"
                "the refusal must name the module whose Read the caller lacks"

            let! audits = eventStore.ReadBySource("alice", "_platform.ai.unauthorized_tool")

            Expect.isNonEmpty
                audits
                "an unauthorized tool dispatch must land a _platform.ai.unauthorized_tool ModuleEvent — a refusal nobody can see is not an enforcement point"

            let evt = Seq.head audits
            Expect.equal evt.EventType "UnauthorizedTool" "audit event type"

            Expect.stringContains
                evt.Payload
                "sales.forecast"
                "the audit payload must name the tool so an operator can correlate with the conversation"
        }

        testCaseAsync "an authorized caller's tool dispatch is unchanged — the gate refuses, it does not break"
        <| async {
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let executed = ref 0

            let executor _ctx _args = async {
                Interlocked.Increment executed |> ignore
                return "{\"ok\":true}"
            }

            let registry =
                registryOf [
                    AIToolRegistry.createTool (toolDef "mood.log" "MoodJournal" ServerResident) executor
                ]

            let ctx =
                buildContext (Some eventStore) [ "MoodJournal", [ ModulePermission.Read ] ]

            let events = ResizeArray<AIStreamEvent>()
            let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

            let! _final =
                AIAgentEngine.runAgentLoop
                    (ForgingProvider("mood.log") :> IAIProvider)
                    registry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    ctx
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    [ AIProviderMessage.text "user" "log my mood" ]
                    None
                    onEvent

            Expect.equal
                executed.Value
                1
                "the permitted tool must execute exactly once — the gate must not refuse a caller who holds Read"

            let! audits = eventStore.ReadBySource("alice", "_platform.ai.unauthorized_tool")
            Expect.isEmpty audits "no denial audit may be written for an authorized dispatch"
        }

        // ── 3. /api/ai/tool-result — the symmetric half ──────────────

        testCase "SourceModuleOf records the dispatched tool's module without consuming the pending call"
        <| fun () ->
            let registry = ClientToolDispatch.ClientToolDispatchRegistry()
            let id = Guid.NewGuid()
            let _task = registry.RegisterPending(id, Some "SalesAnalysis")

            Expect.equal
                (registry.SourceModuleOf id)
                (Some "SalesAnalysis")
                "the source module must be readable before completion"

            Expect.isTrue
                (registry.TryComplete(id, "{}"))
                "reading the source module must not remove the entry — the handler authorizes first, completes second"

        testCase "the legacy single-argument RegisterPending stays ungated (GP 11)"
        <| fun () ->
            let registry = ClientToolDispatch.ClientToolDispatchRegistry()
            let id = Guid.NewGuid()
            let _task = registry.RegisterPending id

            Expect.equal
                (registry.SourceModuleOf id)
                None
                "a pending call registered without a source module carries none"

            Expect.isTrue (registry.TryComplete(id, "{}")) "and completes exactly as before this phase"

        testCaseAsync "a tool-result POST for an unreadable source module is 403 and does NOT complete the pending call"
        <| async {
            let dispatchRegistry = ClientToolDispatch.ClientToolDispatchRegistry()
            let toolCallId = Guid.NewGuid()
            let pending = dispatchRegistry.RegisterPending(toolCallId, Some "SalesAnalysis")

            let services = ServiceCollection()

            services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(dispatchRegistry)
            |> ignore

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()
            ctx.Items["ToolUp.UserId"] <- box "alice"
            ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList [ "MoodJournal", [ ModulePermission.Read ] ])

            let body = sprintf "{\"ToolCallId\":\"%O\",\"ResultJson\":\"{}\"}" toolCallId

            ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
            ctx.Response.Body <- new MemoryStream()

            let! _ =
                ClientToolDispatch.clientToolResultHandler (fun c -> System.Threading.Tasks.Task.FromResult(Some c)) ctx
                |> Async.AwaitTask

            Expect.equal ctx.Response.StatusCode 403 "a caller who cannot read the tool's source module must be refused"

            Expect.isFalse
                pending.IsCompleted
                "the refused result must NOT reach the suspended agent loop — a 403 that still completed the TCS would enforce nothing"

            // The agent loop's own timeout owns the pending lifetime; an
            // unauthorized POST must not be able to cancel it either.
            Expect.equal
                (dispatchRegistry.SourceModuleOf toolCallId)
                (Some "SalesAnalysis")
                "the pending call stays registered — a refusal must not double as a cancellation lever"
        }

        testCaseAsync "a tool-result POST for a readable source module completes normally"
        <| async {
            let dispatchRegistry = ClientToolDispatch.ClientToolDispatchRegistry()
            let toolCallId = Guid.NewGuid()
            let pending = dispatchRegistry.RegisterPending(toolCallId, Some "MoodJournal")

            let services = ServiceCollection()

            services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(dispatchRegistry)
            |> ignore

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()
            ctx.Items["ToolUp.UserId"] <- box "alice"
            ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList [ "MoodJournal", [ ModulePermission.Read ] ])

            let body =
                sprintf "{\"ToolCallId\":\"%O\",\"ResultJson\":\"{\\\"ok\\\":true}\"}" toolCallId

            ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
            ctx.Response.Body <- new MemoryStream()

            let! _ =
                ClientToolDispatch.clientToolResultHandler (fun c -> System.Threading.Tasks.Task.FromResult(Some c)) ctx
                |> Async.AwaitTask

            Expect.equal ctx.Response.StatusCode 200 "an authorized tool-result POST is unaffected by the gate"
            Expect.isTrue pending.IsCompleted "and the suspended agent loop is resumed"
        }
    ]