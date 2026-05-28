// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── IRateLimitStore — Phase 56 inbound rate-limit substrate ─────────
//
// Portable inbound HTTP rate-limit store. Default impl
// `InMemoryRateLimitStore` is single-instance only. External-store
// companions ship as sub-packages (`ToolUp.RateLimit.Redis`,
// `ToolUp.RateLimit.AzureTableStorage`) implementing this interface.
//
// **Phase 9c portability rules** (all six honoured):
//
//   1. Identity by value. `InboundRateLimitKey` is a serialisable DU
//      over `string` — no `IActorRef` or other live handles.
//   2. Async at every boundary. Every method returns `Async<_>`.
//   3. Retry / supervision as data. `RateLimitStoreError` typed; no
//      `OnFailure` callbacks.
//   4. Stateless handlers between invocations. `IncrementAndCheck`
//      re-reads its state per call — works on any node.
//   5. No cross-shard ordering promises. Counts partition by
//      `InboundRateLimitKey`; cross-key totals are not guaranteed
//      monotonic.
//   6. Precision: documented atomic-increment ceiling — store
//      implementations declare their per-key throughput (Redis INCR
//      is O(1); Azure Table is ETag-retry-bounded).

/// Typed error from `IRateLimitStore` operations. Stores wrap their
/// own transport errors in `StoreUnavailable`; semantic violations
/// (a bug in the store impl) surface as `StoreContractViolation`.
type RateLimitStoreError =
    | StoreUnavailable of reason: string
    | StoreContractViolation of reason: string

/// Read-side of the rate-limit store. ISP-split from the write side
/// so companions can register a read-only view (admin widgets,
/// telemetry exporters).
type IRateLimitReader =
    /// Return the current count for the given key in the given
    /// window. Used by the `PlatformAdmin` rate-limit dashboard
    /// (Phase 61) — not in the hot middleware path.
    abstract GetCurrent: key: InboundRateLimitKey * window: RateLimitWindow -> Async<int>

/// Write-side of the rate-limit store. Atomic
/// increment-and-check-against-threshold — load-bearing for
/// serverless correctness under concurrent invocations.
type IRateLimitWriter =
    /// Atomically increment the count for `key` in `window`, compare
    /// against `threshold`, and emit a decision. Always
    /// allow-on-store-failure: the middleware degrades open rather
    /// than denying every request when the store is down (see
    /// `RateLimitMiddleware` for the fail-open contract).
    abstract IncrementAndCheck:
        key: InboundRateLimitKey * window: RateLimitWindow * threshold: int ->
            Async<Result<InboundRateLimitDecision, RateLimitStoreError>>

// `RateLimitDecisionEvent` (the operator-facing wire payload returned
// by `GetRecentDecisions`) lives in `Platform.Core/Shared/SDK.Shared.fs`
// so the Fable client widget can parse it without a server-tier
// reference. See the Phase 56 rate-limit types block there.

/// Aggregate interface combining reader + writer + the Phase 61
/// `GetRecentDecisions` extension (operator-facing read of recent
/// deny events).
type IRateLimitStore =
    inherit IRateLimitReader
    inherit IRateLimitWriter

    /// Most-recent deny decisions, newest first. `count` caps the
    /// returned size; `keyFilter = None` returns events across all
    /// keys. Used by the PlatformAdmin rate-limit event log
    /// (Phase 61). Implementations may best-effort retain only the
    /// last N events; the in-memory default keeps the last 1000.
    abstract GetRecentDecisions:
        keyFilter: InboundRateLimitKey option * count: int -> Async<RateLimitDecisionEvent list>