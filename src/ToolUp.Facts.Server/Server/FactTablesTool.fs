// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactTablesTool ──────────────────────────────────────────────────
//
// The discovery surface over the declared fact TABLES: which metrics sit
// together, in which hierarchy, at which level and grain, refreshed how
// often and when last. `list_metric_coverage` answers the same question by
// metric; this answers it by table, so a model asked for a metric at a level
// learns before it plans whether any table holds it there.
//
// **One projection, over the browse reads.** Every table is read through the
// fact browse's `listTables` / `getTable` (and `detailOf` beside the listing
// row), under the request's scope: the declaration, its description, and the
// table's standing — row count, last committed run, freshness. The tool adds
// the metric registry's declarations (a column's name, unit, display format
// and `Context`) and the hierarchy's path from its root to the table's level
// with each level's description. It reads the store for nothing else, and
// the catalogue fragment renders the same `FactTableView` list.
//
// **Declarations and counts, never a value.** The same bound
// `list_metric_coverage` keeps: a row count, a run stamp and a freshness are
// existence-level; a value is not, and nothing here carries one.
//
// **Disclosure.** Whether the caller may be told about a table is decided by
// the disclosure gate at the `FactToolResult` surface, over a sample of the
// table's own facts: the first page of each metric column's drill-down
// (`FactBrowseHandler.queryRows`, the table's own method lineage), asked of a
// gate fixed at that surface (`FactBrowseHandler.atSurface`). The rows that
// read returns are values, and are dropped where they are read; only whether
// any was admitted, and the policies the rest were withheld under, survive.
//
//   - some sampled fact is disclosable ⇒ the table is described, naming
//     the policies anything sampled was withheld under;
//   - every sampled fact is withheld ⇒ the table is reported as existing,
//     with the restricting policies, and NOTHING more — no description, no
//     column, no level, no standing;
//   - nothing could be sampled while the table reports committed rows (no
//     row carries a rankable value) ⇒ withheld, saying so; a table with no
//     committed row has nothing to withhold and is described.
//
// The sample is a floor, not a proof, for the reason coverage's probe is:
// a table whose sampled heads are disclosable may hold restricted rows below
// them. What it gates is a declaration and a count, never a figure.

/// One level on the path from a hierarchy's root to a table's level.
type FactTableLevelView = {
    /// The level, as the hierarchy declares it.
    Level: string
    /// What the level holds, where the declaring module described it.
    Description: string option
}

/// One metric column of a table, as the assistant is told about it.
type FactTableMetricView = {
    /// The column's metric id — the token the fact tools take.
    Metric: string
    /// The metric's display name; `None` when the metric is not registered.
    Name: string option
    /// The metric's unit; `None` when the metric is not registered.
    Unit: string option
    /// The metric's canonical display format; `None` when the metric is not
    /// registered or declares none.
    DisplayFormat: string option
    /// The analyst's context for the metric family, where declared.
    Context: string option
    /// What the column holds, where the table's description declares it.
    Description: string option
    /// The declared value shape (`Scalar`, `Interval`, …).
    Shape: string
    /// The policies a sampled fact of this column was withheld under.
    RestrictedUnderPolicies: string list
}

/// A table the caller may be told about: its declaration, description and
/// standing, and never a value.
type FactTableShown = {
    /// The table's declared, stable id.
    TableId: string
    /// What the table is for, where the declaring module described it.
    Description: string option
    /// The subject hierarchy the table's rows sit in.
    Hierarchy: string
    /// The hierarchy's display name; `None` when it is not registered.
    HierarchyName: string option
    /// What the hierarchy is, where described.
    HierarchyDescription: string option
    /// The level every row sits at.
    Level: string
    /// The levels from the hierarchy's root down to `Level`, each with its
    /// description. Just `Level` when the hierarchy is not registered.
    Path: FactTableLevelView list
    /// The declared metric columns, in column order.
    Columns: FactTableMetricView list
    /// The declared period grain (`Day` … `Year`, `Snapshot`).
    PeriodGrain: string
    /// The declared refresh cadence, in whole seconds.
    RefreshCadenceSeconds: int64
    /// The declared history mode (`AppendByRun` / `Replace`).
    HistoryMode: string
    /// Rows the current run committed; `None` until a run has committed.
    RowCount: int option
    /// When the current run committed; `None` until a run has committed.
    LastRefreshedAt: DateTime option
    /// The current run's stamp; `None` until a run has committed.
    LastRun: string option
    /// `never-refreshed`, `fresh`, `stale` or `unavailable`.
    Freshness: string
    /// How far past its cadence a stale table is, in whole seconds.
    OverdueBySeconds: int64 option
    /// The latest run's outcome (`in-progress`, `succeeded`, `failed`,
    /// `discarded`); `None` when the table has never run.
    LatestRunOutcome: string option
    /// The policies a sampled fact of the table was withheld under.
    RestrictedUnderPolicies: string list
}

/// A table the caller may not be told about: that it exists, and why not.
type FactTableWithheld = {
    /// The table's declared, stable id.
    TableId: string
    /// The policies its sampled facts were withheld under. Empty when
    /// nothing could be sampled.
    RestrictedUnderPolicies: string list
    /// Why the table is not described.
    Reason: string
}

/// One declared table as the caller may be told about it.
[<RequireQualifiedAccess>]
type FactTableView =
    /// Described: declaration, description and standing.
    | Shown of FactTableShown
    /// Existence and policy only.
    | Withheld of FactTableWithheld

/// The `list_fact_tables` AI tool: the projection of the declared fact tables
/// over the browse reads, its definition and its executor. Declared by
/// `FactsCompose.withFactStore` beside the other fact tools.
[<RequireQualifiedAccess>]
module FactTablesTool =

    /// The tool's name.
    [<Literal>]
    let Name = "list_fact_tables"

    // ── The disclosure sample ─────────────────────────────────────

    /// What the sample of one column established.
    type private ColumnSample =
        /// Some sampled fact is disclosable; the rest were withheld under
        /// these policies.
        | Disclosed of withheldUnder: string list
        /// Every sampled fact was withheld, under these policies.
        | Restricted of policies: string list
        /// Nothing could be sampled.
        | Unsampled

    let private sorted (policies: string list) =
        policies
        |> List.distinct
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    let private sampleColumn
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (tableId: string)
        (metric: string)
        : Async<ColumnSample> =
        async {
            let! page =
                FactBrowseHandler.queryRows deps scope principal {
                    TableId = tableId
                    Metric = metric
                    Direction = TopOfRanking
                    SubjectPrefix = []
                    Page = 0
                    PageSize = CoverageTool.DisclosureProbeK
                }

            match page with
            // A refused read is a store fault, not an answer about the
            // caller: nothing was sampled.
            | Error _ -> return Unsampled
            | Ok page ->
                let withheld = page.Withheld |> List.map _.PolicyRef |> sorted

                match page.Rows, withheld with
                | [], [] -> return Unsampled
                | [], policies -> return Restricted policies
                | _, policies -> return Disclosed policies
        }

    // ── The projection ────────────────────────────────────────────

    let private someText (text: string) =
        if String.IsNullOrWhiteSpace text then None else Some text

    let private column
        (registry: IMetricRegistry option)
        (sample: ColumnSample)
        (view: FactTableColumnView)
        : FactTableMetricView =
        let definition = registry |> Option.bind (fun r -> r.TryGetMetric view.Metric)

        {
            Metric = view.Metric
            Name = definition |> Option.map _.Name
            Unit = definition |> Option.map _.Unit
            DisplayFormat = definition |> Option.bind (fun d -> someText d.DisplayFormat)
            Context = definition |> Option.bind _.Context
            Description = view.Description
            Shape = view.Shape
            RestrictedUnderPolicies =
                match sample with
                | Disclosed policies
                | Restricted policies -> policies
                | Unsampled -> []
        }

    let private shown
        (registry: IMetricRegistry option)
        (detail: FactTableDetail)
        (samples: ColumnSample list)
        : FactTableShown =
        let listing = detail.Listing
        let subject = registry |> Option.bind (fun r -> r.TryGetSubject listing.Hierarchy)

        let described =
            registry
            |> Option.bind (fun r -> MetricRegistry.trySubjectDescription r listing.Hierarchy)

        let levelDescription (level: string) =
            described
            |> Option.bind (fun d -> d.Levels |> List.tryFind (fun (l, _) -> l = level) |> Option.map snd)

        let path =
            match
                subject
                |> Option.bind (fun s ->
                    FactTableDefinition.levelDepth s listing.Level
                    |> Option.map (fun depth -> List.truncate depth s.Levels))
            with
            | Some levels -> levels
            | None -> [ listing.Level ]

        {
            TableId = listing.TableId
            Description = detail.Description
            Hierarchy = listing.Hierarchy
            HierarchyName = subject |> Option.map _.Name
            HierarchyDescription = described |> Option.map _.Description
            Level = listing.Level
            Path =
                path
                |> List.map (fun level -> {
                    Level = level
                    Description = levelDescription level
                })
            Columns = List.map2 (column registry) samples detail.Columns
            PeriodGrain = listing.PeriodGrain
            RefreshCadenceSeconds = detail.RefreshCadenceSeconds
            HistoryMode = listing.HistoryMode
            RowCount = listing.RowCount
            LastRefreshedAt = listing.LastRefreshedAt
            LastRun = listing.RunStamp
            Freshness = listing.Freshness
            OverdueBySeconds = listing.OverdueBySeconds
            LatestRunOutcome = listing.LatestRunOutcome
            RestrictedUnderPolicies =
                samples
                |> List.collect (function
                    | Disclosed policies
                    | Restricted policies -> policies
                    | Unsampled -> [])
                |> sorted
        }

    /// The reason a wholly withheld table gives.
    [<Literal>]
    let RestrictedReason =
        "Every fact sampled from this table is restricted to you, so it is not described. The table exists, and the policies named here are why it is withheld."

    /// The reason a table whose committed rows could not be sampled gives.
    [<Literal>]
    let UnsampledReason =
        "The table holds committed rows, but none carries a value that could be checked against the disclosure policy, so it is withheld rather than assumed disclosable."

    let private view
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (detail: FactTableDetail)
        : Async<FactTableView> =
        async {
            let! samples =
                detail.Columns
                |> List.map (fun c -> sampleColumn deps scope principal detail.Listing.TableId c.Metric)
                |> Async.Sequential

            let samples = List.ofArray samples

            let disclosed =
                samples
                |> List.exists (function
                    | Disclosed _ -> true
                    | _ -> false)

            let restricted =
                samples
                |> List.collect (function
                    | Restricted policies -> policies
                    | _ -> [])
                |> sorted

            let committedRows = detail.Listing.RowCount |> Option.exists (fun n -> n > 0)

            if disclosed then
                return FactTableView.Shown(shown deps.Registry detail samples)
            elif not (List.isEmpty restricted) then
                return
                    FactTableView.Withheld {
                        TableId = detail.Listing.TableId
                        RestrictedUnderPolicies = restricted
                        Reason = RestrictedReason
                    }
            elif committedRows then
                return
                    FactTableView.Withheld {
                        TableId = detail.Listing.TableId
                        RestrictedUnderPolicies = []
                        Reason = UnsampledReason
                    }
            else
                return FactTableView.Shown(shown deps.Registry detail samples)
        }

    /// Every declared table, ordered by id, or the one named by `table`, as
    /// `principal` may be told about it in `scope`. The disclosure sample is
    /// asked at `FactToolResult` whichever gate `deps` carries. An unknown
    /// table id is refused by name.
    let project
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (table: string option)
        : Async<Result<FactTableView list, string>> =
        async {
            let atToolResult = {
                deps with
                    Gate = FactBrowseHandler.atSurface FactToolResult deps.Gate
            }

            let! details =
                match table with
                | Some tableId -> async {
                    match! FactBrowseHandler.getTable deps scope tableId with
                    | Ok detail -> return Ok [ detail ]
                    | Error e -> return Error e
                  }
                | None -> async {
                    match! FactBrowseHandler.listTables deps scope with
                    | Error e -> return Error e
                    | Ok rows ->
                        return
                            Ok(
                                rows
                                |> List.choose (fun row ->
                                    deps.Tables.TryGetTable row.TableId
                                    |> Option.map (fun t -> FactBrowseHandler.detailOf deps t row))
                            )
                  }

            match details with
            | Error e -> return Error e
            | Ok details ->
                let! views = details |> List.map (view atToolResult scope principal) |> Async.Sequential

                return Ok(List.ofArray views)
        }

    // ── The tool ──────────────────────────────────────────────────

    /// The `list_fact_tables` tool declaration.
    let definition: AIToolDefinition = {
        Name = Name
        Description =
            "List THE FACT TABLES THIS DEPLOYMENT HOLDS — call this before you plan a fact question that names a level, a grain or a set of metrics together (\"top four countries for orders\", \"weekly revenue by site\"), so you know whether any table holds that metric at that level before you query. For every declared table it returns: its id (the token the render and pipeline tools take) and what it is for; the subject hierarchy it sits in, the LEVEL its rows sit at, and the path of levels from the hierarchy's root to that level with what each level holds; each metric column with its id, name, unit, display format, the analyst's context and what the column holds; and the table's period grain, refresh cadence, history mode, row count, last committed run and freshness. A metric at a level no table lists is NOT HELD at that level: say so rather than querying. It reports DECLARATIONS AND COUNTS ONLY and never a value — ask the query tools for values. Where you are not permitted to see a table, it is listed as existing with the restricting policy and nothing more. Pass `table` to describe one table by id. Results are scoped to the current user/team."
        Parameters = [
            {
                Name = "table"
                Type = "string"
                Description =
                    "Optional — describe ONE declared table by id. Omit it to list every table, which is the ordinary use."
                Required = false
                Default = None
            }
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    let private parseTable (argsJson: string) : Result<string option, string> =
        if String.IsNullOrWhiteSpace argsJson || argsJson.Trim() = "null" then
            Ok None
        else
            FactTool.parseObject argsJson
            |> Result.bind (fun root -> FactTool.stringArg root "table")
            |> Result.map (Option.filter (String.IsNullOrWhiteSpace >> not) >> Option.map _.Trim())

    let private levelRow (l: FactTableLevelView) : obj =
        match l.Description with
        | Some d -> box {| level = l.Level; description = d |}
        | None -> box {| level = l.Level |}

    let private columnRow (c: FactTableMetricView) = {|
        metric = c.Metric
        name = c.Name
        unit = c.Unit
        displayFormat = c.DisplayFormat
        context = c.Context
        description = c.Description
        shape = c.Shape
        restrictedUnderPolicies = c.RestrictedUnderPolicies
    |}

    /// One table as the tool's result carries it. A withheld table is its id,
    /// `withheld = true`, the policies and the reason: nothing more.
    let private row (v: FactTableView) : obj =
        match v with
        | FactTableView.Withheld w ->
            box {|
                id = w.TableId
                withheld = true
                restrictedUnderPolicies = w.RestrictedUnderPolicies
                reason = w.Reason
            |}
        | FactTableView.Shown s ->
            box {|
                id = s.TableId
                withheld = false
                description = s.Description
                hierarchy = s.Hierarchy
                hierarchyName = s.HierarchyName
                hierarchyDescription = s.HierarchyDescription
                level = s.Level
                path = s.Path |> List.map levelRow
                columns = s.Columns |> List.map columnRow
                periodGrain = s.PeriodGrain
                refreshCadenceSeconds = s.RefreshCadenceSeconds
                historyMode = s.HistoryMode
                rowCount = s.RowCount
                lastRefreshedAt = s.LastRefreshedAt |> Option.map (fun t -> t.ToUniversalTime().ToString "o")
                lastRun = s.LastRun
                freshness = s.Freshness
                overdueBySeconds = s.OverdueBySeconds
                latestRunOutcome = s.LatestRunOutcome
                restrictedUnderPolicies = s.RestrictedUnderPolicies
            |}

    /// The executor over explicit dependencies — the testable core `execute`
    /// adapts `HttpContext` onto.
    let executeWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseTable argsJson with
            | Error message -> return FactTool.serialize {| error = message |}
            | Ok table ->
                match! project deps scope principal table with
                | Error refusal ->
                    return
                        FactTool.serialize {|
                            error = sprintf "%s Call `%s` with no arguments to list every declared table." refusal Name
                        |}
                | Ok views ->
                    return
                        FactTool.serialize {|
                            tableCount = List.length views
                            tables = views |> List.map row
                        |}
        }

    /// The registered `HttpContext` executor: the fact tool shell's scope and
    /// caller, over the fact browse's dependencies for this request.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        FactTool.define
            Name
            (fun c argsJson ->
                // The shell found the store and the gate, which are exactly
                // what the browse dependencies need.
                match FactBrowseHandler.factBrowseDeps ctx with
                | Some deps -> executeWith deps c.Scope c.UserId argsJson
                | None ->
                    async.Return(
                        FactTool.serialize {|
                            error = $"The fact store is not composed in this deployment — `{Name}` is unavailable."
                        |}
                    ))
            ctx
            argsJson