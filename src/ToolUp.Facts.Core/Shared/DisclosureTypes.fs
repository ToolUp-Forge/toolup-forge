// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open ToolUp.Platform.VectorKnowledgeTypes

// ─── Disclosure egress predicate (Phase 525) ─────────────────────────
//
// THE disclosure predicate — `fact × egress-surface → allow/deny` — in one
// place, shared by every egress choke point (retrieval, tool results,
// narrative publication) and never re-implemented per surface. The choke
// points consume it through the fact-companion-free `IFactDisclosureGate`
// seam (`ToolUp.Platform.VectorKnowledgeTypes`); the gate implementation
// in the server companion is the only caller that also holds the typed
// `Fact`, so classification and enforcement cannot drift apart.
//
// `Internal` facts stay fully first-class *inputs* to server-side
// computation and internal audit — this predicate restricts the answer
// composer's egress doors, never computation (plan D14).

/// Resolves a `Restricted` policy ref at an egress surface.
///
///  - `Some true`  — the named policy permits disclosure at this surface.
///  - `Some false` — the named policy forbids it.
///  - `None`       — the policy is unknown to this deployment ⇒ **deny**
///    (conservative resolution; an unrecognised policy ref must never
///    fail open).
///
/// A typed policy vocabulary is a later metric-pack concern (plan open
/// question 7) — until one lands, deployments run the conservative
/// default below and every `Restricted` fact is denied at every door.
type DisclosurePolicyResolver = string -> FactEgressSurface -> bool option

module DisclosurePolicyResolver =
    /// The conservative default: no policy vocabulary is registered, so
    /// every `Restricted` policy ref resolves as unknown ⇒ deny.
    let denyUnknown: DisclosurePolicyResolver = fun _ _ -> None

module DisclosureEgress =

    /// The one disclosure predicate (Phase 525.A). Pure — evaluation over
    /// the classification alone; the calling gate supplies scope
    /// resolution and audit.
    ///
    ///  - `Surfaceable` ⇒ disclosable at every egress surface (composition
    ///    policy above this layer may still narrow — never widen).
    ///  - `Internal` ⇒ never disclosable; the verdict names the
    ///    classification (`"Internal"`) as its policy ref.
    ///  - `Restricted policyRef` ⇒ disclosable only when `resolvePolicy`
    ///    affirmatively permits it at this surface; unknown ⇒ deny.
    let evaluate
        (resolvePolicy: DisclosurePolicyResolver)
        (surface: FactEgressSurface)
        (disclosure: Disclosure)
        : FactDisclosureVerdict =
        match disclosure with
        | Surfaceable -> FactDisclosable
        | Internal -> FactNotDisclosable "Internal"
        | Restricted policyRef ->
            match resolvePolicy policyRef surface with
            | Some true -> FactDisclosable
            | Some false
            | None -> FactNotDisclosable policyRef

    /// The predicate over a full fact — sugar for gate implementations.
    let evaluateFact (resolvePolicy: DisclosurePolicyResolver) (surface: FactEgressSurface) (fact: Fact) =
        evaluate resolvePolicy surface fact.Disclosure

/// Reserved event-type discriminator for the disclosure-deny audit trail.
/// Rides the fact store's `_facts` source module (`FactEvents.SourceModule`)
/// so `IEventStore.ReadBySource scope "_facts"` returns asserts,
/// supersessions, and denies as one queryable record (GP 6).
module DisclosureEvents =
    /// A disclosure check denied a fact at an egress surface.
    [<Literal>]
    let DeniedType = "FactDisclosureDenied"

/// Payload of a `FactDisclosureDenied` audit event (JSON-serialised into
/// `ModuleEvent.Payload`). PII-free: identifiers + classification only —
/// the denied *value* never rides the audit row.
type FactDisclosureDeniedEvent = {
    /// Canonical surface name (`FactEgressSurface.toString`).
    Surface: string
    FactId: string
    /// Registered metric id of the denied fact; empty when the id was
    /// unresolvable in scope.
    Metric: string
    /// Why: `"Internal"`, the `Restricted` policy ref, or `"unknown-fact"`.
    PolicyRef: string
    /// The principal whose egress was refused.
    Principal: string
}