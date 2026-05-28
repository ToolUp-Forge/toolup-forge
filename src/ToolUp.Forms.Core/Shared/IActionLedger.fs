// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.IActionLedger

open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow

// ─── Phase 21d — Workflow action ledger ─────────────────────────────
//
// The ledger turns workflow-action invocation into an exactly-once
// (per-invocation) primitive so a process crash / redeploy between
// state-persist and action-completion cannot re-fire the action on
// restart, and so an exception inside an action cannot silently
// commit the submission while losing the side effect.
//
// **Apply flow with the ledger in place:**
//
//   1. Engine looks up `(submissionId, transitionId, actionName)`.
//      * `Some { Status = Succeeded }` — action already ran in a
//        prior attempt; skip the invocation.
//      * `Some { Status = Pending }` — a prior attempt persisted the
//        state, started the action, and never resolved (process died
//        or is still in flight). Branch on the transition's
//        `OnFailure` policy: `DeadLetter` proceeds with a retry,
//        `FailSubmission` aborts with `ActionPendingFromPriorAttempt`,
//        `LogOnly` proceeds (preserves the pre-21d best-effort
//        posture).
//      * `Some { Status = Failed _ }` — a prior attempt failed;
//        proceed with retry under the policy.
//      * `None` — first attempt for this triple; proceed.
//   2. Engine writes `Pending` ledger entry **before** invoking the
//      action.
//   3. Engine invokes the action inside `try/with`.
//   4. On success → `MarkSucceeded`. On exception → `MarkFailed` +
//      apply `OnFailure` policy.
//
// **Identity.** Entries are keyed by `(SubmissionId, transitionId,
// actionName)`. `transitionId` is derived per-call from the
// transition's `From` / `Event` / `To` triple so two transitions
// firing the same action on the same submission (an unusual but
// legal shape) get independent ledger rows.
//
// **Six-rule portability audit (GP-12 / Phase 9c):**
//
//   1. Identity by value — `SubmissionId` / `transitionId` /
//      `actionName` are all `string`. No live handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry / supervision as data — failure flows through
//      `LedgerError`. No callback or supervision parameters; the
//      `ActionFailurePolicy` decision is data on the caller side.
//   4. Stateless between calls — `Record` / `Lookup` / `MarkSucceeded`
//      / `MarkFailed` take the full key on every call; no in-memory
//      continuity assumed across calls. A grain deactivated between
//      `Record` and `MarkSucceeded` behaves identically.
//   5. No cross-shard ordering — per-key ordering is enforced by the
//      backing store; cross-key ordering is not promised.
//   6. Precision at lower bound — N/A (no scheduling primitives).

/// Where an action invocation sits in its lifecycle. `Failed` carries
/// the exception message verbatim so dead-letter consumers + audit
/// rows expose the same diagnostic the operator would see in logs.
type ActionLedgerStatus =
    | Pending
    | Succeeded
    | Failed of reason: string

/// One ledger row keyed by `(SubmissionId, TransitionId, ActionName)`.
/// `TransitionId` is the engine-derived `"<From>:<Event>:<To>"` triple
/// — stable per (submission, transition, action) combination.
type ActionLedgerEntry = {
    SubmissionId: SubmissionId
    /// Stable identifier for the transition that triggered the action.
    /// Format: `"{from}:{event}:{to}"`. Distinct from `Transition`
    /// record values so the ledger key stays primitive.
    TransitionId: string
    ActionName: string
    Status: ActionLedgerStatus
}

/// Why a ledger call failed. Distinct from `FormError` because the
/// ledger sits below `IFormStore` / `IWorkflowEngine` and need not
/// know about form-level concerns. Wrapped into
/// `FormError.StorageFailed` by the engine when surfacing to callers.
type LedgerError =
    /// Underlying store write / read failed. The string is the
    /// store-specific error message formatted for display.
    | StorageFailed of string
    /// Attempted to `MarkSucceeded` / `MarkFailed` an entry that was
    /// never `Record`ed. Indicates a contract violation on the
    /// caller side (the engine writes `Pending` before invoking the
    /// action, so this should never surface in correct usage).
    | EntryMissing

/// Server-side action-ledger contract. Implementations must be
/// thread-safe — multiple engine instances may run in the same
/// process and persist concurrently against the same key.
type IActionLedger =
    /// Insert a fresh `Pending` entry. Idempotent: if an entry for
    /// the same key already exists, the existing row is returned
    /// unchanged (the engine then branches on its `Status` rather
    /// than overwriting). Returns `Ok ()` on insert-or-retain;
    /// `Error StorageFailed` on backing-store failure.
    abstract Record: entry: ActionLedgerEntry -> Async<Result<unit, LedgerError>>

    /// Look up an entry by composite key. `Ok None` = no such entry
    /// (first-attempt path); `Ok (Some entry)` = entry exists,
    /// caller branches on `entry.Status`.
    abstract Lookup:
        submissionId: SubmissionId * transitionId: string * actionName: string ->
            Async<Result<ActionLedgerEntry option, LedgerError>>

    /// Transition an existing entry to `Succeeded`. `Error EntryMissing`
    /// if the row was never `Record`ed — indicates a contract bug on
    /// the caller side, not a runtime condition.
    abstract MarkSucceeded:
        submissionId: SubmissionId * transitionId: string * actionName: string -> Async<Result<unit, LedgerError>>

    /// Transition an existing entry to `Failed`. Same `EntryMissing`
    /// semantics as `MarkSucceeded`. `reason` is the exception
    /// message captured at the call site; sinks consuming the ledger
    /// (dead-letter retry orchestrators, operator dashboards) read
    /// it verbatim.
    abstract MarkFailed:
        submissionId: SubmissionId * transitionId: string * actionName: string * reason: string ->
            Async<Result<unit, LedgerError>>

module ActionLedger =
    /// Engine-internal helper for deriving the `TransitionId` from a
    /// `Transition` record. Lives here so the contract pack + the
    /// engine + the in-memory default all agree on the format.
    let transitionId (t: Transition) : string = sprintf "%s:%s:%s" t.From t.Event t.To