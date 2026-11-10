# Audit event reference

<!-- GENERATED FILE — do not edit by hand. Regenerate with `dev-scripts/generate-audit-event-reference.ps1`
     (or `TOOLUP_REGEN_AUDIT_EVENT_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests`). The
     sources of truth are the platform's `AuditEvent` union in src/ToolUp.Platform.Core/Shared/AuditTypes.fs,
     its codec in src/ToolUp.Platform.Server/Server/AuditLog.fs, and each companion's `AuditEventFamily`. -->

Every audit event the platform emits (171 cases). All of them are recorded under the reserved `_platform.audit` source module, and every one is decodable for external replication — that is a build gate, not a convention (see [PLATFORM-SECURITY-RULES.md](../security/PLATFORM-SECURITY-RULES.md) AU-2).

**Read the first column when you are writing a SIEM rule or querying an archive.** The wire `EventType` is what is persisted and replicated; the F# case identifier is what you pattern-match on in SDK code. They are equal for almost every event, and the exceptions are listed below — they are pinned deliberately, because a wire string that has already left for a third-party sink cannot be migrated.

Cases whose wire discriminator differs from the F# identifier (3):

- `ModuleArtefactSigned` emits `ArtifactSigned`
- `ModuleArtefactVerified` emits `ArtifactVerified`
- `ModuleArtefactRejected` emits `ArtifactRejected`

> Note the near-collision: `ArtifactSigned` (module artefact signing) and `ArtefactSigned` (signing-key artefact) are two different events one letter apart. Alert rules must not glob them together.

| Event type (wire) | F# case | Payload |
|---|---|---|
| `UserLoggedIn` | — | `UserLoggedInPayload` |
| `TeamCreated` | — | `TeamCreatedPayload` |
| `MemberAdded` | — | `MemberAddedPayload` |
| `MemberRemoved` | — | `MemberRemovedPayload` |
| `MemberRoleChanged` | — | `MemberRoleChangedPayload` |
| `FileUploaded` | — | `FileUploadedPayload` |
| `FileDeleted` | — | `FileDeletedPayload` |
| `FileReprocessed` | — | `FileReprocessedPayload` |
| `DataStoreReset` | — | `DataStoreResetPayload` |
| `SessionStoreReset` | — | `SessionStoreResetPayload` |
| `AnalysisRun` | — | `AnalysisRunPayload` |
| `PermissionChanged` | — | `PermissionChangedPayload` |
| `NotificationSent` | — | `NotificationSentPayload` |
| `NotificationDeliveryFailed` | — | `NotificationDeliveryFailedPayload` |
| `HealthStateChanged` | — | `HealthStateChangedPayload` |
| `EncryptionKeyCreated` | — | `EncryptionKeyEventPayload` |
| `EncryptionKeyRotated` | — | `EncryptionKeyEventPayload` |
| `EncryptionKeyDestroyed` | — | `EncryptionKeyEventPayload` |
| `EncryptionKeyDestroyAcknowledged` | — | `EncryptionKeyDestroyAckPayload` |
| `EncryptionKeyUnreadable` | — | `EncryptionKeyUnreadablePayload` |
| `EntityCreated` | — | `EntityLifecycleEventPayload` |
| `EntityUpdated` | — | `EntityLifecycleEventPayload` |
| `EntityDeleted` | — | `EntityLifecycleEventPayload` |
| `AuditSinkDelivered` | — | `AuditSinkDeliveredPayload` |
| `AuditSinkFailed` | — | `AuditSinkFailedPayload` |
| `AuditSinkDeadLettered` | — | `AuditSinkDeadLetteredPayload` |
| `AuditEventDecodeFailed` | — | `AuditEventDecodeFailedPayload` |
| `NotificationSilentlySkipped` | — | `NotificationSilentlySkippedPayload` |
| `OAuthConnected` | — | `OAuthConnectedPayload` |
| `OAuthDisconnected` | — | `OAuthDisconnectedPayload` |
| `OAuthRefreshFailed` | — | `OAuthRefreshFailedPayload` |
| `OAuth1aConnected` | — | `OAuth1aConnectedPayload` |
| `OAuth1aDisconnected` | — | `OAuth1aDisconnectedPayload` |
| `OAuth1aSigningFailed` | — | `OAuth1aSigningFailedPayload` |
| `OAuthTokenRefreshed` | — | `OAuthTokenRefreshedPayload` |
| `OAuthTokenRefreshFailed` | — | `OAuthTokenRefreshFailedPayload` |
| `OAuthRefreshTokenInvalidated` | — | `OAuthRefreshTokenInvalidatedPayload` |
| `OAuthRefreshDeadLettered` | — | `OAuthRefreshDeadLetteredPayload` |
| `PlatformAdminAssigned` | — | `PlatformAdminAssignedPayload` |
| `PlatformAdminRevoked` | — | `PlatformAdminRevokedPayload` |
| `ShareTokenIssued` | — | `ShareTokenIssuedPayload` |
| `ShareTokenUsed` | — | `ShareTokenUsedPayload` |
| `ShareTokenRevoked` | — | `ShareTokenRevokedPayload` |
| `SessionRevoked` | — | `SessionRevokedPayload` |
| `AllSessionsRevoked` | — | `AllSessionsRevokedPayload` |
| `ServiceAccountCreated` | — | `ServiceAccountCreatedPayload` |
| `ServiceAccountPermissionsChanged` | — | `ServiceAccountPermissionsChangedPayload` |
| `ServiceAccountTokenMinted` | — | `ServiceAccountTokenMintedPayload` |
| `ServiceAccountTokenRevoked` | — | `ServiceAccountTokenRevokedPayload` |
| `ServiceAccountStatusChanged` | — | `ServiceAccountStatusChangedPayload` |
| `ConfigDrift` | — | `ConfigDriftPayload` |
| `DiagnosticBundleAccessed` | — | `DiagnosticBundleAccessedPayload` |
| `RateLimitWaited` | — | `RateLimitWaitedPayload` |
| `RateLimitRefused` | — | `RateLimitRefusedPayload` |
| `ComputeBudgetDenied` | — | `ComputeBudgetDeniedPayload` |
| `ComputeBudgetWarning` | — | `ComputeBudgetWarningPayload` |
| `DataSubjectRequest` | — | `DataSubjectRequestAuditPayload` |
| `ConversationStarted` | — | `ConversationStartedPayload` |
| `ConversationTurnAppended` | — | `ConversationTurnAppendedPayload` |
| `ConversationCompleted` | — | `ConversationCompletedPayload` |
| `ConversationErased` | — | `ConversationErasedPayload` |
| `TeamCreationDenied` | — | `TeamCreationDeniedPayload` |
| `TeamArchived` | — | `TeamArchivedPayload` |
| `TeamRestored` | — | `TeamRestoredPayload` |
| `TeamDeleted` | — | `TeamDeletedPayload` |
| `TeamOwnershipTransferred` | — | `TeamOwnershipTransferredPayload` |
| `TeamInviteIssued` | — | `TeamInviteIssuedPayload` |
| `TeamInviteAccepted` | — | `TeamInviteAcceptedPayload` |
| `TeamInviteAcceptedFromPending` | — | `TeamInviteAcceptedFromPendingPayload` |
| `TeamInviteAcceptedFromPendingFailed` | — | `TeamInviteAcceptedFromPendingFailedPayload` |
| `TeamInviteRevoked` | — | `TeamInviteRevokedPayload` |
| `TeamInviteRedeemed` | — | `TeamInviteRedeemedPayload` |
| `TeamInviteExpired` | — | `TeamInviteExpiredPayload` |
| `ConsentRecorded` | — | `ConsentEvent` |
| `AdImpressionRecorded` | — | `AdImpression` |
| `AdClickRecorded` | — | `AdClick` |
| `PremiumGranted` | — | `UserId * string * string option * DateTimeOffset` |
| `PremiumRevoked` | — | `UserId * string * string option * DateTimeOffset` |
| `AdSlotConfigCreated` | — | `string * string * DateTimeOffset` |
| `AdSlotConfigUpdated` | — | `string * string * DateTimeOffset` |
| `AdSlotConfigDeleted` | — | `string * string * DateTimeOffset` |
| `AnonymousSessionMigrated` | — | `AnonymousSessionMigratedPayload` |
| `AuthScopeResolutionFailed` | — | `ScopeResolutionFailedPayload` |
| `SurfaceDenied` | — | `SurfaceDeniedPayload` |
| `ArtifactSigned` | `ModuleArtefactSigned` | `ModuleArtefactSignedPayload` |
| `ArtifactVerified` | `ModuleArtefactVerified` | `ModuleArtefactVerifiedPayload` |
| `ArtifactRejected` | `ModuleArtefactRejected` | `ModuleArtefactRejectedPayload` |
| `SyntheticSampleGenerated` | — | `SyntheticSampleGeneratedPayload` |
| `SchemaOnlyAccessAttempted` | — | `SchemaOnlyAccessAttemptedPayload` |
| `GrantPolicyRefused` | — | `GrantPolicyRefusedPayload` |
| `UnconsentedGrantRefused` | — | `UnconsentedGrantRefusedPayload` |
| `GrantRecorded` | — | `GrantRecordedPayload` |
| `AdminMutationProposed` | — | `AdminMutationProposedPayload` |
| `AdminMutationApproved` | — | `AdminMutationApprovedPayload` |
| `AdminMutationRejected` | — | `AdminMutationRejectedPayload` |
| `AdminMutationApprovalRefused` | — | `AdminMutationApprovalRefusedPayload` |
| `AdminMutationExpired` | — | `AdminMutationExpiredPayload` |
| `GrantConsentProposed` | — | `GrantConsentProposedPayload` |
| `GrantConsentApproved` | — | `GrantConsentApprovedPayload` |
| `GrantConsentRevoked` | — | `GrantConsentRevokedPayload` |
| `GrantConsentVerificationDenied` | — | `GrantConsentVerificationDeniedPayload` |
| `ClassifiedFieldRead` | — | `ClassifiedFieldReadPayload` |
| `ClassifiedFieldWritten` | — | `ClassifiedFieldWrittenPayload` |
| `TenantProvisioned` | — | `TenantProvisionedPayload` |
| `TenantDeprovisioned` | — | `TenantDeprovisionedPayload` |
| `TenantLifecycleHookFailed` | — | `TenantLifecycleHookFailedPayload` |
| `TenantDataExported` | — | `TenantDataExportedPayload` |
| `TenantOffboardConfirmationRequested` | — | `TenantOffboardConfirmationRequestedPayload` |
| `TenantOffboardConfirmationApproved` | — | `TenantOffboardConfirmationApprovedPayload` |
| `TenantOffboardConfirmationRefused` | — | `TenantOffboardConfirmationRefusedPayload` |
| `TenantDeprovisionScheduled` | — | `TenantDeprovisionScheduledPayload` |
| `TenantDeprovisionCancelled` | — | `TenantDeprovisionCancelledPayload` |
| `RemotingMethodAudited` | — | `RemotingMethodAuditedPayload` |
| `AuthorizationDenied` | — | `AuthorizationDeniedPayload` |
| `HostActionDispatched` | — | `HostActionDispatchedPayload` |
| `EgressBlocked` | — | `EgressBlockedPayload` |
| `EgressDenied` | — | `EgressDeniedPayload` |
| `KnowledgeIndexLoadFailed` | — | `KnowledgeIndexLoadFailedPayload` |
| `OrphanedContentBlobReclaimed` | — | `OrphanedContentBlobReclaimedPayload` |
| `OrphanSweepCompleted` | — | `OrphanSweepCompletedPayload` |
| `ModelFitStarted` | — | `ModelFitStartedPayload` |
| `ModelFitCompleted` | — | `ModelFitCompletedPayload` |
| `ModelFitGateFailed` | — | `ModelFitGateFailedPayload` |
| `ModelFitBatchSubmitted` | — | `ModelFitBatchSubmittedPayload` |
| `ModelArtifactRegistered` | — | `ModelArtifactRegisteredPayload` |
| `ModelArtifactTransitioned` | — | `ModelArtifactTransitionedPayload` |
| `ModelArtifactTransitionDenied` | — | `ModelArtifactTransitionDeniedPayload` |
| `ModelArtifactTransitionAttributed` | — | `ModelArtifactTransitionAttributedPayload` |
| `ModelArtifactProvenanceAttached` | — | `ModelArtifactProvenanceAttachedPayload` |
| `ModelArtifactPromoted` | — | `ModelArtifactPromotionPayload` |
| `ModelScored` | — | `ModelScoredPayload` |
| `ModelScoreRefused` | — | `ModelScoreRefusedPayload` |
| `ModelEvaluated` | — | `ModelEvaluatedPayload` |
| `ModelPromotionPolicyEvaluated` | — | `ModelPromotionPolicyEvaluatedPayload` |
| `ModelArtifactSuperseded` | — | `ModelArtifactSupersededPayload` |
| `ModelRegistrationObserverFailed` | — | `ModelRegistrationObserverFailedPayload` |
| `DatasetSpillCreated` | — | `DatasetSpillCreatedPayload` |
| `DatasetSpillDeleted` | — | `DatasetSpillDeletedPayload` |
| `DatasetDeclassified` | — | `DatasetDeclassifiedPayload` |
| `DatasetRevintaged` | — | `DatasetRevintagedPayload` |
| `DatasetPolicyDenied` | — | `DatasetPolicyDeniedPayload` |
| `SchemaProposed` | — | `SchemaProposedPayload` |
| `SchemaApproved` | — | `SchemaApprovedPayload` |
| `SchemaChanged` | — | `SchemaChangedPayload` |
| `ExternalCallbackResolved` | — | `ExternalCallbackResolvedPayload` |
| `ExternalCallbackRejected` | — | `ExternalCallbackRejectedPayload` |
| `CompositionVerificationRecorded` | — | `CompositionVerificationRecordedPayload` |
| `CompositionCapabilityRefused` | — | `CompositionCapabilityRefusedPayload` |
| `GroundingEnvelopeMutated` | — | `GroundingEnvelopeMutatedPayload` |
| `GroundingMutationRefused` | — | `GroundingMutationRefusedPayload` |
| `DeploymentVerified` | — | `DeploymentVerifiedPayload` |
| `EvidenceChainWalked` | — | `EvidenceChainWalkedPayload` |
| `MediaKeyDelivered` | — | `MediaKeyDeliveredPayload` |
| `BlobStorageAuthFailed` | — | `BlobStorageAuthFailedPayload` |
| `BackupCompleted` | — | `BackupCompletedPayload` |
| `BackupFailed` | — | `BackupFailedPayload` |
| `RestoreDrillPassed` | — | `RestoreDrillPassedPayload` |
| `RestoreDrillFailed` | — | `RestoreDrillFailedPayload` |
| `ContactCreated` | — | `ContactCreatedPayload` |
| `ContactUpdated` | — | `ContactUpdatedPayload` |
| `ContactDeleted` | — | `ContactDeletedPayload` |
| `ContactOptInRecorded` | — | `ContactOptInRecordedPayload` |
| `ContactOptInWithdrawn` | — | `ContactOptInWithdrawnPayload` |
| `NotificationDeliveryRefused` | — | `NotificationDeliveryRefusedPayload` |
| `PublicationReaderSignedIn` | — | `PublicationReaderSignedInPayload` |
| `PublicationReaderGranted` | — | `PublicationReaderGrantChangedPayload` |
| `PublicationReaderRevoked` | — | `PublicationReaderGrantChangedPayload` |
| `PageSaved` | — | `PageSavedPayload` |
| `PageReseeded` | — | `PageReseededPayload` |
| `PageDetached` | — | `PageDetachedPayload` |
| `PageWriteRefused` | — | `PageWriteRefusedPayload` |

Payload record definitions live in the `src/ToolUp.Platform.Core/Shared/Audit/*AuditPayloads.fs` lane files compiled before the union; each case carries a doc comment explaining when it is emitted.

## Companion kinds

Kinds a companion declares through its own `AuditEventFamily` and registers from its compose step (35). Core carries them as `AuditEvent.Opaque`; the companion's assembly holds their event type and payload records.

| Event type (wire) | Registrant |
|---|---|
| `AnswerVerificationFlagged` | `ToolUp.AI` |
| `AnswerVerificationPassed` | `ToolUp.AI` |
| `ArtefactSigned` | `ToolUp.ArtefactSigning` |
| `AssetDeleted` | `ToolUp.AssetStore` |
| `AssetUploaded` | `ToolUp.AssetStore` |
| `BeaconRejected` | `ToolUp.AI` |
| `CertificateIssued` | `ToolUp.Facts` |
| `ContentScanned` | `ToolUp.KnowledgeBase` |
| `ConversationExported` | `ToolUp.AI` |
| `ConversationReplayed` | `ToolUp.AI` |
| `ConversationsPurged` | `ToolUp.AI` |
| `CrossModuleRead` | `ToolUp.AI` |
| `FactImportAccepted` | `ToolUp.Facts` |
| `FactImportRefused` | `ToolUp.Facts` |
| `FederationParticipantDropped` | `ToolUp.InterPlatform` |
| `FederationRoundCompleted` | `ToolUp.InterPlatform` |
| `FederationRunAborted` | `ToolUp.InterPlatform` |
| `FormSubmissionUpdated` | `ToolUp.Forms` |
| `FormSubmitted` | `ToolUp.Forms` |
| `KnowledgeDocumentDeduplicated` | `ToolUp.KnowledgeBase` |
| `KnowledgeDocumentsPurged` | `ToolUp.KnowledgeBase` |
| `KnowledgeIngestionDropped` | `ToolUp.RAG` |
| `KnowledgeOriginalRetrievalDenied` | `ToolUp.KnowledgeBase` |
| `KnowledgeOriginalRetrieved` | `ToolUp.KnowledgeBase` |
| `KnowledgeScopeErased` | `ToolUp.KnowledgeBase` |
| `PasskeyCredentialRegistered` | `ToolUp.AuthProviders.Passkey` |
| `PasskeyCredentialRemoved` | `ToolUp.AuthProviders.Passkey` |
| `PeerCallCompleted` | `ToolUp.InterPlatform` |
| `PeerCleanRoomDecision` | `ToolUp.InterPlatform` |
| `PeerJobCompleted` | `ToolUp.InterPlatform` |
| `PlatformDocumentDeleted` | `ToolUp.KnowledgeBase` |
| `PlatformDocumentUploaded` | `ToolUp.KnowledgeBase` |
| `SigningKeyRotated` | `ToolUp.ArtefactSigning` |
| `WorkflowActionExecuted` | `ToolUp.Forms` |
| `WorkflowTransitioned` | `ToolUp.Forms` |
