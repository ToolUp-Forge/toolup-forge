module ToolUp.Platform.Tests.Contracts.IFactStoreContract

open System
open Expecto
open ToolUp.Facts
open ToolUp.Platform.Grounding

// ─── IFactStore contract pack (Phase 520) ────────────────────────────
//
// Parametrised tests for any `IFactStore` implementation. The factory
// hands back a fresh `(store, scopeA, scopeB)` triple so concurrent runs
// cannot interfere. Coverage: content-address idempotency (law L2),
// derived supersession within a lineage (L3), bitemporal `AsOf`
// reconstruction (L4), competing facts never merged (D19), scope
// isolation (GP 4), and the disclosure / Absent field round-trips.

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft subjectMember metricId inputHash value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ subjectMember ]
    }
    Metric = MetricRef metricId
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

let private assertOk label (store: IFactStore) scope d = async {
    let! r = store.Assert(scope, d)

    match r with
    | Ok f -> return f
    | Error e -> return failtestf "%s: expected Ok, got %s" label e
}

let private assertBatchOk label (store: IFactStore) scope drafts = async {
    let! r = store.AssertBatch(scope, drafts)

    match r with
    | Ok receipt -> return receipt
    | Error e -> return failtestf "%s: expected Ok, got %s" label e
}

/// The whole of a scope's fact base, current heads and superseded alike —
/// the state two write paths have to agree on.
let private wholeStore (store: IFactStore) scope = async {
    let! facts =
        store.Query(
            scope,
            {
                FactQuery.all with
                    IncludeSuperseded = true
            }
        )

    return facts
}

// ─── Population read fixtures (Phase 701) ────────────────────────────

/// A draft at an arbitrary subject path. The point-read `draft` above is
/// fixed at one member, which is exactly what a population is not.
let private popDraft (path: string list) (inputHash: string) (value: FactValue) : FactDraft = {
    Subject = { Hierarchy = "geography"; Path = path }
    Metric = MetricRef "elasticity"
    Value = value
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

/// The handful of seeded facts a population test needs to name.
type private SeededPopulation = {
    /// `[eu; uk]`'s first head (`Scalar 15`), superseded by `UkHead`.
    UkFirst: Fact
    /// `[eu; uk]`'s current head (`Scalar 30`).
    UkHead: Fact
    /// The competing `estimator` head over `[eu; fr]` (`Scalar 99`).
    FrCompeting: Fact
}

/// Seed a three-level `geography` population of `elasticity` facts:
///
/// | level | subject      | value            | method    |
/// |-------|--------------|------------------|-----------|
/// | 0     | `[]`         | `Scalar 50`      | rollup    |
/// | 1     | `[eu]`       | `Scalar 10`      | rollup    |
/// | 1     | `[na]`       | `Scalar 40`      | rollup    |
/// | 2     | `[eu; fr]`   | `Scalar 20`      | rollup    |
/// | 2     | `[eu; fr]`   | `Scalar 99`      | estimator |
/// | 2     | `[na; us]`   | `Scalar 60`      | rollup    |
/// | 2     | `[na; ca]`   | `Categorical`    | rollup    |
/// | 2     | `[na; mx]`   | `Absent`         | rollup    |
/// | 2     | `[eu; uk]`   | `Scalar 15 → 30` | rollup    |
///
/// `[eu; uk]` is seeded **last** so its pre-supersession transaction time
/// is later than every other member's — an `AsOf` replay at that instant
/// therefore sees the whole population with `uk` still at 15.
let private seedPopulation (store: IFactStore) (scope: string) : Async<SeededPopulation> = async {
    let! _ = assertOk "root" store scope (popDraft [] "h-root" (Scalar 50m))
    let! _ = assertOk "eu" store scope (popDraft [ "eu" ] "h-eu" (Scalar 10m))
    let! _ = assertOk "na" store scope (popDraft [ "na" ] "h-na" (Scalar 40m))
    let! _ = assertOk "fr" store scope (popDraft [ "eu"; "fr" ] "h-fr" (Scalar 20m))

    let! frCompeting =
        assertOk "fr-estimator" store scope {
            popDraft [ "eu"; "fr" ] "h-fr-est" (Scalar 99m) with
                Method = Computed("estimator", "1", "p0")
        }

    let! _ = assertOk "us" store scope (popDraft [ "na"; "us" ] "h-us" (Scalar 60m))
    let! _ = assertOk "ca" store scope (popDraft [ "na"; "ca" ] "h-ca" (Categorical "not measured"))
    let! _ = assertOk "mx" store scope (popDraft [ "na"; "mx" ] "h-mx" (Absent "no data loaded"))
    let! ukFirst = assertOk "uk-v1" store scope (popDraft [ "eu"; "uk" ] "h-uk-1" (Scalar 15m))
    let! ukHead = assertOk "uk-v2" store scope (popDraft [ "eu"; "uk" ] "h-uk-2" (Scalar 30m))

    return {
        UkFirst = ukFirst
        UkHead = ukHead
        FrCompeting = frCompeting
    }
}

/// Every level-2 member, largest first — the shape most population
/// assertions below start from.
let private level2Descending: PopulationQuery = {
    PopulationQuery.create (MetricRef "elasticity") "geography" with
        Level = Some 2
        Ordering = Descending
        TopK = 20
}

let private okResult label (r: Result<PopulationResult, string>) =
    match r with
    | Ok p -> p
    | Error e -> failtestf "%s: expected Ok, got refusal %s" label e

let private refusal label (r: Result<PopulationResult, string>) =
    match r with
    | Error e -> e
    | Ok p -> failtestf "%s: expected a typed refusal, got %d ranked" label (List.length p.Ranked)

let private scalars (facts: Fact list) =
    facts
    |> List.map (fun f ->
        match f.Value with
        | Scalar d -> d
        | other -> failtestf "expected a Scalar in the ranking, got %A" other)

let tests (name: string) (factory: unit -> IFactStore * string * string) =

    testList $"{name} — IFactStore contract" [

        // ─── Content-address idempotency (L2) ─────────────────────────

        testCaseAsync "asserting an identical tuple twice yields one fact (idempotent)"
        <| async {
            let store, scopeA, _ = factory ()
            let d = draft "uk" "revenue" "hashA" 100m
            let! f1 = assertOk "first" store scopeA d
            let! f2 = assertOk "second" store scopeA d

            Expect.equal f2.FactId f1.FactId "same content-addressed id"
            Expect.equal f2.AsOf f1.AsOf "idempotent re-assert does not advance AsOf"

            let! current = store.Query(scopeA, FactQuery.forSubjectMetric d.Subject d.Metric)
            Expect.equal current.Length 1 "one current fact, not two"
        }

        // ─── Derived supersession within a lineage (L3) ───────────────

        testCaseAsync "changed inputs yield a new fact with a derived Supersedes edge"
        <| async {
            let store, scopeA, _ = factory ()
            let! f1 = assertOk "v1" store scopeA (draft "uk" "revenue" "hashA" 100m)
            let! f2 = assertOk "v2" store scopeA (draft "uk" "revenue" "hashB" 110m)

            Expect.notEqual f2.FactId f1.FactId "changed input → new id"
            Expect.equal f2.Supersedes (Some f1.FactId) "derived supersession edge"
            Expect.isTrue (f2.AsOf > f1.AsOf) "superseder AsOf strictly greater (L3)"

            let! current = store.Query(scopeA, FactQuery.forSubjectMetric f1.Subject f1.Metric)
            Expect.equal (current |> List.map _.FactId) [ f2.FactId ] "only the head is current now"
        }

        // ─── Bitemporal AsOf reconstruction (L4) ──────────────────────

        testCaseAsync "an AsOf query between two assertions returns the earlier fact"
        <| async {
            let store, scopeA, _ = factory ()
            let! f1 = assertOk "v1" store scopeA (draft "uk" "revenue" "hashA" 100m)
            let! f2 = assertOk "v2" store scopeA (draft "uk" "revenue" "hashB" 110m)

            // As of f1's transaction time, f2 does not yet exist.
            let! atF1 = store.Query(scopeA, FactQuery.forSubjectMetric f1.Subject f1.Metric |> FactQuery.asOf f1.AsOf)
            Expect.equal (atF1 |> List.map _.FactId) [ f1.FactId ] "as-of f1.AsOf sees f1"

            // As of f2's transaction time, f2 is the current head.
            let! atF2 = store.Query(scopeA, FactQuery.forSubjectMetric f1.Subject f1.Metric |> FactQuery.asOf f2.AsOf)
            Expect.equal (atF2 |> List.map _.FactId) [ f2.FactId ] "as-of f2.AsOf sees f2"
        }

        testCaseAsync "IncludeSuperseded returns the full history"
        <| async {
            let store, scopeA, _ = factory ()
            let! f1 = assertOk "v1" store scopeA (draft "uk" "revenue" "hashA" 100m)
            let! f2 = assertOk "v2" store scopeA (draft "uk" "revenue" "hashB" 110m)

            let! history =
                store.Query(
                    scopeA,
                    {
                        FactQuery.forSubjectMetric f1.Subject f1.Metric with
                            IncludeSuperseded = true
                    }
                )

            Expect.equal
                (history |> List.map _.FactId |> List.sort)
                ([ f1.FactId; f2.FactId ] |> List.sort)
                "both versions"
        }

        // ─── Competing facts never merged (D19) ───────────────────────

        testCaseAsync "two methods over one (subject, metric, period) are both current, neither supersedes"
        <| async {
            let store, scopeA, _ = factory ()

            let dA = draft "uk" "revenue" "hashA" 100m

            let dB = {
                draft "uk" "revenue" "hashB" 105m with
                    Method = Computed("estimator", "1", "p0")
            }

            let! fA = assertOk "A" store scopeA dA
            let! fB = assertOk "B" store scopeA dB

            Expect.isNone fB.Supersedes "a different method is a competing fact, not a supersession"

            let! current = store.Query(scopeA, FactQuery.forSubjectMetric dA.Subject dA.Metric)
            Expect.equal current.Length 2 "both competing facts are current"

            // Naming the method disambiguates.
            let! onlyA =
                store.Query(
                    scopeA,
                    {
                        FactQuery.forSubjectMetric dA.Subject dA.Metric with
                            Method = Some dA.Method
                    }
                )

            Expect.equal (onlyA |> List.map _.FactId) [ fA.FactId ] "method filter selects one lineage"
        }

        // ─── Supersession chain ───────────────────────────────────────

        testCaseAsync "QuerySupersessionChain returns the lineage in AsOf order"
        <| async {
            let store, scopeA, _ = factory ()
            let! f1 = assertOk "v1" store scopeA (draft "uk" "revenue" "hashA" 100m)
            let! _ = assertOk "v2" store scopeA (draft "uk" "revenue" "hashB" 110m)
            let! f3 = assertOk "v3" store scopeA (draft "uk" "revenue" "hashC" 120m)

            let! chain = store.QuerySupersessionChain(scopeA, f3.FactId)
            Expect.equal chain.Length 3 "three facts in the lineage"
            let asOfs = chain |> List.map _.AsOf
            Expect.equal asOfs (List.sort asOfs) "ordered by AsOf ascending"
            Expect.equal (List.head chain).FactId f1.FactId "earliest first"
        }

        // ─── Batch assertion (Phase 704) ──────────────────────────────

        testCaseAsync "re-asserting an unchanged population is all-idempotent and writes no new facts"
        <| async {
            let store, scopeA, _ = factory ()

            let drafts = [
                for i in 1..12 -> popDraft [ "eu"; sprintf "m%d" i ] (sprintf "h%d" i) (Scalar(decimal i))
            ]

            let! first = assertBatchOk "first" store scopeA drafts
            Expect.equal first.DraftCount 12 "every draft accounted for"
            Expect.equal first.AssertedCount 12 "all twelve are new"
            Expect.equal first.SupersedingCount 0 "an empty scope has nothing to supersede"
            Expect.equal first.IdempotentCount 0 "and nothing to skip"

            let! second = assertBatchOk "second" store scopeA drafts
            Expect.equal second.IdempotentCount 12 "re-running an unchanged population is a no-op (D1)"
            Expect.equal second.AssertedCount 0 "no new facts"
            Expect.equal second.SupersedingCount 0 "and none superseded"

            Expect.notEqual
                second.Digest
                first.Digest
                "the digest covers the outcomes too, so the same drafts twice are two distinguishable batches"

            let! stored = wholeStore store scopeA
            Expect.equal stored.Length 12 "twelve facts, not twenty-four"
        }

        testCaseAsync "a mixed batch reports new, unchanged and superseding drafts apart"
        <| async {
            let store, scopeA, _ = factory ()

            let a = popDraft [ "eu"; "a" ] "h-a" (Scalar 1m)
            let b = popDraft [ "eu"; "b" ] "h-b" (Scalar 2m)
            let! _ = assertBatchOk "seed" store scopeA [ a; b ]

            // `a` unchanged, `b` recomputed from a new input (so it
            // supersedes within its lineage), `c` brand new.
            let bPrime = popDraft [ "eu"; "b" ] "h-b2" (Scalar 22m)
            let c = popDraft [ "eu"; "c" ] "h-c" (Scalar 3m)
            let! mixed = assertBatchOk "mixed" store scopeA [ a; bPrime; c ]

            Expect.equal mixed.DraftCount 3 "three drafts submitted"
            Expect.equal mixed.IdempotentCount 1 "the unchanged draft is skipped"
            Expect.equal mixed.SupersedingCount 1 "the recomputed one supersedes"
            Expect.equal mixed.AssertedCount 1 "the new one is asserted"

            Expect.equal
                (mixed.AssertedCount + mixed.SupersedingCount + mixed.IdempotentCount)
                mixed.DraftCount
                "the three counts partition the batch"

            let! heads = store.Query(scopeA, FactQuery.forSubjectMetric bPrime.Subject bPrime.Metric)

            match heads with
            | [ head ] ->
                Expect.equal mixed.SupersedingFactIds [ head.FactId ] "the receipt names the superseding fact"
                Expect.isSome head.Supersedes "which carries a derived supersession edge, exactly as Assert derives it"
            | other -> failtestf "expected one current head for b, got %d" (List.length other)
        }

        testCaseAsync "a batch and the same drafts asserted one by one leave the same store state"
        <| async {
            let store, scopeA, scopeB = factory ()

            let drafts = [
                popDraft [ "eu"; "fr" ] "h-fr" (Scalar 20m)
                popDraft [ "eu"; "uk" ] "h-uk-1" (Scalar 15m)
                // Same lineage as the draft above — it must supersede it
                // WITHIN the batch, which is the case a naive batch
                // implementation gets wrong by deriving every head from
                // one pre-batch snapshot of the log.
                popDraft [ "eu"; "uk" ] "h-uk-2" (Scalar 30m)
                popDraft [ "na"; "us" ] "h-us" (Categorical "not measured")
            ]

            let! _ = assertBatchOk "batch" store scopeA drafts

            for d in drafts do
                let! _ = assertOk "scalar" store scopeB d
                ()

            let identity (facts: Fact list) =
                facts
                |> List.map (fun f -> f.FactId, f.Supersedes, f.Value)
                |> List.sortBy (fun (factId, _, _) -> factId)

            let! batched = wholeStore store scopeA
            let! scalar = wholeStore store scopeB

            Expect.equal (identity batched) (identity scalar) "same facts, same derived supersession edges"

            let! batchedHeads = store.Query(scopeA, FactQuery.all)
            let! scalarHeads = store.Query(scopeB, FactQuery.all)

            Expect.equal
                (batchedHeads |> List.map _.FactId |> List.sort)
                (scalarHeads |> List.map _.FactId |> List.sort)
                "and the same current heads"

            // Stated directly as well as by equivalence: the in-batch
            // supersession left one head at the later value.
            let! uk = store.Query(scopeA, FactQuery.forSubjectMetric drafts[1].Subject drafts[1].Metric)
            Expect.equal (uk |> List.map _.Value) [ Scalar 30m ] "the batch's later draft is the current head"
        }

        testCaseAsync "a batch is confined to its scope"
        <| async {
            let store, scopeA, scopeB = factory ()

            let drafts = [ popDraft [ "eu" ] "h-eu" (Scalar 10m); popDraft [ "na" ] "h-na" (Scalar 40m) ]

            let! _ = assertBatchOk "batch" store scopeA drafts

            let! inB = wholeStore store scopeB
            Expect.isEmpty inB "the other scope sees nothing (GP 4)"

            // Content addresses are deployment- and scope-independent;
            // *presence* is not. The same batch in a second scope is
            // entirely new work there.
            let! again = assertBatchOk "same batch, other scope" store scopeB drafts
            Expect.equal again.AssertedCount 2 "the same drafts are new facts in a second scope"
            Expect.equal again.IdempotentCount 0 "presence is per-scope"
        }

        testCaseAsync "one malformed draft refuses the whole batch and commits none of it"
        <| async {
            let store, scopeA, _ = factory ()

            let good1 = popDraft [ "eu"; "fr" ] "h-fr" (Scalar 20m)
            let good2 = popDraft [ "eu"; "uk" ] "h-uk" (Scalar 30m)

            // A degenerate valid-time extent: no period-overlap clause can
            // ever match `[From, From)`, so the fact would be written and
            // then invisible to every read that filters by period.
            let malformed = {
                popDraft [ "eu"; "de" ] "h-de" (Scalar 40m) with
                    Period = { q2 with To = q2.From }
            }

            let! r = store.AssertBatch(scopeA, [ good1; malformed; good2 ])

            match r with
            | Ok receipt -> failtestf "expected a refusal, got a receipt over %d drafts" receipt.DraftCount
            | Error message ->
                Expect.stringContains message "#1" "the refusal names the offender by position"
                Expect.stringContains message "geography/eu>de" "and by subject"
                Expect.stringContains message "half-open" "and says what is wrong with it"

            let! stored = wholeStore store scopeA
            Expect.isEmpty stored "a rejected batch writes nothing at all — not even its well-formed drafts"
        }

        testCaseAsync "a batch keeps the population read model current"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ =
                assertBatchOk "seed" store scopeA [
                    popDraft [ "eu"; "fr" ] "h-fr" (Scalar 20m)
                    popDraft [ "na"; "us" ] "h-us" (Scalar 60m)
                ]

            // Read FIRST, so an implementation that maintains a derived
            // read model has actually built one — otherwise the batch
            // below would be maintaining nothing and this case would pass
            // without touching the path it exists for.
            let! before = store.QueryPopulation(scopeA, level2Descending)
            Expect.equal (scalars (okResult "before" before).Ranked) [ 60m; 20m ] "the seeded population"

            // One supersession of a member already in the model, one
            // subject the model has never seen.
            let! _ =
                assertBatchOk "second" store scopeA [
                    popDraft [ "eu"; "fr" ] "h-fr-2" (Scalar 99m)
                    popDraft [ "na"; "ca" ] "h-ca" (Scalar 5m)
                ]

            let! after = store.QueryPopulation(scopeA, level2Descending)

            Expect.equal
                (scalars (okResult "after" after).Ranked)
                [ 99m; 60m; 5m ]
                "the superseded value is gone and the new subject is in — whichever read model answered"
        }

        testCaseAsync "an empty batch is an answer, not a failure"
        <| async {
            let store, scopeA, _ = factory ()
            let! receipt = assertBatchOk "empty" store scopeA []
            Expect.equal receipt BatchAssertReceipt.empty "the empty receipt"
            Expect.isFalse receipt.Truncated "nothing to truncate"
        }

        testCaseAsync "the receipt caps its id lists and says so, and its digest pins the full set"
        <| async {
            let store, scopeA, scopeB = factory ()
            let size = BatchAssertReceipt.IdListCap + 5

            let drafts = [
                for i in 1..size -> popDraft [ "eu"; sprintf "m%d" i ] (sprintf "h%d" i) (Scalar(decimal i))
            ]

            let! receipt = assertBatchOk "capped" store scopeA drafts
            Expect.equal receipt.AssertedCount size "every draft asserted"

            Expect.equal
                receipt.AssertedFactIds.Length
                BatchAssertReceipt.IdListCap
                "the id list stops at the cap rather than becoming the batch again"

            Expect.isTrue receipt.Truncated "and the receipt says so rather than leaving a reader to infer it"

            // The digest is recomputable by a producer holding the drafts:
            // content addresses are scope-independent, so the identical
            // batch in a fresh scope digests identically.
            let! elsewhere = assertBatchOk "same batch, fresh scope" store scopeB drafts
            Expect.equal elsewhere.Digest receipt.Digest "the same batch digests the same anywhere"

            // Order is part of the batch's identity — two drafts in one
            // lineage supersede in submission order, so a digest blind to
            // order would call two different batches the same.
            let synthetic = [ BatchAsserted, "a"; BatchSuperseding, "b" ]

            Expect.notEqual
                (BatchAssertReceipt.digest synthetic)
                (BatchAssertReceipt.digest (List.rev synthetic))
                "submission order is significant"
        }

        // ─── Field round-trips ────────────────────────────────────────

        testCaseAsync "the disclosure classification round-trips"
        <| async {
            let store, scopeA, _ = factory ()

            let d = {
                draft "uk" "margin" "hashA" 42m with
                    Disclosure = Disclosure.Internal
            }

            let! f = assertOk "assert" store scopeA d
            let! got = store.Get(scopeA, f.FactId)
            Expect.equal (got |> Option.map _.Disclosure) (Some Disclosure.Internal) "Internal survives the round-trip"
        }

        testCaseAsync "an Absent value round-trips (a queryable data gap)"
        <| async {
            let store, scopeA, _ = factory ()

            let d = {
                draft "uk" "share_of_voice" "gap" 0m with
                    Value = Absent "no data loaded for this period"
            }

            let! f = assertOk "assert" store scopeA d
            let! got = store.Get(scopeA, f.FactId)

            match got |> Option.map _.Value with
            | Some(Absent reason) -> Expect.stringContains reason "no data" "absence reason preserved"
            | other -> failtestf "expected Absent, got %A" other
        }

        // ─── Scope isolation (GP 4) ───────────────────────────────────

        testCaseAsync "a fact asserted in scopeA is invisible from scopeB"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! _ = assertOk "assert" store scopeA (draft "uk" "revenue" "hashA" 100m)

            let! fromB = store.Query(scopeB, FactQuery.all)
            Expect.isEmpty fromB "scopeB sees none of scopeA's facts"
        }

        // ─── Competition indicator (Phase 566 / GP 9) ─────────────────

        testCaseAsync "QueryWithCompetition annotates competing heads with each other's method identity"
        <| async {
            let store, scopeA, _ = factory ()

            let dA = draft "uk" "revenue" "hashA" 100m

            let dB = {
                draft "uk" "revenue" "hashB" 105m with
                    Method = Computed("estimator", "1", "p0")
            }

            let! fA = assertOk "A" store scopeA dA
            let! fB = assertOk "B" store scopeA dB

            let! annotated = store.QueryWithCompetition(scopeA, FactQuery.forSubjectMetric dA.Subject dA.Metric)

            let indicatorOf factId =
                annotated
                |> List.tryFind (fun a -> a.Fact.FactId = factId)
                |> Option.map _.CompetingMethods

            Expect.equal
                (indicatorOf fA.FactId)
                (Some [ Fact.methodIdentity dB.Method ])
                "A discloses B's method as competing"

            Expect.equal
                (indicatorOf fB.FactId)
                (Some [ Fact.methodIdentity dA.Method ])
                "B discloses A's method as competing"
        }

        testCaseAsync "QueryWithCompetition returns an empty indicator when a single method computed the metric"
        <| async {
            let store, scopeA, _ = factory ()
            let d = draft "uk" "revenue" "hashA" 100m
            let! _ = assertOk "assert" store scopeA d

            let! annotated = store.QueryWithCompetition(scopeA, FactQuery.forSubjectMetric d.Subject d.Metric)
            Expect.equal annotated.Length 1 "one head"
            Expect.isEmpty (List.head annotated).CompetingMethods "uncontested → empty indicator"
        }

        testCaseAsync "QueryWithCompetition selects the same facts as Query"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = assertOk "v1" store scopeA (draft "uk" "revenue" "hashA" 100m)
            let! _ = assertOk "v2" store scopeA (draft "uk" "revenue" "hashB" 110m)

            let q =
                FactQuery.forSubjectMetric (draft "uk" "revenue" "hashA" 100m).Subject (MetricRef "revenue")

            let! plain = store.Query(scopeA, q)
            let! annotated = store.QueryWithCompetition(scopeA, q)

            Expect.equal
                (annotated |> List.map _.Fact.FactId)
                (plain |> List.map _.FactId)
                "identical selection + ordering"
        }

        // ─── Population read (Phase 701) ──────────────────────────────
        //
        // Registry-free semantics: explicit orderings, the ceiling, the
        // filters, L4 replay, scope isolation and the statistics. The
        // registry-directed half lives in `populationRegistryTests`,
        // which needs a store constructed over a metric registry.

        testCaseAsync "a population read ranks the current heads and never ranks a superseded value"
        <| async {
            let store, scopeA, _ = factory ()
            let! seeded = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeA, level2Descending)
            let population = okResult "level-2 descending" r

            Expect.equal
                (scalars population.Ranked)
                [ 99m; 60m; 30m; 20m ]
                "largest first across the level-2 population"

            Expect.equal population.Direction HighestFirst "an explicit Descending resolves to HighestFirst"

            Expect.isFalse
                (population.Ranked |> List.exists (fun f -> f.FactId = seeded.UkFirst.FactId))
                "the superseded uk head never ranks (L4)"

            Expect.isTrue
                (population.Ranked |> List.exists (fun f -> f.FactId = seeded.UkHead.FactId))
                "the current uk head does"
        }

        testCaseAsync "an explicit Ascending ordering resolves without a registry"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = seedPopulation store scopeA

            let! r =
                store.QueryPopulation(
                    scopeA,
                    {
                        level2Descending with
                            Ordering = Ascending
                    }
                )

            let population = okResult "level-2 ascending" r

            Expect.equal (scalars population.Ranked) [ 20m; 30m; 60m; 99m ] "smallest first"
            Expect.equal population.Direction LowestFirst "an explicit Ascending resolves to LowestFirst"
        }

        testCaseAsync "RegistryDirection against an unregistered metric is a typed refusal naming the gap (GP 9)"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = seedPopulation store scopeA

            let! r =
                store.QueryPopulation(
                    scopeA,
                    {
                        PopulationQuery.create (MetricRef "no-such-metric-701") "geography" with
                            Ordering = RegistryDirection
                    }
                )

            let message = refusal "unregistered metric" r
            Expect.stringContains message "no-such-metric-701" "the refusal names the metric"
            Expect.stringContains message "not registered" "the refusal names the gap"
        }

        testCaseAsync "the top-k ceiling bounds the ranking regardless of the requested k"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = seedPopulation store scopeA

            let! greedy =
                store.QueryPopulation(
                    scopeA,
                    {
                        level2Descending with
                            TopK = PopulationQuery.MaxTopK + 5_000
                    }
                )

            let capped = okResult "greedy k" greedy

            Expect.equal
                capped.EffectiveTopK
                PopulationQuery.MaxTopK
                "a k above the ceiling is clamped to the ceiling, not honoured"

            Expect.isFalse capped.Truncated "the seeded population is well under the ceiling"

            let! narrow = store.QueryPopulation(scopeA, { level2Descending with TopK = 2 })
            let top2 = okResult "k = 2" narrow

            Expect.equal (scalars top2.Ranked) [ 99m; 60m ] "the ranking is bounded by the requested k"
            Expect.equal top2.EffectiveTopK 2 "an in-range k is honoured verbatim"
            Expect.isTrue top2.Truncated "comparable members were dropped by the bound"

            Expect.equal
                top2.Stats.ComparableCount
                4
                "the statistics still describe the whole population, not the returned page"
        }

        testCaseAsync "a threshold filter narrows the population, not merely the ranking"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = seedPopulation store scopeA

            let! r =
                store.QueryPopulation(
                    scopeA,
                    {
                        level2Descending with
                            Threshold = Some(AtLeast 30m)
                    }
                )

            let population = okResult "threshold" r

            Expect.equal (scalars population.Ranked) [ 99m; 60m; 30m ] "only members at or above the bound"
            Expect.equal population.Stats.FactCount 3 "the statistics describe the filtered population"

            Expect.equal
                population.Stats.NonComparableCount
                0
                "a value that cannot be tested against a threshold is not in the filtered population"

            let! between =
                store.QueryPopulation(
                    scopeA,
                    {
                        level2Descending with
                            Threshold = Some(Between(20m, 60m))
                    }
                )

            Expect.equal
                (scalars (okResult "between" between).Ranked)
                [ 60m; 30m; 20m ]
                "Between bounds are inclusive at both ends"
        }

        testCaseAsync "an AsOf population read ranks the head that was current then (L4)"
        <| async {
            let store, scopeA, _ = factory ()
            let! seeded = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeA, level2Descending |> PopulationQuery.asOf seeded.UkFirst.AsOf)

            let population = okResult "as-of replay" r

            Expect.equal
                (scalars population.Ranked)
                [ 99m; 60m; 20m; 15m ]
                "the pre-supersession uk value ranks in its own place"

            Expect.isTrue
                (population.Ranked |> List.exists (fun f -> f.FactId = seeded.UkFirst.FactId))
                "the head current at that instant"

            Expect.isFalse
                (population.Ranked |> List.exists (fun f -> f.FactId = seeded.UkHead.FactId))
                "its successor did not exist yet"
        }

        testCaseAsync "the level and path-prefix filters select the subject set"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = seedPopulation store scopeA

            let baseQuery = {
                PopulationQuery.create (MetricRef "elasticity") "geography" with
                    Ordering = Descending
                    TopK = 20
            }

            let! atLevel1 = store.QueryPopulation(scopeA, { baseQuery with Level = Some 1 })
            Expect.equal (scalars (okResult "level 1" atLevel1).Ranked) [ 40m; 10m ] "level 1 only"

            let! atRoot = store.QueryPopulation(scopeA, { baseQuery with Level = Some 0 })
            Expect.equal (scalars (okResult "level 0" atRoot).Ranked) [ 50m ] "level 0 is the hierarchy root"

            let! underEu =
                store.QueryPopulation(
                    scopeA,
                    {
                        baseQuery with
                            PathPrefix = Some [ "eu" ]
                    }
                )

            Expect.equal
                (scalars (okResult "under eu" underEu).Ranked)
                [ 99m; 30m; 20m; 10m ]
                "the prefix admits the branch root and everything beneath it"

            let! euLeaves =
                store.QueryPopulation(
                    scopeA,
                    {
                        baseQuery with
                            PathPrefix = Some [ "eu" ]
                            Level = Some 2
                    }
                )

            Expect.equal (scalars (okResult "eu leaves" euLeaves).Ranked) [ 99m; 30m; 20m ] "level and prefix compose"

            let! unknownHierarchy =
                store.QueryPopulation(
                    scopeA,
                    {
                        PopulationQuery.create (MetricRef "elasticity") "org-chart" with
                            Ordering = Descending
                    }
                )

            let empty = okResult "unknown hierarchy" unknownHierarchy
            Expect.isEmpty empty.Ranked "no members"
            Expect.equal empty.Stats PopulationStats.empty "an empty population is an answer, not a refusal"
        }

        testCaseAsync "the statistics describe the population, counting the shapes that cannot be ranked"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeA, level2Descending)
            let stats = (okResult "stats" r).Stats

            Expect.equal stats.FactCount 6 "four scalars, one categorical, one absent"
            Expect.equal stats.SubjectCount 5 "fr contributes two competing heads under one subject"
            Expect.equal stats.ComparableCount 4 "only Scalar carries a rankable magnitude"
            Expect.equal stats.NonComparableCount 2 "Categorical and Absent are counted, never ranked"
            Expect.equal stats.Minimum (Some 20m) "smallest comparable value"
            Expect.equal stats.Maximum (Some 99m) "largest comparable value"
            Expect.equal stats.Mean (Some 52.25m) "mean over the comparable members only"
            Expect.equal stats.PeriodFrom (Some q2.From) "period coverage lower bound"
            Expect.equal stats.PeriodTo (Some q2.To) "period coverage upper bound"

            Expect.equal
                (stats.Freshness.FreshCount + stats.Freshness.StaleCount)
                stats.FactCount
                "every member lands in exactly one freshness bucket"
        }

        testCaseAsync "a population query never crosses scopeId (GP 4)"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! _ = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeB, level2Descending)
            let fromB = okResult "other scope" r

            Expect.isEmpty fromB.Ranked "scopeB sees none of scopeA's population"
            Expect.equal fromB.Stats PopulationStats.empty "and no trace of it in the summary"
        }
    ]

/// The registry-directed half of the population contract (Phase 701).
/// Separate from `tests` because it needs a store constructed over a
/// metric registry: `RegistryDirection` ordering and the D19 canonical
/// selection are both *registry* facts, and a store with no registry
/// cannot exhibit either. `registryFactory` hands back a fresh
/// `(store, scopeA, scopeB)` over the supplied registry.
let populationRegistryTests (name: string) (registryFactory: IMetricRegistry -> IFactStore * string * string) =

    let metric (direction: DirectionOfBetter) (canonical: string option) : MetricDefinition = {
        Id = "elasticity"
        Name = "Elasticity"
        Unit = "ratio"
        Dimensionality = "ratio"
        Direction = direction
        DisplayFormat = "N2"
        Staleness = UntilSuperseded
        ProducingOperation = None
        CanonicalMethod = canonical
        RecomputePolicy = None
        RollUp = None
        Context = None
    }

    let registryOf (direction: DirectionOfBetter) (canonical: string option) =
        MetricRegistry.build [
            {
                Module = "test"
                Definition = metric direction canonical
            }
        ] []

    let registryDirected: PopulationQuery = {
        level2Descending with
            Ordering = RegistryDirection
    }

    testList $"{name} — IFactStore population contract (registry-directed)" [

        testCaseAsync "HigherIsBetter ranks the population best-first descending"
        <| async {
            let store, scopeA, _ = registryFactory (registryOf HigherIsBetter None)
            let! _ = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeA, registryDirected)
            let population = okResult "higher is better" r

            Expect.equal population.Direction HighestFirst "best-first is largest-first"
            Expect.equal (scalars population.Ranked) [ 99m; 60m; 30m; 20m ] "descending"
        }

        testCaseAsync "LowerIsBetter ranks the same population best-first ascending"
        <| async {
            let store, scopeA, _ = registryFactory (registryOf LowerIsBetter None)
            let! _ = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeA, registryDirected)
            let population = okResult "lower is better" r

            Expect.equal population.Direction LowestFirst "best-first is smallest-first"

            Expect.equal
                (scalars population.Ranked)
                [ 20m; 30m; 60m; 99m ]
                "the sign of 'best' is a registry fact, not a model judgment"
        }

        testCaseAsync "a Neutral metric refuses a best-first ranking rather than guessing one"
        <| async {
            let store, scopeA, _ = registryFactory (registryOf Neutral None)
            let! _ = seedPopulation store scopeA

            let! r = store.QueryPopulation(scopeA, registryDirected)
            let message = refusal "neutral" r

            Expect.stringContains message "elasticity" "the refusal names the metric"
            Expect.stringContains message "Neutral" "and the declaration that makes it unrankable"

            // The population is still perfectly readable — the caller
            // simply has to say which way is up.
            let! explicit = store.QueryPopulation(scopeA, level2Descending)
            Expect.equal (scalars (okResult "explicit" explicit).Ranked) [ 99m; 60m; 30m; 20m ] "explicit works"
        }

        testCaseAsync "the canonical method is the population default; competitors are on request (D19)"
        <| async {
            let store, scopeA, _ =
                registryFactory (registryOf HigherIsBetter (Some "computed:rollup"))

            let! seeded = seedPopulation store scopeA

            let! canonical = store.QueryPopulation(scopeA, registryDirected)
            let byCanonical = okResult "canonical" canonical

            Expect.equal
                (scalars byCanonical.Ranked)
                [ 60m; 30m; 20m ]
                "the competing estimator head is not ranked under the canonical default"

            Expect.equal byCanonical.Stats.SubjectCount 5 "every subject is still in the population"

            let! all =
                store.QueryPopulation(
                    scopeA,
                    {
                        registryDirected with
                            Methods = AllCompetingMethods
                    }
                )

            let byAll = okResult "all competing" all

            Expect.equal
                (scalars byAll.Ranked)
                [ 99m; 60m; 30m; 20m ]
                "on request every competing head ranks — competition is surfaced, never hidden"

            let! named =
                store.QueryPopulation(
                    scopeA,
                    {
                        registryDirected with
                            Methods = OneMethod(Computed("estimator", "1", "p0"))
                    }
                )

            let byName = okResult "one method" named

            Expect.equal
                (byName.Ranked |> List.map _.FactId)
                [ seeded.FrCompeting.FactId ]
                "naming a method selects exactly its lineage"
        }
    ]