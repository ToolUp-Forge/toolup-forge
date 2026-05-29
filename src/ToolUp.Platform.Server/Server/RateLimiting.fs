module ToolUp.Platform.RateLimiting

open System
open System.Threading.RateLimiting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open ToolUp.Platform

[<Literal>]
let private rateLimitPolicyName = "ToolUp.PerScope"

/// Routes that bypass rate limiting. `/health` and `/ready` are
/// load-balancer probes and must not be 429'd. `/api/notifications`
/// and `/api/ai/events` are long-lived SSE connections — counting
/// either as one request per minute would let one connection
/// saturate the bucket. Same root reason: per-connection rate
/// limits are the wrong shape for streaming endpoints.
let private isBypassed (path: string) =
    path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/ready", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/api/notifications", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/api/ai/events", StringComparison.OrdinalIgnoreCase)

[<Literal>]
let private subjectItemsKey = "ToolUp.Subject"

/// Remote IP for a request, or `"unknown"` when the connection has no
/// resolved peer address (in-process test hosts, some proxy shapes).
let private remoteIp (ctx: HttpContext) =
    match ctx.Connection.RemoteIpAddress with
    | null -> "unknown"
    | ip -> ip.ToString()

/// The request's resolved `Subject`, set in `HttpContext.Items` by the
/// scope-resolution middleware (which runs before `UseRateLimiter`).
/// `None` only for requests that bypassed scope resolution entirely.
let private resolveSubject (ctx: HttpContext) : Subject option =
    match ctx.Items.TryGetValue subjectItemsKey with
    | true, (:? Subject as subject) -> Some subject
    | _ -> None

/// Phase 66 Stream C.3 — resolve the fixed-window policy + partition
/// key for a request. The policy is chosen by the resolved subject's
/// kind (`RateLimitConfig.policyFor`); the partition is implied by the
/// subject itself (`RateLimitPolicy.partitionFor` — `token:` / `team:`
/// / `user:` / `ip:`). `None` = this request's subject kind has no
/// configured limit (→ `GetNoLimiter`). The policy is returned
/// alongside the key so `OnRejected` can stamp the matching
/// `Retry-After`.
let private resolvePolicyAndKey (config: RateLimitConfig) (ctx: HttpContext) : (RateLimitPolicy * string) option =
    match resolveSubject ctx with
    | Some subject ->
        match RateLimitConfig.policyFor config (Subject.kind subject) with
        | Some policy -> Some(policy, RateLimitPolicy.partitionFor (remoteIp ctx) subject)
        | None -> None
    | None ->
        // Defensive: a request that bypassed scope resolution carries no
        // resolved subject. Fall back to the `Default` policy partitioned
        // by IP; if there is no `Default`, the request is unlimited.
        match config.Default with
        | Some policy -> Some(policy, $"ip:{remoteIp ctx}")
        | None -> None

/// Configure the global rate limiter. Called once at startup from
/// `compose` when `ServerConfig.RateLimit` would register a limiter
/// (`RateLimitConfig.isEnabled`). Uses a fixed-window limiter whose
/// per-request policy + partition are derived from the resolved
/// `Subject` (Phase 66 Stream C.3). Excluded routes and subject kinds
/// with no configured policy return `RateLimitPartition.GetNoLimiter`,
/// which short-circuits the limiter for that request.
let configure (config: RateLimitConfig) (options: RateLimiterOptions) =
    options.RejectionStatusCode <- 429

    options.GlobalLimiter <-
        PartitionedRateLimiter.Create<HttpContext, string>(fun ctx ->
            if isBypassed ctx.Request.Path.Value then
                RateLimitPartition.GetNoLimiter("__bypass")
            else
                match resolvePolicyAndKey config ctx with
                | None -> RateLimitPartition.GetNoLimiter("__unlimited")
                | Some(policy, key) ->
                    RateLimitPartition.GetFixedWindowLimiter(
                        key,
                        fun _ ->
                            FixedWindowRateLimiterOptions(
                                PermitLimit = policy.PermitLimit,
                                Window = TimeSpan.FromSeconds(float policy.WindowSeconds),
                                QueueLimit = policy.QueueLimit,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                                AutoReplenishment = true
                            )
                    ))

    // Honour the `Retry-After` convention so well-behaved clients back
    // off rather than hammering the bucket. The window is the rejected
    // request's own policy window (60s fallback when, defensively, no
    // policy resolves — the partition that rejected it always has one).
    options.OnRejected <-
        Func<OnRejectedContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>
            (fun rejectedCtx _ ->
                let retryAfterSeconds =
                    match resolvePolicyAndKey config rejectedCtx.HttpContext with
                    | Some(policy, _) -> policy.WindowSeconds
                    | None -> 60

                rejectedCtx.HttpContext.Response.Headers.RetryAfter <-
                    Microsoft.Extensions.Primitives.StringValues(string retryAfterSeconds)

                System.Threading.Tasks.ValueTask.CompletedTask)