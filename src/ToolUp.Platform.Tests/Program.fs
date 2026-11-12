// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.AI
open ToolUp.Platform.Tests.RAG
open ToolUp.Platform.Tests.Graph
open ToolUp.Platform.Tests.Remoting
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the lists whose test files reach the umbrella
/// (Compose, Lifecycle, Files) or a companion built on it. Which list is in which pack, and why:
/// docs/migrations/1194-platform-pack-partition.md. `[<Tests>]` alone does NOT get a list into this
/// run, which is why the registration guard runs over it.
let private registeredTests =
    testList "ToolUp.Platform.Tests" [
        ConfigMigrationTests.tests
        SeedDataLoaderTests.tests
        ParquetDatasetCodecTests.contractTests
        ParquetDatasetCodecTests.codecTests
        IModelFitProviderContract.tests
        IModelFitProviderContract.opacityTests
        IModelScorerContract.tests
        IModelEvaluationPlanMetricsContract.tests
        IExternalComputeDispatcherContract.tests
        IExternalComputeDispatcherContract.wireTests
        IExternalComputeDispatcherContract.portabilityAudit
        IExternalComputeDispatcherContract.conformanceTests
        IExternalComputeDispatcherContract.selfTests
        ModelEvaluationTests.tests
        ModelPromotionTransferTests.tests
        RunJobTests.tests
        ModelFitBatchTests.tests
        ModelExecutionApiTests.tests
        ModelExecutionComposeTests.tests
        IngestionStatusTests.tests
        IngestionAttemptStoreTests.tests
        IngestionRetryTests.tests
        IngestionEmbedderRetryTests.tests
        IngestionBackpressureTests.tests
        DurableIngestionQueueTests.tests
        MetricRegistryTests.tests
        IFactTableWriterContract.tests "DefaultFactTableWriter" IFactTableWriterContract.defaultWriterFactory
        IFactTableWriterContract.defaultWriterObligationTests
        IFactTableWriterContract.preflightTests
        IFactTableWriterContract.tests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableWriterContract.notifyingWriterFactory
        IFactTableWriterContract.provenanceTests "DefaultFactTableWriter" IFactTableWriterContract.defaultWriterFactory
        IFactTableWriterContract.provenanceTests "DelegateTableWriter" IFactTableWriterContract.delegateWriterFactory
        IFactTableWriterContract.provenanceTests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableWriterContract.notifyingWriterFactory
        IFactTableWriterContract.provenanceTests
            "notifying decorator over DelegateTableWriter"
            IFactTableWriterContract.notifyingDelegateWriterFactory
        IFactTableWriterContract.decoratorTests "notifying decorator" IFactTableWriterContract.notifying
        IFactTableWriterContract.decoratorTests
            "DelegateTableWriter, for a table it does not hold"
            IFactTableWriterContract.delegatePassThrough
        IFactTableWriterContract.exclusivityTests "DefaultFactTableWriter" IFactTableWriterContract.defaultWriterFactory
        IFactTableWriterContract.exclusivityTests "DelegateTableWriter" IFactTableWriterContract.delegateWriterFactory
        IFactTableWriterContract.exclusivityTests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableWriterContract.notifyingWriterFactory
        IFactTableWriterContract.exclusivityTests
            "notifying decorator over DelegateTableWriter"
            IFactTableWriterContract.notifyingDelegateWriterFactory
        IFactTableWriterContract.runOptionsTests "DefaultFactTableWriter" IFactTableWriterContract.defaultWriterFactory
        IFactTableWriterContract.runOptionsTests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableWriterContract.notifyingWriterFactory
        IFactTableWriterContract.runOptionsTests "DelegateTableWriter" IFactTableWriterContract.delegateWriterFactory
        IFactTableWriterContract.runOptionsTests
            "notifying decorator over DelegateTableWriter"
            IFactTableWriterContract.notifyingDelegateWriterFactory
        IFactTableOrphanSweepContract.tests "DefaultFactTableWriter" IFactTableOrphanSweepContract.defaultWriterFactory
        IFactTableOrphanSweepContract.tests "DelegateTableWriter" IFactTableOrphanSweepContract.delegateWriterFactory
        IFactTableWriterContract.runRecordTests "DefaultFactTableWriter" IFactTableWriterContract.defaultWriterFactory
        IFactTableWriterContract.runRecordTests "DelegateTableWriter" IFactTableWriterContract.delegateWriterFactory
        IFactTableWriterContract.runRecordTests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableWriterContract.notifyingWriterFactory
        FactTableRunTests.tests
        IFactTableOrphanSweepContract.tests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableOrphanSweepContract.notifyingWriterFactory
        CanonicalFrameTests.tests
        FactStoreTests.tests
        FactWriteTests.tests
        FactStoreTests.factIdTests
        FactStoreTests.auditAndFreshnessTests
        FactStoreTests.batchAssertTests
        FactStoreTests.populationRegistryTests
        FactStoreTests.populationScaleTests
        FactStoreTests.surfaceTests
        FactStoreTests.surfacePopulationRegistryTests
        FactStoreTests.metricSurfaceTests
        FactStoreTests.metricSurfaceScaleTests
        FactStoreTests.indexTests
        FactStoreTests.indexPopulationRegistryTests
        FactStoreTests.pointReadIndexTests
        FactStoreTests.taintIndexSourceTests
        CanonicalMethodTests.tests
        FactInvalidationTests.tests
        CoherenceCheckTests.tests
        ProvenanceGraphTests.tests
        ProvenanceGraphWireTests.tests
        GroundingWiringTests.tests
        JobSchedulerTests.tests
        ScheduledJobDeclarationTests.tests
        ModuleQueryBusTests.tests
        SecretStoreAtRestPostureValidatorTests.tests
        DataProtectionBackendTests.tests
        PresenceApiTests.tests
        AuthProviderTests.tests
        OidcJwksTtlTests.tests
        AuditTests.tests
        RateLimitTests.tests
        RateLimitTests.middlewareTests
        IdempotencyTests.tests
        ReportingComposeTests.tests
        ReportingAuthorizationTests.tests
        DeckExportSeamTests.tests
        DeckExportSeamTests.bundleTests
        DataSubjectRequestTests.tests
        DataSubjectRequestTests.authorizationTests
        PostgresFactStoreTests.offlineTests
        PostgresFactStoreTests.tests
        TransactionalDispatcherTests.tests
        NotificationPreferenceTests.tests
        WhatsAppChannelTests.tests
        HnswVectorStoreTests.tests
        PgvectorVectorStoreTests.tests
        PostgresFullTextIndexTests.tests
        SparseIndexCompositionTests.tests
        RedisEmbeddingCacheTests.tests
        LocalEmbeddingScopeTests.scopeKeyTests
        LocalEmbeddingScopeTests.isolationTests
        LocalEmbeddingScopeTests.resetTests
        LocalEmbeddingScopeTests.persistenceTests
        LocalEmbeddingScopeTests.backwardCompatibilityTests
        LocalEmbeddingScopeTests.capabilityProbeTests
        LocalEmbeddingScopeTests.cacheKeyingTests
        LocalEmbeddingScopeTests.crossScopeRetrievalTests
        OcrProviderTests.tests
        ContentScannerTests.tests
        KnowledgeUserScopeIsolationTests.tests
        VectorisationHookTests.tests
        VectorisationHookTests.stagedTests
        RagTelemetryTests.tests
        VectorScopeProjectionTests.tests
        ExtractorTests.tests
        TerminalStatusAnnouncementTests.tests
        ConfigValidatorAggregatorTests.tests
        ConfigValidatorSecurityClassTests.tests
        PreflightRuleClassTests.tests
        CorsCredentialsWildcardBootTests.tests
        ComponentIdentityTests.tests
        CompositionManifestTests.tests
        HostedServiceGateTests.tests
        BootstrapRefusalTests.tests
        CompositionDiffTests.tests
        ToolUp.Platform.Tests.Composition.CompositionBaselineTests.tests
        CompanionCapabilityTests.tests
        CompositionCapabilityGateTests.tests
        SeamAuthorityTests.tests
        SeamAuthorityEnforcementTests.tests
        SeamAwareServiceProviderTests.tests
        CompositionValidatorTests.tests
        InvariantRuleManifestTests.tests
        ModuleGraphRuleTests.tests
        RuleVersioningTests.tests
        DataVocabularyTests.tests
        ComponentIdCorrelationTests.tests
        ComponentConfigTests.tests
        ComponentRequirementsTests.tests
        ComposableSurfaceTests.tests
        CompositionProfileSurfaceTests.tests
        EventTopologyTests.tests
        DataFootprintTests.tests
        ScaleReadinessTests.tests
        ApplianceProfileTests.tests
        EntitlementTokenTests.tests
        AuthorizationSurfaceTests.tests
        ModuleSurfaceTests.tests
        ModuleBuilderSemanticsTests.tests
        CompositionGroundingTests.tests
        ComponentProvenanceTests.tests
        HostEnvelopeTests.tests
        ComponentHealthRollupTests.tests
        ResourceEnvelopeTests.tests
        ComponentLifecycleTests.tests
        CompositionHotSwapTests.tests
        CompositionDescriptorTests.tests
        CompositionDescriptorVersionTests.tests
        DescriptorCompletenessTests.tests
        CompositionDryRunTests.tests
        CompositionUpgradePlanTests.tests
        TenantCompositionPresetTests.tests
        ConfigReferenceTests.tests
        ProjectionsTests.tests
        ConfigStartupModeTests.tests
        ConfigResolverTests.tests
        ConfigProfileTests.tests
        AlertRuleEngineTests.tests
        ServiceStatusBoardApiHandlerTests.tests
        DeploymentReadinessReportTests.tests
        testSequencedGroup
            "env-mutating-config-validators"
            (testList "env-mutating config validators (Phase 653)" [
                AIProviderEnvValidatorTests.providerTests
                AIProviderEnvValidatorTests.modelTests
                AIProviderProbeValidatorTests.tests
                OidcAuthValidatorTimeoutTests.tests
            ])
        AIProviderHealthTests.claudeTests
        AIProviderHealthTests.openAiTests
        RedactionAllowlistParityTests.tests
        OidcSignInContractTests.tests
        OidcBearerStrategyTests.tests
        GitHubAuthProviderTests.tests
        GitHubAppFlowTests.tests
        GoogleAnalyticsTests.tests
        LdapAuthProviderTests.tests
        ScimProvisioningTests.tests
        PasskeyAuthProviderTests.tests
        PerScopeKeyResolverWiringTests.unwiredDestroyTests
        PerScopeKeyResolverWiringTests.wiringValidatorTests
        PerScopeKeyResolverWiringTests.wiringDiagnosticsTests
        PerScopeKeyResolverWiringTests.keyMaterialTests
        TenantLifecycleAggregatorTests.tests
        LifecycleSummaryStoreTests.tests
        OffboardConfirmationTests.tests
        ScheduledDeprovisionTests.tests
        PrincipalRegistryTests.tests
        ITenantLifecycleContract.tests
        ILifecycleLockContract.tests
        BlobEntityStoreTests.tests
        ConditionalDataObjectStoreTests.tests
        EntityQueryTests.tests
        EntityGraphProjectionTests.tests
        OtelActivitySinkTests.tests
        ServerModuleMetricsTests.tests
        ObservabilityHandlersTests.tests
        ObservabilityHandlersTests.datadogReadbackTests
        AnonymousSessionBindingTests.tests
        BlobCorruptionChaosTests.tests
        DocumentStoreAdoptersTests.tests
        BlobPlatformAIKeyStoreTests.tests
        MultiPlatformProviderResolutionTests.tests
        AIProviderFallbackTests.tests
        PlatformAIKeysHandlerRbacTests.tests
        AISurfaceCapabilityTests.tests
        AIStreamFramingPinTests.tests
        AIChatWorkerTests.tests
        AITokenBudgetTests.tests
        AIProviderModelOverrideTests.tests
        AISpendBudgetTests.tests
        ConversationReplayStreamTests.tests
        IngestionStatusStreamTests.tests
        ToolOutcomeTests.tests
        ConversationRecordTests.tests
        AgentLoopRailsTests.tests
        TurnOutcomeStatusTests.tests
        GatesAdmitOrderTests.tests
        ProviderProfileApiTests.tests
        TeamProviderTests.tests
        ProviderOAuthFlowTests.claudeOAuthContractTests
        ProviderOAuthFlowTests.openAiOAuthContractTests
        ProviderOAuthFlowTests.claudeProviderFlowContractTests
        ProviderOAuthFlowTests.openAiProviderFlowContractTests
        ProviderOAuthFlowTests.tests
        AuditEventRegistryTests.tests
        AICancellationDispatchInstanceValidatorTests.tests
        AnonymousAIModeValidatorTests.tests
        SecurityHeadersValidatorTests.tests
        SecurityHeadersValidatorTests.cspNonceCacheValidatorTests
        SecurityHeadersValidatorTests.baselineFloorTests
        InternetReadinessScorecardTests.tests
        ForwardedHeadersTrustTests.tests
        FileManagementTests.tests
        ModuleApiFactoryTests.tests
        FastPathBeaconOwnershipTests.tests
        FastPathBeaconIdempotencyTests.tests
        FastPathSequencerTelemetryTests.tests
        FastPathTriageResolverTests.tests
        HnswFidelityTests.tests
        ScopeResolutionFailClosedTests.tests
        AccessContextFactoryTests.tests
        ScopeKeyTests.tests
        IExternalApiConnectorContract.kitTests
        ExternalApiConnectorBindingTests.tests
        HttpTransportBindingTests.tests
        PlatformTestingFrameworkTests.tests
        CitationNormaliserTests.tests
        InMemoryBM25IndexTests.tests
        IndexLifecycleTests.tests
        CrossIndexErasureConformanceTests.tests
        ScopedSnapshotIndexTests.tests
        AIAgentEngineClientResidentAuthorizationTests.tests
        AIAllowlistDiagnosticsTests.tests
        DevVersionEndpointTests.tests
        BuildIdentityEndpointTests.tests
        ClientToolDispatchContractBindings.tests
        SampleClientToolDispatchTests.tests
        UiAwarenessInspectToolTests.tests
        UiInspectionContractBindings.tests
        ListActiveActionsToolTests.tests
        McpHostTests.tests
        HostRenderBoundaryTests.tests
        HostedTreeUsageExportTests.tests
        HostedTreeA11yTests.tests
        AccessibilityAssertionsTests.tests
        LiveSessionHostTests.tests
        TreePatchChannelTests.tests
        HostStateProjectionTests.tests
        HostRouteContractTests.tests
        PublicRenderingTests.tests
        SitemapSearchIndexTests.tests
        PublicEnumerationGateTests.tests
        NarrativeFromDataTests.tests
        NarrativeFactsTests.tests
        NarrativeChartsTests.tests
        EnumerableRoutesTests.tests
        NavStructuredDataTests.tests
        TagFeedTests.tests
        NarrativeDataExportTests.tests
        PaginationTests.tests
        FacetedBrowseTests.tests
        NavComposeTests.tests
        SiteStructureTests.tests
        RenderCacheTests.tests
        RenderCoalescingTests.tests
        ResolvedContentTests.tests
        IndexNowTests.tests
        MultiSiteTests.tests
        StructuredDataConformanceTests.tests
        GatedSsrTests.tests
        DirectoryRoleApiGrantTests.tests
        KnowledgeSurfaceTests.tests
        KnowledgeOriginalRetrievalTests.tests
        KnowledgeSignedOriginalUrlTests.tests
        KnowledgeScopeResetAuditTests.tests
        RAGVacuumJobHandlerTests.tests
        ToolAwareRagFramingTests.tests
        FactFirstRetrievalTests.tests
        AnswerVerifierTests.tests
        DisclosureEgressTests.tests
        DisclosureExportWebhookTests.tests
        DisclosureTaintTests.tests
        MultiPartyDisclosureTests.tests
        DeclassificationBudgetTests.tests
        DeclassificationBudgetAmendmentTests.subjectTests
        DeclassificationBudgetAmendmentTests.applicationTests
        DeclassificationBudgetAmendmentTests.loweringTests
        DeclassificationBudgetAmendmentTests.arcTests
        DeclassificationBudgetAmendmentTests.costTests
        FactResolverComposeTests.tests
        FactQueryToolTests.tests
        PopulationQueryToolTests.tests
        ScopeChokePointTests.tests
        ModelInputEntryTests.tests
        ModelInputEntryTests.renderTests
        CoverageToolTests.tests
        CoverageNarrativeTests.tests
        AnswerPlannerTests.tests
        FactClauseFeederTests.tests
        RagFactTierTests.tests
        GroundedNarrativeTests.tests
        NarrativeOffenceHistoryTests.tests
        ReportSubscriptionScopeTests.tests
        AssistantFactScopeTests.tests
        AIToolResultBudgetTests.tests
        AIToolSchemaTests.tests
        AIToolDispatchRbacTests.tests
        ToolApprovalTests.tests
        GrantPolicyTests.tests
        AccessAttestationTests.tests
        GrantGovernanceTests.tests
        AIQueryabilityOptInTests.tests
        ClassificationSeamTests.tests
        AIConsentTests.tests
        AICrossModuleAuditTests.tests
        GroundingCertificateTests.tests
        CertificateEnvelopeTests.tests
        GroundingSigningConvergenceTests.tests
        FactImportTests.tests
        CertificateIssuanceTransparencyTests.tests
        DeploymentVerificationReportTests.tests
        EgressPolicyTests.tests
        HostingBaselineTests.tests
        RouteEffectContainmentTests.tests
        TrustBoundaryTests.tests
        StaticCorpusContract.tests
        MetadataFilterContract.tests
        QueryRewriteContract.tests
        QueryRewriteComposeTests.tests
        CitationSpanContract.tests
        SparseAnalyzerContract.tests
        KnowledgeUploadPolicyTests.tests
        KnowledgeDedupTests.tests
        KbQuotaRetentionTests.tests
        KbVersioningTests.tests
        KbVersioningTests.ingestionStatusTruth
        KnowledgeIndexReadTests.tests
        KbObjectRetentionTests.tests
        KbDocumentTagsTests.tests
        KbBulkImportTests.tests
        BrandingTests.tests
        FelizLayoutAdapterTests.tests
        AssetStoreTests.tests
        AssetStoreTests.rendererContract
        AssetStoreTests.phase967Tests
        EdgeCacheTests.tests
        DeliveredEgressTests.tests
        ContentAuthoringTests.tests
        ContentAdminAuthorizationTests.tests
        TeamCreationPolicyTests.tests
        TeamCreationPolicyTests.integrityTests
        TeamCreationPolicyTests.quotaTests
        DirectAddIdentityProofTests.tests
        TeamOwnershipTransferTests.tests
        TeamAdminLifecycleTests.tests
        UserMembershipTeardownTests.tests
        TeamInvitationTests.tests
        TeamInvitationTests.pendingInviteExpiryAuditTests
        TeamInvitationTests.expiredInviteVisibilityTests
        TeamInvitationTests.inviteExpiryNotificationTests
        TeamInvitationTests.activeTeamPolicyTests
        TeamInvitationTests.checkMyInvitesTests
        HttpBodyBindingTests.tests
        ModuleIdentityTests.tests
        AnonymousModeContractTests.tests
        AnonymousModeContractTests.exposureTests
        ModuleSurfaceRequirementTests.tests
        ModuleSurfaceRequirementTests.visibilityTests
        CompositionInspectorHandlerTests.roleGateTests
        CompositionInspectorHandlerTests.panelTests
        CompositionInspectorHandlerTests.emptyStateTests
        CompositionInspectorHandlerTests.exportTests
        CompositionInspectorHandlerTests.contractShapeTests
        ReflectionToolsTests.tests
        HomeOverviewTests.countAffordanceTests
        HomeOverviewTests.overviewScopeAiTests
        HomeOverviewTests.overviewActiveAiPresentTests
        HomeOverviewTests.widgetDataTests
        HomeOverviewTests.pinningTests
        RevokeOnIssuerRemovedTests.tests
        SubjectDowngradeObservabilityTests.tests
        TelemetryZeroCostGateTests.tests
        DispatcherBodyAndArgFastpathTests.tests
        ServerRemotingTailTests.tests
        ShutdownDrainTests.tests
        FromContextAsyncBuildOnceTests.tests
        DeployPlaneTests.tenantFleetTests
        DeployPlaneTests.buildOrchestratorTests
        DeployPlaneTests.deployPipelineTests
        DeployPlaneTests.containerSchedulerInMemoryTests
        DeployPlaneTests.containerSchedulerDockerLocalTests
        DeployPlaneTests.dockerWireDtoTests
        DeployPlaneTests.deployPlanDefaultPipelineTests
        DeployPlaneTests.deployPlanFallbackTests
        DeployPlaneTests.deployPlanMutationCheck
        DeployPlaneTests.deployPlanDiffTests
        EvidenceChainWalkerTests.hopCountInvarianceTests
        EvidenceChainWalkerTests.honestAbsenceTests
        EvidenceChainWalkerTests.verdictReachabilityTests
        EvidenceChainWalkerTests.capRefusalTests
        EvidenceChainWalkerTests.auditedReadTests
        EvidenceChainWalkerTests.digestTests
        EvidenceChainWalkerTests.reportSectionTests
        EvidenceBundleExportTests.bundleDeterminismTests
        EvidenceBundleExportTests.statementWrappingTests
        EvidenceBundleExportTests.nestedAttestationRulingTests
        EvidenceBundleExportTests.pureVerifierTests
        EvidenceBundleExportTests.claimBoundaryTests
        EvidenceBundleExportTests.signedRoundTripTests
        EvidenceBundleExportTests.fixtureEmissionTests
        EvidenceChainBreakTests.corpusPlacementTests
        EvidenceChainBreakTests.healthyBaselineTests
        EvidenceChainBreakTests.chainBreakCorpusTests
        EvidenceChainBreakTests.chainFalsificationTests
        EvidenceChainBreakTests.bundleTamperCorpusTests
        EvidenceChainBreakTests.bundleFalsificationTests
        EvidenceChainBreakTests.documentTamperCorpusTests
        EvidenceChainBreakTests.absentVsBrokenTests
        EvidenceChainBreakTests.severedAncestorEdgeTests
        EvidenceChainBreakTests.severedAncestorReportTests
        BootVerificationPreflightTests.tests
        GroundedAnswerChainJoinTests.tests
        GroundingEnvelopeSealTests.tests
        CompositionManifestLiveInterfaceTests.tests
        DeployPlaneTests.defaultPipelineRollbackTests
        PlatformPeerTests.inProcessTests
        PlatformPeerTests.workedExampleTests
        PlatformPeerTests.audienceBindingTests
        PlatformPeerTests.jobPollOwnershipTests
        PeerFederationTests.fanoutTests
        PeerFederationTests.cascadeTests
        PeerFederationTests.proxyForwardingTests
        CleanRoomBrokerTests.tests
        CleanRoomGateTests.structuralTests
        CleanRoomGateTests.enforcementTests
        CleanRoomGateTests.substitutionTests
        CleanRoomGateTests.compositionTests
        CleanRoomGateTests.wireTests
        TemplateApprovalTests.mutationTests
        TemplateApprovalTests.signatureTests
        TemplateApprovalTests.lifecycleTests
        TemplateApprovalTests.gateTests
        TemplateApprovalTests.compositionTests
        TemplateApprovalTests.handshakeTests
        TemplateApprovalTests.queueTests
        PrivacyBudgetLedgerTests.policyTests
        PrivacyBudgetLedgerTests.ledgerContractTests
        PrivacyBudgetLedgerTests.atomicityTests
        PrivacyBudgetLedgerTests.gateTests
        NoiseMechanismTests.samplerTests
        NoiseMechanismTests.policyTests
        NoiseMechanismTests.cohortTests
        NoiseMechanismTests.gateTests
        NoiseMechanismTests.compositionTests
        CohortActivationTests.canonicalTests
        CohortActivationTests.authorisationTests
        CohortActivationTests.purposeTests
        CohortActivationTests.revocationTests
        CohortActivationTests.pipelineTests
        CohortActivationTests.egressTests
        CohortActivationTests.tokenTests
        CohortActivationTests.compositionTests
        OutboundSignalFeedTests.revocationTests
        OutboundSignalFeedTests.boundTests
        OutboundSignalFeedTests.budgetTests
        OutboundSignalFeedTests.restartTests
        OutboundSignalFeedTests.noiseTests
        OutboundSignalFeedTests.validationTests
        OutboundSignalFeedTests.operatorTests
        OutboundSignalFeedTests.stateStoreTests
        SignedShapeSeparatorTests.registryTests
        SignedShapeSeparatorTests.pinnedDigestTests
        PeerSurfaceTests.tests
        AggregatePeerSurfaceTests.tests
        AggregateLongRunningFrontingTests.tests
        FederationPreflightTests.tests
        FederationWireConformanceTests.tests
        CrossRuntimeFederationConformanceTests.tests
        PeerJobRetentionTests.tests
        PeerJwtReplayTests.guardTests
        PeerJwtReplayTests.replayDefenceTests
        PeerJwtReplayTests.callScopingTests
        PeerComposeKnobTests.jobRetentionTests
        PeerComposeKnobTests.roundOrchestratorTests
        PeerComposeKnobTests.tokenPolicyTests
        PeerComposeKnobTests.reachabilityTests
        PeerAudienceBindingTests.bindingTests
        PeerAudienceBindingTests.postureTests
        PeerDelegationVerificationTests.hostVerificationTests
        PeerDelegationVerificationTests.userContextClaimTests
        PeerRobustnessTests.base64UrlSignatureTests
        PeerRobustnessTests.profileNoDowngradeTests
        PeerRobustnessTests.asymmetricProviderTests
        PeerRobustnessTests.defaultCompositionUnchangedTests
        PeerWireHardeningTests.requestBodyCapTests
        PeerWireHardeningTests.pollCorrelationTests
        PeerWireHardeningTests.contractBindingTests
        PeerCascadeBudgetAuthorityTests.budgetAuthorityTests
        PeerCascadeBudgetAuthorityTests.cascadeShapeTests
        PeerCascadeBudgetAuthorityTests.cascadeCompatibilityTests
        PeerTransportTimeoutTests.deadlineTests
        PeerTransportTimeoutTests.cancellationTests
        PeerTransportTimeoutTests.fanoutReachTests
        PeerTransportTlsTests.acceptRuleTests
        PeerTransportTlsTests.transportTests
        PeerTransportTlsTests.handshakeFetchTests
        PeerTransportTlsTests.registryTests
        PeerTransportTlsTests.registryRemoveTests
        PeerTransportTlsTests.composeTests
        FederatedIdentitySanitisationTests.parityTests
        FederatedIdentitySanitisationTests.negativeControlTests
        FederatedIdentitySanitisationTests.boundaryDetailTests
        RoundOrchestratorTests.tests
        CommutativeCipherTests.tests
        CommutativeCipherTests.selfTests
        GiraffeStockHelperTests.tests
        IPeerAuditTransparencyContract.tests
        IPeerCapabilityNegotiationContract.tests
        IPeerNonFSharpSdkContract.tests
        ArchitectureFitnessTests.tests
        BaselineMergeDriverTests.tests
        BackupCoordinatorTests.tests
        SeamPrincipalTests.tests
        AdversarialFailClosedTests.tests
        ConstantTimeCompareTests.tests
        ModuleLoadOutcomeTests.tests
        HydrationParityTests.tests
        SecondBindingNeutralityTests.tests
        ComponentRegistryContract.tests
        ComponentRegistryContract.selfTests
        ModuleContract.tests
        ModuleContract.referenceTests
        ModuleContract.selfTests
        ThreatLensRegressionSuite.tests
        ReactiveRecomputeComposeTests.tests
        UploadValidationTests.contractTests
        UploadValidationTests.tests
        SessionStoreResetTests.tests
        IsolatedExecutionProfileTests.profileTests
        IsolatedExecutionProfileTests.postureTests
        IsolatedExecutionProfileTests.gateTests
        IsolatedExecutionProfileTests.structuralTests
        IsolatedExecutionProfileTests.releaseTests
        ComputeBackendRegistryTests.registryTests
        ComputeBackendRegistryTests.routingTests
        ComputeBackendRegistryTests.refusalTests
        ComputeBackendRegistryTests.handleRoundTripTests
        ComputeBackendRegistryTests.observabilityTests
        ComputeBackendRegistryTests.gp13Tests
        ComputeBackendRegistryTests.structuralTests
        ComputeBudgetTests.tests
        DataObjectOrphanSweepTests.tests
        DataObjectOrphanSweepTests.validatorTests
        DataObjectOrphanSweepTests.composeTests
        DataObjectOrphanSweepTests.inBandGraceTests
        RagConfigValidatorExtensionTests.tests
        ExternalCallbackTests.tests
        HttpComputeDispatcherTests.tests
        FederatedModelExecutionTests.tests
        ExternalModelFitTests.tests
        ScopeEnumerationSweepTests.tests
        CountersignatureRegistryTests.subjectTests
        CountersignatureRegistryTests.completenessTests
        CountersignatureRegistryTests.invalidationTests
        CountersignatureRegistryTests.revocationTests
        CountersignatureRegistryTests.signatureTests
        CountersignatureRegistryTests.storeTests
        CountersignatureRegistryTests.queueTests
        CountersignatureRegistryTests.parityTests
        DecoderAlgebraTests.tests
        JsonDecoderAlgebraTests.tests
        ToolEffectEnvelopeTests.tests
        ToolGateProofOracleTests.tests
        TaintFlowProofOracleTests.tests
        MetricsHistoryFlusherTests.tests
        ConversationRetentionTests.tests
        ReportingAIToolsTests.tests
        ConversationListingTests.tests
        ConversationVisibilityTests.tests
        FactBrowseTests.tests
        FactDeclarationsValidatorTests.tests
        DelegateFactTests.tests
        FactsOperatorTaskTests.tests
        TeamOutputVisibilityTests.tests
        FactPublicationTests.tests
        Phase971InterPlatformTests.tests
        Phase971KnowledgeBaseTests.tests
        FactPublicationTests.phase971Tests
        Phase971PlatformStoresTests.tests
        Phase971PlatformRecordsTests.tests
        Phase971AITests.tests
        Phase971PublicRenderingTests.tests
        DeclaredPagesTests.tests
        DeclaredDataSourcesTests.tests
        ReferenceDataTests.tests
        OidcAdmissionTests.tests
        Phase971PlatformTenantTests.tests
        ProviderExceptionTests.tests
        OidcSignInSpaPrecedenceTests.tests
        ServerAppOrderTests.tests
        ServerBindAddressTests.tests
        PageDocumentStoreTests.tests
        AuditFireAndForgetTests.tests
        LineageDurabilityTests.tests
        AuditCallSiteFireAndForgetTests.tests
        FileApiMountTests.tests
        StreamAuthBothSignInsTests.tests
        DeliveryTriggerTests.tests
        ToleranceTriggerTests.tests
        DeliveryCompletedTests.tests
        FactRenderToolTests.tests
        PipelineToolTests.tests
        FactTablesToolTests.tests
        AggregateMetricToolTests.tests
        PipelineAssertionTests.tests
        SpecToolTests.tests
        ISpecRefusalContract.tests
            "PipelineRefusal"
            typeof<ToolUp.Facts.PipelineRefusal>
            ISpecRefusalContract.pipelineRefusals
        ISpecRefusalContract.tests
            "SpecDoorRefusal"
            typeof<ToolUp.Facts.SpecDoorRefusal>
            ISpecRefusalContract.doorRefusals
        MethodRegistryTests.tests
        ISpecRefusalContract.tests "MethodRefusal" typeof<ToolUp.Facts.MethodRefusal> MethodRegistryTests.methodRefusals
        PipelineProfileTests.tests
        PageRequestResolverTests.tests
        LayoutToolsTests.tests
        NarrativeCalculationTests.tests
        LanguageProfileTests.tests
        IdentitySeamCensusTests.tests
    ]

let private deliberatelyUnregistered: TestRegistrationGuard.Exemption list = []

/// What this pack declares about its own lists for the
/// `TOOLUP_TEST_LANE` lanes. See `Support/TestLane.fs` for the lane
/// vocabulary and `Support/TestLaneFilter.fs` for how a declaration is
/// applied (by physical identity, after the guard).
///
/// **`Slow` is measured, not judged.** The rule is: a registered list
/// costs ≥ 10 s of wall-clock AND its slowest single case is ≥ 2 s —
/// i.e. it is slow because it holds long-running cases (benchmarks,
/// timeout clamps, polling waits), not because it holds many cheap ones.
/// The second clause is what earns the rule its keep: on the 2026-09-12
/// measurement it correctly left the public-API approval baseline
/// (13.6 s spread over 182 cases, slowest 0.3 s) IN the fast lane, where a
/// cheap, frequently-broken gate belongs, while a `≥ 10 s` rule alone would
/// have thrown it out. The rule holds for every pack of the Platform family.
///
/// Re-derive it — do not trust this list because it is written down:
///
///     dotnet src/ToolUp.Platform.Tests<.Part>/bin/Debug/net10.0/ToolUp.Platform.Tests<.Part>.dll \
///         --junit-summary platform.xml
///
/// then group `//testcase/@time` by the second `;`-separated segment of
/// `@name`. Measured 2026-09-12 on the campaign machine (Debug,
/// sequenced): 10,325 cases, 716.3 s of case time inside an 884.8 s pack
/// wall-clock, of which these six lists are 144 cases and 616.0 s — 86 %
/// of the case time in 1.4 % of the cases. `HnswFidelity` alone is
/// 560.6 s, and ONE case inside it (`latency smoke`) is 539.3 s: 56 % of
/// the entire 967 s `VerifyAll`.
let private laneDeclaration: TestLaneFilter.LaneDeclaration = {
    Slow = [
        // 560.6 s / 5 cases — a brute-force-scan latency benchmark.
        HnswFidelityTests.tests
        // 12.8 s / 32 cases — slowest case 6.2 s.
        OcrProviderTests.tests
        // 12.3 s / 66 cases — slowest case 4.0 s.
        HttpComputeDispatcherTests.tests
        // 10.2 s / 16 cases — slowest case 5.0 s.
        GrantGovernanceTests.tests
        // 10.1 s / 16 cases — a 10 s timeout-clamp assertion.
        ConfigValidatorAggregatorTests.tests
        // 10.0 s / 9 cases — a 10 s cancel-law defect probe.
        IExternalComputeDispatcherContract.selfTests
        // 20.7 s / 11 cases — four cases outlive a 4 s server-token lifetime
        // (measured 2026-10-21); slowest case 4.9 s.
        StreamAuthBothSignInsTests.tests
    ]
    // Deliberately EMPTY, and that is a declaration rather than an
    // omission. `pure` means "touches no filesystem, process, socket or
    // environment variable", and no list in this pack has been audited
    // to that standard — the pack is the filesystem-heavy one by
    // construction (`LocalFileStorageTests`, `PersistentEventStoreTests`,
    // the blob-backed store conformance packs). The `pure` lane is
    // therefore carried by the PACK-level declaration in the repo-root
    // `Build.fs`, and this pack is not in it. Running this dll under
    // `TOOLUP_TEST_LANE=pure` runs the registration guard and nothing
    // else — honest, non-vacuous, and never a false purity claim.
    // Adding a list here is an opt-in an auditor makes case by case.
    Pure = []
}

/// The guard's floor is a LOWER BOUND on the `[<Tests>]` bindings its sweep
/// finds (about 85 % of them), so adding a list never needs an edit here; it
/// moves only when bindings are deliberately removed.
[<EntryPoint>]
let main argv =
    PackEntry.run
        {
            Row = "Platform"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 104
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv