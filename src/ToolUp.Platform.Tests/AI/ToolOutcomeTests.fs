// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.ToolOutcomeTests

// ─── One tool-fault vocabulary the agent loop's rails can see ────────
//
// Every way a tool call can fail is one `ToolOutcome` value, rendered to
// the model by `ToolOutcome.toModel` and read by the loop's rails through
// `ToolOutcome.isError`. Three things are pinned here:
//
//   * the rails — a browser-side fault and a built-in tool's argument
//     refusal both advance the consecutive-errored-turns stop and are
//     recorded `Errored = true` on the latency row;
//   * the bytes — `toModel` reproduces, byte for byte, the prose the loop
//     rendered and the JSON bodies the browser and the built-in tools
//     rendered before the vocabulary existed;
//   * the classification — the legacy `require*` helpers and the
//     narrative tools' argument checks land on the argument-fault path.

open System
open System.Text.Json
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI

// ─── The loop harness ────────────────────────────────────────────────

/// A provider that asks for `toolName` on every turn, so only the loop's
/// own rails can end the conversation.
type private InsistentProvider(toolName: string, arguments: string) =
    let mutable calls = 0

    member _.Calls = calls

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-insistent"
            Model = "test-insistent-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            Interlocked.Increment(&calls) |> ignore

            return
                Ok {
                    Content = ""
                    ToolCalls = [
                        {
                            Id = Guid.NewGuid().ToString()
                            Name = toolName
                            Arguments = arguments
                        }
                    ]
                    StopReason = "tool_use"
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

let private definition (name: string) (location: ToolLocation) : AIToolDefinition = {
    Name = name
    Description = "test tool"
    Parameters = []
    SourceModule = "test-module"
    EmitsActions = None
    Location = location
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = UndeclaredEffects
}

type private LoopRun = {
    ProviderCalls: int
    Completed: string list
    Timings: ToolCallTiming list
    StoppedEarly: bool
    Failure: exn option
}

/// Run the loop over `tools`, completing every client-resident dispatch
/// with `clientResult`, and report what the rails did.
let private runLoop
    (tools: AIToolRegistry.RegisteredTool list)
    (toolName: string)
    (arguments: string)
    (clientResult: string)
    =
    async {
        let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let services = ServiceCollection()
        services.AddSingleton<IEventStore>(eventStore) |> ignore
        let ctx = DefaultHttpContext()
        ctx.RequestServices <- services.BuildServiceProvider()

        let registry = AIToolRegistry.AIToolRegistry()
        registry.RegisterAll tools
        let dispatch = ClientToolDispatch.ClientToolDispatchRegistry()
        let provider = InsistentProvider(toolName, arguments)
        let events = ResizeArray<AIStreamEvent>()

        let onEvent (evt: AIStreamEvent) =
            lock events (fun () -> events.Add evt)

            match evt with
            | ClientToolInvoke(_, toolCallId, _, _, _, _) -> dispatch.TryComplete(toolCallId, clientResult) |> ignore
            | _ -> ()

        let! outcome =
            AIAgentEngine.runAgentLoopWithInput
                provider
                registry
                dispatch
                ctx
                (Guid.NewGuid())
                (Guid.NewGuid())
                AISurface.FullPage
                None
                None
                CancellationToken.None
                [ AIProviderMessage.text "user" "do the thing" ]
                ModelInput.empty
                onEvent
            |> Async.Catch

        let! latency = eventStore.ReadBySource("anonymous", AILatencyRecord.SourceModule)
        let options = FableConverters.create ()

        let timings =
            latency
            |> List.collect (fun e -> (JsonSerializer.Deserialize<AILatencyRecord>(e.Payload, options)).ToolCalls)

        let captured = lock events (fun () -> List.ofSeq events)

        return {
            ProviderCalls = provider.Calls
            Completed =
                captured
                |> List.choose (function
                    | ToolCallCompleted(_, _, content) -> Some content
                    | _ -> None)
            Timings = timings
            StoppedEarly =
                captured
                |> List.exists (function
                    | MessageDelta(_, text) -> text.StartsWith "[Agent stopped early]"
                    | _ -> false)
            Failure =
                match outcome with
                | Choice1Of2 _ -> None
                | Choice2Of2 ex -> Some ex
        }
    }

let private expectRailTripped (run: LoopRun) (what: string) =
    Expect.isNonEmpty run.Completed $"{what}: the model was shown each call's result"
    Expect.isNone run.Failure $"{what}: the loop ends through its rail, not by running out of turns"
    Expect.isTrue run.StoppedEarly $"{what}: the consecutive-errored-turns stop fired"
    Expect.equal run.ProviderCalls 2 $"{what}: two errored turns, then the stop"
    Expect.isNonEmpty run.Timings $"{what}: the latency rows carry the tool calls"

    for timing in run.Timings do
        Expect.isTrue timing.Errored $"{what}: the latency row records the call as errored"

/// The browser's envelope for a throwing executor, exactly as it posts it.
let private clientThrewEnvelope =
    """{"error":"ToolThrew","tool":"_test.client.tool","detail":"boom"}"""

let private railTests =
    testList "the rails see every fault source" [

        testCaseAsync "a client-resident tool that threw in the browser advances the errored-turns rail"
        <| async {
            let tool =
                AIToolRegistry.createTool (definition "_test.client.tool" ClientResident) (fun _ _ -> async {
                    return failtest "a client-resident body never runs on the server"
                })

            let! run = runLoop [ tool ] "_test.client.tool" "{}" clientThrewEnvelope

            Expect.allEqual run.Completed clientThrewEnvelope "the model reads the envelope the browser sent"
            expectRailTripped run "browser fault"
        }

        testCaseAsync "a built-in tool that refuses its arguments advances the errored-turns rail"
        <| async {
            let! run = runLoop PlatformAITools.builtIn "_platform.ai.list_results" "{}" ""

            Expect.allEqual
                run.Completed
                """{"error":"InvalidArguments","message":"Required argument 'moduleName' is missing."}"""
                "the model reads the body the tool has always rendered"

            expectRailTripped run "argument refusal"
        }

        testCaseAsync "a tool that produced content does not trip the rail"
        <| async {
            // The rail's control: a body that answers keeps the loop going
            // until the turn cap, so the stop above is the fault's doing.
            let tool =
                AIToolRegistry.createTool (definition "_test.answers" ServerResident) (fun _ _ -> async {
                    return """{"ok":true}"""
                })

            let! run = runLoop [ tool ] "_test.answers" "{}" ""

            Expect.isFalse run.StoppedEarly "no errored-turns stop over successful calls"

            for timing in run.Timings do
                Expect.isFalse timing.Errored "a produced result is not errored"
        }
    ]

let private classificationTests =
    testList "argument faults land on the argument-fault path" [

        testCaseAsync "the legacy require helpers are classified as invalid arguments"
        <| async {
            let tool =
                AIToolRegistry.createTool (definition "_test.legacy" ServerResident) (fun _ args -> async {
                    use doc = JsonDocument.Parse args
                    return ToolHelpers.requireString doc.RootElement "name" "Pass a name."
                })

            let! run = runLoop [ tool ] "_test.legacy" "{}" ""
            Expect.isNonEmpty run.Completed "the call ran"

            Expect.allEqual
                run.Completed
                "Tool '_test.legacy' received invalid arguments: Required argument 'name' is missing. Pass a name."
                "a missing argument tells the model to repair its arguments"
        }

        test "every legacy require helper raises the argument fault, not a bare exception" {
            use doc = JsonDocument.Parse """{"text":5,"number":"x"}"""
            let args = doc.RootElement

            let raisesArgumentError (call: unit -> unit) (what: string) =
                Expect.throwsT<ToolHelpers.ToolArgumentError> call what

            raisesArgumentError
                (fun () -> ToolHelpers.requireString args "missing" "" |> ignore)
                "requireString, absent"

            raisesArgumentError (fun () -> ToolHelpers.requireString args "text" "" |> ignore) "requireString, mistyped"

            raisesArgumentError
                (fun () -> ToolHelpers.requireDecimal args "missing" "" |> ignore)
                "requireDecimal, absent"

            raisesArgumentError
                (fun () -> ToolHelpers.requireDecimal args "number" "" |> ignore)
                "requireDecimal, mistyped"

            raisesArgumentError
                (fun () -> ToolHelpers.requireNumber args "missing" "" |> ignore)
                "requireNumber, absent"

            raisesArgumentError
                (fun () -> ToolHelpers.requireNumber args "number" "" |> ignore)
                "requireNumber, mistyped"

            raisesArgumentError
                (fun () -> ToolHelpers.requireRawJson args "missing" "array" "" |> ignore)
                "requireRawJson, absent"
        }

        testCaseAsync "get_narrative refuses an unparseable argument body as a missing id"
        <| async {
            let! run = runLoop NarrativeTools.builtInTools "get_narrative" "not json" ""

            Expect.allEqual
                run.Completed
                """{"error":"Required argument 'id' is missing. Call list_narratives first to get valid ids."}"""
                "the same refusal a body without an id gets"

            expectRailTripped run "get_narrative"
        }

        testCaseAsync "list_narratives refuses an unparseable argument body instead of listing with defaults"
        <| async {
            let! run = runLoop NarrativeTools.builtInTools "list_narratives" "not json" ""

            for content in run.Completed do
                Expect.stringStarts content """{"error":"Arguments were not valid JSON""" "a refusal, not a listing"

            expectRailTripped run "list_narratives"
        }
    ]

// ─── The bytes the model sees ────────────────────────────────────────

/// Strings that exercise every escaping rule a rendering applies.
let private corpus = [
    "plain"
    "it's \"quoted\""
    @"back\slash"
    "line\nbreak and\r\ncarriage"
    "tab\there"
    "bell\u0007 and nul\u0000 and unit\u001f"
    "accents: café, naïve — and an emoji 🙂"
    "<script>&amp;+'"
    ""
]

let private goldenTests =
    testList "toModel reproduces what the model has always been shown" [

        test "every server-side invocation error renders the prose the loop rendered" {
            let golden = [
                ToolOutcome.Faulted(ToolFault.UnknownTool "mystery"), "Unknown tool: mystery"
                ToolOutcome.Faulted(ToolFault.Threw("calc", "division by zero")), "Tool 'calc' failed: division by zero"
                ToolOutcome.Faulted(ToolFault.InvalidArguments("calc", "'x' must be a number")),
                "Tool 'calc' received invalid arguments: 'x' must be a number"
                ToolOutcome.Refused(ToolRefusal.Denied("calc", "requires Read on module 'Payroll'")),
                "Tool 'calc' was denied: requires Read on module 'Payroll'"
            ]

            for outcome, expected in golden do
                Expect.equal (ToolOutcome.toModel outcome) expected "byte-identical prose"
        }

        test "the loop's own error type is a projection of the same rendering" {
            let cases = [
                AIAgentEngine.UnknownTool "mystery", "Unknown tool: mystery"
                AIAgentEngine.ToolThrew("calc", "boom"), "Tool 'calc' failed: boom"
                AIAgentEngine.InvalidArguments("calc", "bad"), "Tool 'calc' received invalid arguments: bad"
                AIAgentEngine.Denied("calc", "no"), "Tool 'calc' was denied: no"
            ]

            for err, expected in cases do
                Expect.equal (AIAgentEngine.ToolInvocationError.toToolResultContent err) expected "projection"
        }

        test "a built-in tool's argument refusal renders the body its serialiser wrote" {
            for message in corpus do
                Expect.equal
                    (ToolOutcome.toModel (
                        ToolOutcome.Faulted(
                            ToolFault.ArgumentsRefused [ "error", "InvalidArguments"; "message", message ]
                        )
                    ))
                    (ToolHelpers.fableSerialize {|
                        error = "InvalidArguments"
                        message = message
                    |})
                    $"byte-identical body for {message}"

                Expect.equal
                    (ToolOutcome.toModel (
                        ToolOutcome.Faulted(ToolFault.ArgumentsRefused [ "error", message; "id", "x" ])
                    ))
                    (ToolHelpers.fableSerialize {| error = message; id = "x" |})
                    $"byte-identical narrative body for {message}"
        }

        test "a browser fault renders the envelope the browser built by hand" {
            // The browser's own escaping covered `\`, `"`, LF and CR; on
            // every string it rendered as valid JSON the bytes are equal.
            let legacyEnvelope (kind: string) (tool: string) (detail: string) =
                let escape (s: string) =
                    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

                $"""{{"error":"{escape kind}","tool":"{escape tool}","detail":"{escape detail}"}}"""

            let hasOtherControl (s: string) =
                s |> Seq.exists (fun c -> c < ' ' && c <> '\n' && c <> '\r')

            for detail in corpus |> List.filter (hasOtherControl >> not) do
                for fault, kind in
                    [
                        ToolFault.ClientThrew("ui.tool", detail), "ToolThrew"
                        ToolFault.ClientTimedOut("ui.tool", detail), "ClientWatchdogTimeout"
                        ToolFault.ClientToolUnregistered("ui.tool", detail), "UnknownClientTool"
                    ] do
                    Expect.equal
                        (ToolOutcome.toModel (ToolOutcome.Faulted fault))
                        (legacyEnvelope kind "ui.tool" detail)
                        $"byte-identical envelope for {kind} / {detail}"
        }

        test "a browser fault's other control characters are now escaped, so the envelope is valid JSON" {
            let rendered =
                ToolOutcome.toModel (ToolOutcome.Faulted(ToolFault.ClientThrew("ui.tool", "tab\there")))

            Expect.equal rendered """{"error":"ToolThrew","tool":"ui.tool","detail":"tab\there"}""" "a tab is \\t"
            use doc = JsonDocument.Parse rendered
            Expect.equal (doc.RootElement.GetProperty("detail").GetString()) "tab\there" "and it round-trips"
        }

        test "produced content passes through untouched, and only a fault or refusal is an error" {
            for content in corpus do
                Expect.equal (ToolOutcome.toModel (ToolOutcome.Produced content)) content "verbatim"
                Expect.isFalse (ToolOutcome.isError (ToolOutcome.Produced content)) "content is not an error"

            Expect.isTrue
                (ToolOutcome.isError (ToolOutcome.Produced null))
                "a body that returned nothing has not produced"

            Expect.isTrue (ToolOutcome.isError (ToolOutcome.Faulted(ToolFault.UnknownTool "x"))) "a fault is"
            Expect.isTrue (ToolOutcome.isError (ToolOutcome.Refused(ToolRefusal.Denied("x", "no")))) "a refusal is"
        }

        test "a fault's kind is the discriminator its rendering carries" {
            Expect.equal
                (ToolFault.kind (ToolFault.ArgumentsRefused [ "error", "InvalidArguments" ]))
                "InvalidArguments"
                "the error field"

            Expect.equal
                (ToolFault.kind (ToolFault.ClientTimedOut("x", "y")))
                "ClientWatchdogTimeout"
                "the browser's token"

            Expect.equal (ToolFault.kind (ToolFault.Threw("x", "y"))) "ToolThrew" "the loop's name for it"
        }
    ]

// ─── The typed record of tool results ────────────────────────────────

let private modelInputTests =
    testList "ModelInput.withMessages records each result's call and tool" [

        test "a tool result carries its call id and the name of the tool that call named" {
            let assistantTurn: AIProviderMessage = {
                AIProviderMessage.text "assistant" "" with
                    ToolCalls = [
                        {
                            Id = "call-1"
                            Name = "list_results"
                            Arguments = "{}"
                        }
                        {
                            Id = "call-2"
                            Name = "get_narrative"
                            Arguments = "{}"
                        }
                    ]
            }

            let resultTurn: AIProviderMessage = {
                AIProviderMessage.text "user" "" with
                    ToolResults = [
                        {
                            ToolCallId = "call-2"
                            Content = "narrative"
                        }
                        {
                            ToolCallId = "call-1"
                            Content = "rows"
                        }
                    ]
            }

            let input =
                ModelInput.withMessages
                    [ AIProviderMessage.text "user" "go"; assistantTurn; resultTurn ]
                    ModelInput.empty

            Expect.equal
                (input.ToolResults |> List.map (fun r -> r.ToolCallId, r.ToolName, r.Content))
                [ "call-2", "get_narrative", "narrative"; "call-1", "list_results", "rows" ]
                "each result names the tool its call named"
        }

        test "a result no folded call carries keeps an empty tool name" {
            let orphan: AIProviderMessage = {
                AIProviderMessage.text "user" "" with
                    ToolResults = [
                        {
                            ToolCallId = "call-9"
                            Content = "stray"
                        }
                    ]
            }

            let laterCall: AIProviderMessage = {
                AIProviderMessage.text "assistant" "" with
                    ToolCalls = [
                        {
                            Id = "call-9"
                            Name = "too_late"
                            Arguments = "{}"
                        }
                    ]
            }

            let input = ModelInput.withMessages [ orphan; laterCall ] ModelInput.empty

            Expect.equal
                (input.ToolResults |> List.map (fun r -> r.ToolCallId, r.ToolName))
                [ "call-9", "" ]
                "only calls folded before the result name it"
        }
    ]

let tests =
    testList "Phase 1076 — ToolOutcome, one tool-fault vocabulary" [
        railTests
        classificationTests
        goldenTests
        modelInputTests
    ]