// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Grounding

open System
open ToolUp.Platform

// ─── Declared fact tables (Phase 887) ────────────────────────────────
//
// A third sibling to `ServerModule.declareMetrics` / `declareSubjects`: a
// module declares the **fact tables** it produces as part of its output
// contract. A table is one row per subject instance at one level of one
// registered subject hierarchy, with one column per registered metric, so
// a population producer refreshes the whole population as ONE run with
// ONE commit instead of one assertion per subject per metric.
//
// The module knows the table's LOGICAL identity (its stable `Id`). The
// composition binds the PHYSICAL destination (`FactTableBinding`); the
// module writes through the writer seam the platform hands it, and no
// connection string, physical table name or storage type ever crosses
// that seam.
//
// **Optional; zero-weight when unused (GP 13).** A module that declares no
// table composes byte-for-byte as it did before: no registry singleton,
// no preflight validator, no writer.
//
// **Fable-safe (GP 10).** Every type here is plain data — records,
// qualified DUs, primitives, `DateTime` / `TimeSpan` — so a client surface
// can render a declaration, a run history or a change summary from the
// same types the server writes. The digest and validation logic that needs
// the BCL lives server-side, with the writer.
//
// **Generic substrate (GP 1).** No domain vocabulary: a table's columns are
// registered metric ids and its rows are addressed by opaque subject paths.

/// The value shape a fact-table column carries — the `FactValue` case every
/// row's cell for that column must take. A queryable gap (`Absent`) is
/// admitted in any column: a producer that could not compute a value says
/// so rather than dropping the row.
[<RequireQualifiedAccess>]
type FactTableValueShape =
    /// A single decimal value.
    | Scalar
    /// A `[Low, High]` interval.
    | Interval
    /// A named distribution (bucket → value).
    | Distribution
    /// A categorical / label value.
    | Categorical
    /// A reference to a data-object version holding a series.
    | Series

/// The disclosure class a table (or one of its columns) is published under
/// by default. Mirrors the fact tier's three classes one for one, declared
/// here in Core so a declaration stays Fable-safe and names no server type;
/// the writer maps it onto the fact it writes.
[<RequireQualifiedAccess>]
type FactTableDisclosure =
    /// May reach an answer / an external door, subject to composition
    /// policy.
    | Surfaceable
    /// First-class inside computation and internal audit; never an egress
    /// output.
    | Internal
    /// Governed by a named policy (opaque reference).
    | Restricted of policyRef: string

/// The period grain a table's rows describe. Declared metadata a planner
/// or browse surface reads ("this table is monthly"); the commit validates
/// each row's period as a half-open extent, and never re-buckets a period
/// to fit a grain.
[<RequireQualifiedAccess>]
type FactTablePeriodGrain =
    | Day
    | Week
    | Month
    | Quarter
    | Year
    /// A point-in-time snapshot: the row's period is whatever extent the
    /// producer states, with no calendar grain implied.
    | Snapshot

/// How a table keeps its history across runs.
[<RequireQualifiedAccess>]
type FactTableHistoryMode =
    /// Every committed run is a complete, attributable snapshot: each row
    /// of each run is written as a fact carrying that run's watermark, so
    /// "what did run N say" is answerable exactly, row by row. Costs one
    /// fact per cell per run.
    | AppendByRun
    /// The table holds the latest values: a cell is rewritten only when its
    /// value changed since the previous run, so an unchanged population
    /// re-run costs nothing. History is still reconstructible by
    /// transaction time (the fact tier is append-only), but a cell is not
    /// re-attributed to runs in which it did not move.
    | Replace

/// Whether a table is a required part of the declaring module's output.
[<RequireQualifiedAccess>]
type FactTableRequirement =
    /// The module's output is incomplete without this table. A composition
    /// that binds no store for it refuses to start, naming the table; a run
    /// that ends without committing it is reported as a failed run.
    | Required
    /// Produced when the module can; its absence is not a failure.
    | Optional

/// One column of a declared fact table: a registered metric id, the value
/// shape its cells take, and an optional disclosure overriding the table's
/// default for this column.
type FactTableColumn = {
    /// The registered metric id (`MetricDefinition.Id`) this column
    /// carries. Checked against the composed metric registry at compose.
    Metric: string
    /// The `FactValue` case every cell of this column must take.
    Shape: FactTableValueShape
    /// Disclosure for this column's cells; `None` inherits the table's
    /// `Disclosure`.
    Disclosure: FactTableDisclosure option
}

/// A declared fact table — the population output a module states it
/// produces. Identity is the stable `Id`; two modules declaring one id is a
/// compose-time duplicate-registration error.
type FactTableDefinition = {
    /// Stable identity token (`ComponentId`-class: lowercase,
    /// rename-stable). The table's LOGICAL identity — the composition binds
    /// where it physically lives.
    Id: string
    /// Schema version of the declaration. Bumped by the declaring module
    /// when its columns change; recorded on every run.
    SchemaVersion: int
    /// The registered subject hierarchy (`SubjectDefinition.Id`) the rows
    /// are drawn from.
    Hierarchy: string
    /// The level label within that hierarchy (one of its `Levels`) every
    /// row's subject sits at.
    Level: string
    /// The metric columns, in declaration order. Each is a registered
    /// metric id.
    Columns: FactTableColumn list
    /// The period grain the rows describe.
    PeriodGrain: FactTablePeriodGrain
    /// The operation that produces the table (an operation id). Written as
    /// the method of every fact the table produces, so the table's facts
    /// form their own lineage and never merge with another producer's.
    ProducingOperation: string
    /// How often the table is expected to be refreshed. A table whose last
    /// commit is older than this is stale.
    RefreshCadence: TimeSpan
    /// How history is kept across runs.
    HistoryMode: FactTableHistoryMode
    /// Default disclosure for every column that does not declare its own.
    Disclosure: FactTableDisclosure
    /// Whether the table is a required part of the module's output.
    Requirement: FactTableRequirement
}

/// Construction + projection helpers over a `FactTableDefinition`.
[<RequireQualifiedAccess>]
module FactTableDefinition =

    /// A column over a registered metric id, inheriting the table's
    /// disclosure.
    let column (metric: string) (shape: FactTableValueShape) : FactTableColumn = {
        Metric = metric
        Shape = shape
        Disclosure = None
    }

    /// The disclosure a column's cells are written under: the column's own
    /// declaration, else the table's default.
    let columnDisclosure (table: FactTableDefinition) (column: FactTableColumn) : FactTableDisclosure =
        column.Disclosure |> Option.defaultValue table.Disclosure

    /// The metric ids the table carries, in declaration order.
    let metrics (table: FactTableDefinition) : string list = table.Columns |> List.map _.Metric

    /// The subject-path depth the table's level sits at within a hierarchy
    /// — `1` for the hierarchy's first (root-most) level — or `None` when
    /// the level is not one of the hierarchy's `Levels`. A row's subject
    /// path has exactly this many members.
    let levelDepth (subject: SubjectDefinition) (level: string) : int option =
        subject.Levels
        |> List.tryFindIndex (fun l -> l = level)
        |> Option.map (fun i -> i + 1)

/// What a declared table is FOR, beside the declaration it describes: a
/// description of the table and of any of its columns, keyed by table id.
/// A record of its own rather than a field on `FactTableDefinition`, so
/// every existing full-literal declaration (a generated composition's
/// `FactTables.fs` among them) compiles unchanged. Registered by
/// `ServerModule.describeFactTables`; a description naming an undeclared
/// table or column is refused at compose, naming both.
///
/// **Prose about the TABLE, never a value.** A description is
/// interpretation, not data. A number — a level, a ranking, a "currently
/// around X" — belongs in a fact, where it is bitemporal, superseded when
/// it changes, disclosure-classified, and carries its provenance. A value
/// copied here is none of those things: it is a claim nothing can
/// supersede, on a compose-time declaration readable by anything that
/// resolves `IFactTableRegistry`, and it egresses through surfaces the
/// disclosure gate does not guard.
///
/// Generic substrate (GP 1): opaque text the declaring module supplies;
/// nothing here parses or interprets it.
type FactTableDescription = {
    /// The described table's `FactTableDefinition.Id`.
    TableId: string
    /// What the table is for and how its rows are derived.
    Description: string
    /// Per-column descriptions as `(metric id, description)`, each naming
    /// one of the table's `Columns`. A column left out is undescribed.
    Columns: (string * string) list
}

/// One declared table paired with the module that declared it — the
/// provenance the duplicate diagnostic and the preflight name.
type FactTableRegistration = {
    /// The `ServerModule.Name` that declared this table.
    Module: string
    /// The declared table.
    Definition: FactTableDefinition
}

/// One table description paired with the module that registered it — the
/// provenance the compose-time refusal names.
type FactTableDescriptionRegistration = {
    /// The `ServerModule.Name` that registered the description.
    Module: string
    /// The registered description.
    Description: FactTableDescription
}

/// Where the composition binds a declared table's rows — the physical side
/// of the logical declaration. `destination` is an opaque label naming the
/// bound store for diagnostics (the default writer binds `"fact-store"`);
/// it is never a connection string or a physical table name.
type FactTableBinding =
    /// Bind every table the composition declares to one destination.
    | BindAllFactTables of destination: string
    /// Bind one table, by id, to a destination.
    | BindFactTable of tableId: string * destination: string

/// Resolution over a composition's binding list.
[<RequireQualifiedAccess>]
module FactTableBinding =

    /// The destination a table is bound to, or `None` when no binding
    /// covers it. A binding naming the table explicitly wins over a
    /// bind-all; among several of one kind the last declared wins.
    let destinationOf (bindings: FactTableBinding list) (tableId: string) : string option =
        let explicitBinding =
            bindings
            |> List.choose (fun b ->
                match b with
                | BindFactTable(id, destination) when id = tableId -> Some destination
                | _ -> None)
            |> List.tryLast

        match explicitBinding with
        | Some destination -> Some destination
        | None ->
            bindings
            |> List.choose (fun b ->
                match b with
                | BindAllFactTables destination -> Some destination
                | BindFactTable _ -> None)
            |> List.tryLast

/// Read surface over the composed fact-table declarations, resolved from DI
/// by the writer and by discovery / browse surfaces. A compose-time
/// immutable projection: every lookup is a pure in-memory read.
///
/// A sibling of `IMetricRegistry` rather than members added to it: an
/// interface gaining members breaks every external implementation, and a
/// composition that declares no table must see no change at all.
type IFactTableRegistry =
    /// Every declared table, in declaration order, one entry per `Id`.
    abstract Tables: FactTableDefinition list
    /// A table by its stable `Id`; `None` when undeclared.
    abstract TryGetTable: tableId: string -> FactTableDefinition option
    /// Every table declared by the named module.
    abstract TablesByModule: moduleName: string -> FactTableDefinition list
    /// The module that declared a table; `None` when undeclared.
    abstract DeclaringModule: tableId: string -> string option
    /// The destination the composition bound a table to; `None` when
    /// unbound.
    abstract DestinationOf: tableId: string -> string option

/// The optional capability of a table registry that carries declared
/// descriptions (`ServerModule.describeFactTables`). A capability beside
/// `IFactTableRegistry` rather than a member on it, so no external
/// implementation breaks: a reader probes for it
/// (`FactTableRegistry.tryDescription`), and a registry without it simply
/// has no descriptions. The composed registry implements it.
type IDescribesFactTables =
    /// The description registered for a table; `None` when the table is
    /// undescribed or undeclared.
    abstract TryGetTableDescription: tableId: string -> FactTableDescription option

/// Construction for the composed table registry.
[<RequireQualifiedAccess>]
module FactTableRegistry =

    type private Registry
        (tables: FactTableRegistration list, bindings: FactTableBinding list, descriptions: FactTableDescription list) =
        // First declaration wins per id. Two MODULES declaring one id is
        // refused by the compose-time preflight before the app starts, so
        // this only collapses a module re-declaring its own table.
        let distinct = tables |> List.distinctBy _.Definition.Id
        let definitions = distinct |> List.map _.Definition
        let byId = distinct |> List.map (fun r -> r.Definition.Id, r) |> Map.ofList

        let descriptionById = descriptions |> List.map (fun d -> d.TableId, d) |> Map.ofList

        interface IDescribesFactTables with
            member _.TryGetTableDescription tableId = descriptionById |> Map.tryFind tableId

        interface IFactTableRegistry with
            member _.Tables = definitions

            member _.TryGetTable tableId =
                byId |> Map.tryFind tableId |> Option.map _.Definition

            member _.TablesByModule moduleName =
                distinct
                |> List.filter (fun r -> r.Module = moduleName)
                |> List.map _.Definition

            member _.DeclaringModule tableId =
                byId |> Map.tryFind tableId |> Option.map _.Module

            member _.DestinationOf tableId =
                if byId.ContainsKey tableId then
                    FactTableBinding.destinationOf bindings tableId
                else
                    None

    let private quoted (values: string list) : string =
        match values |> List.distinct with
        | [] -> "none"
        | vs -> vs |> List.map (sprintf "'%s'") |> String.concat ", "

    /// Every reason a description cannot stand beside the declarations, each
    /// naming the describing module, the table and (where it applies) the
    /// column: a table no module declares, a column the table does not
    /// carry, a table described twice, a column described twice.
    let private descriptionProblems
        (tables: FactTableRegistration list)
        (descriptions: FactTableDescriptionRegistration list)
        : string list =
        let declared =
            tables
            |> List.distinctBy _.Definition.Id
            |> List.map (fun r -> r.Definition.Id, r.Definition)
            |> Map.ofList

        let twice =
            descriptions
            |> List.groupBy _.Description.TableId
            |> List.choose (fun (tableId, group) ->
                match group with
                | []
                | [ _ ] -> None
                | _ ->
                    Some(
                        sprintf
                            "Fact table '%s' is described more than once (by modules %s). Describe a table once."
                            tableId
                            (quoted (group |> List.map _.Module))
                    ))

        let perDescription =
            descriptions
            |> List.collect (fun r ->
                let d = r.Description

                match declared |> Map.tryFind d.TableId with
                | None -> [
                    sprintf
                        "Module '%s' describes fact table '%s', which no module declares. Declared tables: %s."
                        r.Module
                        d.TableId
                        (quoted (declared |> Map.toList |> List.map fst))
                  ]
                | Some table ->
                    let columns = FactTableDefinition.metrics table

                    [
                        for metric, _ in d.Columns do
                            if not (List.contains metric columns) then
                                sprintf
                                    "Module '%s' describes column '%s' of fact table '%s', which the table does not declare. Its columns: %s."
                                    r.Module
                                    metric
                                    d.TableId
                                    (quoted columns)
                        for metric, count in d.Columns |> List.countBy fst do
                            if count > 1 then
                                sprintf
                                    "Module '%s' describes column '%s' of fact table '%s' %d times. Describe a column once."
                                    r.Module
                                    metric
                                    d.TableId
                                    count
                    ])

        twice @ perDescription

    /// Fold the composition's declarations, bindings and table descriptions
    /// into a registry that also carries `IDescribesFactTables`, or every
    /// reason the descriptions are refused (a table or column no module
    /// declares, a table or column described twice), each naming the module,
    /// the table and the column.
    let tryBuildDescribed
        (tables: FactTableRegistration list)
        (bindings: FactTableBinding list)
        (descriptions: FactTableDescriptionRegistration list)
        : Result<IFactTableRegistry, string> =
        match descriptionProblems tables descriptions with
        | [] -> Ok(Registry(tables, bindings, descriptions |> List.map _.Description) :> IFactTableRegistry)
        | problems -> Error(String.concat "\n" problems)

    /// `tryBuildDescribed` that raises on a refused description — the
    /// compose-time call site, so a description of nothing fails startup
    /// rather than a read.
    let buildDescribed
        (tables: FactTableRegistration list)
        (bindings: FactTableBinding list)
        (descriptions: FactTableDescriptionRegistration list)
        : IFactTableRegistry =
        match tryBuildDescribed tables bindings descriptions with
        | Ok registry -> registry
        | Error message -> failwith message

    /// Fold the composition's declarations and bindings into a registry.
    let build (tables: FactTableRegistration list) (bindings: FactTableBinding list) : IFactTableRegistry =
        buildDescribed tables bindings []

    /// The description a registry carries for a table: `None` when the table
    /// is undescribed, or when the registry does not carry descriptions
    /// (`IDescribesFactTables` is an optional capability).
    let tryDescription (registry: IFactTableRegistry) (tableId: string) : FactTableDescription option =
        match registry with
        | :? IDescribesFactTables as described -> described.TryGetTableDescription tableId
        | _ -> None

    /// The empty registry — no table declared, nothing bound.
    let empty: IFactTableRegistry = build [] []

// ─── Runs (Phase 887) ────────────────────────────────────────────────
//
// A table is refreshed by a RUN: open, write rows in batches, commit. The
// types below are what a run leaves behind — its record, its watermark, and
// the change summary against the previous committed run — as plain data a
// browse surface can render.

/// The token minted when a run commits: which table, the run's position in
/// that table's commit sequence (strictly increasing per table and scope),
/// the transaction time of the commit, and a digest over the run's content.
type FactTableWatermark = {
    /// The table the committed run belongs to.
    TableId: string
    /// 1 for a table's first committed run, then +1 per commit.
    Sequence: int64
    /// When the commit was recorded (second precision, UTC).
    CommittedAt: DateTime
    /// Lowercase hex SHA-256 over the committed rows, in canonical order.
    ContentDigest: string
}

/// Rendering for a `FactTableWatermark`.
[<RequireQualifiedAccess>]
module FactTableWatermark =

    /// The canonical token form, `"<tableId>@<sequence>:<digest prefix>"` —
    /// what a written fact carries to name the run it came from.
    let render (w: FactTableWatermark) : string =
        let prefix =
            if w.ContentDigest.Length > 16 then
                w.ContentDigest.Substring(0, 16)
            else
                w.ContentDigest

        sprintf "%s@%d:%s" w.TableId w.Sequence prefix

/// One of a commit's largest movers: a scalar cell whose value moved the
/// most (by absolute change) since the previous committed run.
type FactTableMover = {
    /// The metric (column) whose cell moved.
    Metric: string
    /// Readable subject reference (`hierarchy/member>member`).
    Subject: string
    /// Readable period (`label` when the row gave one, else `from..to`).
    Period: string
    /// The cell's value in the previous committed run.
    Previous: decimal
    /// The cell's value in this run.
    Current: decimal
    /// `Current - Previous`.
    Delta: decimal
}

/// What a commit changed against the previous committed run of the same
/// table, counted per row (a subject-and-period key). A row is `Changed`
/// when any of its cells changed value.
type FactTableChangeSummary = {
    /// Rows present now and absent from the previous run.
    New: int
    /// Rows present in both with at least one cell changed.
    Changed: int
    /// Rows present in both with every cell unchanged.
    Unchanged: int
    /// Rows present in the previous run and absent now.
    Removed: int
    /// The scalar cells that moved most, largest absolute change first,
    /// capped at `FactTableChangeSummary.MoverCap`.
    LargestMovers: FactTableMover list
}

/// Construction for a `FactTableChangeSummary`.
[<RequireQualifiedAccess>]
module FactTableChangeSummary =

    /// How many movers a summary carries.
    [<Literal>]
    let MoverCap = 10

    /// The summary of a run that changed nothing.
    let empty: FactTableChangeSummary = {
        New = 0
        Changed = 0
        Unchanged = 0
        Removed = 0
        LargestMovers = []
    }

/// What one committed run recorded.
type FactTableCommit = {
    /// The watermark the commit minted.
    Watermark: FactTableWatermark
    /// Rows the run committed.
    RowCount: int
    /// Facts written by the commit (new or superseding values, plus one
    /// absence per cell of a removed row). Idempotent cells write nothing.
    FactsWritten: int
    /// The change against the previous committed run.
    Change: FactTableChangeSummary
    /// The fact store's batch digest for the facts the commit wrote — the
    /// link from the run record to the store's own write receipt.
    BatchDigest: string
}

/// Where a run stands.
[<RequireQualifiedAccess>]
type FactTableRunStatus =
    /// Opened and accepting rows; nothing is visible yet.
    | Open
    /// Committed: the rows are the table's current content.
    | Committed of FactTableCommit
    /// Refused at commit — nothing was written. The reason names the
    /// offending rows.
    | Rejected of reason: string
    /// Ended without a commit by the producer.
    | Abandoned of reason: string

/// The record of one run of one table: what a writer keeps and the wire
/// carries. The plane reads it as its one run envelope through
/// `FactTableRun.toRunRecord`.
type FactTableRunRecord = {
    /// The table the run refreshes.
    TableId: string
    /// The run's identity, minted when it was opened.
    RunId: string
    /// The declaration's schema version the run was opened under.
    SchemaVersion: int
    /// When the run was opened (second precision, UTC).
    OpenedAt: DateTime
    /// The committed sequence the run was opened against; a commit is
    /// refused when another run committed the table in the meantime.
    BaseSequence: int64
    /// Rows staged so far.
    StagedRows: int
    /// Where the run stands.
    Status: FactTableRunStatus
    /// What the run declared when it was opened: its inputs,
    /// the period slice it replaces and its method. Kept on the record, so a
    /// commit observer reads them from the record alone. A record written
    /// before the options were kept reads as `RunOptions.empty`.
    Options: RunOptions
    /// The record's own format version: `FactTableRunRecord.CurrentVersion`
    /// for a record written with its options, 0 for one written before.
    RecordVersion: int
}

/// The run record's stored form.
[<RequireQualifiedAccess>]
module FactTableRunRecord =

    /// The format a record is written in: version 2 carries the run's
    /// options. A record without the member, written before the options
    /// were kept, reads as version 0.
    [<Literal>]
    let CurrentVersion = 2

    /// A record as read from storage: a record written before the options
    /// were kept reads with `Options = RunOptions.empty`.
    let ofStored (run: FactTableRunRecord) : FactTableRunRecord = {
        run with
            Options = RunOptions.ofStored run.Options
    }

/// A run's outcome as the declaration reads it.
[<RequireQualifiedAccess>]
type FactTableRunOutcome =
    /// Still open and within its cadence.
    | InProgress
    /// Committed.
    | Succeeded
    /// A `Required` table's run that ended without a commit — rejected,
    /// abandoned, or left open past the table's refresh cadence.
    | Failed of reason: string
    /// An `Optional` table's run that ended without a commit. Not a failure.
    | Discarded of reason: string

/// Why a run-record transition was refused.
[<RequireQualifiedAccess>]
type FactTableRunTransitionError = {
    /// The run the transition was asked of.
    RunId: string
    /// The run's status when it was asked, by name.
    Status: string
}

/// A run record proven `Open`: what every run transition takes,
/// so only an open run can be staged on, committed, rejected or abandoned.
/// Obtained through `FactTableRun.tryOpen`.
type OpenFactTableRun = private {
    Run: FactTableRunRecord
} with

    /// The open run's record.
    member this.Record = this.Run

/// The run lifecycle as one pure state machine over the record,
/// and derivations over run records.
///
/// A run is `Open` from the moment it is opened until exactly one of
/// `commit`, `reject` or `abandon` ends it; an ended run never moves again
/// (GP 5: the record moves forward only). Every writer executes these
/// transitions rather than restating them, so "what may happen to a run"
/// has one definition. Where the record is kept, the open-run claim that
/// serialises opens, and the commit's writes are the executing writer's;
/// which transition a writer applies, and whether it may, are not.
[<RequireQualifiedAccess>]
module FactTableRun =

    /// The status's name, as a refusal and an audit row render it.
    let statusName (status: FactTableRunStatus) : string =
        match status with
        | FactTableRunStatus.Open -> "open"
        | FactTableRunStatus.Committed _ -> "committed"
        | FactTableRunStatus.Rejected _ -> "rejected"
        | FactTableRunStatus.Abandoned _ -> "abandoned"

    /// Whether the run has ended (committed, rejected or abandoned). An
    /// ended run takes no transition, and only an ended run's leftovers are
    /// ever reclaimed by an orphan sweep.
    let hasEnded (run: FactTableRunRecord) : bool =
        match run.Status with
        | FactTableRunStatus.Open -> false
        | _ -> true

    /// A newly opened run: `Open`, nothing staged, carrying what it declared.
    let opened
        (tableId: string)
        (runId: string)
        (schemaVersion: int)
        (openedAt: DateTime)
        (baseSequence: int64)
        (options: RunOptions)
        : FactTableRunRecord =
        {
            TableId = tableId
            RunId = runId
            SchemaVersion = schemaVersion
            OpenedAt = openedAt
            BaseSequence = baseSequence
            StagedRows = 0
            Status = FactTableRunStatus.Open
            Options = options
            RecordVersion = FactTableRunRecord.CurrentVersion
        }

    /// The run, if it is still open: the one door to every transition below,
    /// so a transition out of an ended run cannot be written. An ended run is
    /// refused naming its status.
    let tryOpen (run: FactTableRunRecord) : Result<OpenFactTableRun, FactTableRunTransitionError> =
        match run.Status with
        | FactTableRunStatus.Open -> Ok { Run = run }
        | status ->
            Error {
                FactTableRunTransitionError.RunId = run.RunId
                Status = statusName status
            }

    /// Stage `count` more rows on an open run; it stays open.
    let stage (count: int) (run: OpenFactTableRun) : FactTableRunRecord = {
        run.Record with
            StagedRows = run.Record.StagedRows + count
    }

    /// Commit an open run.
    let commit (commit: FactTableCommit) (run: OpenFactTableRun) : FactTableRunRecord = {
        run.Record with
            Status = FactTableRunStatus.Committed commit
    }

    /// Refuse an open run at commit; nothing was written.
    let reject (reason: string) (run: OpenFactTableRun) : FactTableRunRecord = {
        run.Record with
            Status = FactTableRunStatus.Rejected reason
    }

    /// End an open run without a commit.
    let abandon (reason: string) (run: OpenFactTableRun) : FactTableRunRecord = {
        run.Record with
            Status = FactTableRunStatus.Abandoned reason
    }

#if !FABLE_COMPILER
    /// The run as the plane's one run envelope, in `scope` — the scope the
    /// reader resolved, in whose shard the record is kept — with the
    /// provenance the run was opened with. Its id is
    /// `RunRecord.id FactTableRun [tableId; runId]`; a committed run's result
    /// is its watermark's content digest.
    let toRunRecord (scope: ResolvedScope) (provenance: RunProvenance) (run: FactTableRunRecord) : RunRecord = {
        Id = RunRecord.id RunKind.FactTableRun [ run.TableId; run.RunId ]
        Kind = RunKind.FactTableRun
        Scope = scope
        Subject = run.TableId
        StartedAt = run.OpenedAt
        Options = RunOptions.ofStored run.Options
        Provenance = provenance
        Outcome =
            match run.Status with
            | FactTableRunStatus.Open -> RunOutcome.Open
            | FactTableRunStatus.Committed commit -> RunOutcome.Succeeded commit.Watermark.ContentDigest
            | FactTableRunStatus.Rejected reason -> RunOutcome.Failed reason
            | FactTableRunStatus.Abandoned reason -> RunOutcome.Abandoned reason
    }
#endif

    /// The outcome of a run under its table's declaration at instant `now`.
    /// Derived, never stored: an open run becomes overdue by the clock, not
    /// by a write.
    let outcome (table: FactTableDefinition) (now: DateTime) (run: FactTableRunRecord) : FactTableRunOutcome =
        let ended reason =
            match table.Requirement with
            | FactTableRequirement.Required -> FactTableRunOutcome.Failed reason
            | FactTableRequirement.Optional -> FactTableRunOutcome.Discarded reason

        match run.Status with
        | FactTableRunStatus.Committed _ -> FactTableRunOutcome.Succeeded
        | FactTableRunStatus.Rejected reason -> ended (sprintf "rejected at commit: %s" reason)
        | FactTableRunStatus.Abandoned reason -> ended (sprintf "abandoned: %s" reason)
        | FactTableRunStatus.Open ->
            if now - run.OpenedAt > table.RefreshCadence then
                ended (
                    sprintf
                        "opened at %s and not committed within the table's refresh cadence (%s)"
                        (run.OpenedAt.ToString("o"))
                        (string table.RefreshCadence)
                )
            else
                FactTableRunOutcome.InProgress

/// How current a table is against its declared refresh cadence.
[<RequireQualifiedAccess>]
type FactTableFreshness =
    /// No run of the table has ever committed.
    | NeverRefreshed
    /// The last commit is within the cadence.
    | Fresh of lastCommittedAt: DateTime
    /// The last commit is older than the cadence — a missed refresh.
    | Stale of lastCommittedAt: DateTime * overdueBy: TimeSpan

/// Derivation of a table's freshness.
[<RequireQualifiedAccess>]
module FactTableFreshness =

    /// Freshness at `now` for a table whose last commit (if any) was at
    /// `lastCommittedAt`.
    let derive (cadence: TimeSpan) (now: DateTime) (lastCommittedAt: DateTime option) : FactTableFreshness =
        match lastCommittedAt with
        | None -> FactTableFreshness.NeverRefreshed
        | Some at ->
            let age = now - at

            if age > cadence then
                FactTableFreshness.Stale(at, age - cadence)
            else
                FactTableFreshness.Fresh at

/// A table's standing, as the writer reports it.
type FactTableStatus = {
    /// The table reported on.
    TableId: string
    /// How current the table is against its refresh cadence.
    Freshness: FactTableFreshness
    /// The table's current commit, if it has ever committed.
    LastCommit: FactTableCommit option
    /// The most recently opened run, if any.
    LatestRun: FactTableRunRecord option
    /// That run's derived outcome.
    LatestRunOutcome: FactTableRunOutcome option
}