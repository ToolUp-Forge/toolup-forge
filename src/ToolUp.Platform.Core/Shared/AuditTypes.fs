// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Audit event types ───────────────────────────────────────────
//
// SDK-emitted audit events for compliance and incident response.
// Every state-changing operation — login, team CRUD, file ops,
// permission changes — is recorded as an `AuditEvent` via
// `IAuditLog`. Persistence reuses the existing `IEventStore`
// surface: each `AuditEvent` is serialised to a `ModuleEvent` with
// `SourceModule = AuditSourceModule` so audit events flow through the
// same blob layout, retention policy, and webhook hooks as every other
// platform event. The DU is the typed F# surface; the wire format is
// `ModuleEvent`.

/// Reserved `SourceModule` for audit events. Filtering `IEventStore`
/// reads on this constant returns the audit trail only — `ReadBySource`
/// is the canonical query path.
module AuditSourceModule =
    [<Literal>]
    let value = "_platform.audit"

/// Phase 66 Stream B.7 (design §3.6 + D15) — who/what an audit event
/// is attributed to. Maps 1:1 to the four `Subject` constructors but
/// shaped for the audit-side serialised form: `ClaimAudit` flattens the
/// `ShareTokenClaim` to the fields downstream sinks actually query on
/// (`tokenId`, `attributedHandle`, `resourceKind`, `resourceId`) so
/// Splunk / Datadog / S3-Archive read structured fields rather than
/// parsing a nested claim record. The four cases are exhaustive across
/// every `Subject` shape; new subject kinds force a new `AuditSubject`
/// case via the [[d15]] contract.
type AuditSubject =
    /// Unauthenticated session-scoped subject. `sessionId` mirrors the
    /// `Subject.AnonymousSession sid` value (the `X-User-Id` cookie or
    /// a freshly-minted GUID when the request did not carry one).
    | AnonymousAudit of sessionId: string
    /// Authenticated user without an active team scope. Covers both the
    /// `Subject.AuthenticatedUser` shape and the dispatcher-derived
    /// "scope is `_platform` so attribute to a system-pseudo-user" path
    /// — the `userId` field carries the sentinel in the latter case.
    | UserAudit of userId: string
    /// Authenticated user acting within a team scope. Both fields
    /// populated when the originating `Subject` was `TeamMember`. When
    /// the dispatcher derives the envelope from `ScopeId` alone (e.g.
    /// because the originating `AccessContext.Subject` was not persisted
    /// alongside the `ModuleEvent`), `userId` is the literal
    /// `"_dispatcher"` sentinel — sinks read this as "team event with
    /// unknown actor" rather than "team event by user named '_dispatcher'".
    | TeamAudit of userId: string * teamId: string
    /// Anonymous reach into a persistent scope gated by a validated
    /// `ShareTokenClaim`. The flattened shape (vs the nested
    /// `ShareTokenClaim` record carried on `Subject.ClaimBearer`) is
    /// deliberate — sinks query on these four fields directly.
    | ClaimAudit of tokenId: string * attributedHandle: string option * resourceKind: string * resourceId: string

// `AuditSubjectKind` is defined in `Shared/Types/AuditSampling.fs`
// (compiled before SDK.Shared.fs so `ServerConfig` can carry an
// `AuditSamplingPolicy`). The `kind` / `kindString` projections below
// reference it from that earlier file.

module AuditSubject =
    /// Project an `AuditSubject` to its lightweight kind tag. Used by
    /// sinks to emit a `subject_kind` tag (Datadog / OpenTelemetry) or a
    /// `_meta.kind` envelope field (Splunk HEC) without inspecting the
    /// payload.
    let kind =
        function
        | AnonymousAudit _ -> AnonymousAuditKind
        | UserAudit _ -> UserAuditKind
        | TeamAudit _ -> TeamAuditKind
        | ClaimAudit _ -> ClaimAuditKind

    /// String form of `kind` — what sinks actually emit as a tag value.
    /// Stable across the audit-schema-version envelope bump; do not
    /// rename without bumping `LatestAuditSchemaVersion`.
    let kindString =
        function
        | AnonymousAuditKind -> "anonymous"
        | UserAuditKind -> "user"
        | TeamAuditKind -> "team"
        | ClaimAuditKind -> "claim"

    /// Construct an `AuditSubject` from the request-side `Subject`. The
    /// canonical bridge — every audit emission site that has a resolved
    /// `AccessContext.Subject` should use this rather than re-derive.
    let fromSubject (subject: Subject) : AuditSubject =
        match subject with
        | AnonymousSession sid -> AnonymousAudit sid
        | AuthenticatedUser uid -> UserAudit uid
        | TeamMember(uid, tid) -> TeamAudit(uid, tid)
        | ClaimBearer claim -> ClaimAudit(claim.TokenId, claim.AttributedHandle, claim.ResourceKind, claim.ResourceId)

    /// Sentinel `userId` for `TeamAudit` cases derived from `ScopeId`
    /// alone — see `fromScopeId`. Sinks treat this value as "team event
    /// with unknown actor".
    [<Literal>]
    let DispatcherSentinelUserId = "_dispatcher"

    /// Sentinel `userId` for `UserAudit` cases derived from the
    /// reserved `_platform` scope id — system-level events with no
    /// per-user attribution at the dispatcher layer.
    [<Literal>]
    let PlatformSentinelUserId = "_platform"

    /// Phase 66 Stream B.7 — derive a best-effort `AuditSubject` from
    /// the `IEventStore` `ScopeId` alone. Used by the audit-replicator
    /// dispatch path, which decodes the persisted `ModuleEvent` back
    /// into an `AuditEvent` without access to the originating
    /// `AccessContext.Subject` (today's `ModuleEvent` envelope does not
    /// persist the subject — Phase 66 follow-on substrate could extend
    /// the wire format; until then, the dispatcher carries this
    /// approximation).
    ///
    /// Mapping:
    /// - `_platform` → `UserAudit "_platform"` (system-level event).
    /// - `session-{sid}` → `AnonymousAudit sid`.
    /// - `user-{uid}` → `UserAudit uid`.
    /// - `team-{tid}` → `TeamAudit ("_dispatcher", tid)`.
    /// - Anything else → `UserAudit scopeId` as a last-resort
    ///   fallback so the envelope always has a subject (downstream
    ///   sinks can tag-route on the unprefixed scope id).
    let fromScopeId (scopeId: string) : AuditSubject =
        if System.String.IsNullOrWhiteSpace scopeId then
            UserAudit PlatformSentinelUserId
        elif scopeId = "_platform" then
            UserAudit PlatformSentinelUserId
        elif scopeId.StartsWith("session-", System.StringComparison.Ordinal) then
            AnonymousAudit(scopeId.Substring 8)
        elif scopeId.StartsWith("user-", System.StringComparison.Ordinal) then
            UserAudit(scopeId.Substring 5)
        elif scopeId.StartsWith("team-", System.StringComparison.Ordinal) then
            TeamAudit(DispatcherSentinelUserId, scopeId.Substring 5)
        else
            UserAudit scopeId

// `AuditSamplingPolicy` (Phase 66 Stream C.2) is defined in
// `Shared/Types/AuditSampling.fs`, compiled before SDK.Shared.fs so
// `ServerConfig` can carry it as a field.

/// First-seen-this-session login. The middleware that resolves the
/// caller's identity emits one of these on each user's first request
/// per session; subsequent requests don't re-emit. Auth providers have
/// no "login" callback (only OIDC's callback handler does), so
/// per-session is the closest practical equivalent.
type UserLoggedInPayload = {
    UserId: string
    /// `Header` / `StaticJwt` / `Oidc` / etc. — the resolved provider's
    /// own kind name.
    AuthProvider: string
}

type TeamCreatedPayload = {
    UserId: string
    TeamId: string
    TeamName: string
}

/// Phase 5f — `TeamApi.CreateTeam` denied because the caller does not
/// hold `PlatformRole.PlatformAdmin` under
/// `TeamCreationPolicy.PlatformAdminOnly`. Distinct from `TeamCreated`
/// (success path) so an admin reviewer can grep specifically for refusal
/// signal — repeated denials from one actor are a clear red flag that
/// the deployment's gate is doing its job and someone is bumping into
/// it. Captures the attempted team name verbatim so the trail records
/// what the caller was trying to create.
type TeamCreationDeniedPayload = {
    /// The caller whose `CreateTeam` was refused.
    UserId: string
    /// The team name the caller submitted. Echoed as-is — operators
    /// triaging a series of denials want to see what the user was
    /// asking for. No team-id is recorded because the gate fires
    /// before any id is minted.
    AttemptedName: string
}

type MemberAddedPayload = {
    UserId: string
    TeamId: string
    /// The user being added. Distinct from `UserId`, which is the actor.
    AffectedUserId: string
    Role: string
}

type MemberRemovedPayload = {
    UserId: string
    TeamId: string
    /// The user being removed. Distinct from `UserId`, which is the actor.
    AffectedUserId: string
}

type MemberRoleChangedPayload = {
    UserId: string
    TeamId: string
    AffectedUserId: string
    OldRole: string
    NewRole: string
}

type FileUploadedPayload = {
    UserId: string
    FileName: string
    DataType: string
    SizeBytes: int64
}

type FileDeletedPayload = { UserId: string; FileName: string }

/// Emitted by the file manager when a previously-uploaded file is
/// re-processed (the user clicks "Reprocess" on the file list, or
/// any future caller invokes `IFileManagementApi.ReprocessFile`).
/// `DataType` is the type the re-run detected — may differ from the
/// original upload's type if the registered detectors changed.
/// `HadError` flags whether the resulting `ProcessedFileEntry` carries
/// an `Error` (the persisted summary now records a processing failure)
/// so audit consumers can distinguish recoveries from stale-state
/// indicators without parsing the entry body.
type FileReprocessedPayload = {
    UserId: string
    FileName: string
    DataType: string
    HadError: bool
}

/// Emitted by the file manager when an Owner / Admin clicks the data-
/// store reset button — wipes every uploaded file plus its
/// `_processed_entry__` sidecar in the caller's storage scope. One
/// audit event per reset, not one per file: per-file `FileDeleted`
/// noise on a deliberate bulk operation isn't useful and drowns the
/// signal in the audit trail. `FileCount` is the count of files
/// removed (could be zero — empty-reset is still a recordable
/// operator action).
type DataStoreResetPayload = { UserId: string; FileCount: int }

/// Module-emitted "an analysis was run" event. SDK ships the case so
/// module code emits via `IAuditLog.Record` with a consistent shape;
/// the SDK never names a module so SDK never emits `AnalysisRun`
/// itself.
type AnalysisRunPayload = {
    UserId: string
    ModuleName: string
    /// Free-form summary suitable for compliance review — module-defined
    /// shape. Modules typically include the kind of analysis, data types
    /// involved, and a result summary.
    Summary: string
}

/// Permission grant or revocation. `ModuleName = ""` denotes a
/// team-defaults change (the defaults map was replaced wholesale).
type PermissionChangedPayload = {
    UserId: string
    TeamId: string
    /// The user whose permissions changed. May equal `UserId` for
    /// self-grants, but the audit event distinguishes actor from
    /// affected member explicitly.
    AffectedUserId: string
    /// Module identifier. Empty string for team-defaults changes.
    ModuleName: string
    /// Comma-separated list of granted permissions (`Read`, `Write`,
    /// `Admin`). Empty string = revoked.
    Permissions: string
}

/// Successful out-of-band transactional notification delivery
/// (email / SMS / push). Emitted by `TransactionalDispatcher` after a
/// sink returns `SinkResult.Delivered`. PII is intentionally not
/// recorded — the audit trail proves "we attempted to deliver to user
/// X" without persisting addresses; deployments correlate with vendor
/// logs via `VendorMessageId` / `CorrelationId`.
type NotificationSentPayload = {
    /// Actor who initiated the publish. Typically `"system"` when the
    /// publish came from a job lifecycle (`JobCompleted`); the
    /// authenticated user's id when published from a request handler.
    UserId: string
    /// Reserved `_platform.notifications` source-module label (set by
    /// the dispatcher; here for completeness so the trail is self-
    /// describing).
    ScopeId: string
    /// `NotificationKind.SinkKind` of the consuming sink — `"Email"`,
    /// `"Sms"`, or `"Push"`. Distinct from `Provider` (the vendor
    /// label) so rotating providers leaves the wire format stable.
    NotificationKind: string
    /// Vendor label from the sink's `Provider` member (`"Smtp"`,
    /// `"SendGrid"`, `"Twilio"`, `"WebPush"`). Surfaces in audit
    /// reports so deployments can tell which adapter delivered.
    Provider: string
    /// User ids the sink resolved through `INotificationAddressBook`
    /// — never the resolved addresses themselves. Empty list when the
    /// envelope had no recipients (sinks short-circuit to `Skipped`
    /// rather than emitting `NotificationSent`).
    RecipientUserIds: string list
    /// Vendor-side message id when the sink reported one. Used to
    /// correlate audit trail entries with vendor delivery logs.
    VendorMessageId: string option
    /// Caller-supplied `CorrelationId` from the envelope. Forwarded by
    /// some sinks to vendor idempotent-send headers; persisted here
    /// regardless so the trail can be queried by it.
    CorrelationId: string option
}

/// Permanent or retry-exhausted transactional notification
/// failure. Emitted by `TransactionalDispatcher` after a sink returns
/// `SinkResult.PermanentFailure` or after the retry budget is
/// exhausted on `SinkResult.TransientFailure`. As with
/// `NotificationSentPayload`, PII is intentionally absent.
type NotificationDeliveryFailedPayload = {
    UserId: string
    ScopeId: string
    NotificationKind: string
    Provider: string
    RecipientUserIds: string list
    /// Sink-supplied error message. Vendor-formatted strings are
    /// preserved verbatim so operators can copy them into vendor-side
    /// support tickets.
    Error: string
    /// Total dispatch attempts including the one that produced the
    /// final failure. `1` for `PermanentFailure` first-attempt;
    /// `MaxAttempts` for retry-exhausted `TransientFailure`.
    Attempts: int
    CorrelationId: string option
}

/// Out-of-band notification dropped because the publishing scope's
/// `_platform.notification_prefs` kill switch for the kind is
/// `false`. The dispatcher emits one of these per
/// envelope-level drop (not per recipient — the policy decision is
/// envelope-scoped, recipient hashes are listed within).
///
/// Recipient identifiers are SHA256-truncated to keep the audit
/// trail PII-free while remaining correlatable across events for
/// the same recipient.
type NotificationSilentlySkippedPayload = {
    /// Notification kind discriminator (`Email` / `Sms` / `Push`),
    /// matching the `INotificationSink.Kind` shape used elsewhere
    /// in audit / replicator events.
    NotificationKind: string
    /// Scope whose notification prefs caused the drop. Mirrors the
    /// envelope's `ScopeId`.
    ScopeId: string
    /// Why the dispatcher skipped delivery. Currently always
    /// `"team_opted_out"` — the only silent-drop path. Future
    /// reasons (rate-limited, sink-disabled-globally) extend this.
    Reason: string
    /// `SHA256(userId)[..8]` for each recipient on the dropped
    /// envelope. Empty for system-published envelopes with no
    /// resolvable recipients.
    RecipientHashes: string list
    /// Optional correlation id from the envelope, preserved
    /// verbatim so the drop event can be tied back to the publish
    /// site (job id, request id, etc.).
    CorrelationId: string option
}

/// Forms companion lifecycle events. Emitted by
/// `FormApiHandler` after successful Submit / UpdateDraft, and by
/// `WorkflowEngine.Apply` after a successful state transition. PII
/// is intentionally absent — payloads carry stable identifiers
/// (FormId / SubmissionId / states / actor userId) but never the
/// submitted field values themselves. Operators correlate against
/// the entity-store blob via `(SubmissionId, Version)` if they need
/// the values.
type FormSubmittedPayload = {
    /// Actor who submitted the form. Always the authenticated caller
    /// — server-set from `AccessContext.UserId`.
    UserId: string
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
    UserId: string
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
    UserId: string
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

/// Phase 21d — workflow action invocation outcome. Emitted by the
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

/// Entity-store lifecycle events. Emitted by `BlobEntityStore`
/// after successful Save / Delete, swallowed-on-failure (audit emission
/// must never fail the primary operation). Each case carries the
/// (entityType, entityId, version) tuple as its payload — enough to
/// cross-reference against the entity blob in `IDataObjectStore`.
type EntityLifecycleEventPayload = {
    /// Actor who triggered the lifecycle event. `"system"` when the
    /// SDK does the write (auto-creation paths); the authenticated
    /// user's id for explicit module-driven CRUD.
    UserId: string
    /// Registered entity-type name (the `Type` field on the record).
    EntityType: string
    /// Entity instance identifier within `(entityType, scopeId)`.
    EntityId: string
    /// Version assigned by the store. For `EntityCreated` this is 1;
    /// for `EntityUpdated` the new version (>1); for `EntityDeleted`
    /// the head version at delete time.
    Version: int
}

/// Encryption-key lifecycle events. Emitted by
/// `IBlobEncryptionKeyResolver` implementations whose lifecycle the
/// SDK manages (`SingleKeyResolver`, `PerScopeKeyResolver`). KMS-backed
/// resolvers typically delegate lifecycle to the cloud KMS itself and
/// do not emit these events — the KMS's own audit log carries the
/// trail.
///
/// `KeyId` travels in the payload so the audit trail can be cross-
/// referenced against encrypted blobs' envelope headers (which carry
/// the same `KeyId`). `ScopeId` distinguishes per-scope keys
/// (`PerScopeKeyResolver`) from the platform-wide key
/// (`SingleKeyResolver` always carries `_platform`).
type EncryptionKeyEventPayload = {
    /// Actor who triggered the lifecycle event. `"system"` for
    /// auto-creation on first resolution; the authenticated user's id
    /// for explicit destruction via the admin endpoint.
    UserId: string
    /// Scope the key belongs to. `_platform` for the platform-wide
    /// `SingleKeyResolver` master key; the team / user scope id for
    /// `PerScopeKeyResolver` per-tenant keys.
    ScopeId: string
    /// Stable key identifier. `_platform/master/v1` for the single-key
    /// resolver; `_platform/scopes/{scopeId}/v1` for per-scope keys.
    /// Same value as the envelope header on encrypted blobs.
    KeyId: string
    /// Resolver class name — `"SingleKeyResolver"`, `"PerScopeKeyResolver"`,
    /// or a third-party impl's name. Surfaces in audit reports so
    /// deployments can confirm which resolver produced each lifecycle
    /// event.
    Resolver: string
}

/// Debounced health-probe state transition. Emitted by
/// `HealthStateTracker` after a probe's stable state changes (3
/// consecutive observations of a new status). Single-observation
/// flaps from 1–10 Hz LB polling are absorbed by the debounce, so the
/// audit trail records only material transitions.
///
/// `FromStatus` / `ToStatus` are strings, not the `HealthResult` DU,
/// so changes to the DU don't ripple into persisted audit payloads;
/// values match `HealthResult.status` ("Healthy" / "Degraded" /
/// "Unhealthy"). `Message` carries the last observation's message —
/// useful when transitioning into Degraded / Unhealthy.
type HealthStateChangedPayload = {
    /// `IHealthCheck.Name` — stable identifier for the probe whose
    /// state changed.
    ProbeName: string
    /// Previous stable status. The first transition for a probe
    /// surfaces "Healthy" by convention even if the very first three
    /// observations were Unhealthy — operators reading the trail want
    /// the *change*, not the bootstrap.
    FromStatus: string
    ToStatus: string
    /// Last observation's message. Empty for transitions into
    /// `Healthy`; carries the failure detail for transitions into
    /// `Degraded` / `Unhealthy`.
    Message: string
    /// Server wall-clock at the moment the third consecutive
    /// observation landed and the transition was recorded.
    ObservedAt: DateTime
}

/// Audit-replicator lifecycle events. Emitted by
/// `AuditReplicator` after a sink batch is delivered, fails (transiently
/// or permanently), or is dead-lettered after retry exhaustion. These
/// are recorded under `_platform` (operator-level) rather than per-tenant
/// so a single audit-trail query gives the operator a global view of
/// pipeline health; the source-scope of the replicated batch travels in
/// the payload's `BatchScopeId` so deployments can filter by tenant.
///
/// **Anti-recursion.** The replicator's `IEventStore` decorator filters
/// these three event types out of its enqueue path so the events do not
/// loop back into the pipeline. They appear in `IAuditLog.GetAuditTrail`
/// for operator visibility but never trigger replication.
///
/// **Volume.** `AuditSinkDelivered` fires once per successfully delivered
/// batch. With `BatchPolicy.LingerMs = 1000` and steady audit traffic, a
/// deployment may emit one delivered-event per sink per second per scope
/// of activity. Tune `LingerMs` upward to reduce the audit-pipeline-on-
/// audit-pipeline volume.
type AuditSinkDeliveredPayload = {
    /// Sink identity from `IAuditSink.Name`. Stable across the
    /// deployment lifetime.
    SinkName: string
    /// Scope whose audit events were delivered in this batch. The
    /// dispatcher delivers one batch per (sinkName, scopeId) pair, so
    /// this is unambiguous.
    BatchScopeId: string
    /// Number of events delivered in the batch. Cardinality only;
    /// payloads do not travel in the audit trail.
    BatchSize: int
    /// `OccurredAt` of the newest event in the batch — the value the
    /// cursor advances to after this delivery succeeds.
    LastDeliveredAt: DateTime
    /// Wall-clock at the moment the delivery succeeded.
    DeliveredAt: DateTime
}

/// Transient delivery failure. Emitted on each retryable
/// failure (sink returned `Result.Error`, retry budget not yet exhausted).
/// Operators monitor these to spot sinks that are consistently slow or
/// unhealthy without yet hitting dead-letter.
type AuditSinkFailedPayload = {
    SinkName: string
    BatchScopeId: string
    BatchSize: int
    /// Attempt number that failed (1-indexed). The dispatcher retries up
    /// to `RetryPolicy.MaxAttempts` before emitting `AuditSinkDeadLettered`.
    AttemptNumber: int
    /// Sink-supplied error message.
    Error: string
    /// Wall-clock at the moment the attempt failed.
    FailedAt: DateTime
}

/// Terminal delivery failure. Emitted once per batch after
/// `RetryPolicy.MaxAttempts` retries fail. The cursor advances PAST the
/// dead-lettered batch (so subsequent events still flow); this audit
/// event is the operator's signal to investigate, not a blocker.
type AuditSinkDeadLetteredPayload = {
    SinkName: string
    BatchScopeId: string
    BatchSize: int
    /// Total attempts including the final failure (== `RetryPolicy.MaxAttempts`).
    AttemptCount: int
    /// Last sink-supplied error message.
    LastError: string
    /// Wall-clock at the moment the dead-letter decision was taken.
    DeadLetteredAt: DateTime
}

/// One or more events in a replication batch failed to decode
/// (schema drift, corrupt payload, future-version event).
/// `AuditReplicator` filters undecodable events out of the batch via
/// `List.choose` and advances the cursor past them; without this audit
/// row the gap is invisible — sinks log only the events that DID
/// decode, so SOC 2 / GDPR Article 30 / SOX compliance assertions
/// become unverifiable. One row emitted per batch with at least one
/// decode failure (not one per failure — that would amplify into
/// hundreds of rows on a schema-drift sweep).
type AuditEventDecodeFailedPayload = {
    /// Sink whose batch contained undecodable events.
    SinkName: string
    /// Scope of the batch.
    BatchScopeId: string
    /// Total events in the batch (decoded + undecodable).
    BatchSize: int
    /// Count of events that failed to decode.
    FailedCount: int
    /// Up to the first 50 failed event ids. Bounded to keep the audit
    /// payload from growing unboundedly during a schema-drift sweep.
    FailedEventIds: Guid list
    /// Distinct event types that failed to decode. Same 50-cap as above.
    FailedEventTypes: string list
    /// Wall-clock at the moment the decode failures were observed.
    FailedAt: DateTime
}

/// OAuth Authorization Code substrate lifecycle events.
/// Emitted by the SDK's OAuth callback / disconnect / refresh paths
/// after the corresponding `IOAuthCredentialFlow` operation succeeds
/// or fails. Source-module label is `_platform.oauth`. Payloads carry
/// the data-source identity and the actor user id but never the
/// upstream tokens themselves — refresh tokens stay in `ISecretStore`,
/// access tokens are minted per-call and never persisted.
type OAuthConnectedPayload = {
    /// Actor who completed the upstream consent flow. Read from the
    /// caller's `AccessContext.UserId` at /authorize time and pinned
    /// to the state-store entry — survives the round-trip even if
    /// the client switches teams during consent.
    UserId: string
    /// Scope where the refresh token was persisted via
    /// `ISecretStore.SetSecret(scope, "{flowName}-refresh-{dataSourceId}", ...)`.
    ScopeId: string
    /// `IOAuthCredentialFlow.Name` — the flow that minted the
    /// credentials. Stable across the deployment lifetime.
    FlowName: string
    /// Data source the connection is bound to. Admin UIs cross-link
    /// from the audit trail to the data-source detail view.
    DataSourceId: string
    /// Wall-clock at the moment the callback succeeded. Surfaces in
    /// `CredentialStatus.Connected of connectedAt` for admin-UI
    /// display.
    ConnectedAt: DateTime
}

/// User-initiated disconnect. Emitted after the substrate
/// deletes the local refresh token; the optional upstream revocation
/// call may have succeeded, returned `RevocationUnsupported`, or
/// failed (see `UpstreamRevoked`).
type OAuthDisconnectedPayload = {
    UserId: string
    ScopeId: string
    FlowName: string
    DataSourceId: string
    /// `true` when `IOAuthCredentialFlow.Revoke` returned `Ok`.
    /// `false` for `RevocationUnsupported` or any other error — the
    /// substrate proceeds with local secret deletion regardless,
    /// since the user's intent is clear.
    UpstreamRevoked: bool
}

/// Refresh-token rotation / revocation upstream. Emitted
/// when `IOAuthCredentialFlow.RefreshAccessToken` returns
/// `ProviderRejected "invalid_grant"` (or equivalent). The substrate
/// transitions `CredentialStatus` to `NeedsReauthorization` and the
/// admin UI surfaces a "Reconnect required" banner.
type OAuthRefreshFailedPayload = {
    /// Actor recorded on the most recent `OAuthConnected`. `"system"`
    /// when the failure surfaces from a scheduled-refresh job rather
    /// than an interactive request.
    UserId: string
    ScopeId: string
    FlowName: string
    DataSourceId: string
    /// Provider's own diagnostic (`"invalid_grant"`, `"invalid_client"`,
    /// etc.). Verbatim — operators understand provider error codes
    /// faster than translated text.
    Reason: string
}

// ─── Phase 10h — OAuth token refresh substrate audit payloads ───────────
//
// Emitted by `IOAuthTokenRefresher` / `OAuthRefreshJobHandler` for the
// background refresh lifecycle. Distinct from the existing
// `OAuthRefreshFailed` (which is per-call refresh from
// `IOAuthCredentialFlow.RefreshAccessToken`): the 10h family covers
// the *scheduled* refresh path. Reserved source-module label is
// `_platform.oauth.refresh`. Per-provider tags (`Provider`,
// `ConfigId`) carry no secret material; tokens stay behind
// `ISecretStore` keys.

module OAuthRefreshSourceModule =
    /// Reserved `SourceModule` for `IOAuthTokenRefresher` /
    /// `OAuthRefreshJobHandler` audit events. Filter
    /// `IEventStore.ReadBySource` on this constant for the
    /// background-refresh audit trail.
    [<Literal>]
    let value = "_platform.oauth.refresh"

/// Background refresh succeeded. The substrate has persisted the new
/// access token (and rotated refresh token, when the upstream rotated
/// it) before this event is emitted. Powers the
/// `toolup.oauth.refresh.succeeded_total` metric +
/// `toolup.oauth.refresh.latency_ms` histogram tagged by `Provider`.
type OAuthTokenRefreshedPayload = {
    /// `IOAuthCredentialFlow.Name` — the flow that minted the
    /// credentials originally. Per-provider audit tag.
    Provider: string
    /// Descriptor instance id (typically the connector's
    /// `DataSourceId`). Distinguishes multiple connections under the
    /// same provider.
    ConfigId: string
    /// Scope the descriptor lives under (team scope or `_platform`).
    ScopeId: string
    /// UTC instant at which the freshly-minted access token will be
    /// rejected by the upstream — the scheduler uses this to compute
    /// the next dispatch time.
    NewExpiry: DateTime
    /// Attempt number that succeeded (1-indexed). > 1 indicates a
    /// `TransientError` recovered on retry.
    Attempt: int
    /// Wall-clock duration of the attempt that succeeded, in
    /// milliseconds. Mirrors `JobRun.DurationMs` precision; the
    /// `toolup.oauth.refresh.latency_ms` histogram is derived from
    /// this field.
    ElapsedMs: int64
}

/// Background refresh attempt failed transiently — recoverable.
/// Emitted per *attempt* (not per descriptor); a refresh that
/// recovers on attempt 3 emits two `OAuthTokenRefreshFailed` rows
/// (attempts 1 + 2) and one `OAuthTokenRefreshed` (attempt 3).
/// Powers the `toolup.oauth.refresh.failed_total` metric tagged by
/// `Provider`.
type OAuthTokenRefreshFailedPayload = {
    Provider: string
    ConfigId: string
    ScopeId: string
    /// Attempt number that failed (1-indexed).
    Attempt: int
    /// Free-form reason as reported by the underlying
    /// `OAuthRefreshResult.TransientError` payload. Never embeds
    /// secret material.
    Reason: string
}

/// Upstream provider rejected the refresh token (`invalid_grant`
/// or equivalent) during a background refresh. Terminal —
/// `CredentialStatus` flips to `NeedsReauthorization` and the admin
/// UI surfaces a "Reconnect required" banner. Distinct from the
/// per-call `OAuthRefreshFailed` (which is emitted by
/// `IOAuthCredentialFlow.RefreshAccessToken` synchronously from a
/// data-fetch path).
type OAuthRefreshTokenInvalidatedPayload = {
    Provider: string
    ConfigId: string
    ScopeId: string
    /// Provider's own diagnostic if available, or the substrate's
    /// classification reason. Verbatim — operators understand
    /// provider error codes faster than translated text.
    Reason: string
}

/// Background refresh exhausted `JobRetryPolicy.MaxAttempts`
/// consecutive failures. Terminal — no further dispatches; the
/// connector's credential status remains `Connected` (the refresh
/// token is still valid; the substrate just can't reach the
/// upstream) but every cached access token will expire and the
/// connector's data-fetch path falls back to its synchronous
/// `RefreshAccessToken` until the operator investigates.
type OAuthRefreshDeadLetteredPayload = {
    Provider: string
    ConfigId: string
    ScopeId: string
    /// Number of attempts before dead-lettering (matches
    /// `JobRetryPolicy.MaxAttempts` at the time of policy evaluation).
    Attempts: int
    /// Final reason from the last `OAuthRefreshResult.TransientError`
    /// or `PermanentError` payload.
    FinalReason: string
}

// ─── Platform Admin role audit payloads ───────────────────────────────

/// `PlatformAdmin` role assigned to a user. Emitted by
/// `IPlatformAdminStore.AssignPlatformAdmin` on success and by the
/// SDK's bootstrap path when `TOOLUP_INITIAL_PLATFORM_ADMIN` seeds
/// the first admin (in which case `Actor = "_bootstrap"`). Recorded
/// under `_platform.audit` with `ScopeId = "_platform"` — Platform
/// Admin role is deployment-wide, not team-scoped.
type PlatformAdminAssignedPayload = {
    /// User who triggered the assignment. `"_bootstrap"` for the
    /// env-var-seeded initial admin; an existing Platform Admin's
    /// userId for subsequent assignments via the API.
    Actor: string
    /// User who received the role.
    TargetUserId: string
}

/// `PlatformAdmin` role revoked from a user. Emitted by
/// `IPlatformAdminStore.RevokePlatformAdmin` on success. Always has a
/// real actor — there's no bootstrap revocation path (the bootstrap
/// only seeds, never removes).
type PlatformAdminRevokedPayload = {
    /// User who triggered the revocation. Must be an existing Platform
    /// Admin (gated by `canModifyPlatformConfig`).
    Actor: string
    /// User whose role was revoked.
    TargetUserId: string
}

/// Platform Knowledge Base document uploaded. Emitted by
/// `IPlatformKnowledgeApi.UploadPlatformDocument` on success. Records
/// the cardinality (size) and identity (id + file name) of the upload
/// without persisting the document body in the audit trail. Recorded
/// under `_platform` scope — Platform KB content is deployment-wide.
type PlatformDocumentUploadedPayload = {
    /// Actor (Platform Admin) who uploaded the document. Read from the
    /// caller's `AccessContext.UserId`; gated by
    /// `canModifyPlatformConfig` server-side.
    Actor: string
    /// Document id assigned by the upload handler. Stable identifier
    /// the operator can cross-reference against the Platform KB blob
    /// at `_platform/knowledge/{DocumentId}/{FileName}`.
    DocumentId: string
    /// Original file name. Surfaced for audit readability — operators
    /// reading the trail recognise file names faster than UUIDs.
    FileName: string
    /// File size in bytes. Cardinality only; the body itself does not
    /// travel through the audit trail.
    SizeBytes: int64
}

/// Platform Knowledge Base document deleted. Emitted by
/// `IPlatformKnowledgeApi.DeletePlatformDocument` on successful
/// deletion (the underlying blob + index entry + vector chunks are
/// removed). Idempotent deletes (the document didn't exist) suppress
/// the audit emission so the trail reflects only material state
/// changes.
type PlatformDocumentDeletedPayload = {
    /// Actor (Platform Admin) who triggered the deletion.
    Actor: string
    /// Document id of the deleted entry. The blob, index entry, and
    /// vector chunks are gone after this event — the id is preserved
    /// in the audit trail so historical reads can identify what was
    /// removed.
    DocumentId: string
    /// File name at the time of deletion. Same audit-readability
    /// rationale as `PlatformDocumentUploadedPayload.FileName`.
    FileName: string
}

// ─── Share-token audit payloads ───────────────────────────────────────

/// `IShareTokenStore.Issue` succeeded. `UserId` is the issuer (the
/// caller's `AccessContext.UserId` at issue time). `AttributedHandle`
/// may be an email or a hashed panel id depending on the issuer's
/// distribution choice — the audit echoes whatever the issuer
/// supplied so forensics can reconstruct the recipient mapping.
type ShareTokenIssuedPayload = {
    UserId: string
    TokenId: string
    ResourceKind: string
    ResourceId: string
    AttributedHandle: string option
    ExpiresAt: System.DateTimeOffset
}

/// `IShareTokenStore.MarkUsed` succeeded. No `UserId` — consumers
/// are anonymous by design. `AttributedHandle` lets the audit trail
/// correlate the consumed token back to the handle the issuer
/// recorded on `Issue` (one consumed-by-handle row per use).
type ShareTokenUsedPayload = {
    TokenId: string
    ResourceKind: string
    ResourceId: string
    AttributedHandle: string option
}

/// `IShareTokenStore.Revoke` succeeded. `UserId` is the actor (admin
/// or the issuer's automation). Subsequent `Validate` calls return
/// `Error RevokedToken`.
type ShareTokenRevokedPayload = {
    UserId: string
    TokenId: string
    ResourceKind: string
    ResourceId: string
}

/// Phase 6j.D — the AI fast-path beacon (or the equivalent
/// `SubmitMessage`) was rejected by the ownership gate. Emitted when
/// the first persisted message of a shared-container conversation
/// (`team-{teamId}`) records a `CreatedBy` that does not match the
/// caller's `AccessContext.UserId`. Surfaces cross-user history-
/// forgery / prompt-injection attempts before the synthetic turns
/// reach the provider-history blob the LLM reads next.
///
/// `Caller` is the `UserId` that attempted the append; `Owner` is the
/// `CreatedBy` recorded on the first persisted message. `Surface`
/// distinguishes which entry point fired the gate (`"beacon"` for the
/// fast-path POST, `"submit"` for `IAIAssistantApi.SubmitMessage`),
/// so forensics can tell whether the cross-user write came in via the
/// fire-and-forget beacon or the full agent-loop path.
///
/// PII-free: ids only — neither the instruction nor the conversation
/// content travels through the audit trail. Conversation owner is
/// already recorded by the deployment under the persisted blob; the
/// audit row exists so cross-user attempts are visible regardless of
/// blob-storage retention.
type BeaconRejectedPayload = {
    /// Conversation id whose append was refused. The blob (if any
    /// existed) is unchanged — the gate fires before the save.
    ConversationId: Guid
    /// The caller's `AccessContext.UserId` at the moment of refusal.
    /// Empty string when no identity was resolved (the handler also
    /// refuses unauthenticated callers).
    Caller: string
    /// The owner recorded on the first persisted message
    /// (`existing[0].CreatedBy`). Empty when the rejection was for a
    /// reason other than ownership mismatch (defensive — the field
    /// is shaped so a future use of `BeaconRejected` for non-ownership
    /// reasons doesn't break the wire shape).
    Owner: string
    /// `"beacon"` for the fast-path POST, `"submit"` for
    /// `IAIAssistantApi.SubmitMessage`. Pin-points the surface so
    /// operator triage can correlate against per-surface metrics.
    Surface: string
}

/// An AI conversation was exported (markdown / JSON
/// download) from the chat side panel. Metadata only by design: the
/// payload never carries conversation content or tool payloads (which
/// may contain PII), only *which* conversation, *whether* the user
/// opted into tool-detail inclusion, and *who* exported it — enough
/// for an admin to detect cross-team export patterns without the audit
/// trail itself becoming a PII sink.
type ConversationExportPayload = {
    ConversationId: string
    /// `true` when the user ticked "Include tool details" (the export
    /// file then contains raw `ToolCalls`); `false` for the sanitised
    /// default (tool calls stripped from the download).
    IncludeToolDetails: bool
    ExportedBy: string
}

// ─── Phase 53 — `IConversationStore` lifecycle audit payloads ───
//
// Five payloads cover the conversation substrate's lifecycle, emitted
// under reserved `SourceModule = "_platform.conversations"`. Bodies
// are deliberately metadata-only: digests + counts + ids, never
// conversation content or tool payloads (PII protection — Phase 6h.A
// owns content-redaction; this audit family piggy-backs on the
// substrate's own digests + counts so the audit trail itself never
// becomes a PII sink). Admin queries filter on `SourceModule`
// + `EventType` for per-event-kind rollups.

module ConversationsSourceModule =
    /// Reserved `SourceModule` for `IConversationStore` audit events.
    /// Filter `IEventStore.ReadBySource` on this constant for the
    /// per-conversation audit trail.
    [<Literal>]
    let value = "_platform.conversations"

/// `BeginConversation` happened. Captures the start-time metadata
/// frozen on the `Conversation` record — `Provider` + `ModelName` +
/// `SystemPromptDigest` + `SdkVersion` — so the audit row alone is
/// enough to reconstruct "which model under which prompt" without
/// reading the conversation blob.
type ConversationStartedPayload = {
    ConversationId: string
    UserId: string
    ScopeId: string
    Provider: string
    ModelName: string
    /// SHA-256 hex of the resolved system prompt.
    SystemPromptDigest: string
    /// SDK label at start (`AssemblyInformationalVersion` or
    /// operator-supplied).
    SdkVersion: string
}

/// One turn was appended. Payload carries digests + token counts —
/// no content. Aggregated rollups (per-conversation token spend,
/// turn count, average turn latency) reduce from this stream.
type ConversationTurnAppendedPayload = {
    ConversationId: string
    TurnId: string
    /// `"user"` / `"assistant"` / `"system"` — wire-form role.
    Role: string
    /// SHA-256 hex of the canonical JSON of the turn's `Content`.
    /// Lets compliance audits assert "this turn was not modified
    /// after-the-fact" without re-reading turn blobs.
    ContentDigest: string
    TokensIn: int option
    TokensOut: int option
}

/// The conversation reached a terminal state (`Completed` / `Errored
/// reason` / `Cancelled`). `FinalStatus` is the case name; `TurnCount`
/// is the total turns appended (for cheap usage rollups).
type ConversationCompletedPayload = {
    ConversationId: string
    /// `"Completed"` / `"Errored"` / `"Cancelled"` — DU case name.
    FinalStatus: string
    /// Free-form detail from `ConversationStatus.Errored of reason`,
    /// or empty for non-error terminal states.
    ErrorReason: string
    TurnCount: int
}

/// `IErasureHandler.Erase` for the `ConversationEraseHandler` ran.
/// Records the policy applied + the count of conversations affected
/// (matched by `CreatedBy = subjectUserId`). Lets the DSR
/// audit trail correlate the conversation-store contribution with
/// the broader run.
type ConversationErasedPayload = {
    /// The DSR subject — the user whose conversations were erased.
    SubjectUserId: string
    /// `"HardDelete"` / `"Tombstone"` / `"RetainPerCompliance"`.
    Policy: string
    /// Total conversations matched. The per-conversation `Id`s are
    /// NOT recorded in the audit row — knowing "user X had Y
    /// conversations" is itself a privacy disclosure under some
    /// regimes. Admins can re-query the store with the same subject
    /// for the per-id list if needed.
    ConversationCount: int
}

/// `ConversationReplay.replay` ran. Links the original to its replay
/// via the two `ConversationId`s + records the operator-supplied
/// override labels (per `ConversationReplayOptions`). `Delta` is
/// the human-readable comparison summary from `ConversationReplayResult.Delta`.
type ConversationReplayedPayload = {
    OriginalConversationId: string
    ReplayConversationId: string
    /// SHA-256 hex of the override system prompt, if supplied;
    /// `None` when the replay re-used the original prompt.
    NewSystemPromptDigest: string option
    /// Provider label override, if supplied; `None` when the replay
    /// re-used the original provider.
    NewProvider: string option
    /// Free-form SDK label the operator stamped on the replay;
    /// `None` when the replay re-used the original SDK version.
    SdkAnnotation: string option
    /// Operator-readable summary from `ConversationReplayResult.Delta`.
    /// Today: turn-count + per-turn token deltas. Do not machine-parse.
    Delta: string
}

/// One field-level difference between the previous startup's
/// snapshotted `ServerConfig` and this startup's resolved value.
/// `Path` is a dotted JSON-path (`AuditLog`, `RateLimit.RequestsPerWindow`,
/// `SecurityHeaders["X-Frame-Options"]`); `From` / `To` are the
/// serialised values, secrets-redacted via the same allowlist the
/// snapshot uses (`<redacted:length=N>`). Both sides may be absent
/// — `From = None` denotes a newly-introduced key (companion added,
/// new map entry); `To = None` denotes a removed key (companion
/// dropped, map entry deleted). At least one of `From` / `To` is
/// always populated.
type ConfigDriftChange = {
    Path: string
    From: string option
    To: string option
}

/// Phase 9q — startup-time `ServerConfig` drift detection.
/// Emitted by `ConfigDriftDetector` after `compose` resolves the
/// effective config and finds the persisted previous snapshot
/// (`_platform/_deploy/last-config.json`) differs. Recorded under
/// `_platform` scope — config drift is a deployment-wide signal,
/// not tenant-scoped — and source-module-labelled
/// `_platform.audit`. The event is pure observation: no abort, no
/// rollback. Operators triage off the audit trail.
///
/// Two top-level drift classes are surfaced:
///   * `Changes`: per-field config diff (`ServerConfig` shape).
///   * `CompanionSetFrom` / `CompanionSetTo`: SHA-256 hash of the
///     active companion set (which SDK assemblies were loaded —
///     `.Server.props` source-injection + `<PackageReference>`
///     companions both surface). A different hash with no
///     accompanying `Changes` row means the companion lineup
///     changed (added / removed / version-bumped) without altering
///     a `ServerConfig` knob.
///
/// Timestamps / build commit are deliberately excluded from the
/// diffed snapshot — they change on every restart and would drown
/// the signal. The detector records `SnapshotTakenAt` separately
/// so the audit trail still timestamps the comparison.
type ConfigDriftPayload = {
    /// Per-field diff between the previous and current resolved
    /// `ServerConfig`. Empty when the only change is the companion
    /// set (see `CompanionSetFrom` / `CompanionSetTo`).
    Changes: ConfigDriftChange list
    /// SHA-256 (lowercase hex) of the previous startup's companion
    /// set. `None` when no prior snapshot existed.
    CompanionSetFrom: string option
    /// SHA-256 (lowercase hex) of this startup's companion set.
    CompanionSetTo: string
    /// Server wall-clock at the moment the comparison ran.
    /// Distinct from the audit event's own `OccurredAt` so
    /// downstream queries can correlate the snapshot-capture time
    /// with the event-write time across audit-replication lag.
    SnapshotTakenAt: DateTime
}

/// Phase 9n — operator (or automated tooling) downloaded the
/// `/dev/bundle` diagnostic-support archive. Recorded under
/// `_platform` scope with reserved `SourceModule =
/// "_platform.diagnostics"`. The bundle download is a privileged
/// action — it ships every dev-inspect section + the audit tail +
/// the resolved (redacted) `ServerConfig`. Operators reading this
/// trail can see when a support bundle was extracted from the
/// deployment without inspecting the underlying webserver log.
type DiagnosticBundleAccessedPayload = {
    /// Caller's resolved userId; falls back to the SDK's anonymous
    /// sentinel when no identity was resolved (the endpoint stays
    /// usable while operators are diagnosing auth failures, mirroring
    /// `/dev/inspect`'s posture).
    UserId: string
    /// Caller's resolved `StorageScope.ScopeId` when scope resolution
    /// succeeded; `None` when no scope resolved (the bundle still
    /// produces, but the audit-tail section narrows to `_platform`
    /// scope only — see the bundle's `manifest.json`).
    ScopeId: string option
    /// Size of the produced tar in bytes after any truncation. A
    /// downstream consumer correlates a smaller-than-expected bundle
    /// with the 50 MB cap rather than a transmission failure.
    BundleSizeBytes: int64
    /// `true` when the 50 MB cap forced audit-tail or service-list
    /// truncation during this bundle's assembly. The bundle's
    /// `manifest.json` records the per-section truncation detail.
    Truncated: bool
}

/// Phase 9v — outbound rate-limit wait exceeded
/// `ServerConfig.SlowRateLimitThreshold`. Emitted by
/// `InProcessRateLimiter` after a `Wait` admitted with a `DelayedBy`
/// outcome whose duration crossed the threshold (default 5 s). Sub-
/// threshold waits are suppressed — the steady-state pacing emissions
/// would drown the audit trail; only material stalls reach durable
/// storage. PII-free: `Provider` + `SubKey` are SDK-controlled labels;
/// `WaitedMs` is cardinality only.
type RateLimitWaitedPayload = {
    /// Scope the throttled caller was operating in. Recorded under the
    /// same scope so per-tenant audit queries surface their own slow
    /// outbound calls.
    ScopeId: string
    /// `RateLimitKey.Provider` — upstream label (`"strava"`, `"ga4"`,
    /// `"openai"`).
    Provider: string
    /// Optional sub-key partitioning a provider further (e.g.
    /// per-property quotas on GA4). `None` for descriptors without a
    /// sub-key.
    SubKey: string option
    /// Wall-clock time the caller was held inside `Wait` before
    /// admission. Integer milliseconds — sub-millisecond precision is
    /// not part of the contract.
    WaitedMs: int64
}

/// Phase 9v — outbound long-window quota exhausted. Emitted by
/// `InProcessRateLimiter` when a descriptor's `LongWindow` (typically
/// a daily ceiling) is hit and `Wait` returns `Refused`. Unlike
/// `RateLimitWaited`, this event is always recorded — refusals are
/// material (the upstream call did NOT happen) and operators need the
/// trail for incident triage.
type RateLimitRefusedPayload = {
    ScopeId: string
    Provider: string
    SubKey: string option
    /// Limiter-supplied refusal reason — typically describes the
    /// triggering long-window quota ("long-window quota exhausted
    /// (1000 in 1.00:00:00)").
    Reason: string
}

/// Phase 9h — data-subject-request lifecycle event. One payload shape
/// across every transition emitted by `DataSubjectRequestApiHandler`
/// (RequestStarted / PreviewCompleted / ErasureCompleted /
/// ErasureFailed / ExportCompleted). The transition discriminator
/// rides in `Kind` so admin queries can filter on "everything DSR" at
/// the wire `EventType` and still distinguish phases via the payload —
/// matches the orchestrator's `DsrAuditEvent` shape verbatim so the
/// composition root translates a `DsrAuditEvent` 1:1 into this record
/// without restructuring.
///
/// Recorded under the scope the admin was acting within (typically the
/// caller's team for `Team`/`MultiTeam`, the caller's user for
/// `Individual`/`AuthenticatedEphemeral`). Cross-scope erasure for one
/// subject across multiple tenants is a deployment-level operation —
/// the admin invokes the API once per scope and gets one trail row per
/// invocation.
type DataSubjectRequestAuditPayload = {
    /// Correlation id matching `DataSubjectRequest.Id`. Threads the
    /// preview / confirm pair plus every audit row for one logical
    /// request together.
    RequestId: string
    /// Transition kind as a string discriminator (`"RequestStarted"` /
    /// `"PreviewCompleted"` / `"ErasureCompleted"` / `"ErasureFailed"` /
    /// `"ExportCompleted"`). String rather than DU avoids dragging the
    /// Server-tier `DsrAuditEventKind` into Core; admin tooling can
    /// branch on string equality.
    Kind: string
    /// Subject of the request — the `SubjectUserId` whose records were
    /// (or would have been) affected.
    SubjectUserId: string
    /// Admin actor who initiated the request.
    Actor: string
    /// Free-text rationale carried from the originating request (ticket
    /// id, regulator inquiry reference, supporting documentation
    /// pointer). Compliance review often requires it; the trail is the
    /// canonical record.
    Reason: string
    /// Free-form transition-specific properties (per-handler counts at
    /// PreviewCompleted, segment counts at ExportCompleted, total
    /// affected at ErasureCompleted, etc.). Empty for RequestStarted.
    Properties: Map<string, string>
}

// ─── Phase 39 — ToolUp.AssetStore companion audit payloads ──────
//
// Image-asset lifecycle events. Source-module label
// `_platform.assets` (reserved). The IAuditLog default impl writes
// every audit event under `_platform.audit`; this constant exists
// for query-time filtering when an external sink (Splunk / Datadog)
// shards by source-module label.
//
// **Alt-text is deliberately excluded from these payloads** —
// treated as user content. The audit trail captures the *fact* of
// the upload (filename, mime, size, hash, profile), not the
// accessibility text the user typed.

module AssetSourceModule =
    /// Reserved `SourceModule` for asset-store lifecycle events.
    /// Forward-looking — current emission rides
    /// `AuditSourceModule.value` via `IAuditLog.Record`. External
    /// sinks filtering on this constant will see asset events when
    /// the IAuditLog impl gains per-event source-module routing.
    [<Literal>]
    let value = "_platform.assets"

/// Emitted by `IAssetStore.Upload` on success. PII-free —
/// `OriginalFilename` is the only user-supplied string and is the
/// uploader's own filename (the data already exists in their
/// scope; the audit echoes it for forensic linkage). Excludes
/// `AltText` and `Caption` deliberately.
type AssetUploadedPayload = {
    /// Actor who performed the upload. Resolved server-side from
    /// `AccessContext.UserId`.
    UserId: string
    /// Asset id minted by the store.
    AssetId: string
    /// SHA-256 of the original bytes (hex, lowercase). Two uploads
    /// of identical bytes share this — operators reconciling
    /// storage footprint with record count grep on this value.
    ContentHash: string
    /// User's original filename. Echoed verbatim from the upload.
    OriginalFilename: string
    /// MIME type sniffed at upload.
    MimeType: string
    /// Size of the original bytes.
    SizeBytes: int64
    /// Derivative profile pinned to the record.
    Profile: string
}

/// Emitted by `IAssetStore.Delete` on a record that existed.
/// Idempotent deletes of unknown ids do NOT emit (the delete
/// returned `Ok` after a no-op — there's no state change to record).
type AssetDeletedPayload = {
    UserId: string
    AssetId: string
    ContentHash: string
}

// ─── Phase 3d — team-invitation audit payloads ───────────────────────
//
// One payload per audit lifecycle event for the team-invitation
// substrate. All five emit under `team-{TeamId}` scope with reserved
// `SourceModule = "_platform.team_invites"` so admin queries filtering
// on the source module surface the invitation trail in isolation.

/// `ITeamInviteApi.IssueInvite` succeeded. Recorded under
/// `team-{TeamId}` scope with `SourceModule = "_platform.team_invites"`.
type TeamInviteIssuedPayload = {
    TeamId: string
    TokenId: string
    InviterUserId: string
    Role: TeamRole
    EmailHint: string option
    ExpiresAt: System.DateTime
    MaxUses: int
}

/// `ITeamInviteApi.AcceptInvite` succeeded — an authenticated visitor
/// was added to the team via a link-redemption flow.
type TeamInviteAcceptedPayload = {
    TeamId: string
    TokenId: string
    InviteeUserId: string
    InviterUserId: string
    Role: TeamRole
}

/// `ScopeResolutionMiddleware` consumed a `PendingInviteByEmail` row
/// on first sign-in matching the email claim — no link redemption was
/// involved. The pending entry is removed atomically with the
/// `AddMember` call.
type TeamInviteAcceptedFromPendingPayload = {
    TeamId: string
    InviteeUserId: string
    InviteeEmail: string
    InviterUserId: string
    Role: TeamRole
}

/// A pending-by-email invitation matched the signed-in user's email
/// claim but the subsequent `ITeamStore.AddMember` call failed (team
/// no longer exists, store glitch, etc.). Distinguishes "silent drop"
/// from "accepted" so an operator inspecting the audit trail can see
/// that the pending entry was consumed but the membership was not
/// applied — and follow up. The pending entry is consumed regardless
/// (single-shot semantics) so this event is the only signal something
/// went wrong.
type TeamInviteAcceptedFromPendingFailedPayload = {
    TeamId: string
    InviteeUserId: string
    InviteeEmail: string
    InviterUserId: string
    Role: TeamRole
    Reason: string
}

/// `ITeamInviteApi.RevokeInvite` succeeded. Subsequent acceptance
/// attempts against the same token return `RevokedToken`.
type TeamInviteRevokedPayload = {
    TeamId: string
    TokenId: string
    ActorUserId: string
}

/// `IShareTokenStore.MarkUsed` succeeded on a team-invite token —
/// emitted alongside `TeamInviteAccepted` for symmetry with the
/// existing `ShareTokenUsed` substrate event. The pair lets a
/// monitoring sink distinguish "invitee successfully joined the team"
/// (`TeamInviteAccepted`) from "the token's use-count was bumped"
/// (`TeamInviteRedeemed`).
type TeamInviteRedeemedPayload = {
    TeamId: string
    TokenId: string
    RemainingUses: int
}

/// Phase 66 Stream C.1 (continuation) — an `IAnonymousSessionMigrator`
/// ran on the first authenticated request following an anonymous
/// session in the same browser. Emitted by
/// `AnonymousSessionMigrationMiddleware` on `Ok`, `PartialFailure`, and
/// `InfrastructureFailed` (the benign `NotEligible` outcome is not
/// audited — it fires on every unwired / no-data deployment and would
/// drown the trail). `Outcome` is the machine-readable discriminator
/// (`"ok"` / `"partial_failure"` / `"infrastructure_failed"`); the
/// migrated-volume counts come from the `MigrationSummary` (zero for
/// the infrastructure-failure case where nothing landed). `FailedItems`
/// + `Error` carry the partial / infrastructure diagnostic so a
/// runbook can correlate a stuck migration without re-deriving from
/// logs. PII-free: `AnonymousSessionId` is an opaque session token and
/// `TargetUserId` matches the convention of the existing
/// `UserLoggedInPayload`. Reserved `SourceModule = "_platform.subject"`.
type AnonymousSessionMigratedPayload = {
    AnonymousSessionId: string
    TargetUserId: string
    Outcome: string
    ItemsMigrated: int
    BytesMigrated: int64
    Modules: string list
    FailedItems: int
    Error: string option
    OccurredAt: DateTimeOffset
}

/// Auth-observability phase A — `ScopeResolutionMiddleware` infrastructure
/// failure. Fires when the catch-all in `ScopeResolutionMiddleware` traps
/// an exception (DI resolution failure, cache failure, store throw) and
/// the request falls through to anonymous-subject behaviour. Pre-A1 the
/// catch was silent; operators had no signal to distinguish "no
/// credentials" from "resolver crashed."
///
/// Reserved `SourceModule = "_platform.auth"`. `ScopeId` for the
/// emission is the literal `"_platform"` (the scope is unresolvable —
/// that's precisely the failure we're recording).
type ScopeResolutionFailedPayload = {
    /// Request method (`GET` / `POST` / …) at the moment of failure.
    Method: string
    /// Request path (unparameterised — operators read this when triaging,
    /// PII risk is low for `/api/*` shapes).
    Path: string
    /// Concrete .NET exception type name (`NullReferenceException`,
    /// `SocketException`, etc.). Operators correlate spikes to infra.
    ExceptionKind: string
    /// Human-readable message from the exception. Bounded — middleware
    /// truncates to 512 chars to keep audit-row size predictable.
    Message: string
    /// Correlation id (read from `CallContext.correlationId()` if set,
    /// otherwise `None`). Stitches this event to the request log + the
    /// client-side trace.
    CorrelationId: string option
    OccurredAt: DateTimeOffset
}

/// Auth-observability phase A — `SurfaceEnforcementMiddleware` denial.
/// Fires on every `writeRejection` so operator dashboards see denial
/// rates (rate-limit on a scripted enumeration, recent surface-config
/// changes that flipped legit calls to denied). Pre-A2 these were
/// completely silent — the middleware wrote a 401/403 JSON body but
/// emitted no audit.
///
/// Reserved `SourceModule = "_platform.auth"`. `ScopeId` is `_platform`
/// (denials are deployment-wide observability, not tenant-scoped).
type SurfaceDeniedPayload = {
    /// Request method.
    Method: string
    /// Request path. **Unparameterised path** today; future evolution
    /// will template (`/api/teams/{id}/members`) once the route registry
    /// exposes the template. For now operators query on path prefix.
    Path: string
    /// `Subject` kind at denial time (`anonymous` / `user` / `team` /
    /// `claim`). The full subject is available in `SubjectId` when not
    /// anonymous.
    SubjectKind: string
    /// Stable identifier of the denied subject. `None` for anonymous.
    SubjectId: string option
    /// Machine-readable denial code (`authentication_required` /
    /// `team_required` / `claim_bearer_not_admitted` / etc.) — matches
    /// the `error` field in the response body.
    DenialCode: string
    /// Optional caller-visible hint that accompanies the denial code
    /// (`select_team` etc.).
    Hint: string option
    /// Correlation id (set by `installRequestSeam` client-side or by the
    /// dispatcher's per-request generator).
    CorrelationId: string option
    OccurredAt: DateTimeOffset
}

// ─── Phase 30a — signed module artefact audit payloads ───────────────
//
// Emitted by `IArtifactSigner.Sign` (hub-side) and
// `IArtifactVerifier.Verify` (edge-side). Reserved `SourceModule =
// "_platform.artefacts"` (see `ArtifactsSourceModule.value` in
// `Shared/Types/ArtifactTypes.fs`). PII-free — payloads carry the
// publisher key id (NEVER the private key bytes), the module id +
// artefact version (manifest-supplied), and a reason on `Rejected`.

/// `IArtifactSigner.Sign` succeeded. Records who signed (the actor that
/// invoked the signer), which publisher key was used (id only — never
/// the private key), and the manifest's module / version identity.
type ArtifactSignedPayload = {
    /// Actor who invoked the signer (typically `"_hub"` for an
    /// automated publish pipeline; the authenticated user's id for
    /// operator-initiated signs).
    Actor: string
    /// `PublisherKeyId.value` of the key used to sign. The signer NEVER
    /// records the private key bytes in the audit trail.
    PublisherKeyId: string
    /// `ArtifactManifest.ModuleId` — the module identity the signed
    /// artefact installs as.
    ModuleId: string
    /// `ArtifactManifest.Version` — SemVer string.
    ArtifactVersion: string
}

/// `IArtifactVerifier.Verify` returned `ArtifactValidation.Ok` (signature
/// valid + publisher key trusted at the edge).
type ArtifactVerifiedPayload = {
    /// `PublisherKeyId.value` of the publisher whose signature was
    /// validated.
    PublisherKeyId: string
    /// `ArtifactManifest.ModuleId`.
    ModuleId: string
    /// `ArtifactManifest.Version`.
    ArtifactVersion: string
}

/// `IArtifactVerifier.Verify` returned `ArtifactValidation.Error reason`.
/// Recorded as a separate case from `ArtifactVerified` so operator
/// dashboards can target refusal rates without scanning every verify
/// row.
type ArtifactRejectedPayload = {
    /// `PublisherKeyId.value` from the manifest. `None` when the
    /// rejection happened before the key id could be parsed (corrupt
    /// manifest, decode failure).
    PublisherKeyId: string option
    /// `ArtifactManifest.ModuleId` when the manifest decoded; empty
    /// string when the rejection happened before the manifest could be
    /// read.
    ModuleId: string
    /// `ArtifactManifest.Version` when the manifest decoded; empty
    /// string otherwise.
    ArtifactVersion: string
    /// Operator-readable refusal reason. Mirrors the
    /// `ArtifactValidation.Error reason` string verbatim:
    /// `"untrusted publisher"`, `"signature mismatch"`,
    /// `"manifest hash mismatch"`, or a sink-specific message.
    Reason: string
}

/// Phase 30d — a `ModulePermission.SchemaOnly` partner-sandbox caller
/// invoked `IDataCatalog.GetSyntheticSample` successfully. Recorded
/// under `_platform.audit` with `SourceModule` derived per call site.
/// Volume note: high-cardinality for chatty partner integrations
/// (each iteration loop call emits a row), so the payload is
/// metadata-only — no synthetic bytes travel through the trail. The
/// `Seed` field IS recorded so a deployment can prove an
/// exfiltration-style "partner generated thousands of differing
/// seeds" pattern after the fact.
type SyntheticSampleGeneratedPayload = {
    /// Acting `AccessContext.UserId` at the moment of the call. Pinned
    /// to a real principal — anonymous callers should never reach this
    /// path (the gating layer refuses them earlier).
    UserId: string
    /// `DataTypeId` the sample was generated for.
    TypeId: string
    /// Number of rows requested. May exceed the configured per-partner
    /// cap; in that case the actual emitted row count is
    /// `min(requested, cap)` (see `EmittedCount`).
    RequestedCount: int
    /// Actual rows emitted after the per-partner cap clamped the
    /// request. Always `<= RequestedCount`.
    EmittedCount: int
    /// Seed passed by the caller. Recorded verbatim so forensic
    /// review can detect "thousands of differing seeds in one
    /// session" exfiltration patterns.
    Seed: int
    /// Per-partner cap that was applied (from
    /// `_platform.notification_prefs.schemaOnly.maxSampleRows` or the
    /// SDK default when unset).
    AppliedMaxRows: int
}

/// Phase 30d — a `ModulePermission.SchemaOnly` caller attempted to
/// access a real-row API path; the substrate refused before any real
/// data was read. Emitted by every shield site so a deployment can
/// dashboard "refusal rate by partner" as a leading-indicator metric
/// for credential leak / misconfiguration / hostile activity. Distinct
/// from `SurfaceDenied` (Phase 66 `SurfaceEnforcementMiddleware` —
/// surface-level deny) because the SchemaOnly refusal happens at the
/// substrate / handler layer, not at the route surface.
type SchemaOnlyAccessAttemptedPayload = {
    /// Acting `AccessContext.UserId`. Required for forensics —
    /// anonymous callers do not reach this path (the surface refuses
    /// them earlier).
    UserId: string
    /// Module name the caller was attempting to read. Mirrors the
    /// `IPermissionStore` key shape so admin queries can correlate
    /// refusals with team-permission grants.
    ModuleName: string
    /// Short stable label for the substrate path that fired the
    /// refusal — `"IDataObjectStore.Get"`, `"IDataObjectStore.ListObjects"`,
    /// `"IDataCatalog.ListObjects"`, `"IFileManagementApi.GetFileContent"`,
    /// etc. Operator dashboards group refusals by call site so a
    /// regressed shield is visible immediately.
    AttemptedPath: string
    /// Best-effort identifier for the requested resource (object id,
    /// file name, scope id, etc.). Empty string when the refusal
    /// happened before any identifier could be resolved.
    AttemptedResource: string
}

/// Phase 18 — a typed inter-platform peer contract call resolved on the
/// receiver (the host dispatched it to a terminal outcome). Emitted once
/// per inbound call by the peer host's contract handler. Reserved
/// `SourceModule = "_platform.peer"`. PII-free: identities are peer ids
/// plus a correlation id — never end-user payload.
type PeerCallCompletedPayload = {
    /// The hosted `contractId` the call targeted.
    ContractId: string
    /// The contract method name dispatched.
    MethodName: string
    /// `PeerId` of the validated *calling* peer, taken from the
    /// authenticated `PeerPrincipal` — never the self-asserted wire body.
    CallerPeerId: string
    /// The cascade-wide correlation id shared across every hop, so a
    /// federated call is reconstructable end to end from audit alone.
    RootRequestId: string
    /// `true` when dispatch returned `Ok`; `false` on a `PeerError`.
    Succeeded: bool
    /// Short outcome label: `"ok"` on success, else the `PeerError` DU
    /// case name (e.g. `"PeerMethodNotFound"`). Operator dashboards group
    /// peer-call failures by this label without reading message detail.
    Outcome: string
    /// Wall-clock time the call resolved.
    OccurredAt: DateTimeOffset
}

/// SDK-standard audit event types. The DU case name is the wire-format
/// `EventType` discriminator string; payload records are JSON-serialised
/// into `ModuleEvent.Payload` via `FableConverters` (matches the
/// existing `WebhookApiHandler` / `KnowledgeBase` audit emission idiom).
type AuditEvent =
    | UserLoggedIn of UserLoggedInPayload
    | TeamCreated of TeamCreatedPayload
    | MemberAdded of MemberAddedPayload
    | MemberRemoved of MemberRemovedPayload
    | MemberRoleChanged of MemberRoleChangedPayload
    | FileUploaded of FileUploadedPayload
    | FileDeleted of FileDeletedPayload
    /// File re-processed via `IFileManagementApi.ReprocessFile`. The
    /// raw bytes are unchanged (no `FileUploaded` is emitted), but the
    /// derived `ProcessedFileEntry` summary has been rebuilt against
    /// the current `DataType` registry.
    | FileReprocessed of FileReprocessedPayload
    /// Owner / Admin invoked `IFileManagementApi.ResetDataStore` and
    /// every uploaded file in the scope was removed. Single event for
    /// the bulk operation; per-file `FileDeleted` is suppressed to
    /// avoid drowning the audit trail.
    | DataStoreReset of DataStoreResetPayload
    | AnalysisRun of AnalysisRunPayload
    | PermissionChanged of PermissionChangedPayload
    /// Successful out-of-band transactional delivery.
    | NotificationSent of NotificationSentPayload
    /// Permanent or retry-exhausted transactional failure.
    | NotificationDeliveryFailed of NotificationDeliveryFailedPayload
    /// Debounced probe state transition (3 consecutive
    /// observations of a new status). Emitted by `HealthStateTracker`
    /// when `ServerConfig.HealthStateTracking = true`.
    | HealthStateChanged of HealthStateChangedPayload
    /// Encryption key auto-created on first resolution by
    /// an SDK-managed resolver (`SingleKeyResolver` /
    /// `PerScopeKeyResolver`).
    | EncryptionKeyCreated of EncryptionKeyEventPayload
    /// Encryption key rotated. Reserved for the future
    /// `_platform/.../v2` rotation flow; not emitted by v1 resolvers.
    | EncryptionKeyRotated of EncryptionKeyEventPayload
    /// Encryption key destroyed via tenant-offboarding
    /// crypto-shred. Emitted by `PerScopeKeyResolver.DestroyKey`
    /// invoked through the admin endpoint. After this event, all
    /// blobs encrypted with the destroyed key are permanently
    /// undecryptable.
    | EncryptionKeyDestroyed of EncryptionKeyEventPayload
    /// Entity created (first version saved).
    | EntityCreated of EntityLifecycleEventPayload
    /// Entity updated (subsequent version saved).
    | EntityUpdated of EntityLifecycleEventPayload
    /// Entity deleted (head version removed; historical
    /// versions remain available via `GetVersion`).
    | EntityDeleted of EntityLifecycleEventPayload
    /// Forms submission committed (Submit). PII-free.
    | FormSubmitted of FormSubmittedPayload
    /// Forms submission edited in `Draft` state
    /// (UpdateDraft). PII-free.
    | FormSubmissionUpdated of FormSubmissionUpdatedPayload
    /// Forms workflow state transition applied. Recorded
    /// after the new state is persisted; the optional workflow
    /// action runs afterwards but does not affect this event.
    | WorkflowTransitioned of WorkflowTransitionedPayload
    /// Successful sink batch delivery. Emitted once per
    /// batch after `IAuditSink.Deliver` returns `Result.Ok`.
    | AuditSinkDelivered of AuditSinkDeliveredPayload
    /// Retryable sink failure. Emitted once per failed
    /// attempt before retry-budget exhaustion.
    | AuditSinkFailed of AuditSinkFailedPayload
    /// Terminal sink failure after retry exhaustion. Cursor
    /// advances past the failed batch; operators investigate.
    | AuditSinkDeadLettered of AuditSinkDeadLetteredPayload
    /// One or more events in a replication batch failed
    /// to decode (schema drift / corrupt payload). One row per batch.
    | AuditEventDecodeFailed of AuditEventDecodeFailedPayload
    /// Out-of-band notification dropped because the publishing
    /// scope's `_platform.notification_prefs` kill switch for the
    /// kind (Email / Sms / Push) is `false`. Without this event the
    /// drop is silent — an admin who thought they enabled email had
    /// no audit trail of the actual policy decision.
    /// `RecipientHash` is `SHA256(userId)[..8]` (PII-free; correlatable
    /// across events for the same recipient without leaking identity).
    | NotificationSilentlySkipped of NotificationSilentlySkippedPayload
    /// User completed an OAuth Authorization Code flow and
    /// the SDK persisted the resulting refresh token in `ISecretStore`.
    /// Source-module label `_platform.oauth`.
    | OAuthConnected of OAuthConnectedPayload
    /// User clicked Disconnect; SDK deleted the local
    /// refresh token (and best-effort revoked it upstream).
    | OAuthDisconnected of OAuthDisconnectedPayload
    /// `IOAuthCredentialFlow.RefreshAccessToken` failed
    /// because the upstream provider rejected the refresh token.
    /// `CredentialStatus` transitions to `NeedsReauthorization`.
    | OAuthRefreshFailed of OAuthRefreshFailedPayload
    /// Phase 10h — background refresh succeeded. Reserved
    /// `SourceModule = "_platform.oauth.refresh"`. Emitted by
    /// `OAuthRefreshJobHandler` after the substrate persists the
    /// new access token + expiry (and rotated refresh token, when
    /// the upstream rotated it).
    | OAuthTokenRefreshed of OAuthTokenRefreshedPayload
    /// Phase 10h — single background refresh attempt failed
    /// transiently. Reserved `SourceModule = "_platform.oauth.refresh"`.
    /// Emitted per attempt; a refresh that recovers on a later
    /// attempt produces one of these per failed attempt plus a
    /// terminal `OAuthTokenRefreshed`.
    | OAuthTokenRefreshFailed of OAuthTokenRefreshFailedPayload
    /// Phase 10h — upstream rejected the refresh token during a
    /// background refresh (`invalid_grant` or equivalent). Terminal
    /// — `CredentialStatus` flips to `NeedsReauthorization`.
    /// Reserved `SourceModule = "_platform.oauth.refresh"`.
    | OAuthRefreshTokenInvalidated of OAuthRefreshTokenInvalidatedPayload
    /// Phase 10h — background refresh exhausted
    /// `JobRetryPolicy.MaxAttempts`. Terminal — no further dispatches;
    /// data-fetch fallback to synchronous `RefreshAccessToken` until
    /// operator investigates. Reserved
    /// `SourceModule = "_platform.oauth.refresh"`.
    | OAuthRefreshDeadLettered of OAuthRefreshDeadLetteredPayload
    /// `PlatformAdmin` role assigned. Emitted by
    /// `IPlatformAdminStore.AssignPlatformAdmin` and by the bootstrap
    /// path when `TOOLUP_INITIAL_PLATFORM_ADMIN` seeds the initial
    /// admin (`Actor = "_bootstrap"`). Recorded under `_platform`
    /// scope — role is deployment-wide.
    | PlatformAdminAssigned of PlatformAdminAssignedPayload
    /// `PlatformAdmin` role revoked. Emitted by
    /// `IPlatformAdminStore.RevokePlatformAdmin`. No bootstrap variant.
    | PlatformAdminRevoked of PlatformAdminRevokedPayload
    /// Platform Knowledge Base document uploaded. Emitted
    /// by `IPlatformKnowledgeApi.UploadPlatformDocument` on success.
    | PlatformDocumentUploaded of PlatformDocumentUploadedPayload
    /// Platform Knowledge Base document deleted. Emitted
    /// by `IPlatformKnowledgeApi.DeletePlatformDocument` on successful
    /// removal (idempotent deletes of unknown ids suppress the event).
    | PlatformDocumentDeleted of PlatformDocumentDeletedPayload
    /// `IShareTokenStore.Issue` succeeded. Reserved
    /// `SourceModule = "_platform.share_tokens"`. `AttributedHandle`
    /// may carry PII when the issuer chose an email as the handle —
    /// the data already lives in the issuer's scope, so the audit
    /// payload echoes it for forensic completeness.
    | ShareTokenIssued of ShareTokenIssuedPayload
    /// `IShareTokenStore.MarkUsed` succeeded. No `UserId`
    /// — consumers are anonymous by design (the token IS the
    /// authentication).
    | ShareTokenUsed of ShareTokenUsedPayload
    /// `IShareTokenStore.Revoke` succeeded. `UserId` is
    /// the actor; subsequent `Validate` calls reject the token with
    /// `RevokedToken`.
    | ShareTokenRevoked of ShareTokenRevokedPayload
    /// An AI conversation was exported from the chat side
    /// panel. Metadata-only payload (no conversation content / tool
    /// payloads) so the audit trail can record export activity without
    /// itself leaking PII.
    | ConversationExported of ConversationExportPayload
    /// Phase 6j.D — the fast-path beacon or `SubmitMessage` was refused
    /// by the conversation-ownership gate (caller's `UserId` did not
    /// match the first persisted message's `CreatedBy`). Distinct from
    /// the `_platform.ai.fastpath` / `FastPathRejected` event the
    /// beacon handler already emits for malformed / oversized / scope-
    /// resolution-missing inputs — this case is specifically for cross-
    /// user history-forgery attempts in shared-container modes.
    | BeaconRejected of BeaconRejectedPayload
    /// Phase 9q — resolved `ServerConfig` differs from the
    /// previous startup's persisted snapshot, or the active companion
    /// set hash changed. Pure observation — `ConfigDriftDetector` emits
    /// one row per restart that finds drift, then proceeds. No abort,
    /// no rollback. Recorded under `_platform` scope; secrets in the
    /// `Changes` payload are pre-redacted to `<redacted:length=N>`
    /// before emission.
    | ConfigDrift of ConfigDriftPayload
    /// Phase 9n — operator (or automated tooling) downloaded the
    /// `/dev/bundle` diagnostic-support archive. Recorded under
    /// `_platform` scope with reserved `SourceModule =
    /// "_platform.diagnostics"`. The download is itself a privileged
    /// action; the audit trail captures who pulled the bundle, when,
    /// and whether the 50 MB cap forced truncation.
    | DiagnosticBundleAccessed of DiagnosticBundleAccessedPayload
    /// Phase 9v — outbound rate-limit wait crossed
    /// `ServerConfig.SlowRateLimitThreshold`. Emitted by
    /// `InProcessRateLimiter` after a `Wait` admitted with a
    /// `DelayedBy` outcome at or above the threshold (default 5 s).
    /// Sub-threshold waits are deliberately silent to keep the audit
    /// trail focused on material stalls.
    | RateLimitWaited of RateLimitWaitedPayload
    /// Phase 9v — outbound long-window quota exhausted. Emitted by
    /// `InProcessRateLimiter` when a descriptor's `LongWindow` ceiling
    /// is hit and `Wait` returns `Refused`. Always recorded — refusals
    /// are material state (the upstream call did NOT happen).
    | RateLimitRefused of RateLimitRefusedPayload
    /// Phase 9h — data-subject-request lifecycle event. One DU case
    /// covers every transition emitted by `DataSubjectRequestApiHandler`
    /// (RequestStarted / PreviewCompleted / ErasureCompleted /
    /// ErasureFailed / ExportCompleted) — the specific transition rides
    /// in the payload's `Kind` field. Admin queries filter on the wire
    /// `EventType` for "every DSR audit row" and branch on `Kind`
    /// inside the payload for per-phase rendering. Recorded under the
    /// scope the admin was acting within; cross-scope erasure for one
    /// subject is a deployment-level operation invoked per scope.
    | DataSubjectRequest of DataSubjectRequestAuditPayload
    /// Phase 53 — `IConversationStore.BeginConversation` happened.
    /// Recorded under `SourceModule = ConversationsSourceModule.value`.
    | ConversationStarted of ConversationStartedPayload
    /// Phase 53 — one `ConversationTurn` appended via
    /// `IConversationStore.AppendTurn`. Per-turn audit — high-volume
    /// for chatty conversations, so payload is digest-only.
    | ConversationTurnAppended of ConversationTurnAppendedPayload
    /// Phase 53 — conversation reached `Completed` / `Errored` /
    /// `Cancelled`.
    | ConversationCompleted of ConversationCompletedPayload
    /// Phase 53 — `ConversationEraseHandler` ran for a DSR subject.
    /// Emitted in addition to the broader `DataSubjectRequest` audit
    /// row so per-store contributions are visible in conversation-store
    /// admin views without the broader run context.
    | ConversationErased of ConversationErasedPayload
    /// Phase 53 — `ConversationReplay.replay` produced a new
    /// `Conversation`. Links the original + replay ids; records the
    /// operator's chosen override labels.
    | ConversationReplayed of ConversationReplayedPayload
    /// Phase 39 — `IAssetStore.Upload` succeeded. PII-free; alt-text
    /// and caption are deliberately excluded (treated as user
    /// content). Reserved `SourceModule = "_platform.assets"`.
    | AssetUploaded of AssetUploadedPayload
    /// Phase 39 — `IAssetStore.Delete` removed an existing record.
    /// Idempotent deletes of unknown ids do not emit. Reserved
    /// `SourceModule = "_platform.assets"`.
    | AssetDeleted of AssetDeletedPayload
    /// Phase 5f — a `TeamApi.CreateTeam` request was refused because
    /// `TeamCreationPolicy = PlatformAdminOnly` and the caller does
    /// not hold `PlatformRole.PlatformAdmin`. Emitted by the
    /// `teamApiHandler.CreateTeam` gate before any team is minted.
    | TeamCreationDenied of TeamCreationDeniedPayload
    /// Phase 3d — `ITeamInviteApi.IssueInvite` succeeded. Reserved
    /// `SourceModule = "_platform.team_invites"`. Recorded under
    /// `team-{TeamId}` scope.
    | TeamInviteIssued of TeamInviteIssuedPayload
    /// Phase 3d — `ITeamInviteApi.AcceptInvite` succeeded. Recorded
    /// under `team-{TeamId}` scope alongside the per-use
    /// `TeamInviteRedeemed` event.
    | TeamInviteAccepted of TeamInviteAcceptedPayload
    /// Phase 3d — `ScopeResolutionMiddleware` consumed a pending-
    /// invite email blob entry on first sign-in matching the email
    /// claim. No token redemption was involved.
    | TeamInviteAcceptedFromPending of TeamInviteAcceptedFromPendingPayload
    /// Phase 3d — `ScopeResolutionMiddleware` matched a pending-invite
    /// email blob entry to a signed-in user but the follow-up
    /// `ITeamStore.AddMember` call failed. The pending entry is
    /// consumed regardless (single-shot semantics); this event is the
    /// audit trail of the silent drop so operators can investigate.
    | TeamInviteAcceptedFromPendingFailed of TeamInviteAcceptedFromPendingFailedPayload
    /// Phase 3d — `ITeamInviteApi.RevokeInvite` succeeded. The token
    /// remains visible to admin listings but rejects subsequent
    /// acceptance attempts.
    | TeamInviteRevoked of TeamInviteRevokedPayload
    /// Phase 3d — `IShareTokenStore.MarkUsed` succeeded on a team-
    /// invite token. Emitted alongside `TeamInviteAccepted` for
    /// substrate-level observability.
    | TeamInviteRedeemed of TeamInviteRedeemedPayload
    /// Phase 21d — workflow-action invocation outcome (succeeded /
    /// failed / skipped_replay / skipped_pending). Emitted by the
    /// `WorkflowEngine` for every action the ledger resolves, so
    /// operator triage can correlate metrics + audit + ledger rows
    /// without inferring from the metric tag alone.
    | WorkflowActionExecuted of WorkflowActionExecutedPayload
    /// Phase 59 — client recorded a consent decision via
    /// `IConsentProvider`; only emitted when
    /// `ServerConfig.ConsentAudit = EnabledConsentAudit`. Reserved
    /// `SourceModule = "_platform.consent"`.
    | ConsentRecorded of ConsentEvent
    /// Phase 60 — an `<AdSlot>` rendered + the AdSense bundle
    /// reported an impression; only emitted when
    /// `ServerConfig.AdAnalytics = EnabledAdAnalytics`. Reserved
    /// `SourceModule = "_platform.ads"`.
    | AdImpressionRecorded of AdImpression
    /// Phase 60 — an ad-click event was recorded via the
    /// click-redirect handler (Phase 60 follow-up); same gating
    /// as `AdImpressionRecorded`.
    | AdClickRecorded of AdClick
    /// Phase 62 — operator granted premium status to a user via
    /// the `GrantPremiumApi`. Reserved
    /// `SourceModule = "_platform.users"`.
    | PremiumGranted of subjectUserId: string * grantor: string * reason: string option * occurredAt: DateTimeOffset
    /// Phase 62 — operator revoked premium status from a user.
    /// Same `SourceModule` as `PremiumGranted`.
    | PremiumRevoked of subjectUserId: string * grantor: string * reason: string option * occurredAt: DateTimeOffset
    /// Phase 61 — operator created an `AdSlotConfig` via the
    /// public-utility PlatformAdmin `AdUnitConfigApi`. Reserved
    /// `SourceModule = "_platform.ads.config"`.
    | AdSlotConfigCreated of slotId: string * actor: string * occurredAt: DateTimeOffset
    /// Phase 61 — operator updated an existing `AdSlotConfig`. Same
    /// `SourceModule` as `AdSlotConfigCreated`.
    | AdSlotConfigUpdated of slotId: string * actor: string * occurredAt: DateTimeOffset
    /// Phase 61 — operator deleted an `AdSlotConfig`. Same
    /// `SourceModule` as `AdSlotConfigCreated`.
    | AdSlotConfigDeleted of slotId: string * actor: string * occurredAt: DateTimeOffset
    /// Phase 66 Stream C.1 (continuation) — anonymous-session data was
    /// migrated into an authenticated subject's scope on the first
    /// authenticated request following an anonymous session. Reserved
    /// `SourceModule = "_platform.subject"`.
    | AnonymousSessionMigrated of AnonymousSessionMigratedPayload
    /// Auth-observability A1 — `ScopeResolutionMiddleware` infra failure
    /// (DI hiccup, store throw, cache miss-and-throw). The request fell
    /// through to anonymous-subject behaviour; this event is the audit
    /// trail of the failure. Reserved
    /// `SourceModule = "_platform.auth"`. Named `Auth` prefix to
    /// disambiguate from `ScopeResolutionError.ScopeResolutionFailed`
    /// (`Types/StorageScope.fs` — different DU, same short name).
    | AuthScopeResolutionFailed of ScopeResolutionFailedPayload
    /// Auth-observability A2 — `SurfaceEnforcementMiddleware` denied
    /// the request. One event per denial; rate spikes indicate either
    /// a scripted enumeration or a recent surface-config change that
    /// flipped legit calls to denied. Reserved
    /// `SourceModule = "_platform.auth"`.
    | SurfaceDenied of SurfaceDeniedPayload
    /// Phase 30a — `IArtifactSigner.Sign` succeeded. Reserved
    /// `SourceModule = "_platform.artefacts"`. Payload carries the
    /// publisher key id (never the private key bytes).
    | ArtifactSigned of ArtifactSignedPayload
    /// Phase 30a — `IArtifactVerifier.Verify` returned
    /// `ArtifactValidation.Ok` (signature valid + publisher key trusted
    /// at the edge). Reserved `SourceModule = "_platform.artefacts"`.
    | ArtifactVerified of ArtifactVerifiedPayload
    /// Phase 30a — `IArtifactVerifier.Verify` returned
    /// `ArtifactValidation.Error reason`. Reserved
    /// `SourceModule = "_platform.artefacts"`. Operator dashboards
    /// query on this case to surface refusal rates without scanning
    /// every verify row.
    | ArtifactRejected of ArtifactRejectedPayload
    /// Phase 30d — `IDataCatalog.GetSyntheticSample` returned
    /// synthetic rows for a `ModulePermission.SchemaOnly` partner-
    /// sandbox caller. Payload is metadata-only (count + seed) — no
    /// synthetic bytes travel.
    | SyntheticSampleGenerated of SyntheticSampleGeneratedPayload
    /// Phase 30d — a `ModulePermission.SchemaOnly` caller attempted
    /// to access a real-row API path and was refused before any real
    /// data was read. Distinct from `SurfaceDenied` — fires at the
    /// substrate / handler layer, not at the route surface.
    | SchemaOnlyAccessAttempted of SchemaOnlyAccessAttemptedPayload
    /// Phase 18 — a typed inter-platform peer contract call resolved on
    /// the receiver. Emitted once per inbound call by the peer host's
    /// contract handler after dispatch reaches a terminal outcome.
    /// Reserved `SourceModule = "_platform.peer"`.
    | PeerCallCompleted of PeerCallCompletedPayload

module AuditEvent =
    /// Wire-format `EventType` discriminator for the given event. The
    /// returned string matches the DU case name; persisted events use
    /// this as the `ModuleEvent.EventType` field, so `IEventStore.ReadByType`
    /// queries can target a single audit kind.
    let eventTypeName (audit: AuditEvent) : string =
        match audit with
        | UserLoggedIn _ -> "UserLoggedIn"
        | TeamCreated _ -> "TeamCreated"
        | MemberAdded _ -> "MemberAdded"
        | MemberRemoved _ -> "MemberRemoved"
        | MemberRoleChanged _ -> "MemberRoleChanged"
        | FileUploaded _ -> "FileUploaded"
        | FileDeleted _ -> "FileDeleted"
        | FileReprocessed _ -> "FileReprocessed"
        | DataStoreReset _ -> "DataStoreReset"
        | AnalysisRun _ -> "AnalysisRun"
        | PermissionChanged _ -> "PermissionChanged"
        | NotificationSent _ -> "NotificationSent"
        | NotificationDeliveryFailed _ -> "NotificationDeliveryFailed"
        | HealthStateChanged _ -> "HealthStateChanged"
        | EncryptionKeyCreated _ -> "EncryptionKeyCreated"
        | EncryptionKeyRotated _ -> "EncryptionKeyRotated"
        | EncryptionKeyDestroyed _ -> "EncryptionKeyDestroyed"
        | EntityCreated _ -> "EntityCreated"
        | EntityUpdated _ -> "EntityUpdated"
        | EntityDeleted _ -> "EntityDeleted"
        | FormSubmitted _ -> "FormSubmitted"
        | FormSubmissionUpdated _ -> "FormSubmissionUpdated"
        | WorkflowTransitioned _ -> "WorkflowTransitioned"
        | AuditSinkDelivered _ -> "AuditSinkDelivered"
        | AuditSinkFailed _ -> "AuditSinkFailed"
        | AuditSinkDeadLettered _ -> "AuditSinkDeadLettered"
        | AuditEventDecodeFailed _ -> "AuditEventDecodeFailed"
        | NotificationSilentlySkipped _ -> "NotificationSilentlySkipped"
        | OAuthConnected _ -> "OAuthConnected"
        | OAuthDisconnected _ -> "OAuthDisconnected"
        | OAuthRefreshFailed _ -> "OAuthRefreshFailed"
        | OAuthTokenRefreshed _ -> "OAuthTokenRefreshed"
        | OAuthTokenRefreshFailed _ -> "OAuthTokenRefreshFailed"
        | OAuthRefreshTokenInvalidated _ -> "OAuthRefreshTokenInvalidated"
        | OAuthRefreshDeadLettered _ -> "OAuthRefreshDeadLettered"
        | PlatformAdminAssigned _ -> "PlatformAdminAssigned"
        | PlatformAdminRevoked _ -> "PlatformAdminRevoked"
        | PlatformDocumentUploaded _ -> "PlatformDocumentUploaded"
        | PlatformDocumentDeleted _ -> "PlatformDocumentDeleted"
        | ShareTokenIssued _ -> "ShareTokenIssued"
        | ShareTokenUsed _ -> "ShareTokenUsed"
        | ShareTokenRevoked _ -> "ShareTokenRevoked"
        | ConversationExported _ -> "ConversationExported"
        | BeaconRejected _ -> "BeaconRejected"
        | ConfigDrift _ -> "ConfigDrift"
        | DiagnosticBundleAccessed _ -> "DiagnosticBundleAccessed"
        | RateLimitWaited _ -> "RateLimitWaited"
        | RateLimitRefused _ -> "RateLimitRefused"
        | DataSubjectRequest _ -> "DataSubjectRequest"
        | ConversationStarted _ -> "ConversationStarted"
        | ConversationTurnAppended _ -> "ConversationTurnAppended"
        | ConversationCompleted _ -> "ConversationCompleted"
        | ConversationErased _ -> "ConversationErased"
        | ConversationReplayed _ -> "ConversationReplayed"
        | AssetUploaded _ -> "AssetUploaded"
        | AssetDeleted _ -> "AssetDeleted"
        | TeamCreationDenied _ -> "TeamCreationDenied"
        | TeamInviteIssued _ -> "TeamInviteIssued"
        | TeamInviteAccepted _ -> "TeamInviteAccepted"
        | TeamInviteAcceptedFromPending _ -> "TeamInviteAcceptedFromPending"
        | TeamInviteAcceptedFromPendingFailed _ -> "TeamInviteAcceptedFromPendingFailed"
        | TeamInviteRevoked _ -> "TeamInviteRevoked"
        | TeamInviteRedeemed _ -> "TeamInviteRedeemed"
        | WorkflowActionExecuted _ -> "WorkflowActionExecuted"
        | ConsentRecorded _ -> "ConsentRecorded"
        | AdImpressionRecorded _ -> "AdImpressionRecorded"
        | AdClickRecorded _ -> "AdClickRecorded"
        | PremiumGranted _ -> "PremiumGranted"
        | PremiumRevoked _ -> "PremiumRevoked"
        | AdSlotConfigCreated _ -> "AdSlotConfigCreated"
        | AdSlotConfigUpdated _ -> "AdSlotConfigUpdated"
        | AdSlotConfigDeleted _ -> "AdSlotConfigDeleted"
        | AnonymousSessionMigrated _ -> "AnonymousSessionMigrated"
        | AuthScopeResolutionFailed _ -> "AuthScopeResolutionFailed"
        | SurfaceDenied _ -> "SurfaceDenied"
        | ArtifactSigned _ -> "ArtifactSigned"
        | ArtifactVerified _ -> "ArtifactVerified"
        | ArtifactRejected _ -> "ArtifactRejected"
        | SyntheticSampleGenerated _ -> "SyntheticSampleGenerated"
        | SchemaOnlyAccessAttempted _ -> "SchemaOnlyAccessAttempted"
        | PeerCallCompleted _ -> "PeerCallCompleted"

/// Phase 66 Stream B.7 (design §3.6 + D15 + D16) — sink-side envelope
/// that wraps an `AuditEvent` with the resolved `AuditSubject` and the
/// recording-side bookkeeping (`OccurredAt`, `ScopeId`). External sinks
/// (`SplunkHec` / `DatadogLogs` / `S3Archive`, plus the in-memory test
/// double) receive `AuditEnvelope list` batches — the audit-event DU
/// stays the load-bearing case shape, but the envelope is the layer
/// downstream Splunk dashboards and Datadog alerts read `subject_kind`
/// off without re-deriving from per-payload introspection.
///
/// **Why a wrapper, not a flat field on every case.** Today's
/// `AuditEvent` is a DU of ~70 case constructors, each carrying its own
/// payload record. Adding a `Subject` field to every payload would
/// touch every payload type and every emission call site for negligible
/// runtime benefit — sinks already need the wrapper for `OccurredAt` +
/// `ScopeId` correlation, so the envelope is the natural home for
/// `Subject` too. The DU stays append-only at the case level; envelope
/// shape evolves under the `LatestAuditSchemaVersion` contract.
///
/// **Construction.** Three call sites construct envelopes:
///   * `IAuditLog.Record` emission sites with an `AccessContext` in
///     scope — use `AuditSubject.fromSubject ctx.Subject`.
///   * `AuditReplicator` dispatch path — decodes `ModuleEvent` into
///     `AuditEvent` and derives `AuditSubject` from `ModuleEvent.ScopeId`
///     via `AuditSubject.fromScopeId` (best-effort; the persisted
///     `ModuleEvent` does not carry the originating subject today).
///   * Tests and contract packs — synthesise envelopes directly.
type AuditEnvelope = {
    /// Resolved per-request subject of the audited operation. Sinks
    /// read this to populate `subject_kind` / `subject.user_id` /
    /// `subject.team_id` tags without inspecting the payload.
    Subject: AuditSubject
    /// The audit event body itself. Existing wire format preserved at
    /// the case level; the envelope is the new layer above it.
    Event: AuditEvent
    /// Wall-clock timestamp the event was recorded. Mirrors the
    /// underlying `ModuleEvent.OccurredAt` so sinks can preserve
    /// per-scope ordering at the destination.
    OccurredAt: System.DateTime
    /// `IEventStore` scope under which the event was persisted.
    /// Preserved so sinks that tag-route by tenant can read it
    /// directly rather than re-derive via per-payload introspection
    /// (the legacy `DatadogLogsAuditSink.extractScopeId` pattern).
    ScopeId: string
}

module AuditEnvelope =
    /// Construct an envelope from a resolved `Subject` (request side)
    /// and the matching `AuditEvent`. Canonical helper for emission
    /// call sites that have an `AccessContext` in scope.
    let fromSubject
        (subject: Subject)
        (scopeId: string)
        (occurredAt: System.DateTime)
        (event: AuditEvent)
        : AuditEnvelope =
        {
            Subject = AuditSubject.fromSubject subject
            Event = event
            OccurredAt = occurredAt
            ScopeId = scopeId
        }

    /// Construct an envelope without a resolved `Subject`. Used by the
    /// audit-replicator dispatch path, which only has `ScopeId`
    /// available post-`ModuleEvent` decode. Defers to
    /// `AuditSubject.fromScopeId`.
    let fromScopeId (scopeId: string) (occurredAt: System.DateTime) (event: AuditEvent) : AuditEnvelope = {
        Subject = AuditSubject.fromScopeId scopeId
        Event = event
        OccurredAt = occurredAt
        ScopeId = scopeId
    }

    /// Project the envelope to its underlying event. Convenience for
    /// sinks that batch-serialise events and need only the inner
    /// payload (callers tag the envelope separately).
    let event (envelope: AuditEnvelope) : AuditEvent = envelope.Event

    /// Project the envelope to its subject-kind string. Convenience for
    /// sinks emitting `subject_kind:user` style tags.
    let subjectKindString (envelope: AuditEnvelope) : string =
        envelope.Subject |> AuditSubject.kind |> AuditSubject.kindString

/// Phase 66 Stream B.7 (design D16) — schema-version contract bumped
/// when the `AuditEnvelope` wire shape changes incompatibly. Sinks
/// declare `IAuditSink.SchemaVersion` against `AuditSchemaVersion.current`
/// so the audit-replicator can warn (or refuse delivery) on mismatch
/// rather than silently mangling downstream dashboards.
///
/// - `pre66 = 1` — bare `AuditEvent` batches; no `Subject` /
///   `OccurredAt` / `ScopeId` envelope fields exposed to sinks; sinks
///   would have to re-derive scope via per-payload introspection.
///   Retained as the documented historical floor. No production sink
///   ships at this version.
/// - `current = 2` — Phase 66 Stream B.7 envelope (this file's
///   `AuditEnvelope`). All three reference companions ship at this
///   version, as does the in-process `InMemoryAuditSink` test double.
module AuditSchemaVersion =
    /// Pre-Phase-66 wire shape. Retained as documented floor for the
    /// `IAuditSink.SchemaVersion` negotiation contract. No production
    /// sink ships at this version.
    [<Literal>]
    let pre66 = 1

    /// Phase 66 Stream B.7 envelope shape. Sinks implementing
    /// `IAuditSink.SchemaVersion` typically return this so they always
    /// ship the latest envelope shape rather than pinning a specific
    /// version.
    [<Literal>]
    let current = 2

/// Query interface for the audit log. SDK-wide DI service registered in
/// `SDK.Server.compose`. The default implementation wraps `IEventStore`
/// (writes through the configured store; reads filter to `SourceModule =
/// AuditSourceModule.value`).
///
/// **Scoping.** `Record` writes the event under the supplied `scopeId`;
/// `GetAuditTrail` reads from that scope only. Cross-scope reads are
/// structurally impossible — the underlying `IEventStore` enforces
/// team isolation via the blob path prefix.
///
/// **Best-effort, never blocking.** `Record` is fire-and-forget from the
/// caller's perspective: implementation failures are logged via
/// `ILogger` and swallowed. Audit emission must never roll back a
/// primary operation — the underlying state is durable in its own store.
type IAuditLog =
    /// Record an audit event under the given scope. Best-effort
    /// durability via the configured `IEventStore`. Failures are logged
    /// at `Warn` and swallowed.
    abstract Record: scopeId: string * audit: AuditEvent -> Async<unit>

    /// Query audit events for `scopeId`. Optional filters:
    /// - `dateRange`: inclusive `(from, to)` filter on `OccurredAt`.
    ///   `None` = no date constraint.
    /// - `eventType`: case-name filter (e.g. `"UserLoggedIn"`).
    ///   `None` = all kinds.
    /// Returned list is reverse-chronological by `OccurredAt`,
    /// matching `IEventStore.ReadAll`.
    abstract GetAuditTrail:
        scopeId: string * dateRange: (DateTime * DateTime) option * eventType: string option -> Async<AuditEvent list>