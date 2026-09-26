// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: share-token, session, service-account,
// beacon, conversation, config-drift, diagnostic-bundle, rate-limit,
// compute-budget and data-subject-request payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

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

/// Phase 504 — the conversation retention sweep purged one or more AI
/// conversations from a scope. One row per run that removed something;
/// a run that expired nothing writes no row (a purge trail records
/// deletions, not the absence of them), mirroring
/// `KnowledgeDocumentsPurged`.
///
/// The conversation ids ARE recorded, unlike `ConversationErased`: a
/// retention purge is a scope-level housekeeping act keyed by
/// conversation id, not by data subject, and a count alone cannot be
/// reconciled against the store afterwards. The ids are opaque GUIDs
/// and name no user.
type ConversationsPurgedPayload = {
    /// Scope whose conversations were swept (GP 4: one scope per run).
    ScopeId: string
    /// Conversation ids removed by this run, oldest activity first.
    ConversationIds: string list
    /// `ConversationIds.Length`, denormalised so a sink can aggregate
    /// without parsing the list.
    PurgedCount: int
    /// The age limit in whole seconds that selected them, when the
    /// policy set one — the row carries the policy that produced it.
    MaxAgeSeconds: int64 option
    /// The count limit, when the policy set one.
    MaxCount: int option
    /// Conversations the sweep selected but could not fully remove
    /// (a sibling blob's delete failed). Non-zero means the next sweep
    /// retries them; the trail carries the same loud signal the
    /// operator log does (GP 9).
    FailedCount: int
}