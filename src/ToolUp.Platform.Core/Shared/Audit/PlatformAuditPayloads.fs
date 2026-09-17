// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: the platform-lane audit payload records
// (identity, teams, membership, files, analysis, permissions, notifications,
// forms, workflow, entity lifecycle, encryption keys, health, audit sinks,
// OAuth 2 / 1.0a / token refresh).
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

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

/// Phase 6p — an ephemeral `SessionFileStore` was dropped by the TTL
/// eviction sweep and has since been RE-CREATED under the same scope
/// container, so every file the caller uploaded before the gap is gone
/// while their client may still be listing them.
///
/// Emitted once per eviction-then-recreate transition, never on a fresh
/// first access (nothing was lost, and there is no client to inform).
///
/// GP 4 — `Container` is the storage-scope container the audit trail
/// already records for every file event in that scope. Deliberately NOT
/// carried: filenames, file contents, file count before the eviction, or
/// any user identity beyond the container. The operator question this
/// answers is "did this scope silently lose its uploads, and when" —
/// answering it does not require knowing what was lost.
///
/// `Reason` uses the same vocabulary as the notification payload
/// (`ProcessedDataTypes.SessionStoreResetNotification`); `"Evicted"` is
/// the only value the server emits.
type SessionStoreResetPayload = { Container: string; Reason: string }

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

/// Phase 553 — the tamper-evidence link a permission-class audit record
/// carries. Two hashes, moved as ONE optional field rather than as two
/// independent optional fields, because a record holding a predecessor
/// with no self-hash (or the reverse) is a state the chain has no
/// meaning for and no writer should be able to express.
///
/// **Both are bare lowercase 64-hex SHA-256**, matching the two nearest
/// neighbours in this substrate — the chained-ledger sink's record
/// digests and the deploy plane's `digestCanonicalForm` — rather than
/// carrying a `sha256:` algorithm prefix. One spelling per substrate is
/// worth more than agreement with a distant one.
type PermissionAuditChainLink = {
    /// The `ContentHash` of the predecessor permission record in this
    /// scope, or `PermissionAuditChain.genesisHash` (64 zeros) for the
    /// first chained record. A record claiming the genesis value asserts
    /// it is first, so a chain truncated from the FRONT cannot pass
    /// verification by simply starting later.
    PrevHash: string
    /// SHA-256 over this record's canonical form, which frames
    /// `PrevHash` — so the hash commits to the whole prefix of the
    /// chain, not merely to this record. Computed server-side; the
    /// canonical form it is taken over lives in
    /// `ToolUp.Platform.PermissionAuditChain` (Server tier, because
    /// hashing needs `System.Security.Cryptography`, which does not
    /// compile under Fable).
    ContentHash: string
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
    /// Phase 553 — the hash-chain link, filled in by the audit log at
    /// WRITE time. Emission call sites construct this `None`; the log
    /// reads the scope's current chain head and replaces it. A caller
    /// that supplies a link is taken at its word (the replicator's
    /// re-emission path, and the fallback replay service, both re-write
    /// records that were already chained).
    ///
    /// **`None` is the shipped default and absorbs every pre-553
    /// record.** The converter set this payload persists through
    /// initialises an absent reference-typed field to `null`, and `None`
    /// IS null for `FSharpOption`, so a blob written before this field
    /// existed deserialises to `None` with no version switch and no
    /// migration — the backward-compatible default GP 11 asks for,
    /// obtained structurally. Verification reports such records as an
    /// UNCHAINED PREFIX rather than as a break: a record written before
    /// the chain existed is not evidence of tampering, and a verifier
    /// that said otherwise would cry wolf on every upgraded deployment.
    Chain: PermissionAuditChainLink option
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
    /// Actor who triggered the lifecycle event — the `Principal` of the
    /// `EntityActor` on the store call, exactly as passed (Phase 806).
    /// `"system"` only when the caller passed `EntityActor.system`; a
    /// store never infers it.
    UserId: string
    /// Phase 806 — the subject `UserId` acted FOR when the write was
    /// delegated (`EntityActor.OnBehalfOf`); `None` when the principal
    /// acted for itself. Absent on every pre-806 row and deserialises to
    /// `None` by the same null-is-`None` mechanism as `Replay` below.
    OnBehalfOf: string option
    /// Registered entity-type name (the `Type` field on the record).
    EntityType: string
    /// Entity instance identifier within `(entityType, scopeId)`.
    EntityId: string
    /// Version assigned by the store. For `EntityCreated` this is 1;
    /// for `EntityUpdated` the new version (>1); for `EntityDeleted`
    /// the head version at delete time.
    Version: int
    /// Phase 759 — `Some` when this row records an offline mutation
    /// applied by replay: the offline sync handler passes the provenance
    /// on the `EntityActor` and the store stamps it here beside the
    /// version it assigned (Phase 806); `None` for a live write.
    ///
    /// **`None` is the shipped default and absorbs every pre-759
    /// record** — the same structural backward compatibility as
    /// `PermissionChangedPayload.Chain`: the converter set this payload
    /// persists through initialises an absent reference-typed field to
    /// `null`, and `None` IS null for `FSharpOption`, so a row written
    /// before this field existed deserialises to `None` with no version
    /// switch and no migration (GP 11).
    Replay: EntityTypes.EntityReplayProvenance option
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