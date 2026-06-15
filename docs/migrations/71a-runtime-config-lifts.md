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

## Not in this increment (Phase 71.A checklist, deferred-within-phase)

`PublicPath` (71.A.5), the boolean/scalar bundle (71.A.6), the flat-case DU bundles server/client (71.A.7/71.A.9), string-list bundle (71.A.8), client brand-string lifts (71.A.10), hybrid case-flips (71.A.11), and the `PublicBaseUrl`-needs-a-token-issuer preflight `Warn` validator — all ride forward. See the phase body for the full ship order.
