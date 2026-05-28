# Phase 56 — Inbound HTTP rate-limit middleware + reference companions

**What changes.** The Phase 56 inbound `IRateLimitStore` substrate (types + interface + in-memory default, all shipped previously) gains:

1. `InboundRateLimitMiddleware` — ASP.NET Core middleware that composes against `ServerConfig.RateLimits`, evaluates each request against matching policies, and emits standard `RateLimit-*` / `Retry-After` headers. Mounts in `configurePipeline` after scope resolution. Works identically on Kestrel (`IServerHost.RunBlocking`) and serverless host adapters (`IServerHost.Invoke`).

2. `ApiError.ErrorCode.RateLimited of RateLimitedError` — typed case so module client code pattern-matches on the typed payload (countdown, threshold, window) rather than parsing the wire format.

3. `Components.RateLimitedBanner` Feliz component — renders the typed `RateLimitedError` with a countdown + retry affordance. Consumers opt in by handling the typed case explicitly; no auto-injection.

4. `ToolUp.RateLimit.AzureTableStorage` companion — ETag-retry atomic increment-and-check via Azure Table Storage. The Functions-default external store.

5. `ToolUp.RateLimit.Redis` companion — `INCR`+`EXPIRE` for calendar windows; Lua-scripted true sliding window. The Kestrel-multi-instance default for high-RPS workloads.

6. `IRateLimitStoreContract` test pack — every store impl binds to the same conformance bar (atomic increment, key isolation, window-boundary reset, threshold respected, GetRecentDecisions filter). Currently bound by `InMemoryRateLimitStore`; external companions bind from their own InProcess tests when live backends are available.

## Diff to apply

```fsharp
// Consumer opts in by setting RateLimitStore + declaring policies:
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        RateLimitStore = InMemoryRateLimitStore   // single-instance default
        RateLimits = [
            RouteLimit.perIpPerMinute "/api/calculate/" 60
            { Route = "/api/export/"
              Key = ByIp
              Window = PerHour
              Threshold = 100
              OnExceeded = Return429 }
        ]
}
```

For multi-instance deployments, swap to one of the reference companions:

```fsharp
open ToolUp.RateLimit.Redis

ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        RateLimitStore = ExternalRateLimitStore
        RateLimits = [ RouteLimit.perIpPerMinute "/api/calculate/" 60 ]
}
|> ServerApp.withServiceConfig (fun services ->
    let options = { Options.defaults with ConnectionString = ... }
    services.AddSingleton<IRateLimitStore>(RedisRateLimitStore.create options logger))
|> ServerApp.run
```

Module client code reacts to the typed error case:

```fsharp
match result with
| Error apiError ->
    match apiError.Code with
    | RateLimited rle ->
        // Render the banner with countdown
        Components.RateLimitedBanner.render rle (fun () -> dispatch RetryRequest)
    | _ -> defaultErrorView apiError
| Ok value -> renderValue value
```

## Standard response headers

The middleware emits IETF-draft `RateLimit-*` headers on every protected route. They sit alongside any handler-specific cache / security headers:

- `RateLimit-Limit: <threshold>` — the configured limit for this policy.
- `RateLimit-Remaining: <count>` — admittances left in the current window. 0 on denied responses.
- `RateLimit-Reset: <seconds>` — seconds until the current window boundary.
- `Retry-After: <seconds>` — sent on 429 only. Mirror of `RateLimit-Reset`.

Web clients and SDKs that respect these headers throttle automatically. Browser fetch hooks can read them to back off retry intervals.

## Edge rate-limiting (complementary, not alternative)

Edge rate-limiting at Cloudflare / Azure Front Door / AWS API Gateway / API Management stops volumetric attacks before the SDK sees them. The Phase 56 middleware is for **per-route business-policy limits** ("60 calculations per minute per IP") — fine-grained policies that the edge can't express because it doesn't have application-level context (which route, which user, which composite key).

Wire both:

- **Edge**: blanket-ban abusive IPs, rate-limit per-IP at the L7 boundary (e.g. 1000 req/min per IP).
- **SDK middleware**: per-route per-policy ("60 /api/calculate/ requests per IP per minute"; "100 /api/export/ requests per IP per hour").

The two are complementary. The SDK middleware sees only what survives the edge ceiling; the edge sees only the aggregate before route-keyed policies apply.

## Fail-open semantics

When `IRateLimitStore.IncrementAndCheck` returns `Error` (Redis flap, Azure Tables 503, etc.), the middleware logs at `Warn` and admits the request. Refusing every caller during a store outage is worse than briefly over-admitting; the operator notices the warn-storm and either restores the store or downgrades to in-memory until they do.

## Verification

1. `dotnet build ToolUp.Forge.sln` — clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — 0 failures, including the 8-test `IRateLimitStoreContract` pack bound to `InMemoryRateLimitStore`.
3. Stock deployment (`RateLimitStore = NoRateLimitStore`, default) — startup log identical to pre-Phase-56; the middleware is not mounted.
4. Opted-in deployment with one policy — confirm the 61st request from one IP in a minute returns 429 with `Retry-After`, and the response body parses as the JSON-rendered `RateLimitedError`.

## Rollback

`RateLimitStore = NoRateLimitStore` (default) + `RateLimits = []`. The middleware short-circuits to a no-op when no policies are declared, AND `compose` doesn't register `IRateLimitStore` at all when the mode is `NoRateLimitStore`.

## Out of scope (Phase 56 follow-ups)

- Sub-companions for Cosmos, DynamoDB, Memcached. Ship as triggered by consumer demand.
- Per-policy override of `OnExceeded` at the handler level (today the policy itself declares it). The substrate supports it; the per-handler API surface is a small future PR.
- `GetRecentDecisions` cross-instance aggregation — today each instance keeps its own buffer. Phase 61's PlatformAdmin widget aggregates via a separate metrics-sink pipe, not via the store.

## Six-rule portability audit (GP 12)

`IRateLimitStore` honours all six rules. Audit documented inline at `Server/IRateLimitStore.fs` top-of-file, in each companion's README, and verified by the `IRateLimitStoreContract` test pack:

1. **Identity by value.** `InboundRateLimitKey` is a serialisable DU over `string`. No live handles, no actor references.
2. **Async at every boundary.** Every interface method returns `Async<_>`.
3. **Retry-as-data.** Failures surface as `Result<_, RateLimitStoreError>`.
4. **Stateless boundaries.** Every `IncrementAndCheck` re-reads its state. The Azure Tables impl re-reads via ETag, Redis re-reads via INCR atomic, in-memory re-reads via per-key lock.
5. **No cross-shard ordering.** Counts partition per `(window, key)`. Cross-key totals are not guaranteed monotonic.
6. **Precision.** Each impl documents its atomic-increment ceiling (in-memory: process-local lock; Azure Tables: ~10-100 RPS per partition; Redis: ~100k RPS per instance).

## Consumers

The migration is **N-A** for consumers that don't expose anonymous traffic and don't need fine-grained per-route rate limits. Public-utility-class apps (SEO-driven calculators and similar) adopt for inbound anonymous protection.
