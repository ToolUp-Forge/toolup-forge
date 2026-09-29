// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes

// ─── FactBrowseHandler (Phase 895) ───────────────────────────────────
//
// The server side of `IFactBrowseApi`: the tables a scope holds, their
// run history, one run's largest movers, paged drill-down rows, and one
// fact's provenance — every one a bounded answer, and every served fact
// judged at the `FactBrowse` egress door.
//
// **The drill-down IS the population tool's read.** A page is answered
// by `IFactStore.QueryPopulation` with `TopK` set to the end of the
// requested window, gated through `PopulationDisclosure.fold` exactly as
// `PopulationQueryTool.executeWith` gates its ranking, and then sliced to
// the window. So page `p` of size `n` serves ranks `p·n+1 … (p+1)·n` of
// precisely the ranking the tool returns for `top_k = (p+1)·n`, and the
// population summary beside it is the tool's, suppressed on the same
// condition. The only thing the browse read adds to the question is the
// table's own method lineage (`OneMethod`), so the rows are the table's
// rows and no other producer's.
//
// **What reaches the client.** Row counts, run counts, change counts and
// freshness are existence-level and always ride. Values — a row's
// rendering, a mover's previous and current value, the population's
// magnitudes, a fact's supersession chain — ride only after the gate
// admitted the fact that carries them. A withheld row is absent from the
// page; the page reports a count grouped by policy, never a value, a
// subject or an id.
//
// **Per run, never per fact.** `withFactBrowse` also decorates the
// composed `IFactTableWriter` so that a committed run publishes ONE
// notification to its scope, carrying the run's counts. Nothing in this
// file notifies per fact.
//
// GP 12: identity by value (strings and records), async at every
// boundary, failure as data (`Result<_, string>` naming the refusal),
// stateless between calls (every answer is read from the stores), the
// scope is the shard key, and no timing primitive beyond the injected
// clock the freshness derivations read.

/// The fact browse read surface over explicit dependencies, and its
/// composition.
module FactBrowseHandler =

    /// What the browse handler reads. Resolved per request from DI by the
    /// `HttpContext` adapter; constructed directly by tests.
    type FactBrowseDeps = {
        /// The declared fact tables.
        Tables: IFactTableRegistry
        /// The composed table writer — the run records and table standing.
        /// `None` when no writer is composed: every table then reads as
        /// never refreshed.
        Writer: IFactTableWriter option
        /// The fact store — the population read and point reads.
        Store: IFactStore
        /// The disclosure gate every served fact passes, at `FactBrowse`.
        Gate: IFactDisclosureGate
        /// The metric registry: display formats, staleness policies and
        /// the hierarchy levels a table sits at.
        Registry: IMetricRegistry option
        /// The clock freshness is derived against.
        Clock: unit -> DateTime
    }

    // ── Projections ──────────────────────────────────────────────

    let private freshnessLabel (freshness: FactTableFreshness) : string * int64 option =
        match freshness with
        | FactTableFreshness.NeverRefreshed -> "never-refreshed", None
        | FactTableFreshness.Fresh _ -> "fresh", None
        | FactTableFreshness.Stale(_, overdueBy) -> "stale", Some(int64 overdueBy.TotalSeconds)

    let private outcomeLabel (outcome: FactTableRunOutcome) : string * string option =
        match outcome with
        | FactTableRunOutcome.InProgress -> "in-progress", None
        | FactTableRunOutcome.Succeeded -> "succeeded", None
        | FactTableRunOutcome.Failed reason -> "failed", Some reason
        | FactTableRunOutcome.Discarded reason -> "discarded", Some reason

    let private statusLabel (status: FactTableRunStatus) : string =
        match status with
        | FactTableRunStatus.Open -> "open"
        | FactTableRunStatus.Committed _ -> "committed"
        | FactTableRunStatus.Rejected _ -> "rejected"
        | FactTableRunStatus.Abandoned _ -> "abandoned"

    let private tableDisclosureLabel (d: FactTableDisclosure) : string =
        match d with
        | FactTableDisclosure.Surfaceable -> "Surfaceable"
        | FactTableDisclosure.Internal -> "Internal"
        | FactTableDisclosure.Restricted policy -> sprintf "Restricted(%s)" policy

    /// The method every fact a table's run writes carries — the writer's
    /// `Computed(producingOperation, "v<schemaVersion>", tableId)`. The
    /// drill-down reads this lineage and no other.
    let tableMethod (table: FactTableDefinition) : MethodRef =
        Computed(table.ProducingOperation, sprintf "v%d" table.SchemaVersion, table.Id)

    /// The trigger reference the writer stamps on every fact of a run.
    let runTrigger (tableId: string) (runId: string) : string =
        sprintf "fact-table-run:%s/%s" tableId runId

    /// The table and run a fact's trigger reference names, when a declared
    /// table's run wrote it.
    let private tableRunOf (triggerRef: string option) : (string * string) option =
        match triggerRef with
        | Some trigger when trigger.StartsWith "fact-table-run:" ->
            let rest = trigger.Substring "fact-table-run:".Length
            let slash = rest.LastIndexOf '/'

            if slash > 0 && slash < rest.Length - 1 then
                Some(rest.Substring(0, slash), rest.Substring(slash + 1))
            else
                None
        | _ -> None

    let private listing
        (deps: FactBrowseDeps)
        (scopeId: string)
        (table: FactTableDefinition)
        : Async<FactTableListing> =
        async {
            let declaringModule = deps.Tables.DeclaringModule table.Id |> Option.defaultValue ""

            let blank = {
                TableId = table.Id
                Module = declaringModule
                Metrics = FactTableDefinition.metrics table
                Hierarchy = table.Hierarchy
                Level = table.Level
                PeriodGrain = sprintf "%A" table.PeriodGrain
                HistoryMode = sprintf "%A" table.HistoryMode
                RowCount = None
                LastRefreshedAt = None
                Freshness = "never-refreshed"
                OverdueBySeconds = None
                LatestRunOutcome = None
                LatestRunReason = None
            }

            match deps.Writer with
            | None -> return blank
            | Some writer ->
                match! writer.Status(scopeId, table.Id) with
                | Error e ->
                    return {
                        blank with
                            Freshness = "unavailable"
                            LatestRunReason = Some(FactTableWriteError.describe e)
                    }
                | Ok status ->
                    let freshness, overdue = freshnessLabel status.Freshness
                    let outcome = status.LatestRunOutcome |> Option.map outcomeLabel

                    return {
                        blank with
                            RowCount = status.LastCommit |> Option.map _.RowCount
                            LastRefreshedAt = status.LastCommit |> Option.map _.Watermark.CommittedAt
                            Freshness = freshness
                            OverdueBySeconds = overdue
                            LatestRunOutcome = outcome |> Option.map fst
                            LatestRunReason = outcome |> Option.bind snd
                    }
        }

    let private runView (table: FactTableDefinition) (now: DateTime) (run: FactTableRunRecord) : FactTableRunView =
        let outcome, reason = outcomeLabel (FactTableRun.outcome table now run)

        let commit =
            match run.Status with
            | FactTableRunStatus.Committed c -> Some c
            | _ -> None

        {
            RunId = run.RunId
            OpenedAt = run.OpenedAt
            Status = statusLabel run.Status
            Outcome = outcome
            Reason = reason
            StagedRows = run.StagedRows
            RowsWritten = commit |> Option.map _.RowCount
            FactsWritten = commit |> Option.map _.FactsWritten
            Sequence = commit |> Option.map _.Watermark.Sequence
            CommittedAt = commit |> Option.map _.Watermark.CommittedAt
            Change =
                commit
                |> Option.map (fun c -> {
                    New = c.Change.New
                    Changed = c.Change.Changed
                    Unchanged = c.Change.Unchanged
                    Removed = c.Change.Removed
                    MoverCount = List.length c.Change.LargestMovers
                })
        }

    let private declared (deps: FactBrowseDeps) (tableId: string) : Result<FactTableDefinition, string> =
        match deps.Tables.TryGetTable tableId with
        | Some table -> Ok table
        | None -> Error(sprintf "No fact table '%s' is declared in this deployment." tableId)

    let private displayFormatOf (deps: FactBrowseDeps) (metric: string) : string =
        deps.Registry
        |> Option.bind (fun r -> r.TryGetMetric metric)
        |> Option.map _.DisplayFormat
        |> Option.defaultValue ""

    let private stalenessOf (deps: FactBrowseDeps) (metric: string) : StalenessPolicy =
        deps.Registry
        |> Option.bind (fun r -> r.TryGetMetric metric)
        |> Option.map _.Staleness
        |> Option.defaultValue UntilSuperseded

    /// One gate call over a set of ids; an id the gate returned nothing for
    /// is denied, so the door never fails open.
    let private judge
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (factIds: string list)
        : Async<string -> FactDisclosureVerdict> =
        async {
            if List.isEmpty factIds then
                return fun _ -> FactNotDisclosable "unknown-fact"
            else
                let! verdicts = deps.Gate.Check(scope, principal, FactBrowse, List.distinct factIds)

                return
                    fun factId ->
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")
        }

    let private withheldCounts (policies: string list) : FactWithheldCount list =
        policies
        |> List.countBy id
        |> List.sortBy fst
        |> List.map (fun (policyRef, count) -> { PolicyRef = policyRef; Count = count })

    let private rowOf (deps: FactBrowseDeps) (now: DateTime) (rank: int) (fact: Fact) : FactRowView =
        let metric = fact.Metric |> fun (MetricRef m) -> m

        let freshness =
            match Freshness.deriveAt (stalenessOf deps metric) fact.AsOf true now with
            | FactFreshness.Fresh -> "fresh"
            | FactFreshness.Stale _ -> "stale"

        {
            Rank = rank
            FactId = fact.FactId
            Subject = SubjectRef.toString fact.Subject
            SubjectPath = fact.Subject.Path
            Rendering = FactRendering.render (displayFormatOf deps metric) fact.Value
            PeriodFrom = fact.Period.From
            PeriodTo = fact.Period.To
            PeriodLabel = fact.Period.Label
            AsOf = fact.AsOf
            Freshness = freshness
        }

    // ── The six reads ────────────────────────────────────────────

    /// Every declared table with its standing, ordered by table id.
    let listTables (deps: FactBrowseDeps) (scope: ResolvedScope) : Async<Result<FactTableListing list, string>> = async {
        let! rows =
            deps.Tables.Tables
            |> List.sortBy _.Id
            |> List.map (listing deps scope.ScopeId)
            |> Async.Sequential

        return Ok(List.ofArray rows)
    }

    /// One table's declaration and standing.
    let getTable
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (tableId: string)
        : Async<Result<FactTableDetail, string>> =
        async {
            match declared deps tableId with
            | Error e -> return Error e
            | Ok table ->
                let! row = listing deps scope.ScopeId table

                return
                    Ok {
                        Listing = row
                        Columns =
                            table.Columns
                            |> List.map (fun column -> {
                                Metric = column.Metric
                                Shape = sprintf "%A" column.Shape
                                Disclosure = tableDisclosureLabel (FactTableDefinition.columnDisclosure table column)
                            })
                        ProducingOperation = table.ProducingOperation
                        SchemaVersion = table.SchemaVersion
                        RefreshCadenceSeconds = int64 table.RefreshCadence.TotalSeconds
                        Requirement = sprintf "%A" table.Requirement
                    }
        }

    /// One page of a table's run history, most recently opened first.
    let listRuns
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (tableId: string, page: int, pageSize: int)
        : Async<Result<FactRunPage, string>> =
        async {
            match declared deps tableId with
            | Error e -> return Error e
            | Ok table ->
                let size = FactBrowseApi.clampPageSize pageSize
                let index = FactBrowseApi.clampPage page

                let empty = {
                    TableId = tableId
                    Runs = []
                    Page = index
                    PageSize = size
                    MaxPageSize = FactBrowseApi.MaxPageSize
                    TotalRuns = 0
                }

                match deps.Writer with
                | None -> return Ok empty
                | Some writer ->
                    match! writer.Runs(scope.ScopeId, tableId) with
                    | Error e -> return Error(FactTableWriteError.describe e)
                    | Ok runs ->
                        let now = deps.Clock().ToUniversalTime()

                        return
                            Ok {
                                empty with
                                    TotalRuns = List.length runs
                                    Runs =
                                        runs
                                        |> List.skip (min (List.length runs) (index * size))
                                        |> List.truncate size
                                        |> List.map (runView table now)
                            }
        }

    let private subjectPathOf (hierarchy: string) (subjectText: string) : string list option =
        let prefix = hierarchy + "/"

        if subjectText.StartsWith prefix then
            let rest = subjectText.Substring prefix.Length

            if rest = "" then
                Some []
            else
                Some(rest.Split '>' |> List.ofArray)
        else
            None

    let private periodText (period: TemporalExtent) : string =
        match period.Label with
        | Some label -> label
        | None -> sprintf "%s..%s" (period.From.ToString("o")) (period.To.ToString("o"))

    /// One committed run's largest movers. Each mover names a cell the run
    /// rewrote; it is served only when the gate admits BOTH the run's fact
    /// for that cell and the fact it replaced, since the mover carries both
    /// values. A mover whose facts cannot be found is withheld, never
    /// served on the run record's word.
    let getRunMovers
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (tableId: string, runId: string)
        : Async<Result<FactRunMovers, string>> =
        async {
            match declared deps tableId, deps.Writer with
            | Error e, _ -> return Error e
            | Ok _, None -> return Error "No fact table writer is composed in this deployment."
            | Ok table, Some writer ->
                match! writer.Runs(scope.ScopeId, tableId) with
                | Error e -> return Error(FactTableWriteError.describe e)
                | Ok runs ->
                    match runs |> List.tryFind (fun r -> r.RunId = runId) with
                    | None -> return Error(sprintf "Table '%s' has no run '%s'." tableId runId)
                    | Some run ->
                        match run.Status with
                        | FactTableRunStatus.Committed commit ->
                            let trigger = runTrigger tableId runId
                            let method = tableMethod table

                            // Resolve each mover to (current, previous) facts.
                            let! resolved =
                                commit.Change.LargestMovers
                                |> List.map (fun mover -> async {
                                    match subjectPathOf table.Hierarchy mover.Subject with
                                    | None -> return mover, None
                                    | Some path ->
                                        let query: FactQuery = {
                                            FactQuery.all with
                                                Subject =
                                                    Some {
                                                        Hierarchy = table.Hierarchy
                                                        Path = path
                                                    }
                                                Metric = Some(MetricRef mover.Metric)
                                                Method = Some method
                                                IncludeSuperseded = true
                                        }

                                        let! history = deps.Store.Query(scope, query)

                                        let samePeriod =
                                            history |> List.filter (fun f -> periodText f.Period = mover.Period)

                                        let candidates = if List.isEmpty samePeriod then history else samePeriod

                                        let current =
                                            candidates
                                            |> List.filter (fun f -> f.Evidence.TriggerRef = Some trigger)
                                            |> List.sortByDescending _.AsOf
                                            |> List.tryHead

                                        let previous =
                                            current
                                            |> Option.bind (fun c ->
                                                candidates
                                                |> List.filter (fun f -> f.AsOf < c.AsOf && f.FactId <> c.FactId)
                                                |> List.sortByDescending _.AsOf
                                                |> List.tryHead)

                                        match current, previous with
                                        | Some c, Some p -> return mover, Some(c, p)
                                        | _ -> return mover, None
                                })
                                |> Async.Sequential

                            let ids =
                                resolved
                                |> Array.toList
                                |> List.collect (fun (_, pair) ->
                                    match pair with
                                    | Some(c, p) -> [ c.FactId; p.FactId ]
                                    | None -> [])

                            let! verdictFor = judge deps scope principal ids

                            let judged =
                                resolved
                                |> Array.toList
                                |> List.map (fun (mover, pair) ->
                                    match pair with
                                    | None -> mover, Error "unknown-fact"
                                    | Some(c, p) ->
                                        match verdictFor c.FactId, verdictFor p.FactId with
                                        | FactDisclosable, FactDisclosable -> mover, Ok()
                                        | FactNotDisclosable policy, _
                                        | _, FactNotDisclosable policy -> mover, Error policy)

                            let format = fun (metric: string) -> displayFormatOf deps metric

                            let render (metric: string) (value: decimal) =
                                FactRendering.render (format metric) (Scalar value)

                            let shown =
                                judged
                                |> List.choose (fun (mover, verdict) ->
                                    match verdict with
                                    | Ok() ->
                                        Some {
                                            FactMoverView.Metric = mover.Metric
                                            Subject = mover.Subject
                                            Period = mover.Period
                                            Previous = render mover.Metric mover.Previous
                                            Current = render mover.Metric mover.Current
                                            Delta = render mover.Metric mover.Delta
                                        }
                                    | Error _ -> None)

                            let withheld =
                                judged
                                |> List.choose (fun (_, verdict) ->
                                    match verdict with
                                    | Error policy -> Some policy
                                    | Ok() -> None)

                            return
                                Ok {
                                    TableId = tableId
                                    RunId = runId
                                    Movers = shown
                                    WithheldCount = List.length withheld
                                    Withheld = withheldCounts withheld
                                }
                        | _ -> return Error(sprintf "Run '%s' did not commit, so it has no movers." runId)
        }

    /// One page of a drill-down — see the file header for why this is the
    /// population tool's read.
    let queryRows
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (request: FactRowQuery)
        : Async<Result<FactRowPage, string>> =
        async {
            match declared deps request.TableId with
            | Error e -> return Error e
            | Ok table when not (List.contains request.Metric (FactTableDefinition.metrics table)) ->
                return Error(sprintf "Table '%s' has no metric column '%s'." table.Id request.Metric)
            | Ok table ->
                let size = FactBrowseApi.clampPageSize request.PageSize
                let index = FactBrowseApi.clampPage request.Page
                let windowStart = index * size
                let windowEnd = windowStart + size
                let ceiling = PopulationQuery.MaxTopK

                let level =
                    deps.Registry
                    |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)
                    |> Option.bind (fun subject -> FactTableDefinition.levelDepth subject table.Level)

                let query: PopulationQuery = {
                    Metric = MetricRef request.Metric
                    Hierarchy = table.Hierarchy
                    Level = level
                    PathPrefix =
                        if List.isEmpty request.SubjectPrefix then
                            None
                        else
                            Some request.SubjectPrefix
                    PeriodOverlaps = None
                    Threshold = None
                    Ordering =
                        match request.Direction with
                        | TopOfRanking -> Descending
                        | BottomOfRanking -> Ascending
                    TopK = max 1 (min windowEnd ceiling)
                    AsOf = None
                    Methods = OneMethod(tableMethod table)
                }

                match! deps.Store.QueryPopulation(scope, query) with
                | Error refusal -> return Error refusal
                | Ok result ->
                    // One gate call over the whole ranking up to the
                    // window's end — the tool's own call for this top-k —
                    // so the magnitude suppression below is the tool's.
                    let! verdictFor = judge deps scope principal (result.Ranked |> List.map _.FactId)
                    let disclosure = PopulationDisclosure.fold verdictFor result.Ranked
                    let now = deps.Clock().ToUniversalTime()

                    let inWindow (rank: int) = rank > windowStart && rank <= windowEnd

                    let rows =
                        disclosure.Disclosable
                        |> List.filter (fst >> inWindow)
                        |> List.map (fun (rank, fact) -> rowOf deps now rank fact)

                    let withheld =
                        result.Ranked
                        |> List.mapi (fun i fact -> i + 1, fact)
                        |> List.filter (fst >> inWindow)
                        |> List.choose (fun (_, fact) ->
                            match verdictFor fact.FactId with
                            | FactDisclosable -> None
                            | FactNotDisclosable policy -> Some policy)

                    let stats = PopulationDisclosure.disclosedStats disclosure result.Stats
                    let format = displayFormatOf deps request.Metric
                    let renderStat = Option.map (fun d -> FactRendering.render format (Scalar d))

                    return
                        Ok {
                            TableId = table.Id
                            Metric = request.Metric
                            Direction = request.Direction
                            SubjectPrefix = request.SubjectPrefix
                            Page = index
                            PageSize = size
                            MaxPageSize = FactBrowseApi.MaxPageSize
                            RankCeiling = ceiling
                            Rows = rows
                            WithheldCount = List.length withheld
                            Withheld = withheldCounts withheld
                            HasMore = result.Truncated && windowEnd < ceiling
                            ReachedRankCeiling = result.Truncated && windowEnd >= ceiling
                            Population = {
                                SubjectCount = stats.SubjectCount
                                FactCount = stats.FactCount
                                ComparableCount = stats.ComparableCount
                                NonComparableCount = stats.NonComparableCount
                                PeriodFrom = stats.PeriodFrom
                                PeriodTo = stats.PeriodTo
                                FreshCount = stats.Freshness.FreshCount
                                StaleCount = stats.Freshness.StaleCount
                                Methods =
                                    stats.MethodMix
                                    |> List.map (fun (identity, count) -> { Method = identity; FactCount = count })
                                Minimum = renderStat stats.Minimum
                                Maximum = renderStat stats.Maximum
                                Mean = renderStat stats.Mean
                                ValueStatisticsWithheld = PopulationDisclosure.valuesWithheld disclosure
                            }
                        }
        }

    /// One fact by id and its supersession chain, each step gated. A fact
    /// the viewer may not see is refused naming the policy, never the value.
    let getFact
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (factId: string)
        : Async<Result<FactDetailView, string>> =
        async {
            match! deps.Store.Get(scope, factId) with
            | None -> return Error "No fact with that id is visible in this scope."
            | Some fact ->
                let! chain = deps.Store.QuerySupersessionChain(scope, factId)
                let ids = fact.FactId :: (chain |> List.map _.FactId)
                let! verdictFor = judge deps scope principal ids

                match verdictFor fact.FactId with
                | FactNotDisclosable policy -> return Error(FactDisclosureVerdict.refusalText policy)
                | FactDisclosable ->
                    let now = deps.Clock().ToUniversalTime()
                    let metric = fact.Metric |> fun (MetricRef m) -> m
                    let format = displayFormatOf deps metric

                    let steps =
                        chain
                        |> List.filter (fun f -> f.FactId <> fact.FactId)
                        |> List.sortByDescending _.AsOf

                    let visible, hidden =
                        steps |> List.partition (fun f -> verdictFor f.FactId = FactDisclosable)

                    let run = tableRunOf fact.Evidence.TriggerRef

                    let tableId =
                        match fact.Method, run with
                        | Computed(_, _, paramHash), Some(t, _) when t = paramHash && (deps.Tables.TryGetTable t).IsSome ->
                            Some t
                        | _ -> None

                    return
                        Ok {
                            Row = rowOf deps now 0 fact
                            Metric = metric
                            Disclosure = Disclosure.toString fact.Disclosure
                            Method = Fact.methodIdentity fact.Method
                            TableId = tableId
                            RunId = tableId |> Option.bind (fun _ -> run |> Option.map snd)
                            ResultRef = fact.Evidence.ResultRef
                            InputHashes = fact.Evidence.InputHashes
                            TriggerRef = fact.Evidence.TriggerRef
                            Chain =
                                visible
                                |> List.map (fun f -> {
                                    FactId = f.FactId
                                    Rendering = FactRendering.render format f.Value
                                    AsOf = f.AsOf
                                    Method = Fact.methodIdentity f.Method
                                    Supersedes = f.Supersedes
                                })
                            ChainWithheldCount = List.length hidden
                        }
        }

    /// The API record over explicit dependencies, for one caller.
    let api (deps: FactBrowseDeps) (scope: ResolvedScope) (principal: string) : IFactBrowseApi = {
        ListTables = fun () -> listTables deps scope
        GetTable = getTable deps scope
        ListRuns = listRuns deps scope
        GetRunMovers = getRunMovers deps scope principal
        QueryRows = queryRows deps scope principal
        GetFact = getFact deps scope principal
    }

    // ── HttpContext adapter ──────────────────────────────────────

    let private serviceOf<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
        match ctx.RequestServices.GetService(typeof<'T>) with
        | :? 'T as service -> Some service
        | _ -> None

    let private userIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let private unavailable: IFactBrowseApi =
        let refusal () = async {
            return Error "The fact store is not composed in this deployment — fact browsing is unavailable."
        }

        {
            ListTables = fun () -> refusal ()
            GetTable = fun _ -> refusal ()
            ListRuns = fun _ -> refusal ()
            GetRunMovers = fun _ -> refusal ()
            QueryRows = fun _ -> refusal ()
            GetFact = fun _ -> refusal ()
        }

    /// The per-request API. The scope is the one the platform's scope
    /// resolution minted for this request (`ScopeResolution.forRequest`),
    /// the principal the request's user.
    let factBrowseApi (ctx: HttpContext) : IFactBrowseApi =
        match serviceOf<IFactStore> ctx, serviceOf<IFactDisclosureGate> ctx with
        | Some store, Some gate ->
            let deps = {
                Tables = serviceOf<IFactTableRegistry> ctx |> Option.defaultValue FactTableRegistry.empty
                Writer = serviceOf<IFactTableWriter> ctx
                Store = store
                Gate = gate
                Registry = serviceOf<IMetricRegistry> ctx
                Clock = fun () -> DateTime.UtcNow
            }

            api deps (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx)
        | _ -> unavailable

    // ── Per-run notification ─────────────────────────────────────

    let private noticeOptions =
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

    /// The notice a committed run publishes — counts only.
    let runNotice (tableId: string) (runId: string) (commit: FactTableCommit) : FactTableRunNotice = {
        TableId = tableId
        RunId = runId
        RowCount = commit.RowCount
        New = commit.Change.New
        Changed = commit.Change.Changed
        Unchanged = commit.Change.Unchanged
        Removed = commit.Change.Removed
    }

    /// Decorate a table writer so each successful `Commit` publishes ONE
    /// `CustomNotification` keyed `FactBrowseLinks.RunCommittedNotificationKey`
    /// to the run's scope. Every other member passes straight through. A
    /// publish failure never fails the commit: the run is already durable,
    /// and the notice is a refresh hint, not a record.
    let notifyingWriter (channel: INotificationChannel) (inner: IFactTableWriter) : IFactTableWriter =
        { new IFactTableWriter with
            member _.OpenRun(scopeId, tableId) = inner.OpenRun(scopeId, tableId)
            member _.WriteRows(scopeId, runId, rows) = inner.WriteRows(scopeId, runId, rows)

            member _.Commit(scopeId, runId) = async {
                let! outcome = inner.Commit(scopeId, runId)

                match outcome with
                | Ok commit ->
                    let notice = runNotice commit.Watermark.TableId runId commit

                    try
                        let payload = JsonSerializer.Serialize(notice, noticeOptions)

                        do!
                            channel.Publish(
                                scopeId,
                                CustomNotification(FactBrowseLinks.RunCommittedNotificationKey, payload)
                            )
                    with _ ->
                        ()
                | Error _ -> ()

                return outcome
            }

            member _.Abandon(scopeId, runId, reason) = inner.Abandon(scopeId, runId, reason)
            member _.Runs(scopeId, tableId) = inner.Runs(scopeId, tableId)
            member _.Status(scopeId, tableId) = inner.Status(scopeId, tableId)
        }

    let private decorateWriter (services: IServiceCollection) : IServiceCollection =
        // The inner writer is taken from the descriptor already in the
        // collection, never from the built provider — by then
        // `IFactTableWriter` resolves to this decorator and the factory
        // would recurse (the coverage-narrative decoration's shape).
        let innerDescriptor =
            services
            |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<IFactTableWriter>)
            |> Seq.tryLast

        let inner: (IServiceProvider -> IFactTableWriter) option =
            match innerDescriptor with
            | Some descriptor when not (isNull (box descriptor.ImplementationFactory)) ->
                let factory = descriptor.ImplementationFactory
                Some(fun sp -> factory.Invoke sp :?> IFactTableWriter)
            | Some descriptor when (descriptor.ImplementationInstance :? IFactTableWriter) ->
                let instance = descriptor.ImplementationInstance :?> IFactTableWriter
                Some(fun _ -> instance)
            | _ -> None

        match inner with
        | None -> services
        | Some resolveInner ->
            services.AddSingleton<IFactTableWriter>(
                Func<IServiceProvider, IFactTableWriter>(fun sp ->
                    let writer = resolveInner sp

                    match sp.GetService(typeof<INotificationChannel>) with
                    | :? INotificationChannel as channel -> notifyingWriter channel writer
                    | _ -> writer)
            )

    /// The mounted route handler.
    let handler: Giraffe.Core.HttpHandler =
        Api.make (factBrowseApi, routeBuilder = FactBrowseApi.routeBuilder)

    /// Compose the fact browse surface: mount `IFactBrowseApi` at
    /// `/api/_facts/browse/*` and decorate the composed table writer so a
    /// committed run publishes one notification.
    ///
    /// Requires the fact store (`ServerConfig.FactStore = EnabledFactStore`,
    /// composed with `FactsCompose.withFactStore`). Call it AFTER
    /// `FactsCompose.withFactTableWriter`, so the writer it decorates is
    /// registered; without a writer the surface still mounts and every
    /// table reads as never refreshed. Under `NoFactStore` the app is
    /// returned unchanged — a composition without this call is
    /// byte-for-byte unchanged (GP 11 / GP 13).
    let withFactBrowse (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let serviceConfig =
                match app.Extensions.ServiceConfig with
                | None -> Some decorateWriter
                | Some existing -> Some(fun s -> decorateWriter (existing s))

            {
                app with
                    Extensions = {
                        app.Extensions with
                            Handlers = app.Extensions.Handlers @ [ handler ]
                            ServiceConfig = serviceConfig
                    }
            }