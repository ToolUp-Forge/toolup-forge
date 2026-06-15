namespace ToolUp.Stripe.TierToken

/// The tier-claim DU
/// (`Anonymous | Free | Personal | Teacher | Pro | Enterprise`).
///
/// Ordering matters: each constructor strictly dominates the previous,
/// which lets `TierGate.tierAtLeast` compare via integer comparison of
/// the discriminator. Stable across versions — new tiers slot in at
/// the **top** of the ordering, never between existing ones, so `rank`
/// / `tryParse` / `toClaim` extend without renumbering and tokens
/// minted before a new tier landed keep their meaning. `Pro` and
/// `Enterprise` were added above `Teacher` for exactly this reason.
type Tier =
    | Anonymous
    | Free
    | Personal
    | Teacher
    | Pro
    | Enterprise

module Tier =
    /// Order-preserving rank so `TierGate.tierAtLeast` can be a single
    /// integer comparison. New tiers take ranks strictly above the
    /// existing ones; existing ranks are NEVER renumbered (that would
    /// reinterpret stored claims).
    let rank =
        function
        | Anonymous -> 0
        | Free -> 1
        | Personal -> 2
        | Teacher -> 3
        | Pro -> 4
        | Enterprise -> 5

    /// Parse the claim string back to a Tier. Unknown strings fall back
    /// to `Anonymous` — the safe minimum.
    let tryParse (s: string | null) : Tier =
        let normalised =
            match s with
            | null -> ""
            | v -> v.Trim().ToLowerInvariant()

        match normalised with
        | "enterprise" -> Enterprise
        | "pro" -> Pro
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
        | Pro -> "pro"
        | Enterprise -> "enterprise"

/// Tier-gating helpers.
module TierGate =
    /// Is the active tier at least `required`? E.g.
    /// `tierAtLeast Personal Free` is `false`.
    let tierAtLeast (required: Tier) (active: Tier) : bool = Tier.rank active >= Tier.rank required