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

/// A registry-backed BlobFactStore for the registry-directed half of the
/// population contract (Phase 701) — `RegistryDirection` ordering and the
/// D19 canonical selection are registry facts, so the registry-less
/// `factory` above cannot exhibit either.
let private registryFactory (registry: IMetricRegistry) : IFactStore * string * string =
    let store =
        BlobFactStore.createWithRegistry
            (InMemoryBlobStorage.InMemoryBlobStorage())
            (InMemoryEventStore.InMemoryEventStore())
            (Some registry)

    store, newScope (), newScope ()

/// The registry-directed population contract bound to BlobFactStore.
let populationRegistryTests =
    IFactStoreContract.populationRegistryTests "BlobFactStore" registryFactory

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

// ─── Population read at the requirement's cardinality (Phase 701) ────
//
// The population tier exists for a deployment tracking ~300,000 subjects
// with one metric each, so "interactive at that cardinality" is a claim
// that has to be MEASURED rather than asserted — and measured on the two
// halves separately, because they scale differently and only one of them
// is Phase 701's job.
//
//   - The **decidable pipeline** (subject predicate → threshold →
//     ranking → statistics) is pure, lives in `PopulationQueryTypes`, and
//     is shared verbatim by every implementation. It is exercised here at
//     the full 300,000, so the indexed read model built on top of it
//     inherits a measured floor rather than a hoped-for one.
//   - The **blob enumeration** in `BlobFactStore` is one full head scan
//     per question. It is correct at any size and, as the phase says,
//     efficient only at small — the second test measures the per-head
//     cost so the extrapolation to 300,000 is a number rather than an
//     adjective.
//
// Neither test asserts a wall-clock bound. A timing assertion is a bomb
// with a date on it — it passes on the machine that wrote it and fails on
// whichever runner is busiest. They measure, print, and assert only
// correctness.

let private baseAsOf = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)

let private syntheticEvidence: Evidence = {
    ResultRef = None
    InputHashes = [ "h" ]
    TriggerRef = None
}

let private syntheticMethod = Computed("rollup", "1", "p0")

let private syntheticFact (index: int) (value: FactValue) : Fact = {
    FactId = sprintf "f%08d" index
    Subject = {
        Hierarchy = "geography"
        Path = [ "eu"; sprintf "sku-%06d" index ]
    }
    Metric = MetricRef "elasticity"
    Value = value
    Period = q2
    AsOf = baseAsOf
    Method = syntheticMethod
    Evidence = syntheticEvidence
    Confidence = None
    Supersedes = None
    Disclosure = Disclosure.Surfaceable
}

/// The stated cardinality of the operator requirement this tier exists
/// for. `(i * 7) % PopulationSize` is a bijection over `0 .. size - 1`
/// (7 is coprime with the size), so the seeded values are a scrambled
/// permutation with exactly known extremes and mean — a measured sort
/// over unsorted input, not a rehearsal over a pre-ordered one.
[<Literal>]
let private PopulationSize = 300_000

/// Members carrying a queryable data gap — counted in the summary, never
/// ranked, and present so the non-comparable path is measured too.
[<Literal>]
let private AbsentMembers = 1_000

let populationScaleTests =
    testList "Phase 701 population read at scale" [

        test "the population pipeline ranks and summarises 300,000 subjects" {
            let build = Diagnostics.Stopwatch.StartNew()

            let population = [
                for i in 0 .. PopulationSize - 1 do
                    syntheticFact i (Scalar(decimal ((i * 7) % PopulationSize)))

                for i in PopulationSize .. PopulationSize + AbsentMembers - 1 do
                    syntheticFact i (Absent "no data loaded for this period")
            ]

            build.Stop()

            let stopwatch = Diagnostics.Stopwatch.StartNew()
            let ranked = PopulationRanking.rank HighestFirst population

            let stats =
                PopulationStats.ofPopulation (fun f -> Freshness.derive UntilSuperseded f true baseAsOf) population

            let top = ranked |> List.truncate PopulationQuery.MaxTopK
            stopwatch.Stop()

            printfn
                "Phase 701 scale: %d members (%d comparable) — seed %dms, rank+summarise %dms (%.2f us/member)"
                stats.FactCount
                stats.ComparableCount
                build.ElapsedMilliseconds
                stopwatch.ElapsedMilliseconds
                (float stopwatch.ElapsedMilliseconds * 1000.0 / float stats.FactCount)

            Expect.equal stats.FactCount (PopulationSize + AbsentMembers) "every seeded member is in the population"
            Expect.equal stats.SubjectCount (PopulationSize + AbsentMembers) "one subject each"
            Expect.equal stats.ComparableCount PopulationSize "the Absent members carry no magnitude"
            Expect.equal stats.NonComparableCount AbsentMembers "and are counted rather than dropped"
            Expect.equal stats.Minimum (Some 0m) "smallest of the permutation"
            Expect.equal stats.Maximum (Some(decimal (PopulationSize - 1))) "largest of the permutation"
            Expect.equal stats.Mean (Some 149999.5m) "exact mean of 0 .. 299,999"

            Expect.equal
                (top |> List.truncate 3 |> List.map _.Value)
                [ Scalar 299999m; Scalar 299998m; Scalar 299997m ]
                "the ceiling page is the true top of the population"

            Expect.equal (List.length top) PopulationQuery.MaxTopK "the ranking is bounded by the ceiling"

            Expect.equal
                stats.Freshness.FreshCount
                (PopulationSize + AbsentMembers)
                "current heads under UntilSuperseded are fresh"
        }

        testCaseAsync "a blob-backed population read is one enumeration over the scope's heads"
        <| async {
            // Deliberately modest: `Assert` re-enumerates the scope on
            // every call to derive the supersession edge, so SEEDING is
            // quadratic even though the READ is linear. The read is what
            // is being measured.
            let seedSize = 500
            let store, _ = newStore ()
            let scope = newScope ()

            let seeding = Diagnostics.Stopwatch.StartNew()

            for i in 0 .. seedSize - 1 do
                let! r =
                    store.Assert(
                        scope,
                        {
                            Subject = {
                                Hierarchy = "geography"
                                Path = [ "eu"; sprintf "sku-%06d" i ]
                            }
                            Metric = MetricRef "elasticity"
                            Value = Scalar(decimal ((i * 7) % seedSize))
                            Period = q2
                            Method = syntheticMethod
                            Evidence = syntheticEvidence
                            Confidence = None
                            Disclosure = Disclosure.Surfaceable
                        }
                    )

                match r with
                | Ok _ -> ()
                | Error e -> failtestf "seed %d failed: %s" i e

            seeding.Stop()

            let query = {
                PopulationQuery.create (MetricRef "elasticity") "geography" with
                    Level = Some 2
                    Ordering = Descending
                    TopK = 5
            }

            // Warm, then measure — the first read pays the deserialisation
            // of every blob for the first time.
            let! _ = store.QueryPopulation(scope, query)
            let reading = Diagnostics.Stopwatch.StartNew()
            let! r = store.QueryPopulation(scope, query)
            reading.Stop()

            match r with
            | Error e -> failtestf "population read refused: %s" e
            | Ok population ->
                printfn
                    "Phase 701 blob read: %d heads — seed %dms, read %dms (%.3f ms/head; ~%.1fs extrapolated to 300,000)"
                    population.Stats.FactCount
                    seeding.ElapsedMilliseconds
                    reading.ElapsedMilliseconds
                    (float reading.ElapsedMilliseconds / float seedSize)
                    (float reading.ElapsedMilliseconds / float seedSize * 300000.0 / 1000.0)

                Expect.equal population.Stats.FactCount seedSize "the whole seeded population"
                Expect.equal population.Stats.SubjectCount seedSize "one head per subject"

                Expect.equal
                    (population.Ranked |> List.map _.Value)
                    [
                        Scalar(decimal (seedSize - 1))
                        Scalar(decimal (seedSize - 2))
                        Scalar(decimal (seedSize - 3))
                        Scalar(decimal (seedSize - 4))
                        Scalar(decimal (seedSize - 5))
                    ]
                    "the top of the population, ordered"

                Expect.isTrue population.Truncated "the rest of the population stayed out of the answer"
        }
    ]

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