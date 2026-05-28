module ToolUp.Platform.RateLimiting

open System
open System.Security.Cryptography
open System.Text
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

/// Phase 21b — first 16 hex chars of SHA-256 over the token. Bounded
/// length keeps the partition-key dictionary small; collision
/// probability at 64 bits is negligible for legitimate traffic and
/// even a deliberate collision only links one attacker's tokens
/// to another's bucket — no security implication beyond rate-limit
/// fairness.
let private tokenDigest (token: string) =
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes token)
    Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant()

/// Resolve the partition key for a request: prefer token-digest for
/// any request carrying a `?token=` (so one respondent can't hammer
/// the public endpoint and starve the rest of their cohort — Phase
/// 66 generalised the per-token partition from the retiring
/// `AnonymousRoutePrefixes` set to any token-bearing request, so
/// every `ClaimBearer` subject's traffic is bucketed by token), then
/// team-id (Team / MultiTeam shapes), fall back to user-id
/// (Individual / AuthenticatedEphemeral), fall back to remote IP
/// (anonymous sessions / requests without identity). Returns a
/// stable string so concurrent requests for the same scope share
/// one bucket.
///
/// Phase 66 Stream C.3 will introduce per-shape `RateLimitConfig`
/// — until then the partition is identical across all configured
/// surfaces.
let private partitionKey (ctx: HttpContext) =
    let tokenFromQuery () =
        match ctx.Request.Query.TryGetValue "token" with
        | true, values when values.Count > 0 && not (String.IsNullOrEmpty values[0]) -> Some values[0]
        | _ -> None

    let teamId =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as scope) when scope.Container.StartsWith("team-") -> Some scope.ScopeId
        | _ -> None

    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as uid) when not (String.IsNullOrEmpty uid) -> Some uid
        | _ -> None

    let remoteIp () =
        match ctx.Connection.RemoteIpAddress with
        | null -> "unknown"
        | ip -> ip.ToString()

    match tokenFromQuery () with
    | Some token -> $"token:{tokenDigest token}"
    | None ->
        match teamId, userId with
        | Some t, _ -> $"team:{t}"
        | None, Some u -> $"user:{u}"
        | None, None -> $"ip:{remoteIp ()}"

/// Configure the global rate limiter. Called once at startup from
/// `compose` when `ServerConfig.RateLimit` is `Some`. Uses a
/// fixed-window limiter partitioned by token / team / user / IP.
/// Excluded routes return `RateLimitPartition.GetNoLimiter` which
/// short-circuits the limiter for that request.
let configure (config: RateLimitConfig) (options: RateLimiterOptions) =
    options.RejectionStatusCode <- 429

    options.GlobalLimiter <-
        PartitionedRateLimiter.Create<HttpContext, string>(fun ctx ->
            if isBypassed ctx.Request.Path.Value then
                RateLimitPartition.GetNoLimiter("__bypass")
            else
                let key = partitionKey ctx

                RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    fun _ ->
                        FixedWindowRateLimiterOptions(
                            PermitLimit = config.PermitLimit,
                            Window = TimeSpan.FromSeconds(float config.WindowSeconds),
                            QueueLimit = config.QueueLimit,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true
                        )
                ))

    // Honour the `Retry-After` convention so well-behaved clients
    // back off rather than hammering the bucket. The window is the
    // shortest time after which the partition will free permits.
    options.OnRejected <-
        Func<OnRejectedContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>
            (fun rejectedCtx _ ->
                rejectedCtx.HttpContext.Response.Headers.RetryAfter <-
                    Microsoft.Extensions.Primitives.StringValues(string config.WindowSeconds)

                System.Threading.Tasks.ValueTask.CompletedTask)