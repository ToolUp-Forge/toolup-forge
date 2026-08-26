module ToolUp.Platform.RateLimiting

open System
open System.Threading.RateLimiting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open ToolUp.Platform

/// Routes that bypass rate limiting entirely. `/health` and `/ready`
/// are load-balancer probes and must not be 429'd.
let private isBypassed (path: string) =
    path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/ready", StringComparison.OrdinalIgnoreCase)

/// SSE connect routes. Long-lived streaming connections are the wrong
/// shape for the per-subject request buckets (one held-open connection
/// would eat a permit from the caller's whole API budget, and the
/// subject policies are sized for request/response traffic), so until
/// Phase 117 both routes bypassed rate limiting entirely. That left
/// the *connect attempt* unmetered — an unauthenticated client could
/// hammer `?userId=` probes or flood the per-scope connection cap at
/// wire speed. These routes now get their own per-IP fixed window
/// (`sseConnectPolicy` below) instead of either extreme.
let private isSseConnect (path: string) =
    path.StartsWith("/api/notifications", StringComparison.OrdinalIgnoreCase)
    || path.StartsWith("/api/ai/events", StringComparison.OrdinalIgnoreCase)

/// Phase 117 — per-IP fixed-window policy for SSE connect attempts.
/// Expressed as data (`RateLimitPolicy`) like every other limit in
/// this subsystem. Sizing: a legitimate tab opens at most two SSE
/// connections (notifications + AI events) and reconnects on auth
/// transitions or the client's bounded retry (≤ 3 attempts / minute);
/// 60 connects/minute/IP leaves an order-of-magnitude headroom for a
/// NAT'd office while still throttling connection-flood / userId-
/// enumeration probing. `QueueLimit = 0` — a refused connect should
/// 429 immediately so EventSource's reconnect backoff (the client
/// honours Retry-After) takes over rather than queueing handshakes.
/// `internal` for the test pack (InternalsVisibleTo), like the
/// partition-key helper.
let internal sseConnectPolicy: RateLimitPolicy = {
    PermitLimit = 60
    WindowSeconds = 60
    QueueLimit = 0
}

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
            elif isSseConnect ctx.Request.Path.Value then
                // Phase 117 — SSE connects get their own per-IP window
                // (see `sseConnectPolicy`), keyed off the remote IP
                // rather than the resolved subject: the probing this
                // throttles is precisely the traffic that has no (or a
                // forged) subject.
                RateLimitPartition.GetFixedWindowLimiter(
                    $"sse:ip:{remoteIp ctx}",
                    fun _ ->
                        FixedWindowRateLimiterOptions(
                            PermitLimit = sseConnectPolicy.PermitLimit,
                            Window = TimeSpan.FromSeconds(float sseConnectPolicy.WindowSeconds),
                            QueueLimit = sseConnectPolicy.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        )
                )
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
                    // Phase 117 — SSE connects are limited under their own
                    // per-IP policy, not the subject policy, so their
                    // Retry-After must come from the same place.
                    if isSseConnect rejectedCtx.HttpContext.Request.Path.Value then
                        sseConnectPolicy.WindowSeconds
                    else
                        match resolvePolicyAndKey config rejectedCtx.HttpContext with
                        | Some(policy, _) -> policy.WindowSeconds
                        | None -> 60

                rejectedCtx.HttpContext.Response.Headers.RetryAfter <-
                    Microsoft.Extensions.Primitives.StringValues(string retryAfterSeconds)

                System.Threading.Tasks.ValueTask.CompletedTask)