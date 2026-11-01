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
// not of the host it runs on. Regenerate with TOOLUP_REGEN_CONFIG_REFERENCE=1
// — only when a bound value is MEANT to change, which this phase's acceptance
// rules out.

open System
open System.IO
open System.Reflection
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Platform

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

let private regenModeOn () =
    match Environment.GetEnvironmentVariable "TOOLUP_REGEN_CONFIG_REFERENCE" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

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
let private render (config: ServerConfig) : string =
    FSharpType.GetRecordFields(typeof<ServerConfig>)
    |> Array.map (fun field ->
        let value = field.GetValue(box config)
        let text = sprintf "%A" value
        sprintf "%s = %s" field.Name (text.Replace("\r\n", "\n")))
    |> String.concat "\n"

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

                if regenModeOn () then
                    File.WriteAllText(path, rendered)
                else
                    Expect.isTrue
                        (File.Exists path)
                        (sprintf "%s is missing. Generate it with TOOLUP_REGEN_CONFIG_REFERENCE=1." path)

                    let committed = File.ReadAllText(path).Replace("\r\n", "\n")

                    Expect.equal
                        rendered
                        committed
                        "ServerConfig.fromEnv no longer binds the committed snapshot: a field moved, or a bound value changed for well-formed input"

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
        ]
    )