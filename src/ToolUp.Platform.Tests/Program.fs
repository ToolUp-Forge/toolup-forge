module ToolUp.Platform.Tests.Program

open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.AI

let allTests =
    testList "ToolUp.Platform.Tests" [
        LocalFileStorageTests.tests
        InMemoryEventStoreTests.tests
        PersistentEventStoreTests.tests
        DataObjectStoreTests.tests
        DataCatalogTests.tests
        ResultStoreTests.tests
        ConversationStoreTests.tests
        LineageStoreTests.tests
        JobStoreTests.tests
        CronExpressionTests.tests
        JobSchedulerTests.tests
        ScheduledJobDeclarationTests.tests
        ModuleQueryBusTests.tests
        InMemoryDataSourceTests.tests
        OAuthSubstrateTests.stateStoreTests
        OAuthSubstrateTests.credentialFlowTests
        FileSecretStoreTests.tests
        PermissionStoreTests.tests
        FeatureFlagStoreTests.tests
        FlagEvaluatorTests.tests
        StorageScopeResolverTests.tests
        AuthProviderTests.tests
        AzureBlobStorageTests.tests
        AwsS3StorageTests.tests
        GoogleCloudStorageTests.tests
        AzureKeyVaultSecretStoreTests.tests
        AwsSecretsManagerSecretStoreTests.tests
        HashiCorpVaultSecretStoreTests.tests
        MarkdownRendererTests.tests
        HtmlRendererTests.tests
        DataSubjectRequestTests.tests
        InMemoryNotificationChannelTests.tests
        RedisNotificationChannelTests.tests
        TransactionalDispatcherTests.tests
        NotificationAddressBookTests.tests
        SmtpNotificationSinkTests.tests
        SendGridNotificationSinkTests.tests
        TwilioNotificationSinkTests.tests
        WebPushNotificationSinkTests.tests
        HnswVectorStoreTests.tests
        HealthyHealthCheckTests.tests
        DegradedHealthCheckTests.tests
        UnhealthyHealthCheckTests.tests
        HealthCheckAggregatorTests.tests
        OkConfigValidatorTests.tests
        WarningConfigValidatorTests.tests
        ErrorConfigValidatorTests.tests
        ConfigValidatorAggregatorTests.tests
        HealthStateTrackerTests.tests
        ServiceStatusBoardApiHandlerTests.tests
        RedisNotificationChannelHealthTests.tests
        AIProviderHealthTests.claudeTests
        AIProviderHealthTests.openAiTests
        MinimumViableShapeTests.tests
        RedactionAllowlistParityTests.tests
        OidcClassifyTokenTests.tests
        OidcDiagnoseTests.tests
        OidcTracerTests.tests
        OidcStateMachineTests.tests
        OidcPresetsTests.tests
        OidcCoherenceValidatorTests.tests
        OidcSignInContractTests.tests
        SSEHandshakeTests.tests
        EncryptedBlobStorageTests.tests
        LocalStorageEncryptionValidatorTests.tests
        BlobEntityStoreTests.tests
        EntityQueryTests.tests
        UsageLogTests.tests
        PrometheusMetricsSinkTests.tests
        OtelActivitySinkTests.tests
        ServerModuleMetricsTests.tests
        JsonConsoleLoggerTests.tests
        InMemoryAuditSinkTests.tests
        S3ArchiveAuditSinkTests.tests
        SplunkHecAuditSinkTests.tests
        DatadogLogsAuditSinkTests.tests
        AuditReplicatorTests.tests
        Ed25519ArtifactSubstrateTests.tests
        ShareTokenStoreTests.tests
        InMemoryPendingInviteStoreTests.tests
        BlobPlatformAIKeyStoreTests.tests
        MultiPlatformProviderResolutionTests.tests
        PlatformAIKeysHandlerRbacTests.tests
        InProcessOAuthTokenRefresherTests.tests
        BlobProviderProfileTests.tests
        SseTraceContributorTests.tests
        HeaderAuthProviderModeValidatorTests.tests
        AuditLogModeValidatorTests.tests
        AuditLogHealthCheckTests.tests
        NotificationSilentlySkippedTests.tests
        EncryptedSecretStoreModeValidatorTests.tests
        JobSchedulerInstanceValidatorTests.tests
        OAuthStateStoreInstanceValidatorTests.tests
        NotificationChannelInstanceValidatorTests.tests
        NotificationsExplicitOffTests.tests
        RateLimitModeValidatorTests.tests
        RateLimitConfigValidatorTests.tests
        RateLimitConfigHelpersTests.tests
        SseAuthModeValidatorTests.tests
        OidcAudienceBindingValidatorTests.tests
        SecurityHeadersValidatorTests.tests
        SecurityHardeningTests.tests
        ForwardedHeadersTrustValidatorTests.tests
        LocalSecretFilePermissionsValidatorTests.tests
        IdentitySanitiserTests.tests
        FileManagementTests.tests
        PeerBearerAuthTests.tests
        ConversationExportAuditTests.tests
        ConversationExporterTests.tests
        FastPathBeaconOwnershipTests.tests
        FastPathSequencerTelemetryTests.tests
        HnswFidelityTests.tests
        PermissionStoreFailClosedTests.tests
        SmokeTestDefaultsTests.tests
        InProcessRateLimiterTests.tests
        PlatformTestingFrameworkTests.tests
        I18nInfrastructureTests.tests
        MultimodalAIProviderTests.tests
        CitationNormaliserTests.tests
        SyntheticClientToolAuthorizerTests.tests
        AIAgentEngineClientResidentAuthorizationTests.tests
        ClientToolDispatchContractBindings.tests
        SampleClientToolDispatchTests.tests
        PublicRenderingTests.tests
        AssetStoreTests.tests
        InMemoryRateLimitStoreTests.tests
        // Phase 56 — full contract pack bound to InMemoryRateLimitStore.
        // External-store companions (AzureTableStorage / Redis) bind to
        // the same pack from their own InProcess test files (when
        // shipped; not in the OSS test suite yet — those need live
        // backends).
        InMemoryRateLimitStoreTests.contractTests
        TeamCreationPolicyTests.tests
        TeamInvitationTests.tests
        EntraExternalIdConfigTests.tests
        WithRequestHeadersPassthroughTests.tests
        ConsentProviderTests.tests
        ConsentProviderTests.subscriptionFiringTests
        AdAnalyticsSinkTests.tests
        AdAnalyticsSinkTests.noOpTests
        UserClaimsTests.tests
        ModuleGroupingValidatorTests.tests
        AnonymousModeContractTests.tests
        ModuleSurfaceRequirementTests.tests
        ModuleSurfaceRequirementTests.visibilityTests
        BuiltInModuleSurfaceTests.tests
        SurfaceCoherenceValidatorTests.tests
        CsrfPrefetchAnonymousGateTests.tests
        SubjectKindClientFlowTests.tests
        RevokeOnIssuerRemovedTests.tests
        AnonymousSessionMigratorTests.tests
        DefaultSubjectResolverTests.tests
        CsrfCarveOutDerivationTests.tests
        SurfaceEnforcementMiddlewareTests.tests
        AnonymousSessionMigrationMiddlewareTests.tests
        SubjectWildcardAnalyzerTests.tests
        // Phase 69l — Telemetry seam zero-cost gate (GP 13 alignment).
        // Source-audit-shape pack pinning the gate composition in
        // `Api.fs` + the dispatcher's gate-read before allocation.
        TelemetryZeroCostGateTests.tests
        // Phase 69m — Dispatcher body + arg-parse fastpath. Source-audit
        // pack pinning the JsonElement-shaped argument arm + the
        // InputBytes cache plumbing.
        DispatcherBodyAndArgFastpathTests.tests
        IAIProviderContract.tests
        // Phase 26 — deploy-plane substrate contract packs bound to
        // their single-node defaults + in-memory mocks. The
        // DockerLocal Docker-backed leg is env-gated; CI without a
        // local Docker socket skips that pack.
        DeployPlaneTests.tenantFleetTests
        DeployPlaneTests.buildOrchestratorTests
        DeployPlaneTests.deployPipelineTests
        DeployPlaneTests.containerSchedulerInMemoryTests
        DeployPlaneTests.containerSchedulerDockerLocalTests
        // v0.5.0 — DOM-attr helper module sanity packs + audit ratchet.
        // The DataProp / AriaProp helper modules (sub-task A) ship
        // sanity tests that mirror SvgPropTests' shape; the audit
        // ratchet (this commit) walks the forge client tree and flags
        // any raw `prop.custom ("data-*"|"aria-*"|"role", _)` call
        // outside the helper modules. All three packs are wired in
        // here together because they share the same v0.5.0 motivation
        // and the audit only earns its keep when the helpers it
        // anchors are themselves under test. SvgPropTests is also
        // wired in for the first time — the pre-existing pack carried
        // an `[<Tests>]` attribute (assuming auto-discovery) but
        // `runTestsWithCLIArgs` only runs the supplied `allTests`
        // list, leaving the pack silently dormant. Discovered when
        // wiring up the new packs.
        SvgPropTests.tests
        DataPropTests.tests
        AriaPropTests.tests
        DomAttrCustomAuditTests.tests
        // v0.5.0 — STJ backward-compat backstop. Frozen-snapshot wire
        // shape + roundtrip-equality for representative persistence
        // DUs + records. First production deployment after v0.5.0 that
        // reads a pre-migration blob fails loudly here rather than
        // silently in production if the wire shape drifts.
        StjBackwardCompatTests.tests
        // Phase 18 — inter-platform peer substrate: the IPlatformPeer
        // contract pack (in-process binding) + the buyer→seller TestServer
        // worked example (identity validation, audit emission, matching
        // RootRequestId across the HTTP boundary).
        PlatformPeerTests.inProcessTests
        PlatformPeerTests.workedExampleTests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests