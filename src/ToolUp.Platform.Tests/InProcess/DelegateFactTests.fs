// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DelegateFactTests

// ─── Phase 889 — the delegate fact ───────────────────────────────────
//
// What this pack pins, one list each:
//
//   1. **Equivalence.** Over one seeded population, a delegated store and a
//      plain one (the default writer's facts) return the same ranking —
//      subjects, values AND fact ids — the same statistics and the same
//      point reads.
//   2. **Only what is quoted becomes a fact.** Over a 100,000-subject table
//      a top-ten question mints at most ten facts, no subject that was never
//      quoted has one, and a row minted twice is one fact.
//   3. **Quoting.** A delegated value passes the numeric-fidelity gate, and
//      the quoted fact's provenance names the run that produced it; the
//      minted fact carries its column's disclosure from birth.
//   4. **History.** An append-by-run table answers `AsOf` from the run that
//      was current then, without moving the current head; a replace table
//      refuses `AsOf` on both doors, with a reason, and never approximates.
//   5. **Refresh.** A commit advances the delegate record and supersedes
//      every fact quoted from the previous run — a changed row to its new
//      value, a removed row to `Absent` — and the next question quotes the
//      new values. The read path alone also never serves a stale head.
//   6. **Whole-store walks.** Coherence checking asks the table for
//      per-parent totals (and a quoted sample raises no false partial load);
//      the coverage narrative reads the last run's reach and mints nothing.
//   7. **Composition.** No delegate is the same app; `withDelegateFacts`
//      decorates the store and the writer in DI, end to end.

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI.AnswerVerifier
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Fixtures ────────────────────────────────────────────────────────

let private t0 = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private resolved (scopeId: string) : ResolvedScope =
    ScopeResolution.ofStorageScope {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

let private metric (id: string) (rollUp: RollUp option) : MetricDefinition = {
    Id = id
    Name = id
    Unit = "count"
    Dimensionality = "count"
    Direction = HigherIsBetter
    DisplayFormat = ""
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = rollUp
    Context = None
}

let private products: SubjectDefinition = {
    Id = "products"
    Name = "Products"
    Levels = [ "brand"; "sku" ]
    Calendar = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build [
        {
            MetricRegistration.Module = "sales"
            Definition = metric "revenue" (Some(Additive 0.01m))
        }
        {
            MetricRegistration.Module = "sales"
            Definition = metric "segment" None
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = products
        }
    ]

let private skuTable (history: FactTableHistoryMode) : FactTableDefinition = {
    Id = "sku-sales"
    SchemaVersion = 1
    Hierarchy = "products"
    Level = "sku"
    Columns = [
        FactTableDefinition.column "revenue" FactTableValueShape.Scalar
        {
            (FactTableDefinition.column "segment" FactTableValueShape.Categorical) with
                Disclosure = Some FactTableDisclosure.Internal
        }
    ]
    PeriodGrain = FactTablePeriodGrain.Month
    ProducingOperation = "sales-rollup"
    RefreshCadence = TimeSpan.FromDays 1.0
    HistoryMode = history
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private tablesFor (history: FactTableHistoryMode) (destination: string) : IFactTableRegistry =
    FactTableRegistry.build [
        {
            FactTableRegistration.Module = "sales"
            Definition = skuTable history
        }
    ] [ BindFactTable("sku-sales", destination) ]

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private row (brand: string) (sku: string) (revenue: decimal) : FactTableRow = {
    Subject = [ brand; sku ]
    Period = september
    Values = Map.ofList [ "revenue", Scalar revenue; "segment", Categorical "core" ]
}

/// A population with ties (the ranking's tiebreak is load-bearing) across
/// ten brands.
let private population (n: int) : FactTableRow list = [
    for i in 1..n -> row (sprintf "brand-%02d" (i % 10)) (sprintf "sku-%06d" i) (decimal ((i * 37) % 97))
]

/// A clock that moves one second per reading.
let private tickingClock () : unit -> DateTime =
    let ticks = ref 0

    fun () ->
        ticks.Value <- ticks.Value + 1
        t0.AddSeconds(float ticks.Value)

type private World = {
    Writer: IFactTableWriter
    /// The composed store — the delegated decorator, or the plain store.
    Store: IFactStore
    /// The store under the decorator — what the fact tier actually holds.
    Inner: IFactStore
    Delegated: DelegatedFactStore option
    Events: IEventStore
    Storage: IBlobStorage
    Scope: ResolvedScope
}

let private plainWorld (history: FactTableHistoryMode) : World =
    let clock = tickingClock ()
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let store =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    {
        Writer =
            DefaultFactTableWriter.createWithClock
                store
                storage
                events
                (tablesFor history DefaultFactTableWriter.Destination)
                (Some registry)
                clock
        Store = store
        Inner = store
        Delegated = None
        Events = events
        Storage = storage
        Scope = resolved (newScope ())
    }

let private delegatedWorldWith (history: FactTableHistoryMode) (refresh: bool) : World =
    let clock = tickingClock ()
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let inner =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let tables = tablesFor history DelegateFact.Destination

    let delegates =
        match DelegateFacts.resolve tables (Some registry) [ "sku-sales" ] with
        | Ok ds -> ds
        | Error e -> failtestf "resolve: %s" e

    let delegated =
        DelegatedFactStore.create inner storage delegates (Some registry) clock

    let composed = delegated :> IFactStore

    {
        Writer =
            DelegatedFactStore.writer
                None
                storage
                events
                tables
                (Some registry)
                delegates
                (fun () -> if refresh then Some composed else None)
                clock
        Store = composed
        Inner = inner
        Delegated = Some delegated
        Events = events
        Storage = storage
        Scope = resolved (newScope ())
    }

let private delegatedWorld (history: FactTableHistoryMode) = delegatedWorldWith history true

/// Open, stage (in batches) and commit.
let private commit (w: World) (rows: FactTableRow list) : string * FactTableCommit =
    let opened =
        match w.Writer.OpenRun(w.Scope.ScopeId, "sku-sales") |> Async.RunSynchronously with
        | Ok r -> r
        | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

    for batch in rows |> List.chunkBySize 10_000 do
        match
            w.Writer.WriteRows(w.Scope.ScopeId, opened.RunId, batch)
            |> Async.RunSynchronously
        with
        | Ok _ -> ()
        | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

    match w.Writer.Commit(w.Scope.ScopeId, opened.RunId) |> Async.RunSynchronously with
    | Ok c -> opened.RunId, c
    | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)

let private top (w: World) (k: int) (configure: PopulationQuery -> PopulationQuery) : PopulationResult =
    let q =
        {
            PopulationQuery.create (MetricRef "revenue") "products" with
                TopK = k
                Ordering = Descending
        }
        |> configure

    match w.Store.QueryPopulation(w.Scope, q) |> Async.RunSynchronously with
    | Ok r -> r
    | Error e -> failtestf "population: %s" e

let private point (w: World) (metricId: string) (path: string list) : Fact list =
    w.Store.Query(w.Scope, FactQuery.forSubjectMetric { Hierarchy = "products"; Path = path } (MetricRef metricId))
    |> Async.RunSynchronously

/// Every fact the fact tier holds for a metric at the sku level (history
/// included) — asked of the INNER store, so nothing is minted by asking.
let private held (w: World) (metricId: string) : Fact list =
    w.Inner.Query(
        w.Scope,
        {
            FactQuery.all with
                Metric = Some(MetricRef metricId)
                IncludeSuperseded = true
        }
    )
    |> Async.RunSynchronously
    |> List.filter (fun f -> f.Subject.Path.Length = 2)

let private valueOf (f: Fact) =
    match f.Value with
    | Scalar d -> d
    | other -> failtestf "expected a scalar, got %A" other

// ─── 1. Equivalence ──────────────────────────────────────────────────

let private equivalenceTests =
    testList "decorator versus plain store over one seeded population" [
        test "the ranking — subjects, values and fact ids — and the statistics are the plain store's" {
            let rows = population 500
            let plain = plainWorld FactTableHistoryMode.AppendByRun
            let delegated = delegatedWorld FactTableHistoryMode.AppendByRun
            commit plain rows |> ignore
            commit delegated rows |> ignore

            for configure in
                [
                    id
                    (fun q -> { q with Ordering = Ascending })
                    (fun q -> {
                        q with
                            PathPrefix = Some [ "brand-03" ]
                    })
                    (fun q -> { q with Threshold = Some(AtLeast 50m) })
                    (fun q -> { q with Level = Some 2 })
                ] do
                let p = top plain 10 configure
                let d = top delegated 10 configure

                let shape (r: PopulationResult) =
                    r.Ranked |> List.map (fun f -> f.FactId, f.Subject, f.Value)

                Expect.equal (shape d) (shape p) "same ranking, same ids"
                Expect.equal d.Truncated p.Truncated "same truncation"
                Expect.equal d.EffectiveTopK p.EffectiveTopK "same effective top-k"
                Expect.equal d.Direction p.Direction "same direction"
                Expect.equal d.Stats p.Stats "same statistics"
        }

        test "a point read quotes the value the plain store holds, under the same id" {
            let rows = population 50
            let plain = plainWorld FactTableHistoryMode.AppendByRun
            let delegated = delegatedWorld FactTableHistoryMode.AppendByRun
            commit plain rows |> ignore
            commit delegated rows |> ignore

            for path in [ [ "brand-01"; "sku-000001" ]; [ "brand-07"; "sku-000017" ] ] do
                for metricId in [ "revenue"; "segment" ] do
                    let p =
                        point plain metricId path |> List.map (fun f -> f.FactId, f.Value, f.Disclosure)

                    let d =
                        point delegated metricId path
                        |> List.map (fun f -> f.FactId, f.Value, f.Disclosure)

                    Expect.equal d p (sprintf "%s of %A" metricId path)
        }

        test "an undelegated read passes through untouched" {
            let w = delegatedWorld FactTableHistoryMode.AppendByRun
            commit w (population 20) |> ignore

            let draft: FactDraft = {
                Subject = {
                    Hierarchy = "products"
                    Path = [ "brand-01" ]
                }
                Metric = MetricRef "revenue"
                Value = Scalar 5m
                Period = september
                Method = HumanAsserted "analyst"
                Evidence = {
                    ResultRef = None
                    InputHashes = []
                    TriggerRef = None
                }
                Confidence = None
                Disclosure = Surfaceable
            }

            let asserted =
                match w.Store.Assert(w.Scope, draft) |> Async.RunSynchronously with
                | Ok f -> f
                | Error e -> failtestf "assert: %s" e

            Expect.equal
                (point w "revenue" [ "brand-01" ] |> List.map _.FactId)
                [ asserted.FactId ]
                "a brand-level read"

            let population = top w 10 (fun q -> { q with Level = Some 1 })

            Expect.equal (population.Ranked |> List.map _.FactId) [ asserted.FactId ] "another level's population"
        }
    ]

// ─── 2. Only what is quoted becomes a fact ───────────────────────────

let private quotedOnlyTests =
    testList "only what is quoted becomes a fact" [
        test "over a 100,000-subject table a top-ten question mints at most ten facts" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            let _, c = commit w (population 100_000)
            Expect.equal c.RowCount 100_000 "the run holds the population"
            Expect.equal c.FactsWritten 0 "and wrote no fact"
            Expect.isEmpty (held w "revenue") "nothing is held before a question"

            let r = top w 10 id
            Expect.equal r.Ranked.Length 10 "a top ten"
            Expect.equal r.Stats.FactCount 100_000 "the summary is of the whole population"
            Expect.isTrue r.Truncated "the ranking was bounded"

            let minted = held w "revenue"
            Expect.isLessThanOrEqual minted.Length 10 "at most ten facts minted"

            Expect.equal
                (minted |> List.map _.FactId |> Set.ofList)
                (r.Ranked |> List.map _.FactId |> Set.ofList)
                "exactly the quoted rows"

            let quoted = r.Ranked |> List.map _.Subject.Path |> Set.ofList

            let unquoted =
                [
                    for i in 1..100_000 -> [ sprintf "brand-%02d" (i % 10); sprintf "sku-%06d" i ]
                ]
                |> List.find (fun p -> not (quoted.Contains p))

            let asked =
                w.Inner.Query(
                    w.Scope,
                    FactQuery.forSubjectMetric
                        {
                            Hierarchy = "products"
                            Path = unquoted
                        }
                        (MetricRef "revenue")
                )
                |> Async.RunSynchronously

            Expect.isEmpty asked "a subject never quoted has no fact"
        }

        test "a row minted twice is one fact" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            commit w (population 100) |> ignore
            let first = top w 5 id
            let second = top w 5 id
            let again = point w "revenue" first.Ranked.Head.Subject.Path

            Expect.equal (second.Ranked |> List.map _.FactId) (first.Ranked |> List.map _.FactId) "the same ids"
            Expect.equal (again |> List.map _.FactId) [ first.Ranked.Head.FactId ] "a point read of a ranked row"
            Expect.equal (held w "revenue").Length 5 "five facts, however often asked"
        }

        test "the delegate is one record in the fact tier, pointing at the run" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            let _, c = commit w (population 10)

            let records =
                w.Inner.Query(
                    w.Scope,
                    FactQuery.forSubjectMetric { Hierarchy = "products"; Path = [] } (MetricRef "revenue")
                )
                |> Async.RunSynchronously

            Expect.equal
                (records |> List.map _.Value)
                [ Series(DelegateFact.token c.Watermark) ]
                "one record, naming the run"

            let current =
                match w.Delegated.Value.Current(w.Scope.ScopeId) |> Async.RunSynchronously with
                | Ok ds -> ds
                | Error e -> failtestf "current: %s" (DelegateRefusal.describe e)

            Expect.equal
                (current |> List.map (fun d -> d.Metric.Value, d.Watermark))
                [ "revenue", Some c.Watermark; "segment", Some c.Watermark ]
                "the records advanced to the run"
        }
    ]

// ─── 3. Quoting ──────────────────────────────────────────────────────

let private quotingTests =
    testList "a quoted number has a fact id and a provenance" [
        test "the numeric-fidelity gate passes an answer quoting a delegated value" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            let runId, c = commit w [ row "acme" "a1" 1234.5m; row "acme" "a2" 20m ]
            let best = (top w 1 id).Ranked.Head

            let verdict =
                NumericFidelity.verify
                    "The best seller made 1234.5 last month."
                    [
                        {
                            FactId = best.FactId
                            Rendering = FactRendering.render "" best.Value
                            Metric = "revenue"
                        }
                    ]
                    (Some registry)
                    "Annotate"

            Expect.equal verdict.Verified 1 "the quoted number is verified"
            Expect.equal verdict.Unmatched 0 "and nothing is unmatched"

            Expect.equal (verdict.Numbers |> List.choose _.MatchedFactId) [ best.FactId ] "against the minted fact's id"

            // The provenance chain reaches the run that produced the row.
            let stored =
                w.Store.Get(w.Scope, best.FactId)
                |> Async.RunSynchronously
                |> Option.defaultWith (fun () -> failtest "the quoted fact is stored")

            Expect.equal stored.Evidence.ResultRef (Some(DelegateFact.token c.Watermark)) "the run's watermark"
            Expect.contains stored.Evidence.InputHashes (DelegateFact.token c.Watermark) "is an input"
            Expect.equal stored.Evidence.TriggerRef (Some(DelegateMint.triggerRef "sku-sales" runId)) "names the run"

            Expect.equal
                stored.Method
                (DelegateFact.methodOf (skuTable FactTableHistoryMode.Replace))
                "the table's method"
        }

        test "a minted fact carries its column's disclosure from birth" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            commit w [ row "acme" "a1" 1m ] |> ignore
            let segment = point w "segment" [ "acme"; "a1" ]
            let revenue = point w "revenue" [ "acme"; "a1" ]
            Expect.equal (segment |> List.map _.Disclosure) [ Disclosure.Internal ] "the column's class"
            Expect.equal (revenue |> List.map _.Disclosure) [ Surfaceable ] "the table's class"

            let gate = FactDisclosureGate.create w.Store w.Events

            let verdicts =
                gate.Check(w.Scope, "reader", FactToolResult, (segment @ revenue) |> List.map _.FactId)
                |> Async.RunSynchronously

            Expect.equal verdicts[segment.Head.FactId] (FactNotDisclosable "Internal") "the gate reads the class"
            Expect.equal verdicts[revenue.Head.FactId] FactDisclosable "and discloses a surfaceable quote"
        }
    ]

// ─── 4. History ──────────────────────────────────────────────────────

let private historyTests =
    testList "history" [
        test "an append-by-run table answers AsOf from the run current then, and the head does not move" {
            let w = delegatedWorld FactTableHistoryMode.AppendByRun
            let _, first = commit w [ row "acme" "a1" 10m; row "acme" "a2" 5m ]
            let _, second = commit w [ row "acme" "a1" 30m; row "acme" "a2" 5m ]
            let between = first.Watermark.CommittedAt.AddTicks 1L

            let asOf =
                w.Store.Query(
                    w.Scope,
                    FactQuery.forSubjectMetric
                        {
                            Hierarchy = "products"
                            Path = [ "acme"; "a1" ]
                        }
                        (MetricRef "revenue")
                    |> FactQuery.asOf between
                )
                |> Async.RunSynchronously

            Expect.equal (asOf |> List.map valueOf) [ 10m ] "the first run's value"
            Expect.contains asOf.Head.Evidence.InputHashes (DelegateFact.token first.Watermark) "named by its run"

            Expect.equal
                (w.Store.Get(w.Scope, asOf.Head.FactId)
                 |> Async.RunSynchronously
                 |> Option.map _.FactId)
                (Some asOf.Head.FactId)
                "resolvable by id, so the gate can judge it"

            let ranked = top w 1 (PopulationQuery.asOf between)
            Expect.equal (ranked.Ranked |> List.map valueOf) [ 10m ] "the population door agrees"

            let now = point w "revenue" [ "acme"; "a1" ]
            Expect.equal (now |> List.map valueOf) [ 30m ] "the current head is still the second run's"
            Expect.contains now.Head.Evidence.InputHashes (DelegateFact.token second.Watermark) "named by its run"
        }

        test "a replace table refuses AsOf on both doors, naming the reason, and never approximates" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            let _, c = commit w [ row "acme" "a1" 10m ]
            let t = c.Watermark.CommittedAt.AddSeconds 1.0

            let q =
                {
                    PopulationQuery.create (MetricRef "revenue") "products" with
                        Ordering = Descending
                }
                |> PopulationQuery.asOf t

            match w.Store.QueryPopulation(w.Scope, q) |> Async.RunSynchronously with
            | Ok r -> failtestf "expected a refusal, got %d ranked" r.Ranked.Length
            | Error reason ->
                Expect.stringContains reason "Approximate" "the population door names the history"
                Expect.stringContains reason "sku-sales" "and the table"

            let pointed =
                w.Store.Query(
                    w.Scope,
                    FactQuery.forSubjectMetric
                        {
                            Hierarchy = "products"
                            Path = [ "acme"; "a1" ]
                        }
                        (MetricRef "revenue")
                    |> FactQuery.asOf t
                )
                |> Async.RunSynchronously

            match pointed |> List.map (fun f -> f, f.Value) with
            | [ refused, Absent reason ] ->
                Expect.stringContains reason "Approximate" "the point door says why"

                Expect.isSome
                    (w.Store.Get(w.Scope, refused.FactId) |> Async.RunSynchronously)
                    "and the refusal is resolvable by id"
            | other -> failtestf "expected one Absent refusal, got %A" (other |> List.map snd)

            Expect.isEmpty (held w "revenue") "a refusal mints nothing"
        }
    ]

// ─── 5. Refresh ──────────────────────────────────────────────────────

let private refreshTests =
    testList "refresh" [
        test "a commit supersedes every fact quoted from the previous run" {
            let w = delegatedWorld FactTableHistoryMode.Replace

            commit w [ row "acme" "a1" 10m; row "acme" "a2" 20m; row "acme" "a3" 30m ]
            |> ignore

            let quotedA1 = point w "revenue" [ "acme"; "a1" ] |> List.exactlyOne
            let quotedA2 = point w "revenue" [ "acme"; "a2" ] |> List.exactlyOne

            // a1 changes, a2 is removed, a3 is unchanged and was never quoted.
            let _, second = commit w [ row "acme" "a1" 15m; row "acme" "a3" 30m ]

            // Read the INNER store: the refresh, not a new question, moved it.
            let headOf path =
                w.Inner.Query(
                    w.Scope,
                    FactQuery.forSubjectMetric { Hierarchy = "products"; Path = path } (MetricRef "revenue")
                )
                |> Async.RunSynchronously
                |> List.exactlyOne

            let a1 = headOf [ "acme"; "a1" ]
            Expect.equal (valueOf a1) 15m "a changed row's quote reads the new value"
            Expect.equal a1.Supersedes (Some quotedA1.FactId) "and supersedes the old quote"

            let a2 = headOf [ "acme"; "a2" ]

            match a2.Value with
            | Absent reason -> Expect.stringContains reason (DelegateFact.token second.Watermark) "names the run"
            | other -> failtestf "a removed row's quote should read Absent, got %A" other

            Expect.equal a2.Supersedes (Some quotedA2.FactId) "superseding the old quote"

            let chain =
                w.Store.QuerySupersessionChain(w.Scope, quotedA1.FactId)
                |> Async.RunSynchronously
                |> List.map _.FactId

            Expect.equal chain [ quotedA1.FactId; a1.FactId ] "the chain runs old to new"

            Expect.equal
                (point w "revenue" [ "acme"; "a1" ] |> List.map valueOf)
                [ 15m ]
                "a repeated question quotes the new value"

            Expect.isEmpty
                (held w "revenue" |> List.filter (fun f -> f.Subject.Path = [ "acme"; "a3" ]))
                "an unquoted row is still not a fact"
        }

        test "the delegated invalidation walk finds exactly the quotes of a superseded run" {
            // No refresh wired: the walk is what a refresh would run.
            let w = delegatedWorldWith FactTableHistoryMode.Replace false
            commit w [ row "acme" "a1" 10m; row "acme" "a2" 20m ] |> ignore
            let quoted = point w "revenue" [ "acme"; "a1" ] |> List.exactlyOne
            let _, second = commit w [ row "acme" "a1" 11m; row "acme" "a2" 20m ]

            let d =
                w.Delegated.Value.Delegates
                |> List.find (fun d -> d.Metric = MetricRef "revenue")

            let stale =
                FactInvalidation.staleDelegatedHeads w.Inner w.Scope.ScopeId d (DelegateFact.token second.Watermark)
                |> Async.RunSynchronously

            Expect.equal (stale |> List.map _.FactId) [ quoted.FactId ] "the one stale quote"

            // Even without the refresh, the read path never serves it.
            Expect.equal (point w "revenue" [ "acme"; "a1" ] |> List.map valueOf) [ 11m ] "the current value"
        }

        test "without a refresh, a removed row's stale quote is retired on read" {
            let w = delegatedWorldWith FactTableHistoryMode.Replace false
            commit w [ row "acme" "a1" 10m; row "acme" "a2" 20m ] |> ignore
            point w "revenue" [ "acme"; "a2" ] |> ignore
            commit w [ row "acme" "a1" 10m ] |> ignore

            match point w "revenue" [ "acme"; "a2" ] |> List.map _.Value with
            | [ Absent _ ] -> ()
            | other -> failtestf "expected the stale quote retired to Absent, got %A" other
        }
    ]

// ─── 6. Whole-store walks ────────────────────────────────────────────

let private brandFact (w: World) (brand: string) (value: decimal) =
    let draft: FactDraft = {
        Subject = {
            Hierarchy = "products"
            Path = [ brand ]
        }
        Metric = MetricRef "revenue"
        Value = Scalar value
        Period = september
        Method = Computed("brand-rollup", "v1", "")
        Evidence = {
            ResultRef = None
            InputHashes = [ "brand-load" ]
            TriggerRef = None
        }
        Confidence = None
        Disclosure = Surfaceable
    }

    match w.Store.Assert(w.Scope, draft) |> Async.RunSynchronously with
    | Ok _ -> ()
    | Error e -> failtestf "assert: %s" e

let private walkTests =
    testList "whole-store walks take the delegated path" [
        test "coherence asks the table for per-parent totals, and a quoted sample is not a partial load" {
            let w = delegatedWorld FactTableHistoryMode.Replace

            commit w [ row "acme" "a1" 10m; row "acme" "a2" 20m; row "zeta" "z1" 5m ]
            |> ignore

            brandFact w "acme" 30m // reconciles with its children
            brandFact w "zeta" 9m // does not
            // Quote one acme child: the fact tier now holds a SAMPLE of acme.
            point w "revenue" [ "acme"; "a1" ] |> ignore

            let findings =
                CoherenceCheck.findings w.Store (Some registry) CoherenceConfig.defaults w.Scope.ScopeId
                |> Async.RunSynchronously

            Expect.equal
                (findings |> List.map (fun f -> f.Subject.Path, f.Expected, f.Found))
                [ [ "zeta" ], 5m, 9m ]
                "one finding"

            Expect.equal findings.Head.ChildCount 1 "counted by the table"
            Expect.equal (held w "revenue").Length 1 "the walk minted nothing"
        }

        test "coverage is generated from the declaration and the last run, with no population read" {
            let w = delegatedWorld FactTableHistoryMode.Replace
            let _, c = commit w (population 1_000)
            let gate = FactDisclosureGate.create w.Store w.Events

            let coverage =
                CoverageNarrative.readCoverageWith
                    (Some(w.Delegated.Value :> IDelegatedFactWalks))
                    (t0.AddDays 1.0)
                    w.Store
                    gate
                    w.Scope.ScopeId
                    "reader"
                    (registry.TryGetMetric "revenue").Value
                    [ products ]
                |> Async.RunSynchronously

            match coverage.Populations with
            | [ p ] ->
                let stats =
                    p.Stats
                    |> Option.defaultWith (fun () -> failtest "a surfaceable population is described")

                Expect.equal stats.FactCount c.RowCount "the run's row count"
                Expect.equal stats.SubjectCount 1_000 "its subjects"
                Expect.equal stats.ComparableCount 1_000 "its comparable cells"
                Expect.equal stats.PeriodFrom (Some september.From) "its period reach"
                Expect.equal stats.PeriodTo (Some september.To) "to the end"
                Expect.isEmpty p.Cited "and cites nothing it would have to mint"
            | other -> failtestf "expected one population, got %d" other.Length

            Expect.isEmpty (held w "revenue") "nothing was minted"

            let segment =
                CoverageNarrative.readCoverageWith
                    (Some(w.Delegated.Value :> IDelegatedFactWalks))
                    (t0.AddDays 1.0)
                    w.Store
                    gate
                    w.Scope.ScopeId
                    "reader"
                    (registry.TryGetMetric "segment").Value
                    [ products ]
                |> Async.RunSynchronously

            match segment.Populations with
            | [ p ] ->
                Expect.equal p.Posture (CoverageNarrative.WhollyRestricted [ "Internal" ]) "an internal column"
                Expect.isNone p.Stats "is not described"
            | other -> failtestf "expected one population, got %d" other.Length
        }
    ]

// ─── 7. Composition ──────────────────────────────────────────────────

let private enabledApp () = {
    ServerApp.empty with
        Config = {
            ServerConfig.defaults with
                FactStore = EnabledFactStore
        }
}

let private compositionTests =
    testList "composition" [
        test "a composition with no delegate is the same app" {
            let app = enabledApp ()
            Expect.isTrue (obj.ReferenceEquals(app, FactsCompose.withDelegateFacts [] app)) "no table named"
            let off = ServerApp.empty
            Expect.isTrue (obj.ReferenceEquals(off, FactsCompose.withDelegateFacts [ "sku-sales" ] off)) "no fact store"
        }

        test "withDelegateFacts binds the table and decorates the store and the writer, end to end" {
            let app = FactsCompose.withDelegateFacts [ "sku-sales" ] (enabledApp ())

            Expect.contains
                app.FactTableBindings
                (BindFactTable("sku-sales", DelegateFact.Destination))
                "the table is bound to the delegate destination"

            let clock = tickingClock ()
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let inner =
                BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

            let tables = tablesFor FactTableHistoryMode.Replace DelegateFact.Destination
            let services = ServiceCollection() :> IServiceCollection
            services.AddSingleton<IBlobStorage>(storage) |> ignore
            services.AddSingleton<IEventStore>(events) |> ignore
            services.AddSingleton<IMetricRegistry>(registry) |> ignore
            services.AddSingleton<IFactTableRegistry>(tables) |> ignore
            services.AddSingleton<IFactStore>(inner) |> ignore

            match app.Extensions.ServiceConfig with
            | Some configure -> configure services |> ignore
            | None -> failtest "the decoration must be registered"

            let sp = services.BuildServiceProvider()
            let store = sp.GetRequiredService<IFactStore>()
            let writer = sp.GetRequiredService<IFactTableWriter>()
            Expect.isTrue (store :? IDelegatedFactWalks) "the store is decorated"

            let w = {
                Writer = writer
                Store = store
                Inner = inner
                Delegated = Some(sp.GetRequiredService<DelegatedFactStore>())
                Events = events
                Storage = storage
                Scope = resolved (newScope ())
            }

            let _, c = commit w (population 200)
            Expect.equal c.FactsWritten 0 "the composed writer holds the table"
            let r = top w 3 id
            Expect.equal r.Stats.FactCount 200 "the composed store answers from it"
            Expect.equal (held w "revenue").Length 3 "minting only the quoted rows"
        }

        // Phase 946 — the two ways a table cannot be delegated surface at
        // the fact-table preflight, not at the first DI resolution.
        test "a delegated table no module declares fails the fact-table preflight (Phase 946)" {
            let app = FactsCompose.withDelegateFacts [ "undeclared-table" ] (enabledApp ())
            let composition = ServerApp.factTableComposition app

            let undeclared =
                FactTablePreflight.defects composition
                |> List.filter (fun d -> d.RuleCode = FactTablePreflight.UndeclaredBindingRule)

            match undeclared with
            | [ d ] ->
                Expect.equal d.Severity DefectError "an error: the composition refuses to start"
                Expect.stringContains d.Message "undeclared-table" "the defect names the table"
            | other -> failtestf "expected one undeclared-binding defect, got %A" other

            // The validator is registered although the app declares no table:
            // a binding alone is enough for the preflight to run.
            let services = ServiceCollection() :> IServiceCollection
            FactTablePreflight.serviceRegistration composition services |> ignore

            let validators =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<ConfigValidation.IConfigValidator>)
                |> Seq.length

            Expect.equal validators 1 "the fact-table preflight runs"
        }

        test "a delegated table over an unregistered hierarchy fails the fact-table preflight (Phase 946)" {
            let geo = {
                skuTable FactTableHistoryMode.Replace with
                    Id = "geo-sales"
                    Hierarchy = "geo"
            }

            let app =
                enabledApp ()
                |> ServerApp.addModules [ ServerModule.create "sales" |> ServerModule.declareFactTables [ geo ] ]
                |> FactsCompose.withDelegateFacts [ "geo-sales" ]

            let codes =
                FactTablePreflight.defects (ServerApp.factTableComposition app)
                |> List.map (fun d -> d.RuleCode, d.Severity)

            Expect.contains
                codes
                (FactTablePreflight.UnknownSubjectLevelRule, DefectError)
                "the unregistered hierarchy is named before any resolution"
        }
    ]

// ─── 8. Imported runs (Phase 938) ────────────────────────────────────

let private kept (w: World) (prefix: string) : string list =
    w.Storage.List(w.Scope.ScopeId, "_delegate-tables/" + prefix)
    |> Async.RunSynchronously

let private importedTests =
    testList "imported runs (Phase 938)" [

        test "a computed run keeps no provenance and records no lineage (GP 11)" {
            let w = delegatedWorld FactTableHistoryMode.AppendByRun
            commit w (population 20) |> ignore
            point w "revenue" [ "brand-01"; "sku-000001" ] |> ignore

            Expect.isEmpty (kept w "provenance/") "no provenance for a computed run"
            Expect.isEmpty (kept w "lineages/") "and no imported lineage"
        }

        test "an imported run keeps its provenance once committed, and an abandoned one drops it" {
            let w = delegatedWorld FactTableHistoryMode.AppendByRun

            let imported =
                ImportedRun [
                    {
                        RootMember = "brand-01"
                        CertificateRef = "cert:brand-01"
                        TriggerRef = "import:brand-01"
                        Withdrawal = None
                        Cells = []
                    }
                ]

            let openImported () =
                match
                    w.Writer.OpenRun(w.Scope.ScopeId, "sku-sales", imported)
                    |> Async.RunSynchronously
                with
                | Ok run -> run
                | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

            let committed = openImported ()

            w.Writer.WriteRows(w.Scope.ScopeId, committed.RunId, population 20)
            |> Async.RunSynchronously
            |> Result.defaultWith (fun e -> failtestf "write: %s" (FactTableWriteError.describe e))
            |> ignore

            w.Writer.Commit(w.Scope.ScopeId, committed.RunId)
            |> Async.RunSynchronously
            |> Result.defaultWith (fun e -> failtestf "commit: %s" (FactTableWriteError.describe e))
            |> ignore

            Expect.hasLength (kept w "provenance/") 1 "a committed run's provenance stays: its rows are minted under it"
            Expect.hasLength (kept w "lineages/") 1 "and the table records the lineage it imported under"

            match point w "revenue" [ "brand-01"; "sku-000001" ] with
            | [ fact ] -> Expect.equal fact.Method (Imported "cert:brand-01") "the origin's row mints Imported"
            | other -> failtestf "expected one fact, got %d" other.Length

            match point w "revenue" [ "brand-02"; "sku-000002" ] with
            | [ fact ] ->
                Expect.equal fact.Method (Computed("sales-rollup", "v1", "sku-sales")) "a row under no origin does not"
            | other -> failtestf "expected one fact, got %d" other.Length

            let abandoned = openImported ()
            Expect.hasLength (kept w "provenance/") 2 "kept while open"

            w.Writer.Abandon(w.Scope.ScopeId, abandoned.RunId, "changed my mind")
            |> Async.RunSynchronously
            |> Result.defaultWith (fun e -> failtestf "abandon: %s" (FactTableWriteError.describe e))
            |> ignore

            Expect.hasLength (kept w "provenance/") 1 "an abandoned run's provenance goes with it"
        }
    ]

/// Every Phase 889 case.
let tests =
    testList "Phase 889 — delegate facts" [
        equivalenceTests
        quotedOnlyTests
        quotingTests
        historyTests
        refreshTests
        walkTests
        compositionTests
        importedTests
    ]