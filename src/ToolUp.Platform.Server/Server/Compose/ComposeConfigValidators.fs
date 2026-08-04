module ToolUp.Platform.ComposeConfigValidators

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption

// ─── compose phase: first-party config + health validators ───────────
//
// First-party config validators. `ConfigValidatorAggregator` (called
// near end-of-compose) walks every `AddSingleton<IConfigValidator>`
// registration, runs each in parallel, and aborts startup if any
// returns `Error`. Registration order is preserved (it determines the
// order messages appear in the preflight summary) — expressed as one
// call per validator via `addConfigValidator` rather than ~25 near-
// identical four-line `AddSingleton` blocks.
//
// Sequencing around the one interleaved health-check registration is
// kept exact: the audit-chain durability probe sits BETWEEN
// `AuditLogModeValidator` and `EncryptedSecretStoreModeValidator` —
// preserve that ordering or the dev report's interleave changes.
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and registers in the same order. Zero behaviour change.

/// Register the first-party `IConfigValidator` set plus the one
/// interleaved audit-chain durability `IHealthCheck`. Order is
/// load-bearing — preserves the inline `compose` body exactly.
let registerFirstPartyConfigValidators
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (secretStore: Secrets.ISecretStore)
    (auth: IAuthProvider)
    (auditLog: IAuditLog)
    (eventStore: IEventStore)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    (configValidators: ConfigValidation.IConfigValidator list)
    : unit =

    let addConfigValidator (v: #ConfigValidation.IConfigValidator) =
        services.AddSingleton<ConfigValidation.IConfigValidator>(v :> ConfigValidation.IConfigValidator)
        |> ignore

    addConfigValidator (ConfigValidator.BlobStorageValidator(resolvedBlobStorage)) // blob storage reachable
    addConfigValidator (BlobStorageSelectionValidator.BlobStorageSelectionValidator(resolvedBlobStorage)) // refuse cloud-declared backend that silently fell back to LocalFileStorage
    addConfigValidator (ConfigValidator.SecretStoreValidator(secretStore)) // secret store reachable
    addConfigValidator (OidcConfigCompletenessValidator.OidcConfigCompletenessValidator()) // refuse auth=oidc with unset issuer; ordered before HeaderAuth so its message lands earlier
    addConfigValidator (OidcAudienceBindingValidator.OidcAudienceBindingValidator(config)) // refuse auth=oidc with unbound audience in authenticated modes (token-reuse; escape hatch)
    addConfigValidator (HeaderAuthProviderModeValidator.HeaderAuthProviderModeValidator(config, auth)) // refuse HeaderAuthProvider in authenticated modes (mTLS escape hatch)
    addConfigValidator (InviteEmailCapabilityValidator.InviteEmailCapabilityValidator(config, services)) // Phase 247 — warn team invite-by-email surface mounted in an auth mode with no IUserDirectory (emails silently never send; acknowledgement knob)
    addConfigValidator (AutoBootstrapDevAdminModeValidator.AutoBootstrapDevAdminModeValidator(config)) // warn (Error on internet-facing) AutoBootstrapDevAdmin left set in an auth-requiring mode (first sign-in becomes Platform Admin)
    addConfigValidator (CsrfDefaultModeValidator.CsrfDefaultModeValidator(config)) // warn cookie auth (SseAuthMode=CookieRequired) without server-side CSRF (NoSecurityHardening)
    addConfigValidator (AuditLogModeValidator.AuditLogModeValidator(config)) // warn authenticated mode + NoAuditLog
    addConfigValidator (ServiceStatusBoardDepsValidator.ServiceStatusBoardDepsValidator(config)) // warn ServiceStatusBoard composes only disabled substrates (Phase 9p.A)

    // Audit chain durability probe (health check, kept interleaved here
    // exactly as before so IHealthCheck registration order is unchanged).
    // Writes a marker via IEventStore under _platform.audit_health, reads
    // back. NoOpAuditLog deployments report Healthy (configured off, not
    // broken).
    services.AddSingleton<HealthChecks.IHealthCheck>(
        AuditLogHealthCheck.AuditLogHealthCheck(auditLog, eventStore) :> HealthChecks.IHealthCheck
    )
    |> ignore

    addConfigValidator (EncryptedSecretStoreModeValidator.EncryptedSecretStoreModeValidator(config, secretStore)) // refuse plaintext secrets in authenticated modes (KMS/FDE escape hatch)
    addConfigValidator (JobSchedulerInstanceValidator.JobSchedulerInstanceValidator(config)) // refuse InProcessJobScheduler in multi-instance deployments
    addConfigValidator (DeployPlaneDepsValidator.DeployPlaneDepsValidator(config, services)) // warn SingleNodeDeployPlane with IJobScheduler / IEntityStore / IContainerScheduler unregistered (else first-request 500 when the affected service resolves)
    addConfigValidator (SignedExportDepsValidator.SignedExportDepsValidator(config, services)) // Phase 162 — refuse DataSubjectRequests SignExports=true with no IExportEnvelopeSigner composed
    addConfigValidator (PublicBaseUrlFormatValidator.PublicBaseUrlFormatValidator(config)) // warn PublicBaseUrl set to a non-absolute-http(s) URL (breaks sitemap loc / canonical / share-token redirects)
    addConfigValidator (NotificationChannelInstanceValidator.NotificationChannelInstanceValidator(config)) // warn in-memory notification channel under multi-instance
    addConfigValidator (MultiInstanceAdminCoherenceValidator.MultiInstanceAdminCoherenceValidator(config)) // Phase 236 — warn in-process admin subsystems (admin store / permission store / webhook dispatcher / DSR preview cache) under multi-instance
    addConfigValidator (RateLimitModeValidator.RateLimitModeValidator(config)) // warn internet-facing authenticated deployment with no rate-limiting
    addConfigValidator (RateLimitModeValidator.AdAnalyticsRateLimitValidator(config)) // warn anonymous ad-analytics ingest enabled without an IRateLimitStore
    addConfigValidator (RateLimitConfigValidator.RateLimitConfigValidator(config)) // range-check ServerConfig.RateLimit (out-of-range = cannot serve)
    addConfigValidator (RateLimiterInstanceValidator.RateLimiterInstanceValidator(config)) // warn in-process/in-memory rate limiters under multi-instance (effective limit is N×)
    addConfigValidator (SessionFileStoreInstanceValidator.SessionFileStoreInstanceValidator(config)) // warn in-memory SessionFileStore under multi-instance + persistent surface (per-replica dictionaries drift from the shared store)
    addConfigValidator (IdempotencyStoreInstanceValidator.IdempotencyStoreInstanceValidator(config, services)) // warn DI-registered in-memory InMemoryIdempotencyStore under multi-instance (per-instance cache lost across replicas → handler re-execution; steer to distributed BlobIdempotencyStore / CAS backend)
    addConfigValidator (SseAuthModeValidator.SseAuthModeValidator(config)) // refuse SseAuthMode = QueryParamFallback in authenticated modes
    addConfigValidator (SecurityHeadersValidator.SecurityHeadersValidator(config)) // warn internet-facing auth-mode deployment with no security headers
    addConfigValidator (SecurityHeadersValidator.CspNonceCacheValidator(services)) // Phase 156 — warn nonce CSP source mode composed with a registered IRenderCache (stale-nonce-on-cache-hit)
    addConfigValidator (StaticPathBehaviourValidator.StaticPathBehaviourValidator(config)) // warn dev StaticPathBehaviour in a production-shaped deployment
    addConfigValidator (PeerBearerConfigValidator.PeerBearerConfigValidator(config, secretStore)) // warn PeerRoutePrefixes set but no peer bearer secrets seeded
    addConfigValidator (MaxRequestBodyBytesValidator.MaxRequestBodyBytesValidator(config)) // warn high request-body cap + no rate-limit (memory-DoS surface)
    addConfigValidator (CorsConfigValidator.CorsConfigValidator(config)) // refuse AllowCredentials + wildcard-origin CORS
    addConfigValidator (ForwardedHeadersTrustValidator.ForwardedHeadersTrustValidator(config)) // Phase 325 — refuse unscoped TrustForwardedHeaders (empty TrustedProxyCidrs, no escape hatch) in auth modes; warn anonymous-only; refuse malformed CIDRs
    addConfigValidator (CsrfHardeningValidator.CsrfHardeningValidator(config)) // warn hardening + PublicBaseUrl: split-origin SPA must call CsrfClient.setApiOrigin
    addConfigValidator (LocalSecretFilePermissionsValidator.LocalSecretFilePermissionsValidator()) // probe working-dir secret files for permissive Unix modes
    addConfigValidator (LocalStorageEncryptionValidator.LocalFileStorageEncryptionAtRestValidator(resolvedBlobStorage)) // warn local blob storage that is not encrypted-at-rest
    addConfigValidator (AdminTokenValidator.AdminTokenValidator(encryptionKeyResolver)) // warn crypto-shred admin endpoint mounted but TOOLUP_ADMIN_TOKEN unset

    addConfigValidator (
        PerScopeKeyResolverDistributedValidator.PerScopeKeyResolverDistributedValidator(
            config,
            encryptionKeyResolver,
            services
        )
    ) // refuse per-scope key resolver with an unwired or in-process channel under declared multi-instance (Phase 458 — the replica count now reads ServerConfig.ReplicaCount as well as TOOLUP_REPLICA_COUNT, and the channel is probed from DI as well as the env var)

    addConfigValidator (KeyDestroyAckCoverageValidator.KeyDestroyAckCoverageValidator(config, services)) // Phase 22b — warn per-scope key resolver + in-proc channel in a Team/MultiTeam shape (crypto-shred fanout reaches no sibling replica; the Error arm above only fires when the replica count is declared)

    addConfigValidator (
        ShareTokenSigningKeyProvenanceValidator.ShareTokenSigningKeyProvenanceValidator(config, secretStore)
    ) // warn share-token signing key would auto-generate (unmanaged) in a production/multi-instance share-token deployment (Wave 19; self-gates to Ok otherwise)

    addConfigValidator (DataProtectionBackendValidator.DataProtectionBackendValidator(resolvedBlobStorage)) // Phase 329 — refuse a misconfigured/unreachable DataProtection key-ring backend (silent ephemeral-key boot → cross-replica CSRF seal failure); security-class

    addConfigValidator (DataObjectOrphanSweep.DataObjectOrphanSweepConfiguredValidator(config, services)) // Phase 7c — warn when a persistent deployment composes no data-object orphan sweep (Save writes content before metadata, so a crash between them strands objects/_content/{hash}.data forever — invisible to subject erasure), or composes one that JobScheduler = NoJobScheduler can never fire

    // Companion-contributed `IConfigValidator` instances (OIDC, Redis,
    // SMTP), wired through `ServerApp.withConfigValidator`. Registered
    // after the first-party set so their preflight messages follow.
    configValidators |> List.iter addConfigValidator