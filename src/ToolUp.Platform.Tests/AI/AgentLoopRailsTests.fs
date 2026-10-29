// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.AgentLoopRailsTests

// ─── The agent loop's safety rails ───────────────────────────────────
//
// Four rails end a turn the model cannot finish on its own: the turn
// ceiling, the confusion marker with tool calls still pending, a run of
// turns whose every tool call errored, and the one outer retry after the
// provider's own budget ran out. Each is driven here to its stop against a
// scripted provider, and each pins what the client is shown: the
// `AITaskFailed` text a hard stop renders, or the streamed early-stop
// message a soft stop commits before the turn completes.
//
// The fifth case pins cancellation: a cancel requested while the provider
// call is in flight reaches the provider and ends the loop promptly,
// rather than waiting for the call to return.

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.AI
open ToolUp.AI.Wire.Conformance
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── The scripted provider ───────────────────────────────────────────

let private capabilities: AIProviderCapabilities = {
    Streaming = false
    ToolUse = true
    Vision = false
    SupportsPromptCaching = false
    SupportsTriage = false
    TriageModelId = None
    ProviderName = "test-rails"
    Model = "test-rails-model"
}

/// A provider whose every call is answered by `script` with the call's
/// one-based number.
type private ScriptedProvider(script: int -> Async<Result<AIProviderResponse, AIProviderError>>) =
    let mutable calls = 0

    member _.Calls = calls

    interface IAIProvider with
        member _.Capabilities = capabilities

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) =
            script (Interlocked.Increment(&calls))

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

let private toolCall (content: string) (toolName: string) : AIProviderResponse = {
    Content = content
    ToolCalls = [
        {
            Id = Guid.NewGuid().ToString()
            Name = toolName
            Arguments = "{}"
        }
    ]
    StopReason = "tool_use"
    Usage = None
}

let private finalReply (content: string) : AIProviderResponse = {
    Content = content
    ToolCalls = []
    StopReason = "end_turn"
    Usage = None
}

let private okTool =
    AIToolRegistry.createTool
        {
            Name = "_test.ok"
            Description = "a tool that always succeeds"
            Parameters = []
            SourceModule = "test-module"
            EmitsActions = None
            Location = ServerResident
            Surface = Both
            IsLiveInterface = false
            ResultBudget = DefaultResultBudget
            Effects = UndeclaredEffects
        }
        (fun _ _ -> async { return """{"ok":true}""" })

// ─── The loop harness ────────────────────────────────────────────────

type private LoopRun = {
    Events: AIStreamEvent list
    Messages: AIProviderMessage list option
    Failure: exn option
}

let private userTurn = AIProviderMessage.text "user" "do the thing"

let private context () =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- ServiceCollection().BuildServiceProvider()
    ctx :> HttpContext

/// Run the loop through its public entry and report what the client was
/// streamed and how the loop ended.
let private runLoop (provider: IAIProvider) (cancel: CancellationToken) = async {
    let registry = AIToolRegistry.AIToolRegistry()
    registry.RegisterAll [ okTool ]
    let events = ResizeArray<AIStreamEvent>()

    let! outcome =
        AIAgentEngine.runAgentLoopWithInput
            provider
            registry
            (ClientToolDispatch.ClientToolDispatchRegistry())
            (context ())
            (Guid.NewGuid())
            (Guid.NewGuid())
            AISurface.FullPage
            None
            None
            cancel
            [ userTurn ]
            ModelInput.empty
            (fun evt -> lock events (fun () -> events.Add evt))
        |> Async.Catch

    return {
        Events = lock events (fun () -> List.ofSeq events)
        Messages =
            match outcome with
            | Choice1Of2 messages -> Some messages
            | Choice2Of2 _ -> None
        Failure =
            match outcome with
            | Choice1Of2 _ -> None
            | Choice2Of2 ex -> Some ex
    }
}

/// The status the client is shown for a turn the loop ended by raising:
/// the turn's failure carries the loop's message verbatim.
let private failedStatus (run: LoopRun) =
    match run.Failure with
    | Some ex -> AITaskFailed ex.Message
    | None -> failtest "the loop was expected to stop the turn"

let private streamedText (run: LoopRun) =
    run.Events
    |> List.choose (function
        | MessageDelta(_, text) -> Some text
        | _ -> None)

let private earlyStopBody (reason: string) =
    $"[Agent stopped early] {reason}\n\n"
    + "Retrying the same request won't help — please clarify your instructions, "
    + "check the input data, or simplify the task and try again."

// ─── The four rails ──────────────────────────────────────────────────

let private railTests =
    testList "the four rails stop the loop with today's text" [

        testCaseAsync "the turn ceiling stops the fifteenth-plus turn and fails the task"
        <| async {
            let provider =
                ScriptedProvider(fun _ -> async { return Ok(toolCall "" "_test.ok") })

            let! run = runLoop provider CancellationToken.None

            Expect.equal provider.Calls 15 "fifteen turns run; the sixteenth is refused before any call"

            Expect.equal
                (failedStatus run)
                (AITaskFailed
                    "Agent loop exceeded 15 turns — stopping to avoid a runaway. If this was a legitimate multi-step task, try breaking it into smaller requests, or simplify the tools' return payloads so the model converges faster.")
                "the client is shown the turn-ceiling text"
        }

        testCaseAsync "a confusion marker with tool calls pending stops early and completes the turn"
        <| async {
            let provider =
                ScriptedProvider(fun _ -> async { return Ok(toolCall "I'm unable to find that column." "_test.ok") })

            let! run = runLoop provider CancellationToken.None

            Expect.equal provider.Calls 1 "the speculative calls are never made"
            Expect.isNone run.Failure "a soft stop is not a failure: the turn completes"

            Expect.contains
                (streamedText run)
                (earlyStopBody
                    "the model indicated it can't proceed (matched \"i'm unable to\") but was still planning to call tools. Calls on an uncertain plan are unlikely to succeed.")
                "the client is streamed the early-stop message naming the marker"

            Expect.equal
                run.Messages
                (Some [ userTurn ])
                "the confused response and its pending calls never reach the history"
        }

        testCaseAsync "two consecutive turns of only failing tool calls stop early and complete the turn"
        <| async {
            let provider =
                ScriptedProvider(fun _ -> async { return Ok(toolCall "" "_test.missing") })

            let! run = runLoop provider CancellationToken.None

            Expect.equal provider.Calls 2 "two errored turns, then the stop"
            Expect.isNone run.Failure "a soft stop is not a failure: the turn completes"

            Expect.contains
                (streamedText run)
                (earlyStopBody
                    "the last 2 turns had only failing tool calls (last attempted: _test.missing). The model isn't recovering from the errors.")
                "the client is streamed the early-stop message naming the failing tools"

            let tail = run.Messages |> Option.map List.last

            Expect.equal
                (tail |> Option.map (fun m -> m.Role, m.ToolResults.Length))
                (Some("user", 1))
                "the last tool round is kept, so the history's call/result pairing holds"
        }

        testCaseAsync "a provider whose own budget ran out gets one outer retry, then fails the task"
        <| async {
            let exhausted = RetriesExhausted(3, TransientServer(529, "overloaded"))

            let provider = ScriptedProvider(fun _ -> async { return Error exhausted })

            let! run = runLoop provider CancellationToken.None

            Expect.equal provider.Calls 2 "the first attempt and exactly one outer retry"

            Expect.equal
                (failedStatus run)
                (AITaskFailed "Retries exhausted after 3 attempts. Last error: Server error (HTTP 529): overloaded")
                "the client is shown the provider's error"
        }
    ]

// ─── Cancellation ────────────────────────────────────────────────────

/// A provider that blocks until the call's cancellation token fires (or
/// the test releases it), and records whether the token reached it.
type private BlockingProvider() =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable sawCancellation = false

    member _.Entered = entered.Task
    member _.SawCancellation = sawCancellation
    member _.Release() = release.TrySetResult() |> ignore

    interface IAIProvider with
        member _.Capabilities = capabilities

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            let! token = Async.CancellationToken
            // Recorded the moment the token fires, not when this call next runs.
            use _ = token.Register(fun () -> sawCancellation <- true)
            entered.TrySetResult() |> ignore

            let cancelled =
                Task.Delay(Timeout.Infinite, token).ContinueWith(fun (_: Task) -> ())

            let! _ = Task.WhenAny(cancelled, release.Task) |> Async.AwaitTask
            return Ok(finalReply "too late")
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

let private cancellationTests =
    testList "a cancellation reaches the provider" [

        testCaseAsync "a cancel requested mid-provider-call reaches the provider and ends the loop"
        <| async {
            let provider = BlockingProvider()
            use cts = new CancellationTokenSource()

            try
                let loop = runLoop provider cts.Token |> Async.StartAsTask

                let! _ = Task.WhenAny(provider.Entered, Task.Delay 5_000) |> Async.AwaitTask
                Expect.isTrue provider.Entered.IsCompleted "the provider call started"

                cts.Cancel()

                let! winner = Task.WhenAny(loop :> Task, Task.Delay 5_000) |> Async.AwaitTask

                Expect.isTrue (obj.ReferenceEquals(winner, loop)) "the loop ended promptly after the cancel"
                Expect.isTrue provider.SawCancellation "the cancellation reached the in-flight provider call"

                let run = loop.Result

                Expect.isTrue
                    (run.Events
                     |> List.exists (function
                         | StreamCancelled _ -> true
                         | _ -> false))
                    "the client is told the stream was cancelled"

                Expect.isTrue
                    (match run.Failure with
                     | Some(:? OperationCanceledException) -> true
                     | _ -> false)
                    "the public entry ends a cancelled turn with OperationCanceledException, as it always has"
            finally
                provider.Release()
        }
    ]

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private shutdownTests =
    testList "a host shutdown reaches an in-flight turn" [

        testCaseAsync "the worker's shutdown token ends a turn blocked in its provider call, inside the budget"
        <| async {
            let provider = BlockingProvider()
            use worker = new AIChatWorker.AIChatWorker(silentLogger)
            let hosted = worker :> Microsoft.Extensions.Hosting.IHostedService

            let ended =
                TaskCompletionSource<exn option>(TaskCreationOptions.RunContinuationsAsynchronously)

            try
                do! hosted.StartAsync CancellationToken.None |> Async.AwaitTask

                let item: AIChatWorker.AIChatWorkItem = {
                    TaskId = Guid.NewGuid()
                    Fingerprint = "rails"
                    Run =
                        fun shutdown -> async {
                            let! run = runLoop provider shutdown
                            ended.TrySetResult run.Failure |> ignore
                        }
                    Reject = fun _ -> ended.TrySetResult None |> ignore
                    Dispose = ignore
                }

                Expect.equal (worker.TryEnqueue item) AIChatWorker.Accepted "the turn is queued"
                let! _ = Task.WhenAny(provider.Entered, Task.Delay 5_000) |> Async.AwaitTask
                Expect.isTrue provider.Entered.IsCompleted "the provider call started"

                // A 200 ms shutdown budget: the turn cannot finish in it, so the
                // worker cancels its in-flight turns when it expires.
                use budget = new CancellationTokenSource(200)
                let stopped = Stopwatch.StartNew()
                do! hosted.StopAsync budget.Token |> Async.AwaitTask
                stopped.Stop()

                Expect.isTrue ended.Task.IsCompleted "the turn ended before the worker finished stopping"
                Expect.isTrue provider.SawCancellation "the shutdown reached the in-flight provider call"

                Expect.isLessThan
                    stopped.Elapsed
                    (TimeSpan.FromSeconds 2.0)
                    "the worker stopped inside its budget plus the settle, not after the provider call"
            finally
                provider.Release()
        }
    ]

/// The record entry returns a hard stop as a value, rendered once to the
/// same status the positional entry's exception carries.
let private recordEntryTests =
    testList "the record entry returns the stop as a value" [

        testCaseAsync "the turn ceiling is LoopStop.MaxTurns, rendered to the same AITaskFailed text"
        <| async {
            let provider =
                ScriptedProvider(fun _ -> async { return Ok(toolCall "" "_test.ok") })

            let registry = AIToolRegistry.AIToolRegistry()
            registry.RegisterAll [ okTool ]

            let turn =
                AIAgentEngine.TurnContext.create
                    provider
                    registry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    (context ())
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    ModelInput.empty
                    AIAgentEngine.AgentCallBudget.interactive
                    SystemPromptBuilder.AgentLoopLimits.defaults
                    ignore
                    CancellationToken.None

            let! result = AIAgentEngine.run turn AIAgentEngine.TurnPlan.RunLoop [ userTurn ]

            match result with
            | Error stop ->
                Expect.equal stop (AIAgentEngine.LoopStop.MaxTurns 15) "the stop is the turn ceiling"

                Expect.equal
                    (AIAgentEngine.TurnOutcome.toStatus (
                        AIAgentEngine.TurnOutcome.Failed(AIAgentEngine.TurnFailure.Stopped stop)
                    ))
                    (AITaskFailed
                        "Agent loop exceeded 15 turns — stopping to avoid a runaway. If this was a legitimate multi-step task, try breaking it into smaller requests, or simplify the tools' return payloads so the model converges faster.")
                    "rendered once, to the text the client has always been shown"
            | Ok _ -> failtest "the turn ceiling must stop the loop"
        }

        testCaseAsync "a lower MaxTurns in the limits stops the loop there"
        <| async {
            let provider =
                ScriptedProvider(fun _ -> async { return Ok(toolCall "" "_test.ok") })

            let registry = AIToolRegistry.AIToolRegistry()
            registry.RegisterAll [ okTool ]

            let turn =
                AIAgentEngine.TurnContext.create
                    provider
                    registry
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    (context ())
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    ModelInput.empty
                    AIAgentEngine.AgentCallBudget.interactive
                    {
                        SystemPromptBuilder.AgentLoopLimits.defaults with
                            MaxTurns = 3
                    }
                    ignore
                    CancellationToken.None

            let! result = AIAgentEngine.run turn AIAgentEngine.TurnPlan.RunLoop [ userTurn ]

            Expect.equal result (Error(AIAgentEngine.LoopStop.MaxTurns 3)) "the configured ceiling"
            Expect.equal provider.Calls 3 "three turns, then the stop"
        }
    ]

// ─── Phase 1131 — a tool result attaches a block ─────────────────────
//
// A server-resident tool answering the record form hands the model its
// text and attaches its parts to the assistant turn: the loop carries them
// in `LoopState.Attached` across rounds, in tool-call order; the stream
// carries each as a `BlockAttached` event when its round completes; and the
// final message carries them after its text, persisted, so a reloaded
// conversation shows each block where the stream placed it. A
// client-resident tool carrying the record form is refused by name.

let private serverDefinition (name: string) : AIToolDefinition = {
    Name = name
    Description = "a tool that attaches a block"
    Parameters = []
    SourceModule = "test-module"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = UndeclaredEffects
}

/// A server-resident tool answering `text` and attaching `part`, after
/// `delayMs` — so a slower first call proves the order is the call order,
/// not the completion order.
let private attaching (name: string) (delayMs: int) (text: string) (part: AIContentPart) =
    AIToolRegistry.createAnsweringTool (serverDefinition name) (fun _ _ -> async {
        do! Async.Sleep delayMs

        return ({ Text = text; Parts = [ part ] }: ToolHelpers.ToolAnswer)
    })

let private tableTool =
    attaching "_test.render_table" 80 "a table of revenue by site" BlockPartFixtures.tableBlock

let private trendTool =
    attaching "_test.render_trend" 0 "a trend of revenue" BlockPartFixtures.trendBlock

let private toolCalls (content: string) (toolNames: string list) : AIProviderResponse = {
    Content = content
    ToolCalls =
        toolNames
        |> List.map (fun name -> {
            Id = Guid.NewGuid().ToString()
            Name = name
            Arguments = "{}"
        })
    StopReason = "tool_use"
    Usage = None
}

/// Run one turn through the record entry over `tools`, returning the
/// loop's state and what was streamed.
let private runTurn (tools: AIToolRegistry.RegisteredTool list) (script: int -> AIProviderResponse) = async {
    let provider = ScriptedProvider(fun n -> async { return Ok(script n) })
    let registry = AIToolRegistry.AIToolRegistry()
    registry.RegisterAll tools
    let events = ResizeArray<AIStreamEvent>()

    let turn =
        AIAgentEngine.TurnContext.create
            provider
            registry
            (ClientToolDispatch.ClientToolDispatchRegistry())
            (context ())
            (Guid.NewGuid())
            (Guid.NewGuid())
            AISurface.FullPage
            None
            None
            ModelInput.empty
            AIAgentEngine.AgentCallBudget.interactive
            SystemPromptBuilder.AgentLoopLimits.defaults
            (fun evt -> lock events (fun () -> events.Add evt))
            CancellationToken.None

    let! result = AIAgentEngine.run turn AIAgentEngine.TurnPlan.RunLoop [ userTurn ]

    match result with
    | Ok state -> return state, lock events (fun () -> List.ofSeq events)
    | Error stop -> return failtestf "the turn was expected to answer, and stopped: %A" stop
}

let private attachedOf (events: AIStreamEvent list) =
    events
    |> List.choose (function
        | BlockAttached(_, part) -> Some part
        | _ -> None)

let private completedOf (events: AIStreamEvent list) =
    events
    |> List.choose (function
        | ToolCallCompleted(_, _, content) -> Some content
        | _ -> None)

let private indexWhere (predicate: AIStreamEvent -> bool) (events: AIStreamEvent list) =
    events |> List.indexed |> List.filter (snd >> predicate) |> List.map fst

/// The assistant handler over an in-memory store, with `tools` registered.
let private assistantWith (storage: IBlobStorage) (provider: IAIProvider) (tools: AIToolRegistry.RegisteredTool list) =
    let registry = AIToolRegistry.AIToolRegistry()
    registry.RegisterAll tools

    let scope: StorageScope = {
        ScopeId = "alice"
        Container = "user-alice"
        Persist = true
    }

    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    services.AddSingleton<IAIProviderFactory>(
        { new IAIProviderFactory with
            member _.Available = []
            member _.PlatformDescriptors = []
            member _.PlatformDescriptor = None
            member _.Resolve _ = async { return Ok provider }
            member _.TryResolveByLabel(_, _) = async { return Ok provider }
            member _.BuildPlatform(_, _, _) = None
        }
    )
    |> ignore

    services.AddSingleton<AIToolRegistry.AIToolRegistry>(registry) |> ignore

    services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(
        ClientToolDispatch.ClientToolDispatchRegistry()
    )
    |> ignore

    services.AddSingleton<AICancellationRegistry.AICancellationRegistry>(
        AICancellationRegistry.AICancellationRegistry()
    )
    |> ignore

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AnonymousSession "alice"))
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.UserId"] <- box "alice"
    AIAssistantHandler.aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx

let private collectStream (stream: System.Collections.Generic.IAsyncEnumerable<AIStreamEvent>) = async {
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
    let e = stream.GetAsyncEnumerator cts.Token
    let events = ResizeArray<AIStreamEvent>()
    let mutable more = true

    while more do
        let! next = e.MoveNextAsync().AsTask() |> Async.AwaitTask

        if next then events.Add e.Current else more <- false

    do! e.DisposeAsync().AsTask() |> Async.AwaitTask
    return List.ofSeq events
}

let private attachTests =
    testList "Phase 1131 — a tool result attaches a block" [

        testCaseAsync "two tools in one round attach their blocks in tool-call order, on the state and the stream"
        <| async {
            let! state, events =
                runTurn [ tableTool; trendTool ] (function
                    | 1 -> toolCalls "" [ "_test.render_table"; "_test.render_trend" ]
                    | _ -> finalReply "Revenue by site for the last delivery.")

            let blocks = [ BlockPartFixtures.tableBlock; BlockPartFixtures.trendBlock ]

            Expect.equal state.Attached blocks "the state carries both blocks in call order, not completion order"

            Expect.equal
                (AIAgentEngine.LoopState.messageParts "Revenue by site for the last delivery." state)
                BlockPartFixtures.parts
                "the final message carries the text part, then the blocks"

            Expect.equal (attachedOf events) blocks "the stream carries each block once, in call order"

            let completed =
                events
                |> indexWhere (function
                    | ToolCallCompleted _ -> true
                    | _ -> false)

            let attached =
                events
                |> indexWhere (function
                    | BlockAttached _ -> true
                    | _ -> false)

            let finished =
                events
                |> indexWhere (function
                    | MessageComplete _ -> true
                    | _ -> false)

            Expect.isLessThan (List.max completed) (List.min attached) "a block is emitted when its round completes"

            Expect.isLessThan
                (List.max attached)
                (List.last finished)
                "every block reaches the stream before the final message completes"

            Expect.equal
                (completedOf events |> List.sort)
                [ "a table of revenue by site"; "a trend of revenue" ]
                "the model is shown each tool's text"

            Expect.isFalse
                (completedOf events
                 |> List.exists (fun c -> c.Contains BlockPartFixtures.NodeMarker))
                "the node never reaches the model"
        }

        testCaseAsync "blocks attached in two rounds accumulate in round order"
        <| async {
            let! state, events =
                runTurn [ tableTool; trendTool ] (function
                    | 1 -> toolCalls "" [ "_test.render_trend" ]
                    | 2 -> toolCalls "" [ "_test.render_table" ]
                    | _ -> finalReply "done")

            let blocks = [ BlockPartFixtures.trendBlock; BlockPartFixtures.tableBlock ]
            Expect.equal state.Attached blocks "the second round's block follows the first's"
            Expect.equal (attachedOf events) blocks "and the stream placed them in the same order"
        }

        testCaseAsync "a string-returning tool attaches nothing and the final message stays plain text"
        <| async {
            let! state, events =
                runTurn [ okTool ] (function
                    | 1 -> toolCall "" "_test.ok"
                    | _ -> finalReply "fine")

            Expect.isEmpty state.Attached "nothing attached"
            Expect.isEmpty (AIAgentEngine.LoopState.messageParts "fine" state) "no parts: the message is plain text"
            Expect.isEmpty (attachedOf events) "no block event"
            Expect.equal (completedOf events) [ """{"ok":true}""" ] "the model is shown the string as before"
        }

        testCaseAsync "a client-resident tool carrying the record form is refused by name and attaches nothing"
        <| async {
            let clientAttaching = {
                attaching "_test.client.attach" 0 "never shown" BlockPartFixtures.tableBlock with
                    Definition = {
                        serverDefinition "_test.client.attach" with
                            Location = ClientResident
                    }
            }

            let! state, events =
                runTurn [ clientAttaching ] (function
                    | 1 -> toolCall "" "_test.client.attach"
                    | _ -> finalReply "ok")

            Expect.equal
                (completedOf events)
                [
                    ToolOutcome.toModel (
                        ToolOutcome.Refused(
                            ToolRefusal.Denied(
                                "_test.client.attach",
                                "a client-resident tool cannot attach parts to the assistant message"
                            )
                        )
                    )
                ]
                "the model is shown the refusal, which names the tool"

            Expect.isFalse
                (events
                 |> List.exists (function
                     | ClientToolInvoke _ -> true
                     | _ -> false))
                "the call never reaches the browser"

            Expect.isEmpty state.Attached "nothing attached"
            Expect.isEmpty (attachedOf events) "no block event"
        }

        test "a client-resident definition is refused at compose time by the record-form funnel" {
            Expect.throwsC
                (fun () ->
                    AIToolRegistry.createAnsweringTool
                        {
                            serverDefinition "_test.client.compose" with
                                Location = ClientResident
                        }
                        (fun _ _ -> async { return ({ Text = ""; Parts = [] }: ToolHelpers.ToolAnswer) })
                    |> ignore)
                (fun ex ->
                    Expect.stringContains ex.Message "_test.client.compose" "the refusal names the tool"
                    Expect.stringContains ex.Message "client-resident" "and says why")
        }

        testCaseAsync "a reloaded conversation shows the blocks where the live stream placed them"
        <| async {
            let provider =
                ScriptedProvider(fun n -> async {
                    return
                        Ok(
                            match n with
                            | 1 -> toolCalls "" [ "_test.render_table"; "_test.render_trend" ]
                            | _ -> finalReply BlockPartFixtures.Prose
                        )
                })

            let assistant, streaming =
                assistantWith (InMemoryBlobStorage()) provider [ tableTool; trendTool ]

            let id = Guid.NewGuid()

            let! events =
                collectStream (
                    streaming.StreamChatV2 {
                        ConversationId = id
                        Content = "Show me revenue by site."
                        ActiveModule = None
                        ActivePage = None
                        ActivePageNarrative = None
                        OverrideProviderLabel = None
                        Surface = FullPage
                        RetrievalFilters = None
                    }
                )

            match List.tryLast events with
            | Some(TaskStatusChanged(_, AITaskCompleted)) -> ()
            | other -> failtestf "the stream was expected to end completed, and ended on %A" other

            let streamed = attachedOf events
            let! reloaded = assistant.GetConversation id

            let finalMessage =
                reloaded |> List.filter (fun m -> m.Participant = AIAssistant) |> List.last

            Expect.equal finalMessage.Content BlockPartFixtures.Prose "the final message's text"

            Expect.equal finalMessage.Parts BlockPartFixtures.parts "persisted: the text part, then the blocks"

            Expect.equal
                (List.tail finalMessage.Parts)
                streamed
                "the reloaded message holds exactly the blocks the stream placed, in the same order"
        }
    ]

let tests =
    testList "Phase 1077 — the agent loop's rails" [
        railTests
        cancellationTests
        shutdownTests
        recordEntryTests
        attachTests
    ]