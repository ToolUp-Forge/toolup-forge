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

/// The seam a producer writes a declared fact table through.
type IFactTableWriter =
    /// Open a run of a declared table in a scope. The run stages rows and
    /// makes nothing visible until it commits.
    abstract OpenRun: scopeId: string * tableId: string -> Async<Result<FactTableRunRecord, FactTableWriteError>>

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
/// disclosure. `AppendByRun` puts the watermark in each fact's content
/// address, so every run is a complete attributable snapshot; `Replace`
/// leaves it out, so an unchanged cell is an idempotent skip. A row the new
/// run no longer carries is superseded by an `Absent` fact per cell, so the
/// table's current population is exactly the latest run.
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
/// backing stores (rule 4). Two runs of one table committing concurrently
/// are serialised by the base-sequence check — the later one is refused as
/// `FactTableCommitConflict` — within the precision of the backing blob
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

    let discardStaged (scopeId: string) (runId: string) : Async<unit> = async {
        let! names = storage.List(scopeId, rowsPrefix runId)

        for name in names do
            let! _ = storage.Delete(scopeId, name)
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

        // Offsets are zero-padded, so name order is staging order.
        return! load (names |> List.sort) []
    }

    let disclosureOf (d: FactTableDisclosure) : Disclosure =
        match d with
        | FactTableDisclosure.Surfaceable -> Disclosure.Surfaceable
        | FactTableDisclosure.Internal -> Disclosure.Internal
        | FactTableDisclosure.Restricted policy -> Disclosure.Restricted policy

    let draftsFor
        (table: FactTableDefinition)
        (runId: string)
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
                do! discardStaged scopeId run.RunId
                do! audit scopeId table closed FactTableEvents.RunRejectedType "Rejected" None (Some reason)
                return Error error
        }

    interface IFactTableWriter with

        member _.OpenRun(scopeId, tableId) = async {
            match declared tableId with
            | Error e -> return Error e
            | Ok table ->
                match! currentSequence scopeId tableId with
                | Error e -> return Error e
                | Ok(sequence, _) ->
                    let run: FactTableRunRecord = {
                        TableId = table.Id
                        RunId = Guid.NewGuid().ToString("N")
                        SchemaVersion = table.SchemaVersion
                        OpenedAt = now ()
                        BaseSequence = sequence
                        StagedRows = 0
                        Status = FactTableRunStatus.Open
                    }

                    match! FactTableBlobIo.put storage scopeId (recordName run.RunId) run with
                    | Error e -> return Error e
                    | Ok() -> return Ok run
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
                            match! stagedRows scopeId runId with
                            | Error e -> return Error e
                            | Ok rows ->
                                match FactTableValidation.defects table (hierarchyOf table) (List.indexed rows) with
                                | _ :: _ as defects ->
                                    return! reject scopeId table run (FactTableRowsRejected(runId, defects))
                                | [] ->
                                    let! previous =
                                        match head with
                                        | None -> async { return Ok None }
                                        | Some h ->
                                            FactTableBlobIo.tryGet<FactTableSnapshot>
                                                storage
                                                scopeId
                                                (snapshotName table.Id h.Sequence)

                                    match previous with
                                    | Error e -> return Error e
                                    | Ok previous ->
                                        let next = sequence + 1L
                                        let snapshot = FactTableSnapshot.ofRows table next rows
                                        let committedAt = now ()

                                        let watermark: FactTableWatermark = {
                                            TableId = table.Id
                                            Sequence = next
                                            CommittedAt = committedAt
                                            ContentDigest = FactTableSnapshot.digest snapshot
                                        }

                                        let removed = FactTableSnapshot.removedRows previous snapshot
                                        let drafts = draftsFor table runId watermark rows removed

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
                                                Change = FactTableSnapshot.changes table.Hierarchy previous snapshot
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
                                                        FactTableBlobIo.put storage scopeId (recordName runId) committed
                                                    with
                                                    | Error e -> return Error e
                                                    | Ok() ->
                                                        do! discardStaged scopeId runId

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
                    do! discardStaged scopeId runId

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