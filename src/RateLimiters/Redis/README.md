# ToolUp.Platform.RateLimiters.Redis

**Status: reservation marker.** This directory is reserved for the future Redis-backed `IRateLimiter` companion (the distributed half of the rate-limiter substrate). No code lives here yet.

## What ships here

A Redis-backed sliding-window implementation of `IRateLimiter` (the
Phase 9v outbound rate-limiter contract). The shipped in-process default
(`InProcessRateLimiter`, in `ToolUp.Platform.Server`) tracks the window
per-process; a load-balanced multi-instance deployment burst past the
declared limit by N×. This companion will track the window through Redis
so all N instances observe one shared window — sufficient to honour
third-party API quotas (Strava 100/15 min, OpenAI tier-based RPM,
Anthropic per-organisation throughput, etc.) under load-balanced
production traffic.

The contract is designed so the swap is contract-free: emission sites
call `IRateLimiter.Wait` and observe `RateLimitDecision` regardless of
which implementation the deployment wires. Apps opt in via
`ServerConfig.RateLimiter = EnabledRateLimiter` and the companion
exposes a `RedisRateLimiter.create` factory that the composition root
hands to a future `ServerApp.withRateLimiter` builder (TBD in the
companion's PR — the SDK default is registered automatically today).

## Directory layout mirrors existing distributed companions

- `src/NotificationChannels/Redis/` (Phase 1f shipped reference)
- `src/JobScheduler/Akka/` (Phase 9c half-1 future)
- `src/RateLimiters/Redis/` ← this directory (Phase 9c half-2 future)

Each companion lives in its own subdirectory under the appropriate
substrate-family root, ships its own `.fsproj` with a `<PackageId>`
matching the directory's brand, and registers its `IHealthCheck`
probe + `IConfigValidator` alongside the implementation.

## Why this marker exists

Reserving the directory plus the package id ahead of the implementation
prevents two follow-up problems:

1. **Discoverability.** Operators planning Phase 9v adoption see the
   distributed-companion neighbourhood and can plan capacity (Redis
   cluster sizing, key-namespace strategy) ahead of the swap.
2. **Layout symmetry.** New SDK contributors can locate the future
   companion at the same path they'd predict from the existing
   `NotificationChannels/Redis/` / `JobScheduler/*` layout.

Consumers tracking Phase 9c half-2 should watch for the first `.fsproj`
to land in this directory.
