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
//     via `FactInvalidation.deriveFreshness` — never stored, so it cannot
//     drift from the truth the timestamps state. An unregistered metric
//     defaults to `UntilSuperseded` (the registry's documented slow-moving
//     default).
//
//   - **Supersession pointer** for stale heads: a head visible at the
//     clause's `AsOf` that a later assertion has since superseded carries
//     its successor's id, so retrieval can surface "this number was later
//     corrected" alongside the stale stamp.
//
// ─── Phase 623.B — upstream-aware freshness + OnQuery recompute ───────
//
// Stage 0 called `Freshness.derive`, which degrades `UntilUpstreamChange`
// to `UntilSuperseded` because it had no invalidation signal. Phase 561
// built the signal (`FactInvalidation.deriveFreshness`) and left the read
// path on the Stage-0 call, so a metric that declared
// `UntilUpstreamChange` never went stale for the reason it declared. The
// resolver now derives `inputsChanged` and routes through
// `FactInvalidation.deriveFreshness`.
//
// **How `inputsChanged` is derived (law L1 — computed, never stored).** A
// fact cites its inputs by data-object version identity
// (`Evidence.InputHashes`, the same string space as the lineage node's
// `ObjectId` — see `FactInvalidation.invalidationSet`). Its inputs have
// changed exactly when one of those objects has gained a version created
// **after the fact was asserted** — `IDataObjectStore.ListVersions` over
// the caller's own scope, compared against `fact.AsOf`. That is the read
// -path mirror of the write-path seed `reactToDataChange` receives when a
// version lands (`ReactiveDataChange.fs`), so the two doors agree by
// construction rather than by a stored flag either could miss.
//
// **Zero cost when not asked for (GP 13).** The `IDataObjectStore` is an
// *optional* dependency, and the version probe runs only for a fact whose
// metric actually declares `UntilUpstreamChange` staleness or an
// `OnQuery` recompute policy. Every other fact — and every deployment
// that composes no object store — takes the identical path, and the
// identical number of store calls, as before Phase 623: `inputsChanged`
// is `false`, and `FactInvalidation.deriveFreshness` with
// `inputsChanged = false` IS `Freshness.derive` (GP 11).
//
// **The `OnQuery` recompute arm.** A fact whose metric declares
// `RecomputePolicy.OnQuery` and whose inputs have changed is recomputed
// *at read* through `FactInvalidation.recomputeNow` — the deployment's
// `IFactRecomputer` produces a fresh draft and it re-asserts through the
// ordinary `IFactStore.Assert` path (supersession stays derived; content
// addressing makes an unchanged recompute a no-op). The resolver then
// projects the re-asserted head, so the caller reads the corrected value
// rather than a stale one plus a stale stamp. The recomputer is optional:
// unwired (or `NoFactRecomputer`) yields `Ok None`, the existing fact
// stands, and the projection is exactly the pre-623 one.
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
type FactStoreFactResolver
    (
        store: IFactStore,
        registry: Grounding.IMetricRegistry option,
        clock: unit -> DateTime,
        dataObjects: IDataObjectStore option,
        recomputer: IFactRecomputer option
    ) =

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

    /// Phase 623.B — has any data object this fact cites as an input
    /// gained a version since the fact was asserted? Derived per call from
    /// `IDataObjectStore` (law L1: never a stored flag). `None` store, no
    /// cited inputs, or a probe that finds nothing newer ⇒ `false`, which
    /// makes `FactInvalidation.deriveFreshness` byte-for-byte
    /// `Freshness.derive` (GP 11).
    ///
    /// The probe reads `ListObjects` once and narrows to the objects whose
    /// **newest version landed after `fact.AsOf`** — nothing else can have
    /// changed under this fact — then matches those against the fact's
    /// cited identities under either convention a producer may use:
    ///
    ///   * the stable `ObjectId` (what `ILineageStore` nodes carry), or
    ///   * a **version** identity, i.e. the `ContentHash` of one of that
    ///     object's own versions that predates the fact. This is the
    ///     convention a `Computed` fact must use, because `Fact.compute`
    ///     folds the input hashes — and NOT the value — into the content
    ///     address: a recompute only produces a new head when it cites the
    ///     new input version, which is the model saying "a recompute over
    ///     identical inputs is the identical fact".
    ///
    /// Cost is bounded by how much data landed since the fact was
    /// asserted: nothing changed ⇒ one `ListObjects` and no history read.
    let inputsChanged (scopeId: string) (fact: Fact) : Async<bool> =
        match dataObjects with
        | None -> async { return false }
        | Some _ when List.isEmpty fact.Evidence.InputHashes -> async { return false }
        | Some objects -> async {
            let inputs = Set.ofList fact.Evidence.InputHashes
            let! latest = objects.ListObjects scopeId

            let candidates =
                latest |> List.filter (fun o -> o.CreatedAt.ToUniversalTime() > fact.AsOf)

            if List.isEmpty candidates then
                return false
            elif candidates |> List.exists (fun o -> inputs.Contains o.ObjectId) then
                return true
            else
                let! histories =
                    candidates
                    |> List.map (fun o -> objects.ListVersions(scopeId, o.ObjectId))
                    |> Async.Parallel

                return
                    histories
                    |> Array.exists (
                        List.exists (fun version ->
                            version.CreatedAt.ToUniversalTime() <= fact.AsOf
                            && inputs.Contains version.ContentHash)
                    )
          }

    new(store: IFactStore, registry: Grounding.IMetricRegistry option, clock: unit -> DateTime) =
        FactStoreFactResolver(store, registry, clock, None, None)

    interface IFactResolver with

        member _.Resolve(scopeId: string, clause: FactClause) : Async<ResolvedFact list> = async {
            let! heads = store.Query(scopeId, toQuery clause)

            let metric = registry |> Option.bind (fun r -> r.TryGetMetric clause.Metric)

            let policy =
                metric
                |> Option.map _.Staleness
                |> Option.defaultValue Grounding.UntilSuperseded

            let recomputePolicy =
                metric |> Option.bind _.RecomputePolicy |> Grounding.RecomputePolicy.resolve

            // The upstream-change probe is what Phase 623.B added to this
            // path, so it runs ONLY where it can change the answer: a
            // metric that declares `UntilUpstreamChange` staleness, or one
            // that declares an `OnQuery` recompute. Every other metric
            // takes the pre-623 path with the pre-623 number of store
            // calls (GP 13).
            let probeUpstream =
                policy = Grounding.UntilUpstreamChange || recomputePolicy = Grounding.OnQuery

            let displayFormat = metric |> Option.map _.DisplayFormat |> Option.defaultValue ""

            let now = clock().ToUniversalTime()

            let! resolved =
                heads
                |> List.map (fun head -> async {
                    let! changed =
                        if probeUpstream then
                            inputsChanged scopeId head
                        else
                            async { return false }

                    // The `OnQuery` arm: a stale-by-upstream-change fact is
                    // recomputed at read through the deployment's
                    // recomputer and the re-asserted head is what the
                    // caller sees. No recomputer (or no recompute path)
                    // leaves the existing head standing, stale-stamped.
                    let! fact =
                        match recomputer with
                        | Some engine when changed && recomputePolicy = Grounding.OnQuery -> async {
                            let! recomputed = FactInvalidation.recomputeNow store engine scopeId head

                            return
                                match recomputed with
                                | Ok(Some fresh) -> fresh
                                | Ok None
                                | Error _ -> head
                          }
                        | _ -> async { return head }

                    // A recompute that produced a NEW head cleared the
                    // upstream change for the value now being projected;
                    // an unchanged (content-addressed no-op) re-assert did
                    // not, so the stale stamp stands and stays honest.
                    let stillChanged = changed && fact.FactId = head.FactId

                    // The head's successor, if a later assertion superseded
                    // it (possible when the clause reconstructs an earlier
                    // `AsOf` — the head-at-t may have been corrected since).
                    let! chain = store.QuerySupersessionChain(scopeId, fact.FactId)

                    let successor = chain |> List.tryFind (fun g -> g.Supersedes = Some fact.FactId)

                    let freshness =
                        match FactInvalidation.deriveFreshness policy fact successor.IsNone stillChanged now with
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

    /// Phase 623.B — the reactive resolver: `create` plus the two optional
    /// dependencies that make `UntilUpstreamChange` real at the read path.
    /// `dataObjects` supplies the derived `inputsChanged` signal (an input
    /// object that gained a version after the fact was asserted);
    /// `recomputer` arms the `OnQuery` recompute-at-read arm. Passing
    /// `None, None` is byte-for-byte `create` (GP 11) — which is exactly
    /// what a deployment with neither composed gets.
    let createReactive
        (store: IFactStore)
        (registry: Grounding.IMetricRegistry option)
        (dataObjects: IDataObjectStore option)
        (recomputer: IFactRecomputer option)
        : IFactResolver =
        FactStoreFactResolver(store, registry, (fun () -> DateTime.UtcNow), dataObjects, recomputer) :> IFactResolver

    /// `createReactive` with an explicit clock (test seam / deterministic
    /// freshness evaluation).
    let createReactiveWithClock
        (store: IFactStore)
        (registry: Grounding.IMetricRegistry option)
        (dataObjects: IDataObjectStore option)
        (recomputer: IFactRecomputer option)
        (clock: unit -> DateTime)
        : IFactResolver =
        FactStoreFactResolver(store, registry, clock, dataObjects, recomputer) :> IFactResolver