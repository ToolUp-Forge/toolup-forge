module ToolUp.Platform.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.AI
open ToolUp.Platform.Tests.RAG
open ToolUp.Platform.Tests.Graph
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
        // Phase 10a — module data-migration framework.
        DataMigrationTests.tests
        // Phase 10b — configuration schema evolution.
        ConfigMigrationTests.tests
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
        IModelEvaluationPlanMetricsContract.tests
        // Phase 318 — external-compute broker seam: the NoExternalCompute
        // default, the Fable-JSON round-trip of the core types, and the
        // Phase 9c six-rule portability audit.
        IExternalComputeDispatcherContract.tests
        IExternalComputeDispatcherContract.wireTests
        IExternalComputeDispatcherContract.portabilityAudit
        // Phase 324 — the conformance bar every IExternalComputeDispatcher
        // companion must pass UNMODIFIED before it is called stable: the
        // parameterised submit/poll/terminal + idempotency + cancel + scope
        // suite (bound against the reference backend and both shipped
        // decorator stacks over it), the IExternalHandleStore sub-pack (bound
        // against both shipped stores), and the two self-tests that prove
        // each pack rejects a deliberately non-conformant implementation.
        IExternalComputeDispatcherContract.conformanceTests
        IExternalComputeDispatcherContract.selfTests
        IExternalHandleStoreContract.tests
        IExternalHandleStoreContract.selfTests
        // Phase 456 — model evaluation & champion-challenger harness.
        ModelEvaluationTests.tests
        // Phase 645 — registry promotion policies (tolerance-gated
        // auto-promotion through the Phase 644 transition seam).
        ModelPromotionPolicyTests.tests
        // Phase 651 — the registration observer seam: the "a new artifact
        // exists" moment as a decorator, with the replay path firing
        // nothing and the promotion policy as its first binding.
        ModelRegistrationObserverTests.tests
        // Phase 646 — promotion-time provenance transfer: the opaque
        // attachment slot, the signed acceptance, and the Phase 524 walk
        // that resolves it all with the builder gone.
        ModelPromotionTransferTests.tests
        // Phase 599 — batch fit submission + bulk outcome/registry retrieval.
        ModelFitBatchTests.tests
        // Phase 600 — model-execution submitter API (wire surface + typed refusals).
        ModelExecutionApiTests.tests
        // Phase 728 — the opt-in model-execution compose leg: the registry
        // composed rather than hand-registered, the byte-parity of composing
        // nothing, and the startup validator that names the gap.
        ModelExecutionComposeTests.tests
        IngestionStatusTests.tests
        IngestionRetryTests.tests
        // Phase 14t — embedder retry + dead-letter (classification / backoff / alerts).
        IngestionEmbedderRetryTests.tests
        // Phase 303 — ingestion-queue backpressure observability.
        IngestionBackpressureTests.tests
        // Phase 509 — durable ingestion queue. The seam arm pins that a
        // deployment composing no store is unchanged; the store-contract
        // arm pins the three properties the acceptance rests on (atomic
        // claim under concurrent drainers, reclaim-and-redeliver across a
        // simulated restart, attempt-capped redelivery), and runs against
        // the Redis companion too when TOOLUP_REDIS_CONNECTION is set.
        DurableIngestionQueueTests.tests
        ColumnMatcherTests.tests
        // Phase 218 — CSV-mapping dry-run validation preview.
        MappingDryRunValidationTests.tests
        // Phase 219 — derived/computed columns in CSV mapping.
        DerivedColumnEvalTests.tests
        IColumnMappingStoreContract.tests
        // Phase 552 — IGrantConsentStore conformance over both shipped
        // implementations (in-memory + blob-backed).
        IGrantConsentStoreContract.tests
        // Phase 7b — user-authored schema store conformance (CRUD, version
        // history, migration direct + via the job handler, scope isolation,
        // audit emission).
        IUserSchemaStoreContract.tests
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
        // Phase 704 — batch fact assertion: the semantics ride the contract
        // pack above (both read models); this is the summarised-audit half —
        // one FactBatchAsserted row per batch, the per-fact shape kept for
        // scalar Assert, and the deliberate audit of an all-idempotent batch.
        FactStoreTests.batchAssertTests
        // Phase 701 — population fact query: the registry-directed half of the
        // IFactStore population contract (DirectionOfBetter ordering, the
        // Neutral / unregistered refusals, D19 canonical-vs-all selection) plus
        // the population pipeline measured at the requirement's cardinality.
        FactStoreTests.populationRegistryTests
        FactStoreTests.populationScaleTests
        // Phase 702 — the metric surface: the same IFactStore contract run a
        // second time through the derived current-heads read model, the two
        // paths' population results compared directly across a query matrix,
        // the maintenance paths (supersession / competition / neighbouring
        // metric / out-of-band write / flush-and-rebuild / AsOf bypass /
        // fallback threshold), and the read measured at 100,000 heads.
        FactStoreTests.surfaceTests
        FactStoreTests.surfacePopulationRegistryTests
        FactStoreTests.metricSurfaceTests
        FactStoreTests.metricSurfaceScaleTests
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
        // Phase 648 — the walk as a typed read-only wire contract: mirror
        // conformance against the server records, the read-only shape pin,
        // chain round-trip, cap refusal (never truncation), and the
        // withheld-marker vs absent distinction at the export door.
        ProvenanceGraphWireTests.tests
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
        // Phase 457 — the at-rest posture of the store that is actually composed.
        SecretStoreAtRestPostureValidatorTests.tests
        ShareTokenSigningKeyProvenanceValidatorTests.tests
        // Phase 460 — share-token signing-key governance (refusal ladder, provenance, race).
        ShareTokenSigningKeyGovernanceTests.tests
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
        // Phase 622 — presence + lock platform API (scope isolation,
        // lock contention, heartbeat fold, hand-mounted path).
        PresenceApiTests.tests
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
        // Phase 12d — AG Grid value-provenance overlay.
        CellProvenanceTests.tests
        // Phase 578 — AG chart capture-to-placeholder export helper.
        AgChartExportTests.tests
        AuthProviderTests.tests
        // Phase 463 — OIDC JWKS configurable TTL + surfaced revocation window.
        OidcJwksTtlTests.tests
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
        // Phase 335 — auth-attribute matching by CLR identity: a
        // colliding third-party attribute cannot classify a method, and
        // the collision refuses startup by name.
        AuthClassifierAttributeIdentityTests.tests
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
        // Phase 727 — the same question 335 answered for the auth
        // classifier, asked of the four families riding alongside it:
        // audit / PII-safety, rate limiting and idempotency move to CLR
        // identity + a startup collision refusal; validation deliberately
        // keeps simple-name matching (pinned, with the reason recorded in
        // Validation.fs); the Streaming diagnostic agrees with the
        // classifier on a forged marker.
        AttributeRecognitionSweepTests.tests
        StreamingDispatchTests.tests
        MarkdownRendererTests.tests
        HtmlRendererTests.tests
        // Tidy-Up (grounding-wave hygiene) — regression guard proving the
        // default renderer registry resolves a renderer for every zero-dep
        // format. Catches the `open`-shadowing class that left Markdown
        // unregistered while the code read as if it were registered.
        ReportingComposeTests.tests
        // Phase 619 — IReportApi secure-by-default authorization: every
        // method gated, the anonymous path proved refused (with a
        // falsifier fixture), and the in-handler management gate.
        ReportingAuthorizationTests.tests
        // Phase 647 — the deck-export seam: the Pptx routing posture
        // (refuses toward the deck tier, unconditionally) and the
        // chart-artifact handoff (determinism + provenance metadata,
        // driven through the real chart grammar).
        DeckExportSeamTests.tests
        // Phase 650 — the chart export bundle: the positional keying rule
        // (including a nested container and two identical blocks), the
        // per-block typed refusals that keep a bundle partial rather than
        // failed, determinism with its falsifier, and the pin that the
        // bundle runs the one `FactExport` door rather than a second one.
        DeckExportSeamTests.bundleTests
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
        // Phase 9i — IDistributedLockContract bound to the Redis reference
        // impl; pending unless TOOLUP_REDIS_CONNECTION is set.
        RedisDistributedLockTests.tests
        TransactionalDispatcherTests.tests
        NotificationAddressBookTests.tests
        SmtpNotificationSinkTests.tests
        SendGridNotificationSinkTests.tests
        TwilioNotificationSinkTests.tests
        WebPushNotificationSinkTests.tests
        HnswVectorStoreTests.tests
        // Phase 507 — the external rung of the vector-store scale story.
        // Structural arm always on (scope isolation read off the SQL,
        // create-time guards, codecs); live arm Pending unless
        // TOOLUP_PGVECTOR_CONNECTION_STRING is set.
        PgvectorVectorStoreTests.tests
        // Phase 513 — Redis IEmbeddingCache companion. Structural arm
        // always on; live arm Pending unless TOOLUP_REDIS_CONNECTION is set.
        RedisEmbeddingCacheTests.tests
        // Phase 14z — scope-keyed LocalEmbeddingProvider. Isolation is
        // asserted differentially against a pristine family, so a shared
        // vocabulary makes the pack red; the "guard is load-bearing"
        // case is the control that stops the comparison being vacuous.
        LocalEmbeddingScopeTests.scopeKeyTests
        LocalEmbeddingScopeTests.isolationTests
        LocalEmbeddingScopeTests.resetTests
        LocalEmbeddingScopeTests.persistenceTests
        LocalEmbeddingScopeTests.backwardCompatibilityTests
        // Phase 14z, Option 1 — the capability probe + its consequences:
        // cross-scope retrieval (the Phase 4b acceptance criterion), the
        // cache keying that stops a shared entry re-creating the leak one
        // layer up, and the GP 11 cost guard that keeps a stateless
        // provider on one query vector.
        LocalEmbeddingScopeTests.capabilityProbeTests
        LocalEmbeddingScopeTests.cacheKeyingTests
        LocalEmbeddingScopeTests.crossScopeRetrievalTests
        // Feature-hashed dimension assignment: a term's slot is a
        // function of the term, so a growing corpus can no longer move a
        // previously-indexed chunk into a coordinate space its query
        // does not share. The "weights DO still move" case is the
        // control that stops the assignment case passing on a frozen
        // embedder.
        LocalEmbeddingHashingTests.dimensionAssignmentTests
        LocalEmbeddingHashingTests.spaceAlignmentTests
        // Phase 500 — the Tesseract IOcrProvider companion and the
        // "OCR unavailable" ingestion signal. Structural arm always on;
        // native arm Pending unless TOOLUP_TESSDATA is set.
        OcrProviderTests.tests
        // Phase 515 — upload-boundary content scanning. Structural arm
        // always on; live ClamAV arm Pending unless TOOLUP_CLAMAV_HOST is
        // set.
        ContentScannerTests.tests
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
        // Phase 585 — preflight rule classes: the composition validator's
        // identity/integrity rules are structural-class and survive
        // SkipPreflight; only the external-probe class is skippable.
        PreflightRuleClassTests.tests
        // Phase 462 — CORS credentials × wildcard refused at boot, before
        // any policy is registered (was: warn + silent credentials drop).
        CorsCredentialsWildcardBootTests.tests
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
        // Phase 688 — seam-granularity module authority grants.
        SeamAuthorityTests.tests
        // Phase 691 — the seam gate's first production call site.
        SeamAuthorityEnforcementTests.tests
        // Phase 281 — composition well-formedness preflight.
        CompositionValidatorTests.tests
        // Phase 294 — composition invariant rule-manifest (well-formedness as data).
        InvariantRuleManifestTests.tests
        // Phase 583 — module-graph composition rules (bus keys, wire TypeNames,
        // declared data needs, client/server module parity).
        ModuleGraphRuleTests.tests
        // Phase 597 — rule-manifest versioning + errata channel (the prover's own lifecycle).
        RuleVersioningTests.tests
        // Phase 594 — pinned data-vocabulary packs.
        DataVocabularyTests.tests
        // Phase 283 — component-id telemetry / audit correlation.
        ComponentIdCorrelationTests.tests
        // Phase 289 — component-scoped configuration binding: id-scoped
        // override reaches its component; stray override fails preflight.
        ComponentConfigTests.tests
        // Phase 432 — component secret & config requirements manifest:
        // derived knob requirements + declared secret requirements, presence
        // preflight, and the no-value-in-any-report property.
        ComponentRequirementsTests.tests
        // Phase 293 — composable-surface descriptor: companion slots / config
        // knob schemas / module contract derived from the live registry.
        ComposableSurfaceTests.tests
        // Phase 431 — event-topology manifest: who emits what and who
        // subscribes, derived from the live registrations; dead topics,
        // orphan subscriptions, the opt-in preflight rule, and the diff.
        EventTopologyTests.tests
        // Phase 433 — component data-footprint manifest: what each component
        // stores and where, derived from the live registrations; the
        // composition join, the DSR/offboarding coverage rule, and the diff.
        DataFootprintTests.tests
        // Phase 434 — composition scale-readiness planner: the Phase 282
        // readiness declarations joined across the manifest into a verdict
        // (the meet of the parts), the Phase 293-derived unblock suggestions,
        // and the opt-in preflight gate keyed on the topology intent
        // ServerConfig already declares.
        ScaleReadinessTests.tests
        // Phase 488 — appliance deployment profile: the declared-offline boot
        // posture (external probes downgraded, security / structural guards
        // still aborting), signed upgrade verification refusing a tampered
        // artefact with the mismatch named, the closed-schema telemetry diode
        // (no string anywhere in its transitive closure; off by default means
        // zero outbound), and the data-class-aware support-bundle redaction.
        ApplianceProfileTests.tests
        // Phase 492 — offline entitlement verification: a real ECDSA signature
        // admitted through the Phase 488.B seam and a different key pair
        // refused with the mismatch named, clock skew applied in the holder's
        // favour (with a zero-skew control), grace as a full-capability state
        // and lapse reducing only governed flag keys while read + export stay
        // reachable, the capacity budget, GP 13 unconfigured-means-unrestricted,
        // the floor's structural ungovernability, an offline-by-construction
        // closure walk falsified against a networked control, and the
        // exhaustive proof that the boot preflight never returns Error.
        EntitlementTokenTests.tests
        // Phase 438 — authorization-surface manifest: what each component
        // exposes and what each entry requires, derived from the live
        // registrations + the dispatcher's own Phase 69d classifier; the
        // anonymous-reachable headline, the policy resolution, and the
        // severity-bearing diff.
        AuthorizationSurfaceTests.tests
        // Phase 581 — module-surface descriptor: one module's provides / needs
        // derived from its own registrations; the coverage diff against the
        // reflected ServerModule / ErasedModule fields is the drift guard.
        ModuleSurfaceTests.tests
        // Phase 526 — composition introspection covers grounding: manifest
        // reports registered metric/subject ids + fact-store kind; the
        // composable-surface descriptor enumerates the grounding surface;
        // grounding-free composition unchanged; rename-stable ids.
        CompositionGroundingTests.tests
        // Phase 288 — component provenance: package/version/assembly per
        // composed companion, id-joined to the manifest; total resolution.
        ComponentProvenanceTests.tests
        // Phase 588 — host envelope: what a deployment offers a module.
        // Each axis is re-derived independently and asserted set-equal, so a
        // composition the envelope misses fails here; plus deterministic
        // canonical JSON and the SHA-256 staleness stamp.
        HostEnvelopeTests.tests
        // Phase 290 — component health rollup: IHealthCheck results keyed by
        // ComponentId; unkeyed probes retained.
        ComponentHealthRollupTests.tests
        // Phase 437 — per-component resource envelopes: the concurrency gate
        // holds a budgeted component to its ceiling and defers (never drops)
        // the rest; unbudgeted composition is byte-for-byte unchanged; every
        // refusal is observable; the rollup's pressure dimension is absent
        // when nothing is declared.
        ResourceEnvelopeTests.tests
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
        // Phase 436 — null-composition dry run: null-bind every companion slot,
        // rebuild, validate, drive the lifecycle; defects are typed findings.
        CompositionDryRunTests.tests
        // Phase 435 — cross-version composition upgrade planner: an identical
        // target surface (fixture + the Phase 287 golden baseline) plans empty;
        // widened / changed / removed slots and knobs carry their severities;
        // chained Phase 292 schema hops sequence in ascending order.
        CompositionUpgradePlanTests.tests
        // Phase 302 — per-tenant composition presets: distinct variants from one
        // base preset; scope-isolated bindings; unbound-hole preflight failure.
        TenantCompositionPresetTests.tests
        ConfigReferenceTests.tests
        ConfigStartupModeTests.tests
        ConfigResolverTests.tests
        ConfigProfileTests.tests
        UnknownConfigKeyValidatorTests.tests
        HealthStateTrackerTests.tests
        AlertRuleEngineTests.tests
        ServiceStatusBoardApiHandlerTests.tests
        DeploymentReadinessReportTests.tests
        RedisNotificationChannelHealthTests.tests
        // Phase 653 — the four previously-orphaned env-mutating config-validator
        // packs (Phase 9m.A AIProviderEnvValidator / AIModelEnvValidator /
        // AIProviderProbeValidator + Phase 248 OidcAuthValidatorTimeout), wired
        // under ONE shared `testSequencedGroup` so they serialise against each
        // other. Each snapshot/restores PROCESS-GLOBAL env vars
        // (`TOOLUP_AI_PROVIDER` / `_MODEL` / `_PROBE_ON_STARTUP` /
        // `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `TOOLUP_OIDC_ISSUER` /
        // `_PREFLIGHT_TIMEOUT_MS`); the AI three share `TOOLUP_AI_PROVIDER`, so
        // wrapping each list in its own `testSequenced` would NOT stop them
        // racing — one shared named group is required (a group serialises every
        // member carrying the same label). The pack runs `CLIArguments.Sequenced`
        // by default (see `main`), so this is belt-and-braces for a `--parallel`
        // invocation. Cross-serialising against already-wired env-touching lists
        // (e.g. `AuthProvider.fromEnv`, which also mutates `TOOLUP_OIDC_ISSUER`)
        // is a pre-existing latent `--parallel` coupling, out of scope here.
        testSequencedGroup
            "env-mutating-config-validators"
            (testList "env-mutating config validators (Phase 653)" [
                AIProviderEnvValidatorTests.providerTests
                AIProviderEnvValidatorTests.modelTests
                AIProviderProbeValidatorTests.tests
                OidcAuthValidatorTimeoutTests.tests
                // Phase 671 — EmbeddingProviderEnv.fromEnv mutates
                // TOOLUP_EMBEDDING_PROVIDER with the same snapshot /
                // restore shape. It shares no variable with the four
                // above, but its unset-arm assertion is exactly the
                // shape a leaked sibling `SetEnvironmentVariable`
                // breaks, so it belongs inside the group rather than
                // beside it.
                EmbeddingProviderEnvTests.tests
            ])
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
        // Phase 10f — Google Analytics 4 connector: IDataSource +
        // IOAuthCredentialFlow contract re-binds over a faked network,
        // query interpretation, schema catalogue, env-gated live arm.
        GoogleAnalyticsTests.tests
        LdapAuthProviderTests.tests
        // Phase 530 — SCIM 2.0 provisioning: recorded Entra ID + Okta
        // sequences (create → assign group → change role → deactivate)
        // replayed against the real TeamStore, plus the bearer gate,
        // scope isolation, and the RFC 7643/7644 wire model.
        ScimProvisioningTests.tests
        // Phase 443 — WebAuthn / passkey companion: ceremony round-trip
        // (stub IFido2), counter-regression clone detection, invite
        // gating, challenge expiry, session-token round-trip, preflight.
        PasskeyAuthProviderTests.tests
        SSEHandshakeTests.tests
        EncryptedBlobStorageTests.tests
        // Phase 22b — cross-replica encryption-key destruction.
        CrossReplicaKeyDestructionTests.tests
        // Phase 458 — the wiring the 22b fanout depends on: optional on one
        // replica, required on more, and now enforced rather than assumed.
        PerScopeKeyResolverWiringTests.unwiredDestroyTests
        PerScopeKeyResolverWiringTests.wiringValidatorTests
        PerScopeKeyResolverWiringTests.wiringDiagnosticsTests
        // Phase 464 — the same fanout shape for webhook signing-secret
        // rotation: two instances over one channel, unwired control.
        WebhookSecretRotationBroadcastTests.tests
        TenantLifecycleAggregatorTests.tests
        LifecycleSummaryStoreTests.tests
        OffboardConfirmationTests.tests
        ScheduledDeprovisionTests.tests
        PrincipalRegistryTests.tests
        ITenantLifecycleContract.tests
        ILifecycleLockContract.tests
        // Phase 9i — the SDK-wide cross-instance lease primitive
        // (in-process default; the Redis binding is env-gated below).
        IDistributedLockContract.tests
        LocalStorageEncryptionValidatorTests.tests
        BlobEntityStoreTests.tests
        // Phase 599 — entity-write outbox (write-ahead intent + version
        // witness: happy path, deferred publish, ghost prevention).
        EntityOutboxTests.tests
        // Phase 600 — blob conditional writes (the ETag CAS seam).
        ConditionalBlobStorageTests.tests
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
        // Phase 68d — entity↔graph projection bridge: pure projection,
        // incremental sync through the lifecycle signal, rebuild + orphan
        // reconciliation, tenant isolation, not-composed byte-identity,
        // six-rule audit.
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
        CefAuditSinkTests.tests
        ChainedAuditLedgerTests.tests
        AuditReplicatorTests.tests
        Ed25519ArtifactSubstrateTests.tests
        ShareTokenStoreTests.tests
        ShareTokenStoreTests.readPathTests
        ShareTokenAuthMiddlewareTests.tests
        ShareTokenMiddlewareRateLimitTests.tests
        // Phase 527 — service accounts. Three top-level lists, all
        // registered here: `--filter` matches a TOP-LEVEL list-name
        // prefix, so a list that is only referenced from another file is
        // selected by nothing and a filtered run reports a vacuous
        // green.
        ServiceAccountStoreTests.tests
        ServiceAccountStoreTests.pureTests
        ServiceAccountStoreTests.persistenceTests
        AnonymousSessionMigrationTests.tests
        // Phase 337 — listed here, not merely attributed: `runTestsWithCLIArgs`
        // runs `allTests`, so a `[<Tests>]` binding absent from this list is
        // dormant and a filtered run reports zero tests AND success.
        AnonymousSessionBindingTests.tests
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
        // Phase 248 OidcAuthValidatorTimeoutTests is wired above (Phase 653),
        // inside the "env-mutating-config-validators" sequenced group.
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
        // Phase 6j.E — beacon endpoint idempotency (dedup window + append plan).
        FastPathBeaconIdempotencyTests.tests
        FastPathSequencerTelemetryTests.tests
        // Phase 6j.B — Tier-3 triage: the plan stage, the agent-loop
        // intercept, and the /dev/ai-fastpath rollup.
        FastPathTriageResolverTests.tests
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
        // Phase 204 — the property twin of the pack above. Listed here and
        // not merely attributed: `runTestsWithCLIArgs` runs `allTests`, so an
        // `[<Tests>]` binding absent from this list is dormant, not run.
        CrossIndexErasureConformanceTests.tests
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
        // Phase 180 — accessibility assertions in the module testing harness:
        // the axe-style rule set over a rendered node tree, the Minimal /
        // Strict profile split, the standalone `Accessibility.assert` entry,
        // `ModuleHarness.AssertAccessible` chaining, and the SDK's own stock
        // (BrandKit SSR) components regression-guarded through Minimal.
        AccessibilityAssertionsTests.tests
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
        // Phase 632 — structural public-content enumeration gate.
        PublicEnumerationGateTests.tests
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
        // Phase 212 — SEO / structured-data conformance lint: the JSON-LD
        // emitters, the sitemap <urlset>/<sitemapindex>, canonical +
        // hreflang across the host-aware site registry, the robots
        // directive vocabulary, and the negative self-tests that prove
        // each rule rejects a malformed input.
        StructuredDataConformanceTests.tests
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
        // Phase 108 — time-bound direct-download URLs for KB originals.
        KnowledgeSignedOriginalUrlTests.tests
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
        MultiPartyDisclosureTests.tests
        // Phase 558 — fact-resolver compose wiring: the IFactStore-backed
        // resolver, the one-knob DI registration, the composed Stage-1 loop.
        FactResolverComposeTests.tests
        // Phase 559 — the query_facts AI tool: declaration + one-knob
        // registration, disclosure-gated results (allow / deny / unknown-id
        // markers), deny audit, scope isolation, parameter validation.
        FactQueryToolTests.tests
        // Phase 703 — the query_metric_population AI tool: declaration +
        // the one-knob double registration, the bounded ranking + summary,
        // the reported ceiling, population-scale disclosure (a policy-
        // grouped count, true rank gaps, gated magnitudes), the two typed
        // ordering refusals, and the end-to-end demo.
        PopulationQueryToolTests.tests
        // Phase 705 — metric context + coverage discovery: the
        // `list_metric_coverage` entry point (registry declarations with
        // their Context, per-hierarchy cardinality / period reach /
        // freshness / method mix), the disclosure folding, and the
        // counts-never-values boundary between discovery and the
        // population read.
        CoverageToolTests.tests
        // Phase 707 — the coverage narrative: the pure generator (bands,
        // never exact counts; the registry context quoted; no value), the
        // material-change predicate and what is deliberately outside it,
        // the 705.B disclosure posture through Phase 706's shared fold,
        // the assertion trigger's one-commit-per-metric invariant across
        // both assertion doors, and the real knowledge-base commit door.
        CoverageNarrativeTests.tests
        // Phase 560 — the grounded answer planner: compiler table (refusal
        // over fabrication), per-branch resolution (UseFact / RefreshFact /
        // ComputeFact / RequestData / Refuse), disclosure interplay, the
        // plan-node round-trip through the provenance chain walk, compose
        // registration.
        AnswerPlannerTests.tests
        // Phase 708 — the fact-clause feeder that finally FIRES the Phase
        // 522 push path in production: plan steps projected into a
        // `RetrievalRequest.FactClause`, the fact leading the prompt under
        // the verbatim contract, byte-identity where nothing resolved, the
        // bounded compile degrading to no clause, and the plan reused for
        // provenance rather than recompiled.
        FactClauseFeederTests.tests
        // Phase 709 — the AI tool-result context budget, the last guard of
        // the population arc: a generous default that changes nothing, a
        // per-tool override and a NoBudget escape, and an over-budget
        // result replaced by a typed JSON marker that names the tool and
        // the elided size — visible rather than silently truncated, and
        // deliberately unlike both a policy withhold and a tool error.
        AIToolResultBudgetTests.tests
        // Phase 36.A — AI tool-dispatch RBAC. The per-turn tool list, the
        // client-visible listing, the agent-loop dispatch site and the
        // /api/ai/tool-result completion all gate on the caller's per-module
        // Read; a forged tool name is refused with a typed Denied before the
        // executor runs and lands a _platform.ai.unauthorized_tool audit row.
        AIToolDispatchRbacTests.tests
        // Phase 551 — module-declared grant policy. Fail-closed policy /
        // grant-state parse (no mangled token reads as AdminDiscretion),
        // narrowing-only composition, the write guard per arm, dispatch
        // refusal of a grant row injected straight into the store (the
        // Phase 311 property — write-path-only enforcement is
        // insufficient), and the GP 11 / GP 13 floor: an all-default
        // deployment composes an empty registry and short-circuits.
        GrantPolicyTests.tests
        // Phase 555 — dual control (two-person rule) over sensitive admin
        // mutations. The full ceremony (a gated grant is inert until a
        // second, DISTINCT administrator approves), structural refusal of
        // self-approval including on a capitalisation variant, lapse-and-
        // sweep, the fingerprint binding an approval to the exact bytes
        // proposed, fail-closed behaviour when the queue cannot be read,
        // and two composition properties: GP 11 byte-parity on the
        // persisted document for every write the gate does not touch, and
        // the shipped chain order in which a Phase 551 policy refusal
        // pre-empts queueing.
        AdminMutationApprovalTests.tests
        // Phase 556 — grant-event notification to affected principals. The
        // pure write-delta classifier (recorded / activated / widened, and
        // never on a revocation or a narrowing), both audiences with the
        // declared party resolved only through the deployment's own
        // PartyRef resolver, the per-audience message shapes, and the
        // delivery discipline: an unpolicied module notifies nobody, a
        // refused grant notifies nobody, and a channel outage leaves the
        // grant durable while logging the failure.
        GrantNotificationTests.tests
        // Phase 552 — the consented-grant registry. The signed consent
        // record and its canonical payload, the lifecycle resolution,
        // verification against a party's registered key material (no `alg`
        // trust from the record, no fall-back on any trust ground), the
        // propose → approve → revoke handshake with revocation effective at
        // the very next call, the counterparty grant write Phase 551 left
        // unreachable, and the trust-vs-lifecycle audit split.
        GrantConsentTests.tests
        // Phase 730 — grant-governance completeness. The GrantRecorded audit
        // twin closing the refusal-only trail Phase 551 shipped; the honest
        // classification of an inner-store failure (a Phase 555 QUEUED write
        // is parked, not unbacked, and a storage outage is neither); and the
        // AI tool gate — a module whose grant is pending or whose consent was
        // revoked is now neither listed to the model nor dispatchable by it,
        // with the list filter and the audited boundary sharing ONE decision.
        GrantGovernanceTests.tests
        // Phase 565 — grounding certificates: sealed, selective provenance
        // disclosure. Issue→verify round-trip (offline against the deployment
        // public key), tamper detection on any byte change, the disclosure
        // predicate withholding a fact's structure (value never present), and
        // the GP-13 no-signing-substrate refusal.
        GroundingCertificateTests.tests
        // Grounding certificate as a DSSE-wrapped in-toto Statement — the
        // standard-interop export. Reference vectors for the encoding, an
        // offline verify against the public key alone, and one distinct
        // refusal per failure mode.
        CertificateEnvelopeTests.tests
        // Grounding-tier signing convergence — one composed key story
        // across deploy records, certificates and ledger heads, rotation
        // continuity, and each transplant refused as what it is.
        GroundingSigningConvergenceTests.tests
        // Certificate-verified fact import — the consuming half of the
        // imported-fact provenance case. Issue on one deployment, import on
        // another, re-certify; the conservative disclosure floor in both
        // directions; one distinct refusal per failure class, each leaving
        // the store empty.
        FactImportTests.tests
        // Certificate issuance transparency — the audit trail as the
        // deployment's own certificate log. One identifier-only row per
        // issuance, three distinct inclusion verdicts, the enumeration
        // surface, and a suppressed issuance probed against a real chained
        // ledger the 658 verifier flags.
        CertificateIssuanceTransparencyTests.tests
        // The one-command deployment verification report — the composed
        // five-section artefact. Probes the composition rather than the
        // five verifiers it composes: absence exits zero without reading
        // as a pass, a seeded failure lands in its own section and no
        // other, and the read-but-unaffirmed states stay distinct from
        // the verified ones.
        DeploymentVerificationReportTests.tests
        // Phase 63 — StaticCorpus MessagePack round-trip + determinism.
        StaticCorpusContract.tests
        // Phase 502 — RetrievalRequest.Filters parity pack (both pipelines).
        MetadataFilterContract.tests
        // Phase 506 — conversation-aware query rewrite (IQueryRewriter stage).
        QueryRewriteContract.tests
        // Phase 506 — the compose-side DI pickup for that stage.
        QueryRewriteComposeTests.tests
        // Phase 505 — citation character-offset spans (chunk → citation).
        CitationSpanContract.tests
        // Phase 501 — the sparse-index analyzer seam + the Snowball / CJK
        // companions (identity default, index/query symmetry, measured lift).
        SparseAnalyzerContract.tests
        KnowledgeUploadPolicyTests.tests
        // Phase 14x — KB upload content-hash dedup.
        KnowledgeDedupTests.tests
        // Phase 512 — per-scope KB corpus quota + age-based retention sweep.
        KbQuotaRetentionTests.tests
        // Phase 510 — KB document versioning + incremental re-index.
        KbVersioningTests.tests
        // Phase 105 — KB original retention on IDataObjectStore: dedup at
        // rest, the convention-path read fallback, and data-subject
        // erasure coverage a raw blob never had.
        KbObjectRetentionTests.tests
        // Phase 502.C — KB document tagging: the chunk re-stamp that makes
        // a tag reachable by the Phase 502.A retrieval filter.
        KbDocumentTagsTests.tests
        // Phase 511 — bulk / programmatic KB import: archive bomb +
        // zip-slip guards, the inert-by-default URL gate, and the
        // per-item scan / dedup / quota claim through the batch surface.
        KbBulkImportTests.tests
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
        // Phase 472 — the CDN / edge-cache seam + the reference
        // sub-companion that proves it from outside the SDK.
        EdgeCacheTests.tests
        // Phase 742 — delivered-egress reconciliation from CDN access
        // logs, plus the reference field-mapped parsers.
        DeliveredEgressTests.tests
        ContentAuthoringTests.tests
        ContentAdminAuthorizationTests.tests
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
        // Phase 549 — opt-in directory existence proof for direct member
        // adds (AddTeamMember / CreateTeamWithOwner), plus the fail-closed
        // preflight for the mode-without-a-directory misconfiguration.
        DirectAddIdentityProofTests.tests
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
        // Phase 547.B/C — expired-invite visibility (the API projection over
        // the TeamInviteExpired trail) + the opt-in inviter notification.
        TeamInvitationTests.expiredInviteVisibilityTests
        TeamInvitationTests.inviteExpiryNotificationTests
        TeamInvitationTests.activeTeamPolicyTests
        // Phase 548 — on-demand pending-invite consumption (CheckMyInvites).
        TeamInvitationTests.checkMyInvitesTests
        EntraExternalIdConfigTests.tests
        // Phase 747 — Google Workspace IUserDirectory companion.
        GoogleDirectoryTests.tests
        WithRequestHeadersPassthroughTests.tests
        ConsentProviderTests.tests
        ConsentProviderTests.subscriptionFiringTests
        // Phase 159 — durable per-subject consent state store.
        ConsentStateStoreTests.tests
        ConsentStateStoreTests.entityBackedTests
        ConsentStateStoreTests.restartPersistenceTests
        // Phase 528 — session registry + revocation.
        SessionRegistryTests.tests
        SessionRegistryTests.derivationTests
        SessionRegistryTests.blobBackedTests
        AdAnalyticsSinkTests.tests
        AdAnalyticsSinkTests.noOpTests
        UserClaimsTests.tests
        ModuleGroupingValidatorTests.tests
        ModuleIdentityTests.tests
        AnonymousModeContractTests.tests
        // Phase 245 — per-team module exposure folded into
        // `computeAccessibleModules` (separate `[<Tests>]` binding that
        // `runTestsWithCLIArgs` would otherwise leave dormant).
        AnonymousModeContractTests.exposureTests
        ModuleSurfaceRequirementTests.tests
        ModuleSurfaceRequirementTests.visibilityTests
        BuiltInModuleSurfaceTests.tests
        // Phase 570 — sidebar visibility matrix over the pure
        // `SidebarVisibility.visible` fold (role × mode × exposure), plus
        // the filter-composition-order pins and the admin group sets.
        // Three separate `[<Tests>]` bindings that `runTestsWithCLIArgs`
        // would otherwise leave dormant.
        SidebarVisibilityContractTests.tests
        SidebarVisibilityContractTests.orderingTests
        SidebarVisibilityContractTests.groupSetTests
        SidebarVisibilityContractTests.navRoleTests
        // Phase 609 — accessible names for every rail row (the two
        // landings and the two area switchers were unnamed icons in the
        // narrow rail); textual pins until Phase 610's shell-a11y
        // fixture set can assert the rendered names.
        SidebarVisibilityContractTests.accessibleNameTests
        // Phase 611 — rail placement as declared data. Code-shape pins
        // only: the fold reads each row's `Placement` (absent ⇒ grouped),
        // `isLandingId` is gone, the render layer suppresses pinning by
        // section rather than by id, and the section order is rail order.
        // The behavioural half runs the real `buildSections` fold Fable-side
        // in `ToolUp.AI.Client.Tests/SidebarPlacementTests.fs` — measured,
        // not assumed: calling it under .NET fires the module initialiser's
        // `importDefault`. One more separate `[<Tests>]` binding that would
        // otherwise lie dormant.
        SidebarVisibilityContractTests.placementTests
        // Phase 612 — rail keyboard navigation (shape): where the handlers
        // are attached and which DOM-level seams the model depends on; the
        // behavioural half runs Fable-side in
        // `ToolUp.AI.Client.Tests/SidebarKeyboardTests.fs`. Carried
        // `[<Tests>]` and was never in this list — DORMANT since Phase 612,
        // and found by the Phase 722 registration guard on its first run.
        SidebarVisibilityContractTests.keyboardNavigationTests
        // Phase 569 — the same decision in its reasoned form: which gate
        // refused a deep-linked route, and the equation pinning the
        // sidebar and the router to one predicate.
        RouteGuardContractTests.routeGuardTests
        RouteGuardContractTests.sharedPredicateTests
        // Phase 571 — the command palette over the SAME fold: the
        // destination list per subject cross-checked against
        // `SidebarVisibility.visibleIds`, the page-expansion rule, the
        // fuzzy scorer's ranking contract, and the overlay state. Four
        // separate `[<Tests>]` bindings that `runTestsWithCLIArgs` would
        // otherwise leave dormant.
        CommandPaletteContractTests.paletteVisibilityTests
        CommandPaletteContractTests.expansionTests
        CommandPaletteContractTests.fuzzyTests
        CommandPaletteContractTests.paletteStateTests
        // Phase 572 — per-user sidebar entry hiding. The pure preference
        // algebra plus the "still reachable" acceptance arm; four
        // separate bindings, all of which must be listed here or they run
        // as dormant `[<Tests>]` values nobody executes.
        SidebarHidingContractTests.hideRestoreTests
        SidebarHidingContractTests.pinHideRuleTests
        SidebarHidingContractTests.legacyBlobTests
        SidebarHidingContractTests.paletteParityTests
        // Phase 637 — server-authoritative module-visibility profiles.
        // The narrowing walk, the ungoverned-id escape, stage 0 of the
        // visibility fold, the server-side scope order, and the route
        // attribution the opt-in hardening middleware resolves against.
        // Five separate `[<Tests>]`-shaped bindings that must be listed
        // here or they run as dormant values nobody executes.
        ModuleVisibilityContractTests.resolutionTests
        ModuleVisibilityContractTests.ungovernedTests
        ModuleVisibilityContractTests.scopeWalkTests
        ModuleVisibilityContractTests.foldTests
        ModuleVisibilityContractTests.routeRegistryTests
        // Phase 573 — the administration landing: tile composition +
        // order, the owning-module visibility filter (and the equation
        // pinning it to `SidebarVisibility.visibleIds`), click-through
        // target, and the two distinct empty states. Four separate
        // bindings; all must be listed here or they run as dormant
        // `[<Tests>]` values nobody executes.
        AdminLandingContractTests.compositionTests
        AdminLandingContractTests.roleFilterTests
        AdminLandingContractTests.clickThroughTests
        AdminLandingContractTests.emptyStateTests
        // NOTE — BuiltInModuleSurfaceTests.visibilityTests is deliberately NOT
        // wired here; since Phase 722 that decision is DECLARED DATA rather
        // than a comment, in `deliberatelyUnregistered` below, and the guard
        // fails if the declaration ever goes stale.
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
        // Phase 185 — the deploy-plane dry-run. Both dispatch routes of
        // the PlanDeploy extension member (native IDeployPlanner and the
        // unchanged-implementer fallback), the pure diff classification,
        // and the mutation check that keeps the read-only assertion
        // falsifiable.
        DeployPlaneTests.deployPlanDefaultPipelineTests
        DeployPlaneTests.deployPlanFallbackTests
        DeployPlaneTests.deployPlanMutationCheck
        DeployPlaneTests.deployPlanDiffTests
        // Phase 656 — build transcript + sealed deploy record. The
        // determinism packs are probed in BOTH directions: equal inputs
        // must digest equally, and every recorded field must reach the
        // digest — a canonical form that silently dropped one would pass
        // the first pack perfectly.
        BuildTranscriptTests.transcriptDeterminismTests
        BuildTranscriptTests.transcriptSensitivityTests
        BuildTranscriptTests.provenanceTests
        BuildTranscriptTests.deployRecordCanonicalFormTests
        BuildTranscriptTests.deployRecordVerificationTests
        // Phase 659 — the dependency-closure upstream-provenance join.
        // The closure's canonical form is probed in both directions like
        // the transcript's; capture reads the restore's own output; the
        // attest seam runs provider-absent (honestly unattested) and
        // against a stub ledger; and the closure's digest is bound into
        // the sealed record — perturb the closure, the seal refuses.
        BuildTranscriptTests.closureCanonicalFormTests
        BuildTranscriptTests.closureAttestationTests
        BuildTranscriptTests.closureCaptureTests
        BuildTranscriptTests.closureBindingTests
        // Phase 712 — the upstream work record as a read-only wire
        // contract: the work/build tier of the shape the fact tier
        // already ships. Bounded both ways (an over-cap request, an
        // over-cap answer, and a source that answers above its own
        // declared cap), withheld distinguishable from absent, a foreign
        // kind crossing intact, every unattested reason recorded rather
        // than dropped, and an uncomposed deployment unchanged.
        WorkProvenanceSourceTests.tests
        // Phase 717 — the SBOM projected from that closure. Each pack is
        // probed in both directions: the schema check is proven able to
        // REFUSE, the completeness check runs against a wholly-unattested
        // closure a filtering emitter would empty, and determinism is
        // paired with a field-by-field perturbation that must move the
        // bytes.
        SbomProjectionTests.sbomSchemaConformanceTests
        SbomProjectionTests.sbomUnattestedPresenceTests
        SbomProjectionTests.sbomDeterminismTests
        SbomProjectionTests.sbomHonestyTests
        SbomProjectionTests.sbomScopeStatementTests
        SbomProjectionTests.sbomEndpointTests
        // Phase 713 — the join across those substrates, with every break
        // reported as data. The load-bearing pack is the first: hop COUNT
        // is invariant to what is composed, probed over a ladder of
        // arrangements and paired with an arm proving the ladder actually
        // varies, so a walk that shortened itself for one middle shape
        // cannot pass. The rest reach every verdict on the hop that
        // produces it, prove absent and broken read differently, refuse
        // outside the declared caps without shortening, and count the
        // audited read.
        EvidenceChainWalkerTests.hopCountInvarianceTests
        EvidenceChainWalkerTests.honestAbsenceTests
        EvidenceChainWalkerTests.verdictReachabilityTests
        EvidenceChainWalkerTests.capRefusalTests
        EvidenceChainWalkerTests.auditedReadTests
        EvidenceChainWalkerTests.digestTests
        EvidenceChainWalkerTests.reportSectionTests
        // Phase 714 — that walk exported as an artefact a counterparty
        // can hold: content-addressed in both directions, wrapped as a
        // standard signed statement, readable without a verifier, and
        // carrying its claim boundary on a clean bundle as well as a
        // broken one. The nested-attestation ruling is pinned here.
        EvidenceBundleExportTests.bundleDeterminismTests
        EvidenceBundleExportTests.statementWrappingTests
        EvidenceBundleExportTests.nestedAttestationRulingTests
        EvidenceBundleExportTests.pureVerifierTests
        EvidenceBundleExportTests.claimBoundaryTests
        EvidenceBundleExportTests.signedRoundTripTests
        EvidenceBundleExportTests.fixtureEmissionTests
        // Phase 715 — the break-injection corpus, which is what makes the
        // two packs above worth anything: one healthy synthetic chain
        // with both canonical forms pinned, then one variant per break
        // class asserting the specific verdict AND the specific position.
        // Every case is demonstrated to fail against code lacking its
        // check — a discriminating twin for the walked chain, a
        // deliberately-weakened copy of the verifier for the bundle and
        // the document — because a check that runs, passes and could not
        // have failed is indistinguishable from one that works.
        EvidenceChainBreakTests.corpusPlacementTests
        EvidenceChainBreakTests.healthyBaselineTests
        EvidenceChainBreakTests.chainBreakCorpusTests
        EvidenceChainBreakTests.chainFalsificationTests
        EvidenceChainBreakTests.bundleTamperCorpusTests
        EvidenceChainBreakTests.bundleFalsificationTests
        EvidenceChainBreakTests.documentTamperCorpusTests
        EvidenceChainBreakTests.absentVsBrokenTests
        // Phase 729 — the corpus's one unproven class, promoted. A
        // severed ANCESTOR edge is recorded as a typed marker rather than
        // dropped, so the hop reads broken at the ref that failed, the
        // enumeration behind it stays incomplete, and both are derived
        // from the same recording rather than from two observations that
        // could drift.
        EvidenceChainBreakTests.severedAncestorEdgeTests
        EvidenceChainBreakTests.severedAncestorReportTests
        // Phase 716 — the walk proves it enumerated everything its own
        // linkage names, not merely everything it liked: the expected
        // positions derived rather than configured, a missing interior
        // position named, a declared bound kept distinct from an
        // omission, and a shorter render unable to satisfy the claim.
        EvidenceEnumerationCompletenessTests.derivationTests
        EvidenceEnumerationCompletenessTests.missingPositionTests
        EvidenceEnumerationCompletenessTests.boundedTests
        EvidenceEnumerationCompletenessTests.shorterRenderTests
        EvidenceEnumerationCompletenessTests.bundleStatementTests
        // Phase 657 — the boot question nobody was asking: is the running
        // composition the one that was sealed? Probed in both directions
        // per axis, plus the verified profile's mandatory capability gate
        // and its audited refusals.
        BootVerificationPreflightTests.tests
        // Phase 680 — the grounded-answer chain join: the serve-tier chain
        // and the grounding chain meet on one audit row, and the walk
        // between them is exercised end to end rather than argued.
        GroundedAnswerChainJoinTests.tests
        // Phase 684 — the post-boot gap Phase 657 names, closed for the
        // grounding tier: seal + recorded mutation chain accounts for the
        // live envelope, an unrecorded drift does not, and the door
        // refuses to extend a chain it can no longer prove.
        GroundingEnvelopeSealTests.tests
        // DefaultDeployPipeline.Rollback regression: a build-sourced
        // deploy's rollback relaunches the artefact ref recovered from
        // the DeployPushing event history (never a synthetic
        // local-build ref), and a failed relaunch surfaces as Error.
        DeployPlaneTests.defaultPipelineRollbackTests
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
        // Phase 314 — cascade-aware typed proxy forwarding: `forward`
        // continues an inbound cascade (root id / route / budget threaded,
        // doomed hops rejected before the wire); `create` still roots.
        PeerFederationTests.proxyForwardingTests
        // Phase 18b — clean-room privacy-gate broker: surface enforcement,
        // gate composition (tighten-only), per-cell suppression, k-floor.
        CleanRoomBrokerTests.tests
        // Phase 311 — the structural half of the same gate: the dispatch
        // wrapper that runs the broker on every answer of a composed
        // contract, so a handler that never calls it cannot leak.
        CleanRoomGateTests.structuralTests
        CleanRoomGateTests.enforcementTests
        CleanRoomGateTests.substitutionTests
        CleanRoomGateTests.compositionTests
        CleanRoomGateTests.wireTests
        // Phase 480 — the bilateral half: a template version is its
        // content hash, an approval is a signature over exactly those
        // bytes, and the composed gate refuses any answer whose
        // template version lacks a live counterparty approval.
        TemplateApprovalTests.mutationTests
        TemplateApprovalTests.signatureTests
        TemplateApprovalTests.lifecycleTests
        TemplateApprovalTests.gateTests
        TemplateApprovalTests.compositionTests
        TemplateApprovalTests.handshakeTests
        TemplateApprovalTests.queueTests
        // Phase 190 — the cumulative half: an epsilon budget accounted
        // per (template, counterparty, epoch), debited atomically before
        // the answer is computed and settled after, so a series of
        // individually-in-floor queries cannot exhaust the protection
        // unobserved.
        PrivacyBudgetLedgerTests.policyTests
        PrivacyBudgetLedgerTests.ledgerContractTests
        PrivacyBudgetLedgerTests.atomicityTests
        PrivacyBudgetLedgerTests.gateTests
        // Phase 481 — the calibrated-noise mechanism: exact discrete
        // Laplace / Gaussian sampling over a CSPRNG, applied to a
        // cleared release, charged to the ledger at the mechanism's own
        // epsilon. Every distributional case is paired with a zero-noise
        // control that must fail it.
        NoiseMechanismTests.samplerTests
        NoiseMechanismTests.policyTests
        NoiseMechanismTests.cohortTests
        NoiseMechanismTests.gateTests
        NoiseMechanismTests.compositionTests
        // Phase 490 — the governed activation seam: the step where a
        // cohort stops being an analytical artefact and starts being
        // used. An authorisation is the content hash of the whole
        // (cohort, purpose, destination) triple, approved through Phase
        // 480's existing bilateral machinery over a DERIVED template, so
        // an edit, a second purpose, and a revocation are each refused
        // by the same mechanism — and invariant 5, the egress
        // projection, is the only thing this phase adds to the chain.
        CohortActivationTests.canonicalTests
        CohortActivationTests.authorisationTests
        CohortActivationTests.purposeTests
        CohortActivationTests.revocationTests
        CohortActivationTests.pipelineTests
        CohortActivationTests.egressTests
        CohortActivationTests.tokenTests
        CohortActivationTests.compositionTests
        // Phase 491 — the governed outbound signal feed: Phase 490's
        // continuous sibling. Continuity changes four things and each
        // has its own list here — a revocation must stop a RUNNING
        // feed (invariant 0 is re-asked per emission, never cached), a
        // feed must carry a bound it cannot outrun, an exhausted
        // budget must pause rather than degrade, and a restart must
        // resume rather than replay.
        OutboundSignalFeedTests.revocationTests
        OutboundSignalFeedTests.boundTests
        OutboundSignalFeedTests.budgetTests
        OutboundSignalFeedTests.restartTests
        OutboundSignalFeedTests.noiseTests
        OutboundSignalFeedTests.validationTests
        OutboundSignalFeedTests.operatorTests
        OutboundSignalFeedTests.stateStoreTests
        // Phase 654 — the signed-shape separator registry. Until this
        // pack, nothing in the repo tested a domain separator at all:
        // one could drift, or two modules could silently choose the
        // same string and defeat domain separation outright, with
        // every other gate green.
        SignedShapeSeparatorTests.registryTests
        SignedShapeSeparatorTests.pinnedDigestTests
        // Phase 590 — PeerSurface descriptor: the deployment's
        // cross-instance face derived from the composed peer
        // registrations, with a deterministic hash-stamped export.
        PeerSurfaceTests.tests
        // Phase 595 — aggregate peer surface + gateway composition: one
        // collective PeerSurface over a set of member surfaces plus an
        // explicit exposure allow-list (posture floored to the weakest
        // exposing member, vocabulary pins on unanimity only), and the
        // gateway that fronts it by delegating to the owning member.
        AggregatePeerSurfaceTests.tests
        // Phase 630 — long-running methods fronted through that same
        // aggregate surface: the gateway mints a content-free group job
        // handle, the host's poll route resolves it by forwarding to the
        // owning member, and the group edge keeps the id echo, caller
        // ownership, non-disclosure and its own terminal audit row.
        AggregateLongRunningFrontingTests.tests
        // Phase 591 — federation-graph preflight: the deployment's
        // consumed peer contracts checked against the pinned labels its
        // counterparties published, before traffic — unsatisfied
        // contract and version skew refuse, a required trust facet a
        // label contradicts refuses, an aged pin reports.
        FederationPreflightTests.tests
        // Phase 596 — the federation seam certifies against its own wire
        // specification: every fixture in the committed corpus
        // round-trips / re-stamps / is refused as specified, the corpus
        // and its manifest agree in both directions, and an emitter
        // shape change that did not regenerate the corpus fails here.
        FederationWireConformanceTests.tests
        // Phase 189 — the GENERATED non-F# peer clients certify against
        // that same corpus: the requests PythonClientGen /
        // TypeScriptClientGen output puts on the wire are round-tripped
        // and member-order-checked against the corpus request vector, the
        // responses it reads are corpus bytes served verbatim, and three
        // legs are dispatched by a live IPlatformPeer.Handle behind a
        // live JwtPeerAuthProvider. A runtime that is absent skips with
        // the probe's reason; a corrupted client must make it go red.
        CrossRuntimeFederationConformanceTests.tests
        // Phase 316 — peer job-result retention: a parked federated
        // result is bounded by a TTL and/or reclaimed after a
        // delete-on-read grace window, and reads as absent once retired.
        PeerJobRetentionTests.tests
        // Phase 338 — peer-token replay defence + call scoping: the
        // `jti` seen-set (bounded, fail-closed, distributed-ready) and
        // the optional `cid` contract binding, each rejection paired
        // with a control asserting the same sequence succeeds once the
        // defence is removed.
        PeerJwtReplayTests.guardTests
        PeerJwtReplayTests.replayDefenceTests
        PeerJwtReplayTests.callScopingTests
        // Phase 629 — compose-level registration for the deferred peer
        // knobs (316 retention, 483 round orchestrator, 338 token
        // policy), each asserted both ways: composing it registers the
        // intended thing, and NOT composing it leaves the pre-629 value
        // exactly in place.
        PeerComposeKnobTests.jobRetentionTests
        PeerComposeKnobTests.roundOrchestratorTests
        PeerComposeKnobTests.tokenPolicyTests
        // Phase 309 — peer audience binding for contract hosts: a
        // wrong-audience token is refused by a bound receiver and
        // ADMITTED by an unbound one (the negative control that makes
        // the exposure falsifiable), plus the compose-time advisory /
        // opt-in strict refusal derived from the hosted-contract set.
        PeerAudienceBindingTests.bindingTests
        PeerAudienceBindingTests.postureTests
        // Phase 330 — delegation assertions verified before dispatch: a
        // forged `Delegated` originator is refused at the host seam and
        // never reaches dispatch, a correctly-signed single- and
        // multi-hop delegation drives the trusted call context, and a
        // malformed `uctx` rejects rather than degrading to Anonymous.
        // The negative control ADMITS the forged assertion once the
        // delegation check is neutered — the pre-330 posture.
        PeerDelegationVerificationTests.hostVerificationTests
        PeerDelegationVerificationTests.userContextClaimTests
        // Phase 343 — peer robustness roundup. A malformed base64url
        // signature is a 401 with a body byte-identical to a merely-wrong
        // one (so the status code stops being an error oracle), asserted
        // at the provider and over a TestServer, plus a host backstop for
        // any provider that throws. A non-2xx capability-PROFILE fetch
        // fails the handshake instead of degrading to the lifecycle-free
        // capability list — the opt-in legacy fallback is kept as the
        // NEGATIVE CONTROL that shows the receiver's `Deprecated`
        // declaration being erased. And the asymmetric ES256 / RS256
        // provider validates with a public key alone, with the paired
        // assertion that the same store CANNOT mint as the caller.
        PeerRobustnessTests.base64UrlSignatureTests
        PeerRobustnessTests.profileNoDowngradeTests
        PeerRobustnessTests.asymmetricProviderTests
        PeerRobustnessTests.defaultCompositionUnchangedTests
        // Phase 315 — peer host wire hardening. The contract route now
        // reads the inbound body under a configurable ceiling instead of
        // buffering whatever arrives: the over-cap cases drive the real
        // routes against a request stream that COUNTS the bytes pulled
        // off it, so "refused before it is fully buffered" is measured
        // rather than argued, and the negative control shows the same
        // payload read end to end under a generous ceiling. The ordering
        // is pinned too — an unauthenticated over-cap request still
        // answers 401, so the size check did not reopen the status-code
        // oracle Phase 343 closed. And the job-poll response echoes the
        // polled jobId as its JSON-RPC `Id` where every answer used to
        // carry "", with the in-tree client asserted unchanged.
        PeerWireHardeningTests.requestBodyCapTests
        PeerWireHardeningTests.pollCorrelationTests
        // Phase 629 — the host validates a contract call through the
        // Phase 338 scoped seam, so a composed `cid` binding is enforced
        // by the shipped host. Pinned by a negative control that ADMITS
        // the mis-scoped token under the pre-629 posture.
        PeerWireHardeningTests.contractBindingTests
        // Phase 331 — receiver-side cascade-budget authority: the host
        // derives `HopsRemaining` / `Route` / `RootRequestId` /
        // `ParentRequestId` from the validated principal + its own policy
        // instead of copying them out of the request body, so the
        // hop-limit and loop guards stop evaluating numbers the caller
        // chose. Every probe is paired — a PRE-331 control that admits
        // the identical forgery through the same `DefaultPlatformPeer`
        // (so "refused" cannot mean "refuses everything"), and record-
        // equality proofs that an honest `create` call and a Phase 314
        // `forward` continuation derive back to exactly what was sent.
        PeerCascadeBudgetAuthorityTests.budgetAuthorityTests
        PeerCascadeBudgetAuthorityTests.cascadeShapeTests
        PeerCascadeBudgetAuthorityTests.cascadeCompatibilityTests
        // Phase 312 — peer transport timeout + cancellation propagation.
        // `HttpPeerClient` now issues every request under a token linked
        // from the workflow's ambient source, the caller's optional
        // explicit one, and the policy's per-call deadline — so a
        // fan-out that stops awaiting a peer aborts its socket instead
        // of leaving it held for the shared client's 100 s default. The
        // three non-answers are pinned APART: a deadline expiry is a
        // timeout-classified `PeerTransport`, a caller-side cancellation
        // completes the computation as cancelled (no value at all, so it
        // can never be counted toward a quorum), and a receiver's own
        // error survives unreclassified. Every cancellation claim is
        // measured by a stub that reports the state of the token it was
        // handed, recorded in a `finally` — F# async cancellation
        // bypasses `with`, so an exception-type probe would be answering
        // a different question.
        PeerTransportTimeoutTests.deadlineTests
        PeerTransportTimeoutTests.cancellationTests
        PeerTransportTimeoutTests.fanoutReachTests
        // Phase 339 — peer transport TLS enforcement. Every outbound
        // peer leg mints a bearer that vouches for the whole deployment
        // and built its URL from `target.BaseUrl` with no scheme check,
        // so an `http://` peer put that token on the path in the clear —
        // one observation is peer impersonation until the signing key
        // rotates. The accept rule is the OIDC side's
        // `isAcceptableKeyFetchUrl`: https anywhere, http to loopback
        // only. Refusal is measured on two counters (requests that
        // reached the wire, tokens that were minted), never inferred
        // from the `Error` case, and every refusal is paired with a
        // pre-339-posture control that ADMITS the same cleartext call —
        // so "refused" cannot quietly mean "refuses everything".
        PeerTransportTlsTests.acceptRuleTests
        PeerTransportTlsTests.transportTests
        PeerTransportTlsTests.handshakeFetchTests
        PeerTransportTlsTests.registryTests
        PeerTransportTlsTests.composeTests
        // Phase 334 — federated-identity sanitisation parity: one hostile
        // corpus driven through the Entra claim mapping, the peer `iss`
        // signing-key lookup and the blob-backed peer directory, with
        // every verdict compared to the canonical IdentitySanitiser one
        // so a future divergence on any single boundary fails. The
        // negative controls pin that the refusal is the sanitiser and not
        // a missing key, a weak key, or a fixture that refuses everything.
        FederatedIdentitySanitisationTests.parityTests
        FederatedIdentitySanitisationTests.negativeControlTests
        FederatedIdentitySanitisationTests.boundaryDetailTests
        // Phase 483 — multi-round protocol orchestrator: a three-round
        // two-party protocol expressed as a step function alone, each
        // DropoutPolicy variant against a missed round deadline,
        // restart-resume from persisted state, and cancellation reaching
        // the participant calls already on the wire.
        RoundOrchestratorTests.tests
        // Phase 18f — commutative cipher (OPRF) + two-party private set
        // intersection: the algebraic law set bound to both shipped
        // backends and to two further prime-order curves, published P-256
        // parameters, recorded wire vectors, malformed / cross-backend
        // rejection, and the end-to-end intersection plus its
        // transcript-opacity assertion.
        CommutativeCipherTests.tests
        // The negative controls for the pack above: two deliberately
        // broken ciphers, each asserted to FAIL the laws that exist to
        // catch it and PASS the ones it genuinely satisfies — so a law
        // that stopped having teeth fails here rather than passing
        // everywhere.
        CommutativeCipherTests.selfTests
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
        // Phase 467 — constant-time token compare unified on
        // JwtCrypto.fixedTimeEqualsUtf8 (byte-normalised behaviour + a
        // source pin per call site, since the gates are private), and the
        // per-IP failure window, whose expiry test and reset stamp now
        // observe one caller-supplied instant rather than two clock reads.
        ConstantTimeCompareTests.tests
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
        // Phase 582 — IModuleContract conformance pack: the reusable
        // module-seam law suite (server/client id parity, wire-TypeName
        // uniqueness, NeedsData satisfiability, action emitter<->decoder
        // key coverage, top-level-namespace convention) bound to the
        // in-repo samples/HelloWorld module and to a synthetic conforming
        // reference, plus a self-test proving each law fails a
        // deliberately non-conforming module.
        ModuleContract.tests
        ModuleContract.referenceTests
        ModuleContract.selfTests
        // Phase 208 — codified threat-lens security-regression suite: the six
        // manual audit lenses as recurring red-team regression cases + a
        // reverted-control proof that the suite catches regressions, not just
        // passes. A reverted security control fails the matching lens here.
        ThreatLensRegressionSuite.tests
        // Phase 623 — reactive fact recomputation activated in composed
        // deployments: the Phase 623.A DI-deferred scheduled-job
        // declaration reaching the scheduler, upstream-aware freshness +
        // OnQuery recompute at the read path, the data-arrival hook
        // driving invalidation end to end, and the facts-free twin that
        // proves a deployment without facts is byte-for-byte unchanged.
        ReactiveRecomputeComposeTests.tests
        // Phase 317 — peer-auth posture advisory: the compose-time ladder
        // separating the static-bearer substrate from the signed-JWT one,
        // the namespace-overlap predicate (both directions, plus the
        // '/peerish/' boundary a naive stem match gets wrong), and the
        // startup advisory's reason text. Every flagged rung is paired
        // with a control differing in one config field.
        PeerAuthPostureTests.ladderTests
        PeerAuthPostureTests.overlapTests
        PeerAuthPostureTests.advisoryTests
        // Phase 186 — IAssetStore upload-validation seam: the
        // IUploadValidator contract bound to the in-tree sniffing
        // validator, the magic-byte + polyglot table, the fail-closed
        // runner (unavailable / raising validators both refuse), the
        // bounded read measured with a counting stream, and the handler
        // ordering that keeps a refused upload out of storage.
        UploadValidationTests.contractTests
        UploadValidationTests.tests
        // Phase 478 — the isolated execution profile: ExecutionProfile as
        // data on the portable ExternalWorkSpec (Standard by default, so
        // the pre-478 path is unchanged), the three-clause isolation
        // posture a backend declares, the refusal that happens BEFORE a
        // submission reaches a non-declaring backend (measured by what the
        // inner dispatcher SAW, each case paired with an unwrapped
        // control), and the gated-output pipeline — the ungated payload
        // unreachable by reflection sweep, and released only through the
        // Phase 311 gate.
        IsolatedExecutionProfileTests.profileTests
        IsolatedExecutionProfileTests.postureTests
        IsolatedExecutionProfileTests.gateTests
        IsolatedExecutionProfileTests.structuralTests
        IsolatedExecutionProfileTests.releaseTests
        // Phase 484 — the compute-backend registry + routing dispatcher.
        // Precedence is asserted on what each backend WAS HANDED (a
        // returned registration is equally consistent with a router that
        // chose correctly and submitted elsewhere), and every step's case
        // is paired with a control differing in exactly that step's input
        // and landing on a DIFFERENT backend — so deleting the profile
        // filter or the resource step turns the case red while the control
        // stays green. Plus the restamp's explicit mutation check, and the
        // GP 13 keystone asserted structurally on the service collection.
        ComputeBackendRegistryTests.registryTests
        ComputeBackendRegistryTests.routingTests
        ComputeBackendRegistryTests.refusalTests
        ComputeBackendRegistryTests.handleRoundTripTests
        ComputeBackendRegistryTests.observabilityTests
        ComputeBackendRegistryTests.gp13Tests
        ComputeBackendRegistryTests.structuralTests
        // Phase 485 — compute-result memoization. The two windows are
        // separate packs because they are separate mechanisms: a cache
        // cannot serve a concurrent duplicate (only Succeeded caches, and a
        // running job has succeeded at nothing), so coalescing is asserted
        // against a deliberately slow backend with the same-fixture
        // distinct-key control. Scope isolation carries the mutation check
        // that makes the envelope's key cross-check load-bearing — a
        // foreign envelope planted at this scope's own path is refused —
        // and the zero-budget claim is measured through a counting
        // pass-through composed exactly where Phase 451's decorator will
        // sit.
        MemoizedComputeDispatcherTests.memoizationTests
        MemoizedComputeDispatcherTests.coalescingTests
        MemoizedComputeDispatcherTests.scopeIsolationTests
        MemoizedComputeDispatcherTests.ttlTests
        MemoizedComputeDispatcherTests.optInTests
        MemoizedComputeDispatcherTests.budgetCompositionTests
        MemoizedComputeDispatcherTests.durabilityTests
        MemoizedComputeDispatcherTests.evictionTests
        // Phase 451 — compute-budget governance. The pure admission policy
        // exhausted without infrastructure, then BOTH enforcement points:
        // the IExternalComputeDispatcher decorator (concurrency cap with
        // its raised-cap control, allowance exhaustion + period reset on an
        // injected clock, per-class differential policy, the duration
        // clamp, transparency under budget) and the fit-job enqueue —
        // including the FEDERATED PEER path, which never touches Submit and
        // which a decorator-only pack would report as covered while an
        // agent walked straight past it. Plus the two audit rows, the Phase
        // 9d metering integration, and the blob store's scope partitioning
        // + its concurrent-admission race.
        ComputeBudgetTests.tests
        // Phase 689 — the platform budget seam Phase 451 was generalised
        // into: one predicate for every ceiling, the three-way verdict,
        // the period-as-storage-key rule, the compute re-expression pinned
        // field for field (including that the shared ledger reads and
        // writes the blob 451 has always written, asserted in BOTH
        // directions), the IBudgetLedger contract against both shipped
        // ledgers, and a worked hourly token budget — the domain no
        // compute test can speak for.
        BudgetSeamTests.tests
        // Phase 7c — data-object orphan-blob sweep. The orphan is produced
        // through the real Save path (metadata write refused, content
        // write already landed), so the sweep is exercised against the
        // residue the crash actually leaves rather than a hand-placed
        // blob. The grace-window pair and the live-content control are
        // each the sole red case for one half of the reclaim predicate.
        DataObjectOrphanSweepTests.tests
        DataObjectOrphanSweepTests.validatorTests
        DataObjectOrphanSweepTests.composeTests
        // Phase 634 — the IN-BAND GC's half of the same problem: a
        // `Delete` racing a `Save` must not reclaim the writer's
        // not-yet-referenced content, while still removing the bytes the
        // delete itself released (Phase 105's gone-at-rest contract).
        DataObjectOrphanSweepTests.inBandGraceTests
        // Phase 9m.B — RAG config validators (extension). The gating
        // cases are the load-bearing ones: a validator that over-fires
        // still looks like it works, and a family an operator learns to
        // scroll past protects nothing.
        RagConfigValidatorExtensionTests.tests
        // Phase 320 — external-compute completion-callback ingress. The
        // load-bearing arm is the MULTI-INSTANCE interleave (two
        // schedulers, separate locks, one shared store), PLACED via a
        // rendezvous rather than raced for: it asserts exactly one
        // completion, and its ungated twin on the identical construction
        // asserts exactly two — so the pair measures the CAS gate rather
        // than the harness's luck at interleaving.
        ExternalCallbackTests.tests
        // Phase 322 — the generic HTTP/REST external-compute companion.
        // Bound against a real in-process compute service on a real
        // socket, and carrying the Phase 324 contract pack unmodified with
        // HonoursIdempotency / ValidatesHandleScope declared FALSE — both
        // substantive, since the pack asserts a real fallback law for each
        // rather than skipping. The push-completion arm has the service
        // call the real Phase 320 ingress back, with a forged-secret
        // control proving the ingress is not simply accepting anything.
        HttpComputeDispatcherTests.tests
        // Phase 486 — signed worker outcomes. Every refusal arm is paired
        // with a positive CONTROL on the same material: the tampered-body
        // test shows the identical genuine envelope verifying over the
        // result it was signed for, so "refused" cannot be a verifier that
        // refuses everything. The policy is asserted in BOTH directions at
        // the HTTP boundary, and the GP 13 pin sends a header so malformed
        // that a 200 proves the gate never ran.
        SignedWorkerOutcomeTests.tests
        // Phase 321 — job progress checkpoints. The coalescing rule is the
        // hot zone and carries two mutation controls: a terminal and a
        // durable checkpoint must each publish at ZERO elapsed time inside
        // an hour-long shedding window, which is the exact input that
        // distinguishes "terminal checked before the interval" from
        // "checked after". The burst case asserts both halves — that 200
        // intermediates coalesced to one frame AND that the terminal frame
        // arrived — because either alone is vacuous.
        JobProgressTests.tests
        // Phase 638 — the federated model-execution profile. The round
        // trip (submit → fit data-side → outcome) is the smallest part;
        // the weight is on what CANNOT be reached: every row-access name
        // the profile enumerates is refused with the row-read class
        // specifically, and a control operation on the same registration
        // is answered, so a broken dispatch cannot pass as a closed
        // surface. The diagnostics arm shows an uncheckable answer being
        // withheld by the gate rather than passed through, and an
        // undeclared projection being refused with its handler probe at
        // zero invocations.
        FederatedModelExecutionTests.tests
        // Phase 450 — the external model-fit binding. The end-to-end arm's
        // stub worker reports `running` forever, so a fit that resolves can
        // only have been resolved by the completion callback — push is
        // proved structurally rather than by winning a race. The schema arm
        // asserts the rendered envelope as literal text, because a
        // round-trip through this repo's own parser would stay green while
        // both halves renamed a field together and every worker broke. The
        // gate arm's load-bearing case is the diagnostic the worker did NOT
        // report: it must fail its gate closed, with no observation
        // invented for it.
        ExternalModelFitTests.tests
        // Phase 602 — certification against the external model-execution
        // conformance corpus. The corpus is canonical over this
        // implementation, not emitted from it, so this family can find a
        // defect no in-repo fixture ever would. Its non-vacuity arm
        // asserts the vector COUNT executed, and its go-red arm proves a
        // mutated document fails — both because a conformance suite is
        // exactly the kind of code that passes by doing nothing. An
        // absent corpus is one loud failure, never a skip.
        Conformance.ModelExecutionSpecConformance.tests
        // Phase 336 — fail-closed dispatch consistency. The two seams in the
        // dispatch-authorization layer that failed OPEN where every other
        // decision point fails closed: the surface gate's missing-Subject
        // fall-through (reachable via a swallowed ScopeResolutionMiddleware
        // resolver exception, not only an unsupported pipeline) and the
        // PlatformAdmin backstop's case-sensitive `/premium` discriminator
        // sitting beside a case-INsensitive prefix guard. Each arm carries
        // its correct-path control — a public route still admitted without a
        // Subject, `premium-status` still open — so "everything is refused"
        // cannot pass as a fix.
        FailClosedDispatchTests.tests
        // Phase 723 — the scope-enumeration seam + the converged restart
        // recovery sweep. Its GP-13 arm asserts against a RECORDING
        // surface rather than a returned count, because a sweep that read
        // every scope and marked none would return zero too; and its
        // enqueue arms are gated on a TaskCompletionSource rather than a
        // sleep, so "the caller got its thread back" is deterministic.
        ScopeEnumerationSweepTests.tests
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

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing.
/// The floor is a LOWER BOUND (140 against ~170 attributed bindings), so
/// adding a pack never needs an edit here; it only moves when bindings
/// are deliberately removed.
let allTests =
    TestRegistrationGuard.withGuardExempting
        (Assembly.GetExecutingAssembly())
        140
        deliberatelyUnregistered
        registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests