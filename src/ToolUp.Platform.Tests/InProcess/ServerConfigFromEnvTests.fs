module ToolUp.Platform.Tests.InProcess.ServerConfigFromEnvTests

// Phase 71.A — runtime-resolvable configuration lifts on the
// `ServerConfig.fromEnv` seam. Env vars are process-global, so the whole
// list is `testSequenced` and every case snapshots + restores what it
// touches (mirrors the AuthProvider.fromEnv test convention).

open System
open Expecto
open ToolUp.Platform

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Logger that captures Warn lines so the PublicBaseUrl warnings can be asserted.
let private capturingLogger () =
    let warnings = ResizeArray<string>()

    let logger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn m = warnings.Add m
            member _.Error(_, _) = ()
        }

    logger, warnings

let private withEnv (pairs: (string * string option) list) (body: unit -> unit) =
    let priors =
        pairs |> List.map (fun (n, _) -> n, Environment.GetEnvironmentVariable n)

    try
        for n, v in pairs do
            Environment.SetEnvironmentVariable(n, v |> Option.toObj)

        body ()
    finally
        for n, prior in priors do
            Environment.SetEnvironmentVariable(n, prior)

/// The six `Accept*` flags whose documented env vars `fromEnv` never read
/// before Phase 71.A.2, paired with their projection out of `ServerConfig`.
let private sixAcceptFlags: (string * (ServerConfig -> bool)) list = [
    "TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE", _.AcceptInProcessIngestionInMultiInstance
    "TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE", _.AcceptSharedEmbeddingCacheInTeamMode
    "TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE", _.AcceptStickyRoutedAiInMultiInstance
    "TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE", _.AcceptUnboundAudienceWhenAuthRequired
    "TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE", _.AcceptInMemoryOAuthStateInMultiInstance
    "TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE", _.AcceptPendingInviteStoreInMultiInstance
]

let tests =
    testSequenced (
        testList "ServerConfig.fromEnv (Phase 71.A)" [
            // ── 71.A.1 — Surfaces precedence inversion (regression pin) ──
            test "Surfaces: TOOLUP_PLATFORM_SURFACES wins over referenceApp override" {
                withEnv [ "TOOLUP_PLATFORM_SURFACES", Some "multi_team" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.referenceApp

                    Expect.equal
                        cfg.Surfaces
                        [ SurfaceProfile.multiTeam ]
                        "env var must win over the library-default override")
            }

            test "Surfaces: unset env falls back to the referenceApp override (byte-compat)" {
                withEnv [ "TOOLUP_PLATFORM_SURFACES", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.referenceApp

                    Expect.equal
                        cfg.Surfaces
                        Surfaces.individual
                        "unset env must preserve the prior Individual posture")
            }

            // ── 71.A.2 — six Accept* flag reads ──
            test "Accept*: each of the six env vars set to 1 lands true" {
                withEnv (sixAcceptFlags |> List.map (fun (n, _) -> n, Some "1")) (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    for envName, project in sixAcceptFlags do
                        Expect.isTrue (project cfg) $"{envName}=1 must resolve true")
            }

            test "Accept*: each of the six env vars unset stays false" {
                withEnv (sixAcceptFlags |> List.map (fun (n, _) -> n, None)) (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    for envName, project in sixAcceptFlags do
                        Expect.isFalse (project cfg) $"{envName} unset must stay false (GP 11)")
            }

            // ── 9m.B — the two RAG Accept* flags ──
            //
            // Same shape as the 71.A.2 pair above, pinned because the
            // failure mode of a `fromEnv` wiring is silent by
            // construction: a typo in the variable name reads as "the
            // operator did not set it", which is indistinguishable from
            // the default. That is precisely the class of silent-default
            // defect Phase 9m.B exists to close, so leaving its own env
            // wiring unpinned would be self-defeating.
            test "Accept*: the two RAG escape-hatch env vars set to 1 land true" {
                let ragAcceptFlags: (string * (ServerConfig -> bool)) list = [
                    "TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX", _.AcceptEphemeralRagIndex
                    "TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE", _.AcceptLocalEmbedderAtScale
                ]

                withEnv (ragAcceptFlags |> List.map (fun (n, _) -> n, Some "1")) (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    for envName, project in ragAcceptFlags do
                        Expect.isTrue (project cfg) $"{envName}=1 must resolve true")

                withEnv (ragAcceptFlags |> List.map (fun (n, _) -> n, None)) (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    for envName, project in ragAcceptFlags do
                        Expect.isFalse (project cfg) $"{envName} unset must stay false (GP 11)")
            }

            // ── 71.A.3 — Port read inside fromEnv ──
            test "Port: SERVER_PORT is read inside the fromEnv seam" {
                withEnv [ "SERVER_PORT", Some "8137" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.Port 8137 "SERVER_PORT must land on ServerConfig.Port")
            }

            test "Port: an out-of-range SERVER_PORT fails loud" {
                withEnv [ "SERVER_PORT", Some "abc" ] (fun () ->
                    Expect.throws
                        (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty |> ignore)
                        "a non-integer SERVER_PORT must fail loud at fromEnv")
            }

            // ── 71.A.4 — PublicBaseUrl runtime resolution ──
            test "PublicBaseUrl: a trailing slash is stripped (idempotent) + warns" {
                withEnv [ "TOOLUP_PUBLIC_BASE_URL", Some "https://surveys.example.com/" ] (fun () ->
                    let logger, warnings = capturingLogger ()
                    let cfg = ServerConfig.fromEnv logger ServerConfigOverrides.empty

                    Expect.equal
                        cfg.PublicBaseUrl
                        (Some "https://surveys.example.com")
                        "trailing slash must be stripped"

                    Expect.isTrue (warnings |> Seq.exists (fun w -> w.Contains "trailing slash")) "stripping must warn")
            }

            test "PublicBaseUrl: an empty/whitespace value resolves None + warns" {
                withEnv [ "TOOLUP_PUBLIC_BASE_URL", Some "   " ] (fun () ->
                    let logger, warnings = capturingLogger ()
                    let cfg = ServerConfig.fromEnv logger ServerConfigOverrides.empty
                    Expect.isNone cfg.PublicBaseUrl "ambiguous empty value must resolve None"
                    Expect.isTrue (warnings.Count > 0) "the ambiguous empty value must warn")
            }

            test "PublicBaseUrl: unset resolves None (default)" {
                withEnv [ "TOOLUP_PUBLIC_BASE_URL", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isNone cfg.PublicBaseUrl "unset must preserve the None default")
            }

            // ── 71.A.5 — PublicPath ──
            test "PublicPath: TOOLUP_PUBLIC_PATH wins over the override-record value" {
                withEnv [ "TOOLUP_PUBLIC_PATH", Some "/srv/static" ] (fun () ->
                    let overrides = {
                        ServerConfigOverrides.empty with
                            PublicPath = Some "override-path"
                    }

                    let cfg = ServerConfig.fromEnv silentLogger overrides
                    Expect.equal cfg.PublicPath "/srv/static" "env var must win over the override")
            }

            test "PublicPath: unset falls back to the override, then the default" {
                withEnv [ "TOOLUP_PUBLIC_PATH", None ] (fun () ->
                    let withOverride =
                        ServerConfig.fromEnv silentLogger {
                            ServerConfigOverrides.empty with
                                PublicPath = Some "override-path"
                        }

                    Expect.equal withOverride.PublicPath "override-path" "override wins when env unset"

                    let noOverride = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal noOverride.PublicPath "deploy/public" "default when neither set")
            }

            // ── 71.A.6 — boolean / scalar bundle ──
            test "IncludePlatformDefaults: default true is preserved when the env var is unset" {
                withEnv [ "TOOLUP_INCLUDE_PLATFORM_DEFAULTS", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    Expect.isTrue
                        cfg.IncludePlatformDefaults
                        "unset must keep the default true (plain envFlag would wrongly flip it false)")
            }

            test "IncludePlatformDefaults: TOOLUP_INCLUDE_PLATFORM_DEFAULTS=0 turns it off" {
                withEnv [ "TOOLUP_INCLUDE_PLATFORM_DEFAULTS", Some "0" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isFalse cfg.IncludePlatformDefaults "explicit 0 must turn it off")
            }

            test "EnableDevEndpoints: env var wins over the override-record value" {
                withEnv [ "TOOLUP_ENABLE_DEV_ENDPOINTS", Some "1" ] (fun () ->
                    let overrides = {
                        ServerConfigOverrides.empty with
                            EnableDevEndpoints = Some false
                    }

                    let cfg = ServerConfig.fromEnv silentLogger overrides
                    Expect.isTrue cfg.EnableDevEndpoints "env=1 must win over override=false")
            }

            test "Boolean bundle: BackfillMissedTicks / SkipPreflight / HealthStateTracking" {
                withEnv
                    [
                        "TOOLUP_BACKFILL_MISSED_TICKS", Some "true"
                        "TOOLUP_SKIP_PREFLIGHT", Some "yes"
                        "TOOLUP_HEALTH_STATE_TRACKING", Some "on"
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.isTrue cfg.BackfillMissedTicks "BackfillMissedTicks"
                        Expect.isTrue cfg.SkipPreflight "SkipPreflight"
                        Expect.isTrue cfg.HealthStateTracking "HealthStateTracking")
            }

            test "Boolean bundle: unset stays at the false default (GP 11)" {
                withEnv
                    [
                        "TOOLUP_BACKFILL_MISSED_TICKS", None
                        "TOOLUP_SKIP_PREFLIGHT", None
                        "TOOLUP_HEALTH_STATE_TRACKING", None
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.isFalse cfg.BackfillMissedTicks "BackfillMissedTicks default"
                        Expect.isFalse cfg.SkipPreflight "SkipPreflight default"
                        Expect.isFalse cfg.HealthStateTracking "HealthStateTracking default")
            }

            test "EnableCitationDevEndpoint: optional bool — set → Some, unset → None" {
                withEnv [ "TOOLUP_ENABLE_CITATION_DEV_ENDPOINT", Some "true" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.EnableCitationDevEndpoint (Some true) "set must resolve Some true")

                withEnv [ "TOOLUP_ENABLE_CITATION_DEV_ENDPOINT", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isNone cfg.EnableCitationDevEndpoint "unset must stay None")
            }

            test "MaxRequestBodyBytes: positive int → Some, unset → None, garbage → None" {
                withEnv [ "TOOLUP_MAX_REQUEST_BODY_BYTES", Some "1048576" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.MaxRequestBodyBytes (Some 1048576L) "positive int → Some")

                withEnv [ "TOOLUP_MAX_REQUEST_BODY_BYTES", Some "nonsense" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isNone cfg.MaxRequestBodyBytes "garbage → None (warn)")
            }

            test "SlowRateLimitThreshold: ms value → TimeSpan, unset → 5s default" {
                withEnv [ "TOOLUP_SLOW_RATE_LIMIT_MS", Some "2500" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.SlowRateLimitThreshold (TimeSpan.FromMilliseconds 2500.0) "2500ms")

                withEnv [ "TOOLUP_SLOW_RATE_LIMIT_MS", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.SlowRateLimitThreshold (TimeSpan.FromSeconds 5.0) "unset → 5s default")
            }

            // ── 71.A.8 — server string lists ──
            test "String lists: WebhookUrlAllowedHosts + PeerRoutePrefixes split, unset → []" {
                withEnv
                    [
                        "TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS", Some "a.example.com, b.example.com ;c.example.com"
                        "TOOLUP_PEER_ROUTE_PREFIXES", Some "/peer /api/peer"
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                        Expect.equal
                            cfg.WebhookUrlAllowedHosts
                            [ "a.example.com"; "b.example.com"; "c.example.com" ]
                            "comma/semicolon/space split"

                        Expect.equal cfg.PeerRoutePrefixes [ "/peer"; "/api/peer" ] "space split")

                withEnv [ "TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS", None; "TOOLUP_PEER_ROUTE_PREFIXES", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isEmpty cfg.WebhookUrlAllowedHosts "unset → []"
                    Expect.isEmpty cfg.PeerRoutePrefixes "unset → []")
            }

            // ── 71.A.7 (batch 1) — flat-case DU lifts ──
            test "Flat DUs: each env token flips the field off its default case" {
                withEnv
                    [
                        "TOOLUP_RESULT_STORE", Some "persistent"
                        "TOOLUP_LINEAGE", Some "enabled"
                        "TOOLUP_DATA_INGESTION", Some "on"
                        "TOOLUP_OAUTH_REFRESHER", Some "yes"
                        "TOOLUP_ENTITY_STORE", Some "enabled"
                        "TOOLUP_USAGE_METERING", Some "enabled"
                        "TOOLUP_METRICS_ENDPOINT", Some "enabled"
                        "TOOLUP_PLATFORM_KNOWLEDGE_BASE", Some "enabled"
                        "TOOLUP_CONFIG_DRIFT_DETECTION", Some "enabled"
                        "TOOLUP_RATE_LIMITER", Some "enabled"
                        "TOOLUP_SMOKE_TEST", Some "enabled"
                        "TOOLUP_ASSET_STORE", Some "enabled"
                        "TOOLUP_CONSENT_AUDIT", Some "enabled"
                        "TOOLUP_AD_ANALYTICS", Some "enabled"
                        "TOOLUP_SERVERLESS_HOST", Some "serverless"
                        "TOOLUP_PROCESS_PROFILE", Some "workeronly"
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.equal cfg.ResultStore PersistentResultStore "ResultStore"
                        Expect.equal cfg.Lineage EnabledLineageStore "Lineage"
                        Expect.equal cfg.DataIngestion EnabledDataIngestion "DataIngestion"
                        Expect.equal cfg.OAuthRefresher EnabledOAuthRefresher "OAuthRefresher"
                        Expect.equal cfg.EntityStore EnabledEntityStore "EntityStore"
                        Expect.equal cfg.UsageMetering EnabledUsageMetering "UsageMetering"
                        Expect.equal cfg.MetricsEndpoint EnabledMetricsEndpoint "MetricsEndpoint"
                        Expect.equal cfg.PlatformKnowledgeBase EnabledPlatformKnowledgeBase "PlatformKnowledgeBase"
                        Expect.equal cfg.ConfigDriftDetection EnabledConfigDriftDetection "ConfigDriftDetection"
                        Expect.equal cfg.RateLimiter EnabledRateLimiter "RateLimiter"
                        Expect.equal cfg.SmokeTest EnabledSmokeTest "SmokeTest"
                        Expect.equal cfg.AssetStore EnabledAssetStore "AssetStore"
                        Expect.equal cfg.ConsentAudit EnabledConsentAudit "ConsentAudit"
                        Expect.equal cfg.AdAnalytics EnabledAdAnalytics "AdAnalytics"
                        Expect.equal cfg.ServerlessHost ServerlessHost "ServerlessHost"
                        Expect.equal cfg.ProcessProfile WorkerOnly "ProcessProfile")
            }

            test "Flat DUs: unset preserves every default case (GP 11)" {
                withEnv
                    [
                        "TOOLUP_RESULT_STORE", None
                        "TOOLUP_LINEAGE", None
                        "TOOLUP_ENTITY_STORE", None
                        "TOOLUP_SERVERLESS_HOST", None
                        "TOOLUP_PROCESS_PROFILE", None
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.equal cfg.ResultStore NoResultStore "ResultStore default"
                        Expect.equal cfg.Lineage NoLineageStore "Lineage default"
                        Expect.equal cfg.EntityStore NoEntityStore "EntityStore default"
                        Expect.equal cfg.ServerlessHost KestrelHost "ServerlessHost default"
                        Expect.equal cfg.ProcessProfile AllInOne "ProcessProfile default")
            }

            test "Flat DUs: an unrecognised token warns and keeps the default" {
                withEnv [ "TOOLUP_PROCESS_PROFILE", Some "nonsense" ] (fun () ->
                    let logger, warnings = capturingLogger ()
                    let cfg = ServerConfig.fromEnv logger ServerConfigOverrides.empty
                    Expect.equal cfg.ProcessProfile AllInOne "garbage → default"
                    Expect.isTrue (warnings |> Seq.exists (fun w -> w.Contains "TOOLUP_PROCESS_PROFILE")) "must warn")
            }

            // ── 71.A.7 (batch 2) — override-bearing toggles + TeamCreationPolicy ──
            test "Batch 2: env wins over the override-record value" {
                withEnv
                    [
                        "TOOLUP_WEBHOOKS", Some "disabled"
                        "TOOLUP_AUDIT_LOG", Some "enabled"
                        "TOOLUP_SECURITY_HARDENING", Some "strict"
                    ]
                    (fun () ->
                        let overrides = {
                            ServerConfigOverrides.empty with
                                Webhooks = Some EnabledWebhooks
                                SecurityHardening = Some DefaultSecurityHardening
                        }

                        let cfg = ServerConfig.fromEnv silentLogger overrides
                        Expect.equal cfg.Webhooks NoWebhooks "env=disabled wins over override=Enabled"
                        Expect.equal cfg.AuditLog EnabledAuditLog "env=enabled (no override)"

                        Expect.equal
                            cfg.SecurityHardening
                            StrictSecurityHardening
                            "env=strict wins over override=Default")
            }

            test "Batch 2: unset → override wins over default" {
                withEnv [ "TOOLUP_WEBHOOKS", None; "TOOLUP_SECURITY_HARDENING", None ] (fun () ->
                    let overrides = {
                        ServerConfigOverrides.empty with
                            Webhooks = Some EnabledWebhooks
                            SecurityHardening = Some StrictSecurityHardening
                    }

                    let cfg = ServerConfig.fromEnv silentLogger overrides
                    Expect.equal cfg.Webhooks EnabledWebhooks "override wins when env unset"
                    Expect.equal cfg.SecurityHardening StrictSecurityHardening "override wins when env unset")
            }

            test "Batch 2: unset + no override → default" {
                withEnv
                    [
                        "TOOLUP_WEBHOOKS", None
                        "TOOLUP_AUDIT_LOG", None
                        "TOOLUP_SHARE_TOKEN_STORE", None
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.equal cfg.Webhooks NoWebhooks "default"
                        Expect.equal cfg.AuditLog NoAuditLog "default"
                        Expect.equal cfg.ShareTokenStore NoShareTokenStore "default")
            }

            test "TeamCreationPolicy: env token flips it; unset → PlatformAdminOnly default" {
                withEnv [ "TOOLUP_TEAM_CREATION_POLICY", Some "any" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.TeamCreationPolicy AnyAuthenticatedUser "env=any → AnyAuthenticatedUser")

                withEnv [ "TOOLUP_TEAM_CREATION_POLICY", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.TeamCreationPolicy PlatformAdminOnly "unset → PlatformAdminOnly default")
            }

            // ── 71.A.11 — hybrid case-flips + nilary HY DUs ──
            test "Nilary HY DUs: JobScheduler + RateLimitStore lift fully" {
                withEnv
                    [
                        "TOOLUP_JOB_SCHEDULER", Some "enabled"
                        "TOOLUP_RATE_LIMIT_STORE", Some "external"
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.equal cfg.JobScheduler InProcessJobScheduler "JobScheduler=enabled → InProcess"
                        Expect.equal cfg.RateLimitStore ExternalRateLimitStore "RateLimitStore=external")

                withEnv [ "TOOLUP_JOB_SCHEDULER", None; "TOOLUP_RATE_LIMIT_STORE", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.JobScheduler NoJobScheduler "JobScheduler default"
                    Expect.equal cfg.RateLimitStore NoRateLimitStore "RateLimitStore default")
            }

            test "EventStore: inmemory / persistent both selectable (persistent → 90-day retention)" {
                withEnv [ "TOOLUP_EVENT_STORE", Some "persistent" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty

                    Expect.equal
                        cfg.EventStore
                        (PersistentBlobBacked EventRetentionPolicy.ninetyDays)
                        "persistent → PersistentBlobBacked(ninetyDays)")

                withEnv [ "TOOLUP_EVENT_STORE", Some "inmemory" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.EventStore InMemoryOnly "inmemory → InMemoryOnly")
            }

            test "Hybrid disable direction: 'no' selects the nilary case; unset → default" {
                withEnv
                    [
                        "TOOLUP_PUBLIC_RENDERING", Some "no"
                        "TOOLUP_DATA_SUBJECT_REQUESTS", Some "disabled"
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.equal cfg.PublicRendering NoPublicRendering "PublicRendering=no"
                        Expect.equal cfg.DataSubjectRequests DataSubjectRequestMode.Disabled "DSR=disabled")

                withEnv [ "TOOLUP_PUBLIC_RENDERING", None; "TOOLUP_CONVERSATION_STORE", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.equal cfg.PublicRendering ServerConfig.defaults.PublicRendering "unset → default"
                    Expect.equal cfg.ConversationStore ServerConfig.defaults.ConversationStore "unset → default")
            }

            test "Hybrid enable direction with a payload-bearing case fails loud" {
                for envName in
                    [
                        "TOOLUP_PUBLIC_RENDERING"
                        "TOOLUP_CONVERSATION_STORE"
                        "TOOLUP_DATA_SUBJECT_REQUESTS"
                    ] do
                    withEnv [ envName, Some "enabled" ] (fun () ->
                        Expect.throws
                            (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty |> ignore)
                            $"{envName}=enabled must fail loud (payload not env-expressible)")
            }

            // ── Phase 170 — module-binding trust anchors ──
            test "ModuleBindingTrust: unset → no anchors + AllowUnbound true (GP 13 default)" {
                withEnv
                    [
                        "TOOLUP_MODULE_BINDING_ANCHORS", None
                        "TOOLUP_MODULE_BINDING_ALLOW_UNBOUND", None
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.isEmpty cfg.ModuleBindingTrust.Anchors "no anchors by default"
                        Expect.isTrue cfg.ModuleBindingTrust.AllowUnbound "AllowUnbound defaults true (binding off)")
            }

            test "ModuleBindingTrust: ANCHORS parses mac + asym entries; ALLOW_UNBOUND=false flips the bit" {
                withEnv
                    [
                        "TOOLUP_MODULE_BINDING_ANCHORS", Some "mac:k1:_platform:mac-secret-1;asym:k2:EcdsaP256:QUJD"
                        "TOOLUP_MODULE_BINDING_ALLOW_UNBOUND", Some "false"
                    ]
                    (fun () ->
                        let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                        Expect.isFalse cfg.ModuleBindingTrust.AllowUnbound "AllowUnbound=false"

                        Expect.equal
                            cfg.ModuleBindingTrust.Anchors
                            [
                                SymmetricAnchorRef("k1", "_platform", "mac-secret-1")
                                AsymmetricAnchorRef("k2", "EcdsaP256", "QUJD")
                            ]
                            "both anchor kinds parsed in order")
            }

            test "ModuleBindingTrust: a malformed anchor entry warns and is skipped" {
                withEnv [ "TOOLUP_MODULE_BINDING_ANCHORS", Some "mac:only-two;mac:k1:_platform:s1" ] (fun () ->
                    let logger, warnings = capturingLogger ()
                    let cfg = ServerConfig.fromEnv logger ServerConfigOverrides.empty

                    Expect.equal
                        cfg.ModuleBindingTrust.Anchors
                        [ SymmetricAnchorRef("k1", "_platform", "s1") ]
                        "only the well-formed entry survives"

                    Expect.isTrue
                        (warnings |> Seq.exists (fun w -> w.Contains "MODULE_BINDING_ANCHORS"))
                        "must warn on the malformed entry")
            }

            // ── Phase 460 — the share-token ephemeral-key acknowledgement ──
            //
            // Kept out of `sixAcceptFlags` above: that list is a fixed
            // historical set (the flags 71.A.2 found unread), and growing it
            // would rename what it records. Same GP 11 shape, pinned in both
            // directions because a flag that reads as `true` when unset would
            // silently disarm the refusal this phase exists to add.
            test "AcceptEphemeralShareTokenKey: TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1 lifts to true" {
                withEnv [ "TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY", Some "1" ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isTrue cfg.AcceptEphemeralShareTokenKey "the env var must be read")
            }

            test "AcceptEphemeralShareTokenKey: unset → false (the refusal stays armed)" {
                withEnv [ "TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY", None ] (fun () ->
                    let cfg = ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty
                    Expect.isFalse cfg.AcceptEphemeralShareTokenKey "unset must not acknowledge anything")
            }
        ]
    )