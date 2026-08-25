module ToolUp.Platform.Tests.InProcess.FactStoreTests

open System
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Remoting.Json.SystemTextJson
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

// ─── The same contract, through the metric surface (Phase 702) ───────
//
// The surface is only *worth* consulting above a size a test suite cannot
// reach, so a threshold-respecting binding would exercise none of it. Both
// bindings below therefore run at `FactSurfaceOptions.always`, which puts
// the entire `IFactStore` contract — every point read, every population
// case, both halves — through the indexed path against the same
// assertions the enumerating bindings above satisfy.
//
// That is the phase's equivalence claim stated as coverage rather than as
// prose: not "we checked the two agree on one seeded population", but "the
// contract that defines what an `IFactStore` means holds through either
// read model". The dedicated byte-for-byte comparison further down is the
// complementary evidence — it compares the two paths' *results* directly,
// on shapes the contract does not enumerate.

let private surfaceFactory () : IFactStore * string * string =
    let store =
        BlobFactStore.createWithSurface
            (InMemoryBlobStorage.InMemoryBlobStorage())
            (InMemoryEventStore.InMemoryEventStore())
            None
            (fun () -> DateTime.UtcNow)
            FactSurfaceOptions.always

    store, newScope (), newScope ()

/// The generic contract pack bound to a surface-backed BlobFactStore.
let surfaceTests =
    IFactStoreContract.tests "BlobFactStore (metric surface)" surfaceFactory

let private surfaceRegistryFactory (registry: IMetricRegistry) : IFactStore * string * string =
    let store =
        BlobFactStore.createWithSurface
            (InMemoryBlobStorage.InMemoryBlobStorage())
            (InMemoryEventStore.InMemoryEventStore())
            (Some registry)
            (fun () -> DateTime.UtcNow)
            FactSurfaceOptions.always

    store, newScope (), newScope ()

/// The registry-directed population contract bound to a surface-backed
/// BlobFactStore.
let surfacePopulationRegistryTests =
    IFactStoreContract.populationRegistryTests "BlobFactStore (metric surface)" surfaceRegistryFactory

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

// ─── The metric surface (Phase 702) ──────────────────────────────────
//
// Phase 701 established the shape of the answer and measured the cost of
// producing it: 0.200 ms per head, one blob read and one JSON
// deserialisation each, which is a minute per question at the 300,000
// subjects the tier exists for. Phase 702's claim is that a derived
// current-heads projection answers the same questions with the same bytes
// and a bounded number of fact reads.
//
// "The same bytes" is checked three ways here, deliberately overlapping:
//
//   1. The whole `IFactStore` contract runs a second time through the
//      surface (`surfaceTests` / `surfacePopulationRegistryTests` above).
//   2. The two paths' `PopulationResult` values are compared directly,
//      over one seeded population and a matrix of query shapes — including
//      shapes the contract does not enumerate, and subject paths carrying
//      every character the projection's wire format escapes.
//   3. The projection's *maintenance* is exercised where it could silently
//      diverge: supersession, method competition, a neighbouring metric, a
//      fact written straight into the log behind the store's back, and a
//      flushed surface.
//
// Nothing below asserts a wall-clock bound. The scale test at the end
// measures and prints; a timing assertion is a bomb with a date on it.

/// An `IBlobStorage` decorator that counts the reads reaching the backing
/// store. The point of an indexed read model is that a question stops
/// costing one fact read per subject — a *structural* property, so it is
/// asserted structurally here rather than inferred from a clock.
type private CountingBlobStorage(inner: IBlobStorage) =
    let gate = obj ()
    let mutable downloads = 0

    member _.Downloads = lock gate (fun () -> downloads)

    member _.Reset() = lock gate (fun () -> downloads <- 0)

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            lock gate (fun () -> downloads <- downloads + 1)
            return! inner.Download(container, blobName)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// A deterministic, strictly-increasing clock. Transaction times that
/// cannot collide keep the supersession edges — and therefore both read
/// paths' view of which head is current — free of any dependence on how
/// fast the machine happened to run the seed.
let private steppingClock (start: DateTime) : unit -> DateTime =
    let current = ref start

    fun () ->
        let value = current.Value
        current.Value <- value.AddSeconds 1.0
        value

let private elasticityMetric (canonical: string option) : MetricDefinition = {
    Id = "elasticity"
    Name = "Elasticity"
    Unit = "ratio"
    Dimensionality = "ratio"
    Direction = HigherIsBetter
    DisplayFormat = "N2"
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = canonical
    RecomputePolicy = None
    RollUp = None
}

let private surfaceRegistry: IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "test"
            Definition = elasticityMetric (Some "computed:rollup")
        }
    ] []

let private draftAt
    (path: string list)
    (metric: string)
    (method': MethodRef)
    (value: FactValue)
    (inputHash: string)
    : FactDraft =
    {
        Subject = { Hierarchy = "geography"; Path = path }
        Metric = MetricRef metric
        Value = value
        Period = q2
        Method = method'
        Evidence = {
            ResultRef = None
            InputHashes = [ inputHash ]
            TriggerRef = None
        }
        Confidence = None
        Disclosure = Disclosure.Surfaceable
    }

let private rollup = Computed("rollup", "1", "p0")

let private elasticity = MetricRef "elasticity"

let private assertSeed (store: IFactStore) (scope: string) (d: FactDraft) = async {
    let! r = store.Assert(scope, d)

    match r with
    | Ok _ -> ()
    | Error e -> failtestf "seed failed: %s" e
}

let private storeOver (storage: IBlobStorage) (clock: unit -> DateTime) (options: FactSurfaceOptions) : IFactStore =
    BlobFactStore.createWithSurface
        storage
        (InMemoryEventStore.InMemoryEventStore())
        (Some surfaceRegistry)
        clock
        options

/// Seed one population carrying every shape the projection has to survive:
/// a supersession, a competing method, non-comparable values, a subject at
/// a different depth, a fact belonging to a NEIGHBOURING metric (a scope's
/// facts share one blob prefix, so a surface has to account for its
/// neighbours), and subject paths containing each character the wire
/// format escapes — including the empty segment, which is the one shape a
/// naive join-and-split round-trips wrongly.
let private seed702 (store: IFactStore) (scope: string) = async {
    for i in 0..5 do
        do!
            assertSeed
                store
                scope
                (draftAt [ "eu"; sprintf "sku-%06d" i ] "elasticity" rollup (Scalar(decimal (10 * i))) (sprintf "h%d" i))

    // A supersession: sku-000000's head is replaced, and the replaced fact
    // must never rank from either path.
    do! assertSeed store scope (draftAt [ "eu"; "sku-000000" ] "elasticity" rollup (Scalar 95m) "h0-v2")

    // A competing method on sku-000001 — never merged (D19).
    do! assertSeed store scope (draftAt [ "eu"; "sku-000001" ] "elasticity" (HumanAsserted "cfo") (Scalar 77m) "cfo-1")

    // Non-comparable shapes: counted in the summary, never ranked.
    do! assertSeed store scope (draftAt [ "eu"; "sku-000006" ] "elasticity" rollup (Absent "no data loaded") "h6")
    do! assertSeed store scope (draftAt [ "eu"; "sku-000007" ] "elasticity" rollup (Categorical "n/a") "h7")

    // A different depth — legal, rarely meaningful, and exactly what
    // `Level` exists to exclude.
    do! assertSeed store scope (draftAt [ "eu" ] "elasticity" rollup (Scalar 500m) "hroot")

    // A neighbouring metric in the same scope.
    do! assertSeed store scope (draftAt [ "eu"; "sku-000000" ] "revenue" rollup (Scalar 1234m) "rev0")

    // Every character the surface's wire format escapes, plus the empty
    // path segment.
    do! assertSeed store scope (draftAt [ "eu"; "we>ird\tta\\b\nnl" ] "elasticity" rollup (Scalar 42m) "hodd")
    do! assertSeed store scope (draftAt [ "eu"; "" ] "elasticity" rollup (Scalar 43m) "hempty")
}

/// The query matrix the two read models are compared over. Every clause
/// the population shape carries appears at least once, and the ordering
/// resolution appears in all three forms.
let private queryMatrix: (string * PopulationQuery) list =
    let baseQuery = PopulationQuery.create elasticity "geography"

    [
        "registry-directed, default top-k", { baseQuery with Level = Some 2 }
        "explicit descending",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
        }
        "explicit ascending",
        {
            baseQuery with
                Level = Some 2
                Ordering = Ascending
        }
        "every depth", { baseQuery with Ordering = Descending }
        "path prefix",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                PathPrefix = Some [ "eu" ]
        }
        "path prefix matching nothing",
        {
            baseQuery with
                Ordering = Descending
                PathPrefix = Some [ "apac" ]
        }
        "threshold AtLeast",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                Threshold = Some(AtLeast 30m)
        }
        "threshold Between",
        {
            baseQuery with
                Level = Some 2
                Ordering = Ascending
                Threshold = Some(Between(20m, 50m))
        }
        "threshold excluding everything",
        {
            baseQuery with
                Level = Some 2
                Ordering = Ascending
                Threshold = Some(AtMost -1m)
        }
        "top-k of one",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                TopK = 1
        }
        "top-k above the ceiling",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                TopK = PopulationQuery.MaxTopK * 4
        }
        "all competing methods",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                Methods = AllCompetingMethods
        }
        "one named method",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                Methods = OneMethod(HumanAsserted "cfo")
        }
        "period overlapping",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                PeriodOverlaps = Some q2
        }
        "period overlapping nothing",
        {
            baseQuery with
                Level = Some 2
                Ordering = Descending
                PeriodOverlaps =
                    Some {
                        From = DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        To = DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                        Label = None
                    }
        }
    ]

let private expectSameAnswers (label: string) (enumerating: IFactStore) (surfaced: IFactStore) (scope: string) = async {
    for name, query in queryMatrix do
        let! viaLog = enumerating.QueryPopulation(scope, query)
        let! viaSurface = surfaced.QueryPopulation(scope, query)

        Expect.equal
            viaSurface
            viaLog
            (sprintf "%s — '%s' must answer identically through either read model" label name)
}

let metricSurfaceTests =
    testList "Phase 702 metric surface" [

        testCaseAsync "the surface answers every population shape identically to the enumeration"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            // Seeded through the DISABLED store, so no surface exists when
            // the surfaced store is first asked: the first comparison
            // therefore also exercises the cold build from the log.
            do! seed702 enumerating scope
            do! expectSameAnswers "cold build" enumerating surfaced scope

            let! surfaceBlobs = storage.List(scope, FactSurface.Prefix)
            Expect.isNonEmpty surfaceBlobs "the read built and persisted a surface"

            do! expectSameAnswers "warm surface" enumerating surfaced scope
        }

        testCaseAsync "incremental maintenance survives supersession, competition and a neighbouring metric"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 surfaced scope

            // Build the surface, then keep asserting THROUGH the surfaced
            // store so every subsequent write takes the incremental
            // maintenance path rather than a rebuild.
            do! expectSameAnswers "after seeding through the surface" enumerating surfaced scope

            // A supersession: the replaced head must leave the projection.
            do! assertSeed surfaced scope (draftAt [ "eu"; "sku-000003" ] "elasticity" rollup (Scalar 999m) "h3-v2")
            do! expectSameAnswers "after supersession" enumerating surfaced scope

            // A competing method: a second row under one subject, never a
            // replacement (D19).
            do!
                assertSeed
                    surfaced
                    scope
                    (draftAt [ "eu"; "sku-000004" ] "elasticity" (HumanAsserted "cfo") (Scalar 61m) "cfo-4")

            do! expectSameAnswers "after competition" enumerating surfaced scope

            // A neighbouring metric's fact: absorbed by the census, never a
            // row in this metric's population.
            do! assertSeed surfaced scope (draftAt [ "eu"; "sku-000005" ] "revenue" rollup (Scalar 7m) "rev5")
            do! expectSameAnswers "after a neighbouring metric" enumerating surfaced scope

            // And a brand-new subject.
            do! assertSeed surfaced scope (draftAt [ "eu"; "sku-000009" ] "elasticity" rollup (Scalar 88m) "h9")
            do! expectSameAnswers "after a new subject" enumerating surfaced scope
        }

        testCaseAsync "dropping the surface is a cache flush — the next read rebuilds and re-verifies"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 surfaced scope
            do! expectSameAnswers "before the flush" enumerating surfaced scope

            do! FactSurface.drop storage scope
            let! afterDrop = storage.List(scope, FactSurface.Prefix)
            Expect.isEmpty afterDrop "the flush removed every surface in the scope"

            do! expectSameAnswers "after the flush" enumerating surfaced scope

            let! rebuilt = storage.List(scope, FactSurface.Prefix)
            Expect.isNonEmpty rebuilt "the next read rebuilt it"
        }

        testCaseAsync "a fact written straight into the log is reconciled, not missed"
        <| async {
            // The guarantee that makes every maintenance failure cost a
            // slower read rather than a different answer: the read census
            // is taken from the log, so a head the surface never heard
            // about is folded in before the question is answered.
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 surfaced scope
            do! expectSameAnswers "converged" enumerating surfaced scope

            // A second replica's write, or a restore: the fact reaches the
            // log without ever reaching this store's surface. Its
            // transaction time is in the read's past, as a real assertion's
            // would be — the FUTURE-stamped case is its own test below,
            // because it exercises the opposite branch.
            let smuggled = {
                syntheticFact 424242 (Scalar 777m) with
                    Subject = {
                        Hierarchy = "geography"
                        Path = [ "eu"; "sku-424242" ]
                    }
                    AsOf = baseAsOf
            }

            let payload =
                JsonSerializer.Serialize(smuggled, FableConverters.create ())
                |> Encoding.UTF8.GetBytes

            let! written = storage.Upload(scope, sprintf "_facts/%s.json" smuggled.FactId, payload)

            match written with
            | Error e -> failtestf "could not write the out-of-band fact: %s" e
            | Ok _ -> ()

            // Verify the probe, not just the verdict: were the hand-written
            // blob not in the store's own format, the reconcile would have
            // nothing to find and this test would pass vacuously.
            let! readBack = enumerating.Get(scope, smuggled.FactId)
            Expect.equal readBack (Some smuggled) "the out-of-band blob is readable as a fact by the store"

            do! expectSameAnswers "after an out-of-band write" enumerating surfaced scope

            let! r =
                surfaced.QueryPopulation(
                    scope,
                    {
                        PopulationQuery.create elasticity "geography" with
                            Level = Some 2
                    }
                )

            match r with
            | Error e -> failtestf "population read refused: %s" e
            | Ok population ->
                Expect.isTrue
                    (population.Ranked |> List.exists (fun f -> f.FactId = smuggled.FactId))
                    "the smuggled head ranks — the reconcile folded it in"
        }

        testCaseAsync "a head stamped in the future makes the surface decline rather than answer differently"
        <| async {
            // Found by this suite rather than reasoned out in advance, and
            // worth its own case for that reason. A fact whose transaction
            // time is ahead of the read's instant has not happened yet
            // under law L4, so the enumeration hides it — while a
            // heads-only projection has no notion of "not yet". The
            // projection cannot reproduce the enumeration's answer here (it
            // would need the head this one superseded, which it has
            // dropped), so it must decline the question, not approximate
            // it. Clock skew between replicas produces exactly this.
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 surfaced scope
            do! expectSameAnswers "converged" enumerating surfaced scope

            let ahead = {
                syntheticFact 999999 (Scalar 888m) with
                    Subject = {
                        Hierarchy = "geography"
                        Path = [ "eu"; "sku-999999" ]
                    }
                    AsOf = baseAsOf.AddDays 3650.0
            }

            let payload =
                JsonSerializer.Serialize(ahead, FableConverters.create ())
                |> Encoding.UTF8.GetBytes

            let! written = storage.Upload(scope, sprintf "_facts/%s.json" ahead.FactId, payload)

            match written with
            | Error e -> failtestf "could not write the future-dated fact: %s" e
            | Ok _ -> ()

            let! readBack = enumerating.Get(scope, ahead.FactId)
            Expect.equal readBack (Some ahead) "the future-dated blob is readable as a fact by the store"

            do! expectSameAnswers "with a future-dated head in the log" enumerating surfaced scope

            let! r =
                surfaced.QueryPopulation(
                    scope,
                    {
                        PopulationQuery.create elasticity "geography" with
                            Level = Some 2
                    }
                )

            match r with
            | Error e -> failtestf "population read refused: %s" e
            | Ok population ->
                Expect.isFalse
                    (population.Ranked |> List.exists (fun f -> f.FactId = ahead.FactId))
                    "a head that has not happened yet does not rank, through either read model"
        }

        testCaseAsync "below the fallback threshold no surface is built and the answer is unchanged"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled

            // A threshold far above this population: GP 13 — a small
            // deployment pays nothing, including in its blob layout.
            let gated =
                storeOver storage clock {
                    FactSurfaceOptions.defaults with
                        MinimumHeads = 100_000
                }

            let scope = newScope ()
            do! seed702 gated scope
            do! expectSameAnswers "below the threshold" enumerating gated scope

            let! surfaceBlobs = storage.List(scope, FactSurface.Prefix)
            Expect.isEmpty surfaceBlobs "no surface blob was written below the threshold"
        }

        testCaseAsync "an AsOf population read bypasses the surface and replays from the log"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            let! first = surfaced.Assert(scope, draftAt [ "eu"; "sku-000000" ] "elasticity" rollup (Scalar 5m) "v1")

            let firstFact =
                match first with
                | Ok f -> f
                | Error e -> failtestf "seed failed: %s" e

            do! assertSeed surfaced scope (draftAt [ "eu"; "sku-000000" ] "elasticity" rollup (Scalar 6m) "v2")

            // A replay instant between the two assertions: the head that
            // was current THEN is one the surface has already dropped, so
            // only the log can answer.
            let historical = {
                PopulationQuery.create elasticity "geography" with
                    Level = Some 2
                    Ordering = Descending
                    AsOf = Some(firstFact.AsOf.AddMilliseconds 1.0)
            }

            let! viaLog = enumerating.QueryPopulation(scope, historical)
            let! viaSurface = surfaced.QueryPopulation(scope, historical)
            Expect.equal viaSurface viaLog "an AsOf read is the same replay through either store"

            match viaSurface with
            | Error e -> failtestf "replay refused: %s" e
            | Ok population ->
                Expect.equal
                    (population.Ranked |> List.map _.Value)
                    [ Scalar 5m ]
                    "the replay ranks the head that was current then, which the surface no longer holds"
        }

        testCaseAsync "a population question stops costing one fact read per subject"
        <| async {
            // The structural form of the phase's claim. Wall-clock is
            // measured in the scale test below; what is ASSERTED here is
            // the thing that makes the clock behave — the number of fact
            // blobs a question reads stops tracking the population's size.
            let counting = CountingBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = counting :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()
            let seedSize = 300

            for i in 0 .. seedSize - 1 do
                do!
                    assertSeed
                        enumerating
                        scope
                        (draftAt
                            [ "eu"; sprintf "sku-%06d" i ]
                            "elasticity"
                            rollup
                            (Scalar(decimal ((i * 7) % seedSize)))
                            (sprintf "h%d" i))

            let query = {
                PopulationQuery.create elasticity "geography" with
                    Level = Some 2
                    Ordering = Descending
                    TopK = 5
            }

            // Warm the surface (the cold read is one enumeration plus a
            // write — the price of building it, paid once).
            let! _ = surfaced.QueryPopulation(scope, query)

            counting.Reset()
            let! viaSurface = surfaced.QueryPopulation(scope, query)
            let surfaceReads = counting.Downloads

            counting.Reset()
            let! viaLog = enumerating.QueryPopulation(scope, query)
            let logReads = counting.Downloads

            printfn
                "Phase 702 read cost: %d subjects — surface %d blob reads, enumeration %d blob reads (%.1fx fewer)"
                seedSize
                surfaceReads
                logReads
                (float logReads / float (max surfaceReads 1))

            Expect.equal viaSurface viaLog "and the cheaper read is the same answer"
            Expect.equal logReads seedSize "the enumeration reads every head"

            // One snapshot, plus the page the ranking returns. The bound is
            // the CONTRACT's ceiling, never the population's size.
            Expect.isLessThanOrEqual
                surfaceReads
                (1 + PopulationQuery.effectiveTopK query)
                "the surface reads one snapshot plus the returned page, and nothing else"
        }
    ]

// ─── The metric surface at the requirement's cardinality (Phase 702) ─
//
// Phase 701 measured its enumeration at 500 heads and extrapolated. The
// acceptance for this phase names 100,000, so this measures at 100,000 —
// both paths, on the same seeded store, in the same process, so the ratio
// is a comparison rather than two numbers from two occasions.
//
// The population is seeded by writing fact blobs directly in the store's
// own on-disk format rather than through `Assert`, because `Assert`
// re-enumerates the scope to derive each supersession edge and is
// therefore quadratic in the seed: 100,000 assertions is not a slow test,
// it is an impossible one. Writing the log directly is also exactly the
// situation a rebuild-from-the-append-only-log exists for — a restore, an
// import, a second replica. The seeded blobs are PROVEN readable by the
// store before anything is measured; a format that had drifted would
// otherwise make every number below a measurement of an empty store.

[<Literal>]
let private SurfaceScaleSize = 100_000

let private scaleFact (index: int) : Fact = {
    syntheticFact index (Scalar(decimal ((index * 7) % SurfaceScaleSize))) with
        Metric = MetricRef "elasticity"
}

let metricSurfaceScaleTests =
    testList "Phase 702 metric surface at scale" [

        testCaseAsync "a 100,000-subject population reads through the surface with a bounded number of fact reads"
        <| async {
            // Counted as well as timed. The wall-clock ratio below is
            // measured over an IN-MEMORY blob backend, where a "blob read"
            // is a dictionary lookup — which flatters the enumeration
            // enormously and is the opposite of the deployment this tier
            // exists for. Against object storage each of those reads is a
            // request; the read COUNT is therefore the claim that survives
            // a change of backend, and the clock is the weaker of the two
            // numbers here rather than the stronger.
            let counting = CountingBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = counting :> IBlobStorage
            let scope = newScope ()
            let json = FableConverters.create ()
            let seeding = Diagnostics.Stopwatch.StartNew()

            for i in 0 .. SurfaceScaleSize - 1 do
                let fact = scaleFact i
                let payload = JsonSerializer.Serialize(fact, json) |> Encoding.UTF8.GetBytes
                let! _ = storage.Upload(scope, sprintf "_facts/%s.json" fact.FactId, payload)
                ()

            seeding.Stop()

            let clock = steppingClock (baseAsOf.AddDays 1.0)
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.defaults

            // Verify the probe before trusting any verdict it produces.
            let probe = scaleFact 7
            let! readBack = enumerating.Get(scope, probe.FactId)
            Expect.equal readBack (Some probe) "the seeded blobs are in the store's own format"

            let query = {
                PopulationQuery.create elasticity "geography" with
                    Level = Some 2
                    Ordering = Descending
                    TopK = 10
            }

            counting.Reset()
            let enumerated = Diagnostics.Stopwatch.StartNew()
            let! viaLog = enumerating.QueryPopulation(scope, query)
            enumerated.Stop()
            let logReads = counting.Downloads

            let cold = Diagnostics.Stopwatch.StartNew()
            let! viaSurfaceCold = surfaced.QueryPopulation(scope, query)
            cold.Stop()

            counting.Reset()
            let warm = Diagnostics.Stopwatch.StartNew()
            let! viaSurfaceWarm = surfaced.QueryPopulation(scope, query)
            warm.Stop()
            let warmReads = counting.Downloads

            let perHead (ms: int64) = float ms / float SurfaceScaleSize

            printfn
                "Phase 702 scale: %d heads — seed %dms | enumeration %dms / %d blob reads (%.3f ms/head, ~%.1fs at 300,000) | surface cold %dms | surface warm %dms / %d blob reads (%.4f ms/head, ~%.2fs at 300,000) | %.1fx faster, %.0fx fewer reads"
                SurfaceScaleSize
                seeding.ElapsedMilliseconds
                enumerated.ElapsedMilliseconds
                logReads
                (perHead enumerated.ElapsedMilliseconds)
                (perHead enumerated.ElapsedMilliseconds * 300000.0 / 1000.0)
                cold.ElapsedMilliseconds
                warm.ElapsedMilliseconds
                warmReads
                (perHead warm.ElapsedMilliseconds)
                (perHead warm.ElapsedMilliseconds * 300000.0 / 1000.0)
                (float enumerated.ElapsedMilliseconds / float (max warm.ElapsedMilliseconds 1L))
                (float logReads / float (max warmReads 1))

            Expect.equal viaSurfaceCold viaLog "the cold surface build answers what the log does"
            Expect.equal viaSurfaceWarm viaLog "and so does the warm read"
            Expect.equal logReads SurfaceScaleSize "the enumeration reads every one of the 100,000 heads"

            Expect.isLessThanOrEqual
                warmReads
                (1 + PopulationQuery.effectiveTopK query)
                "and the surface reads one snapshot plus the page it returns — at a HUNDRED THOUSAND subjects"

            match viaLog with
            | Error e -> failtestf "population read refused: %s" e
            | Ok population ->
                Expect.equal population.Stats.FactCount SurfaceScaleSize "the whole seeded population was summarised"
                Expect.equal population.Stats.SubjectCount SurfaceScaleSize "one head per subject"

                Expect.equal
                    (population.Ranked |> List.truncate 3 |> List.map _.Value)
                    [
                        Scalar(decimal (SurfaceScaleSize - 1))
                        Scalar(decimal (SurfaceScaleSize - 2))
                        Scalar(decimal (SurfaceScaleSize - 3))
                    ]
                    "the page is the true top of the population"

                Expect.equal population.Stats.Minimum (Some 0m) "smallest of the permutation"
                Expect.isTrue population.Truncated "the rest of the population stayed out of the answer"
        }
    ]