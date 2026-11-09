// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open ToolUp.Platform

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
    /// Atomically count one call for `key` in `window` against
    /// `threshold` and answer the `Admission`. A store that cannot answer
    /// returns `Error`; what the caller then sees is the route's declared
    /// `StorePosture`, decided once by `RateLimitVerdict.ofResult`, never
    /// by the store.
    abstract IncrementAndCheck:
        key: InboundRateLimitKey * window: RateLimitWindow * threshold: int ->
            Async<Result<Admission, RateLimitStoreError>>

// `RateLimitDecisionEvent` (the operator-facing wire payload returned
// by `GetRecentDecisions`) lives in `Platform.Core/Shared/Config/InboundRateLimitTypes.fs`
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

/// What a consumer acts on after one `IncrementAndCheck` (Phase 1068): the
/// store's answer, or — when the store failed — what the route's declared
/// `StorePosture` decided. The fail-open/fail-closed decision is taken
/// here, once, not re-decided by each consumer.
[<RequireQualifiedAccess>]
type RateLimitVerdict =
    /// The store answered.
    | Answered of Admission
    /// The store failed under `StorePosture.FailOpen`: admit, and log why.
    | AdmittedOnStoreFailure of reason: string
    /// The store failed under `StorePosture.FailClosed`: refuse, and audit why.
    | RefusedOnStoreFailure of reason: string

module RateLimitVerdict =
    /// Decide one `IncrementAndCheck` result under `posture`.
    let ofResult (posture: StorePosture) (result: Result<Admission, RateLimitStoreError>) : RateLimitVerdict =
        match result with
        | Ok admission -> RateLimitVerdict.Answered admission
        | Error(StoreUnavailable reason)
        | Error(StoreContractViolation reason) ->
            match posture with
            | StorePosture.FailOpen -> RateLimitVerdict.AdmittedOnStoreFailure reason
            | StorePosture.FailClosed -> RateLimitVerdict.RefusedOnStoreFailure reason

    /// Record a `StorePosture.FailClosed` refusal as a `RateLimitRefused`
    /// audit row under `scopeId` through `log` (none composed, nothing
    /// written — GP 13). The caller hands the log in as a value: the
    /// middleware takes it by constructor injection, a handler from its own
    /// composition. `limiter` names the budget that could not be counted (a
    /// route prefix, a budget name); the row carries no caller key, so no
    /// address or user id reaches the trail. Best-effort like every audit
    /// write: a failing log never changes the response.
    let auditRefusal (log: IAuditLog option) (scopeId: string) (limiter: string) (reason: string) : Async<unit> = async {
        match log with
        | Some log ->
            try
                do!
                    log.Record(
                        scopeId,
                        AuditEvent.RateLimitRefused {
                            ScopeId = scopeId
                            Provider = limiter
                            SubKey = None
                            Reason = sprintf "rate-limit store failed under StorePosture.FailClosed: %s" reason
                        }
                    )
                    |> AuditRow.bestEffort
            with _ ->
                ()
        | None -> ()
    }