module ToolUp.Platform.Tests.Contracts.IFactStoreContract

open System
open Expecto
open ToolUp.Facts

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
    ]