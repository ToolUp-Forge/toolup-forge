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

open System
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
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

let private row (brand: string) (sku: string) (revenue: decimal) : FactTableRow = {
    Subject = [ brand; sku ]
    Period = september
    Values = Map.ofList [ "revenue", Scalar revenue; "segment", Categorical "core" ]
}

/// A gate that consults the real one and additionally denies the ids a
/// test names, recording every surface it was asked at.
type private RecordingGate(inner: IFactDisclosureGate, denied: Set<string>) =
    let surfaces = ResizeArray<FactEgressSurface>()

    member _.Surfaces = List.ofSeq surfaces

    interface IFactDisclosureGate with
        member _.Check(scopeId: string, principal: string, surface: FactEgressSurface, factIds: string list) = async {
            surfaces.Add surface
            let! verdicts = inner.Check(scopeId, principal, surface, factIds)

            return
                verdicts
                |> Map.map (fun id v ->
                    if denied.Contains id then
                        FactNotDisclosable "licence-x"
                    else
                        v)
        }

        member _.Check(scope: ResolvedScope, principal: string, surface: FactEgressSurface, factIds: string list) = async {
            surfaces.Add surface
            let! verdicts = inner.Check(scope, principal, surface, factIds)

            return
                verdicts
                |> Map.map (fun id v ->
                    if denied.Contains id then
                        FactNotDisclosable "licence-x"
                    else
                        v)
        }

type private World = {
    Deps: FactBrowseHandler.FactBrowseDeps
    Writer: IFactTableWriter
    Store: IFactStore
    Events: IEventStore
    Scope: ResolvedScope
}

let private worldWith (denied: Set<string>) (clock: unit -> DateTime) : World * RecordingGate =
    let storage = InMemoryBlobStorage()
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let store =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let writer =
        DefaultFactTableWriter.createWithClock store storage events tables (Some registry) clock

    let gate = RecordingGate(FactDisclosureGate.create store events, denied)

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

/// A clock that moves one second per reading, so successive runs and the
/// facts they assert are ordered in time as they are in the real world.
let private tickingClock () : unit -> DateTime =
    let ticks = ref 0

    fun () ->
        ticks.Value <- ticks.Value + 1
        t0.AddSeconds(float ticks.Value)

let private world () =
    worldWith Set.empty (tickingClock ()) |> fst

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
    let checkedIds = ResizeArray<string>()
    member _.CheckedIds = List.ofSeq checkedIds

    interface IFactDisclosureGate with
        member _.Check(_: string, _: string, _: FactEgressSurface, factIds: string list) = async {
            checkedIds.AddRange factIds

            return
                factIds
                |> List.map (fun id ->
                    id,
                    (if denied.Contains id then
                         FactNotDisclosable "licence-x"
                     else
                         FactDisclosable))
                |> Map.ofList
        }

        member _.Check(_: ResolvedScope, _: string, _: FactEgressSurface, factIds: string list) = async {
            checkedIds.AddRange factIds

            return
                factIds
                |> List.map (fun id ->
                    id,
                    (if denied.Contains id then
                         FactNotDisclosable "licence-x"
                     else
                         FactDisclosable))
                |> Map.ofList
        }

let private largeDeps (gate: IFactDisclosureGate) : FactBrowseHandler.FactBrowseDeps = {
    Tables = tables
    Writer = None
    Store = largeStore ()
    Gate = gate
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
                    Gate = RecordingGate(FactDisclosureGate.create w0.Store w0.Events, Set.ofList [ a2.FactId ])
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
                        w.Deps.Gate
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

            match composed.Extensions.ServiceConfig with
            | Some configure -> configure services |> ignore
            | None -> failtest "the decoration must be registered"

            let writer = services.BuildServiceProvider().GetRequiredService<IFactTableWriter>()
            Expect.isFalse (obj.ReferenceEquals(writer, w.Writer)) "the composed writer is decorated"

            run { w with Writer = writer } [ row "acme" "a1" 1m ] |> ignore
            Expect.equal channel.Published.Length 1 "and it announces the run"
        }
    ]

/// Every Phase 895 server case.
let tests =
    testList "Phase 895 — fact browse surface" [
        boundedTests
        disclosureTests
        sameSurfaceTests
        tableTests
        notificationTests
        compositionTests
    ]