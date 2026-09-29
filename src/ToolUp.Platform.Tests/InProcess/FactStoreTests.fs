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

// ─── Batch assertion — the summarised audit (Phase 704) ──────────────
//
// The contract pack holds the *semantics* (idempotency, in-batch
// supersession, scalar equivalence, scope isolation, the malformed-batch
// refusal) against every implementation. What lives here is the half a
// generic contract cannot see: which audit rows a batch emits, and which
// it deliberately does not. Audit capture is construction-specific —
// these need the store's `IEventStore` in hand.

let private batchDrafts count =
    [ for i in 1..count -> scalarDraft (sprintf "hash-%d" i) (decimal (i * 10)) ]
    |> List.mapi (fun i d -> {
        d with
            Subject = {
                Hierarchy = "geography"
                Path = [ sprintf "m%d" i ]
            }
    })

let private factRows (rows: ModuleEvent list) =
    rows
    |> List.filter (fun e -> e.EventType = FactEvents.AssertedType || e.EventType = FactEvents.SupersededType)

let private batchRows (rows: ModuleEvent list) =
    rows |> List.filter (fun e -> e.EventType = FactEvents.BatchAssertedType)

/// The batch size the phase's acceptance names. Large enough that the
/// per-fact path's cost (one full log scan per assert) would be
/// quadratic, which is the whole reason this member exists.
[<Literal>]
let private AcceptanceBatchSize = 10_000

let batchAssertTests =
    testList "BlobFactStore batch assertion (Phase 704)" [

        // The acceptance sentence, run at the cardinality it names. The
        // "same store state as asserting each draft once" half is proved
        // by the contract pack's scalar/batch equivalence case at a size
        // where BOTH paths are runnable — 10,000 sequential `Assert`s each
        // re-scan the whole log, so the comparison at this size would
        // measure the enumeration, not the equivalence. What is measured
        // here is what only shows up at size: the counts, the census, and
        // that a re-run is genuinely a no-op rather than a cheap-looking
        // rewrite.
        testCaseAsync "a 10,000-draft batch asserted twice: 10,000 facts, two audit rows, 10,000 idempotent skips"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let drafts = [
                for i in 1..AcceptanceBatchSize ->
                    {
                        scalarDraft (sprintf "hash-%06d" i) (decimal i) with
                            Subject = {
                                Hierarchy = "geography"
                                Path = [ "eu"; sprintf "sku-%06d" i ]
                            }
                    }
            ]

            let firstRun = Diagnostics.Stopwatch.StartNew()
            let! r1 = store.AssertBatch(scope, drafts)
            firstRun.Stop()

            let secondRun = Diagnostics.Stopwatch.StartNew()
            let! r2 = store.AssertBatch(scope, drafts)
            secondRun.Stop()

            printfn
                "Phase 704: %d drafts asserted in %dms; the unchanged re-run in %dms"
                AcceptanceBatchSize
                firstRun.ElapsedMilliseconds
                secondRun.ElapsedMilliseconds

            match r1, r2 with
            | Ok first, Ok second ->
                Expect.equal first.AssertedCount AcceptanceBatchSize "every draft asserted the first time"
                Expect.equal first.IdempotentCount 0 "and none skipped"

                Expect.equal second.IdempotentCount AcceptanceBatchSize "the whole re-run is idempotent skips"
                Expect.equal second.AssertedCount 0 "no fact was written the second time"
                Expect.equal second.SupersedingCount 0 "and nothing superseded"

                let! stored = store.Query(scope, FactQuery.all)
                Expect.equal stored.Length AcceptanceBatchSize "the store holds one fact per draft, not two"

                let! rows = events.ReadBySource(scope, FactEvents.SourceModule)
                Expect.equal (List.length (batchRows rows)) 2 "one summarised audit row per batch"

                Expect.isEmpty
                    (factRows rows)
                    "and not 10,000 per-fact rows — that is the audit cost this member removes"
            | Error e, _
            | _, Error e -> failtestf "expected Ok from both batches, got %s" e
        }


        testCaseAsync "a batch emits ONE summarised row and no per-fact rows"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let! r = store.AssertBatch(scope, batchDrafts 20)

            match r with
            | Error e -> failtestf "expected Ok, got %s" e
            | Ok receipt ->
                let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                Expect.equal
                    (List.length (batchRows rows))
                    1
                    "one FactBatchAsserted row for the batch — not one per fact (GP 6, summarised)"

                Expect.isEmpty
                    (factRows rows)
                    "and none of the per-fact shape, which stays the scalar path's (task 704.C)"

                // The row carries the receipt the caller was told, so the
                // audit trail and the return value cannot disagree.
                let payload =
                    JsonSerializer.Deserialize<FactBatchAssertedEvent>(
                        (List.head (batchRows rows)).Payload,
                        FableConverters.create ()
                    )

                Expect.equal payload.Receipt.Digest receipt.Digest "the audited digest is the receipt's digest"
                Expect.equal payload.Receipt.DraftCount 20 "over all twenty drafts"
                Expect.equal payload.Receipt.AssertedCount 20 "all newly asserted"
        }

        testCaseAsync "the scalar path keeps the per-fact shape beside it"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let! _ = store.Assert(scope, scalarDraft "hashA" 100m)
            let! _ = store.Assert(scope, scalarDraft "hashB" 110m)
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            Expect.equal (List.length (factRows rows)) 3 "two FactAsserted plus the one FactSuperseded"
            Expect.isEmpty (batchRows rows) "and no batch row — Assert is a batch of one internally, not externally"
        }

        testCaseAsync "a superseding batch audits the supersession through the receipt, not through per-fact rows"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let first = batchDrafts 5
            let! _ = store.AssertBatch(scope, first)

            // Same subjects, changed inputs — every draft supersedes.
            let second =
                first
                |> List.mapi (fun i d -> {
                    d with
                        Evidence = {
                            d.Evidence with
                                InputHashes = [ sprintf "hash-%d-v2" i ]
                        }
                })

            let! r = store.AssertBatch(scope, second)

            match r with
            | Error e -> failtestf "expected Ok, got %s" e
            | Ok receipt ->
                Expect.equal receipt.SupersedingCount 5 "every draft superseded its predecessor"
                Expect.equal receipt.AssertedCount 0 "none of them was new to its lineage"

                let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                Expect.isEmpty
                    (factRows rows)
                    "no FactSuperseded rows — the supersession is attributable through the batch digest"

                Expect.equal (List.length (batchRows rows)) 2 "one summarised row per batch"
        }

        testCaseAsync "an all-idempotent batch still audits one row — 'it re-ran and nothing moved' is a claim"
        <| async {
            let store, events = newStore ()
            let scope = newScope ()

            let drafts = batchDrafts 4
            let! _ = store.AssertBatch(scope, drafts)
            let! r = store.AssertBatch(scope, drafts)

            match r with
            | Error e -> failtestf "expected Ok, got %s" e
            | Ok receipt ->
                Expect.equal receipt.IdempotentCount 4 "nothing was written"

                let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                // Deliberately UNLIKE the scalar idempotent re-assert,
                // which audits nothing: a scalar caller learns "no state
                // changed" from its own return value, while a
                // population-scale producer needs durable evidence that
                // the run happened and found nothing to do.
                Expect.equal (List.length (batchRows rows)) 2 "the second, empty-handed batch is audited too"
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
    let mutable lists = 0
    let mutable surfaceDownloads = 0

    member _.Downloads = lock gate (fun () -> downloads)

    /// Downloads of a metric-surface snapshot (Phase 891) — the read the
    /// parse cache exists to avoid. Also counted in `Downloads`.
    member _.SurfaceDownloads = lock gate (fun () -> surfaceDownloads)

    /// `List` calls since the last reset (Phase 890) — a listing returns
    /// names only, so it is counted apart from the reads that fetch a blob.
    member _.Lists = lock gate (fun () -> lists)

    member _.Reset() =
        lock gate (fun () ->
            downloads <- 0
            lists <- 0
            surfaceDownloads <- 0)

    interface IBlobStorage with
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            lock gate (fun () ->
                downloads <- downloads + 1

                if blobName.StartsWith(FactSurface.Prefix, StringComparison.Ordinal) then
                    surfaceDownloads <- surfaceDownloads + 1)

            return! inner.Download(container, blobName)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)

        member _.List(container, prefix) = async {
            lock gate (fun () -> lists <- lists + 1)
            return! inner.List(container, prefix)
        }

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
    Context = None
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

// ─── Phase 891 — the surface stops growing with history ─────────────
//
// **Measured first**, before any change, at 100,000 heads of one metric
// seeded straight into the log (loaded machine, in-memory backend): the
// census listed 100,000 entries and the snapshot was 13,288,933 bytes;
// writing a second metric of 100,000 facts took the census to 200,000 and
// the elasticity snapshot to 14,288,938 bytes — a megabyte of absorbed ids
// for a metric whose rows had not changed (ten bytes an id here; a real
// content-addressed id is ~68). A superseding batch folded fact by fact
// over those 100,000 rows cost 3,986 / 7,986 / 16,297 / 33,490 ms for
// 500 / 1,000 / 2,000 / 4,000 facts: 8.2 ms a fact, linear in the rows per
// fact, so quadratic in a refresh — some fourteen minutes for a batch
// restating every subject.
//
// The cases below hold the after-shape structurally: row visits rather
// than a clock, blob sizes rather than an estimate, snapshot downloads
// rather than a latency.

let private emptySnapshot (width: int) : FactSurfaceSnapshot = {
    Metric = "elasticity"
    Stale = false
    Rows = []
    Census = FactCensus.empty width
}

/// A head per index, under `metric`, at a distinct transaction time.
let private headAt (metric: string) (index: int) : Fact = {
    syntheticFact index (Scalar(decimal index)) with
        Metric = MetricRef metric
        AsOf = baseAsOf.AddSeconds(float index)
}

/// A fact superseding `prior`, later than it.
let private successorOf (index: int) (prior: Fact) : Fact = {
    syntheticFact index (Scalar(decimal index)) with
        Subject = prior.Subject
        Metric = prior.Metric
        AsOf = prior.AsOf.AddDays 1.0
        Supersedes = Some prior.FactId
}

let private surfaceBlobSize (storage: IBlobStorage) (scope: string) = async {
    let! r = storage.Download(scope, FactSurface.blobName "elasticity")

    return
        match r with
        | Ok bytes -> bytes.Length
        | Error e -> failtestf "no surface blob: %s" e
}

let private writeRaw (storage: IBlobStorage) (scope: string) (fact: Fact) = async {
    let payload =
        JsonSerializer.Serialize(fact, FableConverters.create ())
        |> Encoding.UTF8.GetBytes

    let! written = storage.Upload(scope, sprintf "_facts/%s.json" fact.FactId, payload)

    match written with
    | Error e -> failtestf "could not write %s: %s" fact.FactId e
    | Ok _ -> ()
}

let private levelTwo = {
    PopulationQuery.create elasticity "geography" with
        Level = Some 2
        Ordering = Descending
        TopK = 5
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

        testCase "a superseding batch folds in row visits linear in the batch"
        <| fun () ->
            // 20,000 heads, then 20,000 facts superseding every one of
            // them. Folded fact by fact that is 20,000 walks of 20,000
            // rows — 400 million visits, and three times that counting the
            // two `List.length` calls the old fold made per supersession.
            let size = 20_000
            let heads = [ for i in 0 .. size - 1 -> headAt "elasticity" i ]

            let seeded, seedVisits =
                FactSurfaceFold.applyFacts "elasticity" heads (emptySnapshot 64)

            Expect.equal (List.length seeded.Rows) size "every head is a row"
            Expect.equal seedVisits size "adding heads visits each added row once"

            let successors = heads |> List.mapi (fun i h -> successorOf (size + i) h)

            let clock = Diagnostics.Stopwatch.StartNew()
            let refreshed, visits = FactSurfaceFold.applyFacts "elasticity" successors seeded
            clock.Stop()

            printfn
                "Phase 891 fold: %d superseding facts over %d rows — %d row visits in %dms (fact-by-fact: %d)"
                size
                size
                visits
                clock.ElapsedMilliseconds
                (3 * size * size)

            Expect.isLessThanOrEqual visits (3 * size) "row visits are linear in the batch, not quadratic"

            Expect.equal
                (refreshed.Rows |> List.map _.Member.FactId |> Set.ofList)
                (successors |> List.map _.FactId |> Set.ofList)
                "every superseded head left the rows and every successor joined them"

            Expect.equal refreshed.Census.Count (2 * size) "the census counts the superseded heads too"

        testCase "the batch fold is exactly the fact-by-fact fold, row order and census included"
        <| fun () ->
            let heads = [ for i in 0..39 -> headAt "elasticity" i ]
            let neighbours = [ for i in 100..109 -> headAt "revenue" i ]

            let start =
                FactSurfaceFold.applyFacts "elasticity" (heads @ neighbours) (emptySnapshot 16)
                |> fst

            let added = headAt "elasticity" 200
            let addedThenRetired = successorOf 201 added
            let retiredAgain = successorOf 202 addedThenRetired

            let batch = [
                successorOf 300 heads[3]
                headAt "revenue" 301
                added
                successorOf 302 heads[7]
                addedThenRetired
                {
                    headAt "elasticity" 303 with
                        Subject = heads[9].Subject
                        Method = HumanAsserted "cfo"
                }
                retiredAgain
                successorOf 304 neighbours[2]
                // Supersedes an id the snapshot never held as a row.
                {
                    headAt "elasticity" 305 with
                        Supersedes = Some "no-such-fact"
                }
            ]

            let batched, _ = FactSurfaceFold.applyFacts "elasticity" batch start

            let oneByOne =
                batch
                |> List.fold (fun acc f -> FactSurfaceFold.applyFact "elasticity" f acc) start

            Expect.equal batched.Rows oneByOne.Rows "the same rows, in the same order"
            Expect.equal batched.Census oneByOne.Census "the same census"
            Expect.equal batched oneByOne "the same snapshot"

        testCase "the census decodes a small difference and refuses what it cannot vouch for"
        <| fun () ->
            let ids = [ for i in 0..999 -> sprintf "fact-%04d" i ]
            let census = FactCensus.ofIds (FactCensus.widthFor 32) ids

            Expect.equal
                (FactCensus.valueOf census)
                (FactCensus.valueOfIds ids)
                "the table and the bare value agree on the same ids"

            let fresh = [ for i in 0..19 -> sprintf "new-%02d" i ]

            match FactCensus.unseen census (ids @ fresh) with
            | Ok missing -> Expect.equal (Set.ofList missing) (Set.ofList fresh) "exactly the new ids decode"
            | Error e -> failtestf "a 20-id difference at capacity 32 should decode: %A" e

            match FactCensus.unseen census ids with
            | Ok missing -> Expect.isEmpty missing "a converged census has nothing unseen"
            | Error e -> failtestf "no difference should decode trivially: %A" e

            let tooMany = [ for i in 0..499 -> sprintf "bulk-%03d" i ]

            match FactCensus.unseen census (ids @ tooMany) with
            | Error FactCensus.Undecodable -> ()
            | other -> failtestf "500 new ids cannot decode at capacity 32, got %A" other

            // An erasure that an addition hides from the count: 999 + 1.
            let erasedOne = (ids |> List.filter ((<>) "fact-0500")) @ [ "replacement" ]

            Expect.equal (FactCensus.valueOfIds erasedOne).Count census.Count "the count alone cannot see this shape"

            Expect.notEqual (FactCensus.valueOfIds erasedOne) (FactCensus.valueOf census) "the digest can"

            match FactCensus.unseen census erasedOne with
            | Error(FactCensus.Departed 1) -> ()
            | other -> failtestf "one departed id must be reported, got %A" other

        testCaseAsync "a snapshot's size does not grow when another metric in the scope is refreshed"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled

            // Capacity for the whole refresh below, so the neighbour's 400
            // facts are folded incrementally — the path that used to append
            // each one's id to this snapshot.
            let surfaced =
                storeOver storage clock {
                    FactSurfaceOptions.always with
                        MaxIncrementalFold = 512
                }

            let scope = newScope ()

            for i in 0..199 do
                do!
                    assertSeed
                        surfaced
                        scope
                        (draftAt
                            [ "eu"; sprintf "sku-%06d" i ]
                            "elasticity"
                            rollup
                            (Scalar(decimal i))
                            (sprintf "e%d" i))

            do! expectSameAnswers "seeded" enumerating surfaced scope
            let! before = surfaceBlobSize storage scope

            // Refresh a neighbouring metric twice over, every restatement
            // superseding the last — the history the id list used to carry.
            for round in 0..1 do
                for i in 0..199 do
                    do!
                        assertSeed
                            surfaced
                            scope
                            (draftAt
                                [ "eu"; sprintf "sku-%06d" i ]
                                "revenue"
                                rollup
                                (Scalar(decimal (i + round)))
                                (sprintf "r%d-%d" round i))

            do! expectSameAnswers "after the neighbour's refresh" enumerating surfaced scope
            let! after = surfaceBlobSize storage scope

            printfn "Phase 891 snapshot: %d bytes before a neighbouring refresh of 400 facts, %d after" before after
            Expect.equal after before "the snapshot is a function of its own heads, not of the scope's history"
        }

        testCaseAsync "a second population question against an unchanged store downloads no snapshot"
        <| async {
            let counting = CountingBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = counting :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 enumerating scope

            // Built cold (a rebuild), then asked again.
            let! first = surfaced.QueryPopulation(scope, levelTwo)
            counting.Reset()
            let! second = surfaced.QueryPopulation(scope, levelTwo)
            let snapshotReads = counting.SurfaceDownloads

            // Verify the probe: the same question through a fresh store —
            // a cold cache over the same blob — DOES download the snapshot.
            let fresh = storeOver storage clock FactSurfaceOptions.always
            counting.Reset()
            let! third = fresh.QueryPopulation(scope, levelTwo)
            let coldReads = counting.SurfaceDownloads

            Expect.equal second first "the cached answer is the same answer"
            Expect.equal third first "and so is a cold cache's"
            Expect.equal snapshotReads 0 "a warm cache over an unchanged log reads no snapshot"
            Expect.equal coldReads 1 "a cold cache reads the snapshot blob exactly as before"
            do! expectSameAnswers "cached" enumerating surfaced scope
        }

        testCaseAsync "a second replica writing behind a warm cache is folded in before the next answer"
        <| async {
            let counting = CountingBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = counting :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let replicaA = storeOver storage clock FactSurfaceOptions.always
            let replicaB = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 replicaA scope
            do! expectSameAnswers "replica A warm" enumerating replicaA scope

            // Replica B writes through its OWN maintenance — a supersession
            // and a new subject — and then a fact reaches the log through
            // neither replica.
            do! assertSeed replicaB scope (draftAt [ "eu"; "sku-000002" ] "elasticity" rollup (Scalar 612m) "b-v2")
            do! assertSeed replicaB scope (draftAt [ "eu"; "sku-000077" ] "elasticity" rollup (Scalar 611m) "b-77")

            let smuggled = {
                syntheticFact 777777 (Scalar 613m) with
                    Subject = {
                        Hierarchy = "geography"
                        Path = [ "eu"; "sku-777777" ]
                    }
                    AsOf = baseAsOf
            }

            do! writeRaw storage scope smuggled

            counting.Reset()
            let! viaA = replicaA.QueryPopulation(scope, levelTwo)
            let! viaLog = enumerating.QueryPopulation(scope, levelTwo)

            Expect.equal viaA viaLog "replica A answers what the log says, not what its cache held"
            Expect.isGreaterThanOrEqual counting.SurfaceDownloads 1 "the moved census sent replica A to the blob"

            match viaA with
            | Error e -> failtestf "population read refused: %s" e
            | Ok population ->
                let values = population.Ranked |> List.map _.Value

                Expect.containsAll
                    values
                    [ Scalar 613m; Scalar 612m; Scalar 611m ]
                    "both replicas' writes and the out-of-band one rank"

            do! expectSameAnswers "replica A after replica B" enumerating replicaA scope
        }

        testCaseAsync "an erasure is rebuilt from the log, with or without FactSurface.drop"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 surfaced scope
            do! assertSeed surfaced scope (draftAt [ "eu"; "sku-000040" ] "elasticity" rollup (Scalar 401m) "e40-v1")

            let! latest =
                surfaced.Assert(scope, draftAt [ "eu"; "sku-000040" ] "elasticity" rollup (Scalar 402m) "e40-v2")

            let erased =
                match latest with
                | Ok f -> f
                | Error e -> failtestf "seed failed: %s" e

            do! expectSameAnswers "before the erasure" enumerating surfaced scope

            // The shape the pre-891 reconcile could not see: one fact
            // erased and another added, so the census COUNT is unchanged.
            let! deleted = storage.Delete(scope, sprintf "_facts/%s.json" erased.FactId)

            match deleted with
            | Error e -> failtestf "could not erase: %s" e
            | Ok _ -> ()

            do!
                writeRaw storage scope {
                    headAt "elasticity" 404040 with
                        AsOf = baseAsOf
                }

            // Verify the probe: the erased head is gone from the log, so the
            // enumeration ranks its predecessor again.
            let! gone = enumerating.Get(scope, erased.FactId)
            Expect.isNone gone "the erased fact is no longer readable"

            do! expectSameAnswers "after an erasure, without a drop" enumerating surfaced scope

            let! afterErasure = surfaced.QueryPopulation(scope, levelTwo)

            match afterErasure with
            | Error e -> failtestf "population read refused: %s" e
            | Ok population ->
                Expect.isFalse
                    (population.Ranked |> List.exists (fun f -> f.FactId = erased.FactId))
                    "the erased head never ranks"

            // And the operator's flush still does what it always did.
            do! FactSurface.drop storage scope
            do! expectSameAnswers "after the erasure and a drop" enumerating surfaced scope
            let! rebuilt = storage.List(scope, FactSurface.Prefix)
            Expect.isNonEmpty rebuilt "the next read rebuilt the surface"
        }

        testCaseAsync "a version-1 snapshot reads as no surface and is rebuilt"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let clock = steppingClock baseAsOf
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.always
            let scope = newScope ()

            do! seed702 enumerating scope

            // A pre-891 snapshot: its header and one absorbed id, and no rows.
            let legacy =
                Encoding.UTF8.GetBytes(sprintf "%s\t1\telasticity\t0\t1\t0\nf00000001\n" FactSurfaceCodec.Magic)

            let! _ = storage.Upload(scope, FactSurface.blobName "elasticity", legacy)

            do! expectSameAnswers "over a version-1 snapshot" enumerating surfaced scope

            let! r = storage.Download(scope, FactSurface.blobName "elasticity")

            match r with
            | Error e -> failtestf "no snapshot after the read: %s" e
            | Ok bytes ->
                let header = Encoding.UTF8.GetString(bytes).Split('\n')[0]

                Expect.stringStarts
                    header
                    (sprintf "%s\t%d\t" FactSurfaceCodec.Magic FactSurfaceCodec.Version)
                    "the read rebuilt it in the current format"
        }

        testCase "the parse cache is bounded in entries and bytes and evicts oldest first"
        <| fun () ->
            let snapshotOf (rows: int) =
                FactSurfaceFold.applyFacts
                    "elasticity"
                    [ for i in 0 .. rows - 1 -> headAt "elasticity" i ]
                    (emptySnapshot 8)
                |> fst

            let small = snapshotOf 1
            let weight = FactSurfaceCodec.estimatedBytes small

            let byEntries = FactSurfaceCache(2, Int64.MaxValue)
            byEntries.Store("s", "a", small)
            byEntries.Store("s", "b", small)
            byEntries.Store("s", "c", small)
            let census = FactCensus.valueOf small.Census

            Expect.equal byEntries.Count 2 "never more than the entry bound"
            Expect.isNone (byEntries.TryGet("s", "a", census)) "the oldest went first"
            Expect.isSome (byEntries.TryGet("s", "c", census)) "the newest stayed"

            let byBytes = FactSurfaceCache(100, 2L * weight)
            byBytes.Store("s", "a", small)
            byBytes.Store("s", "b", small)
            byBytes.Store("s", "c", small)

            Expect.isLessThanOrEqual byBytes.Bytes (2L * weight) "never more than the byte bound"
            Expect.isNone (byBytes.TryGet("s", "a", census)) "the oldest went first"

            let large = snapshotOf 50
            byBytes.Store("s", "big", large)

            Expect.isNone
                (byBytes.TryGet("s", "big", FactCensus.valueOf large.Census))
                "an entry over the budget is never admitted"

            let other = FactCensus.valueOf (snapshotOf 2).Census
            Expect.isNone (byEntries.TryGet("s", "c", other)) "a different census is a miss"
            Expect.isNone (byEntries.TryGet("s", "c", census)) "and the out-of-date entry was dropped"

            byEntries.Store("s", "d", { small with Stale = true })
            Expect.isNone (byEntries.TryGet("s", "d", census)) "a stale snapshot is never cached"

        testCase "the parse cache does not exist when the surface is disabled"
        <| fun () ->
            let over options =
                BlobFactStore(
                    InMemoryBlobStorage.InMemoryBlobStorage(),
                    InMemoryEventStore.InMemoryEventStore(),
                    None,
                    (fun () -> DateTime.UtcNow),
                    options
                )

            Expect.isNone (over FactSurfaceOptions.disabled).SurfaceCache "off when the surface is"
            Expect.isSome (over FactSurfaceOptions.defaults).SurfaceCache "on when it is not"
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

        testCaseAsync
            "at 100,000 heads the snapshot ignores a neighbour, the fold is linear and a repeat read parses nothing"
        <| async {
            // Phase 891's measure-first run, repeated after the change on
            // the same shape (the before-figures are in the note above
            // `metricSurfaceTests`' Phase 891 cases). Counted as well as
            // timed; only the counts are asserted.
            let counting = CountingBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = counting :> IBlobStorage
            let scope = newScope ()

            for i in 0 .. SurfaceScaleSize - 1 do
                do! writeRaw storage scope (scaleFact i)

            let clock = steppingClock (baseAsOf.AddDays 1.0)
            let enumerating = storeOver storage clock FactSurfaceOptions.disabled
            let surfaced = storeOver storage clock FactSurfaceOptions.defaults

            let probe = scaleFact 7
            let! readBack = enumerating.Get(scope, probe.FactId)
            Expect.equal readBack (Some probe) "the seeded blobs are in the store's own format"

            let query = {
                PopulationQuery.create elasticity "geography" with
                    Level = Some 2
                    Ordering = Descending
                    TopK = 10
            }

            let! viaLog = enumerating.QueryPopulation(scope, query)
            let! cold = surfaced.QueryPopulation(scope, query)
            let! census = storage.List(scope, "_facts/")
            let! sizeAlone = surfaceBlobSize storage scope

            counting.Reset()
            let warmClock = Diagnostics.Stopwatch.StartNew()
            let! warm = surfaced.QueryPopulation(scope, query)
            warmClock.Stop()
            let warmSnapshotReads = counting.SurfaceDownloads

            // The same read with no cache — a fresh store over the same
            // converged blob — is the pre-891 read path: download and parse.
            let uncachedClock = Diagnostics.Stopwatch.StartNew()
            let! uncached = (storeOver storage clock FactSurfaceOptions.defaults).QueryPopulation(scope, query)
            uncachedClock.Stop()

            // What the census check costs a read: one key per listed id.
            let ids = census |> List.map (fun n -> n.Substring(7, n.Length - 12))
            let digestClock = Diagnostics.Stopwatch.StartNew()
            let value = FactCensus.valueOfIds ids
            digestClock.Stop()
            Expect.equal value.Count SurfaceScaleSize "the digest covered the whole census"

            // A neighbouring metric of the same size, written into the log.
            for i in 0 .. SurfaceScaleSize - 1 do
                do!
                    writeRaw storage scope {
                        scaleFact (SurfaceScaleSize + i) with
                            Metric = MetricRef "revenue"
                    }

            let! viaLogAfter = enumerating.QueryPopulation(scope, query)
            let! afterNeighbour = surfaced.QueryPopulation(scope, query)
            let! censusAfter = storage.List(scope, "_facts/")
            let! sizeWithNeighbour = surfaceBlobSize storage scope

            // A refresh restating every subject, folded into the snapshot.
            let! blob = storage.Download(scope, FactSurface.blobName "elasticity")

            let snapshot =
                match blob |> Result.toOption |> Option.bind FactSurfaceCodec.decode with
                | Some s -> s
                | None -> failtest "the snapshot did not read back"

            let successors =
                snapshot.Rows
                |> List.mapi (fun i row -> {
                    scaleFact (2 * SurfaceScaleSize + i) with
                        Subject = row.Member.Subject
                        AsOf = row.Member.AsOf.AddDays 1.0
                        Supersedes = Some row.Member.FactId
                })

            let foldClock = Diagnostics.Stopwatch.StartNew()
            let refreshed, visits = FactSurfaceFold.applyFacts "elasticity" successors snapshot
            foldClock.Stop()

            printfn
                "Phase 891 scale (loaded machine): %d heads — census %d entries, snapshot %d bytes; with a %d-fact neighbour census %d entries, snapshot %d bytes | read without the cache %dms, cached %dms with %d snapshot downloads | census digest %dms | superseding fold of %d over %d rows %dms, %d row visits"
                SurfaceScaleSize
                (List.length census)
                sizeAlone
                SurfaceScaleSize
                (List.length censusAfter)
                sizeWithNeighbour
                uncachedClock.ElapsedMilliseconds
                warmClock.ElapsedMilliseconds
                warmSnapshotReads
                digestClock.ElapsedMilliseconds
                (List.length successors)
                (List.length snapshot.Rows)
                foldClock.ElapsedMilliseconds
                visits

            Expect.equal cold viaLog "the cold surface answers what the log does"
            Expect.equal warm viaLog "and so does the cached read"
            Expect.equal uncached viaLog "and a read with no cache at all"
            Expect.equal afterNeighbour viaLogAfter "and the read after the neighbour's arrival"
            Expect.equal warmSnapshotReads 0 "a repeat question over an unchanged log downloads no snapshot"
            Expect.equal sizeWithNeighbour sizeAlone "a neighbour of 100,000 facts adds nothing to this snapshot"
            Expect.equal (List.length refreshed.Rows) SurfaceScaleSize "the refresh replaced every head"
            Expect.isLessThanOrEqual visits (3 * SurfaceScaleSize) "in row visits linear in the batch"
        }

    ]

// ─── Phase 890 — the point-read index ────────────────────────────────
//
// **Measured first**, before the index existed, over the seed below
// (10,000 facts: 1,000 subjects × 10 quarters of one metric) through the
// counting decorator: one point read (subject, metric, period) cost 10,000
// downloads and 1 list; `QuerySupersessionChain` 10,001 downloads and 1
// list; one `Assert` superseding a fact 10,001 downloads and 2 lists. The
// tests below hold the after-shape structurally — counts, never a clock —
// and compare it across two scope sizes, because "does not depend on the
// scope's size" is a claim about a slope, not a number.
//
// The whole `IFactStore` contract is bound once more with the index forced
// on (`FactIndexOptions.always`), as Phase 702 did for the surface, so every
// contract case runs through the indexed path; the direct comparison below
// then holds the two paths' answers equal on shapes the contract does not
// enumerate.

let private indexFactory () : IFactStore * string * string =
    let store =
        BlobFactStore.createWithIndex
            (InMemoryBlobStorage.InMemoryBlobStorage())
            (InMemoryEventStore.InMemoryEventStore())
            None
            (fun () -> DateTime.UtcNow)
            FactSurfaceOptions.defaults
            FactIndexOptions.always

    store, newScope (), newScope ()

/// The generic contract pack bound to a store with the point-read index
/// forced on at every size.
let indexTests =
    IFactStoreContract.tests "BlobFactStore (point-read index)" indexFactory

let private indexRegistryFactory (registry: IMetricRegistry) : IFactStore * string * string =
    let store =
        BlobFactStore.createWithIndex
            (InMemoryBlobStorage.InMemoryBlobStorage())
            (InMemoryEventStore.InMemoryEventStore())
            (Some registry)
            (fun () -> DateTime.UtcNow)
            FactSurfaceOptions.defaults
            FactIndexOptions.always

    store, newScope (), newScope ()

/// The registry-directed population contract bound to the same store.
let indexPopulationRegistryTests =
    IFactStoreContract.populationRegistryTests "BlobFactStore (point-read index)" indexRegistryFactory

[<Literal>]
let private PointScaleSize = 10_000

let private pointPeriod (k: int) : TemporalExtent = {
    From = DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3 * k)
    To = DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3 * k + 3)
    Label = None
}

let private pointFact (index: int) : Fact = {
    syntheticFact index (Scalar(decimal index)) with
        FactId = sprintf "p%08d" index
        Subject = {
            Hierarchy = "geography"
            Path = [ "eu"; sprintf "sku-%04d" (index / 10) ]
        }
        Metric = MetricRef "revenue"
        Period = pointPeriod (index % 10)
}

let private seedPoints (storage: IBlobStorage) (scope: string) (count: int) = async {
    let json = FableConverters.create ()

    for i in 0 .. count - 1 do
        let fact = pointFact i
        let payload = JsonSerializer.Serialize(fact, json) |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(scope, sprintf "_facts/%s.json" fact.FactId, payload)
        ()
}

let private blobStoreWithIndex (storage: IBlobStorage) (clock: unit -> DateTime) (index: FactIndexOptions) =
    BlobFactStore(
        storage,
        InMemoryEventStore.InMemoryEventStore(),
        Some surfaceRegistry,
        clock,
        FactSurfaceOptions.defaults,
        index
    )

let private storeWithIndex (storage: IBlobStorage) (clock: unit -> DateTime) (index: FactIndexOptions) : IFactStore =
    blobStoreWithIndex storage clock index :> IFactStore

let private leavesIn (storage: IBlobStorage) (scope: string) = storage.List(scope, FactIndex.Prefix)

/// The counts one measured run produces.
type private PointReadCost = {
    Enumerated: int
    Cold: int
    Warm: int
    WarmLists: int
    Chain: int
    Superseding: int
    NewLineage: int
}

/// Seed `size` facts, then count what each indexed operation reads.
let private measurePointReads (size: int) = async {
    let counting = CountingBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
    let storage = counting :> IBlobStorage
    let scope = newScope ()
    do! seedPoints storage scope size

    let clock = steppingClock (baseAsOf.AddDays 1.0)
    let enumerating = storeWithIndex storage clock FactIndexOptions.disabled
    let indexed = storeWithIndex storage clock FactIndexOptions.defaults

    // Verify the probe before trusting any count it produces.
    let target = pointFact 423
    let! readBack = enumerating.Get(scope, target.FactId)
    Expect.equal readBack (Some target) "the seeded blobs are in the store's own format"

    let query = {
        FactQuery.forSubjectMetric target.Subject target.Metric with
            PeriodOverlaps = Some target.Period
    }

    counting.Reset()
    let! viaLog = enumerating.Query(scope, query)
    let enumerated = counting.Downloads

    // Cold: the first consulting read finds no leaves, so it enumerates
    // (the truth) and writes every missing leaf from the facts it read.
    counting.Reset()
    let! cold = indexed.Query(scope, query)
    let coldReads = counting.Downloads

    counting.Reset()
    let! warm = indexed.Query(scope, query)
    let warmReads = counting.Downloads
    let warmLists = counting.Lists

    counting.Reset()
    let! chain = indexed.QuerySupersessionChain(scope, target.FactId)
    let chainReads = counting.Downloads

    counting.Reset()

    let! superseding =
        indexed.Assert(
            scope,
            {
                draftAt target.Subject.Path "revenue" syntheticMethod (Scalar 1m) "new-input" with
                    Period = target.Period
            }
        )

    let supersedingReads = counting.Downloads

    counting.Reset()

    let! fresh =
        indexed.Assert(
            scope,
            {
                draftAt [ "eu"; "sku-new" ] "revenue" syntheticMethod (Scalar 2m) "fresh" with
                    Period = target.Period
            }
        )

    let newReads = counting.Downloads

    Expect.equal viaLog [ target ] "the enumeration finds the one fact"
    Expect.equal cold viaLog "the cold read answers what the log does"
    Expect.equal warm viaLog "and so does the warm, indexed read"
    Expect.equal chain [ target ] "the chain is the target's lineage"

    match superseding, fresh with
    | Ok f, Ok g ->
        Expect.equal f.Supersedes (Some target.FactId) "the indexed head lookup found the head"
        Expect.isNone g.Supersedes "a new lineage has no head"
    | _ -> failtestf "assert failed: %A / %A" superseding fresh

    return {
        Enumerated = enumerated
        Cold = coldReads
        Warm = warmReads
        WarmLists = warmLists
        Chain = chainReads
        Superseding = supersedingReads
        NewLineage = newReads
    }
}

/// An `IBlobStorage` probe for the failure and fan-out cases: it can refuse
/// every index-leaf write, and it records the most downloads ever in flight
/// at once (with an optional delay so that concurrency can show at all).
type private ProbeBlobStorage(inner: IBlobStorage) =
    let gate = obj ()
    let mutable refuseIndex = false
    let mutable delay = false
    let mutable inFlight = 0
    let mutable maxInFlight = 0

    member _.RefuseIndexWrites
        with get () = refuseIndex
        and set v = refuseIndex <- v

    member _.DelayDownloads
        with get () = delay
        and set v = delay <- v

    member _.MaxInFlight = lock gate (fun () -> maxInFlight)

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            if refuseIndex && blobName.StartsWith(FactIndex.Prefix, StringComparison.Ordinal) then
                async.Return(Error "index write refused (test)")
            else
                inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            lock gate (fun () ->
                inFlight <- inFlight + 1
                maxInFlight <- max maxInFlight inFlight)

            try
                if delay then
                    do! Async.Sleep 2

                return! inner.Download(container, blobName)
            finally
                lock gate (fun () -> inFlight <- inFlight - 1)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private unmarkedPeriod: TemporalExtent = {
    From = DateTime(2026, 1, 1)
    To = DateTime(2026, 2, 1)
    Label = None
}

let private localPeriod: TemporalExtent = {
    From = DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local)
    To = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Local)
    Label = None
}

let private q3: TemporalExtent = {
    From = q2.To
    To = q2.To.AddMonths 3
    Label = Some "Q3-2026"
}

let private estimator = Computed("estimator", "1", "p0")

/// Paths carrying every character a blob path or the lineage key's own
/// separators could trip on, and the empty segment.
let private awkwardPath = [ "a/b"; ""; "c>d"; "x|y"; "100%"; "é" ]

/// Seed every shape a narrowed read could get wrong, through `store`, and
/// return what was written in order.
let private seed890 (store: IFactStore) (scope: string) = async {
    let drafts = [
        draftAt [ "uk" ] "revenue" rollup (Scalar 1m) "uk-q2-v1"
        draftAt [ "uk" ] "revenue" rollup (Scalar 2m) "uk-q2-v2"
        draftAt [ "uk" ] "revenue" estimator (Scalar 3m) "uk-q2-est"
        {
            draftAt [ "uk" ] "revenue" rollup (Scalar 4m) "uk-q3" with
                Period = q3
        }
        {
            draftAt [ "uk" ] "revenue" rollup (Scalar 5m) "uk-un-v1" with
                Period = unmarkedPeriod
        }
        {
            draftAt [ "uk" ] "revenue" rollup (Scalar 6m) "uk-un-v2" with
                Period = unmarkedPeriod
        }
        {
            draftAt [ "uk" ] "revenue" rollup (Scalar 7m) "uk-loc" with
                Period = localPeriod
        }
        draftAt [ "uk" ] "cost" rollup (Scalar 8m) "uk-cost"
        draftAt [ "fr" ] "revenue" rollup (Scalar 9m) "fr-q2"
        draftAt awkwardPath "revenue" rollup (Scalar 10m) "awk-v1"
        draftAt awkwardPath "revenue" rollup (Scalar 11m) "awk-v2"
        draftAt [ "de" ] "elasticity" rollup (Scalar 12m) "de-rollup"
        draftAt [ "de" ] "elasticity" estimator (Scalar 13m) "de-est"
    ]

    let written = ResizeArray<Fact>()

    for d in drafts do
        let! r = store.Assert(scope, d)

        match r with
        | Ok f -> written.Add f
        | Error e -> failtestf "seed failed: %s" e

    return List.ofSeq written
}

/// The query matrix: every subject × metric × period clause × method clause
/// × history flag × visibility instant the seed can distinguish.
let private queries890 (midway: DateTime) : FactQuery list = [
    for path in [ [ "uk" ]; [ "fr" ]; awkwardPath; [ "de" ]; [ "nobody" ] ] do
        for metric in [ "revenue"; "cost"; "elasticity" ] do
            for period in [ None; Some q2; Some q3; Some unmarkedPeriod; Some localPeriod ] do
                for method' in [ None; Some rollup; Some estimator ] do
                    for history in [ false; true ] do
                        for asOf in [ None; Some midway ] ->
                            {
                                Subject = Some { Hierarchy = "geography"; Path = path }
                                Metric = Some(MetricRef metric)
                                PeriodOverlaps = period
                                Method = method'
                                AsOf = asOf
                                IncludeSuperseded = history
                            }
]

/// Both read paths, every query in the matrix, every chain: equal.
let private expectSameReads (enumerating: IFactStore) (indexed: IFactStore) (scope: string) (facts: Fact list) = async {
    let midway = facts[0].AsOf

    for query in queries890 midway do
        let! a = enumerating.Query(scope, query)
        let! b = indexed.Query(scope, query)
        Expect.equal b a (sprintf "Query agrees for %A" query)
        let! ca = enumerating.QueryWithCompetition(scope, query)
        let! cb = indexed.QueryWithCompetition(scope, query)
        Expect.equal cb ca (sprintf "QueryWithCompetition agrees for %A" query)

    for f in facts do
        let! a = enumerating.QuerySupersessionChain(scope, f.FactId)
        let! b = indexed.QuerySupersessionChain(scope, f.FactId)
        Expect.equal b a (sprintf "the chain of %s agrees" f.FactId)
}

let private fixedClock () = baseAsOf.AddDays 30.0

let pointReadIndexTests =
    testList "Phase 890 point-read index" [

        testCaseAsync "a point read reads the facts it concerns, whatever the scope's size"
        <| async {
            let! small = measurePointReads 1_000
            let! large = measurePointReads PointScaleSize

            printfn
                "Phase 890 point reads: before (enumeration) %d downloads at 1,000 facts, %d at 10,000 | after, warm: point read %d / %d downloads (%d lists), chain %d / %d, superseding assert %d / %d, new-lineage assert %d / %d | cold first read %d / %d (enumerates once, writes the index)"
                small.Enumerated
                large.Enumerated
                small.Warm
                large.Warm
                large.WarmLists
                small.Chain
                large.Chain
                small.Superseding
                large.Superseding
                small.NewLineage
                large.NewLineage
                small.Cold
                large.Cold

            Expect.equal large.Enumerated PointScaleSize "the enumeration reads every one of the 10,000 facts"
            Expect.equal large.Cold PointScaleSize "the cold read enumerates exactly once"
            Expect.equal large.Warm 1 "the warm point read reads the one fact it returns"
            Expect.equal large.WarmLists 2 "and lists the census and the leaves"
            Expect.equal large.Chain 2 "the chain reads the target and its one-fact lineage"

            // The surface's own snapshot probe is one of these reads on the
            // assert path; the head lookup's confirmation is the other.
            Expect.isLessThanOrEqual large.Superseding 2 "a superseding assert reads its head, not the scope"
            Expect.isLessThanOrEqual large.NewLineage 1 "a new lineage reads no fact at all"

            Expect.equal
                (large.Warm, large.Chain, large.Superseding, large.NewLineage)
                (small.Warm, small.Chain, small.Superseding, small.NewLineage)
                "no indexed count depends on the scope's size"
        }

        testCaseAsync "the indexed and enumerating paths agree on every query, competition and chain"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let scope = newScope ()
            let writer = storeWithIndex storage (steppingClock baseAsOf) FactIndexOptions.always
            let! facts = seed890 writer scope
            let enumerating = storeWithIndex storage fixedClock FactIndexOptions.disabled
            let indexed = storeWithIndex storage fixedClock FactIndexOptions.always

            let! leaves = leavesIn storage scope
            Expect.equal leaves.Length facts.Length "one leaf per fact, written with the fact"

            do! expectSameReads enumerating indexed scope facts
        }

        testCaseAsync "deleting the index and re-querying rebuilds it and answers the same"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let scope = newScope ()
            let writer = storeWithIndex storage (steppingClock baseAsOf) FactIndexOptions.always
            let! facts = seed890 writer scope
            let enumerating = storeWithIndex storage fixedClock FactIndexOptions.disabled
            let indexed = storeWithIndex storage fixedClock FactIndexOptions.always

            let! before = leavesIn storage scope

            for leaf in before do
                let! _ = storage.Delete(scope, leaf)
                ()

            let! emptied = leavesIn storage scope
            Expect.isEmpty emptied "the index is gone"

            let query =
                FactQuery.forSubjectMetric
                    {
                        Hierarchy = "geography"
                        Path = [ "uk" ]
                    }
                    (MetricRef "revenue")

            let! viaLog = enumerating.Query(scope, query)
            let! viaIndex = indexed.Query(scope, query)
            Expect.equal viaIndex viaLog "the re-query answers what the log does"

            let! rebuilt = leavesIn storage scope
            Expect.equal (List.sort rebuilt) (List.sort before) "and wrote the index back, leaf for leaf"

            do! expectSameReads enumerating indexed scope facts
        }

        testCaseAsync "a failed index write degrades to the enumeration and is repaired, never a different answer"
        <| async {
            let probe = ProbeBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = probe :> IBlobStorage
            let scope = newScope ()
            let writer = storeWithIndex storage (steppingClock baseAsOf) FactIndexOptions.always

            probe.RefuseIndexWrites <- true
            let! facts = seed890 writer scope
            let! refused = leavesIn storage scope
            Expect.isEmpty refused "every leaf write failed, and no assert did"

            let enumerating = storeWithIndex storage fixedClock FactIndexOptions.disabled
            let maintained = blobStoreWithIndex storage fixedClock FactIndexOptions.always
            let indexed = maintained :> IFactStore
            do! expectSameReads enumerating indexed scope facts

            let! consistency = maintained.IndexConsistencyCheck(scope, 100)

            Expect.equal
                (consistency |> List.map (fun e -> e.UnindexedCanonicals))
                [ facts.Length ]
                "the consistency check names the drift"

            probe.RefuseIndexWrites <- false

            let! _ =
                indexed.Query(
                    scope,
                    FactQuery.forSubjectMetric
                        {
                            Hierarchy = "geography"
                            Path = [ "fr" ]
                        }
                        (MetricRef "revenue")
                )

            let! repaired = leavesIn storage scope
            Expect.equal repaired.Length facts.Length "one read repaired every missing leaf"

            let! after = maintained.IndexConsistencyCheck(scope, 100)

            Expect.equal
                (after
                 |> List.map (fun e -> e.SampleSize, e.ConsistentEntries, e.OrphanedIndexEntries, e.UnindexedCanonicals))
                [ facts.Length, facts.Length, 0, 0 ]
                "and the check reports no drift"

            do! expectSameReads enumerating indexed scope facts
        }

        testCaseAsync "a fact written behind the store's back is still read, and indexed by that read"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let scope = newScope ()
            let writer = storeWithIndex storage (steppingClock baseAsOf) FactIndexOptions.always
            let! facts = seed890 writer scope

            let outOfBand = {
                pointFact 1 with
                    Subject = {
                        Hierarchy = "geography"
                        Path = [ "uk" ]
                    }
                    Period = q2
                    Method = Computed("manual", "1", "p0")
            }

            let payload =
                JsonSerializer.Serialize(outOfBand, FableConverters.create ())
                |> Encoding.UTF8.GetBytes

            let! _ = storage.Upload(scope, sprintf "_facts/%s.json" outOfBand.FactId, payload)

            let indexed = storeWithIndex storage fixedClock FactIndexOptions.always
            let enumerating = storeWithIndex storage fixedClock FactIndexOptions.disabled

            let query = {
                FactQuery.forSubjectMetric outOfBand.Subject outOfBand.Metric with
                    PeriodOverlaps = Some q2
            }

            let! viaIndex = indexed.Query(scope, query)
            Expect.contains viaIndex outOfBand "the unindexed fact is in the answer"

            let! leaves = leavesIn storage scope
            Expect.equal leaves.Length (facts.Length + 1) "and now has its leaf"

            do! expectSameReads enumerating indexed scope (outOfBand :: facts)
        }

        testCaseAsync "RebuildIndex writes every leaf and the consistency check finds orphans"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let scope = newScope ()

            let writer =
                storeWithIndex storage (steppingClock baseAsOf) FactIndexOptions.disabled

            let! facts = seed890 writer scope
            let! none = leavesIn storage scope
            Expect.isEmpty none "a disabled store writes no index"

            let maintained = blobStoreWithIndex storage fixedClock FactIndexOptions.always
            let indexed = maintained :> IFactStore
            let! rebuilt = maintained.RebuildIndex scope
            Expect.equal rebuilt facts.Length "every fact indexed"

            let! clean = maintained.IndexConsistencyCheck(scope, 100)

            Expect.equal
                (clean
                 |> List.map (fun e -> e.StoreName, e.IndexName, e.UnindexedCanonicals, e.OrphanedIndexEntries))
                [ "facts", "_factindex", 0, 0 ]
                "a rebuilt index has no drift"

            // An erasure: the fact goes, its leaf stays. Reads ignore the
            // orphan (only census members are read); the check names it.
            let erased = facts |> List.find (fun f -> f.Subject.Path = [ "fr" ])
            let! _ = storage.Delete(scope, sprintf "_facts/%s.json" erased.FactId)
            let! orphaned = maintained.IndexConsistencyCheck(scope, 100)

            Expect.equal (orphaned |> List.map (fun e -> e.OrphanedIndexEntries)) [ 1 ] "the orphaned leaf is reported"

            let! fr = indexed.Query(scope, FactQuery.forSubjectMetric erased.Subject erased.Metric)

            Expect.isEmpty fr "and never read"
        }

        testCaseAsync "below the threshold no index is written and the blob layout is unchanged"
        <| async {
            let plain = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let defaulted = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let scope = newScope ()

            let enumerating =
                storeWithIndex plain (steppingClock baseAsOf) FactIndexOptions.disabled

            let belowThreshold =
                storeWithIndex defaulted (steppingClock baseAsOf) FactIndexOptions.defaults

            let! factsA = seed890 enumerating scope
            let! factsB = seed890 belowThreshold scope
            Expect.equal factsB factsA "the same facts, the same transaction times"

            let query =
                FactQuery.forSubjectMetric
                    {
                        Hierarchy = "geography"
                        Path = [ "uk" ]
                    }
                    (MetricRef "revenue")

            let! a = enumerating.Query(scope, query)
            let! b = belowThreshold.Query(scope, query)
            Expect.equal b a "the same answer"

            let! namesA = plain.List(scope, "")
            let! namesB = defaulted.List(scope, "")
            Expect.equal (List.sort namesB) (List.sort namesA) "the same blobs"
            Expect.isFalse (namesB |> List.exists (fun n -> n.StartsWith(FactIndex.Prefix))) "and no index blob"

            for name in namesA do
                let! bytesA = plain.Download(scope, name)
                let! bytesB = defaulted.Download(scope, name)
                Expect.equal bytesB bytesA (sprintf "%s is byte-identical" name)
        }

        testCaseAsync "every blob fan-out runs under the stated bound"
        <| async {
            let probe = ProbeBlobStorage(InMemoryBlobStorage.InMemoryBlobStorage())
            let storage = probe :> IBlobStorage
            let scope = newScope ()
            do! seedPoints storage scope 2_000
            probe.DelayDownloads <- true

            let store = storeWithIndex storage fixedClock FactIndexOptions.disabled
            let! everything = store.Query(scope, FactQuery.all)
            Expect.equal everything.Length 2_000 "the whole-store walk read every fact"

            // The probe first: a delay that produced no concurrency would
            // make any bound hold vacuously.
            Expect.isGreaterThan probe.MaxInFlight 1 "the reads did overlap"
            Expect.isLessThanOrEqual probe.MaxInFlight 16 "and never more than BlobFanOut.Bound at once"
        }
    ]