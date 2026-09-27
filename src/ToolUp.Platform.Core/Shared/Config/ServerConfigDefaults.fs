// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.Narrative

// Phase 347 — carved out of SDK.Shared.fs: the `ServerConfig` companion module.
// The record itself stays in SDK.Shared.fs; the explicit `ModuleSuffix` keeps
// the compiled name `ServerConfigModule` now that the type and its module sit
// in different files.
//
// Phase 880 — this file holds the half both tiers compile: `defaults`. The
// env-var binder (`ServerConfig.fromEnv` and its private parsers) moved to
// ToolUp.Platform.Server, which declares its own `ServerConfig` module in
// this same namespace, so `ServerConfig.defaults` and `ServerConfig.fromEnv`
// both still resolve for any site that references the server tier.

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ServerConfig =
    let defaults = {
        Port = 5000
        PublicPath = "deploy/public"
        Surfaces = Surfaces.anonymous
        ModuleNames = []
        EventStore = InMemoryOnly
        PlatformKnowledgeBase = NoPlatformKnowledgeBase
        AutoBootstrapDevAdmin = None
        ModuleConfigs = []
        IncludePlatformDefaults = true
        FeatureFlags = []
        ModuleFilter = None
        ModuleBindingTrust = ModuleBindingTrustConfig.defaults
        RequireHttps = false
        TrustForwardedHeaders = true
        TrustedProxyCidrs = []
        AcceptForwardedHeadersFromAnyProxy = false
        StaticPathBehaviour = Warn
        SlowRequestThreshold = TimeSpan.FromSeconds 1.0
        SlowRequestThresholdOverrides = Map.empty
        DefaultTeamStorageQuotaBytes = None
        RateLimit = RateLimitConfig.none
        ResultStore = NoResultStore
        Lineage = NoLineageStore
        JobScheduler = NoJobScheduler
        // Phase 321 — no progress fan-out; `ctx.Progress` is the no-op
        // reporter and nothing is published or persisted (GP 13).
        JobProgress = NoJobProgress
        BackfillMissedTicks = false
        EventTriggerCatchUp = false
        ShareTokenStore = NoShareTokenStore
        ServiceAccounts = NoServiceAccounts
        PeerRoutePrefixes = []
        MaxRequestBodyBytes = None
        WebhookUrlAllowedHosts = []
        MigrateWebhookSecretsAtRest = false
        PublicBaseUrl = None
        DataIngestion = NoDataIngestion
        ColumnMapping = NoColumnMapping
        MappingDryRun = WarnOnValidationFailure
        OAuthRefresher = NoOAuthRefresher
        OAuth1a = NoOAuth1a
        EntityStore = NoEntityStore
        EntityOutbox = NoEntityOutbox
        SeedData = NoSeedData
        // Phase 68 — the in-memory graph store is the default (registered
        // lazily; zero cost until a graph API is resolved — GP 13).
        GraphStore = InMemoryGraphStore
        // Phase 68d — entity→graph projection is opt-in; the default wires
        // no bridge and leaves the entity store byte-identical (GP 13).
        EntityGraphProjection = NoEntityGraphProjection
        TimeSeriesStore = NoTimeSeriesStore
        // Phase 10a — no migration registry, no status store, no
        // startup sweep, no API route (GP 11 / GP 13).
        DataMigrations = NoDataMigrations
        // Phase 10b — no declared config migrators. Every schema reads
        // at the implicit version 1, no `_schema_version` stamp is
        // written, and every persisted document decodes exactly as it
        // did before this substrate existed (GP 11 / GP 13).
        ConfigMigrations = []
        Datasets = NoDatasets
        // Phase 528 — no session registry; nothing is recorded, the
        // revocation middleware is not registered and ISessionApi 404s
        // (GP 11 / GP 13).
        SessionRegistry = NoSessionRegistry
        ModelFitting = NoModelFitting
        ModelExecution = NoModelExecutionApi
        // Phase 318 — no external-compute backend composed; the seam
        // resolves to NoExternalComputeDispatcher and every Submit is a
        // typed not-configured refusal (GP 13).
        ExternalCompute = NoExternalCompute
        // Phase 451 — no compute budgets; nothing is registered and no
        // submission path consults a budget (GP 13).
        ComputeBudget = NoComputeBudget
        TelemetrySink = NoTelemetrySink
        UsageMetering = NoUsageMetering
        MetricsEndpoint = NoMetricsEndpoint
        MetricsSink = MetricsSinkConfig.defaults
        Webhooks = NoWebhooks
        AuditLog = NoAuditLog
        AuditSamplingPolicy = AuditSamplingPolicy.none
        AuditFailurePolicy = LogAndContinue
        AuditFallbackDirectory = None
        Notifications = NotificationsAuto
        SecurityHeaders = Map.empty
        SecurityHardening = NoSecurityHardening
        Cors = None
        EnableDevEndpoints = false
        EnableCitationDevEndpoint = None
        SkipPreflight = false
        HealthStateTracking = false
        AlertRules = AlertRule.none
        NotificationPreferences = NoNotificationPreferences
        ExternalContactStore = NoExternalContactStore
        NotificationCategories = []
        LogLevel = LogLevel.Info
        TraceCategories = Set.empty
        SseAuthMode = QueryParamFallback
        AuthCookieIssuance = NoAuthCookieIssuance
        AcceptHeaderAuthWhenAuthRequired = false
        AcceptPlaintextSecretsWhenAuthRequired = false
        ReplicaCount = 1
        AcceptInProcessSchedulerInMultiInstance = false
        AcceptInProcessIngestionInMultiInstance = false
        AcceptSharedEmbeddingCacheInTeamMode = false
        AcceptEphemeralRagIndex = false
        AcceptLocalEmbedderAtScale = false
        AcceptStickyRoutedAiInMultiInstance = false
        AcceptNoRateLimitWhenAuthRequired = false
        AcceptUnsignedPublishable = false
        AcceptQueryParamSseAuthWhenAuthRequired = false
        AcceptSameSiteOnlyCsrfWhenAuthRequired = false
        AcceptUnboundAudienceWhenAuthRequired = false
        AcceptInMemoryOAuthStateInMultiInstance = false
        AcceptInMemoryShareTokenRateLimiterInMultiInstance = false
        AcceptPendingInviteStoreInMultiInstance = false
        AcceptInviteByEmailWithoutDirectory = false
        NotifyInviterOnInviteExpiry = false
        AcceptEphemeralShareTokenKey = false
        EphemeralStoreEvictionMinutes = 60.0
        NotifyOnSessionStoreReset = true
        MaxSseConnectionsPerScope = Some 10
        DataSubjectRequests = DataSubjectRequestMode.Disabled
        ConfigDriftDetection = NoConfigDriftDetection
        RateLimiter = NoRateLimiter
        SlowRateLimitThreshold = TimeSpan.FromSeconds 5.0
        SmokeTest = NoSmokeTest
        ConversationStore = NoConversationStore
        PublicRendering = NoPublicRendering
        AssetStore = NoAssetStore
        MediaLibrary = NoMediaLibrary
        DeployPlane = NoDeployPlane
        ServerlessHost = KestrelHost
        ProcessProfile = AllInOne
        RateLimitStore = NoRateLimitStore
        RateLimits = []
        ResourceEnvelopes = ResourceEnvelope.emptySignature
        ConsentAudit = NoConsentAudit
        ConsentStateStore = NoConsentStateStore
        AdAnalytics = NoAdAnalytics
        TeamCreationPolicy = PlatformAdminOnly
        TeamCreationQuota = None
        // Phase 549 — membership rows stay admin-asserted by default; a
        // deployment opts into the directory existence-proof (GP 11).
        DirectAddIdentityProof = NoIdentityProof
        NarrativeRetention = NarrativeRetentionPolicy.defaults
        PeerSubstrate = NoPeerSubstrate
        UserSchemaAuthoring = NoUserSchemaAuthoring
        FactStore = NoFactStore
        TenantLifecycle = NoTenantLifecycle
        TenantOffboardConfirmation = NoConfirmation
        DeploymentReadiness = NoReadinessReport
        DeploymentVerification = NoDeploymentVerification
        RegisteredLocales = [ LocaleCode.en ]
        I18nCoverageMode = NoCoverageCheck
        Presence = NoPresence
        CrdtDocuments = NoCrdtDocuments
        ModuleVisibility = NoModuleVisibility
        AdminMutationPolicy = AdminMutationPolicy.SingleAdmin
        GrantConsent = NoGrantConsentStore
        Backup = NoBackup
        PinnedVocabularyPacks = []
        DeclaredDataSchemas = []
        ExpectedModules = None
        // Phase 6m — GP 13: the refusal is on by default, the
        // attestation is opt-in. A deployment with no `Anonymous`
        // surface, or no platform-paid provider, never reaches the rule.
        AcceptAnonymousModeWithAI = false
        // Phase 828 — no self-hosted log store: nothing registered, the
        // logger undecorated, no database file, no sweep (GP 11 / GP 13).
        LogStore = NoLogStore
        // Phase 829 — no metrics history: the live registry is never
        // sampled, no flusher runs, no point is appended (GP 11 / GP 13).
        MetricsHistory = NoMetricsHistory
        // Phase 9w — no Datadog readback: no route mounted, no
        // IDatadogReadbackApi resolved, no credential read (GP 11 / GP 13).
        DatadogReadback = NoDatadogReadback
    }