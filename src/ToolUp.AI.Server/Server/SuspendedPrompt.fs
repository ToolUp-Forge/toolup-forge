// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.SuspendedPrompt

open System
open System.Collections.Concurrent
open System.Threading.Tasks

// ─── The suspended-dispatch mechanism, once ──────────────────────
//
// Three places in the AI tier park a server thread on a decision only the
// browser can make, and they are three CALLERS of one mechanism rather
// than three mechanisms:
//
//   * Phase 6g.A — a client-resident tool call, suspended on
//     `ClientToolDispatchRegistry` until `/api/ai/tool-result` arrives.
//   * Phase 36.D — a cross-module read, suspended until the user answers
//     a consent dialog at `/api/ai/consent`.
//   * Phase 503 — a consequential tool invocation, held until the user
//     approves it at `/api/ai/tool-approval`.
//
// The shape they share is exactly this file: a per-process dictionary of
// `TaskCompletionSource`s keyed by a correlation `Guid`, a read that does
// NOT remove the entry (so a POST handler can authorise first and
// complete second), a completion, an abandonment, and one budget both
// ends agree on.
//
// 36.D already made the BUDGET one declaration with two callers, for the
// reason that applies to the rest of it: a second copy is a second thing
// to keep in step, and the one property every one of these round trips
// has to have — a never-answered prompt fails cleanly, on time — is
// precisely the property a drifted copy quietly loses. 503 is the third
// caller, so the rest of the shape moves here with it.
//
// `ClientToolDispatchRegistry` is deliberately NOT refactored onto this.
// Its completion carries a result STRING and its abandonment raises so
// the loop's `ToolThrew` recovery classifies it — a different contract
// with a different failure semantic, and rewriting it would be an
// opportunistic change to a path this phase has no reason to touch.
//
// Per-process, with the caveat all three carry: a deployment running
// several silos behind a load balancer needs SSE/POST affinity, or a
// shared registry, for the answering POST to find the suspended caller.

/// How long a suspended dispatch waits for the browser before it gives
/// up.
///
/// **One declaration, three callers.** `AIAgentEngine`'s client-resident
/// tool dispatch, `AIConsentDispatch`'s consent await and
/// `ToolApprovalDispatch`'s approval hold all read this. They are the
/// same wait — a server thread parked on a `TaskCompletionSource` that
/// only a browser can complete — and a second constant would be a second
/// budget to keep in step.
///
/// 90 s: generously longer than the client's own 30 s watchdog on the
/// tool path, so the client-side failure wins the race and the user sees
/// the specific cause rather than a generic server abort.
[<Literal>]
let SuspendedDispatchTimeoutMs = 90_000

/// A per-process registry of dispatches suspended on a decision the
/// browser makes.
///
/// Generic in both halves because the three callers differ in exactly
/// those two: `'Decision` is what the browser sends back, and `'Pending`
/// is what the server recorded about the request so the answering POST
/// can be authorised and attributed from the server's own record rather
/// than from the client's body.
type PendingPromptRegistry<'Decision, 'Pending>() =
    let pending =
        ConcurrentDictionary<Guid, TaskCompletionSource<'Decision> * 'Pending>()

    /// Register a suspended dispatch. Returns the Task the caller awaits
    /// — completed by the answering POST, or by `TryAbandon` on the
    /// caller's own timeout path.
    ///
    /// Register BEFORE emitting the SSE event, so a decision that arrives
    /// on a fast local round trip cannot find an unregistered id.
    member _.RegisterPending(promptId: Guid, request: 'Pending) : Task<'Decision> =
        let tcs =
            TaskCompletionSource<'Decision>(TaskCreationOptions.RunContinuationsAsynchronously)

        pending[promptId] <- (tcs, request)
        tcs.Task

    /// What the server recorded for a prompt id, or `None` when the id is
    /// unknown / stale / already answered.
    ///
    /// Read-only: it does NOT remove the entry, so a handler can
    /// authorise first and complete second. Authorising after completion
    /// would authorise nothing — the dispatch has already resumed.
    member _.PendingOf(promptId: Guid) : 'Pending option =
        match pending.TryGetValue promptId with
        | true, (_, request) -> Some request
        | false, _ -> None

    /// Complete a suspended dispatch with the browser's decision.
    /// `false` when no matching request was pending — a late POST after
    /// the caller already timed out, a double-click, or a replayed body.
    member _.TryComplete(promptId: Guid, decision: 'Decision) : bool =
        match pending.TryRemove promptId with
        | true, (tcs, _) -> tcs.TrySetResult decision
        | false, _ -> false

    /// Abandon a suspended dispatch on the caller's own timeout path,
    /// resolving it as `fallback` rather than raising.
    ///
    /// The fallback is supplied by the caller because only the caller
    /// knows which answer is the safe one: for consent and for approval
    /// alike it is the refusal, so an unanswered prompt is indis-
    /// tinguishable — to the model, and in its effect — from an explicit
    /// no.
    member _.TryAbandon(promptId: Guid, fallback: 'Decision) : bool =
        match pending.TryRemove promptId with
        | true, (tcs, _) -> tcs.TrySetResult fallback
        | false, _ -> false

/// Wait for a registered prompt to be answered, on the one shared budget.
///
/// Takes the Task rather than the registry so a caller keeps its own
/// domain-named facade (`AIConsentRegistry`, `ToolApprovalRegistry`) and
/// still shares this race: the two things that must not drift are the
/// budget and the abandon-on-timeout, and both live here.
///
/// `abandon` is run when the budget elapses — it is the caller's
/// `TryAbandon`, which resolves the still-parked source so nothing is
/// left holding a `TaskCompletionSource` nobody will ever complete.
///
/// The budget is a parameter here and fixed at the shared constant in
/// `awaitDecision` below, which is what production calls.
let awaitDecisionWithin (budgetMs: int) (awaited: Task<'Decision>) (abandon: unit -> unit) : Async<'Decision option> = async {
    let timeoutTask = Task.Delay budgetMs
    let! winner = Task.WhenAny(awaited :> Task, timeoutTask) |> Async.AwaitTask

    if winner = (awaited :> Task) then
        let! decision = awaited |> Async.AwaitTask
        return Some decision
    else
        abandon ()
        return None
}

/// The same wait, on the one shared budget. This is what production
/// calls; `awaitDecisionWithin` exists because the elapsed arm is the one
/// arm of every suspended round trip a test suite cannot afford to
/// exercise at 90 s, and an untested refusal path is the one that rots.
let awaitDecision (awaited: Task<'Decision>) (abandon: unit -> unit) : Async<'Decision option> =
    awaitDecisionWithin SuspendedDispatchTimeoutMs awaited abandon