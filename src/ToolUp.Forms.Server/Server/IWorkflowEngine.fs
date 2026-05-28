module ToolUp.Forms.IWorkflowEngine

open System
open ToolUp.Platform
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow

// ─── Phase 21 — IWorkflowEngine interface ───────────────────────────
//
// Server-side abstraction over named workflow definitions. Default
// implementation (`ToolUp.Forms.WorkflowEngine`) holds an immutable
// `Map<WorkflowId, WorkflowDefinition>` populated at compose time via
// `FormsServerApp.withWorkflow`, plus name-keyed registries for
// guards (predicates that can veto a transition) and actions
// (side-effects that run after persistence).
//
// **Scope isolation.** Workflow definitions themselves are
// platform-scoped (registered once at compose time, valid for every
// scope). The submissions they apply to ARE scope-isolated — every
// `Apply` call requires the submission's scope and resolves it
// through `IFormStore`.
//
// **Six-rule portability audit (GP-12 / Phase 9c):**
//
//   1. Identity by value — `WorkflowId` / `SubmissionId` /
//      `WorkflowState` / `TransitionEvent` are all `string`. No live
//      handles in the interface surface.
//
//   2. Async at every boundary — every method returns `Async<_>`.
//      Side-effecting actions are awaited (no fire-and-forget).
//
//   3. Retry / supervision as data — failure flows through the
//      `FormError` DU (`InvalidTransition` / `TransitionDenied` /
//      `WorkflowNotFound`). No callback or supervision parameters.
//
//   4. Stateless between calls — `Apply` reads the current state
//      from `IFormStore`, computes the transition, persists, then
//      runs the action. No assumption of in-memory state across
//      calls. Workflow registrations are constants from compose time
//      (immutable for process lifetime), not in-memory state in the
//      Rule-4 sense.
//
//   5. No cross-shard ordering — transitions on different
//      submissions are independent. Per-submission ordering is
//      enforced by the underlying `IFormStore.SaveSubmission`
//      versioning.
//
//   6. Precision at lower bound — N/A (no scheduling primitives).

/// Phase 1h — context record passed into guard / action invocations.
/// Bundles the submission, caller context, and the resolved
/// `IServiceProvider` so guards and actions can resolve DI services
/// (e.g. `IEntityStore`, `INotificationChannel`, `IAuditLog`)
/// directly per invocation. `Services` is resolved fresh per `Apply`
/// call by the workflow engine — actions safely capture transient
/// services without leaking lifetimes across calls.
type WorkflowContext = {
    Submission: Submission
    AccessContext: AccessContext
    Services: IServiceProvider
}

/// Guard predicate keyed by name in the engine's guard registry.
/// Returns `Error reason` to short-circuit a transition with
/// `FormError.TransitionDenied reason`. Receives the full
/// `WorkflowContext` (submission + caller context + resolved
/// `IServiceProvider`) so guards can encode "actor must be the
/// submitter" / "field X must equal Y" / "team has reached its
/// quota" etc., and can resolve DI services for cross-cutting
/// checks.
///
/// Phase 1h: the prior `Submission * AccessContext -> ...` shape was
/// replaced by `WorkflowContext -> ...` to let guards reach DI
/// without compose-time capture. See `docs/migrations/01h-combinable-composition-roots.md`.
type WorkflowGuard = WorkflowContext -> Async<Result<unit, string>>

/// Action keyed by name in the engine's action registry. Runs after
/// the new state has been persisted; the per-action
/// `ActionFailurePolicy` decides whether an exception aborts the
/// transition, dead-letters the invocation, or is logged best-effort.
/// Receives the full `WorkflowContext` so actions can resolve
/// `IEntityStore`, `INotificationChannel`, etc. from
/// `ctx.Services` and emit cross-module side effects without a
/// request-side workaround.
///
/// Phase 1h: signature changed from `Submission * AccessContext -> ...`
/// to `WorkflowContext -> ...`. See `docs/migrations/01h-combinable-composition-roots.md`.
type WorkflowAction = WorkflowContext -> Async<unit>

/// Server-side workflow apply / introspection surface. Workflow
/// definitions, guards, and actions are immutable for process
/// lifetime — passed through the constructor at compose time, not
/// mutated at runtime.
type IWorkflowEngine =
    /// Apply a transition event to a submission. Resolves the
    /// transition for `(currentState, event)`, runs the optional
    /// guard, persists the new state, and runs the optional action.
    ///
    /// Returns the updated submission on success. Failure modes
    /// (in order of detection):
    ///   * `WorkflowNotFound` — submission's `WorkflowId` isn't
    ///     registered.
    ///   * `InvalidTransition` — no transition is defined for
    ///     `(currentState, event)`.
    ///   * `TransitionDenied` — guard returned `Error reason`.
    ///   * `StorageFailed` — `IFormStore.SaveSubmission` failed.
    ///
    /// Audit emission: a `WorkflowTransitioned` audit event is
    /// recorded after successful persistence, before the action runs.
    /// Action failures are logged at `Warn` but do not roll back
    /// the state change.
    abstract Apply:
        scopeId: string * submissionId: SubmissionId * event: TransitionEvent * accessContext: AccessContext ->
            Async<Result<Submission, FormError>>

    /// Enumerate transitions whose `From` matches the submission's
    /// current state. Returns empty when the submission has no
    /// workflow or has reached a terminal state (one with no
    /// outbound transitions).
    abstract ListPossibleTransitions: scopeId: string * submissionId: SubmissionId -> Async<Transition list>