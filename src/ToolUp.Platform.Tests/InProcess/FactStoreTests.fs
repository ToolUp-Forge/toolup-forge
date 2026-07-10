module ToolUp.Platform.Tests.InProcess.FactStoreTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── BlobFactStore — IFactStore contract binding + audit / freshness ──
//
// Binds the `IFactStore` contract pack to the Phase 520 blob-backed
// default over an `InMemoryBlobStorage` + an `InMemoryEventStore`, then
// adds the impl-specific audit-emission and freshness-derivation tests
// the generic contract does not (audit capture is construction-specific).
// The fact store audits to `IEventStore` under the reserved `_facts`
// source module.

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private factory () : IFactStore * string * string =
    let store =
        BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore())

    store, newScope (), newScope ()

/// The generic contract pack bound to BlobFactStore.
let tests = IFactStoreContract.tests "BlobFactStore" factory

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private scalarDraft inputHash value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

let private newStore () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
    store, events

let auditAndFreshnessTests =
    testList "BlobFactStore audit + freshness (Phase 520)" [

        testCaseAsync "a new assertion emits a FactAsserted event under _facts (and no FactSuperseded when first)"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let! _ = store.Assert(scope, scalarDraft "hashA" 100m)
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            Expect.isTrue (rows |> List.exists (fun e -> e.EventType = FactEvents.AssertedType)) "FactAsserted emitted"

            Expect.isFalse
                (rows |> List.exists (fun e -> e.EventType = FactEvents.SupersededType))
                "no supersession on the first fact"
        }

        testCaseAsync "an idempotent re-assertion emits no further audit (no state change)"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let! _ = store.Assert(scope, scalarDraft "hashA" 100m)
            let! afterFirst = events.ReadBySource(scope, FactEvents.SourceModule)
            let! _ = store.Assert(scope, scalarDraft "hashA" 100m)
            let! afterSecond = events.ReadBySource(scope, FactEvents.SourceModule)

            Expect.equal afterSecond.Length afterFirst.Length "no new audit rows for an idempotent re-assert"
        }

        testCaseAsync "a superseding assertion emits FactSuperseded"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let! _ = store.Assert(scope, scalarDraft "hashA" 100m)
            let! _ = store.Assert(scope, scalarDraft "hashB" 110m)
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            Expect.isTrue
                (rows |> List.exists (fun e -> e.EventType = FactEvents.SupersededType))
                "FactSuperseded emitted on supersession"
        }

        testCaseAsync
            "human-asserted fact: re-asserting a different value supersedes within the principal's lineage (D18)"
        <| async {
            let store, _ = newStore ()
            let scope = newScope ()

            let haDraft value : FactDraft = {
                scalarDraft "ignored" value with
                    Method = HumanAsserted "cfo"
                    Evidence = {
                        ResultRef = None
                        InputHashes = []
                        TriggerRef = None
                    }
            }

            let! f1 = store.Assert(scope, haDraft 100m)
            let! f2 = store.Assert(scope, haDraft 200m)

            match f1, f2 with
            | Ok a, Ok b ->
                Expect.notEqual b.FactId a.FactId "different asserted value → new id"
                Expect.equal b.Supersedes (Some a.FactId) "supersedes within the principal's lineage"
            | _ -> failtest "both asserts should succeed"
        }

        // ─── Freshness derivation (pure; no stored flag — L1/D2) ───────

        test "Freshness.derive honours FreshFor / UntilSuperseded without storing a flag" {
            let asOf = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)

            let fact: Fact = {
                FactId = "x"
                Subject = { Hierarchy = "h"; Path = [] }
                Metric = MetricRef "m"
                Value = Scalar 1m
                Period = q2
                AsOf = asOf
                Method = Computed("o", "1", "p")
                Evidence = {
                    ResultRef = None
                    InputHashes = []
                    TriggerRef = None
                }
                Confidence = None
                Supersedes = None
                Disclosure = Disclosure.Surfaceable
            }

            let policy = FreshFor(TimeSpan.FromDays 1.0)
            Expect.equal (Freshness.derive policy fact true (asOf.AddHours 12.0)) Fresh "within window → Fresh"

            match Freshness.derive policy fact true (asOf.AddDays 2.0) with
            | Stale _ -> ()
            | Fresh -> failtest "past the window → Stale"

            // UntilSuperseded: fresh exactly while current.
            Expect.equal
                (Freshness.derive UntilSuperseded fact true (asOf.AddDays 999.0))
                Fresh
                "current → Fresh regardless of age"

            match Freshness.derive UntilSuperseded fact false (asOf.AddSeconds 1.0) with
            | Stale _ -> ()
            | Fresh -> failtest "superseded → Stale"
        }
    ]