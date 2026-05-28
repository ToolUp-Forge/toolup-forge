// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 9v — outbound call rate limiter ───────────────────────────
//
// Portable interface for rate-limiting **outbound** HTTP / SDK calls
// (data-source connectors, AI providers, transactional sinks, webhook
// dispatchers). Each companion declares its quota at compose time via
// `RateLimitDescriptor`; emission sites call `Wait` before every
// outbound request and branch on the returned `RateLimitDecision`.
//
// **Distinct from `ServerConfig.RateLimit`.** That field (Phase 9
// inbound limiter) gates platform-edge HTTP traffic by team / user /
// IP and lives entirely inside ASP.NET Core's `RateLimiter` middleware.
// `IRateLimiter` (this phase) gates calls *outbound* to third-party
// services and partitions by `(scopeId, provider)` plus an optional
// `subKey`. They share no code path — replacing one does not affect
// the other.
//
// **Six-rule portability audit (Guiding Principle 12 — Phase 9c).**
//   1. Identity by value      — `RateLimitKey` is a record with three
//                                string-shaped fields; identity is
//                                structural so equivalent values from
//                                different construction sites hit the
//                                same bucket.
//   2. Async at boundary      — `Wait` returns `Async<RateLimitDecision>`.
//                                Implementations are free to short-circuit
//                                synchronously when the window has slack,
//                                but the contract is async.
//   3. Retry as data          — outcome is a DU; callers branch on the
//                                tag rather than observe a callback.
//                                `Refused` is data the caller surfaces,
//                                not an exception or back-pressure
//                                signal.
//   4. Stateless handler      — window state lives in the limiter
//                                implementation, not the caller. A
//                                distributed companion (Phase 9c half-2
//                                Redis-backed) can replace the in-process
//                                default with no caller change.
//   5. No cross-shard ordering — per-key ordering only. Two `Wait`
//                                calls on different keys may resume in
//                                any order; the contract makes no
//                                cross-key ordering promise.
//   6. Precision documented   — sliding-window defaults carry a
//                                1-second lower bound. Sub-second
//                                precision is not promised; callers
//                                needing tighter pacing implement
//                                their own gate above this one.

/// Identifies a rate-limit bucket. `ScopeId` is the per-tenant key
/// (typically the scope the caller's storage is partitioned under);
/// `Provider` is the upstream label matching the
/// `RateLimitDescriptor.Provider` declared at compose time
/// (`"strava"`, `"ga4"`, `"openai"`); `SubKey` partitions a provider
/// further (e.g. per-property quotas on GA4, per-tier shapes on
/// OpenAI). Identity is by-value (rule 1) — equivalent records from
/// different construction sites map to the same bucket.
type RateLimitKey = {
    ScopeId: string
    Provider: string
    SubKey: string option
}

/// Outcome of a `Wait` call. `Proceed` means the call was admitted
/// immediately (window had slack); `DelayedBy` reports the wall-clock
/// time the caller was held inside `Wait` before admission — waits
/// exceeding `ServerConfig.SlowRateLimitThreshold` qualify the call
/// for a `RateLimitWaited` audit row. `Refused` is a long-window-
/// exhausted outcome (typically a daily quota that recovers at
/// window-reset); the caller surfaces the refusal rather than blocking
/// past the current request budget.
type RateLimitDecision =
    | Proceed
    | DelayedBy of waitedFor: TimeSpan
    | Refused of reason: string

/// Outbound rate-limit gate. Implementations partition by the supplied
/// `RateLimitKey` and apply the declared `RateLimitDescriptor` for the
/// key's `Provider`. Hot-path callers `do! ctx.RateLimiter.Wait(key)`
/// before every outbound HTTP / SDK call; the limiter holds the call
/// until the window has slack, or returns `Refused` when the long-
/// window quota is exhausted.
///
/// Implementations must be thread-safe — callers may invoke from
/// request threads, job-handler executors, and SSE-stream consumers
/// concurrently against the same key.
type IRateLimiter =
    /// Acquire a slot in the window for `key`. Returns `Proceed` /
    /// `DelayedBy` synchronously or asynchronously depending on
    /// window slack; returns `Refused` when the long-window quota
    /// declared by the matching `RateLimitDescriptor` is exhausted.
    /// Implementations whose registered descriptor's `Provider` does
    /// not match `key.Provider` return `Proceed` immediately (no
    /// declared limit = no gate).
    abstract Wait: key: RateLimitKey -> Async<RateLimitDecision>