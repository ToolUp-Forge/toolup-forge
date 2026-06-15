# `TOOLUP_*` environment variables

Single-source reference for the environment variables `ServerConfig.fromEnv` (and adjacent seams) read. This page grows as the [Phase 71.A](../migrations/71a-runtime-config-lifts.md) runtime-config lifts land; today it covers the config-resolution variables. A deployment that sets none of these resolves byte-for-byte from `ServerConfig.defaults` + the supplied overrides record (GP 11).

**Precedence (every lifted field):** consumer-authored literal (`{ ServerConfig.defaults with X = ... }`) > env var > library-default override-record value (`ServerConfigOverrides.referenceApp`) > `defaults.X`.

## Core deployment shape

| Variable | Field | Default | Parse contract |
|---|---|---|---|
| `SERVER_PORT` | `Port` | `5000` | Positive integer 1–65535; out-of-range / non-integer → fail loud. |
| `TOOLUP_PUBLIC_BASE_URL` | `PublicBaseUrl` | `None` | Non-empty origin; trailing slash stripped + warn; empty/whitespace → `None` + warn. |
| `TOOLUP_PUBLIC_PATH` | `PublicPath` | `deploy/public` | _(planned — 71.A.5)_ |
| `TOOLUP_PLATFORM_SURFACES` | `Surfaces` | `anonymous` | Comma/semicolon/space-separated tokens: `anonymous`, `anonymous_persistent`, `trial`, `individual`, `team`, `multi_team`, `claim_bearer`. Wins over the override-record value. Unrecognised → warn + fall back. |
| `TOOLUP_MODULE` | `ModuleFilter` | `None` | Single module key. |
| `TOOLUP_LOG_LEVEL` | `LogLevel` | `Info` | `Trace`/`Debug`/`Info`/`Warn`/`Error` (case-insensitive). |
| `TOOLUP_TRACE_CATEGORIES` | `TraceCategories` | _(empty)_ | Comma/semicolon/space-separated category names. |

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
