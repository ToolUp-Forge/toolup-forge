// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactDisclosureGate (Phase 525) ──────────────────────────────────
//
// The one `IFactDisclosureGate` implementation — every egress choke point
// (retrieval, tool results, narrative publication) consults this gate,
// and the gate applies the one `DisclosureEgress` predicate, so
// enforcement is never re-implemented per surface. Scope isolation is
// structural (GP 4): fact lookup goes through the scope-filtered
// `IFactStore.Get`, so an id from another scope is unresolvable — and an
// unresolvable id is *denied*, never disclosed. Disclosure therefore
// never widens scope, and scope never overrides a deny.
//
// **Audit (GP 6).** Every deny writes a `FactDisclosureDenied` event
// (surface, fact id, metric, policy ref, principal — never the value)
// under the reserved `_facts` source module, joining the assert /
// supersession trail in one queryable record. Emission is best-effort:
// an audit-write failure never converts a deny into a disclose (the
// verdict stands regardless).
//
// GP 12: stateless between calls; identity by value; async at the
// boundary; scope is the shard key.

/// The default `IFactDisclosureGate` over the composed fact store.
/// Construct via `FactDisclosureGate.create`; registered in DI by
/// `FactsCompose.withFactStore` alongside the store itself, so the fact
/// tier can never be composed without its egress gate.
type FactDisclosureGate(store: IFactStore, events: IEventStore, ?resolvePolicy: DisclosurePolicyResolver) =

    static let jsonOptions = FableConverters.create ()

    let resolve = defaultArg resolvePolicy DisclosurePolicyResolver.denyUnknown

    // Best-effort deny audit — a failed write is swallowed (the deny
    // itself already stands; auditing must never turn a refusal into an
    // exception on the answer path).
    let writeDenied (scopeId: string) (payload: FactDisclosureDeniedEvent) : Async<unit> = async {
        try
            do!
                events.Write {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = scopeId
                    SourceModule = FactEvents.SourceModule
                    EventType = DisclosureEvents.DeniedType
                    Payload = JsonSerializer.Serialize(payload, jsonOptions)
                }
        with _ ->
            ()
    }

    interface IFactDisclosureGate with

        member _.Check(scopeId, principal, surface, factIds) = async {
            let! verdicts =
                factIds
                |> List.distinct
                |> List.map (fun factId -> async {
                    let! fact = store.Get(scopeId, factId)

                    let verdict, metric =
                        match fact with
                        | Some f -> DisclosureEgress.evaluateFact resolve surface f, f.Metric.Value
                        // Unresolvable in this scope (unknown id, or a
                        // fact belonging to another tenant) ⇒ deny,
                        // conservatively — never fail open.
                        | None -> FactNotDisclosable "unknown-fact", ""

                    match verdict with
                    | FactNotDisclosable policyRef ->
                        do!
                            writeDenied scopeId {
                                Surface = FactEgressSurface.toString surface
                                FactId = factId
                                Metric = metric
                                PolicyRef = policyRef
                                Principal = principal
                            }
                    | FactDisclosable -> ()

                    return factId, verdict
                })
                |> Async.Parallel

            return Map.ofArray verdicts
        }

module FactDisclosureGate =

    /// The default gate over the composed store + event store, with the
    /// conservative policy resolver (every `Restricted` ref denies until a
    /// policy vocabulary lands).
    let create (store: IFactStore) (events: IEventStore) : IFactDisclosureGate =
        FactDisclosureGate(store, events) :> IFactDisclosureGate

    /// A gate with a deployment-supplied `Restricted`-policy resolver.
    let createWithPolicyResolver
        (resolvePolicy: DisclosurePolicyResolver)
        (store: IFactStore)
        (events: IEventStore)
        : IFactDisclosureGate =
        FactDisclosureGate(store, events, resolvePolicy) :> IFactDisclosureGate