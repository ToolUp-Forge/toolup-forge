// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IClientToolDispatchContract

// ─── ClientResident dispatch round-trip conformance pack ─────────────
//
// A reusable Expecto list covering the full `ClientResident` tool
// dispatch lifecycle from the agent-loop side:
//
//   1. Server-side `IClientToolAuthorizer` consult (Allow or Deny).
//   2. On Allow: `ClientToolInvoke` SSE emission + `ClientToolDispatch
//      .ClientToolDispatchRegistry.RegisterPending`.
//   3. Simulated client returns a result (test caller supplies the
//      `Simulator` function; pack calls `TryComplete` on the registry
//      with the returned JSON).
//   4. Agent loop resumes; result envelope returns to the caller.
//   5. On Deny: short-circuit before any SSE emit + denial audit
//      written to `IEventStore`.
//
// The pack is companion-agnostic — callers hand it the authorizer
// (denying one tool, allowing another) and a client simulator. The
// pack owns the rest: a scripted `IAIProvider`, the tool registry,
// the dispatch registry, the `IEventStore`, the `HttpContext`.
//
// Six-rule portability audit covered:
//   • Rule 1 (identity-by-value)    — assertion 3 (distinct
//     `ToolCallId` Guids per invocation; no live SSE-handle
//     leakage to handler state).
//   • Rule 2 (async at every method) — structural; assertion 1's
//     `let!` over `runAgentLoop` is the compile-time proof.
//   • Rule 3 (retry-as-data)        — assertion 2 — denials surface
//     as `Denied of toolName * reason` values, never thrown.
//   • Rule 4 (stateless between)    — assertion 4 — completing one
//     pending TCS in the registry does not affect another.
//   • Rule 5 (no cross-shard order) — assertion 3 also exercises
//     parallel ordering — distinct calls land in arbitrary order
//     without interference.
//   • Rule 6 (precision)            — documented at the SSE-tick
//     lower bound (90 s `ClientResidentToolTimeoutMs` in
//     `AIAgentEngine.fs`); not directly asserted because no test
//     can wait 90 s in the suite.
//
// The loop harness below (`runClientToolLoop` and the scripted-turn
// helpers) is public and shared: Phase 540's `IUiInspectionContract`
// drives the same loop through it to pin the inspection semantics of a
// read-only live-interface tool on top of this pack's dispatch semantics.
//
// Phase 46.B will add a binding to the in-tree
// `ToolUp.AI.SampleClientTool` reference companion against this pack;
// today the only binding is a deny-only authorizer stub
// (`InProcess/ClientToolDispatchContractBindings.fs`).

open System
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

/// A simulated client. Given a `ClientToolInvoke` event, returns the
/// JSON result the browser would POST back to `/api/ai/tool-result`.
/// `None` means "do not complete the pending TCS" — used to drive the
/// agent loop into its 90 s timeout path (not exercised by the pack
/// today, but the shape is in place for future tests).
type ClientSimulator = AIStreamEvent -> string option

/// Everything the pack needs to drive the dispatch round-trip end to
/// end against a candidate authorizer + simulator pair.
type ClientToolDispatchContractFixture = {
    /// Human label, suffixed onto the test-list name.
    Name: string
    /// Authorizer under test. Must allow `AllowedToolName` and deny
    /// `DeniedToolName` — those are the two anchor names the pack's
    /// scripted provider emits.
    Authorizer: IClientToolAuthorizer
    /// Tool the fixture's authorizer policy allows. Used to drive
    /// the round-trip's success path.
    AllowedToolName: string
    /// Tool the fixture's authorizer policy denies. Used to drive
    /// the short-circuit path.
    DeniedToolName: string
    /// Handler for `ClientToolInvoke` events. The pack wires it into
    /// the `onEvent` callback and calls `TryComplete` on the
    /// dispatch registry when the simulator returns `Some result`.
    Simulator: ClientSimulator
}

// ─── Internal test infrastructure ────────────────────────────────────

/// Build a one-tool `AIToolRegistry` with `Location = ClientResident`
/// — the only registry shape Phase 46.A exercises. Two tool names so
/// the Allow + Deny scripts can both find a matching registry entry.
let private buildRegistry (allowedName: string) (deniedName: string) : AIToolRegistry.AIToolRegistry =
    let registry = AIToolRegistry.AIToolRegistry()

    let mkDef name = {
        Name = name
        Description = "Pack-supplied test tool"
        Parameters = []
        SourceModule = "contract-test"
        EmitsActions = None
        Location = ClientResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = UndeclaredEffects
    }

    // Executor never runs — Allow path completes via the simulator's
    // `TryComplete`; Deny path short-circuits before dispatch.
    let executor _ctx _argsJson = async { return failtest "ClientResident executor must not be invoked on either path" }

    registry.RegisterAll [
        AIToolRegistry.createTool (mkDef allowedName) executor
        AIToolRegistry.createTool (mkDef deniedName) executor
    ]

    registry

let private buildHttpContext (eventStore: IEventStore) (authorizer: IClientToolAuthorizer) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IEventStore>(eventStore) |> ignore
    services.AddSingleton<IClientToolAuthorizer>(authorizer) |> ignore
    let provider = services.BuildServiceProvider()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx :> HttpContext

/// Scripted `IAIProvider` — emits one response per `SendMessage` call,
/// drawn in order from the supplied list. The pack's tests own the script
/// content so the agent loop's branching is fully deterministic. The only
/// input read is the per-turn tool list, recorded in `offered` so a pack
/// can assert what the model was shown (Phase 540's surface assertion).
type private ScriptedProvider(script: AIProviderResponse list, offered: ResizeArray<string list>) =
    let mutable callCount = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-scripted"
            Model = "test-scripted-model"
        }

        member _.SendMessage(_messages, tools, _systemPrompt, _onStream, _retryPolicy) = async {
            lock offered (fun () -> offered.Add(tools |> List.map _.Name))
            let response = script[callCount]
            callCount <- callCount + 1
            return Ok response
        }

        // Phase 67b — script-driven; structured-output requests draw
        // from the same script. The pack's tests don't exercise
        // schema-conformance directly here; that's the
        // IAIProviderContract's job in ToolUp.Platform.Tests/Contracts.
        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            let response = script[callCount]
            callCount <- callCount + 1
            return Ok response
        }

// ─── Shared loop harness (Phase 540) ─────────────────────────────────
//
// Public so the Phase 540 `IUiInspectionContract` pack drives the SAME
// agent loop, scripted provider, `HttpContext` and `IEventStore` wiring
// this pack does, rather than a second copy of it that could drift.

/// A model tool call naming `name`, with `{}` arguments.
let mkToolCall (name: string) : AIProviderToolCall = {
    Id = Guid.NewGuid().ToString()
    Name = name
    Arguments = "{}"
}

/// A model tool call naming `name`, with the given JSON arguments.
let mkToolCallWith (name: string) (argumentsJson: string) : AIProviderToolCall = {
    mkToolCall name with
        Arguments = argumentsJson
}

/// The turn that ends the loop.
let endTurnResponse: AIProviderResponse = {
    Content = "done"
    ToolCalls = []
    StopReason = "end_turn"
    Usage = None
}

/// A turn asking for `toolCalls`.
let toolUseResponse (toolCalls: AIProviderToolCall list) : AIProviderResponse = {
    Content = ""
    ToolCalls = toolCalls
    StopReason = "tool_use"
    Usage = None
}

/// One request through the loop: the tools it registers, the authorizer
/// the request's services carry, and the request's surface and context.
type ClientToolLoopRequest = {
    Tools: AIToolRegistry.RegisteredTool list
    Authorizer: IClientToolAuthorizer
    Surface: AISurface
    ActiveModule: string option
    ActivePage: string option
}

/// What one loop run produced.
type ClientToolLoopRun = {
    /// Every SSE event the loop emitted, in order.
    Events: AIStreamEvent list
    /// The store the loop's audit rows were written to.
    EventStore: IEventStore
    /// The provider tool names the model was offered, one list per
    /// provider turn, in order.
    OfferedTools: string list list
}

/// Drive `runAgentLoopWithInput` once with the given script + simulator.
/// The simulator hook on `onEvent` resolves the pending TCS as
/// `ClientToolInvoke` events arrive, so the loop's `Task.WhenAny` awakens
/// without hitting the 90 s timeout.
let runClientToolLoop
    (request: ClientToolLoopRequest)
    (script: AIProviderResponse list)
    (simulator: ClientSimulator)
    : Async<ClientToolLoopRun> =
    async {
        let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let ctx = buildHttpContext eventStore request.Authorizer
        let registry = AIToolRegistry.AIToolRegistry()
        registry.RegisterAll request.Tools
        let dispatchRegistry = ClientToolDispatch.ClientToolDispatchRegistry()
        let offered = ResizeArray<string list>()
        let provider = ScriptedProvider(script, offered) :> IAIProvider

        let events = ResizeArray<AIStreamEvent>()

        let onEvent (evt: AIStreamEvent) =
            lock events (fun () -> events.Add evt)

            match evt with
            | ClientToolInvoke(_, toolCallId, _, _, _, _) ->
                match simulator evt with
                | Some resultJson -> dispatchRegistry.TryComplete(toolCallId, resultJson) |> ignore
                | None -> ()
            | _ -> ()

        let initialMessages = [ AIProviderMessage.text "user" "test" ]

        let! _final =
            AIAgentEngine.runAgentLoopWithInput
                provider
                registry
                dispatchRegistry
                ctx
                (Guid.NewGuid())
                (Guid.NewGuid())
                request.Surface
                request.ActiveModule
                request.ActivePage
                CancellationToken.None
                initialMessages
                ToolUp.AI.ModelInput.empty
                onEvent

        return {
            Events = lock events (fun () -> events.ToArray() |> Array.toList)
            EventStore = eventStore
            OfferedTools = lock offered (fun () -> offered.ToArray() |> Array.toList)
        }
    }

/// This pack's fixed request shape over `runClientToolLoop`: the fixture's
/// two anchor tools, full-page surface, a fixed active module and page.
///
/// Returns the captured event stream so the caller can assert on its
/// shape. `eventStore` is exposed so the caller can read denial-audit
/// rows after the loop finishes.
let private driveLoop
    (fixture: ClientToolDispatchContractFixture)
    (script: AIProviderResponse list)
    : Async<AIStreamEvent list * IEventStore> =
    async {
        let! run =
            runClientToolLoop
                {
                    Tools = (buildRegistry fixture.AllowedToolName fixture.DeniedToolName).GetAll()
                    Authorizer = fixture.Authorizer
                    Surface = AISurface.FullPage
                    ActiveModule = Some "TestModule"
                    ActivePage = Some "/test"
                }
                script
                fixture.Simulator

        return run.Events, run.EventStore
    }

// ─── Tests ───────────────────────────────────────────────────────────

let tests (f: ClientToolDispatchContractFixture) : Test =
    testList $"IClientToolDispatch contract — {f.Name}" [

        testCaseAsync "(1) Allow round-trip — ClientToolInvoke emitted + simulator result reaches agent loop"
        <| async {
            let script = [ toolUseResponse [ mkToolCall f.AllowedToolName ]; endTurnResponse ]

            let! captured, _ = driveLoop f script

            let invokes =
                captured
                |> List.choose (function
                    | ClientToolInvoke(_, _, name, _, _, _) -> Some name
                    | _ -> None)

            Expect.equal
                invokes
                [ f.AllowedToolName ]
                "exactly one ClientToolInvoke for the allowed tool — the Allow round-trip is the canonical success path"

            // The simulator's TryComplete fed a result back; the loop's
            // ToolCallCompleted SSE should carry it (NOT a denied /
            // errored shape).
            let completedContents =
                captured
                |> List.choose (function
                    | ToolCallCompleted(_, _, content) -> Some content
                    | _ -> None)

            Expect.isNonEmpty completedContents "ToolCallCompleted must fire when the simulator returns a result"

            for content in completedContents do
                Expect.isFalse
                    (content.Contains "was denied:")
                    "Allow path must not surface a Denied-shaped tool-result"

                Expect.isFalse
                    (content.Contains "Client did not respond within")
                    "Allow path must not surface a timeout-shaped tool-result — simulator should have completed the TCS"
        }

        testCaseAsync "(2) Deny short-circuit — no ClientToolInvoke + Denied tool-result + denial audit"
        <| async {
            let script = [ toolUseResponse [ mkToolCall f.DeniedToolName ]; endTurnResponse ]

            let! captured, eventStore = driveLoop f script

            // No SSE emit at all on the deny path.
            let invokes =
                captured
                |> List.filter (function
                    | ClientToolInvoke _ -> true
                    | _ -> false)

            Expect.isEmpty invokes "Deny must short-circuit before the ClientToolInvoke emit"

            // Typed `Denied`-shaped result returned to the model.
            let deniedResults =
                captured
                |> List.choose (function
                    | ToolCallCompleted(_, _, content) when content.Contains "was denied:" -> Some content
                    | _ -> None)

            Expect.isNonEmpty
                deniedResults
                "model must receive a Denied-shaped tool-result (the retry-as-data shape, rule 3 — denials surface as values, not exceptions)"

            // Denial audit row written.
            let! denialAudits = eventStore.ReadBySource("anonymous", "_platform.ai.tool_allowlist_denial")

            Expect.isNonEmpty denialAudits "deny path must write a _platform.ai.tool_allowlist_denial audit event"

            let auditPayload = (Seq.head denialAudits).Payload

            Expect.stringContains auditPayload f.DeniedToolName "denial audit payload must name the refused tool"
        }

        testCaseAsync
            "(3) Identity-by-value — distinct tool calls in one turn receive distinct ToolCallIds (rules 1 + 5)"
        <| async {
            // Two AllowedToolName calls in a single tool-use turn.
            // Each must register its own pending TCS keyed by a
            // distinct Guid (identity-by-value), and the parallel
            // dispatch in the agent loop must complete both
            // independently (no cross-call ordering).
            let script = [
                toolUseResponse [ mkToolCall f.AllowedToolName; mkToolCall f.AllowedToolName ]
                endTurnResponse
            ]

            let! captured, _ = driveLoop f script

            let toolCallIds =
                captured
                |> List.choose (function
                    | ClientToolInvoke(_, toolCallId, _, _, _, _) -> Some toolCallId
                    | _ -> None)

            Expect.equal
                (List.length toolCallIds)
                2
                "two tool calls in one turn must produce two ClientToolInvoke events"

            Expect.equal
                (List.length (List.distinct toolCallIds))
                2
                "ToolCallIds must be distinct across concurrent invocations (identity-by-value, rule 1; no cross-call ordering, rule 5)"

            // Both should complete cleanly via the simulator.
            let completedCount =
                captured
                |> List.filter (function
                    | ToolCallCompleted _ -> true
                    | _ -> false)
                |> List.length

            Expect.equal
                completedCount
                2
                "both parallel pending TCS must complete via the simulator's TryComplete (rule 4 — dispatcher is stateless between TCS keys)"
        }

        testCase "(4) Stateless dispatcher — completing one pending TCS does not affect another (rule 4)"
        <| fun _ ->
            // Direct exercise of the dispatch registry: register two
            // pending TCS, complete one, assert the other remains
            // pending. The agent loop's correct behaviour over the
            // registry is tested in (3); this assertion isolates the
            // registry's own per-key independence to make the rule's
            // failure mode locatable on a regression.
            let registry = ClientToolDispatch.ClientToolDispatchRegistry()
            let id1 = Guid.NewGuid()
            let id2 = Guid.NewGuid()

            let t1 = registry.RegisterPending id1
            let t2 = registry.RegisterPending id2

            let completed = registry.TryComplete(id1, "first result")
            Expect.isTrue completed "TryComplete on a registered id must succeed"

            Expect.isTrue t1.IsCompleted "completed pending TCS must finish synchronously"

            Expect.isFalse
                t2.IsCompleted
                "completing one TCS must not affect another (rule 4: stateless between invocations)"

            // Clean up the second TCS so the test doesn't leave a
            // dangling pending task in the registry (cosmetic — the
            // registry is GC'd at the end of the test).
            registry.TryComplete(id2, "second result") |> ignore
    ]