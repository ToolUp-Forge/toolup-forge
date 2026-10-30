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

// ─── Viewer-aware resolution (Phase 896) ─────────────────────────────
//
// `DisclosurePolicyResolver` receives no principal and no role, so a
// `Restricted` fact is visible to everyone or to no one at a surface. The
// viewer-aware resolver below sits BESIDE it — the existing type and every
// composition supplying it are untouched — and additionally sees who is
// looking. The gate, never the caller, builds the `DisclosureViewer`: from
// the platform-resolved access context of the request, or, for a carried
// scope with no request (a job, a webhook), as the least-privileged viewer.

/// Who a disclosure decision is made for (Phase 896). Built by the
/// disclosure gate from the request's resolved access context and the
/// team's output-visibility level; a caller can neither supply nor widen it.
type DisclosureViewer = {
    /// The viewer's role in the team whose scope is being read. `None` when
    /// the viewer is not a member, when no team store is composed to say, or
    /// when there is no resolved viewer at all.
    TeamRole: ToolUp.Platform.TeamRole option
    /// Whether the viewer holds `PlatformRole.PlatformAdmin`.
    IsPlatformAdmin: bool
    /// The output-visibility level in force for the team whose scope is
    /// being read; `TeamVisible` outside a team scope.
    OutputVisibility: ToolUp.Platform.TeamVisibilityLevel
}

/// Constructors for `DisclosureViewer`.
module DisclosureViewer =
    /// The viewer a check evaluates as when no viewer was resolved — a job
    /// or a webhook running over a carried scope, or a door whose output
    /// reaches an audience wider than the requester. No team role, not a
    /// platform admin: it sees exactly what `TeamVisible` output admits.
    let leastPrivileged (outputVisibility: ToolUp.Platform.TeamVisibilityLevel) : DisclosureViewer = {
        TeamRole = None
        IsPlatformAdmin = false
        OutputVisibility = outputVisibility
    }

/// Resolves a `Restricted` policy ref at an egress surface FOR A VIEWER
/// (Phase 896). Same answers as `DisclosurePolicyResolver` — `Some true`
/// permits, `Some false` forbids, `None` is an unknown policy and denies —
/// with the viewer as a further input.
type ViewerAwareDisclosurePolicyResolver = string -> FactEgressSurface -> DisclosureViewer -> bool option

/// Constructors for `ViewerAwareDisclosurePolicyResolver`.
module ViewerAwareDisclosurePolicyResolver =
    /// Lift a viewer-blind resolver: the viewer is ignored, so a gate given
    /// this decides exactly as it did with the resolver alone.
    let ofResolver (resolve: DisclosurePolicyResolver) : ViewerAwareDisclosurePolicyResolver =
        fun policyRef surface _ -> resolve policyRef surface

    /// The shipped viewer-aware rule: the deployment's policy must permit
    /// the surface (an unknown policy still denies), AND the team's output
    /// visibility must admit the viewer (`TeamOutputVisibility.canSee`).
    /// Module permission is deliberately not consulted: permission governs
    /// USE of a module, the team's policy governs who sees its output.
    let withTeamOutputVisibility (resolve: DisclosurePolicyResolver) : ViewerAwareDisclosurePolicyResolver =
        fun policyRef surface viewer ->
            resolve policyRef surface
            |> Option.map (fun permitted ->
                permitted
                && ToolUp.Platform.TeamOutputVisibility.canSee
                    viewer.OutputVisibility
                    viewer.TeamRole
                    viewer.IsPlatformAdmin)

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

    /// The predicate for a viewer (Phase 896) — `evaluate` with a
    /// viewer-aware resolver. `Surfaceable` and `Internal` are decided
    /// exactly as `evaluate` decides them; only a `Restricted` fact consults
    /// the viewer, and a denial names its policy ref, never the value.
    let evaluateForViewer
        (resolvePolicy: ViewerAwareDisclosurePolicyResolver)
        (surface: FactEgressSurface)
        (viewer: DisclosureViewer)
        (disclosure: Disclosure)
        : FactDisclosureVerdict =
        match disclosure with
        | Surfaceable -> FactDisclosable
        | Internal -> FactNotDisclosable "Internal"
        | Restricted policyRef ->
            match resolvePolicy policyRef surface viewer with
            | Some true -> FactDisclosable
            | Some false
            | None -> FactNotDisclosable policyRef

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

    /// A fact carrying at least one contributing party's restricted data
    /// disclosed at an egress surface (Phase 674.C — the per-party
    /// **access** facet, plan D4). Emitted ONLY when the disclosed fact has
    /// a contributing contributor scope, so a deployment that declares no
    /// party never sees this row and stays byte-for-byte pre-674 (GP 11 /
    /// GP 13). Rides the same `_facts` source module, so contribution,
    /// access, refusal and declassification are one queryable record.
    [<Literal>]
    let AccessedType = "FactDisclosureAccessed"

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
    /// Why: `"Internal"`, the `Restricted` policy ref, `"unknown-fact"`
    /// for an id the scope cannot see, or a Phase 592 `purpose-*` ref for
    /// a claim outside the surface's allowed purpose set.
    PolicyRef: string
    /// The principal whose egress was refused.
    Principal: string
    /// Phase 592 — the purpose the request claimed; empty when no
    /// taxonomy is declared (facet absent) or the request claimed none.
    Purpose: string
    /// Phase 592 — the taxonomy version the claim was judged against;
    /// empty when the facet is absent.
    TaxonomyVersion: string
    /// Phase 674 — every contributing party whose restricted data reaches
    /// this fact's lineage (the **contribution** facet, plan D4). Empty in
    /// a deployment that declares no contributor scope, so a per-party
    /// projection is derivable from this one stream without a second
    /// (GP 6).
    ContributorScopes: string list
    /// Phase 674 — the contributing parties whose policy this derivation
    /// path did NOT satisfy: the parties that caused the refusal. Names the
    /// party, never the counterparty's data.
    UnsatisfiedScopes: string list
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
    /// Phase 674 — the **contributing party** this policy belongs to, as a
    /// value-typed party id (plan D4). Deliberately NOT an auth principal:
    /// a contributor scope is a declared data-provenance dimension of the
    /// composition, not an identity the request carries — it is resolved
    /// from the fact's own registered policy on the server side and can
    /// never be asserted by a caller.
    ///
    /// `None` (the shipped shape) ⇒ the policy makes no per-party claim and
    /// behaves byte-for-byte as Phase 562: its taint is cleared by any
    /// declared declassification routine. `Some party` opts the policy into
    /// the Phase 674 conjunction, at which point only a routine that names
    /// `party` in its `AcceptingScopes` can clear its taint — one party's
    /// consent can never launder another's data (plan D3).
    ContributorScope: string option
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
    /// Phase 674 — the contributor scopes that **accept** this routine as a
    /// declassifier of their own restricted data (plan D3). Fail-closed by
    /// construction: a party-scoped policy's taint is cleared only when this
    /// list names that party, so the empty list (the shipped shape) accepts
    /// *no* party and can never launder a scoped contribution. An
    /// **unscoped** policy is unaffected — it is cleared by any declared
    /// routine, exactly as Phase 562 (GP 11), so a deployment that declares
    /// no contributor scope never pays for the conjunction (GP 13).
    ///
    /// A scope named here that no registered policy declares is inert: it
    /// clears nothing. Acceptance is a claim about a party that exists.
    AcceptingScopes: string list
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

    /// Phase 1134 — whether the taint walk can change nothing under this
    /// config: no registered policy is `TaintPropagating`, none declares a
    /// `ContributorScope`, and no declassification routine is registered.
    /// The walk then arrives exactly where an empty walk starts (no inherited
    /// taint, no crossing, no contributor beyond the judged fact's own), so a
    /// door may decide without building the derivation graph. The policies
    /// still matter for their per-surface stance (`resolver`), which reads no
    /// graph. An empty config is taint-inert; the converse does not hold.
    ///
    /// The one definition every door reads (the disclosure gate today), so
    /// "this deployment cannot taint" is never decided twice.
    let isTaintInert (config: DisclosureTaintConfig) : bool =
        config.Declassifiers.IsEmpty
        && config.Policies
           |> Map.forall (fun _ policy -> policy.Mode = Plain && Option.isNone policy.ContributorScope)

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

    // ── Phase 674 — contributor scope + policy conjunction ────────
    //
    // Contributor scope is resolved from the REGISTERED POLICY, never from
    // anything the caller supplies: `scopeOf` is a lookup in the composed
    // vocabulary keyed by the ref the fact itself carries. There is no
    // request-borne party claim anywhere in this file, and that is the
    // point — a party id a caller could assert would be an authorisation
    // token, and this is a provenance dimension.

    /// The contributing party a registered policy belongs to (Phase 674).
    /// `None` for an unscoped policy AND for an unregistered ref — an
    /// unregistered ref is not a taint source at all (see
    /// `isTaintPropagating`), so it never reaches the conjunction.
    let scopeOf (config: DisclosureTaintConfig) (policyRef: string) : string option =
        config.Policies.TryFind policyRef |> Option.bind _.ContributorScope

    /// Every contributor scope the composed policy vocabulary declares,
    /// distinct and ordered. Empty ⇒ a single-party (or party-agnostic)
    /// deployment: the conjunction machinery is inert (GP 13).
    let contributorScopes (config: DisclosureTaintConfig) : string list =
        config.Policies
        |> Map.toList
        |> List.choose (fun (_, policy) -> policy.ContributorScope)
        |> List.distinct
        |> List.sort

    /// Whether a declassification routine may clear the taint of the named
    /// policy — the conjunction's single decision point (Phase 674.B).
    ///
    /// **Fail-closed.** An unscoped policy is cleared by any declared
    /// routine (Phase 562, unchanged — the policy makes no party claim). A
    /// party-scoped policy is cleared ONLY by a routine whose
    /// `AcceptingScopes` names that party; absent, unknown or unevaluable
    /// acceptance never clears, so an unsatisfied party's restriction
    /// survives every other party's declassification.
    let routineClears (config: DisclosureTaintConfig) (routine: DeclassificationRoutine) (policyRef: string) : bool =
        match scopeOf config policyRef with
        | None -> true
        | Some party -> List.contains party routine.AcceptingScopes


    /// Phase 794 — the closed label alphabet a deployment declares: its
    /// registered `TaintPropagating` policy refs, distinct and sorted. A
    /// `Plain` policy never taints and an unregistered ref is not a taint
    /// source at all, so neither is a label. Closed because the vocabulary
    /// is compose-time data, which is what makes the lattice laws checkable
    /// EXHAUSTIVELY over a deployment rather than only sampled.
    let taintVocabulary (config: DisclosureTaintConfig) : string list =
        config.Policies
        |> Map.toList
        |> List.choose (fun (policyRef, policy) ->
            if policy.Mode = TaintPropagating then
                Some policyRef
            else
                None)
        |> List.distinct
        |> List.sort

// ─── Phase 794 — the taint label lattice ─────────────────────────────
//
// Taint has been a `string list` of policy refs since Phase 562, combined
// wherever two lineages met by `List.collect … |> List.distinct`: a join in
// fact, with no name, no stated law, and no test that it is one. This
// phase gives it a type. A `TaintLabel` is the SET of taint-propagating
// policy refs a value's lineage carries, ordered by inclusion — `bottom`
// (nothing reaches the value) at the foot, the whole declared vocabulary
// at the head.
//
// `join` is the least upper bound: what a value carries when two lineages
// meet. It is associative, commutative, idempotent, and has `bottom` as a
// two-sided identity. `narrowsTo` is the ONE operator in the model that
// moves DOWN the lattice, and it moves only as far as a declared
// declassification routine's accepting scopes entitle it to
// (`routineClears` above, unchanged by this phase). Nothing else can lower
// a label — which is the property every downstream claim rests on, and the
// reason declassification is a single named operator rather than a
// scattering of filters.
//
// **The laws are executable, and parameterised over the operator under
// test.** `joinLawFailuresOf` / `narrowingLawFailuresOf` return a readable
// sentence per violation rather than asserting, so each is consumed twice:
// bound to the real operator and asserted empty, and bound to a
// deliberately-broken one to assert the specific law that must catch it
// fires. A law that stopped having teeth fails its go-red control instead
// of quietly passing everywhere. And because the vocabulary is closed,
// `vocabulary` enumerates every label a deployment can express, so the
// laws are checked over all of them and not only over samples.

/// The taint a value's lineage carries: the set of taint-propagating
/// policy refs that reach it. Built only through the `TaintLabel` module —
/// the `string list` representation is retired behind the type, so no
/// caller can combine two labels by concatenating them and no caller can
/// lower one except through `narrowsTo`.
type TaintLabel = private TaintRefs of Set<string>

module TaintLabel =

    /// The clean label: no policy reaches the value. The join's identity
    /// and the bottom of the order.
    let bottom: TaintLabel = TaintRefs Set.empty

    /// The label carrying exactly one policy ref.
    let ofPolicyRef (policyRef: string) : TaintLabel = TaintRefs(Set.singleton policyRef)

    /// The label carrying every ref given. Order and duplicates are
    /// immaterial — which is the whole point of the type.
    let ofPolicyRefs (policyRefs: string seq) : TaintLabel = TaintRefs(Set.ofSeq policyRefs)

    /// The refs a label carries, distinct and sorted.
    let policyRefs (TaintRefs refs) : string list = Set.toList refs

    /// Nothing reaches the value.
    let isBottom (TaintRefs refs) : bool = Set.isEmpty refs

    /// Whether the named policy reaches the value.
    let contains (policyRef: string) (TaintRefs refs) : bool = Set.contains policyRef refs

    /// The least upper bound — what a value carries when two lineages meet.
    let join (TaintRefs a) (TaintRefs b) : TaintLabel = TaintRefs(Set.union a b)

    /// The join of many. `bottom` over an empty sequence, which is the
    /// identity law doing its work rather than a special case.
    let joinAll (labels: TaintLabel seq) : TaintLabel = Seq.fold join bottom labels

    /// The lattice order: `a` is at or below `b` when joining it into `b`
    /// adds nothing.
    let below (a: TaintLabel) (b: TaintLabel) : bool = join a b = b

    /// Declassification — the ONE operator that LOWERS a label. `clears` is
    /// the routine's entitlement predicate
    /// (`DisclosureTaintConfig.routineClears config routine`): an unscoped
    /// policy is cleared by any declared routine, a party-scoped one only
    /// by a routine that party accepted. So one party's consent can never
    /// lower another party's label, and no other code path can lower one at
    /// all.
    let narrowsTo (clears: string -> bool) (TaintRefs refs) : TaintLabel =
        TaintRefs(refs |> Set.filter (clears >> not))

    /// The refs `narrowsTo` would drop — what a crossing actually cleared.
    /// Empty ⇒ the routine cleared nothing, so the crossing declassified
    /// nothing and is not recorded as one.
    let clearedBy (clears: string -> bool) (TaintRefs refs) : string list = refs |> Set.filter clears |> Set.toList

    /// Phase 796 — the label as the RUNTIME tier's egress decision sees
    /// it. The projection across the tier boundary, written on this side
    /// because this is the side that can see both types: `ToolUp.Facts`
    /// depends on `ToolUp.Platform`, never the reverse, so the egress
    /// seam cannot name `TaintLabel` and names its mirror instead (the
    /// cross-pillar rule's second shape — a data mirror plus a drift
    /// check, not a type reference).
    ///
    /// The result is always LABELLED, including for `bottom`: a computed
    /// lineage that carries nothing is `EgressLabel.clean`, which is a
    /// different value from `EgressLabel.unlabelled` and is exactly the
    /// distinction the verified profile refuses on. A surface that has a
    /// `TaintLabel` at all has computed one; only a surface with NO
    /// label is unlabelled, and it says so by not calling this.
    let toEgressLabel (label: TaintLabel) : ToolUp.Platform.EgressLabel =
        ToolUp.Platform.EgressLabel.ofPolicyRefs (policyRefs label)

    /// A readable rendering for law-failure messages.
    let render (label: TaintLabel) : string =
        if isBottom label then
            "(clean)"
        else
            policyRefs label |> String.concat "+"

    /// Every label expressible over a closed policy vocabulary — its power
    /// set, `2^n` labels. This is what makes the laws checkable
    /// exhaustively; keep the vocabulary small when calling it, because the
    /// associativity law is cubic in the result.
    let vocabulary (policyRefs: string list) : TaintLabel list =
        policyRefs
        |> List.distinct
        |> List.sort
        |> List.fold
            (fun acc policyRef ->
                acc
                @ (acc |> List.map (fun (TaintRefs refs) -> TaintRefs(Set.add policyRef refs))))
            [ bottom ]

    /// Every way the join under test fails a lattice law over `labels`, as
    /// readable sentences. A broken law fails at many points at once, so
    /// each failure names the labels that witness it. Parameterised over
    /// the operator so a test can perturb it and prove the check has teeth.
    let joinLawFailuresOf (j: TaintLabel -> TaintLabel -> TaintLabel) (labels: TaintLabel list) : string list =
        let coveredBy a b = j a b = b

        [
            for a in labels do
                if j a a <> a then
                    yield sprintf "join is not idempotent at %s" (render a)

                if j bottom a <> a then
                    yield sprintf "bottom is not a LEFT identity at %s" (render a)

                if j a bottom <> a then
                    yield sprintf "bottom is not a RIGHT identity at %s" (render a)

            for a in labels do
                for b in labels do
                    if j a b <> j b a then
                        yield sprintf "join is not commutative at %s / %s" (render a) (render b)

                    let ab = j a b

                    if not (coveredBy a ab && coveredBy b ab) then
                        yield sprintf "the join does not cover its parts at %s / %s" (render a) (render b)

            for a in labels do
                for b in labels do
                    for c in labels do
                        if j (j a b) c <> j a (j b c) then
                            yield sprintf "join is not associative at %s / %s / %s" (render a) (render b) (render c)
        ]

    /// The join laws bound to the real `join`.
    let joinLawFailures (labels: TaintLabel list) : string list = joinLawFailuresOf join labels

    /// Every way the narrowing under test fails a declassification law,
    /// given the entitlement predicate it is handed. Same shape and same
    /// reason as `joinLawFailuresOf`.
    let narrowingLawFailuresOf
        (n: (string -> bool) -> TaintLabel -> TaintLabel)
        (clears: string -> bool)
        (labels: TaintLabel list)
        : string list =
        [
            for a in labels do
                let narrowed = n clears a

                if not (below narrowed a) then
                    yield
                        sprintf "narrowing did not LOWER the label at %s — it produced %s" (render a) (render narrowed)

                if n clears narrowed <> narrowed then
                    yield sprintf "narrowing is not idempotent at %s" (render a)

                if n (fun _ -> false) a <> a then
                    yield sprintf "a routine entitled to clear nothing still lowered the label at %s" (render a)

                if not (isBottom (n (fun _ -> true) a)) then
                    yield sprintf "a routine entitled to clear everything left taint behind at %s" (render a)

                for policyRef in policyRefs a do
                    if contains policyRef narrowed <> not (clears policyRef) then
                        yield
                            sprintf
                                "narrowing cleared the wrong refs at %s: the ref %s %s"
                                (render a)
                                policyRef
                                (if clears policyRef then
                                     "survived a routine entitled to clear it"
                                 else
                                     "was cleared by a routine not entitled to clear it")

            for a in labels do
                for b in labels do
                    if below a b && not (below (n clears a) (n clears b)) then
                        yield sprintf "narrowing is not monotone at %s / %s" (render a) (render b)
        ]

    /// The narrowing laws bound to the real `narrowsTo`.
    let narrowingLawFailures (clears: string -> bool) (labels: TaintLabel list) : string list =
        narrowingLawFailuresOf narrowsTo clears labels

/// Phase 794 — a finding raised alongside a disclosure verdict: something
/// the verdict's reader must be told that is not itself an allow or a deny.
/// A finding never changes a verdict — it qualifies the EVIDENCE the
/// verdict rests on — which is why it is a separate value and not a third
/// verdict case (`DisclosureEgress.evaluate` is unchanged).
[<RequireQualifiedAccess>]
type DisclosureFinding =
    /// The named facts' upstream was INFERRED from evidence linkage (the
    /// Phase 562 projection: a fact's `Evidence.InputHashes` matched against
    /// whichever fact emitted that data-object version as `Series`) rather
    /// than COMPUTED from the transform tree that produced them. That
    /// inference is sound only where a derivation is routed through a series
    /// output; a derivation that is not is invisible to it, and an invisible
    /// upstream carries no taint at all. So a verdict resting on inference
    /// is weaker than one resting on computation, and this is how every
    /// party is told which of the two it was given.
    | LineageInferred of factIds: string list

module DisclosureFinding =

    /// Stable, greppable ref for the inferred-lineage finding — the same
    /// role `DisclosureContributorScope.UnsatisfiedPrefix` plays for the
    /// conjunction refusal.
    [<Literal>]
    let LineageInferredRef = "lineage-inferred"

    let describe (finding: DisclosureFinding) : string =
        match finding with
        | DisclosureFinding.LineageInferred factIds ->
            sprintf
                "%s: the lineage of %d fact(s) was inferred from evidence linkage rather than computed from a transform tree (%s)"
                LineageInferredRef
                (List.length factIds)
                (String.concat ", " factIds)

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
    /// Phase 592 — the purpose the request claimed (grants stamp the
    /// declared why too); empty when the facet is absent.
    Purpose: string
    /// Phase 592 — the taxonomy version the claim was judged against;
    /// empty when the facet is absent.
    TaxonomyVersion: string
    /// Phase 674 — every contributing party whose restricted data reaches
    /// the disclosed fact's lineage (the contribution facet).
    ContributorScopes: string list
    /// Phase 674 — the contributing parties whose taint THIS crossing
    /// cleared, i.e. the parties that accepted this routine. Empty when the
    /// cleared policies declare no party (the Phase 562 shape), which is
    /// exactly the case in which no party accepted anything.
    AcceptedScopes: string list
}

/// Payload of a `FactDisclosureAccessed` audit event (Phase 674.C). The
/// per-party access facet: a fact carrying contributing parties' data left
/// through an egress surface. PII-free — ids, parties, surface, principal;
/// never a fact value. Emitted only when `ContributorScopes` is non-empty.
type FactDisclosureAccessedEvent = {
    /// Canonical surface name (`FactEgressSurface.toString`).
    Surface: string
    FactId: string
    /// Registered metric id of the disclosed fact.
    Metric: string
    /// The contributing parties whose restricted data reaches this fact.
    ContributorScopes: string list
    /// The principal the disclosure was made to.
    Principal: string
    /// Phase 592 — the purpose the request claimed; empty when absent.
    Purpose: string
    /// Phase 592 — the taxonomy version; empty when the facet is absent.
    TaxonomyVersion: string
}

/// Phase 674 — the typed refusal vocabulary for a multi-party conjunction
/// failure. The verdict's policy ref names the **unsatisfied party**, not
/// the counterparty's policy internals and never its data: the refused
/// caller learns which consent is missing and nothing else.
module DisclosureContributorScope =

    /// The `FactNotDisclosable` ref for a derivation that failed one or
    /// more contributing parties' policies. Stable and greppable; the
    /// parties are already sorted+distinct by the walk.
    [<Literal>]
    let UnsatisfiedPrefix = "multi-party-unsatisfied:"

    let unsatisfiedRef (scopes: string list) : string =
        UnsatisfiedPrefix + String.concat "," scopes

// ─── Purpose-bound disclosure — the declared-why facet (Phase 592) ───
//
// The disclosure model above gates *who* may receive *what*; mature
// governance regimes also demand the **declared why** — access justified
// by a stated purpose, with the purpose recorded. Purpose is a typed
// facet of the disclosure vocabulary: a composition declares its purpose
// taxonomy (a closed, versioned set of purpose ids + descriptions —
// plain declared data, not a policy engine), each egress surface carries
// the purposes it is *allowed* to serve, a request carries the purpose
// it *claims* (the ambient `FactPurposeContext` in the server companion),
// and the Phase 525 gate refuses a claim outside the surface's allowed
// set — an auditable refusal, like every other disclosure denial. Grants
// and denials both stamp the claimed purpose into the audit events above,
// so "who did what, why" is answerable from the trail. Because the
// taxonomy, the per-surface allowed sets, and the gate are all
// composition-declared data checked by readable code, purpose governance
// is part of the inspectable disclosure envelope — not runtime
// configuration a reviewer must trust blind. Opt-in (GP 13): no taxonomy
// declared ⇒ the facet is absent and behaviour is byte-for-byte today's
// (GP 11).

/// One declared purpose in the taxonomy: a stable id + a human-readable
/// description of what the purpose covers (for reviewers / admin UIs).
type DisclosurePurpose = {
    PurposeId: string
    Description: string
}

/// The closed, versioned purpose taxonomy a composition declares (Phase
/// 592.A). Plain declared data; the version is stamped into every
/// purpose-bearing audit event so the trail records *which* vocabulary a
/// claim was judged against.
type PurposeTaxonomy = {
    /// Taxonomy version, advanced by the composition when the id set
    /// changes — recorded verbatim in audit events.
    Version: string
    /// The closed purpose id set. A claim outside this set can never be
    /// in any surface's allowed set.
    Purposes: DisclosurePurpose list
}

/// The purpose configuration a deployment registers at compose time
/// (Phase 592.A): the taxonomy + the per-surface allowed sets — "this
/// disclosure route may serve these purposes". A surface absent from the
/// map serves NO purpose (default-deny-by-shape, the same posture as
/// `DisclosurePolicy.PermitSurfaces`).
type DisclosurePurposeConfig = {
    Taxonomy: PurposeTaxonomy
    /// Purpose ids each egress surface is allowed to serve.
    AllowedBySurface: Map<FactEgressSurface, string list>
}

module DisclosurePurposeConfig =

    /// The declared purpose ids (the closed vocabulary).
    let purposeIds (config: DisclosurePurposeConfig) : Set<string> =
        config.Taxonomy.Purposes |> List.map _.PurposeId |> Set.ofList

    /// The purposes a surface is allowed to serve; a surface absent from
    /// the map serves none (default-deny-by-shape).
    let allowedFor (config: DisclosurePurposeConfig) (surface: FactEgressSurface) : string list =
        config.AllowedBySurface.TryFind surface |> Option.defaultValue []

    /// Build a config from the taxonomy + per-surface allowed sets (the
    /// compose-time registration shape). Later entries win on a duplicate
    /// surface.
    let ofLists
        (taxonomy: PurposeTaxonomy)
        (allowed: (FactEgressSurface * string list) list)
        : DisclosurePurposeConfig =
        {
            Taxonomy = taxonomy
            AllowedBySurface = Map.ofList allowed
        }

// ─── A fact computed from named facts ────────────────────────────────
//
// A fact the assistant's pipeline computed (`PipelineAssertion`) names the
// facts it was computed from in its `Evidence.InputHashes`, one entry per
// input, each `fact:<id>`: the fact's content address under the reference
// kind a narrative cites a fact by. The entry is in the content-hash form
// (`InputHash.isContentHash`), so a reader tells it from a run token or a bare
// value hash, and it enters the derived fact's content address, so a moved
// input mints a new fact that supersedes the old within its lineage.
//
// The taint walk reads these entries as the derived fact's COMPUTED lineage:
// the computation named what flowed in, so nothing is inferred
// (`DisclosureTaint`).

/// The evidence entry naming a fact a derived fact was computed from.
[<RequireQualifiedAccess>]
module FactInput =

    /// The prefix every entry carries: the reference kind of a fact.
    [<Literal>]
    let Prefix = "fact:"

    /// The entry naming the fact `factId`.
    let ofFactId (factId: string) : string = Prefix + factId

    /// The fact an entry names, when it names one.
    let tryFactId (entry: string) : string option =
        if
            not (isNull entry)
            && entry.Length > Prefix.Length
            && entry.StartsWith(Prefix, System.StringComparison.Ordinal)
        then
            Some(entry.Substring Prefix.Length)
        else
            None

    /// The facts an evidence list names, in its order.
    let factIdsOf (inputs: string list) : string list = inputs |> List.choose tryFactId

// ─── The disclosure-taint index ──────────────────────────────────────
//
// What the taint walk reads of a scope, maintained by the writes that change
// it, so a gated check reads the index instead of listing every fact in the
// scope. A fact-to-fact derivation edge exists where a fact's evidence names
// a data-object version another fact produced as its `Series` value, and
// where it names another fact outright (`FactInput`);
// supersession is not a derivation and adds no edge, so a write only ever
// ADDS entries (a superseded fact stays a node: a downstream fact may still
// cite what it produced).
//
// The index is a join-semilattice: entries are keyed by content address and
// two entries for one address join field by field, so applying deltas is
// commutative, associative and idempotent, and the order writes land in
// cannot change it. Two writers of one address with different payloads (the
// content address does not cover a fact's classification) leave the JOIN,
// never the last writer's: every policy either named stays a taint source,
// and an address claiming two operations is no declassifier. Fail-closed.

/// One fact as the taint walk reads it: the four facets of a fact the walk
/// consults, and nothing else.
type DisclosureTaintNode = {
    /// The `Restricted` policy refs the fact is classified under (one for a
    /// stored fact; more only where two payloads of one address joined).
    Policies: string list
    /// The `Computed` operation ids the fact came from (at most one for a
    /// stored fact). A node with exactly one may be a declassifier.
    Operations: string list
    /// The data-object versions the fact was derived from, in evidence order.
    Inputs: string list
    /// The data-object versions the fact produced as its `Series` value.
    Produces: string list
}

/// Construction and the join of `DisclosureTaintNode`.
module DisclosureTaintNode =

    /// The node a fact is.
    let ofFact (fact: Fact) : DisclosureTaintNode = {
        Policies =
            match fact.Disclosure with
            | Restricted policyRef -> [ policyRef ]
            | Surfaceable
            | Internal -> []
        Operations =
            match fact.Method with
            | Computed(operationId, _, _) -> [ operationId ]
            | HumanAsserted _
            | Imported _ -> []
        Inputs = fact.Evidence.InputHashes
        Produces =
            match fact.Value with
            | Series version -> [ version ]
            | _ -> []
    }

    /// The facts the node's evidence names outright (`FactInput`), in its
    /// order: its computed lineage.
    let namedInputs (node: DisclosureTaintNode) : string list = FactInput.factIdsOf node.Inputs

    /// The node's other evidence inputs: the data-object versions whose
    /// producers the walk infers.
    let inferredInputs (node: DisclosureTaintNode) : string list =
        node.Inputs |> List.filter (FactInput.tryFactId >> Option.isNone)

    /// Two nodes for one address. Equal nodes join to themselves, so a
    /// well-formed index keeps every field in the fact's own order; unequal
    /// nodes join to the sorted union of every field.
    let join (a: DisclosureTaintNode) (b: DisclosureTaintNode) : DisclosureTaintNode =
        if a = b then
            a
        else
            let union (x: string list) (y: string list) = x @ y |> List.distinct |> List.sort

            {
                Policies = union a.Policies b.Policies
                Operations = union a.Operations b.Operations
                Inputs = union a.Inputs b.Inputs
                Produces = union a.Produces b.Produces
            }

/// The entries one write adds to the index: one per fact the write stored.
type DisclosureTaintDelta = {
    Entries: (string * DisclosureTaintNode) list
}

/// The disclosure-taint index of one scope: every stored fact's node, keyed by
/// content address.
type DisclosureTaintIndex = {
    Nodes: Map<string, DisclosureTaintNode>
}

/// The disclosure-taint index's construction, its deltas and its reads.
module DisclosureTaintIndex =

    /// The index of a scope holding no fact.
    let empty: DisclosureTaintIndex = { Nodes = Map.empty }

    /// The delta of a fact listing, as if each fact had just been written.
    let deltaOfFacts (facts: Fact list) : DisclosureTaintDelta = {
        Entries = facts |> List.map (fun f -> f.FactId, DisclosureTaintNode.ofFact f)
    }

    /// The delta of a write: a node for every fact the plan stores, asserted
    /// or superseding. A write that stores nothing adds nothing.
    let deltaOf (plan: WritePlan) : DisclosureTaintDelta = deltaOfFacts plan.Facts

    /// `index` with `delta` applied: each entry joined into the node already
    /// held for its address, or added.
    let apply (delta: DisclosureTaintDelta) (index: DisclosureTaintIndex) : DisclosureTaintIndex = {
        Nodes =
            delta.Entries
            |> List.fold
                (fun nodes (factId, node) ->
                    match Map.tryFind factId nodes with
                    | Some held -> Map.add factId (DisclosureTaintNode.join held node) nodes
                    | None -> Map.add factId node nodes)
                index.Nodes
    }

    /// The index rebuilt from a listing of every fact in a scope.
    let ofFacts (facts: Fact list) : DisclosureTaintIndex = apply (deltaOfFacts facts) empty

    /// The join of two indexes.
    let union (a: DisclosureTaintIndex) (b: DisclosureTaintIndex) : DisclosureTaintIndex =
        apply { Entries = Map.toList b.Nodes } a

    /// The addresses the index holds a node for.
    let factIds (index: DisclosureTaintIndex) : Set<string> =
        index.Nodes |> Map.toSeq |> Seq.map fst |> Set.ofSeq

    /// `index` without the nodes of addresses outside `factIds`: a store
    /// reconciling its index with the facts it holds.
    let restrictTo (factIds: Set<string>) (index: DisclosureTaintIndex) : DisclosureTaintIndex = {
        Nodes = index.Nodes |> Map.filter (fun factId _ -> factIds.Contains factId)
    }

    /// The node held for an address.
    let tryNode (factId: string) (index: DisclosureTaintIndex) : DisclosureTaintNode option = index.Nodes.TryFind factId

    /// The immediate upstream of every fact: the addresses of the facts that
    /// produced a version it was derived from, in its evidence order. Built
    /// once per index, so a walk pays for the reverse map once.
    let upstream (index: DisclosureTaintIndex) : string -> string list =
        let producers =
            index.Nodes
            |> Map.toList
            |> List.collect (fun (factId, node) -> node.Produces |> List.map (fun version -> version, factId))
            |> List.groupBy fst
            |> List.map (fun (version, pairs) -> version, pairs |> List.map snd)
            |> Map.ofList

        fun factId ->
            match index.Nodes.TryFind factId with
            | None -> []
            | Some node ->
                node.Inputs
                |> List.collect (fun version -> producers.TryFind version |> Option.defaultValue [])
                |> List.distinct

    /// The index as a flat entry list in address order: the shape a store
    /// persists.
    let toEntries (index: DisclosureTaintIndex) : (string * DisclosureTaintNode) list = Map.toList index.Nodes

    /// The index a persisted entry list describes; a repeated address joins.
    let ofEntries (entries: (string * DisclosureTaintNode) list) : DisclosureTaintIndex =
        apply { Entries = entries } empty

/// A fact store that keeps each scope's disclosure-taint index. The
/// disclosure gate reads a scope's index through this rather than listing
/// the scope's facts; a store that does not offer it is listed per check.
///
/// The answer holds a node for every fact the scope held when the read
/// started. A store whose index is missing rebuilds it once from its own
/// listing, writes it and logs the rebuild; nodes of addresses the store no
/// longer holds are dropped.
type IDisclosureTaintIndexSource =
    /// The disclosure-taint index of the scope named by `scopeId`.
    abstract ReadTaintIndex: scopeId: string -> Async<DisclosureTaintIndex>

    /// The disclosure-taint index of `scope`.
    abstract ReadTaintIndex: scope: ToolUp.Platform.ResolvedScope -> Async<DisclosureTaintIndex>