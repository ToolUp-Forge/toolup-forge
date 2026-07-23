module ToolUp.Platform.Tests.Program

open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.AI
open ToolUp.Platform.Tests.RAG
open ToolUp.Platform.Tests.Graph

let allTests =
    testList "ToolUp.Platform.Tests" [
        LocalFileStorageTests.tests
        InMemoryEventStoreTests.tests
        PersistentEventStoreTests.tests
        DataObjectStoreTests.tests
        // Phase 447 — seed / fixture-pack loader.
        SeedDataLoaderTests.tests
        // Phase 448 — IDatasetStore conformance (blob-backed default).
        DatasetStoreTests.tests
        // Phase 598 — Parquet companion codec: contract re-bind + codec pack.
        ParquetDatasetCodecTests.contractTests
        ParquetDatasetCodecTests.codecTests
        // Phase 452 — dataset assembly (transforms-as-data) executor.
        DatasetAssemblyTests.tests
        // Phase 601 — assembly re-vintage trigger + scheduling.
        DatasetRevintageTests.tests
        // Phase 487 — virtual (zero-copy) dataset bindings.
        VirtualDatasetTests.tests
        // Phase 482 — privacy-provenance labels + propagation + policy.
        ProvenanceLabelTests.tests
        // Phase 449 — model-fit envelope conformance (reference provider).
        IModelFitProviderContract.tests
        // Phase 603 — SpecHash opacity contract (submitter-minted, never re-derived).
        IModelFitProviderContract.opacityTests
        // Phase 453 — model registry conformance (blob-backed default).
        IModelRegistryContract.tests
        // Phase 454 — model-scoring seam conformance (reference scorer + blob store).
        IModelScorerContract.tests
        // Phase 456 — model evaluation & champion-challenger harness.
        ModelEvaluationTests.tests
        // Phase 599 — batch fit submission + bulk outcome/registry retrieval.
        ModelFitBatchTests.tests
        // Phase 600 — model-execution submitter API (wire surface + typed refusals).
        ModelExecutionApiTests.tests
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
        // Phase 519 — grounding metric & subject registry: dedup / conflict
        // diagnostics, read-surface lookups, ServerModule → ServerApp fan-in.
        MetricRegistryTests.tests
        // Phase 520 — grounding fact store: IFactStore contract pack (content-
        // address idempotency, AsOf reconstruction, supersession, competing
        // facts, scope isolation, disclosure/Absent round-trips) + BlobFactStore
        // audit emission + freshness derivation.
        FactStoreTests.tests
        FactStoreTests.auditAndFreshnessTests
        // Phase 566 — canonical-method selection for competing facts: selector
        // matching, canonical query default, explicit override, undeclared
        // parity, competition indicator.
        CanonicalMethodTests.tests
        // Phase 561 — reactive fact recomputation: RecomputePolicy registry
        // data, the lineage-driven invalidation walk (derived InputsChanged),
        // per-policy execution (Eager schedules, OnQuery defers, Manual
        // surfaces), UntilUpstreamChange freshness, and the recompute job
        // handler (idempotent re-assert, no-op on unchanged / missing).
        FactInvalidationTests.tests
        CoherenceCheckTests.tests
        // Phase 524 — provenance chain traversal: seeded ingest→run→fact→
        // message chain walks both directions; disclosure carried; scope isolation.
        ProvenanceGraphTests.tests
        // Grounding-plane wiring follow-ups: FactsCompose DI registration,
        // FactStoreEvidenceSource adapter, ConfigDriftDetector grounding parity.
        GroundingWiringTests.tests
        JobStoreTests.tests
        CronExpressionTests.tests
        JobSchedulerTests.tests
        ScheduledJobDeclarationTests.tests
        ModuleQueryBusTests.tests
        InMemoryDataSourceTests.tests
        // Phase 10g — OAuth 1.0a substrate (RFC 5849 signer + state store +
        // IOAuth1aFlow conformance pack).
        OAuth1aSubstrateTests.tests
        OAuth1aSubstrateTests.flowTests
        OAuthSubstrateTests.stateStoreTests
        OAuthSubstrateTests.credentialFlowTests
        OAuthSubstrateTests.pkceCredentialFlowTests
        OAuthSubstrateTests.pkceFlowTests
        OAuthSubstrateTests.refresherScrubTests
        OAuthSecretEncryptionModeValidatorTests.tests
        ShareTokenSigningKeyProvenanceValidatorTests.tests
        // Phase 329 — fail-loud DataProtection key-ring backend (validator + Warn).
        DataProtectionBackendTests.tests
        FileSecretStoreTests.tests
        FileSecretStoreAtomicityTests.tests
        // Phase 176 — transient-fault decorator substrate.
        TransientFaultPolicyTests.tests
        // Phase 238 — generic inbound-webhook receiver substrate.
        WebhookSubstrateTests.tests
        // Phase 235 — outbound webhook signing-secret rotation.
        WebhookSecretRotationTests.tests
        // Phase 6d.A — webhook secret-at-rest migration + preflight validator.
        // Carried `[<Tests>]` but was never in `allTests` — wired in by the
        // 2026-07-20 orphaned-pack audit.
        WebhookSecretMigrationTests.tests
        // Phase 241 — presence substrate.
        PresenceChannelTests.tests
        // Phase 442 — presence tracker + advisory soft-lock conformance.
        IPresenceTrackerContract.tests
        IEntityLockStoreContract.tests
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
        // Phase 328 — bounded idempotency-store eviction: the entries.Clear()
        // mass-wipe is replaced by a bounded FIFO drain; the over-cap
        // recovery path is observable (Warn + OverCapRecoveryCount).
        IdempotencyStoreEvictionTests.tests
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
        // Phase 531 — Postgres IEntityStore companion (env-gated).
        PostgresEntityStoreTests.tests
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
        KnowledgeUserScopeIsolationTests.tests
        HealthyHealthCheckTests.tests
        DegradedHealthCheckTests.tests
        UnhealthyHealthCheckTests.tests
        HealthCheckAggregatorTests.tests
        OkConfigValidatorTests.tests
        WarningConfigValidatorTests.tests
        ErrorConfigValidatorTests.tests
        ConfigValidatorAggregatorTests.tests
        // Security-class classification guard: every shipped auth/secret/
        // CSRF/provenance validator declares IsSecurityClass = true (so a
        // new one can't drift out of the SkipPreflight always-run set) +
        // the aggregator derives that set from the member.
        ConfigValidatorSecurityClassTests.tests
        // Phase 279 — stable component identity (ComponentId).
        ComponentIdentityTests.tests
        // Phase 280 — introspectable composition manifest.
        CompositionManifestTests.tests
        // Phase 286 — composition structural diff (id-keyed, order-independent).
        CompositionDiffTests.tests
        // Phase 287 — composition golden-file CI gate (mirrors Phase 175 api-baseline).
        ToolUp.Platform.Tests.Composition.CompositionBaselineTests.tests
        // Phase 282 — typed companion capability descriptors.
        CompanionCapabilityTests.tests
        // Phase 296 — CompanionCapability effect-join surface.
        EffectJoinTests.tests
        // Phase 300 — composition capability sandbox (runtime default-deny).
        CompositionCapabilityGateTests.tests
        // Phase 281 — composition well-formedness preflight.
        CompositionValidatorTests.tests
        // Phase 294 — composition invariant rule-manifest (well-formedness as data).
        InvariantRuleManifestTests.tests
        // Phase 594 — pinned data-vocabulary packs.
        DataVocabularyTests.tests
        // Phase 283 — component-id telemetry / audit correlation.
        ComponentIdCorrelationTests.tests
        // Phase 289 — component-scoped configuration binding: id-scoped
        // override reaches its component; stray override fails preflight.
        ComponentConfigTests.tests
        // Phase 293 — composable-surface descriptor: companion slots / config
        // knob schemas / module contract derived from the live registry.
        ComposableSurfaceTests.tests
        // Phase 526 — composition introspection covers grounding: manifest
        // reports registered metric/subject ids + fact-store kind; the
        // composable-surface descriptor enumerates the grounding surface;
        // grounding-free composition unchanged; rename-stable ids.
        CompositionGroundingTests.tests
        // Phase 288 — component provenance: package/version/assembly per
        // composed companion, id-joined to the manifest; total resolution.
        ComponentProvenanceTests.tests
        // Phase 290 — component health rollup: IHealthCheck results keyed by
        // ComponentId; unkeyed probes retained.
        ComponentHealthRollupTests.tests
        // Phase 291 — component lifecycle ordering: init-before partial order,
        // stable topo init/dispose, cycle rejected at compose.
        ComponentLifecycleTests.tests
        // Phase 301 — live composition hot-swap: atomic re-point + rollback,
        // in-flight finishes on old, only declared components swap.
        CompositionHotSwapTests.tests
        // Phase 284 — declarative composition descriptor + ServerApp.ofManifest:
        // descriptor builds an equivalent app; manifest round-trip law; unknown
        // component id fails readably.
        CompositionDescriptorTests.tests
        // Phase 292 — descriptor schema-version + migration: older→current
        // migrate + equivalent compose; too-new fails readably; no-op current.
        CompositionDescriptorVersionTests.tests
        // Phase 295 — descriptor completeness round-trip + partial/preset holes:
        // lossless lowering; preset + hole-binding equivalence; unfilled hole fails.
        DescriptorCompletenessTests.tests
        // Phase 302 — per-tenant composition presets: distinct variants from one
        // base preset; scope-isolated bindings; unbound-hole preflight failure.
        TenantCompositionPresetTests.tests
        ConfigReferenceTests.tests
        ConfigStartupModeTests.tests
        HealthStateTrackerTests.tests
        AlertRuleEngineTests.tests
        ServiceStatusBoardApiHandlerTests.tests
        DeploymentReadinessReportTests.tests
        RedisNotificationChannelHealthTests.tests
        // NOTE — Phase 9m.A AIProviderEnvValidator / AIModelEnvValidator /
        // AIProviderProbeValidator packs are deliberately NOT wired here. They
        // are orphaned (carry `[<Tests>]` but were never in `allTests`), but
        // they mutate PROCESS-GLOBAL env vars (`TOOLUP_AI_PROVIDER` /
        // `TOOLUP_AI_MODEL` / `ANTHROPIC_API_KEY` …) with snapshot/restore.
        // Under Expecto's default parallel runner they race each other (a
        // sibling's set/clear leaks across the `withEnv` window), producing
        // "expected Warning, got Ok" and wrong-value message assertions.
        // Wiring them needs a `testSequenced`/`testSequencedGroup` wrap over
        // the whole env-mutating family — Phase 9m.A follow-up, not a blind
        // add. See the 2026-07-20 orphaned-pack audit report.
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
        GitHubAuthProviderTests.tests
        GitHubAppFlowTests.tests
        LdapAuthProviderTests.tests
        // Phase 443 — WebAuthn / passkey companion: ceremony round-trip
        // (stub IFido2), counter-regression clone detection, invite
        // gating, challenge expiry, session-token round-trip, preflight.
        PasskeyAuthProviderTests.tests
        SSEHandshakeTests.tests
        EncryptedBlobStorageTests.tests
        TenantLifecycleAggregatorTests.tests
        LifecycleSummaryStoreTests.tests
        OffboardConfirmationTests.tests
        ScheduledDeprovisionTests.tests
        PrincipalRegistryTests.tests
        ITenantLifecycleContract.tests
        ILifecycleLockContract.tests
        LocalStorageEncryptionValidatorTests.tests
        BlobEntityStoreTests.tests
        // Phase 599 — entity-write outbox (write-ahead intent + version
        // witness: happy path, deferred publish, ghost prevention).
        EntityOutboxTests.tests
        EntityQueryTests.tests
        // Phase 19c — declarative relationship edges.
        RelationshipTests.tests
        // Phase 68 — IGraphStore conformance pack bound to InMemoryGraphStore
        // (six-rule GP12 audit + tenant isolation + subset-floor corpus +
        // cycle-safe termination + out-of-subset-throws).
        InMemoryGraphStoreTests.tests
        // Phase 68b — ToolUp.Graph.Neo4j engine companion: always-on Cypher-
        // translation unit pack + env-gated (TOOLUP_TEST_NEO4J_URI) live
        // IGraphStoreContract arm (skipped, never failed, without a server).
        Neo4jConformanceTests.pureTests
        Neo4jConformanceTests.liveTests
        // Phase 68c — ToolUp.Graph.AGE (Postgres-colocated) engine companion:
        // always-on cypher()-wrapping / agtype-mapping / injection-binding unit
        // pack + env-gated (TOOLUP_TEST_AGE_CONNSTRING) live IGraphStoreContract
        // arm (skipped, never failed, without an AGE-enabled Postgres).
        AgeConformanceTests.pureTests
        AgeConformanceTests.liveTests
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
        ShareTokenMiddlewareRateLimitTests.tests
        AnonymousSessionMigrationTests.tests
        StoreIdSanitisingTests.tests
        SecureByDefaultValidatorTests.tests
        InMemoryPendingInviteStoreTests.tests
        // Phase 205 — blob-corruption chaos / fault-injection pack: the shipped
        // Phase 116 fail-closed RMW sites under corrupt / torn / dropped writes
        // and concurrency (pending-invites decode+quarantine, share-token
        // MarkUsed UseLimit=1, KB index-container lock). Deterministic seed.
        BlobCorruptionChaosTests.tests
        BlobPlatformAIKeyStoreTests.tests
        MultiPlatformProviderResolutionTests.tests
        PlatformAIKeysHandlerRbacTests.tests
        AISurfaceCapabilityTests.tests
        InProcessOAuthTokenRefresherTests.tests
        BlobProviderProfileTests.tests
        SseTraceContributorTests.tests
        HeaderAuthProviderModeValidatorTests.tests
        AuditLogModeValidatorTests.tests
        AuditLogHealthCheckTests.tests
        // Phase 114 — audit-write failure metric + audit-event registry
        // exhaustiveness gate. Both packs carried `[<Tests>]` (assuming
        // auto-discovery) but `runTestsWithCLIArgs` only runs the supplied
        // `allTests` list — wired in by the 2026-07-20 orphaned-pack audit
        // (same class as the SvgPropTests note further down).
        AuditWriteFailureMetricTests.tests
        AuditEventRegistryTests.tests
        // Phase 9t — audit-write failure policy (LogAndContinue / RefuseAction
        // / DegradeToFile + fallback spill capacity + poison quarantine).
        AuditFailurePolicyTests.tests
        DegradedCapabilityRegistryTests.tests
        AuthAuditHookTests.tests
        // Phase 272 — hosted-tree action audit emission (GP 6): authorized/
        // denied actions emit HostActionDispatched; authorize-then-audit is one
        // path; disabled hook is zero-cost; codec round-trip.
        HostActionAuditTests.tests
        NotificationSilentlySkippedTests.tests
        EncryptedSecretStoreModeValidatorTests.tests
        JobSchedulerInstanceValidatorTests.tests
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
        // NOTE — Phase 248 OidcAuthValidatorTimeoutTests is deliberately NOT
        // wired here. Orphaned (carries `[<Tests>]`, never in `allTests`), but
        // its 8 tests mutate the process-global `TOOLUP_OIDC_ISSUER` /
        // `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` env vars with snapshot/restore and
        // race each other under Expecto's parallel runner (the exact-Timeout
        // assertions flake when a sibling clears the var mid-call). Same class
        // as the Phase 9m.A AI validators above — needs a `testSequenced` wrap.
        // See the 2026-07-20 orphaned-pack audit report.
        // Phase 247 — invite-by-email capability validator (warns when the
        // invite surface mounts with no IUserDirectory). Same audit.
        InviteEmailCapabilityValidatorTests.tests
        SecurityHeadersValidatorTests.tests
        // CSP-nonce cache validator + security-headers baseline floor —
        // sibling `[<Tests>]` bindings in the same file that were never in
        // `allTests`; wired in by the 2026-07-20 orphaned-pack audit.
        SecurityHeadersValidatorTests.cspNonceCacheValidatorTests
        SecurityHeadersValidatorTests.baselineFloorTests
        SecurityHardeningTests.tests
        // CSP middleware pack — sibling `[<Tests>]` binding, same audit.
        SecurityHardeningTests.cspMiddlewareTests
        // Phase 209 — internet-readiness secure-default scorecard (pure
        // projection over aggregated preflight outcomes). Same audit.
        InternetReadinessScorecardTests.tests
        ForwardedHeadersTrustValidatorTests.tests
        // Phase 325 — trusted-proxy CIDR allowlist + auth-mode escalation.
        ForwardedHeadersTrustTests.tests
        LocalSecretFilePermissionsValidatorTests.tests
        IdentitySanitiserTests.tests
        FileManagementTests.tests
        // Phase 495 — module API-factory helper + reference migration.
        ModuleApiFactoryTests.tests
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
        // Phase 270 — hosted-tree capability/version negotiation gate:
        // matched sets mount clean; missing capability / below-min version
        // fail with a structured mismatch naming the gap.
        HostCapabilityNegotiationTests.tests
        // Phase 268 — hosted-tree render-failure telemetry sink: faults
        // reach the sink with the right kind + node id; NoOp swallows;
        // counting decorator + onMismatch bridge + forwarding default.
        HostRenderTelemetryTests.tests
        // Phase 273 — SSR hosted-tree error-boundary: a throwing node yields
        // a structured fallback + a Phase 268 sink fault + a completed page;
        // healthy tree unchanged; fallback hydrates parity-clean.
        HostRenderBoundaryTests.tests
        // Phase 297 — ComponentId-keyed hosted-tree usage export: usage
        // events attribute to their ComponentId, scope-isolated snapshot,
        // NoOp default records nothing.
        HostedTreeUsageExportTests.tests
        // Phase 274 — hosted-tree content sanitization seam: injection
        // classes stripped (script / iframe / javascript: / on* / style /
        // unknown tag+attr), safe HTML + markdown preserved, client↔SSR
        // determinism, OSS grep-guard.
        HostContentSanitizerTests.tests
        // Phase 275 — hosted-tree i18n resolution seam: key + placeholder
        // resolves per locale (fallback), missing key flagged not blanked,
        // pseudolocalisation passthrough (qps-ploc audit).
        HostI18nResolverTests.tests
        // Phase 277 — hosted-tree a11y conformance harness: clean fixture
        // passes; each seeded violation class (unlabelled control / missing
        // role / focus-order break / heading skip) fails with a diagnostic;
        // the ToyNode witness is checkable. Runs under VerifyAll.
        HostedTreeA11yTests.tests
        // Phase 278 — hosted-tree render-cost budget gate: evaluate trips
        // node/depth/render-time breaches with a readable report; measureTree
        // over the ToyNode witness; over-budget reports through the Phase 268
        // sink (non-fatal) + enforce hard-fail; not-configured = no measurement.
        HostRenderBudgetTests.tests
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
        // Phase 271 — neutral tree-patch transport envelope: wire round-trip,
        // pure gap-detector, ordered incremental delivery, gap → resync →
        // resume end-to-end, GP 4 scope isolation over the Phase 112 channel.
        TreePatchChannelTests.tests
        // Phase 264 — host-state binding-source projection seam: CSR
        // projection round-trip, SSR scope-isolation, GP 13 zero-cost,
        // toy read-side resolves on both projection paths, OSS grep-guard.
        HostStateProjectionTests.tests
        // Phase 299 — owning ComponentId on the hosting seam (identity
        // bridge): a tagged host carries its owner; an interaction event +
        // a binding resolution attribute to it; an untagged host is pre-299.
        HostingSeamComponentIdTests.tests
        // Phase 267 — multi-region hosted-tree composition: withElementPanes
        // / withElementPages populate the SplitPanel / PageViews slots, a
        // hosted tree renders into every region, capabilities reach concretes
        // from each region, every PageContent case drives, GP 11 + grep-guard.
        HostedTreeLayoutTests.tests
        // Phase 276 — hosted-tree navigation/route contract: client deep-link
        // + Phase 264 param round-trip, SSR route-table registration +
        // crawlability, back/forward consistency, append-only registration.
        HostRouteContractTests.tests
        // Phase 298 — live preview of an unreduced composition's view subtrees:
        // pure Rendered/Placeholder decision, safe degradation, edit-re-preview
        // without rebuild, Phase 264 read-side parity.
        UnreducedViewPreviewTests.tests
        PublicRenderingTests.tests
        // Phase 149/150/157 — sitemap conditional-GET + sharding + search index.
        SitemapSearchIndexTests.tests
        // Phase 85 — NarrativeFromData analytics → Narrative projectors.
        NarrativeFromDataTests.tests
        // Phase 521 — fact-referencing narrative Metric spans.
        NarrativeFactsTests.tests
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
        // Phase 199 — render-cache request coalescing (cold-key stampede
        // protection): IRenderCoalescer single-flight contract + handler
        // concurrent-miss collapse (resolve-once) integration.
        RenderCoalescingTests.tests
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
        // Phase 14r — tool-aware RAG framing (live-interface detection + companion).
        ToolAwareRagFramingTests.tests
        // Phase 522 — fact-first retrieval.
        FactFirstRetrievalTests.tests
        // Phase 523 — numeric-fidelity answer gate.
        AnswerVerifierTests.tests
        // Phase 525 — disclosure egress filtering, first choke points.
        DisclosureEgressTests.tests
        // Phase 564 — disclosure egress: export door + webhook contract.
        DisclosureExportWebhookTests.tests
        // Phase 562 — taint-propagating disclosure + declassification routines.
        DisclosureTaintTests.tests
        // Phase 558 — fact-resolver compose wiring: the IFactStore-backed
        // resolver, the one-knob DI registration, the composed Stage-1 loop.
        FactResolverComposeTests.tests
        // Phase 559 — the query_facts AI tool: declaration + one-knob
        // registration, disclosure-gated results (allow / deny / unknown-id
        // markers), deny audit, scope isolation, parameter validation.
        FactQueryToolTests.tests
        // Phase 560 — the grounded answer planner: compiler table (refusal
        // over fabrication), per-branch resolution (UseFact / RefreshFact /
        // ComputeFact / RequestData / Refuse), disclosure interplay, the
        // plan-node round-trip through the provenance chain walk, compose
        // registration.
        AnswerPlannerTests.tests
        // Phase 565 — grounding certificates: sealed, selective provenance
        // disclosure. Issue→verify round-trip (offline against the deployment
        // public key), tamper detection on any byte change, the disclosure
        // predicate withholding a fact's structure (value never present), and
        // the GP-13 no-signing-substrate refusal.
        GroundingCertificateTests.tests
        // Phase 63 — StaticCorpus MessagePack round-trip + determinism.
        StaticCorpusContract.tests
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
        // Phase 545 — user-scope offboard completeness: PurgeUser
        // (last-Owner refusal / multi-team strip / pointer cleanup /
        // idempotent re-purge) + the user-membership-teardown hook +
        // end-to-end DeprovisionTenant("user-<id>").
        UserMembershipTeardownTests.tests
        // Phase 546 — membership-integrity doctor: drift classification +
        // safe-subset repair (audit-attributed, cache-evicting), with
        // email-keyed / unresolvable rows kept report-only.
        MembershipDoctorTests.tests
        TeamInvitationTests.tests
        // Pending-invite expiry audit + active-team invitation policy —
        // sibling `[<Tests>]` bindings in TeamInvitationTests.fs that were
        // never in `allTests`; wired in by the 2026-07-20 orphaned-pack audit.
        TeamInvitationTests.pendingInviteExpiryAuditTests
        TeamInvitationTests.activeTeamPolicyTests
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
        // NOTE — BuiltInModuleSurfaceTests.visibilityTests is deliberately NOT
        // wired here. It is orphaned (carries `[<Tests>]`, never in `allTests`)
        // but constructs the SDK's built-in *client-side* UI modules
        // (`FileManagerUI.create`, `DataSubjectRequestAdminUI.create`,
        // `HealthMonitorUI.create`, …), whose bodies touch `Icons` — Fable
        // `importDefault` dummy-code that throws under .NET ("You've hit dummy
        // code used for Fable bindings"). It is a Fable-tier pack, same class
        // as the HomeOverviewTests client-tier landing test that lives in the
        // ToolUp.AI.Client.Tests Fable harness. See the 2026-07-20
        // orphaned-pack audit report.
        // Phase 171 — Home/Overview verification (separate `[<Tests>]`
        // bindings that `runTestsWithCLIArgs` would otherwise leave
        // dormant): the server-side CountObjects affordance + the
        // GetOverview scope-correctness + ActiveAi-absence handler check.
        // The client-tier landing-selection test lives in the Fable
        // harness (ToolUp.AI.Client.Tests/HomeLandingTests.fs) — see the
        // note in HomeOverviewTests.fs for why it can't run in-process.
        HomeOverviewTests.countAffordanceTests
        HomeOverviewTests.overviewScopeAiTests
        // The "probe-present converse" of overviewScopeAiTests: with a real
        // IActiveAiProbe registered over a PlatformOnly factory, GetOverview
        // surfaces the wired platform provider rather than "No AI provider
        // configured." Pins the v0.9.4 RAG-path fork-drift regression. The body
        // (Phase 14t) was authored after 26af8d9 removed the dangling reference.
        HomeOverviewTests.overviewActiveAiPresentTests
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
        // Phase 246 — subject-downgrade observability (resolver downgrade
        // signal + fail-closed undeclared-kind scope derivation). Carried
        // `[<Tests>]` but was never in `allTests` — wired in by the
        // 2026-07-20 orphaned-pack audit.
        SubjectDowngradeObservabilityTests.tests
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
        // Phase 590 — PeerSurface descriptor: the deployment's
        // cross-instance face derived from the composed peer
        // registrations, with a deterministic hash-stamped export.
        PeerSurfaceTests.tests
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
        // Phase 57 follow-up — prerender determinism + hydration-mismatch
        // contract. Carried `[<Tests>]` but was never in `allTests` — wired
        // in by the 2026-07-20 orphaned-pack audit.
        PrerenderDeterminismTests.tests
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
        // Phase 285 — IComponentRegistryContract conformance pack: the
        // reusable Phase 279 identity-law suite bound to the in-tree default
        // ServerApp registry (rename / re-order stability, deterministic
        // default derivation, duplicate rejection, id-keyed manifest
        // completeness), plus a self-test proving the pack fails a
        // non-conforming (unstable / positional / duplicate-tolerating)
        // registry. A regression fails `Build.fsproj -- VerifyAll`.
        ComponentRegistryContract.tests
        ComponentRegistryContract.selfTests
        // Phase 208 — codified threat-lens security-regression suite: the six
        // manual audit lenses as recurring red-team regression cases + a
        // reverted-control proof that the suite catches regressions, not just
        // passes. A reverted security control fails the matching lens here.
        ThreatLensRegressionSuite.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests