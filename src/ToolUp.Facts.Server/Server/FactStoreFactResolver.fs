// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactStoreFactResolver (Phase 558 — closes the Phase 522.B seam) ──
//
// The concrete `IFactResolver` over an `IFactStore` — the adapter Phase
// 522 deferred. It maps the pipeline's fact-companion-free `FactClause`
// onto the store's typed `FactQuery` (subject / metric / period / AsOf),
// then projects each current head to a `ResolvedFact`:
//
//   - **Rendering** under the metric registry's declared `DisplayFormat`
//     (Phase 519) — the canonical display form the answer quotes verbatim
//     and the numeric-fidelity gate (Phase 523) canonicalises against. An
//     unregistered metric (or an empty format) renders verbatim.
//   - **Freshness** derived per the metric's `Grounding.StalenessPolicy`
//     via `Freshness.derive` — never stored, so it cannot drift from the
//     truth the timestamps state. An unregistered metric defaults to
//     `UntilSuperseded` (the registry's documented slow-moving default).
//   - **Supersession pointer** for stale heads: a head visible at the
//     clause's `AsOf` that a later assertion has since superseded carries
//     its successor's id, so retrieval can surface "this number was later
//     corrected" alongside the stale stamp.
//
// Scope isolation is structural (GP 4): every read forwards the caller's
// resolved `scopeId` to the scope-filtered store, so a fact from another
// scope is unreachable — the resolver never widens what the store allows.
//
// GP 12 audit: identity by value (strings, value records); async at the
// boundary; no callbacks; stateless between calls (every resolve
// recomputes from the store); scope is the shard key with no cross-scope
// ordering promise; the clock is the injected seam, no timing promise
// beyond the store's own second-precision `AsOf`.

/// The default `IFactResolver` over the composed fact store. Construct via
/// `FactStoreFactResolver.create`; registered in DI by
/// `FactsCompose.withFactStore` alongside the store + disclosure gate, so
/// the fact tier is one compose knob, never three.
type FactStoreFactResolver(store: IFactStore, registry: Grounding.IMetricRegistry option, clock: unit -> DateTime) =

    // The pipeline's generic clause → the store's typed query. Every
    // clause field maps 1:1; the query never includes superseded facts —
    // the resolver reads heads and derives their supersession separately.
    let toQuery (clause: FactClause) : FactQuery = {
        Subject =
            Some {
                Hierarchy = clause.SubjectHierarchy
                Path = clause.SubjectPath
            }
        Metric = Some(MetricRef clause.Metric)
        PeriodOverlaps =
            match clause.PeriodFrom, clause.PeriodTo with
            | None, None -> None
            | from, to' ->
                Some {
                    From = from |> Option.defaultValue DateTime.MinValue
                    To = to' |> Option.defaultValue DateTime.MaxValue
                    Label = None
                }
        Method = None
        AsOf = clause.AsOf
        IncludeSuperseded = false
    }

    // Canonical display rendering under the metric's `DisplayFormat` —
    // the one shared implementation (`FactRendering.render`, Facts.Core),
    // also consumed by the `query_facts` tool (Phase 559) so a value never
    // renders two ways at two doors.
    let renderValue (format: string) (value: FactValue) : string = FactRendering.render format value

    interface IFactResolver with

        member _.Resolve(scopeId: string, clause: FactClause) : Async<ResolvedFact list> = async {
            let! heads = store.Query(scopeId, toQuery clause)

            let metric = registry |> Option.bind (fun r -> r.TryGetMetric clause.Metric)

            let policy =
                metric
                |> Option.map _.Staleness
                |> Option.defaultValue Grounding.UntilSuperseded

            let displayFormat = metric |> Option.map _.DisplayFormat |> Option.defaultValue ""

            let now = clock().ToUniversalTime()

            let! resolved =
                heads
                |> List.map (fun fact -> async {
                    // The head's successor, if a later assertion superseded
                    // it (possible when the clause reconstructs an earlier
                    // `AsOf` — the head-at-t may have been corrected since).
                    let! chain = store.QuerySupersessionChain(scopeId, fact.FactId)

                    let successor = chain |> List.tryFind (fun g -> g.Supersedes = Some fact.FactId)

                    let freshness =
                        match Freshness.derive policy fact successor.IsNone now with
                        | Fresh -> FactFresh
                        | Stale since -> FactStale(since.ToUniversalTime().ToString "o")

                    return {
                        FactId = fact.FactId
                        Rendering = renderValue displayFormat fact.Value
                        Freshness = freshness
                        SupersededBy = successor |> Option.map _.FactId
                        Metric = fact.Metric.Value
                    }
                })
                |> Async.Parallel

            return Array.toList resolved
        }

/// Construction for `FactStoreFactResolver`.
module FactStoreFactResolver =

    /// The default resolver over the composed store. `registry` supplies
    /// each metric's staleness policy + display format (Phase 519); `None`
    /// derives freshness under the `UntilSuperseded` default and renders
    /// values verbatim. Freshness is evaluated at `DateTime.UtcNow`.
    let create (store: IFactStore) (registry: Grounding.IMetricRegistry option) : IFactResolver =
        FactStoreFactResolver(store, registry, fun () -> DateTime.UtcNow) :> IFactResolver

    /// `create` with an explicit clock (test seam / deterministic
    /// freshness evaluation).
    let createWithClock
        (store: IFactStore)
        (registry: Grounding.IMetricRegistry option)
        (clock: unit -> DateTime)
        : IFactResolver =
        FactStoreFactResolver(store, registry, clock) :> IFactResolver