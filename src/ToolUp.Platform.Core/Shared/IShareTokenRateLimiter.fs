// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 21e — per-token share-token rate limiter ──────────────────
//
// Companion-extensible gate over the share-token use rate. Distinct
// from `IRateLimiter` (Phase 9v outbound HTTP) and from
// `ServerConfig.RateLimit` (Phase 9 inbound per-IP/team): this gate
// is consulted by share-token consumers (e.g. `IPublicFormApi`) on
// every token-gated request to decide whether to admit it inside the
// token's declared `ShareTokenRateLimit` window.
//
// Threat model: a publishable-form share-link token leaks; the
// attacker exercises the token at full request rate until its
// `UseLimit` is exhausted (fast enough to skew aggregations or
// poison training data). Per-token rate-limit caps the per-window
// admission so a leaked token's blast radius is bounded.
//
// **Six-rule portability audit (Guiding Principle 12).**
//   1. Identity by value      — `scopeId` / `tokenId` are strings;
//                                 `ShareTokenRateLimit` is a value
//                                 record. No live framework handles
//                                 cross the boundary.
//   2. Async at boundary      — `Admit` returns
//                                 `Async<Result<unit, ShareTokenError>>`.
//   3. Retry as data          — `RateLimited` is a `ShareTokenError`
//                                 case; consumers branch on the tag.
//                                 No callbacks; no thrown exceptions
//                                 on the rate-limit path.
//   4. Stateless handler      — sliding-window state lives in the
//                                 limiter, not the caller. A
//                                 distributed companion (Redis /
//                                 `IRateLimitStore`-backed) replaces
//                                 the in-process default without
//                                 caller changes.
//   5. No cross-shard ordering — per-`tokenId` ordering only. Two
//                                 `Admit` calls for different tokens
//                                 may resume in any order.
//   6. Precision documented   — windows are validated at issue-time
//                                 to be >= 1 second
//                                 (`ShareTokenTypes.validateRateLimit`).
//                                 Sub-second precision is not promised;
//                                 the in-memory default rounds window
//                                 entries to wall-clock ticks.

/// Phase 21e — per-token rate-limit gate. Implementations partition
/// by `(scopeId, tokenId)` and consult the claim's
/// `RateLimit: ShareTokenRateLimit option`. The consumer
/// (`IPublicFormApi.SubmitWithToken`) calls `Admit` before persisting
/// any side-effect and surfaces `ShareTokenError.RateLimited` on
/// rejection. The in-memory default is `InMemoryShareTokenRateLimiter`
/// (Forms.Server); distributed deployments wire a companion backed
/// by Redis / their `IRateLimitStore` (Phase 56) / equivalent.
///
/// Implementations must be thread-safe — Fable.Remoting and the
/// public-form handler may invoke from multiple request threads
/// concurrently against the same token.
type IShareTokenRateLimiter =
    /// Consult the rolling window for `(scopeId, tokenId)`. Returns
    /// `Ok ()` when admission is granted (and bookkeeps the
    /// admission against the window); returns
    /// `Error ShareTokenError.RateLimited` when the caller has
    /// consumed its `rate.MaxUses` budget inside the rolling
    /// `rate.Window`. Implementations whose configuration does not
    /// recognise the token (e.g. no claim ever observed) return
    /// `Ok ()` — no declared rate = no gate. Token claims without a
    /// configured `RateLimit` skip this call entirely; the consumer
    /// never invokes `Admit` for them.
    abstract Admit:
        scopeId: string * tokenId: string * rate: ShareTokenRateLimit -> Async<Result<unit, ShareTokenError>>