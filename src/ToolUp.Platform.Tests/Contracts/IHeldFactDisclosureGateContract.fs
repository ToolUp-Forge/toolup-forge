// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IHeldFactDisclosureGateContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts

// ─── IHeldFactDisclosureGate contract pack (Phase 1125) ──────────────
//
// Parametrised laws for any `IHeldFactDisclosureGate`. The held form's
// whole contract is that it judges as the id form judges, so every law is
// stated against the implementation's own id form over the same store:
//
//   1. Over stored facts handed in as a door reads them, the verdicts and
//      the audit rows are the id form's.
//   2. An id the caller holds no fact for is denied `unknown-fact`, exactly
//      as the id form denies an id the scope does not hold — including a
//      fact that lives in another scope, which the door's read never finds.
//   3. A held fact whose id is not named is not judged.
//
// The factory hands back a fresh harness, so concurrent runs cannot
// interfere.

/// What a binding supplies: the store and audit stream the gate reads and
/// writes, the id form the held form must agree with, the held form itself,
/// and a scope (by id, and as the resolved scope a request would carry).
type HeldGateHarness = {
    Store: IFactStore
    Events: IEventStore
    IdForm: IFactDisclosureGate
    Held: IHeldFactDisclosureGate
    ScopeId: string
    Scope: ResolvedScope
    /// Another scope over the same store, for the never-widens-scope law.
    OtherScope: ResolvedScope
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

let private assertFact (h: HeldGateHarness) (draft: FactDraft) : Async<Fact> = async {
    match! h.Store.Assert(h.ScopeId, draft) with
    | Ok fact -> return fact
    | Error e -> return failtestf "assert failed: %s" e
}

/// The id form's verdicts and audit rows, then the held form's over the
/// facts a door's own reads through `scope` return.
let private bothForms (h: HeldGateHarness) (scope: ResolvedScope) (ids: string list) = async {
    let rowsOf (rows: ModuleEvent list) =
        rows |> List.map (fun e -> e.EventType, e.Payload) |> List.sort

    let! before = h.Events.ReadBySource(scope.ScopeId, FactEvents.SourceModule)
    let! byId = h.IdForm.Check(scope, "user-1", FactRetrieval, ids)
    let! afterId = h.Events.ReadBySource(scope.ScopeId, FactEvents.SourceModule)

    let! read = ids |> List.map (fun factId -> h.Store.Get(scope, factId)) |> Async.Sequential
    let held = read |> Array.choose id |> List.ofArray

    let! byHeld = h.Held.CheckHeld(scope, "user-1", FactRetrieval, ids, held)
    let! afterHeld = h.Events.ReadBySource(scope.ScopeId, FactEvents.SourceModule)

    return byId, rowsOf (afterId |> List.skip before.Length), byHeld, rowsOf (afterHeld |> List.skip afterId.Length)
}

let tests (name: string) (factory: unit -> HeldGateHarness) =
    testList $"{name} — IHeldFactDisclosureGate contract" [

        testCaseAsync "the held form's verdicts and audit rows are the id form's"
        <| async {
            let h = factory ()
            let! surfaceable = assertFact h (draftWith "revenue" Surfaceable)
            let! internal' = assertFact h (draftWith "margin" Internal)
            let! restricted = assertFact h (draftWith "syndicated" (Restricted "vendor-terms"))
            let ids = [ surfaceable.FactId; internal'.FactId; restricted.FactId ]

            let! byId, idRows, byHeld, heldRows = bothForms h h.Scope ids

            Expect.equal byHeld byId "the same verdict for every id"
            Expect.equal heldRows idRows "the same audit rows"
            Expect.equal (byId.TryFind surfaceable.FactId) (Some FactDisclosable) "the oracle admits the open fact"
            Expect.isNonEmpty idRows "the oracle audited its denies, so the rows compared are not vacuous"
        }

        testCaseAsync "an id with no held fact is denied as an unknown id is"
        <| async {
            let h = factory ()
            let! byId, idRows, byHeld, heldRows = bothForms h h.Scope [ "no-such-fact" ]

            Expect.equal (byHeld.TryFind "no-such-fact") (Some(FactNotDisclosable "unknown-fact")) "denied, never open"
            Expect.equal byHeld byId "as the id form denies it"
            Expect.equal heldRows idRows "and audited as the id form audits it"
        }

        testCaseAsync "a fact of another scope is never found by the door, so the held form denies it as unknown"
        <| async {
            let h = factory ()
            let! fact = assertFact h (draftWith "revenue" Surfaceable)
            let! byId, _, byHeld, _ = bothForms h h.OtherScope [ fact.FactId ]

            Expect.equal (byHeld.TryFind fact.FactId) (Some(FactNotDisclosable "unknown-fact")) "never widens scope"
            Expect.equal byHeld byId "as the id form denies it"
        }

        testCaseAsync "a held fact whose id is not named is not judged"
        <| async {
            let h = factory ()
            let! named = assertFact h (draftWith "revenue" Surfaceable)
            let! extra = assertFact h (draftWith "margin" Surfaceable)

            let! verdicts = h.Held.CheckHeld(h.Scope, "user-1", FactRetrieval, [ named.FactId ], [ named; extra ])

            Expect.equal (verdicts |> Map.keys |> List.ofSeq) [ named.FactId ] "only the named id has a verdict"
        }
    ]