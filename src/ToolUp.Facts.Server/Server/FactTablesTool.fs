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
//
// **Members of a small level.** The level a table's rows sit at lists its
// members' names when it holds at most a declared cap of distinct members
// (`FactTablesOptions.MemberCap`, default 20, 0 turns it off). A name is a
// subject identity and never a figure, but it is an egress of its own, so it
// is read the way a drill-down is: each metric column's ranking, paged under
// the request's scope, judged by the gate fixed at `FactToolResult`. A member
// is listed when some fact of it was admitted; the rest are counted, with the
// policies facts were withheld under, and never named. A level above the cap
// reports its count and nothing more, and so does one whose ranking could not
// be read to its end. A wholly withheld table is described by nothing, so no
// member read is made for it.

/// The members of a level whose names were listed: those the caller may be
/// told, and a count of the rest.
type FactLevelMemberList = {
    /// The members' names, ordinal-sorted. Names only, never a value.
    Names: string list
    /// Members not listed: withheld by the disclosure gate, or carrying no
    /// rankable value. Counted, never named.
    Unlisted: int
    /// The policies facts of this level were withheld under.
    RestrictedUnderPolicies: string list
}

/// A level whose members are counted and not named.
type FactLevelMemberCount = {
    /// The level's distinct members in the table, as far as the read could
    /// tell.
    Count: int
    /// The declared cap.
    Cap: int
    /// Why no name is listed.
    Reason: string
}

/// What the table listing says of a level's members.
[<RequireQualifiedAccess>]
type FactLevelMembers =
    /// At most the cap: the visible names, and a count of the rest.
    | Listed of FactLevelMemberList
    /// Above the cap, or not readable to its end: the count only.
    | CountOnly of FactLevelMemberCount

/// One level on the path from a hierarchy's root to a table's level.
type FactTableLevelView = {
    /// The level, as the hierarchy declares it.
    Level: string
    /// What the level holds, where the declaring module described it.
    Description: string option
    /// The level's members: set on the level the table's rows sit at, and
    /// only while member listing is on; `None` elsewhere.
    Members: FactLevelMembers option
}

/// What a deployment declares of the table listing, handed to
/// `FactsCompose.withFactTablesOptions`; undeclared means
/// `FactTablesOptions.defaults`.
type FactTablesOptions = {
    /// The most distinct members a level may hold and still be listed by
    /// name. `0` turns member listing off.
    MemberCap: int
}

[<RequireQualifiedAccess>]
module FactTablesOptions =
    /// The cap a deployment gets when it declares none.
    [<Literal>]
    let DefaultMemberCap = 20

    /// Members listed up to `DefaultMemberCap`.
    let defaults: FactTablesOptions = { MemberCap = DefaultMemberCap }

    /// Member listing off.
    let membersOff: FactTablesOptions = { MemberCap = 0 }

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

    // ── The member read ───────────────────────────────────────────

    /// The reason a level above the cap gives.
    [<Literal>]
    let OverCapReason =
        "This level holds more members than the table listing names, so only their count is given. Use query_metric_population to find a member by ranking."

    /// The reason a level whose ranking could not be read to its end gives.
    [<Literal>]
    let UnenumeratedReason =
        "This level's members could not be read to the end within the ranking ceiling, so only their count is given."

    /// What one metric column's ranking showed of the level's members.
    type private ColumnMembers = {
        /// Names of members with at least one fact the gate admitted.
        Names: string list
        /// The distinct members the column's population matched, admitted or
        /// not — an existence-level count.
        Total: int
        /// The policies facts of the column were withheld under.
        Policies: string list
        /// Whether the ranking was read to its end.
        Complete: bool
    }

    /// The most pages one column's member read takes: the ranking ceiling
    /// over the page size.
    let private maxMemberPages = PopulationQuery.MaxTopK / FactBrowseApi.MaxPageSize + 1

    /// One column's members at the table's level, read page by page through
    /// the drill-down, and stopped as soon as the level is known to exceed
    /// `cap`. `None` when the store refused the read.
    let private columnMembers
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (tableId: string)
        (metric: string)
        (cap: int)
        : Async<ColumnMembers option> =
        let rec loop (page: int) (names: string list) (policies: string list) = async {
            let! read =
                FactBrowseHandler.queryRows deps scope principal {
                    TableId = tableId
                    Metric = metric
                    Direction = TopOfRanking
                    SubjectPrefix = []
                    Page = page
                    PageSize = FactBrowseApi.MaxPageSize
                }

            match read with
            | Error _ -> return None
            | Ok read ->
                let names =
                    read.Rows
                    |> List.choose (fun r -> List.tryLast r.SubjectPath)
                    |> List.append names
                    |> List.distinct

                let policies = read.Withheld |> List.map _.PolicyRef |> List.append policies
                let total = read.Population.SubjectCount

                let result complete = {
                    Names = names
                    Total = total
                    Policies = sorted policies
                    Complete = complete
                }

                if total > cap then
                    return Some(result true)
                elif read.HasMore && page + 1 < maxMemberPages then
                    return! loop (page + 1) names policies
                else
                    return Some(result (not read.HasMore && not read.ReachedRankCeiling))
        }

        loop 0 [] []

    /// The members of the level a shown table's rows sit at, as `principal`
    /// may be told them: each column's ranking is read under the gate fixed
    /// at `FactToolResult`, and a name is listed when some fact of the member
    /// was admitted. `None` when listing is off, the table holds no committed
    /// row, or nothing could be read.
    let private members
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (cap: int)
        (detail: FactTableDetail)
        : Async<FactLevelMembers option> =
        async {
            let committed = detail.Listing.RowCount |> Option.exists (fun n -> n > 0)

            if cap <= 0 || not committed then
                return None
            else
                let! reads =
                    detail.Columns
                    |> List.map (fun c -> columnMembers deps scope principal detail.Listing.TableId c.Metric cap)
                    |> Async.Sequential

                match reads |> Array.choose id |> List.ofArray with
                | [] -> return None
                | columns ->
                    let names = columns |> List.collect _.Names |> List.distinct
                    let total = columns |> List.map _.Total |> List.max

                    let count = max total (List.length names)

                    if count = 0 then
                        return None
                    elif count > cap then
                        return
                            Some(
                                FactLevelMembers.CountOnly {
                                    Count = count
                                    Cap = cap
                                    Reason = OverCapReason
                                }
                            )
                    elif columns |> List.exists (fun c -> not c.Complete) then
                        return
                            Some(
                                FactLevelMembers.CountOnly {
                                    Count = count
                                    Cap = cap
                                    Reason = UnenumeratedReason
                                }
                            )
                    else
                        return
                            Some(
                                FactLevelMembers.Listed {
                                    Names = names |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                                    Unlisted = count - List.length names
                                    RestrictedUnderPolicies = columns |> List.collect _.Policies |> sorted
                                }
                            )
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
        (members: FactLevelMembers option)
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
                    Members = if level = listing.Level then members else None
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
        (cap: int)
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
                let! listed = members deps scope principal cap detail
                return FactTableView.Shown(shown deps.Registry detail samples listed)
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
                return FactTableView.Shown(shown deps.Registry detail samples None)
        }

    /// Every declared table, ordered by id, or the one named by `table`, as
    /// `principal` may be told about it in `scope`, its level listing at most
    /// `memberCap` members by name (`0` lists none). The disclosure sample
    /// and the member read are asked at `FactToolResult` whichever gate
    /// `deps` carries. An unknown table id is refused by name.
    let projectWith
        (memberCap: int)
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
                let! views =
                    details
                    |> List.map (view atToolResult scope principal (max 0 memberCap))
                    |> Async.Sequential

                return Ok(List.ofArray views)
        }

    /// `projectWith` with member listing off: the table projection that
    /// names no member.
    let project
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (table: string option)
        : Async<Result<FactTableView list, string>> =
        projectWith 0 deps scope principal table

    // ── The tool ──────────────────────────────────────────────────

    /// The `list_fact_tables` tool declaration.
    let definition: AIToolDefinition = {
        Name = Name
        Description =
            "List THE FACT TABLES THIS DEPLOYMENT HOLDS — call this before you plan a fact question that names a level, a grain or a set of metrics together (\"top four countries for orders\", \"weekly revenue by site\"), so you know whether any table holds that metric at that level before you query. For every declared table it returns: its id (the token the render and pipeline tools take) and what it is for; the subject hierarchy it sits in, the LEVEL its rows sit at, and the path of levels from the hierarchy's root to that level with what each level holds, and, on the level its rows sit at, the NAMES of its members when it has only a few (use those names as they are; `members.unlisted` counts those you may not see, and a level with `members.listed = false` has more members than are named, so find one by ranking); each metric column with its id, name, unit, display format, the analyst's context and what the column holds; and the table's period grain, refresh cadence, history mode, row count, last committed run and freshness. A metric at a level no table lists is NOT HELD at that level: say so rather than querying. It reports DECLARATIONS AND COUNTS ONLY and never a value — ask the query tools for values. Where you are not permitted to see a table, it is listed as existing with the restricting policy and nothing more. Pass `table` to describe one table by id. Results are scoped to the current user/team."
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

    /// A level's members as the tool carries them: the names and a count of
    /// the rest, or the count alone.
    let private membersRow (m: FactLevelMembers) : obj =
        match m with
        | FactLevelMembers.Listed l ->
            box {|
                listed = true
                count = List.length l.Names + l.Unlisted
                names = l.Names
                unlisted = l.Unlisted
                restrictedUnderPolicies = l.RestrictedUnderPolicies
            |}
        | FactLevelMembers.CountOnly c ->
            box {|
                listed = false
                count = c.Count
                cap = c.Cap
                reason = c.Reason
            |}

    let private levelRow (l: FactTableLevelView) : obj =
        match l.Description, l.Members with
        | Some d, Some m ->
            box {|
                level = l.Level
                description = d
                members = membersRow m
            |}
        | Some d, None -> box {| level = l.Level; description = d |}
        | None, Some m ->
            box {|
                level = l.Level
                members = membersRow m
            |}
        | None, None -> box {| level = l.Level |}

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

    /// The executor over explicit dependencies and options — the testable
    /// core `execute` adapts `HttpContext` onto.
    let executeWithOptions
        (options: FactTablesOptions)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            match parseTable argsJson with
            | Error message -> return FactTool.serialize {| error = message |}
            | Ok table ->
                match! projectWith options.MemberCap deps scope principal table with
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

    /// `executeWithOptions` under `FactTablesOptions.defaults`.
    let executeWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        executeWithOptions FactTablesOptions.defaults deps scope principal argsJson

    /// The `HttpContext` executor under `options`: the fact tool shell's scope
    /// and caller, over the fact browse's dependencies for this request. The
    /// options are a value handed in at composition
    /// (`FactsCompose.withFactTablesOptions`); nothing here reads the container
    /// for them.
    let executeFor (options: FactTablesOptions) (ctx: HttpContext) (argsJson: string) : Async<string> =
        FactTool.define
            Name
            (fun c argsJson ->
                // The shell found the store and the gate, which are exactly
                // what the browse dependencies need.
                match FactBrowseHandler.factBrowseDeps ctx with
                | Some deps -> executeWithOptions options deps c.Scope c.UserId argsJson
                | None ->
                    async.Return(
                        FactTool.serialize {|
                            error = $"The fact store is not composed in this deployment — `{Name}` is unavailable."
                        |}
                    ))
            ctx
            argsJson

    /// The registered `HttpContext` executor under `FactTablesOptions.defaults`.
    let execute (ctx: HttpContext) (argsJson: string) : Async<string> =
        executeFor FactTablesOptions.defaults ctx argsJson