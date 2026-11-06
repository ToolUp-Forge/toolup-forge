// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Generic
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.Platform.Compose
open ToolUp.Platform.Grounding
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics

// ─── The fact-table change signal ───────────────────────────────────
//
// A reader that keeps what it read — a cache of resolved page requests, a
// view that holds a slice — must learn when the facts under it changed. Two
// writes change them: a fact-table run committing (its table), and a data
// object landing that fact invalidation reacts to (any table of the scope,
// since the walk does not say which). This signal carries both, as a scope
// and the tables changed; an empty table list means any table of the scope.
//
// **Raised only when demanded.** A deployment with no reader of the signal
// composes it dormant: `Raise` returns at once, the writer decoration and the
// data-change arming cost nothing (GP 13). A reader that needs it registers
// the demand (`FactTableChangeSignal.demand`) beside its own registration.
//
// **Across replicas through the notification channel.** A raise notifies
// this process's listeners directly, and publishes one notification to the
// change's scope on the composed `INotificationChannel`; a listener's
// subscription to that scope hears the raises of every other process the
// channel reaches, and ignores its own. A channel that delivers nothing
// (the no-op channel, or none composed) leaves each process hearing only its
// own writes, which is exact for a single replica.

/// The signal a fact table's change is raised on.
type IFactTableChangeSignal =
    /// Raise a change: `tableIds` changed in `scope`; empty when any table of
    /// the scope may have changed. Returns once this process's listeners have
    /// been told and the notification is on the channel.
    abstract Raise: scope: ResolvedScope * tableIds: string list -> Async<unit>

    /// Listen for the changes of one scope. `listener` is told the tables
    /// changed (empty: any table of the scope), synchronously, and must not
    /// block. Dispose the result to stop.
    abstract Listen: scopeId: string * listener: (string list -> unit) -> Async<IDisposable>

/// The demand a reader of the signal registers: its presence arms the
/// signal's sources.
type FactTableChangeDemand() = class end

/// The notification a raise publishes to other processes.
type FactTableChangeNotice = {
    /// The raising process's signal, so it ignores its own notice.
    Origin: string
    /// The tables changed; empty for any table of the scope.
    TableIds: string list
}

/// Building and composing the signal.
[<RequireQualifiedAccess>]
module FactTableChangeSignal =

    /// The `CustomNotification` key a raise publishes under.
    [<Literal>]
    let NotificationKey = "Facts.TableChanged"

    let private noticeOptions =
        ToolUp.Remoting.Json.SystemTextJson.FableConverters.shared

    /// The signal over `channel`. `armed` is consulted on every raise; a raise
    /// while it is false does nothing.
    let create (channel: INotificationChannel option) (armed: unit -> bool) : IFactTableChangeSignal =
        let origin = Guid.NewGuid().ToString "N"
        let sync = obj ()
        let listeners = Dictionary<string, Dictionary<Guid, string list -> unit>>()
        let channelSubscriptions = Dictionary<string, NotificationSubscriptionId>()

        let tell (scopeId: string) (tableIds: string list) =
            let current =
                lock sync (fun () ->
                    match listeners.TryGetValue scopeId with
                    | true, byId -> List.ofSeq byId.Values
                    | false, _ -> [])

            for listener in current do
                try
                    listener tableIds
                with _ ->
                    ()

        let onEnvelope (envelope: NotificationEnvelope) =
            match envelope.Notification with
            | CustomNotification(key, payload) when key = NotificationKey ->
                let notice =
                    try
                        Some(JsonSerializer.Deserialize<FactTableChangeNotice>(payload, noticeOptions))
                    with _ ->
                        None

                match notice with
                | Some notice when not (isNull (box notice)) && notice.Origin <> origin ->
                    tell envelope.ScopeId (if isNull (box notice.TableIds) then [] else notice.TableIds)
                | _ -> ()
            | _ -> ()

        { new IFactTableChangeSignal with
            member _.Raise(scope: ResolvedScope, tableIds: string list) = async {
                if armed () then
                    let tableIds = tableIds |> List.distinct |> List.sort
                    tell scope.ScopeId tableIds

                    match channel with
                    | Some channel ->
                        let payload =
                            JsonSerializer.Serialize({ Origin = origin; TableIds = tableIds }, noticeOptions)

                        try
                            do! channel.Publish(scope.ScopeId, CustomNotification(NotificationKey, payload))
                        with _ ->
                            ()
                    | None -> ()
            }

            member _.Listen(scopeId: string, listener: string list -> unit) = async {
                let id = Guid.NewGuid()

                let first =
                    lock sync (fun () ->
                        match listeners.TryGetValue scopeId with
                        | true, byId ->
                            byId.[id] <- listener
                            false
                        | false, _ ->
                            let byId = Dictionary<Guid, string list -> unit>()
                            byId.[id] <- listener
                            listeners.[scopeId] <- byId
                            true)

                match channel with
                | Some channel when first ->
                    let! subscription = channel.Subscribe(scopeId, onEnvelope)

                    let duplicate =
                        lock sync (fun () ->
                            if channelSubscriptions.ContainsKey scopeId then
                                true
                            else
                                channelSubscriptions.[scopeId] <- subscription
                                false)

                    if duplicate then
                        do! channel.Unsubscribe subscription
                | _ -> ()

                return
                    { new IDisposable with
                        member _.Dispose() =
                            lock sync (fun () ->
                                match listeners.TryGetValue scopeId with
                                | true, byId -> byId.Remove id |> ignore
                                | false, _ -> ())
                    }
            }
        }

    /// Decorate a table writer so each successful `Commit` raises a change
    /// of its table. Every other member passes straight through, the orphan
    /// sweep included. A raise failure never fails the commit.
    let raisingWriter (signal: IFactTableChangeSignal) (inner: IFactTableWriter) : IFactTableWriter =
        { new IFactTableWriter with
            member _.OpenRun(scope, tableId, ?provenance, ?options) =
                inner.OpenRun(scope, tableId, ?provenance = provenance, ?options = options)

            member _.WriteRows(scope, runId, rows) = inner.WriteRows(scope, runId, rows)

            member _.Commit(scope: ResolvedScope, runId) = async {
                let! outcome = inner.Commit(scope, runId)

                match outcome with
                | Ok commit ->
                    try
                        do! signal.Raise(scope, [ commit.Watermark.TableId ])
                    with _ ->
                        ()
                | Error _ -> ()

                return outcome
            }

            member _.Abandon(scope, runId, reason) = inner.Abandon(scope, runId, reason)
            member _.Runs(scope, tableId) = inner.Runs(scope, tableId)
            member _.Status(scope, tableId) = inner.Status(scope, tableId)
          interface IFactTableOrphanSweep with
              member _.SweepOrphans(scope) = FactTableOrphanSweep.sweep inner scope
        }

    /// The data-change reaction that raises: a landed data object may change
    /// any table of its scope, so the change names none.
    let reaction (signal: unit -> IFactTableChangeSignal) : FactDataChangeReaction =
        fun scope _ -> signal().Raise(scope, [])

    /// Register the demand that arms the signal. A reader of the signal calls
    /// this beside its own registration; idempotent.
    let demand (services: IServiceCollection) : IServiceCollection =
        services.TryAddSingleton<FactTableChangeDemand>(FactTableChangeDemand())
        services

    /// Whether a reader of the signal is composed.
    let demanded (sp: IServiceProvider) : bool =
        (ServiceComposition.tryService<FactTableChangeDemand> sp).IsSome


// ─── The tolerance trigger ──────────────────────────────────────────
//
// A standing question over a fact table: did this metric move beyond a tolerance?
// It is asked when a unit of work that moved the table completes, not on the signal
// above: a listener hears table ids in a scope it already named, on every replica
// the channel reaches, and a run needs the resolved scope of the one write. The
// evaluation is pure (`read`), and the decoration shares its unit of work with the
// once-per-delivery trigger.

/// What a tolerance trigger compares the newest period against.
[<RequireQualifiedAccess>]
type ToleranceComparison =
    /// The latest earlier period the table holds any fact of the metric for.
    | PreviousDelivery
    /// The period `days` days before the newest. Positive, and at most
    /// `ToleranceTrigger.MaxDaysBefore`.
    | DaysBefore of days: int

/// How far a figure may move before it is news. Neither is negative. A move is
/// beyond it only when strictly greater, so zero admits any move.
[<RequireQualifiedAccess>]
type Tolerance =
    /// The move's size, in the metric's own unit.
    | Absolute of decimal
    /// The move's size over the comparison's magnitude: 0.10 is ten per cent. Any
    /// move from a comparison of zero is beyond it.
    | Relative of decimal

/// A standing question that enqueues a grounded narrative run.
type ToleranceTrigger = {
    /// The run the move enqueues.
    RunKey: string
    /// The declared fact table whose commits ask the question.
    Table: string
    /// A scalar column of that table.
    Metric: string
    /// The subjects asked about, each its path joined by `>`. `None` is all of
    /// them; there is no empty list.
    Subjects: string list option
    /// What the newest period is compared against.
    Comparison: ToleranceComparison
    /// How far a figure may move before the run is enqueued.
    Tolerance: Tolerance
}

/// One subject's figure beyond a trigger's tolerance.
type ToleranceMove = {
    /// The trigger whose tolerance it is beyond.
    Trigger: ToleranceTrigger
    /// The subject's path, joined by `>`.
    Subject: string
    /// The fact compared against.
    From: Fact
    /// The newest fact.
    To: Fact
    /// The figure `From` states.
    FromValue: decimal
    /// The figure `To` states.
    ToValue: decimal
}

/// What asking a trigger's question of the facts came to.
[<RequireQualifiedAccess>]
type ToleranceReading =
    /// The series has no period to compare the newest against, so nothing moved.
    | NothingToCompare of reason: string
    /// The subjects beyond the tolerance, none when nothing is.
    | Compared of ToleranceMove list

/// The firing rule of `FactsCompose.withToleranceTriggers`, and the decoration.
[<RequireQualifiedAccess>]
module ToleranceTrigger =

    /// The largest day offset a comparison may name: ten years, as a presented
    /// comparison's.
    [<Literal>]
    let MaxDaysBefore = 3660

    /// Whether a move from `from` to `current` is beyond `tolerance`.
    let beyond (tolerance: Tolerance) (from: decimal) (current: decimal) : bool =
        let size = abs (current - from)

        match tolerance with
        | Tolerance.Absolute limit -> size > limit
        | Tolerance.Relative limit -> if from = 0m then size <> 0m else size / abs from > limit

    let private subjectOf (fact: Fact) : string = String.concat ">" fact.Subject.Path

    /// Ask `trigger`'s question of `heads`, the current facts of its metric over
    /// the table's hierarchy. The newest period is the newest any subject states,
    /// and each selected subject is compared at that period. A subject with no
    /// fact at either period, or a recorded gap at either, did not move: a gap is
    /// never a zero.
    let read (trigger: ToleranceTrigger) (heads: Fact list) : ToleranceReading =
        let periods =
            heads |> List.map _.Period.From |> List.distinct |> List.sortDescending

        let comparedWith (newest: DateTime) =
            match trigger.Comparison with
            | ToleranceComparison.PreviousDelivery -> periods |> List.tryFind (fun period -> period < newest)
            | ToleranceComparison.DaysBefore days ->
                let period = newest.AddDays(-(float days))
                if List.contains period periods then Some period else None

        match periods with
        | [] -> ToleranceReading.NothingToCompare "the table holds no fact of the metric"
        | newest :: _ ->
            match comparedWith newest with
            | None ->
                ToleranceReading.NothingToCompare(
                    sprintf "the table holds nothing to compare the period of %s against" (newest.ToString "o")
                )
            | Some earlier ->
                let at (period: DateTime) =
                    heads
                    |> List.filter (fun fact -> fact.Period.From = period)
                    |> List.groupBy subjectOf
                    |> List.map (fun (subject, facts) -> subject, facts |> List.maxBy _.AsOf)
                    |> Map.ofList

                let compared = at earlier

                let selected (subject: string) =
                    trigger.Subjects |> Option.forall (List.contains subject)

                at newest
                |> Map.toList
                |> List.filter (fst >> selected)
                |> List.choose (fun (subject, current) ->
                    compared
                    |> Map.tryFind subject
                    |> Option.bind (fun from ->
                        match from.Value, current.Value with
                        | Scalar before, Scalar now when beyond trigger.Tolerance before now ->
                            Some {
                                Trigger = trigger
                                Subject = subject
                                From = from
                                To = current
                                FromValue = before
                                ToValue = now
                            }
                        | _ -> None))
                |> ToleranceReading.Compared

    /// The change-set entry naming a move: what moved, and the two facts that say
    /// so. It carries no figure, because a job's payload is not a disclosure surface.
    let moveEntry (move: ToleranceMove) : string =
        sprintf
            "%s%s:%s:%s:%s->%s"
            GroundedNarrativeTrigger.MovePrefix
            move.Trigger.Table
            move.Trigger.Metric
            move.Subject
            move.From.FactId
            move.To.FactId

    [<Literal>]
    let private FiguresPrefix = "moves:"

    /// What the moves say apart from which facts say it, digested: a re-delivery
    /// asserts the same figures as new facts, and there is nothing new to say.
    let private figures (moves: ToleranceMove list) : string =
        moves
        |> List.map (fun move ->
            sprintf
                "%s|%s|%s|%s|%s|%M|%M"
                move.Trigger.Table
                move.Trigger.Metric
                move.Subject
                (move.From.Period.From.ToString "o")
                (move.To.Period.From.ToString "o")
                move.FromValue
                move.ToValue)
        |> List.sort
        |> fun lines -> FiguresPrefix + CanonicalFrame.digest ("tolerance-moves" :: lines)

    /// The change a run is enqueued over: each move and the period it was observed
    /// at, and the digest of what they say.
    let change (moves: ToleranceMove list) : Set<string> =
        Set.unionMany [
            Set.ofList (moves |> List.map moveEntry)
            Set.ofList (moves |> List.map (fun move -> "period:" + move.To.Period.From.ToString "o"))
            Set.singleton (figures moves)
        ]

    /// Whether the newest job of `runKey` that names moves already says what `change`
    /// says. A job naming none is one of the run's other triggers', and says nothing here.
    let alreadySaid (runKey: string) (change: Set<string>) (jobs: JobDefinition list) : bool =
        let saying (entries: string seq) =
            entries |> Seq.tryFind _.StartsWith(FiguresPrefix, StringComparison.Ordinal)

        jobs
        |> List.filter (fun job ->
            job.Handler = GroundedNarrativeTrigger.HandlerName
            && job.Status <> JobStatus.Cancelled)
        |> List.choose (fun job ->
            GroundedNarrativeTrigger.tryParsePayload job.Payload
            |> Option.filter (fun payload -> payload.RunKey = runKey)
            |> Option.bind (fun payload -> saying payload.InvalidatedInputs)
            |> Option.map (fun said -> job.CreatedAt, said))
        |> function
            | [] -> false
            | said -> Some(said |> List.maxBy fst |> snd) = saying change

    /// What is wrong with `triggers` as written, each a sentence. Empty when each
    /// is well formed.
    let malformed (triggers: ToleranceTrigger list) : string list = [
        for trigger in triggers do
            let name = sprintf "the tolerance trigger of run '%s'" trigger.RunKey

            if String.IsNullOrWhiteSpace trigger.RunKey then
                "a tolerance trigger names no run"

            if String.IsNullOrWhiteSpace trigger.Table then
                sprintf "%s names no table" name

            if String.IsNullOrWhiteSpace trigger.Metric then
                sprintf "%s names no metric" name

            match trigger.Subjects with
            | Some [] -> sprintf "%s selects no subject; all of them is None" name
            | Some subjects when subjects |> List.exists String.IsNullOrWhiteSpace ->
                sprintf "%s selects a blank subject" name
            | Some subjects when List.distinct subjects <> subjects -> sprintf "%s selects a subject twice" name
            | _ -> ()

            match trigger.Comparison with
            | ToleranceComparison.DaysBefore days when days < 1 || days > MaxDaysBefore ->
                sprintf "%s compares %d days back; it must be from 1 to %d" name days MaxDaysBefore
            | _ -> ()

            match trigger.Tolerance with
            | Tolerance.Absolute limit
            | Tolerance.Relative limit when limit < 0m -> sprintf "%s has a negative tolerance" name
            | _ -> ()
    ]

    /// What `triggers` name that `tables` does not declare, each a sentence. A
    /// non-scalar column has no figure to compare, so it is refused with them.
    let undeclared (tables: IFactTableRegistry option) (triggers: ToleranceTrigger list) : string list = [
        for trigger in triggers do
            let name = sprintf "the tolerance trigger of run '%s'" trigger.RunKey

            match tables |> Option.bind (fun registry -> registry.TryGetTable trigger.Table) with
            | None -> sprintf "%s names table '%s', which no module declares" name trigger.Table
            | Some table ->
                match table.Columns |> List.tryFind (fun column -> column.Metric = trigger.Metric) with
                | None -> sprintf "%s names metric '%s', which table '%s' does not carry" name trigger.Metric table.Id
                | Some column when column.Shape <> FactTableValueShape.Scalar ->
                    sprintf
                        "%s names metric '%s', which table '%s' does not carry as a scalar"
                        name
                        trigger.Metric
                        table.Id
                | Some _ -> ()
    ]

    /// `inner`, asking `triggers` when a unit of work that moved one of their tables
    /// completes, under the scope the platform resolved for it. A run is enqueued at
    /// most once per unit, naming every subject its triggers found beyond tolerance.
    /// A move within tolerance enqueues nothing and says nothing. Every refusal on
    /// the path is logged at Warning with the run key and the table, none of them
    /// reaches the commit, and a fault reading the facts is one of them: it is never
    /// read as nothing having moved.
    let decorate
        (triggers: ToleranceTrigger list)
        (tables: unit -> IFactTableRegistry option)
        (store: unit -> IFactStore option)
        (scheduler: unit -> ToolUp.Platform.Jobs.IJobScheduler option)
        (accessor: unit -> Microsoft.AspNetCore.Http.IHttpContextAccessor option)
        (logger: ILogger)
        (inner: IFactTableWriter)
        : IFactTableWriter =

        let refused (runKey: string) (table: string) (reason: string) =
            logger.Warn(sprintf "[ToleranceTrigger] run '%s' on a commit of table '%s': %s" runKey table reason)

        let triggersOf (table: string) =
            triggers |> List.filter (fun trigger -> trigger.Table = table)

        /// The moves one trigger finds. A refusal is logged here and finds none.
        let moves (resolved: ResolvedScope) (trigger: ToleranceTrigger) = async {
            match store (), tables () |> Option.bind (fun registry -> registry.TryGetTable trigger.Table) with
            | None, _ ->
                refused trigger.RunKey trigger.Table "no IFactStore is composed, so the facts could not be read"
                return []
            | _, None ->
                refused trigger.RunKey trigger.Table "the table is not declared, so the facts could not be read"
                return []
            | Some facts, Some table ->
                match!
                    facts.Query(
                        resolved,
                        {
                            FactQuery.all with
                                Metric = Some(MetricRef trigger.Metric)
                        }
                    )
                with
                | Error fault ->
                    refused
                        trigger.RunKey
                        trigger.Table
                        (sprintf
                            "the facts could not be read (%s), so nothing was asked"
                            (FactStoreError.describe fault))

                    return []
                | Ok heads ->
                    match
                        heads
                        |> List.filter (fun fact -> fact.Subject.Hierarchy = table.Hierarchy)
                        |> read trigger
                    with
                    | ToleranceReading.Compared found -> return found
                    | ToleranceReading.NothingToCompare reason ->
                        logger.Info(
                            sprintf
                                "[ToleranceTrigger] run '%s' on table '%s', metric '%s': %s"
                                trigger.RunKey
                                trigger.Table
                                trigger.Metric
                                reason
                        )

                        return []
        }

        /// One run's triggers.
        let ask (resolved: ResolvedScope) (runKey: string) (asked: ToleranceTrigger list) = async {
            let! found = asked |> List.map (moves resolved) |> Async.Sequential

            match List.concat found with
            | [] -> ()
            | found ->
                let change = change found

                let enqueue =
                    CommitUnit.enqueue scheduler refused resolved runKey found.Head.Trigger.Table

                match scheduler () with
                | None -> do! enqueue change
                | Some jobs ->
                    let! listed = jobs.ListJobs resolved.ScopeId

                    if not (alreadySaid runKey change listed) then
                        do! enqueue change
        }

        let flush (pending: CommitUnit.Pending<FactTableCommit>) (resolved: ResolvedScope) = async {
            let mine, foreign = CommitUnit.split pending resolved

            for scopeId, commit in foreign do
                let table = commit.Watermark.TableId

                for trigger in triggersOf table do
                    refused trigger.RunKey table (CommitUnit.foreignReason resolved scopeId)

            let movedTables =
                mine
                |> List.filter DeliveryTrigger.moved
                |> List.map _.Watermark.TableId
                |> Set.ofList

            for runKey, asked in
                triggers
                |> List.filter (fun trigger -> movedTables.Contains trigger.Table)
                |> List.groupBy _.RunKey do
                match! Async.Catch(ask resolved runKey asked) with
                | Choice1Of2() -> ()
                | Choice2Of2 ex -> refused runKey asked.Head.Table (sprintf "asking threw (%s)" ex.Message)
        }

        let pendingOfUnit = CommitUnit.pendingOfUnit accessor logger flush

        { new IFactTableWriter with
            member _.OpenRun(scope, tableId, ?provenance, ?options) =
                inner.OpenRun(scope, tableId, ?provenance = provenance, ?options = options)

            member _.WriteRows(scope, runId, rows) = inner.WriteRows(scope, runId, rows)

            member _.Commit(scope, runId) = async {
                let! result = inner.Commit(scope, runId)

                match result with
                | Error _ -> ()
                | Ok commit ->
                    let table = commit.Watermark.TableId

                    match triggersOf table with
                    | [] -> ()
                    | watching ->
                        try
                            match pendingOfUnit () with
                            | Some pending -> pending.Add(scope.ScopeId, commit)
                            | None ->
                                for trigger in watching do
                                    refused
                                        trigger.RunKey
                                        table
                                        "the commit is off the request path and outside any delivery, so no resolved scope can carry the run"
                        with ex ->
                            for trigger in watching do
                                refused trigger.RunKey table (sprintf "the commit could not be held (%s)" ex.Message)

                return result
            }

            member _.Abandon(scope, runId, reason) = inner.Abandon(scope, runId, reason)
            member _.Runs(scope, tableId) = inner.Runs(scope, tableId)
            member _.Status(scope, tableId) = inner.Status(scope, tableId)
          interface IFactTableOrphanSweep with
              member _.SweepOrphans(scope) = FactTableOrphanSweep.sweep inner scope
        }