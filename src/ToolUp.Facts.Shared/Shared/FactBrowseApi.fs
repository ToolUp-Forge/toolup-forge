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
    /// Phase 1127 — the current run's stamp (`<tableId>@<sequence>:<digest
    /// prefix>`), the token a view slice records as the latest run folded
    /// into it. A slice whose stamp differs from the listing's is stale.
    /// `None` until the table has committed a run.
    RunStamp: string option
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

// ─── The series read (Phase 1113) ────────────────────────────────────
//
// A chart needs a shape the ranked drill-down does not serve: one
// metric's value for each subject in a branch at each period across a
// range, as a NUMBER. `FactRowView` carries the value only rendered, and
// `FactRowQuery` has no period range. The series read is that shape and
// nothing more — forge serves the data; the consumer draws the chart in
// whatever UI tier it uses.
//
// **One fact per point.** A point is the fact the store holds as the
// current head for that subject and period — the head for THAT period,
// not the table's latest run's head — so a figure revised by a later run
// shows its revised value, and a period an earlier run wrote and a later
// slice-scoped run left alone keeps the earlier value. A superseded fact
// is never a point: it is reachable through `GetFact` on the head's
// provenance chain, as every superseded fact is.
//
// **Under the same gate.** Every point passes the disclosure gate at
// `FactBrowse` exactly as a drill-down row does. A withheld point keeps
// its subject, its period and its fact id, and carries no value and no
// rendering, marked withheld — so a chart draws a gap rather than a
// silently shorter line. The result says how many were withheld, grouped
// by policy. It carries no aggregate (no minimum, maximum or mean), so
// there is no magnitude to reconstruct from what remains.
//
// **Bounded.** `FactBrowseApi.MaxSeriesPoints` is the most points any
// one response carries, server-enforced; a range that would exceed it is
// refused naming the cap, never silently truncated or paged — the caller
// narrows the range or the prefix.

/// A series request: one table, one of its metric columns, the subjects
/// under a path prefix at one hierarchy level, across a half-open period
/// range.
type FactSeriesQuery = {
    /// The table to read.
    TableId: string
    /// The metric column to plot. Must be one of the table's columns.
    Metric: string
    /// Subject path prefix (member ids from the hierarchy root). Empty
    /// admits every subject of the table.
    SubjectPrefix: string list
    /// The hierarchy level the points sit at, by level name. `None` is the
    /// table's declared level. A filter on the subject path's depth, never
    /// an aggregation: a level with no published facts yields no points.
    SubjectLevel: string option
    /// Start of the period range (inclusive). A point is in range when its
    /// period overlaps `[PeriodFrom, PeriodTo)`.
    PeriodFrom: DateTime
    /// End of the period range (exclusive).
    PeriodTo: DateTime
    /// The most points the caller wants. Clamped into
    /// `[1, FactBrowseApi.MaxSeriesPoints]`; a series that would exceed
    /// the clamped value is refused, naming it.
    MaxPoints: int
}

/// One point of a series: the head fact for one subject and one period.
type FactSeriesPoint = {
    /// The readable subject (`hierarchy/member>member`).
    Subject: string
    /// The subject's member ids from the hierarchy root.
    SubjectPath: string list
    /// Start of the period the value describes (inclusive).
    PeriodFrom: DateTime
    /// End of the period (exclusive).
    PeriodTo: DateTime
    /// The period's label, when it has one.
    PeriodLabel: string option
    /// The numeric value. `None` when the point is withheld, or when the
    /// fact's value is not a scalar (an absence, an interval).
    Value: decimal option
    /// The value rendered with the metric's display format. `None` when
    /// the point is withheld.
    Rendering: string option
    /// The fact's id — what opens its provenance through `GetFact`.
    FactId: string
    /// When the fact was asserted.
    AsOf: DateTime
    /// `fresh` or `stale`, under the metric's staleness policy.
    Freshness: string
    /// Whether the disclosure gate withheld this point from the viewer.
    Withheld: bool
}

/// A series: every point of one metric over a subject branch across a
/// period range, ordered by subject then period, so a client can draw
/// lines without sorting.
type FactSeries = {
    /// The table read.
    TableId: string
    /// The metric plotted.
    Metric: string
    /// The subject prefix applied.
    SubjectPrefix: string list
    /// The hierarchy level the points sit at.
    SubjectLevel: string
    /// The period range applied (inclusive start).
    PeriodFrom: DateTime
    /// The period range applied (exclusive end).
    PeriodTo: DateTime
    /// The metric's display format, so a client can render a derived
    /// figure the way the facts are rendered. `""` renders verbatim.
    DisplayFormat: string
    /// The points, ordered by subject path then period start. A withheld
    /// point keeps its place.
    Points: FactSeriesPoint list
    /// The point cap in force for this call, after the clamp.
    MaxPoints: int
    /// Points withheld from the viewer.
    WithheldCount: int
    /// The withheld points grouped by policy.
    Withheld: FactWithheldCount list
}

// ─── The view slice (Phase 1127) ─────────────────────────────────────
//
// A view reads ONCE and refines locally. Facts change when a run lands,
// not while a person explores, so a view asks for one bounded slice of a
// table — a period range, an optional subject branch, a metric set — and
// answers every sort, filter, page, regroup and chart inside it on the
// client, as `Fuaran.Compute.DataFrame` pipelines over the slice
// (`FactBrowseSlice`). It reads again only when it leaves the slice or the
// slice's run stamp moves.
//
// **The slice is a `Fuaran.Core.Column` table in its canonical codec**,
// carried as text, so the wire format is the columnar strand's and any
// columnar consumer can bind it without an adapter. Its layout is
// `FactBrowseSlice`'s, stated there column by column.
//
// **Under the same gate.** Every cell passes the disclosure gate at
// `FactBrowse` before the slice is encoded. A withheld cell keeps its
// subject, period, fact id, freshness and ranking position — the
// existence-level facts the series and drill-down reads already serve —
// and carries no value and no rendering. Nothing withheld is encoded.
//
// **Bounded.** `FactBrowseApi.MaxSliceCells` is the most cells (one head
// per subject, period and metric) a slice carries. The count is checked
// before the gate, and a slice over it is refused naming the cap.

/// A slice request: one table, a half-open period range, an optional
/// subject branch, and a metric set.
type FactSliceQuery = {
    /// The table to read.
    TableId: string
    /// Start of the period range (inclusive). A head is in the slice when
    /// its period overlaps `[PeriodFrom, PeriodTo)`.
    PeriodFrom: DateTime
    /// End of the period range (exclusive).
    PeriodTo: DateTime
    /// Subject path prefix (member ids from the hierarchy root). Empty
    /// admits every subject of the table.
    SubjectPrefix: string list
    /// The metric columns to carry. Empty carries every column of the
    /// table; otherwise each must be one of the table's columns.
    Metrics: string list
}

/// One metric a slice carries.
type FactSliceMetric = {
    /// The metric id — also the name of its value column.
    Metric: string
    /// The metric's display format, so a client renders a derived figure
    /// the way the facts are rendered. `""` renders verbatim.
    DisplayFormat: string
}

/// One slice: the table's heads in the requested range as one columnar
/// table, with what a client needs to refine it locally.
type FactSlice = {
    /// The table read.
    TableId: string
    /// The hierarchy the table's subjects sit in.
    Hierarchy: string
    /// The hierarchy level every row sits at.
    Level: string
    /// The method identity every head carries — the table's own lineage.
    Method: string
    /// The period range applied (inclusive start).
    PeriodFrom: DateTime
    /// The period range applied (exclusive end).
    PeriodTo: DateTime
    /// The subject prefix applied.
    SubjectPrefix: string list
    /// The metrics carried, in the table's column order.
    Metrics: FactSliceMetric list
    /// The slice as a `Fuaran.Core.Column` table in its canonical codec
    /// (`ColumnCodec.encode`); decode with `FactBrowseSlice.decode`.
    Table: string
    /// The stamp of the latest run folded into the slice
    /// (`FactTableListing.RunStamp` when it was read). `None` when the table
    /// had committed no run.
    RunStamp: string option
    /// The cells carried (heads across every metric, withheld included).
    CellCount: int
    /// The cell cap in force (`FactBrowseApi.MaxSliceCells`).
    MaxCells: int
    /// The drill-down's rank ceiling, so a local page answers exactly as
    /// the server's `QueryRows` would.
    RankCeiling: int
    /// Cells withheld from the viewer.
    WithheldCount: int
    /// The withheld cells grouped by policy.
    Withheld: FactWithheldCount list
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
    /// One metric's series over a subject branch across a period range
    /// (Phase 1113) — numbers, for a chart drawn elsewhere. Refused, naming
    /// the cap, when the series would exceed `FactBrowseApi.MaxSeriesPoints`.
    [<RequiresClaim "scope">]
    QuerySeries: FactSeriesQuery -> Async<Result<FactSeries, string>>
    /// One view slice (Phase 1127) — a table's heads over a period range
    /// and subject branch as one columnar table, gated before it is
    /// encoded, which a client refines locally (`FactBrowseSlice`). Refused,
    /// naming the cap, when it would exceed `FactBrowseApi.MaxSliceCells`.
    [<RequiresClaim "scope">]
    QuerySlice: FactSliceQuery -> Async<Result<FactSlice, string>>
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

    /// Phase 1113 — the most points one series response carries. Part of
    /// the contract: a series that would exceed it is refused by name, and
    /// every series reports the cap in force. Sized for a chart, not an
    /// export — a fortnight by day across a hundred subjects is 1,400
    /// points; a year by day across ten is 3,650.
    [<Literal>]
    let MaxSeriesPoints = 5000

    /// A requested point cap clamped into `[1, MaxSeriesPoints]`.
    let clampSeriesPoints (requested: int) : int =
        if requested < 1 then 1
        elif requested > MaxSeriesPoints then MaxSeriesPoints
        else requested

    /// Phase 1127 — the most cells (one head per subject, period and
    /// metric, withheld included) one slice carries. Part of the contract: a
    /// slice that would exceed it is refused by name before the gate, and
    /// every slice reports it. Sized by measurement (the Phase 1127 probe):
    /// at the cap a single-metric slice encodes to about six megabytes and
    /// decodes and prepares in about 0.3 s on .NET and about 1 s under Fable
    /// (Node), with a page over it about 0.25 s there; a year by day
    /// across sixty-eight subjects at one metric fits (24,820 cells). A larger
    /// table reads page by page instead.
    [<Literal>]
    let MaxSliceCells = 25000

    /// The period range a drill-down slice asks for: every period. The
    /// drill-down ranks a table's heads across all its periods, so only a
    /// slice over this range can answer it.
    let AllPeriodsFrom = DateTime.MinValue

    /// The exclusive end of `AllPeriodsFrom`'s range.
    let AllPeriodsTo = DateTime.MaxValue

    /// ToolUp.Remoting route builder: `/api/_facts/browse/<method>`.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_facts/browse/%s" methodName

    /// Where bulk extraction lives, stated once so the drill-down's empty
    /// state and the documentation say the same thing.
    [<Literal>]
    let BulkExtractionNote =
        "Browsing shows one page at a time. To take a whole table out of the deployment, export it through the reporting path, where the same disclosure gate applies."

/// One head a slice carries, as the server hands it to the encoder after
/// the gate: the cell's identity and position always, its value only when
/// the gate admitted it.
type FactSliceCell = {
    /// The metric the head is a fact of.
    Metric: string
    /// The fact's id.
    FactId: string
    /// The readable subject (`hierarchy/member>member`).
    Subject: string
    /// The subject's member ids from the hierarchy root.
    SubjectPath: string list
    /// Start of the period (inclusive).
    PeriodFrom: DateTime
    /// End of the period (exclusive).
    PeriodTo: DateTime
    /// The period's label, when it has one.
    PeriodLabel: string option
    /// The scalar value. `None` when withheld, or when the value is not a
    /// scalar.
    Value: decimal option
    /// The value rendered with the metric's display format. `None` when
    /// withheld.
    Rendering: string option
    /// When the fact was asserted.
    AsOf: DateTime
    /// `fresh` or `stale`.
    Freshness: string
    /// The policy that withheld the cell, when the gate did.
    WithheldPolicy: string option
    /// The head's 1-based position in its metric's ranking, largest first
    /// (ties by fact id) — the drill-down's `TopOfRanking` order over the
    /// whole slice. `None` when the value is not comparable. Existence-
    /// level, like a drill-down row's rank: a withheld head keeps it.
    RankDescending: int option
    /// The same, smallest first — the `BottomOfRanking` order.
    RankAscending: int option
}

/// Phase 1127 — the view slice's layout, its codec, and the local reads
/// over it: the drill-down page and the series as `Fuaran.Compute`
/// `Transform` pipelines evaluated over the prepared slice. Fable-safe, so
/// the client and a .NET test evaluate the same pipelines.
///
/// **Layout.** One row per (subject, period), in subject-path then period
/// order; the fixed columns, then eight per metric `m`:
///
/// | Column | Type | Holds |
/// |---|---|---|
/// | `row` | int | the row's position in that order |
/// | `subject` | string | the readable subject |
/// | `subject_path` | string | the member ids joined by `>` |
/// | `period_from` / `period_to` | timestamp (ns) | the period |
/// | `period_label` | string | its label, or null |
/// | `m` | decimal | the value, or null (withheld, non-scalar, absent) |
/// | `m/fact_id` | string | the head's fact id, or null when the row has no `m` head |
/// | `m/as_of` | timestamp (ns) | when it was asserted |
/// | `m/freshness` | string | `fresh` or `stale` |
/// | `m/rendering` | string | the rendered value, or null when withheld |
/// | `m/withheld` | string | the withholding policy, or null |
/// | `m/rank_desc` / `m/rank_asc` | int | the ranking positions, or null |
module FactBrowseSlice =

    open Fuaran.Core
    open Fuaran.Compute

    /// The row-position column.
    [<Literal>]
    let RowColumn = "row"

    /// The readable-subject column.
    [<Literal>]
    let SubjectColumn = "subject"

    /// The joined subject-path column.
    [<Literal>]
    let PathColumn = "subject_path"

    /// The period-start column.
    [<Literal>]
    let FromColumn = "period_from"

    /// The period-end column.
    [<Literal>]
    let ToColumn = "period_to"

    /// The period-label column.
    [<Literal>]
    let LabelColumn = "period_label"

    /// The name of one of metric `metric`'s columns (`""` is the value).
    let metricColumn (metric: string) (part: string) : string =
        if part = "" then metric else metric + "/" + part

    let private instantType = TimestampType TimeUnit.Nanoseconds

    // ── Instants ─────────────────────────────────────────────────

    /// An instant as the columnar strand's canonical timestamp text. The
    /// value's ticks are written as they stand, whatever its `Kind`, so the
    /// instant read back has the same ticks the server's views carry.
    let timestampText (t: DateTime) : string =
        let ticksPerSecond = 10000000L
        let fraction = t.Ticks % ticksPerSecond
        let whole = DateTime(t.Ticks - fraction)

        let head =
            sprintf
                "%04d-%02d-%02dT%02d:%02d:%02d"
                whole.Year
                whole.Month
                whole.Day
                whole.Hour
                whole.Minute
                whole.Second

        if fraction = 0L then
            head + "Z"
        else
            head + "." + (sprintf "%07d" fraction).TrimEnd '0' + "Z"

    /// The instant a canonical timestamp text names.
    let parseTimestamp (text: string) : DateTime =
        let part (start: int) (length: int) = int (text.Substring(start, length))

        let whole =
            DateTime(part 0 4, part 5 2, part 8 2, part 11 2, part 14 2, part 17 2, DateTimeKind.Utc)

        if text.Length > 20 && text.[19] = '.' then
            let digits = text.Substring(20, text.Length - 21)
            let ticks = (digits + "0000000").Substring(0, 7)
            whole.AddTicks(int64 ticks)
        else
            whole

    // ── Decimals ─────────────────────────────────────────────────

    let private decimalCell (d: decimal) : Cell =
#if FABLE_COMPILER
        let text = string d
#else
        let text = d.ToString(System.Globalization.CultureInfo.InvariantCulture)
#endif
        Cell.decimal text |> Option.defaultValue Null

    let private parseDecimal (text: string) : decimal =
#if FABLE_COMPILER
        System.Decimal.Parse text
#else
        System.Decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
#endif

    /// A figure the slice derives (the population mean) rendered with a
    /// metric's display format. On .NET it is exactly
    /// `d.ToString(format, InvariantCulture)`, the server's own rendering.
    /// Under Fable, whose decimal has no format support, the standard
    /// fixed-point specifiers `N<k>`, `F<k>` and `P<k>` (and the blank
    /// format) are rendered to the same text, rounding half away from zero
    /// as .NET does; any other format renders the decimal verbatim.
    let renderDerived (format: string) (d: decimal) : string =
#if FABLE_COMPILER
        let precision (spec: string) =
            if spec.Length = 1 then
                Some 2
            else
                match System.Int32.TryParse(spec.Substring 1) with
                | true, k when k >= 0 && k <= 20 -> Some k
                | _ -> None

        let groupThousands (digits: string) : string =
            let n = digits.Length

            digits
            |> Seq.mapi (fun i c ->
                if i > 0 && (n - i) % 3 = 0 then
                    "," + string c
                else
                    string c)
            |> String.concat ""

        let fixedPoint (grouped: bool) (k: int) (value: decimal) =
            // Half away from zero, by hand: Fable's decimal rounding ignores the
            // MidpointRounding argument and always rounds half to even.
            let scale = pown 10m k
            let scaled = abs value * scale
            let whole = System.Math.Floor scaled
            let magnitude = (if scaled - whole >= 0.5m then whole + 1m else whole) / scale

            let text = string magnitude

            let integral, fraction =
                match text.IndexOf '.' with
                | -1 -> text, ""
                | i -> text.Substring(0, i), text.Substring(i + 1)

            let integral = if grouped then groupThousands integral else integral
            let fraction = (fraction + String.replicate k "0").Substring(0, k)
            let body = if k = 0 then integral else integral + "." + fraction
            if value < 0m && magnitude <> 0m then "-" + body else body

        let spec = if isNull format then "" else format.Trim()

        if spec = "" then
            string d
        else
            match spec.[0], precision spec with
            | ('N' | 'n'), Some k -> fixedPoint true k d
            | ('F' | 'f'), Some k -> fixedPoint false k d
            | ('P' | 'p'), Some k -> fixedPoint true k (d * 100m) + " %"
            | _ -> string d
#else
        if System.String.IsNullOrWhiteSpace format then
            d.ToString System.Globalization.CultureInfo.InvariantCulture
        else
            d.ToString(format, System.Globalization.CultureInfo.InvariantCulture)
#endif

    // ── The codec ────────────────────────────────────────────────

    let private columnError (e: ColumnError) : string =
        "The slice could not be encoded: " + ColumnCodec.errorString e

    let private optStr (v: string option) : Cell =
        match v with
        | Some s -> Str s
        | None -> Null

    let private optInt (v: int option) : Cell =
        match v with
        | Some i -> Int i
        | None -> Null

    /// Build the slice's table from the gated heads: one row per (subject,
    /// period), in subject-path then period order, one column group per
    /// metric in `metrics`' order. A head whose metric is not in `metrics`
    /// is ignored; two heads of one metric on one row keep the later
    /// `AsOf`.
    let build (metrics: string list) (cells: FactSliceCell list) : Result<Table, string> =
        let rows =
            cells
            |> List.filter (fun c -> List.contains c.Metric metrics)
            |> List.groupBy (fun c -> c.SubjectPath, c.PeriodFrom, c.PeriodTo)
            |> List.sortBy fst
            |> List.map (fun (_, group) ->
                let byMetric =
                    group
                    |> List.groupBy _.Metric
                    |> List.map (fun (m, heads) -> m, heads |> List.maxBy _.AsOf)
                    |> Map.ofList

                List.head group, byMetric)

        let column (name: string) (ty: ColumnType) (cells: Cell list) =
            Column.ofCells name ty cells |> Result.map (fun c -> Field.create name ty, c)

        let fixedColumn (name: string) (ty: ColumnType) (cellOf: FactSliceCell -> Cell) =
            column name ty (rows |> List.map (fst >> cellOf))

        let metricColumns (m: string) =
            let head (name: string) (ty: ColumnType) (cellOf: FactSliceCell -> Cell) =
                rows
                |> List.map (fun (_, byMetric: Map<string, FactSliceCell>) ->
                    match byMetric.TryFind m with
                    | Some c -> cellOf c
                    | None -> Null)
                |> column (metricColumn m name) ty

            [
                head "" DecimalType (fun c -> c.Value |> Option.map decimalCell |> Option.defaultValue Null)
                head "fact_id" StringType (fun c -> Str c.FactId)
                head "as_of" instantType (fun c -> Timestamp(timestampText c.AsOf))
                head "freshness" StringType (fun c -> Str c.Freshness)
                head "rendering" StringType (fun c -> optStr c.Rendering)
                head "withheld" StringType (fun c -> optStr c.WithheldPolicy)
                head "rank_desc" IntType (fun c -> optInt c.RankDescending)
                head "rank_asc" IntType (fun c -> optInt c.RankAscending)
            ]

        let all =
            [
                column RowColumn IntType (rows |> List.mapi (fun i _ -> Int i))
                fixedColumn SubjectColumn StringType (fun c -> Str c.Subject)
                fixedColumn PathColumn StringType (fun c -> Str(String.concat ">" c.SubjectPath))
                fixedColumn FromColumn instantType (fun c -> Timestamp(timestampText c.PeriodFrom))
                fixedColumn ToColumn instantType (fun c -> Timestamp(timestampText c.PeriodTo))
                fixedColumn LabelColumn StringType (fun c -> optStr c.PeriodLabel)
            ]
            @ List.collect metricColumns metrics

        match
            all
            |> List.tryPick (fun r ->
                match r with
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error(columnError e)
        | None ->
            let pairs =
                all
                |> List.choose (fun r ->
                    match r with
                    | Ok p -> Some p
                    | Error _ -> None)

            let table = {
                Schema = pairs |> List.map fst
                Columns = pairs |> List.map snd
            }

            match Table.validate table with
            | Error e -> Error(columnError e)
            | Ok() -> Ok table

    /// A built table in the canonical codec.
    let encode (table: Table) : Result<string, string> =
        ColumnCodec.tryEncode (Embedded table) |> Result.mapError columnError

    /// The table a slice carries.
    let decode (slice: FactSlice) : Result<Table, string> =
        match ColumnCodec.decode slice.Table with
        | Ok(Embedded table) -> Ok table
        | Ok(Ref name) -> Error(sprintf "The slice names a source '%s' rather than carrying its rows." name)
        | Error e -> Error("The slice could not be decoded: " + ColumnCodec.errorString e)

    /// A slice decoded and prepared once, for every local read over it.
    type Held = {
        /// The slice as served.
        Slice: FactSlice
        /// Its table, prepared (`DataFrame.prepare`).
        Prepared: Prepared
    }

    /// Decode and prepare a slice.
    let hold (slice: FactSlice) : Result<Held, string> =
        decode slice
        |> Result.map (fun table -> {
            Slice = slice
            Prepared = DataFrame.prepare table
        })

    // ── Coverage: when a request can be answered locally ─────────

    let private isPrefixOf (prefix: string list) (path: string list) =
        List.length path >= List.length prefix
        && List.forall2 (=) prefix (List.truncate (List.length prefix) path)

    let private carries (slice: FactSlice) (metric: string) =
        slice.Metrics |> List.exists (fun m -> m.Metric = metric)

    /// Whether a drill-down request is answerable from the slice: same
    /// table, a metric it carries, a prefix inside its branch, and a slice
    /// over every period (the drill-down has no period range).
    let coversRows (slice: FactSlice) (query: FactRowQuery) : bool =
        slice.TableId = query.TableId
        && carries slice query.Metric
        && isPrefixOf slice.SubjectPrefix query.SubjectPrefix
        && slice.PeriodFrom = FactBrowseApi.AllPeriodsFrom
        && slice.PeriodTo = FactBrowseApi.AllPeriodsTo

    /// Whether a series request is answerable from the slice: same table, a
    /// metric it carries, a prefix inside its branch, a period range inside
    /// its own, and the table's own level.
    let coversSeries (slice: FactSlice) (query: FactSeriesQuery) : bool =
        slice.TableId = query.TableId
        && carries slice query.Metric
        && isPrefixOf slice.SubjectPrefix query.SubjectPrefix
        && query.PeriodFrom >= slice.PeriodFrom
        && query.PeriodTo <= slice.PeriodTo
        && query.SubjectLevel |> Option.forall (fun level -> level = slice.Level)

    // ── The pipelines ────────────────────────────────────────────

    let private notNull (column: string) = Not(IsNull(Col column))

    /// The rows under a subject prefix — the branch filter.
    let branchFilter (prefix: string list) : Transform list =
        match prefix with
        | [] -> []
        | _ ->
            let joined = String.concat ">" prefix

            [
                Filter(
                    Binary(
                        Or,
                        Binary(Eq, Col PathColumn, Lit(Str joined)),
                        Binary(StartsWith, Col PathColumn, Lit(Str(joined + ">")))
                    )
                )
            ]

    /// The population a drill-down ranks: the rows holding a head of the
    /// metric, under the prefix.
    let population (metric: string) (prefix: string list) : Transform list =
        Filter(notNull (metricColumn metric "fact_id")) :: branchFilter prefix

    let private rankColumnOf (metric: string) (direction: FactRowDirection) =
        match direction with
        | TopOfRanking -> metricColumn metric "rank_desc"
        | BottomOfRanking -> metricColumn metric "rank_asc"

    /// The column the ranking window numbers its rows in.
    [<Literal>]
    let RankColumn = "rank"

    /// The drill-down's ranking window: the population's comparable heads
    /// ordered by the slice's ranking position, numbered 1… under the
    /// prefix, and cut to ranks `(fromRank, toRank]`.
    let rankingWindow (query: FactRowQuery) (fromRank: int) (toRank: int) : Transform list =
        let rankBy = rankColumnOf query.Metric query.Direction

        population query.Metric query.SubjectPrefix
        @ [
            Filter(notNull rankBy)
            Window {
                PartitionBy = []
                OrderBy = [ rankBy, Asc ]
                Fn = RowNumber
                Of = rankBy
                As = RankColumn
            }
            Filter(
                Binary(And, Binary(Gt, Col RankColumn, Lit(Int fromRank)), Binary(Le, Col RankColumn, Lit(Int toRank)))
            )
            Sort [ Slot.Lit RankColumn, Asc ]
        ]

    /// The series' points: the rows holding a head of the metric, under
    /// the prefix, whose period overlaps the range, in subject-path then
    /// period order.
    let seriesPoints (query: FactSeriesQuery) : Transform list =
        population query.Metric query.SubjectPrefix
        @ [
            Filter(
                Binary(
                    And,
                    Binary(Lt, Col FromColumn, Lit(Timestamp(timestampText query.PeriodTo))),
                    Binary(Gt, Col ToColumn, Lit(Timestamp(timestampText query.PeriodFrom)))
                )
            )
            Sort [ Slot.Lit RowColumn, Asc ]
        ]

    let private evaluate (held: Held) (pipeline: Transform list) : Result<Table, string> =
        DataFrame.evalPrepared (fun name -> Error(UnresolvedSource name)) Map.empty pipeline held.Prepared
        |> Result.mapError (fun e -> sprintf "A local read over the slice failed: %A" e)

    /// One answered table's rows, as a cell reader per column name.
    let private rowsOf (table: Table) : (string -> Cell) list =
        let columns =
            table.Columns
            |> List.map (fun c -> c.Name, Array.ofList (Column.toCells c))
            |> Map.ofList

        List.init (Table.rowCount table) (fun i -> fun name -> columns.[name].[i])

    let private str (cell: Cell) : string option =
        match cell with
        | Str s -> Some s
        | _ -> None

    let private intOf (cell: Cell) : int option =
        match cell with
        | Int i -> Some i
        | _ -> None

    let private instant (cell: Cell) : DateTime =
        match cell with
        | Timestamp t -> parseTimestamp t
        | _ -> DateTime.MinValue

    let private pathOf (cell: Cell) : string list =
        match str cell with
        | None
        | Some "" -> []
        | Some text -> text.Split '>' |> List.ofArray

    let private withheldCounts (policies: string list) : FactWithheldCount list =
        policies
        |> List.countBy id
        |> List.sortBy fst
        |> List.map (fun (policyRef, count) -> { PolicyRef = policyRef; Count = count })

    let private displayFormatOf (slice: FactSlice) (metric: string) : string =
        slice.Metrics
        |> List.tryFind (fun m -> m.Metric = metric)
        |> Option.map _.DisplayFormat
        |> Option.defaultValue ""

    let private countOf (cell: Cell) : int =
        match cell with
        | Int i -> i
        | Float f -> int f
        | Decimal text -> int (parseDecimal text)
        | _ -> 0

    /// The population summary's aggregate: one row of counts, coverage and
    /// the value total over a drill-down's population, so a page never
    /// materialises the population it summarises.
    let summaryAggregate (metric: string) (prefix: string list) : Transform list =
        let col part = metricColumn metric part

        population metric prefix
        @ [
            Derive("is_fresh", Case([ Binary(Eq, Col(col "freshness"), Lit(Str "fresh")), Lit(Int 1) ], Lit(Int 0)))
            GroupBy(
                [],
                [
                    {
                        Name = "facts"
                        Fn = Count
                        Of = col "fact_id"
                    }
                    {
                        Name = "subjects"
                        Fn = CountDistinct
                        Of = SubjectColumn
                    }
                    {
                        Name = "comparable"
                        Fn = Count
                        Of = col "rank_asc"
                    }
                    {
                        Name = "withheld"
                        Fn = Count
                        Of = col "withheld"
                    }
                    {
                        Name = "fresh"
                        Fn = Sum
                        Of = "is_fresh"
                    }
                    {
                        Name = "from"
                        Fn = Min
                        Of = FromColumn
                    }
                    { Name = "to"; Fn = Max; Of = ToColumn }
                    {
                        Name = "total"
                        Fn = Sum
                        Of = col ""
                    }
                ]
            )
        ]

    /// The head at one end of a drill-down's ranking (its rendering is the
    /// summary's minimum or maximum).
    let rankingEnd (metric: string) (prefix: string list) (rankPart: string) : Transform list =
        let rankBy = metricColumn metric rankPart

        population metric prefix
        @ [
            Filter(notNull rankBy)
            Sort [ Slot.Lit rankBy, Asc ]
            Limit(Slot.Lit 1, Slot.Lit 0)
        ]

    /// The population summary over the slice's matched heads. Counts,
    /// coverage and freshness are existence-level and always ride. The
    /// magnitudes ride only when no matched head is withheld — stricter
    /// than the server read, which withholds them only when a head in the
    /// ranking up to the page's end is: the slice never holds a withheld
    /// value to fold in.
    let private summary (held: Held) (metric: string) (prefix: string list) : Result<int * FactPopulationView, string> =
        let slice = held.Slice

        evaluate held (summaryAggregate metric prefix)
        |> Result.bind (fun aggregate ->
            let row = rowsOf aggregate |> List.tryHead

            let read name =
                row |> Option.map (fun r -> r name) |> Option.defaultValue Null

            let facts = countOf (read "facts")
            let comparable = countOf (read "comparable")
            let withheld = countOf (read "withheld") > 0
            let fresh = countOf (read "fresh")

            let extreme (rankPart: string) =
                if withheld || comparable = 0 then
                    Ok None
                else
                    evaluate held (rankingEnd metric prefix rankPart)
                    |> Result.map (fun t ->
                        rowsOf t
                        |> List.tryHead
                        |> Option.bind (fun r -> str (r (metricColumn metric "rendering"))))

            extreme "rank_asc"
            |> Result.bind (fun minimum ->
                extreme "rank_desc"
                |> Result.map (fun maximum ->
                    let mean =
                        match read "total" with
                        | Decimal text when not withheld && comparable > 0 ->
                            Some(
                                renderDerived (displayFormatOf slice metric) (parseDecimal text / decimal comparable)
                            )
                        | _ -> None

                    comparable,
                    {
                        SubjectCount = if facts = 0 then 0 else countOf (read "subjects")
                        FactCount = facts
                        ComparableCount = comparable
                        NonComparableCount = facts - comparable
                        PeriodFrom = if facts = 0 then None else Some(instant (read "from"))
                        PeriodTo = if facts = 0 then None else Some(instant (read "to"))
                        FreshCount = fresh
                        StaleCount = facts - fresh
                        Methods =
                            if facts = 0 then
                                []
                            else
                                [
                                    {
                                        Method = slice.Method
                                        FactCount = facts
                                    }
                                ]
                        Minimum = minimum
                        Maximum = maximum
                        Mean = mean
                        ValueStatisticsWithheld = withheld
                    })))

    /// One drill-down page answered from the slice: the same rows, order,
    /// ranks, withheld counts and paging flags `QueryRows` serves for the
    /// request (with the page clamps applied the same way). Its population
    /// summary is the slice's own (see `summary`).
    let rowPage (held: Held) (request: FactRowQuery) : Result<FactRowPage, string> =
        let slice = held.Slice
        let size = FactBrowseApi.clampPageSize request.PageSize
        let index = FactBrowseApi.clampPage request.Page
        let windowStart = index * size
        let windowEnd = windowStart + size
        let ceiling = slice.RankCeiling
        let topK = max 1 (min windowEnd ceiling)
        let col part = metricColumn request.Metric part

        summary held request.Metric request.SubjectPrefix
        |> Result.bind (fun (rankedCount, population) ->
            evaluate held (rankingWindow request windowStart topK)
            |> Result.map (fun window ->
                let windowRows = rowsOf window
                let truncated = rankedCount > topK
                let withheld = windowRows |> List.choose (fun r -> str (r (col "withheld")))

                {
                    TableId = slice.TableId
                    Metric = request.Metric
                    Direction = request.Direction
                    SubjectPrefix = request.SubjectPrefix
                    Page = index
                    PageSize = size
                    MaxPageSize = FactBrowseApi.MaxPageSize
                    RankCeiling = ceiling
                    Rows =
                        windowRows
                        |> List.filter (fun r -> (str (r (col "withheld"))).IsNone)
                        |> List.map (fun r -> {
                            Rank = intOf (r RankColumn) |> Option.defaultValue 0
                            FactId = str (r (col "fact_id")) |> Option.defaultValue ""
                            Subject = str (r SubjectColumn) |> Option.defaultValue ""
                            SubjectPath = pathOf (r PathColumn)
                            Rendering = str (r (col "rendering")) |> Option.defaultValue ""
                            PeriodFrom = instant (r FromColumn)
                            PeriodTo = instant (r ToColumn)
                            PeriodLabel = str (r LabelColumn)
                            AsOf = instant (r (col "as_of"))
                            Freshness = str (r (col "freshness")) |> Option.defaultValue ""
                        })
                    WithheldCount = List.length withheld
                    Withheld = withheldCounts withheld
                    HasMore = truncated && windowEnd < ceiling
                    ReachedRankCeiling = truncated && windowEnd >= ceiling
                    Population = population
                }))

    /// One series answered from the slice: the same points, order, withheld
    /// marks and cap refusal `QuerySeries` serves for the request.
    let series (held: Held) (request: FactSeriesQuery) : Result<FactSeries, string> =
        let slice = held.Slice
        let cap = FactBrowseApi.clampSeriesPoints request.MaxPoints
        let col part = metricColumn request.Metric part

        if request.PeriodFrom >= request.PeriodTo then
            Error(
                sprintf
                    "The period range %s..%s is not a half-open [from, to) extent: from must be before to."
                    (request.PeriodFrom.ToString "o")
                    (request.PeriodTo.ToString "o")
            )
        else
            evaluate held (seriesPoints request)
            |> Result.bind (fun table ->
                let rows = rowsOf table

                if List.length rows > cap then
                    Error(
                        sprintf
                            "The series would hold %d points, over the cap of %d (FactBrowseApi.MaxSeriesPoints is %d). Narrow the period range or the subject prefix."
                            (List.length rows)
                            cap
                            FactBrowseApi.MaxSeriesPoints
                    )
                else
                    let withheld = rows |> List.choose (fun r -> str (r (col "withheld")))

                    Ok {
                        TableId = slice.TableId
                        Metric = request.Metric
                        SubjectPrefix = request.SubjectPrefix
                        SubjectLevel = slice.Level
                        PeriodFrom = request.PeriodFrom
                        PeriodTo = request.PeriodTo
                        DisplayFormat = displayFormatOf slice request.Metric
                        Points =
                            rows
                            |> List.map (fun r ->
                                let isWithheld = (str (r (col "withheld"))).IsSome

                                {
                                    Subject = str (r SubjectColumn) |> Option.defaultValue ""
                                    SubjectPath = pathOf (r PathColumn)
                                    PeriodFrom = instant (r FromColumn)
                                    PeriodTo = instant (r ToColumn)
                                    PeriodLabel = str (r LabelColumn)
                                    Value =
                                        match r (col "") with
                                        | Decimal text when not isWithheld -> Some(parseDecimal text)
                                        | _ -> None
                                    Rendering = if isWithheld then None else str (r (col "rendering"))
                                    FactId = str (r (col "fact_id")) |> Option.defaultValue ""
                                    AsOf = instant (r (col "as_of"))
                                    Freshness = str (r (col "freshness")) |> Option.defaultValue ""
                                    Withheld = isWithheld
                                })
                        MaxPoints = cap
                        WithheldCount = List.length withheld
                        Withheld = withheldCounts withheld
                    })