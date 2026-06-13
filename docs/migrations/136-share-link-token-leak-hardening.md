# Phase 136 — Share-link token-leak hardening + multi-replica rate-limit refusal (consumer migration)

**What changes.** Two file-disjoint controls harden the publishable-form share-token surface:

1. **Part 1 (forge `7a2fcf4`).** `ShareTokenAuthMiddleware` stamps `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on any response reached via a share token (header or `?token=` query), blunting the `Referer` / browser-history / CDN-log leak of the bearer secret. Per-route overrides are preserved (stamped only when absent). Automatic — no consumer action.
2. **Part 2 (this phase).** `IShareTokenRateLimiter` gains an `IsDistributed: bool` capability (declared as data, GP 12), and a new startup validator `ShareTokenRateLimiterDistributionValidator` refuses / warns when the in-memory limiter is paired with a scale-out-shaped deployment.

**The gap part 2 closes.** The default `InMemoryShareTokenRateLimiter` keeps each per-token sliding window in a process-local dictionary (`IsDistributed = false`). Behind a load balancer fronting **N** replicas, each replica enforces the declared `ShareTokenRateLimit.MaxUses` independently, so a leaked share-token's effective per-window admission cap is silently **N × MaxUses**. The validator surfaces this at startup instead of leaving it to be discovered under attack (GP 9). The absolute persisted `UseLimit` cap is unaffected and still bounds a leaked token.

**Severity.**

| Deployment shape | Resolved limiter | Outcome |
|---|---|---|
| Single instance (`ReplicaCount = 1`, no `PublicBaseUrl`) | in-memory | `Ok` |
| `PublicBaseUrl` set, `ReplicaCount = 1` | in-memory | **`Warning`** (inferred scale-out) |
| `ReplicaCount > 1` | in-memory | **`Error`** — refuses startup |
| `ReplicaCount > 1` | distributed (`IsDistributed = true`) | `Ok` |
| `ReplicaCount > 1` + escape hatch | in-memory | `Ok` |
| No share-token surface (no `EnabledShareTokenStore` / claim-bearer) | any | `Ok` |

The validator is **not** security-class — it is bypassed by `ServerConfig.SkipPreflight` like `rate-limiter-instance` / `job-scheduler-instance` (an over-permissive rate limit is a quota concern, not an auth hole).

## Diff to apply

**Single-instance / non-publishable consumers:** nothing.

**Scale-out consumers (`ReplicaCount > 1`) running publishable share links** — pick one:

```fsharp
// A. Wire a distributed limiter (the correct fix — windows shared
//    across replicas). IsDistributed must report true.
app
|> FormsServerApp.withShareTokenRateLimiter myRedisBackedLimiter

// B. Knowingly accept the N × MaxUses burst (traffic pinned to one
//    replica, or the burst is tolerable). The absolute UseLimit holds.
//    ServerConfig field, or TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE=1
{ config with AcceptInMemoryShareTokenRateLimiterInMultiInstance = true }
```

A custom `IShareTokenRateLimiter` implementation **must** now declare `IsDistributed` — `true` only if its window state is genuinely shared across replicas (Redis / `IRateLimitStore`-backed). The `IShareTokenRateLimiterContract` pack asserts the declared value.

**Embeds.** `?token=` remains supported for `EventSource` / plain `/r/{token}` embed links, but `X-Share-Token` (header) is preferred where the client can set headers — it keeps the secret out of the URL entirely. The part-1 response headers blunt the `?token=` leak for the cases where the query transport is unavoidable.

## Verification

- `dotnet build` — clean.
- `dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj` — `Phase 136 part 2 — rate-limiter distribution validator` passes, and the `IShareTokenRateLimiter contract` pack asserts `IsDistributed = false` for the in-memory default.
- Manual: boot with `ReplicaCount = 2` + a publishable form + the default limiter → startup refuses, naming the `N × MaxUses` bypass. Add the escape hatch or a distributed limiter → boots clean.

## Rollback

Remove the `ShareTokenRateLimiterDistributionValidator` registration in `FormsCompose.composeForms`. The `IsDistributed` capability is additive (defaults to honest per-impl values) and need not be reverted. Part 1's response headers are independent.
