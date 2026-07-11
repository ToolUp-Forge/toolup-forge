// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.Narrative

/// Phase 521.C — stale-narrative discovery over the narrative store.
///
/// Given a set of fact ids known to be superseded, enumerate the
/// committed narratives visible to a scope that cite any of them, and
/// return each with its staleness flags. Discovery / flagging only (plan
/// D4): a superseded fact never rewrites a narrative — it makes the
/// staleness a queryable annotation the surface (UI, an AI tool, an audit
/// sweep) can act on.
///
/// `IFactStore` is deliberately *not* a dependency here — this stays in
/// the platform tier, decoupled from the fact companion (GP 1). The caller
/// holds the fact store, resolves each superseded fact's lineage head
/// (`IFactStore.QuerySupersessionChain`), and passes the resulting
/// `superseded id → superseding id option` map in; the walk itself is a
/// pure application of `NarrativeFacts.staleFlags` over the store's
/// entries.
module NarrativeSupersession =

    /// The narratives visible to `scopeId` (newest first, up to `limit`)
    /// that cite at least one superseded fact, each paired with its
    /// staleness flags. `supersededBy` maps a superseded fact id to the id
    /// that superseded it (the lineage's current head), or `None` when the
    /// caller doesn't know the head. An empty map short-circuits to `[]`
    /// (no fact-store round-trips, no per-entry reads).
    ///
    /// Reads are per-entry (`List` for the visible summaries, then `Get`
    /// for each entry's full document) so the walk works against any
    /// `INarrativeStore` implementation without a bespoke query method;
    /// the in-memory default holds the recent tail per scope, which is the
    /// intended discovery surface.
    let findStaleNarratives
        (store: INarrativeStore)
        (scopeId: string)
        (limit: int)
        (supersededBy: Map<string, string option>)
        : Async<(NarrativeEntryInfo * NarrativeFacts.StaleNarrativeFlag list) list> =
        async {
            if Map.isEmpty supersededBy then
                return []
            else
                let! infos = store.List(scopeId, limit)

                let! flagged =
                    infos
                    |> List.map (fun info -> async {
                        let! entry = store.Get(scopeId, info.Id)

                        match entry with
                        | Some e ->
                            match NarrativeFacts.staleFlags supersededBy e.Document with
                            | [] -> return None
                            | flags -> return Some(info, flags)
                        | None -> return None
                    })
                    |> Async.Sequential

                return flagged |> Array.toList |> List.choose id
        }