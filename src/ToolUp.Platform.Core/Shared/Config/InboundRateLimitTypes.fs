// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs: the Phase 56 inbound rate-limit
// substrate types. Same namespace, same names — only the file boundary moved.

// ─── Wave 10 — Phase 56 inbound rate-limit substrate types ────────

/// Identity key used by the inbound `IRateLimitStore` substrate.
/// Identity-by-value (GP 12 rule 1) — every key is a serialisable
/// string. `InboundComposite` combines two dimensions (e.g. IP +
/// route) so a policy can rate-limit per-`(ip, route)` rather than
/// just per-IP. Stores partition counts per `InboundRateLimitKey`.
/// Distinct from the record-shaped `RateLimitKey` used by
/// `IRateLimiter` (outbound, per-provider quotas).
type InboundRateLimitKey =
    | IpAddressKey of string
    | UserIdKey of string
    | InboundComposite of string

module InboundRateLimitKey =
    /// Stable string projection for store-key derivation. Always
    /// prefixed so an IP key never collides with a UserId key that
    /// happens to be a valid IP literal.
    let asStoreKey =
        function
        | IpAddressKey ip -> sprintf "ip:%s" ip
        | UserIdKey u -> sprintf "uid:%s" u
        | InboundComposite c -> sprintf "c:%s" c

/// Window shape for an `IRateLimitStore` count. `PerSecond` /
/// `PerMinute` / `PerHour` / `PerDay` are calendar-aligned (the
/// store's wall-clock implementation truncates `OccurredAt` to the
/// matching boundary). `SlidingWindow` is duration-bounded; the
/// store keeps a rolling count of events within the trailing
/// window.
type RateLimitWindow =
    | PerSecond
    | PerMinute
    | PerHour
    | PerDay
    | SlidingWindow of duration: TimeSpan * bucketCount: int

/// Typed error payload returned to the client when a rate-limit
/// policy denies the request. Mirrored on the wire as the body of
/// the 429 response and emitted via `RateLimit-Limit` /
/// `RateLimit-Remaining` / `RateLimit-Reset` / `Retry-After`
/// headers.
type RateLimitedError = {
    RetryAfterSeconds: int
    Limit: int
    Window: RateLimitWindow
}

/// Outcome of an atomic `IncrementAndCheck` call on
/// `IRateLimitStore`. `AllowWithRemaining` carries the remaining
/// count so the middleware can stamp `RateLimit-Remaining`.
/// `DenyWithError` carries the typed error. Distinct from
/// `IRateLimiter`'s outbound `RateLimitDecision`.
type InboundRateLimitDecision =
    | AllowWithRemaining of remaining: int
    | DenyWithError of RateLimitedError

/// What to do when a policy's threshold is exceeded. `Return429` is
/// the canonical case (HTTP 429 with typed payload). `DelayAndAllow`
/// holds the request for up to the window boundary then admits it
/// (degraded-service mode — useful for non-critical analytics
/// endpoints). `DenySilently` returns 204 — for endpoints where the
/// caller should not be told they hit the cap (anti-abuse).
type RateLimitOnExceeded =
    | Return429
    | DelayAndAllow of maxDelay: TimeSpan
    | DenySilently

/// Selector for `RouteLimit.Key` — which axis the middleware reads.
type RateLimitKeyKind =
    | ByIp
    | ByUserId
    | ByComposite of customKey: string

/// Declarative rate-limit policy. `Route` is a prefix match
/// (case-insensitive `StartsWith`) against the request path. `Key`
/// names which `InboundRateLimitKey` dimension to evaluate; the
/// middleware extracts the value from `HttpContext` per dimension
/// (IP from `Connection.RemoteIpAddress`, UserId from the resolved
/// `AccessContext`, Composite from a developer-supplied
/// `keyOverride`).
type RouteLimit = {
    Route: string
    Key: RateLimitKeyKind
    Window: RateLimitWindow
    Threshold: int
    OnExceeded: RateLimitOnExceeded
}

/// Audit-event shape recorded when `IRateLimitStore.IncrementAndCheck`
/// returns a `Deny`. Surfaced via the Phase 61 PlatformAdmin rate-limit
/// event log widget (`/api/_platform/admin/rate-limits`). Identity-by-
/// value (GP 12 rule 1): the key is a serialisable DU over `string`,
/// the route is the matched prefix. Lives in `Platform.Core` (not
/// `Platform.Server`) because the Fable client widget parses this
/// shape over the wire — wire types must be visible to both tiers.
type RateLimitDecisionEvent = {
    Key: InboundRateLimitKey
    Route: string
    Window: RateLimitWindow
    Threshold: int
    Decision: InboundRateLimitDecision
    OccurredAt: DateTimeOffset
}

module RouteLimit =
    /// Per-IP-per-minute policy with `Return429` on exceed — the
    /// most common shape.
    let perIpPerMinute (route: string) (threshold: int) = {
        Route = route
        Key = ByIp
        Window = PerMinute
        Threshold = threshold
        OnExceeded = Return429
    }