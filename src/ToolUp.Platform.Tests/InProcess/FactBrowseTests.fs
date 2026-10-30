// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactBrowseTests

// ─── Phase 895 — the fact browse surface, server side ────────────────
//
// What this pack pins, one list each:
//
//   1. **Bounded.** Over a 100,000-subject table no response carries more
//      rows than the page ceiling, and every page reports the ceiling.
//   2. **Gated at its own door.** Every served fact is judged at the
//      `FactBrowse` surface; a denied row is absent, counted by policy,
//      and its value, subject and id appear in no response.
//   3. **One query surface.** The drill-down's ranking for a metric is
//      the population tool's ranking for the same question.
//   4. **Tables and runs, not facts.** A table with no runs reads as
//      never refreshed; run history pages; one run's movers are gated;
//      one fact opens its table, its run and its supersession chain.
//   5. **Per run, never per fact.** A committed run publishes exactly one
//      notification, carrying counts and no value.
//   6. **The shared names and the composition.** The link names other
//      companions use match their producers, and a composition without
//      the fact store is unchanged by `withFactBrowse`.
//
// ─── Phase 1018 — the composed demo ──────────────────────────────────
//
//   7. **All four at once.** One composed app holds the whole
//      `grounding-scale-fact-tables` criterion as a single scenario, so
//      the criterion has an oracle rather than four lists a judge would
//      have to conjoin on its behalf.

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AnswerVerifier
open ToolUp.AI.AIToolRegistry
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Fixtures ────────────────────────────────────────────────────────

let private t0 = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)

let private resolved (scopeId: string) : ResolvedScope =
    ScopeResolution.ofStorageScope {
        ScopeId = scopeId
        Container = "container-" + scopeId
        Persist = true
    }

let private newScope () = "team-" + Guid.NewGuid().ToString("N")

let private metric (id: string) : MetricDefinition = {
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
    RollUp = None
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
            Definition = metric "revenue"
        }
        {
            MetricRegistration.Module = "sales"
            Definition = metric "segment"
        }
    ] [
        {
            SubjectRegistration.Module = "sales"
            Definition = products
        }
    ]

let private skuTable: FactTableDefinition = {
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
    HistoryMode = FactTableHistoryMode.Replace
    Disclosure = FactTableDisclosure.Surfaceable
    Requirement = FactTableRequirement.Optional
}

let private tables: IFactTableRegistry =
    FactTableRegistry.build [
        {
            FactTableRegistration.Module = "sales"
            Definition = skuTable
        }
    ] [ BindAllFactTables DefaultFactTableWriter.Destination ]

let private september: TemporalExtent = {
    From = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-09"
}

let private october: TemporalExtent = {
    From = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "2026-10"
}

let private rowIn (period: TemporalExtent) (brand: string) (sku: string) (revenue: decimal) : FactTableRow = {
    Subject = [ brand; sku ]
    Period = period
    Values = Map.ofList [ "revenue", Scalar revenue; "segment", Categorical "core" ]
}

let private row (brand: string) (sku: string) (revenue: decimal) : FactTableRow = rowIn september brand sku revenue

/// A gate that consults the real one, in its held form (Phase 1125) and its
/// row form (Phase 1126), and additionally denies the ids a test names,
/// recording every surface it was asked at.
type private RecordingGate(inner: FactDisclosureGate, denied: Set<string>) =
    let surfaces = ResizeArray<FactEgressSurface>()

    let overlay (verdicts: Map<string, FactDisclosureVerdict>) =
        verdicts
        |> Map.map (fun id v ->
            if denied.Contains id then
                FactNotDisclosable "licence-x"
            else
                v)

    member _.Surfaces = List.ofSeq surfaces

    interface IHeldFactDisclosureGate with
        member _.CheckHeld(scope, principal, surface, factIds, held) = async {
            surfaces.Add surface
            let! verdicts = (inner :> IHeldFactDisclosureGate).CheckHeld(scope, principal, surface, factIds, held)
            return overlay verdicts
        }

    interface IHeldFactRowDisclosureGate with
        member _.CheckHeldRows(scope, principal, surface, factIds, held) = async {
            surfaces.Add surface

            let! verdicts =
                (inner :> IHeldFactRowDisclosureGate).CheckHeldRows(scope, principal, surface, factIds, held)

            return overlay verdicts
        }

/// The same gate offered in its held form only, so a series read through it
/// reads facts — the fact path the row path must agree with (Phase 1126).
let private heldOnly (gate: IHeldFactDisclosureGate) : IHeldFactDisclosureGate =
    { new IHeldFactDisclosureGate with
        member _.CheckHeld(scope, principal, surface, factIds, held) =
            gate.CheckHeld(scope, principal, surface, factIds, held)
    }

type private World = {
    Deps: FactBrowseHandler.FactBrowseDeps
    Writer: IFactTableWriter
    Store: IFactStore
    Events: IEventStore
    Scope: ResolvedScope
}

/// A world over the given storage and surface policy — Phase 1122 passes a
/// counting probe, Phase 1126 a surface at every size.
let private worldOver
    (storage: IBlobStorage)
    (surface: FactSurfaceOptions)
    (denied: Set<string>)
    (clock: unit -> DateTime)
    : World * RecordingGate =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let store =
        BlobFactStore.createWithSurface storage events (Some registry) clock surface

    let writer =
        DefaultFactTableWriter.createWithClock store storage events tables (Some registry) clock

    let gate = RecordingGate(FactDisclosureGate(store, events), denied)

    {
        Deps = {
            Tables = tables
            Writer = Some writer
            Store = store
            Gate = gate
            Registry = Some registry
            Clock = clock
        }
        Writer = writer
        Store = store
        Events = events
        Scope = resolved (newScope ())
    },
    gate

let private worldOn (storage: IBlobStorage) (denied: Set<string>) (clock: unit -> DateTime) : World * RecordingGate =
    worldOver storage FactSurfaceOptions.defaults denied clock

let private worldWith (denied: Set<string>) (clock: unit -> DateTime) : World * RecordingGate =
    worldOn (InMemoryBlobStorage()) denied clock

/// A clock that moves one second per reading, so successive runs and the
/// facts they assert are ordered in time as they are in the real world.
let private tickingClock () : unit -> DateTime =
    let ticks = ref 0

    fun () ->
        ticks.Value <- ticks.Value + 1
        t0.AddSeconds(float ticks.Value)

let private world () =
    worldWith Set.empty (tickingClock ()) |> fst

/// A run opened with options — Phase 1113's series cases open a run over
/// one period slice so an earlier run's other periods stand.
let private runWith (w: World) (options: FactTableRunOptions) (rows: FactTableRow list) : string * FactTableCommit =
    let opened =
        match
            w.Writer.OpenRun(w.Scope.ScopeId, skuTable.Id, options = options)
            |> Async.RunSynchronously
        with
        | Ok r -> r
        | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

    match
        w.Writer.WriteRows(w.Scope.ScopeId, opened.RunId, rows)
        |> Async.RunSynchronously
    with
    | Ok _ -> ()
    | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

    match w.Writer.Commit(w.Scope.ScopeId, opened.RunId) |> Async.RunSynchronously with
    | Ok commit -> opened.RunId, commit
    | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)

let private run (w: World) (rows: FactTableRow list) : string * FactTableCommit =
    let opened =
        match w.Writer.OpenRun(w.Scope.ScopeId, skuTable.Id) |> Async.RunSynchronously with
        | Ok r -> r
        | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

    match
        w.Writer.WriteRows(w.Scope.ScopeId, opened.RunId, rows)
        |> Async.RunSynchronously
    with
    | Ok _ -> ()
    | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

    match w.Writer.Commit(w.Scope.ScopeId, opened.RunId) |> Async.RunSynchronously with
    | Ok commit -> opened.RunId, commit
    | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)

let private ok (label: string) (r: Async<Result<'T, string>>) : 'T =
    match Async.RunSynchronously r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %s" label e

let private query (metric: string) (direction: FactRowDirection) (page: int) (size: int) : FactRowQuery = {
    TableId = skuTable.Id
    Metric = metric
    Direction = direction
    SubjectPrefix = []
    Page = page
    PageSize = size
}

/// Phase 1113 — a series request over the whole table at its own level,
/// under the contract's cap.
let private series (metric: string) (from: DateTime) (``to``: DateTime) : FactSeriesQuery = {
    TableId = skuTable.Id
    Metric = metric
    SubjectPrefix = []
    SubjectLevel = None
    PeriodFrom = from
    PeriodTo = ``to``
    MaxPoints = FactBrowseApi.MaxSeriesPoints
}

let private jsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private wire (value: 'T) =
    JsonSerializer.Serialize(value, jsonOptions)

let private revenueFacts (w: World) : Fact list =
    w.Store.Query(
        w.Scope,
        {
            FactQuery.all with
                Metric = Some(MetricRef "revenue")
        }
    )
    |> Async.RunSynchronously

// ─── A 100,000-subject population ────────────────────────────────────

let private populationSize = 100_000

let private syntheticFact (i: int) : Fact = {
    FactId = sprintf "f%06d" i
    Subject = {
        Hierarchy = "products"
        Path = [ "brand"; sprintf "sku-%06d" i ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar(decimal i)
    Period = september
    AsOf = t0
    Method = FactBrowseHandler.tableMethod skuTable
    Evidence = {
        ResultRef = None
        InputHashes = []
        TriggerRef = None
    }
    Confidence = None
    Supersedes = None
    Disclosure = Disclosure.Surfaceable
}

let private syntheticPopulation =
    lazy (List.init populationSize (fun i -> syntheticFact (i + 1)))

/// A store whose population read ranks a synthetic 100,000-member table
/// the way the contract says every store must: clamp `TopK`, report the
/// truncation, summarise the whole matched population.
let private largeStore () : IFactStore =
    let population (query: PopulationQuery) : Async<Result<PopulationResult, string>> = async {
        let all = syntheticPopulation.Force()
        let topK = PopulationQuery.effectiveTopK query

        let sorted =
            match query.Ordering with
            | Ascending -> all |> List.sortBy (fun f -> f.Value)
            | _ -> all |> List.sortByDescending (fun f -> f.Value)

        return
            Ok {
                Ranked = sorted |> List.truncate topK
                Direction =
                    (match query.Ordering with
                     | Ascending -> LowestFirst
                     | _ -> HighestFirst)
                EffectiveTopK = topK
                Truncated = all.Length > topK
                Stats = {
                    PopulationStats.empty with
                        SubjectCount = all.Length
                        FactCount = all.Length
                        ComparableCount = all.Length
                        Minimum = Some 1m
                        Maximum = Some(decimal populationSize)
                }
            }
    }

    let notHere () : Async<'T> = async { return failtest "not read in this test" }

    { new IFactStore with
        member _.Assert(_: string, _: FactDraft) : Async<Result<Fact, string>> = notHere ()
        member _.Assert(_: ResolvedScope, _: FactDraft) : Async<Result<Fact, string>> = notHere ()

        member _.AssertBatch(_: string, _: FactDraft list) : Async<Result<BatchAssertReceipt, string>> = notHere ()

        member _.AssertBatch(_: ResolvedScope, _: FactDraft list) : Async<Result<BatchAssertReceipt, string>> =
            notHere ()

        member _.Get(_: string, _: string) : Async<Fact option> = notHere ()
        member _.Get(_: ResolvedScope, _: string) : Async<Fact option> = notHere ()
        member _.Query(_: string, _: FactQuery) : Async<Fact list> = notHere ()
        member _.Query(_: ResolvedScope, _: FactQuery) : Async<Fact list> = notHere ()

        member _.QueryWithCompetition(_: string, _: FactQuery) : Async<FactWithCompetition list> = notHere ()

        member _.QueryWithCompetition(_: ResolvedScope, _: FactQuery) : Async<FactWithCompetition list> = notHere ()

        member _.QuerySupersessionChain(_: string, _: string) : Async<Fact list> = notHere ()
        member _.QuerySupersessionChain(_: ResolvedScope, _: string) : Async<Fact list> = notHere ()
        member _.QueryPopulation(_: string, q: PopulationQuery) = population q
        member _.QueryPopulation(_: ResolvedScope, q: PopulationQuery) = population q
    }

/// Admits every id, except the ones named.
type private DenySetGate(denied: Set<string>) =
    let verdicts (factIds: string list) =
        factIds
        |> List.map (fun id ->
            id,
            (if denied.Contains id then
                 FactNotDisclosable "licence-x"
             else
                 FactDisclosable))
        |> Map.ofList
        |> async.Return

    interface IFactDisclosureGate with
        member _.Check(_: string, _: string, _: FactEgressSurface, factIds: string list) = verdicts factIds
        member _.Check(_: ResolvedScope, _: string, _: FactEgressSurface, factIds: string list) = verdicts factIds

let private largeDeps (gate: IFactDisclosureGate) : FactBrowseHandler.FactBrowseDeps = {
    Tables = tables
    Writer = None
    Store = largeStore ()
    Gate = FactDisclosureGate.held gate
    Registry = Some registry
    Clock = fun () -> t0
}

let private boundedTests =
    testList "bounded pages over a 100,000-subject table" [
        test "no page carries more rows than the ceiling, and each page reports it" {
            let deps = largeDeps (DenySetGate Set.empty)
            let scope = resolved (newScope ())

            let page =
                ok "page" (FactBrowseHandler.queryRows deps scope "u" (query "revenue" TopOfRanking 0 1_000_000))

            Expect.equal page.PageSize FactBrowseApi.MaxPageSize "a request over the ceiling is clamped"
            Expect.equal page.MaxPageSize FactBrowseApi.MaxPageSize "the ceiling is reported"
            Expect.equal page.Rows.Length FactBrowseApi.MaxPageSize "one full page, never the population"
            Expect.equal page.Population.SubjectCount populationSize "the summary describes the whole table"
            Expect.isTrue page.HasMore "the ranking continues"

            Expect.equal
                (page.Rows |> List.map _.Rank)
                [ 1 .. FactBrowseApi.MaxPageSize ]
                "the first page is the top of the ranking"
        }

        test "a later page is a window onto the same ranking" {
            let deps = largeDeps (DenySetGate Set.empty)
            let scope = resolved (newScope ())

            let page =
                ok "page 3" (FactBrowseHandler.queryRows deps scope "u" (query "revenue" TopOfRanking 3 50))

            Expect.equal (page.Rows |> List.map _.Rank) [ 151..200 ] "ranks 151 to 200"
            Expect.equal page.Rows.Head.Rendering (string (populationSize - 150)) "rank 151 is the 151st largest"
        }

        test "the deepest page stops at the rank ceiling and says so" {
            let deps = largeDeps (DenySetGate Set.empty)
            let scope = resolved (newScope ())

            let last =
                ok "last" (FactBrowseHandler.queryRows deps scope "u" (query "revenue" TopOfRanking 19 50))

            Expect.equal last.Rows.Length 50 "ranks 951 to 1000"
            Expect.isFalse last.HasMore "no page past the ceiling"
            Expect.isTrue last.ReachedRankCeiling "the ceiling is reported, not silently hit"

            let beyond =
                ok "beyond" (FactBrowseHandler.queryRows deps scope "u" (query "revenue" TopOfRanking 20 50))

            Expect.isEmpty beyond.Rows "past the ceiling there is nothing to browse"
            Expect.equal beyond.RankCeiling PopulationQuery.MaxTopK "the ceiling is the population read's own"
        }

        test "run history is paged under the same ceiling" {
            let w = world ()

            for i in 1..3 do
                run w [ row "acme" "a1" (decimal i) ] |> ignore

            let page =
                ok "runs" (FactBrowseHandler.listRuns w.Deps w.Scope (skuTable.Id, 0, 10_000))

            Expect.equal page.PageSize FactBrowseApi.MaxPageSize "clamped"
            Expect.equal page.TotalRuns 3 "every run counted"

            let small =
                ok "runs" (FactBrowseHandler.listRuns w.Deps w.Scope (skuTable.Id, 1, 1))

            Expect.equal small.Runs.Length 1 "one run per page of one"
        }
    ]

// ─── Disclosure at the FactBrowse door ───────────────────────────────

let private disclosureTests =
    testList "every served fact passes the gate at FactBrowse" [
        test "a denied row is absent, counted by policy, and its value is in no response" {
            // Rank 2 of the synthetic table is sku-099999, value 99999.
            let deniedId = sprintf "f%06d" (populationSize - 1)
            let gate = DenySetGate(Set.ofList [ deniedId ])
            let deps = largeDeps gate
            let scope = resolved (newScope ())

            let page =
                ok "page" (FactBrowseHandler.queryRows deps scope "u" (query "revenue" TopOfRanking 0 5))

            Expect.equal
                (page.Rows |> List.map _.Rank)
                [ 1; 3; 4; 5 ]
                "the withheld row leaves a gap, never a promotion"

            Expect.equal page.WithheldCount 1 "counted"
            Expect.equal page.Withheld [ { PolicyRef = "licence-x"; Count = 1 } ] "grouped by policy"
            Expect.isTrue page.Population.ValueStatisticsWithheld "a maximum IS a member's value"
            Expect.isNone page.Population.Maximum "so the magnitudes are withheld"

            let payload = wire page
            Expect.isFalse (payload.Contains deniedId) "the denied id is in no response"
            Expect.isFalse (payload.Contains "sku-099999") "nor its subject"
            Expect.isFalse (payload.Contains "99999") "nor its value"
        }

        test "every read that serves a fact asks at the FactBrowse surface" {
            let w, gate = worldWith Set.empty (tickingClock ())
            let runId, _ = run w [ row "acme" "a1" 10m; row "acme" "a2" 20m ]
            run w [ row "acme" "a1" 15m; row "acme" "a2" 20m ] |> ignore

            ok "rows" (FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "revenue" TopOfRanking 0 10))
            |> ignore

            let fact = revenueFacts w |> List.head
            ok "fact" (FactBrowseHandler.getFact w.Deps w.Scope "u" fact.FactId) |> ignore

            ok "series" (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "revenue" september.From october.To))
            |> ignore

            ignore runId

            Expect.isNonEmpty gate.Surfaces "the gate was consulted"
            Expect.allEqual gate.Surfaces FactBrowse "only at the browse door, never at another surface"
        }

        test "an Internal column's facts never cross the browse door" {
            let w = world ()
            run w [ row "acme" "a1" 10m ] |> ignore

            let segment =
                w.Store.Query(
                    w.Scope,
                    {
                        FactQuery.all with
                            Metric = Some(MetricRef "segment")
                    }
                )
                |> Async.RunSynchronously
                |> List.head

            match
                FactBrowseHandler.getFact w.Deps w.Scope "u" segment.FactId
                |> Async.RunSynchronously
            with
            | Ok _ -> failtest "an Internal fact must be refused"
            | Error message ->
                Expect.stringContains message "Internal" "the refusal names the policy"
                Expect.isFalse (message.Contains "core") "and never the value"
        }

        test "a mover is served only when both its values may be seen" {
            let w0 = world ()
            run w0 [ row "acme" "a1" 10m; row "acme" "a2" 20.5m ] |> ignore
            let runId, commit = run w0 [ row "acme" "a1" 15m; row "acme" "a2" 26.73m ]
            Expect.equal commit.Change.LargestMovers.Length 2 "both cells moved"

            let open' =
                ok "movers" (FactBrowseHandler.getRunMovers w0.Deps w0.Scope "u" (skuTable.Id, runId))

            Expect.equal open'.Movers.Length 2 "both served when nothing is denied"
            Expect.equal open'.Movers.Head.Current "26.73" "largest first, rendered"

            // Deny the run's fact for a2: its mover must be withheld.
            let a2 = revenueFacts w0 |> List.find (fun f -> f.Subject.Path = [ "acme"; "a2" ])

            let gated = {
                w0.Deps with
                    Gate = RecordingGate(FactDisclosureGate(w0.Store, w0.Events), Set.ofList [ a2.FactId ])
            }

            let closed =
                ok "movers" (FactBrowseHandler.getRunMovers gated w0.Scope "u" (skuTable.Id, runId))

            Expect.equal (closed.Movers |> List.map _.Subject) [ "products/acme>a1" ] "only the visible mover"
            Expect.equal closed.WithheldCount 1 "the other is counted"
            Expect.isFalse ((wire closed).Contains "26.73") "its value is in no response"
            Expect.isFalse ((wire closed).Contains "20.5") "nor the value it replaced"
        }
    ]

// ─── One query surface ───────────────────────────────────────────────

let private sameSurfaceTests =
    testList "the drill-down is the population tool's ranking" [
        for direction, ordering in [ TopOfRanking, "descending"; BottomOfRanking, "ascending" ] do
            test (sprintf "ranking %s agrees with query_metric_population" ordering) {
                let w = world ()

                run w [
                    row "acme" "a1" 40m
                    row "acme" "a2" 10m
                    row "acme" "a3" 30m
                    row "zeta" "z1" 50m
                    row "zeta" "z2" 20m
                    row "zeta" "z3" 60m
                ]
                |> ignore

                let page =
                    ok "rows" (FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "revenue" direction 0 6))

                let raw =
                    PopulationQueryTool.executeWith
                        w.Store
                        (FactDisclosureGate.create w.Store w.Events)
                        (Some registry)
                        (fun () -> t0)
                        w.Scope
                        "u"
                        (sprintf
                            """{"metric":"revenue","subject_hierarchy":"products","level":2,"ordering":"%s","top_k":6}"""
                            ordering)
                    |> Async.RunSynchronously

                let toolRanking =
                    (JsonDocument.Parse raw).RootElement.GetProperty("ranked").EnumerateArray()
                    |> Seq.map (fun e -> e.GetProperty("factId").GetString())
                    |> List.ofSeq

                Expect.hasLength toolRanking 6 "the tool ranked the table"
                Expect.equal (page.Rows |> List.map _.FactId) toolRanking "one question, one ranking"
            }
    ]

// ─── Tables and runs ─────────────────────────────────────────────────

let private tableTests =
    testList "tables and runs, never a stream of facts" [
        test "a table with no runs lists as never refreshed and has no run history" {
            let w = world ()
            let listed = ok "tables" (FactBrowseHandler.listTables w.Deps w.Scope)

            match listed with
            | [ t ] ->
                Expect.equal t.TableId skuTable.Id "the declared table"
                Expect.equal t.Module "sales" "its declaring module"
                Expect.equal t.Metrics [ "revenue"; "segment" ] "its metrics"
                Expect.equal t.Level "sku" "its subject level"
                Expect.equal t.Freshness "never-refreshed" "never refreshed"
                Expect.isNone t.RowCount "no row count before a commit"
            | other -> failtestf "expected one table, got %A" other

            let runs =
                ok "runs" (FactBrowseHandler.listRuns w.Deps w.Scope (skuTable.Id, 0, 10))

            Expect.equal runs.TotalRuns 0 "no runs"

            let rows =
                ok "rows" (FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "revenue" TopOfRanking 0 10))

            Expect.isEmpty rows.Rows "nothing to rank yet"
        }

        test "a refresh shows as one run with its change summary and the table's standing" {
            let w = world ()
            run w [ row "acme" "a1" 10m; row "acme" "a2" 20m ] |> ignore
            run w [ row "acme" "a1" 15m; row "acme" "a3" 5m ] |> ignore

            let t = ok "tables" (FactBrowseHandler.listTables w.Deps w.Scope) |> List.head
            Expect.equal t.RowCount (Some 2) "the current run's rows"
            Expect.equal t.Freshness "fresh" "within its cadence"

            let page =
                ok "runs" (FactBrowseHandler.listRuns w.Deps w.Scope (skuTable.Id, 0, 10))

            let latest = page.Runs.Head
            Expect.equal latest.Outcome "succeeded" "committed"
            Expect.equal latest.RowsWritten (Some 2) "rows written"

            match latest.Change with
            | Some c ->
                Expect.equal (c.New, c.Changed, c.Unchanged, c.Removed) (1, 1, 0, 1) "new, changed, unchanged, removed"
            | None -> failtest "a committed run carries its change summary"
        }

        test "one fact opens its table, its run and its supersession chain" {
            let w = world ()
            run w [ row "acme" "a1" 10m ] |> ignore
            let secondRun, _ = run w [ row "acme" "a1" 15m ]

            let current = revenueFacts w |> List.find (fun f -> f.Value = Scalar 15m)

            let detail = ok "fact" (FactBrowseHandler.getFact w.Deps w.Scope "u" current.FactId)
            Expect.equal detail.TableId (Some skuTable.Id) "the table that wrote it"
            Expect.equal detail.RunId (Some secondRun) "the run that wrote it"
            Expect.equal detail.Row.Rendering "15" "its value"
            Expect.equal (detail.Chain |> List.map _.Rendering) [ "10" ] "the value it superseded"
            Expect.equal detail.ChainWithheldCount 0 "nothing withheld"

            Expect.equal
                detail.TriggerRef
                (Some(FactBrowseHandler.runTrigger skuTable.Id secondRun))
                "the trigger the writer stamps is the one the browse surface reads"
        }

        test "one fact names the input its run was computed from (Phase 994)" {
            let w = world ()
            let input = "sha256:" + String('c', 64)

            let opened =
                match
                    w.Writer.OpenRun(
                        w.Scope.ScopeId,
                        skuTable.Id,
                        options = (FactTableRunOptions.none |> FactTableRunOptions.withInputs [ input ])
                    )
                    |> Async.RunSynchronously
                with
                | Ok r -> r
                | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

            match
                w.Writer.WriteRows(w.Scope.ScopeId, opened.RunId, [ row "acme" "a1" 10m ])
                |> Async.RunSynchronously
            with
            | Ok _ -> ()
            | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

            match w.Writer.Commit(w.Scope.ScopeId, opened.RunId) |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)

            let fact = revenueFacts w |> List.exactlyOne
            let detail = ok "fact" (FactBrowseHandler.getFact w.Deps w.Scope "u" fact.FactId)
            Expect.equal detail.RunId (Some opened.RunId) "the run that wrote it"
            Expect.contains detail.InputHashes input "and the input that run was computed from"
        }

        test "an unknown table or metric column is refused by name" {
            let w = world ()

            match FactBrowseHandler.getTable w.Deps w.Scope "ghost" |> Async.RunSynchronously with
            | Error e -> Expect.stringContains e "ghost" "names the table"
            | Ok _ -> failtest "an undeclared table must be refused"

            match
                FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "margin" TopOfRanking 0 10)
                |> Async.RunSynchronously
            with
            | Error e -> Expect.stringContains e "margin" "names the column"
            | Ok _ -> failtest "a column the table does not declare must be refused"
        }
    ]

// ─── Per-run notification ────────────────────────────────────────────

type private CapturingChannel() =
    let published = ResizeArray<string * Notification>()
    member _.Published = List.ofSeq published

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Add((scopeId, notification)) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }

let private notificationTests =
    testList "notifications are per run, never per fact" [
        test "a committed run publishes exactly one notification, with counts and no value" {
            let w = world ()
            let channel = CapturingChannel()

            let notifying = {
                w with
                    Writer = FactBrowseHandler.notifyingWriter channel w.Writer
            }

            run notifying [ row "acme" "a1" 123.45m; row "acme" "a2" 678.9m; row "zeta" "z1" 1m ]
            |> ignore

            match channel.Published with
            | [ scope, CustomNotification(key, payload) ] ->
                Expect.equal scope w.Scope.ScopeId "published to the run's scope"
                Expect.equal key FactBrowseLinks.RunCommittedNotificationKey "the per-run key"

                let notice = JsonSerializer.Deserialize<FactTableRunNotice>(payload, jsonOptions)
                Expect.equal notice.TableId skuTable.Id "names the table"
                Expect.equal notice.RowCount 3 "three rows, one notification"
                Expect.isFalse (payload.Contains "123.45") "no value rides the notice"
                Expect.isFalse (payload.Contains "acme") "nor a subject"
            | other -> failtestf "expected exactly one notification, got %A" other
        }

        test "opening, staging and a rejected commit publish nothing" {
            let w = world ()
            let channel = CapturingChannel()
            let writer = FactBrowseHandler.notifyingWriter channel w.Writer

            let opened =
                match writer.OpenRun(w.Scope.ScopeId, skuTable.Id) |> Async.RunSynchronously with
                | Ok r -> r
                | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

            // A row at the wrong level rejects the whole run.
            let bad = {
                row "acme" "a1" 1m with
                    Subject = [ "acme" ]
            }

            writer.WriteRows(w.Scope.ScopeId, opened.RunId, [ bad ])
            |> Async.RunSynchronously
            |> ignore

            match writer.Commit(w.Scope.ScopeId, opened.RunId) |> Async.RunSynchronously with
            | Ok _ -> failtest "a malformed run must be rejected"
            | Error _ -> ()

            Expect.isEmpty channel.Published "nothing committed, nothing announced"
        }
    ]

// ─── Shared names and composition ────────────────────────────────────

let private compositionTests =
    testList "shared names and composition" [
        test "the browse surface is its own egress door" {
            Expect.equal (FactEgressSurface.toString FactBrowse) "Browse" "its canonical audit name"
        }

        test "the coverage-narrative keys other companions read match the producer" {
            Expect.equal FactBrowseLinks.CoverageNarrativeModuleId CoverageNarrative.ModuleId "the module id"

            Expect.equal
                (FactBrowseLinks.coverageMetric CoverageNarrative.ModuleId (CoverageNarrative.settingsKey "revenue"))
                (Some "revenue")
                "a coverage narrative names its metric"

            Expect.isNone
                (FactBrowseLinks.coverageMetric "Pricing" "metric-coverage:revenue")
                "another module's narrative is not one"
        }

        test "the browse module is composed only when the shell lists it" {
            Expect.isFalse (FactBrowseLinks.isComposed [ "KnowledgeBase" ]) "absent"
            Expect.isTrue (FactBrowseLinks.isComposed [ "KnowledgeBase"; FactBrowseLinks.ModuleId ]) "present"
        }

        test "without the fact store the composition is untouched" {
            let app = ServerApp.empty
            let composed = FactBrowseHandler.withFactBrowse app
            Expect.isTrue (obj.ReferenceEquals(app, composed)) "the same value, byte for byte"
        }

        test "with the fact store the route mounts and the writer is decorated" {
            let app = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = EnabledFactStore
                    }
            }

            let composed = FactBrowseHandler.withFactBrowse app
            Expect.equal composed.Extensions.Handlers.Length (app.Extensions.Handlers.Length + 1) "one route"

            let w = world ()
            let channel = CapturingChannel()
            let services = ServiceCollection() :> IServiceCollection

            services.AddSingleton<INotificationChannel>(channel :> INotificationChannel)
            |> ignore

            services.AddSingleton<IFactTableWriter>(w.Writer) |> ignore

            match ServerApp.serviceConfig composed with
            | Some configure -> configure services |> ignore
            | None -> failtest "the decoration must be registered"

            let writer = services.BuildServiceProvider().GetRequiredService<IFactTableWriter>()
            Expect.isFalse (obj.ReferenceEquals(writer, w.Writer)) "the composed writer is decorated"

            run { w with Writer = writer } [ row "acme" "a1" 1m ] |> ignore
            Expect.equal channel.Published.Length 1 "and it announces the run"
        }
    ]

// ─── Phase 1018 — the composed demo ──────────────────────────────────
//
// `grounding-scale-fact-tables` is a CONJUNCTIVE criterion: in a composed
// app with a module that declares a fact table over a seeded large
// population, a refresh is one audited run, AND the assistant answers a
// top-k population question quoting minted facts verbatim, AND the client
// shows the run and its change summary — WITHOUT the population reaching
// model context.
//
// Each conjunct already had a test and the conjunction had none, so a
// judge wired over the four lists would have gone green over a demo
// nobody ran. The last conjunct had no coverage on this surface at all:
// "the population never reaches the model" was asserted only over a
// 250-subject point-fact population (`PopulationQueryToolTests`' Phase
// 703 list), never over a fact table.
//
// This list is the demo: one app, one population, one run, one question.

/// Large enough that the withholding is a claim about the ranking's bound
/// rather than about the page ceiling.
let private demoPopulationSize = 100_000

/// The bound. Ten of the hundred thousand reach the model.
let private demoTopK = 10

/// Values ascend strictly with the row index, so the top-k is exactly the
/// last k rows, and sit offset clear of every count the scenario states,
/// so no quoted rendering can coincide with one.
let private demoValue (i: int) : decimal = decimal (1_000_000 + i)

let private demoSubject (i: int) : string = sprintf "sku-%06d" i

let private demoRow (i: int) : FactTableRow =
    row (sprintf "brand-%02d" (i % 10)) (demoSubject i) (demoValue i)

/// Phase 889 — the declaring module's table bound to the DELEGATE
/// destination, so the population is held as rows and only a quoted row
/// becomes a fact.
let private demoTables: IFactTableRegistry =
    FactTableRegistry.build [
        {
            FactTableRegistration.Module = "sales"
            Definition = skuTable
        }
    ] [ BindFactTable(skuTable.Id, DelegateFact.Destination) ]

type private DemoApp = {
    App: ServerApp
    Services: IServiceProvider
    Store: IFactStore
    Writer: IFactTableWriter
    Events: IEventStore
    ScopeId: string
    Scope: ResolvedScope
}

/// ONE composed app: the fact store with 887's writer seam over it, the
/// table held as a delegate (889), and the browse surface mounted over
/// both (895). Every handle the test reads afterwards is resolved out of
/// that container, so no part of the scenario is wired by hand.
let private composeDemo () : DemoApp =
    let app =
        {
            ServerApp.empty with
                Config = {
                    ServerConfig.defaults with
                        FactStore = EnabledFactStore
                }
        }
        |> FactsCompose.withFactStore
        |> FactsCompose.withFactTableWriter
        |> FactsCompose.withDelegateFacts [ skuTable.Id ]
        |> FactBrowseHandler.withFactBrowse

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

    services.AddSingleton<IEventStore>(InMemoryEventStore.InMemoryEventStore())
    |> ignore

    services.AddSingleton<IMetricRegistry>(registry) |> ignore
    services.AddSingleton<IFactTableRegistry>(demoTables) |> ignore

    match ServerApp.serviceConfig app with
    | Some configure -> configure services |> ignore
    | None -> failtest "the composed fact tier must register its services"

    let built = services.BuildServiceProvider() :> IServiceProvider
    let scopeId = newScope ()

    {
        App = app
        Services = built
        Store = built.GetRequiredService<IFactStore>()
        Writer = built.GetRequiredService<IFactTableWriter>()
        Events = built.GetRequiredService<IEventStore>()
        ScopeId = scopeId
        Scope = resolved scopeId
    }

/// The request context the mounted surfaces take their scope and principal
/// from — the two items the scope-resolution middleware seeds.
let private demoContext (d: DemoApp) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- d.Services

    let scope: StorageScope = {
        ScopeId = d.ScopeId
        Container = "container-" + d.ScopeId
        Persist = true
    }

    ctx.Items["ToolUp.StorageScope"] <- box scope
    ScopeResolution.remember ctx scope |> ignore
    ctx.Items["ToolUp.UserId"] <- box "demo-user"
    ctx :> HttpContext

/// Open, stage and commit ONE run over the whole population.
let private refresh (d: DemoApp) (rows: FactTableRow list) : string * FactTableCommit =
    let opened =
        match d.Writer.OpenRun(d.ScopeId, skuTable.Id) |> Async.RunSynchronously with
        | Ok r -> r
        | Error e -> failtestf "open: %s" (FactTableWriteError.describe e)

    for batch in rows |> List.chunkBySize 10_000 do
        match d.Writer.WriteRows(d.ScopeId, opened.RunId, batch) |> Async.RunSynchronously with
        | Ok _ -> ()
        | Error e -> failtestf "write: %s" (FactTableWriteError.describe e)

    match d.Writer.Commit(d.ScopeId, opened.RunId) |> Async.RunSynchronously with
    | Ok commit -> opened.RunId, commit
    | Error e -> failtestf "commit: %s" (FactTableWriteError.describe e)

/// A fake provider that asks the population tool once and then answers
/// from the tool result alone — never from any wider context. It records
/// every byte it was ever handed, so the test can audit model context, and
/// the `(factId, rendering)` pairs it quoted, so the fidelity assertion is
/// made against what the model actually read.
type private FactTableAskingProvider(argsJson: string) =
    let mutable turns = 0
    let mutable quoted: (string * string) list = []
    let seen = ResizeArray<string>()

    /// Every byte of context this provider was ever given.
    member _.ModelContext = String.concat "\n" (List.ofSeq seen)

    /// The ranking's `(factId, rendering)` pairs, in the order returned.
    member _.Quoted = quoted

    member _.Turns = turns

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-fact-table"
            Model = "test-fact-table-model"
        }

        member _.SendMessage(messages, _tools, systemPrompt, _onStream, _retryPolicy) = async {
            turns <- turns + 1
            seen.Add(defaultArg systemPrompt "")

            for m in messages do
                seen.Add m.Content

                for tr in m.ToolResults do
                    seen.Add tr.Content

            if turns = 1 then
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "query_metric_population"
                                Arguments = argsJson
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            else
                // Quote the returned renderings VERBATIM and state no
                // other number, so the fidelity gate's verdict is about
                // the whole answer rather than one sentence of it.
                let toolResult =
                    messages
                    |> List.collect _.ToolResults
                    |> List.tryLast
                    |> Option.map _.Content
                    |> Option.defaultValue "{}"

                let root = (JsonDocument.Parse toolResult).RootElement

                quoted <-
                    root.GetProperty("ranked").EnumerateArray()
                    |> Seq.map (fun entry ->
                        entry.GetProperty("factId").GetString(), entry.GetProperty("rendering").GetString())
                    |> List.ofSeq

                return
                    Ok {
                        Content =
                            "The highest revenue in the table, best first, reads: "
                            + (quoted |> List.map snd |> String.concat ", ")
                            + " — each quoted exactly as the ranking returned it."
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

/// Subject ids are the honest withholding probe: unlike a value, a subject
/// id cannot coincide with a summary statistic the model was legitimately
/// told.
let private demoSubjectPattern = Regex(@"sku-\d{6}", RegexOptions.CultureInvariant)

let private composedDemoTests =
    testList "the composed demo — one app, one population, one run, one question" [

        testCaseAsync
            "a declared table over a 100,000-subject population refreshes as one audited run, answers a top-k question quoting minted facts verbatim, and shows that run to the client with the population withheld"
        <| async {
            let d = composeDemo ()
            let client = FactBrowseHandler.factBrowseApi (demoContext d)

            // ── The declaration, before anything has run ──────────────
            match ok "tables" (client.ListTables()) with
            | [ t ] ->
                Expect.equal t.TableId skuTable.Id "the module's declared table"
                Expect.equal t.Module "sales" "its declaring module"
                Expect.equal t.Freshness "never-refreshed" "nothing has refreshed it"
            | other -> failtestf "expected the one declared table, got %A" other

            Expect.equal (ok "runs" (client.ListRuns(skuTable.Id, 0, 10))).TotalRuns 0 "and it has no run history"

            // ── ONE refresh over the whole population ─────────────────
            let runId, commit = refresh d [ for i in 1..demoPopulationSize -> demoRow i ]

            Expect.equal commit.RowCount demoPopulationSize "the run holds the whole population"

            Expect.equal commit.FactsWritten 0 "and mints no fact — the table is held, not expanded into the fact tier"

            // ── One refresh, ONE audited run ──────────────────────────
            let! committed = d.Events.ReadByType(d.ScopeId, FactTableEvents.RunCommittedType)

            Expect.equal committed.Length 1 "one refresh, one audit record — not one per subject"

            let! factTierAudit = d.Events.ReadBySource(d.ScopeId, FactEvents.SourceModule)

            Expect.equal
                (factTierAudit |> List.map _.EventType |> List.sort)
                (FactTableEvents.RunCommittedType
                 :: (skuTable.Columns |> List.map (fun _ -> FactEvents.AssertedType))
                 |> List.sort)
                (sprintf
                    "the whole fact-tier audit of the %d-row refresh is the run record plus one delegate record per declared column — bounded by the DECLARATION, never by the population"
                    demoPopulationSize)

            Expect.equal
                (ok "runs" (client.ListRuns(skuTable.Id, 0, 10))).TotalRuns
                1
                "the run count advanced by exactly one"

            // ── The top-k question, through the composed tool set ─────
            let tools = AIToolRegistry()

            tools.RegisterAll(d.App.AITools |> List.map (fun (def, exec) -> createTool def exec))

            let provider =
                FactTableAskingProvider(
                    sprintf """{"metric":"revenue","subject_hierarchy":"products","top_k":%d}""" demoTopK
                )

            let streamed = ResizeArray<AIStreamEvent>()

            let onEvent (evt: AIStreamEvent) =
                lock streamed (fun () -> streamed.Add evt)

            let! answered =
                AIAgentEngine.runAgentLoopWithInput
                    (provider :> IAIProvider)
                    tools
                    (ClientToolDispatch.ClientToolDispatchRegistry())
                    (demoContext d)
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    AISurface.FullPage
                    None
                    None
                    CancellationToken.None
                    [
                        AIProviderMessage.text "user" "Which skus had the highest revenue last month?"
                    ]
                    ModelInput.empty
                    onEvent

            let captured = lock streamed (fun () -> streamed.ToArray() |> Array.toList)

            Expect.equal
                (captured
                 |> List.choose (function
                     | ToolCallStarted(_, name, _) -> Some name
                     | _ -> None))
                [ "query_metric_population" ]
                "the population question was answered by exactly one tool call, not a hundred thousand point reads"

            Expect.equal provider.Turns 2 "one tool turn, one answer turn"

            // ── The quoted numbers are minted facts, byte for byte ────
            let quoted = provider.Quoted
            Expect.equal quoted.Length demoTopK "the model was handed the bound, not the population"

            let displayFormat =
                registry.TryGetMetric "revenue"
                |> Option.map _.DisplayFormat
                |> Option.defaultWith (fun () -> failtest "the metric must be registered")

            for factId, rendering in quoted do
                let stored =
                    d.Store.Get(d.Scope, factId)
                    |> Async.RunSynchronously
                    |> Option.defaultWith (fun () -> failtestf "the quoted fact %s is minted and stored" factId)

                Expect.equal
                    rendering
                    (FactRendering.render displayFormat stored.Value)
                    "the rendering the model quoted is the stored fact's, byte for byte"

            let answer =
                answered
                |> List.filter (fun m -> m.Role = "assistant" && not (String.IsNullOrWhiteSpace m.Content))
                |> List.tryLast
                |> Option.map _.Content
                |> Option.defaultWith (fun () -> failtest "the agent loop produced no assistant answer")

            let scoped: ScopedFact list =
                quoted
                |> List.map (fun (factId, rendering) -> {
                    FactId = factId
                    Rendering = rendering
                    Metric = "revenue"
                })

            // Phase 889's numeric-fidelity gate, over the delegated mints.
            let verdict = NumericFidelity.verify answer scoped (Some registry) "Annotate"

            Expect.equal verdict.Verified demoTopK "every number the answer states is verified against a minted fact"

            Expect.equal verdict.Unmatched 0 "and it states no number the fact tier did not mint"

            Expect.equal
                (verdict.Numbers |> List.choose _.MatchedFactId)
                (quoted |> List.map fst)
                "each quoted number carries its fact id, in the order the ranking returned them"

            // ── THE POINT: the population was withheld ────────────────
            let context = provider.ModelContext

            let mentioned = demoSubjectPattern.Matches context |> Seq.map _.Value |> Set.ofSeq

            Expect.isLessThanOrEqual
                mentioned.Count
                demoTopK
                "the subjects reaching model context are bounded by the effective top-k"

            Expect.equal
                (Set.toList mentioned)
                ([ demoPopulationSize - demoTopK + 1 .. demoPopulationSize ]
                 |> List.map demoSubject
                 |> List.sort)
                "and they are exactly the returned top-k, not an arbitrary ten"

            Expect.equal
                (demoPopulationSize - mentioned.Count)
                (demoPopulationSize - demoTopK)
                (sprintf
                    "the other %d of the %d subjects never reached the model"
                    (demoPopulationSize - demoTopK)
                    demoPopulationSize)

            Expect.stringContains context "\"truncated\":true" "and the model was told its ranking was bounded"

            Expect.stringContains
                context
                (sprintf "\"subjectCount\":%d" demoPopulationSize)
                "so it could state the population size it never saw"

            // ── The client shows THAT run, with its change summary ────
            let latest = (ok "runs" (client.ListRuns(skuTable.Id, 0, 10))).Runs.Head
            Expect.equal latest.RunId runId "the run the client shows is the run the refresh opened"
            Expect.equal latest.Outcome "succeeded" "committed"
            Expect.equal latest.RowsWritten (Some demoPopulationSize) "the rows it wrote"

            match latest.Change with
            | Some c ->
                Expect.equal
                    (c.New, c.Changed, c.Unchanged, c.Removed)
                    (demoPopulationSize, 0, 0, 0)
                    "its change summary against the table's previous standing"
            | None -> failtest "a committed run carries its change summary"

            // The binding that makes this ONE scenario rather than two
            // asserted side by side: the run the client is showing is the
            // run the quoted facts were minted under.
            let topFactId, _ = List.head quoted

            let topStored =
                d.Store.Get(d.Scope, topFactId)
                |> Async.RunSynchronously
                |> Option.defaultWith (fun () -> failtest "the leading quoted fact is stored")

            Expect.equal
                topStored.Evidence.TriggerRef
                (Some(DelegateMint.triggerRef skuTable.Id latest.RunId))
                "the quoted fact names the run the client is showing"

            let listed = ok "tables" (client.ListTables()) |> List.head
            Expect.equal listed.RowCount (Some demoPopulationSize) "the table's standing is that run's row count"
            Expect.isSome listed.LastRefreshedAt "and it reads as refreshed"
        }
    ]

// ─── Phase 1113 — the series read ────────────────────────────────────

let private seriesTests =
    testList "Phase 1113 — a series serves numbers over a period, under the gate" [
        test "a series over a seeded table returns each subject's number per period, ordered for charting" {
            let w = world ()

            run w [
                rowIn september "zeta" "z1" 50m
                rowIn october "zeta" "z1" 55m
                rowIn september "acme" "a2" 20m
                rowIn october "acme" "a2" 22m
                rowIn september "acme" "a1" 10m
                rowIn october "acme" "a1" 12m
            ]
            |> ignore

            let s =
                ok
                    "series"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "revenue" september.From october.To))

            Expect.equal
                (s.Points |> List.map (fun p -> p.SubjectPath, p.PeriodLabel, p.Value))
                [
                    [ "acme"; "a1" ], Some "2026-09", Some 10m
                    [ "acme"; "a1" ], Some "2026-10", Some 12m
                    [ "acme"; "a2" ], Some "2026-09", Some 20m
                    [ "acme"; "a2" ], Some "2026-10", Some 22m
                    [ "zeta"; "z1" ], Some "2026-09", Some 50m
                    [ "zeta"; "z1" ], Some "2026-10", Some 55m
                ]
                "every point carries its number, ordered by subject then period whatever order the rows were written in"

            Expect.equal s.SubjectLevel "sku" "the table's own level by default"
            Expect.equal s.MaxPoints FactBrowseApi.MaxSeriesPoints "the cap in force is reported"
            Expect.equal s.WithheldCount 0 "nothing withheld"
            Expect.allEqual (s.Points |> List.map _.Withheld) false "and no point is marked so"
            Expect.allEqual (s.Points |> List.map _.Freshness) "fresh" "current heads are fresh under UntilSuperseded"

            Expect.equal
                (s.Points |> List.map _.Rendering)
                [ Some "10"; Some "12"; Some "20"; Some "22"; Some "50"; Some "55" ]
                "each point is also rendered under the metric's display format"

            for p in s.Points do
                Expect.isSome
                    (w.Store.Get(w.Scope, p.FactId) |> Async.RunSynchronously)
                    "every point's fact id opens the fact it was drawn from"

            // The range is a window: September alone leaves October out.
            let septemberOnly =
                ok
                    "september"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "revenue" september.From september.To))

            Expect.equal
                (septemberOnly.Points |> List.map _.PeriodLabel)
                [ Some "2026-09"; Some "2026-09"; Some "2026-09" ]
                "a period outside the range is not a point"

            // A prefix narrows the branch.
            let acme =
                ok
                    "acme"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                        series "revenue" september.From october.To with
                            SubjectPrefix = [ "acme" ]
                    })

            Expect.equal
                (acme.Points |> List.map _.Subject |> List.distinct)
                [ "products/acme>a1"; "products/acme>a2" ]
                "only the subjects under the prefix"
        }

        test "a period revised by a later run returns the revision, and a period it left alone keeps its value" {
            let w = world ()

            run w [ rowIn september "acme" "a1" 10m; rowIn october "acme" "a1" 12m ]
            |> ignore

            // A second run over the October slice only: it rewrites a1's
            // October cell and leaves September standing.
            runWith w (FactTableRunOptions.none |> FactTableRunOptions.forSlice october) [
                rowIn october "acme" "a1" 99m
            ]
            |> ignore

            let s =
                ok
                    "series"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "revenue" september.From october.To))

            Expect.equal
                (s.Points |> List.map (fun p -> p.PeriodLabel, p.Value))
                [ Some "2026-09", Some 10m; Some "2026-10", Some 99m ]
                "October is the revision; September is the head the first run wrote — the head for THAT period, \
                 not the latest run's"

            // The superseded October fact is not a point; it is on the head's chain.
            let octoberHead = s.Points |> List.find (fun p -> p.PeriodLabel = Some "2026-10")

            let detail =
                ok "fact" (FactBrowseHandler.getFact w.Deps w.Scope "u" octoberHead.FactId)

            Expect.equal
                (detail.Chain |> List.map _.Rendering)
                [ "12" ]
                "the superseded value is reachable through GetFact"

            Expect.isFalse ((wire s).Contains(detail.Chain.Head.FactId)) "and its id is in no point"
        }

        test "a withheld point keeps its place with no value, and the series says how many" {
            let w0 = world ()

            run w0 [ rowIn september "acme" "a1" 10m; rowIn september "acme" "a2" 20.5m ]
            |> ignore

            let a2 = revenueFacts w0 |> List.find (fun f -> f.Subject.Path = [ "acme"; "a2" ])

            let gated = {
                w0.Deps with
                    Gate = RecordingGate(FactDisclosureGate(w0.Store, w0.Events), Set.ofList [ a2.FactId ])
            }

            let s =
                ok
                    "series"
                    (FactBrowseHandler.querySeries gated w0.Scope "u" (series "revenue" september.From october.To))

            Expect.equal
                (s.Points |> List.map _.SubjectPath)
                [ [ "acme"; "a1" ]; [ "acme"; "a2" ] ]
                "both points, in place"

            let withheld = s.Points |> List.find (fun p -> p.SubjectPath = [ "acme"; "a2" ])
            Expect.isTrue withheld.Withheld "marked withheld"
            Expect.isNone withheld.Value "no value"
            Expect.isNone withheld.Rendering "no rendering"
            Expect.equal withheld.FactId a2.FactId "the fact id stays, so the gap can be explained through GetFact"
            Expect.equal withheld.PeriodLabel (Some "2026-09") "and so does the period"
            Expect.equal s.WithheldCount 1 "counted"
            Expect.equal s.Withheld [ { PolicyRef = "licence-x"; Count = 1 } ] "grouped by policy"
            Expect.isFalse ((wire s).Contains "20.5") "the withheld value is in no response"
        }

        test "the cap refuses by name; a request over the ceiling is clamped" {
            let w = world ()

            run w [ rowIn september "acme" "a1" 10m; rowIn september "acme" "a2" 20m ]
            |> ignore

            match
                FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                    series "revenue" september.From october.To with
                        MaxPoints = 1
                }
                |> Async.RunSynchronously
            with
            | Ok _ -> failtest "two points over a cap of one must be refused"
            | Error message ->
                Expect.stringContains message "2 points" "the refusal names the count"
                Expect.stringContains message "cap of 1" "and the cap in force"
                Expect.stringContains message "MaxSeriesPoints" "and the contract's ceiling"
                Expect.isFalse (message.Contains "10") "and never a value"

            let clamped =
                ok
                    "clamped"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                        series "revenue" september.From october.To with
                            MaxPoints = 1_000_000
                    })

            Expect.equal
                clamped.MaxPoints
                FactBrowseApi.MaxSeriesPoints
                "a request over the ceiling is clamped, not refused"
        }

        test "an unknown table, an unknown metric, a level the hierarchy lacks and an empty range refuse" {
            let w = world ()
            run w [ rowIn september "acme" "a1" 10m ] |> ignore

            let refused (label: string) (q: FactSeriesQuery) =
                match FactBrowseHandler.querySeries w.Deps w.Scope "u" q |> Async.RunSynchronously with
                | Ok _ -> failtestf "%s: must be refused" label
                | Error message -> message

            let base' = series "revenue" september.From october.To

            Expect.stringContains (refused "table" { base' with TableId = "nope" }) "nope" "names the table"
            Expect.stringContains (refused "metric" { base' with Metric = "margin" }) "margin" "names the metric"

            Expect.stringContains
                (refused "level" {
                    base' with
                        SubjectLevel = Some "region"
                })
                "region"
                "names the level"

            Expect.stringContains
                (refused "range" {
                    base' with
                        PeriodTo = base'.PeriodFrom
                })
                "half-open"
                "names the shape the range must take"

            // A level the hierarchy has, but no facts sit at, is an empty series — not a refusal.
            let brand =
                ok
                    "brand"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                        base' with
                            SubjectLevel = Some "brand"
                    })

            Expect.isEmpty brand.Points "no brand-level facts are published, so there is nothing to plot"
            Expect.equal brand.SubjectLevel "brand" "the level applied is reported"
        }

        test "a scope sees only its own facts" {
            let w = world ()
            run w [ rowIn september "acme" "a1" 10m ] |> ignore

            let other = resolved (newScope ())

            let s =
                ok
                    "series"
                    (FactBrowseHandler.querySeries w.Deps other "u" (series "revenue" september.From october.To))

            Expect.isEmpty s.Points "another scope's facts are structurally unreachable"
        }

        test "the series travels the wire as the client decodes it" {
            let w = world ()
            run w [ rowIn september "acme" "a1" 10.25m ] |> ignore

            let s =
                ok
                    "series"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "revenue" september.From october.To))

            let back = JsonSerializer.Deserialize<FactSeries>(wire s, jsonOptions)
            Expect.equal back s "round-trips through the ToolUp.Remoting JSON converters"
            Expect.equal (back.Points.Head.Value) (Some 10.25m) "the number arrives as a decimal"
        }
    ]

// ─── Phase 1122 — a chart and a point read cost their answer ─────────
//
// The handler-level twin of the store pins in `FactStoreTests`: one table
// run of 2,400 facts into an empty scope (the shape a consumer
// deployment's first upload takes), then the three reads a browse makes,
// counted at the storage. Before 1122 the first point read enumerated the
// scope (the batch had crossed the index threshold without indexing) and
// every chart did too (the series read named no subject).

let private costTests =
    testList "Phase 1122 — a chart and a point read download their answer, not the scope" [
        test "after one large run, a point read and a chart read only the facts they serve" {
            let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
            let w, _ = worldOn counting Set.empty (tickingClock ())

            // 600 SKUs x two months x two columns = 2,400 facts, in one run.
            run w [
                for i in 0..599 do
                    let brand = sprintf "b%02d" (i % 12)
                    let sku = sprintf "s%04d" i
                    rowIn september brand sku (decimal i)
                    rowIn october brand sku (decimal (i + 1))
            ]
            |> ignore

            // Phase 1124 — the run's one batch held the whole (empty) scope,
            // so it built the revenue surface as it wrote: the table's first
            // read is as cheap as every later one. Until 1124 it enumerated
            // the scope once to build that surface (2,421 downloads here).
            counting.Reset()

            let firstPage =
                ok "rows" (FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "revenue" TopOfRanking 0 10))

            let coldRowsReads = counting.Downloads

            counting.Reset()

            ok "rows again" (FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "revenue" TopOfRanking 0 10))
            |> ignore

            let rowsReads = counting.Downloads
            let targetId = firstPage.Rows.Head.FactId

            counting.Reset()
            let detail = ok "fact" (FactBrowseHandler.getFact w.Deps w.Scope "u" targetId)
            let factReads = counting.Downloads

            counting.Reset()

            let chart =
                ok
                    "chart"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                        series "revenue" september.From october.To with
                            SubjectPrefix = [ "b03" ]
                    })

            let chartReads = counting.Downloads - counting.SurfaceDownloads
            let points = List.length chart.Points

            printfn
                "Phase 1122 browse over 2,400 facts: QueryRows cold %d downloads, warm %d (page of 10), GetFact %d, QuerySeries %d fact downloads for %d points of a 1,200-head metric"
                coldRowsReads
                rowsReads
                factReads
                chartReads
                points

            Expect.equal detail.Row.FactId targetId "the point read opens the fact"
            Expect.equal points 100 "the chart is one brand's 50 SKUs over two months"

            Expect.equal
                rowsReads
                10
                "a warm page of ten reads its ten rows, nothing more (the gate judges the rows it holds, Phase 1125)"

            Expect.equal
                coldRowsReads
                rowsReads
                "the first page after the upload costs what a warm page does: the batch left the surface built"

            Expect.isLessThanOrEqual factReads 5 "a point read reads its fact and its one-fact chain — never the scope"

            // The chart is answered from the brand's surface rows in range and
            // judged as rows (Phase 1126): no fact blob downloads, at any size.
            Expect.equal chartReads 0 "a chart reads its branch's surface rows, never a fact blob"
        }
    ]

// ─── Phase 1123 / 1126 — a chart of one branch is one surface download ─
//
// The same browse at two sizes: `skus` SKUs over two months and two
// columns (4 x `skus` facts), twenty brands, one brand charted. Before 1123
// the store returned the metric's heads across every brand and the handler
// filtered the prefix after; 1123 narrowed the surface rows to the branch
// before anything downloaded, so a chart cost one fact download per point.
// Since 1126 the points ARE the surface rows: a chart downloads at most the
// metric's surface snapshot (nothing at all when the parse cache holds it)
// and no fact blob, whatever the branch's size.

/// The browse's gate rebuilt over the world's store with a taint
/// configuration composed (Phase 1134); `None` keeps the world's own gate.
let private gatedBy (taint: DisclosureTaintConfig option) (w: World) : FactBrowseHandler.FactBrowseDeps =
    match taint with
    | None -> w.Deps
    | Some config -> {
        w.Deps with
            Gate = RecordingGate(FactDisclosureGate(w.Store, w.Events, taint = config), Set.empty)
      }

/// One table run of `skus` SKUs over two months and two columns (4 x `skus`
/// facts), twenty brands.
let private runTwentyBrands (w: World) (skus: int) =
    run w [
        for i in 0 .. skus - 1 do
            let brand = sprintf "b%02d" (i % 20)
            let sku = sprintf "s%05d" i
            rowIn september brand sku (decimal i)
            rowIn october brand sku (decimal (i + 1))
    ]
    |> ignore

/// One brand's revenue chart over both months.
let private brandChart (brand: string) : FactSeriesQuery = {
    series "revenue" september.From october.To with
        SubjectPrefix = [ brand ]
}

/// One one-brand chart over `skus` SKUs, judged by a gate composed with
/// `taint`: its fact-blob downloads, its surface-snapshot downloads, its
/// points and its wall time. `warm` charts once first; otherwise this is the
/// first read after the upload, over the surface the run's batch built as it
/// wrote (Phase 1124).
let private oneBrandChartUnder
    (taint: DisclosureTaintConfig option)
    (skus: int)
    (warm: bool)
    : int * int * int * float =
    let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
    let w, _ = worldOn counting Set.empty (tickingClock ())
    runTwentyBrands w skus
    let deps = gatedBy taint w

    let chart () =
        ok "chart" (FactBrowseHandler.querySeries deps w.Scope "u" (brandChart "b07"))

    if warm then
        chart () |> ignore

    counting.Reset()
    let clock = Diagnostics.Stopwatch.StartNew()
    let points = List.length (chart ()).Points
    clock.Stop()

    counting.Downloads - counting.SurfaceDownloads, counting.SurfaceDownloads, points, clock.Elapsed.TotalMilliseconds

let private oneBrandChart (skus: int) (warm: bool) : int * int * int * float = oneBrandChartUnder None skus warm

let private branchCostTests =
    testList "Phase 1126 — a chart of one branch is one surface download, not one download per point" [
        for skus in [ 500; 5_000 ] do
            for warm in [ true; false ] do
                let shape = if warm then "warm" else "first read after the upload"

                test (sprintf "one brand of twenty, %d facts, %s" (4 * skus) shape) {
                    let factDownloads, surfaceDownloads, points, ms = oneBrandChart skus warm

                    printfn
                        "Phase 1126 one-brand chart over %d facts (%s): %d fact downloads, %d surface downloads, %d points, %.1f ms"
                        (4 * skus)
                        shape
                        factDownloads
                        surfaceDownloads
                        points
                        ms

                    Expect.equal points (2 * skus / 20) "the chart is one brand's SKUs over two months"
                    Expect.equal factDownloads 0 "the points are the surface's rows: no fact blob is downloaded"
                    Expect.isLessThanOrEqual surfaceDownloads 1 "at most the metric's one surface snapshot"
                }
    ]

// ─── Phase 1134 — a taint config that cannot taint costs a chart nothing ─
//
// Every chart read is also a disclosure check, and the gate built the
// derivation graph — a whole-scope listing and download — whenever a taint
// config was composed at all. A consumer deployment registers exactly one
// policy, `Plain`, with no contributor scope and no declassifier, for its
// per-surface stance: its walk can change no verdict, yet each warm chart
// downloaded the scope (1,748 blobs at 1,742 facts). Pinned by counts at 2k
// and 20k facts; the converse pins that a config whose walk CAN change a
// verdict walks, over the scope's disclosure-taint index rather than its facts.

/// The consumer deployment's registration, exactly: one `Plain` policy
/// permitting four surfaces, no contributor scope, no declassifier.
let private tradeFigures: DisclosurePolicy = {
    PolicyRef = "trade-figures"
    Mode = Plain
    PermitSurfaces = [ FactRetrieval; FactToolResult; FactNarrativePublication; FactBrowse ]
    ContributorScope = None
}

let private inertTaint = DisclosureTaintConfig.ofLists [ tradeFigures ] []

/// The three ways a config makes the walk live (the shard's rule), each one
/// change away from the inert registration.
let private liveTaints = [
    "a TaintPropagating policy",
    DisclosureTaintConfig.ofLists [
        {
            tradeFigures with
                Mode = TaintPropagating
        }
    ] []
    "a declared contributor scope",
    DisclosureTaintConfig.ofLists [
        {
            tradeFigures with
                ContributorScope = Some "party-a"
        }
    ] []
    "a declassification routine",
    DisclosureTaintConfig.ofLists [ tradeFigures ] [
        {
            OperationId = "aggregate-over-k"
            Rationale = "aggregation over at least five members loses individual attribution"
            AcceptingScopes = []
        }
    ]
]

/// Storage that holds every fact-blob download, once armed, until
/// `readers + 1` listings of the fact prefix have been taken (each reader's
/// census, plus one rebuild's) or ten seconds have passed. So concurrent cold
/// readers are all in flight before any rebuild can finish, whatever the
/// machine's speed, and a reader that does not share the first rebuild runs
/// its own.
type private HoldingBlobStorage(inner: IBlobStorage, readers: int) =
    let released =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let mutable listings = 0
    let mutable armed = false

    member _.Arm() = armed <- true

    interface IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom
        member _.ComposeFrom(c, b, parts) = inner.ComposeFrom(c, b, parts)
        member _.Upload(c, b, content) = inner.Upload(c, b, content)

        member _.Download(c, b) = async {
            if armed && b.StartsWith("_facts/", StringComparison.Ordinal) then
                let! _ =
                    System.Threading.Tasks.Task.WhenAny(
                        released.Task,
                        System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 10.0)
                    )
                    |> Async.AwaitTask

                ()

            return! inner.Download(c, b)
        }

        member _.Delete(c, b) = inner.Delete(c, b)

        member _.List(c, prefix) = async {
            if armed && prefix = "_facts/" && Interlocked.Increment(&listings) >= readers + 1 then
                released.TrySetResult() |> ignore

            return! inner.List(c, prefix)
        }

        member _.Exists(c, b) = inner.Exists(c, b)
        member _.GetMetadata(c, b) = inner.GetMetadata(c, b)

        member _.DownloadRange(c, b, offset, length) =
            inner.DownloadRange(c, b, offset, length)

        member _.Erase(c, prefix, policy, dryRun) = inner.Erase(c, prefix, policy, dryRun)

/// A cold page of one-brand charts read at once, after the metric's surface
/// was flushed (`FactSurface.drop` — the state a deploy finds when the
/// snapshot shape moved). Returns the fact-blob downloads, the surface
/// writes, the scope's size, each chart as the page read it and as it reads
/// alone afterwards, and the page's wall time.
let private coldPage (taint: DisclosureTaintConfig option) (skus: int) (brands: string list) =
    let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
    let holding = HoldingBlobStorage(counting, List.length brands)
    let w, _ = worldOn holding Set.empty (tickingClock ())
    runTwentyBrands w skus
    let deps = gatedBy taint w
    FactSurface.drop counting w.Scope.ScopeId |> Async.RunSynchronously

    holding.Arm()
    counting.Reset()
    let clock = Diagnostics.Stopwatch.StartNew()

    let together =
        brands
        |> List.map (fun brand -> FactBrowseHandler.querySeries deps w.Scope "u" (brandChart brand))
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Array.map (function
            | Ok s -> s
            | Error e -> failtestf "cold chart: %s" e)
        |> List.ofArray

    clock.Stop()
    let factDownloads = counting.Downloads - counting.SurfaceDownloads
    let surfaceWrites = counting.SurfaceUploads

    let alone =
        brands
        |> List.map (fun brand -> ok "alone" (FactBrowseHandler.querySeries deps w.Scope "u" (brandChart brand)))

    factDownloads, surfaceWrites, 4 * skus, together, alone, clock.Elapsed.TotalMilliseconds

let private inertTaintTests =
    testList "Phase 1134 — a taint config that cannot taint costs a chart nothing" [
        for skus in [ 500; 5_000 ] do
            test (sprintf "a warm chart under the inert registration downloads no fact blob, %d facts" (4 * skus)) {
                let factDownloads, surfaceDownloads, points, ms =
                    oneBrandChartUnder (Some inertTaint) skus true

                printfn
                    "Phase 1134 warm one-brand chart, inert config, %d facts: %d fact downloads, %d surface downloads, %d points, %.1f ms"
                    (4 * skus)
                    factDownloads
                    surfaceDownloads
                    points
                    ms

                Expect.equal points (2 * skus / 20) "the chart is one brand's SKUs over two months"
                Expect.equal factDownloads 0 "a Plain-only config walks nothing, so the gate reads no fact"
            }

            test (sprintf "a cold three-chart page under the inert registration rebuilds once, %d facts" (4 * skus)) {
                let factDownloads, surfaceWrites, scopeSize, together, alone, ms =
                    coldPage (Some inertTaint) skus [ "b03"; "b07"; "b11" ]

                printfn
                    "Phase 1134 cold three-chart page, inert config, %d facts: %d fact downloads, %d surface writes, %.1f ms"
                    scopeSize
                    factDownloads
                    surfaceWrites
                    ms

                Expect.equal surfaceWrites 1 "three concurrent cold reads of one metric share one rebuild and one PUT"
                Expect.equal factDownloads scopeSize "the one rebuild reads the scope once; the gate reads nothing"
                Expect.equal together alone "each chart is the answer it gives alone"
            }

            test (
                sprintf
                    "a warm chart under a TaintPropagating config reads the taint index, not the scope, %d facts"
                    (4 * skus)
            ) {
                let factDownloads, _, points, ms =
                    oneBrandChartUnder (Some(snd liveTaints.Head)) skus true

                printfn
                    "Phase 1134 warm one-brand chart, TaintPropagating config, %d facts: %d fact downloads, %d points, %.1f ms"
                    (4 * skus)
                    factDownloads
                    points
                    ms

                Expect.equal points (2 * skus / 20) "the same chart"

                Expect.isLessThanOrEqual
                    factDownloads
                    1
                    "the walk can change a verdict, and reads the scope's index: one blob"
            }

        for name, config in liveTaints do
            test (sprintf "under %s the walk is live and reads the taint index" name) {
                let factDownloads, _, _, _ = oneBrandChartUnder (Some config) 100 true
                let untainted, _, _, _ = oneBrandChartUnder None 100 true

                Expect.equal
                    factDownloads
                    (untainted + 1)
                    "the walk is live, so the gate reads the scope's index, and nothing else"
            }
    ]

// ─── Phase 1126 — the row path answers what the fact path answers ────
//
// The oracle is the same series read through the same gate offered in its
// held form only (`heldOnly`), which makes the handler read facts — the
// pre-1126 path. Over a corpus with originals, revisions, a slice-scoped
// run that supersedes one period and leaves the other standing, `Absent`
// heads and an `Internal` categorical column, the two `FactSeries` must be
// equal field for field, and the gate must write the same audit rows.

/// A world with a surface at every size, holding the mixed corpus.
let private mixedWorld (storage: IBlobStorage) : World * RecordingGate =
    let w, gate =
        worldOver storage FactSurfaceOptions.always Set.empty (tickingClock ())

    let gap (period: TemporalExtent) (brand: string) (sku: string) = {
        rowIn period brand sku 0m with
            Values =
                Map.ofList [
                    "revenue", Absent "no data loaded for this period"
                    "segment", Categorical "core"
                ]
    }

    run w [
        rowIn september "acme" "a1" 10m
        rowIn october "acme" "a1" 12m
        rowIn september "acme" "a2" 20m
        rowIn october "acme" "a2" 22m
        rowIn september "zeta" "z1" 50m
        gap october "zeta" "z1"
        gap september "zeta" "z2"
    ]
    |> ignore

    // October only: a1 is revised, z1's gap is filled, a2 is restated as it was.
    runWith w (FactTableRunOptions.none |> FactTableRunOptions.forSlice october) [
        rowIn october "acme" "a1" 99m
        rowIn october "zeta" "z1" 55m
        rowIn october "acme" "a2" 22m
    ]
    |> ignore

    w, gate

let private parityTests =
    testList "Phase 1126 — the row path's series is the fact path's, field for field" [
        test "over originals, revisions, a slice-scoped run, gaps and an Internal column" {
            let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
            let w, gate = mixedWorld counting
            let factPath = { w.Deps with Gate = heldOnly gate }

            let reads = [
                "revenue, both months", series "revenue" september.From october.To
                "revenue, September", series "revenue" september.From september.To
                "revenue, acme",
                {
                    series "revenue" september.From october.To with
                        SubjectPrefix = [ "acme" ]
                }
                "segment (Internal), both months", series "segment" september.From october.To
            ]

            let auditOf () =
                w.Events.ReadBySource(w.Scope.ScopeId, FactEvents.SourceModule)
                |> Async.RunSynchronously
                |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)
                |> List.map _.Payload

            for name, request in reads do
                // Warm the surface first.
                ok name (FactBrowseHandler.querySeries w.Deps w.Scope "u" request) |> ignore

                let before = auditOf ()
                counting.Reset()
                let viaRows = ok name (FactBrowseHandler.querySeries w.Deps w.Scope "u" request)
                let rowFactReads = counting.Downloads - counting.SurfaceDownloads
                let rowAudit = auditOf () |> List.skip before.Length

                let viaFacts = ok name (FactBrowseHandler.querySeries factPath w.Scope "u" request)
                let factAudit = auditOf () |> List.skip (before.Length + rowAudit.Length)

                Expect.isNonEmpty
                    viaFacts.Points
                    (sprintf "%s: the oracle has points, so the comparison is not vacuous" name)

                Expect.equal viaRows viaFacts (sprintf "%s: the row path's series is the fact path's" name)
                Expect.equal rowFactReads 0 (sprintf "%s: and the row path read no fact to answer it" name)
                Expect.equal (List.sort rowAudit) (List.sort factAudit) (sprintf "%s: the same audit rows" name)

            let all =
                ok "all" (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "revenue" september.From october.To))

            Expect.equal
                (all.Points
                 |> List.map (fun p -> p.SubjectPath, p.PeriodLabel, p.Value, p.Rendering))
                [
                    [ "acme"; "a1" ], Some "2026-09", Some 10m, Some "10"
                    [ "acme"; "a1" ], Some "2026-10", Some 99m, Some "99"
                    [ "acme"; "a2" ], Some "2026-09", Some 20m, Some "20"
                    [ "acme"; "a2" ], Some "2026-10", Some 22m, Some "22"
                    [ "zeta"; "z1" ], Some "2026-09", Some 50m, Some "50"
                    [ "zeta"; "z1" ], Some "2026-10", Some 55m, Some "55"
                    [ "zeta"; "z2" ], Some "2026-09", None, Some "absent (no data loaded for this period)"
                ]
                "the corpus is what it says: the revision, the period left alone, the filled gap and a standing one"

            let segment =
                ok
                    "segment"
                    (FactBrowseHandler.querySeries w.Deps w.Scope "u" (series "segment" september.From october.To))

            Expect.allEqual (segment.Points |> List.map _.Withheld) true "every Internal point is withheld"
        }

        test "under a taint configuration the row path's verdicts are the fact path's, at the taint index read alone" {
            let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
            let w, _ = mixedWorld counting

            let taint =
                DisclosureTaintConfig.ofLists [
                    {
                        PolicyRef = "licensed"
                        Mode = TaintPropagating
                        PermitSurfaces = [ FactRetrieval ]
                        ContributorScope = None
                    }
                ] []

            let gate =
                RecordingGate(FactDisclosureGate(w.Store, w.Events, taint = taint), Set.empty)

            let rowPath = { w.Deps with Gate = gate }
            let factPath = { w.Deps with Gate = heldOnly gate }
            let request = series "revenue" september.From october.To

            ok "warm" (FactBrowseHandler.querySeries rowPath w.Scope "u" request) |> ignore

            let downloads (deps: FactBrowseHandler.FactBrowseDeps) =
                counting.Reset()
                let s = ok "chart" (FactBrowseHandler.querySeries deps w.Scope "u" request)
                s, counting.Downloads - counting.SurfaceDownloads

            let viaRows, rowReads = downloads rowPath
            let viaFacts, factReads = downloads factPath

            counting.Reset()

            (w.Store :?> IDisclosureTaintIndexSource).ReadTaintIndex w.Scope
            |> Async.RunSynchronously
            |> ignore

            let graphLoad = counting.Downloads - counting.SurfaceDownloads

            printfn
                "Phase 1126 chart under a taint config (%d points): row path %d fact downloads, fact path (after 1125) %d, the taint index read alone %d"
                (List.length viaRows.Points)
                rowReads
                factReads
                graphLoad

            Expect.equal viaRows viaFacts "the same series, verdicts included"
            Expect.isGreaterThan graphLoad 0 "the probe sees the index read"
            Expect.equal rowReads graphLoad "the row path pays the taint index read and reads no point"

            Expect.equal
                factReads
                (graphLoad + List.length viaFacts.Points)
                "the fact path paid a read per point on top of it"
        }
    ]

// ─── Phase 1125 — the gate judges the facts the read already holds ──
//
// The oracle for "downloaded once" is the same read through a gate that
// reads nothing (it admits every id it is handed): what that read
// downloads is what the browse's own store reads cost. The platform gate
// must add nothing to it. Before 1125 it re-read every fact it judged, so
// each warm read cost its store reads plus one download per fact.

let private heldTests =
    testList "Phase 1125 — a browse read downloads its facts once, not again at the gate" [
        test "the composed held form is the composed gate itself" {
            let d = composeDemo ()
            let gate = d.Services.GetRequiredService<IFactDisclosureGate>()
            let held = d.Services.GetRequiredService<IHeldFactDisclosureGate>()

            Expect.isTrue
                (obj.ReferenceEquals(gate, held))
                "the fact tier composes the held form over its own gate, so the two cannot disagree"
        }

        // The chart read is not in this list: since Phase 1126 it is answered
        // and judged from surface rows, so it reads no fact at all (the 1126
        // list pins that) and has no fact reads for the gate to repeat.
        test "a warm row page, movers page and fact detail cost only their store reads" {
            let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
            let w, _ = worldOn counting Set.empty (tickingClock ())

            let cells (shift: decimal) = [
                for i in 0..199 do
                    let brand = sprintf "b%02d" (i % 4)
                    let sku = sprintf "s%04d" i
                    rowIn september brand sku (decimal i)
                    rowIn october brand sku (decimal i + shift)
            ]

            run w (cells 0m) |> ignore
            let runId, _ = run w (cells 3m)

            let oracle = {
                w.Deps with
                    Gate = FactDisclosureGate.held (DenySetGate Set.empty)
            }

            // The pre-1125 shape, kept as the "before" of the measurement: the
            // platform gate behind a decorator, so its held form is its id
            // form and it re-reads every fact it judges.
            let idForm = {
                w.Deps with
                    Gate =
                        FactDisclosureGate.held (
                            let platform = FactDisclosureGate.create w.Store w.Events

                            { new IFactDisclosureGate with
                                member _.Check(scopeId: string, principal, surface, factIds) =
                                    platform.Check(scopeId, principal, surface, factIds)

                                member _.Check(scope: ResolvedScope, principal, surface, factIds) =
                                    platform.Check(scope, principal, surface, factIds)
                            }
                        )
            }

            let firstPage =
                ok "rows" (FactBrowseHandler.queryRows w.Deps w.Scope "u" (query "revenue" TopOfRanking 0 10))

            let reads: (string * (FactBrowseHandler.FactBrowseDeps -> unit)) list = [
                "QueryRows",
                fun deps ->
                    ok "rows" (FactBrowseHandler.queryRows deps w.Scope "u" (query "revenue" TopOfRanking 0 10))
                    |> ignore
                "GetRunMovers",
                fun deps ->
                    ok "movers" (FactBrowseHandler.getRunMovers deps w.Scope "u" (skuTable.Id, runId))
                    |> ignore
                "GetFact",
                fun deps ->
                    ok "fact" (FactBrowseHandler.getFact deps w.Scope "u" firstPage.Rows.Head.FactId)
                    |> ignore
            ]

            let downloads (read: FactBrowseHandler.FactBrowseDeps -> unit) (deps: FactBrowseHandler.FactBrowseDeps) =
                counting.Reset()
                read deps
                counting.Downloads

            for name, read in reads do
                // Warm: the first read of a metric may build its surface.
                read w.Deps
                let storeOnly = downloads read oracle
                let gated = downloads read w.Deps
                let before = downloads read idForm

                printfn
                    "Phase 1125 %s: %d downloads through the gate (%d through the id form), %d for the read alone"
                    name
                    gated
                    before
                    storeOnly

                Expect.isGreaterThan storeOnly 0 (sprintf "%s reads its facts from the store" name)
                Expect.isGreaterThan before storeOnly (sprintf "%s: the probe sees the id form's re-reads" name)
                Expect.equal gated storeOnly (sprintf "%s: the gate judges the facts the read holds, reading none" name)
        }
    ]

/// Every Phase 895 server case, and Phase 1113's series cases.
// ─── Phase 1127 — a view reads once and refines locally ──────────────
//
// The oracle is the server: for every drill-down page and every series the
// existing reads serve over a corpus with revisions, a slice-scoped run,
// gaps, an Internal column and facts denied by name, the local pipeline
// over the slice (`FactBrowseSlice.rowPage` / `.series`) must answer the
// same rows, order, ranks, withheld positions and paging flags. The probe
// below then counts what a representative browsing session costs both ways.

let private allPeriods (metrics: string list) : FactSliceQuery = {
    TableId = skuTable.Id
    PeriodFrom = FactBrowseApi.AllPeriodsFrom
    PeriodTo = FactBrowseApi.AllPeriodsTo
    SubjectPrefix = []
    Metrics = metrics
}

let private holdSlice (deps: FactBrowseHandler.FactBrowseDeps) (scope: ResolvedScope) : FactBrowseSlice.Held =
    match FactBrowseSlice.hold (ok "slice" (FactBrowseHandler.querySlice deps scope "u" (allPeriods []))) with
    | Ok held -> held
    | Error e -> failtestf "hold: %s" e

let private localOk (label: string) (r: Result<'T, string>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %s" label e

/// The mixed corpus with two revenue heads denied by name, read through the
/// row form and through the held (fact) form, so both the slice's paths and
/// the withheld positions are exercised.
let private deniedWorld () : World * FactBrowseHandler.FactBrowseDeps list =
    let w, _ = mixedWorld (InMemoryBlobStorage())

    let denied =
        revenueFacts w
        |> List.filter (fun f -> f.Subject.Path = [ "acme"; "a2" ] || f.Subject.Path = [ "zeta"; "z1" ])
        |> List.filter (fun f -> f.Period = october)
        |> List.map _.FactId
        |> Set.ofList

    let gate = RecordingGate(FactDisclosureGate(w.Store, w.Events), denied)
    let rowForm = { w.Deps with Gate = gate }
    w, [ rowForm; { rowForm with Gate = heldOnly gate } ]

let private sliceTests =
    testList "Phase 1127 — the slice answers what the server reads answer" [
        test "every drill-down page, both directions, every prefix, both forms" {
            let w, forms = deniedWorld ()

            for deps in forms do
                let held = holdSlice deps w.Scope
                Expect.isGreaterThan held.Slice.WithheldCount 0 "the corpus withholds cells"

                for metric in [ "revenue"; "segment" ] do
                    for direction in [ TopOfRanking; BottomOfRanking ] do
                        for prefix in [ []; [ "acme" ]; [ "zeta" ]; [ "acme"; "a1" ]; [ "nobody" ] ] do
                            for size in [ 1; 2; 3; 50 ] do
                                for page in 0..4 do
                                    let request = {
                                        query metric direction page size with
                                            SubjectPrefix = prefix
                                    }

                                    let label = sprintf "%s %A %A page %d of %d" metric direction prefix page size
                                    let server = ok label (FactBrowseHandler.queryRows deps w.Scope "u" request)
                                    let local = localOk label (FactBrowseSlice.rowPage held request)

                                    Expect.isTrue (FactBrowseSlice.coversRows held.Slice request) label
                                    Expect.equal local.Rows server.Rows (label + ": rows, order and ranks")
                                    Expect.equal local.WithheldCount server.WithheldCount (label + ": withheld")
                                    Expect.equal local.Withheld server.Withheld (label + ": withheld policies")
                                    Expect.equal local.HasMore server.HasMore (label + ": has more")

                                    Expect.equal
                                        local.ReachedRankCeiling
                                        server.ReachedRankCeiling
                                        (label + ": rank ceiling")

                                    Expect.equal
                                        {
                                            local with
                                                Rows = []
                                                Population = server.Population
                                        }
                                        { server with Rows = [] }
                                        (label + ": every other page field")

                                    let lp, sp = local.Population, server.Population
                                    Expect.equal lp.SubjectCount sp.SubjectCount (label + ": subjects")
                                    Expect.equal lp.FactCount sp.FactCount (label + ": facts")
                                    Expect.equal lp.ComparableCount sp.ComparableCount (label + ": comparable")
                                    Expect.equal lp.PeriodFrom sp.PeriodFrom (label + ": coverage from")
                                    Expect.equal lp.PeriodTo sp.PeriodTo (label + ": coverage to")
                                    Expect.equal lp.Methods sp.Methods (label + ": methods")

                                    // The local magnitudes are never looser than the
                                    // server's, and equal to them where both ride.
                                    if sp.ValueStatisticsWithheld then
                                        Expect.isTrue lp.ValueStatisticsWithheld (label + ": withheld is withheld")

                                    if not lp.ValueStatisticsWithheld then
                                        Expect.equal
                                            (lp.Minimum, lp.Maximum, lp.Mean)
                                            (sp.Minimum, sp.Maximum, sp.Mean)
                                            (label + ": magnitudes")
        }

        test "every series, both forms" {
            let w, forms = deniedWorld ()

            for deps in forms do
                let held = holdSlice deps w.Scope

                for metric in [ "revenue"; "segment" ] do
                    for from, until in
                        [
                            september.From, october.To
                            september.From, september.To
                            october.From, october.To
                            september.From.AddDays 10.0, october.From.AddDays 3.0
                        ] do
                        for prefix in [ []; [ "acme" ]; [ "zeta"; "z1" ] ] do
                            for cap in [ 1; FactBrowseApi.MaxSeriesPoints ] do
                                let request = {
                                    series metric from until with
                                        SubjectPrefix = prefix
                                        MaxPoints = cap
                                }

                                let label = sprintf "%s %O..%O %A cap %d" metric from until prefix cap
                                Expect.isTrue (FactBrowseSlice.coversSeries held.Slice request) label

                                Expect.equal
                                    (FactBrowseSlice.series held request)
                                    (Async.RunSynchronously(FactBrowseHandler.querySeries deps w.Scope "u" request))
                                    (label + ": the same series, or the same refusal")
        }

        test "no withheld value or rendering is encoded" {
            let w, forms = deniedWorld ()
            let held = holdSlice (List.head forms) w.Scope
            let table = localOk "decode" (FactBrowseSlice.decode held.Slice)

            let cells (name: string) =
                table.Columns
                |> List.find (fun c -> c.Name = name)
                |> Fuaran.Core.Column.toCells
                |> Array.ofList

            let mutable withheld = 0

            for metric in [ "revenue"; "segment" ] do
                let policies = cells (FactBrowseSlice.metricColumn metric "withheld")
                let values = cells metric
                let renderings = cells (FactBrowseSlice.metricColumn metric "rendering")

                for i in 0 .. policies.Length - 1 do
                    if policies.[i] <> Fuaran.Core.Cell.Null then
                        withheld <- withheld + 1
                        Expect.equal values.[i] Fuaran.Core.Cell.Null "a withheld cell carries no value"
                        Expect.equal renderings.[i] Fuaran.Core.Cell.Null "a withheld cell carries no rendering"

            Expect.equal withheld held.Slice.WithheldCount "every withheld cell is counted"

            for denied in [ "12"; "55" ] do
                Expect.isFalse
                    (held.Slice.Table.Contains(sprintf "\"%s\"" denied))
                    (sprintf "the denied value %s is nowhere in the encoded slice" denied)
        }

        test "the run stamp is the listing's, and moves with a run" {
            let w = world ()
            run w [ row "acme" "a1" 1m ] |> ignore

            let first =
                ok "slice" (FactBrowseHandler.querySlice w.Deps w.Scope "u" (allPeriods []))

            let listing = ok "table" (FactBrowseHandler.getTable w.Deps w.Scope skuTable.Id)
            Expect.isSome first.RunStamp "a committed table has a stamp"
            Expect.equal first.RunStamp listing.Listing.RunStamp "the slice records the listing's stamp"
            run w [ row "acme" "a1" 2m ] |> ignore
            let after = ok "table" (FactBrowseHandler.getTable w.Deps w.Scope skuTable.Id)
            Expect.notEqual after.Listing.RunStamp first.RunStamp "a run moves the stamp the slice is checked against"
        }

        test "a slice over the cap is refused before the gate, naming the cap" {
            let facts = syntheticPopulation.Force()
            let notHere () : Async<'T> = async { return failtest "not read in this test" }

            let store =
                { new IFactStore with
                    member _.Assert(_: string, _: FactDraft) : Async<Result<Fact, string>> = notHere ()
                    member _.Assert(_: ResolvedScope, _: FactDraft) : Async<Result<Fact, string>> = notHere ()

                    member _.AssertBatch(_: string, _: FactDraft list) : Async<Result<BatchAssertReceipt, string>> =
                        notHere ()

                    member _.AssertBatch
                        (_: ResolvedScope, _: FactDraft list)
                        : Async<Result<BatchAssertReceipt, string>> =
                        notHere ()

                    member _.Get(_: string, _: string) : Async<Fact option> = notHere ()
                    member _.Get(_: ResolvedScope, _: string) : Async<Fact option> = notHere ()
                    member _.Query(_: string, _: FactQuery) : Async<Fact list> = notHere ()
                    member _.Query(_: ResolvedScope, _: FactQuery) : Async<Fact list> = async.Return facts

                    member _.QueryWithCompetition(_: string, _: FactQuery) : Async<FactWithCompetition list> =
                        notHere ()

                    member _.QueryWithCompetition(_: ResolvedScope, _: FactQuery) : Async<FactWithCompetition list> =
                        notHere ()

                    member _.QuerySupersessionChain(_: string, _: string) : Async<Fact list> = notHere ()
                    member _.QuerySupersessionChain(_: ResolvedScope, _: string) : Async<Fact list> = notHere ()

                    member _.QueryPopulation(_: string, _: PopulationQuery) : Async<Result<PopulationResult, string>> =
                        notHere ()

                    member _.QueryPopulation
                        (_: ResolvedScope, _: PopulationQuery)
                        : Async<Result<PopulationResult, string>> =
                        notHere ()
                }

            let gate =
                { new IHeldFactDisclosureGate with
                    member _.CheckHeld(_, _, _, _, _) = async { return failtest "the gate is never asked" }
                }

            let deps = {
                largeDeps (DenySetGate Set.empty) with
                    Store = store
                    Gate = gate
            }

            match
                FactBrowseHandler.querySlice deps (resolved (newScope ())) "u" (allPeriods [ "revenue" ])
                |> Async.RunSynchronously
            with
            | Ok _ -> failtest "a 100,000-cell slice must be refused"
            | Error e ->
                Expect.stringContains e "100000 cells" "the refusal names the count"
                Expect.stringContains e "FactBrowseApi.MaxSliceCells" "and the cap"
                Expect.stringContains e "Narrow the period range" "and how to narrow"
        }

        test "a derived figure renders as the server renders it (the Fable pack asserts the same strings)" {
            Expect.equal (FactBrowseSlice.renderDerived "N2" 1234.5m) "1,234.50" "N2"
            Expect.equal (FactBrowseSlice.renderDerived "N0" -1234.5m) "-1,235" "N0, half away from zero"
            Expect.equal (FactBrowseSlice.renderDerived "F1" 2.25m) "2.3" "F1"
            Expect.equal (FactBrowseSlice.renderDerived "P1" 0.1234m) "12.3 %" "P1"
            Expect.equal (FactBrowseSlice.renderDerived "" 3.5m) "3.5" "verbatim"
        }

        test "a metric the table lacks, and an empty range, are refused" {
            let w = world ()

            for request, needle in
                [
                    allPeriods [ "nope" ], "no metric column 'nope'"
                    {
                        allPeriods [] with
                            PeriodTo = FactBrowseApi.AllPeriodsFrom
                    },
                    "not a half-open"
                ] do
                match
                    FactBrowseHandler.querySlice w.Deps w.Scope "u" request
                    |> Async.RunSynchronously
                with
                | Ok _ -> failtestf "expected a refusal containing %s" needle
                | Error e -> Expect.stringContains e needle "the refusal says why"
        }
    ]

// ─── Phase 1127 — what a browsing session costs, before and after ────
//
// One representative session over a table of `skus` SKUs (two months, two
// columns, twenty brands): open the table, change metric twice, sort, filter
// to a branch, page three times, open one chart. BEFORE is the session as the
// server reads answer it (every interaction a read); AFTER is one slice read,
// then every interaction a local pipeline. Downloads count blob reads of any
// kind.

type private SessionCost = {
    Reads: int
    Downloads: int
    Milliseconds: float list
}

let private sessionSteps: (string * Choice<FactRowQuery, FactSeriesQuery>) list = [
    "open the table", Choice1Of2(query "revenue" TopOfRanking 0 25)
    "change metric", Choice1Of2(query "segment" TopOfRanking 0 25)
    "change metric back", Choice1Of2(query "revenue" TopOfRanking 0 25)
    "sort", Choice1Of2(query "revenue" BottomOfRanking 0 25)
    "filter to a branch",
    Choice1Of2 {
        query "revenue" TopOfRanking 0 25 with
            SubjectPrefix = [ "b07" ]
    }
    for page in 1..3 do
        sprintf "page %d" page,
        Choice1Of2 {
            query "revenue" TopOfRanking page 25 with
                SubjectPrefix = [ "b07" ]
        }
    "open one chart",
    Choice2Of2 {
        series "revenue" september.From october.To with
            SubjectPrefix = [ "b07" ]
    }
]

let private session (skus: int) : SessionCost * SessionCost * int * float =
    let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
    let w, _ = worldOn counting Set.empty (tickingClock ())

    run w [
        for i in 0 .. skus - 1 do
            let brand = sprintf "b%02d" (i % 20)
            let sku = sprintf "s%05d" i
            rowIn september brand sku (decimal i)
            rowIn october brand sku (decimal (i + 1))
    ]
    |> ignore

    let timed (f: unit -> unit) =
        let clock = Diagnostics.Stopwatch.StartNew()
        f ()
        clock.Stop()
        clock.Elapsed.TotalMilliseconds

    counting.Reset()

    let beforeMs =
        sessionSteps
        |> List.map (fun (_, step) ->
            timed (fun () ->
                match step with
                | Choice1Of2 q -> ok "rows" (FactBrowseHandler.queryRows w.Deps w.Scope "u" q) |> ignore
                | Choice2Of2 q -> ok "series" (FactBrowseHandler.querySeries w.Deps w.Scope "u" q) |> ignore))

    let before = {
        Reads = List.length sessionSteps
        Downloads = counting.Downloads
        Milliseconds = beforeMs
    }

    counting.Reset()
    let mutable held = None

    let sliceMs = timed (fun () -> held <- Some(holdSlice w.Deps w.Scope))

    let held = held.Value
    let sliceDownloads = counting.Downloads

    let localMs =
        sessionSteps
        |> List.map (fun (label, step) ->
            timed (fun () ->
                match step with
                | Choice1Of2 q ->
                    Expect.isTrue (FactBrowseSlice.coversRows held.Slice q) label
                    localOk label (FactBrowseSlice.rowPage held q) |> ignore
                | Choice2Of2 q ->
                    Expect.isTrue (FactBrowseSlice.coversSeries held.Slice q) label
                    localOk label (FactBrowseSlice.series held q) |> ignore))

    let after = {
        Reads = 1
        Downloads = counting.Downloads
        Milliseconds = localMs
    }

    Expect.equal after.Downloads sliceDownloads "the local interactions download nothing"
    before, after, held.Slice.Table.Length, sliceMs

let private sessionCostTests =
    testList "Phase 1127 — a browsing session pays one read per slice and nothing per interaction" [
        for skus in [ 500; 5_000 ] do
            test (sprintf "the session over %d facts" (4 * skus)) {
                let before, after, encodedBytes, sliceMs = session skus

                let show (ms: float list) =
                    ms |> List.map (sprintf "%.1f") |> String.concat " / "

                printfn
                    "Phase 1127 session over %d facts — BEFORE: %d server reads, %d downloads, per interaction ms [%s]; AFTER: %d slice read (%.1f ms incl. decode+prepare, %d downloads, %d encoded bytes), %d downloads after it, per interaction ms [%s]"
                    (4 * skus)
                    before.Reads
                    before.Downloads
                    (show before.Milliseconds)
                    after.Reads
                    sliceMs
                    after.Downloads
                    encodedBytes
                    0
                    (show after.Milliseconds)

                Expect.equal before.Reads 9 "the session is nine interactions, each a server read today"
                Expect.equal after.Reads 1 "after: one slice read for the table"
                Expect.isLessThanOrEqual after.Downloads 2 "one surface download per metric, no fact blob"
            }

        test "a slice at the cap encodes and prepares within the stated budget" {
            let cells = [
                for i in 0 .. FactBrowseApi.MaxSliceCells - 1 do
                    {
                        Metric = "revenue"
                        FactId = sprintf "f%06d" i
                        Subject = sprintf "products/b%02d>s%05d" (i % 20) (i / 2)
                        SubjectPath = [ sprintf "b%02d" (i % 20); sprintf "s%05d" (i / 2) ]
                        PeriodFrom = (if i % 2 = 0 then september else october).From
                        PeriodTo = (if i % 2 = 0 then september else october).To
                        PeriodLabel = (if i % 2 = 0 then september else october).Label
                        Value = Some(decimal i)
                        Rendering = Some(string i)
                        AsOf = t0
                        Freshness = "fresh"
                        WithheldPolicy = None
                        RankDescending = Some(FactBrowseApi.MaxSliceCells - i)
                        RankAscending = Some(i + 1)
                    }
                    : FactSliceCell
            ]

            let encoded =
                FactBrowseSlice.build [ "revenue" ] cells
                |> Result.bind FactBrowseSlice.encode
                |> localOk "encode"

            let slice = {
                TableId = skuTable.Id
                Hierarchy = "products"
                Level = "sku"
                Method = "computed:sales-rollup:v1:sku-sales"
                PeriodFrom = FactBrowseApi.AllPeriodsFrom
                PeriodTo = FactBrowseApi.AllPeriodsTo
                SubjectPrefix = []
                Metrics = [
                    {
                        Metric = "revenue"
                        DisplayFormat = ""
                    }
                ]
                Table = encoded
                RunStamp = None
                CellCount = FactBrowseApi.MaxSliceCells
                MaxCells = FactBrowseApi.MaxSliceCells
                RankCeiling = PopulationQuery.MaxTopK
                WithheldCount = 0
                Withheld = []
            }

            let clock = Diagnostics.Stopwatch.StartNew()
            let held = localOk "hold" (FactBrowseSlice.hold slice)
            let prepareMs = clock.Elapsed.TotalMilliseconds
            clock.Restart()

            let page =
                localOk "page" (FactBrowseSlice.rowPage held (query "revenue" TopOfRanking 0 25))

            let pageMs = clock.Elapsed.TotalMilliseconds

            printfn
                "Phase 1127 slice at the cap (%d cells): %d encoded bytes, decode+prepare %.1f ms, one local page %.1f ms (.NET)"
                FactBrowseApi.MaxSliceCells
                encoded.Length
                prepareMs
                pageMs

            Expect.equal (List.length page.Rows) 25 "a page of the largest"

            Expect.equal
                page.Rows.Head.FactId
                (sprintf "f%06d" (FactBrowseApi.MaxSliceCells - 1))
                "ranked by the slice's own positions"
        }
    ]

// ─── A fact table describes itself ───────────────────────────────────
//
// A description is declared beside the declaration it describes
// (`ServerModule.describeFactTables`), refused at compose when it names a
// table or column no module declares, and read back on the path the browse
// reads AND the model's fact tools share: the `IFactTableRegistry` the
// request resolves (`FactBrowseHandler.factBrowseDeps`).

let private skuDescription: FactTableDescription = {
    TableId = skuTable.Id
    Description = "Monthly revenue by SKU, from the order header extract, excluding cancelled orders."
    Columns = [ "revenue", "Net revenue after returns." ]
}

let private describingApp (descriptions: FactTableDescription list) : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules [
        ServerModule.create "sales"
        |> ServerModule.declareFactTables [ skuTable ]
        |> ServerModule.describeFactTables descriptions
    ]
    |> ServerApp.bindFactTables (BindAllFactTables DefaultFactTableWriter.Destination)

/// The message `ServerApp.factTableRegistry` refuses a composition with.
let private refusal (app: ServerApp) : string =
    let refused =
        try
            ServerApp.factTableRegistry app |> ignore
            None
        with ex ->
            Some ex.Message

    match refused with
    | Some message -> message
    | None -> failtest "expected the composition to be refused at compose"

let private descriptionTests =
    testList "a fact table describes itself" [
        test "a composed description reaches the browse detail through the registry the request resolves" {
            let app = describingApp [ skuDescription ]

            let composed =
                ServerApp.factTableRegistry app
                |> Option.defaultWith (fun () -> failtest "a declaring composition registers a table registry")

            let w = world ()
            let services = ServiceCollection()
            services.AddSingleton<IFactStore>(w.Store) |> ignore

            services.AddSingleton<IFactDisclosureGate>(FactDisclosureGate(w.Store, w.Events))
            |> ignore

            services.AddSingleton<IFactTableRegistry>(composed) |> ignore
            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()

            let deps =
                FactBrowseHandler.factBrowseDeps ctx
                |> Option.defaultWith (fun () -> failtest "the fact tier is composed")

            Expect.equal
                (FactTableRegistry.tryDescription deps.Tables skuTable.Id)
                (Some skuDescription)
                "the model's tools read the description off the same resolved registry"

            let detail = ok "table" (FactBrowseHandler.getTable deps w.Scope skuTable.Id)

            Expect.equal detail.Description (Some skuDescription.Description) "the table's description"

            Expect.equal
                (detail.Columns |> List.map (fun c -> c.Metric, c.Description))
                [ "revenue", Some "Net revenue after returns."; "segment", None ]
                "each column carries its description, and an undescribed column reads None"
        }

        test "a table declared without a description reads None" {
            let w = world ()
            let detail = ok "table" (FactBrowseHandler.getTable w.Deps w.Scope skuTable.Id)
            Expect.isNone detail.Description "no table description"
            Expect.all detail.Columns (fun c -> c.Description.IsNone) "no column description"

            let composed = describingApp [] |> ServerApp.factTableRegistry |> Option.get
            Expect.isNone (FactTableRegistry.tryDescription composed skuTable.Id) "nothing described"
        }

        test "a registry without the capability has no descriptions" {
            let bare =
                { new IFactTableRegistry with
                    member _.Tables = [ skuTable ]
                    member _.TryGetTable _ = Some skuTable
                    member _.TablesByModule _ = [ skuTable ]
                    member _.DeclaringModule _ = Some "sales"
                    member _.DestinationOf _ = None
                }

            Expect.isNone (FactTableRegistry.tryDescription bare skuTable.Id) "probed, absent"
        }

        test "a composition that declares and describes nothing registers no table registry" {
            Expect.isNone (ServerApp.factTableRegistry ServerApp.empty) "nothing registered (GP 13)"
        }

        test "a description of a table no module declares is refused at compose, naming the module and the table" {
            let message =
                refusal (
                    describingApp [
                        {
                            skuDescription with
                                TableId = "brand-sales"
                                Columns = []
                        }
                    ]
                )

            Expect.stringContains message "'brand-sales'" "the undeclared table"
            Expect.stringContains message "'sales'" "the describing module"
            Expect.stringContains message "'sku-sales'" "the declared tables"
        }

        test "a description of a column the table does not declare is refused at compose, naming both" {
            let message =
                refusal (
                    describingApp [
                        {
                            skuDescription with
                                Columns = [ "margin", "Gross margin." ]
                        }
                    ]
                )

            Expect.stringContains message "column 'margin'" "the undeclared column"
            Expect.stringContains message "fact table 'sku-sales'" "its table"
            Expect.stringContains message "'revenue', 'segment'" "the table's declared columns"
        }

        test "a description with no table declared at all is refused at compose" {
            let app =
                ServerApp.empty
                |> ServerApp.addModules [
                    ServerModule.create "notes"
                    |> ServerModule.describeFactTables [ skuDescription ]
                ]

            Expect.stringContains (refusal app) "'sku-sales', which no module declares" "refused, not ignored"
        }

        test "a table or a column described twice is refused at compose" {
            let twice =
                refusal (
                    ServerApp.empty
                    |> ServerApp.addModules [
                        ServerModule.create "sales"
                        |> ServerModule.declareFactTables [ skuTable ]
                        |> ServerModule.describeFactTables [ skuDescription ]
                        ServerModule.create "finance"
                        |> ServerModule.describeFactTables [ skuDescription ]
                    ]
                )

            Expect.stringContains twice "described more than once" "a table described twice"
            Expect.stringContains twice "'sales', 'finance'" "both describing modules"

            let column =
                refusal (
                    describingApp [
                        {
                            skuDescription with
                                Columns = [ "revenue", "Net."; "revenue", "Gross." ]
                        }
                    ]
                )

            Expect.stringContains column "column 'revenue' of fact table 'sku-sales' 2 times" "a column described twice"
        }
    ]

let tests =
    testList "Phase 895 — fact browse surface" [
        boundedTests
        disclosureTests
        sameSurfaceTests
        tableTests
        notificationTests
        compositionTests
        composedDemoTests
        seriesTests
        costTests
        branchCostTests
        inertTaintTests
        parityTests
        heldTests
        sliceTests
        sessionCostTests
        descriptionTests
    ]