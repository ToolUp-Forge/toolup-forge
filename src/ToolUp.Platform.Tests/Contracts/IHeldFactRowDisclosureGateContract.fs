// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IHeldFactRowDisclosureGateContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts

// ─── IHeldFactRowDisclosureGate contract pack (Phase 1126) ───────────
//
// Parametrised laws for any `IHeldFactRowDisclosureGate`. The row form's
// whole contract is that it judges as the id form judges, so every law is
// stated against the implementation's own id form over the same store:
//
//   1. Over the rows of stored facts, as a door reads them, the verdicts and
//      the audit rows are the id form's.
//   2. An id the caller holds no row for is denied `unknown-fact`, exactly
//      as the id form denies an id the scope does not hold.
//   3. A held row whose id is not named is not judged.
//
// The factory hands back a fresh harness, so concurrent runs cannot
// interfere.

/// What a binding supplies: the store and audit stream the gate reads and
/// writes, the id form the row form must agree with, the row form itself,
/// and a scope (by id, and as the resolved scope a request would carry).
type RowGateHarness = {
    Store: IFactStore
    Events: IEventStore
    IdForm: IFactDisclosureGate
    Rows: IHeldFactRowDisclosureGate
    ScopeId: string
    Scope: ResolvedScope
}

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draftWith (metric: string) (disclosure: Disclosure) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metric
    Value = Scalar 100m
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "hashA" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

let private assertFact (h: RowGateHarness) (draft: FactDraft) : Async<Fact> = async {
    match! h.Store.Assert(h.ScopeId, draft) with
    | Ok fact -> return fact
    | Error e -> return failtestf "assert failed: %s" e
}

/// The id form's verdicts and audit rows, then the row form's over the rows
/// of the facts a door's own reads return.
let private bothForms (h: RowGateHarness) (ids: string list) = async {
    let rowsOf (rows: ModuleEvent list) =
        rows |> List.map (fun e -> e.EventType, e.Payload) |> List.sort

    let! before = h.Events.ReadBySource(h.ScopeId, FactEvents.SourceModule)
    let! byId = h.IdForm.Check(h.Scope, "user-1", FactRetrieval, ids)
    let! afterId = h.Events.ReadBySource(h.ScopeId, FactEvents.SourceModule)

    let! read = ids |> List.map (fun factId -> h.Store.Get(h.Scope, factId)) |> Async.Sequential
    let held = read |> Array.choose id |> Array.map HeldFactRow.ofFact |> List.ofArray

    let! byRows = h.Rows.CheckHeldRows(h.Scope, "user-1", FactRetrieval, ids, held)
    let! afterRows = h.Events.ReadBySource(h.ScopeId, FactEvents.SourceModule)

    return byId, rowsOf (afterId |> List.skip before.Length), byRows, rowsOf (afterRows |> List.skip afterId.Length)
}

let tests (name: string) (factory: unit -> RowGateHarness) =
    testList $"{name} — IHeldFactRowDisclosureGate contract" [

        testCaseAsync "the row form's verdicts and audit rows are the id form's"
        <| async {
            let h = factory ()
            let! surfaceable = assertFact h (draftWith "revenue" Surfaceable)
            let! internal' = assertFact h (draftWith "margin" Internal)
            let! restricted = assertFact h (draftWith "syndicated" (Restricted "vendor-terms"))
            let ids = [ surfaceable.FactId; internal'.FactId; restricted.FactId ]

            let! byId, idRows, byRows, rowRows = bothForms h ids

            Expect.equal byRows byId "the same verdict for every id"
            Expect.equal rowRows idRows "the same audit rows"
            Expect.equal (byId.TryFind surfaceable.FactId) (Some FactDisclosable) "the oracle admits the open fact"
            Expect.isNonEmpty idRows "the oracle audited its denies, so the rows compared are not vacuous"
        }

        testCaseAsync "an id with no held row is denied as an unknown id is"
        <| async {
            let h = factory ()
            let! byId, idRows, byRows, rowRows = bothForms h [ "no-such-fact" ]

            Expect.equal (byRows.TryFind "no-such-fact") (Some(FactNotDisclosable "unknown-fact")) "denied, never open"
            Expect.equal byRows byId "as the id form denies it"
            Expect.equal rowRows idRows "and audited as the id form audits it"
        }

        testCaseAsync "a held row whose id is not named is not judged"
        <| async {
            let h = factory ()
            let! named = assertFact h (draftWith "revenue" Surfaceable)
            let! extra = assertFact h (draftWith "margin" Surfaceable)

            let! verdicts =
                h.Rows.CheckHeldRows(
                    h.Scope,
                    "user-1",
                    FactRetrieval,
                    [ named.FactId ],
                    [ HeldFactRow.ofFact named; HeldFactRow.ofFact extra ]
                )

            Expect.equal (verdicts |> Map.keys |> List.ofSeq) [ named.FactId ] "only the named id has a verdict"
        }
    ]