// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 62 — feature-flag sources ─────────────────────────────
//
// Extra gating sources applied to a feature flag on top of the
// `User → Team → Platform → declared default` scope walk that
// `FlagEvaluator` runs. A registered source short-circuits the walk
// to `false` when the source's condition fails — independent of any
// scope override.
//
// Today the only source is `PremiumOnly`, gating on
// `IUserClaims.GetPremiumStatus`. Future sources (`BetaCohort`,
// `RegionalAvailability`) compose by adding another DU branch; the
// evaluator's source dispatch is the registered append point.
//
// Registration is purely additive: keys without an entry preserve
// today's evaluator behaviour byte-for-byte. Deployments that don't
// use premium gating need not import this module — the default
// `FlagEvaluator.create` ignores sources entirely.

/// Extra gating source applied on top of the scope walk for a given
/// flag key. A flag may declare exactly one source — sources do not
/// compose with each other (a future need to compose would extend
/// this to a list, but no current consumer needs that shape).
[<RequireQualifiedAccess>]
type FeatureFlagSource =
    /// Flag is enabled only when the requesting user holds `Premium`
    /// status (as reported by `IUserClaims.GetPremiumStatus`).
    /// Anonymous users and non-premium logged-in users see the flag
    /// as `false` regardless of any scope override. Premium users see
    /// the value resolved by the normal scope walk — a `PremiumOnly`
    /// flag whose declared default is `false` still needs a platform
    /// or per-user override to flip on for premium users; the source
    /// is a *floor*, not a value substitute.
    | PremiumOnly

/// Registry mapping flag keys to extra gating sources. Constructed
/// at compose time from module + platform flag declarations. Lookup
/// is the only operation — the registry is immutable post-compose.
type FeatureFlagSourceRegistry = {
    /// Source for a given flag key, or `None` if the key carries no
    /// extra gating beyond the standard scope walk.
    TryFind: string -> FeatureFlagSource option
}

module FeatureFlagSourceRegistry =
    /// Empty registry — every key returns `None`. The default
    /// supplied by `FlagEvaluator.create` so callers that have no
    /// premium-gated flags pay zero overhead.
    let empty: FeatureFlagSourceRegistry = { TryFind = fun _ -> None }

    /// Construct a registry from a `Map`. The map is captured by
    /// reference into a closure — callers must not mutate the source
    /// map after `ofMap` returns.
    let ofMap (map: Map<string, FeatureFlagSource>) : FeatureFlagSourceRegistry = {
        TryFind = fun key -> Map.tryFind key map
    }

    /// Build a registry by listing premium-gated keys explicitly.
    /// Convenience for the common case where a deployment knows the
    /// finite set of `PremiumOnly` flags at compose time.
    let premiumOnly (keys: string seq) : FeatureFlagSourceRegistry =
        let map = keys |> Seq.map (fun k -> k, FeatureFlagSource.PremiumOnly) |> Map.ofSeq

        ofMap map