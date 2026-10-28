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
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

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
            entered.TrySetResult() |> ignore

            let cancelled =
                Task.Delay(Timeout.Infinite, token).ContinueWith(fun (_: Task) -> ())

            let! _ = Task.WhenAny(cancelled, release.Task) |> Async.AwaitTask

            if token.IsCancellationRequested then
                sawCancellation <- true

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

let tests =
    testList "Phase 1077 — the agent loop's rails" [ railTests; cancellationTests ]