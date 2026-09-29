// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Concurrent
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

/// An answer to a `DelegateTableRead`.
[<RequireQualifiedAccess>]
type internal DelegateTableAnswer =
    | Rows of DelegateTableRow list
    | Ranked of ranked: DelegateTableRow list * stats: PopulationStats * truncated: bool
    | Totals of DelegateChildTotal list

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

    /// The draft a row mints: the delegate's metric, method and class; the
    /// row's subject, period and value; the run's watermark as the result
    /// reference and among the input hashes. For an append-by-run table this
    /// is exactly the fact the default writer would have written.
    let draftOf (delegateFact: DelegateFact) (run: DelegateRun) (row: DelegateTableRow) : FactDraft =
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

    /// The content-addressed id a row mints under.
    let factIdOf (delegateFact: DelegateFact) (run: DelegateRun) (row: DelegateTableRow) : string =
        let draft = draftOf delegateFact run row
        Fact.compute draft.Subject draft.Metric draft.Period draft.Method draft.Evidence.InputHashes

    /// The draft that supersedes a quoted fact whose row the current run no
    /// longer carries — an `Absent` in the fact's own lineage.
    let removedDraft (delegateFact: DelegateFact) (run: DelegateRun) (fact: Fact) : FactDraft =
        draftOf delegateFact run {
            Subject = fact.Subject.Path
            Period = fact.Period
            Value = Absent(sprintf "removed by fact-table run %s" (DelegateFact.token run.Watermark))
        }

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
    /// from a run that is no longer current is reconstructed.
    let reconstruct (draft: FactDraft) (asOf: DateTime) : Fact = {
        FactId = Fact.compute draft.Subject draft.Metric draft.Period draft.Method draft.Evidence.InputHashes
        Subject = draft.Subject
        Metric = draft.Metric
        Value = draft.Value
        Period = draft.Period
        AsOf = asOf
        Method = draft.Method
        Evidence = draft.Evidence
        Confidence = draft.Confidence
        Supersedes = None
        Disclosure = draft.Disclosure
    }

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
    // handful of entries: the current run of each hot table.
    let cache =
        ConcurrentDictionary<string * string * int64 * string, FactTableRow array>()

    let CacheCap = 8

    member _.RecordName(runId: string) = sprintf "%sruns/%s.json" root runId
    member _.RecordsPrefix = sprintf "%sruns/" root
    member _.StagedPrefix(runId: string) = sprintf "%sstaged/%s/" root runId

    member this.StagedName (runId: string) (offset: int) =
        sprintf "%s%010d.json" (this.StagedPrefix runId) offset

    member _.HeadName(tableId: string) = sprintf "%shead/%s.json" root tableId

    member _.RowsName (tableId: string) (sequence: int64) =
        sprintf "%srows/%s/%020d.json" root tableId sequence

    member _.QuoteName(factId: string) = sprintf "%squotes/%s.json" root factId

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

    /// A committed run's rows.
    member this.Rows(scopeId: string, run: DelegateRun) : Async<Result<FactTableRow array, string>> = async {
        let key = scopeId, run.TableId, run.Watermark.Sequence, run.Watermark.ContentDigest

        match cache.TryGetValue key with
        | true, rows -> return Ok rows
        | _ ->
            match!
                FactTableBlobIo.tryGet<FactTableRow list>
                    storage
                    scopeId
                    (this.RowsName run.TableId run.Watermark.Sequence)
            with
            | Error e -> return Error(FactTableWriteError.describe e)
            | Ok None -> return Error(sprintf "the rows of run %s are missing" (DelegateFact.token run.Watermark))
            | Ok(Some rows) ->
                let image = List.toArray rows

                if cache.Count >= CacheCap then
                    cache.Clear()

                cache[key] <- image
                return Ok image
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
            match! this.Rows(scopeId, run) with
            | Error e -> return Error e
            | Ok rows ->
                let column = delegateFact.Query.ValueColumn

                let cell (row: FactTableRow) : DelegateTableRow option =
                    row.Values
                    |> Map.tryFind column
                    |> Option.map (fun value -> {
                        Subject = row.Subject
                        Period = row.Period
                        Value = value
                    })

                let overlaps (filter: TemporalExtent option) (period: TemporalExtent) =
                    filter |> Option.forall (fun p -> p.From < period.To && period.From < p.To)

                match read with
                | DelegateTableRead.Point(subject, periodOverlaps) ->
                    return
                        rows
                        |> Array.filter (fun r -> r.Subject = subject && overlaps periodOverlaps r.Period)
                        |> Array.choose cell
                        |> Array.toList
                        |> DelegateTableAnswer.Rows
                        |> Ok

                | DelegateTableRead.Ranked(prefix, periodOverlaps, threshold, direction, topK) ->
                    let prefixMatches (path: string list) =
                        prefix
                        |> Option.forall (fun p ->
                            List.length path >= List.length p && List.truncate (List.length p) path = p)

                    let matched =
                        rows
                        |> Array.filter (fun r -> prefixMatches r.Subject && overlaps periodOverlaps r.Period)
                        |> Array.choose cell
                        |> Array.filter (fun r ->
                            threshold |> Option.forall (fun t -> ValueThreshold.satisfies t r.Value))
                        |> Array.toList

                    let methodIdentity = Fact.methodIdentity delegateFact.Method

                    let memberOf (row: DelegateTableRow) : PopulationMember * DelegateTableRow =
                        {
                            FactId = DelegateMint.factIdOf delegateFact run row
                            Subject = {
                                Hierarchy = delegateFact.Query.Hierarchy
                                Path = row.Subject
                            }
                            Magnitude = PopulationValue.comparable row.Value
                            PeriodFrom = row.Period.From
                            PeriodTo = row.Period.To
                            AsOf = run.Watermark.CommittedAt
                            MethodIdentity = methodIdentity
                        },
                        row

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
                        rows
                        |> Array.filter (fun r -> overlaps periodOverlaps r.Period)
                        |> Array.choose cell
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

    /// A reconstruction recorded beside the log, by id.
    member this.Quote(scopeId: string, factId: string) : Async<Fact option> = async {
        match! FactTableBlobIo.tryGet<Fact> storage scopeId (this.QuoteName factId) with
        | Ok found -> return found
        | Error _ -> return None
    }

    /// Record a reconstruction beside the log (idempotent by id).
    member this.RecordQuote(scopeId: string, fact: Fact) : Async<Result<unit, string>> = async {
        match! FactTableBlobIo.put storage scopeId (this.QuoteName fact.FactId) fact with
        | Ok() -> return Ok()
        | Error e -> return Error(FactTableWriteError.describe e)
    }

// The accessor pair a delegated read runs over — the inner store bound to
// the scope form the caller holds, so the typed (`ResolvedScope`) door
// reaches the inner store through ITS typed overloads, never through a
// string it re-derived.
type private InnerScope = {
    ScopeId: string
    Assert: FactDraft -> Async<Result<Fact, string>>
    Get: string -> Async<Fact option>
    Query: FactQuery -> Async<Fact list>
    QueryWithCompetition: FactQuery -> Async<FactWithCompetition list>
    Chain: string -> Async<Fact list>
    Population: PopulationQuery -> Async<Result<PopulationResult, string>>
}

/// The `IFactStore` decorator a delegated metric is read through (Phase
/// 889). A delegated metric's point and population reads are pushed down to
/// its table and the rows returned are minted as ordinary facts; every
/// other read, and every write, reaches the inner store untouched.
///
/// **Which reads are delegated.** A point read (`Query` /
/// `QueryWithCompetition`) naming a delegated metric and a subject at the
/// table's level; a population read (`QueryPopulation`) naming a delegated
/// metric and its hierarchy, at the table's level or at no level, for the
/// delegate's method or no particular one. A read with no subject — "every
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

    let ofString (scopeId: string) : InnerScope = {
        ScopeId = scopeId
        Assert = fun d -> inner.Assert(scopeId, d)
        Get = fun id -> inner.Get(scopeId, id)
        Query = fun q -> inner.Query(scopeId, q)
        QueryWithCompetition = fun q -> inner.QueryWithCompetition(scopeId, q)
        Chain = fun id -> inner.QuerySupersessionChain(scopeId, id)
        Population = fun q -> inner.QueryPopulation(scopeId, q)
    }

    let ofResolved (scope: ResolvedScope) : InnerScope = {
        ScopeId = scope.ScopeId
        Assert = fun d -> inner.Assert(scope, d)
        Get = fun id -> inner.Get(scope, id)
        Query = fun q -> inner.Query(scope, q)
        QueryWithCompetition = fun q -> inner.QueryWithCompetition(scope, q)
        Chain = fun id -> inner.QuerySupersessionChain(scope, id)
        Population = fun q -> inner.QueryPopulation(scope, q)
    }

    let methodMatches (delegateFact: DelegateFact) (m: MethodRef option) =
        m
        |> Option.forall (fun m -> Fact.methodIdentity m = Fact.methodIdentity delegateFact.Method)

    let delegateOf (metric: MetricRef) =
        delegates |> List.tryFind (fun d -> d.Metric = metric)

    let pointDelegate (query: FactQuery) : DelegateFact option =
        match query.Subject, query.Metric with
        | Some subject, Some metric ->
            delegateOf metric
            |> Option.filter (fun d -> DelegateFact.covers d subject && methodMatches d query.Method)
        | _ -> None

    let populationDelegate (query: PopulationQuery) : DelegateFact option =
        delegateOf query.Metric
        |> Option.filter (fun d ->
            d.Query.Hierarchy = query.Hierarchy
            && (query.Level |> Option.forall (fun level -> level = d.Query.Level))
            && (match query.Methods with
                | CanonicalMethodOnly
                | AllCompetingMethods -> true
                | OneMethod m -> Fact.methodIdentity m = Fact.methodIdentity d.Method))

    let policyOf (metric: MetricRef) =
        registry
        |> Option.bind (fun r -> r.TryGetMetric metric.Value)
        |> Option.map _.Staleness
        |> Option.defaultValue UntilSuperseded

    let refusal (delegateFact: DelegateFact) (detail: string) =
        DelegateRefusal.SourceUnavailable(delegateFact.TableId, detail)

    let read (ops: InnerScope) (d: DelegateFact) (run: DelegateRun) (r: DelegateTableRead) (at: DateTime) =
        let policy = policyOf d.Metric
        tables.Read(ops.ScopeId, d, run, r, (fun m -> Freshness.deriveAt policy m.AsOf true at))

    // Mint rows of the CURRENT run as ordinary facts, in order. A repeated
    // read of an unchanged table re-asserts identical tuples, which the
    // store answers idempotently by content address.
    let mint (ops: InnerScope) (d: DelegateFact) (run: DelegateRun) (rows: DelegateTableRow list) = async {
        let rec go (remaining: DelegateTableRow list) (acc: Fact list) = async {
            match remaining with
            | [] -> return Ok(List.rev acc)
            | row :: rest ->
                match! ops.Assert(DelegateMint.draftOf d run row) with
                | Ok fact -> return! go rest (fact :: acc)
                | Error e -> return Error e
        }

        return! go rows []
    }

    // Quote rows of a run that is NO LONGER current: the fact minted while
    // it was current if there is one, else a reconstruction recorded beside
    // the log. Never an ordinary assertion — that would make a past value
    // the current head of its lineage.
    let quoteHistorical (ops: InnerScope) (d: DelegateFact) (run: DelegateRun) (rows: DelegateTableRow list) = async {
        let rec go (remaining: DelegateTableRow list) (acc: Fact list) = async {
            match remaining with
            | [] -> return Ok(List.rev acc)
            | row :: rest ->
                let draft = DelegateMint.draftOf d run row
                let rebuilt = DelegateMint.reconstruct draft run.Watermark.CommittedAt

                match! ops.Get rebuilt.FactId with
                | Some stored -> return! go rest (stored :: acc)
                | None ->
                    match! tables.RecordQuote(ops.ScopeId, rebuilt) with
                    | Ok() -> return! go rest (rebuilt :: acc)
                    | Error e -> return Error e
        }

        return! go rows []
    }

    // The quoted heads of one subject minted from a run that is no longer
    // current and whose row the current run no longer carries: superseded
    // by an `Absent` so a stale value is never served as a current head.
    let retireRemoved
        (ops: InnerScope)
        (d: DelegateFact)
        (run: DelegateRun)
        (subject: SubjectRef)
        (live: DelegateTableRow list)
        =
        async {
            let token = DelegateFact.token run.Watermark

            let! heads =
                ops.Query {
                    FactQuery.all with
                        Subject = Some subject
                        Metric = Some d.Metric
                        Method = Some d.Method
                }

            let removed =
                heads
                |> List.filter (fun f ->
                    FactInvalidation.isMintedUnderStaleRun d token f
                    && not (
                        live
                        |> List.exists (fun r -> r.Period.From = f.Period.From && r.Period.To = f.Period.To)
                    ))

            for fact in removed do
                let! _ = ops.Assert(DelegateMint.removedDraft d run fact)
                ()
        }

    // The point door's refusal: a typed `Absent` fact whose reason is the
    // refusal, recorded beside the log so the gate resolves it. Its id is
    // content-addressed over the question, so asking twice is one record.
    let refusedPoint (ops: InnerScope) (d: DelegateFact) (query: FactQuery) (reason: DelegateRefusal) (asOf: DateTime) = async {
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
        let! _ = tables.RecordQuote(ops.ScopeId, fact)
        return [ fact ]
    }

    /// The point read, delegated. `Ok None` means "not delegated — ask the
    /// inner store"; `Ok (Some facts)` is the answer.
    let pointRead (ops: InnerScope) (query: FactQuery) : Async<Result<Fact list option, DelegateRefusal>> = async {
        match pointDelegate query with
        | None -> return Ok None
        | Some d ->
            let subject = query.Subject.Value

            let rowsOf (commit: DelegateTableCommit) at = async {
                match! read ops d commit.Run (DelegateTableRead.Point(subject.Path, query.PeriodOverlaps)) at with
                | Ok(DelegateTableAnswer.Rows rows) -> return Ok rows
                | Ok _ -> return Ok []
                | Error e -> return Error(refusal d e)
            }

            match! tables.Current(ops.ScopeId, d.TableId) with
            | Error e -> return Error(refusal d e)
            | Ok current ->
                match query.AsOf, current with
                // Never committed: the table holds nothing, and neither does
                // the fact tier for it — the inner store answers (empty).
                | None, None -> return Ok None
                | None, Some commit ->
                    match! rowsOf commit (now ()) with
                    | Error e -> return Error e
                    | Ok rows ->
                        match! mint ops d commit.Run rows with
                        | Error e -> return Error(refusal d e)
                        | Ok _ ->
                            do! retireRemoved ops d commit.Run subject rows
                            // The minted facts are ordinary heads now, so the
                            // inner store answers with its own selection,
                            // history and competition rules intact.
                            return Ok None
                | Some t, _ ->
                    match d.History with
                    | SnapshotFidelity.Approximate ->
                        let! refused =
                            refusedPoint
                                ops
                                d
                                query
                                (DelegateRefusal.ApproximateHistory(d.Metric.Value, d.TableId, t))
                                t

                        return Ok(Some refused)
                    | SnapshotFidelity.Exact ->
                        match! tables.At(ops.ScopeId, d.TableId, t) with
                        | Error e -> return Error(refusal d e)
                        | Ok None -> return Ok(Some [])
                        | Ok(Some commit) ->
                            let isCurrent = current |> Option.exists (fun c -> c.Run.RunId = commit.Run.RunId)

                            match! rowsOf commit t with
                            | Error e -> return Error e
                            | Ok rows ->
                                if isCurrent then
                                    match! mint ops d commit.Run rows with
                                    | Error e -> return Error(refusal d e)
                                    | Ok facts -> return Ok(Some facts)
                                else
                                    match! quoteHistorical ops d commit.Run rows with
                                    | Error e -> return Error(refusal d e)
                                    | Ok facts -> return Ok(Some facts)
    }

    let query (ops: InnerScope) (q: FactQuery) : Async<Fact list> = async {
        match! pointRead ops q with
        | Ok None -> return! ops.Query q
        | Ok(Some facts) -> return facts
        // A table that cannot be read is a fault, not an answer: the point
        // door has no error channel, so the refusal travels as the typed
        // `Absent` the fact model reserves for "no value, and here is why".
        | Error refused ->
            match pointDelegate q with
            | Some d -> return! refusedPoint ops d q refused (q.AsOf |> Option.defaultValue (now ()))
            | None -> return []
    }

    let queryWithCompetition (ops: InnerScope) (q: FactQuery) : Async<FactWithCompetition list> = async {
        match! pointRead ops q with
        | Ok None -> return! ops.QueryWithCompetition q
        | Ok(Some facts) -> return facts |> List.map (fun f -> { Fact = f; CompetingMethods = [] })
        | Error _ ->
            let! facts = query ops q
            return facts |> List.map (fun f -> { Fact = f; CompetingMethods = [] })
    }

    let population (ops: InnerScope) (q: PopulationQuery) : Async<Result<PopulationResult, string>> = async {
        match populationDelegate q with
        | None -> return! ops.Population q
        | Some d ->
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

                let! current = tables.Current(ops.ScopeId, d.TableId)

                let! target =
                    match q.AsOf, d.History with
                    | None, _ -> async.Return(current |> Result.map (fun c -> c, true))
                    | Some t, SnapshotFidelity.Approximate ->
                        async.Return(
                            Error(
                                DelegateRefusal.describe (
                                    DelegateRefusal.ApproximateHistory(d.Metric.Value, d.TableId, t)
                                )
                            )
                        )
                    | Some t, SnapshotFidelity.Exact -> async {
                        match! tables.At(ops.ScopeId, d.TableId, t) with
                        | Error e -> return Error e
                        | Ok found ->
                            let isCurrent =
                                match current, found with
                                | Ok(Some c), Some f -> c.Run.RunId = f.Run.RunId
                                | _ -> false

                            return Ok(found, isCurrent)
                      }

                match target with
                | Error e -> return Error e
                | Ok(None, _) -> return Ok empty
                | Ok(Some commit, isCurrent) ->
                    let at = q.AsOf |> Option.defaultValue (now ())

                    let pushed =
                        DelegateTableRead.Ranked(q.PathPrefix, q.PeriodOverlaps, q.Threshold, direction, k)

                    match! read ops d commit.Run pushed at with
                    | Error e -> return Error(DelegateRefusal.describe (refusal d e))
                    | Ok(DelegateTableAnswer.Ranked(rows, stats, truncated)) ->
                        let! quoted =
                            if isCurrent then
                                mint ops d commit.Run rows
                            else
                                quoteHistorical ops d commit.Run rows

                        match quoted with
                        | Error e -> return Error e
                        | Ok facts ->
                            return
                                Ok {
                                    Ranked = facts
                                    Direction = direction
                                    EffectiveTopK = k
                                    Truncated = truncated
                                    Stats = stats
                                }
                    | Ok _ -> return Ok empty
    }

    let get (ops: InnerScope) (factId: string) : Async<Fact option> = async {
        match! ops.Get factId with
        | Some fact -> return Some fact
        | None ->
            if List.isEmpty delegates then
                return None
            else
                return! tables.Quote(ops.ScopeId, factId)
    }

    let chain (ops: InnerScope) (factId: string) : Async<Fact list> = async {
        match! ops.Chain factId with
        | [] ->
            match! get ops factId with
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

        member _.CurrentRun(scopeId, metric) = runOf scopeId metric

        member _.CurrentRows(scopeId, metric, subjects) = async {
            match delegateOf metric with
            | None -> return Ok []
            | Some d ->
                match! tables.Current(scopeId, d.TableId) with
                | Error e -> return Error(refusal d e)
                | Ok None -> return Ok []
                | Ok(Some commit) ->
                    let ops = ofString scopeId

                    let! answers =
                        subjects
                        |> List.distinct
                        |> List.map (fun path -> read ops d commit.Run (DelegateTableRead.Point(path, None)) (now ()))
                        |> Async.Sequential

                    return
                        answers
                        |> Array.fold
                            (fun acc answer ->
                                match acc, answer with
                                | Error e, _ -> Error e
                                | _, Error e -> Error(refusal d e)
                                | Ok xs, Ok(DelegateTableAnswer.Rows rows) -> Ok(xs @ rows)
                                | Ok xs, Ok _ -> Ok xs)
                            (Ok [])
        }

        member _.ChildTotals(scopeId, metric, periodOverlaps) = async {
            match delegateOf metric with
            | None -> return Ok []
            | Some d ->
                match! tables.Current(scopeId, d.TableId) with
                | Error e -> return Error(refusal d e)
                | Ok None -> return Ok []
                | Ok(Some commit) ->
                    match!
                        read (ofString scopeId) d commit.Run (DelegateTableRead.ChildTotals periodOverlaps) (now ())
                    with
                    | Ok(DelegateTableAnswer.Totals totals) -> return Ok totals
                    | Ok _ -> return Ok []
                    | Error e -> return Error(refusal d e)
        }

    interface IFactStore with
        // Writes are the inner store's: a delegate never intercepts an
        // assertion (a hand assertion of a delegated metric is the "two
        // homes" the fact-table preflight warns about, not something this
        // layer re-routes).
        member _.Assert(scopeId: string, draft: FactDraft) = inner.Assert(scopeId, draft)
        member _.Assert(scope: ResolvedScope, draft: FactDraft) = inner.Assert(scope, draft)
        member _.AssertBatch(scopeId: string, drafts: FactDraft list) = inner.AssertBatch(scopeId, drafts)
        member _.AssertBatch(scope: ResolvedScope, drafts: FactDraft list) = inner.AssertBatch(scope, drafts)
        member _.Get(scopeId: string, factId: string) = get (ofString scopeId) factId
        member _.Get(scope: ResolvedScope, factId: string) = get (ofResolved scope) factId
        member _.Query(scopeId: string, q: FactQuery) = query (ofString scopeId) q
        member _.Query(scope: ResolvedScope, q: FactQuery) = query (ofResolved scope) q

        member _.QueryWithCompetition(scopeId: string, q: FactQuery) =
            queryWithCompetition (ofString scopeId) q

        member _.QueryWithCompetition(scope: ResolvedScope, q: FactQuery) =
            queryWithCompetition (ofResolved scope) q

        member _.QuerySupersessionChain(scopeId: string, factId: string) = chain (ofString scopeId) factId
        member _.QuerySupersessionChain(scope: ResolvedScope, factId: string) = chain (ofResolved scope) factId
        member _.QueryPopulation(scopeId: string, q: PopulationQuery) = population (ofString scopeId) q
        member _.QueryPopulation(scope: ResolvedScope, q: PopulationQuery) = population (ofResolved scope) q

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
                    |> List.tryFind (fun r -> r.Period.From = fact.Period.From && r.Period.To = fact.Period.To)
                with
                | Some row -> return Ok(DelegateMint.draftOf delegateFact commit.Run row)
                | None -> return Ok(DelegateMint.removedDraft delegateFact commit.Run fact)
            | Ok _ -> return Ok(DelegateMint.removedDraft delegateFact commit.Run fact)
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
        (scopeId: string)
        (tableId: string)
        (run: DelegateRun)
        : Async<Result<Fact list, string>> =
        async {
            let token = DelegateFact.token run.Watermark

            match! tables.Current(scopeId, tableId) with
            | Error e -> return Error e
            | Ok None -> return Ok []
            | Ok(Some commit) ->
                let rec walk (remaining: DelegateFact list) (acc: Fact list) = async {
                    match remaining with
                    | [] -> return Ok acc
                    | d :: rest ->
                        match! store.Assert(scopeId, DelegateMint.recordDraft d run) with
                        | Error e -> return Error e
                        | Ok _ ->
                            let! stale = FactInvalidation.staleDelegatedHeads store scopeId d token

                            let rec supersede (facts: Fact list) (done': Fact list) = async {
                                match facts with
                                | [] -> return Ok done'
                                | fact :: more ->
                                    match! recomputed tables d scopeId commit fact with
                                    | Error e -> return Error e
                                    | Ok draft ->
                                        match! store.Assert(scopeId, draft) with
                                        | Error e -> return Error e
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
        match! FactTableBlobIo.tryGet<FactTableRunRecord> storage scopeId (tables.RecordName runId) with
        | Ok(Some run) -> return Ok(Some run)
        | Ok None -> return Ok None
        | Error e -> return Error e
    }

    let statusName (status: FactTableRunStatus) =
        match status with
        | FactTableRunStatus.Open -> "open"
        | FactTableRunStatus.Committed _ -> "committed"
        | FactTableRunStatus.Rejected _ -> "rejected"
        | FactTableRunStatus.Abandoned _ -> "abandoned"

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

    let discardStaged (scopeId: string) (runId: string) = async {
        let! names = storage.List(scopeId, tables.StagedPrefix runId)

        for name in names do
            let! _ = storage.Delete(scopeId, name)
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

    let reject (scopeId: string) (table: FactTableDefinition) (run: FactTableRunRecord) (error: FactTableWriteError) = async {
        let reason = FactTableWriteError.describe error

        let closed = {
            run with
                Status = FactTableRunStatus.Rejected reason
        }

        match! FactTableBlobIo.put storage scopeId (tables.RecordName run.RunId) closed with
        | Error e -> return Error e
        | Ok() ->
            do! discardStaged scopeId run.RunId
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

    let commit (scopeId: string) (run: FactTableRunRecord) (table: FactTableDefinition) = async {
        match! loadHead scopeId table.Id with
        | Error e -> return Error e
        | Ok head ->
            let current = List.tryLast head.Commits

            let sequence =
                current |> Option.map _.Run.Watermark.Sequence |> Option.defaultValue 0L

            if sequence <> run.BaseSequence then
                return! reject scopeId table run (FactTableCommitConflict(run.RunId, run.BaseSequence, sequence))
            else
                match! stagedRows scopeId run.RunId with
                | Error e -> return Error e
                | Ok rows ->
                    match FactTableValidation.defects table (hierarchyOf table) (List.indexed rows) with
                    | _ :: _ as defects -> return! reject scopeId table run (FactTableRowsRejected(run.RunId, defects))
                    | [] ->
                        let! previous = async {
                            match current with
                            | None -> return Ok None
                            | Some c ->
                                match! tables.Rows(scopeId, c.Run) with
                                | Ok prior ->
                                    return
                                        Ok(
                                            Some(
                                                FactTableSnapshot.ofRows
                                                    table
                                                    c.Run.Watermark.Sequence
                                                    (List.ofArray prior)
                                            )
                                        )
                                | Error e -> return Error(FactTableStorageFailure e)
                        }

                        match previous with
                        | Error e -> return Error e
                        | Ok previous ->
                            let next = sequence + 1L
                            let snapshot = FactTableSnapshot.ofRows table next rows

                            let watermark: FactTableWatermark = {
                                TableId = table.Id
                                Sequence = next
                                CommittedAt = now ()
                                ContentDigest = FactTableSnapshot.digest snapshot
                            }

                            // Canonical order, as the snapshot orders them.
                            let image = rows |> List.sortBy (fun r -> r.Subject, r.Period.From, r.Period.To)

                            let result: FactTableCommit = {
                                Watermark = watermark
                                RowCount = rows.Length
                                // The delegate writes no fact: the table IS the
                                // population, and only a quote mints one.
                                FactsWritten = 0
                                Change = FactTableSnapshot.changes table.Hierarchy previous snapshot
                                BatchDigest = BatchAssertReceipt.empty.Digest
                            }

                            let reach = reachOf table run.RunId watermark rows

                            let committed = {
                                run with
                                    Status = FactTableRunStatus.Committed result
                            }

                            match! FactTableBlobIo.put storage scopeId (tables.RowsName table.Id next) image with
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
                                        FactTableBlobIo.put storage scopeId (tables.RecordName run.RunId) committed
                                    with
                                    | Error e -> return Error e
                                    | Ok() ->
                                        do! discardStaged scopeId run.RunId

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
                                                    scopeId
                                                    table.Id
                                                    reach

                                            ()
                                        | None -> ()

                                        return Ok result
    }

    interface IFactTableWriter with

        member _.OpenRun(scopeId, tableId) = async {
            if not (isDelegated tableId) then
                match inner with
                | Some w -> return! w.OpenRun(scopeId, tableId)
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
                        let run: FactTableRunRecord = {
                            TableId = table.Id
                            RunId = Guid.NewGuid().ToString("N")
                            SchemaVersion = table.SchemaVersion
                            OpenedAt = now ()
                            BaseSequence =
                                head.Commits
                                |> List.tryLast
                                |> Option.map _.Run.Watermark.Sequence
                                |> Option.defaultValue 0L
                            StagedRows = 0
                            Status = FactTableRunStatus.Open
                        }

                        match! FactTableBlobIo.put storage scopeId (tables.RecordName run.RunId) run with
                        | Error e -> return Error e
                        | Ok() -> return Ok run
        }

        member _.WriteRows(scopeId, runId, rows) = async {
            match! loadRun scopeId runId with
            | Error e -> return Error e
            | Ok None ->
                match inner with
                | Some w -> return! w.WriteRows(scopeId, runId, rows)
                | None -> return Error(FactTableRunUnknown runId)
            | Ok(Some run) ->
                match run.Status with
                | FactTableRunStatus.Open ->
                    if List.isEmpty rows then
                        return Ok run
                    else
                        match! FactTableBlobIo.put storage scopeId (tables.StagedName runId run.StagedRows) rows with
                        | Error e -> return Error e
                        | Ok() ->
                            let staged = {
                                run with
                                    StagedRows = run.StagedRows + rows.Length
                            }

                            match! FactTableBlobIo.put storage scopeId (tables.RecordName runId) staged with
                            | Error e -> return Error e
                            | Ok() -> return Ok staged
                | other -> return Error(FactTableRunClosed(runId, statusName other))
        }

        member _.Commit(scopeId, runId) = async {
            match! loadRun scopeId runId with
            | Error e -> return Error e
            | Ok None ->
                match inner with
                | Some w -> return! w.Commit(scopeId, runId)
                | None -> return Error(FactTableRunUnknown runId)
            | Ok(Some run) ->
                match run.Status with
                | FactTableRunStatus.Open ->
                    match registrations.TryGetTable run.TableId with
                    | None -> return Error(FactTableUndeclared run.TableId)
                    | Some table ->
                        if not (isDelegated table.Id) then
                            return passOn table.Id
                        else
                            return! commit scopeId run table
                | other -> return Error(FactTableRunClosed(runId, statusName other))
        }

        member _.Abandon(scopeId, runId, reason) = async {
            match! loadRun scopeId runId with
            | Error e -> return Error e
            | Ok None ->
                match inner with
                | Some w -> return! w.Abandon(scopeId, runId, reason)
                | None -> return Error(FactTableRunUnknown runId)
            | Ok(Some run) ->
                match run.Status with
                | FactTableRunStatus.Open ->
                    let closed = {
                        run with
                            Status = FactTableRunStatus.Abandoned reason
                    }

                    match! FactTableBlobIo.put storage scopeId (tables.RecordName runId) closed with
                    | Error e -> return Error e
                    | Ok() ->
                        do! discardStaged scopeId runId

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
                | other -> return Error(FactTableRunClosed(runId, statusName other))
        }

        member _.Runs(scopeId, tableId) = async {
            if not (isDelegated tableId) then
                match inner with
                | Some w -> return! w.Runs(scopeId, tableId)
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
                        match! FactTableBlobIo.tryGet<FactTableRunRecord> storage scopeId name with
                        | Ok(Some run) when run.TableId = tableId -> return! load rest (run :: acc)
                        | Ok _ -> return! load rest acc
                        | Error e -> return Error e
                }

                match! load names [] with
                | Error e -> return Error e
                | Ok runs -> return Ok(runs |> List.sortByDescending (fun r -> r.OpenedAt, r.BaseSequence, r.RunId))
        }

        member this.Status(scopeId, tableId) = async {
            if not (isDelegated tableId) then
                match inner with
                | Some w -> return! w.Status(scopeId, tableId)
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
                        match! (this :> IFactTableWriter).Runs(scopeId, tableId) with
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