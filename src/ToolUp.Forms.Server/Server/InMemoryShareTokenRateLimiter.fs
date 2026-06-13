// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.InMemoryShareTokenRateLimiter

open System
open System.Collections.Concurrent
open ToolUp.Platform

// ─── Phase 21e — in-memory IShareTokenRateLimiter default ────────────
//
// **Single-instance only.** State lives in a per-process
// `ConcurrentDictionary` keyed by `(scopeId, tokenId)`; a
// multi-replica deployment cannot share window state across nodes,
// so an attacker hitting two replicas defeats the per-token cap in
// proportion to the replica count. Distributed deployments wire a
// companion backed by Redis / their `IRateLimitStore` (Phase 56) /
// equivalent.
//
// **Algorithm.** Sliding window via a per-key bounded queue of
// admission timestamps. On `Admit`:
//   1. Drop timestamps older than `rate.Window` (cheap when calls
//      are evenly spaced; bounded by `rate.MaxUses` in the worst
//      case).
//   2. If the queue's length is `< rate.MaxUses`, append now and
//      return `Ok`.
//   3. Otherwise return `Error RateLimited`. The queue is unchanged
//      so the next admission attempts still see the same state.
//
// Window precision is wall-clock UtcNow ticks — the interface
// declares sub-second precision is not promised (rule 6).

let private nowUtc () : DateTimeOffset = DateTimeOffset.UtcNow

type private WindowKey =
    struct
        val ScopeId: string
        val TokenId: string
        new(s, t) = { ScopeId = s; TokenId = t }
    end

/// In-memory `IShareTokenRateLimiter`. Thread-safe via per-key locks
/// on the underlying `ResizeArray`. Single-instance only — see
/// module header.
type InMemoryShareTokenRateLimiter() =
    let windows = ConcurrentDictionary<WindowKey, ResizeArray<DateTimeOffset>>()

    let getWindow (key: WindowKey) =
        windows.GetOrAdd(key, fun _ -> ResizeArray<DateTimeOffset>())

    interface IShareTokenRateLimiter with
        // Phase 136 part 2 — per-process window state (see module
        // header), so the partition guarantee is `false`. A scale-out
        // deployment that wires this limiter is caught by
        // `ShareTokenRateLimiterDistributionValidator` at startup.
        member _.IsDistributed = false

        member _.Admit(scopeId, tokenId, rate) = async {
            let key = WindowKey(scopeId, tokenId)
            let window = getWindow key

            return
                lock window (fun () ->
                    let now = nowUtc ()
                    let cutoff = now - rate.Window

                    // Drop timestamps older than the window. Linear in
                    // window length, bounded by `rate.MaxUses` for any
                    // admission that previously succeeded — the queue
                    // never grows past that cap.
                    while window.Count > 0 && window[0] <= cutoff do
                        window.RemoveAt 0

                    if window.Count < rate.MaxUses then
                        window.Add now
                        Ok()
                    else
                        Error ShareTokenError.RateLimited)
        }

/// Convenience constructor matching the SDK companion convention.
let create () : IShareTokenRateLimiter =
    InMemoryShareTokenRateLimiter() :> IShareTokenRateLimiter