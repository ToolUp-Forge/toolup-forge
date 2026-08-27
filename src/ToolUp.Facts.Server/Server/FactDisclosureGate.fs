// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactDisclosureGate (Phase 525 + Phase 562 taint) ────────────────
//
// The one `IFactDisclosureGate` implementation — every egress choke point
// (retrieval, tool results, narrative publication, export, webhook)
// consults this gate, and the gate applies the one `DisclosureEgress`
// predicate, so enforcement is never re-implemented per surface. Scope
// isolation is structural (GP 4): fact lookup goes through the scope-
// filtered `IFactStore.Get`, so an id from another scope is unresolvable —
// and an unresolvable id is *denied*, never disclosed. Disclosure
// therefore never widens scope, and scope never overrides a deny.
//
// **Phase 562 — taint propagation.** When a `DisclosureTaintConfig` is
// composed, the gate layers a derivation-walk verdict on top of the Phase
// 525 predicate: a fact whose derivation includes a
// `Restricted(TaintPropagating)` input is denied even when its own
// disclosure permits egress, *unless* the path crosses a declared
// declassification routine. Each declassification-crossing on a disclosed
// fact is audited (GP 6). An **empty** config (the default) skips the walk
// entirely — the gate is byte-for-byte the Phase 525 gate (GP 11 / GP 13).
//
// **Phase 674 — multi-party conjunction.** When policies declare a
// contributor scope, the taint verdict becomes a conjunction over the
// contributing parties: the derivation path must satisfy EVERY one, and a
// declassification routine clears only the parties that accepted it. One
// party's consent therefore never launders another's data. Fail-closed —
// absent or unevaluable acceptance denies — and inert when no policy
// declares a scope, so a single-party deployment is byte-for-byte the
// Phase 562 gate. The contributor scope is resolved from the composed
// policy vocabulary keyed by the ref the STORED fact carries; no party
// identity is ever read from the caller.
//
// **Audit (GP 6).** Every deny writes a `FactDisclosureDenied` event
// (surface, fact id, metric, policy ref, principal — never the value); a
// declassification-cleared disclosure writes a `FactDisclosureDeclassified`
// event per crossing; and — Phase 674.C — a disclosure carrying a
// contributing party's data writes a `FactDisclosureAccessed` event. All
// three carry the contributor scopes involved, so the per-party
// projection (plan D4) is derivable from this one stream. They ride the
// reserved `_facts` source module, joining the assert / supersession trail
// in one queryable record. Emission is best-effort: an audit-write failure
// never flips the verdict.
//
// GP 12: stateless between calls; identity by value; async at the
// boundary; scope is the shard key.

/// The ambient claimed-purpose carrier (Phase 592). A request states the
/// purpose it claims — typically resolved by the composition's handler
/// from a header / tool argument — and the gate reads it at evaluation
/// time. Ambient per GP 7 (request context rides the async chain), so
/// the `IFactDisclosureGate` seam's signature stays unchanged and every
/// existing choke point is purpose-checked without edits. With a
/// declared taxonomy, an unclaimed purpose refuses at every
/// purpose-bound surface (default-deny-by-shape); without one the
/// carrier is inert data nothing reads.
module FactPurposeContext =

    let private claimed = System.Threading.AsyncLocal<string option>()

    /// The purpose the current async chain claims, if any.
    let current () : string option =
        match claimed.Value with
        | Some p -> Some p
        | _ -> None

    /// Claim a purpose for the current async chain (flows into child
    /// asyncs, never into siblings or parents).
    let claim (purpose: string) : unit = claimed.Value <- Some purpose

    /// Clear the current chain's claim.
    let clear () : unit = claimed.Value <- None

/// The default `IFactDisclosureGate` over the composed fact store.
/// Construct via `FactDisclosureGate.create`; registered in DI by
/// `FactsCompose.withFactStore` alongside the store itself, so the fact
/// tier can never be composed without its egress gate.
type FactDisclosureGate
    (
        store: IFactStore,
        events: IEventStore,
        ?resolvePolicy: DisclosurePolicyResolver,
        ?taint: DisclosureTaintConfig,
        ?purpose: DisclosurePurposeConfig
    ) =

    static let jsonOptions = FableConverters.create ()

    let taintConfig = defaultArg taint DisclosureTaintConfig.empty

    // Phase 592 — the purpose facet. `None` (no taxonomy declared) keeps
    // the facet absent: no claim is read, no refusal minted, behaviour
    // byte-for-byte pre-592 (GP 11 / GP 13).
    let purposeConfig = purpose

    // The policy resolver: an explicit one wins; otherwise the registered
    // taint policy vocabulary drives it (Phase 562.A — the resolver's first
    // real vocabulary); otherwise the conservative deny-unknown default.
    let resolve =
        match resolvePolicy with
        | Some r -> r
        | None when not (DisclosureTaintConfig.isEmpty taintConfig) -> DisclosureTaintConfig.resolver taintConfig
        | None -> DisclosurePolicyResolver.denyUnknown

    // Best-effort audit write under the reserved `_facts` source module — a
    // failed write is swallowed (the verdict already stands; auditing must
    // never turn a refusal into an exception on the answer path, nor block
    // a permitted disclosure).
    let writeEvent (scopeId: string) (eventType: string) (payload: string) : Async<unit> = async {
        try
            do!
                events.Write {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = scopeId
                    SourceModule = FactEvents.SourceModule
                    EventType = eventType
                    Payload = payload
                }
        with _ ->
            ()
    }

    let writeDenied (scopeId: string) (payload: FactDisclosureDeniedEvent) : Async<unit> =
        writeEvent scopeId DisclosureEvents.DeniedType (JsonSerializer.Serialize(payload, jsonOptions))

    let writeDeclassified (scopeId: string) (payload: FactDisclosureDeclassifiedEvent) : Async<unit> =
        writeEvent scopeId DisclosureEvents.DeclassifiedType (JsonSerializer.Serialize(payload, jsonOptions))

    // Phase 674.C — the per-party access facet. Written only for a fact
    // that actually carries a contributing party's data, so a deployment
    // declaring no contributor scope never emits this row (GP 11 / GP 13).
    let writeAccessed (scopeId: string) (payload: FactDisclosureAccessedEvent) : Async<unit> =
        writeEvent scopeId DisclosureEvents.AccessedType (JsonSerializer.Serialize(payload, jsonOptions))

    // Phase 674 — the contributing party of a fact's OWN registered policy.
    // Resolved server-side from the composed vocabulary keyed by the ref the
    // stored fact carries; nothing a caller supplies reaches this lookup.
    let ownContributorScopes (fact: Fact option) : string list =
        match fact with
        | Some f ->
            match f.Disclosure with
            | Restricted policyRef -> DisclosureTaintConfig.scopeOf taintConfig policyRef |> Option.toList
            | _ -> []
        | None -> []

    // The Phase 525 base verdict over a fact's own disclosure classification,
    // plus its metric for the audit payload.
    let baseVerdict (surface: FactEgressSurface) (fact: Fact option) : FactDisclosureVerdict * string =
        match fact with
        | Some f -> DisclosureEgress.evaluateFact resolve surface f, f.Metric.Value
        // Unresolvable in this scope (unknown id, or a fact belonging to
        // another tenant) ⇒ deny, conservatively — never fail open.
        | None -> FactNotDisclosable "unknown-fact", ""

    interface IFactDisclosureGate with

        member _.Check(scopeId, principal, surface, factIds) = async {
            let ids = factIds |> List.distinct

            // Phase 592 — the purpose facet, evaluated once per check
            // (the claim and the surface's allowed set are check-level,
            // not per-fact). With a declared taxonomy the claimed purpose
            // must be in this surface's allowed set; out-of-set or
            // missing claims refuse with the allowed set enumerated
            // (default-deny-by-shape). No taxonomy ⇒ inert.
            let claimedPurpose =
                match purposeConfig with
                | Some _ -> FactPurposeContext.current () |> Option.defaultValue ""
                | None -> ""

            let taxonomyVersion =
                purposeConfig |> Option.map _.Taxonomy.Version |> Option.defaultValue ""

            let purposeRefusal: string option =
                match purposeConfig with
                | None -> None
                | Some cfg ->
                    let allowed = DisclosurePurposeConfig.allowedFor cfg surface

                    let allowedText =
                        if List.isEmpty allowed then
                            "none"
                        else
                            String.concat ", " allowed

                    match FactPurposeContext.current () with
                    | Some p when List.contains p allowed -> None
                    | Some p -> Some(sprintf "purpose-denied:%s (allowed: %s)" p allowedText)
                    | None -> Some(sprintf "purpose-unclaimed (allowed: %s)" allowedText)

            // The derivation graph is built once per check, and only when a
            // taint config is composed — a deployment without taint policies
            // never loads the fact listing (GP 13, byte-identical to 525).
            // A purpose-refused check never builds it either: every fact is
            // already denied before any per-fact evaluation.
            let! graph =
                if DisclosureTaintConfig.isEmpty taintConfig || Option.isSome purposeRefusal then
                    async.Return None
                else
                    async {
                        let! all =
                            store.Query(
                                scopeId,
                                {
                                    FactQuery.all with
                                        IncludeSuperseded = true
                                }
                            )

                        return Some(DisclosureTaint.buildGraph all)
                    }

            let! verdicts =
                ids
                |> List.map (fun factId -> async {
                    let! fact = store.Get(scopeId, factId)
                    let baseV, metric = baseVerdict surface fact

                    // Phase 592 — a purpose refusal denies every fact in
                    // the check before per-fact evaluation (the claim is
                    // check-level); otherwise layer the taint verdict only
                    // on a fact the Phase 525 predicate would disclose — a
                    // directly-denied fact stays denied on its own policy
                    // ref, and the taint walk is skipped.
                    let verdict =
                        match purposeRefusal with
                        | Some refusalRef -> FactNotDisclosable refusalRef, None
                        | None ->
                            match baseV, graph with
                            | FactDisclosable, Some g ->
                                let outcome = DisclosureTaint.analyze taintConfig g factId

                                // Phase 674 — the conjunction: the path must
                                // satisfy EVERY contributing party's policy.
                                // A surviving party-scoped restriction denies
                                // and the ref names the unsatisfied party
                                // (never the counterparty's data); an
                                // unscoped survivor keeps the Phase 562 ref,
                                // so single-party verdicts are unchanged.
                                match outcome.InheritedPolicyRef with
                                | Some inheritedRef ->
                                    let denyRef =
                                        if List.isEmpty outcome.UnsatisfiedScopes then
                                            inheritedRef
                                        else
                                            DisclosureContributorScope.unsatisfiedRef outcome.UnsatisfiedScopes

                                    FactNotDisclosable denyRef, Some outcome
                                | None -> FactDisclosable, Some outcome
                            | _ -> baseV, None

                    let finalVerdict, taintOutcome = verdict

                    // Phase 674.C — the contribution facet on every audit
                    // row. With a taint outcome it is the whole walked
                    // lineage; without one (a directly-denied fact, or a
                    // deployment with no taint config) it is the fact's own
                    // registered policy — empty when no party is declared.
                    let contributorScopes =
                        match taintOutcome with
                        | Some outcome -> outcome.ContributorScopes
                        | None -> ownContributorScopes fact

                    let unsatisfiedScopes =
                        match taintOutcome with
                        | Some outcome -> outcome.UnsatisfiedScopes
                        | None -> []

                    match finalVerdict with
                    | FactNotDisclosable policyRef ->
                        do!
                            writeDenied scopeId {
                                Surface = FactEgressSurface.toString surface
                                FactId = factId
                                Metric = metric
                                PolicyRef = policyRef
                                Principal = principal
                                Purpose = claimedPurpose
                                TaxonomyVersion = taxonomyVersion
                                ContributorScopes = contributorScopes
                                UnsatisfiedScopes = unsatisfiedScopes
                            }
                    | FactDisclosable ->
                        // A disclosed fact whose derivation crossed a declared
                        // declassification routine audits each crossing (GP 6).
                        match taintOutcome with
                        | Some outcome ->
                            for crossing in outcome.Crossings do
                                do!
                                    writeDeclassified scopeId {
                                        Surface = FactEgressSurface.toString surface
                                        FactId = factId
                                        DeclassifierFactId = crossing.DeclassifierFactId
                                        OperationId = crossing.OperationId
                                        Rationale = crossing.Rationale
                                        Principal = principal
                                        Purpose = claimedPurpose
                                        TaxonomyVersion = taxonomyVersion
                                        ContributorScopes = outcome.ContributorScopes
                                        AcceptedScopes = crossing.AcceptedScopes
                                    }
                        | None -> ()

                        // Phase 674.C — the per-party ACCESS row. Only when
                        // a contributing party's data actually left, so a
                        // party-free deployment emits nothing new (GP 11).
                        if not (List.isEmpty contributorScopes) then
                            do!
                                writeAccessed scopeId {
                                    Surface = FactEgressSurface.toString surface
                                    FactId = factId
                                    Metric = metric
                                    ContributorScopes = contributorScopes
                                    Principal = principal
                                    Purpose = claimedPurpose
                                    TaxonomyVersion = taxonomyVersion
                                }

                    return factId, finalVerdict
                })
                |> Async.Parallel

            return Map.ofArray verdicts
        }

module FactDisclosureGate =

    /// The default gate over the composed store + event store, with the
    /// conservative policy resolver (every `Restricted` ref denies until a
    /// policy vocabulary lands) and no taint propagation. Byte-for-byte the
    /// Phase 525 gate.
    let create (store: IFactStore) (events: IEventStore) : IFactDisclosureGate =
        FactDisclosureGate(store, events) :> IFactDisclosureGate

    /// A gate with a deployment-supplied `Restricted`-policy resolver (no
    /// taint propagation).
    let createWithPolicyResolver
        (resolvePolicy: DisclosurePolicyResolver)
        (store: IFactStore)
        (events: IEventStore)
        : IFactDisclosureGate =
        FactDisclosureGate(store, events, resolvePolicy) :> IFactDisclosureGate

    /// A gate with a Phase 562 taint configuration: the registered policy
    /// vocabulary drives the resolver (562.A) and taint propagates along the
    /// fact derivation graph, cleared only by a declared declassification
    /// routine (562.B/C). An empty config yields the Phase 525 gate exactly
    /// (GP 11).
    let createWithTaint (taint: DisclosureTaintConfig) (store: IFactStore) (events: IEventStore) : IFactDisclosureGate =
        FactDisclosureGate(store, events, ?resolvePolicy = None, taint = taint) :> IFactDisclosureGate

    /// Phase 592 — the fully-configured gate: optional taint (562) +
    /// optional purpose binding (592). With a purpose config, every check
    /// requires the ambient `FactPurposeContext` claim to be in the
    /// surface's allowed set; out-of-set or missing claims refuse with
    /// the allowed set enumerated, and grants and denials both stamp the
    /// claimed purpose + taxonomy version into the audit events. `None`
    /// on both axes is the Phase 525 gate exactly (GP 11 / GP 13). This
    /// is the shape `FactsCompose`'s DI factory builds from the optional
    /// registrations.
    let createConfigured
        (taint: DisclosureTaintConfig option)
        (purpose: DisclosurePurposeConfig option)
        (store: IFactStore)
        (events: IEventStore)
        : IFactDisclosureGate =
        FactDisclosureGate(store, events, ?resolvePolicy = None, ?taint = taint, ?purpose = purpose)
        :> IFactDisclosureGate