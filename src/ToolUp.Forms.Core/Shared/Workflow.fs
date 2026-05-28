// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.Workflow

// ─── Phase 21 — Forms companion workflow types ──────────────────────
//
// Small state-machine primitive over named transitions. Workflows are
// registered server-side via `FormsServerApp.withWorkflow`; the client
// only sees state names + transition events for badge rendering.
//
// Departure from the design spec: `Guard` and `Action` are name-keyed
// (looked up server-side in the workflow engine's registry), not
// closures. Same Fable-compat reason as `ValidationRule.Custom` — the
// Shared layer carries no functions.

/// Workflow position. Type alias for `string` so workflow authors
/// pick natural names (`new`, `triaged`, `closed`, `awaiting-approval`).
type WorkflowState = string

/// Stable identifier for a workflow definition. Type alias for `string`.
type WorkflowId = string

/// Stable identifier for a transition event. Type alias for `string`.
/// Events are arbitrary names (`submit`, `approve`, `reject`,
/// `escalate`); the workflow definition is the source of truth for
/// which events apply in which states.
type TransitionEvent = string

/// One transition in a workflow. `From` is the precondition state;
/// `Event` is the trigger; `To` is the resulting state. Optional
/// `Guard` and `Action` references resolve server-side at apply time.
type Transition = {
    From: WorkflowState
    Event: TransitionEvent
    To: WorkflowState
    /// Registered guard name. `None` = unconditional transition.
    /// Resolved server-side via the workflow engine's guard registry;
    /// shape `WorkflowContext -> Async<Result<unit, string>>` (Phase
    /// 1h — the context carries submission + access context + the
    /// resolved `IServiceProvider`). Errors short-circuit the
    /// transition with `TransitionDenied`.
    Guard: string option
    /// Registered action name. `None` = pure state change with no
    /// side effect. Resolved server-side via the workflow engine's
    /// action registry; shape `WorkflowContext -> Async<unit>` (Phase
    /// 1h — the context carries submission + access context + the
    /// resolved `IServiceProvider`, so actions can resolve
    /// `IEntityStore`, `INotificationChannel`, etc. directly).
    /// Actions run after persistence; the action ledger (Phase 21d)
    /// guards exactly-once invocation, and the per-action failure
    /// policy registered via `FormsServerApp.withActionPolicy` decides
    /// whether an exception aborts the transition, dead-letters the
    /// invocation, or is logged best-effort.
    Action: string option
}

/// A complete workflow. Registered at compose time via
/// `FormsServerApp.withWorkflow`; immutable thereafter for the
/// lifetime of the process.
type WorkflowDefinition = {
    Id: WorkflowId
    /// State assigned to a submission whose `WorkflowId = Some Id` at
    /// submit time. Must be the `From` state of at least one
    /// `Transition` (otherwise the submission is born terminal).
    InitialState: WorkflowState
    Transitions: Transition list
}

// ─── Phase 21d — Action failure policy ──────────────────────────────
//
// Per-action policy controlling what happens when a workflow action
// throws. Registered alongside actions via
// `FormsServerApp.withActionPolicy actionName policy`. Actions
// without an explicit policy use the engine default (`DeadLetter`).
// The pre-21d "swallow + warn" posture is opt-in under `LogOnly` —
// callers migrating from Phase 21 who want to preserve byte-for-byte
// behaviour must register their actions explicitly under `LogOnly`,
// the type system makes the choice visible at the registration site.

/// What to do when an action throws.
type ActionFailurePolicy =
    /// Abort the transition. The engine surfaces
    /// `FormError.ActionFailed (actionName, reason)`. Submission
    /// state is NOT committed (the engine rolls back the state
    /// write before returning the error). Choose for actions whose
    /// downstream effect is load-bearing — payment capture, legal
    /// notice, regulatory webhook — where committing the transition
    /// without the side effect leaves the system in a worse state
    /// than not transitioning at all.
    | FailSubmission
    /// Default for new registrations. Commit the submission state
    /// (the transition is durable) AND persist a `Failed` ledger
    /// entry the operator can drain via a dead-letter retry
    /// orchestrator. Emits the action-outcome metric + the
    /// `WorkflowActionExecuted` audit row. No silent loss — the
    /// failure is observable from three independent surfaces
    /// (metric, audit, ledger).
    | DeadLetter
    /// Pre-21d behaviour. Warn-log the exception; commit the
    /// submission; emit the audit row + metric. No dead-letter
    /// entry. Choose for genuinely best-effort sinks where a retry
    /// orchestrator would add no value (e.g. fire-and-forget
    /// observability beacons).
    | LogOnly

module WorkflowDefinition =
    /// Find the transition that applies to `(state, event)`. Returns
    /// `None` if no transition is defined for that pair (caller emits
    /// `FormError.InvalidTransition`).
    let tryFindTransition
        (state: WorkflowState)
        (event: TransitionEvent)
        (def: WorkflowDefinition)
        : Transition option =
        def.Transitions |> List.tryFind (fun t -> t.From = state && t.Event = event)

    /// Enumerate transitions whose `From` matches the given state.
    /// Useful for "what can the user do next" UI.
    let listFromState (state: WorkflowState) (def: WorkflowDefinition) : Transition list =
        def.Transitions |> List.filter (fun t -> t.From = state)

    /// All distinct states referenced by the workflow's transitions
    /// (union of `From` and `To`), plus `InitialState`.
    let states (def: WorkflowDefinition) : Set<WorkflowState> =
        def.Transitions
        |> List.collect (fun t -> [ t.From; t.To ])
        |> List.append [ def.InitialState ]
        |> Set.ofList