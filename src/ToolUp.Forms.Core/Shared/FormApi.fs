// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormApi

open System
open ToolUp.Platform // 0.5.0 — forge-native auth + audit attributes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.AggregationTypes

// ─── Phase 21 — Fable.Remoting wire contract ────────────────────────
//
// The client-facing API. Method shapes mirror `IFormStore` /
// `IWorkflowEngine` minus per-request `scopeId` / actor parameters —
// those are resolved server-side from the caller's `AccessContext`.
// Writes that mutate shared state (`SaveSchema`, `DeleteSchema`) are
// Owner/Admin-gated by the handler before reaching the store.
//
// Read methods return data directly; write methods return
// `Result<X, FormError>` so the UI can branch on validation /
// not-found / permission / workflow failures with a single match.

/// `Submit` request payload — server fills in `Id`, `SubmittedAt`,
/// `SubmittedBy`, `SchemaVersion`, and `Type` / `Version` reflection
/// fields from the resolved `AccessContext` and the latest schema.
type SubmitRequest = {
    /// Schema this submission satisfies.
    FormId: FormSchemaId
    /// Field values keyed by `FieldSchema.Key`. Validated server-side.
    Values: Map<string, FieldValue>
    /// Optional workflow binding. `None` => state becomes `Submitted`;
    /// `Some workflowId` => state becomes the workflow's `InitialState`.
    WorkflowId: WorkflowId option
}

/// `UpdateDraft` request — only valid against submissions in `Draft`
/// state. Server preserves `SubmittedBy` / `SubmittedAt` from the
/// original; only `Values` are replaced wholesale.
type UpdateDraftRequest = {
    SubmissionId: SubmissionId
    Values: Map<string, FieldValue>
}

/// `ApplyTransition` request — drives a workflow event against an
/// existing submission. Guard runs server-side; on success the new
/// state is persisted and the action (if any) is invoked.
type ApplyTransitionRequest = {
    SubmissionId: SubmissionId
    Event: TransitionEvent
}

/// Phase 21b — `IssueTokens` recipient. `Handle` is opaque to the
/// platform — the issuer chooses the format (email, hashed panel id,
/// arbitrary string). It's persisted as the token's
/// `AttributedHandle` and echoed back on every successful submission
/// so the creator can join results to their own respondent records
/// without the platform having to interpret the value.
type IssueRecipient = {
    Handle: string
    DisplayName: string option
}

/// Phase 21b — `IssueTokens` request payload. Owner/Admin gated.
/// Server-side checks: the schema must exist, must be `Publishable`,
/// and `ServerConfig.PublicBaseUrl` must be set.
type IssueTokensRequest = {
    [<PiiSafe>]
    SchemaId: FormSchemaId
    // `Recipients` deliberately NOT [<PiiSafe>] — handles may be
    // emails / panel ids and display names are personal data; the
    // audit payload redacts the field.
    Recipients: IssueRecipient list
    /// `None` = use the share-token store's default lifetime
    /// (typically issue-time + 30 days).
    [<PiiSafe>]
    ExpiresAt: DateTimeOffset option
    /// `None` = use the share-token store's default (`Some 1` —
    /// one-and-done). `Some None` = explicitly unlimited; useful
    /// for shared-link surveys. `Some (Some n)` = at most `n` uses.
    [<PiiSafe>]
    UseLimit: int option option
}

/// Phase 21b — one row per issued token in `IssueTokens`'s response.
/// `Url` is fully composed against `ServerConfig.PublicBaseUrl` so
/// the caller can hand the entire CSV to a downstream distribution
/// system without running its own URL builder.
type IssuedToken = {
    /// Echoed verbatim from the request so the caller can correlate
    /// rows in the response with rows in their input list.
    Handle: string
    /// Platform-generated public id; persisted on the
    /// `ShareTokenClaim`. Useful for revocation (`IShareTokenStore.
    /// Revoke`) without having to retain the original wire string.
    TokenId: string
    /// Fully-composed embed URL: `{PublicBaseUrl}/r/{token}`.
    Url: string
    ExpiresAt: DateTimeOffset
}

/// Phase 21b — `DispatchInvitationsByEmail` recipient. Carries a real
/// email address (unlike `IssueRecipient.Handle` which is opaque).
/// The address is stored server-side in the address book under a
/// synthesised `respondent:{tokenId}` userId so the no-PII-on-wire
/// principle of Phase 6f's notification channel is preserved —
/// envelopes carry only the synthesised id, the sink resolves to
/// the real address at dispatch time.
type EmailInvitation = {
    Email: string
    /// Substituted into the body template's `{{displayName}}`
    /// placeholder when present; falls back to the email address
    /// itself if absent.
    DisplayName: string option
}

/// Phase 21b — `DispatchInvitationsByEmail` request payload. Convenience
/// wrapper around `IssueTokens` that issues tokens AND publishes one
/// `TransactionalEmail` notification per recipient. Apps that prefer
/// to dispatch via their own MTA / a third-party survey provider call
/// `IssueTokens` directly and ignore this surface entirely.
///
/// `BodyTemplate` substitution variables: `{{link}}` (mandatory —
/// the embed URL), `{{displayName}}` (optional, falls back to the
/// recipient's email address).
type DispatchInvitationsRequest = {
    // Phase 69e.H — required identifiers / message body are rejected
    // up-front when blank (an empty schema id misses the schema lookup;
    // an empty subject / body produces a broken invitation email).
    [<NotEmpty>]
    SchemaId: FormSchemaId
    Invitations: EmailInvitation list
    ExpiresAt: DateTimeOffset option
    UseLimit: int option option
    [<NotEmpty>]
    Subject: string
    [<NotEmpty>]
    BodyTemplate: string
}

/// Phase 21b (slice 7) — derived survey lifecycle status. Renderer-
/// friendly tri-state computed from `(Visibility, has-any-tokens?)`.
/// Not persisted on the schema record — the server derives it on
/// every `ListSchemasOverview` call so it stays in sync with the
/// underlying `IShareTokenStore` state.
type SurveyStatus =
    /// `Visibility = Internal` AND no tokens have ever been issued.
    /// Operator hasn't started distributing yet.
    | Draft
    /// `Visibility = Publishable`. Currently accepting submissions
    /// (per-token validity still applies — expired / revoked tokens
    /// fail at the public submit handler).
    | Active
    /// `Visibility = Internal` AND at least one token was issued
    /// historically. The survey was distributed but is now closed —
    /// existing tokens fail validation, history is preserved, no
    /// new tokens can be issued without flipping back to Publishable.
    | Closed

/// Phase 21b (slice 7) — one row per schema in the multi-survey
/// overview. Composes `(schema, submissionCount, invitedCount,
/// responseRate, status)` into a single payload so the operator
/// admin doesn't N+1 `GetAggregations` per row when rendering "all
/// my surveys".
type SurveyOverviewRow = {
    Schema: FormSchema
    /// Committed (non-`Draft`) submissions for this schema in scope.
    SubmissionCount: int
    /// Total tokens issued (active + revoked + expired). Always 0
    /// when `ServerConfig.ShareTokenStore = NoShareTokenStore`.
    InvitedCount: int
    /// `SubmissionCount / InvitedCount` (None when InvitedCount = 0
    /// to avoid division-by-zero); renderer formats as percentage.
    ResponseRate: float option
    Status: SurveyStatus
}

/// Phase 21b (slice 7) — `CloseSurvey` operation mode.
type CloseSurveyMode =
    /// Flip `Visibility` to `Internal`. The
    /// `PublicFormApiHandler` re-checks visibility on every
    /// request, so existing tokens immediately fail validation
    /// without a per-token revoke pass. Cheaper but reversible —
    /// flipping `Visibility` back to `Publishable` re-activates
    /// every outstanding non-expired token.
    | HideSchema
    /// Bulk-revoke every outstanding token via
    /// `IShareTokenStore.Revoke` in a loop. Schema's `Visibility`
    /// stays unchanged — operator can issue new tokens later
    /// without an extra step. Irreversible at the per-token level.
    | RevokeAllTokens
    /// Both. Default for "I'm done with this survey" workflows —
    /// flips visibility AND revokes tokens so re-publishing
    /// requires both re-issuing tokens AND flipping visibility.
    | HideSchemaAndRevoke

/// Phase 21b (slice 7) — `CloseSurvey` request payload.
type CloseSurveyRequest = {
    [<PiiSafe>]
    SchemaId: FormSchemaId
    [<PiiSafe>]
    Mode: CloseSurveyMode
}

/// Phase 21b (slice 7) — `CloseSurvey` response. `TokensRevoked`
/// counts tokens the call actually marked revoked (already-revoked
/// tokens don't recount). `NewVisibility` reflects whatever
/// `Visibility` value is in effect after the call — `Internal` for
/// `HideSchema` / `HideSchemaAndRevoke`, unchanged for
/// `RevokeAllTokens` only.
type CloseSurveyResult = {
    SchemaId: FormSchemaId
    NewVisibility: FormVisibility
    TokensRevoked: int
}

/// Phase 21b — `DispatchInvitationsByEmail` per-recipient outcome.
/// `Email` is the original input; `TokenId` / `Url` are populated
/// when the issuance step succeeded regardless of dispatch outcome
/// (so the operator can re-send from outside the platform if email
/// delivery is flaky); `Error` carries an operator-visible
/// diagnostic for failures.
type DispatchedInvitation = {
    Email: string
    TokenId: string option
    Url: string option
    Error: string option
}

/// Phase 21b — `DispatchInvitationsByEmail` response summary. Carries
/// per-recipient outcomes so the caller can surface a "3 sent, 1
/// failed" summary in the admin UI without re-querying.
type DispatchSummary = {
    Recipients: DispatchedInvitation list
    /// Total successfully issued + queued for dispatch. Differs from
    /// `Recipients.Length` only when issuance failed for some.
    SuccessCount: int
}

/// Fable.Remoting record-of-functions. Each call goes over HTTP via
/// `ToolUp.Remoting.Client` proxy; server-side handler in
/// `Server/FormApiHandler.fs` resolves the AccessContext, applies
/// permission gating, and delegates to `IFormStore` / `IWorkflowEngine`.
type IFormApi = {
    // ─── Schema management (Owner/Admin) ─────────────────────────
    // Handler gate (`FormApiHandler.withWriteGate`): Owner/Admin in
    // Team mode; ungated in user-scoped / anonymous-session modes —
    // so the dispatcher-level classification is `AllowAnonymous` and
    // the in-handler role gate + StorageScope isolation keep gating.
    [<AllowAnonymous>]
    SaveSchema: FormSchema -> Async<Result<FormSchema, FormError>>
    [<AllowAnonymous>]
    GetSchema: FormSchemaId * int option -> Async<FormSchema option>
    [<AllowAnonymous>]
    ListSchemas: unit -> Async<FormSchema list>
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    DeleteSchema: FormSchemaId -> Async<Result<unit, FormError>>

    // ─── Submission lifecycle (any authenticated) ────────────────
    [<AllowAnonymous>]
    Submit: SubmitRequest -> Async<Result<Submission, FormError>>
    [<AllowAnonymous>]
    UpdateDraft: UpdateDraftRequest -> Async<Result<Submission, FormError>>
    [<AllowAnonymous>]
    GetSubmission: SubmissionId -> Async<Submission option>
    /// Filtered list. Members see their own submissions only;
    /// Owner/Admin see everything in scope. Server intersects the
    /// caller-supplied query with the visibility predicate.
    [<AllowAnonymous>]
    ListSubmissions: EntityQuery<Submission> -> Async<Result<Submission list, FormError>>

    // ─── Workflow operations ─────────────────────────────────────
    [<AllowAnonymous>]
    ApplyTransition: ApplyTransitionRequest -> Async<Result<Submission, FormError>>
    /// Enumerate transitions available from the submission's current
    /// state under the workflow it's bound to. Empty list if the
    /// submission has no workflow or has reached a terminal state.
    [<AllowAnonymous>]
    ListPossibleTransitions: SubmissionId -> Async<Transition list>

    // ─── Phase 21b — share-link issuance (Owner/Admin) ────────────
    /// Bulk-issue share-link tokens against a `Publishable` form.
    /// Returns one `(handle, tokenId, url, expiresAt)` row per
    /// recipient. The platform sends nothing — the caller handles
    /// distribution (own MTA, third-party survey provider, paper /
    /// QR codes, etc.). Requires `ServerConfig.ShareTokenStore =
    /// EnabledShareTokenStore` and `PublicBaseUrl = Some _`.
    [<AllowAnonymous>]
    [<Audit "Custom:ShareTokensIssued">]
    IssueTokens: IssueTokensRequest -> Async<Result<IssuedToken list, FormError>>

    // ─── Phase 21b — aggregation dashboard (Owner/Admin) ──────────
    /// Roll-up the response distribution for a form. Returns
    /// per-question aggregations keyed by `FieldSchema.Key`, the
    /// response-rate progress against issued tokens, and per-
    /// recipient response status. Only counts non-`Draft`
    /// submissions; counts include both authenticated-user and
    /// invited-respondent submissions. Reserved analyser-output
    /// slot stays empty until an `IFormSubmissionAnalyser`
    /// companion is registered server-side.
    [<AllowAnonymous>]
    GetAggregations: FormSchemaId -> Async<Result<AggregationSummary, FormError>>

    // ─── Phase 21b (slice 6) — optional email-dispatch convenience
    /// Issue tokens AND mail the resulting embed URLs in one call.
    /// Convenience wrapper over `IssueTokens` for deployments that
    /// want platform-driven dispatch; deployments that distribute
    /// via their own system or a third-party survey provider use
    /// `IssueTokens` directly and skip this surface. Requires
    /// transactional email (`ServerApp.withTransactionalSink`)
    /// alongside the existing `IssueTokens` prerequisites
    /// (ShareTokenStore enabled + PublicBaseUrl set + schema
    /// Publishable).
    [<AllowAnonymous>]
    DispatchInvitationsByEmail: DispatchInvitationsRequest -> Async<Result<DispatchSummary, FormError>>

    // ─── Phase 21b (slice 7) — multi-survey admin ─────────────────
    /// One-call list of every schema in scope with metric
    /// summaries. Powers the multi-survey overview pane (operator's
    /// "all my surveys" list). Single round-trip — avoids per-row
    /// `GetAggregations` calls. Any authenticated caller; the
    /// returned list is filtered to the caller's scope by the
    /// underlying `IFormStore.ListSchemas` semantics.
    [<AllowAnonymous>]
    ListSchemasOverview: unit -> Async<SurveyOverviewRow list>
    /// Close a survey. `HideSchema` flips `Visibility` to
    /// `Internal` (instant — `PublicFormApiHandler` re-checks on
    /// every request); `RevokeAllTokens` enumerates the survey's
    /// tokens and revokes each individually (cleaner audit trail,
    /// re-publishing doesn't re-activate); `HideSchemaAndRevoke`
    /// does both. Owner/Admin gated. Returns the count of tokens
    /// revoked plus the post-call visibility.
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    CloseSurvey: CloseSurveyRequest -> Async<Result<CloseSurveyResult, FormError>>

    // ─── Phase 21c — analyser-output cache control (Owner/Admin) ──
    /// Force a re-run of every registered `IFormSubmissionAnalyser`
    /// against this schema's submissions. Used when an analyser's
    /// *configuration* changes without a schema bump — e.g. swapping
    /// the AI provider behind a registered sentiment analyser, or
    /// tuning a topic-cluster threshold. Owner/Admin gated. Returns
    /// the count of cache entries invalidated. No-op (returns `0`)
    /// when no cache is wired (deployments without
    /// `withAnalyserCache`).
    [<AllowAnonymous>]
    RebuildAnalyserOutputs: FormSchemaId -> Async<Result<int, FormError>>
}

/// HTTP route prefix for the forms API. Mirrors `IPlatformApi` /
/// `ISchedulingApi` convention so the client proxy and the server
/// handler agree on URL shape without naming the routes individually.
let routeBuilder (typeName: string) (methodName: string) : string =
    sprintf "/api/%s/%s" typeName methodName