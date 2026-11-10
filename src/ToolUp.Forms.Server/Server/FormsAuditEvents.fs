// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Forms companion's audit events: its payload records, its event
/// union and the family it registers from `composeForms`. Core never names
/// them; they are recorded as `AuditEvent.Opaque` and read back typed
/// through `FormsAuditEvent.tryRead`.
///
/// Forms lifecycle events are emitted by `FormApiHandler` after a
/// successful Submit / UpdateDraft, and by `WorkflowEngine.Apply` after a
/// successful state transition. PII is intentionally absent: payloads
/// carry stable identifiers (FormId / SubmissionId / states / actor
/// userId) but never the submitted field values. Operators correlate
/// against the entity-store blob via `(SubmissionId, Version)` if they
/// need the values.
module ToolUp.Forms.FormsAuditEvents

open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

type FormSubmittedPayload = {
    /// Actor who submitted the form. Always the authenticated caller
    /// — server-set from `AccessContext.UserId`.
    UserId: UserId
    /// Schema this submission satisfies.
    FormId: string
    /// Server-allocated submission identifier.
    SubmissionId: string
    /// Schema version captured at submit time.
    SchemaVersion: int
    /// Number of populated fields. Cardinality only — values do not
    /// travel in the audit trail.
    FieldCount: int
    /// Workflow this submission was bound to, if any. `None` for
    /// ad-hoc submissions.
    WorkflowId: string option
    /// Initial state assigned by the API handler. Either `"Submitted"`
    /// for ad-hoc submissions, or the workflow's `InitialState`.
    InitialState: string
}

type FormSubmissionUpdatedPayload = {
    UserId: UserId
    FormId: string
    SubmissionId: string
    /// Number of populated fields after the update. Cardinality only.
    FieldCount: int
    /// New entity version after the write (>= 2 — initial submit
    /// produces version 1; updates increment).
    Version: int
}

/// Workflow state transition. Emitted by `WorkflowEngine.Apply` after
/// the new state has been persisted (and before the optional action
/// runs). Records the actor, the workflow involved, and both states
/// — operators can reconstruct the full state-machine trail by
/// filtering audit events on `WorkflowTransitioned` for a given
/// `SubmissionId`.
type WorkflowTransitionedPayload = {
    UserId: UserId
    FormId: string
    SubmissionId: string
    WorkflowId: string
    /// State the submission held immediately before the transition.
    FromState: string
    /// Triggering event name (`submit`, `approve`, `reject`, ...).
    Event: string
    /// State the submission entered. Persisted to the entity store
    /// before this audit event is emitted.
    ToState: string
}

/// Workflow action invocation outcome. Emitted by the
/// `WorkflowEngine` after the action ledger resolves (either to
/// `Succeeded`, `Failed`, `SkippedReplay` for a successful prior
/// attempt, or `SkippedPending` for a pending prior attempt under a
/// `LogOnly` policy). Distinct from `WorkflowTransitioned` (which
/// fires on the state transition) so operator queries can filter on
/// action-specific behaviour without scanning every transition row.
/// PII-free: identifiers + status only; no action payload travels.
type WorkflowActionExecutedPayload = {
    /// Submission whose transition the action ran against.
    SubmissionId: string
    /// `"{from}:{event}:{to}"` — engine-derived transition id, matches
    /// the ledger key for cross-referencing the audit trail with
    /// dead-letter ledger rows.
    TransitionId: string
    /// Registered action name (matches `Transition.Action`).
    ActionName: string
    /// One of `"succeeded"` / `"failed"` / `"skipped_replay"` /
    /// `"skipped_pending"`. Matches the metric counter's `status`
    /// tag so dashboards stay self-consistent.
    Status: string
    /// Exception message captured at the call site when `Status =
    /// "failed"`; empty string for the success / skip paths.
    Reason: string
}

/// Every audit event the Forms companion records.
type FormsAuditEvent =
    /// A submission committed (Submit). PII-free.
    | FormSubmitted of FormSubmittedPayload
    /// A submission edited in `Draft` state (UpdateDraft). PII-free.
    | FormSubmissionUpdated of FormSubmissionUpdatedPayload
    /// A workflow state transition applied. Recorded after the new state
    /// is persisted; the optional workflow action runs afterwards but does
    /// not affect this event.
    | WorkflowTransitioned of WorkflowTransitionedPayload
    /// A workflow action's outcome (succeeded / failed / skipped_replay /
    /// skipped_pending), for every action the ledger resolves, so operator
    /// triage can correlate metrics, audit and ledger rows.
    | WorkflowActionExecuted of WorkflowActionExecutedPayload

module FormsAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.Forms"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The Forms family: its kinds and its codec.
    let family: AuditEventFamily<FormsAuditEvent> = {
        Registrant = Registrant
        Kinds = [
            kind "FormSubmitted"
            kind "FormSubmissionUpdated"
            kind "WorkflowTransitioned"
            kind "WorkflowActionExecuted"
        ]
        Encode =
            function
            | FormSubmitted p -> "FormSubmitted", AuditPayloadJson.write p
            | FormSubmissionUpdated p -> "FormSubmissionUpdated", AuditPayloadJson.write p
            | WorkflowTransitioned p -> "WorkflowTransitioned", AuditPayloadJson.write p
            | WorkflowActionExecuted p -> "WorkflowActionExecuted", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "FormSubmitted" -> FormSubmitted(AuditPayloadJson.read payload)
                | "FormSubmissionUpdated" -> FormSubmissionUpdated(AuditPayloadJson.read payload)
                | "WorkflowTransitioned" -> WorkflowTransitioned(AuditPayloadJson.read payload)
                | "WorkflowActionExecuted" -> WorkflowActionExecuted(AuditPayloadJson.read payload)
                | other -> failwithf "not a Forms audit kind: %s" other
    }

    /// The `AuditEvent` a Forms event is recorded as.
    let event (value: FormsAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The Forms event an `AuditEvent` carries, if it carries one.
    let tryRead (audit: AuditEvent) : FormsAuditEvent option = AuditEventFamily.tryRead family audit