// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormSubmission

open System
open ToolUp.Forms.FormSchema

// ─── Phase 21 — Forms companion submission types ────────────────────
//
// Persisted artefact of a completed (or in-progress) form. Stored as
// a Phase 19 entity with declared indexes on `FormId`, `SubmittedBy`,
// `State`, and a compound `(FormId, State)` for "show me all open
// submissions of form X" queries.
//
// `FieldValue` mirrors `FieldKind` shape one-to-one. The `Values` map
// is keyed by `FieldSchema.Key`; missing keys are treated as
// "unprovided" by the validator (which then enforces `Required`).

/// Stable identifier for a submission. Server-allocated (`Guid` over
/// the wire) so clients can submit without coordinating Ids.
type SubmissionId = string

/// Per-field value carrier. Mirrors `FieldKind` — the validator
/// rejects `(FieldKind, FieldValue)` pairs whose shapes don't match.
/// Null/empty handling: `None` for absent values; the value DU only
/// represents present values.
type FieldValue =
    | TextValue of string
    | NumberValue of float
    | DateValue of DateOnly
    | DateTimeValue of DateTimeOffset
    | BoolValue of bool
    | ChoiceValue of string
    | MultiChoiceValue of string list
    /// Reference to a previously-uploaded file (Phase 7 / Data Manager).
    /// The string is the file's stable identifier; the renderer resolves
    /// to a download link at display time.
    | FileValue of fileId: string
    /// Reference to a Phase 19 entity by id. Renderer resolves the
    /// entity's display label at render time.
    | EntityRefValue of entityId: string
    /// Reference to a nested submission's id. The validator descends
    /// into the nested form's schema for full validation.
    | NestedSubmissionValue of SubmissionId

/// Where a submission sits in its workflow. v1 ships two reserved
/// states (`Draft` for in-progress, `Submitted` for committed without
/// a workflow) plus an open `Custom` carrier so workflow-driven forms
/// can name arbitrary states.
type SubmissionState =
    /// In-progress. `UpdateDraft` accepts edits; `Submit` transitions
    /// to `Submitted` (or to a workflow's `InitialState` if registered).
    | Draft
    /// Committed without an attached workflow.
    | Submitted
    /// Workflow-defined state. The string matches a `WorkflowState` in
    /// the registered `WorkflowDefinition`.
    | Custom of state: string

module SubmissionState =
    /// Wire-stable string for an `IEntityStore` index value. The
    /// `Custom` case carries its own state name verbatim.
    let toIndexValue (s: SubmissionState) : string =
        match s with
        | Draft -> "Draft"
        | Submitted -> "Submitted"
        | Custom s -> s

    /// Inverse of `toIndexValue`. `"Draft"` / `"Submitted"` round-trip
    /// to their reserved cases; everything else is a `Custom`.
    let fromIndexValue (s: string) : SubmissionState =
        match s with
        | "Draft" -> Draft
        | "Submitted" -> Submitted
        | other -> Custom other

/// Phase 21b — typed identity of whoever produced a submission.
/// `AuthenticatedUser` is the Phase 21 baseline (the API gates on
/// `AccessContext.UserId`); `InvitedRespondent` is the public-form
/// case — an anonymous respondent submitted via a share-link token.
/// `tokenId` lets the creator's dashboard correlate the submission
/// back to the issued token + (optionally) the original handle the
/// creator attributed it to.
type SubmissionAuthor =
    | AuthenticatedUser of userId: string
    | InvitedRespondent of tokenId: string * attributedHandle: string option

module SubmissionAuthor =
    /// Stable wire string for `Author`-keyed `IEntityStore` index
    /// values. Prefix-tagged so a single `Eq("Author", ...)` query
    /// can target either flavour without collision (`u:` for
    /// authenticated, `t:` for invited-respondent).
    let toIndexValue (a: SubmissionAuthor) : string =
        match a with
        | AuthenticatedUser uid -> "u:" + uid
        | InvitedRespondent(tokenId, _) -> "t:" + tokenId

    /// Build the index value for an authenticated `userId` lookup.
    /// Use in `Eq("Author", indexValueForUser uid)` queries.
    let indexValueForUser (userId: string) = "u:" + userId

    /// Build the index value for a token lookup. Use in
    /// `Eq("Author", indexValueForToken tokenId)` queries.
    let indexValueForToken (tokenId: string) = "t:" + tokenId

/// A submission instance. Carries both the schema reference (`FormId`
/// + `SchemaVersion`) and the submitted values; the schema's
/// `Version`-at-submit-time is preserved so historical re-renders
/// work even after the schema has evolved.
type Submission = {
    /// Phase 19 reflection contract — entity primary key. Server-allocated.
    Id: SubmissionId
    /// Phase 19 reflection contract — entity type discriminator.
    Type: string
    /// Phase 19 reflection contract — overwritten by the store.
    Version: int
    /// Schema this submission satisfies. Indexed.
    FormId: FormSchemaId
    /// Schema version at submission time. Distinct from the entity's
    /// `Version` (which counts revisions of *this submission*).
    SchemaVersion: int
    SubmittedAt: DateTimeOffset
    /// Phase 21b — typed submitter identity. Indexed via
    /// `SubmissionAuthor.toIndexValue`. Server-set on `Submit` /
    /// `SubmitWithToken`; never trust client-supplied values.
    Author: SubmissionAuthor
    /// Field values keyed by `FieldSchema.Key`.
    Values: Map<string, FieldValue>
    /// Workflow position. Indexed.
    State: SubmissionState
    /// Optional workflow this submission is bound to. `None` for
    /// ad-hoc submissions (state stays `Submitted`).
    WorkflowId: string option
}

module Submission =
    /// Stable entity-type discriminator for `IEntityStore` registration.
    [<Literal>]
    let entityType = "Submission"

    /// Index name constants for predicate queries. Match the
    /// declarations registered with `EntityRegistration.withIndex` in
    /// `EntityRegistrations.fs`.
    [<Literal>]
    let indexFormId = "FormId"

    /// Phase 21b — replaces the Phase 21 `SubmittedBy` index. Stores
    /// the prefix-tagged `SubmissionAuthor` index value
    /// (`"u:{uid}"` or `"t:{tokenId}"`) so a single index keys both
    /// authenticated and invited-respondent lookups.
    [<Literal>]
    let indexAuthor = "Author"

    [<Literal>]
    let indexState = "State"

    [<Literal>]
    let indexFormIdState = "FormId+State"

/// Why a form operation failed. Distinct from `IEntityStore`'s
/// `EntityError` — surfaced through `IFormApi` so the client UI can
/// branch on validation failures vs storage failures vs permission
/// failures vs workflow failures with a single match.
type FieldError = {
    /// `FieldSchema.Key` of the offending field. Empty string for
    /// errors that aren't field-specific.
    FieldKey: string
    /// Wire-stable error code (`required`, `regex`, `range`,
    /// `choice-not-allowed`, `custom`, `wrong-type`). Renderer can
    /// localise / theme based on this.
    Code: string
    /// Human-readable message. May embed the offending value but
    /// must not embed PII beyond what the user submitted.
    Message: string
}

type FormError =
    /// Submission / schema not found in the caller's scope.
    | NotFound of resource: string * id: string
    /// One or more fields failed validation.
    | ValidationFailed of FieldError list
    /// Workflow guard rejected the requested transition.
    | TransitionDenied of reason: string
    /// Underlying `IEntityStore` write failed. The string is the
    /// store's `EntityError` formatted for display.
    | StorageFailed of string
    /// Caller lacks the required permission. Reserved for the API
    /// gating layer; `IFormStore` itself does not raise this.
    | Unauthorised
    /// `ApplyTransition` referenced an event the current state
    /// doesn't accept.
    | InvalidTransition of currentState: string * attemptedEvent: string
    /// `ApplyTransition` named a workflow that wasn't registered.
    | WorkflowNotFound of workflowId: string
    /// Phase 21d — guard predicate threw during evaluation. Distinct
    /// from `TransitionDenied` (which is a clean `Error reason`
    /// return from the guard) so callers can choose to retry on
    /// transient exceptions vs surface a hard denial to the user.
    /// `guard` is the guard's registered name; `reason` is the
    /// exception message.
    | GuardEvaluationFailed of guard: string * reason: string
    /// Phase 21d — workflow action threw and the action's
    /// `ActionFailurePolicy` is `FailSubmission`. The transition has
    /// been rolled back; submission state is unchanged. `actionName`
    /// is the registered action name; `reason` is the exception
    /// message captured at the call site.
    | ActionFailed of actionName: string * reason: string
    /// Phase 21d — the action ledger holds a `Pending` entry from a
    /// prior attempt and the action's `ActionFailurePolicy` is
    /// `FailSubmission`. The earlier attempt persisted the
    /// transition state but never resolved the action invocation
    /// (process died mid-flight, or another instance is still
    /// running it). Caller decides whether to wait, retry, or
    /// escalate. `submissionId` + `actionName` identify the stuck
    /// invocation.
    | ActionPendingFromPriorAttempt of submissionId: string * actionName: string
    /// Phase 21e — per-token rate limit exceeded. Surfaced by
    /// `IPublicFormApi.SubmitWithToken` when the token's
    /// `ShareTokenClaim.RateLimit.MaxUses` has been consumed inside
    /// the rolling `Window`. Distinct from `Unauthorised` so
    /// server-side audit can distinguish the case; the public embed
    /// collapses both to the same user-visible message (L1).
    | RateLimited