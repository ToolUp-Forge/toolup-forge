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
// **Audit (GP 6).** Every deny writes a `FactDisclosureDenied` event
// (surface, fact id, metric, policy ref, principal — never the value); a
// declassification-cleared disclosure writes a `FactDisclosureDeclassified`
// event per crossing. Both ride the reserved `_facts` source module,
// joining the assert / supersession trail in one queryable record.
// Emission is best-effort: an audit-write failure never flips the verdict.
//
// GP 12: stateless between calls; identity by value; async at the
// boundary; scope is the shard key.

/// The default `IFactDisclosureGate` over the composed fact store.
/// Construct via `FactDisclosureGate.create`; registered in DI by
/// `FactsCompose.withFactStore` alongside the store itself, so the fact
/// tier can never be composed without its egress gate.
type FactDisclosureGate
    (store: IFactStore, events: IEventStore, ?resolvePolicy: DisclosurePolicyResolver, ?taint: DisclosureTaintConfig) =

    static let jsonOptions = FableConverters.create ()

    let taintConfig = defaultArg taint DisclosureTaintConfig.empty

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

            // The derivation graph is built once per check, and only when a
            // taint config is composed — a deployment without taint policies
            // never loads the fact listing (GP 13, byte-identical to 525).
            let! graph =
                if DisclosureTaintConfig.isEmpty taintConfig then
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

                    // Layer the taint verdict only on a fact the Phase 525
                    // predicate would otherwise disclose — a directly-denied
                    // fact stays denied on its own policy ref, and the taint
                    // walk is skipped.
                    let verdict =
                        match baseV, graph with
                        | FactDisclosable, Some g ->
                            let outcome = DisclosureTaint.analyze taintConfig g factId

                            match outcome.InheritedPolicyRef with
                            | Some inheritedRef -> FactNotDisclosable inheritedRef, Some outcome
                            | None -> FactDisclosable, Some outcome
                        | _ -> baseV, None

                    let finalVerdict, taintOutcome = verdict

                    match finalVerdict with
                    | FactNotDisclosable policyRef ->
                        do!
                            writeDenied scopeId {
                                Surface = FactEgressSurface.toString surface
                                FactId = factId
                                Metric = metric
                                PolicyRef = policyRef
                                Principal = principal
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
                                    }
                        | None -> ()

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