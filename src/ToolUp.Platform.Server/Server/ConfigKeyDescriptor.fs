// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConfigKeys

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

/// One environment-variable-backed config key the SDK reads at startup.
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

/// Canonical env-var name constants. The `*FromEnv` readers reference
/// these instead of inlining the string literal, so a rename is a
/// compile error that the reference doc can never silently lag behind.
[<RequireQualifiedAccess>]
module Names =
    // Storage & secrets
    let blobStorage = "TOOLUP_BLOB_STORAGE"
    let secretStore = "TOOLUP_SECRET_STORE"
    let secretsMasterKey = "TOOLUP_SECRETS_MASTER_KEY"
    let secretsPath = "TOOLUP_SECRETS_PATH"
    let azureStorageConnectionString = "TOOLUP_AZURE_STORAGE_CONNECTION_STRING"
    let awsS3Bucket = "TOOLUP_AWS_S3_BUCKET"
    let gcsBucket = "TOOLUP_GCS_BUCKET"

    // Auth & identity
    let authMode = "TOOLUP_AUTH_MODE"
    let oidcIssuer = "TOOLUP_OIDC_ISSUER"
    let oidcAudience = "TOOLUP_OIDC_AUDIENCE"
    let sseAuth = "TOOLUP_SSE_AUTH"
    let initialPlatformAdmin = "TOOLUP_INITIAL_PLATFORM_ADMIN"
    let adminToken = "TOOLUP_ADMIN_TOKEN"
    let allowDevAdminBootstrap = "TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP"
    let initialTeamName = "TOOLUP_INITIAL_TEAM_NAME"
    let initialTeamId = "TOOLUP_INITIAL_TEAM_ID"
    let oauthRedirectBase = "TOOLUP_OAUTH_REDIRECT_BASE"

    // Logging & observability
    let logLevel = "TOOLUP_LOG_LEVEL"
    let logFormat = "TOOLUP_LOG_FORMAT"
    let traceCategories = "TOOLUP_TRACE_CATEGORIES"
    let appName = "TOOLUP_APP_NAME"

    // Deployment shape
    let replicaCount = "TOOLUP_REPLICA_COUNT"
    let notificationChannel = "TOOLUP_NOTIFICATION_CHANNEL"
    let redisConnection = "TOOLUP_REDIS_CONNECTION"
    let requireHttps = "TOOLUP_REQUIRE_HTTPS"
    let trustForwardedHeaders = "TOOLUP_TRUST_FORWARDED_HEADERS"
    let maxRequestBodyBytes = "TOOLUP_MAX_REQUEST_BODY_BYTES"
    let maxFileBytes = "TOOLUP_MAX_FILE_BYTES"
    let smokeToken = "TOOLUP_SMOKE_TOKEN"
    let auditAdminRequired = "TOOLUP_AUDIT_ADMIN_REQUIRED"

    // Security preflight escape hatches — each lowers a specific
    // `IConfigValidator` refusal to a warning. Documented so an operator
    // can see the full list of "I know what I'm doing" overrides.
    let acceptLocalFallback = "TOOLUP_ACCEPT_LOCAL_FALLBACK"
    let acceptHeaderAuthInAuthMode = "TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE"
    let acceptUnboundAudienceInAuthMode = "TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE"

    let acceptSameSiteOnlyCsrfInAuthMode =
        "TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE"

    let acceptNoRateLimitInAuthMode = "TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE"

    let acceptQueryParamSseAuthInAuthMode =
        "TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE"

    let acceptInviteByEmailWithoutDirectory =
        "TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY"

    let acceptPendingInviteStoreMultiInstance =
        "TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE"

    let acceptInMemoryOAuthStateMultiInstance =
        "TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE"

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
        EnvVar = Names.redisConnection
        Description =
            "Redis connection string for the distributed notification channel / caches used when TOOLUP_NOTIFICATION_CHANNEL=redis."
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
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptHeaderAuthInAuthMode
        Description =
            "Acknowledge running the spoofable HeaderAuthProvider in an authenticated mode (only safe behind a mTLS proxy)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptUnboundAudienceInAuthMode
        Description = "Acknowledge an unset OIDC audience in an authenticated mode (token-reuse risk)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptSameSiteOnlyCsrfInAuthMode
        Description = "Acknowledge relying on SameSite cookies alone (no server-side CSRF token) for cookie auth."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptNoRateLimitInAuthMode
        Description = "Acknowledge an internet-facing authenticated deployment with no rate limiting."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptQueryParamSseAuthInAuthMode
        Description =
            "Acknowledge SSE query-param auth fallback in an authenticated mode (leaks the userId in URLs/logs)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptInviteByEmailWithoutDirectory
        Description =
            "Acknowledge a team invite-by-email surface mounted with no IUserDirectory (emails silently never send)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptPendingInviteStoreMultiInstance
        Description =
            "Acknowledge the in-memory pending-invite store under a multi-instance deployment (per-replica drift)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
    {
        EnvVar = Names.acceptInMemoryOAuthStateMultiInstance
        Description =
            "Acknowledge the in-memory OAuth state store under a multi-instance deployment (callback may hit a replica without the state)."
        Type = BoolKey
        Default = Some "false"
        IsSecret = false
        Category = "Security preflight escape hatches"
    }
]

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
                "     of truth is `ConfigKeys.all` in src/ToolUp.Platform.Server/Server/ConfigKeyDescriptor.fs. -->"
            )
        |> ignore

        sb.AppendLine "" |> ignore

        sb.AppendLine(
            sprintf
                "Every environment variable the platform reads at startup, projected from the central config-key registry (%d keys). Run `--print-config` to see the effective resolved value of each on a running deployment, or `--validate-config` to run the startup preflight without booting."
                keys.Length
        )
        |> ignore

        sb.AppendLine "" |> ignore

        for category in orderedCategories keys do
            sb.AppendLine(sprintf "## %s" category) |> ignore
            sb.AppendLine "" |> ignore
            sb.AppendLine "| Env var | Type | Default | Secret | Description |" |> ignore
            sb.AppendLine "|---|---|---|---|---|" |> ignore

            let inCategory =
                keys |> List.filter (fun k -> k.Category = category) |> List.sortBy _.EnvVar

            for k in inCategory do
                let defaultCell =
                    match k.Default with
                    | Some d -> cell d
                    | None -> "—"

                let secretCell = if k.IsSecret then "yes" else "no"

                sb.AppendLine(
                    sprintf
                        "| `%s` | %s | %s | %s | %s |"
                        k.EnvVar
                        (cell (typeLabel k.Type))
                        defaultCell
                        secretCell
                        (cell k.Description)
                )
                |> ignore

            sb.AppendLine "" |> ignore

        // Normalise to `\n` so the golden-file comparison is stable
        // across platforms (StringBuilder.AppendLine emits the platform
        // newline; the committed file is `\n`).
        sb.ToString().Replace("\r\n", "\n")