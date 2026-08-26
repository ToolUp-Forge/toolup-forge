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

/// A Platform Admin archived a team via `TeamApi.ArchiveTeam`. Reversible
/// (data retained); the team is hidden from members until restored.
type TeamArchivedPayload = {
    /// The Platform Admin who archived the team.
    UserId: string
    TeamId: string
    TeamName: string
}

/// A Platform Admin restored a previously-archived team via
/// `TeamApi.RestoreTeam`.
type TeamRestoredPayload = {
    UserId: string
    TeamId: string
    TeamName: string
}

/// A Platform Admin irreversibly deleted a team via
/// `TeamApi.DeleteTeamHard` — the team record and every membership row
/// referencing it were purged. The team name is captured here because
/// the record no longer exists after the event fires.
type TeamDeletedPayload = {
    UserId: string
    TeamId: string
    TeamName: string
}

/// Phase 304 — team ownership transferred via `TeamApi.TransferOwnership`.
/// The outgoing Owner (`FromUserId`) was demoted to `Admin` and the
/// incoming Owner (`ToUserId`) promoted to `Owner`. `ActorUserId` is the
/// caller who performed the transfer — always equal to `FromUserId` under
/// the current gate (the outgoing Owner transfers their own team), carried
/// as a distinct field so a future admin-driven reassignment path stays
/// wire-compatible. Recorded under the `team-{TeamId}` audit scope (GP 6).
type TeamOwnershipTransferredPayload = {
    TeamId: string
    /// Outgoing Owner, demoted to `Admin` by the transfer.
    FromUserId: string
    /// Incoming Owner, promoted from their prior role.
    ToUserId: string
    /// Caller who invoked the transfer. Equal to `FromUserId` today.
    ActorUserId: string
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

/// Phase 22b — one replica's acknowledgement that it evicted its cached
/// copy of a destroyed encryption key. Emitted by `PerScopeKeyResolver`'s
/// `KeyDestroyed` subscription handler, once per replica that receives
/// the broadcast; NOT emitted by the replica that originated the destroy
/// (that one already recorded `EncryptionKeyDestroyed`).
///
/// **Why a distinct payload rather than `EncryptionKeyEventPayload`.**
/// Forensic completeness is the point of this event — "prove every
/// replica saw the destroy" is only answerable if each acknowledgement
/// names the replica that made it, and the propagation delay is only
/// computable if both instants are recorded. Neither fits the four
/// lifecycle fields, and adding required fields to
/// `EncryptionKeyEventPayload` would break every consumer that
/// constructs one.
type EncryptionKeyDestroyAckPayload = {
    /// Actor who requested the destroy on the originating replica,
    /// carried across from the `KeyDestroyedEnvelope`. `"system"` when
    /// the SDK destroyed the key without a user action. Deliberately the
    /// requester, not the acknowledging replica — so a query for "who
    /// crypto-shredded this tenant" returns one actor across every
    /// replica's acknowledgement.
    UserId: string
    /// Scope whose key was destroyed and whose cache entry this replica
    /// evicted.
    ScopeId: string
    /// Stable key identifier that was destroyed. Matches the
    /// `EncryptionKeyDestroyed` event on the originating replica and the
    /// envelope header of every blob now undecryptable.
    KeyId: string
    /// Resolver class name that handled the eviction — always
    /// `"PerScopeKeyResolver"` today; present so a third-party resolver
    /// adopting the same broadcast is distinguishable in the trail.
    Resolver: string
    /// The replica that evicted and is acknowledging. Distinguishes one
    /// replica's acknowledgement from another's in the shared audit
    /// trail — without it, N replicas produce N indistinguishable rows
    /// and "did every replica see it?" is unanswerable. Defaults to
    /// `{machine-name}/{process-id}`, which in a container deployment is
    /// the pod / container identity.
    AcknowledgedBy: string
    /// The replica the destroy originated on (the one that recorded
    /// `EncryptionKeyDestroyed`). Pairs each acknowledgement with its
    /// originating action when several destroys are in flight.
    OriginReplicaId: string
    /// When the destroy was requested on the originating replica.
    RequestedAt: DateTimeOffset
    /// When this replica completed its eviction.
    /// `AcknowledgedAt - RequestedAt` is the measured replica-fanout
    /// window for this replica — the number the technical guide's timing
    /// contract promises only at minute grain.
    AcknowledgedAt: DateTimeOffset
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

// ─── Phase 10g — OAuth 1.0a substrate audit payloads ────────────────────
//
// Emitted by the OAuth 1.0a callback / disconnect paths + the per-call
// signer. Reserved source-module label `_platform.oauth1a`. Like the
// OAuth 2.0 payloads, they carry the connection identity + actor but never
// the token pair (which stays behind `ISecretStore`).

module OAuth1aSourceModule =
    /// Reserved `SourceModule` for OAuth 1.0a substrate audit events.
    /// Filter `IEventStore.ReadBySource` on this constant for the trail.
    [<Literal>]
    let value = "_platform.oauth1a"

/// OAuth 1.0a access-token connection established — leg 3 succeeded and the
/// access token pair was persisted via `ISecretStore`.
type OAuth1aConnectedPayload = {
    UserId: string
    ScopeId: string
    FlowName: string
    ResourceId: string
    ConnectedAt: DateTime
}

/// User-initiated OAuth 1.0a disconnect — the local token pair was deleted.
type OAuth1aDisconnectedPayload = {
    UserId: string
    ScopeId: string
    FlowName: string
    ResourceId: string
}

/// OAuth 1.0a request signing failed — a persisted token pair was malformed
/// / unreadable, so the per-call HMAC-SHA1 signature could not be minted.
/// The connector surfaces `CredentialMissing`; this records the diagnostic.
/// Value-free (no secret material).
type OAuth1aSigningFailedPayload = {
    ScopeId: string
    FlowName: string
    ResourceId: string
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

/// Knowledge Base *original* document retrieved (Phase 107). Emitted by
/// the KB `GetOriginalDocument` handler on every successful fetch of an
/// original ingested document — a state-observing access to potentially
/// sensitive content, audited distinctly from the upload event so the
/// trail answers "who pulled which source when" (GP 6 extended to
/// sensitive reads). Identifiers + source kind only — no document
/// content, no bytes (same PII envelope as the KB upload events).
type KnowledgeOriginalRetrievedPayload = {
    /// User who fetched the original.
    UserId: string
    /// Id of the `KnowledgeDocument` whose original was fetched.
    DocumentId: string
    /// Scope the document lives in (the caller's resolved scope —
    /// the structural gate guarantees they match, GP 4).
    ScopeId: string
    /// Source-kind case name ("UploadedFile" / "Note" /
    /// "FromNarrative") so the trail distinguishes binary originals
    /// from note-markdown fetches without payload introspection.
    SourceKind: string
    /// Original file name. Audit-readability — operators reading the
    /// trail recognise file names faster than UUIDs.
    FileName: string
}

/// Knowledge Base original-document fetch refused (Phase 107). Emitted
/// by the KB `GetOriginalDocument` handler when a fetch is denied —
/// out-of-scope document id, or a source kind with no retrievable
/// original. The refusal is itself audit-worthy: denials on the team
/// boundary are material security signals (GP 4 + GP 6).
type KnowledgeOriginalRetrievalDeniedPayload = {
    /// User whose fetch was refused.
    UserId: string
    /// Document id the caller asked for. May not exist anywhere —
    /// recorded verbatim so enumeration attempts are visible.
    DocumentId: string
    /// Scope the caller was acting within.
    ScopeId: string
    /// Refusal reason — the `KnowledgeBaseError` case name
    /// ("NotInScope" / "NoOriginalAvailable").
    Reason: string
}

/// Knowledge Base scope wiped (Phase 115). Emitted by the KB
/// `ResetIndex` handler after `performReset` has fanned the deletion
/// out across every retrieval index (vector store + sparse BM25 leg +
/// persisted snapshots) via `IIndexLifecycle`. Distinct from, and
/// complementary to, the generic `[<Audit "Custom:KnowledgeIndexReset">]`
/// action row the dispatcher already emits: that records *who* called
/// reset; this records the *erasure outcome* — how many documents the
/// scope held and, critically, whether the fan-out left any chunk
/// retrievable in the indexes (GP 6 + GP 9 — a half-completed delete is
/// audit-worthy and must be loud). Identifiers + counts only, no
/// document content (same PII envelope as the other KB audit events).
type KnowledgeScopeErasedPayload = {
    /// User who triggered the scope reset (the caller's resolved `UserId`).
    UserId: string
    /// Scope that was wiped (the caller's resolved scope — the structural
    /// gate guarantees they match, GP 4).
    ScopeId: string
    /// Number of `KnowledgeDocument`s the scope held at reset time.
    DocumentCount: int
    /// Chunks that survived the fan-out across the retrieval indexes — `0`
    /// on a clean wipe. A non-zero value means RAG may keep surfacing
    /// wiped documents, so the audit trail carries the same loud signal
    /// the operator log does (GP 9).
    OrphanChunkCount: int
}

/// Phase 14v — reserved audit scope for RAG/KB knowledge-index
/// infrastructure events. `KnowledgeIndexLoadFailed` is recorded under
/// this scope (via `IAuditLog.Record`) so an operator can query the
/// knowledge-index health trail in isolation, distinct from per-tenant
/// activity. Deployment-wide, like the `_platform` scope the Platform
/// KB document events use.
module KnowledgeSourceModule =
    [<Literal>]
    let value = "_platform.knowledge"

/// Phase 14v — a persisted RAG vector-index blob failed to deserialise
/// on scope load. Today the in-memory vector store catches the
/// deserialisation failure, logs a single `Warn`, and starts the scope
/// empty; in multi-replica deploys a blob corrupted by one node (disk
/// failure, partial flush during a pod kill) makes the next replica read
/// that scope and start it silently empty — retrieval returns nothing
/// and the operator has no signal beyond a buried log line. This event
/// makes the corrupt load loud (GP 6 + GP 9). Identifiers + cardinality
/// only; no index content travels through the audit trail.
type KnowledgeIndexLoadFailedPayload = {
    /// Vector-store scope key whose index failed to load —
    /// `platform` / `deployment` / `team:{teamId}`.
    ScopeKey: string
    /// Deserialisation failure detail (the exception message). Verbatim
    /// so operators can correlate against the store's own `Warn` log line.
    Reason: string
    /// Size of the corrupt blob in bytes. Cardinality only — the body
    /// itself never travels through the audit trail.
    Bytes: int
    /// Blob location (the index path within the RAG container) so the
    /// operator can find and replace / delete the corrupt artefact.
    BlobLocation: string
}

/// Phase 303 — a `DocumentIngestionJob` was dropped because the
/// in-process ingestion queue was at capacity and the bounded enqueue
/// retry was exhausted. The source file persists to KB / Data-Manager
/// blob storage and appears in the document list, but its chunks never
/// reach retrieval — so without this row the loss is silent (the user
/// thinks the upload "worked"; retrieval returns nothing relevant).
/// Recorded under `KnowledgeSourceModule.value` scope (deployment-wide,
/// like the corrupt-index trail) so an operator can query queue-drop
/// pressure in isolation. Identifiers + cardinality only; no chunk
/// content travels through the audit trail.
type KnowledgeIngestionDroppedPayload = {
    /// Vector-store scope key the dropped document was bound for —
    /// `platform` / `deployment` / `team:{teamId}`.
    ScopeKey: string
    /// Document id (the file name) that was dropped. Recorded verbatim
    /// so the operator can correlate against the KB / Data-Manager
    /// document list and re-upload.
    DocId: string
    /// Number of chunks (including any summary chunk) that would have
    /// been indexed. Cardinality only — the chunk bodies never travel
    /// through the audit trail.
    ChunkCount: int
    /// Configured `IngestionQueue.Capacity` at drop time, so the
    /// operator can size the gap between offered load and capacity.
    QueueCapacity: int
    /// Why the document was dropped (e.g. "ingestion queue full after
    /// bounded retry").
    Reason: string
}

/// Phase 14x — a KB upload was deduplicated onto an existing document:
/// the caller's scope already held a `KnowledgeDocument` with the same
/// content hash, so `UploadDocument` returned the existing document and
/// skipped ingestion entirely (no re-chunk, no re-embed, no duplicate
/// retrieval hits). Audited so the idempotent-upload decision is
/// queryable in the trail (GP 5 — the dedup outcome is recorded, not
/// silent). Identifiers + hash only; no document content travels
/// through the audit trail (same PII envelope as the other KB events).
type KnowledgeDocumentDeduplicatedPayload = {
    /// User whose upload was deduplicated.
    UserId: string
    /// Scope the existing document lives in — the caller's resolved
    /// scope (the hash index is container-local, GP 4).
    ScopeId: string
    /// Id of the pre-existing `KnowledgeDocument` the upload matched
    /// and that was returned to the caller.
    ExistingDocumentId: string
    /// File name of the *attempted* upload. May differ from the stored
    /// document's name — dedup keys on content, not name.
    FileName: string
    /// Lowercase SHA-256 hex of the uploaded bytes. A correlation
    /// identifier, not content.
    ContentHash: string
}

/// Phase 512 — one or more `KnowledgeDocument`s were purged from a scope
/// by the age-based retention sweep. Emitted once per sweep run that
/// removed anything (a run that expired nothing writes no row — a purge
/// trail must record deletions, not the absence of them), under the
/// swept scope, so an operator can answer "what did retention take, and
/// when" from the trail alone (GP 6). Identifiers + cardinality only; no
/// document content travels through the audit trail.
type KnowledgeDocumentsPurgedPayload = {
    /// Scope whose corpus was swept — the container's scope id (GP 4:
    /// the sweep only ever reaches one scope per run).
    ScopeId: string
    /// Document ids removed by this run, in index order. The list is the
    /// trail's evidence — a count alone cannot be reconciled against the
    /// corpus afterwards.
    DocumentIds: string list
    /// Number of documents removed (`DocumentIds.Length`, denormalised so
    /// a sink can aggregate without parsing the list).
    PurgedCount: int
    /// Total `SizeBytes` reclaimed across the purged documents.
    ReclaimedBytes: int64
    /// Retention age in whole seconds that selected them, so the row
    /// carries the policy that produced it rather than requiring the
    /// reader to correlate against a config snapshot.
    MaxAgeSeconds: int64
    /// Chunks that survived the index fan-out across the retrieval
    /// indexes — `0` on a clean purge. Non-zero means RAG may keep
    /// surfacing purged documents, so the trail carries the same loud
    /// signal the operator log does (GP 9), exactly as
    /// `KnowledgeScopeErased` does for a scope reset.
    OrphanChunkCount: int
}

/// Phase 515 — an upload was inspected by the composed `IContentScanner`
/// at the upload boundary. Emitted on **every** verdict, not only a
/// refusal (GP 6): "this file was scanned and came back clean at 14:02"
/// is precisely the fact an incident reconstruction needs when the same
/// file is implicated a week later, and a trail that records only
/// rejections cannot distinguish a scanner that passed the payload from
/// one that was never consulted. A deployment that composed no scanner
/// emits nothing at all (GP 13) — there is no row for the no-op default.
///
/// Identifiers, the verdict label and the scanner's own reason string
/// only. The payload itself never travels through the audit trail; the
/// digest is the correlation handle, exactly as in
/// `KnowledgeDocumentDeduplicated`.
type ContentScannedPayload = {
    /// Subject whose upload was scanned.
    UserId: string
    /// Scope the upload was made under — the caller's resolved scope
    /// (GP 4: it comes from the resolver, never from the caller).
    ScopeId: string
    /// `IContentScanner.Name` of the scanner that produced the verdict,
    /// so a trail spanning a scanner swap stays attributable.
    ScannerName: string
    /// Sanitised file name of the upload, post `Path.GetFileName`.
    FileName: string
    /// Lowercase SHA-256 hex of the scanned bytes — a correlation
    /// identifier, not content. Always present: the digest is computed
    /// for the audit row even where the upload path would not otherwise
    /// hash (e.g. `withDocumentDedup false`), because a scan verdict
    /// with no handle on WHAT was scanned is not investigable.
    ContentHash: string
    /// Size in bytes of the scanned payload.
    SizeBytes: int64
    /// `ScanVerdict.label` — `"clean"` / `"rejected"` / `"unavailable"`.
    Verdict: string
    /// The scanner's reason for a non-clean verdict; `None` when clean.
    Reason: string option
    /// `true` when the platform refused the upload on the strength of
    /// this verdict. Distinct from `Verdict` because the two come apart
    /// exactly where it matters: an `"unavailable"` verdict under
    /// `FailOpenOnScanError` is recorded and ADMITTED, and an operator
    /// auditing a fail-open deployment needs to find those rows without
    /// re-deriving the policy that was in force at the time.
    Refused: bool
    OccurredAt: DateTimeOffset
}

// ─── Data-object orphan-blob sweep payloads (Phase 7c) ────────────────

/// Phase 7c — one orphaned content blob was reclaimed from a scope's
/// content-addressable dedup pool (`objects/_content/{hash}.data`). An
/// orphan is a content blob no surviving `v{N}.json` metadata blob
/// references — the residue of a `Save` that wrote its content and then
/// died before writing its metadata. Emitted once **per reclaimed blob**,
/// under the swept scope, so a deletion is attributable rather than only
/// countable: the GDPR question ("is the deleted user's content actually
/// gone from content-addressable storage?") is answered per hash, and the
/// storage-cost question is answered by summing `Bytes`.
///
/// Identifiers + sizes only. The content hash is a correlation
/// identifier, never content — the bytes themselves never travel through
/// the audit trail (same envelope as every other storage-side event).
type OrphanedContentBlobReclaimedPayload = {
    /// Scope the blob was reclaimed from — the container's scope id.
    /// GP 4: one sweep run reaches exactly one scope's container.
    ScopeId: string
    /// Lowercase SHA-256 hex the blob was keyed by (its
    /// `_content/{hash}.data` name).
    ContentHash: string
    /// Size of the reclaimed blob in bytes, as the backing store
    /// reported it immediately before the delete.
    Bytes: int64
    /// Whole hours between the blob's last write and the sweep — always
    /// at least the configured grace period, since younger orphans are
    /// deferred rather than reclaimed. Carried so the row shows the
    /// evidence for the reclaim decision, not just its result.
    AgeHours: int64
}

/// Phase 7c — aggregate summary of one orphan-sweep run over one scope.
/// Emitted **only by a run that reclaimed at least one blob**, alongside
/// the per-blob `OrphanedContentBlobReclaimed` rows.
///
/// **Deviation from the phase text, recorded deliberately.** The Phase 7c
/// task asked for a summary "per scope per run". A daily sweep across N
/// scopes would then write N rows a day forever saying nothing happened —
/// and Phase 512 settled the estate posture on exactly this question: a
/// purge trail records deletions, not the absence of them
/// (`KnowledgeDocumentsPurged` is emitted only by runs that removed
/// something). A run that reclaimed nothing is visible in the operator
/// log and the returned report; it does not need an audit row.
type OrphanSweepCompletedPayload = {
    /// Scope swept — the container's scope id (GP 4).
    ScopeId: string
    /// Orphaned content blobs found in the container, before the grace
    /// filter. `OrphansFound - ReclaimedCount - Failures` is the number
    /// deferred as too young.
    OrphansFound: int
    /// Blobs actually deleted by this run.
    ReclaimedCount: int
    /// Total bytes reclaimed across `ReclaimedCount`.
    ReclaimedBytes: int64
    /// Orphans left in place because they were younger than the grace
    /// window — the in-flight-`Save` protection working, not a failure.
    DeferredCount: int
    /// Grace window in whole hours that produced `DeferredCount`, so the
    /// row carries the policy that produced it.
    GracePeriodHours: int64
    /// Deletes the backing store refused. Non-zero means the orphans are
    /// still there and the next run retries them (GP 9).
    FailureCount: int
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

/// Phase 528 — one recorded session was revoked, by its owner or by a
/// team administrator. `ActorUserId` is who performed the revocation and
/// `SubjectUserId` is whose session it was; they differ exactly on the
/// admin force-revoke path, which is the case worth being able to find
/// in the trail later.
///
/// `SessionId` is the derived, one-way session id — safe to record
/// because it is a hash of the credential rather than the credential
/// (see `SessionTypes.fs`). No token, cookie value, or `User-Agent` is
/// carried here: the audit row answers "which session, revoked by whom,
/// when", and anything more would put credential-adjacent material into
/// the one store designed to be replicated off-box.
type SessionRevokedPayload = {
    ActorUserId: string
    SubjectUserId: string
    SessionId: string
    /// Coarse device descriptor as stored on the record — enough to
    /// recognise which session was cut off without re-deriving it.
    DeviceDescriptor: string
    /// `true` when the actor revoked someone else's session (the
    /// admin force-revoke path). Denormalised rather than left to a
    /// reader comparing the two id fields, so an alerting rule over the
    /// audit stream can key off it directly.
    ByAdministrator: bool
}

/// Phase 528 — a wholesale revocation: sign-out-everywhere, or a team
/// administrator cutting off another user entirely. `RevokedCount` is
/// how many records moved from active to revoked, so a trail reader can
/// tell a real sign-out-everywhere from a no-op repeat.
type AllSessionsRevokedPayload = {
    ActorUserId: string
    SubjectUserId: string
    RevokedCount: int
    ByAdministrator: bool
}

// ─── Service accounts (Phase 527) ────────────────────────────────────
//
// Machine principals and their scoped API tokens. Reserved
// `SourceModule = "_platform.audit.service_accounts"`
// (`ServiceAccountTypes.AuditSourceModule`).
//
// Every payload carries `AccountId` so the whole life of a machine
// principal — created, granted, minted, revoked, disabled — reads as one
// filterable trail. None of them ever carries a token SECRET; `TokenId`
// is a public identifier by construction (it rides the token string in
// the clear), and the secret exists only in the mint response.
//
// `UserId` is the human ACTOR who performed the management act, not the
// machine principal — a service account never creates or disables
// itself, so attribution here is always to a person.

/// `IServiceAccountStore.Create` succeeded. `Modules` lists the module
/// names in the declared permission set (names only — the grant levels
/// live on the account record) so an operator can see the credential's
/// reach without a second read.
type ServiceAccountCreatedPayload = {
    UserId: string
    AccountId: string
    DisplayName: string
    Modules: string list
}

/// `IServiceAccountStore.SetPermissions` succeeded — the account's
/// declared authority ceiling changed. Both sides are recorded because
/// "what was it before" is the question an incident asks first, and the
/// prior value is otherwise unrecoverable from the account record.
type ServiceAccountPermissionsChangedPayload = {
    UserId: string
    AccountId: string
    PreviousModules: string list
    Modules: string list
}

/// `IServiceAccountStore.MintToken` succeeded. The credential itself is
/// NOT in this payload and never can be — the store retains only a
/// salted hash.
type ServiceAccountTokenMintedPayload = {
    UserId: string
    AccountId: string
    TokenId: string
    DisplayName: string
    ExpiresAt: System.DateTimeOffset
}

/// `IServiceAccountStore.RevokeToken` succeeded. Subsequent validations
/// of this token return `RevokedToken`.
type ServiceAccountTokenRevokedPayload = {
    UserId: string
    AccountId: string
    TokenId: string
}

/// `IServiceAccountStore.SetStatus` succeeded. `Disabled = true` means
/// every token belonging to the account is now refused wholesale; the
/// tokens themselves are untouched, so the transition is reversible and
/// this event is emitted for both directions.
type ServiceAccountStatusChangedPayload = {
    UserId: string
    AccountId: string
    Disabled: bool
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

/// Phase 451 — a compute submission was refused by the scope's budget.
/// Emitted by `ComputeBudgetGuard` on every denial, from both enforcement
/// points (the `IExternalComputeDispatcher.Submit` decorator and the
/// fit-job enqueue path), so an operator sees one uniform row whichever
/// surface the submission arrived on.
///
/// **Always recorded, never sampled.** A budget refusal is material state
/// — the work did NOT happen, someone is waiting for a result that will
/// not arrive, and the reason is a policy decision the deployment made.
/// That is the same argument that made `RateLimitRefused` unconditional
/// while `RateLimitWaited` is threshold-gated.
type ComputeBudgetDeniedPayload = {
    /// The typed refusal, verbatim — the same value the caller received,
    /// so the audit row and the client's error cannot disagree.
    Denial: ComputeBudgetDenial
    /// Which enforcement point refused: `"external-compute"` (the
    /// dispatcher decorator) or `"model-fit-enqueue"`.
    Surface: string
    /// Work discriminator of the refused submission — `ExternalWorkSpec.Kind`
    /// for an external submission, the batch id for a fit enqueue. Opaque
    /// to the platform; recorded so an operator can tell which workload is
    /// exhausting the budget.
    Kind: string
    /// Submitter identity, where the surface resolved one. Empty when the
    /// seam carries no identity (`IExternalComputeDispatcher.Submit` takes
    /// a scope, not a principal).
    SubmittedBy: string
    /// Wall-clock of the refusal (UTC).
    RefusedAt: DateTime
}

/// Phase 451 — a compute submission was **admitted** while the scope's
/// period allowance was at or past its warning threshold.
///
/// The event exists because a budget whose only signal is refusal tells an
/// operator about the problem exactly once — at the moment work starts
/// failing. This is the row that arrives before that, and it is emitted on
/// the admitted path, so it is a leading indicator rather than a
/// post-mortem.
///
/// Emitted only when the crossing is NEW (the submission took the scope
/// from below the threshold to at-or-above it), never on every subsequent
/// submission. A per-submission warning on an exhausted budget is a log
/// flood that operators mute, which is the same as not having the signal.
type ComputeBudgetWarningPayload = {
    ScopeId: string
    /// `SubmitterClass.label` of the submission that crossed the threshold.
    SubmitterClass: string
    /// `ComputeBudgetPeriod.key` of the accounting period.
    PeriodKey: string
    /// The configured period allowance, in abstract cost units.
    Quota: decimal
    /// Cost units consumed after admitting this submission.
    Spent: decimal
    /// The fraction of `Quota` that triggers the warning (e.g. `0.8M`).
    Threshold: decimal
    /// Which enforcement point admitted it — same vocabulary as
    /// `ComputeBudgetDeniedPayload.Surface`.
    Surface: string
    /// Wall-clock of the crossing (UTC).
    ObservedAt: DateTime
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

/// Phase 547 — an email-keyed pending invite expired unconsumed and was
/// swept from `IPendingInviteStore`. Emitted once per dropped entry by
/// every sweep site in the store impl (`SweepExpired`, the opportunistic
/// compaction inside `Upsert`, and the expired-branch of
/// `TryConsumeForEmail`), recorded under the `team-{TeamId}` scope.
/// Without it the sweep is silent — the invitee ends up in neither the
/// Members panel nor Pending Invites and nobody is told, so the
/// operator's mental model diverges permanently from system state
/// (GP 6, "audit the silent path"). PII envelope matches the sibling
/// `TeamInviteAcceptedFromPending*` events, which already carry the
/// invitee email verbatim for this team-scoped, admin-only trail.
type TeamInviteExpiredPayload = {
    TeamId: string
    /// Lower-cased email the invite was keyed on (the store's map key).
    InviteeEmail: string
    /// Inviter captured at issue time — the natural recipient of the
    /// optional expiry notification (Phase 547.C).
    InviterUserId: string
    /// Role the invitee would have been granted had they signed in in
    /// time.
    Role: TeamRole
    /// When the invite was issued. `DateTime.MinValue` for entries
    /// persisted before Phase 547 added `PendingInviteByEmail.IssuedAt`
    /// (the record decodes leniently — a missing field defaults rather
    /// than quarantining the blob).
    IssuedAt: System.DateTime
    /// The entry's `ExpiresAt` — the instant it became unconsumable.
    ExpiredAt: System.DateTime
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

/// Phase 120 — the uniform structured authorization-denial row written by
/// `IAuthAuditHook` across every denial class on the HTTP surface
/// (surface-enforcement / RBAC role / share-token / SSE-identity /
/// module-permission / KB-destructive). Generalises the write side of the
/// AI tool-allowlist denial stream (Phase 45) to the whole auth surface,
/// so operators get one queryable trail keyed by route/requirement/scope
/// instead of scattered per-subsystem metrics and log lines (GP 6).
///
/// Reserved `SourceModule = AuditSourceModule.value`; written under the
/// caller's scope when known (so the `/dev/auth-denials` rollup is
/// caller-scope-only, GP 4) and under `_platform` for scope-less
/// (anonymous / pre-scope) denials.
///
/// PII envelope: nothing beyond the sanitised `SubjectId` (the same id the
/// `SurfaceDenied` row already carries) — no request bodies, no headers.
type AuthorizationDeniedPayload = {
    /// Request route the denial fired on (method + path, unparameterised).
    Route: string
    /// Requirement class that was not satisfied — one of `"surface"` /
    /// `"role"` / `"share-token"` / `"sse-identity"` /
    /// `"module-permission"` / `"kb-destructive"`. String at the wire
    /// boundary so the `AuthDenialRequirement` DU can evolve without
    /// breaking persisted rows.
    Requirement: string
    /// Subject kind at denial time (`anonymous` / `user` / `team` /
    /// `claim`). Full subject id is in `SubjectId` when not anonymous.
    SubjectKind: string
    /// Stable identifier of the denied subject. `None` for anonymous.
    SubjectId: string option
    /// Machine-readable verdict / denial code (e.g. `team_required`,
    /// `revoked_token`, `use_limit_exceeded`, `claim_bearer_not_admitted`).
    Verdict: string
    /// Human-readable reason. Bounded + sanitised — carries no PII beyond
    /// `SubjectId`.
    Reason: string
    /// Scope the denial occurred in (`team-…` / `user-…` / claim scope).
    /// `None` when there was no caller scope (anonymous / pre-scope).
    ScopeId: string option
    /// Correlation id stitching this row to the request log + client trace.
    CorrelationId: string option
    /// Phase 120 flood guard — how many denials this single row represents.
    /// `1` for a leading-edge row; `> 1` for a window-rollover summary that
    /// coalesced a probing burst on the same `(route, subject)` key, so a
    /// scripted enumeration yields bounded audit volume with an accurate
    /// total rather than one row per probe.
    DedupCount: int
    OccurredAt: DateTimeOffset
}

/// Phase 272 — a hosted-tree action was authorized (or denied) through the
/// host-neutral action seam (Phase 113 `IActionAuthorizer`). GP 6 mandates
/// "audit everything that changes state" — an action a user drove through a
/// hosted UI must be traceable for the regulated / Sovereign buyers in the
/// Vision (provenance is the moat). Keyed on the neutral `ActionDescriptor`
/// (kind / target / scope) + the decision; NO tree-language type appears
/// (open-core boundary). PII-free beyond `SubjectId` — same envelope as the
/// Phase 120 `AuthorizationDenied` row. A DENIED action is audited too (the
/// security-relevant case).
type HostActionDispatchedPayload = {
    /// Subject kind at dispatch (`anonymous` / `user` / `team` / `claim`).
    SubjectKind: string
    /// Stable subject id; `None` for anonymous (no PII).
    SubjectId: string option
    /// `ActionDescriptor.Kind` — the action space (`dispatch` / `call` /
    /// `navigate` / `notify` / `invoke` / host-defined).
    ActionKind: string
    /// `ActionDescriptor.Target` — the specific action within the kind (a
    /// message case name, a Remoting method, a route, a capability id).
    ActionTarget: string
    /// Scope the action targeted (`ActionDescriptor.Scope`); `None` when the
    /// action carried no explicit scope.
    ScopeId: string option
    /// `true` when the authorizer granted the action; `false` for a denial.
    Allowed: bool
    /// Human-readable decision reason — the authorizer's `Deny` reason, or a
    /// fixed `"allowed"` marker on the grant path. Bounded + PII-free beyond
    /// `SubjectId`.
    Reason: string
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
//
// Phase 625 — these three payloads and their `AuditEvent` cases were
// renamed `Artifact*` -> `ModuleArtefact*`. They were a one-letter
// homograph of the Phase 40 `_platform.signing` family
// (`ArtefactSignedPayload` / `AuditEvent.ArtefactSigned`), which is a
// DIFFERENT event about a DIFFERENT subject, and neither the compiler
// nor a reviewer distinguishes `Artifact`/`Artefact` reliably. The
// `Module` qualifier makes the two families a word apart rather than a
// vowel apart, and adopts the estate's `artefact` house spelling.
//
// The WIRE IS UNCHANGED: `AuditEvent.eventTypeName` still returns the
// historical `"ArtifactSigned"` / `"ArtifactVerified"` /
// `"ArtifactRejected"` discriminators. See the decision record at
// `auditEventCodecs` in `Server/AuditLog.fs`. Record FIELD names are
// likewise untouched (they ARE serialised); only the F#-facing type and
// case identifiers moved.

/// `IArtifactSigner.Sign` succeeded. Records who signed (the actor that
/// invoked the signer), which publisher key was used (id only — never
/// the private key), and the manifest's module / version identity.
///
/// Wire `EventType` is the historical `"ArtifactSigned"` (Phase 625).
type ModuleArtefactSignedPayload = {
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
///
/// Wire `EventType` is the historical `"ArtifactVerified"` (Phase 625).
type ModuleArtefactVerifiedPayload = {
    /// `PublisherKeyId.value` of the publisher whose signature was
    /// validated.
    PublisherKeyId: string
    /// `ArtifactManifest.ModuleId`.
    ModuleId: string
    /// `ArtifactManifest.Version`.
    ArtifactVersion: string
}

/// `IArtifactVerifier.Verify` returned `ArtifactValidation.Error reason`.
/// Recorded as a separate case from `ModuleArtefactVerified` so operator
/// dashboards can target refusal rates without scanning every verify
/// row.
///
/// Wire `EventType` is the historical `"ArtifactRejected"` (Phase 625) —
/// deliberately pinned, because `CefFormatter`'s `highEvents` severity
/// set and operator-owned SIEM rules key on that exact string.
type ModuleArtefactRejectedPayload = {
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

/// Phase 551 — a grant WRITE was refused because it did not satisfy the
/// target module's declared `GrantPolicy`. Emitted by the grant-policy
/// write guard before anything is persisted, so the refusal is visible
/// even though no state changed (GP 6). Its dispatch-time twin is
/// `UnconsentedGrantRefused` — the two are deliberately separate events
/// because they answer different questions: this one says an admin tried
/// to create authority the module does not admit, that one says
/// authority already recorded is not being honoured.
type GrantPolicyRefusedPayload = {
    /// The administrator attempting the grant.
    ActorId: string
    /// The subject the grant was being written for. Empty when the write
    /// was a whole-document replacement with no single subject.
    SubjectId: string
    /// Module the grant targeted. Mirrors the `IPermissionStore` /
    /// `AccessContext.ModulePermissions` key shape — the SAME key the
    /// module declared its policy under, so no second naming axis exists
    /// to drift.
    ModuleName: string
    /// The module's declared policy, as its stable wire token.
    DeclaredPolicy: string
    /// Stable refusal discriminator (`GrantRefusal.code`) — the field an
    /// operator dashboard groups by.
    RefusalCode: string
}

/// Phase 551 — a request was refused at DISPATCH because the caller's
/// permission entry on a policy-bearing module carried no live grant
/// record. This is the control that survives a grant row written
/// straight into the store: the write guard can be bypassed, the
/// dispatch check cannot (Phase 311 lesson). Distinct from
/// `SurfaceDenied` (route surface) and from `SchemaOnlyAccessAttempted`
/// (substrate read) — this fires at the module-access gate.
type UnconsentedGrantRefusedPayload = {
    /// Acting `AccessContext.UserId`.
    UserId: string
    /// Module whose routes were refused.
    ModuleName: string
    /// The module's currently declared policy, as its stable wire token.
    DeclaredPolicy: string
    /// Why the grant was inert — `"no-grant-record"`,
    /// `"awaiting-subject-consent"`, `"evidence-below-declared-policy"`,
    /// or `"counterparty-approval-unavailable"`
    /// (`GrantPolicy.inertReason`). A dashboard separating the first from
    /// the second separates suspected injection from ordinary pending
    /// consent.
    InertReason: string
}

// ─── Phase 555 — dual control for sensitive admin mutations ──────────
//
// Five events, one per act in the ceremony, because an operator asking
// "what is queued", "who approved what", "what was turned down", "what
// was attempted and structurally refused" and "what lapsed unreviewed"
// is asking five different questions with five different responses. A
// single `AdminMutationDecided` row carrying an outcome field would make
// the fourth question — the one that is a security signal rather than an
// operations signal — a filter over the others.
//
// Every row carries `RequestId` and (except the refusal, where the
// payload may not be readable) `Fingerprint`, so the propose→decide pair
// joins on an identity that binds to the exact bytes proposed rather
// than to a mutable id.

/// Phase 555 — a gated admin mutation was captured as a pending record
/// and did NOT apply. The first half of the two-person ceremony: this
/// row means authority was proposed, not created.
type AdminMutationProposedPayload = {
    /// The pending record's opaque identifier — the string an approver
    /// names, and the join key to the decision row.
    RequestId: string
    /// The team whose permission document the mutation targets.
    TeamId: string
    /// The administrator who proposed it. Never empty: an unattributable
    /// write is refused rather than parked, so no proposal row can be
    /// anonymous.
    ProposerId: string
    /// `AdminMutationKind.toToken` — what class of write is queued.
    MutationKind: string
    /// SHA-256 over the captured mutation. Binds this row to the exact
    /// payload, so a decision row naming the same fingerprint provably
    /// decided the same change.
    Fingerprint: string
    /// The operator-facing one-liner the approver will be shown.
    Summary: string
    /// When the proposal lapses if nobody decides it.
    ExpiresAtUtc: DateTimeOffset
}

/// Phase 555 — a second, distinct administrator approved a pending
/// mutation. `Applied` distinguishes "approved and the underlying write
/// succeeded" from "approved and the underlying store then refused it" —
/// a distinction an approver cannot see and an auditor must.
type AdminMutationApprovedPayload = {
    RequestId: string
    TeamId: string
    /// The administrator who proposed it.
    ProposerId: string
    /// The administrator who approved it. Structurally never equal to
    /// `ProposerId` — that is the control.
    ApproverId: string
    MutationKind: string
    Fingerprint: string
    /// Did the approved mutation actually land? `false` means the
    /// approval was valid and the underlying store refused the write
    /// (storage failure, or a Phase 551 grant-policy refusal evaluated
    /// against a document that moved since the proposal).
    Applied: bool
}

/// Phase 555 — a second administrator deliberately turned a pending
/// mutation down. Distinct from `AdminMutationApprovalRefused`: this is a
/// decision, that is a refused attempt.
type AdminMutationRejectedPayload = {
    RequestId: string
    TeamId: string
    ProposerId: string
    /// The administrator who rejected it.
    ApproverId: string
    MutationKind: string
    Fingerprint: string
    /// The reason the rejecting administrator gave. May be empty.
    Reason: string
}

/// Phase 555 — an approval ATTEMPT was structurally refused. The
/// security-signal row of the family: a proposer trying to approve their
/// own proposal, an attempt on a lapsed record, or an attempt on a
/// request that does not exist all land here rather than being invisible
/// because nothing changed.
type AdminMutationApprovalRefusedPayload = {
    RequestId: string
    TeamId: string
    /// The proposer, where the record was readable. Empty when the
    /// request was not found.
    ProposerId: string
    /// Who attempted the approval.
    AttemptedApproverId: string
    /// `AdminMutationKind.toToken`, or empty when the record was not
    /// readable.
    MutationKind: string
    /// Stable refusal discriminator (`AdminMutationRefusal.code`) — the
    /// field an operator dashboard groups by. `self-approval-refused` is
    /// the one worth alerting on.
    RefusalCode: string
}

/// Phase 555 — a pending mutation lapsed without a decision and was
/// swept. Emitted at the moment the record is discarded, so the trail
/// shows a proposal ending rather than merely stopping.
type AdminMutationExpiredPayload = {
    RequestId: string
    TeamId: string
    ProposerId: string
    MutationKind: string
    Fingerprint: string
    /// When it lapsed.
    ExpiredAtUtc: DateTimeOffset
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

/// Phase 310 — a *long-running* peer call reached its terminal outcome on
/// the receiver. Emitted once per finished job by `PeerJobHandler.Execute`,
/// after the typed result has been parked in the `IPeerJobResultStore`.
/// Reserved `SourceModule = "_platform.peer"`, the same family as
/// `PeerCallCompleted`. PII-free on the same terms: peer ids, a correlation
/// id, and a short outcome label — never the computed result.
///
/// **Why a distinct case rather than a field on `PeerCallCompletedPayload`.**
/// A long-running call already emits one `PeerCallCompleted` row, and it is
/// emitted at *schedule* time: `peer.Handle` returns `Ok jobId`, so that row
/// records `Succeeded = true, Outcome = "ok"` however the background
/// computation later ends. The two rows answer different questions — "the
/// receiver accepted the call" versus "the receiver's computation finished
/// like this" — and they land minutes apart. Marking the phase on the
/// existing payload would have changed the wire shape of every immediate
/// call's row for the sake of the minority that are long-running; a new case
/// leaves that payload byte-for-byte identical (GP 11) and lets an operator
/// query terminal outcomes without scanning schedule-time noise.
///
/// The pair is correlated by `RootRequestId` — the cascade-wide id the
/// receiver *derived* (Phase 331), threaded to the execution side on the job
/// payload so the terminal row is filed under the same correlation as the
/// schedule-time row rather than a freshly-minted one.
///
/// **Expiry is deliberately not a terminal outcome here.** Phase 316's
/// retention can retire a parked result before anyone polls it, but that is
/// the lifetime of the *record*, not the outcome of the *call*: this row is
/// written when the computation finishes, so the trail stays truthful
/// whether or not the result is ever collected. Auditing expiry would also
/// mean emitting from `IPeerJobResultStore.TryGetResult`, i.e. from inside
/// the poll route's read path — a write in a read seam, and the shape the
/// estate's post-response side-effect hazard lives in.
type PeerJobCompletedPayload = {
    /// The hosted `contractId` the long-running call targeted.
    ContractId: string
    /// The contract method name whose job resolved.
    MethodName: string
    /// `PeerId` of the peer that *scheduled* the call, taken from the
    /// validated `PeerCallContext` at dispatch time and carried on the job
    /// payload — never re-derived on the execution side, which has no
    /// request and therefore no principal to read.
    CallerPeerId: string
    /// The same cascade-wide correlation id the schedule-time
    /// `PeerCallCompleted` row carries, so the two rows line up.
    RootRequestId: string
    /// Substrate `JobId` of the backing job — the id the caller polls with,
    /// so an operator can join this row to a poll trace.
    JobId: Guid
    /// `true` when the job resolved `Completed`; `false` on `Failed`.
    Succeeded: bool
    /// Short outcome label: `"ok"` on `Completed`, else the `PeerError` DU
    /// case name (e.g. `"PeerHandler"`). Same vocabulary as
    /// `PeerCallCompletedPayload.Outcome`, so a dashboard groups both rows
    /// on one axis.
    Outcome: string
    /// Wall-clock time the job's terminal status was recorded.
    OccurredAt: DateTimeOffset
}

/// Phase 311 — the receiver's composed clean-room gate reached a decision
/// over one contract answer. Emitted once per gated dispatch by
/// `CleanRoomGate`, whichever way the decision went. Reserved
/// `SourceModule = "_platform.peer"`, the same family as
/// `PeerCallCompleted`, because a suppression is a federation event and an
/// operator reconstructs a call from one trail.
///
/// **Why a distinct case rather than fields on `PeerCallCompletedPayload`.**
/// Same argument Phase 310 made for `PeerJobCompleted`: the call-completed
/// row answers "the receiver dispatched this call and it ended like this",
/// and a gate decision answers "and this is what the privacy floor did to
/// the answer". Widening the existing payload would change the wire shape
/// of every immediate peer call's row for the sake of the minority that are
/// gated; a new case leaves it byte-for-byte identical (GP 11) and lets an
/// operator query suppressions without scanning every call.
///
/// **This is the ONLY place the withhold reason is recorded.** The wire
/// refusal (`PeerCleanRoomWithheld`) carries the template id and nothing
/// else on purpose: the broker's reasons are quantitative ("released cohort
/// 7 is below the k-anonymity floor 10") and a caller able to read them back
/// while varying its query has a counting oracle over the protected data.
/// Receiver-side audit is where that detail belongs, and the Phase 18a
/// audit-transparency contract is the deliberate, caller-scoped opt-in for
/// exposing any of it.
///
/// PII-free on the same terms as the rest of the family: peer ids, a
/// correlation id, an outcome flag, and `SuppressedCells` — which are the
/// *author-chosen bucket labels* of a histogram ("age-25-34", "region-north"),
/// never a cell's value and never an end-user identifier. A deployment whose
/// bucket labels would themselves be identifying has authored a clean-room
/// template that leaks with or without this row.
type PeerCleanRoomDecisionPayload = {
    /// The hosted `contractId` the gated call targeted.
    ContractId: string
    /// The contract method name whose answer was gated.
    MethodName: string
    /// `TemplateId` of the `CleanRoomTemplate` composed for this contract.
    TemplateId: string
    /// `PeerId` of the validated *calling* peer, taken from the derived
    /// `PeerCallContext` — never the self-asserted wire body.
    CallerPeerId: string
    /// The cascade-wide correlation id, so this row joins the
    /// `PeerCallCompleted` row the same call produces.
    RootRequestId: string
    /// `true` when the answer was released (possibly with cells
    /// suppressed); `false` when the whole answer was withheld.
    Released: bool
    /// Labels of the cells dropped by per-cell suppression. Empty on a
    /// withhold (nothing was released) and on an untouched release.
    SuppressedCells: string list
    /// The gate's own explanation — the broker's `Withheld` reason, or the
    /// substrate's reason for overriding a release. Empty on a clean
    /// release. Recorded here and never sent on the wire.
    Reason: string
    /// Wall-clock time the gate decided.
    OccurredAt: DateTimeOffset
}

// ─── Phase 483 — multi-round federation-run audit payloads ─────────────
//
// Emitted by `ToolUp.InterPlatform`'s `IRoundOrchestrator` as an
// iterative cross-party protocol (split-learning rounds, multi-round PSI,
// federated aggregation) advances. Reserved `SourceModule =
// "_platform.peer"`, the same family as `PeerCallCompleted`, because a
// round is a federation event and an operator reconstructs a run from the
// same trail as the calls it fanned out.
//
// The three cases carry the `Federation` qualifier at the F# surface even
// though the phase names them `RoundCompleted` / `ParticipantDropped` /
// `RunAborted`: `RoundEvent` in `ToolUp.InterPlatform` uses those bare
// names for the observer stream, and two DUs one `open` apart sharing
// case names is exactly how a call site silently binds the wrong one.
// The emitted `EventType` discriminators carry the qualifier too — these
// are new events, so there is no pinned legacy wire name to preserve.
//
// All three are PII-free: run / peer ids plus counts and a reason label,
// never a protocol payload (GP 1 — forge owns the round mechanics and
// never reads the content, so it could not audit it even if it wanted to).

/// Phase 483 — one round of a multi-round federated run reached its
/// barrier and its responses were folded. Emitted once per completed
/// round, whatever the dropout outcome.
type FederationRoundCompletedPayload = {
    /// Caller-assigned stable id of the run this round belongs to.
    RunId: string
    /// 1-based round number within the run.
    RoundNumber: int
    /// Participants the round was fanned out to.
    ParticipantCount: int
    /// Participants that answered before the round's effective deadline.
    RespondedCount: int
    /// Participants recorded as dropped for this round.
    DroppedCount: int
    /// Wall-clock time the round's barrier resolved.
    OccurredAt: DateTimeOffset
}

/// Phase 483 — a participant failed to answer a round before its
/// effective deadline (or answered with an error) and the run's
/// `DropoutPolicy` classified it as dropped. One row per dropped
/// participant per round, so every dropout decision is auditable
/// individually rather than as a count.
type FederationParticipantDroppedPayload = {
    /// Caller-assigned stable id of the run.
    RunId: string
    /// 1-based round number the participant dropped out of.
    RoundNumber: int
    /// `PeerId` of the dropped participant.
    PeerId: string
    /// Short, PII-free explanation — the `PeerError` DU case name or the
    /// substrate's own deadline label.
    Reason: string
    /// Wall-clock time the dropout was decided.
    OccurredAt: DateTimeOffset
}

/// Phase 483 — a multi-round run terminated without reaching its
/// completion condition: the dropout policy refused to continue, the
/// consumer's fold aborted, or the run was cancelled. The persisted
/// `RoundState` survives, so an aborted run is resumable.
type FederationRunAbortedPayload = {
    /// Caller-assigned stable id of the run.
    RunId: string
    /// The round the run was in when it aborted (0 before the first
    /// round completed).
    RoundNumber: int
    /// Short, PII-free explanation of the abort.
    Reason: string
    /// Wall-clock time the run aborted.
    OccurredAt: DateTimeOffset
}

// ─── Phase 40 — artefact-signing substrate audit payloads ──────────────
//
// Emitted by the `ToolUp.ArtefactSigning` companion's
// `DefaultArtefactSigner` for the general-purpose detached-JWS signing
// path. Reserved `SourceModule = "_platform.signing"`. Distinct from the
// Phase 30a `_platform.artefacts` family (`ModuleArtefactSigned` /
// `ModuleArtefactVerified` / `ModuleArtefactRejected`), which signs
// module-distribution artefacts against an `ArtifactManifest` — this
// family signs arbitrary byte payloads for compliance non-repudiation.
// Payloads carry the key id + a SHA-256 of the artefact, NEVER the
// artefact bytes or the private-key material.
//
// Phase 625 renamed the 30a family to carry the `Module` qualifier; it
// and this family were previously one letter apart
// (`ArtifactSigned` / `ArtefactSigned`). This family is UNCHANGED — it
// already used the house `artefact` spelling.

module SigningSourceModule =
    /// Reserved `SourceModule` for `ToolUp.ArtefactSigning` audit events.
    /// Filter `IEventStore.ReadBySource` on this constant for the
    /// artefact-signing audit trail.
    [<Literal>]
    let value = "_platform.signing"

/// `IArtefactSigner.Sign` succeeded. Reserved `SourceModule =
/// "_platform.signing"`. PII-free + secret-free: only the key id,
/// algorithm name, and a SHA-256 digest of the signed artefact travel —
/// never the artefact bytes nor any key material.
type ArtefactSignedPayload = {
    /// Actor who invoked the signer. `"system"` for automated signing
    /// pipelines; the authenticated user's id for operator-initiated
    /// signs.
    Actor: string
    /// Active signing-key id the artefact was signed under.
    KeyId: string
    /// `SigningAlgorithm.name` — `"EcdsaP256"` or `"Ed25519"`.
    Algorithm: string
    /// Lowercase-hex SHA-256 of the signed artefact bytes. Lets a
    /// compliance audit prove "this exact artefact was signed under this
    /// key" without the bytes entering the audit trail.
    ArtefactSha256: string
}

/// A new signing key became active for `IArtefactSigner.Sign`, rotating
/// out a prior key (whose public component remains discoverable for
/// archival verification). Reserved `SourceModule = "_platform.signing"`.
/// Emitted by the rotation helper; the in-process default does not
/// auto-rotate, so this fires only on an explicit operator rotation.
type SigningKeyRotatedPayload = {
    /// Actor who triggered the rotation.
    Actor: string
    /// Key id rotated out of active signing. `None` for the very first
    /// key activation (no predecessor).
    OldKeyId: string option
    /// Key id now active for signing.
    NewKeyId: string
    /// `SigningAlgorithm.name` of the new active key.
    Algorithm: string
}

// ─── Phase 41 — data-classification audit payloads ─────────────────────
//
// Emitted by `ClassificationGate` when a classified field is read
// (`AuditOnRead = true`) or written. Reserved `SourceModule =
// "_platform.classification"`. Payload carries entity + field-path +
// classification level + caller, NEVER the field value — the audit trail
// proves "this caller touched this classified field" without itself
// becoming a sink for the sensitive data it guards.

module ClassificationSourceModule =
    /// Reserved `SourceModule` for `IFieldClassifier` / `ClassificationGate`
    /// audit events.
    [<Literal>]
    let value = "_platform.classification"

/// A classified field was read by a caller (emitted when the field's
/// `AuditOnRead` is set). Value-free by design.
type ClassifiedFieldReadPayload = {
    /// Acting `AccessContext.UserId`.
    UserId: string
    /// Entity type the field belongs to.
    EntityName: string
    /// Dotted field path that was read.
    FieldPath: string
    /// `ClassificationLevel.name` of the field.
    Level: string
    /// `true` when the gate's policy redacted the value for this caller;
    /// `false` when the caller was allowed to read it. Lets a reviewer
    /// distinguish "saw the data" from "was denied the data".
    Redacted: bool
}

/// A classified field was written by a caller. Value-free by design.
type ClassifiedFieldWrittenPayload = {
    UserId: string
    EntityName: string
    FieldPath: string
    Level: string
}

/// Phase 188 — a classified field was redacted or blocked at an egress
/// boundary (export payload / RPC response / audit-or-log sink) by the
/// `EgressGate` because the active `EgressPolicy` returned a non-`Allow`
/// decision for its `ClassificationLevel`. Reserved
/// `SourceModule = "_platform.classification"` (same module as the
/// read/write gate). Value-free by design — records *that* a classified
/// field was stopped at egress, never the field value. One row per
/// non-`Allow` decision, so a deny is observable and never silent
/// (GP 12).
type EgressBlockedPayload = {
    /// Acting subject the egress was destined for (`EgressContext.Actor`)
    /// — a recipient user id, peer id, or sink name.
    Actor: string
    /// Entity type the field belongs to.
    EntityName: string
    /// Dotted field path that was redacted / dropped.
    FieldPath: string
    /// `ClassificationLevel.name` of the field.
    Level: string
    /// The gate's decision — `"Redact"` or `"Block"` (`Allow` is never
    /// audited).
    Decision: string
    /// The egress boundary the field was leaving — `"ExportPayload"` /
    /// `"RpcResponse"` / `"AuditSink"` / `"LogSink"` / a custom label.
    Boundary: string
    /// Optional concrete destination label (`EgressContext.Destination`)
    /// — a sink name, a peer URL, a file path. `None` when unspecified.
    Destination: string option
}

// ─── Phase 54 — tenant-lifecycle substrate audit payloads ──────────────
//
// Emitted by `TenantLifecycleAggregator` for the
// provision / deprovision choreography. Reserved
// `SourceModule = "_platform.tenant"`. Metadata-only by design: scope
// id, actor, per-phase hook counts + elapsed — never tenant data, never
// the bytes any hook erased. `TenantDeprovisioned` is the single
// end-of-offboard marker the phase exists to provide; per-hook failures
// surface as `TenantLifecycleHookFailed` rows without aborting the run.

module TenantLifecycleSourceModule =
    /// Reserved `SourceModule` for `ITenantLifecycle` audit events.
    /// Filter `IEventStore.ReadBySource` on this constant for the
    /// tenant-lifecycle audit trail.
    [<Literal>]
    let value = "_platform.tenant"

/// A tenant scope finished provisioning: every registered
/// `ITenantLifecycle.OnProvisioned` hook ran. Counts only — the per-hook
/// disposition lives on the returned `LifecycleSummary`; this row is the
/// durable "provisioning completed" marker.
type TenantProvisionedPayload = {
    /// Tenant scope provisioned.
    ScopeId: string
    /// Operator (Owner / Platform-Admin) who triggered provisioning.
    Actor: string
    /// Total hooks dispatched.
    HooksRun: int
    /// Hooks that returned `Completed`.
    HooksCompleted: int
    /// Hooks that returned `Skipped` (substrate inactive).
    HooksSkipped: int
    /// Hooks that returned `Failed` (run continued regardless).
    HooksFailed: int
    /// Wall-clock for the aggregated run, in milliseconds.
    ElapsedMs: int64
}

/// A tenant scope finished deprovisioning (offboard): every registered
/// `ITenantLifecycle.OnDeprovisioned` hook ran. The single end-of-
/// offboard marker — an operator querying the audit trail for this case
/// gets exactly one row per completed offboard, with the hook counts
/// proving how much cleanup ran.
type TenantDeprovisionedPayload = {
    ScopeId: string
    Actor: string
    HooksRun: int
    HooksCompleted: int
    HooksSkipped: int
    HooksFailed: int
    ElapsedMs: int64
}

/// One lifecycle hook failed during a provision / deprovision run.
/// Per-hook failure does NOT abort the run (the offboard continues so a
/// single misbehaving companion hook can't block crypto-shred / erasure
/// of the rest); the summary records the partial state and one of these
/// rows fires per failed hook for operator triage.
type TenantLifecycleHookFailedPayload = {
    ScopeId: string
    Actor: string
    /// `"Provisioning"` / `"Deprovisioning"` — which phase was running.
    Phase: string
    /// `ITenantLifecycle.Name` of the hook that failed.
    HookName: string
    /// Hook-supplied error text (or the timeout message when the hook
    /// exceeded its per-hook budget). Verbatim — operators read the
    /// hook's own diagnostic.
    Error: string
}

/// Phase 54j — the tenant's data-export archive was durably written as
/// the pre-step of an export-then-erase offboard, BEFORE any erasure
/// hook ran (fail-closed ordering: a failed export aborts the offboard,
/// so this row's presence proves the export committed first). Metadata
/// only — the archive reference, not its contents. Reserved
/// `SourceModule = "_platform.tenant"`.
type TenantDataExportedPayload = {
    ScopeId: string
    Actor: string
    /// Blob container the archive was written to.
    ArchiveContainer: string
    /// Blob path of the durable export archive (content-addressable).
    ArchivePath: string
    /// SHA-256 hex of the archive bytes — lets the departing customer
    /// verify the archive they received.
    ContentHash: string
    /// Number of export segments the archive bundles.
    SegmentCount: int
}

// ─── Phase 54i — offboard confirmation-gate audit payloads ─────────────
//
// Emitted by `PlatformTenantApiHandler` when `TenantOffboardConfirmation`
// is `TokenConfirmation` / `TwoPersonRule`. Reserved
// `SourceModule = "_platform.tenant"` (the same trail as the offboard
// itself). Metadata-only: scope, the requesting/redeeming admin ids, and
// the refusal reason — never tenant data, never the token secret.

/// A confirmation token was minted for a pending offboard
/// (`RequestDeprovisionToken`). The durable record that an admin asked to
/// arm a destructive offboard — the request itself touches no tenant data.
type TenantOffboardConfirmationRequestedPayload = {
    ScopeId: string
    /// Platform-Admin who requested the token.
    RequestedBy: string
    /// Operator-supplied reason for the offboard.
    Reason: string
    /// Token expiry — the window within which the redemption must happen.
    ExpiresAt: System.DateTimeOffset
}

/// A pending offboard's confirmation token was accepted and the
/// destructive offboard proceeded (`DeprovisionTenantConfirmed`). Under
/// `TwoPersonRule`, `ApprovedBy` differs from the original `RequestedBy`.
type TenantOffboardConfirmationApprovedPayload = {
    ScopeId: string
    /// Platform-Admin who requested the token (`ShareTokenClaim.IssuedBy`).
    RequestedBy: string
    /// Platform-Admin who redeemed the token and executed the offboard.
    ApprovedBy: string
}

/// A confirmation-gated offboard was refused at the gate (before any
/// destruction): a token-less destructive call under a confirmation mode,
/// a missing/expired/wrong-scope token, or a same-admin redemption under
/// `TwoPersonRule`. One row per refusal so a blocked teardown is never
/// silent (GP 6).
type TenantOffboardConfirmationRefusedPayload = {
    ScopeId: string
    /// Platform-Admin whose offboard attempt was refused.
    Actor: string
    /// Human-readable refusal cause (`"confirmation required"`,
    /// `"token expired"`, `"token scope mismatch"`,
    /// `"two-person rule: requester cannot self-approve"`, …).
    Reason: string
}

// ─── Phase 54f — scheduled / grace-period offboard audit payloads ──────
//
// Emitted by `PlatformTenantApiHandler` when an offboard is scheduled
// behind a grace window or that pending schedule is cancelled. Reserved
// `SourceModule = "_platform.tenant"`. Metadata-only. The eventual fire
// is recorded by the offboard's own `TenantDeprovisioned` marker, so
// there is no separate "fired" event.

/// A grace-period offboard was scheduled (`ScheduleDeprovision`): the
/// tenant will be deprovisioned at `DueAt` unless cancelled first.
type TenantDeprovisionScheduledPayload = {
    ScopeId: string
    /// Platform-Admin who scheduled the offboard.
    RequestedBy: string
    /// Operator-supplied reason.
    Reason: string
    /// When the offboard fires unless cancelled (UTC).
    DueAt: System.DateTimeOffset
    /// Backing scheduler job id (string-rendered).
    JobId: string
}

/// A pending grace-period offboard was cancelled
/// (`CancelScheduledDeprovision`) before it fired — the tenant survives.
type TenantDeprovisionCancelledPayload = {
    ScopeId: string
    /// Platform-Admin who cancelled the pending offboard.
    CancelledBy: string
    /// The reason the cancelled schedule carried (for the trail).
    Reason: string
}

/// Phase 69h.tail — uniform-shape audit row emitted by the ToolUp.Remoting
/// dispatcher for `[<Audit>]`-annotated API record methods. One payload
/// shape for every annotated method: the dispatcher knows the method
/// name, the resolved subject, the request correlation id, the declared
/// audit kind, and a PII-redacted snapshot of the input record (fields
/// without `[<PiiSafe>]` are `<redacted:TypeName>`). Bespoke per-domain
/// audit cases continue to exist where richer payloads are load-bearing;
/// this case is the structural floor every annotated method gets for free.
type RemotingMethodAuditedPayload = {
    /// Declared audit kind from the `[<Audit "...">]` attribute —
    /// `"MoneyMoved"`, `"PolicyChanged"`, `"PermissionGranted"`, … or
    /// `"Custom:<name>"` for open-vocabulary kinds.
    Kind: string
    /// The invoked API record method's bare name (e.g. `SetOverride`).
    MethodName: string
    /// Resolved subject id from the request's auth context
    /// (`user:{id}` / `team:{tid}:user:{uid}` / `anonymous:{sid}`), or
    /// `"anonymous"` when no auth context resolved.
    SubjectId: string
    /// Request correlation id (Phase 69b.D) for joining against logs +
    /// telemetry of the same request.
    CorrelationId: string option
    /// PII-redacted input-record snapshot. Field name → string value
    /// for `[<PiiSafe>]` fields; `<redacted:TypeName>` otherwise.
    Payload: Map<string, string>
}

/// Phase 443 — a WebAuthn passkey credential was enrolled for a user via
/// the passkey auth companion's registration ceremony. PII-free apart
/// from the platform `UserId` (already the audit actor). The credential
/// id is truncated so the trail correlates without persisting the full
/// authenticator handle. Source-module label `_platform.auth.passkey`.
type PasskeyCredentialRegisteredPayload = {
    UserId: string
    /// First 12 chars of the base64url credential id — correlatable,
    /// non-reversible to the raw authenticator handle.
    CredentialIdPrefix: string
    /// How the registration was authorised: `ExistingSession` /
    /// `Bootstrap` / `PendingInvite` / `OpenRegistration`.
    Grant: string
}

/// Phase 443 — a passkey credential was removed for a user (self-service
/// deregistration or admin revocation). Source-module label
/// `_platform.auth.passkey`.
type PasskeyCredentialRemovedPayload = {
    UserId: string
    /// First 12 chars of the base64url credential id.
    CredentialIdPrefix: string
}

// ─── Phase 449 — model-fit envelope audit payloads ─────────────────────
//
// Every fit run is audited under `_platform.audit` (GP 6) carrying the
// composite key (`CompositeKeyHash`), so an operator can reconstruct the
// full lifecycle of a reproducible fit — started, completed, and any gate
// failures — from the trail alone. PII-free: identity + cardinality only;
// no dataset rows, no artifact bytes, no opaque spec payload travel.

/// A fit run began — the provider was resolved and the composite identity
/// computed. Reserved `SourceModule = "_platform.audit"`.
type ModelFitStartedPayload = {
    /// SHA-256 hex of the run's composite identity (plan D5). Correlates
    /// the started / completed / gate-failed rows for one fit.
    CompositeKeyHash: string
    /// SHA-256 hex of the opaque model spec.
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` of the vintage the fit read.
    DatasetVersion: string
    /// Seed making the fit reproducible.
    Seed: int64
    /// Resolved provider `Kind`.
    ProviderId: string
    /// Resolved provider version — a component of the composite identity.
    ProviderVersion: string
    /// Scope the fit ran under.
    ScopeId: string
}

/// A fit run produced an outcome — diagnostics + gate verdicts persisted.
/// Emitted whether or not gates passed (a failed gate is a verdict, not a
/// failure of the run). Reserved `SourceModule = "_platform.audit"`.
type ModelFitCompletedPayload = {
    CompositeKeyHash: string
    ProviderId: string
    ProviderVersion: string
    /// Number of diagnostics the provider reported. Cardinality only.
    DiagnosticCount: int
    /// Number of gates evaluated against the diagnostics.
    GatesEvaluated: int
    /// Number of those gates that failed (`0` on a clean pass).
    GatesFailed: int
    /// SHA-256 hex of the produced artifact — cross-references the outcome
    /// without the bytes travelling.
    ArtifactHash: string
    ScopeId: string
}

/// One or more diagnostic gates failed on an otherwise-completed fit. A
/// typed, audited verdict — never an exception (acceptance). One row per
/// run with at least one failed gate; the names travel so an operator can
/// see which gates failed without re-reading the outcome. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelFitGateFailedPayload = {
    CompositeKeyHash: string
    ProviderId: string
    /// Names of the gates that failed (the `GateSpec.Name` diagnostic keys).
    FailedGates: string list
    ScopeId: string
}

/// Phase 599 — a fit batch was submitted: N per-item fit jobs enqueued under
/// one correlation id. The single batch-level audit row; each item's run
/// then emits its own Phase 449 fit rows carrying the same batch id in its
/// registration annotations. Reserved `SourceModule = "_platform.audit"`.
type ModelFitBatchSubmittedPayload = {
    /// Caller-supplied batch correlation id — the value per-item outcomes
    /// carry in their `batch.id` registration annotation.
    BatchId: string
    /// Number of fit requests in the batch.
    ItemCount: int
    /// Actor who submitted the batch.
    SubmittedBy: string
    ScopeId: string
}

// ─── Phase 453 — model-registry audit payloads ─────────────────────────
//
// Every registry lifecycle event is audited under `_platform.audit` (GP 6)
// carrying the artifact's composite-key hash (plan D5), so an operator can
// reconstruct an evidence base's governance history — registered, promoted,
// retired, and any refused promotion — from the trail alone. PII-free:
// identity + status only; no diagnostics, no parameter bytes, no opaque spec
// payload travel.

/// A model artifact was registered from a completed fit (plan Stage 4). The
/// first-seen registration only — an idempotent re-register of an existing
/// composite key emits nothing (no state changed). Reserved
/// `SourceModule = "_platform.audit"`.
type ModelArtifactRegisteredPayload = {
    /// SHA-256 hex of the artifact's composite identity (plan D5). Correlates
    /// the registered / transitioned rows for one artifact.
    CompositeKeyHash: string
    /// SHA-256 hex of the opaque model spec.
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` of the vintage the fit read.
    DatasetVersion: string
    /// Resolved provider `Kind`.
    ProviderId: string
    /// Resolved provider version — a component of the composite identity.
    ProviderVersion: string
    /// Lifecycle status at registration (always `"Fitted"` today, carried as
    /// data so a future pre-fit `Draft` path stays wire-compatible).
    Status: string
    /// Actor who registered the artifact.
    RegisteredBy: string
    /// Scope the artifact lives under.
    ScopeId: string
}

/// A model artifact's lifecycle status transitioned (plan Stage 4). Emitted
/// after the new version is persisted. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelArtifactTransitionedPayload = {
    CompositeKeyHash: string
    /// Status the artifact held before the transition.
    FromStatus: string
    /// Status the artifact entered.
    ToStatus: string
    /// Actor who performed the transition.
    ActorUserId: string
    ScopeId: string
}

/// A model artifact lifecycle transition was refused (plan Stage 4 / GP 4).
/// A denied `Approved` promotion from a non-Owner/Admin, or an edge the
/// lifecycle graph forbids — the refusal is itself audit-worthy (repeated
/// denials are a governance-gate signal, like `TeamCreationDenied`).
/// Reserved `SourceModule = "_platform.audit"`.
type ModelArtifactTransitionDeniedPayload = {
    CompositeKeyHash: string
    /// Status the caller attempted to move the artifact into.
    AttemptedStatus: string
    /// Actor whose transition was refused.
    ActorUserId: string
    /// Why the transition was refused (`"requires Owner/Admin"` /
    /// `"illegal transition Fitted → Approved"` / …).
    Reason: string
    ScopeId: string
}

/// Phase 644 — a lifecycle transition JUDGED at the author-agnostic seam,
/// with the author and the channel it arrived on. Reserved
/// `SourceModule = "_platform.audit"`.
///
/// **Why this is a third row and not two more fields on the two above.**
/// Those two are written by the registry, which knows the actor id it was
/// handed and nothing else: it has no way to learn whether the call came
/// from a local admin screen, from a peer deployment across a federation
/// edge, or from a promotion policy, because none of that is in its
/// signature and widening the signature would break every
/// `IModelRegistry` implementation. This row is written by the seam, which
/// is the only place all three are known — so the attribution is recorded
/// where it EXISTS rather than inferred where it does not.
///
/// It is written for an admitted transition **and for a refused one**, so
/// the attributed trail is complete on its own: "which peer tried to
/// approve what, and was told no" is answerable from this event type
/// alone, without joining it to a refusal the registry never saw (a
/// transition refused at the seam never reaches the registry at all).
type ModelArtifactTransitionAttributedPayload = {
    CompositeKeyHash: string
    /// Status the artifact held when the seam judged. Present even on a
    /// refusal, except an `UnknownArtifact` one where there is no
    /// artifact to have a status — `""` there.
    FromStatus: string
    /// Status the author asked the artifact to enter.
    ToStatus: string
    /// Where the invocation entered this deployment: `"local"` or
    /// `"peer"`. A closed two-value vocabulary — a policy verdict is
    /// authored data-side, so it arrives on the local channel and is
    /// distinguished by `AuthorKind`, not by a third channel.
    Channel: string
    /// What kind of author judged: `"user"` / `"peer"` / `"policy"`.
    AuthorKind: string
    /// The author's identity, in the form its kind implies — a user id, a
    /// `{peerId}/{actorId}` pair, or a policy id.
    AuthorId: string
    /// The author's stated reason. `""` when none was given; a rationale
    /// is optional on the wire and this trail does not invent one.
    Rationale: string
    /// Did the transition land? `false` carries `Refusal`.
    Admitted: bool
    /// The seam's refusal, described. `""` on an admitted transition.
    Refusal: string
    ScopeId: string
}

/// Phase 646 — opaque provenance attachments were appended to a model
/// artifact, and (where a promotion was accepted) the acceptance signature
/// recorded. Reserved `SourceModule = "_platform.audit"`.
///
/// **Hashes and media types, never bytes.** The attachment content is
/// opaque by construction — forge does not read it, so an audit trail that
/// carried it would be publishing a payload this deployment cannot
/// characterise into a store with a different retention policy from the
/// artifact's. The digest is what a later investigation actually needs: it
/// resolves to the attachment, or it does not resolve at all, and either
/// answer is the one being asked for.
type ModelArtifactProvenanceAttachedPayload = {
    CompositeKeyHash: string
    /// Digests of the attachments this call ADDED. Empty when the call
    /// only recorded a signature.
    AttachmentHashes: string list
    /// The distinct media types added, in arrival order.
    MediaTypes: string list
    /// How many attachments the artifact holds after the append, and how
    /// many bytes — the two dimensions the declared cap bounds, recorded so
    /// an operator can see an artifact approaching one.
    TotalAttachments: int
    TotalBytes: int
    /// The signing-key id of the acceptance signature recorded by this
    /// call, or the one already held. `""` when the artifact carries none
    /// — an artifact this deployment fitted itself, or a promotion
    /// accepted with no signer composed.
    SigningKeyId: string
    ScopeId: string
}

/// Phase 646 — a promotion transfer JUDGED at the transfer seam: a final
/// artifact, its spec payload and its provenance attachments landing in
/// this deployment's registry as one recorded act. Reserved
/// `SourceModule = "_platform.audit"`.
///
/// **Why this is its own row rather than the attributed transition row
/// plus an attachment row.** A promotion is one act with one outcome, and
/// the question it has to answer later is "did this data host accept this
/// artifact from this peer, and does it still hold what it accepted". Read
/// off two rows written by two layers, that question needs a join on a key
/// neither row was designed to correlate on — and a refused transfer writes
/// no attachment row at all, so the join would silently lose exactly the
/// cases worth finding.
///
/// Written for an accepted transfer AND for a refused one, for the reason
/// `ModelArtifactTransitionAttributedPayload` is: a transfer refused at the
/// seam never reaches the registry, so a trail of successful writes could
/// not answer which peer tried to promote what.
type ModelArtifactPromotionPayload = {
    CompositeKeyHash: string
    /// The lifecycle status the transfer asked the artifact to hold.
    TargetStatus: string
    /// Where the transfer entered this deployment: `"local"` or `"peer"`.
    Channel: string
    /// `"user"` / `"peer"` / `"policy"` — `ModelTransitionAuthor.kind`.
    AuthorKind: string
    AuthorId: string
    /// Digests of every attachment the transfer carried.
    AttachmentHashes: string list
    /// The signing-key id of the acceptance signature. `""` when the
    /// transfer was refused, or accepted with no signer composed.
    SigningKeyId: string
    /// Did the transfer land? `false` carries `Refusal`.
    Accepted: bool
    /// The identical transfer was already held; nothing was written. An
    /// accepted replay, which is a different fact from a first acceptance
    /// and is the one an idempotency question is about.
    Replayed: bool
    /// The seam's refusal, described. `""` on an accepted transfer.
    Refusal: string
    ScopeId: string
}

// ─── Phase 454 — model-scoring audit payloads ──────────────────────────
//
// A scoring run applies a governed artifact (Phase 453) to a new dataset
// vintage (Phase 448), landing predictions as a NEW dataset version. Both
// the success and the typed refusals are audited under `_platform.audit`
// (GP 6) carrying the artifact's composite-key hash, so an operator can
// reconstruct which artifact scored which vintage into which output — and
// which scores were refused and why — from the trail alone. PII-free:
// identity + cardinality only; no dataset rows, no artifact bytes, no
// prediction values travel.

/// A scoring run produced predictions — a new dataset version whose
/// provenance names the scoring artifact + input vintage. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelScoredPayload = {
    /// SHA-256 hex of the scoring artifact's composite identity (plan D5).
    CompositeKeyHash: string
    /// Resolved provider `Kind` that produced the predictions.
    ProviderId: string
    /// Provider version — a component of the artifact's composite identity.
    ProviderVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the input vintage scored.
    InputVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the predictions dataset version
    /// the run wrote.
    OutputVersion: string
    /// Number of prediction rows written. Cardinality only.
    RowCount: int64
    /// Scope the score ran under.
    ScopeId: string
}

/// A scoring run was refused as typed data — the approved-only guard
/// rejected a non-`Approved` artifact (task C / GP 4), the input schema
/// lacked a provider-required column, the input vintage was unavailable, or
/// the provider raised. The refusal is itself audit-worthy (repeated denials
/// are a governance-gate signal, like `ModelArtifactTransitionDenied`).
/// Reserved `SourceModule = "_platform.audit"`.
type ModelScoreRefusedPayload = {
    CompositeKeyHash: string
    /// Resolved provider `Kind` from the artifact's composite identity.
    ProviderId: string
    /// `{scopeId}/{datasetId}@v{version}` of the input vintage the caller
    /// asked to score.
    InputVersion: string
    /// Why the score was refused (the `ScoreError` case name + detail).
    Reason: string
    ScopeId: string
}

// ─── Phase 456 — model-evaluation audit payload ────────────────────────
//
// A holdout-evaluation run scores an artifact (Phase 454) against a holdout
// vintage and stores the provider-computed metric map against the artifact
// (plan Stage 6). The run is audited under `_platform.audit` (GP 6) carrying
// the artifact's composite-key hash + both vintage keys, so an operator can
// reconstruct the out-of-time track record's provenance from the trail
// alone. PII-free: identity + cardinality only; no metric values, no
// dataset rows travel (forge stores metrics in the run record — the audit
// row names the run, it never re-states provider numbers).

/// A holdout-evaluation run stored a provider-computed metric map against a
/// model artifact (plan Stage 6). Reserved `SourceModule = "_platform.audit"`.
type ModelEvaluatedPayload = {
    /// SHA-256 hex of the evaluated artifact's composite identity (plan D5).
    CompositeKeyHash: string
    /// Resolved provider `Kind` that computed the metrics.
    ProviderId: string
    /// Provider version — a component of the artifact's composite identity.
    ProviderVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the holdout vintage evaluated.
    HoldoutVersion: string
    /// `{scopeId}/{datasetId}@v{version}` of the predictions vintage the
    /// scoring leg wrote.
    PredictionsVersion: string
    /// Number of metrics the provider reported. Cardinality only — the
    /// values live in the stored `EvaluationRun`, never on the audit row.
    MetricCount: int
    /// Scope the evaluation ran under.
    ScopeId: string
}

// ─── Phase 645 — promotion-policy audit payloads ───────────────────────
//
// A declared promotion policy judged a newly registered artifact and either
// promoted it, held it for human curation, or refused it. Both rows are
// emitted under `_platform.audit` (GP 6) and together they ARE the
// subscription surface for promotion events — a consumer that wants to
// react attaches an `IAuditSink` rather than a bespoke pub/sub.
//
// PII-free, and metric-value-free: identity, the verdict, and cardinality
// only. The per-metric numbers a verdict rested on live in the stored
// `PromotionDecision`, exactly as `ModelEvaluated`'s live in the stored
// `EvaluationRun` — an audit row names the judgment, it never re-states
// provider numbers.

/// Phase 645 — a promotion policy reached a verdict for a model artifact.
/// Written for EVERY verdict, including a queue (which moves nothing and so
/// leaves no transition row of its own) and including the fail-safe "no
/// policy governed this artifact" case. Reserved
/// `SourceModule = "_platform.audit"`.
type ModelPromotionPolicyEvaluatedPayload = {
    /// SHA-256 hex of the judged artifact's composite identity (plan D5).
    CompositeKeyHash: string
    /// The policy that judged. `""` when no declared policy governed the
    /// artifact — the honest value, rather than a policy id it never had.
    PolicyId: string
    /// The judging policy's declared version. `0` when none governed.
    PolicyVersion: int
    /// Where the judged metrics came from: `"diagnostics"` /
    /// `"latest-evaluation"`, or `""` when no policy governed.
    MetricSource: string
    /// `"AutoPromote"` / `"QueueForCuration"` / `"Reject"`.
    Verdict: string
    /// One-line reason, always populated.
    Reason: string
    /// The currently-approved artifact the candidate was judged against.
    /// `""` when there was no incumbent.
    IncumbentKeyHash: string
    /// How many declared tolerances were evaluated. Cardinality only — the
    /// per-tolerance evidence lives in the stored decision record.
    ToleranceCount: int
    /// Did the verdict's transition land? `false` for a queue (which drives
    /// none) and for one the transition seam refused.
    TransitionApplied: bool
    /// Scope the decision was made under.
    ScopeId: string
}

/// Phase 645 — an auto-promotion displaced a previously promoted artifact.
///
/// **A separate row because supersession is a separate fact.** The promotion
/// itself is already an attributed transition (Phase 644) and the retirement
/// is another, but neither says the two are the same act — and "no refresh
/// ever silently changes what a consumer resolves" is a claim about exactly
/// that link. Reserved `SourceModule = "_platform.audit"`.
type ModelArtifactSupersededPayload = {
    /// The newly promoted artifact's composite-key hash.
    SupersedingKeyHash: string
    /// The artifact it displaced.
    SupersededKeyHash: string
    /// The policy whose verdict justified the supersession.
    PolicyId: string
    /// How many metrics had both an observed and an incumbent value — the
    /// deltas that justified it. Cardinality only; the values live in the
    /// stored decision record.
    MetricCount: int
    /// Did the displaced artifact actually retire? `false` when the
    /// retirement was refused, which leaves two approved artifacts and is
    /// precisely the state an operator must be told about.
    Retired: bool
    ScopeId: string
}

/// Phase 651 — a registration observer raised, and the failure was isolated.
///
/// **The row exists because the isolation is otherwise invisible.** An
/// observer runs after the artifact is durably registered, so its failure
/// changes nothing the registrar can see: the registration returns `Ok`, the
/// caller carries on, and whatever the observer existed to do — apply a
/// promotion policy, notify a downstream — silently did not happen. Swallowing
/// that quietly would make "observe, don't gate" indistinguishable from
/// "observers sometimes do not run". Reserved
/// `SourceModule = "_platform.audit"`.
type ModelRegistrationObserverFailedPayload = {
    /// SHA-256 hex of the registered artifact's composite identity (plan D5).
    /// The registration itself stands — this row is about the observer.
    CompositeKeyHash: string
    /// `IModelRegistrationObserver.Name` of the observer that raised. Naming
    /// it is the whole value of the row: "an observer failed" is not
    /// actionable when several are composed.
    Observer: string
    /// The exception's message, one line. Type + message only — no stack, no
    /// payload values.
    Reason: string
    /// Scope the registration was made under.
    ScopeId: string
}

// --- Phase 482 / 487 — dataset provenance & virtual-spill audit payloads --
//
// Emitted under `_platform.audit`. Identity + cardinality only — no dataset
// rows, no label content values beyond the closed DU shape travel.

/// Phase 487 — an ephemeral materialisation ("spill") of a **virtual**
/// dataset version was written to a retention-bounded scratch blob for
/// compute handoff. Virtual versions read through to the deployment's own
/// stores with no durable copy; a spill is the declared, observable
/// exception to zero-copy — always audited so the copy is never silent.
type DatasetSpillCreatedPayload = {
    /// Actor that requested the handoff materialisation.
    Actor: string
    ScopeId: string
    /// Scratch dataset id the spill landed under.
    SpillDatasetId: string
    /// The virtual version's watermark — its vintage identity.
    Watermark: string
    /// UTC instant after which the spill is eligible for deletion.
    ExpiresAt: DateTime
    /// Rows spilled. Cardinality only.
    RowCount: int64
}

/// Phase 487 — a spill blob was deleted (TTL reached, or explicit cleanup).
/// Closes the spill lifecycle in the trail so a leaked scratch copy is
/// visible by its absence of a matching delete row.
type DatasetSpillDeletedPayload = {
    Actor: string
    ScopeId: string
    SpillDatasetId: string
    /// Why deleted — `"ttl-expired"` / `"explicit"`.
    Reason: string
}

/// Phase 482 — privacy-provenance labels were removed from a dataset version
/// by an explicit admin act (the **only** removal path; labels are otherwise
/// immutable provenance). Declassification writes a new, unlabelled version;
/// this row records the actor, both version numbers, and the justification so
/// the removal is accountable (GP 4 / GP 6).
type DatasetDeclassifiedPayload = {
    /// Owner / Admin who declassified.
    Actor: string
    ScopeId: string
    DatasetId: string
    /// The labelled version whose labels were cleared.
    FromVersion: int
    /// The new, unlabelled version created by the declassify.
    ToVersion: int
    /// Count of labels removed. Cardinality only.
    LabelCount: int
    /// Operator-supplied justification.
    Reason: string
}

/// Phase 482 — a label-carrying dataset version was refused a dispatch or a
/// raw export by an enabled data-provenance policy (GP 4 / GP 6). A typed,
/// audited denial — repeated denials are a governance-gate signal, like
/// `ModelArtifactTransitionDenied`.
type DatasetPolicyDeniedPayload = {
    ScopeId: string
    DatasetId: string
    Version: int
    /// Which policy fired — `"dispatch"` (labelled data needs Isolated
    /// compute) or `"export"` (raw export of label-carrying content).
    Policy: string
    /// Human-readable refusal reason.
    Reason: string
}

/// Phase 601 — an assembly re-vintage ran: the spec recorded on a produced
/// version was re-executed against current sources, landing new immutable
/// version(s). One row per replay carrying the spec ref + the produced
/// versions (GP 6). Reserved `SourceModule = "_platform.audit"`.
type DatasetRevintagedPayload = {
    /// SHA-256 hex of the re-bound spec that actually ran.
    SpecHash: string
    /// `{scopeId}/{datasetId}@v{version}` key of the spec-carrying version
    /// the replay was triggered from.
    SourceVersion: string
    /// `{scopeId}/{datasetId}@v{version}` keys of the produced version(s),
    /// one per subset.
    ProducedVersions: string list
    /// Actor (or the job's system principal) that requested the replay.
    RequestedBy: string
    ScopeId: string
}

/// SDK-standard audit event types. The DU case name is the wire-format
/// `EventType` discriminator string; payload records are JSON-serialised
/// into `ModuleEvent.Payload` via `FableConverters` (matches the
/// existing `WebhookApiHandler` / `KnowledgeBase` audit emission idiom).
// ─── Phase 7b — schema-first user-authoring audit payloads ───────────
//
// Emitted by the `IUserSchemaApi` handler + `BlobUserSchemaStore` on
// schema lifecycle transitions. Reserved source-module label
// `_platform.user_schema`. PII-free: identifiers + cardinality + version
// provenance only — never the schema's field values or instance data.

/// Reserved `SourceModule` for schema-first user-authoring audit events.
/// Filter `IEventStore.ReadBySource` on this constant for the trail.
module UserSchemaSourceModule =
    [<Literal>]
    let value = "_platform.user_schema"

/// Phase 7b — the AI proposed a candidate user-authored schema for a
/// scope, surfaced for human review. Emitted by the AI-propose flow
/// (which lives in the consuming application, not the substrate); the
/// substrate ships the payload so an approved proposal's provenance is
/// durable end-to-end.
type SchemaProposedPayload = {
    /// Actor the proposal is attributed to (the user in the conversation).
    UserId: string
    /// Scope the proposed schema targets.
    ScopeId: string
    /// Schema id of the proposal.
    SchemaId: string
    /// Human-facing version label of the proposal.
    VersionLabel: string
    /// Conversation the AI proposal originated from — the proposal→approval
    /// trace anchor.
    ConversationId: string
    /// Number of fields in the proposal. Cardinality only.
    FieldCount: int
}

/// Phase 7b — a user approved a committed schema version whose provenance
/// was `AuthoredBy.AIWithApproval`. Emitted by the store at commit time.
type SchemaApprovedPayload = {
    /// Approving actor.
    UserId: string
    /// Scope the schema belongs to.
    ScopeId: string
    SchemaId: string
    /// Store-assigned numeric version of the approved commit.
    Version: int
    VersionLabel: string
    /// `AuthoredBy` projected to a string (`"Human"` /
    /// `"AIWithApproval:{conversationId}"`).
    ProposedBy: string
    /// Originating conversation id when the commit was AI-proposed.
    ConversationId: string option
}

/// Phase 7b — a user-authored schema version was created, updated,
/// migrated, or deleted. Emitted by the store on every committed state
/// change (GP 6). Identifiers + cardinality only.
type SchemaChangedPayload = {
    /// Actor who triggered the change (`"system"` for job-driven runs).
    UserId: string
    ScopeId: string
    SchemaId: string
    /// Store-assigned numeric version after the change (`0` for a delete).
    Version: int
    VersionLabel: string
    /// One of `"Created"` / `"Updated"` / `"Migrated"` / `"Deleted"`.
    ChangeKind: string
    /// The predecessor schema id when this is an evolution; `None` for a
    /// fresh authoring or a non-evolution edit.
    EvolvedFrom: string option
    /// Number of migration steps applied (0 for a plain save).
    MigrationsApplied: int
    /// Number of stored instances transformed by a migration (0 for a
    /// plain save).
    InstancesMigrated: int
}

/// Phase 320 — an external-compute completion callback was accepted and
/// its handle resolved (or found already resolved). Emitted on **every**
/// resolution, including the idempotent duplicate (GP 6): "this handle
/// was resolved twice and the second was a no-op" is exactly the fact an
/// incident reconstruction needs, and an audit trail that records only
/// the first cannot distinguish a well-behaved retrying backend from a
/// forged replay.
///
/// Carries no secret and no payload — identifiers, the outcome label, and
/// what the platform did with it.
type ExternalCallbackResolvedPayload = {
    /// `ExternalHandle.HandleId` the callback named.
    HandleId: string
    /// `ExternalHandle.Backend` from the stored record — the platform's
    /// own view of which backend owns the work, never the caller's claim.
    Backend: string
    /// Scope the handle was submitted under, from the stored record.
    ScopeId: string
    /// `JobRun.RunId` the handle routed to.
    JobRunId: string
    /// Terminal outcome the callback reported (`ExternalOutcome.label`).
    Outcome: string
    /// What the platform did: `"resolved"` (this callback won the
    /// terminal claim and drove the run), `"already-resolved"` (a
    /// duplicate, or the reconciliation poll got there first — no-op),
    /// `"no-awaiting-run"`, `"scope-mismatch"`, `"sink-not-configured"`.
    Resolution: string
    /// Terminal run status written, when this callback drove the run
    /// (`"succeeded"` / `"failed"` / `"dead-lettered"` /
    /// `"externally-cancelled"`); `None` for every non-`"resolved"`
    /// resolution.
    RunStatus: string option
    /// Phase 486 — the **verified** worker identity that produced this
    /// outcome. `None` when no signature was presented (or the deployment
    /// has not composed signed-outcome verification), so the field
    /// distinguishes "unattributed" from "attributed to X" and never
    /// asserts an unverified claim: a presented-but-unverified signature
    /// refuses the callback rather than reaching this event.
    ///
    /// This is where per-worker attribution is *recorded* — the audit
    /// trail is the queryable surface, since `IExternalCompletionSink`
    /// cannot gain a field without breaking every implementation of it.
    WorkerId: string option
    /// Phase 486 — which of that worker's registered keys signed. Present
    /// exactly when `WorkerId` is.
    WorkerKeyId: string option
    /// Phase 486 — the algorithm the signature verified under
    /// (`WorkerKeyAlgorithm.label`), taken from the REGISTERED key and
    /// never from the request.
    SignatureAlgorithm: string option
    /// Phase 486 — the verified digest of the outcome the worker signed
    /// (`SignedOutcomeVerifier.artifactHash`). The provenance link between
    /// this audit row and the artefact the worker committed to.
    ArtifactHash: string option
    OccurredAt: DateTimeOffset
}

/// Phase 320 — an external-compute completion callback was REFUSED.
///
/// A distinct event from `ExternalCallbackResolved` rather than a
/// `Resolution` value on it, because the two answer different questions
/// and are read by different people: resolutions are operational history,
/// refusals are a **forged-callback signal** an operator wants to alert
/// on. Folding them into one kind means the alert query has to filter on
/// a payload field, and the same reasoning gave `BeaconRejected` its own
/// case rather than a flag on the beacon event.
///
/// `HandleId` is a `string option` because the most suspicious refusals
/// are the ones whose body did not parse at all.
type ExternalCallbackRejectedPayload = {
    /// The handle the caller named, when the body parsed far enough to
    /// carry one.
    HandleId: string option
    /// Why, internally: `"malformed-body"`, `"missing-secret"`,
    /// `"unknown-handle"`, `"secret-mismatch"`, `"scope-mismatch"`,
    /// `"non-terminal-status"`, `"throttled"` — plus, from Phase 486's
    /// signed-outcome gate, the `"signature-*"` family
    /// (`SignedOutcomeRejection.label`: `"signature-required"`,
    /// `"signature-malformed-envelope"`, `"signature-unknown-key"`,
    /// `"signature-key-not-approved"`, `"signature-key-revoked"`,
    /// `"signature-artifact-mismatch"`,
    /// `"signature-unparseable-timestamp"`,
    /// `"signature-stale-timestamp"`, `"signature-invalid"`). The HTTP
    /// response is uniform — this field is the part that is not (the Phase
    /// 232 encryption-admin posture).
    ///
    /// `"signature-artifact-mismatch"` is the one to alert on hardest: it
    /// means a signature that may well be genuine arrived over a result it
    /// does not cover, which is what a substituting relay looks like.
    Reason: string
    /// Remote address the refusal came from, for correlation with the
    /// rate-limited warning. `"unknown"` when the connection reports
    /// none.
    ClientIp: string
    OccurredAt: DateTimeOffset
}

/// Phase 657 — the verdict the boot-time composition verification reached,
/// recorded once per process start.
///
/// **Recorded on every verdict, including the affirmative one.** A record
/// written only when something is wrong cannot distinguish "verified" from
/// "the check never ran" after the fact, and those are the two states an
/// operator most needs to tell apart. The row is one per start, so the
/// volume is bounded by restarts rather than by traffic.
///
/// **PII-free by construction.** Every field is a composition fact — a
/// component id, a config-knob name, a digest — none of which carries user
/// data. Findings are the substrate's own rendered strings, never
/// caller-supplied text.
type CompositionVerificationRecordedPayload = {
    /// Stable verdict label: `"verified"`, `"unverified"`, `"unsealed"`,
    /// or `"drifted"`. Machine-readable; the free-text account is
    /// `Summary`.
    Verdict: string
    /// Composition profile the deployment started under: `"standard"` or
    /// `"verified"`.
    Profile: string
    /// The policy that decided what a non-affirmative verdict does:
    /// `"log-and-serve"` or `"refuse-on-drift"`.
    Policy: string
    /// Whether this verdict refused the process a start. `false` under the
    /// log-and-serve default even when the verdict is not `"verified"` —
    /// which is exactly the pair of facts an operator rolling the policy
    /// forward wants to read together.
    RefusedStart: bool
    /// One rendered line per finding, each naming what moved or what
    /// failed. Empty on an affirmative verdict.
    Findings: string list
    /// One-line human-readable account of the verdict.
    Summary: string
    OccurredAt: DateTimeOffset
}

/// Phase 657 — a composed component was refused a capability beyond the
/// envelope its composition declared.
///
/// The refusal the mandatory capability gate produces under the verified
/// composition profile. Emitted through `IAuditLog` like any other event,
/// so whichever sinks a deployment composed record it and the substrate
/// takes no dependency on which those are.
type CompositionCapabilityRefusedPayload = {
    /// The composed component that attempted the access — the raw
    /// `ComponentId` value.
    Component: string
    /// The capability the attempted operation required, rendered as
    /// `effect/determinism/readiness`.
    Required: string
    /// The envelope the component declared, same rendering. The identity
    /// (`pure/deterministic/distributed-ready`) for an undeclared
    /// component — which is what makes an undeclared component's effecting
    /// access a refusal rather than a pass.
    Declared: string
    /// The gate's own reason, verbatim: the component, the axes it
    /// exceeded, and the remedy.
    Reason: string
    /// Composition profile in force when the refusal happened.
    Profile: string
    OccurredAt: DateTimeOffset
}

/// Phase 680 — one numeric token from a verified answer, with the
/// fact-match status the answer-verification gate reached for it.
///
/// PII-free by construction: the token is a figure the answer already
/// stated, `Canonical` is that figure normalised, and `MatchedFactId` is a
/// content-addressed fact id. No prose, no principal, no free text.
type AnswerVerificationTokenAudit = {
    /// The numeric token exactly as it appeared in the answer.
    Token: string
    /// The canonical decimal value it normalised to (invariant string).
    /// Empty when the token carried no parseable numeric core.
    Canonical: string
    /// The verdict reached for this token: `"verified"`, `"unmatched"`, or
    /// `"no-facts-in-scope"`.
    Verdict: string
    /// The fact this token verified against. `Some` only on a `"verified"`
    /// token whose matching fact carried an id.
    MatchedFactId: string option
}

/// Phase 680 — the answer-verification verdict for one served answer, and
/// the joins from that runtime row to the provenance the answer stands on.
///
/// **Recorded on the affirmative verdict too**, the Phase 657 discipline:
/// a row written only when a figure went unverified cannot distinguish a
/// clean answer from an answer the gate never saw, and those are the two
/// states an auditor most needs to tell apart. One row per verified answer,
/// so the volume is bounded by answered turns rather than by tokens.
///
/// **Emitted BESIDE the existing `IEventStore` trail, not instead of it.**
/// The per-unmatched-token `IEventStore` records remain the module-scoped
/// query surface; this row is the one that rides `IAuditLog`, so whichever
/// sinks a deployment composed — a hash-chained ledger among them — record
/// it, and the answer path depends on none of them.
///
/// **Every join field is optional, and absence is honest.** A deployment
/// that composes no certificates and starts from no sealed composition
/// records `None` for both rather than a placeholder; a placeholder would
/// be a claim, and this row makes none it cannot support.
type AnswerVerificationPayload = {
    TaskId: Guid
    ConversationId: Guid
    /// Gate mode in force: `"Annotate"` or `"Strict"`. An `Off` gate runs
    /// no verification and records no row at all.
    Mode: string
    /// Numeric tokens that matched a retrieved fact.
    Verified: int
    /// Numeric tokens with no matching fact while facts WERE in scope —
    /// the anti-hallucination signal.
    Unmatched: int
    /// Numeric tokens the turn had no facts to check against.
    Unverifiable: int
    /// How many facts were in scope for the turn. `0` is why a token can be
    /// unverifiable without being unmatched.
    FactsInScope: int
    /// Per-token verdicts, in the answer's reading order.
    Tokens: AnswerVerificationTokenAudit list
    /// The distinct fact ids this answer's verified figures cite, sorted.
    /// The walk from this row into the fact tier.
    CitedFactIds: string list
    /// SHA-256 over the canonical join of `CitedFactIds` — a
    /// deployment-independent head for the provenance chain this answer
    /// stands on, recomputable by anyone holding the ids. `None` when the
    /// answer verified against no fact.
    ProvenanceChainHead: string option
    /// The grounding certificate covering this answer's chain, when the
    /// deployment holds one. `None` when it issues no certificates.
    CertificateRef: string option
    /// The sealed-composition identity this process affirmed at boot, when
    /// it started under a verified profile. `None` under an unsealed start
    /// or a non-affirmative verdict — naming a seal for a composition the
    /// boot check declined to affirm would assert exactly what it refused.
    CompositionSealId: string option
    ProviderName: string
    ProviderModel: string
    OccurredAt: DateTimeOffset
}

/// Phase 680 — the deployment-side anchors an answer-verification audit
/// row joins to.
///
/// Neither anchor is derivable inside the answer path: the composition seal
/// is a boot-time fact, and a certificate is issued by a substrate the
/// answer tier holds no dependency on. Both therefore arrive as data
/// through this seam, which **nothing composes by default** — absent, both
/// anchors resolve to `None` and the recorded row says so (GP 11 / GP 13).
///
/// **GP 12.** Identity by value (ids and strings, never live handles);
/// async at the boundary that may do I/O; stateless between calls — every
/// input arrives as a parameter.
type IAnswerProvenanceAnchors =
    /// The sealed-composition identity this process affirmed at boot.
    /// A property rather than a call: it is a process constant, fixed
    /// before the first answer is served.
    abstract CompositionSealId: string option

    /// The certificate ref covering this answer's provenance chain, when
    /// the deployment already holds one. Implementations REPORT what
    /// exists; issuing a certificate here would put a signing round-trip
    /// on every answered turn. `None` whenever none was issued.
    abstract TryCertificateRef:
        scopeId: string * conversationId: Guid * citedFactIds: string list -> Async<string option>

/// Phase 683 — one attempt to import a fact from a peer deployment under a
/// grounding certificate, accepted or refused.
///
/// **Recorded on the accepted verdict too**, the Phase 657 / 680
/// discipline: a trail carrying only refusals cannot distinguish a
/// deployment whose imports were all sound from one whose import door was
/// never composed, and those are the two states an auditor most needs to
/// tell apart.
///
/// **PII-free by construction.** Identifiers, a metric id, a rendered
/// subject reference, and disclosure stances. The imported fact's VALUE
/// never rides this row — nor does it appear in the certificate, which
/// carries chain structure only.
///
/// **Both stances are recorded because the pair is the claim.** `Declared`
/// is what the peer sealed into its certificate; `Effective` is what the
/// fact landed under. An import may narrow and may never widen, so a row
/// where `Effective` is more permissive than `Declared` is a defect
/// visible from the trail alone, with no access to the door's code.
type FactImportPayload = {
    /// The peer whose key material the certificate was checked against —
    /// the name the importing deployment composed the anchor under, not a
    /// value read out of the offered document.
    PeerId: string
    /// The signing-key id the peer's anchor names.
    PeerKeyId: string
    /// The root the peer's certificate is issued over. Empty when the
    /// certificate could not be read at all.
    CertificateRoot: string
    /// The content-addressed reference recorded as the imported fact's
    /// provenance (`MethodRef.Imported`). Empty on a refusal that never
    /// reached a readable certificate.
    CertificateRef: string
    /// The content-addressed fact id the door re-derived from the offered
    /// identity tuple — the value compared against `CertificateRoot`.
    DerivedFactId: string
    /// The id of the fact actually asserted locally. Differs from
    /// `DerivedFactId` by construction: the local assertion's method is
    /// `Imported`, which participates in the content address. Empty on
    /// every refusal path, where nothing was asserted.
    ImportedFactId: string
    /// Readable subject reference (`hierarchy/level>level`).
    Subject: string
    /// Registered metric id of the offered fact.
    Metric: string
    /// The stance the peer sealed into the certificate
    /// (`Surfaceable` / `Internal` / `Restricted(policy)`). Empty when the
    /// certificate was never read.
    DeclaredDisclosure: string
    /// The stance the import landed under — the conservative floor of the
    /// declared stance and the anchor's ceiling. Never wider than
    /// `DeclaredDisclosure`. Empty on a refusal.
    EffectiveDisclosure: string
    /// The attestation level the peer's certificate claims, as its stable
    /// wire name — present only when the offered document was the
    /// levels-bound projection, and recorded on a refusal on level grounds
    /// as well as on an accepted import.
    ///
    /// **Empty means the document claimed no level, never that it claimed
    /// the weakest one.** A certificate carrying a detached seal makes no
    /// statement about the signing key's custody at all, and defaulting
    /// this field to a level nobody claimed would put an assertion into the
    /// trail that no signature covers — which is the one thing an audit row
    /// must never do.
    AttestationLevel: string
    /// The typed refusal, rendered. Empty on an accepted import.
    Reason: string
    OccurredAt: DateTimeOffset
}

/// Phase 684 — one grounding-envelope mutation that landed through the
/// audited choke point.
///
/// **Before and after, as digests, on the same row.** A mutation record
/// carrying only the new state proves nothing about what it replaced, so a
/// chain of them cannot be walked. With both digests present, `seal +
/// recorded chain ⇒ current envelope` is a computation an auditor can
/// perform from the trail alone — which is the whole point of routing the
/// mutation through a door rather than trusting that nobody moved.
///
/// **Recorded on the clean mutation too**, the Phase 657 / 680 / 683
/// discipline: a trail carrying only the anomalous mutations cannot
/// distinguish a deployment whose grounding envelope moved lawfully from
/// one whose door was never composed.
///
/// **Identifiers only.** Component ids, facet labels, digests, and the
/// operator principal the audit trail already records elsewhere. No
/// declared value, no fact value, no caller-supplied data rides this row.
type GroundingEnvelopeMutatedPayload = {
    /// Which facet of the declared grounding envelope moved:
    /// `"metric-registration"`, `"subject-registration"`,
    /// `"purpose-declaration"`, `"canonical-method"`, or
    /// `"disclosure-policy"`.
    Facet: string
    /// The declaration's subject — the metric id, subject-hierarchy id,
    /// purpose id, or egress-surface name the mutation concerned.
    Subject: string
    /// Lowercase-hex digest of the canonical grounding envelope as it
    /// stood BEFORE this mutation.
    BeforeDigest: string
    /// Lowercase-hex digest of the envelope AFTER it.
    AfterDigest: string
    /// Position of this mutation in the chain, counting from 1. The
    /// position a continuity divergence is reported at.
    Sequence: int
    /// Composition profile in force: `"standard"` or `"verified"`.
    Profile: string
    /// The principal that asked for the mutation.
    Principal: string
    /// The reason the caller stated. Free-form operator text.
    Reason: string
    /// Findings that WOULD have refused this mutation under the verified
    /// profile, recorded rather than enforced because the deployment is
    /// running `standard`. Empty on a clean in-path mutation, and always
    /// empty under `verified` — there the mutation was refused instead.
    /// The same log-then-refuse adoption ladder Phase 657's
    /// `LogAndServe` → `RefuseOnDrift` policy offers.
    Observations: string list
    OccurredAt: DateTimeOffset
}

/// Phase 684 — a grounding-envelope mutation was refused at the choke
/// point and nothing moved.
///
/// Its own event type rather than a flag on the mutation row, for the
/// reason Phase 683's import pair records: the discriminator is what a
/// SIEM rule and a chained-ledger query cut on, and folding it into the
/// payload puts that cut where neither can reach it without decoding
/// every row.
type GroundingMutationRefusedPayload = {
    /// The facet the refused mutation claimed. Same vocabulary as
    /// `GroundingEnvelopeMutatedPayload.Facet`.
    Facet: string
    /// The subject the refused mutation claimed.
    Subject: string
    /// The digest the recorded chain proves the envelope should stand at.
    ChainedDigest: string
    /// The digest actually observed — of the live envelope for an
    /// out-of-path drift, or of the baseline the caller presented for a
    /// stale request.
    ObservedDigest: string
    /// One rendered line per refusal reason, each naming its subject.
    Reasons: string list
    /// Composition profile in force. Always `"verified"` — the standard
    /// profile records observations on the mutation row and refuses
    /// nothing.
    Profile: string
    /// The principal whose mutation was refused.
    Principal: string
    OccurredAt: DateTimeOffset
}

/// Phase 685 — one grounding certificate was issued.
///
/// **The issuance log is what makes a certificate enumerable.** A holder
/// has always been able to verify the certificate in their hand; nobody
/// could ask the other question — *what has this deployment certified?* —
/// and a certificate that was issued and then quietly disowned left no
/// trace at all. One row per issuance turns the audit trail into the
/// deployment's own certificate log, and under a chained ledger that log
/// is tamper-evident: a suppressed issuance is a break the chain verifier
/// positions, not an absence nobody can see.
///
/// **Identifiers only — never the body.** A digest, the subject content
/// id, the signing-key id, and which seal was used. That is deliberate
/// and it is the whole reason this row is safe to keep: the certificate
/// body carries a provenance chain filtered through the disclosure
/// predicate, and copying any of it onto an audit row would move that
/// content to a surface the predicate never ran at. The digest is
/// sufficient for inclusion — a holder recomputes it from the bytes they
/// hold — and insufficient for anything else, which is exactly the
/// property wanted.
type CertificateIssuedPayload = {
    /// Lowercase-hex SHA-256 over the certificate's canonical signed bytes
    /// — the same digest a holder recomputes from the document they hold,
    /// so an inclusion check needs nothing from the issuer to run.
    Digest: string
    /// The subject the certificate is issued over: the answer message id
    /// or the fact content id at the chain root.
    Subject: string
    /// The signing-key id bound into the signed body. Names WHICH key
    /// sealed it, so a rotation leaves the log still readable.
    KeyId: string
    /// Which issue path sealed it: `"detached-jws"` (the direct
    /// `IArtefactSigner` path) or `"application-seal"` (the attested path,
    /// whose envelope also carries the purpose and attestation level).
    /// A discriminator rather than a lookup, because the two make
    /// different claims and an enumerator should not have to fetch the
    /// document to tell them apart.
    Seal: string
    /// The certificate interchange format version the body declared.
    Format: string
    OccurredAt: DateTimeOffset
}

/// Phase 686 — one run of the deployment verification report.
///
/// **Verification leaves a trace without mutating anything.** The report
/// reads five verifiers and writes nothing back; this row is the only
/// artefact it produces. Recording it turns "who has checked this
/// deployment, and what did it say when they did" into a question the
/// trail answers — and under a chained ledger the answer is
/// tamper-evident, so a run whose findings someone would rather nobody
/// saw cannot be quietly removed.
///
/// **The digest, not the report.** The row carries the verdict digest
/// and the per-section verdict labels, never the section detail. That is
/// deliberate: the detail names ledger positions, envelope digests and
/// certificate counts — a deployment-wide evidence summary — and the
/// audit trail has its own readership and its own export paths. The
/// digest is sufficient to prove two runs said the same thing, and
/// insufficient to be a second copy of the report.
///
/// **Recorded on every outcome, including the clean one.** A row written
/// only when something failed cannot distinguish a deployment nobody
/// checked from one that was checked and was fine — the Phase 657
/// discipline, and those are the two states an assessor most needs to
/// tell apart.
type DeploymentVerifiedPayload = {
    /// Who ran the report.
    Actor: string
    /// Top-line outcome label: `"nothing-composed"`,
    /// `"all-composed-verified"`, `"partially-verified"` or
    /// `"failures-present"`.
    Outcome: string
    /// SHA-256 over the report's canonical form — the verdict SET, with
    /// the clock and the actor excluded, so two runs against an unchanged
    /// deployment produce the same digest and drift is visible as a
    /// change rather than inferred from prose.
    VerdictDigest: string
    /// One `"<section-id>=<verdict-label>"` entry per section, in report
    /// order. The shape a SIEM rule cuts on without parsing the report.
    Sections: string list
    /// The process exit code this run would return in CI: non-zero when
    /// any composed section was failed or unreadable.
    ExitCode: int
    OccurredAt: DateTimeOffset
}

/// Phase 713 — one walk of the evidence chain.
///
/// **Producing evidence itself leaves evidence.** The walk reads seven
/// joins and writes nothing back (GP 6); this row is the only artefact
/// it produces. Recording it turns "who has traced this deployment back
/// to the work that authored it, and what did the chain say when they
/// did" into a question the trail answers — and under a chained ledger
/// the answer is tamper-evident, so a walk whose breaks someone would
/// rather nobody saw cannot be quietly removed.
///
/// **The digest and the link labels, never the chain.** The row carries
/// the verdict digest and one label per hop, never the hop detail. The
/// detail names record ids, closure digests and ledger positions — a
/// deployment-wide evidence summary — and the audit trail has its own
/// readership and its own export paths. The digest is sufficient to
/// prove two walks said the same thing, and insufficient to be a second
/// copy of the chain.
///
/// **Recorded on every outcome, including the complete one.** A row
/// written only when a hop broke cannot distinguish a deployment nobody
/// traced from one that was traced and was whole — and those are the two
/// states a reader most needs to tell apart.
type EvidenceChainWalkedPayload = {
    /// Who walked.
    Actor: string
    /// Top-line outcome label: `"chain-unrecorded"`, `"chain-complete"`,
    /// `"chain-partial"` or `"chain-broken"`.
    Outcome: string
    /// SHA-256 over the chain's canonical form — the LINK SET, with the
    /// clock and the actor excluded, so two walks against an unchanged
    /// deployment produce the same digest and drift is visible as a
    /// change rather than inferred from prose.
    VerdictDigest: string
    /// One `"<hop-id>=<link-label>"` entry per hop, in walk order. The
    /// shape a SIEM rule cuts on without parsing the chain. Always the
    /// same length, whatever the deployment composes.
    Hops: string list
    OccurredAt: DateTimeOffset
}

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
    /// Phase 22b — one replica evicted its cached copy of a key another
    /// replica destroyed. Emitted per receiving replica by
    /// `PerScopeKeyResolver`'s `KeyDestroyed` subscription handler, so the
    /// trail proves the crypto-shred reached the whole fleet rather than
    /// only the replica that served the admin request. The originating
    /// replica records `EncryptionKeyDestroyed` and does not
    /// self-acknowledge.
    | EncryptionKeyDestroyAcknowledged of EncryptionKeyDestroyAckPayload
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
    /// Phase 10g — OAuth 1.0a access-token connection established.
    | OAuth1aConnected of OAuth1aConnectedPayload
    /// Phase 10g — OAuth 1.0a connection disconnected.
    | OAuth1aDisconnected of OAuth1aDisconnectedPayload
    /// Phase 10g — OAuth 1.0a per-call request signing failed.
    | OAuth1aSigningFailed of OAuth1aSigningFailedPayload
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
    /// Phase 528 — `ISessionRegistry.Revoke` succeeded on one session.
    /// The actor is the caller who revoked; the subject is the session's
    /// owner. Recorded under the session's own scope, so a team's trail
    /// carries its members' revocations.
    | SessionRevoked of SessionRevokedPayload
    /// Phase 528 — `ISessionRegistry.RevokeAllForUser` succeeded:
    /// sign-out-everywhere, or an administrator cutting a user off
    /// wholesale. Distinct from a burst of `SessionRevoked` rows because
    /// the INTENT differs, and an alerting rule that cares about mass
    /// revocation should not have to infer it from a count.
    | AllSessionsRevoked of AllSessionsRevokedPayload
    /// Phase 527 — `IServiceAccountStore.Create` succeeded. Reserved
    /// `SourceModule = "_platform.audit.service_accounts"`.
    | ServiceAccountCreated of ServiceAccountCreatedPayload
    /// Phase 527 — a machine principal's declared authority ceiling
    /// changed. Records both the prior and the new module set.
    | ServiceAccountPermissionsChanged of ServiceAccountPermissionsChangedPayload
    /// Phase 527 — a scoped API token was minted. Carries the token's
    /// public id and expiry; the secret is never recorded anywhere.
    | ServiceAccountTokenMinted of ServiceAccountTokenMintedPayload
    /// Phase 527 — a scoped API token was permanently revoked.
    | ServiceAccountTokenRevoked of ServiceAccountTokenRevokedPayload
    /// Phase 527 — a machine principal was disabled or re-enabled.
    /// Disabling refuses every one of its tokens wholesale.
    | ServiceAccountStatusChanged of ServiceAccountStatusChangedPayload
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
    /// Phase 451 — a compute submission was refused by the scope's
    /// compute budget, at either enforcement point. Always recorded:
    /// the work did not happen and the reason is a policy decision.
    | ComputeBudgetDenied of ComputeBudgetDeniedPayload
    /// Phase 451 — a compute submission was admitted while newly at or
    /// past the period allowance's warning threshold. Emitted once per
    /// crossing, not per submission.
    | ComputeBudgetWarning of ComputeBudgetWarningPayload
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
    /// A Platform Admin archived a team (`TeamApi.ArchiveTeam`).
    /// Reversible — data retained, team hidden from members.
    | TeamArchived of TeamArchivedPayload
    /// A Platform Admin restored an archived team (`TeamApi.RestoreTeam`).
    | TeamRestored of TeamRestoredPayload
    /// A Platform Admin irreversibly deleted a team
    /// (`TeamApi.DeleteTeamHard`) — record + membership rows purged.
    | TeamDeleted of TeamDeletedPayload
    /// Phase 304 — team ownership transferred (`TeamApi.TransferOwnership`).
    /// Outgoing Owner demoted to `Admin`, incoming member promoted to
    /// `Owner`. Recorded under the `team-{TeamId}` scope.
    | TeamOwnershipTransferred of TeamOwnershipTransferredPayload
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
    /// Phase 547 — an email-keyed pending invite expired unconsumed and
    /// was swept from the store. One row per dropped entry, recorded
    /// under `team-{TeamId}` scope. Makes the previously-silent expiry
    /// sweep observable so an operator can see (and re-issue) an invite
    /// that lapsed before the invitee signed in (GP 6).
    | TeamInviteExpired of TeamInviteExpiredPayload
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
    ///
    /// Phase 625: renamed from `ArtifactSigned`. Wire `EventType`
    /// remains `"ArtifactSigned"` — do not "tidy" it to match the case
    /// name; see the decision record at `AuditLog.auditEventCodecs`.
    | ModuleArtefactSigned of ModuleArtefactSignedPayload
    /// Phase 30a — `IArtifactVerifier.Verify` returned
    /// `ArtifactValidation.Ok` (signature valid + publisher key trusted
    /// at the edge). Reserved `SourceModule = "_platform.artefacts"`.
    ///
    /// Phase 625: renamed from `ArtifactVerified`. Wire `EventType`
    /// remains `"ArtifactVerified"`.
    | ModuleArtefactVerified of ModuleArtefactVerifiedPayload
    /// Phase 30a — `IArtifactVerifier.Verify` returned
    /// `ArtifactValidation.Error reason`. Reserved
    /// `SourceModule = "_platform.artefacts"`. Operator dashboards
    /// query on this case to surface refusal rates without scanning
    /// every verify row.
    ///
    /// Phase 625: renamed from `ArtifactRejected`. Wire `EventType`
    /// remains `"ArtifactRejected"` — `CefFormatter` grades that exact
    /// string `CefHigh`, and operator SIEM rules key on it.
    | ModuleArtefactRejected of ModuleArtefactRejectedPayload
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
    /// Phase 551 — a grant write was refused because it did not satisfy
    /// the target module's declared `GrantPolicy`. Write-time twin of
    /// `UnconsentedGrantRefused`.
    | GrantPolicyRefused of GrantPolicyRefusedPayload
    /// Phase 551 — a module's routes were refused at dispatch because the
    /// caller's permission entry carried no live grant record under the
    /// module's declared `GrantPolicy`.
    | UnconsentedGrantRefused of UnconsentedGrantRefusedPayload
    /// Phase 555 — a sensitive admin mutation was captured as a pending
    /// record under dual control and did NOT apply.
    | AdminMutationProposed of AdminMutationProposedPayload
    /// Phase 555 — a second, distinct administrator approved a pending
    /// mutation.
    | AdminMutationApproved of AdminMutationApprovedPayload
    /// Phase 555 — a second administrator rejected a pending mutation;
    /// nothing was applied.
    | AdminMutationRejected of AdminMutationRejectedPayload
    /// Phase 555 — an approval attempt was structurally refused (self
    /// approval, an expired record, an unknown request).
    | AdminMutationApprovalRefused of AdminMutationApprovalRefusedPayload
    /// Phase 555 — a pending mutation lapsed without a decision and was
    /// swept.
    | AdminMutationExpired of AdminMutationExpiredPayload
    /// Phase 18 — a typed inter-platform peer contract call resolved on
    /// the receiver. Emitted once per inbound call by the peer host's
    /// contract handler after dispatch reaches a terminal outcome.
    /// Reserved `SourceModule = "_platform.peer"`.
    | PeerCallCompleted of PeerCallCompletedPayload
    /// Phase 310 — a long-running peer call reached its terminal outcome.
    /// Emitted by `PeerJobHandler.Execute` once the backing job has
    /// resolved and its typed result is parked. Distinct from
    /// `PeerCallCompleted`, which for a long-running method records only
    /// that the call was *accepted and scheduled*. Reserved
    /// `SourceModule = "_platform.peer"`.
    | PeerJobCompleted of PeerJobCompletedPayload
    /// Phase 311 — the receiver's composed clean-room gate decided over one
    /// contract answer (released, possibly with cells suppressed, or
    /// withheld whole). Emitted by `CleanRoomGate` once per gated dispatch.
    /// Reserved `SourceModule = "_platform.peer"`.
    | PeerCleanRoomDecision of PeerCleanRoomDecisionPayload
    /// Phase 483 — one round of a multi-round federated run reached its
    /// barrier and its responses were folded. Emitted by
    /// `IRoundOrchestrator` once per completed round. Reserved
    /// `SourceModule = "_platform.peer"`.
    | FederationRoundCompleted of FederationRoundCompletedPayload
    /// Phase 483 — a participant was classified as dropped for a round by
    /// the run's `DropoutPolicy`. One row per dropped participant per
    /// round. Reserved `SourceModule = "_platform.peer"`.
    | FederationParticipantDropped of FederationParticipantDroppedPayload
    /// Phase 483 — a multi-round run terminated without reaching its
    /// completion condition. Reserved `SourceModule = "_platform.peer"`.
    | FederationRunAborted of FederationRunAbortedPayload
    /// Phase 40 — `IArtefactSigner.Sign` produced a detached-JWS
    /// signature over an arbitrary artefact. Reserved `SourceModule =
    /// "_platform.signing"`. Payload carries the key id + artefact
    /// SHA-256, never the bytes. Distinct from the Phase 30a
    /// module-distribution family, which Phase 625 renamed to
    /// `ModuleArtefactSigned` precisely because a one-vowel difference
    /// was not a safe way to tell two security events apart.
    | ArtefactSigned of ArtefactSignedPayload
    /// Phase 40 — a new artefact-signing key became active, rotating out
    /// a predecessor whose public key remains discoverable for archival
    /// verification. Reserved `SourceModule = "_platform.signing"`.
    | SigningKeyRotated of SigningKeyRotatedPayload
    /// Phase 41 — a classified field was read by a caller (field's
    /// `AuditOnRead` set). Reserved `SourceModule =
    /// "_platform.classification"`. Value-free; carries entity +
    /// field-path + level + caller + whether the value was redacted.
    | ClassifiedFieldRead of ClassifiedFieldReadPayload
    /// Phase 41 — a classified field was written by a caller. Reserved
    /// `SourceModule = "_platform.classification"`. Value-free.
    | ClassifiedFieldWritten of ClassifiedFieldWrittenPayload
    /// Phase 54 — a tenant scope finished provisioning; every registered
    /// `ITenantLifecycle.OnProvisioned` hook ran. Reserved
    /// `SourceModule = "_platform.tenant"`. Counts-only payload.
    | TenantProvisioned of TenantProvisionedPayload
    /// Phase 54 — a tenant scope finished deprovisioning (offboard).
    /// The single end-of-offboard marker. Reserved
    /// `SourceModule = "_platform.tenant"`.
    | TenantDeprovisioned of TenantDeprovisionedPayload
    /// Phase 54 — one lifecycle hook failed during a provision /
    /// deprovision run. Non-aborting; one row per failed hook. Reserved
    /// `SourceModule = "_platform.tenant"`.
    | TenantLifecycleHookFailed of TenantLifecycleHookFailedPayload
    /// Phase 54j — the tenant's data-export archive was durably written
    /// before the erasure sweep (export-then-erase, fail-closed). Reserved
    /// `SourceModule = "_platform.tenant"`.
    | TenantDataExported of TenantDataExportedPayload
    /// Phase 54i — a confirmation token was minted for a pending offboard
    /// (`RequestDeprovisionToken`). Reserved
    /// `SourceModule = "_platform.tenant"`.
    | TenantOffboardConfirmationRequested of TenantOffboardConfirmationRequestedPayload
    /// Phase 54i — a confirmation token was accepted and the destructive
    /// offboard proceeded (`DeprovisionTenantConfirmed`). Reserved
    /// `SourceModule = "_platform.tenant"`.
    | TenantOffboardConfirmationApproved of TenantOffboardConfirmationApprovedPayload
    /// Phase 54i — a confirmation-gated offboard was refused at the gate
    /// before any destruction (missing/expired/wrong-scope token, or a
    /// same-admin redemption under `TwoPersonRule`). Reserved
    /// `SourceModule = "_platform.tenant"`.
    | TenantOffboardConfirmationRefused of TenantOffboardConfirmationRefusedPayload
    /// Phase 54f — a grace-period offboard was scheduled to fire after a
    /// cancellable window. Reserved `SourceModule = "_platform.tenant"`.
    | TenantDeprovisionScheduled of TenantDeprovisionScheduledPayload
    /// Phase 54f — a pending grace-period offboard was cancelled before it
    /// fired. Reserved `SourceModule = "_platform.tenant"`.
    | TenantDeprovisionCancelled of TenantDeprovisionCancelledPayload
    /// Phase 107 — an original ingested document was fetched from the
    /// Knowledge Base via `GetOriginalDocument`. Sensitive-read audit,
    /// distinct from the upload event (GP 6).
    | KnowledgeOriginalRetrieved of KnowledgeOriginalRetrievedPayload
    /// Phase 107 — a `GetOriginalDocument` fetch was refused
    /// (out-of-scope id or no retrievable original). Denials on the
    /// team boundary are audited (GP 4 + GP 6).
    | KnowledgeOriginalRetrievalDenied of KnowledgeOriginalRetrievalDeniedPayload
    /// Phase 69h.tail — uniform dispatcher-emitted audit row for an
    /// `[<Audit>]`-annotated ToolUp.Remoting API method. Emitted by the
    /// default `IAuditEmitter` bridge `Api.make` composes over the
    /// registered `IAuditLog`. Reserved `SourceModule = "_platform.audit"`.
    | RemotingMethodAudited of RemotingMethodAuditedPayload
    /// Phase 115 — a Knowledge Base scope was wiped via `ResetIndex`,
    /// fanning the deletion across every retrieval index. Carries the
    /// erasure outcome (document count + surviving-chunk count) so a
    /// half-completed fan-out is loud in the audit trail (GP 6 + GP 9),
    /// complementing the generic dispatcher action row.
    | KnowledgeScopeErased of KnowledgeScopeErasedPayload
    /// Phase 120 — uniform structured authorization-denial row emitted by
    /// `IAuthAuditHook.RecordDenial` across every HTTP-surface denial class
    /// (surface / role / share-token / SSE-identity / module-permission /
    /// KB-destructive). One queryable trail keyed by route/requirement/scope
    /// (GP 6); coalesced under a per-`(route, subject)` flood guard.
    | AuthorizationDenied of AuthorizationDeniedPayload
    /// Phase 272 — a hosted-tree action was authorized (or denied) through the
    /// Phase 113 action authorizer and dispatched. GP 6 — every state-changing
    /// hosted action leaves a trail keyed on the neutral `ActionDescriptor` +
    /// the decision; a denied action audits the denial.
    | HostActionDispatched of HostActionDispatchedPayload
    /// Phase 188 — a classified field was redacted / blocked at an egress
    /// boundary (export / RPC response / sink) by the `EgressGate`.
    /// Reserved `SourceModule = "_platform.classification"`. Value-free;
    /// one row per non-`Allow` decision so a DLP deny is never silent.
    | EgressBlocked of EgressBlockedPayload
    /// Phase 14v — a persisted RAG vector-index blob failed to
    /// deserialise on scope load (disk corruption / partial flush during
    /// a pod kill). Recorded under `KnowledgeSourceModule.value` scope;
    /// makes the formerly `Warn`-only silent-empty load loud (GP 6 + GP 9).
    | KnowledgeIndexLoadFailed of KnowledgeIndexLoadFailedPayload
    /// Phase 303 — a document was dropped from ingestion because the
    /// in-process queue was full and the bounded enqueue retry was
    /// exhausted. Recorded under `KnowledgeSourceModule.value`; makes the
    /// formerly telemetry-only queue-overflow loss queryable per-document
    /// (GP 6 + GP 9).
    | KnowledgeIngestionDropped of KnowledgeIngestionDroppedPayload
    /// Phase 14x — a KB upload matched an existing document's content
    /// hash in the caller's scope and was deduplicated onto it
    /// (idempotent upload; ingestion skipped). Recorded under the
    /// caller's scope.
    | KnowledgeDocumentDeduplicated of KnowledgeDocumentDeduplicatedPayload
    /// Phase 512 — the age-based KB retention sweep purged one or more
    /// documents from a scope. Recorded under the swept scope; emitted
    /// only by runs that actually removed something.
    | KnowledgeDocumentsPurged of KnowledgeDocumentsPurgedPayload
    /// Phase 515 — the composed `IContentScanner` returned a verdict for
    /// an upload at the upload boundary. Emitted for every verdict
    /// (clean included), under the uploader's scope; absent entirely on a
    /// deployment that composed no scanner.
    | ContentScanned of ContentScannedPayload
    /// Phase 7c — the data-object orphan sweep reclaimed one unreferenced
    /// content blob from a scope's dedup pool. One row per blob, under the
    /// swept scope.
    | OrphanedContentBlobReclaimed of OrphanedContentBlobReclaimedPayload
    /// Phase 7c — aggregate summary of one orphan-sweep run, emitted only
    /// by runs that reclaimed something.
    | OrphanSweepCompleted of OrphanSweepCompletedPayload
    /// Phase 443 — a WebAuthn passkey credential was enrolled via the
    /// passkey auth companion's registration ceremony. Recorded under
    /// `_platform` scope; source-module `_platform.auth.passkey`.
    | PasskeyCredentialRegistered of PasskeyCredentialRegisteredPayload
    /// Phase 443 — a passkey credential was removed for a user.
    | PasskeyCredentialRemoved of PasskeyCredentialRemovedPayload
    /// Phase 449 — a model-fit run began (provider resolved, composite
    /// identity computed). Reserved `SourceModule = "_platform.audit"`.
    | ModelFitStarted of ModelFitStartedPayload
    /// Phase 449 — a model-fit run produced an outcome (diagnostics + gate
    /// verdicts). Emitted whether or not gates passed.
    | ModelFitCompleted of ModelFitCompletedPayload
    /// Phase 449 — one or more diagnostic gates failed on a completed fit.
    /// A typed, audited verdict — not an exception.
    | ModelFitGateFailed of ModelFitGateFailedPayload
    /// Phase 599 — a fit batch was submitted (N per-item jobs under one
    /// correlation id).
    | ModelFitBatchSubmitted of ModelFitBatchSubmittedPayload
    /// Phase 453 — a model artifact was registered from a completed fit.
    | ModelArtifactRegistered of ModelArtifactRegisteredPayload
    /// Phase 453 — a model artifact's lifecycle status transitioned.
    | ModelArtifactTransitioned of ModelArtifactTransitionedPayload
    /// Phase 453 — a model artifact lifecycle transition was refused (GP 4).
    | ModelArtifactTransitionDenied of ModelArtifactTransitionDeniedPayload
    /// Phase 644 — a lifecycle transition judged at the author-agnostic
    /// seam, carrying the author and the channel it arrived on.
    | ModelArtifactTransitionAttributed of ModelArtifactTransitionAttributedPayload
    /// Phase 646 — opaque provenance attachments were appended to a model
    /// artifact (and/or its acceptance signature recorded).
    | ModelArtifactProvenanceAttached of ModelArtifactProvenanceAttachedPayload
    /// Phase 646 — a promotion transfer was judged at the transfer seam:
    /// artifact + spec payload + attachments landing as one recorded act.
    | ModelArtifactPromoted of ModelArtifactPromotionPayload
    /// Phase 454 — a scoring run produced predictions as a new dataset
    /// version (provenance names the artifact + input vintage).
    | ModelScored of ModelScoredPayload
    /// Phase 454 — a scoring run was refused (approved-guard / schema
    /// mismatch / input unavailable / provider raised). A typed, audited
    /// refusal — not an exception.
    | ModelScoreRefused of ModelScoreRefusedPayload
    /// Phase 456 — a holdout-evaluation run stored a provider-computed
    /// metric map against a model artifact (out-of-time track record).
    | ModelEvaluated of ModelEvaluatedPayload
    /// Phase 645 — a declared promotion policy reached a verdict for a
    /// model artifact (auto-promote / queue for curation / reject).
    | ModelPromotionPolicyEvaluated of ModelPromotionPolicyEvaluatedPayload
    /// Phase 645 — an auto-promotion displaced a previously promoted
    /// artifact, with the deltas that justified it.
    | ModelArtifactSuperseded of ModelArtifactSupersededPayload
    /// Phase 651 — a registration observer raised and the failure was
    /// isolated; the registration itself stands.
    | ModelRegistrationObserverFailed of ModelRegistrationObserverFailedPayload
    /// Phase 487 — a virtual dataset version was materialised to a
    /// retention-bounded scratch blob for compute handoff.
    | DatasetSpillCreated of DatasetSpillCreatedPayload
    /// Phase 487 — a spill blob was deleted (TTL reached / explicit cleanup).
    | DatasetSpillDeleted of DatasetSpillDeletedPayload
    /// Phase 482 — a dataset version's privacy-provenance labels were removed
    /// by an explicit admin act (the only removal path).
    | DatasetDeclassified of DatasetDeclassifiedPayload
    /// Phase 601 — an assembly re-vintage produced new version(s) from a
    /// recorded spec.
    | DatasetRevintaged of DatasetRevintagedPayload
    /// Phase 482 — a label-carrying dataset version was refused a dispatch /
    /// raw export by policy. A typed, audited denial.
    | DatasetPolicyDenied of DatasetPolicyDeniedPayload
    /// Phase 7b — the AI proposed a candidate user-authored schema for a
    /// scope, surfaced for human review. Emitted by the AI-propose flow.
    | SchemaProposed of SchemaProposedPayload
    /// Phase 7b — a user approved a committed schema version whose
    /// provenance was `AuthoredBy.AIWithApproval`.
    | SchemaApproved of SchemaApprovedPayload
    /// Phase 7b — a user-authored schema version was created, updated,
    /// migrated, or deleted.
    | SchemaChanged of SchemaChangedPayload
    /// Phase 320 — an external-compute completion callback resolved its
    /// handle, or found it already resolved (the idempotent no-op).
    | ExternalCallbackResolved of ExternalCallbackResolvedPayload
    /// Phase 320 — an external-compute completion callback was refused.
    /// The forged-callback signal; see the payload doc for why it is its
    /// own kind rather than a field on the resolution event.
    | ExternalCallbackRejected of ExternalCallbackRejectedPayload
    /// Phase 657 — the boot-time composition verification verdict, one row
    /// per process start. Recorded on the affirmative verdict too: absence
    /// of a row means the check did not run, and that is a different fact
    /// from a clean one.
    | CompositionVerificationRecorded of CompositionVerificationRecordedPayload
    /// Phase 657 — a composed component was refused a capability beyond
    /// its declared envelope by the mandatory capability gate.
    | CompositionCapabilityRefused of CompositionCapabilityRefusedPayload
    /// Phase 680 — an answer was verified and every numeric figure it
    /// carried matched a retrieved fact (or the turn had no facts to check
    /// against). The affirmative row; it exists so that the absence of a
    /// row stays a different fact from a clean one.
    | AnswerVerificationPassed of AnswerVerificationPayload
    /// Phase 680 — an answer carried at least one numeric figure that
    /// matched no fact in the turn's retrieved set while facts WERE in
    /// scope. The grounding refusal, on the same chained path as every
    /// other audited refusal.
    | AnswerVerificationFlagged of AnswerVerificationPayload
    /// Phase 683 — a fact offered by a peer verified against that peer's
    /// certificate, re-derived to the id the certificate names, and landed
    /// under a stance no wider than the one the peer declared.
    | FactImportAccepted of FactImportPayload
    /// Phase 683 — an offered fact was refused at the import door and
    /// nothing was asserted. The unverifiable / tampered / id-mismatched
    /// signal, on the same audited path as every other refusal.
    | FactImportRefused of FactImportPayload
    /// Phase 684 — a declared grounding-envelope facet moved through the
    /// audited choke point, carrying the before/after envelope digests
    /// that make the mutation chain walkable.
    | GroundingEnvelopeMutated of GroundingEnvelopeMutatedPayload
    /// Phase 684 — a grounding-envelope mutation was refused under the
    /// verified composition profile and nothing moved.
    | GroundingMutationRefused of GroundingMutationRefusedPayload
    /// Phase 685 — a grounding certificate was issued. Identifiers only:
    /// the certificate digest, its subject, the sealing key id. This is
    /// the row that makes issuance enumerable and a suppressed
    /// certificate visible.
    | CertificateIssued of CertificateIssuedPayload
    /// Phase 686 — the deployment verification report was run. An audited
    /// READ: nothing moved, and the row records who asked, what the
    /// verdict set was, and the digest that commits to it.
    | DeploymentVerified of DeploymentVerifiedPayload
    /// Phase 713 — the evidence chain was walked. An audited READ:
    /// nothing moved, and the row records who asked, what the link set
    /// was, and the digest that commits to it.
    | EvidenceChainWalked of EvidenceChainWalkedPayload

module AuditEvent =
    /// Wire-format `EventType` discriminator for the given event. The
    /// returned string matches the DU case name for every case EXCEPT the
    /// three Phase 625 `ModuleArtefact*` cases, which keep emitting their
    /// historical `Artifact*` strings. Persisted events use this as the
    /// `ModuleEvent.EventType` field, so `IEventStore.ReadByType` queries can
    /// target a single audit kind. The full mapping is projected into
    /// `docs/reference/audit-event-reference.md`.
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
        | EncryptionKeyDestroyAcknowledged _ -> "EncryptionKeyDestroyAcknowledged"
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
        | OAuth1aConnected _ -> "OAuth1aConnected"
        | OAuth1aDisconnected _ -> "OAuth1aDisconnected"
        | OAuth1aSigningFailed _ -> "OAuth1aSigningFailed"
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
        | SessionRevoked _ -> "SessionRevoked"
        | AllSessionsRevoked _ -> "AllSessionsRevoked"
        | ServiceAccountCreated _ -> "ServiceAccountCreated"
        | ServiceAccountPermissionsChanged _ -> "ServiceAccountPermissionsChanged"
        | ServiceAccountTokenMinted _ -> "ServiceAccountTokenMinted"
        | ServiceAccountTokenRevoked _ -> "ServiceAccountTokenRevoked"
        | ServiceAccountStatusChanged _ -> "ServiceAccountStatusChanged"
        | ConversationExported _ -> "ConversationExported"
        | BeaconRejected _ -> "BeaconRejected"
        | ConfigDrift _ -> "ConfigDrift"
        | DiagnosticBundleAccessed _ -> "DiagnosticBundleAccessed"
        | RateLimitWaited _ -> "RateLimitWaited"
        | RateLimitRefused _ -> "RateLimitRefused"
        | ComputeBudgetDenied _ -> "ComputeBudgetDenied"
        | ComputeBudgetWarning _ -> "ComputeBudgetWarning"
        | DataSubjectRequest _ -> "DataSubjectRequest"
        | ConversationStarted _ -> "ConversationStarted"
        | ConversationTurnAppended _ -> "ConversationTurnAppended"
        | ConversationCompleted _ -> "ConversationCompleted"
        | ConversationErased _ -> "ConversationErased"
        | ConversationReplayed _ -> "ConversationReplayed"
        | AssetUploaded _ -> "AssetUploaded"
        | AssetDeleted _ -> "AssetDeleted"
        | TeamCreationDenied _ -> "TeamCreationDenied"
        | TeamArchived _ -> "TeamArchived"
        | TeamRestored _ -> "TeamRestored"
        | TeamDeleted _ -> "TeamDeleted"
        | TeamOwnershipTransferred _ -> "TeamOwnershipTransferred"
        | TeamInviteIssued _ -> "TeamInviteIssued"
        | TeamInviteAccepted _ -> "TeamInviteAccepted"
        | TeamInviteAcceptedFromPending _ -> "TeamInviteAcceptedFromPending"
        | TeamInviteAcceptedFromPendingFailed _ -> "TeamInviteAcceptedFromPendingFailed"
        | TeamInviteRevoked _ -> "TeamInviteRevoked"
        | TeamInviteRedeemed _ -> "TeamInviteRedeemed"
        | TeamInviteExpired _ -> "TeamInviteExpired"
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
        // Phase 625 — PINNED legacy wire names. These three cases were
        // renamed `Artifact*` -> `ModuleArtefact*` at the F# surface;
        // the emitted discriminator deliberately did NOT move, because
        // it is already replicated into operator-owned SIEMs and
        // append-only archives that forge cannot migrate. Changing a
        // string here silently breaks existing alert rules and makes
        // every archived row of this family undecodable. The pin is
        // asserted by `AuditEventRegistryTests`.
        | ModuleArtefactSigned _ -> "ArtifactSigned"
        | ModuleArtefactVerified _ -> "ArtifactVerified"
        | ModuleArtefactRejected _ -> "ArtifactRejected"
        | SyntheticSampleGenerated _ -> "SyntheticSampleGenerated"
        | SchemaOnlyAccessAttempted _ -> "SchemaOnlyAccessAttempted"
        | GrantPolicyRefused _ -> "GrantPolicyRefused"
        | UnconsentedGrantRefused _ -> "UnconsentedGrantRefused"
        | AdminMutationProposed _ -> "AdminMutationProposed"
        | AdminMutationApproved _ -> "AdminMutationApproved"
        | AdminMutationRejected _ -> "AdminMutationRejected"
        | AdminMutationApprovalRefused _ -> "AdminMutationApprovalRefused"
        | AdminMutationExpired _ -> "AdminMutationExpired"
        | PeerCallCompleted _ -> "PeerCallCompleted"
        | PeerJobCompleted _ -> "PeerJobCompleted"
        | PeerCleanRoomDecision _ -> "PeerCleanRoomDecision"
        | FederationRoundCompleted _ -> "FederationRoundCompleted"
        | FederationParticipantDropped _ -> "FederationParticipantDropped"
        | FederationRunAborted _ -> "FederationRunAborted"
        | ArtefactSigned _ -> "ArtefactSigned"
        | SigningKeyRotated _ -> "SigningKeyRotated"
        | ClassifiedFieldRead _ -> "ClassifiedFieldRead"
        | ClassifiedFieldWritten _ -> "ClassifiedFieldWritten"
        | TenantProvisioned _ -> "TenantProvisioned"
        | TenantDeprovisioned _ -> "TenantDeprovisioned"
        | TenantLifecycleHookFailed _ -> "TenantLifecycleHookFailed"
        | TenantDataExported _ -> "TenantDataExported"
        | TenantOffboardConfirmationRequested _ -> "TenantOffboardConfirmationRequested"
        | TenantOffboardConfirmationApproved _ -> "TenantOffboardConfirmationApproved"
        | TenantOffboardConfirmationRefused _ -> "TenantOffboardConfirmationRefused"
        | TenantDeprovisionScheduled _ -> "TenantDeprovisionScheduled"
        | TenantDeprovisionCancelled _ -> "TenantDeprovisionCancelled"
        | KnowledgeOriginalRetrieved _ -> "KnowledgeOriginalRetrieved"
        | KnowledgeOriginalRetrievalDenied _ -> "KnowledgeOriginalRetrievalDenied"
        | RemotingMethodAudited _ -> "RemotingMethodAudited"
        | KnowledgeScopeErased _ -> "KnowledgeScopeErased"
        | AuthorizationDenied _ -> "AuthorizationDenied"
        | HostActionDispatched _ -> "HostActionDispatched"
        | EgressBlocked _ -> "EgressBlocked"
        | KnowledgeIndexLoadFailed _ -> "KnowledgeIndexLoadFailed"
        | KnowledgeIngestionDropped _ -> "KnowledgeIngestionDropped"
        | KnowledgeDocumentDeduplicated _ -> "KnowledgeDocumentDeduplicated"
        | KnowledgeDocumentsPurged _ -> "KnowledgeDocumentsPurged"
        | ContentScanned _ -> "ContentScanned"
        | OrphanedContentBlobReclaimed _ -> "OrphanedContentBlobReclaimed"
        | OrphanSweepCompleted _ -> "OrphanSweepCompleted"
        | PasskeyCredentialRegistered _ -> "PasskeyCredentialRegistered"
        | PasskeyCredentialRemoved _ -> "PasskeyCredentialRemoved"
        | ModelFitStarted _ -> "ModelFitStarted"
        | ModelFitCompleted _ -> "ModelFitCompleted"
        | ModelFitGateFailed _ -> "ModelFitGateFailed"
        | ModelFitBatchSubmitted _ -> "ModelFitBatchSubmitted"
        | ModelArtifactRegistered _ -> "ModelArtifactRegistered"
        | ModelArtifactTransitioned _ -> "ModelArtifactTransitioned"
        | ModelArtifactTransitionDenied _ -> "ModelArtifactTransitionDenied"
        | ModelArtifactTransitionAttributed _ -> "ModelArtifactTransitionAttributed"
        | ModelArtifactProvenanceAttached _ -> "ModelArtifactProvenanceAttached"
        | ModelArtifactPromoted _ -> "ModelArtifactPromoted"
        | ModelScored _ -> "ModelScored"
        | ModelScoreRefused _ -> "ModelScoreRefused"
        | ModelEvaluated _ -> "ModelEvaluated"
        | ModelPromotionPolicyEvaluated _ -> "ModelPromotionPolicyEvaluated"
        | ModelArtifactSuperseded _ -> "ModelArtifactSuperseded"
        | ModelRegistrationObserverFailed _ -> "ModelRegistrationObserverFailed"
        | DatasetSpillCreated _ -> "DatasetSpillCreated"
        | DatasetSpillDeleted _ -> "DatasetSpillDeleted"
        | DatasetDeclassified _ -> "DatasetDeclassified"
        | DatasetRevintaged _ -> "DatasetRevintaged"
        | DatasetPolicyDenied _ -> "DatasetPolicyDenied"
        | SchemaProposed _ -> "SchemaProposed"
        | SchemaApproved _ -> "SchemaApproved"
        | SchemaChanged _ -> "SchemaChanged"
        | ExternalCallbackResolved _ -> "ExternalCallbackResolved"
        | ExternalCallbackRejected _ -> "ExternalCallbackRejected"
        | CompositionVerificationRecorded _ -> "CompositionVerificationRecorded"
        | CompositionCapabilityRefused _ -> "CompositionCapabilityRefused"
        | AnswerVerificationPassed _ -> "AnswerVerificationPassed"
        | AnswerVerificationFlagged _ -> "AnswerVerificationFlagged"
        | FactImportAccepted _ -> "FactImportAccepted"
        | FactImportRefused _ -> "FactImportRefused"
        | GroundingEnvelopeMutated _ -> "GroundingEnvelopeMutated"
        | GroundingMutationRefused _ -> "GroundingMutationRefused"
        | CertificateIssued _ -> "CertificateIssued"
        | DeploymentVerified _ -> "DeploymentVerified"
        | EvidenceChainWalked _ -> "EvidenceChainWalked"

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

// ─── Phase 120 — IAuthAuditHook (structured authz-denial trail) ──────
//
// A single write-side seam every authorization-denial emission point
// calls, so the whole HTTP auth surface produces one uniform
// `AuthorizationDenied` audit row instead of scattered per-subsystem
// metrics + log lines. Generalises the AI tool-allowlist denial stream
// (Phase 45) to surface-enforcement / RBAC / share-token / SSE-identity
// / module-permission / KB-destructive denials.
//
// The default implementation (`AuthAuditHook`, Server tier) writes an
// `AuthorizationDenied` event through the registered `IAuditLog` and
// coalesces probing bursts via a per-`(route, subject)` dedup window, so
// a scripted enumeration produces bounded audit volume with an accurate
// count (GP 6 + GP 13 — no new infrastructure; the hook's backing store
// is the existing audit log).

/// The requirement class that was not satisfied at a denial. A DU (not a
/// bare string) so every emission call site is exhaustiveness-checked and
/// the read-side rollup can cut by typed requirement; serialised to its
/// `requirementString` form on the persisted `AuthorizationDeniedPayload`.
type AuthDenialRequirement =
    /// `SurfaceEnforcementMiddleware` route-surface matrix denial.
    | SurfaceDenialRequirement
    /// RBAC role / platform-admin gate denial.
    | RoleDenialRequirement
    /// Share-token validation failure (signature / revoked / expired /
    /// use-limit / rate-limit).
    | ShareTokenDenialRequirement
    /// SSE `?userId=` / principal-mismatch denial (the emission point lands
    /// when identity-aware SSE lifecycle ships; the case exists now so that
    /// wiring emits through the same uniform seam).
    | SseIdentityDenialRequirement
    /// Module-permission denial (e.g. a `SchemaOnly` caller reaching a
    /// real-row path).
    | ModulePermissionDenialRequirement
    /// Knowledge-Base destructive-op authorization denial.
    | KbDestructiveDenialRequirement

module AuthDenialRequirement =
    /// Stable wire string for the persisted payload's `Requirement` field.
    let toString =
        function
        | SurfaceDenialRequirement -> "surface"
        | RoleDenialRequirement -> "role"
        | ShareTokenDenialRequirement -> "share-token"
        | SseIdentityDenialRequirement -> "sse-identity"
        | ModulePermissionDenialRequirement -> "module-permission"
        | KbDestructiveDenialRequirement -> "kb-destructive"

/// Value-typed denial record handed to `IAuthAuditHook.RecordDenial`.
/// Carries the resolved `Subject` so emission sites stay terse; the hook
/// sanitises it to `(kind, id)` when it writes the audit row (no PII
/// beyond the subject id reaches the trail). Six-rule rule 1 (identity by
/// value): every field is a value — `Subject` is a value DU, no live
/// handles.
type AuthDenial = {
    /// Route the denial fired on (method + path, unparameterised).
    Route: string
    /// Resolved subject at denial time. Sanitised to kind + id by the hook.
    Subject: Subject
    /// Requirement class that was not satisfied.
    Requirement: AuthDenialRequirement
    /// Machine-readable verdict / denial code (mirrors the response body's
    /// `error` field where one exists).
    Verdict: string
    /// Human-readable reason. Callers keep this PII-free beyond the subject.
    Reason: string
    /// Scope the denial occurred in; `None` for scope-less (anonymous)
    /// denials. Determines the audit write scope (caller-scope when present,
    /// `_platform` otherwise) so the read-side rollup is caller-scoped (GP 4).
    ScopeId: string option
    /// Correlation id stitching to the request log.
    CorrelationId: string option
}

/// Write-side hook for authorization denials. SDK-wide DI singleton; the
/// default implementation writes an `AuthorizationDenied` audit event.
///
/// **Best-effort, never blocking** — same contract as `IAuditLog.Record`:
/// a hook failure on the denial path MUST NOT stall the response. Emission
/// sites wrap the call defensively.
///
/// Six-rule portability audit:
///   1. Identity by value      — `AuthDenial` is all values (`Subject` is a
///                                 value DU). No live handles.
///   2. Async                  — `RecordDenial : AuthDenial -> Async<unit>`.
///   3. Retry as data          — none; emission is best-effort fire-and-
///                                 forget, matching `IAuditLog`.
///   4. Stateless handlers     — the contract carries all state per call;
///                                 the default impl's dedup window is an
///                                 in-process optimisation, documented
///                                 single-instance (a distributed impl can
///                                 no-op the coalescing and emit per call).
///   5. No cross-shard ordering — denial rows are independent; no ordering
///                                 promise across routes/subjects.
///   6. Precision lower bound   — the dedup window is a coarse `TimeSpan`;
///                                 no sub-second promise.
type IAuthAuditHook =
    /// Record an authorization denial. Best-effort; failures are swallowed
    /// by the implementation. The default impl coalesces probing bursts on
    /// the same `(route, subject)` key within its dedup window.
    abstract RecordDenial: denial: AuthDenial -> Async<unit>