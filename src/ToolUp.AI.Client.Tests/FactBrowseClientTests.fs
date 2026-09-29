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
}

let private detail (tableId: string) : FactTableDetail = {
    Listing = listing tableId [ "elasticity" ]
    Columns = [
        {
            Metric = "elasticity"
            Shape = "Scalar"
            Disclosure = "Surfaceable"
        }
    ]
    ProducingOperation = "price-model"
    SchemaVersion = 1
    RefreshCadenceSeconds = 86_400L
    Requirement = "Optional"
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

/// Every Phase 895 client case.
let tests =
    testList "Phase 895 — fact browse client" [ modelTests; pageTests; badgeTests ]