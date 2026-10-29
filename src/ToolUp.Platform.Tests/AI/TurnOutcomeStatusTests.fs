// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.TurnOutcomeStatusTests

// ─── A turn's outcome, rendered once ─────────────────────────────────
//
// `TurnOutcome.toStatus` is the one rendering of a turn's end to the wire.
// Every case is pinned here, word for word, against the `AITaskStatus` the
// client was shown before the outcome was a type: the strings the turn's
// exception arms, its ownership refusal, its record adapters and the
// loop's stops each emitted.

open System
open Expecto
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.AIAgentEngine

let private conversationId = Guid.Parse "6f0f7c1e-2b9d-4a51-8c3e-0d4b2a9e7f10"

let private budget: AgentCallBudget = {
    CallTimeout = TimeSpan.FromSeconds 60.0
    MaxTokens = Some 2048
    RaiseWith = Some "raise CallTimeout"
}

let private failed (failure: TurnFailure) =
    TurnOutcome.toStatus (TurnOutcome.Failed failure)

let private table: (string * AITaskStatus * AITaskStatus) list = [
    "a completed turn", TurnOutcome.toStatus TurnOutcome.Completed, AITaskCompleted
    "a turn the user cancelled completes, not fails", TurnOutcome.toStatus TurnOutcome.CancelledByUser, AITaskCompleted
    "the ownership refusal",
    failed (TurnFailure.Refused "someone-else"),
    AITaskFailed "Conversation does not belong to the current user — refusing to append."
    "no provider configured",
    failed (TurnFailure.ProviderUnresolved NoProviderConfigured),
    AITaskFailed "No AI provider configured for your account. Open AI settings to select and configure a provider."
    "an unreadable conversation blob",
    failed (TurnFailure.RecordUnreadable(conversationId, "operator detail never shown")),
    AITaskFailed
        "The stored history of conversation 6f0f7c1e-2b9d-4a51-8c3e-0d4b2a9e7f10 could not be read, so it was left untouched rather than overwritten."
    "an exchange that could not be written",
    failed (TurnFailure.RecordUnwritten(conversationId, "operator detail never shown")),
    AITaskFailed "The reply for conversation 6f0f7c1e-2b9d-4a51-8c3e-0d4b2a9e7f10 could not be saved."
    "the turn ceiling",
    failed (TurnFailure.Stopped(LoopStop.MaxTurns 15)),
    AITaskFailed
        "Agent loop exceeded 15 turns — stopping to avoid a runaway. If this was a legitimate multi-step task, try breaking it into smaller requests, or simplify the tools' return payloads so the model converges faster."
    "a fatal provider error, after the outer retry",
    failed (TurnFailure.Stopped(LoopStop.ProviderFatal(RetriesExhausted(3, TransientServer(529, "overloaded"))))),
    AITaskFailed "Retries exhausted after 3 attempts. Last error: Server error (HTTP 529): overloaded"
    "a fatal provider error, no retry",
    failed (TurnFailure.Stopped(LoopStop.ProviderFatal(PermanentClient(400, "bad request")))),
    AITaskFailed "Permanent client error (HTTP 400): bad request"
    "a background caller's call budget",
    failed (
        TurnFailure.Stopped(
            LoopStop.CallBudgetExceeded(budget, TimeSpan.FromSeconds 61.0, "Streaming aborted mid-response: cut")
        )
    ),
    AITaskFailed
        "the model was still writing when the call reached a call budget of 60 s and a reply of up to 2048 tokens (it ran 61 s): Streaming aborted mid-response: cut. To give it longer, raise CallTimeout."
    "a cancelled loop outside a user's cancel",
    failed (TurnFailure.Stopped LoopStop.Cancelled),
    AITaskFailed "Cancelled by user"
    "host shutdown", failed TurnFailure.ShutDown, AITaskFailed AIChatWorker.ShuttingDownReason
    "host shutdown, the literal", failed TurnFailure.ShutDown, AITaskFailed "Server is shutting down"
    "a bounded wait that timed out",
    failed (TurnFailure.Faulted(TimeoutException "Conversation load (ownership gate) timed out after 10s")),
    AITaskFailed "Conversation load (ownership gate) timed out after 10s"
    "anything else carries its exception's message",
    failed (TurnFailure.Faulted(InvalidOperationException "boom")),
    AITaskFailed "boom"
]

/// The two soft stops never reach the wire as a failure — the turn
/// completes — but their text is what the chat is shown.
let private softStops: (string * LoopStop * string) list = [
    "a confusion marker",
    LoopStop.Confusion "i'm unable to",
    "[Agent stopped early] the model indicated it can't proceed (matched \"i'm unable to\") but was still planning to call tools. Calls on an uncertain plan are unlikely to succeed.\n\nRetrying the same request won't help — please clarify your instructions, check the input data, or simplify the task and try again."
    "consecutive errored turns",
    LoopStop.ConsecutiveErrors(2, [ "_test.a"; "_test.b" ]),
    "[Agent stopped early] the last 2 turns had only failing tool calls (last attempted: _test.a, _test.b). The model isn't recovering from the errors.\n\nRetrying the same request won't help — please clarify your instructions, check the input data, or simplify the task and try again."
]

let tests =
    testList "Phase 1077 — TurnOutcome.toStatus, word for word" [
        testList "every outcome's status" [
            for name, actual, expected in table do
                test name { Expect.equal actual expected name }
        ]

        testList "the soft stops' chat message" [
            for name, stop, expected in softStops do
                test name {
                    Expect.isTrue (LoopStop.isSoft stop) $"{name} is a soft stop"
                    Expect.equal (LoopStop.earlyStopMessage stop) expected name
                }
        ]

        test "every other stop is hard" {
            for stop in
                [
                    LoopStop.MaxTurns 15
                    LoopStop.ProviderFatal(MalformedResponse "x")
                    LoopStop.CallBudgetExceeded(budget, TimeSpan.Zero, "x")
                    LoopStop.Cancelled
                ] do
                Expect.isFalse (LoopStop.isSoft stop) $"{LoopStop.message stop} fails the turn"
        }
    ]