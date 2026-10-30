// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.FactBrowseClientTests

// ─── Phase 895 — the fact browse client companion ────────────────────
//
// Pins, under Fable, the client half of the fact browse surface:
//
//   1. **The pages render with a table that has no runs yet**, and every
//      page has its empty state — asserted over real markup from a real
//      mount.
//   2. **The browser holds one page.** The model's transitions never
//      widen a request past the contract's page ceiling, and a drill-down
//      page replaces the last rather than accumulating.
//   3. **The knowledge base badge is gated on composition.** A coverage
//      narrative shows no fact-table badge until the fact browse module
//      is in the shell's published module list, and every other document
//      is untouched either way — so a deployment without the companion
//      renders byte-for-byte as before.
//
// Fable-side for the reason every pack here records: the model holds a
// module-level `Api.makeProxy`, which cannot be built under .NET
// reflection, and rendering needs a JS runtime.

open System
open SharedTypes
open ToolUp.Platform
open ToolUp.Platform.Testing
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Facts.Client
open ToolUp.Facts.Client.FactBrowseModel
open ToolUp.AI.Client.Tests.NodeTest

// ─── Fixtures ────────────────────────────────────────────────────────

let private listing (tableId: string) (metrics: string list) : FactTableListing = {
    TableId = tableId
    Module = "Pricing"
    Metrics = metrics
    Hierarchy = "product"
    Level = "sku"
    PeriodGrain = "Month"
    HistoryMode = "AppendByRun"
    RowCount = None
    LastRefreshedAt = None
    Freshness = "never-refreshed"
    OverdueBySeconds = None
    LatestRunOutcome = None
    LatestRunReason = None
    RunStamp = None
}

let private detail (tableId: string) : FactTableDetail = {
    Listing = listing tableId [ "elasticity" ]
    Columns = [
        {
            Metric = "elasticity"
            Shape = "Scalar"
            Disclosure = "Surfaceable"
            Description = None
        }
    ]
    ProducingOperation = "price-model"
    SchemaVersion = 1
    RefreshCadenceSeconds = 86_400L
    Requirement = "Optional"
    Description = None
}

/// The same table, described: a table description and one column hint.
let private describedDetail (tableId: string) : FactTableDetail =
    let plain = detail tableId

    {
        plain with
            Description = Some "Monthly price response by SKU, from the price-test extract."
            Columns =
                plain.Columns
                |> List.map (fun c -> {
                    c with
                        Description = Some "Own-price elasticity of unit volume."
                })
    }

let private noRuns (tableId: string) : FactRunPage = {
    TableId = tableId
    Runs = []
    Page = 0
    PageSize = FactBrowseApi.DefaultPageSize
    MaxPageSize = FactBrowseApi.MaxPageSize
    TotalRuns = 0
}

let private noCall () : Async<'T> = async { return failwith "not called in this test" }

/// A fake API: the transitions only BUILD commands here, so no member is
/// ever invoked — building one that fails loudly proves it.
let private fakeApi: IFactBrowseApi = {
    ListTables = fun () -> noCall ()
    GetTable = fun _ -> noCall ()
    ListRuns = fun _ -> noCall ()
    GetRunMovers = fun _ -> noCall ()
    QueryRows = fun _ -> noCall ()
    GetFact = fun _ -> noCall ()
    QuerySeries = fun _ -> noCall ()
    QuerySlice = fun _ -> noCall ()
}

let private step (msg: Msg) (model: Model) = updateWith fakeApi msg model |> fst

/// The messages a command dispatches synchronously (`Cmd.ofMsg`).
let private dispatched (cmd: ToolUp.Elmish.Cmd<Msg>) : Msg list =
    let seen = ResizeArray<Msg>()

    for effect in cmd do
        try
            effect seen.Add
        with _ ->
            ()

    List.ofSeq seen

let private noop (_: Msg) = ()

// ─── Model ───────────────────────────────────────────────────────────

let private modelTests =
    testList "fact browse model" [
        testCase "init asks for the table list and nothing else"
        <| fun _ ->
            let model, _ = initWith fakeApi ()
            Expect.equal model.Tables Loading "the table list loads first"
            Expect.equal model.Rows NotAsked "no row is asked for before a table is chosen"
            Expect.equal model.Runs NotAsked "no run history before a table is chosen"

        testCase "a drill-down request never exceeds the page ceiling"
        <| fun _ ->
            let request = {
                defaultRowQuery "t" "elasticity" with
                    PageSize = 100_000
                    Page = -3
            }

            let model = step (RowsRequested request) empty

            match model.RowQuery with
            | Some q ->
                Expect.equal q.PageSize FactBrowseApi.MaxPageSize "clamped to the contract's ceiling"
                Expect.equal q.Page 0 "a negative page reads as the first"
            | None -> failwith "the request must be recorded"

        testCase "a metric filter that matches one table opens it"
        <| fun _ ->
            let loaded = {
                empty with
                    Tables = Loaded [ listing "a" [ "elasticity" ]; listing "b" [ "margin" ] ]
            }

            let model, cmd = updateWith fakeApi (FilterByMetric "margin") loaded
            Expect.equal (visibleTables model |> List.map _.TableId) [ "b" ] "narrowed to the carrying table"
            Expect.equal (dispatched cmd) [ OpenTable "b" ] "the only match is opened"

        testCase "a metric filter that matches several tables lists them"
        <| fun _ ->
            let loaded = {
                empty with
                    Tables = Loaded [ listing "a" [ "elasticity" ]; listing "b" [ "elasticity"; "margin" ] ]
            }

            let model, cmd = updateWith fakeApi (FilterByMetric "elasticity") loaded
            Expect.equal (visibleTables model |> List.length) 2 "both carry it"
            Expect.equal (dispatched cmd) [] "nothing is opened on the user's behalf"

        testCase "opening a table resets the drill-down, so one page is held at a time"
        <| fun _ ->
            let before = {
                empty with
                    SelectedTable = Some "a"
                    RowQuery = Some(defaultRowQuery "a" "elasticity")
                    Rows = LoadFailed "stale"
            }

            let model = step (OpenTable "b") before
            Expect.equal model.SelectedTable (Some "b") "selected"
            Expect.equal model.Rows NotAsked "the previous table's page is dropped"
            Expect.isNone model.RowQuery "the previous query is dropped"

        testCase "the detail's arrival asks for the first page of the first metric"
        <| fun _ ->
            let model = step (DetailLoaded(Ok(detail "a"))) (step (OpenTable "a") empty)

            match model.RowQuery with
            | Some q ->
                Expect.equal q.Metric "elasticity" "the first declared column"
                Expect.equal q.Direction TopOfRanking "top of the ranking"
                Expect.equal q.Page 0 "first page"
            | None -> failwith "the population summary needs its first page"

        testCase "a subject prefix is typed as a path"
        <| fun _ ->
            Expect.equal (parseSubjectPrefix " region-1 > store-9 ") [ "region-1"; "store-9" ] "split and trimmed"
            Expect.equal (parseSubjectPrefix "") [] "blank is no prefix"
    ]

// ─── Pages ───────────────────────────────────────────────────────────

let private pageTests =
    testList "fact browse pages" [
        testCase "the tables page states that no table is declared"
        <| fun _ ->
            let markup =
                ViewMount.mount (FactBrowseView.tablesView { empty with Tables = Loaded [] } noop)

            Expect.isTrue (markup.Contains "No fact tables are declared") "a considered empty state"

        testCase "the tables page lists a table that has never run"
        <| fun _ ->
            let markup =
                ViewMount.mount (
                    FactBrowseView.tablesView
                        {
                            empty with
                                Tables = Loaded [ listing "prices" [ "elasticity" ] ]
                        }
                        noop
                )

            Expect.isTrue (markup.Contains "prices") "the table is listed"
            Expect.isTrue (markup.Contains "never refreshed") "its standing says so"

        testCase "the table page renders a table with no runs yet"
        <| fun _ ->
            let model = {
                empty with
                    SelectedTable = Some "prices"
                    Detail = Loaded(detail "prices")
                    Runs = Loaded(noRuns "prices")
                    Rows = NotAsked
            }

            let markup = ViewMount.mount (FactBrowseView.TablePage model noop)
            Expect.isTrue (markup.Contains "not been refreshed yet") "the run history's empty state"
            Expect.isTrue (markup.Contains "elasticity") "the declared columns render"

        testCase "the table page shows a declared description under the title, and each column's as its hint"
        <| fun _ ->
            let model = {
                empty with
                    SelectedTable = Some "prices"
                    Detail = Loaded(describedDetail "prices")
                    Runs = Loaded(noRuns "prices")
                    Rows = NotAsked
            }

            let markup = ViewMount.mount (FactBrowseView.TablePage model noop)

            Expect.isTrue
                (markup.Contains "Monthly price response by SKU, from the price-test extract.")
                "the table's description renders"

            Expect.isTrue (markup.Contains "Own-price elasticity of unit volume.") "the column's description renders"

            Expect.isTrue
                (markup.Contains "title=\"Own-price elasticity of unit volume.\"")
                "the column's description is its hint"

        testCase "an undescribed table renders no description and no hint"
        <| fun _ ->
            let model = {
                empty with
                    SelectedTable = Some "prices"
                    Detail = Loaded(detail "prices")
                    Runs = Loaded(noRuns "prices")
                    Rows = NotAsked
            }

            let markup = ViewMount.mount (FactBrowseView.TablePage model noop)
            Expect.isFalse (markup.Contains "Own-price elasticity") "no column hint"
            Expect.isFalse (markup.Contains "price-test extract") "no table description"

        testCase "the table page with nothing chosen points at the tables page"
        <| fun _ ->
            let markup = ViewMount.mount (FactBrowseView.TablePage empty noop)
            Expect.isTrue (markup.Contains "No table is chosen") "a considered empty state"

        testCase "an empty drill-down page says where bulk extraction lives"
        <| fun _ ->
            let page: FactRowPage = {
                TableId = "prices"
                Metric = "elasticity"
                Direction = TopOfRanking
                SubjectPrefix = []
                Page = 0
                PageSize = FactBrowseApi.DefaultPageSize
                MaxPageSize = FactBrowseApi.MaxPageSize
                RankCeiling = 1000
                Rows = []
                WithheldCount = 0
                Withheld = []
                HasMore = false
                ReachedRankCeiling = false
                Population = {
                    SubjectCount = 0
                    FactCount = 0
                    ComparableCount = 0
                    NonComparableCount = 0
                    PeriodFrom = None
                    PeriodTo = None
                    FreshCount = 0
                    StaleCount = 0
                    Methods = []
                    Minimum = None
                    Maximum = None
                    Mean = None
                    ValueStatisticsWithheld = false
                }
            }

            let model = {
                empty with
                    SelectedTable = Some "prices"
                    Detail = Loaded(detail "prices")
                    RowQuery = Some(defaultRowQuery "prices" "elasticity")
                    Rows = Loaded page
            }

            let markup = ViewMount.mount (FactBrowseView.RowsPage model noop)
            Expect.isTrue (markup.Contains "reporting path") "bulk extraction is an export, and the page says so"
    ]

// ─── The knowledge base badge ────────────────────────────────────────

let private coverageDoc: KnowledgeDocument = {
    Id = "coverage-1"
    FileName = "elasticity coverage"
    FileType = "narrative"
    UploadedAt = DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)
    UploadedBy = "_facts"
    Status = IngestionStatus.Complete 1
    SizeBytes = 400L
    ChunkCount = 1
    Source =
        KnowledgeSource.FromNarrative {
            ModuleId = FactBrowseLinks.CoverageNarrativeModuleId
            PageRoute = None
            SettingsKey = FactBrowseLinks.CoverageSettingsKeyPrefix + "elasticity"
            SettingsDisplay = []
            GeneratedAt = DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)
        }
    ContentHash = None
    Version = 1
    Tags = []
}

let private listConfig: KnowledgeListView.KnowledgeListConfig = {
    EmptyStateText = "nothing here"
    RowAction = None
    InstanceKey = "phase-895-tests"
}

/// The badge's distinguishing class.
[<Literal>]
let private BadgeClass = "bg-teal-50"

let private withModules (ids: string list) (body: unit -> unit) =
    let before = RegisteredModules.snapshot ()

    RegisteredModules.publish (
        ids
        |> List.map (fun id -> {
            RegisteredModules.ModuleEntry.ModuleId = id
            ModuleName = id
            PageRoutes = []
        })
    )

    try
        body ()
    finally
        RegisteredModules.publish before

let private badgeTests =
    testList "knowledge base fact-table badge" [
        testCase "a coverage narrative shows no badge while the browse module is absent"
        <| fun _ ->
            withModules [] (fun () ->
                let markup =
                    ViewMount.mount (KnowledgeListView.KnowledgeListView listConfig [ coverageDoc ])

                Expect.isFalse (markup.Contains BadgeClass) "no link to a page the deployment does not have")

        testCase "a coverage narrative is one item with a fact-table badge once the module is composed"
        <| fun _ ->
            withModules [ FactBrowseLinks.ModuleId ] (fun () ->
                let markup =
                    ViewMount.mount (KnowledgeListView.KnowledgeListView listConfig [ coverageDoc ])

                Expect.isTrue (markup.Contains BadgeClass) "the badge renders"
                Expect.isTrue (markup.Contains "elasticity") "it names the metric it links to")

        testCase "any other document is byte-identical whether or not the module is composed"
        <| fun _ ->
            let plain = {
                coverageDoc with
                    Source = KnowledgeSource.UploadedFile
            }

            let mutable absent = ""
            let mutable present = ""

            withModules [] (fun () ->
                absent <- ViewMount.mount (KnowledgeListView.KnowledgeListView listConfig [ plain ]))

            withModules [ FactBrowseLinks.ModuleId ] (fun () ->
                present <- ViewMount.mount (KnowledgeListView.KnowledgeListView listConfig [ plain ]))

            Expect.isTrue (absent.Length > 0) "the row actually rendered"
            Expect.equal present absent "the companion changes nothing about a document it does not describe"
    ]

// ─── Phase 1127 — the slice, refined locally under Fable ─────────────
//
// The .NET pack holds the slice's pipelines to the server reads over a real
// store. Here the same pipelines run under Fable over a slice built and
// encoded on this host, against answers worked out by hand: ranks keep a
// withheld head's position, the branch filter is a prefix of members, the
// series keeps a withheld point's place, and a page inside the slice costs
// no server read.

let private september = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
let private october = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)

let private sliceCell (brand: string) (sku: string) (value: decimal) (desc: int) (asc: int) (withheld: bool) =
    {
        Metric = "revenue"
        FactId = "f-" + sku
        Subject = sprintf "product/%s>%s" brand sku
        SubjectPath = [ brand; sku ]
        PeriodFrom = september
        PeriodTo = october
        PeriodLabel = Some "2026-09"
        Value = if withheld then None else Some value
        Rendering = if withheld then None else Some(string value)
        AsOf = september.AddDays 3.0
        Freshness = "fresh"
        WithheldPolicy = if withheld then Some "licence-x" else None
        RankDescending = Some desc
        RankAscending = Some asc
    }
    : FactSliceCell

/// Three heads: z1 = 50 (largest), a2 = 20 (withheld), a1 = 10 (smallest).
let private fixtureSlice: FactSlice =
    let encoded =
        FactBrowseSlice.build [ "revenue" ] [
            sliceCell "acme" "a1" 10m 3 1 false
            sliceCell "acme" "a2" 20m 2 2 true
            sliceCell "zeta" "z1" 50m 1 3 false
        ]
        |> Result.bind FactBrowseSlice.encode

    {
        TableId = "t"
        Hierarchy = "product"
        Level = "sku"
        Method = "computed:price-model:v1:t"
        PeriodFrom = FactBrowseApi.AllPeriodsFrom
        PeriodTo = FactBrowseApi.AllPeriodsTo
        SubjectPrefix = []
        Metrics = [
            {
                Metric = "revenue"
                DisplayFormat = ""
            }
        ]
        Table =
            match encoded with
            | Ok text -> text
            | Error e -> failwith e
        RunStamp = Some "t@1:abc"
        CellCount = 3
        MaxCells = FactBrowseApi.MaxSliceCells
        RankCeiling = 1000
        WithheldCount = 1
        Withheld = [ { PolicyRef = "licence-x"; Count = 1 } ]
    }

let private heldFixture () =
    match FactBrowseSlice.hold fixtureSlice with
    | Ok held -> held
    | Error e -> failwith e

let private page (direction: FactRowDirection) (prefix: string list) (index: int) (size: int) =
    let request = {
        defaultRowQuery "t" "revenue" with
            Direction = direction
            SubjectPrefix = prefix
            Page = index
            PageSize = size
    }

    match FactBrowseSlice.rowPage (heldFixture ()) request with
    | Ok p -> p
    | Error e -> failwith e

let private ranks (p: FactRowPage) =
    p.Rows |> List.map (fun r -> r.Rank, r.FactId)

let private sliceTests =
    testList "Phase 1127 — the view slice, refined locally" [
        testCase "a page keeps a withheld head's rank, so no row is promoted"
        <| fun _ ->
            let top = page TopOfRanking [] 0 2
            Expect.equal (ranks top) [ 1, "f-z1" ] "z1 is first; a2's rank 2 is withheld"
            Expect.equal top.WithheldCount 1 "one withheld head on the page"
            Expect.isTrue top.HasMore "a third ranked head lies past the page"
            Expect.equal (ranks (page TopOfRanking [] 1 2)) [ 3, "f-a1" ] "a1 keeps rank 3"
            Expect.equal (ranks (page BottomOfRanking [] 0 3)) [ 1, "f-a1"; 3, "f-z1" ] "the smallest first"

        testCase "the branch filter ranks within the branch"
        <| fun _ ->
            Expect.equal (ranks (page TopOfRanking [ "acme" ] 0 1)) [] "a2 heads the acme branch and is withheld"
            Expect.equal (ranks (page TopOfRanking [ "acme" ] 1 1)) [ 2, "f-a1" ] "a1 is second in its branch"
            Expect.equal (ranks (page TopOfRanking [ "acme"; "a1" ] 0 5)) [ 1, "f-a1" ] "a member path is a branch"
            Expect.equal (ranks (page TopOfRanking [ "acm" ] 0 5)) [] "a prefix is of members, not of text"

        testCase "the summary withholds magnitudes whenever a matched head is withheld"
        <| fun _ ->
            let all = (page TopOfRanking [] 0 5).Population
            Expect.isTrue all.ValueStatisticsWithheld "a2 is in the population"
            Expect.isNone all.Minimum "no minimum"
            Expect.equal (all.SubjectCount, all.FactCount, all.ComparableCount) (3, 3, 3) "counts always ride"
            let zeta = (page TopOfRanking [ "zeta" ] 0 5).Population
            Expect.isFalse zeta.ValueStatisticsWithheld "nothing withheld in zeta"

            Expect.equal
                (zeta.Minimum, zeta.Maximum, zeta.Mean)
                (Some "50", Some "50", Some "50")
                "the magnitudes over one head"

        testCase "the series keeps a withheld point's place, and its cap"
        <| fun _ ->
            let request: FactSeriesQuery = {
                TableId = "t"
                Metric = "revenue"
                SubjectPrefix = []
                SubjectLevel = None
                PeriodFrom = september
                PeriodTo = october
                MaxPoints = FactBrowseApi.MaxSeriesPoints
            }

            match FactBrowseSlice.series (heldFixture ()) request with
            | Error e -> failwith e
            | Ok s ->
                Expect.equal (s.Points |> List.map _.FactId) [ "f-a1"; "f-a2"; "f-z1" ] "subject then period order"
                Expect.equal (s.Points |> List.map _.Value) [ Some 10m; None; Some 50m ] "a gap where withheld"
                Expect.equal (s.Points |> List.map _.Withheld) [ false; true; false ] "marked withheld"
                Expect.equal s.Points.Head.PeriodFrom september "the instant round-trips"

            match FactBrowseSlice.series (heldFixture ()) { request with MaxPoints = 2 } with
            | Ok _ -> failwith "three points over a cap of two must be refused"
            | Error e -> Expect.isTrue (e.Contains "over the cap of 2") "the refusal names the cap"

        testCase "a derived figure renders as .NET renders it"
        <| fun _ ->
            Expect.equal (FactBrowseSlice.renderDerived "N2" 1234.5m) "1,234.50" "N2"
            Expect.equal (FactBrowseSlice.renderDerived "N0" -1234.5m) "-1,235" "N0, half away from zero"
            Expect.equal (FactBrowseSlice.renderDerived "F1" 2.25m) "2.3" "F1"
            Expect.equal (FactBrowseSlice.renderDerived "P1" 0.1234m) "12.3 %" "P1"
            Expect.equal (FactBrowseSlice.renderDerived "" 3.5m) "3.5" "verbatim"

        testCase "inside the slice a page, a metric or a prefix costs no server read"
        <| fun _ ->
            let query = defaultRowQuery "t" "revenue"

            let waiting = {
                empty with
                    SelectedTable = Some "t"
                    RowQuery = Some query
                    Rows = Loading
                    Slice = Loading
            }

            let held, cmd = updateWith fakeApi (SliceLoaded("t", Ok fixtureSlice)) waiting
            Expect.isTrue (List.isEmpty cmd) "the waiting page is answered from the slice"

            match held.Rows with
            | Loaded p -> Expect.equal (ranks p) [ 1, "f-z1"; 3, "f-a1" ] "the first page, a2's rank 2 withheld"
            | other -> failwithf "expected a page, got %A" other

            let paged, cmd =
                updateWith
                    fakeApi
                    (RowsRequested {
                        query with
                            SubjectPrefix = [ "acme" ]
                            Page = 1
                            PageSize = 1
                    })
                    held

            Expect.isTrue (List.isEmpty cmd) "a branch page is local"

            match paged.Rows with
            | Loaded p -> Expect.equal (ranks p) [ 2, "f-a1" ] "ranked within the branch"
            | other -> failwithf "expected a page, got %A" other

        testCase "a moved run stamp, or the run notification, drops the slice"
        <| fun _ ->
            let held, _ =
                updateWith fakeApi (SliceLoaded("t", Ok fixtureSlice)) {
                    empty with
                        SelectedTable = Some "t"
                        RowQuery = Some(defaultRowQuery "t" "revenue")
                        Slice = Loading
                }

            let unchanged, _ =
                updateWith
                    fakeApi
                    (DetailLoaded(
                        Ok {
                            detail "t" with
                                Listing = {
                                    listing "t" [ "revenue" ] with
                                        RunStamp = Some "t@1:abc"
                                }
                        }
                    ))
                    held

            match unchanged.Slice with
            | Loaded _ -> ()
            | other -> failwithf "the same stamp keeps the slice, got %A" other

            let moved, cmd =
                updateWith
                    fakeApi
                    (DetailLoaded(
                        Ok {
                            detail "t" with
                                Listing = {
                                    listing "t" [ "revenue" ] with
                                        RunStamp = Some "t@2:def"
                                }
                        }
                    ))
                    held

            Expect.equal moved.Slice Loading "a new stamp reads the slice again"
            Expect.isFalse (List.isEmpty cmd) "and asks for it"

            let notified, _ =
                updateWith
                    fakeApi
                    (RunCommitted {
                        TableId = "t"
                        RunId = "r2"
                        RowCount = 3
                        New = 0
                        Changed = 1
                        Unchanged = 2
                        Removed = 0
                    })
                    held

            Expect.equal notified.Slice Loading "the run's notification drops the slice too"

        testCase "a slice at the cap decodes, prepares and pages in the browser's runtime"
        <| fun _ ->
            let n = FactBrowseApi.MaxSliceCells

            let encoded =
                FactBrowseSlice.build [ "revenue" ] [
                    for i in 0 .. n - 1 do
                        sliceCell (sprintf "b%02d" (i % 20)) (sprintf "s%05d" i) (decimal i) (n - i) (i + 1) false
                ]
                |> Result.bind FactBrowseSlice.encode

            let text =
                match encoded with
                | Ok t -> t
                | Error e -> failwith e

            let started = DateTime.UtcNow

            let held =
                match
                    FactBrowseSlice.hold {
                        fixtureSlice with
                            Table = text
                            CellCount = n
                    }
                with
                | Ok h -> h
                | Error e -> failwith e

            let prepared = DateTime.UtcNow
            let prepareMs = (prepared - started).TotalMilliseconds

            let first =
                match FactBrowseSlice.rowPage held (defaultRowQuery "t" "revenue") with
                | Ok p -> p
                | Error e -> failwith e

            let pageMs = (DateTime.UtcNow - prepared).TotalMilliseconds

            printfn
                "Phase 1127 slice at the cap under Fable (%d cells, %d encoded chars): decode+prepare %.0f ms, one local page %.0f ms"
                n
                text.Length
                prepareMs
                pageMs

            Expect.equal (List.length first.Rows) FactBrowseApi.DefaultPageSize "a full first page"
            Expect.equal first.Rows.Head.FactId (sprintf "f-s%05d" (n - 1)) "the largest first"

        testCase "a refused slice sends the drill-down to the server's pages"
        <| fun _ ->
            let model, cmd =
                updateWith fakeApi (SliceLoaded("t", Error "The slice would hold 60000 cells, over the cap")) {
                    empty with
                        SelectedTable = Some "t"
                        RowQuery = Some(defaultRowQuery "t" "revenue")
                        Slice = Loading
                }

            Expect.equal model.Rows Loading "the page is read from the server"
            Expect.isFalse (List.isEmpty cmd) "one QueryRows read"

            match model.Slice with
            | LoadFailed _ -> ()
            | other -> failwithf "the refusal is held, got %A" other
    ]

/// Every Phase 895 client case.
let tests =
    testList "Phase 895 — fact browse client" [ modelTests; pageTests; badgeTests; sliceTests ]