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
        IngestionStatusTests.tests
        IngestionRetryTests.tests
        // Phase 14t — embedder retry + dead-letter (classification / backoff / alerts).
        IngestionEmbedderRetryTests.tests
        // Phase 303 — ingestion-queue backpressure observability.
        IngestionBackpressureTests.tests
        ColumnMatcherTests.tests
        // Phase 218 — CSV-mapping dry-run validation preview.
        MappingDryRunValidationTests.tests
        // Phase 219 — derived/computed columns in CSV mapping.
        DerivedColumnEvalTests.tests
        IColumnMappingStoreContract.tests
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
        OAuthSubstrateTests.pkceCredentialFlowTests
        OAuthSubstrateTests.pkceFlowTests
        OAuthSubstrateTests.refresherScrubTests
        OAuthSecretEncryptionModeValidatorTests.tests
        ShareTokenSigningKeyProvenanceValidatorTests.tests
        FileSecretStoreTests.tests
        // Phase 176 — transient-fault decorator substrate.
        TransientFaultPolicyTests.tests
        // Phase 238 — generic inbound-webhook receiver substrate.
        WebhookSubstrateTests.tests
        // Phase 235 — outbound webhook signing-secret rotation.
        WebhookSecretRotationTests.tests
        // Phase 241 — presence substrate.
        PresenceChannelTests.tests
        // Phase 242 — A/B experiment substrate.
        ExperimentSubstrateTests.tests
        // Phase 243 — BPMN-shaped workflow engine.
        WorkflowEngineTests.tests
        // Phase 239 — IFlagSource seam + OpenFeature companion.
        FlagSourceTests.tests
        PermissionStoreTests.tests
        FeatureFlagStoreTests.tests
        FlagEvaluatorTests.tests
        StorageScopeResolverTests.tests
        ServerConfigFromEnvTests.tests
        ClientBrandLiftTests.tests
        AuthProviderTests.tests
        AzureBlobStorageTests.tests
        AwsS3StorageTests.tests
        GoogleCloudStorageTests.tests
        AzureKeyVaultSecretStoreTests.tests
        AwsSecretsManagerSecretStoreTests.tests
        HashiCorpVaultSecretStoreTests.tests
        // Wired in for the first time alongside the always-on
        // key-parsing pack that follows — the env-gated GCP contract
        // pack carried an `[<Tests>]` attribute (assuming
        // auto-discovery) but `runTestsWithCLIArgs` only runs the
        // supplied `allTests` list, leaving the pack silently dormant
        // (same class as the SvgPropTests wiring note further down).
        GcpSecretManagerSecretStoreTests.tests
        GcpSecretManagerSecretStoreTests.serviceAccountParseTests
        // Phase 69d.tail + 69h.tail — authorization classifier default-on
        // + audit annotation sweep contract packs.
        AuthorizationTests.tests
        AuditTests.tests
        RateLimitTests.tests
        ValidationTests.tests
        IdempotencyTests.tests
        IdempotencyReplayAuditTests.tests
        AuditBodyDisposalTests.tests
        StreamingTests.tests
        StreamingDispatchTests.tests
        MarkdownRendererTests.tests
        HtmlRendererTests.tests
        NarrativeElementTests.tests
        DataSubjectRequestTests.tests
        DataSubjectRequestTests.authorizationTests
        SignedExportTests.tests
        TimeSeriesStoreTests.tests
        TimescaleTimeSeriesStoreTests.tests
        TelemetrySinkTests.tests
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
        // Phase 279 — stable component identity (ComponentId).
        ComponentIdentityTests.tests
        // Phase 280 — introspectable composition manifest.
        CompositionManifestTests.tests
        ConfigReferenceTests.tests
        ConfigStartupModeTests.tests
        HealthStateTrackerTests.tests
        AlertRuleEngineTests.tests
        ServiceStatusBoardApiHandlerTests.tests
        DeploymentReadinessReportTests.tests
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
        TenantLifecycleAggregatorTests.tests
        LifecycleSummaryStoreTests.tests
        OffboardConfirmationTests.tests
        ScheduledDeprovisionTests.tests
        ITenantLifecycleContract.tests
        ILifecycleLockContract.tests
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
        ShareTokenAuthMiddlewareTests.tests
        AnonymousSessionMigrationTests.tests
        StoreIdSanitisingTests.tests
        SecureByDefaultValidatorTests.tests
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
        DegradedCapabilityRegistryTests.tests
        AuthAuditHookTests.tests
        NotificationSilentlySkippedTests.tests
        EncryptedSecretStoreModeValidatorTests.tests
        JobSchedulerInstanceValidatorTests.tests
        OAuthStateStoreInstanceValidatorTests.tests
        NotificationChannelInstanceValidatorTests.tests
        MultiInstanceAdminCoherenceValidatorTests.tests
        NotificationsExplicitOffTests.tests
        RateLimitModeValidatorTests.tests
        RateLimitConfigValidatorTests.tests
        RateLimitConfigHelpersTests.tests
        SseAuthModeValidatorTests.tests
        OidcAudienceBindingValidatorTests.tests
        SecurityHeadersValidatorTests.tests
        SecurityHardeningTests.tests
        ForwardedHeadersTrustValidatorTests.tests
        // Phase 325 — trusted-proxy CIDR allowlist + auth-mode escalation.
        ForwardedHeadersTrustTests.tests
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
        I18nCoverageTests.tests
        MultimodalAIProviderTests.tests
        CitationNormaliserTests.tests
        InMemoryBM25IndexTests.tests
        IndexLifecycleTests.tests
        SyntheticClientToolAuthorizerTests.tests
        AIAgentEngineClientResidentAuthorizationTests.tests
        ClientToolDispatchContractBindings.tests
        SampleClientToolDispatchTests.tests
        // Phase 113 — host-neutral default-deny action authorizer:
        // policy matching + PermissionStoreActionAuthorizer semantics +
        // contract binding.
        ActionAuthorizerTests.tests
        // Phase 266 — extensible host-capability registry: authorizer-gated
        // Invoke (registered-only-when-granted / unregistered-denies /
        // cross-scope-denies / empty-registry-deny-all) + built-ins.
        HostCapabilityRegistryTests.tests
        // Phase 245 — tri-state ModuleExposure persistence migration
        // (legacy `hidden[]` → Hidden, dual-write back-compat).
        ModuleExposureMigrationTests.tests
        // Parameterized no-active-team landing — effective-id precedence,
        // built-in module factory, prepareModules injection guards.
        NoActiveTeamLandingTests.tests
        // Phase 227 (task #4) — typed client-side scope-denial
        // classification (NeedsActiveTeam | NeedsAuthentication | Forbidden)
        // from SurfaceEnforcementMiddleware rejection bodies.
        ScopeDenialTests.tests
        // Phase 112 — scope-isolated live-session host: contract pack,
        // cap refusal, endpoint integration (404 / 429 / SSE frames).
        LiveSessionHostTests.tests
        // Phase 264 — host-state binding-source projection seam: CSR
        // projection round-trip, SSR scope-isolation, GP 13 zero-cost,
        // toy read-side resolves on both projection paths, OSS grep-guard.
        HostStateProjectionTests.tests
        PublicRenderingTests.tests
        // Phase 149/150/157 — sitemap conditional-GET + sharding + search index.
        SitemapSearchIndexTests.tests
        // Phase 85 — NarrativeFromData analytics → Narrative projectors.
        NarrativeFromDataTests.tests
        NarrativeChartsTests.tests
        EnumerableRoutesTests.tests
        NavStructuredDataTests.tests
        TagFeedTests.tests
        NarrativeDataExportTests.tests
        PaginationTests.tests
        FacetedBrowseTests.tests
        NavComposeTests.tests
        // Phase 90 — site structure: nav tree + taxonomy.
        SiteStructureTests.tests
        // Phase 84 — SSR render cache: IRenderCache contract bindings
        // (in-memory + blob), CachePolicy.parse / hash unit tests, and
        // PublicPageHandler cache-integration (miss→hit, 304, headers,
        // off-route, pre-84 path, stale-while-revalidate).
        RenderCacheTests.tests
        // Phase 111 — resolved-content head metadata: codec round-trip,
        // synthesis (GP 11 bare-body parity), head injection + handler
        // integration (Phase 84 cache-ownership), enumerable reach.
        ResolvedContentTests.tests
        // Phase 109 — IndexNow push-indexing: key derivation, resumable
        // submission state machine (postmortem resume), /{key}.txt endpoint
        // match/fall-through, publish ping, compose gate (GP 11/13).
        IndexNowTests.tests
        // Phase 114 — multi-host site registry: host → site resolution,
        // per-site serving + sitemap origins, render-cache namespacing,
        // startup validator matrix.
        MultiSiteTests.tests
        // Phase 86 — gated/tenant/audience SSR: PageAudience.parse +
        // AudienceGate evaluate matrix + handler authorization pre-check
        // (401/403/200) + sitemap exclusion + cross-tenant isolation.
        GatedSsrTests.tests
        // Phase 91 — RAG-backed answer pages: RagAnswerSource grounding,
        // extractive + synthesis-hook answers, and StrictlyGrounded refusal.
        KnowledgeSurfaceTests.tests
        // Wave 15 (Phases 102/103/104/106/107) — KB original-document
        // retrieval: IOriginalSourceResolver per-KnowledgeSource contract,
        // getOriginalDocument scope gate + typed refusals, access/denial
        // audit, _originalRef chunk stamp + RetrievedSource.OriginalRef
        // projection + neutral SourceLocator mapping + wire backward-compat.
        KnowledgeOriginalRetrievalTests.tests
        KnowledgeScopeResetAuditTests.tests
        RAGVacuumJobHandlerTests.tests
        KnowledgeUploadPolicyTests.tests
        // Phase 14x — KB upload content-hash dedup.
        KnowledgeDedupTests.tests
        BrandingTests.tests
        BrandKitTests.tests
        // Phase 269 — brandkit → hosted-tree theme-token bridge: projection,
        // palette-override precedence + scope isolation, GP 13, snapshot determinism.
        HostThemeTokensTests.tests
        // Phase 92 — BrandKit layout library (seven layouts: a11y
        // baseline, class hooks, optional-slot rule) + Feliz.ViewEngine
        // layout adapter (single doctype, registry round-trip).
        BrandKitLayoutTests.tests
        FelizLayoutAdapterTests.tests
        AssetStoreTests.tests
        MediaLibraryTests.tests
        ContentAuthoringTests.tests
        InMemoryRateLimitStoreTests.tests
        // Phase 56 — full contract pack bound to InMemoryRateLimitStore.
        // External-store companions (AzureTableStorage / Redis) bind to
        // the same pack from their own InProcess test files (when
        // shipped; not in the OSS test suite yet — those need live
        // backends).
        InMemoryRateLimitStoreTests.contractTests
        TeamCreationPolicyTests.tests
        TeamCreationPolicyTests.integrityTests
        TeamCreationPolicyTests.quotaTests
        // Phase 304 — TeamApi.TransferOwnership: Owner-gated ownership
        // hand-over (promote-then-demote single-Owner invariant, member /
        // self / non-Owner rejections, TeamOwnershipTransferred audit).
        TeamOwnershipTransferTests.tests
        // Platform-Admin team lifecycle — ListAllTeams / Archive / Restore
        // / DeleteTeamHard gating + archived-team access enforcement.
        TeamAdminLifecycleTests.tests
        TeamInvitationTests.tests
        EntraExternalIdConfigTests.tests
        WithRequestHeadersPassthroughTests.tests
        ConsentProviderTests.tests
        ConsentProviderTests.subscriptionFiringTests
        // Phase 159 — durable per-subject consent state store.
        ConsentStateStoreTests.tests
        ConsentStateStoreTests.entityBackedTests
        ConsentStateStoreTests.restartPersistenceTests
        AdAnalyticsSinkTests.tests
        AdAnalyticsSinkTests.noOpTests
        UserClaimsTests.tests
        ModuleGroupingValidatorTests.tests
        AnonymousModeContractTests.tests
        // Phase 245 — per-team module exposure folded into
        // `computeAccessibleModules` (separate `[<Tests>]` binding that
        // `runTestsWithCLIArgs` would otherwise leave dormant).
        AnonymousModeContractTests.exposureTests
        ModuleSurfaceRequirementTests.tests
        ModuleSurfaceRequirementTests.visibilityTests
        BuiltInModuleSurfaceTests.tests
        // Phase 171 — Home/Overview verification (separate `[<Tests>]`
        // bindings that `runTestsWithCLIArgs` would otherwise leave
        // dormant): the server-side CountObjects affordance + the
        // GetOverview scope-correctness + ActiveAi-absence handler check.
        // The client-tier landing-selection test lives in the Fable
        // harness (ToolUp.AI.Client.Tests/HomeLandingTests.fs) — see the
        // note in HomeOverviewTests.fs for why it can't run in-process.
        HomeOverviewTests.countAffordanceTests
        HomeOverviewTests.overviewScopeAiTests
        // NOTE: `HomeOverviewTests.overviewActiveAiPresentTests` (the
        // IActiveAiProbe probe-present converse, registered by the Phase
        // 14t commit) was never committed on the HomeOverviewTests.fs
        // side, leaving this project uncompilable — the dangling
        // registration is removed until the test body lands.
        // Phase 217 — IHomeWidgetDataProvider merge + scope-correctness.
        HomeOverviewTests.widgetDataTests
        // Phase 217 — recents/pinning per-user round-trip + scope isolation.
        HomeOverviewTests.pinningTests
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
        // Phase 69n — fromContextAsync build-once dispatcher table.
        // Source-audit pack pinning the `buildDispatcherTable` carve +
        // the compose-time bind in the FromContextAsync arm.
        FromContextAsyncBuildOnceTests.tests
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
        // Always-on wire-DTO pack for the DockerLocal companion — the
        // contract leg above needs a Docker socket; this one pins the
        // STJ serialisability of the Docker API DTOs everywhere.
        DeployPlaneTests.dockerWireDtoTests
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
        ClientToolkitThemingTests.tests
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
        PlatformPeerTests.audienceBindingTests
        // Phase 308 — job-poll caller-ownership scoping: a parked
        // long-running result is readable only by the peer that
        // scheduled it.
        PlatformPeerTests.jobPollOwnershipTests
        // Phase 18c — federation primitives: IPeerFanout (scatter to N
        // peers, total result map under timeout / quorum / first-success)
        // + IPeerCascade (next-hop Route / HopsRemaining bookkeeping with
        // caller-side loop / budget guards).
        PeerFederationTests.fanoutTests
        PeerFederationTests.cascadeTests
        // Phase 18b — clean-room privacy-gate broker: surface enforcement,
        // gate composition (tighten-only), per-cell suppression, k-floor.
        CleanRoomBrokerTests.tests
        // Giraffe stock-helper DI defaults — the SDK composition registers
        // INegotiationConfig + Json.ISerializer (FableConverters-backed) +
        // Xml.ISerializer so consumer handlers can use RequestErrors.* /
        // negotiate / json without a MissingDependencyException; TryAdd
        // semantics keep consumer-registered overrides winning.
        GiraffeStockHelperTests.tests
        // Phase 18a — cross-deployment audit transparency: caller-scoped
        // audit projection + bespoke context-aware dispatch + typed proxy
        // round-trip. The scoping (a peer reads only its own rows) is the
        // security-critical property under test.
        IPeerAuditTransparencyContract.tests
        // Phase 18d — sophisticated capability negotiation: per-method
        // profile resolution (Active / Deprecated / Removed at the highest
        // mutual version), profileFor reflection + overlay, fromCapability-
        // List degradation, and the handshake NegotiateMethod wrapper.
        IPeerCapabilityNegotiationContract.tests
        // Phase 18e — non-F# peer SDK: language-neutral schema export +
        // TypeScript / Python generator emit-correctness (type vocabulary,
        // record flattening, schema JSON round-trip, generated source
        // declarations + JSON-RPC POST skeleton).
        IPeerNonFSharpSdkContract.tests
        // Wave 8 (Epoch 2) Phase 188 — field-classification egress / DLP
        // gate: permissive default is a pure pass-through (GP 13), opt-in
        // deny rules redact / block the matching ClassificationLevel, and
        // every non-Allow decision emits exactly one EgressBlocked audit row.
        IEgressGateContract.tests
        // Wave 8 (Epoch 2) Phase 187 — compliance evidence-pack generator:
        // deterministic signed manifest over composed audit / classification
        // / DSR substrate, classification sidecar fidelity, and the disabled
        // (NoEvidencePackGenerator) default.
        IEvidencePackGeneratorContract.tests
        // Phase 174 — architecture-fitness dependency-direction gate.
        // Reflection over the compiled ToolUp.Platform.{Core,Server,Client}
        // assembly graph (no Server→Client / Client→Server / Core→upward
        // edge, no AG Grid Enterprise reference from the default Client
        // tier) + source-tree open scans (infra opens under Shared/,
        // Enterprise shim in the Client tree, cross-module opens across
        // the samples module set). Each live check is paired with a
        // fail-closed fixture so a green run means the gate checked
        // something, not that it found nothing to look at.
        ArchitectureFitnessTests.tests
        // Phase 175 — Public-API approval / baseline (SemVer guard).
        // Per-assembly surface diff over the packable ToolUp.* set vs
        // committed api-baselines/*.approved.txt (a removed / renamed /
        // retyped member fails and is named; additive growth passes) +
        // synthetic comparer fixtures (fails-closed on removal, no
        // false-positive on add). MetadataLoadContext, metadata-only.
        PublicApiApprovalTests.tests
        // Phase 195 — compile-time auth/audit analyzer recognition parity.
        // The source-linked Recognition.fs decision core vs the runtime
        // AuthClassifier (unclassified-set equality), plus TUR0001/TUR0002
        // finding shape and source-name normalisation.
        RemotingAnalyzerRecognitionTests.tests
        // Phase 196 — adversarial fail-closed pack: proves un-annotated /
        // mis-annotated / under-credentialled calls refuse to start or fail
        // closed (the inverse of the Phase 69d/69h happy-path coverage), plus
        // audit-omission observability + the PII-redaction default.
        AdversarialFailClosedTests.tests
        // Phase 169 — module-load startup observability (the addModule
        // outcome accumulator emitted through the startup logger).
        ModuleLoadOutcomeTests.tests
        // Phase 203 — hydration-parity conformance harness: SSR fragment vs
        // CSR mount structural normalisation + node-naming diff (gates the
        // silent hydration-mismatch class at build time, not the console).
        HydrationParityTests.tests
        // Phase 202 — second in-tree reference tree-binding: the
        // samples/ToyTreeBinding toy proves the Wave-16 seams are
        // renderer-neutral (fragment / live channel / action authorizer)
        // + the open-core grep-guard + the client-binding shape pin.
        SecondBindingNeutralityTests.tests
        // Phase 265 — reusable ClientHostCapabilities conformance bar:
        // the four-capability host-bridge seam (Navigate / Notify /
        // Dispatch / Call) asserted against the in-tree default and the
        // Phase 202 ToyNode second binding, so neutrality is asserted by
        // contract not by a one-off sample. A routing regression fails
        // this pack (and `Build.fsproj -- VerifyAll`).
        ClientHostCapabilitiesContract.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests