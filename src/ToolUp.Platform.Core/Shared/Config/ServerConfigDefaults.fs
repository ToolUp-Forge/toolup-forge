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

/// Phase 877 — the `SecurityPosture` concern's defaults — what `ServerConfig.defaults` composes for it.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SecurityPosture =
    let defaults: SecurityPosture = {
        RequireHttps = false
        TrustForwardedHeaders = true
        TrustedProxyCidrs = []
        MaxRequestBodyBytes = None
        WebhookUrlAllowedHosts = []
        SecurityHeaders = Map.empty
        SecurityHardening = NoSecurityHardening
        Cors = None
        SseAuthMode = QueryParamFallback
        AuthCookieIssuance = FeatureMode.Disabled
    }

/// Phase 877 — the `RiskAcknowledgements` concern's defaults — what `ServerConfig.defaults` composes for it.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RiskAcknowledgements =
    let defaults: RiskAcknowledgements = {
        AcceptForwardedHeadersFromAnyProxy = false
        AcceptHeaderAuthWhenAuthRequired = false
        AcceptPlaintextSecretsWhenAuthRequired = false
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
        AcceptEphemeralShareTokenKey = false
        // Phase 6m — GP 13: the refusal is on by default, the
        // attestation is opt-in. A deployment with no `Anonymous`
        // surface, or no platform-paid provider, never reaches the rule.
        AcceptAnonymousModeWithAI = false
    }

/// Phase 877 — the `MultiInstance` concern's defaults — what `ServerConfig.defaults` composes for it.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module MultiInstance =
    let defaults: MultiInstance = {
        PeerRoutePrefixes = []
        ReplicaCount = 1
        ProcessProfile = AllInOne
        PeerSubstrate = FeatureMode.Disabled
    }

/// Phase 877 — the `Observability` concern's defaults — what `ServerConfig.defaults` composes for it.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Observability =
    let defaults: Observability = {
        SlowRequestThreshold = TimeSpan.FromSeconds 1.0
        SlowRequestThresholdOverrides = Map.empty
        TelemetrySink = FeatureMode.Disabled
        MetricsEndpoint = FeatureMode.Disabled
        MetricsSink = MetricsSinkConfig.defaults
        HealthStateTracking = false
        AlertRules = AlertRule.none
        LogLevel = LogLevel.Info
        TraceCategories = Set.empty
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

/// Phase 877 — the `Stores` concern's defaults — what `ServerConfig.defaults` composes for it.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Stores =
    let defaults: Stores = {
        EventStore = InMemoryOnly
        ResultStore = NoResultStore
        ShareTokenStore = FeatureMode.Disabled
        ServiceAccounts = NoServiceAccounts
        EntityStore = FeatureMode.Disabled
        EntityOutbox = FeatureMode.Disabled
        // Phase 68 — the in-memory graph store is the default (registered
        // lazily; zero cost until a graph API is resolved — GP 13).
        GraphStore = InMemoryGraphStore
        TimeSeriesStore = NoTimeSeriesStore
        Datasets = NoDatasets
        // Phase 528 — no session registry; nothing is recorded, the
        // revocation middleware is not registered and ISessionApi 404s
        // (GP 11 / GP 13).
        SessionRegistry = NoSessionRegistry
        ExternalContactStore = FeatureMode.Disabled
        ConversationStore = NoConversationStore
        AssetStore = FeatureMode.Disabled
        RateLimitStore = NoRateLimitStore
        ConsentStateStore = NoConsentStateStore
        FactStore = FeatureMode.Disabled
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ServerConfig =
    let defaults = {
        Port = 5000
        BindAddress = None
        PublicPath = "deploy/public"
        Surfaces = Surfaces.anonymous
        ModuleNames = []
        Stores = Stores.defaults
        PlatformKnowledgeBase = FeatureMode.Disabled
        AutoBootstrapDevAdmin = None
        ModuleConfigs = []
        IncludePlatformDefaults = true
        FeatureFlags = []
        ModuleFilter = None
        ModuleBindingTrust = ModuleBindingTrustConfig.defaults
        SecurityPosture = SecurityPosture.defaults
        RiskAcknowledgements = RiskAcknowledgements.defaults
        StaticPathBehaviour = Warn
        Observability = Observability.defaults
        DefaultTeamStorageQuotaBytes = None
        RateLimit = RateLimitConfig.none
        Lineage = FeatureMode.Disabled
        LineageDurability = FeatureMode.Disabled
        JobScheduler = NoJobScheduler
        // Phase 321 — no progress fan-out; `ctx.Progress` is the no-op
        // reporter and nothing is published or persisted (GP 13).
        JobProgress = FeatureMode.Disabled
        BackfillMissedTicks = false
        EventTriggerCatchUp = false
        MultiInstance = MultiInstance.defaults
        MigrateWebhookSecretsAtRest = false
        PublicBaseUrl = None
        DataIngestion = FeatureMode.Disabled
        ColumnMapping = FeatureMode.Disabled
        MappingDryRun = WarnOnValidationFailure
        OAuthRefresher = FeatureMode.Disabled
        OAuth1a = FeatureMode.Disabled
        SeedData = NoSeedData
        // Phase 68d — entity→graph projection is opt-in; the default wires
        // no bridge and leaves the entity store byte-identical (GP 13).
        EntityGraphProjection = FeatureMode.Disabled
        // Phase 10a — no migration registry, no status store, no
        // startup sweep, no API route (GP 11 / GP 13).
        DataMigrations = NoDataMigrations
        // Phase 10b — no declared config migrators. Every schema reads
        // at the implicit version 1, no `_schema_version` stamp is
        // written, and every persisted document decodes exactly as it
        // did before this substrate existed (GP 11 / GP 13).
        ConfigMigrations = []
        ModelFitting = FeatureMode.Disabled
        ModelExecution = FeatureMode.Disabled
        // Phase 318 — no external-compute backend composed; the seam
        // resolves to NoExternalComputeDispatcher and every Submit is a
        // typed not-configured refusal (GP 13).
        ExternalCompute = FeatureMode.Disabled
        // Phase 451 — no compute budgets; nothing is registered and no
        // submission path consults a budget (GP 13).
        ComputeBudget = FeatureMode.Disabled
        UsageMetering = FeatureMode.Disabled
        Webhooks = FeatureMode.Disabled
        AuditLog = FeatureMode.Disabled
        AuditSamplingPolicy = AuditSamplingPolicy.none
        AuditFailurePolicy = LogAndContinue
        AuditFallbackDirectory = None
        Notifications = NotificationsAuto
        EnableDevEndpoints = false
        EnableCitationDevEndpoint = None
        SkipPreflight = false
        NotificationPreferences = NoNotificationPreferences
        NotificationCategories = []
        NotifyInviterOnInviteExpiry = false
        EphemeralStoreEvictionMinutes = 60.0
        NotifyOnSessionStoreReset = true
        MaxSseConnectionsPerScope = Some 10
        DataSubjectRequests = DataSubjectRequestMode.Disabled
        ConfigDriftDetection = FeatureMode.Disabled
        RateLimiter = FeatureMode.Disabled
        SlowRateLimitThreshold = TimeSpan.FromSeconds 5.0
        SmokeTest = FeatureMode.Disabled
        PublicRendering = NoPublicRendering
        MediaLibrary = FeatureMode.Disabled
        DeployPlane = NoDeployPlane
        ServerlessHost = KestrelHost
        RateLimits = []
        ResourceEnvelopes = ResourceEnvelope.emptySignature
        ConsentAudit = FeatureMode.Disabled
        AdAnalytics = FeatureMode.Disabled
        TeamCreationPolicy = PlatformAdminOnly
        TeamCreationQuota = None
        // Phase 549 — membership rows stay admin-asserted by default; a
        // deployment opts into the directory existence-proof (GP 11).
        DirectAddIdentityProof = NoIdentityProof
        NarrativeRetention = NarrativeRetentionPolicy.defaults
        UserSchemaAuthoring = FeatureMode.Disabled
        TenantLifecycle = FeatureMode.Disabled
        TenantOffboardConfirmation = NoConfirmation
        DeploymentReadiness = FeatureMode.Disabled
        DeploymentVerification = FeatureMode.Disabled
        RegisteredLocales = [ LocaleCode.en ]
        I18nCoverageMode = NoCoverageCheck
        Presence = FeatureMode.Disabled
        CrdtDocuments = NoCrdtDocuments
        ModuleVisibility = NoModuleVisibility
        AdminMutationPolicy = AdminMutationPolicy.SingleAdmin
        GrantConsent = NoGrantConsentStore
        Backup = NoBackup
        PinnedVocabularyPacks = []
        DeclaredDataSchemas = []
        ExpectedModules = None
    }

/// Phase 877 — the token each `FeatureMode` field of `ServerConfig` spelled
/// before the thirty-seven two-case modes became one type.
///
/// A composition manifest's config knobs are SEALED bytes: a binding taken
/// before 877 holds `EntityStore = NoEntityStore`, and the boot seal
/// re-canonicalises the running composition to compare. The knob's value
/// is a wire token, not an F# case, so the token stays what it was and the
/// rename never reads as drift; the same table is what the knob schema
/// admits and what the dev diagnostics print, so one deployment renders one
/// spelling everywhere. Keyed by the FLAT field name (the name a knob
/// carries), which is also the name the concern records keep.
[<RequireQualifiedAccess>]
module FeatureModeKnob =
    /// Field name → (the off token, the on token).
    let legacyNames: Map<string, string * string> =
        Map.ofList [
            "PlatformKnowledgeBase", ("NoPlatformKnowledgeBase", "EnabledPlatformKnowledgeBase")
            "Lineage", ("NoLineageStore", "EnabledLineageStore")
            "LineageDurability", ("NoDurableLineage", "EnabledDurableLineage")
            "JobProgress", ("NoJobProgress", "EnabledJobProgress")
            "ShareTokenStore", ("NoShareTokenStore", "EnabledShareTokenStore")
            "DataIngestion", ("NoDataIngestion", "EnabledDataIngestion")
            "ColumnMapping", ("NoColumnMapping", "EnabledColumnMapping")
            "OAuthRefresher", ("NoOAuthRefresher", "EnabledOAuthRefresher")
            "OAuth1a", ("NoOAuth1a", "EnabledOAuth1a")
            "EntityStore", ("NoEntityStore", "EnabledEntityStore")
            "EntityOutbox", ("NoEntityOutbox", "EnabledEntityOutbox")
            "EntityGraphProjection", ("NoEntityGraphProjection", "EnabledEntityGraphProjection")
            "ModelFitting", ("NoModelFitting", "EnabledModelFitting")
            "ModelExecution", ("NoModelExecutionApi", "EnabledModelExecutionApi")
            "ExternalCompute", ("NoExternalCompute", "CustomExternalCompute")
            "ComputeBudget", ("NoComputeBudget", "EnabledComputeBudget")
            "TelemetrySink", ("NoTelemetrySink", "CustomTelemetrySink")
            "UsageMetering", ("NoUsageMetering", "EnabledUsageMetering")
            "MetricsEndpoint", ("NoMetricsEndpoint", "EnabledMetricsEndpoint")
            "Webhooks", ("NoWebhooks", "EnabledWebhooks")
            "AuditLog", ("NoAuditLog", "EnabledAuditLog")
            "ExternalContactStore", ("NoExternalContactStore", "EnabledExternalContactStore")
            "AuthCookieIssuance", ("NoAuthCookieIssuance", "EnabledAuthCookieIssuance")
            "ConfigDriftDetection", ("NoConfigDriftDetection", "EnabledConfigDriftDetection")
            "RateLimiter", ("NoRateLimiter", "EnabledRateLimiter")
            "SmokeTest", ("NoSmokeTest", "EnabledSmokeTest")
            "AssetStore", ("NoAssetStore", "EnabledAssetStore")
            "MediaLibrary", ("NoMediaLibrary", "EnabledMediaLibrary")
            "ConsentAudit", ("NoConsentAudit", "EnabledConsentAudit")
            "AdAnalytics", ("NoAdAnalytics", "EnabledAdAnalytics")
            "PeerSubstrate", ("NoPeerSubstrate", "EnabledPeerSubstrate")
            "UserSchemaAuthoring", ("NoUserSchemaAuthoring", "EnabledUserSchemaAuthoring")
            "FactStore", ("NoFactStore", "EnabledFactStore")
            "TenantLifecycle", ("NoTenantLifecycle", "EnabledTenantLifecycle")
            "DeploymentReadiness", ("NoReadinessReport", "EnabledReadinessReport")
            "DeploymentVerification", ("NoDeploymentVerification", "EnabledDeploymentVerification")
            "Presence", ("NoPresence", "EnabledPresence")
        ]

    /// The knob value for `field` holding `mode`: its legacy token, or the
    /// case name for a field the table does not know.
    let render (field: string) (mode: FeatureMode) : string =
        match legacyNames.TryFind field, mode with
        | Some(_, on), FeatureMode.Enabled -> on
        | Some(off, _), FeatureMode.Disabled -> off
        | None, FeatureMode.Enabled -> "Enabled"
        | None, FeatureMode.Disabled -> "Disabled"

    /// The values a knob over `field` admits, off first.
    let values (field: string) : string list =
        match legacyNames.TryFind field with
        | Some(off, on) -> [ off; on ]
        | None -> [ "Disabled"; "Enabled" ]