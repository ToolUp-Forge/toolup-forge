// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.Narrative

// Phase 347 — carved out of SDK.Shared.fs: the `ServerConfig` companion module —
// `defaults` (Fable-visible) and the server-only `fromEnv` binder with its
// private env parsers (`#if !FABLE_COMPILER`). The record itself stays in
// SDK.Shared.fs; the explicit `ModuleSuffix` keeps the compiled name
// `ServerConfigModule` now that the type and its module sit in different files.
// Same namespace, same names — only the file boundary moved.

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
    }

// ─── Phase 11.G — env-var-driven config construction ──────────
//
// Server-only. Fable can't compile `System.Environment.GetEnvironmentVariable`;
// wrapping in `#if !FABLE_COMPILER` keeps the helpers usable from
// server composition roots while leaving the Fable client to see
// only the `ServerConfig` type + `defaults` value above.

#if !FABLE_COMPILER
    /// Phase 696 — every key this reader consults now resolves through the
    /// `ConfigResolution` seam rather than reading the environment
    /// directly, so a deployment configuration manifest can supply the
    /// declared base and the environment stays the per-instance override
    /// lane. The seam's env arm folds null/empty to `None` exactly as this
    /// helper always did, so with no manifest installed — the ordinary
    /// state — `fromEnv` resolves byte-for-byte as before (GP 11).
    ///
    /// This one helper is why the whole Phase 71.A cluster (87 keys)
    /// migrates in a single change: the ~40 private parsers below all
    /// funnel through it. `ConfigKeys.manifestBindable` declares that set,
    /// and the coverage test holds the declaration to this call graph.
    let private envVar (name: string) = ConfigResolution.tryValue name

    let private envFlag (name: string) =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | Some("1" | "true" | "yes" | "on") -> true
        | _ -> false

    /// Phase 16d — parse a boolean env var with an explicit
    /// `defaultWhenMissing` and **fail loud** on any unrecognised
    /// value. Used for flags whose silently-wrong values are dangerous
    /// (forwarded-headers trust silently off in a containerised deploy
    /// misreports client IPs and breaks HTTPS redirects). Missing →
    /// `defaultWhenMissing`. Recognised: `1` / `true` / `yes` / `on`
    /// → `true`; `0` / `false` / `no` / `off` → `false` (all case-
    /// insensitive). Any other value throws at startup, mirroring the
    /// `SERVER_PORT` fail-fast pattern in `SDK.Server.compose` — names
    /// the offending value and points at the recognised set.
    let private envFlagOrFail (name: string) (defaultWhenMissing: bool) =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> defaultWhenMissing
        | Some("1" | "true" | "yes" | "on") -> true
        | Some("0" | "false" | "no" | "off") -> false
        | Some other ->
            failwithf
                "%s=%s is not a recognised boolean value. Expected one of: 1, true, yes, on (case-insensitive) → on; 0, false, no, off → off. Unset the variable to use the default (%b)."
                name
                other
                defaultWhenMissing

    /// Phase 66 Stream A.8 — parse a single token from
    /// `TOOLUP_PLATFORM_SURFACES` into a `SurfaceProfile`. Accepts
    /// the canonical lowercase form plus a couple of separator
    /// tolerant aliases (`multi-team` / `multi_team` / `multiteam`).
    /// Returns `Error <raw>` for unrecognised tokens so the caller
    /// can surface a clear list of bad entries.
    let private parseSurfaceProfile (raw: string) : Result<SurfaceProfile, string> =
        match raw with
        | "anonymous" -> Ok SurfaceProfile.anonymous
        | "anonymous_persistent"
        | "anonymous-persistent"
        | "anonymouspersistent" -> Ok SurfaceProfile.anonymousPersistent
        | "trial"
        | "authephemeral"
        | "auth-ephemeral"
        | "auth_ephemeral" -> Ok SurfaceProfile.trial
        | "individual" -> Ok SurfaceProfile.individual
        | "team" -> Ok SurfaceProfile.team
        | "multiteam"
        | "multi-team"
        | "multi_team" -> Ok SurfaceProfile.multiTeam
        | "claimbearer"
        | "claim-bearer"
        | "claim_bearer" -> Ok SurfaceProfile.claimBearer
        | other -> Error other

    let private parseStaticPathBehaviour (logger: ILogger) =
        match envVar ConfigKeys.Names.staticPathBehaviour |> Option.map _.ToLowerInvariant() with
        | Some "warn"
        | None -> Warn
        | Some("require" | "requireexist" | "require-exist") -> RequireExist
        | Some("skip" | "skipsilent" | "skip-silent") -> SkipSilent
        | Some other ->
            logger.Warn
                $"TOOLUP_STATIC_PATH_BEHAVIOUR={other} not recognised. Valid: warn, require, skip. Falling back to Warn."

            Warn

    /// Phase 719 — `cookies` and `cookieonly` select the cookie mode here
    /// as well as at `AuthProviderFromEnv.tokenLocationFromEnv`.
    ///
    /// Both readers consult `TOOLUP_SSE_AUTH` and the registry has
    /// declared all three cookie spellings since the key was registered,
    /// but only the auth-provider reader honoured the two aliases: a
    /// deployment setting `TOOLUP_SSE_AUTH=cookies` got a provider that
    /// accepts the cookie AND an SSE mode left at `QueryParamFallback` —
    /// the mode `SseAuthModeValidator` refuses under an auth surface,
    /// reached through a spelling the reference doc offered. Two readers
    /// of one key must agree on what the key says.
    let private parseSseAuthMode (logger: ILogger) =
        match envVar ConfigKeys.Names.sseAuth |> Option.map _.ToLowerInvariant() with
        | Some "cookie"
        | Some "cookies"
        | Some "cookieonly" -> CookieRequired
        | Some "fallback"
        | Some "queryparam"
        | None -> QueryParamFallback
        | Some other ->
            logger.Warn
                $"TOOLUP_SSE_AUTH={other} not recognised. Valid values: cookie (or cookies / cookieonly), fallback (or queryparam). Falling back to fallback (default)."

            QueryParamFallback

    let private parseAuthCookieIssuance (logger: ILogger) =
        match envVar ConfigKeys.Names.authCookieIssuance |> Option.map _.ToLowerInvariant() with
        | Some "enabled"
        | Some "on"
        | Some "1" -> EnabledAuthCookieIssuance
        | Some "disabled"
        | Some "off"
        | Some "0"
        | None -> NoAuthCookieIssuance
        | Some other ->
            logger.Warn
                $"TOOLUP_AUTH_COOKIE_ISSUANCE={other} not recognised. Valid values: enabled, disabled. Falling back to disabled (default)."

            NoAuthCookieIssuance

    let private parseReplicaCount (logger: ILogger) =
        match envVar ConfigKeys.Names.replicaCount with
        | None -> 1
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> n
            | _ ->
                logger.Warn $"TOOLUP_REPLICA_COUNT={raw} not a positive integer. Defaulting to 1."
                1

    let private parseEphemeralStoreEvictionMinutes (logger: ILogger) =
        match envVar ConfigKeys.Names.storeEvictionMinutes with
        | None -> defaults.EphemeralStoreEvictionMinutes
        | Some raw ->
            match Double.TryParse raw with
            | true, n when n > 0.0 -> n
            | _ ->
                logger.Warn $"TOOLUP_STORE_EVICTION_MINUTES={raw} not a positive number. Using default 60."
                defaults.EphemeralStoreEvictionMinutes

    let private parseRateLimit (logger: ILogger) =
        let parsePositive (name: string) =
            match envVar name with
            | None -> None
            | Some raw ->
                match Int32.TryParse raw with
                | true, n when n > 0 -> Some n
                | _ ->
                    logger.Warn $"{name}={raw} not a positive integer. Rate limit disabled."
                    None

        // Phase 66 Stream C.3 — the env-var path configures a single
        // uniform policy (one limit for every subject kind). Per-shape
        // overrides are a code-level concern (`RateLimitConfig.perShape`
        // / `.withOverrides`), not expressible via three scalar env vars.
        match
            parsePositive ConfigKeys.Names.rateLimitPermits,
            parsePositive ConfigKeys.Names.rateLimitWindowSeconds,
            parsePositive ConfigKeys.Names.rateLimitQueue
        with
        | Some permits, Some windowSeconds, Some queue ->
            RateLimitConfig.uniform {
                PermitLimit = permits
                WindowSeconds = windowSeconds
                QueueLimit = queue
            }
        | None, None, None -> RateLimitConfig.none
        | _ ->
            logger.Warn
                "Rate limit requires all three of TOOLUP_RATE_LIMIT_PERMITS / _WINDOW_SECONDS / _QUEUE. Partial configuration ignored — rate limit disabled."

            RateLimitConfig.none

    let private parseDefaultTeamStorageQuotaBytes (logger: ILogger) =
        match envVar ConfigKeys.Names.defaultStorageQuotaBytes with
        | None -> defaults.DefaultTeamStorageQuotaBytes
        | Some "none"
        | Some "0" -> None
        | Some raw ->
            match Int64.TryParse raw with
            | true, n when n > 0L -> Some n
            | _ ->
                logger.Warn $"TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES={raw} not a positive integer or 'none'. Using default."

                defaults.DefaultTeamStorageQuotaBytes

    let private parseSlowRequestThreshold (logger: ILogger) =
        match envVar ConfigKeys.Names.slowRequestMs with
        | None -> defaults.SlowRequestThreshold
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> TimeSpan.FromMilliseconds(float n)
            | _ ->
                logger.Warn $"TOOLUP_SLOW_REQUEST_MS={raw} not a positive integer. Using default 1000ms."
                defaults.SlowRequestThreshold

    let private parseMaxSseConnectionsPerScope (logger: ILogger) =
        match envVar ConfigKeys.Names.maxSseConnectionsPerScope with
        | None -> defaults.MaxSseConnectionsPerScope
        | Some "none"
        | Some "0" -> None
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> Some n
            | _ ->
                logger.Warn
                    $"TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE={raw} not a positive integer or 'none'. Using default."

                defaults.MaxSseConnectionsPerScope

    let private parseLogLevel () : LogLevel * Set<string> =
        let level =
            match envVar ConfigKeys.Names.logLevel with
            | None -> LogLevel.Info
            | Some raw ->
                match LogLevel.tryParse raw with
                | Some lvl -> lvl
                | None ->
                    // Bootstrap problem — the SDK's `fromEnv` helpers
                    // emit this warning via `eprintfn` because the
                    // logger they'd use is constructed from the same
                    // env var. `ServerConfig.fromEnv` is called AFTER
                    // the logger is built, so it could use it here,
                    // but using `eprintfn` keeps the warning surfacing
                    // identical to `ConsoleLogger.envSettings`. Worth
                    // it: an operator misreading TOOLUP_LOG_LEVEL once
                    // shouldn't see the warning twice.
                    eprintfn $"[WRN] TOOLUP_LOG_LEVEL={raw} not recognised. Using Info."
                    LogLevel.Info

        let categories =
            match envVar ConfigKeys.Names.traceCategories with
            | None -> Set.empty
            | Some raw ->
                raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map _.Trim()
                |> Array.filter (fun s -> s <> "")
                |> Set.ofArray

        level, categories

    /// Phase 71.A.3 — `SERVER_PORT` read inside the `fromEnv` seam so a
    /// `/dev/inspect` config snapshot reflects the actually-bound port
    /// (previously `Port` stayed at `defaults.Port` because the only
    /// read lived in `SDK.Server.compose`). Non-integer / out-of-range
    /// → fail loud, mirroring the compose-time guard. Unset → default.
    let private parseServerPort () : int =
        match envVar "SERVER_PORT" with
        | None -> defaults.Port
        | Some raw ->
            match Int32.TryParse raw with
            | true, p when p >= 1 && p <= 65535 -> p
            | _ ->
                failwithf
                    "SERVER_PORT=%s is not a valid TCP port. Expected an integer in 1-65535 (unset SERVER_PORT to use the default %d)."
                    raw
                    defaults.Port

    /// Phase 71.A.4 — `TOOLUP_PUBLIC_BASE_URL` runtime resolution. Empty
    /// / whitespace is ambiguous → warn + fall back to `None`. A trailing
    /// slash is stripped (idempotent) because token issuers append their
    /// own `/r/{token}`-style segment and a pasted `https://x/` otherwise
    /// produces a double slash. Unset → `defaults.PublicBaseUrl` (`None`).
    let private parsePublicBaseUrl (logger: ILogger) : string option =
        match envVar ConfigKeys.Names.publicBaseUrl with
        | None -> defaults.PublicBaseUrl
        | Some raw ->
            let trimmed = raw.Trim()

            if trimmed = "" then
                logger.Warn
                    "TOOLUP_PUBLIC_BASE_URL is set but empty/whitespace; the ambiguous empty value is ignored. Unset the variable or give it a value. Falling back to no public base URL."

                defaults.PublicBaseUrl
            else
                let noTrailing = trimmed.TrimEnd('/')

                if noTrailing <> trimmed then
                    logger.Warn
                        $"TOOLUP_PUBLIC_BASE_URL={raw} had a trailing slash; stripped to {noTrailing} (token issuers append their own path segment)."

                // Validate it parses as an absolute http(s) URL. A malformed
                // value (missing scheme, non-http scheme, stray host) would
                // otherwise be accepted silently and produce broken
                // share-token / public / OAuth-redirect links that only fail
                // at link-follow time. Fail soft (warn + fall back to None),
                // mirroring the empty-value handling above.
                match System.Uri.TryCreate(noTrailing, System.UriKind.Absolute) with
                | true, uri when uri.Scheme = System.Uri.UriSchemeHttp || uri.Scheme = System.Uri.UriSchemeHttps ->
                    Some noTrailing
                | _ ->
                    logger.Warn
                        $"TOOLUP_PUBLIC_BASE_URL={raw} is not a valid absolute http(s) URL; ignoring it (public links fall back to relative). Set a value like https://app.example.com."

                    defaults.PublicBaseUrl

    /// Phase 71.A.5 — `TOOLUP_PUBLIC_PATH`. Canonical precedence:
    /// env var > override-record value > `defaults.PublicPath`.
    let private resolvePublicPath (overrides: ServerConfigOverrides) : string =
        envVar ConfigKeys.Names.publicPath
        |> Option.orElse overrides.PublicPath
        |> Option.defaultValue defaults.PublicPath

    /// Phase 71.A.6 — boolean env var with an override-record middle tier:
    /// env (fail loud on garbage) > override > fallback. Needed for fields
    /// like `IncludePlatformDefaults` (default `true`) where a plain
    /// `envFlag` (false-when-unset) would wrongly flip an unset var off.
    let private envFlagTri (name: string) (overrideVal: bool option) (fallback: bool) : bool =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | Some("1" | "true" | "yes" | "on") -> true
        | Some("0" | "false" | "no" | "off") -> false
        | Some other ->
            failwithf
                "%s=%s is not a recognised boolean value. Expected 1/true/yes/on or 0/false/no/off (case-insensitive). Unset the variable to use the configured value."
                name
                other
        | None -> overrideVal |> Option.defaultValue fallback

    /// Phase 71.A.6 — optional boolean: `Some` when set (fail loud on
    /// garbage), `None` when unset (preserves a `bool option` default).
    let private envFlagOpt (name: string) : bool option =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> None
        | Some("1" | "true" | "yes" | "on") -> Some true
        | Some("0" | "false" | "no" | "off") -> Some false
        | Some other ->
            failwithf
                "%s=%s is not a recognised boolean value. Expected 1/true/yes/on or 0/false/no/off (case-insensitive). Unset the variable to leave it unset."
                name
                other

    /// Phase 71.A.6 — optional positive int64: parse when set, warn + `None`
    /// on garbage, `None` (or `none`/`0`) when unset.
    let private envInt64Opt (logger: ILogger) (name: string) : int64 option =
        match envVar name with
        | None
        | Some "none"
        | Some "0" -> None
        | Some raw ->
            match Int64.TryParse raw with
            | true, n when n > 0L -> Some n
            | _ ->
                logger.Warn $"{name}={raw} not a positive integer or 'none'. Leaving unset."
                None

    /// Phase 71.A.6 — positive-millisecond `TimeSpan` with a fallback;
    /// warn + fallback on garbage.
    let private envTimeSpanMs (logger: ILogger) (name: string) (fallback: TimeSpan) : TimeSpan =
        match envVar name with
        | None -> fallback
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> TimeSpan.FromMilliseconds(float n)
            | _ ->
                logger.Warn
                    $"{name}={raw} not a positive integer (milliseconds). Using default {fallback.TotalMilliseconds}ms."

                fallback

    /// Phase 71.A.8 — comma / semicolon / space-separated string list
    /// (the Surfaces-parser tokenisation, reused). Empty / whitespace → `[]`.
    let private parseStringList (name: string) : string list =
        match envVar name with
        | None -> []
        | Some raw ->
            raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s <> "")
            |> Array.toList

    /// Phase 71.A.7 — generic flat-case-DU env reader. `cases` maps
    /// lowercase tokens → the (payload-free) DU value; precedence is
    /// env > override > fallback. An unrecognised token warns (naming
    /// the valid tokens) and falls through to the configured value, so
    /// a typo never silently flips a subsystem on/off.
    let private parseFlatDuCase
        (logger: ILogger)
        (name: string)
        (cases: (string * 'T) list)
        (overrideVal: 'T option)
        (fallback: 'T)
        : 'T =
        let configured = overrideVal |> Option.defaultValue fallback

        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> configured
        | Some raw ->
            match cases |> List.tryFind (fun (tok, _) -> tok = raw) with
            | Some(_, v) -> v
            | None ->
                let valid = cases |> List.map fst |> String.concat ", "
                logger.Warn $"{name}={raw} not recognised. Valid: {valid}. Using the configured value."
                configured

    /// Phase 71.A.7 — the common `No* | Enabled*` binary shape. Accepts
    /// `no`/`off`/`disabled` and `enabled`/`on`/`yes` (case-insensitive).
    let private enabledDisabledTokens (disabledVal: 'T) (enabledVal: 'T) : (string * 'T) list = [
        "no", disabledVal
        "off", disabledVal
        "disabled", disabledVal
        "enabled", enabledVal
        "on", enabledVal
        "yes", enabledVal
    ]

    let private parseEnabledDisabled
        (logger: ILogger)
        (name: string)
        (disabledVal: 'T)
        (enabledVal: 'T)
        (fallback: 'T)
        : 'T =
        parseFlatDuCase logger name (enabledDisabledTokens disabledVal enabledVal) None fallback

    /// Phase 71.A.7 batch 2 — `parseEnabledDisabled` with an override-record
    /// middle tier (env > override > fallback) for binary toggles that ship
    /// a `ServerConfigOverrides` member.
    let private parseEnabledDisabledWith
        (logger: ILogger)
        (name: string)
        (overrideVal: 'T option)
        (disabledVal: 'T)
        (enabledVal: 'T)
        (fallback: 'T)
        : 'T =
        parseFlatDuCase logger name (enabledDisabledTokens disabledVal enabledVal) overrideVal fallback

    /// Phase 71.A.11 — select a hybrid DU case from an env var. `cases`
    /// maps a token to either `Ok value` (a case that's constructible
    /// from a default / curated factory — e.g. the nilary disabled case,
    /// or `PersistentBlobBacked EventRetentionPolicy.ninetyDays`) or
    /// `Error why` (the case carries a payload that can't be expressed via
    /// a single env var — `EnabledPublicRendering` needs a `ContentRoot`
    /// path; enabling DSR needs an explicit `ErasurePolicy`). An `Error`
    /// token **fails loud** naming how to supply the payload; unset /
    /// unrecognised → the configured value (GP 11).
    let private parseHybridCase
        (logger: ILogger)
        (name: string)
        (cases: (string * Result<'T, string>) list)
        (fallback: 'T)
        : 'T =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> fallback
        | Some raw ->
            match cases |> List.tryFind (fun (tok, _) -> tok = raw) with
            | Some(_, Ok v) -> v
            | Some(_, Error why) -> failwithf "%s=%s cannot be selected via env var: %s" name raw why
            | None ->
                let valid = cases |> List.map fst |> String.concat ", "
                logger.Warn $"{name}={raw} not recognised. Valid: {valid}. Using the configured value."
                fallback

    /// Build a `ServerConfig` from `TOOLUP_*` env vars + a curated
    /// overrides record. Every env-var read, warning message, and
    /// fallback semantics is byte-for-byte identical to the
    /// hand-written reference composition root pre-11.G — except for
    /// the Phase 66 Stream A.8 cutover from `TOOLUP_PLATFORM_MODE` to
    /// `TOOLUP_PLATFORM_SURFACES` (clean cutover; no aliasing).
    ///
    /// Surface-resolution (Phase 71.A — env-var beats library-default
    /// override-record value):
    ///   1. `TOOLUP_PLATFORM_SURFACES` (comma- / semicolon- /
    ///      space-separated token list) when set and at least one
    ///      token parses cleanly.
    ///   2. `overrides.Surfaces` when `Some` and non-empty (the
    ///      library-default fallback — `ServerConfigOverrides.referenceApp`
    ///      pins `Some Surfaces.individual`).
    ///   3. `defaults.Surfaces` as a final fallback.
    ///
    /// Consumer-authored literals (`{ ServerConfig.defaults with
    /// Surfaces = ... }`) never traverse this helper, so they still win
    /// at the highest altitude. The flip moves operator-deployer intent
    /// (env var) ahead of library-author defaults — fixes the
    /// silent-precedence trap documented in
    /// [`docs/migrations/71-runtime-config-audit.md`](../../docs/migrations/71-runtime-config-audit.md)
    /// §3.
    ///
    /// An unrecognised token (or an empty-after-parse result) falls
    /// back to the override-record value when present, else
    /// `defaults.Surfaces`, and surfaces the bad tokens via the
    /// supplied logger.
    let fromEnv (logger: ILogger) (overrides: ServerConfigOverrides) : ServerConfig =
        let overridesFallback =
            match overrides.Surfaces with
            | Some s when not (List.isEmpty s) -> s
            | _ -> defaults.Surfaces

        let surfaces =
            match envVar ConfigKeys.Names.platformSurfaces with
            | None -> overridesFallback
            | Some raw ->
                let tokens =
                    raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map _.Trim().ToLowerInvariant()
                    |> Array.filter (fun s -> s <> "")
                    |> Array.toList

                let parsed = tokens |> List.map parseSurfaceProfile

                let errors =
                    parsed
                    |> List.choose (function
                        | Error e -> Some e
                        | _ -> None)

                match errors with
                | [] ->
                    let resolved =
                        parsed
                        |> List.choose (function
                            | Ok s -> Some s
                            | _ -> None)

                    if List.isEmpty resolved then
                        logger.Warn
                            $"TOOLUP_PLATFORM_SURFACES={raw} resolved to an empty surface list. Valid tokens: anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer. Falling back to the library-default override value (or defaults)."

                        overridesFallback
                    else
                        resolved
                | bad ->
                    let badList = String.concat ", " bad

                    logger.Warn
                        $"TOOLUP_PLATFORM_SURFACES={raw} contains unrecognised token(s): {badList}. Valid tokens: anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer. Falling back to the library-default override value (or defaults)."

                    overridesFallback

        let logLevel, traceCategories = parseLogLevel ()

        // Phase 170 — module-binding trust anchors from the environment.
        // `TOOLUP_MODULE_BINDING_ALLOW_UNBOUND` is the policy bit (default
        // matches the off-by-default config); `TOOLUP_MODULE_BINDING_ANCHORS`
        // is a `;`-separated list of `mac:<keyId>:<scope>:<key>` (symmetric;
        // key resolved via ISecretStore at compose time) or
        // `asym:<keyId>:<alg>:<base64pubkey>` (asymmetric). A malformed entry
        // is warned + skipped; an unresolvable symmetric secret is the
        // compose-time validator's fail-closed concern (Phase 170 validator).
        let moduleBindingTrust =
            let allowUnbound =
                envFlagOrFail ConfigKeys.Names.moduleBindingAllowUnbound ModuleBindingTrustConfig.defaults.AllowUnbound

            let anchors =
                match envVar ConfigKeys.Names.moduleBindingAnchors with
                | None -> []
                | Some raw ->
                    raw.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose (fun entry ->
                        match entry.Trim().Split(':') with
                        | [| "mac"; keyId; scope; key |] -> Some(SymmetricAnchorRef(keyId, scope, key))
                        | [| "asym"; keyId; alg; pub |] -> Some(AsymmetricAnchorRef(keyId, alg, pub))
                        | _ ->
                            logger.Warn
                                $"TOOLUP_MODULE_BINDING_ANCHORS entry '{entry}' is malformed (expected 'mac:<keyId>:<scope>:<key>' or 'asym:<keyId>:<alg>:<base64pubkey>'); skipped."

                            None)
                    |> Array.toList

            {
                Anchors = anchors
                AllowUnbound = allowUnbound
            }

        {
            defaults with
                PublicPath = resolvePublicPath overrides // Phase 71.A.5
                Surfaces = surfaces
                ModuleFilter = envVar ConfigKeys.Names.moduleFilter
                ModuleBindingTrust = moduleBindingTrust
                RequireHttps = envFlag ConfigKeys.Names.requireHttps
                TrustForwardedHeaders =
                    envFlagOrFail ConfigKeys.Names.trustForwardedHeaders defaults.TrustForwardedHeaders
                // Phase 325 — trusted-proxy CIDR allowlist + its escape hatch.
                // Entries are validated (fail-loud on malformed CIDR) by the
                // preflight validator + the pipeline's options builder, not here:
                // `fromEnv` stays a pure string read so the error surfaces with
                // the same message whichever construction path built the config.
                TrustedProxyCidrs = parseStringList ConfigKeys.Names.trustedProxyCidrs
                AcceptForwardedHeadersFromAnyProxy = envFlag ConfigKeys.Names.acceptForwardedHeadersFromAnyProxy
                StaticPathBehaviour = parseStaticPathBehaviour logger
                SlowRequestThresholdOverrides =
                    overrides.SlowRequestThresholdOverrides
                    |> Option.defaultValue defaults.SlowRequestThresholdOverrides
                // Phase 71.A.6 — env wins over override-record value, else fallback.
                EnableDevEndpoints =
                    envFlagTri
                        ConfigKeys.Names.enableDevEndpoints
                        overrides.EnableDevEndpoints
                        defaults.EnableDevEndpoints
                AutoBootstrapDevAdmin = overrides.AutoBootstrapDevAdmin
                IncludePlatformDefaults =
                    envFlagTri
                        ConfigKeys.Names.includePlatformDefaults
                        overrides.IncludePlatformDefaults
                        defaults.IncludePlatformDefaults
                // Phase 71.A.7 batch 2 — override-bearing toggles: env > override > default.
                ShareTokenStore =
                    parseEnabledDisabledWith
                        logger
                        ConfigKeys.Names.shareTokenStore
                        overrides.ShareTokenStore
                        NoShareTokenStore
                        EnabledShareTokenStore
                        defaults.ShareTokenStore
                Webhooks =
                    parseEnabledDisabledWith
                        logger
                        ConfigKeys.Names.webhooks
                        overrides.Webhooks
                        NoWebhooks
                        EnabledWebhooks
                        defaults.Webhooks
                AuditLog =
                    parseEnabledDisabledWith
                        logger
                        ConfigKeys.Names.auditLog
                        overrides.AuditLog
                        NoAuditLog
                        EnabledAuditLog
                        defaults.AuditLog
                SecurityHardening =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.securityHardening
                        [
                            "no", NoSecurityHardening
                            "off", NoSecurityHardening
                            "disabled", NoSecurityHardening
                            "default", DefaultSecurityHardening
                            "on", DefaultSecurityHardening
                            "strict", StrictSecurityHardening
                        ]
                        overrides.SecurityHardening
                        defaults.SecurityHardening
                LogLevel = logLevel
                TraceCategories = traceCategories
                SseAuthMode = parseSseAuthMode logger
                AuthCookieIssuance = parseAuthCookieIssuance logger
                AcceptHeaderAuthWhenAuthRequired = envFlag ConfigKeys.Names.acceptHeaderAuthInAuthMode
                // Phase 457 — two spellings, one acknowledgement. The
                // shorter name is what the at-rest-posture validator's
                // refusal prints; ORing here is what keeps it from becoming
                // a second, separately-honoured opt-out. Unset → unchanged
                // (GP 11).
                AcceptPlaintextSecretsWhenAuthRequired =
                    envFlag ConfigKeys.Names.acceptPlaintextSecretsInAuthMode
                    || envFlag ConfigKeys.Names.acceptPlaintextSecrets
                ReplicaCount = parseReplicaCount logger
                AcceptInProcessSchedulerInMultiInstance = envFlag ConfigKeys.Names.acceptInProcessSchedulerMultiInstance
                AcceptNoRateLimitWhenAuthRequired = envFlag ConfigKeys.Names.acceptNoRateLimitInAuthMode
                AcceptUnsignedPublishable = envFlag ConfigKeys.Names.acceptUnsignedPublishable
                AcceptQueryParamSseAuthWhenAuthRequired = envFlag ConfigKeys.Names.acceptQueryParamSseAuthInAuthMode
                AcceptSameSiteOnlyCsrfWhenAuthRequired = envFlag ConfigKeys.Names.acceptSameSiteOnlyCsrfInAuthMode
                AcceptInMemoryShareTokenRateLimiterInMultiInstance =
                    envFlag ConfigKeys.Names.acceptInMemoryShareTokenRateLimiterMultiInstance
                // Phase 71.A.2 — six `Accept*` flags whose documented env
                // vars `fromEnv` never read (audit §7). Each preserves
                // GP 11: unset → `false`, and the matching validator still
                // refuses startup unless the operator opts in.
                AcceptInProcessIngestionInMultiInstance = envFlag ConfigKeys.Names.acceptInProcessIngestionMultiInstance
                AcceptSharedEmbeddingCacheInTeamMode = envFlag ConfigKeys.Names.acceptSharedEmbeddingCacheInTeamMode
                // Phase 9m.B — the two RAG escape hatches this phase
                // introduced. Same GP 11 shape as the rest of the family:
                // unset ⇒ `false`, and the matching validator still fires.
                AcceptEphemeralRagIndex = envFlag ConfigKeys.Names.acceptEphemeralRagIndex
                AcceptLocalEmbedderAtScale = envFlag ConfigKeys.Names.acceptLocalEmbedderAtScale
                AcceptStickyRoutedAiInMultiInstance = envFlag ConfigKeys.Names.acceptStickyRoutedAiMultiInstance
                // Phase 6m — the anonymous-AI cost attestation. Same
                // GP 11 shape as the rest of the family: unset ⇒ `false`,
                // and `AnonymousAIModeValidator` still refuses startup.
                AcceptAnonymousModeWithAI = envFlag ConfigKeys.Names.acceptAnonymousModeWithAi
                AcceptUnboundAudienceWhenAuthRequired = envFlag ConfigKeys.Names.acceptUnboundAudienceInAuthMode
                AcceptInMemoryOAuthStateInMultiInstance = envFlag ConfigKeys.Names.acceptInMemoryOAuthStateMultiInstance
                AcceptPendingInviteStoreInMultiInstance = envFlag ConfigKeys.Names.acceptPendingInviteStoreMultiInstance
                AcceptInviteByEmailWithoutDirectory = envFlag ConfigKeys.Names.acceptInviteByEmailWithoutDirectory
                // Phase 547.C — inviter notification on invite expiry.
                // Same GP 11 shape: unset ⇒ `false` (off).
                NotifyInviterOnInviteExpiry = envFlag ConfigKeys.Names.notifyInviterOnInviteExpiry
                // Phase 460 — the share-token ephemeral-key acknowledgement.
                // Same GP 11 shape as the rest of the family: unset ⇒ `false`,
                // and the provenance validator still refuses a production-shaped
                // deployment whose signing key is unprovisioned.
                AcceptEphemeralShareTokenKey = envFlag ConfigKeys.Names.acceptEphemeralShareTokenKey
                // Phase 71.A.3 / 71.A.4 — Port + PublicBaseUrl now resolve
                // inside the `fromEnv` seam (were compose-only / unread).
                Port = parseServerPort ()
                PublicBaseUrl = parsePublicBaseUrl logger
                // Phase 71.A.6 — boolean / scalar bundle. Each is additive and
                // preserves GP 11: unset → the prior `defaults.X` value.
                BackfillMissedTicks = envFlag ConfigKeys.Names.backfillMissedTicks
                // Phase 598 tail — env lift for the event-trigger catch-up
                // opt-in, 71.A.6 parity with TOOLUP_BACKFILL_MISSED_TICKS.
                EventTriggerCatchUp = envFlag ConfigKeys.Names.eventTriggerCatchUp
                MigrateWebhookSecretsAtRest = envFlag ConfigKeys.Names.migrateWebhookSecretsAtRest
                SkipPreflight = envFlag ConfigKeys.Names.skipPreflight
                HealthStateTracking = envFlag ConfigKeys.Names.healthStateTracking
                EnableCitationDevEndpoint = envFlagOpt ConfigKeys.Names.enableCitationDevEndpoint
                MaxRequestBodyBytes = envInt64Opt logger ConfigKeys.Names.maxRequestBodyBytes
                SlowRateLimitThreshold =
                    envTimeSpanMs logger ConfigKeys.Names.slowRateLimitMs defaults.SlowRateLimitThreshold
                // Phase 71.A.8 — server string lists.
                WebhookUrlAllowedHosts = parseStringList ConfigKeys.Names.webhookUrlAllowedHosts
                PeerRoutePrefixes = parseStringList ConfigKeys.Names.peerRoutePrefixes
                // Phase 71.A.7 (batch 1) — flat-case DU lifts (no override
                // member, no payload). Additive: unset → `defaults.X`.
                AuditFailurePolicy =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.auditFailurePolicy
                        [
                            "log", LogAndContinue
                            "logandcontinue", LogAndContinue
                            "refuse", RefuseAction
                            "refuseaction", RefuseAction
                            "degrade", DegradeToFile
                            "degradetofile", DegradeToFile
                        ]
                        None
                        defaults.AuditFailurePolicy
                ResultStore =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.resultStore
                        [
                            "no", NoResultStore
                            "inmemory", InMemoryResultStore
                            "in-memory", InMemoryResultStore
                            "persistent", PersistentResultStore
                        ]
                        None
                        defaults.ResultStore
                Lineage =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.lineage
                        NoLineageStore
                        EnabledLineageStore
                        defaults.Lineage
                DataIngestion =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.dataIngestion
                        NoDataIngestion
                        EnabledDataIngestion
                        defaults.DataIngestion
                ColumnMapping =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.columnMapping
                        NoColumnMapping
                        EnabledColumnMapping
                        defaults.ColumnMapping
                MappingDryRun =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.mappingDryRunBlock
                        WarnOnValidationFailure
                        BlockOnValidationFailure
                        defaults.MappingDryRun
                OAuthRefresher =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.oauthRefresher
                        NoOAuthRefresher
                        EnabledOAuthRefresher
                        defaults.OAuthRefresher
                EntityStore =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.entityStore
                        NoEntityStore
                        EnabledEntityStore
                        defaults.EntityStore
                EntityOutbox =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.entityOutbox
                        NoEntityOutbox
                        EnabledEntityOutbox
                        defaults.EntityOutbox
                UsageMetering =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.usageMetering
                        NoUsageMetering
                        EnabledUsageMetering
                        defaults.UsageMetering
                ComputeBudget =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.computeBudget
                        NoComputeBudget
                        EnabledComputeBudget
                        defaults.ComputeBudget
                MetricsEndpoint =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.metricsEndpoint
                        NoMetricsEndpoint
                        EnabledMetricsEndpoint
                        defaults.MetricsEndpoint
                PlatformKnowledgeBase =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.platformKnowledgeBase
                        NoPlatformKnowledgeBase
                        EnabledPlatformKnowledgeBase
                        defaults.PlatformKnowledgeBase
                ConfigDriftDetection =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.configDriftDetection
                        NoConfigDriftDetection
                        EnabledConfigDriftDetection
                        defaults.ConfigDriftDetection
                RateLimiter =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.rateLimiter
                        NoRateLimiter
                        EnabledRateLimiter
                        defaults.RateLimiter
                SmokeTest =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.smokeTest
                        NoSmokeTest
                        EnabledSmokeTest
                        defaults.SmokeTest
                DeploymentReadiness =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.deploymentReadiness
                        NoReadinessReport
                        EnabledReadinessReport
                        defaults.DeploymentReadiness
                DeploymentVerification =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.deploymentVerification
                        NoDeploymentVerification
                        EnabledDeploymentVerification
                        defaults.DeploymentVerification
                AssetStore =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.assetStore
                        NoAssetStore
                        EnabledAssetStore
                        defaults.AssetStore
                ConsentAudit =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.consentAudit
                        NoConsentAudit
                        EnabledConsentAudit
                        defaults.ConsentAudit
                AdAnalytics =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.adAnalytics
                        NoAdAnalytics
                        EnabledAdAnalytics
                        defaults.AdAnalytics
                // Phase 159 — durable per-subject consent-state store mode.
                ConsentStateStore =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.consentStateStore
                        [
                            "no", NoConsentStateStore
                            "off", NoConsentStateStore
                            "disabled", NoConsentStateStore
                            "inmemory", InMemoryConsentStateStore
                            "in-memory", InMemoryConsentStateStore
                            "entity", EntityBackedConsentStateStore
                            "entity-backed", EntityBackedConsentStateStore
                        ]
                        None
                        defaults.ConsentStateStore
                ServerlessHost =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.serverlessHost
                        [ "kestrel", KestrelHost; "serverless", ServerlessHost ]
                        None
                        defaults.ServerlessHost
                ProcessProfile =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.processProfile
                        [
                            "allinone", AllInOne
                            "all-in-one", AllInOne
                            "web", WebOnly
                            "webonly", WebOnly
                            "worker", WorkerOnly
                            "workeronly", WorkerOnly
                            "dispatcher", DispatcherOnly
                            "dispatcheronly", DispatcherOnly
                        ]
                        None
                        defaults.ProcessProfile
                // Phase 71.A.7 batch 2 — TeamCreationPolicy (no override member).
                TeamCreationPolicy =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.teamCreationPolicy
                        [
                            "platformadminonly", PlatformAdminOnly
                            "platform-admin-only", PlatformAdminOnly
                            "admin", PlatformAdminOnly
                            "anyauthenticateduser", AnyAuthenticatedUser
                            "any", AnyAuthenticatedUser
                            "authenticated", AnyAuthenticatedUser
                        ]
                        None
                        defaults.TeamCreationPolicy
                // Phase 549 — direct-add existence proof. Accepts the
                // boolean spellings an operator reaches for on a
                // `REQUIRE_*` variable as well as the estate's
                // enabled/disabled family, so neither reading is a silent
                // miss; an unrecognised token warns and keeps the
                // configured value (GP 11 — unset ⇒ `NoIdentityProof`).
                DirectAddIdentityProof =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.requireDirectoryProofForDirectAdd
                        [
                            "0", NoIdentityProof
                            "false", NoIdentityProof
                            "no", NoIdentityProof
                            "off", NoIdentityProof
                            "disabled", NoIdentityProof
                            "1", RequireDirectoryProof
                            "true", RequireDirectoryProof
                            "yes", RequireDirectoryProof
                            "on", RequireDirectoryProof
                            "enabled", RequireDirectoryProof
                        ]
                        None
                        defaults.DirectAddIdentityProof
                // Phase 71.A.11 — fully-nilary DUs the audit grouped as HY
                // (the in-tree DU carries no payload): pure flat lifts.
                JobScheduler =
                    parseEnabledDisabled
                        logger
                        ConfigKeys.Names.jobScheduler
                        NoJobScheduler
                        InProcessJobScheduler
                        defaults.JobScheduler
                RateLimitStore =
                    parseFlatDuCase
                        logger
                        ConfigKeys.Names.rateLimitStore
                        [
                            "no", NoRateLimitStore
                            "off", NoRateLimitStore
                            "disabled", NoRateLimitStore
                            "inmemory", InMemoryRateLimitStore
                            "in-memory", InMemoryRateLimitStore
                            "external", ExternalRateLimitStore
                        ]
                        None
                        defaults.RateLimitStore
                // Phase 71.A.11 — hybrid case-flips: nilary / curated-default
                // cases select; payload-bearing cases fail loud (the payload
                // must be supplied via overrides / a `defaults with` literal).
                EventStore =
                    parseHybridCase
                        logger
                        ConfigKeys.Names.eventStore
                        [
                            "inmemory", Ok InMemoryOnly
                            "in-memory", Ok InMemoryOnly
                            "persistent", Ok(PersistentBlobBacked EventRetentionPolicy.ninetyDays)
                        ]
                        defaults.EventStore
                ConversationStore =
                    parseHybridCase
                        logger
                        ConfigKeys.Names.conversationStore
                        [
                            "no", Ok NoConversationStore
                            "off", Ok NoConversationStore
                            "disabled", Ok NoConversationStore
                            "enabled",
                            Error
                                "EnabledConversationStore requires a retentionDays value; set ServerConfig.ConversationStore via overrides or a `{ defaults with ... }` literal"
                        ]
                        defaults.ConversationStore
                PublicRendering =
                    parseHybridCase
                        logger
                        ConfigKeys.Names.publicRendering
                        [
                            "no", Ok NoPublicRendering
                            "off", Ok NoPublicRendering
                            "disabled", Ok NoPublicRendering
                            "enabled",
                            Error
                                "EnabledPublicRendering requires a ContentRoot path; set ServerConfig.PublicRendering via overrides"
                        ]
                        defaults.PublicRendering
                DataSubjectRequests =
                    parseHybridCase
                        logger
                        ConfigKeys.Names.dataSubjectRequests
                        [
                            "disabled", Ok DataSubjectRequestMode.Disabled
                            "no", Ok DataSubjectRequestMode.Disabled
                            "off", Ok DataSubjectRequestMode.Disabled
                            "enabled",
                            Error
                                "Enabling DSR requires an explicit ErasurePolicy (a compliance decision, not defaulted); set ServerConfig.DataSubjectRequests via overrides"
                        ]
                        defaults.DataSubjectRequests
                EphemeralStoreEvictionMinutes = parseEphemeralStoreEvictionMinutes logger
                RateLimit = parseRateLimit logger
                DefaultTeamStorageQuotaBytes = parseDefaultTeamStorageQuotaBytes logger
                SlowRequestThreshold = parseSlowRequestThreshold logger
                MaxSseConnectionsPerScope = parseMaxSseConnectionsPerScope logger
        }
#endif