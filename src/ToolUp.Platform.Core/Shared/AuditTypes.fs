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
//
// Phase 347 — the payload records this union registers were carved into
// Shared/Audit/*AuditPayloads.fs (declaration order preserved) and the
// envelope / IAuditLog seam into Shared/Audit/AuditEnvelope.fs; this file
// keeps the `AuditEvent` union and its `eventTypeName` projection — the
// documented irreducible core (one union type plus its companion module).
// The union stays append-only: new payload records land in the lane file
// that owns their subsystem, new cases at the end of the union.

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
    /// An ephemeral session file store was evicted by the TTL sweep and
    /// then re-created (Phase 6p) — the scope's uploads were lost
    /// without any user action. Not emitted on first access.
    | SessionStoreReset of SessionStoreResetPayload
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
    /// Phase 730 — a grant on a policy-bearing module was recorded. The
    /// success twin of `GrantPolicyRefused`, closing the asymmetry Phase
    /// 551 shipped with: refusals were dashboardable, grants were not.
    | GrantRecorded of GrantRecordedPayload
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
    /// Phase 552 — a consent record was lodged awaiting the counterparty.
    /// Authority was requested, not created.
    | GrantConsentProposed of GrantConsentProposedPayload
    /// Phase 552 — a counterparty's signed approval was accepted. The row
    /// that makes a `RequiresCounterpartyApproval` grant possible.
    | GrantConsentApproved of GrantConsentApprovedPayload
    /// Phase 552 — a consent was withdrawn. Effective at the next call,
    /// because consent is verified on use.
    | GrantConsentRevoked of GrantConsentRevokedPayload
    /// Phase 552 — a presented consent record failed verification on a
    /// TRUST ground (bad signature, unregistered key, algorithm
    /// disagreement, wrong subject/party). Not emitted for ordinary
    /// lifecycle denials.
    | GrantConsentVerificationDenied of GrantConsentVerificationDeniedPayload
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
    /// Phase 772 — an outbound HTTP call was refused by the server-side
    /// `IEgressPolicy` before its socket opened. Reserved `_platform`
    /// scope. Carries origin + component, never the URL; one row per
    /// denial so a refused call is never silent.
    | EgressDenied of EgressDeniedPayload
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
    /// Phase 739 — the decryption key for a gated HLS media item was
    /// delivered. The grant twin of the `AuthorizationDenied` row the
    /// same endpoint already emits on a refusal, closing the asymmetry
    /// Phase 471 shipped with: refusals were queryable, grants were a
    /// log line.
    | MediaKeyDelivered of MediaKeyDeliveredPayload
    /// Phase 2c — a cloud-storage companion's call was rejected 401/403,
    /// i.e. the credential it holds is no longer accepted. The trail
    /// beside the Phase 2c health probe's alarm: the probe says the
    /// deployment is unwell, this says when it started, on which store,
    /// and doing what.
    | BlobStorageAuthFailed of BlobStorageAuthFailedPayload
    /// Phase 36.E — one invocation of the built-in cross-module AI tool
    /// family, allowed or refused. The read-side twin of Phase 36.D's
    /// consent decisions: those record what the user ALLOWED, this
    /// records what was actually reached, and only the two together
    /// answer "what did the agent read out of modules nobody named".
    | CrossModuleRead of CrossModuleReadPayload
    /// Phase 445 — a platform snapshot completed and its manifest landed
    /// on the backup target.
    | BackupCompleted of BackupCompletedPayload
    /// Phase 445 — a platform snapshot did not complete; no manifest was
    /// written.
    | BackupFailed of BackupFailedPayload
    /// Phase 445 — a restore drill restored, verified and passed.
    | RestoreDrillPassed of RestoreDrillPassedPayload
    /// Phase 445 — a restore drill did not pass; the health probe reports
    /// `Degraded` until one does.
    | RestoreDrillFailed of RestoreDrillFailedPayload

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
        | SessionStoreReset _ -> "SessionStoreReset"
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
        | GrantRecorded _ -> "GrantRecorded"
        | AdminMutationProposed _ -> "AdminMutationProposed"
        | AdminMutationApproved _ -> "AdminMutationApproved"
        | AdminMutationRejected _ -> "AdminMutationRejected"
        | AdminMutationApprovalRefused _ -> "AdminMutationApprovalRefused"
        | AdminMutationExpired _ -> "AdminMutationExpired"
        | GrantConsentProposed _ -> "GrantConsentProposed"
        | GrantConsentApproved _ -> "GrantConsentApproved"
        | GrantConsentRevoked _ -> "GrantConsentRevoked"
        | GrantConsentVerificationDenied _ -> "GrantConsentVerificationDenied"
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
        | EgressDenied _ -> "EgressDenied"
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
        | MediaKeyDelivered _ -> "MediaKeyDelivered"
        | BlobStorageAuthFailed _ -> "BlobStorageAuthFailed"
        | CrossModuleRead _ -> "CrossModuleRead"
        | BackupCompleted _ -> "BackupCompleted"
        | BackupFailed _ -> "BackupFailed"
        | RestoreDrillPassed _ -> "RestoreDrillPassed"
        | RestoreDrillFailed _ -> "RestoreDrillFailed"