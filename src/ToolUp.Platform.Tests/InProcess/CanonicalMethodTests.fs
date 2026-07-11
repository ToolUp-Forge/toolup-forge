module ToolUp.Platform.Tests.InProcess.CanonicalMethodTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Phase 566 — canonical-method selection for competing facts ───────
//
// D19 closure: several methods computing one (subject, metric, period) is
// normal and the store never merges the competitors; a metric registration
// may now declare a **canonical method** selector, and a *method-less*
// query resolves to the canonical lineage's head by default. Coverage:
// selector matching semantics, undeclared-metric parity (GP 11), the
// canonical query default, the explicit-`Method` override, the
// `IncludeSuperseded` listing staying untouched, the empty-canonical-
// lineage fallback, and the derived competing-methods indicator (GP 9).

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private metricWithCanonical (id: string) (canonical: string option) : MetricDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = HigherIsBetter
    DisplayFormat = "N0"
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = canonical
}

/// A registry declaring `revenue` canonical to the `rollup` operation
/// (any version / parameterisation) and `margin` with no declaration.
let private registry: IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "test"
            Definition = metricWithCanonical "revenue" (Some "computed:rollup")
        }
        {
            Module = "test"
            Definition = metricWithCanonical "margin" None
        }
    ] []

let private draft metricId (method: MethodRef) inputHash value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metricId
    Value = Scalar value
    Period = q2
    Method = method
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

let private rollup = Computed("rollup", "1", "p0")
let private estimator = Computed("estimator", "1", "p0")

let private newStore (registry: IMetricRegistry option) : IFactStore =
    BlobFactStore.createWithRegistry
        (InMemoryBlobStorage.InMemoryBlobStorage())
        (InMemoryEventStore.InMemoryEventStore())
        registry

let private assertOk label (store: IFactStore) scope d = async {
    let! r = store.Assert(scope, d)

    match r with
    | Ok f -> return f
    | Error e -> return failtestf "%s: expected Ok, got %s" label e
}

/// Seed one contested metric: two methods assert `revenue` over the same
/// (subject, period). Returns (store, scope, rollupFact, estimatorFact).
let private seedContested (registry: IMetricRegistry option) = async {
    let store = newStore registry
    let scope = newScope ()
    let! fRollup = assertOk "rollup" store scope (draft "revenue" rollup "hashA" 100m)
    let! fEstimator = assertOk "estimator" store scope (draft "revenue" estimator "hashB" 105m)
    return store, scope, fRollup, fEstimator
}

let private methodlessRevenue: FactQuery = {
    FactQuery.all with
        Metric = Some(MetricRef "revenue")
}

let tests =
    testList "Canonical-method selection (Phase 566)" [

        // ─── Selector matching semantics ──────────────────────────────

        test "a full method identity matches exactly" {
            Expect.isTrue (CanonicalMethod.matches "computed:rollup:1:p0" "computed:rollup:1:p0") "exact match"

            Expect.isFalse
                (CanonicalMethod.matches "computed:rollup:1:p0" "computed:rollup:2:p0")
                "a different version is a different identity"
        }

        test "a shorter selector matches at a segment boundary only" {
            Expect.isTrue
                (CanonicalMethod.matches "computed:rollup" "computed:rollup:2:p7")
                "operation selector matches every version/parameterisation"

            Expect.isFalse
                (CanonicalMethod.matches "computed:roll" "computed:rollup:1:p0")
                "a mid-segment prefix never matches"

            Expect.isFalse
                (CanonicalMethod.matches "computed:rollup" "computed:rollup-alt:1:p0")
                "a sibling operation sharing a prefix never matches"
        }

        test "asserted / imported identities are selectable" {
            Expect.isTrue (CanonicalMethod.matches "asserted:cfo" "asserted:cfo") "principal selector"
            Expect.isFalse (CanonicalMethod.matches "asserted:cfo" "asserted:cto") "a different principal misses"
            Expect.isTrue (CanonicalMethod.matches "imported:cert-1" "imported:cert-1") "certificate selector"
        }

        // ─── Undeclared-metric parity (GP 11) ─────────────────────────

        testCaseAsync "a registry-less store surfaces every competing head (pre-566 behaviour)"
        <| async {
            let! store, scope, _, _ = seedContested None
            let! heads = store.Query(scope, methodlessRevenue)
            Expect.equal heads.Length 2 "both competing heads without a registry"
        }

        testCaseAsync "a metric with no canonical declaration surfaces every competing head"
        <| async {
            let store = newStore (Some registry)
            let scope = newScope ()
            let! _ = assertOk "rollup" store scope (draft "margin" rollup "hashA" 40m)
            let! _ = assertOk "estimator" store scope (draft "margin" estimator "hashB" 42m)

            let! heads =
                store.Query(
                    scope,
                    {
                        FactQuery.all with
                            Metric = Some(MetricRef "margin")
                    }
                )

            Expect.equal heads.Length 2 "no declaration → both competing heads (undeclared parity)"
        }

        // ─── Canonical default + explicit override ────────────────────

        testCaseAsync "a method-less query returns the canonical lineage's head"
        <| async {
            let! store, scope, fRollup, _ = seedContested (Some registry)
            let! heads = store.Query(scope, methodlessRevenue)
            Expect.equal (heads |> List.map _.FactId) [ fRollup.FactId ] "the declared canonical head only"
        }

        testCaseAsync "the canonical default resolves to the lineage's *current* head after supersession"
        <| async {
            let! store, scope, _, _ = seedContested (Some registry)
            // Supersede the canonical lineage: same method, changed input.
            let! fRollup2 = assertOk "rollup v2" store scope (draft "revenue" rollup "hashC" 110m)

            let! heads = store.Query(scope, methodlessRevenue)
            Expect.equal (heads |> List.map _.FactId) [ fRollup2.FactId ] "the superseding canonical head"
        }

        testCaseAsync "an explicit Method clause overrides the canonical declaration"
        <| async {
            let! store, scope, _, fEstimator = seedContested (Some registry)

            let! heads =
                store.Query(
                    scope,
                    {
                        methodlessRevenue with
                            Method = Some estimator
                    }
                )

            Expect.equal (heads |> List.map _.FactId) [ fEstimator.FactId ] "naming the competitor selects its lineage"
        }

        // ─── Competitors stay reachable ───────────────────────────────

        testCaseAsync "an IncludeSuperseded listing is untouched by the canonical declaration"
        <| async {
            let! store, scope, fRollup, fEstimator = seedContested (Some registry)
            let! fRollup2 = assertOk "rollup v2" store scope (draft "revenue" rollup "hashC" 110m)

            let! history =
                store.Query(
                    scope,
                    {
                        methodlessRevenue with
                            IncludeSuperseded = true
                    }
                )

            Expect.equal
                (history |> List.map _.FactId |> List.sort)
                ([ fRollup.FactId; fEstimator.FactId; fRollup2.FactId ] |> List.sort)
                "the full history across every lineage"
        }

        testCaseAsync "a canonical declaration no head matches falls back to every competing head"
        <| async {
            let missingCanonical =
                MetricRegistry.build [
                    {
                        Module = "test"
                        Definition = metricWithCanonical "revenue" (Some "computed:missing")
                    }
                ] []

            let! store, scope, _, _ = seedContested (Some missingCanonical)
            let! heads = store.Query(scope, methodlessRevenue)

            Expect.equal
                heads.Length
                2
                "an empty canonical lineage surfaces the competitors rather than hiding the metric"
        }

        // ─── Competition indicator (GP 9) ─────────────────────────────

        testCaseAsync "the canonical head carries the competing method identities"
        <| async {
            let! store, scope, fRollup, _ = seedContested (Some registry)
            let! annotated = store.QueryWithCompetition(scope, methodlessRevenue)

            Expect.equal (annotated |> List.map _.Fact.FactId) [ fRollup.FactId ] "same selection as Query"

            Expect.equal
                (annotated |> List.collect _.CompetingMethods)
                [ Fact.methodIdentity estimator ]
                "the losing competitor's method identity is disclosed"
        }

        testCaseAsync "an uncontested fact carries an empty competition indicator"
        <| async {
            let store = newStore (Some registry)
            let scope = newScope ()
            let! _ = assertOk "rollup" store scope (draft "revenue" rollup "hashA" 100m)

            let! annotated = store.QueryWithCompetition(scope, methodlessRevenue)
            Expect.equal annotated.Length 1 "one head"
            Expect.isEmpty (List.head annotated).CompetingMethods "no competing method exists"
        }

        testCaseAsync "an explicit-Method query still discloses the competition"
        <| async {
            let! store, scope, _, fEstimator = seedContested (Some registry)

            let! annotated =
                store.QueryWithCompetition(
                    scope,
                    {
                        methodlessRevenue with
                            Method = Some estimator
                    }
                )

            Expect.equal (annotated |> List.map _.Fact.FactId) [ fEstimator.FactId ] "the named lineage's head"

            Expect.equal
                (annotated |> List.collect _.CompetingMethods)
                [ Fact.methodIdentity rollup ]
                "the other current method is disclosed even under an explicit selection"
        }
    ]