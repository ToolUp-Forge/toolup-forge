# Phase 71.A — Runtime-resolvable configuration lifts (`fromEnv` env-var cluster)

Operationalises the [Phase 71 runtime-config audit](71-runtime-config-audit.md). This first increment lifts the highest-value runtime-liftable `ServerConfig` fields onto the `ServerConfig.fromEnv` seam so one container image reconfigures by environment alone.

## What changes

All four changes live in `ServerConfig.fromEnv` (`src/ToolUp.Platform.Core/Shared/SDK.Shared.fs`). **Every one is backward-compatible (GP 11): a deployment that sets none of these env vars resolves byte-for-byte as before.** The single intentional behaviour change is the Surfaces precedence inversion — a bug fix, not a default change.

| Field | Env var | Behaviour |
|---|---|---|
| `Surfaces` | `TOOLUP_PLATFORM_SURFACES` | **Now wins over `overrides.Surfaces`** (71.A.1, already shipped `662fc22`). Fixes the silent-precedence trap where a `ServerConfigOverrides.referenceApp` consumer was pinned to `Individual` regardless of the env var. |
| Six `Accept*` flags | `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE`, `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE`, `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE`, `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE`, `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE`, `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE` | **Now read by `fromEnv`** (71.A.2). These six documented their env var but `fromEnv` never read it — setting the var was silently ignored. Unset → `false` (unchanged); the matching validator still refuses startup unless opted in. |
| `Port` | `SERVER_PORT` | **Now read inside `fromEnv`** (71.A.3) so a `/dev/inspect` config snapshot reflects the actually-bound port. The compose-time read in `SDK.Server.compose` is retained for consumers not using `fromEnv`. Non-integer / out-of-range → fail loud (mirrors the compose-time guard). |
| `PublicBaseUrl` | `TOOLUP_PUBLIC_BASE_URL` | **New runtime read** (71.A.4). Empty/whitespace → `None` + warn; trailing slash stripped (idempotent) + warn; unset → `None`. |

## Migration

- **No env var set:** nothing to do — behaviour is identical.
- **Consumer on `ServerConfigOverrides.referenceApp` + `TOOLUP_PLATFORM_SURFACES`:** the env var now wins (this is the fix). If you *intended* compile-time pinning, move the literal into your own composition root: `{ ServerConfig.defaults with Surfaces = Surfaces.individual }` — consumer-authored literals still win over the env var.
- **Container deploys:** prefer `SERVER_PORT` + `TOOLUP_PUBLIC_BASE_URL` + the relevant `TOOLUP_ACCEPT_*` flags over baking them into the consumer binary.

## Verification

`src/ToolUp.Platform.Tests/InProcess/ServerConfigFromEnvTests.fs` (9 cases, `testSequenced`): Surfaces precedence both directions; the six `Accept*` flags set→true / unset→false; `SERVER_PORT` read + fail-loud; `PublicBaseUrl` trailing-slash strip / empty→None+warn / unset→None. Run: `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`.

## Rollback

Revert the `fromEnv` edits in `SDK.Shared.fs`. The Surfaces precedence inversion (71.A.1) shipped earlier in `662fc22`; reverting only this increment leaves it in place.

## Increment 2 (71.A.5 + 71.A.6 + 71.A.8 — server scalars + string lists)

| Field | Env var | Notes |
|---|---|---|
| `PublicPath` (71.A.5) | `TOOLUP_PUBLIC_PATH` | env > override > default `deploy/public`. |
| `IncludePlatformDefaults` (71.A.6) | `TOOLUP_INCLUDE_PLATFORM_DEFAULTS` | tri-state (env wins over override; **default `true`** preserved when unset). |
| `EnableDevEndpoints` (71.A.6) | `TOOLUP_ENABLE_DEV_ENDPOINTS` | tri-state; env wins over override. |
| `BackfillMissedTicks` / `SkipPreflight` / `HealthStateTracking` (71.A.6) | `TOOLUP_BACKFILL_MISSED_TICKS` / `TOOLUP_SKIP_PREFLIGHT` / `TOOLUP_HEALTH_STATE_TRACKING` | boolean flags, default `false`. |
| `EnableCitationDevEndpoint` (71.A.6) | `TOOLUP_ENABLE_CITATION_DEV_ENDPOINT` | optional bool. |
| `MaxRequestBodyBytes` (71.A.6) | `TOOLUP_MAX_REQUEST_BODY_BYTES` | optional positive int64. |
| `SlowRateLimitThreshold` (71.A.6) | `TOOLUP_SLOW_RATE_LIMIT_MS` | positive ms → `TimeSpan`, default 5s. |
| `WebhookUrlAllowedHosts` / `PeerRoutePrefixes` (71.A.8) | `TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS` / `TOOLUP_PEER_ROUTE_PREFIXES` | comma/semicolon/space-separated lists. |

All backward-compatible (unset → prior `defaults.X`). New shared helpers in `SDK.Shared.fs`: `resolvePublicPath`, `envFlagTri`, `envFlagOpt`, `envInt64Opt`, `envTimeSpanMs`, `parseStringList`. Verified by 11 added cases in `ServerConfigFromEnvTests.fs`. See [the full env-var table](../operations/env-vars.md).

## Increment 3 (71.A.7 batch 1 — server flat-case DU toggles)

Sixteen payload-free server mode DUs now resolve from `TOOLUP_*` env vars via two new shared helpers (`parseFlatDuCase` + the `No*`/`Enabled*` shorthand `parseEnabledDisabled`): `ResultStore`, `Lineage`, `DataIngestion`, `OAuthRefresher`, `EntityStore`, `UsageMetering`, `MetricsEndpoint`, `PlatformKnowledgeBase`, `ConfigDriftDetection`, `RateLimiter`, `SmokeTest`, `AssetStore`, `ConsentAudit`, `AdAnalytics`, `ServerlessHost`, `ProcessProfile`. All additive — unset → the prior `defaults.X` (GP 11); an unrecognised token warns and keeps the default. See [the env-var table](../operations/env-vars.md#flat-case-subsystem-toggles-phase-71a7-batch-1). Verified by 3 added cases in `ServerConfigFromEnvTests.fs`.

## Increment 4 (71.A.7 batch 2 — override-bearing toggles + TeamCreationPolicy) — completes 71.A.7

The five remaining flat-DU toggles. `Webhooks`, `AuditLog`, `SecurityHardening`, and `ShareTokenStore` already had an override-record read; their precedence is now **env > override > default** (via the new override-aware `parseEnabledDisabledWith`, and `parseFlatDuCase` for the 3-way `SecurityHardening`). `TeamCreationPolicy` (no override member) is a new `env > default` read. The `parseEnabledDisabled` helper was refactored to share an `enabledDisabledTokens` list with the override-aware sibling. Backward-compatible: a consumer setting none of these env vars resolves exactly as before (override → default). Verified by 4 added cases. **With this, 71.A.7 (all 21 server flat-DU fields) is complete.**

## Not yet (Phase 71.A checklist, ride forward)

- **71.A.9 / 71.A.10** — client-side flat-DU + brand-string lifts via Vite defines + `BundleConstants` accessors (need a Fable verification pass).
- **71.A.11** — hybrid case-flips.
- The `PublicBaseUrl`-needs-a-token-issuer preflight `Warn` validator. See the phase body for the full ship order.
