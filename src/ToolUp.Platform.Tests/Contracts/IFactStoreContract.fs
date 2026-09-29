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

let private assertOk label (store: IFactStore) (scope: string) d = async {
    let! r = store.Assert(scope, d)

    match r with
    | Ok f -> return f
    | Error e -> return failtestf "%s: expected Ok, got %s" label e
}

let private assertBatchOk label (store: IFactStore) (scope: string) drafts = async {
    let! r = store.AssertBatch(scope, drafts)

    match r with
    | Ok receipt -> return receipt
    | Error e -> return failtestf "%s: expected Ok, got %s" label e
}

/// The whole of a scope's fact base, current heads and superseded alike —
/// the state two write paths have to agree on.
let private wholeStore (store: IFactStore) (scope: string) = async {
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

        // ─── Point reads beside their neighbours (Phase 890) ──────────
        //
        // An indexed point read narrows before it filters, so these pin
        // the shapes a narrowing could get wrong: a neighbouring period,
        // a competing lineage, another subject, and a period whose
        // `DateTime` carries no kind.

        testCaseAsync "a period clause returns the overlapping lineage and not its neighbours"
        <| async {
            let store, scopeA, _ = factory ()

            let quarter (k: int) : TemporalExtent = {
                From = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3 * k)
                To = DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3 * k + 3)
                Label = None
            }

            let! facts =
                [ 0..3 ]
                |> List.map (fun k -> {
                    draft "uk" "revenue" (sprintf "q%d" k) (decimal k) with
                        Period = quarter k
                })
                |> List.map (assertOk "quarter" store scopeA)
                |> Async.Sequential

            let! _ =
                assertOk "other subject" store scopeA {
                    draft "fr" "revenue" "q1-fr" 7m with
                        Period = quarter 1
                }

            let! q1 =
                store.Query(
                    scopeA,
                    {
                        FactQuery.forSubjectMetric facts[1].Subject facts[1].Metric with
                            PeriodOverlaps = Some(quarter 1)
                    }
                )

            Expect.equal (q1 |> List.map _.FactId) [ facts[1].FactId ] "adjacent quarters do not overlap"

            let! all = store.Query(scopeA, FactQuery.forSubjectMetric facts[1].Subject facts[1].Metric)

            Expect.equal
                (all |> List.map _.FactId)
                (facts |> Array.map _.FactId |> Array.toList)
                "no period clause returns every quarter, ordered by period"
        }

        testCaseAsync "QuerySupersessionChain walks its own lineage and no competitor's"
        <| async {
            let store, scopeA, _ = factory ()
            let! f1 = assertOk "v1" store scopeA (draft "uk" "revenue" "hashA" 100m)
            let! f2 = assertOk "v2" store scopeA (draft "uk" "revenue" "hashB" 110m)

            let! _ =
                assertOk "competitor" store scopeA {
                    draft "uk" "revenue" "hashE" 105m with
                        Method = Computed("estimator", "1", "p0")
                }

            let! _ =
                assertOk "neighbour period" store scopeA {
                    draft "uk" "revenue" "hashN" 90m with
                        Period = {
                            From = q2.To
                            To = q2.To.AddMonths 3
                            Label = None
                        }
                }

            let! chain = store.QuerySupersessionChain(scopeA, f2.FactId)
            Expect.equal (chain |> List.map _.FactId) [ f1.FactId; f2.FactId ] "the lineage, and only the lineage"
        }

        testCaseAsync "a period with no DateTimeKind supersedes within its lineage"
        <| async {
            let store, scopeA, _ = factory ()

            let unmarked: TemporalExtent = {
                From = DateTime(2026, 1, 1)
                To = DateTime(2026, 2, 1)
                Label = None
            }

            let! f1 =
                assertOk "v1" store scopeA {
                    draft "uk" "revenue" "hashA" 1m with
                        Period = unmarked
                }

            let! f2 =
                assertOk "v2" store scopeA {
                    draft "uk" "revenue" "hashB" 2m with
                        Period = unmarked
                }

            Expect.equal f2.Supersedes (Some f1.FactId) "the unmarked period is one lineage"

            let! current =
                store.Query(
                    scopeA,
                    {
                        FactQuery.forSubjectMetric f1.Subject f1.Metric with
                            PeriodOverlaps = Some unmarked
                    }
                )

            Expect.equal (current |> List.map _.FactId) [ f2.FactId ] "the head is current"

            let! chain = store.QuerySupersessionChain(scopeA, f1.FactId)
            Expect.equal (chain |> List.map _.FactId) [ f1.FactId; f2.FactId ] "and the chain walks it"
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
// ─── Differential pack — two implementations, one answer (Phase 888) ─
//
// The contract packs above hold one store to the laws. This one holds a
// CANDIDATE store to a REFERENCE store: one seeded fact base, asserted
// through both under the same deterministic clock, then every query shape
// compared value for value — the Phase 702 population matrix, a point-read
// matrix over every clause the seed can distinguish (subject × metric ×
// period × method × history × visibility instant), every supersession
// chain and every fact by id.
//
// What is normalised, and why: a point read's listing is ordered by
// (hierarchy, metric, period start) under the contract, and ties are left
// to the implementation — so both listings are compared re-sorted with the
// content address as the tiebreak, and each is separately required to be
// in the contract's order. A competition indicator is a set rendered as a
// list, so it is compared sorted. Nothing else is normalised: population
// results, chains and facts compare exactly.

/// A deterministic, strictly-increasing clock — identical sequences give
/// both stores identical transaction times.
let private differentialClock () : unit -> DateTime =
    let current = ref (DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc))

    fun () ->
        let value = current.Value
        current.Value <- value.AddSeconds 1.0
        value

let private differentialRegistry: IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "test"
            Definition = {
                Id = "elasticity"
                Name = "Elasticity"
                Unit = "ratio"
                Dimensionality = "ratio"
                Direction = HigherIsBetter
                DisplayFormat = "N2"
                Staleness = UntilSuperseded
                ProducingOperation = None
                CanonicalMethod = Some "computed:rollup"
                RecomputePolicy = None
                RollUp = None
                Context = None
            }
        }
    ] []

let private q3Differential: TemporalExtent = {
    From = q2.To
    To = q2.To.AddMonths 3
    Label = Some "Q3-2026"
}

let private unmarkedDifferential: TemporalExtent = {
    From = DateTime(2026, 1, 1)
    To = DateTime(2026, 2, 1)
    Label = None
}

let private localDifferential: TemporalExtent = {
    From = DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local)
    To = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Local)
    Label = None
}

let private rollupMethod = Computed("rollup", "1", "p0")
let private estimatorMethod = Computed("estimator", "1", "p0")

/// Paths carrying every character a key, a path or a separator could trip
/// on, and the empty segment.
let private awkwardDifferentialPath = [ "a/b"; ""; "c>d"; "x|y"; "100%"; "é"; "we>ird\tta\\b\nnl" ]

let private differentialDraft
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

/// The seed: the Phase 702 population (supersession, competition, the
/// non-comparable shapes, a second depth, a neighbouring metric, awkward
/// and empty path segments, equal magnitudes at different scales) plus the
/// Phase 890 point-read shapes (several periods, unmarked and local-kind
/// periods, supersession within each), asserted one draft at a time — then
/// a batch carrying one lineage twice.
let private differentialDrafts: FactDraft list = [
    for i in 0..5 do
        differentialDraft
            [ "eu"; sprintf "sku-%06d" i ]
            "elasticity"
            rollupMethod
            (Scalar(decimal (10 * i)))
            (sprintf "h%d" i)
    differentialDraft [ "eu"; "sku-000000" ] "elasticity" rollupMethod (Scalar 95m) "h0-v2"
    differentialDraft [ "eu"; "sku-000001" ] "elasticity" (HumanAsserted "cfo") (Scalar 77m) "cfo-1"
    differentialDraft [ "eu"; "sku-000006" ] "elasticity" rollupMethod (Absent "no data loaded") "h6"
    differentialDraft [ "eu"; "sku-000007" ] "elasticity" rollupMethod (Categorical "n/a") "h7"
    differentialDraft [ "eu"; "sku-000008" ] "elasticity" rollupMethod (Interval(1m, 2m)) "h8"
    differentialDraft [ "eu" ] "elasticity" rollupMethod (Scalar 500m) "hroot"
    differentialDraft [ "eu"; "sku-000000" ] "revenue" rollupMethod (Scalar 1234m) "rev0"
    differentialDraft [ "eu"; "we>ird\tta\\b\nnl" ] "elasticity" rollupMethod (Scalar 42m) "hodd"
    differentialDraft [ "eu"; "" ] "elasticity" rollupMethod (Scalar 43.0m) "hempty"
    differentialDraft [ "eu"; "sku-000009" ] "elasticity" rollupMethod (Scalar 43.00m) "htie"
    differentialDraft [ "uk" ] "revenue" rollupMethod (Scalar 1m) "uk-q2-v1"
    differentialDraft [ "uk" ] "revenue" rollupMethod (Scalar 2m) "uk-q2-v2"
    differentialDraft [ "uk" ] "revenue" estimatorMethod (Scalar 3m) "uk-q2-est"
    {
        differentialDraft [ "uk" ] "revenue" rollupMethod (Scalar 4m) "uk-q3" with
            Period = q3Differential
    }
    {
        differentialDraft [ "uk" ] "revenue" rollupMethod (Scalar 5m) "uk-un-v1" with
            Period = unmarkedDifferential
    }
    {
        differentialDraft [ "uk" ] "revenue" rollupMethod (Scalar 6m) "uk-un-v2" with
            Period = unmarkedDifferential
    }
    {
        differentialDraft [ "uk" ] "revenue" rollupMethod (Scalar 7m) "uk-loc" with
            Period = localDifferential
    }
    differentialDraft [ "uk" ] "cost" rollupMethod (Scalar 8m) "uk-cost"
    differentialDraft [ "fr" ] "revenue" rollupMethod (Scalar 9m) "fr-q2"
    differentialDraft awkwardDifferentialPath "revenue" rollupMethod (Scalar 10m) "awk-v1"
    differentialDraft awkwardDifferentialPath "revenue" rollupMethod (Scalar 11m) "awk-v2"
    differentialDraft [ "de" ] "elasticity" rollupMethod (Scalar 12m) "de-rollup"
    differentialDraft [ "de" ] "elasticity" estimatorMethod (Scalar 13m) "de-est"
    // An idempotent replay — no new fact and no clock read, in either store.
    differentialDraft [ "fr" ] "revenue" rollupMethod (Scalar 9m) "fr-q2"
]

let private differentialBatch: FactDraft list = [
    differentialDraft [ "eu"; "sku-000003" ] "elasticity" rollupMethod (Scalar 999m) "h3-v2"
    differentialDraft [ "eu"; "sku-000003" ] "elasticity" rollupMethod (Scalar 998m) "h3-v3"
    differentialDraft [ "eu"; "sku-000004" ] "elasticity" (HumanAsserted "cfo") (Scalar 61m) "cfo-4"
    differentialDraft [ "eu"; "sku-000010" ] "elasticity" rollupMethod (Scalar 88m) "h10"
    differentialDraft [ "eu"; "sku-000002" ] "elasticity" rollupMethod (Scalar 20m) "h2"
]

let private seedDifferential (store: IFactStore) (scope: string) : Async<Fact list * BatchAssertReceipt> = async {
    let written = ResizeArray<Fact>()

    for d in differentialDrafts do
        let! f = assertOk "differential seed" store scope d
        written.Add f

    let! receipt = assertBatchOk "differential batch" store scope differentialBatch
    return List.ofSeq written, receipt
}

/// The Phase 702 population matrix, plus the shapes the seed adds.
let private populationMatrix: (string * PopulationQuery) list =
    let baseQuery = PopulationQuery.create (MetricRef "elasticity") "geography"
    let level2 = { baseQuery with Level = Some 2 }

    let noWhen = {
        From = DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        To = DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        Label = None
    }

    [
        "registry-directed, default top-k", level2
        "explicit descending", { level2 with Ordering = Descending }
        "explicit ascending", { level2 with Ordering = Ascending }
        "every depth", { baseQuery with Ordering = Descending }
        "root level",
        {
            baseQuery with
                Level = Some 0
                Ordering = Descending
        }
        "path prefix",
        {
            level2 with
                Ordering = Descending
                PathPrefix = Some [ "eu" ]
        }
        "empty path prefix",
        {
            baseQuery with
                Ordering = Descending
                PathPrefix = Some []
        }
        "path prefix matching nothing",
        {
            baseQuery with
                Ordering = Descending
                PathPrefix = Some [ "apac" ]
        }
        "threshold AtLeast",
        {
            level2 with
                Ordering = Descending
                Threshold = Some(AtLeast 30m)
        }
        "threshold AtMost",
        {
            level2 with
                Ordering = Ascending
                Threshold = Some(AtMost 43m)
        }
        "threshold Between",
        {
            level2 with
                Ordering = Ascending
                Threshold = Some(Between(20m, 50m))
        }
        "threshold excluding everything",
        {
            level2 with
                Ordering = Ascending
                Threshold = Some(AtMost -1m)
        }
        "threshold over all competing methods",
        {
            level2 with
                Ordering = Descending
                Methods = AllCompetingMethods
                Threshold = Some(AtLeast 50m)
        }
        "top-k of one",
        {
            level2 with
                Ordering = Descending
                TopK = 1
        }
        "top-k of zero clamps up",
        {
            level2 with
                Ordering = Descending
                TopK = 0
        }
        "top-k above the ceiling",
        {
            level2 with
                Ordering = Descending
                TopK = PopulationQuery.MaxTopK * 4
        }
        "all competing methods",
        {
            level2 with
                Ordering = Descending
                Methods = AllCompetingMethods
        }
        "one named method",
        {
            level2 with
                Ordering = Descending
                Methods = OneMethod(HumanAsserted "cfo")
        }
        "period overlapping",
        {
            level2 with
                Ordering = Descending
                PeriodOverlaps = Some q2
        }
        "period overlapping nothing",
        {
            level2 with
                Ordering = Descending
                PeriodOverlaps = Some noWhen
        }
        "another hierarchy",
        {
            baseQuery with
                Hierarchy = "nowhere"
                Ordering = Descending
        }
        "a neighbouring metric",
        {
            PopulationQuery.create (MetricRef "revenue") "geography" with
                Ordering = Descending
        }
        "a registry-directed refusal", PopulationQuery.create (MetricRef "revenue") "geography"
    ]

/// The point-read matrix: every subject × metric × period clause × method
/// clause × history flag × visibility instant the seed distinguishes, and
/// the whole-scope reads.
let private pointMatrix (instants: DateTime list) : FactQuery list = [
    let paths = [
        [ "uk" ]
        [ "fr" ]
        awkwardDifferentialPath
        [ "de" ]
        [ "eu"; "sku-000003" ]
        [ "nobody" ]
    ]

    let periods = [
        None
        Some q2
        Some q3Differential
        Some unmarkedDifferential
        Some localDifferential
    ]

    let asOfs = None :: (instants |> List.map Some)

    for path in paths do
        for metric in [ "revenue"; "cost"; "elasticity" ] do
            for period in periods do
                for method' in [ None; Some rollupMethod; Some estimatorMethod ] do
                    for history in [ false; true ] do
                        for asOf in asOfs do
                            yield {
                                Subject = Some { Hierarchy = "geography"; Path = path }
                                Metric = Some(MetricRef metric)
                                PeriodOverlaps = period
                                Method = method'
                                AsOf = asOf
                                IncludeSuperseded = history
                            }

    for history in [ false; true ] do
        for asOf in asOfs do
            yield {
                FactQuery.all with
                    IncludeSuperseded = history
                    AsOf = asOf
            }

            yield {
                FactQuery.all with
                    Metric = Some(MetricRef "elasticity")
                    IncludeSuperseded = history
                    AsOf = asOf
            }
]

let private listingKey (f: Fact) =
    f.Subject.Hierarchy, f.Metric.Value, f.Period.From

let private byListingThenId (a: Fact) (b: Fact) =
    match compare (listingKey a) (listingKey b) with
    | 0 -> String.CompareOrdinal(a.FactId, b.FactId)
    | c -> c

let private expectContractOrder (label: string) (facts: Fact list) =
    Expect.equal (facts |> List.map listingKey) (facts |> List.map listingKey |> List.sort) label

/// Hold `candidate` to `reference` over one seeded fact base and every
/// query shape (Phase 888). Each factory builds a FRESH store over the
/// supplied registry and clock, and returns it with a scope to seed.
let differentialTests
    (name: string)
    (referenceFactory: IMetricRegistry option -> (unit -> DateTime) -> IFactStore * string)
    (candidateFactory: IMetricRegistry option -> (unit -> DateTime) -> IFactStore * string)
    =

    let bothOver (registry: IMetricRegistry option) = async {
        let reference, referenceScope = referenceFactory registry (differentialClock ())
        let candidate, candidateScope = candidateFactory registry (differentialClock ())
        let! referenceFacts, referenceReceipt = seedDifferential reference referenceScope
        let! candidateFacts, candidateReceipt = seedDifferential candidate candidateScope

        Expect.equal candidateFacts referenceFacts "the seed wrote the same facts, with the same transaction times"
        Expect.equal candidateReceipt referenceReceipt "the batch settled identically, digest included"

        return reference, referenceScope, candidate, candidateScope, referenceFacts
    }

    let comparePopulation (registry: IMetricRegistry option) = async {
        let! reference, referenceScope, candidate, candidateScope, facts = bothOver registry
        let midpoint = facts[12].AsOf

        let queries =
            populationMatrix
            @ (populationMatrix
               |> List.map (fun (label, q) -> label + " (as of the seed's midpoint)", PopulationQuery.asOf midpoint q))

        for label, query in queries do
            let! expected = reference.QueryPopulation(referenceScope, query)
            let! actual = candidate.QueryPopulation(candidateScope, query)
            Expect.equal actual expected (sprintf "%s — population '%s' answers identically" name label)
    }

    let comparePoints (registry: IMetricRegistry option) = async {
        let! reference, referenceScope, candidate, candidateScope, facts = bothOver registry
        let instants = [ facts[0].AsOf; facts[13].AsOf; facts[20].AsOf ]

        let renderCompetition (xs: FactWithCompetition list) =
            xs
            |> List.sortWith (fun a b -> byListingThenId a.Fact b.Fact)
            |> List.map (fun x -> x.Fact, List.sort x.CompetingMethods)

        for query in pointMatrix instants do
            let! expected = reference.Query(referenceScope, query)
            let! actual = candidate.Query(candidateScope, query)
            expectContractOrder (sprintf "%s — Query listing order for %A" name query) actual

            Expect.equal
                (List.sortWith byListingThenId actual)
                (List.sortWith byListingThenId expected)
                (sprintf "%s — Query agrees for %A" name query)

            let! expectedC = reference.QueryWithCompetition(referenceScope, query)
            let! actualC = candidate.QueryWithCompetition(candidateScope, query)

            Expect.equal
                (renderCompetition actualC)
                (renderCompetition expectedC)
                (sprintf "%s — QueryWithCompetition agrees for %A" name query)

        let! everything =
            reference.Query(
                referenceScope,
                {
                    FactQuery.all with
                        IncludeSuperseded = true
                }
            )

        Expect.isGreaterThan (List.length everything) 30 "the whole-history read saw the whole seed"

        for f in everything do
            let! expectedChain = reference.QuerySupersessionChain(referenceScope, f.FactId)
            let! actualChain = candidate.QuerySupersessionChain(candidateScope, f.FactId)
            Expect.equal actualChain expectedChain (sprintf "%s — the chain of %s agrees" name f.FactId)

            let! got = candidate.Get(candidateScope, f.FactId)
            Expect.equal got (Some f) (sprintf "%s — Get %s agrees" name f.FactId)

        let! unknown = candidate.QuerySupersessionChain(candidateScope, "no-such-fact")
        Expect.isEmpty unknown "an unknown id has no chain"
    }

    testList $"{name} — differential against the reference store (Phase 888)" [
        testCaseAsync "every population shape answers identically (registry with a canonical method)"
        <| comparePopulation (Some differentialRegistry)

        testCaseAsync "every population shape answers identically (no registry)"
        <| comparePopulation None

        testCaseAsync "every point read, competition indicator, chain and id answers identically (registry)"
        <| comparePoints (Some differentialRegistry)

        testCaseAsync "every point read, competition indicator, chain and id answers identically (no registry)"
        <| comparePoints None
    ]