// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AIAgentEngineClientResidentAuthorizationTests

// ─── Phase 46 — agent-loop dispatch-enumeration regression test ──────
//
// Asserts the structural promise that *every* `ClientResident` tool
// dispatch path in `AIAgentEngine.runAgentLoop` flows through the
// `IClientToolAuthorizer` consult before any `ClientToolInvoke` SSE
// event is emitted. The contract pack
// (`IClientToolAuthorizerContract`) verifies a *single authorizer's*
// decision-shape invariants; this test verifies the *loop's* behaviour
// when a denying authorizer is wired:
//
//   1. No `ClientToolInvoke` SSE is emitted (the deny short-circuits
//      before the registry registration + SSE emit).
//   2. The model receives a `Denied`-shaped tool result via the
//      `ToolCallCompleted` SSE — surfaced through
//      `ToolInvocationError.toToolResultContent` as "Tool 'X' was
//      denied: …".
//   3. A `_platform.ai.tool_allowlist_denial` ModuleEvent lands in
//      the configured `IEventStore`.
//
// A deliberately-introduced bypass branch in the loop (one that
// `RegisterPending` + emits `ClientToolInvoke` without consulting the
// authorizer) would break assertion (1) — the regression bar Phase 46
// requires.

open System
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

// ─── Test-only fakes ──────────────────────────────────────────────────

/// Deny-all authorizer — every consult returns `Deny`. The contract
/// pack covers the per-decision invariants of *any* authorizer; this
/// stub exists only to force the loop into the deny branch so the SSE
/// + audit + tool-result shape can be observed.
type private DenyAllAuthorizer() =
    interface IClientToolAuthorizer with
        member _.Authorize(_toolName, _argsJson, _activeModule, _activePage) = Deny "deny-all (test-only authorizer)"

/// Minimal `IAIProvider` that emits one client-resident tool call on
/// the first `SendMessage` and ends the conversation on the second.
/// Inputs are ignored — the provider drives the loop by its own
/// internal counter so the test doesn't have to model message-history
/// state.
type private CannedToolCallProvider(toolNameToCall: string) =
    let mutable callCount = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-canned"
            Model = "test-canned-model"
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

        // Phase 67b — these tests don't exercise structured-output;
        // the structured path delegates to the default fallback for
        // symmetry. Tests that care about structured-output live in
        // the dedicated 67b contract pack.
        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

// ─── Test harness ────────────────────────────────────────────────────

let private buildHttpContext (eventStore: IEventStore) (authorizer: IClientToolAuthorizer) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IEventStore>(eventStore) |> ignore
    services.AddSingleton<IClientToolAuthorizer>(authorizer) |> ignore
    let provider = services.BuildServiceProvider()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx :> HttpContext

let private buildToolRegistry (toolName: string) : AIToolRegistry.AIToolRegistry =
    let registry = AIToolRegistry.AIToolRegistry()

    let definition: AIToolDefinition = {
        Name = toolName
        Description = "Test-only client-resident tool"
        Parameters = []
        SourceModule = "test-module"
        EmitsActions = None
        Location = ClientResident
        Surface = Both
        IsLiveInterface = false
    }

    let executor _ctx _argsJson = async {
        // Should never run — the deny short-circuit must prevent
        // dispatch. If it does run, the test still fails on assertion
        // (1) below because the loop will have emitted ClientToolInvoke
        // first.
        return failtest "ClientResident tool executor must not be invoked when the authorizer denies"
    }

    registry.RegisterAll [ AIToolRegistry.createTool definition executor ]
    registry

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 46 — AIAgentEngine ClientResident dispatch flows through IClientToolAuthorizer" [
        testCaseAsync "Deny short-circuits before ClientToolInvoke, surfaces Denied tool-result, writes denial audit"
        <| async {
            let toolName = "_test.client.resident"
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let authorizer = DenyAllAuthorizer() :> IClientToolAuthorizer
            let ctx = buildHttpContext eventStore authorizer
            let registry = buildToolRegistry toolName
            let dispatchRegistry = ClientToolDispatch.ClientToolDispatchRegistry()
            let provider = CannedToolCallProvider(toolName) :> IAIProvider

            // Capture every SSE event emitted by the loop so we can
            // assert the absence of ClientToolInvoke and the presence
            // of a Denied-shaped ToolCallCompleted.
            let events = ResizeArray<AIStreamEvent>()
            let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

            let initialMessages = [ AIProviderMessage.text "user" "please do the thing" ]

            let! _finalMessages =
                AIAgentEngine.runAgentLoop
                    provider
                    registry
                    dispatchRegistry
                    ctx
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    (Some "SalesAnalysis")
                    (Some "/dashboard")
                    CancellationToken.None
                    initialMessages
                    None
                    onEvent

            let captured = lock events (fun () -> events.ToArray() |> Array.toList)

            // (1) No ClientToolInvoke SSE was emitted — the deny
            // short-circuits in `AIAgentEngine.fs` (the `Deny reason
            // -> … return Error(Denied(...))` branch) before
            // `RegisterPending` or the emit run.
            let clientToolInvokes =
                captured
                |> List.filter (function
                    | ClientToolInvoke _ -> true
                    | _ -> false)

            Expect.isEmpty
                clientToolInvokes
                "ClientToolInvoke must NOT be emitted when the authorizer denies — bypassing the consult is the regression class Phase 46 guards against"

            // (2) Model received a Denied-shaped tool result. The agent
            // loop renders `Denied` via
            // `ToolInvocationError.toToolResultContent` → "Tool 'X'
            // was denied: …" and surfaces it on `ToolCallCompleted`.
            let deniedResults =
                captured
                |> List.choose (function
                    | ToolCallCompleted(_, _, content) when content.Contains "was denied:" -> Some content
                    | _ -> None)

            Expect.isNonEmpty
                deniedResults
                "agent loop must surface the typed Denied tool-result back to the model via ToolCallCompleted"

            // (3) Denial audit row landed in the event store. The loop
            // writes one ModuleEvent per denial under
            // `_platform.ai.tool_allowlist_denial` (see
            // `AIAgentEngine.writeDenialAudit`). Scope defaults to
            // "anonymous" because the test doesn't seed ctx.Items —
            // matches `resolveLatencyScope`'s fallback.
            let! denialAudits = eventStore.ReadBySource("anonymous", "_platform.ai.tool_allowlist_denial")

            Expect.isNonEmpty
                denialAudits
                "_platform.ai.tool_allowlist_denial ModuleEvent must be written on every Deny — operator visibility is part of the trust boundary"

            let auditEvt = denialAudits |> Seq.head
            Expect.equal auditEvt.EventType "ToolAllowlistDenied" "audit event type"

            Expect.stringContains
                auditEvt.Payload
                toolName
                "audit payload must name the denied tool so operators can correlate with conversation logs"
        }
    ]