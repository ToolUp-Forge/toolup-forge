// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding

// ─── IFactTableWriter (Phase 887) ────────────────────────────────────
//
// The seam a module writes a declared fact table through: open a run,
// write rows in batches, commit. The module names the table's LOGICAL
// identity (its declared id); the composition binds where it physically
// lives. No connection string, physical table name or storage type
// crosses this seam — a row is subject path + period + one value per
// declared metric column, and everything else is the writer's business.
//
// The commit is the one act a refresh is: it validates every row against
// the declaration (the subject path sits at the declared level, no
// subject-and-period key twice, every value the column's shape) and on
// ANY failure rejects the whole run and writes nothing, naming the rows —
// the `AssertBatch` all-or-nothing posture (Phase 704). On success it
// swaps the table's current run, mints the run's watermark, computes the
// change summary against the previous run, and writes ONE audit record
// for the run.
//
// **Six portability rules (GP 12), audited.**
//  1. Identity by value — scope, table and run ids are strings; rows,
//     records, watermarks and summaries are values. No handle, cursor or
//     connection is ever returned.
//  2. Async at every boundary — every member returns `Async<Result<_, _>>`.
//  3. Failure as data — every refusal is a `FactTableWriteError` case
//     naming what failed (the rejected rows by position); nothing throws
//     across the seam and no callback is taken.
//  4. Stateless between calls — a run's staged rows and its record live in
//     the backing store, keyed by run id; any instance (or replica) can
//     take the next call of a run another one opened.
//  5. No cross-shard ordering — the shard key is (scope, table). A table's
//     commit sequence is strictly increasing within its scope and nothing
//     is promised across tables or scopes. Within one run, batches are
//     ordered by the offset they were staged at; a run has one producer.
//  6. Precision at the lower bound — run and commit times are stamped at
//     second precision from the injected clock; staleness is measured
//     against the declared cadence at that precision, never finer.

/// One row of a declared fact table: a subject at the table's level, the
/// period the values describe, and one value per declared metric column.
type FactTableRow = {
    /// Member ids from the hierarchy's root level down to the table's
    /// level — exactly as many as the level's depth.
    Subject: string list
    /// The valid-time extent the values describe (half-open).
    Period: TemporalExtent
    /// One value per declared column, keyed by metric id. `Absent` records
    /// a queryable gap in any column.
    Values: Map<string, FactValue>
}

/// Why one row of a run cannot be committed.
type FactTableRowDefect = {
    /// The row's position in the run (0-based, in staging order).
    Position: int
    /// The row's readable subject (`hierarchy/member>member`).
    Subject: string
    /// What is wrong with it.
    Problem: string
}

/// A writer refusal. Every case names what it refused.
type FactTableWriteError =
    /// The table id is not declared in this composition.
    | FactTableUndeclared of tableId: string
    /// The table is declared but bound to a different store (or to none),
    /// so this writer does not write it.
    | FactTableNotBoundHere of tableId: string * boundTo: string option * writer: string
    /// No run with this id exists in the scope.
    | FactTableRunUnknown of runId: string
    /// The run is no longer open (committed, rejected or abandoned).
    | FactTableRunClosed of runId: string * status: string
    /// The commit validated the run and refused it; nothing was written.
    | FactTableRowsRejected of runId: string * defects: FactTableRowDefect list
    /// Another run of the table committed after this one was opened; this
    /// run's change summary would be computed against the wrong base.
    | FactTableCommitConflict of runId: string * openedAgainst: int64 * current: int64
    /// The backing store failed. The run is left open and the commit may
    /// be retried: facts are content-addressed, so a retry is exact.
    | FactTableStorageFailure of detail: string
    /// Another run of the table is open in the scope (Phase 994): a table
    /// takes one run at a time per scope. End that run (commit or abandon
    /// it) or let it pass the table's refresh cadence, then open again.
    | FactTableRunInProgress of tableId: string * openRunId: string
    /// The options a run was opened with cannot be honoured (Phase 994): an
    /// input hash not in the content-hash form, a slice that is not a
    /// half-open extent or that splits a committed row, or an option the
    /// writer does not support. Nothing was written.
    | FactTableRunOptionsRefused of tableId: string * reason: string

/// Rendering for a `FactTableWriteError`.
module FactTableWriteError =

    /// How many rejected rows a rendered refusal names before summarising.
    [<Literal>]
    let RenderedDefectCap = 20

    let private renderDefects (defects: FactTableRowDefect list) : string =
        let shown =
            defects
            |> List.truncate RenderedDefectCap
            |> List.map (fun d -> sprintf "row %d (%s): %s" d.Position d.Subject d.Problem)
            |> String.concat "; "

        if defects.Length > RenderedDefectCap then
            sprintf "%s; and %d more" shown (defects.Length - RenderedDefectCap)
        else
            shown

    /// A one-line, operator-readable account of the refusal.
    let describe (error: FactTableWriteError) : string =
        match error with
        | FactTableUndeclared tableId -> sprintf "fact table '%s' is not declared in this composition" tableId
        | FactTableNotBoundHere(tableId, boundTo, writer) ->
            sprintf
                "fact table '%s' is bound to %s, not to this writer ('%s')"
                tableId
                (boundTo |> Option.map (sprintf "'%s'") |> Option.defaultValue "no store")
                writer
        | FactTableRunUnknown runId -> sprintf "no fact-table run '%s' exists in this scope" runId
        | FactTableRunClosed(runId, status) -> sprintf "fact-table run '%s' is %s, not open" runId status
        | FactTableRowsRejected(runId, defects) ->
            sprintf
                "fact-table run '%s' was rejected and nothing was written — %d invalid row(s): %s"
                runId
                defects.Length
                (renderDefects defects)
        | FactTableCommitConflict(runId, openedAgainst, current) ->
            sprintf
                "fact-table run '%s' was opened against commit %d but the table is now at commit %d; open a new run"
                runId
                openedAgainst
                current
        | FactTableStorageFailure detail -> sprintf "fact-table storage failed: %s" detail
        | FactTableRunInProgress(tableId, openRunId) ->
            sprintf
                "fact table '%s' already has an open run '%s' in this scope; commit or abandon it before opening another"
                tableId
                openRunId
        | FactTableRunOptionsRefused(tableId, reason) ->
            sprintf "a run of fact table '%s' cannot be opened with these options: %s" tableId reason

// ─── Run provenance (Phases 932, 938) ────────────────────────────────
//
// A run's facts are `Computed` by the table's producing operation — the
// table's own lineage — unless the run was OPENED with an imported
// provenance (`IFactTableWriter.OpenRun`'s `provenance`, Phase 938). An
// importing producer therefore writes through the COMPOSED writer, whatever
// store the table is bound to, and every decoration of it (the per-run
// browse notice, audit, budgets) reaches imported runs exactly as it
// reaches computed ones. A run opened with no provenance, or with
// `ComputedRun`, writes byte-for-byte what it wrote before (GP 11).

/// One imported cell's disclosure as its origin published it.
type FactTableImportedCell = {
    /// The row's subject path in the importing table.
    Subject: string list
    /// The column's metric id.
    Metric: string
    /// The row's period start.
    From: DateTime
    /// The row's period end.
    To: DateTime
    /// What the origin published the cell at.
    Disclosure: Disclosure
}

/// One origin of an imported run: the rows under one root subject member.
type FactTableImportOrigin = {
    /// The root member the origin's rows sit under.
    RootMember: string
    /// The certificate the origin's facts name (`Imported certificateRef`).
    CertificateRef: string
    /// The evidence trigger the origin's facts carry.
    TriggerRef: string
    /// When set, the origin is withdrawn: the absences the run mints for
    /// its rows name this reason rather than the run.
    Withdrawal: string option
    /// The cells the origin published, with their disclosures.
    Cells: FactTableImportedCell list
}

/// Where a run's facts come from. Part of the `IFactTableWriter` contract
/// since Phase 938: every writer, and every decorator of one, honours it.
type FactTableRunProvenance =
    /// Computed by the table's producing operation — the default.
    | ComputedRun
    /// Imported: a row under an origin's root member is written with
    /// `Imported` provenance naming the origin's certificate, evidence naming
    /// the origin, and a disclosure no wider than the origin published it —
    /// the floor of the two (`Disclosure.floor`), and `Internal` for a cell
    /// the origin did not place. A row under no origin stays `Computed`.
    | ImportedRun of origins: FactTableImportOrigin list

// ─── Run options: inputs and the period slice (Phase 994) ────────────
//
// A daily producer publishes one day per run. Two declarations make that
// cheap and auditable, and both are made when the run is OPENED, so any
// instance can commit the run (rule 4):
//
//  - **Inputs** — the content hashes of the files or objects the run was
//    computed from. Every fact the run commits names them in its evidence
//    (`Evidence.InputHashes`, in the `InputHash` form), so a number reaches
//    the upload it came from in one hop.
//  - **Period slice** — the half-open extent the run replaces. Its rows
//    must sit inside it, and the commit withdraws only the table's rows
//    inside it: every committed row outside the slice stands, unrewritten.
//    No slice replaces the whole table, as before (GP 11).
//
// A run opened with no options, or with `FactTableRunOptions.none`, writes
// byte-for-byte what it wrote before.

/// What a run declares beyond its table and provenance (Phase 994). Part of
/// the `IFactTableWriter` contract: every writer honours it or refuses the
/// run with `FactTableRunOptionsRefused`, and every decorator passes it on
/// untouched.
type FactTableRunOptions = {
    /// The content hashes of the inputs the run was computed from, each in
    /// the `InputHash` form (`sha256:<64 lowercase hex>`). Every fact the run
    /// commits, absences included, carries them among its input hashes.
    /// Empty declares nothing.
    Inputs: string list
    /// The period slice the run replaces — usually one period at the
    /// table's `PeriodGrain`. `None` replaces the whole table.
    Slice: TemporalExtent option
}

/// Construction and validation of `FactTableRunOptions`.
module FactTableRunOptions =

    /// No options: the run replaces the whole table and names no inputs.
    let none: FactTableRunOptions = { Inputs = []; Slice = None }

    /// Name the inputs the run was computed from.
    let withInputs (inputs: string list) (options: FactTableRunOptions) : FactTableRunOptions = {
        options with
            Inputs = inputs
    }

    /// Replace only the given period slice.
    let forSlice (slice: TemporalExtent) (options: FactTableRunOptions) : FactTableRunOptions = {
        options with
            Slice = Some slice
    }

    /// Whether the options declare nothing (`none`, or an omitted argument).
    let isNone (options: FactTableRunOptions) : bool =
        List.isEmpty options.Inputs && options.Slice.IsNone

    /// The options as a writer keeps them: each input once, in ordinal
    /// order — or the reason they cannot be honoured.
    let normalise (options: FactTableRunOptions) : Result<FactTableRunOptions, string> =
        let inputs = if isNull (box options.Inputs) then [] else options.Inputs

        match inputs |> List.filter (InputHash.isContentHash >> not) with
        | bad :: _ ->
            Error(
                sprintf
                    "input '%s' is not a content hash in the <algorithm>:<lowercase hex> form (at least %d hex digits)"
                    bad
                    InputHash.MinimumDigits
            )
        | [] ->
            match options.Slice with
            | Some slice when slice.From >= slice.To ->
                Error(
                    sprintf
                        "the slice %s..%s is not a half-open extent"
                        (slice.From.ToString "o")
                        (slice.To.ToString "o")
                )
            | slice ->
                Ok {
                    Inputs =
                        inputs
                        |> List.distinct
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    Slice = slice
                }

    /// Whether a period lies inside a slice.
    let contains (slice: TemporalExtent) (from: DateTime) (``to``: DateTime) : bool =
        from >= slice.From && ``to`` <= slice.To

    /// Whether a period overlaps a slice without lying inside it — a
    /// committed row such a slice would split.
    let splits (slice: TemporalExtent) (from: DateTime) (``to``: DateTime) : bool =
        from < slice.To && ``to`` > slice.From && not (contains slice from ``to``)

    /// The draft rewrite the options imply: the inputs join every draft's
    /// input hashes. Identity when the options name no input.
    let rewrite (options: FactTableRunOptions) : FactDraft -> FactDraft =
        match options.Inputs with
        | [] -> id
        | inputs ->
            fun (draft: FactDraft) -> {
                draft with
                    Evidence = {
                        draft.Evidence with
                            InputHashes = draft.Evidence.InputHashes @ inputs
                    }
            }

/// The seam a producer writes a declared fact table through.
type IFactTableWriter =
    /// Open a run of a declared table in a scope. The run stages rows and
    /// makes nothing visible until it commits.
    ///
    /// `provenance` (Phase 938) says where the run's facts come from, and the
    /// writer keeps it with the run until the run ends: omitted, or
    /// `ComputedRun`, the run writes the table's own lineage exactly as
    /// before; `ImportedRun` writes each origin's rows `Imported`, per
    /// `FactTableRunProvenance.rewrite`. A decorator hands it to the writer
    /// it decorates untouched — an omitted provenance stays omitted.
    ///
    /// `options` (Phase 994) names the inputs the run was computed from and
    /// the period slice it replaces (`FactTableRunOptions`); omitted, or
    /// `FactTableRunOptions.none`, the run replaces the whole table as
    /// before. A writer that cannot honour them refuses the run with
    /// `FactTableRunOptionsRefused`; a decorator passes them on untouched.
    ///
    /// A table takes ONE open run per scope (Phase 994): while another run
    /// of it is open, and within the table's refresh cadence, the open is
    /// refused with `FactTableRunInProgress` naming that run.
    abstract OpenRun:
        scopeId: string * tableId: string * ?provenance: FactTableRunProvenance * ?options: FactTableRunOptions ->
            Async<Result<FactTableRunRecord, FactTableWriteError>>

    /// Stage a batch of rows on an open run. Rows are validated at commit,
    /// not here, so a producer streams without a round trip per defect.
    /// Returns the run's record with its updated staged-row count.
    abstract WriteRows:
        scopeId: string * runId: string * rows: FactTableRow list ->
            Async<Result<FactTableRunRecord, FactTableWriteError>>

    /// Commit an open run: validate every staged row against the
    /// declaration and either reject the whole run (writing nothing, naming
    /// the rows) or swap it in as the table's current content, minting its
    /// watermark, its change summary and ONE audit record.
    abstract Commit: scopeId: string * runId: string -> Async<Result<FactTableCommit, FactTableWriteError>>

    /// End an open run without committing it. For a `Required` table the
    /// run then reads as a failed run.
    abstract Abandon:
        scopeId: string * runId: string * reason: string -> Async<Result<FactTableRunRecord, FactTableWriteError>>

    /// Every run of a table in a scope, most recently opened first.
    abstract Runs: scopeId: string * tableId: string -> Async<Result<FactTableRunRecord list, FactTableWriteError>>

    /// A table's standing: freshness against its cadence, its last commit,
    /// and its latest run's derived outcome.
    abstract Status: scopeId: string * tableId: string -> Async<Result<FactTableStatus, FactTableWriteError>>

/// Pure row validation against a declaration — shared by every writer so
/// "what a valid run is" has one definition.
module FactTableValidation =

    /// The readable subject of a row (`hierarchy/member>member`).
    let subjectText (table: FactTableDefinition) (row: FactTableRow) : string =
        let subject: SubjectRef = {
            Hierarchy = table.Hierarchy
            Path = row.Subject
        }

        SubjectRef.toString subject

    let private shapeOf (value: FactValue) : FactTableValueShape option =
        match value with
        | Scalar _ -> Some FactTableValueShape.Scalar
        | Interval _ -> Some FactTableValueShape.Interval
        | Distribution _ -> Some FactTableValueShape.Distribution
        | Categorical _ -> Some FactTableValueShape.Categorical
        | Series _ -> Some FactTableValueShape.Series
        | Absent _ -> None

    /// Every defect in a run's rows, given the table's declaration and its
    /// registered hierarchy (`None` when the hierarchy is not registered,
    /// which makes every row unplaceable). Rows are `(position, row)`.
    let defects
        (table: FactTableDefinition)
        (hierarchy: SubjectDefinition option)
        (rows: (int * FactTableRow) list)
        : FactTableRowDefect list =
        let depth =
            hierarchy |> Option.bind (fun h -> FactTableDefinition.levelDepth h table.Level)

        let declared = table.Columns |> List.map (fun c -> c.Metric, c.Shape) |> Map.ofList

        let perRow =
            rows
            |> List.collect (fun (position, row) ->
                let problem text : FactTableRowDefect = {
                    Position = position
                    Subject = subjectText table row
                    Problem = text
                }

                [
                    match depth with
                    | None ->
                        problem (
                            sprintf
                                "level '%s' of hierarchy '%s' is not registered, so no subject can sit at it"
                                table.Level
                                table.Hierarchy
                        )
                    | Some d when row.Subject.Length <> d ->
                        problem (
                            sprintf
                                "subject path has %d member(s) but level '%s' sits at depth %d"
                                row.Subject.Length
                                table.Level
                                d
                        )
                    | Some _ -> ()

                    if row.Subject |> List.exists String.IsNullOrWhiteSpace then
                        problem "subject path has an empty member id"

                    if row.Period.From >= row.Period.To then
                        problem "period is not a half-open [From, To) extent"

                    for column in table.Columns do
                        match row.Values |> Map.tryFind column.Metric with
                        | None -> problem (sprintf "no value for column '%s'" column.Metric)
                        | Some value ->
                            match shapeOf value with
                            | Some shape when shape <> column.Shape ->
                                problem (
                                    sprintf
                                        "column '%s' is declared %A but the value is %A"
                                        column.Metric
                                        column.Shape
                                        shape
                                )
                            | _ -> ()

                    for metricId in row.Values |> Map.keys do
                        if not (declared.ContainsKey metricId) then
                            problem (sprintf "'%s' is not a column of this table" metricId)
                ])

        let duplicateKeys =
            rows
            |> List.groupBy (fun (_, row) -> row.Subject, row.Period.From, row.Period.To)
            |> List.collect (fun (_, group) ->
                match group with
                | []
                | [ _ ] -> []
                | (first, _) :: rest ->
                    rest
                    |> List.map (fun (position, row) -> {
                        FactTableRowDefect.Position = position
                        Subject = subjectText table row
                        Problem = sprintf "duplicates the subject-and-period key of row %d" first
                    }))

        perRow @ duplicateKeys |> List.sortBy _.Position

    /// The rows of a run whose period lies outside the slice it was opened
    /// to replace (Phase 994). Empty when the run replaces the whole table.
    let outsideSlice
        (table: FactTableDefinition)
        (slice: TemporalExtent option)
        (rows: (int * FactTableRow) list)
        : FactTableRowDefect list =
        match slice with
        | None -> []
        | Some slice ->
            rows
            |> List.filter (fun (_, row) -> not (FactTableRunOptions.contains slice row.Period.From row.Period.To))
            |> List.map (fun (position, row) -> {
                Position = position
                Subject = subjectText table row
                Problem =
                    sprintf
                        "the period %s..%s lies outside the run's slice %s..%s"
                        (row.Period.From.ToString "o")
                        (row.Period.To.ToString "o")
                        (slice.From.ToString "o")
                        (slice.To.ToString "o")
            })

// ─── Snapshots, digests and change summaries ─────────────────────────

/// One cell of a committed run, as the next commit compares against it.
type FactTableSnapshotCell = {
    /// The column's metric id.
    Metric: string
    /// `Fact.valueHash` of the cell's value.
    ValueHash: string
    /// The scalar value, when the cell is a `Scalar` — the movers read it.
    ScalarValue: decimal option
}

/// One row of a committed run.
type FactTableSnapshotRow = {
    /// Member ids from the hierarchy's root down to the table's level.
    Subject: string list
    /// Start of the row's period (inclusive).
    From: DateTime
    /// End of the row's period (exclusive).
    To: DateTime
    /// The period's optional human label.
    Label: string option
    /// One cell per declared column, in column order.
    Cells: FactTableSnapshotCell list
}

/// The rows a run committed — what the next run's change summary and its
/// removed-row handling are computed against.
type FactTableSnapshot = {
    /// The table the snapshot belongs to.
    TableId: string
    /// The commit sequence that produced it.
    Sequence: int64
    /// The committed rows, in canonical order.
    Rows: FactTableSnapshotRow list
}

/// Pure snapshot construction, digests and change summaries.
module FactTableSnapshot =

    let private sha256Hex (s: string) : string =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes s)
        |> Array.map (sprintf "%02x")
        |> String.concat ""

    let private scalarOf (value: FactValue) : decimal option =
        match value with
        | Scalar d -> Some d
        | _ -> None

    /// The snapshot of a validated run's rows, in canonical order (subject,
    /// then period), each row's cells in column order.
    let ofRows (table: FactTableDefinition) (sequence: int64) (rows: FactTableRow list) : FactTableSnapshot = {
        TableId = table.Id
        Sequence = sequence
        Rows =
            rows
            |> List.map (fun row -> {
                FactTableSnapshotRow.Subject = row.Subject
                From = row.Period.From
                To = row.Period.To
                Label = row.Period.Label
                Cells =
                    table.Columns
                    |> List.map (fun column ->
                        let value = row.Values |> Map.find column.Metric

                        {
                            FactTableSnapshotCell.Metric = column.Metric
                            ValueHash = Fact.valueHash value
                            ScalarValue = scalarOf value
                        })
            })
            |> List.sortBy (fun r -> r.Subject, r.From, r.To)
    }

    let private key (row: FactTableSnapshotRow) = row.Subject, row.From, row.To

    /// SHA-256 over the snapshot's canonical content — the watermark's
    /// content digest. Two runs committing the same rows digest the same.
    let digest (snapshot: FactTableSnapshot) : string =
        snapshot.Rows
        |> List.map (fun r ->
            let cells =
                r.Cells
                |> List.map (fun c -> sprintf "%s=%s" c.Metric c.ValueHash)
                |> String.concat ";"

            sprintf
                "%s|%s|%s|%s"
                (String.concat ">" r.Subject)
                (r.From.ToUniversalTime().ToString("o"))
                (r.To.ToUniversalTime().ToString("o"))
                cells)
        |> String.concat "\n"
        |> sprintf "%s\n%s" snapshot.TableId
        |> sha256Hex

    /// The readable period of a snapshot row.
    let periodText (row: FactTableSnapshotRow) : string =
        match row.Label with
        | Some label -> label
        | None -> sprintf "%s..%s" (row.From.ToString("o")) (row.To.ToString("o"))

    /// The change from `previous` (the last committed run, if any) to
    /// `current`: rows new / changed / unchanged / removed, and the scalar
    /// cells that moved most.
    let changes
        (hierarchy: string)
        (previous: FactTableSnapshot option)
        (current: FactTableSnapshot)
        : FactTableChangeSummary =
        let before =
            previous
            |> Option.map (fun p -> p.Rows |> List.map (fun r -> key r, r) |> Map.ofList)
            |> Option.defaultValue Map.empty

        let now = current.Rows |> List.map (fun r -> key r, r) |> Map.ofList

        // Per current row: `None` when new, else the cells that moved.
        let compared =
            current.Rows
            |> List.map (fun (row: FactTableSnapshotRow) ->
                match before |> Map.tryFind (key row) with
                | None -> row, None
                | Some old ->
                    let oldCells = old.Cells |> List.map (fun c -> c.Metric, c) |> Map.ofList

                    let moved =
                        row.Cells
                        |> List.choose (fun c ->
                            match oldCells |> Map.tryFind c.Metric with
                            | Some o when o.ValueHash = c.ValueHash -> None
                            | o -> Some(c, o))

                    row, Some moved)

        let movers =
            compared
            |> List.collect (fun (row, moved) ->
                moved
                |> Option.defaultValue []
                |> List.choose (fun (c, old) ->
                    match old |> Option.bind _.ScalarValue, c.ScalarValue with
                    | Some previousValue, Some currentValue ->
                        let subject: SubjectRef = {
                            Hierarchy = hierarchy
                            Path = row.Subject
                        }

                        let mover: FactTableMover = {
                            Metric = c.Metric
                            Subject = SubjectRef.toString subject
                            Period = periodText row
                            Previous = previousValue
                            Current = currentValue
                            Delta = currentValue - previousValue
                        }

                        Some mover
                    | _ -> None))

        let count predicate =
            compared |> List.filter (fun (_, moved) -> predicate moved) |> List.length

        let summary: FactTableChangeSummary = {
            New = count Option.isNone
            Changed = count (fun moved -> moved |> Option.exists (List.isEmpty >> not))
            Unchanged = count (fun moved -> moved |> Option.exists List.isEmpty)
            Removed = before |> Map.filter (fun k _ -> not (now.ContainsKey k)) |> Map.count
            LargestMovers =
                movers
                |> List.sortBy (fun m -> -(abs m.Delta), m.Metric, m.Subject)
                |> List.truncate FactTableChangeSummary.MoverCap
        }

        summary

    /// Rows of `previous` that `current` no longer carries.
    let removedRows (previous: FactTableSnapshot option) (current: FactTableSnapshot) : FactTableSnapshotRow list =
        match previous with
        | None -> []
        | Some p ->
            let now = current.Rows |> List.map key |> Set.ofList
            p.Rows |> List.filter (fun r -> not (now.Contains(key r)))

// ─── Audit (GP 6) ────────────────────────────────────────────────────

/// Event types the fact-table writer audits under the fact tier's reserved
/// `_facts` source module — one record per run, never per row.
module FactTableEvents =

    /// A run committed.
    [<Literal>]
    let RunCommittedType = "FactTableRunCommitted"

    /// A run was refused at commit; nothing was written.
    [<Literal>]
    let RunRejectedType = "FactTableRunRejected"

    /// A run was ended without a commit.
    [<Literal>]
    let RunAbandonedType = "FactTableRunAbandoned"

/// Payload of every fact-table run audit record.
type FactTableRunEvent = {
    /// The table the run refreshed.
    TableId: string
    /// The run's identity.
    RunId: string
    /// The declaration's schema version the run was opened under.
    SchemaVersion: int
    /// The table's requirement, so a reader can tell a failed required run
    /// from a discarded optional one without the declaration.
    Required: bool
    /// `Committed` / `Rejected` / `Abandoned`.
    Outcome: string
    /// The commit, for a committed run.
    Commit: FactTableCommit option
    /// The refusal or the producer's reason, for a run that did not commit.
    Reason: string option
    /// Rows staged when the run ended.
    StagedRows: int
}

// ─── The default writer ──────────────────────────────────────────────

/// Blob I/O shared by the default writer (JSON through the platform's F#
/// converter set).
module internal FactTableBlobIo =

    let private jsonOptions = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, jsonOptions)

    let deserialize<'T> (name: string) (bytes: byte[]) : Result<'T, FactTableWriteError> =
        try
            Ok(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, jsonOptions))
        with ex ->
            Error(FactTableStorageFailure(sprintf "'%s' is unreadable: %s" name ex.Message))

    let put
        (storage: IBlobStorage)
        (scopeId: string)
        (name: string)
        (value: 'T)
        : Async<Result<unit, FactTableWriteError>> =
        async {
            match! storage.Upload(scopeId, name, Encoding.UTF8.GetBytes(serialize value)) with
            | Ok _ -> return Ok()
            | Error e -> return Error(FactTableStorageFailure(sprintf "write of '%s' failed: %s" name e))
        }

    let tryGet<'T>
        (storage: IBlobStorage)
        (scopeId: string)
        (name: string)
        : Async<Result<'T option, FactTableWriteError>> =
        async {
            let! exists = storage.Exists(scopeId, name)

            if not exists then
                return Ok None
            else
                match! storage.Download(scopeId, name) with
                | Ok bytes ->
                    try
                        return Ok(Some(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, jsonOptions)))
                    with ex ->
                        return Error(FactTableStorageFailure(sprintf "'%s' is unreadable: %s" name ex.Message))
                | Error e -> return Error(FactTableStorageFailure(sprintf "read of '%s' failed: %s" name e))
        }

/// The pointer a committed run swaps in: the table's current commit.
type FactTableHead = {
    /// The table's current commit sequence.
    Sequence: int64
    /// The run that committed it.
    RunId: string
    /// What that run committed.
    Commit: FactTableCommit
}

/// The claim a table's open run holds in its scope (Phase 994).
type internal FactTableRunClaimRecord = {
    /// The run that claimed the table.
    RunId: string
    /// When it claimed it (second precision, UTC).
    ClaimedAt: DateTime
}

/// One open run per table and scope (Phase 994), shared by both platform
/// writers. A run claims its table by writing a claim blob naming it BEFORE
/// its record exists; the claim is live while the run it names is open and
/// within the table's refresh cadence (the window `FactTableRun.outcome`
/// reads as in progress), and for `Grace` after the claim when the run's
/// record is not written yet. A claim is never released: the run's terminal
/// record ends it, so ending a run needs no second write that could race a
/// new claim.
///
/// On an `IConditionalBlobStorage` the claim is a create-only or
/// `IfMatch`-guarded write, so two concurrent opens of one table admit
/// exactly one. On a store without conditional writes it holds within the
/// precision of the store's read-then-write — the posture the commit's
/// base-sequence check already documents — and that check still refuses
/// the second of two commits.
module internal FactTableRunClaim =

    /// How long a claim whose run record is not yet written stays live: the
    /// opening instance writes the record straight after the claim.
    let Grace = TimeSpan.FromMinutes 1.0

    /// Attempts a conditional claim makes before reporting the winner.
    [<Literal>]
    let private Attempts = 4

    /// Whether the run a claim names still holds the table at `now`.
    let private holds
        (table: FactTableDefinition)
        (now: DateTime)
        (loadRun: string -> Async<Result<FactTableRunRecord option, FactTableWriteError>>)
        (claim: FactTableRunClaimRecord)
        : Async<Result<bool, FactTableWriteError>> =
        async {
            match! loadRun claim.RunId with
            | Error e -> return Error e
            | Ok(Some run) ->
                return
                    Ok(
                        run.Status = FactTableRunStatus.Open
                        && now - run.OpenedAt <= table.RefreshCadence
                    )
            | Ok None -> return Ok(now - claim.ClaimedAt <= Grace)
        }

    /// Claim `table` in `scopeId` for `runId` at blob `name`, or refuse with
    /// `FactTableRunInProgress` naming the run that holds it.
    let acquire
        (storage: IBlobStorage)
        (scopeId: string)
        (name: string)
        (table: FactTableDefinition)
        (now: DateTime)
        (loadRun: string -> Async<Result<FactTableRunRecord option, FactTableWriteError>>)
        (runId: string)
        : Async<Result<unit, FactTableWriteError>> =
        let mine: FactTableRunClaimRecord = { RunId = runId; ClaimedAt = now }
        let bytes = Encoding.UTF8.GetBytes(FactTableBlobIo.serialize mine)

        let inProgress (holder: FactTableRunClaimRecord) =
            Error(FactTableRunInProgress(table.Id, holder.RunId))

        match storage with
        | :? IConditionalBlobStorage as cas ->
            let rec attempt (remaining: int) = async {
                let! exists = storage.Exists(scopeId, name)

                let! current = async {
                    if not exists then
                        return Ok None
                    else
                        match! cas.DownloadWithETag(scopeId, name) with
                        | Ok(content, etag) ->
                            return
                                FactTableBlobIo.deserialize<FactTableRunClaimRecord> name content
                                |> Result.map (fun claim -> Some(claim, etag))
                        // Deleted between the probe and the read: absent.
                        | Error _ when remaining > 1 -> return Ok None
                        | Error e -> return Error(FactTableStorageFailure(sprintf "read of '%s' failed: %s" name e))
                }

                match current with
                | Error e -> return Error e
                | Ok current ->
                    let! live = async {
                        match current with
                        | None -> return Ok None
                        | Some(claim, etag) ->
                            match! holds table now loadRun claim with
                            | Error e -> return Error e
                            | Ok true -> return Ok(Some(Choice1Of2 claim))
                            | Ok false -> return Ok(Some(Choice2Of2 etag))
                    }

                    match live with
                    | Error e -> return Error e
                    | Ok(Some(Choice1Of2 holder)) -> return inProgress holder
                    | Ok decision ->
                        let condition =
                            match decision with
                            | Some(Choice2Of2 etag) -> IfMatch etag
                            | _ -> IfAbsent

                        match! cas.UploadWithETag(scopeId, name, bytes, condition) with
                        | Ok _ -> return Ok()
                        | Error(ETagMismatch _) when remaining > 1 -> return! attempt (remaining - 1)
                        | Error(ETagMismatch _) ->
                            // Lost every race: name whoever holds it now.
                            match! FactTableBlobIo.tryGet<FactTableRunClaimRecord> storage scopeId name with
                            | Ok(Some holder) -> return inProgress holder
                            | Ok None ->
                                return Error(FactTableStorageFailure(sprintf "the claim '%s' kept moving" name))
                            | Error e -> return Error e
                        | Error(ConditionalWriteFailure e) ->
                            return Error(FactTableStorageFailure(sprintf "write of '%s' failed: %s" name e))
            }

            attempt Attempts
        | _ -> async {
            match! FactTableBlobIo.tryGet<FactTableRunClaimRecord> storage scopeId name with
            | Error e -> return Error e
            | Ok current ->
                let! live = async {
                    match current with
                    | None -> return Ok None
                    | Some claim ->
                        match! holds table now loadRun claim with
                        | Error e -> return Error e
                        | Ok true -> return Ok(Some claim)
                        | Ok false -> return Ok None
                }

                match live with
                | Error e -> return Error e
                | Ok(Some holder) -> return inProgress holder
                | Ok None ->
                    match! storage.Upload(scopeId, name, bytes) with
                    | Ok _ -> return Ok()
                    | Error e -> return Error(FactTableStorageFailure(sprintf "write of '%s' failed: %s" name e))
          }

    /// Give up a claim a failed open made, when it still names `runId`, so
    /// the table is not held for `Grace` by a run that never existed.
    let release (storage: IBlobStorage) (scopeId: string) (name: string) (runId: string) : Async<unit> = async {
        match! FactTableBlobIo.tryGet<FactTableRunClaimRecord> storage scopeId name with
        | Ok(Some claim) when claim.RunId = runId ->
            let! _ = storage.Delete(scopeId, name) // best-effort-write: a claim left behind names a run with no record, which holds the table only for Grace
            ()
        | _ -> ()
    }

/// Keeping and applying a run's provenance.
module FactTableRunProvenance =

    /// The prefix the default writer keeps every run's provenance under.
    [<Literal>]
    let internal Root = "_fact-tables/provenance/"

    /// The blob the default writer keeps an imported run's provenance in,
    /// beside the run's staged rows, in the run's scope (rule 4: any
    /// instance can commit a run another opened). Nothing is kept for a
    /// computed run.
    let internal blobName (runId: string) = sprintf "%s%s.json" Root runId

    /// Keep a run's provenance at open: an imported one is written, a
    /// computed one writes nothing (GP 11).
    let internal keep
        (storage: IBlobStorage)
        (scopeId: string)
        (name: string)
        (provenance: FactTableRunProvenance)
        : Async<Result<unit, FactTableWriteError>> =
        async {
            match provenance with
            | ComputedRun -> return Ok()
            | ImportedRun _ -> return! FactTableBlobIo.put storage scopeId name provenance
        }

    /// A run's kept provenance: `ComputedRun` when none is kept.
    let internal read
        (storage: IBlobStorage)
        (scopeId: string)
        (name: string)
        : Async<Result<FactTableRunProvenance, FactTableWriteError>> =
        async {
            match! FactTableBlobIo.tryGet<FactTableRunProvenance> storage scopeId name with
            | Ok(Some provenance) -> return Ok provenance
            | Ok None -> return Ok ComputedRun
            | Error e -> return Error e
        }

    /// The draft rewrite a provenance implies: identity for `ComputedRun`.
    /// ONE definition of what an imported row is, shared by every writer —
    /// apply it to each draft a run of the provenance mints.
    let rewrite (provenance: FactTableRunProvenance) : FactDraft -> FactDraft =
        match provenance with
        | ComputedRun -> id
        | ImportedRun origins ->
            let byRoot = origins |> List.map (fun o -> o.RootMember, o) |> Map.ofList

            let cells =
                origins
                |> Seq.collect (fun o ->
                    o.Cells |> Seq.map (fun c -> (c.Subject, c.Metric, c.From, c.To), c.Disclosure))
                |> Map.ofSeq

            fun (draft: FactDraft) ->
                match draft.Subject.Path with
                | root :: _ ->
                    match byRoot.TryFind root with
                    | None -> draft
                    | Some origin ->
                        let value =
                            match draft.Value, origin.Withdrawal with
                            | Absent _, Some reason -> Absent reason
                            | value, _ -> value

                        let disclosure =
                            match draft.Value with
                            | Absent _ -> draft.Disclosure
                            | _ ->
                                match
                                    cells.TryFind(
                                        draft.Subject.Path,
                                        draft.Metric.Value,
                                        draft.Period.From,
                                        draft.Period.To
                                    )
                                with
                                | Some published -> Disclosure.floor published draft.Disclosure
                                // A cell the origin did not place is narrowed
                                // to the bottom rather than trusted.
                                | None -> Disclosure.Internal

                        {
                            draft with
                                Value = value
                                Method = Imported origin.CertificateRef
                                Disclosure = disclosure
                                Evidence = {
                                    draft.Evidence with
                                        TriggerRef = Some origin.TriggerRef
                                }
                        }
                | [] -> draft

/// What one orphan sweep reclaimed (Phase 977): the leftovers of runs the
/// run ledger records as ended — staged rows, and kept provenance the
/// writer does not keep past the run's end.
type FactTableOrphanSweepReport = {
    /// Ended runs the sweep found leftovers of.
    RunsSwept: int
    /// Staged-row blobs deleted.
    StagedBlobsDeleted: int
    /// Kept-provenance blobs deleted.
    ProvenanceBlobsDeleted: int
    /// Blobs whose delete the store refused, each with the refusal. They are
    /// still at rest and still listed, so the next sweep retries them.
    Refused: string list
    /// Runs left untouched because their record could not be read, each
    /// with the reason: a run not PROVEN ended is never swept.
    Unreadable: string list
}

/// Combining sweep reports.
module FactTableOrphanSweepReport =

    /// A sweep that found nothing to reclaim.
    let empty: FactTableOrphanSweepReport = {
        RunsSwept = 0
        StagedBlobsDeleted = 0
        ProvenanceBlobsDeleted = 0
        Refused = []
        Unreadable = []
    }

    /// Two sweeps' reports as one — a writer and the writer it decorates.
    let combine (a: FactTableOrphanSweepReport) (b: FactTableOrphanSweepReport) : FactTableOrphanSweepReport = {
        RunsSwept = a.RunsSwept + b.RunsSwept
        StagedBlobsDeleted = a.StagedBlobsDeleted + b.StagedBlobsDeleted
        ProvenanceBlobsDeleted = a.ProvenanceBlobsDeleted + b.ProvenanceBlobsDeleted
        Refused = a.Refused @ b.Refused
        Unreadable = a.Unreadable @ b.Unreadable
    }

/// A fact-table writer that reclaims the leftovers of its ended runs
/// (Phase 977). A writer stages a run's rows, and keeps an imported run's
/// provenance, as blobs it deletes when the run ends; a refused delete
/// there leaves bytes nothing reads again. The sweep finds them by the
/// record that proves them leftovers — the run's terminal record — so a run
/// that is open, or whose record does not exist yet, is never touched, and a
/// sweep is safe beside a live run. Idempotent: a second sweep over the same
/// state reclaims nothing.
///
/// Both platform writers implement it and run it over the run's scope at
/// the end of every run they end (commit, rejection, abandonment), so a
/// refused delete is retried by the scope's next run. A decorator forwards
/// it to the writer it decorates.
type IFactTableOrphanSweep =
    /// Sweep a scope: delete the staged rows, and the provenance the writer
    /// does not keep, of every run its ledger records as ended, and report
    /// what went and what the store refused.
    abstract SweepOrphans: scopeId: string -> Async<Result<FactTableOrphanSweepReport, FactTableWriteError>>

/// Running the orphan sweep, and the sweep the platform writers share.
module FactTableOrphanSweep =

    /// Sweep a scope through a composed writer: `SweepOrphans` when the
    /// writer implements `IFactTableOrphanSweep`, otherwise an empty report
    /// (a writer that keeps no leftovers has none to reclaim).
    let sweep
        (writer: IFactTableWriter)
        (scopeId: string)
        : Async<Result<FactTableOrphanSweepReport, FactTableWriteError>> =
        match box writer with
        | :? IFactTableOrphanSweep as sweeper -> sweeper.SweepOrphans scopeId
        | _ -> async { return Ok FactTableOrphanSweepReport.empty }

    /// Where a writer keeps a run's leftovers.
    type internal Layout = {
        /// The prefix every run's staged rows sit under, as `<root><runId>/…`.
        StagedRoot: string
        /// The prefix every run's kept provenance sits under, as `<root><runId>.json`.
        ProvenanceRoot: string
        /// The blob a run's record is kept in.
        RecordName: string -> string
        /// Whether an ended run's provenance is kept past its end (the
        /// delegate writer keeps a committed run's: its rows are minted
        /// under it for as long as they are read).
        KeepsProvenance: FactTableRunStatus -> bool
    }

    let private runOfStaged (layout: Layout) (name: string) : string option =
        if name.StartsWith layout.StagedRoot then
            let rest = name.Substring layout.StagedRoot.Length

            match rest.IndexOf '/' with
            | slash when slash > 0 -> Some(rest.Substring(0, slash))
            | _ -> None
        else
            None

    let private runOfProvenance (layout: Layout) (name: string) : string option =
        if name.StartsWith layout.ProvenanceRoot then
            let rest = name.Substring layout.ProvenanceRoot.Length

            if rest.EndsWith ".json" && rest.Length > 5 && not (rest.Contains '/') then
                Some(rest.Substring(0, rest.Length - 5))
            else
                None
        else
            None

    let private deleteAll (storage: IBlobStorage) (scopeId: string) (names: string list) : Async<int * string list> =
        let rec go (remaining: string list) (deleted: int) (refused: string list) = async {
            match remaining with
            | [] -> return deleted, List.rev refused
            | name :: rest ->
                match! storage.Delete(scopeId, name) with
                | Ok() -> return! go rest (deleted + 1) refused
                | Error e -> return! go rest deleted (sprintf "%s (%s)" name e :: refused)
        }

        go names 0 []

    /// Sweep one scope's leftovers under `layout`. `spared` names runs whose
    /// provenance is known kept, so the sweep need not read their records.
    let internal run
        (storage: IBlobStorage)
        (scopeId: string)
        (layout: Layout)
        (spared: Set<string>)
        : Async<FactTableOrphanSweepReport> =
        async {
            let! staged = storage.List(scopeId, layout.StagedRoot)
            let! kept = storage.List(scopeId, layout.ProvenanceRoot)

            let stagedByRun =
                staged
                |> List.choose (fun name -> runOfStaged layout name |> Option.map (fun runId -> runId, name))
                |> List.groupBy fst
                |> List.map (fun (runId, names) -> runId, names |> List.map snd |> List.sort)
                |> Map.ofList

            let provenanceByRun =
                kept
                |> List.choose (fun name -> runOfProvenance layout name |> Option.map (fun runId -> runId, name))
                |> List.filter (fun (runId, _) -> not (spared.Contains runId))
                |> Map.ofList

            let candidates =
                Set.union (stagedByRun |> Map.keys |> Set.ofSeq) (provenanceByRun |> Map.keys |> Set.ofSeq)
                |> Set.toList

            let rec go (remaining: string list) (report: FactTableOrphanSweepReport) = async {
                match remaining with
                | [] -> return report
                | runId :: rest ->
                    match! FactTableBlobIo.tryGet<FactTableRunRecord> storage scopeId (layout.RecordName runId) with
                    | Error e ->
                        let why = sprintf "%s (%s)" runId (FactTableWriteError.describe e)

                        return!
                            go rest {
                                report with
                                    Unreadable = report.Unreadable @ [ why ]
                            }
                    // No record yet: a run is being opened (its provenance is
                    // kept before its record) — never touched.
                    | Ok None -> return! go rest report
                    | Ok(Some run) ->
                        match run.Status with
                        | FactTableRunStatus.Open -> return! go rest report
                        | status ->
                            let stagedNames = stagedByRun.TryFind runId |> Option.defaultValue []

                            let provenanceNames =
                                match provenanceByRun.TryFind runId with
                                | Some name when not (layout.KeepsProvenance status) -> [ name ]
                                | _ -> []

                            if List.isEmpty stagedNames && List.isEmpty provenanceNames then
                                return! go rest report
                            else
                                let! stagedDeleted, stagedRefused = deleteAll storage scopeId stagedNames
                                let! provenanceDeleted, provenanceRefused = deleteAll storage scopeId provenanceNames

                                return!
                                    go rest {
                                        report with
                                            RunsSwept = report.RunsSwept + 1
                                            StagedBlobsDeleted = report.StagedBlobsDeleted + stagedDeleted
                                            ProvenanceBlobsDeleted = report.ProvenanceBlobsDeleted + provenanceDeleted
                                            Refused = report.Refused @ stagedRefused @ provenanceRefused
                                    }
            }

            return! go candidates FactTableOrphanSweepReport.empty
        }

/// The default `IFactTableWriter`, over the composed `IFactStore` — so a
/// declared table is usable before any dedicated table store is composed,
/// and its rows are ordinary facts every population and point read already
/// answers over.
///
/// **What it writes.** Each cell is one fact: the row's subject at the
/// table's level, the column's metric, the row's period, method
/// `Computed(producingOperation, "v<schemaVersion>", tableId)` — so the
/// table's facts are their own lineage and never merge with another
/// producer's — evidence naming the run's watermark, and the column's
/// disclosure — unless the run was opened with an imported provenance
/// (`FactTableRunProvenance`, Phases 932 and 938), when the rows under each
/// origin are written `Imported`. `AppendByRun` puts the watermark in each fact's content
/// address, so every run is a complete attributable snapshot; `Replace`
/// leaves it out, so an unchanged cell is an idempotent skip. A row the new
/// run no longer carries is superseded by an `Absent` fact per cell, so the
/// table's current population is exactly the latest run — or, for a run
/// opened with a period slice (Phase 994), the latest run inside the slice
/// and every committed row outside it, unrewritten. A run's declared inputs
/// join every fact's input hashes.
///
/// **What "atomic" means here.** Validation is all-or-nothing and runs
/// before the first write: a rejected run writes no fact. The facts of an
/// accepted run go through one `AssertBatch`, and the table's current run —
/// its head pointer, snapshot and change summary — moves in one write after
/// that batch succeeds. A storage failure part-way through the batch leaves
/// the head where it was and the run open; the facts already written are
/// content-addressed, so re-committing is exact (the Phase 704 posture).
/// A deployment wanting a single physical transaction composes a table
/// store behind this seam.
///
/// **Size.** Correct at any size; cost is linear in the run plus the
/// previous run's snapshot, and it is efficient at small. Runs are staged
/// and snapshots kept as blobs beside the scope's facts (container = scope,
/// GP 4), under `_fact-tables/`.
///
/// **Distributed-ready**, single producer per table: all state lives in the
/// backing stores (rule 4). A table takes one open run per scope
/// (`FactTableRunClaim`, Phase 994). Two runs of one table committing
/// concurrently — possible only once one has outlived the table's cadence —
/// are serialised by the base-sequence check: the later one is refused as
/// `FactTableCommitConflict`, within the precision of the backing blob
/// store's read-then-write (no compare-and-swap is assumed of it).
type DefaultFactTableWriter
    (
        facts: IFactStore,
        storage: IBlobStorage,
        events: IEventStore,
        tables: IFactTableRegistry,
        registry: IMetricRegistry option,
        clock: unit -> DateTime,
        destination: string
    ) =

    let root = "_fact-tables/"
    let recordName (runId: string) = sprintf "%sruns/%s.json" root runId
    let recordsPrefix = sprintf "%sruns/" root
    let rowsPrefix (runId: string) = sprintf "%srows/%s/" root runId

    let batchName (runId: string) (offset: int) =
        sprintf "%s%010d.json" (rowsPrefix runId) offset

    let headName (tableId: string) = sprintf "%shead/%s.json" root tableId

    let snapshotName (tableId: string) (sequence: int64) =
        sprintf "%ssnapshots/%s/%020d.json" root tableId sequence

    // Phase 994 — the claim a table's open run holds, and the options a run
    // was opened with. The options are staged with the run's rows: only the
    // commit reads them, and the orphan sweep reclaims them with the rows.
    let claimName (tableId: string) = sprintf "%sopen/%s.json" root tableId

    let optionsName (runId: string) =
        sprintf "%soptions.json" (rowsPrefix runId)

    let now () =
        let t = clock().ToUniversalTime()
        DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc)

    let hierarchyOf (table: FactTableDefinition) : SubjectDefinition option =
        registry |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)

    let declared (tableId: string) : Result<FactTableDefinition, FactTableWriteError> =
        match tables.TryGetTable tableId with
        | None -> Error(FactTableUndeclared tableId)
        | Some table ->
            match tables.DestinationOf tableId with
            | Some bound when bound = destination -> Ok table
            | boundTo -> Error(FactTableNotBoundHere(tableId, boundTo, destination))

    let loadRun (scopeId: string) (runId: string) : Async<Result<FactTableRunRecord, FactTableWriteError>> = async {
        match! FactTableBlobIo.tryGet<FactTableRunRecord> storage scopeId (recordName runId) with
        | Ok(Some run) -> return Ok run
        | Ok None -> return Error(FactTableRunUnknown runId)
        | Error e -> return Error e
    }

    let statusName (status: FactTableRunStatus) : string =
        match status with
        | FactTableRunStatus.Open -> "open"
        | FactTableRunStatus.Committed _ -> "committed"
        | FactTableRunStatus.Rejected _ -> "rejected"
        | FactTableRunStatus.Abandoned _ -> "abandoned"

    let openRun (scopeId: string) (runId: string) : Async<Result<FactTableRunRecord, FactTableWriteError>> = async {
        match! loadRun scopeId runId with
        | Error e -> return Error e
        | Ok run ->
            match run.Status with
            | FactTableRunStatus.Open -> return Ok run
            | other -> return Error(FactTableRunClosed(runId, statusName other))
    }

    let currentSequence
        (scopeId: string)
        (tableId: string)
        : Async<Result<int64 * FactTableHead option, FactTableWriteError>> =
        async {
            match! FactTableBlobIo.tryGet<FactTableHead> storage scopeId (headName tableId) with
            | Ok(Some head) -> return Ok(head.Sequence, Some head)
            | Ok None -> return Ok(0L, None)
            | Error e -> return Error e
        }

    let audit
        (scopeId: string)
        (table: FactTableDefinition)
        (run: FactTableRunRecord)
        (eventType: string)
        (outcome: string)
        (commit: FactTableCommit option)
        (reason: string option)
        : Async<unit> =
        let payload: FactTableRunEvent = {
            TableId = table.Id
            RunId = run.RunId
            SchemaVersion = run.SchemaVersion
            Required = (table.Requirement = FactTableRequirement.Required)
            Outcome = outcome
            Commit = commit
            Reason = reason
            StagedRows = run.StagedRows
        }

        events.Write {
            Id = Guid.NewGuid()
            OccurredAt = now ()
            ScopeId = scopeId
            SourceModule = FactEvents.SourceModule
            EventType = eventType
            Payload = FactTableBlobIo.serialize payload
        }

    // Phase 977 — the orphan sweep over this writer's layout. A run's staged
    // rows and its kept provenance are read by nothing once its record is
    // terminal (`stagedRows` reads an open run only, and the facts a commit
    // writes carry their provenance), so an ended run keeps neither.
    let orphanLayout: FactTableOrphanSweep.Layout = {
        StagedRoot = sprintf "%srows/" root
        ProvenanceRoot = FactTableRunProvenance.Root
        RecordName = recordName
        KeepsProvenance = fun _ -> false
    }

    let sweepOrphans (scopeId: string) : Async<FactTableOrphanSweepReport> =
        FactTableOrphanSweep.run storage scopeId orphanLayout Set.empty

    // The end of every run: its terminal record is persisted, so the sweep
    // reclaims its staged rows and provenance, and retries any earlier run's
    // the store refused. The report is the sweep's own: what a refused delete
    // leaves stays listed, and the scope's next run (or a host's
    // `SweepOrphans`) sweeps it again.
    let endRun (scopeId: string) : Async<unit> = async {
        let! _ = sweepOrphans scopeId
        ()
    }

    let stagedRows (scopeId: string) (runId: string) : Async<Result<FactTableRow list, FactTableWriteError>> = async {
        let! names = storage.List(scopeId, rowsPrefix runId)

        let rec load (remaining: string list) (acc: FactTableRow list list) = async {
            match remaining with
            | [] -> return Ok(acc |> List.rev |> List.concat)
            | name :: rest ->
                match! FactTableBlobIo.tryGet<FactTableRow list> storage scopeId name with
                | Ok(Some rows) -> return! load rest (rows :: acc)
                | Ok None -> return! load rest acc
                | Error e -> return Error e
        }

        // Offsets are zero-padded, so name order is staging order; the run's
        // options sit beside its batches and are not rows.
        return! load (names |> List.filter (fun n -> n <> optionsName runId) |> List.sort) []
    }

    let disclosureOf (d: FactTableDisclosure) : Disclosure =
        match d with
        | FactTableDisclosure.Surfaceable -> Disclosure.Surfaceable
        | FactTableDisclosure.Internal -> Disclosure.Internal
        | FactTableDisclosure.Restricted policy -> Disclosure.Restricted policy

    let draftsFor
        (table: FactTableDefinition)
        (runId: string)
        (provenance: FactTableRunProvenance)
        (options: FactTableRunOptions)
        (watermark: FactTableWatermark)
        (rows: FactTableRow list)
        (removed: FactTableSnapshotRow list)
        : FactDraft list =
        let token = FactTableWatermark.render watermark

        let method =
            Computed(table.ProducingOperation, sprintf "v%d" table.SchemaVersion, table.Id)

        let inputHashes (value: FactValue) =
            match table.HistoryMode with
            | FactTableHistoryMode.AppendByRun -> [ Fact.valueHash value; token ]
            | FactTableHistoryMode.Replace -> [ Fact.valueHash value ]

        let draft
            (subject: string list)
            (period: TemporalExtent)
            (column: FactTableColumn)
            (value: FactValue)
            : FactDraft =
            {
                Subject = {
                    SubjectRef.Hierarchy = table.Hierarchy
                    Path = subject
                }
                Metric = MetricRef column.Metric
                Value = value
                Period = period
                Method = method
                Evidence = {
                    ResultRef = Some token
                    InputHashes = inputHashes value
                    TriggerRef = Some(sprintf "fact-table-run:%s/%s" table.Id runId)
                }
                Confidence = None
                Disclosure = disclosureOf (FactTableDefinition.columnDisclosure table column)
            }

        let present =
            rows
            |> List.collect (fun row ->
                table.Columns
                |> List.map (fun column -> draft row.Subject row.Period column (row.Values |> Map.find column.Metric)))

        let absences =
            removed
            |> List.collect (fun row ->
                let period: TemporalExtent = {
                    From = row.From
                    To = row.To
                    Label = row.Label
                }

                table.Columns
                |> List.map (fun column ->
                    draft row.Subject period column (Absent(sprintf "removed by fact-table run %s" token))))

        present @ absences
        |> List.map (FactTableRunOptions.rewrite options >> FactTableRunProvenance.rewrite provenance)

    let reject
        (scopeId: string)
        (table: FactTableDefinition)
        (run: FactTableRunRecord)
        (error: FactTableWriteError)
        : Async<Result<FactTableCommit, FactTableWriteError>> =
        async {
            let reason = FactTableWriteError.describe error

            let closed = {
                run with
                    Status = FactTableRunStatus.Rejected reason
            }

            match! FactTableBlobIo.put storage scopeId (recordName run.RunId) closed with
            | Error e -> return Error e
            | Ok() ->
                do! endRun scopeId
                do! audit scopeId table closed FactTableEvents.RunRejectedType "Rejected" None (Some reason)
                return Error error
        }

    interface IFactTableWriter with

        member _.OpenRun(scopeId, tableId, ?provenance, ?options) = async {
            match declared tableId with
            | Error e -> return Error e
            | Ok table ->
                match FactTableRunOptions.normalise (defaultArg options FactTableRunOptions.none) with
                | Error reason -> return Error(FactTableRunOptionsRefused(table.Id, reason))
                | Ok options ->
                    let runId = Guid.NewGuid().ToString("N")
                    let openedAt = now ()

                    // The claim first (Phase 994): one open run per table and
                    // scope, and the base sequence is read once it is held.
                    match!
                        FactTableRunClaim.acquire
                            storage
                            scopeId
                            (claimName table.Id)
                            table
                            openedAt
                            (fun id -> FactTableBlobIo.tryGet<FactTableRunRecord> storage scopeId (recordName id))
                            runId
                    with
                    | Error e -> return Error e
                    | Ok() ->
                        let! opened = async {
                            match! currentSequence scopeId tableId with
                            | Error e -> return Error e
                            | Ok(sequence, _) ->
                                let run: FactTableRunRecord = {
                                    TableId = table.Id
                                    RunId = runId
                                    SchemaVersion = table.SchemaVersion
                                    OpenedAt = openedAt
                                    BaseSequence = sequence
                                    StagedRows = 0
                                    Status = FactTableRunStatus.Open
                                }

                                // The provenance and the options first: a run
                                // whose record exists is a run, so it is never
                                // visible without what it was opened with. No
                                // options, nothing kept (GP 11).
                                match!
                                    FactTableRunProvenance.keep
                                        storage
                                        scopeId
                                        (FactTableRunProvenance.blobName run.RunId)
                                        (defaultArg provenance ComputedRun)
                                with
                                | Error e -> return Error e
                                | Ok() ->
                                    let! kept =
                                        if FactTableRunOptions.isNone options then
                                            async { return Ok() }
                                        else
                                            FactTableBlobIo.put storage scopeId (optionsName run.RunId) options

                                    match kept with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        match! FactTableBlobIo.put storage scopeId (recordName run.RunId) run with
                                        | Error e -> return Error e
                                        | Ok() -> return Ok run
                        }

                        match opened with
                        | Ok run -> return Ok run
                        | Error e ->
                            do! FactTableRunClaim.release storage scopeId (claimName table.Id) runId
                            return Error e
        }

        member _.WriteRows(scopeId, runId, rows) = async {
            match! openRun scopeId runId with
            | Error e -> return Error e
            | Ok run ->
                if List.isEmpty rows then
                    return Ok run
                else
                    match! FactTableBlobIo.put storage scopeId (batchName runId run.StagedRows) rows with
                    | Error e -> return Error e
                    | Ok() ->
                        let staged = {
                            run with
                                StagedRows = run.StagedRows + rows.Length
                        }

                        match! FactTableBlobIo.put storage scopeId (recordName runId) staged with
                        | Error e -> return Error e
                        | Ok() -> return Ok staged
        }

        member _.Commit(scopeId, runId) = async {
            match! openRun scopeId runId with
            | Error e -> return Error e
            | Ok run ->
                match declared run.TableId with
                | Error e -> return Error e
                | Ok table ->
                    match! currentSequence scopeId table.Id with
                    | Error e -> return Error e
                    | Ok(sequence, head) ->
                        if sequence <> run.BaseSequence then
                            return!
                                reject scopeId table run (FactTableCommitConflict(runId, run.BaseSequence, sequence))
                        else
                            let! staged = async {
                                match! stagedRows scopeId runId with
                                | Error e -> return Error e
                                | Ok rows ->
                                    match!
                                        FactTableRunProvenance.read
                                            storage
                                            scopeId
                                            (FactTableRunProvenance.blobName runId)
                                    with
                                    | Error e -> return Error e
                                    | Ok provenance ->
                                        match!
                                            FactTableBlobIo.tryGet<FactTableRunOptions>
                                                storage
                                                scopeId
                                                (optionsName runId)
                                        with
                                        | Error e -> return Error e
                                        | Ok options ->
                                            return
                                                Ok(
                                                    rows,
                                                    provenance,
                                                    options |> Option.defaultValue FactTableRunOptions.none
                                                )
                            }

                            match staged with
                            | Error e -> return Error e
                            | Ok(rows, provenance, options) ->
                                let defects =
                                    FactTableValidation.defects table (hierarchyOf table) (List.indexed rows)
                                    @ FactTableValidation.outsideSlice table options.Slice (List.indexed rows)
                                    |> List.sortBy _.Position

                                match defects with
                                | _ :: _ as defects ->
                                    return! reject scopeId table run (FactTableRowsRejected(runId, defects))
                                | [] ->
                                    let! previous = async {
                                        match head with
                                        | None -> return Ok None
                                        | Some h ->
                                            let name = snapshotName table.Id h.Sequence

                                            match! FactTableBlobIo.tryGet<FactTableSnapshot> storage scopeId name with
                                            | Ok(Some snapshot) -> return Ok(Some snapshot)
                                            // A head names its snapshot: a missing one is
                                            // never read as an empty table, which would
                                            // withdraw nothing and drop every carried row.
                                            | Ok None ->
                                                return
                                                    Error(
                                                        FactTableStorageFailure(
                                                            sprintf "the table's current snapshot '%s' is missing" name
                                                        )
                                                    )
                                            | Error e -> return Error e
                                    }

                                    match previous with
                                    | Error e -> return Error e
                                    | Ok previous ->
                                        let next = sequence + 1L
                                        let written = FactTableSnapshot.ofRows table next rows

                                        // Phase 994 — the rows the run replaces: the
                                        // whole table, or the committed rows inside its
                                        // slice. A committed row the slice would split
                                        // refuses the run.
                                        let inSlice (row: FactTableSnapshotRow) =
                                            options.Slice
                                            |> Option.forall (fun slice ->
                                                FactTableRunOptions.contains slice row.From row.To)

                                        let split =
                                            match options.Slice, previous with
                                            | Some slice, Some p ->
                                                p.Rows
                                                |> List.filter (fun r -> FactTableRunOptions.splits slice r.From r.To)
                                            | _ -> []

                                        match split with
                                        | first :: _ ->
                                            let subject: SubjectRef = {
                                                Hierarchy = table.Hierarchy
                                                Path = first.Subject
                                            }

                                            let reason =
                                                sprintf
                                                    "the slice splits %d committed row(s), the first %s at %s; a slice must contain each committed row it overlaps"
                                                    split.Length
                                                    (SubjectRef.toString subject)
                                                    (FactTableSnapshot.periodText first)

                                            return!
                                                reject scopeId table run (FactTableRunOptionsRefused(table.Id, reason))
                                        | [] ->
                                            let replaced =
                                                previous
                                                |> Option.map (fun p -> {
                                                    p with
                                                        Rows = p.Rows |> List.filter inSlice
                                                })

                                            let carried =
                                                previous
                                                |> Option.map (fun p -> p.Rows |> List.filter (inSlice >> not))
                                                |> Option.defaultValue []

                                            // The table after the commit: the carried rows
                                            // and the run's, in canonical order.
                                            let snapshot = {
                                                written with
                                                    Rows =
                                                        carried @ written.Rows
                                                        |> List.sortBy (fun r -> r.Subject, r.From, r.To)
                                            }

                                            let committedAt = now ()

                                            let watermark: FactTableWatermark = {
                                                TableId = table.Id
                                                Sequence = next
                                                CommittedAt = committedAt
                                                ContentDigest = FactTableSnapshot.digest snapshot
                                            }

                                            let removed = FactTableSnapshot.removedRows replaced written

                                            let drafts = draftsFor table runId provenance options watermark rows removed

                                            match! facts.AssertBatch(scopeId, drafts) with
                                            | Error e ->
                                                return
                                                    Error(
                                                        FactTableStorageFailure(
                                                            sprintf "the fact store refused the run's facts: %s" e
                                                        )
                                                    )
                                            | Ok receipt ->
                                                let commit: FactTableCommit = {
                                                    Watermark = watermark
                                                    RowCount = rows.Length
                                                    FactsWritten = receipt.AssertedCount + receipt.SupersedingCount
                                                    Change = FactTableSnapshot.changes table.Hierarchy replaced written
                                                    BatchDigest = receipt.Digest
                                                }

                                                let committed = {
                                                    run with
                                                        Status = FactTableRunStatus.Committed commit
                                                }

                                                let newHead: FactTableHead = {
                                                    Sequence = next
                                                    RunId = runId
                                                    Commit = commit
                                                }

                                                match!
                                                    FactTableBlobIo.put
                                                        storage
                                                        scopeId
                                                        (snapshotName table.Id next)
                                                        snapshot
                                                with
                                                | Error e -> return Error e
                                                | Ok() ->
                                                    // The swap: the table's current run moves here.
                                                    match!
                                                        FactTableBlobIo.put storage scopeId (headName table.Id) newHead
                                                    with
                                                    | Error e -> return Error e
                                                    | Ok() ->
                                                        match!
                                                            FactTableBlobIo.put
                                                                storage
                                                                scopeId
                                                                (recordName runId)
                                                                committed
                                                        with
                                                        | Error e -> return Error e
                                                        | Ok() ->
                                                            // Phase 994 — the superseded snapshot is
                                                            // read by nothing once the head moved, so
                                                            // a table keeps one snapshot, not one per
                                                            // commit. A refused delete leaves bytes
                                                            // nothing reads; the commit stands.
                                                            match head with
                                                            | Some h ->
                                                                let superseded = snapshotName table.Id h.Sequence
                                                                let! _ = storage.Delete(scopeId, superseded) // best-effort-write: a surviving superseded snapshot is read by nothing; the head names the current one

                                                                ()
                                                            | None -> ()

                                                            do! endRun scopeId

                                                            do!
                                                                audit
                                                                    scopeId
                                                                    table
                                                                    committed
                                                                    FactTableEvents.RunCommittedType
                                                                    "Committed"
                                                                    (Some commit)
                                                                    None

                                                            return Ok commit
        }

        member _.Abandon(scopeId, runId, reason) = async {
            match! openRun scopeId runId with
            | Error e -> return Error e
            | Ok run ->
                let closed = {
                    run with
                        Status = FactTableRunStatus.Abandoned reason
                }

                match! FactTableBlobIo.put storage scopeId (recordName runId) closed with
                | Error e -> return Error e
                | Ok() ->
                    do! endRun scopeId

                    match tables.TryGetTable run.TableId with
                    | Some table ->
                        do! audit scopeId table closed FactTableEvents.RunAbandonedType "Abandoned" None (Some reason)
                    | None -> ()

                    return Ok closed
        }

        member _.Runs(scopeId, tableId) = async {
            match declared tableId with
            | Error e -> return Error e
            | Ok table ->
                let! names = storage.List(scopeId, recordsPrefix)

                let rec load (remaining: string list) (acc: FactTableRunRecord list) = async {
                    match remaining with
                    | [] -> return Ok acc
                    | name :: rest ->
                        match! FactTableBlobIo.tryGet<FactTableRunRecord> storage scopeId name with
                        | Ok(Some run) when run.TableId = table.Id -> return! load rest (run :: acc)
                        | Ok _ -> return! load rest acc
                        | Error e -> return Error e
                }

                match! load names [] with
                | Error e -> return Error e
                | Ok runs -> return Ok(runs |> List.sortByDescending (fun r -> r.OpenedAt, r.BaseSequence, r.RunId))
        }

        member this.Status(scopeId, tableId) = async {
            match declared tableId with
            | Error e -> return Error e
            | Ok table ->
                match! currentSequence scopeId tableId with
                | Error e -> return Error e
                | Ok(_, head) ->
                    match! (this :> IFactTableWriter).Runs(scopeId, tableId) with
                    | Error e -> return Error e
                    | Ok runs ->
                        let at = now ()
                        let lastCommit = head |> Option.map _.Commit
                        let latest = runs |> List.tryHead

                        let status: FactTableStatus = {
                            TableId = table.Id
                            Freshness =
                                FactTableFreshness.derive
                                    table.RefreshCadence
                                    at
                                    (lastCommit |> Option.map _.Watermark.CommittedAt)
                            LastCommit = lastCommit
                            LatestRun = latest
                            LatestRunOutcome = latest |> Option.map (FactTableRun.outcome table at)
                        }

                        return Ok status
        }

    interface IFactTableOrphanSweep with

        member _.SweepOrphans(scopeId) = async {
            let! report = sweepOrphans scopeId
            return Ok report
        }

/// Construction for the default writer.
module DefaultFactTableWriter =

    /// The destination label the default writer serves — what
    /// `FactsCompose.withFactTableWriter` binds every declared table to.
    [<Literal>]
    let Destination = "fact-store"

    /// The default writer over a fact store, with the system clock.
    let create
        (facts: IFactStore)
        (storage: IBlobStorage)
        (events: IEventStore)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        : IFactTableWriter =
        DefaultFactTableWriter(facts, storage, events, tables, registry, (fun () -> DateTime.UtcNow), Destination)
        :> IFactTableWriter

    /// `create` with an explicit clock (test seam / deterministic replay).
    let createWithClock
        (facts: IFactStore)
        (storage: IBlobStorage)
        (events: IEventStore)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        (clock: unit -> DateTime)
        : IFactTableWriter =
        DefaultFactTableWriter(facts, storage, events, tables, registry, clock, Destination) :> IFactTableWriter