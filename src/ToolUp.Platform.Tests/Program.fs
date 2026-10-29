module ToolUp.Platform.Tests.Program

open System.Reflection
open Expecto
// Phase 762 — the `TOOLUP_TEST_LANE` vocabulary, shared with the
// `VerifyAll` driver by source-link (see Support/TestLane.fs).
open ToolUp.Forge
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.AI
open ToolUp.Platform.Tests.RAG
open ToolUp.Platform.Tests.Graph
// Phase 784 — the remoting wire differential corpus's two suites.
open ToolUp.Platform.Tests.Remoting
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs. `[<Tests>]` alone does
/// NOT get a list into this run — `runTestsWithCLIArgs` executes exactly
/// what is registered here — which is why `allTests` below appends the
/// Phase 722 registration guard over it.
let private registeredTests =
    testList "ToolUp.Platform.Tests" [
        LocalFileStorageTests.tests
        InMemoryEventStoreTests.tests
        PersistentEventStoreTests.tests
        DataObjectStoreTests.tests
        DataMigrationTests.tests
        ConfigMigrationTests.tests
        SeedDataLoaderTests.tests
        DatasetStoreTests.tests
        ParquetDatasetCodecTests.contractTests
        ParquetDatasetCodecTests.codecTests
        DatasetAssemblyTests.tests
        DatasetRevintageTests.tests
        VirtualDatasetTests.tests
        ProvenanceLabelTests.tests
        IModelFitProviderContract.tests
        IModelFitProviderContract.opacityTests
        IModelRegistryContract.tests
        IModelScorerContract.tests
        IModelEvaluationPlanMetricsContract.tests
        IExternalComputeDispatcherContract.tests
        IExternalComputeDispatcherContract.wireTests
        IExternalComputeDispatcherContract.portabilityAudit
        IExternalComputeDispatcherContract.conformanceTests
        IExternalComputeDispatcherContract.selfTests
        IExternalHandleStoreContract.tests
        IExternalHandleStoreContract.selfTests
        ModelEvaluationTests.tests
        ModelPromotionPolicyTests.tests
        ModelRegistrationObserverTests.tests
        ModelPromotionTransferTests.tests
        ModelFitBatchTests.tests
        ModelExecutionApiTests.tests
        ModelExecutionComposeTests.tests
        IngestionStatusTests.tests
        IngestionRetryTests.tests
        IngestionEmbedderRetryTests.tests
        IngestionBackpressureTests.tests
        DurableIngestionQueueTests.tests
        ColumnMatcherTests.tests
        ColumnMappingTests.tests
        MappingDryRunValidationTests.tests
        DerivedColumnEvalTests.tests
        IColumnMappingStoreContract.tests
        IGrantConsentStoreContract.tests
        IUserSchemaStoreContract.tests
        INotificationPreferenceStoreContract.tests
        DataCatalogTests.tests
        ResultStoreTests.tests
        ConversationStoreTests.tests
        LineageStoreTests.tests
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
        IFactTableWriterContract.runOptionsRefusedTests
            "DelegateTableWriter"
            IFactTableWriterContract.delegateWriterFactory
        IFactTableOrphanSweepContract.tests "DefaultFactTableWriter" IFactTableOrphanSweepContract.defaultWriterFactory
        IFactTableOrphanSweepContract.tests "DelegateTableWriter" IFactTableOrphanSweepContract.delegateWriterFactory
        IFactTableOrphanSweepContract.tests
            "notifying decorator over DefaultFactTableWriter"
            IFactTableOrphanSweepContract.notifyingWriterFactory
        FactStoreTests.tests
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
        CanonicalMethodTests.tests
        FactInvalidationTests.tests
        CoherenceCheckTests.tests
        ProvenanceGraphTests.tests
        ProvenanceGraphWireTests.tests
        GroundingWiringTests.tests
        JobStoreTests.tests
        CronExpressionTests.tests
        JobSchedulerTests.tests
        QuartzJobSchedulerTests.tests
        ScheduledJobDeclarationTests.tests
        ModuleQueryBusTests.tests
        InMemoryDataSourceTests.tests
        InMemoryDataSourceTests.payloadFormatTests
        DataSourceFidelityContract.selfTests
        DataIngestorTests.tests
        OAuth1aSubstrateTests.tests
        OAuth1aSubstrateTests.flowTests
        OAuthSubstrateTests.stateStoreTests
        OAuthSubstrateTests.credentialFlowTests
        OAuthSubstrateTests.pkceCredentialFlowTests
        OAuthSubstrateTests.pkceFlowTests
        OAuthSubstrateTests.refresherScrubTests
        OAuthSecretEncryptionModeValidatorTests.tests
        SecretStoreAtRestPostureValidatorTests.tests
        ShareTokenSigningKeyProvenanceValidatorTests.tests
        ShareTokenSigningKeyGovernanceTests.tests
        DataProtectionBackendTests.tests
        FileSecretStoreTests.tests
        FileSecretStoreAtomicityTests.tests
        FileSecretStoreCancellationTests.tests
        TransientFaultPolicyTests.tests
        WebhookSubstrateTests.tests
        WebhookSecretRotationTests.tests
        WebhookSecretMigrationTests.tests
        WebhookFailureStateLeaseTests.tests
        CronTickElectionTests.tests
        WebhookDispatcherTests.tests
        PresenceChannelTests.tests
        PresenceApiTests.tests
        IPresenceTrackerContract.tests
        IEntityLockStoreContract.tests
        ICrdtDocumentStoreContract.tests
        ExperimentSubstrateTests.tests
        WorkflowEngineTests.tests
        FlagSourceTests.tests
        PermissionStoreTests.tests
        FeatureFlagStoreTests.tests
        FlagEvaluatorTests.tests
        StorageScopeResolverTests.tests
        ServerConfigFromEnvTests.tests
        ClientBrandLiftTests.tests
        CellProvenanceTests.tests
        AgChartExportTests.tests
        AuthProviderTests.tests
        OidcJwksTtlTests.tests
        AzureBlobStorageTests.tests
        AwsS3StorageTests.tests
        GoogleCloudStorageTests.tests
        AzureKeyVaultSecretStoreTests.tests
        AwsSecretsManagerSecretStoreTests.tests
        HashiCorpVaultSecretStoreTests.tests
        GcpSecretManagerSecretStoreTests.tests
        GcpSecretManagerSecretStoreTests.serviceAccountParseTests
        AuthorizationTests.tests
        AuthClassifierAttributeIdentityTests.tests
        AuditTests.tests
        RateLimitTests.tests
        ValidationTests.tests
        IdempotencyTests.tests
        IdempotencyReplayAuditTests.tests
        IdempotencyStoreEvictionTests.tests
        AuditBodyDisposalTests.tests
        PostDispatchBodyReadTests.tests
        StreamingTests.tests
        AttributeRecognitionSweepTests.tests
        StreamingDispatchTests.tests
        JobHandleTests.tests
        MarkdownRendererTests.tests
        HtmlRendererTests.tests
        ReportingComposeTests.tests
        ReportingAuthorizationTests.tests
        DeckExportSeamTests.tests
        DeckExportSeamTests.bundleTests
        NarrativeElementTests.tests
        DataSubjectRequestTests.tests
        DataSubjectRequestTests.authorizationTests
        SignedExportTests.tests
        TimeSeriesStoreTests.tests
        TimescaleTimeSeriesStoreTests.tests
        PostgresEntityStoreTests.tests
        PostgresFactStoreTests.offlineTests
        PostgresFactStoreTests.tests
        TelemetrySinkTests.tests
        InMemoryNotificationChannelTests.tests
        RedisNotificationChannelTests.tests
        RedisDistributedLockTests.tests
        TransactionalDispatcherTests.tests
        NotificationPreferenceTests.tests
        NotificationAddressBookTests.tests
        ExternalContactTests.tests
        WhatsAppChannelTests.tests
        SmtpNotificationSinkTests.tests
        SendGridNotificationSinkTests.tests
        TwilioNotificationSinkTests.tests
        TwilioWhatsAppNotificationSinkTests.tests
        WebPushNotificationSinkTests.tests
        MetaWhatsAppCloudNotificationSinkTests.tests
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
        LocalEmbeddingHashingTests.dimensionAssignmentTests
        LocalEmbeddingHashingTests.spaceAlignmentTests
        OcrProviderTests.tests
        ContentScannerTests.tests
        KnowledgeUserScopeIsolationTests.tests
        VectorisationHookTests.tests
        RagTelemetryTests.tests
        ExtractorTests.tests
        TerminalStatusAnnouncementTests.tests
        HealthyHealthCheckTests.tests
        DegradedHealthCheckTests.tests
        UnhealthyHealthCheckTests.tests
        HealthCheckAggregatorTests.tests
        OkConfigValidatorTests.tests
        WarningConfigValidatorTests.tests
        ErrorConfigValidatorTests.tests
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
        EffectJoinTests.tests
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
        ConfigStartupModeTests.tests
        ConfigResolverTests.tests
        ConfigProfileTests.tests
        UnknownConfigKeyValidatorTests.tests
        TraceCategoriesValidatorTests.tests
        HealthStateTrackerTests.tests
        AlertRuleEngineTests.tests
        ServiceStatusBoardApiHandlerTests.tests
        DeploymentReadinessReportTests.tests
        RedisNotificationChannelHealthTests.tests
        testSequencedGroup
            "env-mutating-config-validators"
            (testList "env-mutating config validators (Phase 653)" [
                AIProviderEnvValidatorTests.providerTests
                AIProviderEnvValidatorTests.modelTests
                AIProviderProbeValidatorTests.tests
                OidcAuthValidatorTimeoutTests.tests
                EmbeddingProviderEnvTests.tests
                ConfigBoundsValidatorTests.intKeyTests
                ConfigBoundsValidatorTests.retentionTests
            ])
        AIProviderHealthTests.claudeTests
        AIProviderHealthTests.openAiTests
        EmbeddingProviderKeyRotationTests.tests
        MinimumViableShapeTests.tests
        RedactionAllowlistParityTests.tests
        OidcClassifyTokenTests.tests
        OidcRefreshPlanTests.tests
        GoogleIdentityCompanionTests.tests
        OidcDiagnoseTests.tests
        OidcTracerTests.tests
        OidcStateMachineTests.tests
        OidcPresetsTests.tests
        OidcCoherenceValidatorTests.tests
        OidcSignInContractTests.tests
        OidcBearerStrategyTests.tests
        OidcSecondaryFlowTests.tests
        GitHubAuthProviderTests.tests
        GitHubAppFlowTests.tests
        GoogleAnalyticsTests.tests
        LdapAuthProviderTests.tests
        ScimProvisioningTests.tests
        PasskeyAuthProviderTests.tests
        SSEHandshakeTests.tests
        EncryptedBlobStorageTests.tests
        CrossReplicaKeyDestructionTests.tests
        PerScopeKeyResolverWiringTests.unwiredDestroyTests
        PerScopeKeyResolverWiringTests.wiringValidatorTests
        PerScopeKeyResolverWiringTests.wiringDiagnosticsTests
        PerScopeKeyResolverWiringTests.keyMaterialTests
        WebhookSecretRotationBroadcastTests.tests
        TenantLifecycleAggregatorTests.tests
        LifecycleSummaryStoreTests.tests
        OffboardConfirmationTests.tests
        ScheduledDeprovisionTests.tests
        PrincipalRegistryTests.tests
        ITenantLifecycleContract.tests
        ILifecycleLockContract.tests
        IDistributedLockContract.tests
        LocalStorageEncryptionValidatorTests.tests
        BlobEntityStoreTests.tests
        EntityOutboxTests.tests
        ConditionalBlobStorageTests.tests
        ConditionalDataObjectStoreTests.tests
        EntityQueryTests.tests
        RelationshipTests.tests
        InMemoryGraphStoreTests.tests
        Neo4jConformanceTests.pureTests
        Neo4jConformanceTests.liveTests
        AgeConformanceTests.pureTests
        AgeConformanceTests.liveTests
        EntityGraphProjectionTests.tests
        UsageLogTests.tests
        PrometheusMetricsSinkTests.tests
        OtelActivitySinkTests.tests
        ServerModuleMetricsTests.tests
        JsonConsoleLoggerTests.tests
        InMemoryAuditSinkTests.tests
        S3ArchiveAuditSinkTests.tests
        SplunkHecAuditSinkTests.tests
        DatadogLogsAuditSinkTests.tests
        DatadogReadbackClientTests.tests
        ObservabilityHandlersTests.tests
        ObservabilityHandlersTests.datadogReadbackTests
        CefAuditSinkTests.tests
        ChainedAuditLedgerTests.tests
        AuditReplicatorTests.tests
        Ed25519ArtifactSubstrateTests.tests
        ShareTokenStoreTests.tests
        ShareTokenStoreTests.readPathTests
        ShareTokenAuthMiddlewareTests.tests
        ShareTokenMiddlewareRateLimitTests.tests
        ServiceAccountStoreTests.tests
        ServiceAccountStoreTests.pureTests
        ServiceAccountStoreTests.persistenceTests
        AnonymousSessionMigrationTests.tests
        AnonymousSessionBindingTests.tests
        StoreIdSanitisingTests.tests
        SecureByDefaultValidatorTests.tests
        InMemoryPendingInviteStoreTests.tests
        BlobPendingInviteStoreTests.tests
        BlobPendingInviteStoreTests.validatorGateTests
        BlobCorruptionChaosTests.tests
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
        InProcessOAuthTokenRefresherTests.tests
        BlobProviderProfileTests.tests
        ProviderProfileApiTests.tests
        TeamProviderTests.tests
        ProviderOAuthFlowTests.claudeOAuthContractTests
        ProviderOAuthFlowTests.openAiOAuthContractTests
        ProviderOAuthFlowTests.claudeProviderFlowContractTests
        ProviderOAuthFlowTests.openAiProviderFlowContractTests
        ProviderOAuthFlowTests.tests
        SseTraceContributorTests.tests
        SsePerConnectionWriterTests.tests
        HeaderAuthProviderModeValidatorTests.tests
        AuditLogModeValidatorTests.tests
        EventStoreChainValidatorTests.tests
        AuditLogHealthCheckTests.tests
        AuditWriteFailureMetricTests.tests
        AuditEventRegistryTests.tests
        BlobStorageAuthAuditTests.tests
        AuditFailurePolicyTests.tests
        PermissionAuditChainTests.tests
        DegradedCapabilityRegistryTests.tests
        AuthAuditHookTests.tests
        HostActionAuditTests.tests
        NotificationSilentlySkippedTests.tests
        EncryptedSecretStoreModeValidatorTests.tests
        JobSchedulerInstanceValidatorTests.tests
        AICancellationDispatchInstanceValidatorTests.tests
        AnonymousAIModeValidatorTests.tests
        OAuthStateStoreInstanceValidatorTests.tests
        NotificationChannelInstanceValidatorTests.tests
        IdempotencyStoreInstanceValidatorTests.tests
        MultiInstanceAdminCoherenceValidatorTests.tests
        NotificationsExplicitOffTests.tests
        RateLimitModeValidatorTests.tests
        RateLimitConfigValidatorTests.tests
        RateLimitConfigHelpersTests.tests
        SseAuthModeValidatorTests.tests
        OidcAudienceBindingValidatorTests.tests
        OidcClaimMappingAdvisoryValidatorTests.tests
        InviteEmailCapabilityValidatorTests.tests
        SecurityHeadersValidatorTests.tests
        SecurityHeadersValidatorTests.cspNonceCacheValidatorTests
        SecurityHeadersValidatorTests.baselineFloorTests
        SecurityHardeningTests.tests
        SecurityHardeningTests.cspMiddlewareTests
        InternetReadinessScorecardTests.tests
        ForwardedHeadersTrustValidatorTests.tests
        ForwardedHeadersTrustTests.tests
        LocalSecretFilePermissionsValidatorTests.tests
        IdentitySanitiserTests.tests
        FileManagementTests.tests
        ModuleApiFactoryTests.tests
        PeerBearerAuthTests.tests
        ConversationExportAuditTests.tests
        ConversationExporterTests.tests
        FastPathBeaconOwnershipTests.tests
        FastPathBeaconIdempotencyTests.tests
        FastPathSequencerTelemetryTests.tests
        FastPathTriageResolverTests.tests
        HnswFidelityTests.tests
        PermissionStoreFailClosedTests.tests
        ScopeResolutionFailClosedTests.tests
        AccessContextFactoryTests.tests
        AuditStreamKeyTests.tests
        ScopeKeyTests.tests
        SmokeTestDefaultsTests.tests
        InProcessRateLimiterTests.tests
        IExternalApiConnectorContract.kitTests
        ExternalApiConnectorBindingTests.tests
        HttpTransportBindingTests.tests
        PlatformTestingFrameworkTests.tests
        I18nInfrastructureTests.tests
        I18nCoverageTests.tests
        MultimodalAIProviderTests.tests
        CitationNormaliserTests.tests
        InMemoryBM25IndexTests.tests
        IndexLifecycleTests.tests
        CrossIndexErasureConformanceTests.tests
        SyntheticClientToolAuthorizerTests.tests
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
        ActionAuthorizerTests.tests
        HostCapabilityRegistryTests.tests
        HostCapabilityNegotiationTests.tests
        HostRenderTelemetryTests.tests
        HostRenderBoundaryTests.tests
        HostedTreeUsageExportTests.tests
        HostContentSanitizerTests.tests
        HostI18nResolverTests.tests
        HostedTreeA11yTests.tests
        AccessibilityAssertionsTests.tests
        HostRenderBudgetTests.tests
        ModuleExposureMigrationTests.tests
        NoActiveTeamLandingTests.tests
        ScopeDenialTests.tests
        LiveSessionHostTests.tests
        TreePatchChannelTests.tests
        HostStateProjectionTests.tests
        HostingSeamComponentIdTests.tests
        HostedTreeLayoutTests.tests
        HostRouteContractTests.tests
        UnreducedViewPreviewTests.tests
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
        ReportSubscriptionScopeTests.tests
        AssistantFactScopeTests.tests
        AIToolResultBudgetTests.tests
        AIToolSchemaTests.tests
        AIToolDispatchRbacTests.tests
        ToolApprovalTests.tests
        GrantPolicyTests.tests
        AdminMutationApprovalTests.tests
        GrantNotificationTests.tests
        GrantConsentTests.tests
        AccessAttestationTests.tests
        GrantGovernanceTests.tests
        AIQueryabilityOptInTests.tests
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
        KbObjectRetentionTests.tests
        KbDocumentTagsTests.tests
        KbBulkImportTests.tests
        BrandingTests.tests
        BrandKitTests.tests
        HostThemeTokensTests.tests
        BrandKitLayoutTests.tests
        FelizLayoutAdapterTests.tests
        AssetStoreTests.tests
        AssetStoreTests.rendererContract
        AssetStoreTests.phase967Tests
        MediaLibraryTests.tests
        EdgeCacheTests.tests
        DeliveredEgressTests.tests
        ContentAuthoringTests.tests
        ContentAdminAuthorizationTests.tests
        InMemoryRateLimitStoreTests.tests
        InMemoryRateLimitStoreTests.contractTests
        TeamCreationPolicyTests.tests
        TeamCreationPolicyTests.integrityTests
        TeamCreationPolicyTests.quotaTests
        DirectAddIdentityProofTests.tests
        TeamOwnershipTransferTests.tests
        TeamAdminLifecycleTests.tests
        UserMembershipTeardownTests.tests
        MembershipDoctorTests.tests
        TeamInvitationTests.tests
        TeamInvitationTests.pendingInviteExpiryAuditTests
        TeamInvitationTests.expiredInviteVisibilityTests
        TeamInvitationTests.inviteExpiryNotificationTests
        TeamInvitationTests.activeTeamPolicyTests
        TeamInvitationTests.checkMyInvitesTests
        GoogleDirectoryTests.tests
        WithRequestHeadersPassthroughTests.tests
        ConsentProviderTests.tests
        ConsentProviderTests.subscriptionFiringTests
        ConsentGatedScriptTests.tests
        ConsentStateStoreTests.tests
        ConsentStateStoreTests.entityBackedTests
        ConsentStateStoreTests.restartPersistenceTests
        SessionRegistryTests.tests
        SessionRegistryTests.derivationTests
        SessionRegistryTests.blobBackedTests
        AdAnalyticsSinkTests.tests
        AdAnalyticsSinkTests.noOpTests
        AdAnalyticsObservabilityTests.tests
        HttpBodyBindingTests.tests
        UserClaimsTests.tests
        ModuleGroupingValidatorTests.tests
        ModuleIdentityTests.tests
        AnonymousModeContractTests.tests
        AnonymousModeContractTests.exposureTests
        ModuleSurfaceRequirementTests.tests
        ModuleSurfaceRequirementTests.visibilityTests
        BuiltInModuleSurfaceTests.tests
        SidebarVisibilityContractTests.tests
        SidebarVisibilityContractTests.orderingTests
        SidebarVisibilityContractTests.groupSetTests
        SidebarVisibilityContractTests.navRoleTests
        SidebarVisibilityContractTests.accessibleNameTests
        SidebarVisibilityContractTests.placementTests
        SidebarVisibilityContractTests.keyboardNavigationTests
        RouteGuardContractTests.routeGuardTests
        RouteGuardContractTests.sharedPredicateTests
        CommandPaletteContractTests.paletteVisibilityTests
        CommandPaletteContractTests.expansionTests
        CommandPaletteContractTests.fuzzyTests
        CommandPaletteContractTests.paletteStateTests
        MessageCatalogContractTests.tests
        MessageCatalogContractTests.teamDefaultTests
        MessageCatalogContractTests.consumerOverrideTests
        MessageCatalogContractTests.localeSwitchTests
        LocalizationTests.tests
        SidebarHidingContractTests.hideRestoreTests
        SidebarHidingContractTests.pinHideRuleTests
        SidebarHidingContractTests.legacyBlobTests
        SidebarHidingContractTests.paletteParityTests
        ModuleVisibilityContractTests.resolutionTests
        ModuleVisibilityContractTests.ungovernedTests
        ModuleVisibilityContractTests.scopeWalkTests
        ModuleVisibilityContractTests.foldTests
        ModuleVisibilityContractTests.routeRegistryTests
        AuditViewApiHandlerTests.roleGateTests
        AuditViewApiHandlerTests.scopeIsolationTests
        AuditViewApiHandlerTests.filterTests
        AuditViewApiHandlerTests.pagingTests
        AuditViewApiHandlerTests.exportTests
        AuditViewApiHandlerTests.substratePairingTests
        CompositionInspectorHandlerTests.roleGateTests
        CompositionInspectorHandlerTests.panelTests
        CompositionInspectorHandlerTests.emptyStateTests
        CompositionInspectorHandlerTests.exportTests
        CompositionInspectorHandlerTests.contractShapeTests
        ReflectionToolsTests.tests
        AdminLandingContractTests.compositionTests
        AdminLandingContractTests.roleFilterTests
        AdminLandingContractTests.clickThroughTests
        AdminLandingContractTests.emptyStateTests
        HomeOverviewTests.countAffordanceTests
        HomeOverviewTests.overviewScopeAiTests
        HomeOverviewTests.overviewActiveAiPresentTests
        HomeOverviewTests.widgetDataTests
        HomeOverviewTests.pinningTests
        SurfaceCoherenceValidatorTests.tests
        CsrfPrefetchAnonymousGateTests.tests
        SubjectKindClientFlowTests.tests
        RevokeOnIssuerRemovedTests.tests
        AnonymousSessionMigratorTests.tests
        DefaultSubjectResolverTests.tests
        SubjectDowngradeObservabilityTests.tests
        CsrfCarveOutDerivationTests.tests
        SurfaceEnforcementMiddlewareTests.tests
        AnonymousSessionMigrationMiddlewareTests.tests
        SubjectWildcardAnalyzerTests.tests
        TelemetryZeroCostGateTests.tests
        DispatcherBodyAndArgFastpathTests.tests
        ServerRemotingTailTests.tests
        ShutdownDrainTests.tests
        FromContextAsyncBuildOnceTests.tests
        GeneratedInvocationChainTests.tests
        IAIProviderContract.tests
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
        BuildTranscriptTests.transcriptDeterminismTests
        BuildTranscriptTests.transcriptSensitivityTests
        BuildTranscriptTests.provenanceTests
        BuildTranscriptTests.deployRecordCanonicalFormTests
        BuildTranscriptTests.deployRecordVerificationTests
        BuildTranscriptTests.closureCanonicalFormTests
        BuildTranscriptTests.closureAttestationTests
        BuildTranscriptTests.closureCaptureTests
        BuildTranscriptTests.closureBindingTests
        WorkProvenanceSourceTests.tests
        SbomProjectionTests.sbomSchemaConformanceTests
        SbomProjectionTests.sbomUnattestedPresenceTests
        SbomProjectionTests.sbomDeterminismTests
        SbomProjectionTests.sbomHonestyTests
        SbomProjectionTests.sbomScopeStatementTests
        SbomProjectionTests.sbomEndpointTests
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
        EvidenceEnumerationCompletenessTests.derivationTests
        EvidenceEnumerationCompletenessTests.missingPositionTests
        EvidenceEnumerationCompletenessTests.boundedTests
        EvidenceEnumerationCompletenessTests.shorterRenderTests
        EvidenceEnumerationCompletenessTests.bundleStatementTests
        BootVerificationPreflightTests.tests
        GroundedAnswerChainJoinTests.tests
        GroundingEnvelopeSealTests.tests
        DeployPlaneTests.defaultPipelineRollbackTests
        SvgPropTests.tests
        DataPropTests.tests
        AriaPropTests.tests
        ClientToolkitThemingTests.tests
        DomAttrCustomAuditTests.tests
        StjBackwardCompatTests.tests
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
        IEgressGateContract.tests
        IEvidencePackGeneratorContract.tests
        ArchitectureFitnessTests.tests
        PublicApiApprovalTests.tests
        BaselineMergeDriverTests.tests
        BackupCoordinatorTests.tests
        SeamPrincipalTests.tests
        RemotingAnalyzerRecognitionTests.tests
        RemotingDecodeRefusalTests.tests
        SchemaVersionTests.tests
        MsgPackHostileInputTests.tests
        AdversarialFailClosedTests.tests
        ConstantTimeCompareTests.tests
        ModuleLoadOutcomeTests.tests
        PrerenderDeterminismTests.tests
        HydrationParityTests.tests
        SecondBindingNeutralityTests.tests
        OpenCoreVocabularyNeutralityTests.tests
        ClientHostCapabilitiesContract.tests
        ComponentRegistryContract.tests
        ComponentRegistryContract.selfTests
        ModuleContract.tests
        ModuleContract.referenceTests
        ModuleContract.selfTests
        ThreatLensRegressionSuite.tests
        ReactiveRecomputeComposeTests.tests
        PeerAuthPostureTests.ladderTests
        PeerAuthPostureTests.overlapTests
        PeerAuthPostureTests.advisoryTests
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
        MemoizedComputeDispatcherTests.memoizationTests
        MemoizedComputeDispatcherTests.coalescingTests
        MemoizedComputeDispatcherTests.scopeIsolationTests
        MemoizedComputeDispatcherTests.ttlTests
        MemoizedComputeDispatcherTests.optInTests
        MemoizedComputeDispatcherTests.budgetCompositionTests
        MemoizedComputeDispatcherTests.durabilityTests
        MemoizedComputeDispatcherTests.evictionTests
        ComputeBudgetTests.tests
        BudgetSeamTests.tests
        DataObjectOrphanSweepTests.tests
        DataObjectOrphanSweepTests.validatorTests
        DataObjectOrphanSweepTests.composeTests
        DataObjectOrphanSweepTests.inBandGraceTests
        RagConfigValidatorExtensionTests.tests
        ExternalCallbackTests.tests
        HttpComputeDispatcherTests.tests
        SignedWorkerOutcomeTests.tests
        JobProgressTests.tests
        FederatedModelExecutionTests.tests
        ExternalModelFitTests.tests
        Conformance.ModelExecutionSpecConformance.tests
        FailClosedDispatchTests.tests
        ScopeEnumerationSweepTests.tests
        CountersignatureRegistryTests.subjectTests
        CountersignatureRegistryTests.completenessTests
        CountersignatureRegistryTests.invalidationTests
        CountersignatureRegistryTests.revocationTests
        CountersignatureRegistryTests.signatureTests
        CountersignatureRegistryTests.storeTests
        CountersignatureRegistryTests.queueTests
        CountersignatureRegistryTests.parityTests
        MsgPackRoundTripTests.tests
        StjRoundTripTests.tests
        DecoderAlgebraTests.tests
        JsonDecoderAlgebraTests.tests
        JsonTextTests.tests
        JsonWideValueTests.tests
        ReadPolicyAttributeTests.tests
        RemotingBatchTests.tests
        GeneratorFidelityTests.tests
        ClientProxyGenerationTests.tests
        PlatformDecodersGenerationTests.tests
        ApiRecordCensusTests.tests
        ProofOracleTests.tests
        DisclosureProofOracleTests.tests
        ToolEffectEnvelopeTests.tests
        ToolGateProofOracleTests.tests
        ModelInputProofOracleTests.tests

        TaintFlowProofOracleTests.tests
        ElmishProofOracleTests.tests
        ElmishLoopProofOracleTests.tests
        SqliteLogStoreTests.tests
        MetricsHistoryFlusherTests.tests
        ConversationRetentionTests.tests
        ReportingAIToolsTests.tests
        ConversationListingTests.tests
        ConversationVisibilityTests.tests
        FactBrowseTests.tests
        DelegateFactTests.tests
        TeamOutputVisibilityTests.tests
        FactPublicationTests.tests
        Phase971InterPlatformTests.tests
        MediaLibraryTests.phase971Tests
        Phase971KnowledgeBaseTests.tests
        FactPublicationTests.phase971Tests
        Phase971PlatformTeamsTests.tests
        Phase971PlatformStoresTests.tests
        Phase971PlatformRecordsTests.tests
        Phase971EmbeddingTests.tests
        Phase971AITests.tests
        Phase971PublicRenderingTests.tests
        Phase971PlatformTenantTests.tests
        ProviderExceptionTests.tests
        OidcSignInSpaPrecedenceTests.tests
        ServerAppSecretStoreTests.tests
        ServerAppOrderTests.tests
        ServerBindAddressTests.tests
        AuditFireAndForgetTests.tests
        StoreFireAndForgetTests.tests
        LineageDurabilityTests.tests
        AuditCallSiteFireAndForgetTests.tests
        FileApiMountTests.tests
        StreamAuthBothSignInsTests.tests
        DeliveryCompletedTests.tests
        FactPresentationTests.tests
        FactRenderToolTests.tests
    ]

/// The `[<Tests>]` bindings this pack deliberately does not run, each
/// with the reason. Phase 722: a deliberate omission is declared data the
/// guard reads, not prose beside the list — a stale entry here fails the
/// guard rather than quietly excusing something.
let private deliberatelyUnregistered = [
    {
        TestRegistrationGuard.Binding = "ToolUp.Platform.Tests.InProcess.BuiltInModuleSurfaceTests.visibilityTests"
        TestRegistrationGuard.Reason =
            "constructs the SDK's built-in CLIENT-side UI modules (FileManagerUI.create, "
            + "DataSubjectRequestAdminUI.create, HealthMonitorUI.create, …), whose bodies touch `Icons` — "
            + "Fable `importDefault` dummy code that throws under .NET. It is a Fable-tier pack, same class "
            + "as the HomeOverviewTests client-tier landing test in the ToolUp.AI.Client.Tests Fable "
            + "harness. See the 2026-07-20 orphaned-pack audit report."
    }
]

/// Phase 762 — what this pack declares about its own lists for the
/// `TOOLUP_TEST_LANE` lanes. See `Support/TestLane.fs` for the lane
/// vocabulary and `Support/TestLaneFilter.fs` for how a declaration is
/// applied (by physical identity, after the guard).
///
/// **`Slow` is measured, not judged.** The rule is: a registered list
/// costs ≥ 10 s of wall-clock AND its slowest single case is ≥ 2 s —
/// i.e. it is slow because it holds long-running cases (benchmarks,
/// timeout clamps, polling waits), not because it holds many cheap ones.
/// The second clause is what earns the rule its keep: on the 2026-09-12
/// measurement it correctly leaves `Phase 175 — Public-API approval
/// baseline` (13.6 s spread over 182 cases, slowest 0.3 s) IN the fast
/// lane, where a cheap, frequently-broken gate belongs, while a
/// `≥ 10 s` rule alone would have thrown it out.
///
/// Re-derive it — do not trust this list because it is written down:
///
///     dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll \
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
        // (Phase 1047, measured 2026-10-21); slowest case 4.9 s.
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

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing.
/// The floor is a LOWER BOUND (140 against ~170 attributed bindings), so
/// adding a pack never needs an edit here; it only moves when bindings
/// are deliberately removed.
///
/// Phase 762 — the lane filter is applied AFTER the guard, deliberately:
/// the guard is about what this pack REGISTERS (all of it, in every
/// lane), the lane only about what RUNS. Under `full` — the default, and
/// the only lane a ship may cite — `forLane` returns the guarded tree
/// physically unchanged.
let allTests =
    TestRegistrationGuard.withGuardExempting
        (Assembly.GetExecutingAssembly())
        140
        deliberatelyUnregistered
        registeredTests
    |> TestLaneFilter.forLane laneDeclaration registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    // Phase 762 — say which lane produced the result, so a log a reader
    // finds later cannot be mistaken for a full run. Silent under `full`
    // (the default), so the shipping gate's output is unchanged.
    let dropped = TestLaneFilter.dropped laneDeclaration registeredTests

    TestLane.announce (
        sprintf
            "%d case(s) in %d registered list(s) are not in this run"
            (dropped |> List.sumBy TestLaneFilter.caseCount)
            (List.length dropped)
    )

    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests