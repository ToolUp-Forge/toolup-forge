namespace ToolUp.Stripe.TierToken

/// The tier-claim DU. v0 ships the superset
/// (`Anonymous | Free | Personal | Teacher`). The `Pro | Enterprise`
/// extension lands at v0.2 once a consumer demands them.
///
/// Ordering matters: each constructor strictly dominates the previous,
/// which lets `TierGate.tierAtLeast` compare via integer comparison of
/// the discriminator. Stable across versions — new tiers slot in at
/// the top, never between existing ones.
type Tier =
    | Anonymous
    | Free
    | Personal
    | Teacher

module Tier =
    /// Order-preserving rank so `TierGate.tierAtLeast` can be a single
    /// integer comparison.
    let rank =
        function
        | Anonymous -> 0
        | Free -> 1
        | Personal -> 2
        | Teacher -> 3

    /// Parse the claim string back to a Tier. Unknown strings fall back
    /// to `Anonymous` — the safe minimum.
    let tryParse (s: string | null) : Tier =
        let normalised =
            match s with
            | null -> ""
            | v -> v.Trim().ToLowerInvariant()

        match normalised with
        | "teacher" -> Teacher
        | "personal" -> Personal
        | "free" -> Free
        | _ -> Anonymous

    /// String form used in tokens + DB rows.
    let toClaim =
        function
        | Anonymous -> "anonymous"
        | Free -> "free"
        | Personal -> "personal"
        | Teacher -> "teacher"

/// Tier-gating helpers.
module TierGate =
    /// Is the active tier at least `required`? E.g.
    /// `tierAtLeast Personal Free` is `false`.
    let tierAtLeast (required: Tier) (active: Tier) : bool = Tier.rank active >= Tier.rank required