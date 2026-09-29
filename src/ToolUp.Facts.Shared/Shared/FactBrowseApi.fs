// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform

// ─── IFactBrowseApi (Phase 895) ──────────────────────────────────────
//
// The client-callable read surface over declared fact tables. A person
// sees what was PUBLISHED — tables and the runs that refreshed them —
// and can look any subject up, but the client never receives a
// population: three levels, summary first.
//
//   1. **Tables** — one row per declared table (tens of rows).
//   2. **Table detail** — the table's run history, one row per refresh,
//      paged on the server, each with its change summary; the largest
//      movers of one run on request.
//   3. **Drill-down** — rows of one table ranked by one metric column,
//      top or bottom, optionally under a subject path prefix, paged on
//      the server; and one fact with its provenance chain.
//
// **One query surface.** The drill-down is answered by the same
// population read and the same disclosure fold the assistant's
// population tool runs, so a user and the model asking one question
// get one ranking. A page is a window onto that ranking.
//
// **Bounded by contract.** `FactBrowseApi.MaxPageSize` is the page
// ceiling and every page reports it; a request over it is clamped, never
// refused. The deepest reachable rank is the population read's own
// ceiling, reported as `RankCeiling`. Bulk extraction is not offered
// here — it is an export through the gated reporting path.
//
// **Its own egress door.** Every served fact passes the disclosure gate
// at the `FactBrowse` surface. A row the viewer may not see is absent;
// the page reports how many were withheld, grouped by policy, and never
// a value, a subject or an id.
//
// **Every view is a projection**, as the composition inspector's are:
// the source types live in tiers the Fable client cannot compile, so the
// records here carry strings, counts and rendered values. A kind that
// may grow (a run's status, a freshness state) travels as a string, so a
// client one release behind renders an unfamiliar value rather than
// failing to decode the page.

/// One declared fact table, as the tables page lists it.
type FactTableListing = {
    /// The table's declared, stable id.
    TableId: string
    /// The module that declared the table.
    Module: string
    /// The metric id of every declared column, in column order.
    Metrics: string list
    /// The subject hierarchy the table's rows sit in.
    Hierarchy: string
    /// The hierarchy level every row sits at.
    Level: string
    /// The declared period grain (`Day` … `Year`, `Snapshot`).
    PeriodGrain: string
    /// The declared history mode (`AppendByRun` / `Replace`).
    HistoryMode: string
    /// Rows the table's current run committed. `None` until the table has
    /// committed a run.
    RowCount: int option
    /// When the current run committed. `None` until the table has
    /// committed a run.
    LastRefreshedAt: DateTime option
    /// `never-refreshed`, `fresh` or `stale`, against the declared
    /// refresh cadence.
    Freshness: string
    /// How far past its cadence a stale table is, in whole seconds.
    /// `None` unless `Freshness` is `stale`.
    OverdueBySeconds: int64 option
    /// The latest run's derived outcome (`in-progress`, `succeeded`,
    /// `failed`, `discarded`). `None` when the table has never run.
    LatestRunOutcome: string option
    /// Why the latest run failed or was discarded, when it was.
    LatestRunReason: string option
}

/// One declared column of a table.
type FactTableColumnView = {
    /// The column's metric id.
    Metric: string
    /// The declared value shape (`Scalar`, `Interval`, …).
    Shape: string
    /// The column's effective disclosure classification — the policy a
    /// row's value is judged against, never a value.
    Disclosure: string
}

/// One table's declaration, beside its listing row.
type FactTableDetail = {
    /// The listing row the tables page shows for the table.
    Listing: FactTableListing
    /// The declared columns, in column order.
    Columns: FactTableColumnView list
    /// The operation that produces the table.
    ProducingOperation: string
    /// The declared schema version.
    SchemaVersion: int
    /// The declared refresh cadence, in whole seconds.
    RefreshCadenceSeconds: int64
    /// `Required` or `Optional`.
    Requirement: string
}

/// A run's change counts against the run before it. Counts are
/// existence-level and always ride; the movers are values and are served
/// only through `GetRunMovers`.
type FactRunChangeView = {
    /// Rows the previous run did not carry.
    New: int
    /// Rows whose cells moved.
    Changed: int
    /// Rows whose cells did not move.
    Unchanged: int
    /// Rows the previous run carried and this one does not.
    Removed: int
    /// How many largest-mover entries the run recorded (at most the
    /// writer's cap). Their values are fetched separately.
    MoverCount: int
}

/// One refresh of a table.
type FactTableRunView = {
    /// The run's id.
    RunId: string
    /// When the run was opened.
    OpenedAt: DateTime
    /// The run record's state: `open`, `committed`, `rejected`,
    /// `abandoned`.
    Status: string
    /// The derived outcome: `in-progress`, `succeeded`, `failed`,
    /// `discarded`. For a `Required` table an abandoned run reads as
    /// failed, and an open run past its cadence as failed.
    Outcome: string
    /// Why a run was rejected, abandoned or failed. Names rows and
    /// policies, never a value.
    Reason: string option
    /// Rows staged on the run.
    StagedRows: int
    /// Rows the run committed. `None` unless it committed.
    RowsWritten: int option
    /// Facts the run wrote. `None` unless it committed.
    FactsWritten: int option
    /// The commit sequence. `None` unless it committed.
    Sequence: int64 option
    /// When the run committed. `None` unless it committed.
    CommittedAt: DateTime option
    /// The change against the previous run. `None` unless it committed.
    Change: FactRunChangeView option
}

/// One page of a table's run history, most recently opened first.
type FactRunPage = {
    /// The table the runs belong to.
    TableId: string
    /// The runs on this page.
    Runs: FactTableRunView list
    /// The 0-based page index served.
    Page: int
    /// The page size applied, after the `MaxPageSize` clamp.
    PageSize: int
    /// The page ceiling in force (`FactBrowseApi.MaxPageSize`).
    MaxPageSize: int
    /// How many runs the table has in all.
    TotalRuns: int
}

/// One of a run's largest movers that the viewer may see.
type FactMoverView = {
    /// The column's metric id.
    Metric: string
    /// The readable subject (`hierarchy/member>member`).
    Subject: string
    /// The readable period.
    Period: string
    /// The previous value, rendered.
    Previous: string
    /// The run's value, rendered.
    Current: string
    /// The change, rendered.
    Delta: string
}

/// How many served items were withheld under one policy.
type FactWithheldCount = {
    /// The policy that withheld them — never a value.
    PolicyRef: string
    /// How many.
    Count: int
}

/// One run's largest movers, each passed through the disclosure gate.
type FactRunMovers = {
    /// The table.
    TableId: string
    /// The run.
    RunId: string
    /// The movers the viewer may see, largest first.
    Movers: FactMoverView list
    /// Movers withheld from this viewer, in all.
    WithheldCount: int
    /// The withheld movers grouped by policy.
    Withheld: FactWithheldCount list
}

/// Which end of a metric's ranking a drill-down page is drawn from.
type FactRowDirection =
    /// Largest values first.
    | TopOfRanking
    /// Smallest values first.
    | BottomOfRanking

/// A drill-down request: one table, one of its metric columns, one end of
/// the ranking, an optional subject prefix, one page.
type FactRowQuery = {
    /// The table to read.
    TableId: string
    /// The metric column to rank by. Must be one of the table's columns.
    Metric: string
    /// Which end of the ranking.
    Direction: FactRowDirection
    /// Subject path prefix (member ids from the hierarchy root). Empty
    /// admits every subject of the table.
    SubjectPrefix: string list
    /// The 0-based page index.
    Page: int
    /// Rows per page. Clamped into `[1, FactBrowseApi.MaxPageSize]`.
    PageSize: int
}

/// One ranked row the viewer may see.
type FactRowView = {
    /// The row's true rank in the ranking (1-based). A withheld row keeps
    /// its rank, so the row below it is never promoted. `0` for a fact
    /// opened by id rather than from a ranking.
    Rank: int
    /// The fact's id — what opens its provenance.
    FactId: string
    /// The readable subject (`hierarchy/member>member`).
    Subject: string
    /// The subject's member ids from the hierarchy root.
    SubjectPath: string list
    /// The ranked metric's value, rendered with the metric's display
    /// format.
    Rendering: string
    /// Start of the period the value describes (inclusive).
    PeriodFrom: DateTime
    /// End of the period (exclusive).
    PeriodTo: DateTime
    /// The period's label, when it has one.
    PeriodLabel: string option
    /// When the fact was asserted.
    AsOf: DateTime
    /// `fresh` or `stale`, under the metric's staleness policy.
    Freshness: string
}

/// A method identity and how many facts of a population it produced.
type FactMethodCount = {
    /// The method identity.
    Method: string
    /// How many facts.
    FactCount: int
}

/// The population summary of one metric of one table — the population
/// read's statistics after the disclosure fold. Counts and coverage are
/// existence-level and always ride; the magnitudes are withheld whenever
/// any ranked member was, because a minimum or a maximum IS some member's
/// value.
type FactPopulationView = {
    /// Distinct subjects matched.
    SubjectCount: int
    /// Facts matched.
    FactCount: int
    /// Facts with a rankable value.
    ComparableCount: int
    /// Facts without one.
    NonComparableCount: int
    /// Earliest period start seen.
    PeriodFrom: DateTime option
    /// Latest period end seen.
    PeriodTo: DateTime option
    /// Facts fresh under the metric's staleness policy.
    FreshCount: int
    /// Facts stale under it.
    StaleCount: int
    /// The facts per method identity.
    Methods: FactMethodCount list
    /// Rendered minimum. `None` when withheld or absent.
    Minimum: string option
    /// Rendered maximum. `None` when withheld or absent.
    Maximum: string option
    /// Rendered mean. `None` when withheld or absent.
    Mean: string option
    /// Whether the magnitudes were withheld from this viewer.
    ValueStatisticsWithheld: bool
}

/// One page of a drill-down.
type FactRowPage = {
    /// The table read.
    TableId: string
    /// The metric ranked by.
    Metric: string
    /// The end of the ranking read.
    Direction: FactRowDirection
    /// The subject prefix applied.
    SubjectPrefix: string list
    /// The 0-based page index served.
    Page: int
    /// The page size applied, after the `MaxPageSize` clamp.
    PageSize: int
    /// The page ceiling in force (`FactBrowseApi.MaxPageSize`).
    MaxPageSize: int
    /// The deepest rank any page can reach — the population read's own
    /// ceiling. Past it, the ranking is an export's business.
    RankCeiling: int
    /// The rows of this page the viewer may see, in rank order.
    Rows: FactRowView list
    /// Rows of this page withheld from the viewer.
    WithheldCount: int
    /// This page's withheld rows grouped by policy.
    Withheld: FactWithheldCount list
    /// Whether a further page holds ranked rows.
    HasMore: bool
    /// Whether the ranking continues past `RankCeiling`.
    ReachedRankCeiling: bool
    /// The population summary for the same question.
    Population: FactPopulationView
}

/// One step of a fact's supersession chain the viewer may see.
type FactChainEntry = {
    /// The fact's id.
    FactId: string
    /// Its value, rendered.
    Rendering: string
    /// When it was asserted.
    AsOf: DateTime
    /// The method identity that produced it.
    Method: string
    /// The fact it superseded, if any.
    Supersedes: string option
}

/// One fact, opened from a row or a citation: the row it belongs to and
/// its provenance chain.
type FactDetailView = {
    /// The fact as a row.
    Row: FactRowView
    /// The metric.
    Metric: string
    /// The fact's disclosure classification.
    Disclosure: string
    /// The method identity that produced it.
    Method: string
    /// The table that produced it, when a declared table's run did.
    TableId: string option
    /// The run that produced it, when a declared table's run did.
    RunId: string option
    /// The analysis-result / watermark reference the fact was computed
    /// from.
    ResultRef: string option
    /// Content hashes of the inputs.
    InputHashes: string list
    /// What caused the fact to be computed.
    TriggerRef: string option
    /// The supersession chain, newest first, restricted to the steps the
    /// viewer may see.
    Chain: FactChainEntry list
    /// Chain steps withheld from the viewer.
    ChainWithheldCount: int
}

/// The payload of the one notification a committed fact-table run
/// publishes (`FactBrowseLinks.RunCommittedNotificationKey`) to the run's
/// scope. Counts only — never a value, a subject or a fact id — and one
/// per run, never one per fact.
type FactTableRunNotice = {
    /// The table the run refreshed.
    TableId: string
    /// The run.
    RunId: string
    /// Rows the run committed.
    RowCount: int
    /// Rows the previous run did not carry.
    New: int
    /// Rows whose cells moved.
    Changed: int
    /// Rows whose cells did not move.
    Unchanged: int
    /// Rows the previous run carried and this one does not.
    Removed: int
}

/// The fact browse read surface (Phase 895). Read-only by construction:
/// no method writes, and none returns a population — every list is a
/// page under `FactBrowseApi.MaxPageSize`, or the declared tables.
///
/// Scope: the caller's resolved scope, as every request-path door reads
/// it. Every served fact passes the disclosure gate at the `FactBrowse`
/// surface.
type IFactBrowseApi = {
    /// Every declared table, with its standing.
    [<RequiresClaim "scope">]
    ListTables: unit -> Async<Result<FactTableListing list, string>>
    /// One table's declaration and standing.
    [<RequiresClaim "scope">]
    GetTable: string -> Async<Result<FactTableDetail, string>>
    /// One page of a table's run history. Arguments: table id, 0-based
    /// page, page size (clamped).
    [<RequiresClaim "scope">]
    ListRuns: string * int * int -> Async<Result<FactRunPage, string>>
    /// One committed run's largest movers, gated. Arguments: table id,
    /// run id.
    [<RequiresClaim "scope">]
    GetRunMovers: string * string -> Async<Result<FactRunMovers, string>>
    /// One page of a drill-down.
    [<RequiresClaim "scope">]
    QueryRows: FactRowQuery -> Async<Result<FactRowPage, string>>
    /// One fact and its provenance chain, by id.
    [<RequiresClaim "scope">]
    GetFact: string -> Async<Result<FactDetailView, string>>
}

/// Constants and helpers of the fact browse contract.
module FactBrowseApi =

    /// The page ceiling — the most rows or runs any one response carries.
    /// Part of the contract: every page reports it.
    [<Literal>]
    let MaxPageSize = 50

    /// The page size a client asks for when it has no preference.
    [<Literal>]
    let DefaultPageSize = 25

    /// A requested page size clamped into `[1, MaxPageSize]`.
    let clampPageSize (requested: int) : int =
        if requested < 1 then 1
        elif requested > MaxPageSize then MaxPageSize
        else requested

    /// A requested page index clamped to be non-negative.
    let clampPage (requested: int) : int = max 0 requested

    /// ToolUp.Remoting route builder: `/api/_facts/browse/<method>`.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_facts/browse/%s" methodName

    /// Where bulk extraction lives, stated once so the drill-down's empty
    /// state and the documentation say the same thing.
    [<Literal>]
    let BulkExtractionNote =
        "Browsing shows one page at a time. To take a whole table out of the deployment, export it through the reporting path, where the same disclosure gate applies."