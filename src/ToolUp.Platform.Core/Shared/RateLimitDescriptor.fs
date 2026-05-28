// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 9v — declarative outbound rate-limit descriptors ──────────
//
// `RateLimitDescriptor` is the compose-time declaration form: each
// companion that issues outbound calls to a quota-bearing provider
// (Strava, GA4, OpenAI, Anthropic, HubSpot, Salesforce, etc.) registers
// one descriptor naming its provider, the short-window quota
// (count / duration), the optional long-window quota (typically a
// daily ceiling), and a fairness mode that decides whether the window
// is per-tenant or shared across the deployment.
//
// Descriptors carry no runtime state; the registered `IRateLimiter`
// looks them up by `Provider` at `Wait` time. A `RateLimitKey` whose
// `Provider` does not match any registered descriptor is admitted
// immediately (the default `IRateLimiter` is silent for unregistered
// providers — a companion that forgets to register is not
// rate-limited at all, which fails open by design so a misconfig
// doesn't manifest as an outage).

/// How a descriptor partitions its window across callers.
///
///   * `PerScope` — each `RateLimitKey.ScopeId` gets its own window.
///     The natural fit for per-user OAuth connections — each Strava
///     athlete has their own 100/15min budget against Strava's quota;
///     one user saturating their bucket does not slow another user.
///   * `PerProvider` — all callers share one window. The natural fit
///     for organisation-tier provider quotas — Anthropic
///     per-organisation throughput, OpenAI tier-based RPM apply to
///     the whole deployment regardless of which tenant requested.
///
/// `SubKey` partitions further within either mode — a GA4 descriptor
/// with `PerScope` fairness still gives each property (the property
/// id carried in `RateLimitKey.SubKey`) its own window inside one
/// scope.
type RateLimitFairnessMode =
    | PerScope
    | PerProvider

/// Declared limits for one upstream provider. Companions call
/// `ServerApp.withRateLimitDescriptor` at compose time once per
/// provider they consume; the SDK feeds the accumulated list into
/// the registered `IRateLimiter` at construction so `Wait` calls
/// know which window to apply for each `RateLimitKey.Provider`.
///
///   * `Provider` — string label matching the `Provider` field on
///     `RateLimitKey` at emission sites.
///   * `ShortWindow` — `(count, duration)` pair (e.g. `(100, 15 min)`
///     for Strava's short window). The default in-process limiter
///     applies a soft 95% ceiling and holds calls past it, resuming
///     them as the sliding window opens up; the soft ceiling leaves
///     headroom for retries inside a single window.
///   * `LongWindow` — optional `(count, duration)` for daily-style
///     quotas (e.g. `(1_000, 24 h)` for Strava). When the long
///     window is exhausted the limiter returns `Refused` — callers
///     surface the refusal rather than blocking past the window
///     reset (typically much longer than the request budget).
///   * `FairnessMode` — partitioning model. See `RateLimitFairnessMode`.
type RateLimitDescriptor = {
    Provider: string
    ShortWindow: int * TimeSpan
    LongWindow: (int * TimeSpan) option
    FairnessMode: RateLimitFairnessMode
}