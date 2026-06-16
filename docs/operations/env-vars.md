# `TOOLUP_*` environment variables

Single-source reference for the environment variables `ServerConfig.fromEnv` (and adjacent seams) read. This page grows as the [Phase 71.A](../migrations/71a-runtime-config-lifts.md) runtime-config lifts land; today it covers the config-resolution variables. A deployment that sets none of these resolves byte-for-byte from `ServerConfig.defaults` + the supplied overrides record (GP 11).

**Precedence (every lifted field):** consumer-authored literal (`{ ServerConfig.defaults with X = ... }`) > env var > library-default override-record value (`ServerConfigOverrides.referenceApp`) > `defaults.X`.

## Core deployment shape

| Variable | Field | Default | Parse contract |
|---|---|---|---|
| `SERVER_PORT` | `Port` | `5000` | Positive integer 1–65535; out-of-range / non-integer → fail loud. |
| `TOOLUP_PUBLIC_BASE_URL` | `PublicBaseUrl` | `None` | Non-empty origin; trailing slash stripped + warn; empty/whitespace → `None` + warn. |
| `TOOLUP_PUBLIC_PATH` | `PublicPath` | `deploy/public` | Any path string; env wins over the override-record value. |
| `TOOLUP_PLATFORM_SURFACES` | `Surfaces` | `anonymous` | Comma/semicolon/space-separated tokens: `anonymous`, `anonymous_persistent`, `trial`, `individual`, `team`, `multi_team`, `claim_bearer`. Wins over the override-record value. Unrecognised → warn + fall back. |
| `TOOLUP_MODULE` | `ModuleFilter` | `None` | Single module key. |
| `TOOLUP_LOG_LEVEL` | `LogLevel` | `Info` | `Trace`/`Debug`/`Info`/`Warn`/`Error` (case-insensitive). |
| `TOOLUP_TRACE_CATEGORIES` | `TraceCategories` | _(empty)_ | Comma/semicolon/space-separated category names. |

## Boolean / scalar bundle (Phase 71.A.6) + string lists (71.A.8)

All additive and backward-compatible — unset preserves the prior `defaults.X`.

| Variable | Field | Default | Parse contract |
|---|---|---|---|
| `TOOLUP_INCLUDE_PLATFORM_DEFAULTS` | `IncludePlatformDefaults` | `true` | `1`/`true`/`yes`/`on` ↔ `0`/`false`/`no`/`off`; wins over override; unrecognised → fail loud. |
| `TOOLUP_ENABLE_DEV_ENDPOINTS` | `EnableDevEndpoints` | `false` | Same; env wins over override. |
| `TOOLUP_BACKFILL_MISSED_TICKS` | `BackfillMissedTicks` | `false` | Boolean flag. |
| `TOOLUP_SKIP_PREFLIGHT` | `SkipPreflight` | `false` | Boolean flag. |
| `TOOLUP_HEALTH_STATE_TRACKING` | `HealthStateTracking` | `false` | Boolean flag. |
| `TOOLUP_ENABLE_CITATION_DEV_ENDPOINT` | `EnableCitationDevEndpoint` | _(unset)_ | Optional boolean: set → `Some`, unset → `None`. |
| `TOOLUP_MAX_REQUEST_BODY_BYTES` | `MaxRequestBodyBytes` | _(unset)_ | Positive int64 → `Some`; `none`/`0`/unset → `None`; garbage → `None` + warn. |
| `TOOLUP_SLOW_RATE_LIMIT_MS` | `SlowRateLimitThreshold` | `5000` | Positive integer milliseconds; garbage → default + warn. |
| `TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS` | `WebhookUrlAllowedHosts` | _(empty)_ | Comma/semicolon/space-separated host list. |
| `TOOLUP_PEER_ROUTE_PREFIXES` | `PeerRoutePrefixes` | _(empty)_ | Comma/semicolon/space-separated prefix list. |

## Flat-case subsystem toggles (Phase 71.A.7)

Each selects a payload-free DU case; unset → the prior `defaults.X`. Binary toggles accept `enabled`/`on`/`yes` and `no`/`off`/`disabled` (case-insensitive); an unrecognised token warns and keeps the default. The override-bearing toggles (`Webhooks` / `AuditLog` / `SecurityHardening` / `ShareTokenStore`) resolve env > override > default.

| Variable | Field | Tokens |
|---|---|---|
| `TOOLUP_RESULT_STORE` | `ResultStore` | `no` / `inmemory` / `persistent` |
| `TOOLUP_LINEAGE` | `Lineage` | binary |
| `TOOLUP_DATA_INGESTION` | `DataIngestion` | binary |
| `TOOLUP_OAUTH_REFRESHER` | `OAuthRefresher` | binary |
| `TOOLUP_ENTITY_STORE` | `EntityStore` | binary |
| `TOOLUP_USAGE_METERING` | `UsageMetering` | binary |
| `TOOLUP_METRICS_ENDPOINT` | `MetricsEndpoint` | binary |
| `TOOLUP_PLATFORM_KNOWLEDGE_BASE` | `PlatformKnowledgeBase` | binary |
| `TOOLUP_CONFIG_DRIFT_DETECTION` | `ConfigDriftDetection` | binary |
| `TOOLUP_RATE_LIMITER` | `RateLimiter` | binary |
| `TOOLUP_SMOKE_TEST` | `SmokeTest` | binary |
| `TOOLUP_ASSET_STORE` | `AssetStore` | binary |
| `TOOLUP_CONSENT_AUDIT` | `ConsentAudit` | binary |
| `TOOLUP_AD_ANALYTICS` | `AdAnalytics` | binary |
| `TOOLUP_SERVERLESS_HOST` | `ServerlessHost` | `kestrel` / `serverless` |
| `TOOLUP_PROCESS_PROFILE` | `ProcessProfile` | `allinone` / `web` / `worker` / `dispatcher` |
| `TOOLUP_WEBHOOKS` | `Webhooks` | binary (env > override > default) |
| `TOOLUP_AUDIT_LOG` | `AuditLog` | binary (env > override > default) |
| `TOOLUP_SHARE_TOKEN_STORE` | `ShareTokenStore` | binary (env > override > default) |
| `TOOLUP_SECURITY_HARDENING` | `SecurityHardening` | `no` / `default` / `strict` (env > override > default) |
| `TOOLUP_TEAM_CREATION_POLICY` | `TeamCreationPolicy` | `admin` (PlatformAdminOnly) / `any` (AnyAuthenticatedUser) |

## Hybrid subsystem toggles (Phase 71.A.11)

Server DUs whose enabled case carries a payload. The env var selects the **case**; a payload-free / curated-default case is constructed directly, a payload-bearing case **fails loud** at startup naming how to supply the payload (overrides / a `{ defaults with ... }` literal) — it is never silently defaulted. Unset → the configured value.

| Variable | Field | Tokens |
|---|---|---|
| `TOOLUP_JOB_SCHEDULER` | `JobScheduler` | `no` / `enabled` (both nilary — full lift) |
| `TOOLUP_RATE_LIMIT_STORE` | `RateLimitStore` | `no` / `inmemory` / `external` (all nilary) |
| `TOOLUP_EVENT_STORE` | `EventStore` | `inmemory` / `persistent` (persistent → 90-day retention default) |
| `TOOLUP_CONVERSATION_STORE` | `ConversationStore` | `no` (off); `enabled` → **fail-loud** (needs `retentionDays`) |
| `TOOLUP_PUBLIC_RENDERING` | `PublicRendering` | `no` (off); `enabled` → **fail-loud** (needs a `ContentRoot` path) |
| `TOOLUP_DATA_SUBJECT_REQUESTS` | `DataSubjectRequests` | `disabled` (off); `enabled` → **fail-loud** (DSR needs an explicit `ErasurePolicy` — a compliance decision) |

Client (Vite defines) — **off-direction only**; enabling carries a structured config (`AdPanelConfig`) or id that must be set in code, so a non-`no` token leaves the config value as-is:

| Vite define | `ClientConfig` field | Tokens |
|---|---|---|
| `__TOOLUP_AD_PANEL__` | `AdPanel` | `no` / `off` / `disabled` |
| `__TOOLUP_CONSENT_PROVIDER__` | `ConsentProvider` | `no` / `off` / `disabled` |

## Transport / security

| Variable | Field | Default |
|---|---|---|
| `TOOLUP_REQUIRE_HTTPS` | `RequireHttps` | `false` |
| `TOOLUP_TRUST_FORWARDED_HEADERS` | `TrustForwardedHeaders` | `true` (fail-loud on unrecognised value) |
| `TOOLUP_STATIC_PATH_BEHAVIOUR` | `StaticPathBehaviour` | `warn` (`warn`/`require`/`skip`) |
| `TOOLUP_SSE_AUTH` | `SseAuthMode` | `fallback` (`cookie`/`fallback`) |
| `TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE` | `MaxSseConnectionsPerScope` | `10` (positive int or `none`) |
| `TOOLUP_SLOW_REQUEST_MS` | `SlowRequestThreshold` | `1000` |
| `TOOLUP_REPLICA_COUNT` | `ReplicaCount` | `1` |

## `Accept*` escape-hatch flags

Each is `false` by default; setting it to `1`/`true`/`yes`/`on` opts the deployment past a safety validator that would otherwise refuse startup. **Set one only when you understand why its validator fires.** (Phase 71.A.2 closed the six that were documented-but-unread.)

| Variable | Field |
|---|---|
| `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE` | `AcceptHeaderAuthWhenAuthRequired` |
| `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE` | `AcceptPlaintextSecretsWhenAuthRequired` |
| `TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE` | `AcceptInProcessSchedulerInMultiInstance` |
| `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE` | `AcceptInProcessIngestionInMultiInstance` |
| `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE` | `AcceptSharedEmbeddingCacheInTeamMode` |
| `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE` | `AcceptStickyRoutedAiInMultiInstance` |
| `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE` | `AcceptNoRateLimitWhenAuthRequired` |
| `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE` | `AcceptUnsignedPublishable` |
| `TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE` | `AcceptQueryParamSseAuthWhenAuthRequired` |
| `TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE` | `AcceptSameSiteOnlyCsrfWhenAuthRequired` |
| `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE` | `AcceptUnboundAudienceWhenAuthRequired` |
| `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE` | `AcceptInMemoryOAuthStateInMultiInstance` |
| `TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE` | `AcceptInMemoryShareTokenRateLimiterInMultiInstance` |
| `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE` | `AcceptPendingInviteStoreInMultiInstance` |

_Other already-honoured scalars (`TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES`, `TOOLUP_STORE_EVICTION_MINUTES`, `TOOLUP_RATE_LIMIT_*`) are documented at their field definitions in `SDK.Shared.fs`; they move into this table as the later Phase 71.A increments consolidate the parsers._

## Client-side Vite `define`s (Phase 71.A.10)

The client tier reads **build-time Vite `define`s** (`__TOOLUP_*__`), not runtime env vars — they're substituted into the Fable bundle at `vite build`. Wire them in `vite.config.mts` `define: { ... }` (typically from `process.env`), then `ClientConfig.fromBundleConstants` folds them in with **Vite-define > override-record > default** precedence. A define left unset leaves the override/default untouched.

| Vite define | `ClientConfig` field | Type |
|---|---|---|
| `__TOOLUP_APP_NAME__` | `AppName` | string |
| `__TOOLUP_APP_LOGO__` | `AppLogo` | string |
| `__TOOLUP_ACTIVE_MODULE__` | `ActiveModule` | string |
| `__TOOLUP_DEV_DEFAULT_USER_ID__` | `DevDefaultUserId` | string |
| `__TOOLUP_ENABLE_ELMISH_TRACE__` | `EnableElmishConsoleTrace` | bool (`true`/`false` or JS boolean) |
| `__TOOLUP_SHOW_DEBUG_MODULES__` | `ShowDebugOnlyModules` | bool |

_(The pre-existing `__TOOLUP_MODULE__`, `__TOOLUP_PLATFORM_SURFACES__`, `__AG_GRID_LICENSE__`, `__CLERK_PUBLISHABLE_KEY__`, `__ENTRA_*__`, `__OIDC_*__` defines are unchanged.)_

### Client admin-module / profile toggles (Phase 71.A.9)

The admin-module mode DUs are **hybrid**: their `No*` / `Default*` case-flip lifts, but the `Configured*` / `External*` / `Custom*` cases carry function values and stay compile-time. A define of `no`/`off`/`disabled` selects the disabled case, `default`/`on`/`enabled` the default case; an unset / unrecognised value leaves the config's existing value untouched (so a `Configured`/`External` posture set in code is never clobbered). `PlatformAdminProfile` and `InputsPaneWidth` are fully nilary.

| Vite define | `ClientConfig` field | Tokens |
|---|---|---|
| `__TOOLUP_TEAM_MANAGER__` | `TeamManager` | `no` / `default` |
| `__TOOLUP_TEAM_CONFIG__` | `TeamConfig` | `no` / `default` |
| `__TOOLUP_PLATFORM_ADMIN__` | `PlatformAdmin` | `no` / `default` |
| `__TOOLUP_PERMISSIONS_ADMIN__` | `PermissionsAdmin` | `no` / `default` |
| `__TOOLUP_HEALTH_MONITOR__` | `HealthMonitor` | `no` / `default` |
| `__TOOLUP_SERVICE_STATUS_BOARD__` | `ServiceStatusBoard` | `no` / `default` |
| `__TOOLUP_DATA_SUBJECT_REQUEST_ADMIN__` | `DataSubjectRequestAdmin` | `no` / `default` |
| `__TOOLUP_TOAST_CENTRE__` | `ToastCentre` | `no` / `default` |
| `__TOOLUP_PLATFORM_ADMIN_PROFILE__` | `PlatformAdminProfile` | `standard` / `publicutility` |
| `__TOOLUP_INPUTS_PANE_WIDTH__` | `InputsPaneWidth` | `narrow` / `wide` / `auto` |

_(`PremiumModel` is a single-case DU today — nothing to select — so it has no define.)_
