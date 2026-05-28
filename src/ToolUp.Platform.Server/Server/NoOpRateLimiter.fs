namespace ToolUp.Platform

// ─── Phase 9v — no-op outbound rate limiter ──────────────────────────
//
// Default `IRateLimiter` registered when
// `ServerConfig.RateLimiter = NoRateLimiter` (GP 13 — no opt-in, zero
// runtime cost). Returns `Proceed` immediately for every `Wait` call so
// emission sites (data-source connectors, AI providers, webhook
// dispatchers) resolve `IRateLimiter` from DI unconditionally and the
// call elides. Mirrors `NoOpMetricsSink` / `NoOpUsageLog` / `NoOpAuditLog`.

/// SDK-default no-op implementation of `IRateLimiter`. Registered when
/// `ServerConfig.RateLimiter = NoRateLimiter` so consumers resolve the
/// service unconditionally — `IRateLimiter` is never `null` in DI
/// when the SDK is composed.
type NoOpRateLimiter() =
    interface IRateLimiter with
        member _.Wait(_key) = async { return Proceed }