// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding

// ─── Delegated fact store (Phase 889) ────────────────────────────────
//
// A metric population held as a pointer to a table. Three pieces:
//
//   - **the table** — a declared fact table (Phase 887) bound to the
//     delegate destination is held as ROWS, one image per committed run,
//     rather than written as one fact per cell. `DelegateTableWriter` is the
//     `IFactTableWriter` that holds it: the same run lifecycle, validation,
//     watermark, change summary and one audit record as the default writer,
//     and no fact.
//   - **the decorator** — `DelegatedFactStore` is an `IFactStore` over the
//     composed store. A read of a delegated metric is pushed down to the
//     table as a typed `DelegateTableRead`; the rows it returns — the single
//     point-read row, or a ranking bounded by `PopulationQuery.MaxTopK` —
//     are asserted as ordinary facts at that moment, with the run's
//     watermark among their input hashes. Every other read passes through
//     untouched, and so does every write.
//   - **the refresh** — a commit advances the delegate's record (one fact in
//     the fact tier naming the run) and walks the facts minted under earlier
//     runs through the Phase 561 path, so they read as superseded.
//
// **Imported runs (Phase 938).** A run opened with an imported provenance
// keeps it beside the run, and every row of that run is minted through
// `FactTableRunProvenance.rewrite` — so a row under an origin mints an
// `Imported` fact, exactly as the default writer would have written it. The
// certificates a table's runs have imported under are recorded per table, so
// the refresh and the read path retire a quoted imported fact as they retire
// a computed one.
//
// **Declared methods (Phase 1040).** A run may declare the method that
// produced it (`FactTableRunOptions.Method`); the delegate keeps it beside the
// run, as it keeps an imported provenance, and every row minted under the run
// names it in its evidence — exactly the token the default writer stamps.
//
// **Attributed rows and period slices.** Every row of a stored image names
// the run that wrote it (`DelegateStoredRow.WrittenBy`). A run opened with a
// period slice replaces exactly the committed rows inside its slice and
// carries every other row forward still naming its own run, so a carried row
// is minted under that run's watermark, inputs, method and provenance, never
// the carrying run's. An image stored before rows were attributed holds rows
// with no writer; such an image was always written whole by its own run, so
// its rows are read under that run, and a slice over it is refused by name
// until the `facts-attribute-delegate-rows` operator task records the
// attribution.
//
// **What the fact tier holds** is the delegate records and exactly what was
// quoted. A subject nobody asked about has no fact.
//
// **Bitemporality.** An append-by-run table keeps every run, so an `AsOf`
// read is answered from the run that was current then (`Exact`). A value
// from a run that is no longer current cannot be asserted as an ordinary
// fact — asserting it now would make it the CURRENT head of its lineage —
// so such a quote is recorded beside the log as a reconstruction, which
// `Get` and the disclosure gate resolve like any fact. A replace table keeps
// only its latest run, so its history is `Approximate` and an `AsOf` read is
// refused (GP 9): the population door says so on its error channel, and the
// point door returns a typed `Absent` fact whose reason is the refusal.
//
// **Distributed-ready** under the same terms as the default writer: all
// state lives in the backing blob store (rule 4); the one in-process cache
// holds parsed row images keyed by run sequence AND content digest, which
// are immutable once committed.

/// One committed run of a delegated table, as the table's head records it.
type DelegateTableCommit = {
    /// The run's reach — what the coverage narrative and the walks read.
    Run: DelegateRun
    /// What the commit reported to its producer.
    Commit: FactTableCommit
}

/// A delegated table's head: every committed run, oldest first. The last
/// is the current run; the rest are what an `AsOf` read of an append-by-run
/// table answers from.
type DelegateTableHead = {
    /// Committed runs, in commit order.
    Commits: DelegateTableCommit list
}

/// One row of a delegated table's stored image, with the run that wrote it.
/// The stored form of the image: a row stored before rows were attributed
/// reads with `WrittenBy = None` (the member is absent).
type internal DelegateStoredRow = {
    /// Member ids from the hierarchy root down to the table's level.
    Subject: string list
    /// The row's period.
    Period: TemporalExtent
    /// One value per declared column, keyed by metric id.
    Values: Map<string, FactValue>
    /// The run that wrote the row; `None` for a row stored before rows were
    /// attributed, which the image's own run wrote.
    WrittenBy: string option
}

/// Construction and reading of stored rows.
[<RequireQualifiedAccess>]
module internal DelegateStoredRow =

    /// A row written by `runId`.
    let written (runId: string) (row: FactTableRow) : DelegateStoredRow = {
        Subject = row.Subject
        Period = row.Period
        Values = row.Values
        WrittenBy = Some runId
    }

    /// The table row a stored row holds.
    let row (stored: DelegateStoredRow) : FactTableRow = {
        Subject = stored.Subject
        Period = stored.Period
        Values = stored.Values
    }

    /// Whether the row names the run that wrote it. Read through
    /// `Option.isSome`: an absent member deserialises as null, which is `None`.
    let isAttributed (stored: DelegateStoredRow) : bool = Option.isSome stored.WrittenBy

/// What a committed run mints its rows with: the provenance it was opened
/// with, and the inputs and method it
/// declared.
type internal DelegateRunMinting = {
    Provenance: FactTableRunProvenance
    Inputs: string list
    Method: DeclaredMethod option
}

/// A row a table read returns, with the run that wrote it and what that run
/// mints its rows with — so a row carried forward by a later run is minted
/// under its own run.
type internal DelegateMintedRow = {
    /// The row, as the read asked for it.
    Row: DelegateTableRow
    /// The run that wrote the row.
    Run: DelegateRun
    /// What that run mints its rows with.
    Minting: DelegateRunMinting
}

/// An answer to a `DelegateTableRead`.
[<RequireQualifiedAccess>]
type internal DelegateTableAnswer =
    | Rows of DelegateMintedRow list
    | Ranked of ranked: DelegateMintedRow list * stats: PopulationStats * truncated: bool
    | Totals of DelegateChildTotal list

/// What a delegate mints a draft for.
type internal DelegateMintSource =
    /// A row of the run.
    | MintedRow of DelegateTableRow
    /// A quoted fact whose row the run no longer carries.
    | RemovedQuote of Fact

/// Minting — how a table row becomes an ordinary fact. Shared by the
/// decorator, the ranking tiebreak and the refresh walk, so the id a row
/// is ranked by is the id it is minted under.
module DelegateMint =

    /// The period a delegate's record fact spans: fixed, so every refresh
    /// of one delegate lands in ONE lineage and supersedes the last.
    let RecordPeriod: TemporalExtent = {
        From = DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        To = DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc)
        Label = Some "delegate"
    }

    /// The trigger reference a minted fact carries — the run that produced
    /// its row, in the default writer's form.
    let triggerRef (tableId: string) (runId: string) : string =
        sprintf "fact-table-run:%s/%s" tableId runId

    /// The draft a row mints before its run's provenance and declared method
    /// are applied: the delegate's metric, method and class; the row's
    /// subject, period and value; the run's watermark as the result
    /// reference and among the input hashes. For an append-by-run table this
    /// is exactly the fact the default writer would have written.
    let private rowDraft (delegateFact: DelegateFact) (run: DelegateRun) (row: DelegateTableRow) : FactDraft =
        let token = DelegateFact.token run.Watermark

        {
            Subject = {
                Hierarchy = delegateFact.Query.Hierarchy
                Path = row.Subject
            }
            Metric = delegateFact.Metric
            Value = row.Value
            Period = row.Period
            Method = delegateFact.Method
            Evidence = {
                ResultRef = Some token
                InputHashes = [ Fact.valueHash row.Value; token ]
                TriggerRef = Some(triggerRef run.TableId run.RunId)
            }
            Confidence = None
            Disclosure = delegateFact.Disclosure
        }

    /// The stamp a run's declared inputs and method imply — the default
    /// writer's own rewrite, so a row reads the same through either writer.
    /// Identity when the run declared neither.
    let private stamp (minting: DelegateRunMinting) : FactDraft -> FactDraft =
        FactTableRunOptions.rewrite {
            FactTableRunOptions.none with
                Inputs = minting.Inputs
                Method = minting.Method
        }

    /// The one draft a delegate mints, for a row its run carries or for the
    /// absence that supersedes a quoted fact whose row the run no longer
    /// carries. Rewritten as the run's provenance says (identical for a
    /// computed run) and stamped with its declared inputs and method. A removal is
    /// always in the QUOTED fact's own lineage, naming an origin's withdrawal
    /// where the provenance records one, so it supersedes that fact whichever
    /// lineage the run would have minted the row into.
    let internal draft
        (minting: DelegateRunMinting)
        (delegateFact: DelegateFact)
        (run: DelegateRun)
        (source: DelegateMintSource)
        : FactDraft =
        let rewrite = FactTableRunProvenance.rewrite minting.Provenance

        match source with
        | MintedRow row -> rowDraft delegateFact run row |> rewrite
        | RemovedQuote fact ->
            let removal =
                rowDraft delegateFact run {
                    Subject = fact.Subject.Path
                    Period = fact.Period
                    Value = Absent(sprintf "removed by fact-table run %s" (DelegateFact.token run.Watermark))
                }
                |> rewrite

            if Fact.methodIdentity removal.Method = Fact.methodIdentity fact.Method then
                removal
            else
                { removal with Method = fact.Method }
        |> stamp minting

    /// The delegate's record: one fact per delegate, at the hierarchy root,
    /// whose value points at the run (`Series` of the watermark token). A
    /// refresh re-asserts it, and it supersedes the last one.
    let recordDraft (delegateFact: DelegateFact) (run: DelegateRun) : FactDraft =
        let token = DelegateFact.token run.Watermark

        {
            Subject = {
                Hierarchy = delegateFact.Query.Hierarchy
                Path = []
            }
            Metric = delegateFact.Metric
            Value = Series token
            Period = RecordPeriod
            Method = delegateFact.Method
            Evidence = {
                ResultRef = Some token
                InputHashes = [ token ]
                TriggerRef = Some(triggerRef run.TableId run.RunId)
            }
            Confidence = None
            Disclosure = delegateFact.Disclosure
        }

    /// A draft as the fact it would be, recorded at `asOf` — how a quote
    /// from a run that is no longer current is reconstructed. The write
    /// plan's own draft-to-fact step (`FactWrite.recordedAt`),
    /// so a quote is addressed exactly as a write of the same draft is.
    let reconstruct (draft: FactDraft) (asOf: DateTime) : Fact = FactWrite.recordedAt draft asOf

/// Resolution of the composition's delegated tables into delegate records.
module DelegateFacts =

    /// The delegate records for the named tables — one per column — or an
    /// `Error` naming the first table that cannot be delegated (undeclared,
    /// its hierarchy unregistered, or its level not one of the hierarchy's).
    let resolve
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        (tableIds: string list)
        : Result<DelegateFact list, string> =
        let one (tableId: string) =
            match tables.TryGetTable tableId with
            | None -> Error(sprintf "delegate fact: table '%s' is not declared by any module" tableId)
            | Some table ->
                match registry |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy) with
                | None ->
                    Error(
                        sprintf
                            "delegate fact: table '%s' names hierarchy '%s', which no module registers"
                            tableId
                            table.Hierarchy
                    )
                | Some hierarchy -> DelegateFact.ofTable hierarchy table

        tableIds
        |> List.distinct
        |> List.fold
            (fun acc tableId ->
                match acc, one tableId with
                | Error e, _ -> Error e
                | Ok _, Error e -> Error e
                | Ok xs, Ok ys -> Ok(xs @ ys))
            (Ok [])

/// The table storage — row images per committed run, and the head that
/// names them. Internal: the only doors onto it are the writer and the
/// decorator, and the only questions it answers are `DelegateTableRead`s.
type internal DelegateTableStore(storage: IBlobStorage) =

    let root = "_delegate-tables/"

    // Parsed row images, keyed by the run's identity AND its content digest
    // — immutable once committed, so a cache entry can never go stale. A
    // handful of entries: the current run of each hot table. Every cache
    // here is a `BoundedMemoIndex`, so at the cap the oldest entry goes and
    // the rest stay — a ninth table never empties the other eight.
    let CacheCap = 8

    let cache =
        BoundedMemoIndex<string * string * int64 * string, DelegateStoredRow array>(CacheCap, ignore)

    // A committed run's provenance, and the imported lineages as of a
    // committed run — keyed as the rows are, and immutable under that key
    // for the same reason.
    let provenanceCache =
        BoundedMemoIndex<string * string * int64 * string, FactTableRunProvenance>(CacheCap, ignore)

    let lineageCache =
        BoundedMemoIndex<string * string * int64 * string, string list>(CacheCap, ignore)

    let optionsCache =
        BoundedMemoIndex<string * string * int64 * string, FactTableRunOptions>(CacheCap, ignore)

    /// How many row images the row cache holds now — never more than its
    /// cap, and never emptied by reaching it.
    member internal _.CachedRowImages = cache.Count

    member _.RecordName(runId: string) = sprintf "%sruns/%s.json" root runId
    member _.RecordsPrefix = sprintf "%sruns/" root
    member _.StagedPrefix(runId: string) = sprintf "%sstaged/%s/" root runId
    member _.StagedRoot = sprintf "%sstaged/" root

    member this.StagedName (runId: string) (offset: int) =
        sprintf "%s%010d.json" (this.StagedPrefix runId) offset

    member _.HeadName(tableId: string) = sprintf "%shead/%s.json" root tableId
    /// The claim a table's open run holds (Phase 994).
    member _.ClaimName(tableId: string) = sprintf "%sopen/%s.json" root tableId
    member _.HeadsPrefix = sprintf "%shead/" root

    member _.RowsName (tableId: string) (sequence: int64) =
        sprintf "%srows/%s/%020d.json" root tableId sequence

    member _.QuoteName(factId: string) = sprintf "%squotes/%s.json" root factId

    /// Where a run's provenance is kept (Phase 938) — only for an imported
    /// run; a computed run keeps nothing.
    member _.ProvenanceName(runId: string) =
        sprintf "%sprovenance/%s.json" root runId

    member _.ProvenanceRoot = sprintf "%sprovenance/" root

    /// Where a run's declared method is kept (Phase 1040) — only for a run
    /// that declared one.
    member _.MethodName(runId: string) = sprintf "%smethods/%s.json" root runId

    member _.MethodRoot = sprintf "%smethods/" root

    /// Where the certificates a table's runs imported under are recorded.
    member _.LineagesName(tableId: string) =
        sprintf "%slineages/%s.json" root tableId

    member _.Storage = storage

    member this.Head(scopeId: string, tableId: string) : Async<Result<DelegateTableHead option, string>> = async {
        match! FactTableBlobIo.tryGet<DelegateTableHead> storage scopeId (this.HeadName tableId) with
        | Ok head -> return Ok head
        | Error e -> return Error(FactTableWriteError.describe e)
    }

    /// The run current now.
    member this.Current(scopeId: string, tableId: string) : Async<Result<DelegateTableCommit option, string>> = async {
        match! this.Head(scopeId, tableId) with
        | Error e -> return Error e
        | Ok None -> return Ok None
        | Ok(Some head) -> return Ok(List.tryLast head.Commits)
    }

    /// The run that was current at `asOf` — the latest committed at or
    /// before it.
    member this.At
        (scopeId: string, tableId: string, asOf: DateTime)
        : Async<Result<DelegateTableCommit option, string>> =
        async {
            match! this.Head(scopeId, tableId) with
            | Error e -> return Error e
            | Ok None -> return Ok None
            | Ok(Some head) ->
                let t = asOf.ToUniversalTime()

                return
                    Ok(
                        head.Commits
                        |> List.filter (fun c -> c.Run.Watermark.CommittedAt <= t)
                        |> List.tryLast
                    )
        }

    /// The stored image at `sequence`, read from storage rather than the
    /// cache: what a commit carries forward and what the attribution task
    /// rewrites, so neither ever acts on an image read before an attribution
    /// was recorded. `None` when no image is stored there.
    member this.StoredImage
        (scopeId: string, tableId: string, sequence: int64)
        : Async<Result<DelegateStoredRow list option, FactTableWriteError>> =
        FactTableBlobIo.tryGet<DelegateStoredRow list> storage scopeId (this.RowsName tableId sequence)

    /// A committed run's stored rows.
    member this.Rows(scopeId: string, run: DelegateRun) : Async<Result<DelegateStoredRow array, string>> = async {
        let key = scopeId, run.TableId, run.Watermark.Sequence, run.Watermark.ContentDigest

        match cache.TryGet key with
        | Some rows -> return Ok rows
        | None ->
            match! this.StoredImage(scopeId, run.TableId, run.Watermark.Sequence) with
            | Error e -> return Error(FactTableWriteError.describe e)
            | Ok None -> return Error(sprintf "the rows of run %s are missing" (DelegateFact.token run.Watermark))
            | Ok(Some rows) ->
                let image = List.toArray rows
                cache.Set(key, image)
                return Ok image
    }

    /// A committed run's rows, each with the run that wrote it and what that
    /// run mints with. A row with no writer was written by `image`'s own run;
    /// a row naming a run the table's head does not record is a fault.
    member this.Attributed
        (scopeId: string, image: DelegateRun)
        : Async<Result<(DelegateStoredRow * DelegateRun * DelegateRunMinting) array, string>> =
        async {
            match! this.Rows(scopeId, image) with
            | Error e -> return Error e
            | Ok rows ->
                let others =
                    rows
                    |> Array.choose _.WrittenBy
                    |> Array.distinct
                    |> Array.filter (fun runId -> runId <> image.RunId)

                let! known = async {
                    if Array.isEmpty others then
                        return Ok Map.empty
                    else
                        match! this.Head(scopeId, image.TableId) with
                        | Error e -> return Error e
                        | Ok head ->
                            let runs =
                                head
                                |> Option.map (fun h ->
                                    h.Commits |> List.map (fun c -> c.Run.RunId, c.Run) |> Map.ofList)
                                |> Option.defaultValue Map.empty

                            match others |> Array.tryFind (runs.ContainsKey >> not) with
                            | Some missing ->
                                return
                                    Error(
                                        sprintf
                                            "the rows of run %s name run %s as their writer, which table '%s' has no commit of"
                                            (DelegateFact.token image.Watermark)
                                            missing
                                            image.TableId
                                    )
                            | None -> return Ok runs
                }

                match known with
                | Error e -> return Error e
                | Ok known ->
                    let writerOf (row: DelegateStoredRow) =
                        match row.WrittenBy with
                        | Some runId when runId <> image.RunId -> known[runId]
                        | _ -> image

                    let writers = rows |> Array.map writerOf |> Array.distinctBy _.RunId |> List.ofArray

                    let rec mintings (remaining: DelegateRun list) (acc: Map<string, DelegateRunMinting>) = async {
                        match remaining with
                        | [] -> return Ok acc
                        | run :: rest ->
                            match! this.Minting(scopeId, run) with
                            | Error e -> return Error e
                            | Ok minting -> return! mintings rest (Map.add run.RunId minting acc)
                    }

                    match! mintings writers Map.empty with
                    | Error e -> return Error e
                    | Ok byRun ->
                        return
                            Ok(
                                rows
                                |> Array.map (fun row ->
                                    let run = writerOf row
                                    row, run, byRun[run.RunId])
                            )
        }

    /// The provenance a committed run was opened with: `ComputedRun` when it
    /// kept none.
    member this.Provenance(scopeId: string, run: DelegateRun) : Async<Result<FactTableRunProvenance, string>> = async {
        let key = scopeId, run.TableId, run.Watermark.Sequence, run.Watermark.ContentDigest

        match provenanceCache.TryGet key with
        | Some provenance -> return Ok provenance
        | None ->
            match! FactTableRunProvenance.read storage scopeId (this.ProvenanceName run.RunId) with
            | Error e -> return Error(FactTableWriteError.describe e)
            | Ok provenance ->
                provenanceCache.Set(key, provenance)
                return Ok provenance
    }

    /// What a committed run declared, read from its record. A
    /// run recorded before the record kept its options declared no inputs (a
    /// delegated table refused them) and kept its method, if any, beside the
    /// record.
    member this.Options(scopeId: string, run: DelegateRun) : Async<Result<FactTableRunOptions, string>> = async {
        let key = scopeId, run.TableId, run.Watermark.Sequence, run.Watermark.ContentDigest

        match optionsCache.TryGet key with
        | Some options -> return Ok options
        | None ->
            let! read = async {
                match! FactTableRunRecordIo.tryGet storage scopeId (this.RecordName run.RunId) with
                | Error e -> return Error e
                | Ok(Some record) when record.RecordVersion >= FactTableRunRecord.CurrentVersion ->
                    return Ok(FactTableRunOptions.ofRun record.Options)
                | Ok _ ->
                    match! FactTableBlobIo.tryGet<DeclaredMethod> storage scopeId (this.MethodName run.RunId) with
                    | Error e -> return Error e
                    | Ok method ->
                        return
                            Ok {
                                FactTableRunOptions.none with
                                    Method = method
                            }
            }

            match read with
            | Error e -> return Error(FactTableWriteError.describe e)
            | Ok options ->
                optionsCache.Set(key, options)
                return Ok options
    }

    /// What a committed run mints its rows with: its provenance, and the
    /// inputs and method it declared.
    member this.Minting(scopeId: string, run: DelegateRun) : Async<Result<DelegateRunMinting, string>> = async {
        match! this.Provenance(scopeId, run) with
        | Error e -> return Error e
        | Ok provenance ->
            match! this.Options(scopeId, run) with
            | Error e -> return Error e
            | Ok options ->
                return
                    Ok {
                        Provenance = provenance
                        Inputs = options.Inputs
                        Method = options.Method
                    }
    }

    /// The certificates the table's runs have imported under, as of the
    /// committed run `current` — every lineage besides the delegate's own a
    /// quoted fact of the table can sit in.
    member this.ImportedLineages(scopeId: string, current: DelegateRun) : Async<Result<string list, string>> = async {
        let key =
            scopeId, current.TableId, current.Watermark.Sequence, current.Watermark.ContentDigest

        match lineageCache.TryGet key with
        | Some lineages -> return Ok lineages
        | None ->
            match! FactTableBlobIo.tryGet<string list> storage scopeId (this.LineagesName current.TableId) with
            | Error e -> return Error(FactTableWriteError.describe e)
            | Ok found ->
                let lineages = found |> Option.defaultValue []
                lineageCache.Set(key, lineages)
                return Ok lineages
    }

    /// Record the certificates an imported run adds to the table's
    /// lineages. A computed run adds none and writes nothing.
    member this.RecordLineages
        (scopeId: string, tableId: string, provenance: FactTableRunProvenance)
        : Async<Result<unit, FactTableWriteError>> =
        async {
            match provenance with
            | ComputedRun -> return Ok()
            | ImportedRun origins ->
                match! FactTableBlobIo.tryGet<string list> storage scopeId (this.LineagesName tableId) with
                | Error e -> return Error e
                | Ok found ->
                    let known = found |> Option.defaultValue []

                    let added =
                        origins
                        |> List.map _.CertificateRef
                        |> List.distinct
                        |> List.filter (fun c -> not (List.contains c known))

                    if List.isEmpty added then
                        return Ok()
                    else
                        return! FactTableBlobIo.put storage scopeId (this.LineagesName tableId) (known @ added)
        }

    /// Answer one typed read over a run's rows. The ONLY question the table
    /// is ever asked — so nothing a caller supplies can become a query.
    member this.Read
        (
            scopeId: string,
            delegateFact: DelegateFact,
            run: DelegateRun,
            read: DelegateTableRead,
            freshnessOf: PopulationMember -> FactFreshness
        ) : Async<Result<DelegateTableAnswer, string>> =
        async {
            match! this.Attributed(scopeId, run) with
            | Error e -> return Error e
            | Ok attributed ->
                let column = delegateFact.Query.ValueColumn

                let cell (row: DelegateStoredRow, writer: DelegateRun, minting: DelegateRunMinting) =
                    row.Values
                    |> Map.tryFind column
                    |> Option.map (fun value -> {
                        Row = {
                            Subject = row.Subject
                            Period = row.Period
                            Value = value
                        }
                        Run = writer
                        Minting = minting
                    })

                let overlaps (filter: TemporalExtent option) (period: TemporalExtent) =
                    filter |> Option.forall (fun p -> p.From < period.To && period.From < p.To)

                let where (keep: DelegateStoredRow -> bool) =
                    attributed |> Array.filter (fun (row, _, _) -> keep row) |> Array.choose cell

                match read with
                | DelegateTableRead.Point(subject, periodOverlaps) ->
                    return
                        where (fun r -> r.Subject = subject && overlaps periodOverlaps r.Period)
                        |> Array.toList
                        |> DelegateTableAnswer.Rows
                        |> Ok

                | DelegateTableRead.Ranked(prefix, periodOverlaps, threshold, direction, topK) ->
                    let prefixMatches (path: string list) =
                        prefix
                        |> Option.forall (fun p ->
                            List.length path >= List.length p && List.truncate (List.length p) path = p)

                    let matched =
                        where (fun r -> prefixMatches r.Subject && overlaps periodOverlaps r.Period)
                        |> Array.filter (fun m ->
                            threshold |> Option.forall (fun t -> ValueThreshold.satisfies t m.Row.Value))
                        |> Array.toList

                    let memberOf (minted: DelegateMintedRow) : PopulationMember * DelegateMintedRow =
                        // The id and lineage the row is minted under: its own run's.
                        let row = minted.Row

                        let draft =
                            DelegateMint.draft minted.Minting delegateFact minted.Run (MintedRow row)

                        {
                            FactId =
                                Fact.compute
                                    draft.Subject
                                    draft.Metric
                                    draft.Period
                                    draft.Method
                                    draft.Evidence.InputHashes
                            Subject = {
                                Hierarchy = delegateFact.Query.Hierarchy
                                Path = row.Subject
                            }
                            Magnitude = PopulationValue.comparable row.Value
                            PeriodFrom = row.Period.From
                            PeriodTo = row.Period.To
                            AsOf = minted.Run.Watermark.CommittedAt
                            MethodIdentity = Fact.methodIdentity draft.Method
                        },
                        minted

                    let members = matched |> List.map memberOf
                    let stats = PopulationStats.ofMembers freshnessOf (members |> List.map fst)

                    // The shared comparator, ids and all — the ranking an
                    // enumerating store would return over the same rows.
                    let ranked =
                        PopulationRanking.rankBy direction (fun (m, _) -> m.Magnitude) (fun (m, _) -> m.FactId) members

                    return
                        Ok(
                            DelegateTableAnswer.Ranked(
                                ranked |> List.truncate topK |> List.map snd,
                                stats,
                                List.length ranked > topK
                            )
                        )

                | DelegateTableRead.ChildTotals periodOverlaps ->
                    let depth = delegateFact.Query.Level - 1

                    return
                        where (fun r -> overlaps periodOverlaps r.Period)
                        |> Array.map _.Row
                        |> Array.groupBy (fun r -> List.truncate depth r.Subject, r.Period.From, r.Period.To)
                        |> Array.map (fun ((parent, _, _), children) ->
                            let first = children[0]

                            {
                                Parent = parent
                                Period = first.Period
                                Total =
                                    children
                                    |> Array.sumBy (fun c ->
                                        match c.Value with
                                        | Scalar d -> d
                                        | _ -> 0m)
                                ChildCount = children.Length
                                AbsentCount =
                                    children
                                    |> Array.filter (fun c ->
                                        match c.Value with
                                        | Absent _ -> true
                                        | _ -> false)
                                    |> Array.length
                            })
                        |> Array.toList
                        |> DelegateTableAnswer.Totals
                        |> Ok
        }

    /// A reconstruction recorded beside the log, by id. A quote that cannot
    /// be read is an `Error`, never an absent quote.
    member this.Quote(scopeId: string, factId: string) : Async<Result<Fact option, string>> = async {
        match! FactTableBlobIo.tryGet<Fact> storage scopeId (this.QuoteName factId) with
        | Ok found -> return Ok found
        | Error e -> return Error(FactTableWriteError.describe e)
    }

    /// Record a reconstruction beside the log (idempotent by id).
    member this.RecordQuote(scopeId: string, fact: Fact) : Async<Result<unit, string>> = async {
        match! FactTableBlobIo.put storage scopeId (this.QuoteName fact.FactId) fact with
        | Ok() -> return Ok()
        | Error e -> return Error(FactTableWriteError.describe e)
    }

/// The lineages a delegate's quoted facts sit in (Phase 938): its own
/// method, and `Imported` under each certificate the table's runs imported
/// under. Every walk that retires quoted facts walks all of them.
module internal DelegateLineages =

    /// Phase 964 — the path prefix a ranked read for the lineage
    /// `Imported certificateRef` pushes down over a run opened with
    /// `provenance`: the caller's prefix narrowed to the root member of the
    /// run's origin for that certificate. `Ok None` when the run holds no
    /// row in that lineage (no such origin, or a prefix outside its root);
    /// `Error roots` when several origins share the certificate, since one
    /// ranked read answers from one subtree.
    let importedPrefix
        (provenance: FactTableRunProvenance)
        (certificateRef: string)
        (requested: string list option)
        : Result<string list option option, string list> =
        let roots =
            match provenance with
            | ComputedRun -> []
            | ImportedRun origins ->
                origins
                |> List.filter (fun o -> o.CertificateRef = certificateRef)
                |> List.map _.RootMember
                |> List.distinct

        match roots with
        | [] -> Ok None
        | [ root ] ->
            match requested with
            | None
            | Some [] -> Ok(Some(Some [ root ]))
            | Some(first :: _) when first = root -> Ok(Some requested)
            | Some _ -> Ok None
        | several -> Error several

    /// Whether `fact`, in the lineage `method`, was minted by `delegateFact`
    /// from a run of its table other than `currentToken`.
    let isStale (delegateFact: DelegateFact) (currentToken: string) (method: MethodRef) (fact: Fact) : bool =
        Fact.methodIdentity fact.Method = Fact.methodIdentity method
        && FactInvalidation.isMintedUnderStaleRun { delegateFact with Method = method } currentToken fact

    /// The imported lineages' current heads a query narrowed by `narrow`
    /// returns, each paired with its lineage.
    let importedHeads
        (query: FactQuery -> Async<Fact list>)
        (delegateFact: DelegateFact)
        (lineages: string list)
        (narrow: FactQuery -> FactQuery)
        : Async<(MethodRef * Fact) list> =
        async {
            let! found =
                lineages
                |> List.map (fun certificateRef -> async {
                    let method = Imported certificateRef

                    let! heads =
                        query (
                            narrow {
                                FactQuery.all with
                                    Metric = Some delegateFact.Metric
                                    Method = Some method
                            }
                        )

                    return heads |> List.map (fun f -> method, f)
                })
                |> Async.Sequential

            return found |> List.concat
        }

/// Where one read of the delegated store is answered — decided once per call,
/// before anything is read: the inner store untouched, or the delegate's
/// table.
[<RequireQualifiedAccess>]
type internal ReadFrom =
    /// The inner store answers, untouched.
    | Inner
    /// The delegate's table answers; `importedCertificate` is the certificate
    /// the read was selected through when it named an imported lineage.
    | DelegateTable of delegateFact: DelegateFact * importedCertificate: string option

/// What a point read of a delegated table leaves the decorator to answer.
[<RequireQualifiedAccess>]
type internal DelegatedPoint =
    /// The current rows are ordinary heads in the inner store now (or the
    /// table never committed): the inner store answers, with its own
    /// selection, history and competition rules.
    | InnerAnswers
    /// The table answered: an `AsOf` read, or its refusal.
    | Answered of Fact list

/// The `IFactStore` decorator a delegated metric is read through. A
/// delegated metric's point and population reads are pushed down to
/// its table and the rows returned are minted as ordinary facts; every
/// other read, and every write, reaches the inner store untouched.
///
/// **Which reads are delegated.** A point read (`Query` /
/// `QueryWithCompetition`) naming a delegated metric and a subject at the
/// table's level; a population read (`QueryPopulation`) naming a delegated
/// metric and its hierarchy, at the table's level or at no level, for the
/// delegate's method or no particular one — or (Phase 964) for `Imported c`
/// where `c` is a certificate the table's runs recorded importing under.
/// A read with no subject — "every
/// fact for this metric" — is NOT a point read: it reaches the inner store,
/// which holds what was quoted, because answering it from the table would
/// materialise the population.
type DelegatedFactStore
    internal
    (
        inner: IFactStore,
        tables: DelegateTableStore,
        delegates: DelegateFact list,
        registry: IMetricRegistry option,
        clock: unit -> DateTime
    ) =

    let now () = clock().ToUniversalTime()

    // The inner store under the read's one scope. A fault the inner store
    // reports is raised here and answered as `StorageFault` at the
    // interface (`FactStoreResult.ofRead`), so a delegated read never turns a fault
    // into a shorter answer.
    let innerAssert (scope: ResolvedScope) (draft: FactDraft) : Async<Result<Fact, string>> = async {
        let! asserted = inner.Assert(scope, draft)
        return asserted |> Result.mapError FactStoreError.describe
    }

    let innerGet (scope: ResolvedScope) (factId: string) =
        inner.Get(scope, factId) |> FactStoreResult.orRaiseAsync

    let innerQuery (scope: ResolvedScope) (q: FactQuery) =
        inner.Query(scope, q) |> FactStoreResult.orRaiseAsync

    let innerQueryWithCompetition (scope: ResolvedScope) (q: FactQuery) =
        inner.QueryWithCompetition(scope, q) |> FactStoreResult.orRaiseAsync

    let innerChain (scope: ResolvedScope) (factId: string) =
        inner.QuerySupersessionChain(scope, factId) |> FactStoreResult.orRaiseAsync

    let innerPopulation (scope: ResolvedScope) (q: PopulationQuery) : Async<Result<PopulationResult, string>> = async {
        match! inner.QueryPopulation(scope, q) with
        | Ok result -> return Ok result
        | Error(FactStoreError.Refused reason) -> return Error reason
        | Error fault -> return raise (FactStoreFault fault)
    }

    let methodMatches (delegateFact: DelegateFact) (m: MethodRef option) =
        m
        |> Option.forall (fun m -> Fact.methodIdentity m = Fact.methodIdentity delegateFact.Method)

    let delegateOf (metric: MetricRef) =
        delegates |> List.tryFind (fun d -> d.Metric = metric)

    // A delegated table that cannot be read is a fault: raised here and
    // answered as `StorageFault` at the interface, never as a shorter answer.
    let orFault (d: DelegateFact) (read: Async<Result<'T, string>>) : Async<'T> = async {
        match! read with
        | Ok value -> return value
        | Error detail ->
            return raise (FactStoreFault(FactStoreError.StorageFault(sprintf "delegated table '%s'" d.TableId, detail)))
    }

    // A read naming `Imported c` is delegated when `c` is one of the
    // certificates the table's runs recorded importing under
    // (`DelegateTableStore.ImportedLineages`): the table holds that lineage's
    // rows, so asking only the inner store answered only what was already
    // quoted.
    let recordsImport (scope: ResolvedScope) (d: DelegateFact) (certificateRef: string) : Async<bool> = async {
        match! tables.Current(scope.ScopeId, d.TableId) |> orFault d with
        | None -> return false
        | Some commit ->
            let! lineages = tables.ImportedLineages(scope.ScopeId, commit.Run) |> orFault d
            return List.contains certificateRef lineages
    }

    let selectByMethod (scope: ResolvedScope) (d: DelegateFact) (m: MethodRef option) : Async<ReadFrom> = async {
        if methodMatches d m then
            return ReadFrom.DelegateTable(d, None)
        else
            match m with
            | Some(Imported certificateRef) ->
                let! recorded = recordsImport scope d certificateRef

                return
                    if recorded then
                        ReadFrom.DelegateTable(d, Some certificateRef)
                    else
                        ReadFrom.Inner
            | _ -> return ReadFrom.Inner
    }

    // Where a point read is answered, decided once for the call.
    let pointReadFrom (scope: ResolvedScope) (query: FactQuery) : Async<ReadFrom> = async {
        match query.Subject, query.Metric with
        // A subject outside the query's branch is no point read: the inner
        // store, which honours the branch, answers nothing.
        | Some subject, Some metric when FactQuery.admitsSubject query subject ->
            match delegateOf metric |> Option.filter (fun d -> DelegateFact.covers d subject) with
            | Some d -> return! selectByMethod scope d query.Method
            | None -> return ReadFrom.Inner
        | _ -> return ReadFrom.Inner
    }

    // Where a population read is answered, decided once for the call.
    let populationReadFrom (scope: ResolvedScope) (query: PopulationQuery) : Async<ReadFrom> = async {
        let shaped =
            delegateOf query.Metric
            |> Option.filter (fun d ->
                d.Query.Hierarchy = query.Hierarchy
                && (query.Level |> Option.forall (fun level -> level = d.Query.Level)))

        match shaped, query.Methods with
        | None, _ -> return ReadFrom.Inner
        | Some d, CanonicalMethodOnly
        | Some d, AllCompetingMethods -> return ReadFrom.DelegateTable(d, None)
        | Some d, OneMethod m -> return! selectByMethod scope d (Some m)
    }

    /// Only the facts in the lineage a read named, when it was selected
    /// through an imported certificate.
    let inLineage (imported: string option) (facts: Fact list) =
        match imported with
        | None -> facts
        | Some certificateRef ->
            let lineage = Fact.methodIdentity (Imported certificateRef)
            facts |> List.filter (fun f -> Fact.methodIdentity f.Method = lineage)

    let policyOf (metric: MetricRef) =
        registry
        |> Option.bind (fun r -> r.TryGetMetric metric.Value)
        |> Option.map _.Staleness
        |> Option.defaultValue UntilSuperseded

    let refusal (delegateFact: DelegateFact) (detail: string) =
        DelegateRefusal.SourceUnavailable(delegateFact.TableId, detail)

    let read (scope: ResolvedScope) (d: DelegateFact) (run: DelegateRun) (r: DelegateTableRead) (at: DateTime) =
        let policy = policyOf d.Metric
        tables.Read(scope.ScopeId, d, run, r, (fun m -> Freshness.deriveAt policy m.AsOf true at))

    // Mint rows of the CURRENT image as ordinary facts, in order, each under
    // the run that wrote it. A repeated read of an unchanged table re-asserts
    // identical tuples, which the store answers idempotently by content
    // address; a row a slice carried forward re-asserts the fact its own run
    // minted.
    let mint (scope: ResolvedScope) (d: DelegateFact) (rows: DelegateMintedRow list) = async {
        let rec go (remaining: DelegateMintedRow list) (acc: Fact list) = async {
            match remaining with
            | [] -> return Ok(List.rev acc)
            | minted :: rest ->
                match! innerAssert scope (DelegateMint.draft minted.Minting d minted.Run (MintedRow minted.Row)) with
                | Ok fact -> return! go rest (fact :: acc)
                | Error e -> return Error e
        }

        return! go rows []
    }

    // Quote rows of an image that is NO LONGER current: the fact minted while
    // it was current if there is one, else a reconstruction recorded beside
    // the log. Never an ordinary assertion — that would make a past value
    // the current head of its lineage.
    let quoteHistorical (scope: ResolvedScope) (d: DelegateFact) (rows: DelegateMintedRow list) = async {
        let rec go (remaining: DelegateMintedRow list) (acc: Fact list) = async {
            match remaining with
            | [] -> return Ok(List.rev acc)
            | minted :: rest ->
                let draft = DelegateMint.draft minted.Minting d minted.Run (MintedRow minted.Row)
                let rebuilt = DelegateMint.reconstruct draft minted.Run.Watermark.CommittedAt

                match! innerGet scope rebuilt.FactId with
                | Some stored -> return! go rest (stored :: acc)
                | None ->
                    match! tables.RecordQuote(scope.ScopeId, rebuilt) with
                    | Ok() -> return! go rest (rebuilt :: acc)
                    | Error e -> return Error e
        }

        return! go rows []
    }

    // The quoted heads of one subject minted from a run that is no longer
    // current and whose row the current image no longer carries: superseded
    // by an `Absent` under the current run, so a stale value is never served
    // as a current head. A row the image carries is live whichever run wrote
    // it. A table read that fails fails the read: the stale head it was to
    // retire would otherwise be served as current.
    let retireRemoved
        (scope: ResolvedScope)
        (d: DelegateFact)
        (run: DelegateRun)
        (subject: SubjectRef)
        (live: DelegateMintedRow list)
        =
        async {
            let token = DelegateFact.token run.Watermark

            let! heads =
                innerQuery scope {
                    FactQuery.all with
                        Subject = Some subject
                        Metric = Some d.Metric
                        Method = Some d.Method
                }

            // The imported lineages too. A table that never imported records
            // none, and asks nothing more.
            let! lineages = tables.ImportedLineages(scope.ScopeId, run) |> orFault d

            let! imported =
                match lineages with
                | [] -> async.Return []
                | certificates ->
                    DelegateLineages.importedHeads (innerQuery scope) d certificates (fun q -> {
                        q with
                            Subject = Some subject
                    })

            let removed =
                (heads |> List.map (fun f -> d.Method, f)) @ imported
                |> List.filter (fun (method, f) ->
                    DelegateLineages.isStale d token method f
                    && not (
                        live
                        |> List.exists (fun m -> m.Row.Period.From = f.Period.From && m.Row.Period.To = f.Period.To)
                    ))

            match removed with
            | [] -> return Ok()
            | _ ->
                let! minting = tables.Minting(scope.ScopeId, run) |> orFault d

                let rec retire (remaining: (MethodRef * Fact) list) = async {
                    match remaining with
                    | [] -> return Ok()
                    | (_, fact) :: rest ->
                        match! innerAssert scope (DelegateMint.draft minting d run (RemovedQuote fact)) with
                        | Ok _ -> return! retire rest
                        | Error e -> return Error e
                }

                return! retire removed
        }

    // The point door's refusal of an `AsOf` read the table cannot answer: a
    // typed `Absent` fact whose reason is the refusal, recorded beside the
    // log so the gate resolves it. Its id is content-addressed over the
    // question, so asking twice is one record.
    let refusedPoint
        (scope: ResolvedScope)
        (d: DelegateFact)
        (query: FactQuery)
        (reason: DelegateRefusal)
        (asOf: DateTime)
        =
        async {
            let subject =
                query.Subject
                |> Option.defaultValue {
                    Hierarchy = d.Query.Hierarchy
                    Path = []
                }

            let period = query.PeriodOverlaps |> Option.defaultValue DelegateMint.RecordPeriod

            let draft: FactDraft = {
                Subject = subject
                Metric = d.Metric
                Value = Absent(DelegateRefusal.describe reason)
                Period = period
                Method = d.Method
                Evidence = {
                    ResultRef = None
                    InputHashes = [ sprintf "refused:%s" (asOf.ToUniversalTime().ToString "o") ]
                    TriggerRef = None
                }
                Confidence = None
                Disclosure = d.Disclosure
            }

            let fact = DelegateMint.reconstruct draft asOf
            do! tables.RecordQuote(scope.ScopeId, fact) |> orFault d
            return [ fact ]
        }

    /// A point read the delegate's table answers.
    let pointFromTable
        (scope: ResolvedScope)
        (d: DelegateFact)
        (imported: string option)
        (query: FactQuery)
        : Async<DelegatedPoint> =
        async {
            let subject = query.Subject.Value

            let rowsOf (commit: DelegateTableCommit) at = async {
                match!
                    read scope d commit.Run (DelegateTableRead.Point(subject.Path, query.PeriodOverlaps)) at
                    |> orFault d
                with
                | DelegateTableAnswer.Rows rows -> return rows
                | _ -> return []
            }

            let! current = tables.Current(scope.ScopeId, d.TableId) |> orFault d

            match query.AsOf, current with
            // Never committed: the table holds nothing, and neither does the
            // fact tier for it — the inner store answers (empty).
            | None, None -> return DelegatedPoint.InnerAnswers
            | None, Some commit ->
                let! rows = rowsOf commit (now ())
                let! _ = mint scope d rows |> orFault d
                do! retireRemoved scope d commit.Run subject rows |> orFault d
                // The minted facts are ordinary heads now, so the inner
                // store answers with its own selection, history and
                // competition rules intact.
                return DelegatedPoint.InnerAnswers
            | Some t, _ ->
                match d.History with
                | SnapshotFidelity.Approximate ->
                    let! refused =
                        refusedPoint scope d query (DelegateRefusal.ApproximateHistory(d.Metric.Value, d.TableId, t)) t

                    return DelegatedPoint.Answered refused
                | SnapshotFidelity.Exact ->
                    match! tables.At(scope.ScopeId, d.TableId, t) |> orFault d with
                    | None -> return DelegatedPoint.Answered []
                    | Some commit ->
                        let isCurrent = current |> Option.exists (fun c -> c.Run.RunId = commit.Run.RunId)
                        let! rows = rowsOf commit t

                        let! facts =
                            (if isCurrent then
                                 mint scope d rows
                             else
                                 quoteHistorical scope d rows)
                            |> orFault d

                        return DelegatedPoint.Answered(inLineage imported facts)
        }

    let query (scope: ResolvedScope) (q: FactQuery) : Async<Fact list> = async {
        match! pointReadFrom scope q with
        | ReadFrom.Inner -> return! innerQuery scope q
        | ReadFrom.DelegateTable(d, imported) ->
            match! pointFromTable scope d imported q with
            | DelegatedPoint.InnerAnswers -> return! innerQuery scope q
            | DelegatedPoint.Answered facts -> return facts
    }

    let queryWithCompetition (scope: ResolvedScope) (q: FactQuery) : Async<FactWithCompetition list> = async {
        match! pointReadFrom scope q with
        | ReadFrom.Inner -> return! innerQueryWithCompetition scope q
        | ReadFrom.DelegateTable(d, imported) ->
            match! pointFromTable scope d imported q with
            | DelegatedPoint.InnerAnswers -> return! innerQueryWithCompetition scope q
            | DelegatedPoint.Answered facts -> return facts |> List.map (fun f -> { Fact = f; CompetingMethods = [] })
    }

    /// A population read the delegate's table answers.
    let populationFromTable
        (scope: ResolvedScope)
        (d: DelegateFact)
        (imported: string option)
        (q: PopulationQuery)
        : Async<Result<PopulationResult, string>> =
        async {
            let declared =
                registry
                |> Option.bind (fun r -> r.TryGetMetric q.Metric.Value)
                |> Option.map _.Direction

            // The ordering first, as every store resolves it: a refusal costs
            // no table read (GP 9).
            match PopulationOrdering.resolve q.Metric.Value q.Ordering declared with
            | Error refused -> return Error refused
            | Ok direction ->
                let k = PopulationQuery.effectiveTopK q

                let empty: PopulationResult = {
                    Ranked = []
                    Direction = direction
                    EffectiveTopK = k
                    Truncated = false
                    Stats = PopulationStats.empty
                }

                // The path clauses as the ONE prefix the table's declared
                // query reads. A filter that query cannot express is refused
                // by name, before any table read: dropping it would rank a
                // wider population than the one asked about.
                let path =
                    match q.PathAnyOf, q.PathExclude with
                    | None, None -> Ok(Some q.PathPrefix)
                    | _ ->
                        match PopulationQuery.asOnePrefix q with
                        | UnderPrefix [] -> Ok(Some None)
                        | UnderPrefix prefix -> Ok(Some(Some prefix))
                        | NoSubject -> Ok None
                        | NotOnePrefix filter ->
                            Error(
                                sprintf
                                    "the population read for '%s' on delegated table '%s' is refused: the table's declared query reads one path prefix, and the %s filter asked for cannot be stated as one. Ask for one branch at a time, or drop the filter."
                                    q.Metric.Value
                                    d.TableId
                                    filter
                            )

                match path with
                | Error refused -> return Error refused
                | Ok None -> return Ok empty
                | Ok(Some pathPrefix) ->
                    let! current = tables.Current(scope.ScopeId, d.TableId) |> orFault d

                    let! target = async {
                        match q.AsOf, d.History with
                        | None, _ -> return Ok(current, true)
                        | Some t, SnapshotFidelity.Approximate ->
                            return
                                Error(
                                    DelegateRefusal.describe (
                                        DelegateRefusal.ApproximateHistory(d.Metric.Value, d.TableId, t)
                                    )
                                )
                        | Some t, SnapshotFidelity.Exact ->
                            let! found = tables.At(scope.ScopeId, d.TableId, t) |> orFault d

                            let isCurrent =
                                match current, found with
                                | Some c, Some f -> c.Run.RunId = f.Run.RunId
                                | _ -> false

                            return Ok(found, isCurrent)
                    }

                    match target with
                    | Error refused -> return Error refused
                    | Ok(None, _) -> return Ok empty
                    | Ok(Some commit, isCurrent) ->
                        let at = q.AsOf |> Option.defaultValue (now ())

                        // A read for one imported lineage ranks the rows that
                        // lineage holds: those under the root member of the
                        // run's origin for that certificate, so the prefix is
                        // narrowed to it. A run with no origin for it holds no
                        // row in that lineage.
                        let! prefix = async {
                            match imported with
                            | None -> return Ok(Some pathPrefix)
                            | Some certificateRef ->
                                let! provenance = tables.Provenance(scope.ScopeId, commit.Run) |> orFault d

                                return
                                    DelegateLineages.importedPrefix provenance certificateRef pathPrefix
                                    |> Result.mapError (fun roots ->
                                        sprintf
                                            "the population read for Imported %s on delegated table '%s' is refused: its run imports that certificate under %d root members (%s), and a ranked read over one imported lineage is answered from one root"
                                            certificateRef
                                            d.TableId
                                            (List.length roots)
                                            (String.concat ", " roots))
                        }

                        match prefix with
                        | Error refused -> return Error refused
                        | Ok None -> return Ok empty
                        | Ok(Some pathPrefix) ->
                            let pushed =
                                DelegateTableRead.Ranked(pathPrefix, q.PeriodOverlaps, q.Threshold, direction, k)

                            match! read scope d commit.Run pushed at |> orFault d with
                            | DelegateTableAnswer.Ranked(rows, stats, truncated) ->
                                let! facts =
                                    (if isCurrent then
                                         mint scope d rows
                                     else
                                         quoteHistorical scope d rows)
                                    |> orFault d

                                return
                                    Ok {
                                        Ranked = facts
                                        Direction = direction
                                        EffectiveTopK = k
                                        Truncated = truncated
                                        Stats = stats
                                    }
                            | _ -> return Ok empty
        }

    let population (scope: ResolvedScope) (q: PopulationQuery) : Async<Result<PopulationResult, string>> = async {
        match! populationReadFrom scope q with
        | ReadFrom.Inner -> return! innerPopulation scope q
        | ReadFrom.DelegateTable(d, imported) -> return! populationFromTable scope d imported q
    }

    let get (scope: ResolvedScope) (factId: string) : Async<Fact option> = async {
        match! innerGet scope factId with
        | Some fact -> return Some fact
        | None ->
            if List.isEmpty delegates then
                return None
            else
                match! tables.Quote(scope.ScopeId, factId) with
                | Ok quote -> return quote
                | Error detail -> return raise (FactStoreFault(FactStoreError.StorageFault("delegated quote", detail)))
    }

    let chain (scope: ResolvedScope) (factId: string) : Async<Fact list> = async {
        match! innerChain scope factId with
        | [] ->
            match! get scope factId with
            | Some quote -> return [ quote ]
            | None -> return []
        | found -> return found
    }

    let runOf (scopeId: string) (metric: MetricRef) = async {
        match delegateOf metric with
        | None -> return Ok None
        | Some d ->
            match! tables.Current(scopeId, d.TableId) with
            | Error e -> return Error(refusal d e)
            | Ok commit -> return Ok(commit |> Option.map _.Run)
    }

    /// The composed delegate records, as declared.
    member _.Delegates = delegates

    /// The delegate records advanced to their tables' current runs in a
    /// scope — the records as the fact tier holds them. A delegate whose
    /// table has never committed there keeps `Watermark = None`.
    member _.Current(scopeId: string) : Async<Result<DelegateFact list, DelegateRefusal>> = async {
        let! resolved =
            delegates
            |> List.map (fun d -> async {
                match! tables.Current(scopeId, d.TableId) with
                | Error e -> return Error(refusal d e)
                | Ok None -> return Ok d
                | Ok(Some commit) -> return Ok(DelegateFact.advance commit.Run d)
            })
            |> Async.Sequential

        return
            resolved
            |> Array.fold
                (fun acc r ->
                    match acc, r with
                    | Error e, _
                    | _, Error e -> Error e
                    | Ok xs, Ok d -> Ok(xs @ [ d ]))
                (Ok [])
    }

    interface IDelegatedFactWalks with
        member _.Delegates = delegates

        member _.CurrentRun(scope, metric) = runOf scope.ScopeId metric

        member _.CurrentRows(scope, metric, subjects) = async {
            let scopeId = scope.ScopeId

            match delegateOf metric with
            | None -> return Ok []
            | Some d ->
                match! tables.Current(scopeId, d.TableId) with
                | Error e -> return Error(refusal d e)
                | Ok None -> return Ok []
                | Ok(Some commit) ->

                    let! answers =
                        subjects
                        |> List.distinct
                        |> List.map (fun path -> read scope d commit.Run (DelegateTableRead.Point(path, None)) (now ()))
                        |> Async.Sequential

                    return
                        answers
                        |> Array.fold
                            (fun acc answer ->
                                match acc, answer with
                                | Error e, _ -> Error e
                                | _, Error e -> Error(refusal d e)
                                | Ok xs, Ok(DelegateTableAnswer.Rows rows) -> Ok(xs @ (rows |> List.map _.Row))
                                | Ok xs, Ok _ -> Ok xs)
                            (Ok [])
        }

        member _.ChildTotals(scope, metric, periodOverlaps) = async {
            let scopeId = scope.ScopeId

            match delegateOf metric with
            | None -> return Ok []
            | Some d ->
                match! tables.Current(scopeId, d.TableId) with
                | Error e -> return Error(refusal d e)
                | Ok None -> return Ok []
                | Ok(Some commit) ->
                    match! read scope d commit.Run (DelegateTableRead.ChildTotals periodOverlaps) (now ()) with
                    | Ok(DelegateTableAnswer.Totals totals) -> return Ok totals
                    | Ok _ -> return Ok []
                    | Error e -> return Error(refusal d e)
        }

    // The disclosure-taint index is the inner store's. Every fact a delegate
    // mints is asserted through the inner store, and a read naming no metric
    // (the whole-scope listing the index is rebuilt from) is never delegated,
    // so the index rebuilt from this store is exactly the inner store's. An
    // inner store that keeps no index is listed, as the gate would list this
    // one.
    interface IDisclosureTaintIndexSource with
        member _.ReadTaintIndex(scope: ResolvedScope) =
            match inner with
            | :? IDisclosureTaintIndexSource as source -> source.ReadTaintIndex scope
            | _ -> async {
                let! all =
                    innerQuery scope {
                        FactQuery.all with
                            IncludeSuperseded = true
                    }

                return DisclosureTaintIndex.ofFacts all
              }

    interface IFactStore with
        // Writes are the inner store's: a delegate never intercepts an
        // assertion (a hand assertion of a delegated metric is the "two
        // homes" the fact-table preflight warns about, not something this
        // layer re-routes).
        member _.Assert(scope: ResolvedScope, draft: FactDraft) = inner.Assert(scope, draft)
        member _.AssertBatch(scope: ResolvedScope, drafts: FactDraft list) = inner.AssertBatch(scope, drafts)

        member _.Get(scope: ResolvedScope, factId: string) =
            FactStoreResult.ofRead "get" (get scope factId)

        member _.Query(scope: ResolvedScope, q: FactQuery) =
            FactStoreResult.ofRead "query" (query scope q)

        member _.QueryWithCompetition(scope: ResolvedScope, q: FactQuery) =
            FactStoreResult.ofRead "query" (queryWithCompetition scope q)

        member _.QuerySupersessionChain(scope: ResolvedScope, factId: string) =
            FactStoreResult.ofRead "supersession chain" (chain scope factId)

        member _.QueryPopulation(scope: ResolvedScope, q: PopulationQuery) = async {
            match! FactStoreResult.ofRead "population query" (population scope q) with
            | Ok(Ok result) -> return Ok result
            | Ok(Error refusal) -> return Error(FactStoreError.Refused refusal)
            | Error fault -> return Error fault
        }

/// What a commit does to the fact tier: advance each delegate's record, and
/// walk the facts quoted from earlier runs through the Phase 561
/// invalidation path.
module internal DelegateRefresh =

    // A quoted fact's value re-read from the table's CURRENT run — the
    // recompute step of the Phase 561 walk for a delegated metric. A row the
    // current run no longer carries recomputes to an `Absent` in the fact's
    // lineage, so a stale value is superseded rather than left standing.
    //
    // Deliberately a function rather than an `IFactRecomputer`: that seam is
    // the deployment's compute engine, composed once per deployment, and the
    // refresh must not displace it or be displaced by it.
    let private recomputed
        (tables: DelegateTableStore)
        (delegateFact: DelegateFact)
        (scopeId: string)
        (commit: DelegateTableCommit)
        (fact: Fact)
        : Async<Result<FactDraft, string>> =
        async {
            match! tables.Minting(scopeId, commit.Run) with
            | Error e -> return Error e
            | Ok minting ->
                match!
                    tables.Read(
                        scopeId,
                        delegateFact,
                        commit.Run,
                        DelegateTableRead.Point(fact.Subject.Path, Some fact.Period),
                        (fun _ -> Fresh)
                    )
                with
                | Error e -> return Error e
                | Ok(DelegateTableAnswer.Rows rows) ->
                    match
                        rows
                        |> List.tryFind (fun m ->
                            m.Row.Period.From = fact.Period.From && m.Row.Period.To = fact.Period.To)
                    with
                    // A row the image carries is minted under the run that
                    // wrote it — for a row a slice carried forward, the fact
                    // that run minted.
                    | Some m -> return Ok(DelegateMint.draft m.Minting delegateFact m.Run (MintedRow m.Row))
                    | None -> return Ok(DelegateMint.draft minting delegateFact commit.Run (RemovedQuote fact))
                | Ok _ -> return Ok(DelegateMint.draft minting delegateFact commit.Run (RemovedQuote fact))
        }

    /// Refresh the delegates of one table after a commit: assert each
    /// delegate's record for the new run (it supersedes the last), then find
    /// the quoted heads minted under any other run
    /// (`FactInvalidation.staleDelegatedHeads` — the delegated form of the
    /// Phase 561 invalidation walk) and re-assert each from the new run, which
    /// supersedes it within its own lineage. `store` is the COMPOSED store, so
    /// every re-assertion reaches whatever decorates it (the coverage
    /// narrative's trigger among them). Returns the facts it superseded.
    let internal afterCommit
        (store: IFactStore)
        (tables: DelegateTableStore)
        (delegates: DelegateFact list)
        (scope: ResolvedScope)
        (tableId: string)
        (run: DelegateRun)
        : Async<Result<Fact list, string>> =
        async {
            let scopeId = scope.ScopeId
            let token = DelegateFact.token run.Watermark

            match! tables.Current(scopeId, tableId) with
            | Error e -> return Error e
            | Ok None -> return Ok []
            | Ok(Some commit) ->
                let rec walk (remaining: DelegateFact list) (acc: Fact list) = async {
                    match remaining with
                    | [] -> return Ok acc
                    | d :: rest ->
                        match! store.Assert(scope, DelegateMint.recordDraft d run) with
                        | Error e -> return Error(FactStoreError.describe e)
                        | Ok _ ->
                            let! own =
                                FactInvalidation.staleDelegatedHeads store scope d token
                                |> FactStoreResult.orRaiseAsync

                            // The imported lineages too (Phase 938).
                            let! imported = async {
                                match! tables.ImportedLineages(scopeId, commit.Run) with
                                | Ok(_ :: _ as certificates) ->
                                    let! heads =
                                        DelegateLineages.importedHeads
                                            (fun q -> store.Query(scope, q) |> FactStoreResult.orRaiseAsync)
                                            d
                                            certificates
                                            id

                                    return
                                        heads
                                        |> List.filter (fun (method, f) -> DelegateLineages.isStale d token method f)
                                        |> List.map snd
                                | _ -> return []
                            }

                            let stale = own @ imported

                            let rec supersede (facts: Fact list) (done': Fact list) = async {
                                match facts with
                                | [] -> return Ok done'
                                | fact :: more ->
                                    match! recomputed tables d scopeId commit fact with
                                    | Error e -> return Error e
                                    | Ok draft ->
                                        match! store.Assert(scope, draft) with
                                        | Error e -> return Error(FactStoreError.describe e)
                                        | Ok _ -> return! supersede more (fact :: done')
                            }

                            match! supersede stale [] with
                            | Error e -> return Error e
                            | Ok superseded -> return! walk rest (acc @ superseded)
                }

                return! walk (delegates |> List.filter (fun d -> d.TableId = tableId)) []
        }

/// The `IFactTableWriter` that holds a delegated table (Phase 889): the
/// default writer's run lifecycle — open, stage, validate-all-or-nothing,
/// swap, watermark, change summary, one audit record — over row images
/// instead of facts. A table bound anywhere else is handed to `inner`
/// (the composed writer), so this is the one writer a module resolves.
///
/// A commit then refreshes the table's delegates through the composed store
/// (`store`, resolved per commit): the delegate records advance and every
/// fact quoted from an earlier run is superseded. A refresh that fails is
/// not a failed commit — the table has moved, and the read path re-mints
/// and retires on its own (a stale head is never served) — so the commit
/// reports success and the refresh is retried by the next commit.
type DelegateTableWriter
    internal
    (
        inner: IFactTableWriter option,
        tables: DelegateTableStore,
        events: IEventStore,
        registrations: IFactTableRegistry,
        registry: IMetricRegistry option,
        delegates: DelegateFact list,
        store: unit -> IFactStore option,
        clock: unit -> DateTime
    ) =

    let storage = tables.Storage

    let now () =
        let t = clock().ToUniversalTime()
        DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc)

    let isDelegated (tableId: string) =
        registrations.DestinationOf tableId = Some DelegateFact.Destination

    let hierarchyOf (table: FactTableDefinition) =
        registry |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)

    let passOn (tableId: string) =
        Error(FactTableNotBoundHere(tableId, registrations.DestinationOf tableId, DelegateFact.Destination))

    let loadRun (scopeId: string) (runId: string) = async {
        match! FactTableRunRecordIo.tryGet storage scopeId (tables.RecordName runId) with
        | Ok(Some run) -> return Ok(Some run)
        | Ok None -> return Ok None
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
        =
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

    // Phase 977 — the orphan sweep over the delegate layout. An ended run's
    // staged rows are read by nothing (`stagedRows` reads an open run only),
    // and neither is a rejected or abandoned run's kept provenance; a
    // committed run's provenance STAYS, because its rows are minted under it
    // for as long as they are read.
    let orphanLayout: FactTableOrphanSweep.Layout = {
        StagedRoot = tables.StagedRoot
        ProvenanceRoots = [ tables.ProvenanceRoot; tables.MethodRoot ]
        RecordName = tables.RecordName
        KeepsProvenance =
            function
            | FactTableRunStatus.Committed _ -> true
            | _ -> false
    }

    // The runs every table head records as committed: their provenance is
    // kept, so the sweep reads no record for them — its cost follows the
    // scope's tables and its leftovers, never its committed history.
    let committedRuns (scopeId: string) = async {
        let! heads = storage.List(scopeId, tables.HeadsPrefix)

        let rec load (remaining: string list) (acc: Set<string>) = async {
            match remaining with
            | [] -> return acc
            | name :: rest ->
                match! FactTableBlobIo.tryGet<DelegateTableHead> storage scopeId name with
                | Ok(Some head) -> return! load rest (head.Commits |> List.fold (fun s c -> Set.add c.Run.RunId s) acc)
                // An unreadable head spares nothing; the sweep then reads
                // each run's record, which keeps a committed run's anyway.
                | Ok None
                | Error _ -> return! load rest acc
        }

        return! load heads Set.empty
    }

    let sweepOrphans (scopeId: string) = async {
        let! spared = committedRuns scopeId
        return! FactTableOrphanSweep.run storage scopeId orphanLayout spared
    }

    // The end of every run: its terminal record is persisted, so the sweep
    // reclaims its leftovers and retries any earlier run's the store
    // refused. What a refused delete leaves stays listed, and the scope's
    // next run (or a host's `SweepOrphans`) sweeps it again.
    let endRun (scopeId: string) = async {
        let! _ = sweepOrphans scopeId
        ()
    }

    let stagedRows (scopeId: string) (runId: string) = async {
        let! names = storage.List(scopeId, tables.StagedPrefix runId)

        let rec load (remaining: string list) (acc: FactTableRow list list) = async {
            match remaining with
            | [] -> return Ok(acc |> List.rev |> List.concat)
            | name :: rest ->
                match! FactTableBlobIo.tryGet<FactTableRow list> storage scopeId name with
                | Ok(Some rows) -> return! load rest (rows :: acc)
                | Ok None -> return! load rest acc
                | Error e -> return Error e
        }

        return! load (names |> List.sort) []
    }

    let loadHead (scopeId: string) (tableId: string) = async {
        match! FactTableBlobIo.tryGet<DelegateTableHead> storage scopeId (tables.HeadName tableId) with
        | Ok(Some head) -> return Ok head
        | Ok None -> return Ok { Commits = [] }
        | Error e -> return Error e
    }

    let reject (scopeId: string) (table: FactTableDefinition) (run: OpenFactTableRun) (error: FactTableWriteError) = async {
        let reason = FactTableWriteError.describe error
        let closed = FactTableRun.reject reason run

        match! FactTableBlobIo.put storage scopeId (tables.RecordName closed.RunId) closed with
        | Error e -> return Error e
        | Ok() ->
            do! endRun scopeId
            do! audit scopeId table closed FactTableEvents.RunRejectedType "Rejected" None (Some reason)
            return Error error
    }

    // The reach a run records for the coverage narrative and the walks.
    let reachOf
        (table: FactTableDefinition)
        (runId: string)
        (watermark: FactTableWatermark)
        (rows: FactTableRow list)
        : DelegateRun =
        {
            TableId = table.Id
            RunId = runId
            Watermark = watermark
            RowCount = rows.Length
            SubjectCount = rows |> List.map _.Subject |> List.distinct |> List.length
            ComparableByColumn =
                table.Columns
                |> List.map (fun column ->
                    column.Metric,
                    rows
                    |> List.filter (fun r ->
                        r.Values
                        |> Map.tryFind column.Metric
                        |> Option.bind PopulationValue.comparable
                        |> Option.isSome)
                    |> List.length)
                |> Map.ofList
            PeriodFrom =
                if List.isEmpty rows then
                    None
                else
                    Some(rows |> List.map _.Period.From |> List.min)
            PeriodTo =
                if List.isEmpty rows then
                    None
                else
                    Some(rows |> List.map _.Period.To |> List.max)
        }

    let periodText (period: TemporalExtent) =
        sprintf "%s..%s" (period.From.ToString "o") (period.To.ToString "o")

    // Why a period slice cannot be taken over an image holding rows stored
    // before rows were attributed: the slice would carry them forward, and a
    // carried row must name the run that wrote it.
    let unattributedRefusal (unattributed: DelegateStoredRow list) =
        let first = List.head unattributed

        sprintf
            "a period slice would carry forward %d row(s) stored before rows were attributed, the first [%s] at %s; record their attribution with the facts-attribute-delegate-rows operator task (toolup facts migrate --attribute-delegate-rows), or commit a whole-table run"
            unattributed.Length
            (String.concat "/" first.Subject)
            (periodText first.Period)

    // The rows a run opened with `slice` would carry forward from the image
    // at `sequence` that name no writer — read from storage, never the cache,
    // so an attribution recorded since the image was cached is seen.
    let unattributedCarried (scopeId: string) (tableId: string) (sequence: int64) (slice: TemporalExtent) = async {
        if sequence = 0L then
            return Ok []
        else
            match! tables.StoredImage(scopeId, tableId, sequence) with
            | Error e -> return Error e
            | Ok None -> return Ok []
            | Ok(Some image) ->
                return
                    Ok(
                        image
                        |> List.filter (fun r ->
                            not (FactTableRunOptions.contains slice r.Period.From r.Period.To)
                            && not (DelegateStoredRow.isAttributed r))
                    )
    }

    let commit (scope: ResolvedScope) (opened: OpenFactTableRun) (table: FactTableDefinition) = async {
        let scopeId = scope.ScopeId
        let run = opened.Record
        let options = FactTableRunOptions.ofRun run.Options

        match! loadHead scopeId table.Id with
        | Error e -> return Error e
        | Ok head ->
            let current = List.tryLast head.Commits

            let sequence =
                current |> Option.map _.Run.Watermark.Sequence |> Option.defaultValue 0L

            if sequence <> run.BaseSequence then
                return! reject scopeId table opened (FactTableCommitConflict(run.RunId, run.BaseSequence, sequence))
            else
                match! stagedRows scopeId run.RunId with
                | Error e -> return Error e
                | Ok rows ->
                    let defects =
                        FactTableValidation.defects table (hierarchyOf table) (List.indexed rows)
                        @ FactTableValidation.outsideSlice table options.Slice (List.indexed rows)
                        |> List.sortBy _.Position

                    match defects with
                    | _ :: _ as defects ->
                        return! reject scopeId table opened (FactTableRowsRejected(run.RunId, defects))
                    | [] ->
                        // The image the run replaces, read from storage rather
                        // than the cache: every row it carries forward keeps
                        // the attribution recorded for it.
                        let! previous = async {
                            match current with
                            | None -> return Ok []
                            | Some c ->
                                match! tables.StoredImage(scopeId, table.Id, c.Run.Watermark.Sequence) with
                                | Ok(Some image) -> return Ok image
                                // A head names its image: a missing one is never
                                // read as an empty table, which would carry
                                // nothing and withdraw nothing.
                                | Ok None ->
                                    return
                                        Error(
                                            FactTableStorageFailure(
                                                sprintf
                                                    "the rows of run %s are missing"
                                                    (DelegateFact.token c.Run.Watermark)
                                            )
                                        )
                                | Error e -> return Error e
                        }

                        match previous with
                        | Error e -> return Error e
                        | Ok previous ->
                            // The rows the run replaces: the whole image, or the
                            // committed rows inside its slice. Every other row
                            // is carried forward under the run that wrote it.
                            let inSlice (row: DelegateStoredRow) =
                                options.Slice
                                |> Option.forall (fun slice ->
                                    FactTableRunOptions.contains slice row.Period.From row.Period.To)

                            let split =
                                match options.Slice with
                                | Some slice ->
                                    previous
                                    |> List.filter (fun r -> FactTableRunOptions.splits slice r.Period.From r.Period.To)
                                | None -> []

                            let carried = previous |> List.filter (inSlice >> not)

                            let unattributed = carried |> List.filter (DelegateStoredRow.isAttributed >> not)

                            match split, unattributed with
                            | first :: _, _ ->
                                let reason =
                                    sprintf
                                        "the slice splits %d committed row(s), the first [%s] at %s; a slice must contain each committed row it overlaps"
                                        split.Length
                                        (String.concat "/" first.Subject)
                                        (periodText first.Period)

                                return! reject scopeId table opened (FactTableRunOptionsRefused(table.Id, reason))
                            | [], _ :: _ ->
                                return!
                                    reject
                                        scopeId
                                        table
                                        opened
                                        (FactTableRunOptionsRefused(table.Id, unattributedRefusal unattributed))
                            | [], [] ->
                                let next = sequence + 1L
                                let written = FactTableSnapshot.ofRows table next rows

                                // Canonical order, as the snapshot orders them.
                                let image =
                                    carried @ (rows |> List.map (DelegateStoredRow.written run.RunId))
                                    |> List.sortBy (fun r -> r.Subject, r.Period.From, r.Period.To)

                                let tableRows = image |> List.map DelegateStoredRow.row

                                let replaced =
                                    current
                                    |> Option.map (fun c ->
                                        FactTableSnapshot.ofRows
                                            table
                                            c.Run.Watermark.Sequence
                                            (previous |> List.filter inSlice |> List.map DelegateStoredRow.row))

                                let watermark: FactTableWatermark = {
                                    TableId = table.Id
                                    Sequence = next
                                    CommittedAt = now ()
                                    ContentDigest =
                                        FactTableSnapshot.digest (FactTableSnapshot.ofRows table next tableRows)
                                }

                                let result: FactTableCommit = {
                                    Watermark = watermark
                                    RowCount = rows.Length
                                    // The delegate writes no fact: the table IS the
                                    // population, and only a quote mints one.
                                    FactsWritten = 0
                                    Change = FactTableSnapshot.changes table.Hierarchy replaced written
                                    BatchDigest = BatchAssertReceipt.empty.Digest
                                }

                                // The reach of the table after the commit: the
                                // carried rows and the run's.
                                let reach = reachOf table run.RunId watermark tableRows

                                let committed = FactTableRun.commit result opened

                                // The run's imported lineages are recorded before
                                // it becomes current, so every walk that reads
                                // the current run finds them.
                                let! recorded = async {
                                    match!
                                        FactTableRunProvenance.read storage scopeId (tables.ProvenanceName run.RunId)
                                    with
                                    | Error e -> return Error e
                                    | Ok provenance -> return! tables.RecordLineages(scopeId, table.Id, provenance)
                                }

                                match recorded with
                                | Error e -> return Error e
                                | Ok() ->
                                    match!
                                        FactTableBlobIo.put storage scopeId (tables.RowsName table.Id next) image
                                    with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        // The swap: the table's current run moves here.
                                        let head' = {
                                            Commits = head.Commits @ [ { Run = reach; Commit = result } ]
                                        }

                                        match! FactTableBlobIo.put storage scopeId (tables.HeadName table.Id) head' with
                                        | Error e -> return Error e
                                        | Ok() ->
                                            match!
                                                FactTableBlobIo.put
                                                    storage
                                                    scopeId
                                                    (tables.RecordName run.RunId)
                                                    committed
                                            with
                                            | Error e -> return Error e
                                            | Ok() ->
                                                do! endRun scopeId

                                                do!
                                                    audit
                                                        scopeId
                                                        table
                                                        committed
                                                        FactTableEvents.RunCommittedType
                                                        "Committed"
                                                        (Some result)
                                                        None

                                                match store () with
                                                | Some composed ->
                                                    let! _ =
                                                        DelegateRefresh.afterCommit
                                                            composed
                                                            tables
                                                            delegates
                                                            scope
                                                            table.Id
                                                            reach

                                                    ()
                                                | None -> ()

                                                return Ok result
    }

    interface IFactTableWriter with

        member _.OpenRun(scope: ResolvedScope, tableId, ?provenance, ?options) = async {
            let scopeId = scope.ScopeId

            if not (isDelegated tableId) then
                match inner with
                | Some w -> return! w.OpenRun(scope, tableId, ?provenance = provenance, ?options = options)
                | None ->
                    match registrations.TryGetTable tableId with
                    | None -> return Error(FactTableUndeclared tableId)
                    | Some _ -> return passOn tableId
            else
                match registrations.TryGetTable tableId with
                | None -> return Error(FactTableUndeclared tableId)
                | Some table ->
                    // A delegated table holds ONE row image per committed run,
                    // and every row of it names the run that wrote it. A run
                    // with a period slice carries every row outside its slice
                    // forward under that row's own run, so a slice over an
                    // image whose rows predate attribution is refused here,
                    // before the table is held: carried, those rows could
                    // name no writer.
                    let! normalised = async {
                        match FactTableRunOptions.normalise (defaultArg options FactTableRunOptions.none) with
                        | Error reason -> return Error(FactTableRunOptionsRefused(table.Id, reason))
                        | Ok({ Slice = Some slice } as o) ->
                            match! loadHead scopeId tableId with
                            | Error e -> return Error e
                            | Ok head ->
                                let sequence =
                                    head.Commits
                                    |> List.tryLast
                                    |> Option.map _.Run.Watermark.Sequence
                                    |> Option.defaultValue 0L

                                match! unattributedCarried scopeId tableId sequence slice with
                                | Error e -> return Error e
                                | Ok [] -> return Ok o
                                | Ok unattributed ->
                                    return Error(FactTableRunOptionsRefused(table.Id, unattributedRefusal unattributed))
                        | Ok o -> return Ok o
                    }

                    match normalised with
                    | Error e -> return Error e
                    | Ok declared ->
                        let runId = Guid.NewGuid().ToString("N")
                        let openedAt = now ()

                        match!
                            FactTableRunClaim.acquire
                                storage
                                scopeId
                                (tables.ClaimName table.Id)
                                table
                                openedAt
                                (loadRun scopeId)
                                runId
                        with
                        | Error e -> return Error e
                        | Ok() ->
                            let! opened = async {
                                match! loadHead scopeId tableId with
                                | Error e -> return Error e
                                | Ok head ->
                                    // What the run declared rides its record
                                    //, so every row it carries is
                                    // minted under its inputs and method.
                                    let run =
                                        FactTableRun.opened
                                            table.Id
                                            runId
                                            table.SchemaVersion
                                            openedAt
                                            (head.Commits
                                             |> List.tryLast
                                             |> Option.map _.Run.Watermark.Sequence
                                             |> Option.defaultValue 0L)
                                            (FactTableRunOptions.toRun declared)

                                    // The provenance first, as the default writer keeps it:
                                    // a run whose record exists never lacks it.
                                    match!
                                        FactTableRunProvenance.keep
                                            storage
                                            scopeId
                                            (tables.ProvenanceName run.RunId)
                                            (defaultArg provenance ComputedRun)
                                    with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        // The method also beside it, where a
                                        // replica of an earlier release looks
                                        // for it when it mints the run's rows
                                        // during a rolling upgrade.
                                        let! kept =
                                            match declared.Method with
                                            | None -> async { return Ok() }
                                            | Some method ->
                                                FactTableBlobIo.put storage scopeId (tables.MethodName run.RunId) method

                                        match kept with
                                        | Error e -> return Error e
                                        | Ok() ->
                                            match!
                                                FactTableBlobIo.put storage scopeId (tables.RecordName run.RunId) run
                                            with
                                            | Error e -> return Error e
                                            | Ok() -> return Ok run
                            }

                            match opened with
                            | Ok run -> return Ok run
                            | Error e ->
                                do! FactTableRunClaim.release storage scopeId (tables.ClaimName table.Id) runId
                                return Error e
        }

        member _.WriteRows(scope: ResolvedScope, runId, rows) = async {
            let scopeId = scope.ScopeId

            match! loadRun scopeId runId with
            | Error e -> return Error e
            | Ok None ->
                match inner with
                | Some w -> return! w.WriteRows(scope, runId, rows)
                | None -> return Error(FactTableRunUnknown runId)
            | Ok(Some run) ->
                match FactTableRun.tryOpen run with
                | Error refused -> return Error(FactTableWriteError.ofTransition refused)
                | Ok opened ->
                    if List.isEmpty rows then
                        return Ok run
                    else
                        match! FactTableBlobIo.put storage scopeId (tables.StagedName runId run.StagedRows) rows with
                        | Error e -> return Error e
                        | Ok() ->
                            let staged = FactTableRun.stage rows.Length opened

                            match! FactTableBlobIo.put storage scopeId (tables.RecordName runId) staged with
                            | Error e -> return Error e
                            | Ok() -> return Ok staged
        }

        member _.Commit(scope: ResolvedScope, runId) = async {
            let scopeId = scope.ScopeId

            match! loadRun scopeId runId with
            | Error e -> return Error e
            | Ok None ->
                match inner with
                | Some w -> return! w.Commit(scope, runId)
                | None -> return Error(FactTableRunUnknown runId)
            | Ok(Some run) ->
                match FactTableRun.tryOpen run with
                | Error refused -> return Error(FactTableWriteError.ofTransition refused)
                | Ok opened ->
                    match registrations.TryGetTable run.TableId with
                    | None -> return Error(FactTableUndeclared run.TableId)
                    | Some table ->
                        if not (isDelegated table.Id) then
                            return passOn table.Id
                        else
                            return! commit scope opened table
        }

        member _.Abandon(scope: ResolvedScope, runId, reason) = async {
            let scopeId = scope.ScopeId

            match! loadRun scopeId runId with
            | Error e -> return Error e
            | Ok None ->
                match inner with
                | Some w -> return! w.Abandon(scope, runId, reason)
                | None -> return Error(FactTableRunUnknown runId)
            | Ok(Some run) ->
                match FactTableRun.tryOpen run with
                | Error refused -> return Error(FactTableWriteError.ofTransition refused)
                | Ok opened ->
                    let closed = FactTableRun.abandon reason opened

                    match! FactTableBlobIo.put storage scopeId (tables.RecordName runId) closed with
                    | Error e -> return Error e
                    | Ok() ->
                        do! endRun scopeId

                        match registrations.TryGetTable run.TableId with
                        | Some table ->
                            do!
                                audit
                                    scopeId
                                    table
                                    closed
                                    FactTableEvents.RunAbandonedType
                                    "Abandoned"
                                    None
                                    (Some reason)
                        | None -> ()

                        return Ok closed
        }

        member _.Runs(scope: ResolvedScope, tableId) = async {
            let scopeId = scope.ScopeId

            if not (isDelegated tableId) then
                match inner with
                | Some w -> return! w.Runs(scope, tableId)
                | None ->
                    match registrations.TryGetTable tableId with
                    | None -> return Error(FactTableUndeclared tableId)
                    | Some _ -> return passOn tableId
            else
                let! names = storage.List(scopeId, tables.RecordsPrefix)

                let rec load (remaining: string list) (acc: FactTableRunRecord list) = async {
                    match remaining with
                    | [] -> return Ok acc
                    | name :: rest ->
                        match! FactTableRunRecordIo.tryGet storage scopeId name with
                        | Ok(Some run) when run.TableId = tableId -> return! load rest (run :: acc)
                        | Ok _ -> return! load rest acc
                        | Error e -> return Error e
                }

                match! load names [] with
                | Error e -> return Error e
                | Ok runs -> return Ok(runs |> List.sortByDescending (fun r -> r.OpenedAt, r.BaseSequence, r.RunId))
        }

        member this.Status(scope: ResolvedScope, tableId) = async {
            let scopeId = scope.ScopeId

            if not (isDelegated tableId) then
                match inner with
                | Some w -> return! w.Status(scope, tableId)
                | None ->
                    match registrations.TryGetTable tableId with
                    | None -> return Error(FactTableUndeclared tableId)
                    | Some _ -> return passOn tableId
            else
                match registrations.TryGetTable tableId with
                | None -> return Error(FactTableUndeclared tableId)
                | Some table ->
                    match! loadHead scopeId tableId with
                    | Error e -> return Error e
                    | Ok head ->
                        match! (this :> IFactTableWriter).Runs(scope, tableId) with
                        | Error e -> return Error e
                        | Ok runs ->
                            let at = now ()
                            let lastCommit = head.Commits |> List.tryLast |> Option.map _.Commit
                            let latest = runs |> List.tryHead

                            return
                                Ok {
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
        }

    interface IFactTableOrphanSweep with

        // The delegate layout, then the writer it hands every other table to.
        member _.SweepOrphans(scope: ResolvedScope) = async {
            let scopeId = scope.ScopeId
            let! own = sweepOrphans scopeId

            match inner with
            | None -> return Ok own
            | Some w ->
                match! FactTableOrphanSweep.sweep w scope with
                | Error e -> return Error e
                | Ok passed -> return Ok(FactTableOrphanSweepReport.combine own passed)
        }

/// Construction for the delegate pieces. The table store is internal, so a
/// deployment builds the decorator and the writer here, over ONE blob store
/// they share.
module DelegatedFactStore =

    /// The decorator over `inner`, reading delegated tables held in
    /// `storage`.
    let create
        (inner: IFactStore)
        (storage: IBlobStorage)
        (delegates: DelegateFact list)
        (registry: IMetricRegistry option)
        (clock: unit -> DateTime)
        : DelegatedFactStore =
        DelegatedFactStore(inner, DelegateTableStore storage, delegates, registry, clock)

    /// The writer holding delegated tables in `storage`, handing every other
    /// table to `inner`. `store` resolves the composed fact store at commit
    /// time for the refresh (`None` skips the refresh — the read path still
    /// never serves a stale head).
    let writer
        (inner: IFactTableWriter option)
        (storage: IBlobStorage)
        (events: IEventStore)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        (delegates: DelegateFact list)
        (store: unit -> IFactStore option)
        (clock: unit -> DateTime)
        : IFactTableWriter =
        DelegateTableWriter(inner, DelegateTableStore storage, events, tables, registry, delegates, store, clock)
        :> IFactTableWriter