// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Phase 642 — federated data-visibility authority levels ──────────
//
// What a remote peer may see of a data host's raw data, as an EXPLICIT,
// DECLARED, SERVER-ENFORCED level rather than as a property somebody
// infers from which operations happen to exist.
//
// The federated model-execution profile shipped closed against row
// egress: no operation on it can carry a row, so a modeller sees
// governed aggregates and nothing else. That posture is correct and it
// is also unnamed — a counterparty reading the profile learns what it
// may ask for, not what the deployment has AGREED it may see, and a
// deployment that later wants to offer more has no vocabulary to say so
// in. Three levels give both sides the word:
//
//   * `AggregatesOnly` — governed diagnostics and metadata. The shipped
//     posture, now named, and the DEFAULT: a deployment that declares
//     nothing declares this, and a descriptor whose level member is
//     absent reads as this. Fail-closed in both directions.
//   * `ViewOnly` — server-rendered bounded views. The data is rendered
//     where it lives and the RENDERING crosses the seam, never the
//     series. The machinery is a later phase; the level is declared and
//     enforced here, so a request for a view is refused as an AUTHORITY
//     question from the first day rather than as an unknown operation.
//   * `Full` — raw data. Co-located or otherwise fully-trusted
//     deployments only. **A declaration, not a new surface**: nothing in
//     the profile serves a row at any level, and this level does not add
//     one. It exists so that a deployment which one day does serve one
//     cannot serve it below `Full`, and so that asking is refused as an
//     authority question rather than as an unrecognised string.
//
// **The honesty boundary, stated where the type is defined rather than
// only in the specification.** This is bulk-egress prevention plus an
// audit trail. It is NOT cryptographic non-exportability: a peer granted
// `ViewOnly` sees rendered values and can transcribe what it sees, and a
// peer granted `Full` has the data. What the level buys is that the
// *bulk* path is closed by construction at the lower two levels, that
// the grant is declared in a document both sides pin, and that every
// gated answer leaves a record. A control that claims more than that
// would be claiming to prevent a human reading a screen.
//
// **Ordering is total, and that is load-bearing.** Unlike the opaque
// string facets of the trust posture — whose values are unordered, so a
// disagreement can only be reported as `mixed:` — the levels are ranked,
// so "narrower" is a computable fact. Every comparison in this file goes
// through `rank`; no call site compares labels.

/// What a remote peer may see of this deployment's data. Ordered:
/// `AggregatesOnly` < `ViewOnly` < `Full`.
[<RequireQualifiedAccess>]
type PeerDataVisibilityLevel =
    /// Governed aggregates + metadata only — the default, and what an
    /// undeclared or unreadable declaration reads as.
    | AggregatesOnly
    /// Server-rendered bounded views of the data, rendered data-side.
    | ViewOnly
    /// Raw data. Co-located / trusted deployments only.
    | Full

[<RequireQualifiedAccess>]
module PeerDataVisibilityLevel =

    /// The level a deployment holds when it has declared nothing, and
    /// the level an absent or unrecognised declaration reads as.
    let default': PeerDataVisibilityLevel = PeerDataVisibilityLevel.AggregatesOnly

    /// The stable wire label. A closed vocabulary of exact case names, so
    /// a peer written in another language emits a string a shell script
    /// can produce and a reader can compare without a parser.
    let label (level: PeerDataVisibilityLevel) : string =
        match level with
        | PeerDataVisibilityLevel.AggregatesOnly -> "AggregatesOnly"
        | PeerDataVisibilityLevel.ViewOnly -> "ViewOnly"
        | PeerDataVisibilityLevel.Full -> "Full"

    /// The total order. Every comparison in the family goes through
    /// this — a call site that compared labels would be comparing
    /// alphabetically, which puts `Full` below `ViewOnly` and is exactly
    /// backwards.
    let rank (level: PeerDataVisibilityLevel) : int =
        match level with
        | PeerDataVisibilityLevel.AggregatesOnly -> 0
        | PeerDataVisibilityLevel.ViewOnly -> 1
        | PeerDataVisibilityLevel.Full -> 2

    /// Every level, weakest first — the enumeration an operator surface
    /// and a diagnostic message both read from, so neither hand-lists it.
    let all: PeerDataVisibilityLevel list = [
        PeerDataVisibilityLevel.AggregatesOnly
        PeerDataVisibilityLevel.ViewOnly
        PeerDataVisibilityLevel.Full
    ]

    /// Parse a declared label, exactly. `None` for anything else —
    /// including `null` and the empty string.
    let ofLabel (declared: string) : PeerDataVisibilityLevel option =
        all |> List.tryFind (fun level -> label level = declared)

    /// **The fail-closed read, and the only one a descriptor should
    /// use.** An absent member (`null` after deserialisation), an empty
    /// string, or a label this build does not recognise all read as
    /// `AggregatesOnly`.
    ///
    /// The `null` arm is the pre-642 case: a counterparty's label
    /// published before this member existed carries no level, and the
    /// honest reading of "said nothing" is the narrowest level, never the
    /// broadest. The unrecognised arm is the forward case: a label
    /// naming a level a later version invented must not widen this
    /// deployment's behaviour, because a level it cannot name is a level
    /// it cannot enforce.
    let ofLabelOrDefault (declared: string) : PeerDataVisibilityLevel =
        match ofLabel declared with
        | Some level -> level
        | None -> default'

    /// Does a peer holding `declared` reach what `required` demands?
    let admits (declared: PeerDataVisibilityLevel) (required: PeerDataVisibilityLevel) : bool =
        rank required <= rank declared

    /// The narrower of two levels. **Narrowing may only lower**: this is
    /// a minimum and never a maximum, which is what makes every layer of
    /// the scope walk below monotone.
    let narrow (a: PeerDataVisibilityLevel) (b: PeerDataVisibilityLevel) : PeerDataVisibilityLevel =
        if rank b < rank a then b else a

    /// The floor over a set of levels — the narrowest any of them
    /// declares, and `AggregatesOnly` for the empty set.
    ///
    /// An aggregate surface (a gateway fronting a group) publishes this:
    /// a call routed through the group lands on some member, so the group
    /// can honestly claim only what its weakest exposing member claims.
    /// Unlike the opaque posture facets there is no `mixed:` marker,
    /// because the levels are ORDERED — a floor is computable, and
    /// reporting a computable floor as a disagreement would throw away
    /// the one thing this facet has that those do not.
    let floor (levels: PeerDataVisibilityLevel list) : PeerDataVisibilityLevel =
        match levels with
        | [] -> default'
        | _ -> levels |> List.reduce narrow

// ─── The scope walk (the Phase 637 narrowing shape) ──────────────────
//
// The peer's declared level is a CEILING. Beneath it a deployment may
// narrow per team and per user, and the walk composes exactly as the
// module-visibility walk does: outermost-first, every layer narrowing the
// one before it, no layer able to widen.
//
// This is deliberately not the feature-flag walk's semantics. A flag
// walks innermost-first and the first hit wins, because a user opting
// into a beta the platform left off is the point. Applied to a data
// grant that would mean the innermost, least authoritative layer could
// re-admit data the agreement excluded — so the layers compose
// monotonically instead, and "per-user narrowing beneath the peer
// ceiling" means exactly what it says.

/// One layer of the peer → team → user walk.
type PeerVisibilityScope =
    /// The peer's own declared ceiling — the outermost layer, and the
    /// only one that is not a narrowing.
    | PeerCeiling of peerId: string
    /// A team beneath the peer's binding.
    | TeamNarrowing of teamId: string
    /// A user beneath the team.
    | UserNarrowing of userId: string

[<RequireQualifiedAccess>]
module PeerVisibilityScope =

    /// Canonical string form, shared by refusal messages and audit rows
    /// so the two never word the same scope differently.
    let toString (scope: PeerVisibilityScope) : string =
        match scope with
        | PeerCeiling peerId -> $"peer:{peerId}"
        | TeamNarrowing teamId -> $"team:{teamId}"
        | UserNarrowing userId -> $"user:{userId}"

/// One declared narrowing beneath the peer ceiling.
type PeerVisibilityLayer = {
    Scope: PeerVisibilityScope
    /// What this layer declares. A value ABOVE what the layer inherited
    /// is clamped, never honoured — see `PeerVisibility.resolve`.
    Level: PeerDataVisibilityLevel
}

/// What one peer binding declares: the ceiling, plus the narrowing
/// layers this deployment holds beneath it, outermost-first.
///
/// The layers live on the binding rather than being fetched per call
/// because a peer binding is receiver-side declared data — the same
/// place the addressed scope is decided — and because the execution side
/// of a long-running fit has no request and no principal to fetch them
/// from (GP 12 rules 1 and 4).
type PeerVisibilityBinding = {
    Ceiling: PeerDataVisibilityLevel
    /// Ordered outermost-first (`Team` before `User`). Empty is the
    /// ordinary case: the ceiling is the effective level.
    Narrowing: PeerVisibilityLayer list
}

/// The resolved answer for one binding: the ceiling it started from, the
/// level after the walk, and — recorded rather than discarded — every
/// layer that tried to widen.
type PeerVisibilityResolution = {
    /// The peer's declared ceiling.
    Ceiling: PeerDataVisibilityLevel
    /// After the walk. Never above `Ceiling`, by construction.
    Effective: PeerDataVisibilityLevel
    /// The scopes that contributed, outermost-first, starting with the
    /// peer ceiling. Never empty.
    ContributingScopes: PeerVisibilityScope list
    /// Layers whose declared level was ABOVE what they inherited. They
    /// were clamped to the inherited level; naming them here is what
    /// keeps a mis-declared layer visible to an operator instead of
    /// silently doing nothing. A non-empty list is a configuration
    /// defect, not a refusal — the request is judged on `Effective`.
    ClampedScopes: PeerVisibilityScope list
    /// The innermost scope that actually LOWERED the level, when any.
    /// This is what a narrowing refusal names: "the ceiling would have
    /// admitted this; that layer does not".
    NarrowedBy: PeerVisibilityScope option
}

[<RequireQualifiedAccess>]
module PeerVisibilityBinding =

    /// The binding of a peer that declared nothing: the default ceiling,
    /// no narrowing. Byte-for-byte the pre-642 posture (GP 11).
    let default': PeerVisibilityBinding = {
        Ceiling = PeerDataVisibilityLevel.default'
        Narrowing = []
    }

    /// A ceiling with no narrowing beneath it — the ordinary bilateral
    /// grant.
    let ofCeiling (ceiling: PeerDataVisibilityLevel) : PeerVisibilityBinding = { Ceiling = ceiling; Narrowing = [] }

    /// Add one narrowing layer. Appended, so declaration order IS walk
    /// order and a composition states `Team` before `User`.
    let withNarrowing (scope: PeerVisibilityScope) (level: PeerDataVisibilityLevel) (binding: PeerVisibilityBinding) = {
        binding with
            Narrowing = binding.Narrowing @ [ { Scope = scope; Level = level } ]
    }

[<RequireQualifiedAccess>]
module PeerVisibility =

    /// Walk the layers beneath a ceiling and fold them into one
    /// resolution.
    ///
    /// Total and pure over its inputs — no store read, no clock, nothing
    /// ambient — so both halves of a long-running call resolve the same
    /// way from the same binding, and a test can assert a walk without
    /// composing anything.
    let resolve (binding: PeerVisibilityBinding) (peerId: string) : PeerVisibilityResolution =
        let folded =
            binding.Narrowing
            |> List.fold
                (fun (level, clamped, narrowedBy) layer ->
                    let next = PeerDataVisibilityLevel.narrow level layer.Level

                    let clamped =
                        if PeerDataVisibilityLevel.rank layer.Level > PeerDataVisibilityLevel.rank level then
                            clamped @ [ layer.Scope ]
                        else
                            clamped

                    let narrowedBy =
                        if PeerDataVisibilityLevel.rank next < PeerDataVisibilityLevel.rank level then
                            Some layer.Scope
                        else
                            narrowedBy

                    next, clamped, narrowedBy)
                (binding.Ceiling, [], None)

        let effective, clamped, narrowedBy = folded

        {
            Ceiling = binding.Ceiling
            Effective = effective
            ContributingScopes = PeerCeiling peerId :: (binding.Narrowing |> List.map _.Scope)
            ClampedScopes = clamped
            NarrowedBy = narrowedBy
        }

    /// The resolution of a peer that declared nothing.
    let unresolved (peerId: string) : PeerVisibilityResolution =
        resolve PeerVisibilityBinding.default' peerId