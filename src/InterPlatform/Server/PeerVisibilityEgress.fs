// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 642 — level-gated egress rides the disclosure plane ───────
//
// A declared authority level says what a peer MAY see. It does not say
// what a particular answer CARRIES, and those are different questions: a
// deployment can grant `AggregatesOnly` honestly and still compose an
// aggregate over facts its own disclosure policy classifies `Internal`.
// The level is the agreement; the disclosure plane is the door.
//
// So a level-gated answer takes the SHIPPED door rather than a new one.
// The Phase 525 predicate — `fact × egress-surface → allow/deny` — is
// consumed through the `IFactDisclosureGate` seam at the new
// `FactPeerEgress` surface, and the Phase 592 purpose facet rides with
// it: the route claims a declared purpose before it asks, so a
// deployment with a taxonomy judges the crossing under a stated why and
// records it. **No new ledger.** Every deny this route produces is
// audited by the gate itself, in the same `_facts` trail the retrieval,
// tool-result, publication, export and webhook doors already write to,
// and is queryable data-side by the deployment that owns the data. The
// federation seam adds a door; it does not add a bookkeeper.
//
// **Suppression-on-deny, the webhook posture.** A denied reference is
// ABSENT from what crosses — never annotated, never marker-substituted.
// A marker naming the policy is the right answer at a door inside the
// trust boundary, where it tells an operator the control worked; across
// a federation edge it would tell a counterparty that a fact it may not
// see EXISTS, which is itself the disclosure.
//
// ── Why the purpose is a function and not a value (GP 1) ─────────────
//
// The gate reads its claimed purpose from an ambient carrier — a
// deliberate Phase 592 decision, so the `IFactDisclosureGate` signature
// stays unchanged and every existing choke point is purpose-checked
// without edits. That carrier lives in the fact companion, which this
// substrate must not reference: the peer substrate is a companion at the
// SDK boundary and a companion never reaches into another. So the route
// carries the claim as a composition-supplied function
// (`FactPurposeContext.claim`), which keeps the dependency where it
// belongs and makes the pairing structural — `PeerEgressPurpose` holds
// the id and the claimer together, so "declared a purpose with no way to
// state it" is unrepresentable rather than merely discouraged.
//
// ── Cost when unused (GP 11 / GP 13) ─────────────────────────────────
//
// A binding with no route (`None`) routes nothing, consults nothing and
// allocates nothing; a route asked about an empty reference set returns
// before it reaches the gate. A deployment with no fact substrate has no
// gate to wire and is byte-for-byte a pre-642 deployment.

/// Phase 592 — the declared purpose a federated egress is bound to, and
/// the means of stating it.
///
/// One record rather than two fields so the pairing cannot come apart. A
/// purpose id with no claimer would be a `why` nobody records; a claimer
/// with no id would be a claim of nothing.
type PeerEgressPurpose = {
    /// The purpose id from this deployment's declared taxonomy. Judged
    /// against the surface's allowed set by the gate, exactly as any
    /// other door's claim is.
    PurposeId: string
    /// Installs the ambient claim the Phase 525 gate reads
    /// (`FactPurposeContext.claim` in the fact companion). Supplied by
    /// the composition so this substrate keeps no dependency on the fact
    /// tier.
    Claim: string -> unit
}

/// The disclosure-plane route one peer binding's level-gated answers
/// take before they cross the seam.
type PeerEgressRoute = {
    /// The shipped Phase 525 gate. The route never re-implements the
    /// predicate — the whole point of the seam is that there is one.
    Gate: IFactDisclosureGate
    Purpose: PeerEgressPurpose
    /// The fact references an answer to `operation` carries, in this
    /// binding's scope.
    ///
    /// **Supplied rather than inferred, and that is honest rather than
    /// lazy.** No shape in the model-execution profile carries a fact id,
    /// so the substrate cannot derive the reference set: a deployment
    /// whose governed answers are grounded in classified facts states
    /// which ones, and a deployment whose answers are not returns `[]`
    /// and pays for nothing. Inventing a derivation would produce a
    /// route that looked enforced and checked nothing.
    References: string -> string list
    /// Phase 643 — the fact references a RENDERED VIEW carries, given the
    /// view id and the series the artifact actually covers.
    ///
    /// **A second function rather than a reuse of `References`, because
    /// the operation name cannot distinguish two renders.** Every
    /// `RenderView` call names the same operation, and the whole point of
    /// the audit the `ViewOnly` level buys is that a data owner can ask
    /// which peer viewed WHICH SERIES when. Keying the reference set on
    /// the operation alone would produce one indistinguishable record per
    /// render, which is the shape of an audit trail that answers nothing.
    ///
    /// Supplied rather than inferred for the same reason `References` is:
    /// no shape in the view contract carries a fact id, so a deployment
    /// whose series are classified facts says which, and one whose series
    /// are not returns `[]` and pays for nothing.
    ViewReferences: string -> string list -> string list
}

/// What one crossing decided — recorded as data so an operator surface,
/// a test and a refusal message all read the same value rather than
/// three reconstructions of it.
type PeerEgressDecision = {
    /// The operation whose answer was routed.
    Operation: string
    /// The canonical egress-surface name (`FactEgressSurface.toString`)
    /// the crossing was judged at — `"PeerEgress"`.
    Surface: string
    /// The purpose claimed. Empty when no route is composed.
    Purpose: string
    /// References the gate affirmatively permitted, input order and
    /// multiplicity preserved.
    Permitted: string list
    /// References suppressed. A non-empty list means the answer must not
    /// cross as it stands.
    Withheld: string list
}

[<RequireQualifiedAccess>]
module PeerEgressDecision =

    /// The decision of a binding with no route: nothing referenced,
    /// nothing withheld, no purpose claimed. What a pre-642 deployment
    /// (and a deployment with no fact substrate) produces for every
    /// answer.
    let unrouted (operation: string) : PeerEgressDecision = {
        Operation = operation
        Surface = FactEgressSurface.toString FactPeerEgress
        Purpose = ""
        Permitted = []
        Withheld = []
    }

    /// Did everything the answer references clear the door?
    let cleared (decision: PeerEgressDecision) : bool = List.isEmpty decision.Withheld

[<RequireQualifiedAccess>]
module PeerVisibilityEgress =

    /// Route one level-gated answer's references through the shipped
    /// disclosure gate at the `FactPeerEgress` surface, under the route's
    /// declared purpose.
    ///
    /// The purpose is claimed BEFORE the gate is asked, on the same async
    /// chain, because that is how the ambient carrier is read (GP 7). An
    /// empty reference set short-circuits without claiming or consulting
    /// anything: a crossing that discloses no classified fact has nothing
    /// for a purpose to justify.
    /// The shared tail of every crossing: claim the purpose, ask the
    /// gate, and split the references by what it affirmatively permitted.
    ///
    /// Factored out when Phase 643 added the view-keyed route rather than
    /// copied, because "which verdicts count as permission" is the one
    /// decision in this file that must not be able to differ between two
    /// doors.
    let private judge
        (r: PeerEgressRoute)
        (scopeId: string)
        (principal: string)
        (operation: string)
        (references: string list)
        : Async<PeerEgressDecision> =
        async {
            match references with
            | [] ->
                return {
                    PeerEgressDecision.unrouted operation with
                        Purpose = r.Purpose.PurposeId
                }
            | references ->
                r.Purpose.Claim r.Purpose.PurposeId

                let! verdicts = r.Gate.Check(scopeId, principal, FactPeerEgress, references)

                let permitted =
                    references
                    |> List.filter (fun id ->
                        match verdicts.TryFind id with
                        | Some FactDisclosable -> true
                        // Fail-closed on all three of denied, unknown
                        // and unresolvable-in-scope. A reference the
                        // gate did not affirmatively permit is one
                        // nothing said may cross.
                        | Some(FactNotDisclosable _)
                        | None -> false)

                return {
                    Operation = operation
                    Surface = FactEgressSurface.toString FactPeerEgress
                    Purpose = r.Purpose.PurposeId
                    Permitted = permitted
                    Withheld = references |> List.filter (fun id -> not (List.contains id permitted))
                }
        }

    let route
        (routeOf: PeerEgressRoute option)
        (scopeId: string)
        (principal: string)
        (operation: string)
        : Async<PeerEgressDecision> =
        async {
            match routeOf with
            | None -> return PeerEgressDecision.unrouted operation
            | Some r -> return! judge r scopeId principal operation (r.References operation)
        }

    /// Phase 643 — the same door for a rendered view, with the reference
    /// set keyed by the view and the series the artifact covers.
    ///
    /// A render takes THIS route and not `route`, and takes it exactly
    /// once: one crossing, one gate check, one audit row. The operation
    /// recorded is still the operation, so a data owner reading the
    /// `_facts` trail sees a `RenderView` crossing whose references name
    /// the series — which is the query the `ViewOnly` level was sold on.
    let routeView
        (routeOf: PeerEgressRoute option)
        (scopeId: string)
        (principal: string)
        (operation: string)
        (viewId: string)
        (series: string list)
        : Async<PeerEgressDecision> =
        async {
            match routeOf with
            | None -> return PeerEgressDecision.unrouted operation
            | Some r -> return! judge r scopeId principal operation (r.ViewReferences viewId series)
        }