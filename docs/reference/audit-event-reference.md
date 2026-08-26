# Audit event reference

<!-- GENERATED FILE — do not edit by hand. Regenerate with `dev-scripts/generate-audit-event-reference.ps1`
     (or `TOOLUP_REGEN_AUDIT_EVENT_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests`). The
     sources of truth are the `AuditEvent` union in src/ToolUp.Platform.Core/Shared/AuditTypes.fs and
     the codec registry `auditEventCodecs` in src/ToolUp.Platform.Server/Server/AuditLog.fs. -->

Every audit event the SDK emits (163 cases). All of them are recorded under the reserved `_platform.audit` source module, and every one is decodable for external replication — that is a build gate, not a convention (see [PLATFORM-SECURITY-RULES.md](../security/PLATFORM-SECURITY-RULES.md) AU-2).

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
| `AnalysisRun` | — | `AnalysisRunPayload` |
| `PermissionChanged` | — | `PermissionChangedPayload` |
| `NotificationSent` | — | `NotificationSentPayload` |
| `NotificationDeliveryFailed` | — | `NotificationDeliveryFailedPayload` |
| `HealthStateChanged` | — | `HealthStateChangedPayload` |
| `EncryptionKeyCreated` | — | `EncryptionKeyEventPayload` |
| `EncryptionKeyRotated` | — | `EncryptionKeyEventPayload` |
| `EncryptionKeyDestroyed` | — | `EncryptionKeyEventPayload` |
| `EncryptionKeyDestroyAcknowledged` | — | `EncryptionKeyDestroyAckPayload` |
| `EntityCreated` | — | `EntityLifecycleEventPayload` |
| `EntityUpdated` | — | `EntityLifecycleEventPayload` |
| `EntityDeleted` | — | `EntityLifecycleEventPayload` |
| `FormSubmitted` | — | `FormSubmittedPayload` |
| `FormSubmissionUpdated` | — | `FormSubmissionUpdatedPayload` |
| `WorkflowTransitioned` | — | `WorkflowTransitionedPayload` |
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
| `PlatformDocumentUploaded` | — | `PlatformDocumentUploadedPayload` |
| `PlatformDocumentDeleted` | — | `PlatformDocumentDeletedPayload` |
| `ShareTokenIssued` | — | `ShareTokenIssuedPayload` |
| `ShareTokenUsed` | — | `ShareTokenUsedPayload` |
| `ShareTokenRevoked` | — | `ShareTokenRevokedPayload` |
| `ConversationExported` | — | `ConversationExportPayload` |
| `BeaconRejected` | — | `BeaconRejectedPayload` |
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
| `ConversationReplayed` | — | `ConversationReplayedPayload` |
| `AssetUploaded` | — | `AssetUploadedPayload` |
| `AssetDeleted` | — | `AssetDeletedPayload` |
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
| `WorkflowActionExecuted` | — | `WorkflowActionExecutedPayload` |
| `ConsentRecorded` | — | `ConsentEvent` |
| `AdImpressionRecorded` | — | `AdImpression` |
| `AdClickRecorded` | — | `AdClick` |
| `PremiumGranted` | — | `string * string * string option * DateTimeOffset` |
| `PremiumRevoked` | — | `string * string * string option * DateTimeOffset` |
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
| `PeerCallCompleted` | — | `PeerCallCompletedPayload` |
| `PeerJobCompleted` | — | `PeerJobCompletedPayload` |
| `PeerCleanRoomDecision` | — | `PeerCleanRoomDecisionPayload` |
| `FederationRoundCompleted` | — | `FederationRoundCompletedPayload` |
| `FederationParticipantDropped` | — | `FederationParticipantDroppedPayload` |
| `FederationRunAborted` | — | `FederationRunAbortedPayload` |
| `ArtefactSigned` | — | `ArtefactSignedPayload` |
| `SigningKeyRotated` | — | `SigningKeyRotatedPayload` |
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
| `KnowledgeOriginalRetrieved` | — | `KnowledgeOriginalRetrievedPayload` |
| `KnowledgeOriginalRetrievalDenied` | — | `KnowledgeOriginalRetrievalDeniedPayload` |
| `RemotingMethodAudited` | — | `RemotingMethodAuditedPayload` |
| `KnowledgeScopeErased` | — | `KnowledgeScopeErasedPayload` |
| `AuthorizationDenied` | — | `AuthorizationDeniedPayload` |
| `HostActionDispatched` | — | `HostActionDispatchedPayload` |
| `EgressBlocked` | — | `EgressBlockedPayload` |
| `KnowledgeIndexLoadFailed` | — | `KnowledgeIndexLoadFailedPayload` |
| `KnowledgeIngestionDropped` | — | `KnowledgeIngestionDroppedPayload` |
| `KnowledgeDocumentDeduplicated` | — | `KnowledgeDocumentDeduplicatedPayload` |
| `KnowledgeDocumentsPurged` | — | `KnowledgeDocumentsPurgedPayload` |
| `ContentScanned` | — | `ContentScannedPayload` |
| `OrphanedContentBlobReclaimed` | — | `OrphanedContentBlobReclaimedPayload` |
| `OrphanSweepCompleted` | — | `OrphanSweepCompletedPayload` |
| `PasskeyCredentialRegistered` | — | `PasskeyCredentialRegisteredPayload` |
| `PasskeyCredentialRemoved` | — | `PasskeyCredentialRemovedPayload` |
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
| `AnswerVerificationPassed` | — | `AnswerVerificationPayload` |
| `AnswerVerificationFlagged` | — | `AnswerVerificationPayload` |
| `FactImportAccepted` | — | `FactImportPayload` |
| `FactImportRefused` | — | `FactImportPayload` |
| `GroundingEnvelopeMutated` | — | `GroundingEnvelopeMutatedPayload` |
| `GroundingMutationRefused` | — | `GroundingMutationRefusedPayload` |
| `CertificateIssued` | — | `CertificateIssuedPayload` |
| `DeploymentVerified` | — | `DeploymentVerifiedPayload` |
| `EvidenceChainWalked` | — | `EvidenceChainWalkedPayload` |

Payload record definitions live beside the union in `src/ToolUp.Platform.Core/Shared/AuditTypes.fs`; each case carries a doc comment explaining when it is emitted.
