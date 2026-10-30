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
// run history, one run's largest movers, paged drill-down rows, one
// fact's provenance and (Phase 1113) one metric's series over a period
// range — every one a bounded answer, and every served fact judged at the
// `FactBrowse` egress door.
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
// **Judged over what was read (Phase 1125).** Each read hands the gate,
// in its held form, the facts it has just taken from the store in this
// scope and this request, so a served fact is downloaded once — never
// again at the gate. The verdicts and audit are the id form's. The series
// read goes further (Phase 1126): it answers from the metric surface's rows
// and hands the gate those rows, so a chart downloads no fact at all.
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

/// Phase 1126 — one head of a series read, from whichever form the store
/// returned it in: a fact (`Fact`), or a surface row with the head the gate
/// judges it by (`Row`). The point mapping reads only these fields, so a
/// point is the same whichever form it came from.
type internal SeriesHead = {
    FactId: string
    Subject: SubjectRef
    PeriodFrom: DateTime
    PeriodTo: DateTime
    Label: string option
    Value: FactValue
    AsOf: DateTime
    /// The fact, when the head was read as one.
    Fact: Fact option
    /// The gate's row, when the head was read as a surface row. A row whose
    /// classification does not parse has none, and is denied as an id the
    /// read holds nothing for.
    Row: HeldFactRow option
}

module internal SeriesHead =

    let ofFact (f: Fact) : SeriesHead = {
        FactId = f.FactId
        Subject = f.Subject
        PeriodFrom = f.Period.From
        PeriodTo = f.Period.To
        Label = f.Period.Label
        Value = f.Value
        AsOf = f.AsOf
        Fact = Some f
        Row = None
    }

    let ofRow (metric: string) (r: FactSurfaceRow) : SeriesHead = {
        FactId = r.Member.FactId
        Subject = r.Member.Subject
        PeriodFrom = r.Member.PeriodFrom
        PeriodTo = r.Member.PeriodTo
        Label = r.Label
        Value = r.Value
        AsOf = r.Member.AsOf
        Fact = None
        Row =
            Disclosure.tryParse r.Disclosure
            |> Option.map (fun disclosure ->
                ({
                    FactId = r.Member.FactId
                    Metric = metric
                    Disclosure = disclosure
                }
                : HeldFactRow))
    }

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
        /// The disclosure gate every served fact passes, at `FactBrowse`, in
        /// its held form (Phase 1125): each read hands the gate the facts it
        /// has just read from `Store`, so a fact is downloaded once, not
        /// again at the gate. `FactDisclosureGate.held` gives the held form
        /// of any `IFactDisclosureGate`.
        Gate: IHeldFactDisclosureGate
        /// The metric registry: display formats, staleness policies and
        /// the hierarchy levels a table sits at.
        Registry: IMetricRegistry option
        /// The clock freshness is derived against.
        Clock: unit -> DateTime
    }

    /// `inner` asked at `surface` whichever door a read names — the browse
    /// reads ask at `FactBrowse`, and a reader whose answer is a tool result
    /// (the render tools, `evaluate_pipeline`) is judged at `FactToolResult`.
    /// The row form is offered exactly when `inner` offers it, so a read keeps
    /// its one-download path and a decorator is never bypassed.
    let atSurface (surface: FactEgressSurface) (inner: IHeldFactDisclosureGate) : IHeldFactDisclosureGate =
        match inner with
        | :? IHeldFactRowDisclosureGate as rows ->
            { new IHeldFactDisclosureGate with
                member _.CheckHeld(scope, principal, _, factIds, held) =
                    inner.CheckHeld(scope, principal, surface, factIds, held)
              interface IHeldFactRowDisclosureGate with
                  member _.CheckHeldRows(scope, principal, _, factIds, held) =
                      rows.CheckHeldRows(scope, principal, surface, factIds, held)
            }
        | _ ->
            { new IHeldFactDisclosureGate with
                member _.CheckHeld(scope, principal, _, factIds, held) =
                    inner.CheckHeld(scope, principal, surface, factIds, held)
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
                RunStamp = None
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
                            RunStamp = status.LastCommit |> Option.map (fun c -> FactTableWatermark.render c.Watermark)
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

    /// One gate call over the facts a read has just taken from the store,
    /// in this scope and this request — the held form's contract. An id the
    /// gate returned nothing for is denied, so the door never fails open.
    let private judge
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (facts: Fact list)
        : Async<string -> FactDisclosureVerdict> =
        async {
            if List.isEmpty facts then
                return fun _ -> FactNotDisclosable "unknown-fact"
            else
                let factIds = facts |> List.map _.FactId |> List.distinct
                let! verdicts = deps.Gate.CheckHeld(scope, principal, FactBrowse, factIds, facts)

                return
                    fun factId ->
                        verdicts
                        |> Map.tryFind factId
                        |> Option.defaultValue (FactNotDisclosable "unknown-fact")
        }

    /// Phase 1126 — the row form of `judge`: one gate call over the rows a
    /// series read has just taken from the store's surface, in this scope
    /// and this request. An id the gate returned nothing for is denied.
    let private judgeRows
        (gate: IHeldFactRowDisclosureGate)
        (scope: ResolvedScope)
        (principal: string)
        (factIds: string list)
        (held: HeldFactRow list)
        : Async<string -> FactDisclosureVerdict> =
        async {
            if List.isEmpty factIds then
                return fun _ -> FactNotDisclosable "unknown-fact"
            else
                let! verdicts = gate.CheckHeldRows(scope, principal, FactBrowse, List.distinct factIds, held)

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

    let private freshnessOf (staleness: StalenessPolicy) (now: DateTime) (asOf: DateTime) : string =
        match Freshness.deriveAt staleness asOf true now with
        | FactFreshness.Fresh -> "fresh"
        | FactFreshness.Stale _ -> "stale"

    let private rowOf (deps: FactBrowseDeps) (now: DateTime) (rank: int) (fact: Fact) : FactRowView =
        let metric = fact.Metric |> fun (MetricRef m) -> m

        let freshness = freshnessOf (stalenessOf deps metric) now fact.AsOf

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

    // ── The seven reads ──────────────────────────────────────────

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
                let description = FactTableRegistry.tryDescription deps.Tables tableId

                let columnDescription (metric: string) =
                    description
                    |> Option.bind (fun d -> d.Columns |> List.tryFind (fun (m, _) -> m = metric) |> Option.map snd)

                return
                    Ok {
                        Listing = row
                        Columns =
                            table.Columns
                            |> List.map (fun column -> {
                                Metric = column.Metric
                                Shape = sprintf "%A" column.Shape
                                Disclosure = tableDisclosureLabel (FactTableDefinition.columnDisclosure table column)
                                Description = columnDescription column.Metric
                            })
                        ProducingOperation = table.ProducingOperation
                        SchemaVersion = table.SchemaVersion
                        RefreshCadenceSeconds = int64 table.RefreshCadence.TotalSeconds
                        Requirement = sprintf "%A" table.Requirement
                        Description = description |> Option.map _.Description
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

                            let held =
                                resolved
                                |> Array.toList
                                |> List.collect (fun (_, pair) ->
                                    match pair with
                                    | Some(c, p) -> [ c; p ]
                                    | None -> [])

                            let! verdictFor = judge deps scope principal held

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
                    let! verdictFor = judge deps scope principal result.Ranked
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
                let! verdictFor = judge deps scope principal (fact :: chain)

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

    /// The head query the series and slice reads ask the store: one
    /// metric's current heads under the table's lineage, over a subject
    /// branch at a depth, whose period overlaps `[from, to)`.
    let private headQuery
        (table: FactTableDefinition)
        (prefix: string list)
        (depth: int option)
        (metric: string)
        (from: DateTime)
        (until: DateTime)
        : FactQuery =
        {
            FactQuery.all with
                SubjectBranch =
                    Some {
                        Hierarchy = table.Hierarchy
                        PathPrefix = if List.isEmpty prefix then None else Some prefix
                        Level = depth
                    }
                Metric = Some(MetricRef metric)
                Method = Some(tableMethod table)
                PeriodOverlaps =
                    Some {
                        From = from
                        To = until
                        Label = None
                    }
        }

    /// Phase 1126 — the row form, when the store can project heads as its
    /// surface rows and the gate can judge rows.
    let private rowForm (deps: FactBrowseDeps) : (IFactSeriesRowRead * IHeldFactRowDisclosureGate) option =
        match deps.Store, deps.Gate with
        | (:? IFactSeriesRowRead as store), (:? IHeldFactRowDisclosureGate as gate) -> Some(store, gate)
        | _ -> None

    /// One metric's heads for a head query: as surface rows in the row form
    /// (one snapshot download, no fact), otherwise as facts — the same
    /// heads either way. Two heads sharing a subject and period (only two
    /// overlapping-but-unequal extents can) are one, the later `AsOf`.
    let private readHeads
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (metric: string)
        (query: FactQuery)
        : Async<SeriesHead list> =
        async {
            let! rows =
                match rowForm deps with
                | Some(store, _) -> store.QuerySeriesRows(scope, query)
                | None -> async.Return None

            let! heads = async {
                match rows with
                | Some rows -> return rows |> List.map (SeriesHead.ofRow metric)
                | None ->
                    let! facts = deps.Store.Query(scope, query)
                    return facts |> List.map SeriesHead.ofFact
            }

            return
                heads
                |> List.groupBy (fun h -> h.Subject.Path, h.PeriodFrom, h.PeriodTo)
                |> List.map (fun (_, group) -> group |> List.maxBy _.AsOf)
        }

    /// The gate over a read's heads: one row-form call over the heads read
    /// as rows, one held-form call over the heads read as facts. An id the
    /// gate returned nothing for is denied.
    let private judgeHeads
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (heads: SeriesHead list)
        : Async<string -> FactDisclosureVerdict> =
        async {
            let asRows, asFacts = heads |> List.partition (fun h -> h.Fact.IsNone)

            let! rowVerdict =
                match rowForm deps, asRows with
                | Some(_, gate), _ :: _ ->
                    judgeRows gate scope principal (asRows |> List.map _.FactId) (asRows |> List.choose _.Row)
                | _ -> async.Return(fun _ -> FactNotDisclosable "unknown-fact")

            let! factVerdict = judge deps scope principal (asFacts |> List.choose _.Fact)
            let rowIds = asRows |> List.map _.FactId |> Set.ofList

            return
                fun factId ->
                    if rowIds.Contains factId then
                        rowVerdict factId
                    else
                        factVerdict factId
        }

    /// Phase 1113 — one metric's series over a subject branch across a
    /// period range, as numbers. See the `FactBrowseApi` header for the
    /// contract; what the handler decides is recorded here:
    ///
    /// - **The read is the store's own head read**, `IFactStore.Query`
    ///   under the table's lineage (`OneMethod`-equivalent: `Method` is the
    ///   table's `tableMethod`) with `IncludeSuperseded = false`, so each
    ///   returned fact is the current head of its (subject, period)
    ///   lineage. That is the "head for that period" rule: a later run
    ///   that rewrote a cell superseded the old fact, and the head is the
    ///   revision; a period a later slice-scoped run left alone keeps the
    ///   head the earlier run wrote. A superseded fact is never served as
    ///   a point — it is on the head's chain through `GetFact`.
    /// - **What the read costs is the store's** (Phase 1122, 1123). The
    ///   hierarchy, prefix and level travel to the store as the query's
    ///   `SubjectBranch`, so on the blob store a metric-only head query
    ///   rides the metric surface and downloads the branch's heads in the
    ///   range — not the scope, and not the metric's other branches.
    /// - **A chart is one download** (Phase 1126). When the store can return
    ///   the heads as its surface rows (`IFactSeriesRowRead`) and the gate
    ///   can judge rows (`IHeldFactRowDisclosureGate`), the points are built
    ///   from the rows and judged as rows: one snapshot download, no fact.
    ///   Otherwise the heads are read as facts and judged in the held form.
    ///   Either way they are the same heads in the same order, and every
    ///   rule below applies to both.
    /// - **Period membership is overlap**, `FactQuery.PeriodOverlaps` over
    ///   `[PeriodFrom, PeriodTo)`: a period straddling the range's edge is
    ///   a point, since a chart of the window wants it, and an aligned
    ///   grain gives exactly the periods inside the window.
    /// - **Level and prefix filter the subject path**; neither aggregates.
    ///   A `SubjectLevel` the hierarchy lacks is refused; without a metric
    ///   registry the level cannot be resolved to a depth, and the depth
    ///   filter is not applied (the drill-down makes the same concession).
    /// - **Should two heads ever share a subject and period** (two
    ///   overlapping-but-unequal extents are two points; two equal ones
    ///   cannot both be heads of one lineage), the later `AsOf` is the
    ///   point. Stated so the rule is a rule and not an accident of order;
    ///   for the same reason two points sharing a subject and a start are
    ///   ordered by their end (Phase 1127), as the view slice orders rows.
    /// - **The cap is checked before the gate**, so a refusal reveals only
    ///   a count — existence-level, like every count this surface serves.
    let querySeries
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (request: FactSeriesQuery)
        : Async<Result<FactSeries, string>> =
        async {
            match declared deps request.TableId with
            | Error e -> return Error e
            | Ok table when not (List.contains request.Metric (FactTableDefinition.metrics table)) ->
                return Error(sprintf "Table '%s' has no metric column '%s'." table.Id request.Metric)
            | Ok _ when request.PeriodFrom >= request.PeriodTo ->
                return
                    Error(
                        sprintf
                            "The period range %s..%s is not a half-open [from, to) extent: from must be before to."
                            (request.PeriodFrom.ToString "o")
                            (request.PeriodTo.ToString "o")
                    )
            | Ok table ->
                let level = request.SubjectLevel |> Option.defaultValue table.Level

                let subject =
                    deps.Registry |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)

                let depth = subject |> Option.bind (fun s -> FactTableDefinition.levelDepth s level)

                match subject, depth with
                | Some _, None -> return Error(sprintf "Hierarchy '%s' has no level '%s'." table.Hierarchy level)
                | _ ->
                    let cap = FactBrowseApi.clampSeriesPoints request.MaxPoints

                    let query =
                        headQuery table request.SubjectPrefix depth request.Metric request.PeriodFrom request.PeriodTo

                    let! heads = readHeads deps scope request.Metric query

                    let points =
                        heads |> List.sortBy (fun h -> h.Subject.Path, h.PeriodFrom, h.PeriodTo)

                    if List.length points > cap then
                        return
                            Error(
                                sprintf
                                    "The series would hold %d points, over the cap of %d (FactBrowseApi.MaxSeriesPoints is %d). Narrow the period range or the subject prefix."
                                    (List.length points)
                                    cap
                                    FactBrowseApi.MaxSeriesPoints
                            )
                    else
                        let! verdictFor = judgeHeads deps scope principal points

                        let now = deps.Clock().ToUniversalTime()
                        let format = displayFormatOf deps request.Metric
                        let staleness = stalenessOf deps request.Metric

                        let pointOf (head: SeriesHead) : FactSeriesPoint =
                            let withheld = verdictFor head.FactId <> FactDisclosable
                            let freshness = freshnessOf staleness now head.AsOf

                            {
                                Subject = SubjectRef.toString head.Subject
                                SubjectPath = head.Subject.Path
                                PeriodFrom = head.PeriodFrom
                                PeriodTo = head.PeriodTo
                                PeriodLabel = head.Label
                                Value =
                                    match withheld, head.Value with
                                    | false, Scalar d -> Some d
                                    | _ -> None
                                Rendering =
                                    if withheld then
                                        None
                                    else
                                        Some(FactRendering.render format head.Value)
                                FactId = head.FactId
                                AsOf = head.AsOf
                                Freshness = freshness
                                Withheld = withheld
                            }

                        let withheld =
                            points
                            |> List.choose (fun f ->
                                match verdictFor f.FactId with
                                | FactDisclosable -> None
                                | FactNotDisclosable policy -> Some policy)

                        return
                            Ok {
                                TableId = table.Id
                                Metric = request.Metric
                                SubjectPrefix = request.SubjectPrefix
                                SubjectLevel = level
                                PeriodFrom = request.PeriodFrom
                                PeriodTo = request.PeriodTo
                                DisplayFormat = format
                                Points = points |> List.map pointOf
                                MaxPoints = cap
                                WithheldCount = List.length withheld
                                Withheld = withheldCounts withheld
                            }
        }

    /// Phase 1127 — one view slice. See the `FactBrowseApi` header for the
    /// contract; what the handler decides is recorded here:
    ///
    /// - **The read is the series read, once per metric.** Each metric's
    ///   heads come from the same head query `QuerySeries` asks
    ///   (`headQuery`): the table's lineage, the branch at the table's level,
    ///   the period overlap. On the blob store with the row-form gate that is
    ///   one surface download per metric and no fact; otherwise the heads
    ///   are read as facts, exactly as the series read falls back.
    /// - **The run stamp is read first**, before any head, so a run that
    ///   commits during the read leaves a stamp OLDER than the data: the next
    ///   check sees it move and the slice is read again. The other order
    ///   could hand a client a stamp newer than its data, which nothing
    ///   would ever correct.
    /// - **The cap is checked before the gate**, over every metric's heads,
    ///   so a refusal reveals only a count.
    /// - **The ranking positions are the drill-down's.** Each metric's
    ///   comparable heads are ranked over the whole slice by
    ///   `PopulationRanking.rankBy` — the population read's own order, value
    ///   then fact id — in both directions, withheld heads included. A
    ///   drill-down's ranking under any prefix inside the slice is that order
    ///   restricted to the prefix, so a client numbers it without a value it
    ///   was not shown.
    let querySlice
        (deps: FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (request: FactSliceQuery)
        : Async<Result<FactSlice, string>> =
        async {
            match declared deps request.TableId with
            | Error e -> return Error e
            | Ok table ->
                let tableMetrics = FactTableDefinition.metrics table

                let metrics =
                    if List.isEmpty request.Metrics then
                        tableMetrics
                    else
                        tableMetrics |> List.filter (fun m -> List.contains m request.Metrics)

                match request.Metrics |> List.tryFind (fun m -> not (List.contains m tableMetrics)) with
                | Some missing -> return Error(sprintf "Table '%s' has no metric column '%s'." table.Id missing)
                | None when request.PeriodFrom >= request.PeriodTo ->
                    return
                        Error(
                            sprintf
                                "The period range %s..%s is not a half-open [from, to) extent: from must be before to."
                                (request.PeriodFrom.ToString "o")
                                (request.PeriodTo.ToString "o")
                        )
                | None ->
                    let depth =
                        deps.Registry
                        |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)
                        |> Option.bind (fun subject -> FactTableDefinition.levelDepth subject table.Level)

                    let! listingRow = listing deps scope.ScopeId table

                    let! perMetric =
                        metrics
                        |> List.map (fun metric -> async {
                            let query =
                                headQuery table request.SubjectPrefix depth metric request.PeriodFrom request.PeriodTo

                            let! heads = readHeads deps scope metric query
                            return metric, heads
                        })
                        |> Async.Sequential

                    let cellCount = perMetric |> Array.sumBy (snd >> List.length)

                    if cellCount > FactBrowseApi.MaxSliceCells then
                        return
                            Error(
                                sprintf
                                    "The slice would hold %d cells, over the cap of %d (FactBrowseApi.MaxSliceCells). Narrow the period range, the subject prefix or the metric set."
                                    cellCount
                                    FactBrowseApi.MaxSliceCells
                            )
                    else
                        let! verdictFor = judgeHeads deps scope principal (perMetric |> Seq.collect snd |> List.ofSeq)

                        let now = deps.Clock().ToUniversalTime()

                        let cellsOf (metric: string, heads: SeriesHead list) =
                            let format = displayFormatOf deps metric
                            let staleness = stalenessOf deps metric

                            let positions (direction: RankDirection) =
                                heads
                                |> PopulationRanking.rankBy
                                    direction
                                    (fun h -> PopulationValue.comparable h.Value)
                                    _.FactId
                                |> List.mapi (fun i h -> h.FactId, i + 1)
                                |> Map.ofList

                            let descending = positions HighestFirst
                            let ascending = positions LowestFirst

                            heads
                            |> List.map (fun h ->
                                let policy =
                                    match verdictFor h.FactId with
                                    | FactDisclosable -> None
                                    | FactNotDisclosable policy -> Some policy

                                {
                                    Metric = metric
                                    FactId = h.FactId
                                    Subject = SubjectRef.toString h.Subject
                                    SubjectPath = h.Subject.Path
                                    PeriodFrom = h.PeriodFrom
                                    PeriodTo = h.PeriodTo
                                    PeriodLabel = h.Label
                                    Value =
                                        match policy, h.Value with
                                        | None, Scalar d -> Some d
                                        | _ -> None
                                    Rendering =
                                        match policy with
                                        | None -> Some(FactRendering.render format h.Value)
                                        | Some _ -> None
                                    AsOf = h.AsOf
                                    Freshness = freshnessOf staleness now h.AsOf
                                    WithheldPolicy = policy
                                    RankDescending = descending.TryFind h.FactId
                                    RankAscending = ascending.TryFind h.FactId
                                }
                                : FactSliceCell)

                        let cells = perMetric |> Seq.collect cellsOf |> List.ofSeq
                        let withheld = cells |> List.choose _.WithheldPolicy

                        match FactBrowseSlice.build metrics cells |> Result.bind FactBrowseSlice.encode with
                        | Error e -> return Error e
                        | Ok encoded ->
                            return
                                Ok {
                                    TableId = table.Id
                                    Hierarchy = table.Hierarchy
                                    Level = table.Level
                                    Method = Fact.methodIdentity (tableMethod table)
                                    PeriodFrom = request.PeriodFrom
                                    PeriodTo = request.PeriodTo
                                    SubjectPrefix = request.SubjectPrefix
                                    Metrics =
                                        metrics
                                        |> List.map (fun m -> {
                                            Metric = m
                                            DisplayFormat = displayFormatOf deps m
                                        })
                                    Table = encoded
                                    RunStamp = listingRow.RunStamp
                                    CellCount = cellCount
                                    MaxCells = FactBrowseApi.MaxSliceCells
                                    RankCeiling = PopulationQuery.MaxTopK
                                    WithheldCount = List.length withheld
                                    Withheld = withheldCounts withheld
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
        QuerySeries = querySeries deps scope principal
        QuerySlice = querySlice deps scope principal
    }

    // ── HttpContext adapter ──────────────────────────────────────

    let private serviceOf<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
        ServiceComposition.tryService<'T> ctx.RequestServices

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
            QuerySeries = fun _ -> refusal ()
            QuerySlice = fun _ -> refusal ()
        }

    /// The browse dependencies the request's composition holds, or `None`
    /// when the fact store or its disclosure gate is not composed. Phase
    /// 1132 — public, so another in-process reader of the fact tables (the
    /// render tools) takes them as a value from this one resolution rather
    /// than resolving the container itself.
    let factBrowseDeps (ctx: HttpContext) : FactBrowseDeps option =
        match serviceOf<IFactStore> ctx, serviceOf<IFactDisclosureGate> ctx with
        | Some store, Some gate ->
            Some {
                Tables = serviceOf<IFactTableRegistry> ctx |> Option.defaultValue FactTableRegistry.empty
                Writer = serviceOf<IFactTableWriter> ctx
                Store = store
                // Phase 1125 — the composed held form (`FactsCompose`
                // registers it over the composed gate); a deployment that
                // registered only a gate gets that gate's held form.
                Gate =
                    serviceOf<IHeldFactDisclosureGate> ctx
                    |> Option.defaultWith (fun () -> FactDisclosureGate.held gate)
                Registry = serviceOf<IMetricRegistry> ctx
                Clock = fun () -> DateTime.UtcNow
            }
        | _ -> None

    /// The per-request API. The scope is the one the platform's scope
    /// resolution minted for this request (`ScopeResolution.forRequest`),
    /// the principal the request's user.
    let factBrowseApi (ctx: HttpContext) : IFactBrowseApi =
        match factBrowseDeps ctx with
        | Some deps -> api deps (StorageScopeResolver.ScopeResolution.forRequest ctx) (userIdOf ctx)
        | None -> unavailable

    // ── Per-run notification ─────────────────────────────────────

    let private noticeOptions =
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.shared

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
    /// to the run's scope. Every other member passes straight through — a
    /// run's provenance and options among them, untouched (Phases 938 and
    /// 994), and the orphan sweep (Phase 977). A publish failure
    /// never fails the commit: the run is already durable, and the notice is
    /// a refresh hint, not a record.
    let notifyingWriter (channel: INotificationChannel) (inner: IFactTableWriter) : IFactTableWriter =
        { new IFactTableWriter with
            member _.OpenRun(scopeId, tableId, ?provenance, ?options) =
                inner.OpenRun(scopeId, tableId, ?provenance = provenance, ?options = options)

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
          // Phase 977 — the orphan sweep passes through, so the decorated
          // writer still reclaims its ended runs' leftovers.
          interface IFactTableOrphanSweep with
              member _.SweepOrphans(scopeId) =
                  FactTableOrphanSweep.sweep inner scopeId
        }

    let private decorateWriter (services: IServiceCollection) : IServiceCollection =
        // The inner writer is taken from the descriptor already in the
        // collection, never from the built provider — by then
        // `IFactTableWriter` resolves to this decorator and the factory
        // would recurse (the coverage-narrative decoration's shape).
        match
            ServiceComposition.captureDescriptor<IFactTableWriter> services
            |> Option.map _.Resolve
        with
        | None -> services
        | Some resolveInner ->
            services.AddSingleton<IFactTableWriter>(
                Func<IServiceProvider, IFactTableWriter>(fun sp ->
                    let writer = resolveInner sp

                    match ServiceComposition.tryService<INotificationChannel> sp with
                    | Some channel -> notifyingWriter channel writer
                    | None -> writer)
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
    /// **Adds** one handler, and the writer decoration through `ServerApp.addServiceConfig` (a later `withExtensions` keeps it).
    let withFactBrowse (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            {
                app with
                    Extensions.Handlers = app.Extensions.Handlers @ [ handler ]
            }
            |> ServerApp.addServiceConfig decorateWriter