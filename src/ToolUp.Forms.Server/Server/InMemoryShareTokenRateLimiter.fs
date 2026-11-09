// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.InMemoryShareTokenRateLimiter

open ToolUp.Platform
open ToolUp.Platform.Compose

// ─── Phase 21e — in-memory IShareTokenRateLimiter default ────────────
//
// **Single-instance only.** State lives in a per-process store
// keyed by `(scopeId, tokenId)`; a
// multi-replica deployment cannot share window state across nodes,
// so an attacker hitting two replicas defeats the per-token cap in
// proportion to the replica count. Distributed deployments wire a
// companion backed by Redis / their `IRateLimitStore` (Phase 56) /
// equivalent.
//
// **Algorithm.** The one in-memory sliding-window store,
// `ToolUp.Remoting.Server.InMemoryRateLimitStore` (Phase 1068), keyed per
// `(scopeId, tokenId)`: atomic per key, bounded by its bucket cap, and the
// same implementation the remoting dispatcher and the Phase 56 inbound
// store count against. A denied admission is not recorded, so the next
// attempt sees the same window. A non-positive `MaxUses` refuses every
// admission, as it always has here.
//
// Window precision is wall-clock UtcNow ticks — the interface
// declares sub-second precision is not promised (rule 6).

/// In-memory `IShareTokenRateLimiter`. Single-instance only — see the
/// module header.
type InMemoryShareTokenRateLimiter() =
    let store = ToolUp.Remoting.Server.InMemoryRateLimitStore()

    interface IShareTokenRateLimiter with
        // Phase 136 part 2 — per-process window state (see module
        // header), so the partition guarantee is `false`. A scale-out
        // deployment that wires this limiter is caught by
        // `ShareTokenRateLimiterDistributionValidator` at startup.
        member _.IsDistributed = false

        member _.Admit(scopeId, tokenId, rate) = async {
            if rate.MaxUses <= 0 then
                return Error ShareTokenError.RateLimited
            else
                // Length-prefixed, so no (scopeId, tokenId) pair can spell
                // another's key whatever characters either holds.
                let key = sprintf "%d:%s|%s" scopeId.Length scopeId tokenId

                match store.Acquire(key, rate.MaxUses, rate.Window) with
                | Admission.Admit _ -> return Ok()
                | Admission.Deny _ -> return Error ShareTokenError.RateLimited
        }

/// Convenience constructor matching the SDK companion convention.
let create () : IShareTokenRateLimiter =
    InMemoryShareTokenRateLimiter() :> IShareTokenRateLimiter