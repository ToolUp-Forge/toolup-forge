# Configuration reference

<!-- GENERATED FILE — do not edit by hand. Regenerate with `dev-scripts/generate-config-reference.ps1`
     (or `TOOLUP_REGEN_CONFIG_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests`). The source
     of truth is `ConfigKeys.all` in src/ToolUp.Platform.Server/Server/ConfigKeyDescriptor.fs. -->

Every environment variable the platform reads at startup, projected from the central config-key registry (39 keys). Run `--print-config` to see the effective resolved value of each on a running deployment, or `--validate-config` to run the startup preflight without booting.

## Storage & secrets

| Env var | Type | Default | Secret | Description |
|---|---|---|---|---|
| `TOOLUP_AWS_S3_BUCKET` | string | — | no | Target S3 bucket name used when TOOLUP_BLOB_STORAGE=s3. |
| `TOOLUP_AZURE_STORAGE_CONNECTION_STRING` | string | — | yes | Azure Blob Storage connection string used when TOOLUP_BLOB_STORAGE=azure. |
| `TOOLUP_BLOB_STORAGE` | enum: local, azure, s3, gcs | local | no | Selects the IBlobStorage backend. Unrecognised / cloud-without-credentials values warn and fall back to local. |
| `TOOLUP_GCS_BUCKET` | string | — | no | Target Google Cloud Storage bucket name used when TOOLUP_BLOB_STORAGE=gcs. |
| `TOOLUP_SECRETS_MASTER_KEY` | string | — | yes | Base64-encoded 32-byte master key for the encrypted local secret store. Unset stores secrets as plaintext at rest (preflight warns). |
| `TOOLUP_SECRETS_PATH` | string | — | no | Filesystem path the file/encrypted secret store reads and writes secrets under. |
| `TOOLUP_SECRET_STORE` | enum: encrypted, file, env, azure-key-vault, aws-secrets-manager, gcp-secret-manager, vault | encrypted | no | Selects the ISecretStore backend. Cloud values require their companion's own env vars; unset uses the encrypted local file store. |

## Auth & identity

| Env var | Type | Default | Secret | Description |
|---|---|---|---|---|
| `TOOLUP_ADMIN_TOKEN` | string | — | yes | Bearer token guarding the crypto-shred encryption-admin endpoints. Unset leaves those endpoints unmounted (preflight warns if the surface is composed). |
| `TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP` | bool | false | no | When true in an auth-requiring mode, the first sign-in auto-promotes to Platform Admin (privilege-escalation surface; preflight warns). |
| `TOOLUP_AUTH_MODE` | enum: oidc | (unset — dev HeaderAuthProvider) | no | Selects the IAuthProvider. Unset uses the dev-only HeaderAuthProvider (trusts X-User-Id); 'oidc' requires TOOLUP_OIDC_ISSUER. An unrecognised value refuses startup. |
| `TOOLUP_INITIAL_PLATFORM_ADMIN` | string | — | no | User id (OIDC sub/oid) granted Platform Admin on first boot when no admin exists yet. |
| `TOOLUP_INITIAL_TEAM_ID` | string | — | no | Stable id of the bootstrap team created on first boot. |
| `TOOLUP_INITIAL_TEAM_NAME` | string | — | no | Display name of the bootstrap team created on first boot. |
| `TOOLUP_OAUTH_REDIRECT_BASE` | string | — | no | Absolute base URL used to build OAuth-connector redirect URIs (must match the provider's registered callback origin). |
| `TOOLUP_OIDC_AUDIENCE` | string | — | no | Expected OIDC token audience. Unset accepts any audience (preflight warns in authenticated modes). |
| `TOOLUP_OIDC_ISSUER` | string | — | no | OIDC provider discovery URL. Required when TOOLUP_AUTH_MODE=oidc; missing issuer refuses startup. |
| `TOOLUP_SSE_AUTH` | enum: cookie, cookies, cookieonly | (unset — bearer header only) | no | When set to a cookie value, the OIDC provider also accepts the JWT from the toolup-auth-token cookie so EventSource SSE handshakes authenticate. Unset keeps bearer-header-only. |

## Logging & observability

| Env var | Type | Default | Secret | Description |
|---|---|---|---|---|
| `TOOLUP_APP_NAME` | string | — | no | Display name the platform shell and startup banner present for this deployment. |
| `TOOLUP_LOG_FORMAT` | enum: text, json | text | no | Selects the default logger's output shape: human-readable text or structured JSON lines. |
| `TOOLUP_LOG_LEVEL` | enum: Debug, Info, Warn, Error | Info | no | Floor for the default ConsoleLogger. Error is never silenced. An unrecognised value warns and uses Info. |
| `TOOLUP_TRACE_CATEGORIES` | string | — | no | Comma/space-separated whitelist of trace categories to emit (e.g. ai.sse,platform.sse). Empty emits no Trace output. |

## Deployment shape

| Env var | Type | Default | Secret | Description |
|---|---|---|---|---|
| `TOOLUP_AUDIT_ADMIN_REQUIRED` | bool | false | no | When true, audit-log read endpoints require Platform Admin rather than team-level access. |
| `TOOLUP_MAX_FILE_BYTES` | int | — | no | Maximum accepted upload size in bytes for file-management endpoints. |
| `TOOLUP_MAX_REQUEST_BODY_BYTES` | int | — | no | Kestrel per-request body cap in bytes. Unset leaves the framework's 30 MB default. |
| `TOOLUP_NOTIFICATION_CHANNEL` | enum: inmemory, redis | inmemory | no | Selects the INotificationChannel backend. 'redis' requires TOOLUP_REDIS_CONNECTION; unset uses the single-instance in-memory channel. |
| `TOOLUP_REDIS_CONNECTION` | string | — | yes | Redis connection string for the distributed notification channel / caches used when TOOLUP_NOTIFICATION_CHANNEL=redis. |
| `TOOLUP_REPLICA_COUNT` | int | 1 | no | Number of instances this deployment runs behind a load balancer. >1 makes multi-instance config validators refuse single-instance substrates. |
| `TOOLUP_REQUIRE_HTTPS` | bool | false | no | When true, the platform enforces HTTPS (redirect + HSTS) for browser-facing surfaces. |
| `TOOLUP_SMOKE_TOKEN` | string | — | yes | Bearer token guarding the post-deploy smoke-test endpoint (GET /api/_internal/smoke). |
| `TOOLUP_TRUST_FORWARDED_HEADERS` | bool | false | no | When true, trusts X-Forwarded-* headers from the upstream proxy. Only safe behind a proxy that strips/re-injects them (preflight warns without RequireHttps). |

## Security preflight escape hatches

| Env var | Type | Default | Secret | Description |
|---|---|---|---|---|
| `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE` | bool | false | no | Acknowledge running the spoofable HeaderAuthProvider in an authenticated mode (only safe behind a mTLS proxy). |
| `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE` | bool | false | no | Acknowledge the in-memory OAuth state store under a multi-instance deployment (callback may hit a replica without the state). |
| `TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY` | bool | false | no | Acknowledge a team invite-by-email surface mounted with no IUserDirectory (emails silently never send). |
| `TOOLUP_ACCEPT_LOCAL_FALLBACK` | bool | false | no | Acknowledge a cloud-declared blob backend silently falling back to local storage (downgrades the refusal to a warning). |
| `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE` | bool | false | no | Acknowledge an internet-facing authenticated deployment with no rate limiting. |
| `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE` | bool | false | no | Acknowledge the in-memory pending-invite store under a multi-instance deployment (per-replica drift). |
| `TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE` | bool | false | no | Acknowledge SSE query-param auth fallback in an authenticated mode (leaks the userId in URLs/logs). |
| `TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE` | bool | false | no | Acknowledge relying on SameSite cookies alone (no server-side CSRF token) for cookie auth. |
| `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE` | bool | false | no | Acknowledge an unset OIDC audience in an authenticated mode (token-reuse risk). |

