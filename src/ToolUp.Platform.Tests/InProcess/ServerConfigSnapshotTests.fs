module ToolUp.Platform.Tests.InProcess.ServerConfigSnapshotTests

// `ServerConfig.fromEnv` pinned by a snapshot taken BEFORE the
// binder was moved onto the typed config-key registry, so the move could be
// shown to leave `ServerConfig`'s shape and every bound value unchanged for
// well-formed input.
//
// Three cases: nothing set, every key the binder reads set to a well-formed
// non-default value, and nothing set over the reference app's overrides
// record (the override middle tier). Each renders every `ServerConfig` field
// by name, in declaration order, so a moved, added or re-typed field shows as
// a diff here too.
//
// Every registered key is unset for the duration of a case (and restored
// after), so the snapshot is a function of the fixed environment below and
// not of the host it runs on. There is no regeneration mode: a mismatch
// writes what was bound to a temporary file the failure names, and replacing
// the fixture with it is a deliberate act — for a bound value that is MEANT
// to change.

open System
open System.IO
open System.Reflection
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Jobs
open ToolUp.Platform.Audit
open ToolUp.Platform.Audit.Consent
open ToolUp.Platform.Compose

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private snapshotPath () =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Tests", "fixtures", "server-config-fromenv-snapshot.txt")

/// One well-formed, non-default value for every key `fromEnv` reads. A key
/// that selects a payload-carrying case it cannot construct from a token
/// (`enabled` on the conversation store, public rendering and data-subject
/// requests) is given another of its accepted tokens.
let private fixedEnvironment: (string * string) list = [
    "SERVER_PORT", "8137"
    "SERVER_BIND_ADDRESS", "0.0.0.0"
    "TOOLUP_ACCEPT_ANONYMOUS_MODE_WITH_AI", "1"
    "TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX", "true"
    "TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY", "yes"
    "TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY", "on"
    "TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE", "1"
    "TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE", "1"
    "TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE", "1"
    "TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE", "1"
    "TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE", "1"
    "TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY", "1"
    "TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE", "1"
    "TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE", "1"
    "TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE", "1"
    "TOOLUP_ACCEPT_PLAINTEXT_SECRETS", "1"
    "TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE", "1"
    "TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE", "1"
    "TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE", "1"
    "TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE", "1"
    "TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE", "1"
    "TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE", "1"
    "TOOLUP_AD_ANALYTICS", "enabled"
    "TOOLUP_ASSET_STORE", "on"
    "TOOLUP_AUDIT_FAILURE_POLICY", "refuse"
    "TOOLUP_AUDIT_LOG", "yes"
    "TOOLUP_AUTH_COOKIE_ISSUANCE", "on"
    "TOOLUP_BACKFILL_MISSED_TICKS", "true"
    "TOOLUP_COLUMN_MAPPING", "enabled"
    "TOOLUP_COMPUTE_BUDGET", "enabled"
    "TOOLUP_CONFIG_DRIFT_DETECTION", "enabled"
    "TOOLUP_CONSENT_AUDIT", "enabled"
    "TOOLUP_CONSENT_STATE_STORE", "entity"
    "TOOLUP_CONVERSATION_STORE", "off"
    "TOOLUP_DATA_INGESTION", "enabled"
    "TOOLUP_DATA_SUBJECT_REQUESTS", "no"
    "TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES", "1048576"
    "TOOLUP_DEPLOYMENT_READINESS", "enabled"
    "TOOLUP_DEPLOYMENT_VERIFICATION", "enabled"
    "TOOLUP_ENABLE_CITATION_DEV_ENDPOINT", "on"
    "TOOLUP_ENABLE_DEV_ENDPOINTS", "yes"
    "TOOLUP_ENTITY_OUTBOX", "enabled"
    "TOOLUP_ENTITY_STORE", "enabled"
    "TOOLUP_EVENT_STORE", "persistent"
    "TOOLUP_EVENT_TRIGGER_CATCHUP", "true"
    "TOOLUP_EXTERNAL_CONTACT_STORE", "enabled"
    "TOOLUP_HEALTH_STATE_TRACKING", "true"
    "TOOLUP_INCLUDE_PLATFORM_DEFAULTS", "false"
    "TOOLUP_JOB_SCHEDULER", "enabled"
    "TOOLUP_LINEAGE", "enabled"
    "TOOLUP_LINEAGE_DURABILITY", "enabled"
    "TOOLUP_LOG_LEVEL", "debug"
    "TOOLUP_MAPPING_DRYRUN_BLOCK", "enabled"
    "TOOLUP_MAX_REQUEST_BODY_BYTES", "2097152"
    "TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE", "25"
    "TOOLUP_METRICS_ENDPOINT", "enabled"
    "TOOLUP_MIGRATE_WEBHOOK_SECRETS", "true"
    "TOOLUP_MODULE_BINDING_ALLOW_UNBOUND", "true"
    "TOOLUP_MODULE_BINDING_ANCHORS", "asym:k1:ed25519:AAAA;mac:k2:platform:binding-key"
    "TOOLUP_MODULE", "survey"
    "TOOLUP_NOTIFY_INVITER_ON_INVITE_EXPIRY", "true"
    "TOOLUP_OAUTH_REFRESHER", "enabled"
    "TOOLUP_PEER_ROUTE_PREFIXES", "/peer,/fed"
    "TOOLUP_PLATFORM_KNOWLEDGE_BASE", "enabled"
    "TOOLUP_PLATFORM_SURFACES", "team,multi_team"
    "TOOLUP_PROCESS_PROFILE", "web"
    "TOOLUP_PUBLIC_BASE_URL", "https://app.example.com"
    "TOOLUP_PUBLIC_PATH", "/srv/public"
    "TOOLUP_PUBLIC_RENDERING", "disabled"
    "TOOLUP_RATE_LIMIT_PERMITS", "100"
    "TOOLUP_RATE_LIMIT_QUEUE", "5"
    "TOOLUP_RATE_LIMIT_STORE", "external"
    "TOOLUP_RATE_LIMIT_WINDOW_SECONDS", "60"
    "TOOLUP_RATE_LIMITER", "enabled"
    "TOOLUP_REPLICA_COUNT", "3"
    "TOOLUP_REQUIRE_DIRECTORY_PROOF_FOR_DIRECT_ADD", "true"
    "TOOLUP_REQUIRE_HTTPS", "true"
    "TOOLUP_RESULT_STORE", "persistent"
    "TOOLUP_SECURITY_HARDENING", "strict"
    "TOOLUP_SERVERLESS_HOST", "serverless"
    "TOOLUP_SHARE_TOKEN_STORE", "enabled"
    "TOOLUP_SKIP_PREFLIGHT", "1"
    "TOOLUP_SLOW_RATE_LIMIT_MS", "750"
    "TOOLUP_SLOW_REQUEST_MS", "2500"
    "TOOLUP_SMOKE_TEST", "enabled"
    "TOOLUP_SSE_AUTH", "cookie"
    "TOOLUP_STATIC_PATH_BEHAVIOUR", "require"
    "TOOLUP_STORE_EVICTION_MINUTES", "2.5"
    "TOOLUP_TEAM_CREATION_POLICY", "any"
    "TOOLUP_TRACE_CATEGORIES", "auth,db"
    "TOOLUP_TRUST_FORWARDED_HEADERS", "true"
    "TOOLUP_TRUSTED_PROXY_CIDRS", "10.0.0.0/8;192.168.0.0/16"
    "TOOLUP_USAGE_METERING", "enabled"
    "TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS", "hooks.example.com"
    "TOOLUP_WEBHOOKS", "enabled"
]

/// Every name a case clears before it runs: the registry, the fixed
/// environment (which names the two `SERVER_*` keys), deduplicated.
let private clearedNames () =
    (ConfigKeys.all |> List.map _.EnvVar) @ (fixedEnvironment |> List.map fst)
    |> List.distinct

let private withEnvironment (pairs: (string * string) list) (body: unit -> 'T) : 'T =
    let names = clearedNames ()
    let priors = names |> List.map (fun n -> n, Environment.GetEnvironmentVariable n)

    try
        for n in names do
            Environment.SetEnvironmentVariable(n, null)

        for n, v in pairs do
            Environment.SetEnvironmentVariable(n, v)

        body ()
    finally
        for n, prior in priors do
            Environment.SetEnvironmentVariable(n, prior)

/// Every field of `config`, by name, in declaration order.
// ── Phase 877 — the flat rendering, projected from the nested record ──
//
// The fixture was taken before `ServerConfig` was nested by concern and
// before the payload-free modes became `FeatureMode`, and it is NOT
// regenerated to match: it is the oracle that the same environment still
// binds the same effective configuration. So the render walks the FLAT
// field list below in the fixture's order, reads each field through its
// concern (`Codemod.concernFields` — the same table the consumer codemod
// nests by, so the two cannot disagree), and prints a `FeatureMode` as the
// case name the retired mode spelled (`Codemod.featureModes`). A field
// that moves, a field that is dropped, or a bound value that changes is a
// diff against unchanged bytes.

/// A flat field that kept its own type.
let private flat (name: string) : string * string option = name, None

/// A flat field whose type was one of the retired two-case modes.
let private mode (name: string) (retiredType: string) : string * string option = name, Some retiredType

/// Every field of the flat `ServerConfig`, in the order the fixture
/// renders them, with the retired mode type where there was one.
let private flatFields: (string * string option) list = [
    flat "Port"
    flat "BindAddress"
    flat "PublicPath"
    flat "Surfaces"
    flat "ModuleNames"
    flat "EventStore"
    mode "PlatformKnowledgeBase" "PlatformKnowledgeBaseMode"
    flat "AutoBootstrapDevAdmin"
    flat "ModuleConfigs"
    flat "IncludePlatformDefaults"
    flat "FeatureFlags"
    flat "ModuleFilter"
    flat "ModuleBindingTrust"
    flat "RequireHttps"
    flat "TrustForwardedHeaders"
    flat "TrustedProxyCidrs"
    flat "AcceptForwardedHeadersFromAnyProxy"
    flat "StaticPathBehaviour"
    flat "SlowRequestThreshold"
    flat "SlowRequestThresholdOverrides"
    flat "DefaultTeamStorageQuotaBytes"
    flat "RateLimit"
    flat "ResultStore"
    mode "Lineage" "LineageStoreMode"
    mode "LineageDurability" "LineageDurabilityMode"
    flat "JobScheduler"
    mode "JobProgress" "JobProgressMode"
    flat "BackfillMissedTicks"
    flat "EventTriggerCatchUp"
    mode "ShareTokenStore" "ShareTokenStoreMode"
    flat "ServiceAccounts"
    flat "PeerRoutePrefixes"
    flat "MaxRequestBodyBytes"
    flat "WebhookUrlAllowedHosts"
    flat "MigrateWebhookSecretsAtRest"
    flat "PublicBaseUrl"
    mode "DataIngestion" "DataIngestionMode"
    mode "ColumnMapping" "ColumnMappingMode"
    flat "MappingDryRun"
    mode "OAuthRefresher" "OAuthRefresherMode"
    mode "OAuth1a" "OAuth1aMode"
    mode "EntityStore" "EntityStoreMode"
    mode "EntityOutbox" "EntityOutboxMode"
    flat "SeedData"
    flat "GraphStore"
    mode "EntityGraphProjection" "EntityGraphProjectionMode"
    flat "TimeSeriesStore"
    flat "DataMigrations"
    flat "ConfigMigrations"
    flat "Datasets"
    flat "SessionRegistry"
    mode "ModelFitting" "ModelFittingMode"
    mode "ModelExecution" "ModelExecutionApiMode"
    mode "ExternalCompute" "ExternalComputeMode"
    mode "ComputeBudget" "ComputeBudgetMode"
    mode "TelemetrySink" "TelemetrySinkMode"
    mode "UsageMetering" "UsageMeteringMode"
    mode "MetricsEndpoint" "MetricsEndpointMode"
    flat "MetricsSink"
    mode "Webhooks" "WebhookMode"
    mode "AuditLog" "AuditLogMode"
    flat "AuditSamplingPolicy"
    flat "AuditFailurePolicy"
    flat "AuditFallbackDirectory"
    flat "Notifications"
    flat "SecurityHeaders"
    flat "SecurityHardening"
    flat "Cors"
    flat "EnableDevEndpoints"
    flat "EnableCitationDevEndpoint"
    flat "SkipPreflight"
    flat "HealthStateTracking"
    flat "AlertRules"
    flat "NotificationPreferences"
    mode "ExternalContactStore" "ExternalContactStoreMode"
    flat "NotificationCategories"
    flat "LogLevel"
    flat "TraceCategories"
    flat "SseAuthMode"
    mode "AuthCookieIssuance" "AuthCookieIssuanceMode"
    flat "MaxSseConnectionsPerScope"
    flat "AcceptHeaderAuthWhenAuthRequired"
    flat "AcceptPlaintextSecretsWhenAuthRequired"
    flat "ReplicaCount"
    flat "AcceptInProcessSchedulerInMultiInstance"
    flat "AcceptInProcessIngestionInMultiInstance"
    flat "AcceptSharedEmbeddingCacheInTeamMode"
    flat "AcceptEphemeralRagIndex"
    flat "AcceptLocalEmbedderAtScale"
    flat "AcceptStickyRoutedAiInMultiInstance"
    flat "AcceptNoRateLimitWhenAuthRequired"
    flat "AcceptUnsignedPublishable"
    flat "AcceptQueryParamSseAuthWhenAuthRequired"
    flat "AcceptSameSiteOnlyCsrfWhenAuthRequired"
    flat "AcceptUnboundAudienceWhenAuthRequired"
    flat "AcceptInMemoryOAuthStateInMultiInstance"
    flat "AcceptInMemoryShareTokenRateLimiterInMultiInstance"
    flat "AcceptPendingInviteStoreInMultiInstance"
    flat "AcceptInviteByEmailWithoutDirectory"
    flat "NotifyInviterOnInviteExpiry"
    flat "AcceptEphemeralShareTokenKey"
    flat "EphemeralStoreEvictionMinutes"
    flat "NotifyOnSessionStoreReset"
    flat "DataSubjectRequests"
    mode "ConfigDriftDetection" "ConfigDriftDetectionMode"
    mode "RateLimiter" "RateLimiterMode"
    flat "SlowRateLimitThreshold"
    mode "SmokeTest" "SmokeTestMode"
    flat "ConversationStore"
    flat "PublicRendering"
    mode "AssetStore" "AssetStoreMode"
    mode "MediaLibrary" "MediaLibraryMode"
    flat "DeployPlane"
    flat "ServerlessHost"
    flat "ProcessProfile"
    flat "RateLimitStore"
    flat "RateLimits"
    flat "ResourceEnvelopes"
    mode "ConsentAudit" "ConsentAuditMode"
    flat "ConsentStateStore"
    mode "AdAnalytics" "AdAnalyticsMode"
    flat "TeamCreationPolicy"
    flat "TeamCreationQuota"
    flat "DirectAddIdentityProof"
    flat "NarrativeRetention"
    mode "PeerSubstrate" "PeerSubstrateMode"
    mode "UserSchemaAuthoring" "UserSchemaAuthoringMode"
    mode "FactStore" "FactStoreMode"
    mode "TenantLifecycle" "TenantLifecycleMode"
    flat "TenantOffboardConfirmation"
    mode "DeploymentReadiness" "DeploymentReadinessMode"
    mode "DeploymentVerification" "DeploymentVerificationMode"
    flat "RegisteredLocales"
    flat "I18nCoverageMode"
    mode "Presence" "PresenceMode"
    flat "CrdtDocuments"
    flat "ModuleVisibility"
    flat "AdminMutationPolicy"
    flat "GrantConsent"
    flat "Backup"
    flat "PinnedVocabularyPacks"
    flat "DeclaredDataSchemas"
    flat "ExpectedModules"
    flat "AcceptAnonymousModeWithAI"
    flat "LogStore"
    flat "MetricsHistory"
    flat "DatadogReadback"
]

let private concernOf: Map<string, string> =
    Codemod.concernFields
    |> List.collect (fun (concern, fields) -> fields |> List.map (fun f -> f, concern))
    |> Map.ofList

let private legacyCases: Map<string, string * string> =
    Codemod.featureModes
    |> List.map (fun (retiredType, off, on) -> retiredType, (off, on))
    |> Map.ofList

let private fieldOf (record: Type) (name: string) =
    FSharpType.GetRecordFields record
    |> Array.tryFind (fun f -> f.Name = name)
    |> Option.defaultWith (fun () -> failwithf "%s has no field %s" record.Name name)

/// The flat field's value, read through its concern record when it has one.
let private readFlat (config: ServerConfig) (name: string) : obj =
    match concernOf.TryFind name with
    | None -> (fieldOf typeof<ServerConfig> name).GetValue(box config)
    | Some concern ->
        let nested = (fieldOf typeof<ServerConfig> concern).GetValue(box config)
        (fieldOf (nested.GetType()) name).GetValue nested

let private render (config: ServerConfig) : string =
    flatFields
    |> List.map (fun (name, retiredType) ->
        let value = readFlat config name

        let text =
            match retiredType, value with
            | Some retired, (:? FeatureMode as m) ->
                let off, on = legacyCases[retired]
                if FeatureMode.isEnabled m then on else off
            | Some retired, other ->
                failwithf "%s was a %s and is rendered as a FeatureMode, but holds %A" name retired other
            | None, other -> sprintf "%A" other

        sprintf "%s = %s" name (text.Replace("\r\n", "\n")))
    |> String.concat "\n"

/// Every field the nested record declares, as (concern option, name).
let private nestedFields () =
    let concerns = Codemod.concernFields |> List.map fst |> Set.ofList

    [
        for f in FSharpType.GetRecordFields typeof<ServerConfig> do
            if concerns.Contains f.Name then
                for inner in FSharpType.GetRecordFields f.PropertyType do
                    yield Some f.Name, inner.Name
            else
                yield None, f.Name
    ]

let private cases () = [
    "no environment, empty overrides",
    (fun () -> withEnvironment [] (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty))
    "fixed environment, empty overrides",
    (fun () ->
        withEnvironment fixedEnvironment (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty))
    "no environment, reference-app overrides",
    (fun () -> withEnvironment [] (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.referenceApp))
]

let private renderAll () =
    cases ()
    |> List.map (fun (name, build) -> sprintf "### %s\n%s\n" name (render (build ())))
    |> String.concat "\n"

let tests =
    testSequenced (
        testList "ServerConfig.fromEnv snapshot" [
            testCase "fromEnv binds exactly the committed snapshot"
            <| fun _ ->
                let rendered = renderAll ()

                Expect.isFalse
                    (rendered.Contains "<fun:")
                    "a rendered field holds a closure, whose %A text is not stable across builds — render that field another way"

                let path = snapshotPath ()

                Expect.isTrue (File.Exists path) (sprintf "%s is missing." path)
                let committed = File.ReadAllText(path).Replace("\r\n", "\n")

                if rendered <> committed then
                    let received =
                        Path.Combine(Path.GetTempPath(), "server-config-fromenv-snapshot.received.txt")

                    File.WriteAllText(received, rendered)

                    failtestf
                        "ServerConfig.fromEnv no longer binds the committed snapshot: a field moved, or a bound value changed for well-formed input. What it bound is at %s; diff it against %s."
                        received
                        path

            testCase "the fixed environment sets every key the binder reads to a value its reader accepts"
            <| fun _ ->
                // A value the reader warned about and replaced with its default
                // would pin the default twice and the value never: assert each
                // fixed value changed at least the case it selects.
                let unset =
                    withEnvironment [] (fun () -> ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty)

                let set =
                    withEnvironment fixedEnvironment (fun () ->
                        ServerConfig.fromEnv silentLogger ServerConfigOverrides.empty)

                Expect.notEqual set unset "the fixed environment bound nothing"

            testCase
                "the flat field list names every nested field exactly once, under the concern the codemod nests it by"
            <| fun _ ->
                let declared = nestedFields () |> List.map (fun (concern, name) -> name, concern)

                let projected =
                    flatFields |> List.map (fun (name, _) -> name, concernOf.TryFind name)

                Expect.equal
                    (List.sort projected)
                    (List.sort declared)
                    "a field was added, dropped or re-homed without the flat projection (and the codemod's concern table) following it"

                Expect.equal (List.length flatFields) 147 "the flat record had 147 fields"

            testCase
                "every retired mode the flat list names is in the codemod's mode table, and renders its legacy case names"
            <| fun _ ->
                for name, retired in flatFields do
                    match retired with
                    | None -> ()
                    | Some t ->
                        Expect.isTrue
                            (legacyCases.ContainsKey t)
                            (sprintf "%s: %s is not in Codemod.featureModes" name t)

                        let off, on = legacyCases[t]
                        Expect.stringStarts off "No" (sprintf "%s: the off case" t)
                        Expect.isTrue (on.StartsWith "Enabled" || on.StartsWith "Custom") (sprintf "%s: the on case" t)

                        Expect.equal
                            (FeatureModeKnob.legacyNames.TryFind name)
                            (Some(off, on))
                            (sprintf "%s: the knob table Core seals by agrees with the codemod's mode table" name)

                Expect.equal
                    (FeatureModeKnob.legacyNames.Count)
                    (flatFields |> List.filter (snd >> Option.isSome) |> List.length)
                    "the knob table names every FeatureMode field and no other"
        ]
    )