// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.ShareTokenRateLimiterDistributionValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 136 part 2 — multi-replica share-token rate-limit refusal ─
//
// The default `InMemoryShareTokenRateLimiter` keeps each per-token
// sliding window in a process-local dictionary (`IsDistributed = false`).
// In a single-instance deployment that is exact. Behind a load balancer
// fronting N replicas, each replica enforces the declared
// `ShareTokenRateLimit.MaxUses` independently, so a leaked share-token's
// effective per-window admission cap is `N × MaxUses` — the operator
// configured a rate limit and silently got a weaker one. The limiter's
// own header documents this honestly, but nothing surfaced it at
// startup, so the gap was only discoverable under attack.
//
// This validator mirrors `OAuthStateStoreInstanceValidator` /
// `JobSchedulerInstanceValidator`: it reads the resolved limiter's
// declared partition guarantee (`IsDistributed`, Phase 136 part 2 — a
// capability as DATA, GP 12, not a runtime-type guess) and the
// operator-declared scale-out shape, and refuses / warns accordingly.
//
//   * `ReplicaCount > 1` is an *explicit* scale-out declaration → the
//     `N × MaxUses` bypass is live → `Error` (refuse startup).
//   * `PublicBaseUrl` set with `ReplicaCount = 1` is an *inferred*
//     public surface that is commonly load-balanced → `Warning` (the
//     bypass is latent if the operator ever scales out).
//   * Single instance with no public surface → `Ok` (the in-memory
//     limiter is exact).
//
// Gated on the share-token surface actually being live (explicit
// `EnabledShareTokenStore`, or the Phase 66 claim-bearer auto-promotion)
// so a Forms deployment that issues no tokens never sees the signal.
//
// Not security-class (it is not bypassed by `SkipPreflight`'s
// emergency-boot lever) — consistent with `rate-limiter-instance` /
// `job-scheduler-instance`: an over-permissive rate limit is a
// quota-imprecision concern, not an identity / unauthenticated-access
// hole, and the absolute persisted `UseLimit` cap still bounds a leaked
// token regardless of the per-window rate.

/// Refuses (Error) / warns on the in-memory `IShareTokenRateLimiter`
/// paired with a scale-out-shaped deployment. The escape hatch is a
/// distributed limiter (`FormsServerApp.withShareTokenRateLimiter` over
/// a Redis / `IRateLimitStore`-backed companion) or
/// `ServerConfig.AcceptInMemoryShareTokenRateLimiterInMultiInstance =
/// true`.
type ShareTokenRateLimiterDistributionValidator
    (config: ServerConfig, rateLimiter: IShareTokenRateLimiter, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "share-token-rate-limiter-distribution"
        member _.Timeout = timeout

        member _.Validate() = async {
            // The share-token surface is live when the substrate is
            // explicitly enabled or auto-promoted by a claim-bearer
            // surface (Phase 66) — only then can rate-limited tokens be
            // issued and the limiter consulted.
            let shareTokenSurfaceLive =
                config.ShareTokenStore = EnabledShareTokenStore
                || DeploymentConfig.hasClaimBearer config

            let inMemoryLimiter = not rateLimiter.IsDistributed
            let escapeHatch = config.AcceptInMemoryShareTokenRateLimiterInMultiInstance

            if not shareTokenSurfaceLive || not inMemoryLimiter || escapeHatch then
                return Ok
            else
                let declaredScaleOut = config.ReplicaCount > 1

                let inferredScaleOut =
                    match config.PublicBaseUrl with
                    | Some url -> not (String.IsNullOrWhiteSpace url)
                    | None -> false

                if declaredScaleOut then
                    return
                        Error(
                            sprintf
                                "ServerConfig.ReplicaCount = %d with the in-memory IShareTokenRateLimiter (IsDistributed = false). Each per-token sliding window is held in a process-local dictionary, so every replica enforces ShareTokenRateLimit.MaxUses independently — a leaked share-token's effective per-window cap across the deployment is %d× the configured value. Wire a distributed limiter via FormsServerApp.withShareTokenRateLimiter (a Redis / IRateLimitStore-backed companion, Phase 56) so per-token windows are shared across replicas, run a single instance, or set ServerConfig.AcceptInMemoryShareTokenRateLimiterInMultiInstance = true (TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE=1) if share-token traffic is pinned to one replica or the N× burst is knowingly accepted. The absolute persisted UseLimit cap still holds. After fixing, verify in the HealthMonitorUI Preflight tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                                config.ReplicaCount
                                config.ReplicaCount
                        )
                elif inferredScaleOut then
                    return
                        Warning(
                            "ServerConfig.PublicBaseUrl is set (a public share-link surface) with the in-memory IShareTokenRateLimiter (IsDistributed = false). ReplicaCount = 1 today, so per-token rate limits are exact — but if this deployment is ever scaled out behind a load balancer, each replica will enforce ShareTokenRateLimit.MaxUses independently and a leaked token's effective per-window cap becomes N× the configured value. Before scaling out, wire a distributed limiter via FormsServerApp.withShareTokenRateLimiter (Redis / IRateLimitStore-backed). Set ServerConfig.AcceptInMemoryShareTokenRateLimiterInMultiInstance = true to silence this notice once the topology is confirmed single-instance."
                        )
                else
                    return Ok
        }