# Configuration reference

<!-- GENERATED FILE — do not edit by hand. Regenerate with `dev-scripts/generate-config-reference.ps1`
     (or `TOOLUP_REGEN_CONFIG_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests`). The source
     of truth is `ConfigKeys.all` in src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs. -->

Every `TOOLUP_*` environment variable the SDK reads, projected from the central config-key registry (181 keys). Most are read at startup by `ServerConfig.fromEnv` or a companion's `create`; the "Build & tooling" section covers the few read by the build and analyzer instead. Run `--print-config` to see the effective resolved value and source of each on a running deployment, `--print-config --diff` for the non-default values only, or `--validate-config` to run the startup preflight without booting.

The **Manifest** column says whether a deployment configuration manifest may supply the key: `yes` (its reader resolves through the config-resolution seam), `pending` (registered, but its reader has not migrated yet — the manifest would state it and nothing would read it, so the loader warns), `never` (a secret; the manifest is refused outright, set the environment variable instead), `n/a` (the manifest cannot name its own location). Precedence is consumer literal > environment variable > manifest > override record > default.

## Storage & secrets

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_AWS_S3_BUCKET` | string | — | no | pending | Target S3 bucket name used when TOOLUP_BLOB_STORAGE=s3. |
| `TOOLUP_AWS_S3_ENDPOINT` | string | — | no | pending | Custom S3-compatible endpoint for the S3 blob-storage companion. |
| `TOOLUP_AWS_S3_REGION` | string | — | no | pending | AWS region for the S3 blob-storage companion. |
| `TOOLUP_AWS_SECRETS_REGION` | string | — | no | pending | AWS region for the Secrets Manager secret-store companion. |
| `TOOLUP_AZURE_KEY_VAULT_URL` | string | — | no | pending | Vault URL for the Azure Key Vault secret-store companion. |
| `TOOLUP_AZURE_STORAGE_CONNECTION_STRING` | string | — | yes | never | Azure Blob Storage connection string used when TOOLUP_BLOB_STORAGE=azure. |
| `TOOLUP_BLOB_STORAGE` | enum: local, azure, s3, gcs | local | no | pending | Selects the IBlobStorage backend. Unrecognised / cloud-without-credentials values warn and fall back to local. |
| `TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES` | int | — | no | yes | Default per-team storage quota in bytes. Unset means unlimited. |
| `TOOLUP_GCP_PROJECT_ID` | string | — | no | pending | GCP project id for the Secret Manager and Cloud Storage companions. |
| `TOOLUP_GCS_BUCKET` | string | — | no | pending | Target Google Cloud Storage bucket name used when TOOLUP_BLOB_STORAGE=gcs. |
| `TOOLUP_GCS_CREDENTIALS_JSON` | string | — | yes | never | Service-account credentials JSON for the Google Cloud Storage companion. |
| `TOOLUP_SECRETS_MASTER_KEY` | string | — | yes | never | Base64-encoded 32-byte master key for the encrypted local secret store. Unset stores secrets as plaintext at rest (preflight warns). |
| `TOOLUP_SECRETS_PATH` | string | — | no | pending | Filesystem path the file/encrypted secret store reads and writes secrets under. |
| `TOOLUP_SECRET_STORE` | enum: encrypted, file, env, azure-key-vault, aws-secrets-manager, gcp-secret-manager, vault | encrypted | no | pending | Selects the ISecretStore backend. Cloud values require their companion's own env vars; unset uses the encrypted local file store. |

## Auth & identity

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_ADMIN_TOKEN` | string | — | yes | never | Bearer token guarding the crypto-shred encryption-admin endpoints. Unset leaves those endpoints unmounted (preflight warns if the surface is composed). |
| `TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP` | bool | false | no | pending | When true in an auth-requiring mode, the first sign-in auto-promotes to Platform Admin (privilege-escalation surface; preflight warns). |
| `TOOLUP_AUTH_COOKIE_ISSUANCE` | enum: enabled, disabled | disabled | no | yes | Issues the platform auth cookie alongside the bearer token, so SSE can authenticate without a query parameter. |
| `TOOLUP_AUTH_MODE` | enum: oidc | (unset — dev HeaderAuthProvider) | no | pending | Selects the IAuthProvider. Unset uses the dev-only HeaderAuthProvider (trusts X-User-Id); 'oidc' requires TOOLUP_OIDC_ISSUER. An unrecognised value refuses startup. |
| `TOOLUP_ENTRA_DIRECTORY_ENABLED` | bool | false | no | pending | Enables the Entra directory companion for user lookup and invitation via Microsoft Graph. |
| `TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT` | string | — | no | pending | Microsoft Graph endpoint override for the Entra directory companion. |
| `TOOLUP_ENTRA_DIRECTORY_SENDER_OID` | string | — | no | pending | Object id of the principal used as the sender for directory invitations. |
| `TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE` | string | — | no | pending | Expected token audience for the Entra External ID auth provider. |
| `TOOLUP_ENTRA_EXTERNAL_ID_CLOCK_SKEW_SECONDS` | int | — | no | pending | Permitted clock skew, in seconds, when validating Entra tokens. |
| `TOOLUP_ENTRA_EXTERNAL_ID_CUSTOM_DOMAIN` | string | — | no | pending | Custom sign-in domain for the Entra External ID auth provider. |
| `TOOLUP_ENTRA_EXTERNAL_ID_SIGN_IN_POLICY` | string | — | no | pending | Sign-in user-flow policy id. |
| `TOOLUP_ENTRA_EXTERNAL_ID_SIGN_UP_POLICY` | string | — | no | pending | Sign-up user-flow policy id. |
| `TOOLUP_ENTRA_EXTERNAL_ID_TENANT` | string | — | no | pending | Entra External ID tenant name. |
| `TOOLUP_GITHUB_ALLOWED_ORGS` | string | — | no | pending | Comma-separated GitHub organisations whose members may sign in. Unset allows any account. |
| `TOOLUP_GITHUB_API_BASE_URL` | string | https://api.github.com | no | pending | GitHub API base URL. Override it for GitHub Enterprise. |
| `TOOLUP_GITHUB_AUTH` | bool | false | no | pending | Enables the GitHub OAuth auth provider. |
| `TOOLUP_GITHUB_CACHE_TTL_SECONDS` | int | — | no | pending | Seconds a resolved GitHub identity is cached. |
| `TOOLUP_GITHUB_FETCH_PRIMARY_EMAIL` | bool | false | no | pending | Additionally fetches the primary email address, which requires the user:email scope. |
| `TOOLUP_GITHUB_USER_AGENT` | string | — | no | pending | User-Agent sent on GitHub API calls. |
| `TOOLUP_INITIAL_PLATFORM_ADMIN` | string | — | no | pending | User id (OIDC sub/oid) granted Platform Admin on first boot when no admin exists yet. |
| `TOOLUP_INITIAL_TEAM_ID` | string | — | no | pending | Stable id of the bootstrap team created on first boot. |
| `TOOLUP_INITIAL_TEAM_NAME` | string | — | no | pending | Display name of the bootstrap team created on first boot. |
| `TOOLUP_LDAP_ALLOW_PLAINTEXT` | bool | false | no | pending | Allows an unencrypted LDAP connection, which sends bind credentials in the clear. |
| `TOOLUP_LDAP_ALLOW_UNTRUSTED_CERT` | bool | false | no | pending | Skips LDAP server certificate validation. |
| `TOOLUP_LDAP_AUTH` | bool | false | no | pending | Enables the LDAP / Active Directory auth provider. |
| `TOOLUP_LDAP_BIND_DN` | string | — | no | pending | DN of the service account used to bind before searching. |
| `TOOLUP_LDAP_BIND_SECRET_KEY` | string | — | no | pending | ISecretStore key holding the service-account bind password. The password itself is never read from the environment. |
| `TOOLUP_LDAP_CACHE_TTL_SECONDS` | int | — | no | pending | Seconds a resolved LDAP identity is cached. |
| `TOOLUP_LDAP_CERT_THUMBPRINT` | string | — | no | pending | Pins the LDAP server certificate to this thumbprint. |
| `TOOLUP_LDAP_CHANNEL` | string | — | no | pending | Transport security used for the LDAP connection. |
| `TOOLUP_LDAP_DISPLAY_ATTR` | string | — | no | pending | LDAP attribute the display name is read from. |
| `TOOLUP_LDAP_EMAIL_ATTR` | string | — | no | pending | LDAP attribute the email address is read from. |
| `TOOLUP_LDAP_HOST` | string | — | no | pending | LDAP server hostname. |
| `TOOLUP_LDAP_LOGIN_ATTR` | string | — | no | pending | LDAP attribute the login name is read from. |
| `TOOLUP_LDAP_MEMBEROF_ATTR` | string | — | no | pending | LDAP attribute the group membership is read from. |
| `TOOLUP_LDAP_NESTED_GROUPS` | bool | false | no | pending | Resolves nested group memberships when mapping roles. |
| `TOOLUP_LDAP_PORT` | int | — | no | pending | LDAP server port. Defaults to the standard port for the selected channel. |
| `TOOLUP_LDAP_SEARCH_BASE` | string | — | no | pending | Base DN for user searches. |
| `TOOLUP_LDAP_TIMEOUT_SECONDS` | int | — | no | pending | LDAP operation timeout, in seconds. |
| `TOOLUP_LDAP_USER_ID_ATTR` | string | — | no | pending | LDAP attribute the stable user id is read from. |
| `TOOLUP_LDAP_USER_OBJECTCLASS` | string | — | no | pending | objectClass used to filter user entries. |
| `TOOLUP_OAUTH_REDIRECT_BASE` | string | — | no | pending | Absolute base URL used to build OAuth-connector redirect URIs (must match the provider's registered callback origin). |
| `TOOLUP_OIDC_AUDIENCE` | string | — | no | pending | Expected OIDC token audience. Unset accepts any audience (preflight warns in authenticated modes). |
| `TOOLUP_OIDC_ISSUER` | string | — | no | pending | OIDC provider discovery URL. Required when TOOLUP_AUTH_MODE=oidc; missing issuer refuses startup. |
| `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` | int | — | no | pending | Milliseconds the OIDC preflight waits for the issuer discovery document. |
| `TOOLUP_SSE_AUTH` | enum: cookie, cookies, cookieonly | (unset — bearer header only) | no | yes | When set to a cookie value, the OIDC provider also accepts the JWT from the toolup-auth-token cookie so EventSource SSE handshakes authenticate. Unset keeps bearer-header-only. |
| `TOOLUP_TEAM_CREATION_POLICY` | enum: platformadminonly, anyauthenticateduser | platformadminonly | no | yes | Who may create a team: platform admins only, or any authenticated user. |

## Logging & observability

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_APP_NAME` | string | — | no | pending | Display name the platform shell and startup banner present for this deployment. |
| `TOOLUP_HEALTH_STATE_TRACKING` | bool | false | no | yes | Tracks health-check state transitions, so a probe can report how long a component has been unhealthy. |
| `TOOLUP_LOG_FORMAT` | enum: text, json | text | no | pending | Selects the default logger's output shape: human-readable text or structured JSON lines. |
| `TOOLUP_LOG_LEVEL` | enum: Debug, Info, Warn, Error | Info | no | yes | Floor for the default ConsoleLogger. Error is never silenced. An unrecognised value warns and uses Info. |
| `TOOLUP_METRICS_ENDPOINT` | enum: enabled, disabled | disabled | no | yes | Exposes the Prometheus-style scrape endpoint for the registered IMetricsSink. |
| `TOOLUP_SLOW_REQUEST_MS` | int | 1000 | no | yes | Milliseconds above which a request is logged as slow. |
| `TOOLUP_TRACE_CATEGORIES` | string | — | no | yes | Comma/space-separated whitelist of trace categories to emit (e.g. ai.sse,platform.sse). Empty emits no Trace output. |

## Deployment shape

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_AUDIT_ADMIN_REQUIRED` | bool | false | no | pending | When true, audit-log read endpoints require Platform Admin rather than team-level access. |
| `TOOLUP_COMPONENT__` | string | — | no | pending | Prefix for per-component config overrides, spelled TOOLUP_COMPONENT__ComponentId__Key. Not read as a variable in its own right. |
| `TOOLUP_CONFIG_FILE` | string | (unset — probes ./toolup.config.json) | no | n/a | Path to the deployment configuration manifest (JSON, keys are these env-var names). Set: the named file must exist. Unset: ./toolup.config.json is probed and used when present, else no manifest is loaded. |
| `TOOLUP_DISTRIBUTED_LOCK` | enum: inprocess, redis | inprocess | no | pending | Phase 9i — selects the IDistributedLock backend (the SDK-wide cross-instance lease primitive). 'redis' requires TOOLUP_REDIS_CONNECTION; unset uses InProcessDistributedLock, which is correct for a single instance and excludes nothing across replicas. Read by DistributedLockSelection.fromEnv, which the composition root threads its companion resolvers into. |
| `TOOLUP_ENABLE_DEV_ENDPOINTS` | bool | false | no | yes | Exposes the /dev/* inspection endpoints. Should stay off in production. |
| `TOOLUP_INCLUDE_PLATFORM_DEFAULTS` | bool | true | no | yes | Merges the SDK platform default config schema into the composed surface. |
| `TOOLUP_MAX_FILE_BYTES` | int | — | no | pending | Maximum accepted upload size in bytes for file-management endpoints. |
| `TOOLUP_MAX_REQUEST_BODY_BYTES` | int | — | no | yes | Kestrel per-request body cap in bytes. Unset leaves the framework's 30 MB default. |
| `TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE` | int | 10 | no | yes | Maximum concurrent SSE connections per scope. |
| `TOOLUP_MODULE` | string | — | no | yes | Restricts the composed surface to a single named module. Intended for local iteration. |
| `TOOLUP_NOTIFICATION_CHANNEL` | enum: inmemory, redis | inmemory | no | pending | Selects the INotificationChannel backend. 'redis' requires TOOLUP_REDIS_CONNECTION; unset uses the single-instance in-memory channel. |
| `TOOLUP_PEER_ROUTE_PREFIXES` | string | — | no | yes | Comma-separated route prefixes served by the cross-deployment peer substrate. |
| `TOOLUP_PLATFORM_SURFACES` | string | — | no | yes | Comma-separated surface profiles the deployment exposes, for example anonymous, user, multi-team or claim-bearer. |
| `TOOLUP_PROCESS_PROFILE` | enum: allinone, web, worker, dispatcher | allinone | no | yes | Which role this process plays when the deployment is split: everything, web only, worker only, or dispatcher only. |
| `TOOLUP_REDIS_CONNECTION` | string | — | yes | never | Redis connection string for the distributed notification channel / caches / distributed lock used when TOOLUP_NOTIFICATION_CHANNEL=redis or TOOLUP_DISTRIBUTED_LOCK=redis. |
| `TOOLUP_REPLICA_COUNT` | int | 1 | no | yes | Number of instances this deployment runs behind a load balancer. >1 makes multi-instance config validators refuse single-instance substrates. |
| `TOOLUP_REQUIRE_HTTPS` | bool | false | no | yes | When true, the platform enforces HTTPS (redirect + HSTS) for browser-facing surfaces. |
| `TOOLUP_SECURITY_HARDENING` | enum: no, default, strict | no | no | yes | Security-header and hardening posture applied to every response. |
| `TOOLUP_SERVERLESS_HOST` | enum: kestrel, serverless | kestrel | no | yes | Host shape the server assumes: the standard Kestrel host, or a serverless host that skips long-lived background services. |
| `TOOLUP_SKIP_PREFLIGHT` | bool | false | no | yes | Skips the entire startup config preflight. Intended for local iteration; a production deployment that sets it boots unvalidated. |
| `TOOLUP_SMOKE_TOKEN` | string | — | yes | never | Bearer token guarding the post-deploy smoke-test endpoint (GET /api/_internal/smoke). |
| `TOOLUP_STATIC_PATH_BEHAVIOUR` | enum: warn, require, skip | warn | no | yes | How a missing static-content path is treated at boot: warn, refuse to start, or skip silently. |
| `TOOLUP_STORE_EVICTION_MINUTES` | int | 60 | no | yes | Idle minutes before an ephemeral in-memory store entry is evicted. |
| `TOOLUP_TRUSTED_PROXY_CIDRS` | string | — | no | yes | Comma-separated CIDR ranges whose X-Forwarded-* headers are trusted. |
| `TOOLUP_TRUST_FORWARDED_HEADERS` | bool | false | no | yes | When true, trusts X-Forwarded-* headers from the upstream proxy. Only safe behind a proxy that strips/re-injects them (preflight warns without RequireHttps). |

## Security preflight escape hatches

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX` | bool | false | no | yes | Allows a RAG index that does not survive a restart. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY` | bool | false | no | yes | Trusts X-Forwarded-* headers from any peer instead of the configured proxy CIDRs, which lets a client spoof its own IP. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE` | bool | false | no | yes | Acknowledge running the spoofable HeaderAuthProvider in an authenticated mode (only safe behind a mTLS proxy). |
| `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE` | bool | false | no | yes | Acknowledge the in-memory OAuth state store under a multi-instance deployment (callback may hit a replica without the state). |
| `TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE` | bool | false | no | yes | Allows the in-memory share-token rate limiter when ReplicaCount is above 1, making the limit per-instance. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE` | bool | false | no | yes | Allows in-process ingestion when ReplicaCount is above 1, so a document may be ingested more than once. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE` | bool | false | no | yes | Allows the in-process job scheduler when ReplicaCount is above 1, so scheduled jobs run on every instance. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY` | bool | false | no | yes | Acknowledge a team invite-by-email surface mounted with no IUserDirectory (emails silently never send). |
| `TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE` | bool | false | no | yes | Allows the local embedding provider at a corpus size it is not built for. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_LOCAL_FALLBACK` | bool | false | no | pending | Acknowledge a cloud-declared blob backend silently falling back to local storage (downgrades the refusal to a warning). |
| `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE` | bool | false | no | yes | Acknowledge an internet-facing authenticated deployment with no rate limiting. |
| `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE` | bool | false | no | yes | Acknowledge the in-memory pending-invite store under a multi-instance deployment (per-replica drift). |
| `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE` | bool | false | no | yes | Allows a plaintext secret store while auth is required. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE` | bool | false | no | yes | Acknowledge SSE query-param auth fallback in an authenticated mode (leaks the userId in URLs/logs). |
| `TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE` | bool | false | no | yes | Acknowledge relying on SameSite cookies alone (no server-side CSRF token) for cookie auth. |
| `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE` | bool | false | no | yes | Allows a shared embedding cache in a team-scoped deployment, weakening tenant isolation of cached vectors. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE` | bool | false | no | yes | Allows sticky-routed AI streaming when ReplicaCount is above 1 without a distributed notification channel. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE` | bool | false | no | yes | Acknowledge an unset OIDC audience in an authenticated mode (token-reuse risk). |
| `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE` | bool | false | no | yes | Allows publishable surfaces without artefact signing. Lowers a startup preflight refusal to a warning. |
| `TOOLUP_MODULE_BINDING_ALLOW_UNBOUND` | bool | false | no | yes | Allows modules that carry no signed binding manifest to load. |
| `TOOLUP_MODULE_BINDING_ANCHORS` | string | — | yes | never | Semicolon-separated module-binding trust anchors, each mac:keyId:scope:key or asym:keyId:alg:base64pubkey. |

## Platform subsystems

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_AD_ANALYTICS` | enum: enabled, disabled | disabled | no | yes | Enables the advertising-analytics surface. |
| `TOOLUP_ASSET_STORE` | enum: enabled, disabled | disabled | no | yes | Enables the IAssetStore substrate for uploaded media and derivative rendering. |
| `TOOLUP_AUDIT_FAILURE_POLICY` | enum: log, refuse, degrade | log | no | yes | What happens when an audit sink write fails: log and continue, refuse the action, or degrade to a local file. |
| `TOOLUP_AUDIT_LOG` | enum: enabled, disabled | disabled | no | yes | Enables the audit log and its sink dispatcher. |
| `TOOLUP_BACKFILL_MISSED_TICKS` | bool | false | no | yes | On startup, runs schedule ticks that were missed while the process was down. |
| `TOOLUP_COMPUTE_BUDGET` | enum: enabled, disabled | disabled | no | yes | Enables compute-budget accounting and enforcement for long-running work. |
| `TOOLUP_CONFIG_DRIFT_DETECTION` | enum: enabled, disabled | disabled | no | yes | Enables startup detection of drift between persisted config and the composed defaults. |
| `TOOLUP_DEPLOYMENT_READINESS` | enum: enabled, disabled | disabled | no | yes | Enables the deployment-readiness report surface. |
| `TOOLUP_DEPLOYMENT_VERIFICATION` | enum: enabled, disabled | disabled | no | yes | Enables the one-command post-deployment verification report. |
| `TOOLUP_ENABLE_CITATION_DEV_ENDPOINT` | bool | false | no | yes | Exposes the RAG citation inspection dev endpoint. |
| `TOOLUP_ENTITY_OUTBOX` | enum: enabled, disabled | disabled | no | yes | Enables the entity outbox, so entity saves publish transactionally instead of being discarded unpublished. |
| `TOOLUP_ENTITY_STORE` | enum: enabled, disabled | disabled | no | yes | Enables the IEntityStore substrate (registered entity types and persistence). |
| `TOOLUP_EVENT_STORE` | enum: inmemory, persistent | inmemory | no | yes | Selects the IEventStore backend. The persistent option uses the blob-backed store with the 90-day retention policy. |
| `TOOLUP_EVENT_TRIGGER_CATCHUP` | bool | false | no | yes | On startup, replays event triggers that fired while the process was down. |
| `TOOLUP_EXTERNAL_COMPUTE` | string | — | no | pending | Selects the external-compute companion. |
| `TOOLUP_EXTERNAL_COMPUTE_HTTP_` | string | — | no | pending | Prefix for the HTTP external-compute companion settings; the suffix names the setting. Not read as a variable in its own right. |
| `TOOLUP_JOB_SCHEDULER` | enum: enabled, disabled | disabled | no | yes | Selects the in-process IJobScheduler. Dev-shaped: a multi-instance deployment needs a distributed scheduler companion. |
| `TOOLUP_MIGRATE_WEBHOOK_SECRETS` | bool | false | no | yes | Migrates inline webhook secrets into the secret store on boot. |
| `TOOLUP_OAUTH_REFRESHER` | enum: enabled, disabled | disabled | no | yes | Enables the background OAuth token refresher for stored data-source credentials. |
| `TOOLUP_PLATFORM_KNOWLEDGE_BASE` | enum: enabled, disabled | disabled | no | yes | Enables the platform-level knowledge base, the SDK-shipped document KB surface. |
| `TOOLUP_RESULT_STORE` | enum: no, inmemory, persistent | no | no | yes | Selects the result store backing long-running job output retrieval. |
| `TOOLUP_SHARE_TOKEN_STORE` | enum: enabled, disabled | disabled | no | yes | Enables the IShareTokenStore substrate backing publishable share links (signed tokens + claim store). |
| `TOOLUP_SMOKE_TEST` | enum: enabled, disabled | disabled | no | yes | Enables the post-boot smoke-test surface, which is itself guarded by TOOLUP_SMOKE_TOKEN. |
| `TOOLUP_USAGE_METERING` | enum: enabled, disabled | disabled | no | yes | Enables per-scope usage metering, the counters feeding quota and billing surfaces. |
| `TOOLUP_WEBHOOKS` | enum: enabled, disabled | disabled | no | yes | Enables outbound webhook delivery (subscriptions, signing, retry). |
| `TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS` | string | — | no | yes | Comma-separated host allow-list for outbound webhook URLs. Unset allows any host. |

## Data, ingestion & compliance

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_COLUMN_MAPPING` | enum: enabled, disabled | disabled | no | yes | Enables the column-mapping subsystem for uploaded tabular data. |
| `TOOLUP_CONSENT_AUDIT` | enum: enabled, disabled | disabled | no | yes | Enables consent-change auditing. |
| `TOOLUP_CONSENT_STATE_STORE` | enum: off, inmemory, entity | off | no | yes | Selects the consent-state backend. |
| `TOOLUP_DATA_INGESTION` | enum: enabled, disabled | disabled | no | yes | Enables the data-ingestion pipeline (IDataIngestor plus the background ingestion service). |
| `TOOLUP_DATA_SUBJECT_REQUESTS` | enum: disabled | disabled | no | yes | Disables the data-subject-request surface. Enabling it requires an explicit ErasurePolicy, a compliance decision, so it must be set in ServerConfig. |
| `TOOLUP_LINEAGE` | enum: enabled, disabled | disabled | no | yes | Enables the lineage store recording dataset and derivation provenance. |
| `TOOLUP_MAPPING_DRYRUN_BLOCK` | enum: enabled, disabled | disabled | no | yes | When enabled, a failed column-mapping dry run blocks the import instead of only warning. |

## Rate limiting

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_RATE_LIMITER` | enum: enabled, disabled | disabled | no | yes | Enables the request rate-limiter middleware. |
| `TOOLUP_RATE_LIMIT_PERMITS` | int | — | no | yes | Requests allowed per window. Set alongside the window and queue keys to switch rate limiting on. |
| `TOOLUP_RATE_LIMIT_QUEUE` | int | — | no | yes | How many requests may queue once the permit count is exhausted. |
| `TOOLUP_RATE_LIMIT_STORE` | enum: no, inmemory, external | no | no | yes | Selects where rate-limit counters live. The in-memory store is per-instance and therefore wrong for a multi-instance deployment. |
| `TOOLUP_RATE_LIMIT_WINDOW_SECONDS` | int | — | no | yes | Length of the rate-limit window, in seconds. |
| `TOOLUP_SLOW_RATE_LIMIT_MS` | int | 5000 | no | yes | Milliseconds a request may wait on the rate limiter before that wait is logged as slow. |

## AI

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_AI_MODEL` | string | — | no | pending | Model id passed to the selected AI provider. |
| `TOOLUP_AI_PROBE_ON_STARTUP` | bool | false | no | pending | Probes the configured AI provider during preflight, so a bad key fails at boot rather than on first use. |
| `TOOLUP_AI_PROVIDER` | string | — | no | pending | Selects the IAIProvider companion the AI surface resolves at startup. |
| `TOOLUP_CONVERSATION_STORE` | enum: no | no | no | yes | Disables AI conversation persistence. Enabling it requires a retentionDays value, so it must be set in ServerConfig rather than here. |
| `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` | bool | false | no | pending | Refuses to start when the vector index fails its integrity check, instead of rebuilding it. |

## Public surface & rendering

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_PUBLIC_BASE_URL` | string | — | no | yes | Absolute base URL the deployment is reachable at. Used to build links in emails, share tokens and OAuth redirects. |
| `TOOLUP_PUBLIC_PATH` | string | deploy/public | no | yes | Filesystem path served as static public content. |
| `TOOLUP_PUBLIC_RENDERING` | enum: no | no | no | yes | Disables server-side public page rendering. Enabling it requires a ContentRoot path, so it must be set in ServerConfig rather than here. |

## Notification channels

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_SENDGRID_ENDPOINT` | string | — | no | pending | SendGrid API endpoint override. |
| `TOOLUP_SENDGRID_FROM` | string | — | no | pending | Default From address for SendGrid-delivered notifications. |
| `TOOLUP_SENDGRID_FROM_NAME` | string | — | no | pending | Default From display name for SendGrid-delivered notifications. |
| `TOOLUP_SMTP_FROM` | string | — | no | pending | Default From address for SMTP-delivered notifications. |
| `TOOLUP_SMTP_FROM_NAME` | string | — | no | pending | Default From display name for SMTP-delivered notifications. |
| `TOOLUP_SMTP_HOST` | string | — | no | pending | SMTP server hostname for the email notification sink. |
| `TOOLUP_SMTP_PASSWORD` | string | — | yes | never | SMTP password. |
| `TOOLUP_SMTP_PORT` | int | — | no | pending | SMTP server port. |
| `TOOLUP_SMTP_TLS` | string | — | no | pending | TLS mode used for the SMTP connection. |
| `TOOLUP_SMTP_USERNAME` | string | — | no | pending | SMTP username. |
| `TOOLUP_TWILIO_ACCOUNT_SID` | string | — | no | pending | Twilio account SID for the SMS notification sink. |
| `TOOLUP_TWILIO_ENDPOINT` | string | — | no | pending | Twilio API endpoint override. |
| `TOOLUP_TWILIO_FROM` | string | — | no | pending | Originating phone number for Twilio-delivered SMS. |

## Build & tooling

| Env var | Type | Default | Secret | Manifest | Description |
|---|---|---|---|---|---|
| `TOOLUP_BEIR_CACHE` | string | — | no | pending | Benchmark-only: directory the BEIR retrieval corpus is cached in. |
| `TOOLUP_COOKBOOK_PATH` | string | — | no | pending | Overrides the path the AG Charts AI cookbook is loaded from. |
| `TOOLUP_EMIT_SBOM` | bool | false | no | pending | Build-time: emits a CycloneDX SBOM alongside the packed artefacts. |
| `TOOLUP_ENTERPRISE_COOKBOOK_PATH` | string | — | no | pending | Overrides the path the AG Grid Enterprise AI cookbook is loaded from. |
| `TOOLUP_PUBLISH_SOURCE` | string | — | no | pending | Build-time: overrides the NuGet source the Publish target pushes to. |
| `TOOLUP_REMOTING_ANALYZER_AUDIT` | bool | false | no | pending | Analyzer-time: emits an audit report of remoting API classification. |
| `TOOLUP_TEST_ARGS` | string | — | no | pending | Build-time: extra arguments passed to each Expecto test pack. |

