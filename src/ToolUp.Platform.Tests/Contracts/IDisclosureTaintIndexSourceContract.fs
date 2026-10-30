// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IDisclosureTaintIndexSourceContract

open System
open Expecto
open ToolUp.Facts
open ToolUp.Platform

// ─── IDisclosureTaintIndexSource — the conformance pack ──────────────
//
// A fact store that keeps each scope's disclosure-taint index answers, for
// every scope and after every write, exactly the index rebuilt from its own
// whole listing (`DisclosureTaintIndex.ofFacts` over the facts it holds,
// superseded facts included). Three laws:
//
//  1. after any write the index equals the rebuild;
//  2. the index does not depend on the order writes land in (the delta is a
//     lattice join, so two scopes written in opposite orders agree);
//  3. reading again rebuilds nothing: where the store counts its rebuilds,
//     the count moves at most once, on the first read of a scope, and never
//     on a later read or after a later write.

/// What an implementation hands the pack: the index source, the fact store
/// that writes it (usually the same object), two fresh scopes, and the
/// store's rebuild counter when it exposes one.
type TaintIndexHarness = {
    Source: IDisclosureTaintIndexSource
    Store: IFactStore
    ScopeA: string
    ScopeB: string
    Rebuilds: (unit -> int64) option
}

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (subjectMember: string) (metric: string) (inputs: string list) (value: FactValue) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ subjectMember ]
    }
    Metric = MetricRef metric
    Value = value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = inputs
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Surfaceable
}

/// A tainted producer, a fact derived from it, and an unrelated fact.
let private producer = {
    draft "uk" "raw" [ "dov-in" ] (Series "dov-raw") with
        Disclosure = Restricted "licensed"
}

let private derived = {
    draft "uk" "derived" [ "dov-raw" ] (Scalar 1m) with
        Method = Computed("passthrough", "1", "p0")
}

let private unrelated = draft "fr" "revenue" [ "sha256:fr" ] (Scalar 2m)

/// The producer's next version in its lineage: it supersedes the first.
let private producerV2 = {
    producer with
        Evidence = {
            producer.Evidence with
                InputHashes = [ "dov-in-2" ]
        }
}

let private ok (label: string) (r: Result<'T, FactStoreError>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %s" label (FactStoreError.describe e)

let private wholeStore (store: IFactStore) (scope: string) =
    store.Query(
        TestScope.resolved scope,
        {
            FactQuery.all with
                IncludeSuperseded = true
        }
    )
    |> TestScope.ok

/// The index `source` answers for `scope`, asserted equal to the rebuild.
let private agrees (h: TaintIndexHarness) (label: string) (scope: string) = async {
    let! index = h.Source.ReadTaintIndex(TestScope.resolved scope)
    let! all = wholeStore h.Store scope
    Expect.equal index (DisclosureTaintIndex.ofFacts all) label
    return index
}

let tests (name: string) (harness: unit -> TaintIndexHarness) =
    testList $"{name} — IDisclosureTaintIndexSource contract" [

        testCaseAsync "after every write the index equals the index rebuilt from the store"
        <| async {
            let h = harness ()
            let! empty = agrees h "an empty scope" h.ScopeA
            Expect.isEmpty empty.Nodes "an empty scope holds no node"

            let! p = h.Store.Assert(TestScope.resolved h.ScopeA, producer)
            let p = ok "producer" p
            let! _ = agrees h "after a scalar assert" h.ScopeA

            let! batch = h.Store.AssertBatch(TestScope.resolved h.ScopeA, [ derived; unrelated ])
            ok "batch" batch |> ignore
            let! _ = agrees h "after a batch" h.ScopeA

            let! v2 = h.Store.Assert(TestScope.resolved h.ScopeA, producerV2)
            ok "v2" v2 |> ignore
            let! afterSupersession = agrees h "after a supersession" h.ScopeA

            Expect.isSome
                (DisclosureTaintIndex.tryNode p.FactId afterSupersession)
                "the superseded producer is still a node: a derived fact may cite what it produced"

            let! again = h.Store.Assert(TestScope.resolved h.ScopeA, producer)
            ok "again" again |> ignore
            let! afterIdempotent = agrees h "after an idempotent assert" h.ScopeA
            Expect.equal afterIdempotent afterSupersession "an idempotent write moves no node"

            let! other = agrees h "another scope" h.ScopeB
            Expect.isEmpty other.Nodes "a write in one scope adds no node to another"
        }

        testCaseAsync "the index does not depend on the order the writes landed in"
        <| async {
            let h = harness ()
            let writes = [ producer; derived; unrelated; producerV2 ]

            for d in writes do
                let! r = h.Store.Assert(TestScope.resolved h.ScopeA, d)
                ok "forward" r |> ignore

            for d in List.rev writes do
                let! r = h.Store.Assert(TestScope.resolved h.ScopeB, d)
                ok "reverse" r |> ignore

            let! forward = agrees h "written forward" h.ScopeA
            let! reverse = agrees h "written in reverse" h.ScopeB

            Expect.equal forward reverse "the same facts in either order are the same index"
            Expect.equal (Map.count forward.Nodes) writes.Length "a node per stored fact"
        }

        testCaseAsync "reading again rebuilds nothing, and neither does a later write"
        <| async {
            let h = harness ()

            let rebuilds () =
                h.Rebuilds |> Option.map (fun read -> read ()) |> Option.defaultValue 0L

            let! batch = h.Store.AssertBatch(TestScope.resolved h.ScopeA, [ producer; derived ])
            ok "seed" batch |> ignore

            let before = rebuilds ()
            let! first = agrees h "the first read" h.ScopeA
            let afterFirst = rebuilds ()
            Expect.isLessThanOrEqual (afterFirst - before) 1L "the first read rebuilds at most once"

            let! second = agrees h "a second read" h.ScopeA
            Expect.equal second first "a second read answers the same index"

            let! r = h.Store.Assert(TestScope.resolved h.ScopeA, unrelated)
            ok "a later write" r |> ignore
            let! _ = agrees h "a read after a later write" h.ScopeA

            Expect.equal (rebuilds ()) afterFirst "neither a later read nor a later write rebuilt the index"
        }
    ]