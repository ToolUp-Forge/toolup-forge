// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform
open ToolUp.Platform.Grounding

// ─── Delegate facts (Phase 889) ──────────────────────────────────────
//
// A **delegate fact** holds a whole metric population as ONE record in the
// fact tier that points at a declared fact table (Phase 887), in place of
// one stored fact per subject. It is the fact-tier counterpart of the
// virtual dataset binding (Phase 487): a source reference, a declared query
// spec and a watermark — and it reuses that binding's `SnapshotFidelity`
// vocabulary for what the source can say about the past.
//
// **Only what is quoted becomes a fact.** A read the delegate answers is
// pushed down to the table; the rows it actually returns — the single
// point-read row, or a ranking bounded by `PopulationQuery.MaxTopK` — are
// minted as ordinary facts at that moment, with the run's watermark among
// their input hashes. A number an answer quotes therefore always has a fact
// id, and the store holds nothing for a subject nobody asked about.
//
// **No free-form query.** The query spec is declared at composition and is
// a column mapping, never a query string: a caller supplies only what the
// fact tools already carry — metric, subject, period, ordering and count —
// and those arrive as the typed `DelegateTableRead` below.
//
// **Fable-safe (GP 10).** Records and DUs over primitives, the Core fact
// model and the Platform.Core fact-table types.

/// The column mapping a delegate reads its table through. The subject is
/// the row's subject path at the table's level, the period is the row's own
/// period, and the value is the named column's cell — so the whole mapping
/// is declared data, and nothing a caller supplies can widen it.
type DelegateQuerySpec = {
    /// The subject hierarchy the table's rows are addressed in.
    Hierarchy: string
    /// The subject-path depth of every row (`1` for the hierarchy's first
    /// level) — `FactTableDefinition.levelDepth` of the table's level.
    Level: int
    /// The table column (a registered metric id) whose cell is the value.
    ValueColumn: string
}

/// A delegated metric population: one record per (table, value column)
/// that points at the table where the population lives.
type DelegateFact = {
    /// The metric the population answers for — the value column's metric.
    Metric: MetricRef
    /// The declared fact table (`FactTableDefinition.Id`) the rows live in.
    TableId: string
    /// Where the table is held — an opaque label naming the bound source
    /// (the table's binding destination), never a connection string.
    SourceRef: string
    /// The declared column mapping.
    Query: DelegateQuerySpec
    /// The table's current run, once the record has been advanced by a
    /// commit; `None` for a declaration that has never been refreshed.
    Watermark: FactTableWatermark option
    /// The method every minted fact carries — the table's own lineage
    /// (`Computed(producingOperation, "v<schemaVersion>", tableId)`), so a
    /// minted fact names the operation that produced its row.
    Method: MethodRef
    /// The class a minted fact is born with: the column's own disclosure
    /// where the table declares one, else the table's.
    Disclosure: Disclosure
    /// What the table can say about the past. `Exact` for an append-by-run
    /// table, which keeps every run and answers an `AsOf` read from the run
    /// that was current then; `Approximate` for a replace table, which
    /// keeps only its latest state and therefore refuses an `AsOf` read.
    History: SnapshotFidelity
}

/// What one committed run of a delegated table left behind — the reach the
/// coverage narrative describes, read without touching a row.
type DelegateRun = {
    /// The table the run belongs to.
    TableId: string
    /// The run that committed.
    RunId: string
    /// The watermark the commit minted.
    Watermark: FactTableWatermark
    /// Rows the run committed.
    RowCount: int
    /// Distinct subjects among those rows.
    SubjectCount: int
    /// Per value column (metric id), the rows whose cell is comparable (a
    /// scalar, or an interval's midpoint) — what a ranking can order.
    ComparableByColumn: Map<string, int>
    /// Earliest period start among the rows; `None` for an empty run.
    PeriodFrom: DateTime option
    /// Latest period end among the rows; `None` for an empty run.
    PeriodTo: DateTime option
}

/// One row of a delegated table as a read returns it — a subject, a period
/// and the value column's cell. Never more than the read asked for.
type DelegateTableRow = {
    /// Member ids from the hierarchy root down to the table's level.
    Subject: string list
    /// The row's period.
    Period: TemporalExtent
    /// The value column's cell.
    Value: FactValue
}

/// Per-parent totals of a delegated column — the answer to the coherence
/// check's question, computed by the table rather than by enumerating
/// facts.
type DelegateChildTotal = {
    /// The parent subject path (one level above the table's rows).
    Parent: string list
    /// The period the children share.
    Period: TemporalExtent
    /// Sum of the children's scalar cells.
    Total: decimal
    /// Children counted under the parent.
    ChildCount: int
    /// Children whose cell is `Absent` — a partial-load signal.
    AbsentCount: int
}

/// The typed reads a delegate pushes down to its table. Each case carries
/// only values a fact tool's parameters already carry — there is no case a
/// free-form query could ride in.
[<RequireQualifiedAccess>]
type DelegateTableRead =
    /// One subject's rows, optionally restricted to periods overlapping an
    /// extent.
    | Point of subject: string list * periodOverlaps: TemporalExtent option
    /// A bounded ranking over the rows matching an optional path prefix,
    /// period overlap and value threshold.
    | Ranked of
        pathPrefix: string list option *
        periodOverlaps: TemporalExtent option *
        threshold: ValueThreshold option *
        direction: RankDirection *
        topK: int
    /// Per-parent totals at `parentDepth` (the table's level minus one).
    | ChildTotals of periodOverlaps: TemporalExtent option

/// Why a delegate declined a read. A refusal is an answer (GP 9): the
/// delegate never approximates what its table cannot say.
[<RequireQualifiedAccess>]
type DelegateRefusal =
    /// An `AsOf` read against a table whose history is `Approximate` — the
    /// table keeps only its latest state, so what it held at that instant
    /// cannot be reconstructed.
    | ApproximateHistory of metric: string * tableId: string * asOf: DateTime
    /// The table's storage could not be read.
    | SourceUnavailable of tableId: string * detail: string

/// Construction and rendering for delegate records.
module DelegateFact =

    /// The destination label a delegated table is bound to
    /// (`FactTableBinding.BindFactTable(tableId, Destination)`).
    [<Literal>]
    let Destination = "delegate-table"

    /// The class a delegated table's disclosure maps to.
    let disclosureOf (d: FactTableDisclosure) : Disclosure =
        match d with
        | FactTableDisclosure.Surfaceable -> Disclosure.Surfaceable
        | FactTableDisclosure.Internal -> Disclosure.Internal
        | FactTableDisclosure.Restricted policy -> Disclosure.Restricted policy

    /// The history a table's mode supports: every run kept is `Exact`, only
    /// the latest kept is `Approximate`.
    let historyOf (mode: FactTableHistoryMode) : SnapshotFidelity =
        match mode with
        | FactTableHistoryMode.AppendByRun -> SnapshotFidelity.Exact
        | FactTableHistoryMode.Replace -> SnapshotFidelity.Approximate

    /// The method a table's facts carry — identical to what the default
    /// writer stamps, so a minted fact and a written one share a lineage.
    let methodOf (table: FactTableDefinition) : MethodRef =
        Computed(table.ProducingOperation, sprintf "v%d" table.SchemaVersion, table.Id)

    /// The delegate records a declared table yields — one per column —
    /// given the table's hierarchy. `Error` names why the declaration cannot
    /// be delegated (its level is not one of the hierarchy's levels).
    let ofTable (hierarchy: SubjectDefinition) (table: FactTableDefinition) : Result<DelegateFact list, string> =
        match FactTableDefinition.levelDepth hierarchy table.Level with
        | None ->
            Error(
                sprintf
                    "delegate fact: table '%s' sits at level '%s', which hierarchy '%s' does not declare"
                    table.Id
                    table.Level
                    hierarchy.Id
            )
        | Some depth ->
            table.Columns
            |> List.map (fun column -> {
                Metric = MetricRef column.Metric
                TableId = table.Id
                SourceRef = Destination
                Query = {
                    Hierarchy = table.Hierarchy
                    Level = depth
                    ValueColumn = column.Metric
                }
                Watermark = None
                Method = methodOf table
                Disclosure = disclosureOf (FactTableDefinition.columnDisclosure table column)
                History = historyOf table.HistoryMode
            })
            |> Ok

    /// The record advanced to a committed run.
    let advance (run: DelegateRun) (delegateFact: DelegateFact) : DelegateFact = {
        delegateFact with
            Watermark = Some run.Watermark
    }

    /// The token a minted fact carries in its input hashes to name its run
    /// (`FactTableWatermark.render`).
    let token (watermark: FactTableWatermark) : string = FactTableWatermark.render watermark

    /// Whether an input-hash entry names a run of `tableId` — the
    /// `"<tableId>@<sequence>:<digest>"` shape `FactTableWatermark.render`
    /// produces.
    let isRunToken (tableId: string) (inputHash: string) : bool =
        inputHash.StartsWith(tableId + "@", StringComparison.Ordinal)

    /// Whether a subject is one of the delegate's rows: its hierarchy and
    /// its depth.
    let covers (delegateFact: DelegateFact) (subject: SubjectRef) : bool =
        subject.Hierarchy = delegateFact.Query.Hierarchy
        && List.length subject.Path = delegateFact.Query.Level

/// Rendering for `DelegateRefusal`.
module DelegateRefusal =

    /// The refusal as the one-line text a store's error channel carries.
    let describe (refusal: DelegateRefusal) : string =
        match refusal with
        | DelegateRefusal.ApproximateHistory(metric, tableId, asOf) ->
            sprintf
                "delegate fact: metric '%s' is held by table '%s', whose history is Approximate (it keeps only its latest run), so what it held as of %s cannot be reconstructed. Ask without AsOf, or hold the table append-by-run."
                metric
                tableId
                (asOf.ToUniversalTime().ToString "o")
        | DelegateRefusal.SourceUnavailable(tableId, detail) ->
            sprintf "delegate fact: table '%s' could not be read: %s" tableId detail