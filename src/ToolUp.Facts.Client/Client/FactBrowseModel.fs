// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Facts.Client.FactBrowseModel

// ─── Fact browse — state and transitions (Phase 895) ─────────────────
//
// The Elmish half of the fact browse companion. Three pages share this
// model — Tables, Table detail, Drill-down — and it holds what the server
// handed it: the table list (tens of rows), one page of one table's runs,
// one run's movers at a time, one opened fact, and the open table's VIEW
// SLICE.
//
// **The drill-down reads once and refines locally (Phase 1127, revising
// Phase 895's "every change is a server read").** Opening a table's
// drill-down reads its slice — every head of every metric over every
// period, gated, as one columnar table — once, and holds it prepared.
// Every change of page, metric, direction or subject prefix inside it is
// a `Transform` pipeline over the held slice (`FactBrowseSlice.rowPage`),
// not a server read. The slice is read again only when the view leaves it
// (another table) or its run stamp moves: the table's listing carries the
// current stamp, so a refreshed detail or table list that names a
// different one drops the slice, and so does the per-run notification
// for the table. The bound is the contract's: a table whose slice would
// exceed `FactBrowseApi.MaxSliceCells` is refused before the gate, and the
// drill-down then reads page by page from the server exactly as before —
// so a table too large to hold is never held. `GetFact` (one fact's
// provenance chain) and `GetRunMovers` (one run's movers and the facts
// they replaced) stay server reads: neither answer is in a slice of
// current heads.
//
// `updateWith` takes the API record as a parameter so the transitions
// are testable against a fake; `update` binds the real proxy.

open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Facts

/// One server read's state.
type Load<'T> =
    /// Not requested yet.
    | NotAsked
    /// In flight.
    | Loading
    /// Answered.
    | Loaded of 'T
    /// Refused or failed — the server's own sentence.
    | LoadFailed of string

/// The browse state.
type Model = {
    /// The declared tables.
    Tables: Load<FactTableListing list>
    /// A metric the table list is narrowed to (opened from a coverage
    /// narrative's badge). `None` lists every table.
    MetricFilter: string option
    /// The table the detail and drill-down pages read.
    SelectedTable: string option
    /// The selected table's declaration and standing.
    Detail: Load<FactTableDetail>
    /// The page of the selected table's run history.
    Runs: Load<FactRunPage>
    /// The run whose movers are open, and their state.
    Movers: (string * Load<FactRunMovers>) option
    /// The last drill-down request.
    RowQuery: FactRowQuery option
    /// The drill-down page.
    Rows: Load<FactRowPage>
    /// The opened fact.
    Fact: Load<FactDetailView>
    /// Phase 1127 — the selected table's view slice, prepared. `LoadFailed`
    /// (a slice over the cap, say) sends the drill-down to the server's
    /// page reads until the table is opened again.
    Slice: Load<FactBrowseSlice.Held>
}

/// Browse messages.
type Msg =
    /// Reload the table list (and the selected table's standing).
    | RefreshTables
    /// The table list arrived.
    | TablesLoaded of Result<FactTableListing list, string>
    /// Narrow the table list to the tables carrying a metric; when exactly
    /// one does, it is also selected.
    | FilterByMetric of metric: string
    /// Drop the metric filter.
    | ClearMetricFilter
    /// Select a table: load its detail, its first page of runs, and the
    /// first drill-down page of its first metric.
    | OpenTable of tableId: string
    /// The selected table's detail arrived.
    | DetailLoaded of Result<FactTableDetail, string>
    /// Ask for a page of the selected table's runs.
    | RunsPageRequested of page: int
    /// A page of runs arrived.
    | RunsLoaded of Result<FactRunPage, string>
    /// Open (or close, when already open) one run's movers.
    | ToggleMovers of runId: string
    /// One run's movers arrived.
    | MoversLoaded of runId: string * Result<FactRunMovers, string>
    /// Ask for a drill-down page.
    | RowsRequested of FactRowQuery
    /// A drill-down page arrived.
    | RowsLoaded of Result<FactRowPage, string>
    /// Open one fact and its provenance.
    | OpenFact of factId: string
    /// The opened fact arrived.
    | FactLoaded of Result<FactDetailView, string>
    /// Close the opened fact.
    | CloseFact
    /// A run committed in this scope — the per-run notification.
    | RunCommitted of FactTableRunNotice
    /// A table's view slice arrived (Phase 1127).
    | SliceLoaded of tableId: string * Result<FactSlice, string>

/// The empty state: nothing loaded yet.
let empty: Model = {
    Tables = NotAsked
    MetricFilter = None
    SelectedTable = None
    Detail = NotAsked
    Runs = NotAsked
    Movers = None
    RowQuery = None
    Rows = NotAsked
    Fact = NotAsked
    Slice = NotAsked
}

/// The drill-down request a freshly selected table opens with: its first
/// metric, top of the ranking, no prefix, first page.
let defaultRowQuery (tableId: string) (metric: string) : FactRowQuery = {
    TableId = tableId
    Metric = metric
    Direction = TopOfRanking
    SubjectPrefix = []
    Page = 0
    PageSize = FactBrowseApi.DefaultPageSize
}

/// The tables the list page shows under the model's metric filter.
let visibleTables (model: Model) : FactTableListing list =
    match model.Tables with
    | Loaded tables ->
        match model.MetricFilter with
        | None -> tables
        | Some metric -> tables |> List.filter (fun t -> List.contains metric t.Metrics)
    | _ -> []

/// A subject prefix as a user types it (`member>member`), split into
/// member ids. Blank segments are dropped.
let parseSubjectPrefix (text: string) : string list =
    if isNull text then
        []
    else
        text.Split '>'
        |> Array.map _.Trim()
        |> Array.filter (fun s -> s <> "")
        |> List.ofArray

let private settle (result: Result<'T, string>) : Load<'T> =
    match result with
    | Ok value -> Loaded value
    | Error message -> LoadFailed message

let private failure (ofResult: Result<'T, string> -> Msg) : exn -> Msg = fun ex -> ofResult (Error ex.Message)

let private loadTables (api: IFactBrowseApi) : Cmd<Msg> =
    Cmd.OfRemoting.call api.ListTables () TablesLoaded (failure TablesLoaded)

let private loadRuns (api: IFactBrowseApi) (tableId: string) (page: int) : Cmd<Msg> =
    Cmd.OfRemoting.call api.ListRuns (tableId, page, FactBrowseApi.DefaultPageSize) RunsLoaded (failure RunsLoaded)

let private loadRows (api: IFactBrowseApi) (query: FactRowQuery) : Cmd<Msg> =
    Cmd.OfRemoting.call api.QueryRows query RowsLoaded (failure RowsLoaded)

let private loadDetail (api: IFactBrowseApi) (tableId: string) : Cmd<Msg> =
    Cmd.OfRemoting.call api.GetTable tableId DetailLoaded (failure DetailLoaded)

/// The slice a table's drill-down reads (Phase 1127): every metric over
/// every period across the whole table, so every drill-down request on the
/// table is answered inside it.
let sliceQueryFor (tableId: string) : FactSliceQuery = {
    TableId = tableId
    PeriodFrom = FactBrowseApi.AllPeriodsFrom
    PeriodTo = FactBrowseApi.AllPeriodsTo
    SubjectPrefix = []
    Metrics = []
}

let private loadSlice (api: IFactBrowseApi) (tableId: string) : Cmd<Msg> =
    Cmd.OfRemoting.call api.QuerySlice (sliceQueryFor tableId) (fun r -> SliceLoaded(tableId, r)) (fun ex ->
        SliceLoaded(tableId, Error ex.Message))

/// Answer a drill-down request: locally when the held slice covers it;
/// after the slice when one is in flight; by reading the slice first when
/// none is held; and from the server page by page when the table's slice
/// was refused (over the cap, say).
let private requestRows (api: IFactBrowseApi) (model: Model) (query: FactRowQuery) : Model * Cmd<Msg> =
    let model = { model with RowQuery = Some query }

    match model.Slice with
    | Loaded held when FactBrowseSlice.coversRows held.Slice query ->
        {
            model with
                Rows = settle (FactBrowseSlice.rowPage held query)
        },
        Cmd.none
    | Loading -> { model with Rows = Loading }, Cmd.none
    | LoadFailed _ -> { model with Rows = Loading }, loadRows api query
    | NotAsked
    | Loaded _ ->
        {
            model with
                Rows = Loading
                Slice = Loading
        },
        loadSlice api query.TableId

/// Drop the held slice when the table's listing names a run stamp other
/// than the slice's — a run landed since the slice was read — and read the
/// open drill-down again through a fresh slice.
let private dropStaleSlice (api: IFactBrowseApi) (listing: FactTableListing) (model: Model) : Model * Cmd<Msg> =
    match model.Slice with
    | Loaded held when held.Slice.TableId = listing.TableId && held.Slice.RunStamp <> listing.RunStamp ->
        let model = { model with Slice = NotAsked }

        match model.RowQuery with
        | Some query -> requestRows api model query
        | None -> model, Cmd.none
    | _ -> model, Cmd.none

/// Initial state: load the table list.
let initWith (api: IFactBrowseApi) () : Model * Cmd<Msg> =
    { empty with Tables = Loading }, loadTables api

/// The transitions, over an explicit API.
let updateWith (api: IFactBrowseApi) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | RefreshTables ->
        let reloadSelected =
            match model.SelectedTable with
            | Some tableId -> [ loadDetail api tableId; loadRuns api tableId 0 ]
            | None -> []

        { model with Tables = Loading }, Cmd.batch (loadTables api :: reloadSelected)

    | TablesLoaded result ->
        let model = { model with Tables = settle result }

        // The selected table's listing carries its current run stamp.
        let model, refresh =
            match result, model.SelectedTable with
            | Ok tables, Some tableId ->
                match tables |> List.tryFind (fun t -> t.TableId = tableId) with
                | Some row -> dropStaleSlice api row model
                | None -> model, Cmd.none
            | _ -> model, Cmd.none

        // A metric filter that narrows to exactly one table selects it.
        match model.MetricFilter, visibleTables model with
        | Some _, [ only ] when model.SelectedTable <> Some only.TableId ->
            model, Cmd.batch [ refresh; Cmd.ofMsg (OpenTable only.TableId) ]
        | _ -> model, refresh

    | FilterByMetric metric ->
        let model = {
            model with
                MetricFilter = Some metric
        }

        match model.Tables with
        | Loaded _ ->
            match visibleTables model with
            | [ only ] -> model, Cmd.ofMsg (OpenTable only.TableId)
            | _ -> model, Cmd.none
        | Loading -> model, Cmd.none
        | NotAsked
        | LoadFailed _ -> { model with Tables = Loading }, loadTables api

    | ClearMetricFilter -> { model with MetricFilter = None }, Cmd.none

    | OpenTable tableId ->
        {
            model with
                SelectedTable = Some tableId
                Detail = Loading
                Runs = Loading
                Movers = None
                RowQuery = None
                Rows = NotAsked
                // A slice is the selected table's: another table's is
                // dropped, and a refused one is asked for again.
                Slice =
                    match model.Slice with
                    | Loaded held when held.Slice.TableId = tableId -> model.Slice
                    | _ -> NotAsked
        },
        Cmd.batch [ loadDetail api tableId; loadRuns api tableId 0 ]

    | DetailLoaded result ->
        let model = { model with Detail = settle result }

        let model, refresh =
            match result with
            | Ok detail -> dropStaleSlice api detail.Listing model
            | Error _ -> model, Cmd.none

        match result, model.RowQuery with
        // The detail names the table's metrics, so the drill-down's first
        // page (and with it the population summary) can be asked for now —
        // unless a fact opened the table and already asked for its row.
        | Ok detail, None when model.SelectedTable = Some detail.Listing.TableId ->
            match detail.Listing.Metrics with
            | metric :: _ ->
                let model, cmd =
                    requestRows api model (defaultRowQuery detail.Listing.TableId metric)

                model, Cmd.batch [ refresh; cmd ]
            | [] -> model, refresh
        | _ -> model, refresh

    | RunsPageRequested page ->
        match model.SelectedTable with
        | Some tableId ->
            {
                model with
                    Runs = Loading
                    Movers = None
            },
            loadRuns api tableId page
        | None -> model, Cmd.none

    | RunsLoaded result -> { model with Runs = settle result }, Cmd.none

    | ToggleMovers runId ->
        match model.Movers, model.SelectedTable with
        | Some(openRun, _), _ when openRun = runId -> { model with Movers = None }, Cmd.none
        | _, Some tableId ->
            {
                model with
                    Movers = Some(runId, Loading)
            },
            Cmd.OfRemoting.call api.GetRunMovers (tableId, runId) (fun r -> MoversLoaded(runId, r)) (fun ex ->
                MoversLoaded(runId, Error ex.Message))
        | _, None -> model, Cmd.none

    | MoversLoaded(runId, result) ->
        match model.Movers with
        // A late answer for a run the user has since closed is dropped.
        | Some(openRun, _) when openRun = runId ->
            {
                model with
                    Movers = Some(runId, settle result)
            },
            Cmd.none
        | _ -> model, Cmd.none

    | RowsRequested query ->
        let query = {
            query with
                Page = FactBrowseApi.clampPage query.Page
                PageSize = FactBrowseApi.clampPageSize query.PageSize
        }

        requestRows api model query

    | RowsLoaded result -> { model with Rows = settle result }, Cmd.none

    | SliceLoaded(tableId, result) ->
        // A late answer for a table the user has since left is dropped.
        if model.SelectedTable <> Some tableId then
            model, Cmd.none
        else
            let held = result |> Result.bind FactBrowseSlice.hold
            let model = { model with Slice = settle held }

            // The waiting request is answered locally when the slice covers
            // it, and by the server otherwise — never by asking again.
            match model.RowQuery, model.Slice with
            | Some query, Loaded slice when FactBrowseSlice.coversRows slice.Slice query -> requestRows api model query
            | Some query, _ -> { model with Rows = Loading }, loadRows api query
            | None, _ -> model, Cmd.none

    | OpenFact factId ->
        { model with Fact = Loading }, Cmd.OfRemoting.call api.GetFact factId FactLoaded (failure FactLoaded)

    | FactLoaded result ->
        let model = { model with Fact = settle result }

        match result with
        // A fact a declared table wrote opens its table, and the
        // drill-down narrows to its subject so its row is on the page.
        | Ok fact ->
            match fact.TableId with
            | Some tableId ->
                let query = {
                    defaultRowQuery tableId fact.Metric with
                        SubjectPrefix = fact.Row.SubjectPath
                }

                let reloadTable =
                    if model.SelectedTable = Some tableId then
                        []
                    else
                        [ loadDetail api tableId; loadRuns api tableId 0 ]

                let model, rows =
                    requestRows
                        api
                        {
                            model with
                                SelectedTable = Some tableId
                                Detail = (if reloadTable.IsEmpty then model.Detail else Loading)
                                Runs = (if reloadTable.IsEmpty then model.Runs else Loading)
                                Movers = (if reloadTable.IsEmpty then model.Movers else None)
                                Slice = (if reloadTable.IsEmpty then model.Slice else NotAsked)
                        }
                        query

                model, Cmd.batch (rows :: reloadTable)
            | None -> model, Cmd.none
        | Error _ -> model, Cmd.none

    | CloseFact -> { model with Fact = NotAsked }, Cmd.none

    | RunCommitted notice ->
        // One notification per run: refresh the list's standing, and the
        // open table's runs when the run was its own. The run moved the
        // table's stamp, so its slice is dropped and the open drill-down is
        // read again through a fresh one.
        if model.SelectedTable = Some notice.TableId then
            let model, rows =
                match model.RowQuery with
                | Some query -> requestRows api { model with Slice = NotAsked } query
                | None -> { model with Slice = NotAsked }, Cmd.none

            model,
            Cmd.batch [
                loadTables api
                loadDetail api notice.TableId
                loadRuns api notice.TableId 0
                rows
            ]
        else
            model, loadTables api

// ─── The proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// `UserSession.withRequestHeaders`.
let private factBrowseApi: IFactBrowseApi =
    Api.makeProxy<IFactBrowseApi> (
        routeBuilder = FactBrowseApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

/// Initial state over the real proxy.
let init () : Model * Cmd<Msg> = initWith factBrowseApi ()

/// The transitions over the real proxy.
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> = updateWith factBrowseApi msg model