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

    /// A declared declassification routine cleared inherited taint on a
    /// fact that then disclosed (Phase 562.C — every declassification an
    /// auditable event, GP 6). Rides the same `_facts` source module as
    /// the deny trail, so the whole disclosure story is one queryable
    /// record.
    [<Literal>]
    let DeclassifiedType = "FactDisclosureDeclassified"

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

// ─── Taint-propagating disclosure vocabulary (Phase 562) ─────────────
//
// The licensed-data story: a `Restricted` policy gains an opt-in
// **`TaintPropagating`** mode. Anything *derived* from a restricted input
// inherits the restriction — unless the derivation path crosses a declared
// **declassification routine** (a catalog entry carrying `Declassifies:
// true` + rationale, every crossing an auditable event). Information-flow
// control implemented as *data* on the existing derivation walk, not a
// type system — a routine is data the gate reads, never code the gate
// special-cases. The default stays permissive (deriving disclosable
// insight from confidential inputs is the point): no policy declared ⇒ the
// conservative Phase 525 resolver, no declassifier ⇒ no path ever clears
// taint, and — crucially — an empty config is byte-for-byte the Phase 525
// gate (GP 11 / GP 13; plan D17).
//
// Honesty boundary (restated from the plan): this is NOT statistical
// disclosure control / inference-control. It propagates a *declared*
// classification along *recorded* derivation edges; it makes no claim
// about what a determined reader could infer from an aggregate.

/// The disclosure mode of a `Restricted` policy (Phase 562.A). `Plain` is
/// the Phase 525 behaviour — the policy governs only the facts directly
/// classified under it. `TaintPropagating` additionally taints everything
/// derived from such a fact (see `DisclosureTaint`), until a declared
/// declassification routine clears it.
type DisclosureMode =
    | Plain
    | TaintPropagating

/// A registered disclosure policy — the typed vocabulary a deployment
/// declares at compose time so `Restricted(policyRef)` refs resolve to a
/// real per-surface allow/deny stance (Phase 562.A). This is the first
/// concrete source the Phase 525 `DisclosurePolicyResolver` seam has had;
/// an undeclared ref still resolves as unknown ⇒ deny (conservative
/// default unchanged).
type DisclosurePolicy = {
    /// The ref carried by `Restricted(policyRef)`; the registry key.
    PolicyRef: string
    Mode: DisclosureMode
    /// The egress surfaces at which the policy permits *direct* disclosure
    /// of a fact classified under it. A surface absent from the list is
    /// denied; an empty list is a fully-sealed policy (denied everywhere).
    /// Taint of *derived* facts is governed separately by `DisclosureTaint`
    /// — this stance is only about a directly-classified fact's own egress.
    PermitSurfaces: FactEgressSurface list
}

/// Catalog metadata declaring a deterministic operation a **declassification
/// routine** (Phase 562.B): its output is disclosable regardless of tainted
/// inputs, because the operation is an approved information-losing transform
/// (aggregation-over-k, an approved index formula). A routine is *data* —
/// the `OperationId` matches a `Computed(operationId, _, _)` method; the
/// gate reads this catalog and never special-cases an operation in code.
type DeclassificationRoutine = {
    /// The `Computed` operation id whose output declassifies its inputs.
    OperationId: string
    /// Why this operation is a safe declassifier — recorded verbatim on
    /// every crossing (GP 6), so the audit trail explains the exemption.
    Rationale: string
}

/// The taint configuration a deployment registers at compose time (Phase
/// 562.A/B): the policy vocabulary keyed by policy ref + the
/// declassification catalog keyed by operation id. Inert when empty
/// (`DisclosureTaintConfig.empty`): the gate then behaves byte-for-byte as
/// the Phase 525 gate (GP 11).
type DisclosureTaintConfig = {
    Policies: Map<string, DisclosurePolicy>
    Declassifiers: Map<string, DeclassificationRoutine>
}

module DisclosureTaintConfig =

    /// The inert config — no policy vocabulary, no declassifiers. A gate
    /// built with this is indistinguishable from the Phase 525 gate.
    let empty: DisclosureTaintConfig = {
        Policies = Map.empty
        Declassifiers = Map.empty
    }

    /// Build a config from a list of policies + declassification routines
    /// (the compose-time registration shape). Later entries win on a
    /// duplicate key.
    let ofLists
        (policies: DisclosurePolicy list)
        (declassifiers: DeclassificationRoutine list)
        : DisclosureTaintConfig =
        {
            Policies = policies |> List.map (fun p -> p.PolicyRef, p) |> Map.ofList
            Declassifiers = declassifiers |> List.map (fun r -> r.OperationId, r) |> Map.ofList
        }

    /// Nothing registered ⇒ the gate skips the taint walk entirely and is
    /// byte-for-byte the Phase 525 gate (GP 13).
    let isEmpty (config: DisclosureTaintConfig) : bool =
        config.Policies.IsEmpty && config.Declassifiers.IsEmpty

    /// The Phase 525 `DisclosurePolicyResolver` built from the registered
    /// policies (Phase 562.A) — the policy vocabulary the resolver seam has
    /// lacked. A registered ref permits a surface iff it is in the policy's
    /// `PermitSurfaces`; an *unregistered* ref stays unknown ⇒ deny, so the
    /// conservative default is preserved for undeclared refs.
    let resolver (config: DisclosureTaintConfig) : DisclosurePolicyResolver =
        fun policyRef surface ->
            config.Policies.TryFind policyRef
            |> Option.map (fun policy -> List.contains surface policy.PermitSurfaces)

    /// Whether a policy ref is registered as `TaintPropagating` (an
    /// unregistered or `Plain` ref is not — taint only flows from a
    /// declared taint-propagating source).
    let isTaintPropagating (config: DisclosureTaintConfig) (policyRef: string) : bool =
        match config.Policies.TryFind policyRef with
        | Some policy -> policy.Mode = TaintPropagating
        | None -> false

    /// The declassification routine registered for a `Computed` operation
    /// id, when any.
    let declassifierFor (config: DisclosureTaintConfig) (operationId: string) : DeclassificationRoutine option =
        config.Declassifiers.TryFind operationId

/// Payload of a `FactDisclosureDeclassified` audit event (Phase 562.C —
/// JSON-serialised into `ModuleEvent.Payload`). PII-free: the fact being
/// disclosed, the declassifier fact on its derivation path, the operation
/// id + catalog rationale, the surface, and the principal — never any
/// fact *value*.
type FactDisclosureDeclassifiedEvent = {
    /// Canonical surface name (`FactEgressSurface.toString`).
    Surface: string
    /// The disclosed fact whose derivation crossed the declassifier.
    FactId: string
    /// The declassifier fact on the path (a `Computed` fact whose operation
    /// is a declared routine) that cleared otherwise-inherited taint.
    DeclassifierFactId: string
    /// The declassifying operation id.
    OperationId: string
    /// The rationale recorded from the catalog (why the exemption holds).
    Rationale: string
    /// The principal whose egress the declassification permitted.
    Principal: string
}