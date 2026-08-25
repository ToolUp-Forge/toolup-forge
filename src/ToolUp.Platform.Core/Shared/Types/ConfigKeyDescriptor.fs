// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Reserved identifiers for the config subsystem, and the central
/// registry of environment-variable-backed config keys.
module ToolUp.Platform.ConfigKeys

/// Reserved module key for cross-module platform configuration
/// (locale, timezone, date format). Indexed the same way as any
/// other module's config — lives under
/// `_platform/config/{container}/_platform.json` when persisted.
///
/// Exposed as a literal so deployments, the admin UI, and the
/// shell can refer to the same key without reintroducing the
/// string at every call site.
[<Literal>]
let PlatformModuleKey = "_platform"

/// Field keys for the SDK-shipped per-team branding fields
/// (Phase 5e). These live on the reserved `_platform` schema — the
/// existing Platform Defaults admin tab — and are merged in by
/// `compose` only when the deployment is team-scoped. The client
/// shell resolves them via `Branding.resolve` against the prefetched
/// `_platform` config, each field falling back to the composition
/// root's `ClientConfig` default when the active team set no
/// override. Centralised so the server schema and the client
/// resolver name the same keys.
module BrandingKeys =
    [<Literal>]
    let AppName = "appName"

    [<Literal>]
    let PrimaryColor = "primaryColor"

    [<Literal>]
    let LogoUrl = "logoUrl"

    [<Literal>]
    let FaviconUrl = "faviconUrl"

    // Phase 223 — full allow-listed palette (colours only; never fonts or
    // component shape). Each drives the matching client-toolkit/shell theming
    // token at :root, so a team's override re-skins the whole client surface.
    [<Literal>]
    let BrandDarkColor = "brandDarkColor"

    [<Literal>]
    let SidebarColor = "sidebarColor"

    [<Literal>]
    let PosColor = "posColor"

    [<Literal>]
    let NegColor = "negColor"

/// Reserved module key for the per-team notification-prefs surface.
/// Auto-injected by `compose` so deployments enabling
/// transactional sinks (email / SMS / push) get a dedicated admin
/// tab without each app re-declaring the schema. A `false` default
/// on every `*.enabled` flag is the team-wide kill switch — admins
/// must explicitly opt in to outbound transactional delivery.
/// `IConfigStore` reads / writes the document at
/// `{container}/_platform/notification_prefs.json`.
[<Literal>]
let NotificationPrefsModuleKey = "_platform.notification_prefs"

/// Field keys inside the `_platform.notification_prefs` schema.
/// Centralised so sinks read the same key the schema declares —
/// the dispatcher resolves these via `IConfigStore.GetEffective`
/// before invoking any sink.
module NotificationPrefsKeys =
    [<Literal>]
    let EmailEnabled = "email.enabled"

    [<Literal>]
    let EmailFromAddress = "email.fromAddress"

    [<Literal>]
    let SmsEnabled = "sms.enabled"

    [<Literal>]
    let PushEnabled = "push.enabled"

    /// Phase 30d — per-scope upper bound on
    /// `IDataCatalog.GetSyntheticSample`'s row `count`.
    /// `ModulePermission.SchemaOnly` partner-sandbox callers cannot
    /// exceed this value (the catalog gate clamps before generation).
    /// Unset = SDK default
    /// (`SyntheticSampleGenerator.DefaultMaxSampleRows`). The key
    /// lives on `_platform.notification_prefs` because that schema is
    /// already the per-scope partner-policy lane; folding it in
    /// avoids minting a new platform module for one field. Despite
    /// the lane name, this is unrelated to notification delivery.
    [<Literal>]
    let SchemaOnlyMaxSampleRows = "schemaOnly.maxSampleRows"


// ─── Phase 214 — central config-key registry ─────────────────────────
//
// Phase 71 / 71.A lifted a large surface of composition-root behaviour
// onto environment variables (the `*FromEnv` readers: blob storage,
// secret store, auth provider, logging, health checks, …). This module
// is the single source of truth for *what those env vars are*: each key
// declares its name, value type, default, one-line description, and
// whether the resolved value is a secret. From this one list:
//
//   * `ReferenceDoc.render` projects `docs/reference/config-reference.md`
//     (regenerated, never hand-maintained — the coverage test fails if
//     the committed file drifts);
//   * `--print-config` (see `StartupModes`) reads each key's effective
//     value and prints it, redacting the `IsSecret` ones;
//   * the coverage test asserts every key the `*FromEnv` readers consult
//     carries a descriptor here.
//
// The module is deliberately dependency-free (System / FSharp.Core only)
// so it can sit early in the compile order and the `*FromEnv` readers can
// reference `Names.*` instead of duplicating string literals — a renamed
// env var then fails to compile rather than silently drifting from the
// reference doc.

/// The value-kind a config key resolves to. Drives the "Type" column in
/// the generated reference and how `--print-config` labels the value.
type ConfigKeyType =
    | StringKey
    | BoolKey
    | IntKey
    /// A closed set of accepted string values (case-insensitive at the
    /// reader). The list is the recognised set; an unset var falls back
    /// to the descriptor's `Default`.
    | EnumKey of choices: string list

/// One environment-variable-backed config key the SDK reads.
/// Declared once, centrally — the reference doc, `--print-config`, and
/// the coverage test all read from this single source.
type ConfigKeyDescriptor = {
    /// The environment variable name, e.g. `"TOOLUP_AUTH_MODE"`.
    EnvVar: string
    /// What the value means / how it is used (one line).
    Description: string
    Type: ConfigKeyType
    /// The effective value when the var is unset, rendered verbatim into
    /// the doc's "Default" column. `None` when there is no default — the
    /// feature is simply off / unset until the operator sets the var.
    Default: string option
    /// `true` when the resolved value is sensitive (master keys, admin
    /// tokens, connection strings carrying credentials). `--print-config`
    /// redacts these; the reference doc flags them so an operator knows
    /// not to commit the value.
    IsSecret: bool
    /// Grouping bucket for the reference doc's section headings. Stable,
    /// human-facing strings (e.g. `"Auth"`, `"Storage & secrets"`).
    Category: string
}

/// The category naming the keys that are read by the build, test and
/// analyzer tooling rather than by a running server.
///
/// It is a `Category` rather than a second flag on `ConfigKeyDescriptor`
/// because the grouping already existed and already carried exactly this
/// meaning — adding a parallel boolean would have created two statements
/// of one fact, and the two would drift the first time someone added a
/// descriptor to the section without setting the flag. Derived membership
/// (`toolingKeys` below) cannot drift from the section a reader sees.
[<Literal>]
let ToolingCategory = "Build & tooling"

/// The category naming the keys that each ACKNOWLEDGE a specific preflight
/// refusal — the escape hatches an operator opens to say "I meant that".
///
/// A `Category` for the same reason `ToolingCategory` is: the grouping
/// already existed and already carried exactly this meaning, so a parallel
/// flag would be a second statement of one fact and the two would drift.
/// Derived membership (`escapeHatchKeys` below) cannot drift from the
/// section a reader sees in the generated reference.
///
/// **Not a name-prefix rule, and the difference is load-bearing.** Most
/// members are spelled `TOOLUP_ACCEPT_*`, but not all are, so a projection
/// that filtered on the prefix would silently omit the ones that are not —
/// and an inventory of accepted risk that is silently short is worse than
/// no inventory. The registry's own classification is the authority.
[<Literal>]
let EscapeHatchCategory = "Security preflight escape hatches"

/// The one non-registry property a deployment configuration manifest may
/// carry: the pointer an editor follows to validate the file as it is
/// typed. The loader skips it rather than binding it.
///
/// Declared here, beside the registry, rather than beside the loader that
/// tolerates it — the generated schema names the same property, and the
/// two statements would be free to drift the moment either moved.
[<Literal>]
let ManifestSchemaProperty = "$schema"

/// Phase 700 — the second non-registry property a deployment
/// configuration manifest may carry: the name of the configuration
/// profile it imports.
///
/// Selection is spelled `"$profile"` inside a manifest rather than
/// `"TOOLUP_PROFILE"` because two spellings of one fact are two things
/// to keep level. The loader refuses the env-var spelling by name and
/// says which to use — the same treatment `TOOLUP_CONFIG_FILE` gets, and
/// for the same reason: a manifest cannot coherently state the thing
/// that was resolved in order to read it.
[<Literal>]
let ManifestProfileProperty = "$profile"

/// Canonical env-var name constants. The `*FromEnv` readers reference
/// these instead of inlining the string literal, so a rename is a
/// compile error that the reference doc can never silently lag behind.
[<RequireQualifiedAccess>]
module Names =
    // Storage & secrets
    [<Literal>]
    let blobStorage = "TOOLUP_BLOB_STORAGE"

    [<Literal>]
    let secretStore = "TOOLUP_SECRET_STORE"

    [<Literal>]
    let secretsMasterKey = "TOOLUP_SECRETS_MASTER_KEY"

    [<Literal>]
    let secretsPath = "TOOLUP_SECRETS_PATH"

    [<Literal>]
    let azureStorageConnectionString = "TOOLUP_AZURE_STORAGE_CONNECTION_STRING"

    [<Literal>]
    let awsS3Bucket = "TOOLUP_AWS_S3_BUCKET"

    [<Literal>]
    let gcsBucket = "TOOLUP_GCS_BUCKET"

    // Auth & identity
    [<Literal>]
    let authMode = "TOOLUP_AUTH_MODE"

    [<Literal>]
    let oidcIssuer = "TOOLUP_OIDC_ISSUER"

    [<Literal>]
    let oidcAudience = "TOOLUP_OIDC_AUDIENCE"

    [<Literal>]
    let sseAuth = "TOOLUP_SSE_AUTH"

    [<Literal>]
    let initialPlatformAdmin = "TOOLUP_INITIAL_PLATFORM_ADMIN"

    [<Literal>]
    let adminToken = "TOOLUP_ADMIN_TOKEN"

    [<Literal>]
    let allowDevAdminBootstrap = "TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP"

    [<Literal>]
    let initialTeamName = "TOOLUP_INITIAL_TEAM_NAME"

    [<Literal>]
    let initialTeamId = "TOOLUP_INITIAL_TEAM_ID"

    [<Literal>]
    let oauthRedirectBase = "TOOLUP_OAUTH_REDIRECT_BASE"

    // Logging & observability
    [<Literal>]
    let logLevel = "TOOLUP_LOG_LEVEL"

    [<Literal>]
    let logFormat = "TOOLUP_LOG_FORMAT"

    [<Literal>]
    let traceCategories = "TOOLUP_TRACE_CATEGORIES"

    [<Literal>]
    let appName = "TOOLUP_APP_NAME"

    // Deployment shape
    [<Literal>]
    let configFile = "TOOLUP_CONFIG_FILE"

    [<Literal>]
    let strictConfig = "TOOLUP_STRICT_CONFIG"

    [<Literal>]
    let profile = "TOOLUP_PROFILE"

    [<Literal>]
    let replicaCount = "TOOLUP_REPLICA_COUNT"

    [<Literal>]
    let notificationChannel = "TOOLUP_NOTIFICATION_CHANNEL"

    [<Literal>]
    let distributedLock = "TOOLUP_DISTRIBUTED_LOCK"

    [<Literal>]
    let redisConnection = "TOOLUP_REDIS_CONNECTION"

    [<Literal>]
    let requireHttps = "TOOLUP_REQUIRE_HTTPS"

    [<Literal>]
    let trustForwardedHeaders = "TOOLUP_TRUST_FORWARDED_HEADERS"

    [<Literal>]
    let maxRequestBodyBytes = "TOOLUP_MAX_REQUEST_BODY_BYTES"

    [<Literal>]
    let maxFileBytes = "TOOLUP_MAX_FILE_BYTES"

    [<Literal>]
    let smokeToken = "TOOLUP_SMOKE_TOKEN"

    [<Literal>]
    let auditAdminRequired = "TOOLUP_AUDIT_ADMIN_REQUIRED"

    // Security preflight escape hatches — each lowers a specific
    // `IConfigValidator` refusal to a warning. Documented so an operator
    // can see the full list of "I know what I'm doing" overrides.
    [<Literal>]
    let acceptLocalFallback = "TOOLUP_ACCEPT_LOCAL_FALLBACK"

    [<Literal>]
    let acceptHeaderAuthInAuthMode = "TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE"

    [<Literal>]
    let acceptUnboundAudienceInAuthMode = "TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE"

    [<Literal>]
    let acceptSameSiteOnlyCsrfInAuthMode =
        "TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE"

    [<Literal>]
    let acceptNoRateLimitInAuthMode = "TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE"

    [<Literal>]
    let acceptQueryParamSseAuthInAuthMode =
        "TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE"

    [<Literal>]
    let acceptInviteByEmailWithoutDirectory =
        "TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY"

    [<Literal>]
    let acceptPendingInviteStoreMultiInstance =
        "TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE"

    [<Literal>]
    let acceptInMemoryOAuthStateMultiInstance =
        "TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE"

    // --- Phase 689: keys previously read with no descriptor ---
    [<Literal>]
    let shareTokenStore = "TOOLUP_SHARE_TOKEN_STORE"

    [<Literal>]
    let webhooks = "TOOLUP_WEBHOOKS"

    [<Literal>]
    let auditLog = "TOOLUP_AUDIT_LOG"

    [<Literal>]
    let lineage = "TOOLUP_LINEAGE"

    [<Literal>]
    let dataIngestion = "TOOLUP_DATA_INGESTION"

    [<Literal>]
    let columnMapping = "TOOLUP_COLUMN_MAPPING"

    [<Literal>]
    let oauthRefresher = "TOOLUP_OAUTH_REFRESHER"

    [<Literal>]
    let entityStore = "TOOLUP_ENTITY_STORE"

    [<Literal>]
    let entityOutbox = "TOOLUP_ENTITY_OUTBOX"

    [<Literal>]
    let usageMetering = "TOOLUP_USAGE_METERING"

    [<Literal>]
    let computeBudget = "TOOLUP_COMPUTE_BUDGET"

    [<Literal>]
    let metricsEndpoint = "TOOLUP_METRICS_ENDPOINT"

    [<Literal>]
    let platformKnowledgeBase = "TOOLUP_PLATFORM_KNOWLEDGE_BASE"

    [<Literal>]
    let configDriftDetection = "TOOLUP_CONFIG_DRIFT_DETECTION"

    [<Literal>]
    let rateLimiter = "TOOLUP_RATE_LIMITER"

    [<Literal>]
    let smokeTest = "TOOLUP_SMOKE_TEST"

    [<Literal>]
    let deploymentReadiness = "TOOLUP_DEPLOYMENT_READINESS"

    [<Literal>]
    let deploymentVerification = "TOOLUP_DEPLOYMENT_VERIFICATION"

    [<Literal>]
    let assetStore = "TOOLUP_ASSET_STORE"

    [<Literal>]
    let consentAudit = "TOOLUP_CONSENT_AUDIT"

    [<Literal>]
    let adAnalytics = "TOOLUP_AD_ANALYTICS"

    [<Literal>]
    let jobScheduler = "TOOLUP_JOB_SCHEDULER"

    [<Literal>]
    let staticPathBehaviour = "TOOLUP_STATIC_PATH_BEHAVIOUR"

    [<Literal>]
    let authCookieIssuance = "TOOLUP_AUTH_COOKIE_ISSUANCE"

    [<Literal>]
    let auditFailurePolicy = "TOOLUP_AUDIT_FAILURE_POLICY"

    [<Literal>]
    let resultStore = "TOOLUP_RESULT_STORE"

    [<Literal>]
    let consentStateStore = "TOOLUP_CONSENT_STATE_STORE"

    [<Literal>]
    let serverlessHost = "TOOLUP_SERVERLESS_HOST"

    [<Literal>]
    let processProfile = "TOOLUP_PROCESS_PROFILE"

    [<Literal>]
    let teamCreationPolicy = "TOOLUP_TEAM_CREATION_POLICY"

    [<Literal>]
    let rateLimitStore = "TOOLUP_RATE_LIMIT_STORE"

    [<Literal>]
    let eventStore = "TOOLUP_EVENT_STORE"

    [<Literal>]
    let conversationStore = "TOOLUP_CONVERSATION_STORE"

    [<Literal>]
    let publicRendering = "TOOLUP_PUBLIC_RENDERING"

    [<Literal>]
    let dataSubjectRequests = "TOOLUP_DATA_SUBJECT_REQUESTS"

    [<Literal>]
    let securityHardening = "TOOLUP_SECURITY_HARDENING"

    [<Literal>]
    let mappingDryRunBlock = "TOOLUP_MAPPING_DRYRUN_BLOCK"

    [<Literal>]
    let platformSurfaces = "TOOLUP_PLATFORM_SURFACES"

    [<Literal>]
    let acceptPlaintextSecretsInAuthMode =
        "TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE"

    [<Literal>]
    let acceptInProcessSchedulerMultiInstance =
        "TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE"

    [<Literal>]
    let acceptUnsignedPublishable = "TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE"

    [<Literal>]
    let acceptInMemoryShareTokenRateLimiterMultiInstance =
        "TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE"

    [<Literal>]
    let acceptInProcessIngestionMultiInstance =
        "TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE"

    [<Literal>]
    let acceptSharedEmbeddingCacheInTeamMode =
        "TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE"

    [<Literal>]
    let acceptEphemeralRagIndex = "TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX"

    [<Literal>]
    let acceptLocalEmbedderAtScale = "TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE"

    [<Literal>]
    let acceptStickyRoutedAiMultiInstance =
        "TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE"

    [<Literal>]
    let acceptForwardedHeadersFromAnyProxy =
        "TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY"

    [<Literal>]
    let backfillMissedTicks = "TOOLUP_BACKFILL_MISSED_TICKS"

    [<Literal>]
    let eventTriggerCatchUp = "TOOLUP_EVENT_TRIGGER_CATCHUP"

    [<Literal>]
    let migrateWebhookSecretsAtRest = "TOOLUP_MIGRATE_WEBHOOK_SECRETS"

    [<Literal>]
    let skipPreflight = "TOOLUP_SKIP_PREFLIGHT"

    [<Literal>]
    let healthStateTracking = "TOOLUP_HEALTH_STATE_TRACKING"

    [<Literal>]
    let enableCitationDevEndpoint = "TOOLUP_ENABLE_CITATION_DEV_ENDPOINT"

    [<Literal>]
    let enableDevEndpoints = "TOOLUP_ENABLE_DEV_ENDPOINTS"

    [<Literal>]
    let moduleBindingAllowUnbound = "TOOLUP_MODULE_BINDING_ALLOW_UNBOUND"

    [<Literal>]
    let includePlatformDefaults = "TOOLUP_INCLUDE_PLATFORM_DEFAULTS"

    [<Literal>]
    let storeEvictionMinutes = "TOOLUP_STORE_EVICTION_MINUTES"

    [<Literal>]
    let rateLimitPermits = "TOOLUP_RATE_LIMIT_PERMITS"

    [<Literal>]
    let rateLimitWindowSeconds = "TOOLUP_RATE_LIMIT_WINDOW_SECONDS"

    [<Literal>]
    let rateLimitQueue = "TOOLUP_RATE_LIMIT_QUEUE"

    [<Literal>]
    let defaultStorageQuotaBytes = "TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES"

    [<Literal>]
    let slowRequestMs = "TOOLUP_SLOW_REQUEST_MS"

    [<Literal>]
    let maxSseConnectionsPerScope = "TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE"

    [<Literal>]
    let slowRateLimitMs = "TOOLUP_SLOW_RATE_LIMIT_MS"

    [<Literal>]
    let publicBaseUrl = "TOOLUP_PUBLIC_BASE_URL"

    [<Literal>]
    let publicPath = "TOOLUP_PUBLIC_PATH"

    [<Literal>]
    let moduleFilter = "TOOLUP_MODULE"

    [<Literal>]
    let trustedProxyCidrs = "TOOLUP_TRUSTED_PROXY_CIDRS"

    [<Literal>]
    let webhookUrlAllowedHosts = "TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS"

    [<Literal>]
    let peerRoutePrefixes = "TOOLUP_PEER_ROUTE_PREFIXES"

    [<Literal>]
    let moduleBindingAnchors = "TOOLUP_MODULE_BINDING_ANCHORS"

    [<Literal>]
    let aiProvider = "TOOLUP_AI_PROVIDER"

    [<Literal>]
    let aiModel = "TOOLUP_AI_MODEL"

    [<Literal>]
    let aiProbeOnStartup = "TOOLUP_AI_PROBE_ON_STARTUP"

    [<Literal>]
    let ragRefuseOnIndexCorruption = "TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION"

    [<Literal>]
    let awsS3Region = "TOOLUP_AWS_S3_REGION"

    [<Literal>]
    let awsS3Endpoint = "TOOLUP_AWS_S3_ENDPOINT"

    [<Literal>]
    let awsSecretsRegion = "TOOLUP_AWS_SECRETS_REGION"

    [<Literal>]
    let azureKeyVaultUrl = "TOOLUP_AZURE_KEY_VAULT_URL"

    [<Literal>]
    let gcpProjectId = "TOOLUP_GCP_PROJECT_ID"

    [<Literal>]
    let gcsCredentialsJson = "TOOLUP_GCS_CREDENTIALS_JSON"

    [<Literal>]
    let entraExternalIdTenant = "TOOLUP_ENTRA_EXTERNAL_ID_TENANT"

    [<Literal>]
    let entraExternalIdAudience = "TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE"

    [<Literal>]
    let entraExternalIdCustomDomain = "TOOLUP_ENTRA_EXTERNAL_ID_CUSTOM_DOMAIN"

    [<Literal>]
    let entraExternalIdSignInPolicy = "TOOLUP_ENTRA_EXTERNAL_ID_SIGN_IN_POLICY"

    [<Literal>]
    let entraExternalIdSignUpPolicy = "TOOLUP_ENTRA_EXTERNAL_ID_SIGN_UP_POLICY"

    [<Literal>]
    let entraExternalIdClockSkewSeconds = "TOOLUP_ENTRA_EXTERNAL_ID_CLOCK_SKEW_SECONDS"

    [<Literal>]
    let entraDirectoryEnabled = "TOOLUP_ENTRA_DIRECTORY_ENABLED"

    [<Literal>]
    let entraDirectoryGraphEndpoint = "TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT"

    [<Literal>]
    let entraDirectorySenderOid = "TOOLUP_ENTRA_DIRECTORY_SENDER_OID"

    [<Literal>]
    let githubAuth = "TOOLUP_GITHUB_AUTH"

    [<Literal>]
    let githubApiBaseUrl = "TOOLUP_GITHUB_API_BASE_URL"

    [<Literal>]
    let githubAllowedOrgs = "TOOLUP_GITHUB_ALLOWED_ORGS"

    [<Literal>]
    let githubCacheTtlSeconds = "TOOLUP_GITHUB_CACHE_TTL_SECONDS"

    [<Literal>]
    let githubFetchPrimaryEmail = "TOOLUP_GITHUB_FETCH_PRIMARY_EMAIL"

    [<Literal>]
    let githubUserAgent = "TOOLUP_GITHUB_USER_AGENT"

    [<Literal>]
    let ldapAuth = "TOOLUP_LDAP_AUTH"

    [<Literal>]
    let ldapHost = "TOOLUP_LDAP_HOST"

    [<Literal>]
    let ldapPort = "TOOLUP_LDAP_PORT"

    [<Literal>]
    let ldapChannel = "TOOLUP_LDAP_CHANNEL"

    [<Literal>]
    let ldapAllowPlaintext = "TOOLUP_LDAP_ALLOW_PLAINTEXT"

    [<Literal>]
    let ldapAllowUntrustedCert = "TOOLUP_LDAP_ALLOW_UNTRUSTED_CERT"

    [<Literal>]
    let ldapCertThumbprint = "TOOLUP_LDAP_CERT_THUMBPRINT"

    [<Literal>]
    let ldapSearchBase = "TOOLUP_LDAP_SEARCH_BASE"

    [<Literal>]
    let ldapBindDn = "TOOLUP_LDAP_BIND_DN"

    [<Literal>]
    let ldapBindSecretKey = "TOOLUP_LDAP_BIND_SECRET_KEY"

    [<Literal>]
    let ldapTimeoutSeconds = "TOOLUP_LDAP_TIMEOUT_SECONDS"

    [<Literal>]
    let ldapCacheTtlSeconds = "TOOLUP_LDAP_CACHE_TTL_SECONDS"

    [<Literal>]
    let ldapNestedGroups = "TOOLUP_LDAP_NESTED_GROUPS"

    [<Literal>]
    let ldapUserIdAttr = "TOOLUP_LDAP_USER_ID_ATTR"

    [<Literal>]
    let ldapLoginAttr = "TOOLUP_LDAP_LOGIN_ATTR"

    [<Literal>]
    let ldapEmailAttr = "TOOLUP_LDAP_EMAIL_ATTR"

    [<Literal>]
    let ldapDisplayAttr = "TOOLUP_LDAP_DISPLAY_ATTR"

    [<Literal>]
    let ldapMemberOfAttr = "TOOLUP_LDAP_MEMBEROF_ATTR"

    [<Literal>]
    let ldapUserObjectClass = "TOOLUP_LDAP_USER_OBJECTCLASS"

    [<Literal>]
    let smtpHost = "TOOLUP_SMTP_HOST"

    [<Literal>]
    let smtpPort = "TOOLUP_SMTP_PORT"

    [<Literal>]
    let smtpUsername = "TOOLUP_SMTP_USERNAME"

    [<Literal>]
    let smtpPassword = "TOOLUP_SMTP_PASSWORD"

    [<Literal>]
    let smtpTls = "TOOLUP_SMTP_TLS"

    [<Literal>]
    let smtpFrom = "TOOLUP_SMTP_FROM"

    [<Literal>]
    let smtpFromName = "TOOLUP_SMTP_FROM_NAME"

    [<Literal>]
    let sendGridFrom = "TOOLUP_SENDGRID_FROM"

    [<Literal>]
    let sendGridFromName = "TOOLUP_SENDGRID_FROM_NAME"

    [<Literal>]
    let sendGridEndpoint = "TOOLUP_SENDGRID_ENDPOINT"

    [<Literal>]
    let twilioAccountSid = "TOOLUP_TWILIO_ACCOUNT_SID"

    [<Literal>]
    let twilioFrom = "TOOLUP_TWILIO_FROM"

    [<Literal>]
    let twilioEndpoint = "TOOLUP_TWILIO_ENDPOINT"

    [<Literal>]
    let oidcPreflightTimeoutMs = "TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS"

    [<Literal>]
    let externalCompute = "TOOLUP_EXTERNAL_COMPUTE"

    [<Literal>]
    let externalComputeHttpPrefix = "TOOLUP_EXTERNAL_COMPUTE_HTTP_"

    [<Literal>]
    let componentConfigPrefix = "TOOLUP_COMPONENT__"

    [<Literal>]
    let emitSbom = "TOOLUP_EMIT_SBOM"

    [<Literal>]
    let publishSource = "TOOLUP_PUBLISH_SOURCE"

    [<Literal>]
    let testArgs = "TOOLUP_TEST_ARGS"

    [<Literal>]
    let cookbookPath = "TOOLUP_COOKBOOK_PATH"

    [<Literal>]
    let enterpriseCookbookPath = "TOOLUP_ENTERPRISE_COOKBOOK_PATH"

    [<Literal>]
    let beirCache = "TOOLUP_BEIR_CACHE"

    [<Literal>]
    let remotingAnalyzerAudit = "TOOLUP_REMOTING_ANALYZER_AUDIT"

    [<Literal>]
    let approveApi = "TOOLUP_APPROVE_API"

    [<Literal>]
    let regenConfigReference = "TOOLUP_REGEN_CONFIG_REFERENCE"

/// The full registry. Add a descriptor here whenever a `*FromEnv` reader
/// gains a new env var; the coverage test fails if a reader consults a
/// var with no descriptor, and the golden-file test fails until the
/// reference doc is regenerated.
let all: ConfigKeyDescriptor list = [
    // ─── Storage & secrets ──────────────────────────────────────────
    {
        EnvVar = Names.blobStorage
        Description =
            "Selects the IBlobStorage backend. Unrecognised / cloud-without-credentials values warn and fall back to local."
        Type = EnumKey [ "local"; "azure"; "s3"; "gcs" ]
        Default = Some "local"
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.azureStorageConnectionString
        Description = "Azure Blob Storage connection string used when TOOLUP_BLOB_STORAGE=azure."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.awsS3Bucket
        Description = "Target S3 bucket name used when TOOLUP_BLOB_STORAGE=s3."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.gcsBucket
        Description = "Target Google Cloud Storage bucket name used when TOOLUP_BLOB_STORAGE=gcs."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.secretStore
        Description =
            "Selects the ISecretStore backend. Cloud values require their companion's own env vars; unset uses the encrypted local file store."
        Type =
            EnumKey [
                "encrypted"
                "file"
                "env"
                "azure-key-vault"
                "aws-secrets-manager"
                "gcp-secret-manager"
                "vault"
            ]
        Default = Some "encrypted"
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.secretsMasterKey
        Description =
            "Base64-encoded 32-byte master key for the encrypted local secret store. Unset stores secrets as plaintext at rest (preflight warns)."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.secretsPath
        Description = "Filesystem path the file/encrypted secret store reads and writes secrets under."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }

    // ─── Auth & identity ────────────────────────────────────────────
    {
        EnvVar = Names.authMode
        Description =
            "Selects the IAuthProvider. Unset uses the dev-only HeaderAuthProvider (trusts X-User-Id); 'oidc' requires TOOLUP_OIDC_ISSUER. An unrecognised value refuses startup."
        Type = EnumKey [ "oidc" ]
        Default = Some "(unset — dev HeaderAuthProvider)"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.oidcIssuer
        Description =
            "OIDC provider discovery URL. Required when TOOLUP_AUTH_MODE=oidc; missing issuer refuses startup."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.oidcAudience
        Description =
            "Expected OIDC token audience. Unset accepts any audience (preflight warns in authenticated modes)."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.sseAuth
        Description =
            "When set to a cookie value, the OIDC provider also accepts the JWT from the toolup-auth-token cookie so EventSource SSE handshakes authenticate. Unset keeps bearer-header-only."
        Type = EnumKey [ "cookie"; "cookies"; "cookieonly" ]
        Default = Some "(unset — bearer header only)"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.initialPlatformAdmin
        Description = "User id (OIDC sub/oid) granted Platform Admin on first boot when no admin exists yet."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.adminToken
        Description =
            "Bearer token guarding the crypto-shred encryption-admin endpoints. Unset leaves those endpoints unmounted (preflight warns if the surface is composed)."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.allowDevAdminBootstrap
        Description =
            "When true in an auth-requiring mode, the first sign-in auto-promotes to Platform Admin (privilege-escalation surface; preflight warns)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.initialTeamName
        Description = "Display name of the bootstrap team created on first boot."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.initialTeamId
        Description = "Stable id of the bootstrap team created on first boot."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.oauthRedirectBase
        Description =
            "Absolute base URL used to build OAuth-connector redirect URIs (must match the provider's registered callback origin)."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }

    // ─── Logging & observability ────────────────────────────────────
    {
        EnvVar = Names.logLevel
        Description =
            "Floor for the default ConsoleLogger. Error is never silenced. An unrecognised value warns and uses Info."
        Type = EnumKey [ "Debug"; "Info"; "Warn"; "Error" ]
        Default = Some "Info"
        IsSecret = false
        Category = "Logging & observability"
    }
    {
        EnvVar = Names.logFormat
        Description = "Selects the default logger's output shape: human-readable text or structured JSON lines."
        Type = EnumKey [ "text"; "json" ]
        Default = Some "text"
        IsSecret = false
        Category = "Logging & observability"
    }
    {
        EnvVar = Names.traceCategories
        Description =
            "Comma/space-separated whitelist of trace categories to emit (e.g. ai.sse,platform.sse). Empty emits no Trace output."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Logging & observability"
    }
    {
        EnvVar = Names.appName
        Description = "Display name the platform shell and startup banner present for this deployment."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Logging & observability"
    }

    // ─── Deployment shape ───────────────────────────────────────────
    {
        EnvVar = Names.configFile
        Description =
            "Path to the deployment configuration manifest (JSON, keys are these env-var names). Set: the named file must exist. Unset: ./toolup.config.json is probed and used when present, else no manifest is loaded."
        Type = StringKey
        Default = Some "(unset — probes ./toolup.config.json)"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.strictConfig
        Description =
            "Escalates the unknown-config-key preflight guard from a warning to a startup refusal. Off: a set TOOLUP_* variable whose name is in no registry entry is warned about once at preflight. On: it refuses the boot."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.profile
        Description =
            "Name of the configuration profile this deployment imports — a named bundle of keys resolved one rung BELOW the manifest, so any explicit environment or manifest line still wins. A manifest selects one with its \"$profile\" entry instead, which takes precedence over this variable; an unrecognised name refuses startup and lists the available profiles."
        Type = StringKey
        Default = Some "(unset — no profile is imported)"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.replicaCount
        Description =
            "Number of instances this deployment runs behind a load balancer. >1 makes multi-instance config validators refuse single-instance substrates."
        Type = IntKey
        Default = Some "1"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.notificationChannel
        Description =
            "Selects the INotificationChannel backend. 'redis' requires TOOLUP_REDIS_CONNECTION; unset uses the single-instance in-memory channel."
        Type = EnumKey [ "inmemory"; "redis" ]
        Default = Some "inmemory"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.distributedLock
        Description =
            "Phase 9i — selects the IDistributedLock backend (the SDK-wide cross-instance lease primitive). 'redis' requires TOOLUP_REDIS_CONNECTION; unset uses InProcessDistributedLock, which is correct for a single instance and excludes nothing across replicas. Read by DistributedLockSelection.fromEnv, which the composition root threads its companion resolvers into."
        Type = EnumKey [ "inprocess"; "redis" ]
        Default = Some "inprocess"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.redisConnection
        Description =
            "Redis connection string for the distributed notification channel / caches / distributed lock used when TOOLUP_NOTIFICATION_CHANNEL=redis or TOOLUP_DISTRIBUTED_LOCK=redis."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.requireHttps
        Description = "When true, the platform enforces HTTPS (redirect + HSTS) for browser-facing surfaces."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.trustForwardedHeaders
        Description =
            "When true, trusts X-Forwarded-* headers from the upstream proxy. Only safe behind a proxy that strips/re-injects them (preflight warns without RequireHttps)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.maxRequestBodyBytes
        Description = "Kestrel per-request body cap in bytes. Unset leaves the framework's 30 MB default."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.maxFileBytes
        Description = "Maximum accepted upload size in bytes for file-management endpoints."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.smokeToken
        Description = "Bearer token guarding the post-deploy smoke-test endpoint (GET /api/_internal/smoke)."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.auditAdminRequired
        Description = "When true, audit-log read endpoints require Platform Admin rather than team-level access."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Deployment shape"
    }

    // ─── Security preflight escape hatches ──────────────────────────
    {
        EnvVar = Names.acceptLocalFallback
        Description =
            "Acknowledge a cloud-declared blob backend silently falling back to local storage (downgrades the refusal to a warning)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptHeaderAuthInAuthMode
        Description =
            "Acknowledge running the spoofable HeaderAuthProvider in an authenticated mode (only safe behind a mTLS proxy)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptUnboundAudienceInAuthMode
        Description = "Acknowledge an unset OIDC audience in an authenticated mode (token-reuse risk)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptSameSiteOnlyCsrfInAuthMode
        Description = "Acknowledge relying on SameSite cookies alone (no server-side CSRF token) for cookie auth."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptNoRateLimitInAuthMode
        Description = "Acknowledge an internet-facing authenticated deployment with no rate limiting."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptQueryParamSseAuthInAuthMode
        Description =
            "Acknowledge SSE query-param auth fallback in an authenticated mode (leaks the userId in URLs/logs)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptInviteByEmailWithoutDirectory
        Description =
            "Acknowledge a team invite-by-email surface mounted with no IUserDirectory (emails silently never send)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptPendingInviteStoreMultiInstance
        Description =
            "Acknowledge the in-memory pending-invite store under a multi-instance deployment (per-replica drift)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptInMemoryOAuthStateMultiInstance
        Description =
            "Acknowledge the in-memory OAuth state store under a multi-instance deployment (callback may hit a replica without the state)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    // --- Platform subsystems ---
    {
        EnvVar = Names.shareTokenStore
        Description =
            "Enables the IShareTokenStore substrate backing publishable share links (signed tokens + claim store)."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.webhooks
        Description = "Enables outbound webhook delivery (subscriptions, signing, retry)."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.auditLog
        Description = "Enables the audit log and its sink dispatcher."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.oauthRefresher
        Description = "Enables the background OAuth token refresher for stored data-source credentials."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.entityStore
        Description = "Enables the IEntityStore substrate (registered entity types and persistence)."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.entityOutbox
        Description =
            "Enables the entity outbox, so entity saves publish transactionally instead of being discarded unpublished."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.usageMetering
        Description = "Enables per-scope usage metering, the counters feeding quota and billing surfaces."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.computeBudget
        Description = "Enables compute-budget accounting and enforcement for long-running work."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.platformKnowledgeBase
        Description = "Enables the platform-level knowledge base, the SDK-shipped document KB surface."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.configDriftDetection
        Description = "Enables startup detection of drift between persisted config and the composed defaults."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.smokeTest
        Description = "Enables the post-boot smoke-test surface, which is itself guarded by TOOLUP_SMOKE_TOKEN."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.deploymentReadiness
        Description = "Enables the deployment-readiness report surface."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.deploymentVerification
        Description = "Enables the one-command post-deployment verification report."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.assetStore
        Description = "Enables the IAssetStore substrate for uploaded media and derivative rendering."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.adAnalytics
        Description = "Enables the advertising-analytics surface."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.jobScheduler
        Description =
            "Selects the in-process IJobScheduler. Dev-shaped: a multi-instance deployment needs a distributed scheduler companion."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.auditFailurePolicy
        Description =
            "What happens when an audit sink write fails: log and continue, refuse the action, or degrade to a local file."
        Type = EnumKey [ "log"; "refuse"; "degrade" ]
        Default = Some "log"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.resultStore
        Description = "Selects the result store backing long-running job output retrieval."
        Type = EnumKey [ "no"; "inmemory"; "persistent" ]
        Default = Some "no"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.eventStore
        Description =
            "Selects the IEventStore backend. The persistent option uses the blob-backed store with the 90-day retention policy."
        Type = EnumKey [ "inmemory"; "persistent" ]
        Default = Some "inmemory"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.backfillMissedTicks
        Description = "On startup, runs schedule ticks that were missed while the process was down."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.eventTriggerCatchUp
        Description = "On startup, replays event triggers that fired while the process was down."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.migrateWebhookSecretsAtRest
        Description = "Migrates inline webhook secrets into the secret store on boot."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.enableCitationDevEndpoint
        Description = "Exposes the RAG citation inspection dev endpoint."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.webhookUrlAllowedHosts
        Description = "Comma-separated host allow-list for outbound webhook URLs. Unset allows any host."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.externalCompute
        Description = "Selects the external-compute companion."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Platform subsystems"
    }
    {
        EnvVar = Names.externalComputeHttpPrefix
        Description =
            "Prefix for the HTTP external-compute companion settings; the suffix names the setting. Not read as a variable in its own right."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Platform subsystems"
    }
    // --- Data, ingestion & compliance ---
    {
        EnvVar = Names.lineage
        Description = "Enables the lineage store recording dataset and derivation provenance."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    {
        EnvVar = Names.dataIngestion
        Description = "Enables the data-ingestion pipeline (IDataIngestor plus the background ingestion service)."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    {
        EnvVar = Names.columnMapping
        Description = "Enables the column-mapping subsystem for uploaded tabular data."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    {
        EnvVar = Names.consentAudit
        Description = "Enables consent-change auditing."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    {
        EnvVar = Names.consentStateStore
        Description = "Selects the consent-state backend."
        Type = EnumKey [ "off"; "inmemory"; "entity" ]
        Default = Some "off"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    {
        EnvVar = Names.dataSubjectRequests
        Description =
            "Disables the data-subject-request surface. Enabling it requires an explicit ErasurePolicy, a compliance decision, so it must be set in ServerConfig."
        Type = EnumKey [ "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    {
        EnvVar = Names.mappingDryRunBlock
        Description = "When enabled, a failed column-mapping dry run blocks the import instead of only warning."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Data, ingestion & compliance"
    }
    // --- Logging & observability ---
    {
        EnvVar = Names.metricsEndpoint
        Description = "Exposes the Prometheus-style scrape endpoint for the registered IMetricsSink."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Logging & observability"
    }
    {
        EnvVar = Names.healthStateTracking
        Description =
            "Tracks health-check state transitions, so a probe can report how long a component has been unhealthy."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Logging & observability"
    }
    {
        EnvVar = Names.slowRequestMs
        Description = "Milliseconds above which a request is logged as slow."
        Type = IntKey
        Default = Some "1000"
        IsSecret = false
        Category = "Logging & observability"
    }
    // --- Rate limiting ---
    {
        EnvVar = Names.rateLimiter
        Description = "Enables the request rate-limiter middleware."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Rate limiting"
    }
    {
        EnvVar = Names.rateLimitStore
        Description =
            "Selects where rate-limit counters live. The in-memory store is per-instance and therefore wrong for a multi-instance deployment."
        Type = EnumKey [ "no"; "inmemory"; "external" ]
        Default = Some "no"
        IsSecret = false
        Category = "Rate limiting"
    }
    {
        EnvVar = Names.rateLimitPermits
        Description = "Requests allowed per window. Set alongside the window and queue keys to switch rate limiting on."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Rate limiting"
    }
    {
        EnvVar = Names.rateLimitWindowSeconds
        Description = "Length of the rate-limit window, in seconds."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Rate limiting"
    }
    {
        EnvVar = Names.rateLimitQueue
        Description = "How many requests may queue once the permit count is exhausted."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Rate limiting"
    }
    {
        EnvVar = Names.slowRateLimitMs
        Description = "Milliseconds a request may wait on the rate limiter before that wait is logged as slow."
        Type = IntKey
        Default = Some "5000"
        IsSecret = false
        Category = "Rate limiting"
    }
    // --- Deployment shape ---
    {
        EnvVar = Names.staticPathBehaviour
        Description = "How a missing static-content path is treated at boot: warn, refuse to start, or skip silently."
        Type = EnumKey [ "warn"; "require"; "skip" ]
        Default = Some "warn"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.serverlessHost
        Description =
            "Host shape the server assumes: the standard Kestrel host, or a serverless host that skips long-lived background services."
        Type = EnumKey [ "kestrel"; "serverless" ]
        Default = Some "kestrel"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.processProfile
        Description =
            "Which role this process plays when the deployment is split: everything, web only, worker only, or dispatcher only."
        Type = EnumKey [ "allinone"; "web"; "worker"; "dispatcher" ]
        Default = Some "allinone"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.securityHardening
        Description = "Security-header and hardening posture applied to every response."
        Type = EnumKey [ "no"; "default"; "strict" ]
        Default = Some "no"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.platformSurfaces
        Description =
            "Comma-separated surface profiles the deployment exposes, for example anonymous, user, multi-team or claim-bearer."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.skipPreflight
        Description =
            "Skips the entire startup config preflight. Intended for local iteration; a production deployment that sets it boots unvalidated."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.enableDevEndpoints
        Description = "Exposes the /dev/* inspection endpoints. Should stay off in production."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.includePlatformDefaults
        Description = "Merges the SDK platform default config schema into the composed surface."
        Type = BoolKey
        Default = Some "true"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.storeEvictionMinutes
        Description = "Idle minutes before an ephemeral in-memory store entry is evicted."
        Type = IntKey
        Default = Some "60"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.maxSseConnectionsPerScope
        Description = "Maximum concurrent SSE connections per scope."
        Type = IntKey
        Default = Some "10"
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.moduleFilter
        Description = "Restricts the composed surface to a single named module. Intended for local iteration."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.trustedProxyCidrs
        Description = "Comma-separated CIDR ranges whose X-Forwarded-* headers are trusted."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.peerRoutePrefixes
        Description = "Comma-separated route prefixes served by the cross-deployment peer substrate."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    {
        EnvVar = Names.componentConfigPrefix
        Description =
            "Prefix for per-component config overrides, spelled TOOLUP_COMPONENT__ComponentId__Key. Not read as a variable in its own right."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Deployment shape"
    }
    // --- Auth & identity ---
    {
        EnvVar = Names.authCookieIssuance
        Description =
            "Issues the platform auth cookie alongside the bearer token, so SSE can authenticate without a query parameter."
        Type = EnumKey [ "enabled"; "disabled" ]
        Default = Some "disabled"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.teamCreationPolicy
        Description = "Who may create a team: platform admins only, or any authenticated user."
        Type = EnumKey [ "platformadminonly"; "anyauthenticateduser" ]
        Default = Some "platformadminonly"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraExternalIdTenant
        Description = "Entra External ID tenant name."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraExternalIdAudience
        Description = "Expected token audience for the Entra External ID auth provider."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraExternalIdCustomDomain
        Description = "Custom sign-in domain for the Entra External ID auth provider."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraExternalIdSignInPolicy
        Description = "Sign-in user-flow policy id."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraExternalIdSignUpPolicy
        Description = "Sign-up user-flow policy id."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraExternalIdClockSkewSeconds
        Description = "Permitted clock skew, in seconds, when validating Entra tokens."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraDirectoryEnabled
        Description = "Enables the Entra directory companion for user lookup and invitation via Microsoft Graph."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraDirectoryGraphEndpoint
        Description = "Microsoft Graph endpoint override for the Entra directory companion."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.entraDirectorySenderOid
        Description = "Object id of the principal used as the sender for directory invitations."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.githubAuth
        Description = "Enables the GitHub OAuth auth provider."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.githubApiBaseUrl
        Description = "GitHub API base URL. Override it for GitHub Enterprise."
        Type = StringKey
        Default = Some "https://api.github.com"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.githubAllowedOrgs
        Description = "Comma-separated GitHub organisations whose members may sign in. Unset allows any account."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.githubCacheTtlSeconds
        Description = "Seconds a resolved GitHub identity is cached."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.githubFetchPrimaryEmail
        Description = "Additionally fetches the primary email address, which requires the user:email scope."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.githubUserAgent
        Description = "User-Agent sent on GitHub API calls."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapAuth
        Description = "Enables the LDAP / Active Directory auth provider."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapHost
        Description = "LDAP server hostname."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapPort
        Description = "LDAP server port. Defaults to the standard port for the selected channel."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapChannel
        Description = "Transport security used for the LDAP connection."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapAllowPlaintext
        Description = "Allows an unencrypted LDAP connection, which sends bind credentials in the clear."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapAllowUntrustedCert
        Description = "Skips LDAP server certificate validation."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapCertThumbprint
        Description = "Pins the LDAP server certificate to this thumbprint."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapSearchBase
        Description = "Base DN for user searches."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapBindDn
        Description = "DN of the service account used to bind before searching."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapBindSecretKey
        Description =
            "ISecretStore key holding the service-account bind password. The password itself is never read from the environment."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapTimeoutSeconds
        Description = "LDAP operation timeout, in seconds."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapCacheTtlSeconds
        Description = "Seconds a resolved LDAP identity is cached."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapNestedGroups
        Description = "Resolves nested group memberships when mapping roles."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapUserIdAttr
        Description = "LDAP attribute the stable user id is read from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapLoginAttr
        Description = "LDAP attribute the login name is read from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapEmailAttr
        Description = "LDAP attribute the email address is read from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapDisplayAttr
        Description = "LDAP attribute the display name is read from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapMemberOfAttr
        Description = "LDAP attribute the group membership is read from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.ldapUserObjectClass
        Description = "objectClass used to filter user entries."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    {
        EnvVar = Names.oidcPreflightTimeoutMs
        Description = "Milliseconds the OIDC preflight waits for the issuer discovery document."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Auth & identity"
    }
    // --- AI ---
    {
        EnvVar = Names.conversationStore
        Description =
            "Disables AI conversation persistence. Enabling it requires a retentionDays value, so it must be set in ServerConfig rather than here."
        Type = EnumKey [ "no" ]
        Default = Some "no"
        IsSecret = false
        Category = "AI"
    }
    {
        EnvVar = Names.aiProvider
        Description = "Selects the IAIProvider companion the AI surface resolves at startup."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "AI"
    }
    {
        EnvVar = Names.aiModel
        Description = "Model id passed to the selected AI provider."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "AI"
    }
    {
        EnvVar = Names.aiProbeOnStartup
        Description =
            "Probes the configured AI provider during preflight, so a bad key fails at boot rather than on first use."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "AI"
    }
    {
        EnvVar = Names.ragRefuseOnIndexCorruption
        Description = "Refuses to start when the vector index fails its integrity check, instead of rebuilding it."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "AI"
    }
    // --- Public surface & rendering ---
    {
        EnvVar = Names.publicRendering
        Description =
            "Disables server-side public page rendering. Enabling it requires a ContentRoot path, so it must be set in ServerConfig rather than here."
        Type = EnumKey [ "no" ]
        Default = Some "no"
        IsSecret = false
        Category = "Public surface & rendering"
    }
    {
        EnvVar = Names.publicBaseUrl
        Description =
            "Absolute base URL the deployment is reachable at. Used to build links in emails, share tokens and OAuth redirects."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Public surface & rendering"
    }
    {
        EnvVar = Names.publicPath
        Description = "Filesystem path served as static public content."
        Type = StringKey
        Default = Some "deploy/public"
        IsSecret = false
        Category = "Public surface & rendering"
    }
    // --- Security preflight escape hatches ---
    {
        EnvVar = Names.acceptPlaintextSecretsInAuthMode
        Description =
            "Allows a plaintext secret store while auth is required. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptInProcessSchedulerMultiInstance
        Description =
            "Allows the in-process job scheduler when ReplicaCount is above 1, so scheduled jobs run on every instance. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptUnsignedPublishable
        Description =
            "Allows publishable surfaces without artefact signing. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptInMemoryShareTokenRateLimiterMultiInstance
        Description =
            "Allows the in-memory share-token rate limiter when ReplicaCount is above 1, making the limit per-instance. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptInProcessIngestionMultiInstance
        Description =
            "Allows in-process ingestion when ReplicaCount is above 1, so a document may be ingested more than once. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptSharedEmbeddingCacheInTeamMode
        Description =
            "Allows a shared embedding cache in a team-scoped deployment, weakening tenant isolation of cached vectors. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptEphemeralRagIndex
        Description =
            "Allows a RAG index that does not survive a restart. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptLocalEmbedderAtScale
        Description =
            "Allows the local embedding provider at a corpus size it is not built for. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptStickyRoutedAiMultiInstance
        Description =
            "Allows sticky-routed AI streaming when ReplicaCount is above 1 without a distributed notification channel. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.acceptForwardedHeadersFromAnyProxy
        Description =
            "Trusts X-Forwarded-* headers from any peer instead of the configured proxy CIDRs, which lets a client spoof its own IP. Lowers a startup preflight refusal to a warning."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.moduleBindingAllowUnbound
        Description = "Allows modules that carry no signed binding manifest to load."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = EscapeHatchCategory
    }
    {
        EnvVar = Names.moduleBindingAnchors
        Description =
            "Semicolon-separated module-binding trust anchors, each mac:keyId:scope:key or asym:keyId:alg:base64pubkey."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = EscapeHatchCategory
    }
    // --- Storage & secrets ---
    {
        EnvVar = Names.defaultStorageQuotaBytes
        Description = "Default per-team storage quota in bytes. Unset means unlimited."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.awsS3Region
        Description = "AWS region for the S3 blob-storage companion."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.awsS3Endpoint
        Description = "Custom S3-compatible endpoint for the S3 blob-storage companion."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.awsSecretsRegion
        Description = "AWS region for the Secrets Manager secret-store companion."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.azureKeyVaultUrl
        Description = "Vault URL for the Azure Key Vault secret-store companion."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.gcpProjectId
        Description = "GCP project id for the Secret Manager and Cloud Storage companions."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Storage & secrets"
    }
    {
        EnvVar = Names.gcsCredentialsJson
        Description = "Service-account credentials JSON for the Google Cloud Storage companion."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Storage & secrets"
    }
    // --- Notification channels ---
    {
        EnvVar = Names.smtpHost
        Description = "SMTP server hostname for the email notification sink."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.smtpPort
        Description = "SMTP server port."
        Type = IntKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.smtpUsername
        Description = "SMTP username."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.smtpPassword
        Description = "SMTP password."
        Type = StringKey
        Default = None
        IsSecret = true
        Category = "Notification channels"
    }
    {
        EnvVar = Names.smtpTls
        Description = "TLS mode used for the SMTP connection."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.smtpFrom
        Description = "Default From address for SMTP-delivered notifications."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.smtpFromName
        Description = "Default From display name for SMTP-delivered notifications."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.sendGridFrom
        Description = "Default From address for SendGrid-delivered notifications."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.sendGridFromName
        Description = "Default From display name for SendGrid-delivered notifications."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.sendGridEndpoint
        Description = "SendGrid API endpoint override."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.twilioAccountSid
        Description = "Twilio account SID for the SMS notification sink."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.twilioFrom
        Description = "Originating phone number for Twilio-delivered SMS."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    {
        EnvVar = Names.twilioEndpoint
        Description = "Twilio API endpoint override."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = "Notification channels"
    }
    // --- Build & tooling ---
    {
        EnvVar = Names.emitSbom
        Description = "Build-time: emits a CycloneDX SBOM alongside the packed artefacts."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.publishSource
        Description = "Build-time: overrides the NuGet source the Publish target pushes to."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.testArgs
        Description = "Build-time: extra arguments passed to each Expecto test pack."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.cookbookPath
        Description = "Overrides the path the AG Charts AI cookbook is loaded from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.enterpriseCookbookPath
        Description = "Overrides the path the AG Grid Enterprise AI cookbook is loaded from."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.beirCache
        Description = "Benchmark-only: directory the BEIR retrieval corpus is cached in."
        Type = StringKey
        Default = None
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.remotingAnalyzerAudit
        Description = "Analyzer-time: emits an audit report of remoting API classification."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.approveApi
        Description =
            "Test-time: rewrites every public-API approval baseline instead of comparing against them. Never set on a running deployment."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = ToolingCategory
    }
    {
        EnvVar = Names.regenConfigReference
        Description =
            "Test-time: rewrites the generated configuration reference instead of comparing against the committed copy. Never set on a running deployment."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = ToolingCategory
    }
]

/// The keys a deployment configuration manifest may bind, declared.
///
/// A key is manifest-bindable only once its reader resolves through the
/// `ConfigResolution` seam; until then the manifest could state it and
/// nothing would consult it — worse than having no manifest at all.
/// Declaring the set makes that partial coverage *visible*: the generated
/// reference carries a column, `--print-config` labels each key's source,
/// and a manifest naming a registered-but-unbindable key warns at startup
/// naming the migration it waits on.
///
/// This is the Phase 71.A `ServerConfig.fromEnv` cluster — the largest
/// single reader in the SDK, migrated wholesale because its ~40 private
/// parsers all funnel through one env-read helper. The remaining
/// `*FromEnv` readers and env-reading validators migrate in family
/// batches, each flipping its own keys into this list; the coverage test
/// holds the list to what the readers actually do, in both directions, so
/// the sweep terminates instead of decaying.
///
/// Three kinds of key are absent by construction and never join the list.
/// **Secrets**: the loader refuses them outright and no acceptance hatch
/// lowers that, so declaring one bindable would be a claim nothing can
/// honour — `TOOLUP_MODULE_BINDING_ANCHORS` resolves through the seam but
/// may carry an inline symmetric key, and is excluded for exactly that
/// reason. **`TOOLUP_CONFIG_FILE`**: a manifest cannot name its own
/// location, which is already resolved by the time the file is read, so
/// the loader refuses that key by name rather than warning about a
/// migration that will never come. **`TOOLUP_PROFILE`** (Phase 700): the
/// same shape one rung lower — a manifest states the profile it imports
/// with its `"$profile"` entry, and admitting the env-var spelling as
/// well would be two ways to say one thing.
let manifestBindable: Set<string> =
    Set.ofList [
        Names.acceptEphemeralRagIndex
        Names.acceptForwardedHeadersFromAnyProxy
        Names.acceptHeaderAuthInAuthMode
        Names.acceptInMemoryOAuthStateMultiInstance
        Names.acceptInMemoryShareTokenRateLimiterMultiInstance
        Names.acceptInProcessIngestionMultiInstance
        Names.acceptInProcessSchedulerMultiInstance
        Names.acceptInviteByEmailWithoutDirectory
        Names.acceptLocalEmbedderAtScale
        Names.acceptLocalFallback
        Names.acceptNoRateLimitInAuthMode
        Names.acceptPendingInviteStoreMultiInstance
        Names.acceptPlaintextSecretsInAuthMode
        Names.acceptQueryParamSseAuthInAuthMode
        Names.acceptSameSiteOnlyCsrfInAuthMode
        Names.acceptSharedEmbeddingCacheInTeamMode
        Names.acceptStickyRoutedAiMultiInstance
        Names.acceptUnboundAudienceInAuthMode
        Names.acceptUnsignedPublishable
        Names.adAnalytics
        Names.allowDevAdminBootstrap
        Names.appName
        Names.assetStore
        Names.auditAdminRequired
        Names.auditFailurePolicy
        Names.auditLog
        Names.authCookieIssuance
        Names.authMode
        Names.backfillMissedTicks
        Names.blobStorage
        Names.columnMapping
        Names.computeBudget
        Names.configDriftDetection
        Names.consentAudit
        Names.consentStateStore
        Names.conversationStore
        Names.dataIngestion
        Names.dataSubjectRequests
        Names.defaultStorageQuotaBytes
        Names.deploymentReadiness
        Names.deploymentVerification
        Names.distributedLock
        Names.enableCitationDevEndpoint
        Names.enableDevEndpoints
        Names.entityOutbox
        Names.entityStore
        Names.eventStore
        Names.eventTriggerCatchUp
        Names.healthStateTracking
        Names.includePlatformDefaults
        Names.initialPlatformAdmin
        Names.initialTeamId
        Names.initialTeamName
        Names.jobScheduler
        Names.lineage
        Names.logFormat
        Names.logLevel
        Names.mappingDryRunBlock
        Names.maxFileBytes
        Names.maxRequestBodyBytes
        Names.maxSseConnectionsPerScope
        Names.metricsEndpoint
        Names.migrateWebhookSecretsAtRest
        Names.moduleBindingAllowUnbound
        Names.moduleFilter
        Names.notificationChannel
        Names.oauthRedirectBase
        Names.oauthRefresher
        Names.oidcAudience
        Names.oidcIssuer
        Names.peerRoutePrefixes
        Names.platformKnowledgeBase
        Names.platformSurfaces
        Names.processProfile
        Names.publicBaseUrl
        Names.publicPath
        Names.publicRendering
        Names.rateLimitPermits
        Names.rateLimitQueue
        Names.rateLimitStore
        Names.rateLimitWindowSeconds
        Names.rateLimiter
        Names.replicaCount
        Names.requireHttps
        Names.resultStore
        Names.secretStore
        Names.secretsPath
        Names.securityHardening
        Names.serverlessHost
        Names.shareTokenStore
        Names.skipPreflight
        Names.slowRateLimitMs
        Names.slowRequestMs
        Names.smokeTest
        Names.sseAuth
        Names.staticPathBehaviour
        Names.storeEvictionMinutes
        Names.strictConfig
        Names.teamCreationPolicy
        Names.traceCategories
        Names.trustForwardedHeaders
        Names.trustedProxyCidrs
        Names.usageMetering
        Names.webhookUrlAllowedHosts
        Names.webhooks
    ]

/// Whether `envVar` may be supplied by a deployment configuration
/// manifest. Drives the generated reference's column and the loader's
/// registered-but-not-yet-bindable warning.
let isManifestBindable (envVar: string) : bool = Set.contains envVar manifestBindable

/// The keys read by the build, test and analyzer tooling rather than by a
/// running server — **derived** from `ToolingCategory`, never listed a
/// second time.
///
/// The unknown-key preflight guard quantifies over the environment a
/// server process actually has, and a developer box legitimately carries
/// these: a build that emitted an SBOM, a test run that regenerated a
/// baseline. Warning about them would train an operator to scroll past the
/// one class of finding the guard exists to surface, so they are excluded
/// by name and the generated reference says so under the section heading.
///
/// Note the exclusion is belt-and-braces rather than load-bearing today:
/// every key here also carries a descriptor, so the guard's registry arm
/// already covers it. That is forced rather than incidental — the coverage
/// test demands a descriptor for any `TOOLUP_*` literal appearing in
/// shipped source, and naming a tooling key anywhere in the SDK (including
/// in this file) is exactly such a literal. What the classification adds is
/// that the exclusion is *stated*: a later reader can tell that a tooling
/// key is out of scope by intent, not by the accident of being registered.
let toolingKeys: Set<string> =
    all
    |> List.filter (fun k -> k.Category = ToolingCategory)
    |> List.map _.EnvVar
    |> Set.ofList

/// Whether `envVar` is a build / test / analyzer key rather than one a
/// running server reads. Drives the unknown-key preflight guard's
/// exclusion and the generated reference's section note.
let isToolingKey (envVar: string) : bool = Set.contains envVar toolingKeys

/// Every key that acknowledges a preflight refusal — **derived** from
/// `EscapeHatchCategory`, never listed a second time, and carrying the
/// whole descriptor rather than only the name.
///
/// The descriptor rather than the name because the one consumer that
/// needs this set needs the `Description` beside it: the deployment
/// verification report's accepted-acknowledgement section states each
/// active hatch's meaning in the same line as its name, so an assessor
/// reads what was accepted rather than a variable to go and look up. The
/// `Type` comes along for the same reason — a boolean hatch is active
/// only when its value reads as on, and that is a fact about the
/// descriptor, not about the name.
///
/// Sorted by name, so the same deployment always renders the same order.
let escapeHatchKeys: ConfigKeyDescriptor list =
    all
    |> List.filter (fun k -> k.Category = EscapeHatchCategory)
    |> List.sortBy _.EnvVar

// ─── Phase 700 — configuration profiles ──────────────────────────────
//
// The operator surface is ~180 keys. Almost none of them are chosen
// independently: a deployment behind a load balancer needs a
// cross-instance notification channel AND a cross-instance lock AND a
// shared rate-limit store, and the existing preflight expresses that
// coupling only NEGATIVELY — a refusal once you have got it wrong, plus
// an acceptance hatch to say you meant it.
//
// A profile is the positive-space twin: a named bundle stating a posture
// once, resolved one rung below the manifest, leaving the operator a
// profile name plus their genuine deltas. It is a CLAIM the preflight
// then checks, not a way around the preflight — see the header of
// `ConfigResolution` for the precedence and the reasoning behind it.
//
// **A profile carries no secrets, and the built-in set is written to
// make that visible rather than to work around it.** The manifest's
// refusal of secret keys applies here for the same reason and with more
// force: a bundle is shared across deployments, so a credential in one
// would be a credential in all of them. `production-multi-instance`
// therefore selects the Redis-backed channel and lock but cannot supply
// `TOOLUP_REDIS_CONNECTION`, and says so in `Requires` — a machine
// -checked field, not a sentence in a doc that can drift from the
// bundle beside it.

/// A named bundle of configuration keys — a posture a deployment can
/// import by name instead of restating key by key.
type ConfigProfile = {
    /// The name a manifest's `"$profile"` entry or `TOOLUP_PROFILE`
    /// selects. Matched case-insensitively, like every enum-valued
    /// reader; this spelling is the canonical one reports use.
    Name: string
    /// What posture this profile claims, in one line. Rendered into the
    /// generated reference, so it is operator-facing prose.
    Description: string
    /// Registered keys the operator must supply THEMSELVES for this
    /// posture to work — secrets and per-deployment values a shareable
    /// bundle structurally cannot carry. Declared rather than described
    /// so it can be checked: every entry must name a registered key.
    Requires: string list
    /// The bundle, in authored order (which is the order the generated
    /// reference renders). Keys are canonical env-var names; values are
    /// strings, so a reader parses a profile value exactly as it parses
    /// an environment one.
    Values: (string * string) list
}

/// The built-in profiles. Deliberately small — three postures that
/// recur in every deployment, each a combination the preflight already
/// has an opinion about. A fourth is a design question, not a
/// convenience: the value of the set is that a reader can hold all of it
/// in their head.
///
/// Each bundle states either a value that DIFFERS from the key's default
/// or one that IS the posture the name claims (`dev-single-instance`
/// naming its single-instance substrates, for instance). Restating a
/// default for any other reason is deliberately avoided — a profile
/// cannot enforce anything, because every rung above it wins, so a
/// default restated "for safety" is theatre that makes the bundle harder
/// to read.
let builtInProfiles: ConfigProfile list = [
    {
        Name = "dev-single-instance"
        Description =
            "A developer machine: one instance, in-process substrates, verbose logs and the /dev/* inspection endpoints open."
        Requires = []
        Values = [
            Names.replicaCount, "1"
            Names.notificationChannel, "inmemory"
            Names.distributedLock, "inprocess"
            Names.logLevel, "Debug"
            Names.enableDevEndpoints, "true"
        ]
    }
    {
        Name = "production-multi-instance"
        Description =
            "Several instances behind a load balancer: cross-instance channel, lock and rate-limit store, HTTPS enforced, hardened headers, structured logs."
        Requires = [ Names.redisConnection ]
        Values = [
            Names.replicaCount, "2"
            Names.notificationChannel, "redis"
            Names.distributedLock, "redis"
            Names.rateLimiter, "enabled"
            Names.rateLimitStore, "external"
            Names.requireHttps, "true"
            Names.securityHardening, "strict"
            Names.logFormat, "json"
        ]
    }
    {
        Name = "serverless"
        Description =
            "A serverless host with no long-lived background services: nothing in-process survives an invocation, so state that must outlive one is persisted."
        Requires = [ Names.blobStorage ]
        Values = [
            Names.serverlessHost, "serverless"
            Names.jobScheduler, "disabled"
            Names.eventStore, "persistent"
            Names.resultStore, "persistent"
            Names.logFormat, "json"
        ]
    }
]

/// The problems with `candidate`, measured against the registry and
/// against the profiles already `available`. Empty means it may be
/// declared.
///
/// Every rule here is one the loader already enforces for a manifest,
/// applied to a bundle instead — because a profile is the same
/// declarative act one rung lower, and a rule that held for the file but
/// not for the bundle would be a way around it. A secret in a profile is
/// worse than a secret in a manifest, not better: the bundle is shared
/// across deployments by design.
///
/// Reports EVERY problem rather than the first, so a consumer fixing a
/// profile declaration does it in one edit.
let profileProblems (available: ConfigProfile list) (candidate: ConfigProfile) : string list =
    let registered = all |> List.map _.EnvVar |> Set.ofList
    let secrets = all |> List.filter _.IsSecret |> List.map _.EnvVar |> Set.ofList

    let nameProblems =
        if System.String.IsNullOrWhiteSpace candidate.Name then
            [
                "a profile must have a name — it is how a manifest or TOOLUP_PROFILE selects it."
            ]
        elif candidate.Name.Trim() <> candidate.Name then
            [
                sprintf
                    "the profile name \"%s\" has leading or trailing whitespace, which no operator can type reliably."
                    candidate.Name
            ]
        else
            let lowered = candidate.Name.ToLowerInvariant()

            available
            |> List.filter (fun p -> p.Name.ToLowerInvariant() = lowered)
            |> List.map (fun p ->
                sprintf
                    "the profile name \"%s\" is already taken by \"%s\". Profile names are the selection key, so a duplicate would make which bundle a deployment imported depend on registration order."
                    candidate.Name
                    p.Name)

    let descriptionProblems =
        if System.String.IsNullOrWhiteSpace candidate.Description then
            [
                sprintf
                    "profile \"%s\" has no description. It is rendered into the generated configuration reference, where an operator chooses between profiles by reading it."
                    candidate.Name
            ]
        else
            []

    let requiresProblems =
        candidate.Requires
        |> List.filter (fun key -> not (Set.contains key registered))
        |> List.map (
            sprintf "profile \"%s\" declares that it requires %s, which is not a recognised config key." candidate.Name
        )

    let duplicateKeys =
        candidate.Values
        |> List.map fst
        |> List.groupBy id
        |> List.filter (fun (_, occurrences) -> List.length occurrences > 1)
        |> List.map (fun (key, _) ->
            sprintf
                "profile \"%s\" sets %s more than once, so which value it supplies is arbitrary."
                candidate.Name
                key)

    let valueProblems =
        candidate.Values
        |> List.collect (fun (key, value) ->
            if not (Set.contains key registered) then
                [
                    sprintf
                        "profile \"%s\" sets %s, which is not a recognised config key. Every profile key must be a documented TOOLUP_* variable."
                        candidate.Name
                        key
                ]
            elif Set.contains key secrets then
                [
                    sprintf
                        "profile \"%s\" sets %s, which is a secret. A profile is shared across deployments by design, so a credential in one is a credential in all of them — name it in Requires and let the operator set the environment variable."
                        candidate.Name
                        key
                ]
            elif key = Names.configFile || key = Names.profile then
                [
                    sprintf
                        "profile \"%s\" sets %s, which selects what to load and is therefore already resolved by the time the bundle is read."
                        candidate.Name
                        key
                ]
            elif not (isManifestBindable key) then
                [
                    sprintf
                        "profile \"%s\" sets %s, whose reader does not resolve through the config-resolution seam, so the value would never take effect."
                        candidate.Name
                        key
                ]
            elif System.String.IsNullOrEmpty value then
                [
                    sprintf
                        "profile \"%s\" sets %s to the empty string, which every reader treats as unset — remove the entry instead."
                        candidate.Name
                        key
                ]
            else
                [])

    nameProblems
    @ descriptionProblems
    @ requiresProblems
    @ duplicateKeys
    @ valueProblems

/// Project the registry to `docs/reference/config-reference.md`. Pure —
/// the same input always yields the same bytes, so the golden-file test
/// can compare the committed doc against a fresh render.
[<RequireQualifiedAccess>]
module ReferenceDoc =
    open System
    open System.Text

    let private typeLabel (t: ConfigKeyType) =
        match t with
        | StringKey -> "string"
        | BoolKey -> "bool"
        | IntKey -> "int"
        | EnumKey choices -> "enum: " + String.concat ", " choices

    /// Markdown-escape a cell value: pipes would break the table, and a
    /// `|` inside an enum/description is the only realistic offender.
    let private cell (s: string) = s.Replace("|", "\\|")

    /// Distinct categories in first-appearance order, so the doc's
    /// section order is stable and authored (not alphabetised).
    let private orderedCategories (keys: ConfigKeyDescriptor list) =
        keys
        |> List.fold
            (fun acc k ->
                if List.contains k.Category acc then
                    acc
                else
                    acc @ [ k.Category ])
            []

    /// Render the full reference document for `keys`. The header marks
    /// the file as generated; `Names`/`all` in `ConfigKeyDescriptor.fs`
    /// is the source of truth.
    let render (keys: ConfigKeyDescriptor list) : string =
        let sb = StringBuilder()

        sb.AppendLine "# Configuration reference" |> ignore
        sb.AppendLine "" |> ignore

        sb
            .AppendLine(
                "<!-- GENERATED FILE — do not edit by hand. Regenerate with `dev-scripts/generate-config-reference.ps1`"
            )
            .AppendLine(
                "     (or `TOOLUP_REGEN_CONFIG_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests`). The source"
            )
            .AppendLine(
                "     of truth is `ConfigKeys.all` in src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs. -->"
            )
        |> ignore

        sb.AppendLine "" |> ignore

        sb.AppendLine(
            sprintf
                "Every `TOOLUP_*` environment variable the SDK reads, projected from the central config-key registry (%d keys). Most are read at startup by `ServerConfig.fromEnv` or a companion's `create`; the \"Build & tooling\" section covers the few read by the build and analyzer instead. Run `--print-config` to see the effective resolved value and source of each on a running deployment, `--print-config --diff` for the non-default values only, or `--validate-config` to run the startup preflight without booting.\n\nThe **Manifest** column says whether a deployment configuration manifest may supply the key: `yes` (its reader resolves through the config-resolution seam), `pending` (registered, but its reader has not migrated yet — the manifest would state it and nothing would read it, so the loader warns), `never` (a secret; the manifest is refused outright, set the environment variable instead), `n/a` (the key is outside the manifest's reach altogether — a build/test/analyzer variable no running server reads, or one of the two variables that name what to load, `TOOLUP_CONFIG_FILE` and `TOOLUP_PROFILE`). Precedence is consumer literal > environment variable > manifest > profile > override record > default."
                keys.Length
        )
        |> ignore

        sb.AppendLine "" |> ignore

        // The schema is generated from this same registry and lists
        // exactly the keys the Manifest column marks `yes`, so it belongs
        // beside that paragraph rather than in a section of its own.
        sb.AppendLine(
            sprintf
                "A manifest can be validated **as it is typed**: [`toolup.config.schema.json`](toolup.config.schema.json) beside this file is generated from the same registry and carries exactly the keys marked `yes` above, with `additionalProperties: false` — so an unknown key, a secret key, a `pending` key and an out-of-enum value are all flagged in the editor rather than at boot. Point at it from the top of the manifest:\n\n```json\n{\n  \"%s\": \"./toolup.config.schema.json\",\n  \"TOOLUP_PLATFORM_SURFACES\": \"team\"\n}\n```\n\nThe schema pointer and `%s` are the only non-registry properties the loader tolerates; neither is bound to a config key."
                ManifestSchemaProperty
                ManifestProfileProperty
        )
        |> ignore

        sb.AppendLine "" |> ignore

        // Phase 700 — the profiles, rendered from the same registry data
        // as everything else above. A bundle stated in a doc and defined
        // in code would be two sources of truth for one fact; this is the
        // projection of the definition.
        sb.AppendLine "## Configuration profiles" |> ignore
        sb.AppendLine "" |> ignore

        sb.AppendLine(
            sprintf
                "A **profile** is a named bundle of the keys above, resolved one rung below the manifest — so importing a posture never takes a setting away from the deployment that imported it, and any explicit environment or manifest line still wins. Select one with a `\"%s\"` entry in the manifest (which takes precedence) or with the `%s` environment variable; an unrecognised name refuses startup and lists the available profiles.\n\nA profile is a *claim*, not a bypass. Its values reach every reader through the same resolution seam an environment variable does, so the startup preflight validates the resolved combination exactly as if each key had been typed by hand — and a refusal names the profile in force. `--print-config` labels each value it supplied `profile:<name>`.\n\nNo profile carries a secret: a bundle is shared across deployments by design, so a credential in one would be a credential in all of them. Where a posture depends on one, the profile says which variable the operator must set themselves under **Requires**.\n\nA consumer registers its own profiles before building the logger; the %d below ship with the SDK."
                ManifestProfileProperty
                Names.profile
                builtInProfiles.Length
        )
        |> ignore

        sb.AppendLine "" |> ignore

        for p in builtInProfiles do
            sb.AppendLine(sprintf "### `%s`" p.Name) |> ignore
            sb.AppendLine "" |> ignore
            sb.AppendLine(cell p.Description) |> ignore
            sb.AppendLine "" |> ignore

            match p.Requires with
            | [] -> ()
            | required ->
                sb.AppendLine(
                    sprintf
                        "**Requires** (set these yourself — a profile cannot carry them): %s"
                        (required |> List.map (sprintf "`%s`") |> String.concat ", ")
                )
                |> ignore

                sb.AppendLine "" |> ignore

            sb.AppendLine "| Env var | Value |" |> ignore
            sb.AppendLine "|---|---|" |> ignore

            for key, value in p.Values do
                sb.AppendLine(sprintf "| `%s` | `%s` |" key (cell value)) |> ignore

            sb.AppendLine "" |> ignore

        for category in orderedCategories keys do
            sb.AppendLine(sprintf "## %s" category) |> ignore
            sb.AppendLine "" |> ignore

            // The tooling classification, rendered where it is acted on.
            // These keys belong to the build, the test run and the
            // analyzer, so the startup unknown-key guard deliberately
            // never reports them — an operator reading a preflight
            // warning needs to know which names are out of its scope.
            if category = ToolingCategory then
                sb.AppendLine(
                    "These keys are read by the build, the test run or the analyzer, never by a running server. The startup unknown-key preflight guard classifies them as tooling and never reports them, so a development machine that has run a build or a test pack does not warn on its own leftovers."
                )
                |> ignore

                sb.AppendLine "" |> ignore

            sb.AppendLine "| Env var | Type | Default | Secret | Manifest | Description |"
            |> ignore

            sb.AppendLine "|---|---|---|---|---|---|" |> ignore

            let inCategory =
                keys |> List.filter (fun k -> k.Category = category) |> List.sortBy _.EnvVar

            for k in inCategory do
                let defaultCell =
                    match k.Default with
                    | Some d -> cell d
                    | None -> "—"

                let secretCell = if k.IsSecret then "yes" else "no"

                // A secret key can never appear in a manifest (the file's
                // value IS that it is shareable and committable), so the
                // column says "never" rather than "no" — the two are
                // different facts and an operator reading "no" would
                // reasonably wait for a migration that will never come.
                // Phase 698 — a TOOLING key reads `n/a`, not `pending`.
                // `pending` promises a reader that will migrate; for a key
                // the build, the test run or the analyzer reads and no
                // server ever does, no such reader exists and the promise
                // is simply false. The sweep that closed `pending` for the
                // Platform-side keys is what made the distinction visible:
                // what remained was not a queue of unmigrated readers but
                // three different reasons a key cannot be declared.
                let manifestCell =
                    if k.EnvVar = Names.configFile || k.EnvVar = Names.profile then
                        "n/a"
                    elif k.IsSecret then
                        "never"
                    elif isManifestBindable k.EnvVar then
                        "yes"
                    elif isToolingKey k.EnvVar then
                        "n/a"
                    else
                        "pending"

                sb.AppendLine(
                    sprintf
                        "| `%s` | %s | %s | %s | %s | %s |"
                        k.EnvVar
                        (cell (typeLabel k.Type))
                        defaultCell
                        secretCell
                        manifestCell
                        (cell k.Description)
                )
                |> ignore

            sb.AppendLine "" |> ignore

        // Normalise to `\n` so the golden-file comparison is stable
        // across platforms (StringBuilder.AppendLine emits the platform
        // newline; the committed file is `\n`).
        sb.ToString().Replace("\r\n", "\n")

/// Project the registry to `docs/reference/toolup.config.schema.json` —
/// the JSON Schema an editor validates a deployment configuration
/// manifest against while it is being typed. Pure, like `ReferenceDoc`:
/// the same registry always yields the same bytes, so the golden-file
/// test can compare the committed schema against a fresh render.
///
/// **It describes what a valid manifest may contain today, not the whole
/// registry.** A secret key is refused by the loader outright, and a
/// registered key whose reader has not migrated to the resolution seam is
/// accepted-and-warned rather than honoured — so both are omitted, and
/// `additionalProperties: false` reports either at edit time with the same
/// finality the loader has at boot. The property set therefore GROWS as
/// readers migrate; the golden-file test is what keeps the committed copy
/// level with the registry rather than lagging it.
///
/// The `enum` clauses carry each descriptor's declared choices, which are
/// the **canonical** spellings rather than every accepted one: several
/// readers additionally take a hyphenated or abbreviated alias no
/// descriptor lists (`in-memory`, `all-in-one`, `admin`), and all of them
/// match case-insensitively. An editor therefore flags a
/// working-but-undocumented spelling. That is the intended reading — the
/// documented value is the one to write — and the schema's own description
/// says so, because an operator meeting a squiggle on a file that boots
/// needs to be told which of the two is wrong.
[<RequireQualifiedAccess>]
module ConfigSchema =
    open System
    open System.Text

    /// The JSON Schema dialect the generated document declares. 2020-12 is
    /// the current draft and the one editors' bundled validators default
    /// to; it is the schema's own `$schema`, not the manifest's.
    [<Literal>]
    let Dialect = "https://json-schema.org/draft/2020-12/schema"

    /// JSON string escaping. Hand-rolled rather than reached for from a
    /// serialiser because this module sits in the Fable-packed tier and
    /// must build its output from strings alone.
    let private escape (s: string) =
        let sb = StringBuilder()

        for ch in s do
            match ch with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString()

    let private str (s: string) = "\"" + escape s + "\""

    /// `true` when `raw` is a JSON-legal integer literal. `Int32.TryParse`
    /// is more permissive than JSON — a leading `+`, leading zeros and
    /// surrounding whitespace all parse for a reader but are not legal
    /// JSON numbers — so a default in any of those shapes is omitted
    /// rather than emitted as a document no validator can load.
    let private isJsonInteger (raw: string) =
        let body = if raw.StartsWith "-" then raw.Substring 1 else raw

        body.Length > 0
        && body |> Seq.forall Char.IsDigit
        && (body = "0" || not (body.StartsWith "0"))

    /// The JSON boolean a descriptor's default text denotes, when it
    /// denotes one. The recognised token set is the readers' own
    /// (`1` / `true` / `yes` / `on` and their negatives).
    let private jsonBool (raw: string) =
        match raw.ToLowerInvariant() with
        | "1"
        | "true"
        | "yes"
        | "on" -> Some "true"
        | "0"
        | "false"
        | "no"
        | "off" -> Some "false"
        | _ -> None

    /// The instance constraint for a key's value.
    ///
    /// Bool and int keys accept **both** the natural JSON scalar and a
    /// string, because that is what the loader accepts: it renders every
    /// scalar back to the string an environment variable would have
    /// carried, so `true` and `"true"` reach the reader identically. The
    /// `pattern` beside an int constrains only the string arm — a
    /// `pattern` keyword ignores every non-string instance — so `3` stays
    /// valid while `"3.5"` and `"soon"` do not.
    let private typeClauses (t: ConfigKeyType) =
        match t with
        | StringKey -> [ "\"type\": \"string\"" ]
        | BoolKey -> [ "\"type\": [ \"boolean\", \"string\" ]" ]
        | IntKey -> [ "\"type\": [ \"integer\", \"string\" ]"; "\"pattern\": \"^-?[0-9]+$\"" ]
        | EnumKey choices -> [
            "\"type\": \"string\""
            "\"enum\": [ " + (choices |> List.map str |> String.concat ", ") + " ]"
          ]

    /// The descriptor's default, carried over **only when it is a valid
    /// instance of the property's own schema**.
    ///
    /// `Default` is documentation text, not always a value: a key that is
    /// simply unset until set carries prose (`(unset — probes
    /// ./toolup.config.json)`). In a reference table that reads correctly;
    /// in a schema a `default` is what an editor offers to insert, so a
    /// prose default would be a suggestion that refuses the moment it is
    /// accepted. Anything that does not round-trip — a non-token bool, a
    /// non-JSON integer, a value outside the enum, a parenthesised
    /// annotation — is dropped, and the reference table remains the place
    /// the prose is read.
    let private defaultClauses (k: ConfigKeyDescriptor) =
        match k.Default with
        | None -> []
        | Some raw ->
            let trimmed = raw.Trim()

            match k.Type with
            | BoolKey -> jsonBool trimmed |> Option.map (fun b -> "\"default\": " + b) |> Option.toList
            | IntKey ->
                if isJsonInteger trimmed then
                    [ "\"default\": " + trimmed ]
                else
                    []
            | EnumKey choices ->
                choices
                |> List.tryFind (fun c -> c.ToLowerInvariant() = trimmed.ToLowerInvariant())
                |> Option.map (fun c -> "\"default\": " + str c)
                |> Option.toList
            | StringKey ->
                if trimmed.StartsWith "(" then
                    []
                else
                    [ "\"default\": " + str trimmed ]

    let private block (indent: string) (name: string) (clauses: string list) =
        let inner = indent + "  "
        let body = clauses |> List.map (fun c -> inner + c) |> String.concat ",\n"
        indent + str name + ": {\n" + body + "\n" + indent + "}"

    /// Render the schema for `keys`. Only the manifest-bindable,
    /// non-secret subset appears; properties are ordered by name so the
    /// committed file diffs by the key that changed.
    let render (keys: ConfigKeyDescriptor list) : string =
        let bindable =
            keys
            |> List.filter (fun k -> not k.IsSecret && isManifestBindable k.EnvVar)
            |> List.sortBy _.EnvVar

        let schemaProperty =
            block "    " ManifestSchemaProperty [
                "\"type\": \"string\""
                "\"description\": "
                + str
                    "Optional pointer to this schema, so an editor validates the manifest as it is typed. Tolerated by the loader and never bound to a config key."
            ]

        // Phase 700 — the second tolerated non-registry property.
        // `additionalProperties: false` means an omitted property is an
        // editor error, so the schema has to admit `$profile` or it would
        // flag the very selection the reference doc tells operators to
        // write. Deliberately NOT an `enum` of the built-in names: a
        // consumer may declare its own profiles, and a schema that
        // refused those would be wrong on the file it was generated for.
        let profileProperty =
            block "    " ManifestProfileProperty [
                "\"type\": \"string\""
                "\"description\": "
                + str (
                    sprintf
                        "Name of the configuration profile this manifest imports — a named bundle resolved one rung below the manifest, so every key set here still wins over it. Ships with: %s. A consumer may declare further profiles, so any name is accepted here; an unrecognised one refuses startup and lists what is available."
                        (builtInProfiles |> List.map _.Name |> String.concat ", ")
                )
            ]

        let keyProperties =
            bindable
            |> List.map (fun k ->
                block
                    "    "
                    k.EnvVar
                    ([ "\"description\": " + str k.Description ]
                     @ typeClauses k.Type
                     @ defaultClauses k))

        let properties =
            (schemaProperty :: profileProperty :: keyProperties) |> String.concat ",\n"

        let description =
            sprintf
                "A ToolUp deployment configuration manifest: one JSON file of canonical TOOLUP_* keys, resolved one rung below the environment, optionally importing a named configuration profile via $profile (which resolves one rung below the manifest, so every key set here beats it). It lists the %d keys a manifest may set today — a secret key is refused outright (set the environment variable instead), and a registered key whose reader has not migrated to the config-resolution seam is omitted until it can be honoured. Enum values are the documented canonical spellings; readers also accept them case-insensitively and take some undocumented aliases, so a flagged value may still boot — write the documented one. Full descriptions, defaults and per-key manifest status: docs/reference/config-reference.md."
                bindable.Length

        let sb = StringBuilder()

        sb.Append("{\n") |> ignore
        sb.Append("  \"$schema\": " + str Dialect + ",\n") |> ignore

        sb.Append(
            "  \"$comment\": "
            + str
                "GENERATED FILE — do not edit by hand. Regenerate with dev-scripts/generate-config-reference.ps1. The source of truth is ConfigKeys.all in src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs."
            + ",\n"
        )
        |> ignore

        sb.Append("  \"title\": " + str "ToolUp deployment configuration manifest" + ",\n")
        |> ignore

        sb.Append("  \"description\": " + str description + ",\n") |> ignore
        sb.Append("  \"type\": \"object\",\n") |> ignore
        sb.Append("  \"additionalProperties\": false,\n") |> ignore
        sb.Append("  \"properties\": {\n") |> ignore
        sb.Append(properties) |> ignore
        sb.Append("\n  }\n") |> ignore
        sb.Append("}\n") |> ignore

        // `\n` throughout by construction (no `AppendLine`), so the
        // committed bytes are identical on every platform.
        sb.ToString()