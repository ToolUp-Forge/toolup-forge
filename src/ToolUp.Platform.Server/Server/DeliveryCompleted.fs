// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1042 — a module registers work to run once a delivery has completed.
///
/// A DELIVERY is the ingestion one unit of work performs: every
/// `IDataIngestor.RunIngestion` call one request makes (an upload, a
/// derivation, a refresh served inline), or every call one job attempt makes
/// (a declared scheduled pull, a watched location's poll, a refresh the
/// admin API queued). It completes when that unit does — after the response
/// is written, or after the job attempt returns — so every table of it has
/// settled, committed or failed.
///
/// A module registers a `DeliveryCompletedHook` (`ServerModule.withDeliveryCompleted`
/// or `ServerApp.withDeliveryCompleted`), and the platform calls it ONCE per
/// completed delivery that recorded at least one run, with the delivery's
/// runs, under the scope the platform resolved for the delivery: the
/// request's `ScopeResolution.forRequest`, or the job's carried
/// `JobContext.Scope`. Nothing here mints a scope (Phase 797); a delivery
/// whose unit resolved none — an anonymous request, a job scheduled through
/// the string overload — calls no hook, and says so in the log.
///
/// A hook that throws, synchronously or asynchronously, is caught: the
/// failure is logged at `Warn` and audited as a `DeliveryCallbackFailed`
/// event, and it never reaches the delivery, its response or its job result
/// (Phase 1024's rule). Every other hook still runs.
///
/// A deployment that registers no hook composes byte-for-byte as before: the
/// ingestor is not decorated and no middleware is mounted (GP 11 / GP 13).
module ToolUp.Platform.DeliveryCompleted

open System
open System.Text.Json
open System.Threading
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── The delivery a hook receives ──────────────────────────────────────

/// What delivered the data.
[<RequireQualifiedAccess>]
type DeliveryOrigin =
    /// A request: its method and path (never the query string).
    | Request of method: string * path: string
    /// A job attempt — a declared scheduled pull, a watched location's poll,
    /// a queued refresh: the handler it ran under and its job id.
    | Job of handler: string * jobId: JobId

/// How one table of a delivery settled.
[<RequireQualifiedAccess>]
type DeliveredOutcome =
    /// The table's payload was committed.
    | Committed
    /// The table did not commit, and why — reported so a hook can decide
    /// whether a partial delivery is worth acting on.
    | Failed of IngestionError

/// One table of a delivery.
type DeliveredRun = {
    DataSourceId: DataSourceId
    Table: string
    /// The ingestion run, as `IDataIngestor.GetRecentRuns` reports it. `None`
    /// only for a refusal before any run was recorded (an unconfigured
    /// source, an unregistered connector).
    RunId: Guid option
    /// Where the committed payload landed (`IDataObjectStore` object id).
    /// `None` for a table that did not commit.
    ResultObjectId: string option
    /// When the table settled.
    SettledAt: DateTime
    Outcome: DeliveredOutcome
}

/// A completed delivery: its runs, in the order they settled, under the
/// scope the platform resolved for it.
[<NoComparison>]
type Delivery = {
    DeliveryId: Guid
    /// The scope the platform resolved for the delivery's request or carried
    /// on its job. Never anonymous: a delivery that resolved none calls no
    /// hook.
    Scope: ResolvedScope
    Origin: DeliveryOrigin
    Runs: DeliveredRun list
    CompletedAt: DateTime
}

/// Readers over a `Delivery`.
[<RequireQualifiedAccess>]
module Delivery =

    /// The tables that committed.
    let committed (delivery: Delivery) : DeliveredRun list =
        delivery.Runs |> List.filter (fun r -> r.Outcome = DeliveredOutcome.Committed)

    /// The tables that did not commit.
    let failed (delivery: Delivery) : DeliveredRun list =
        delivery.Runs |> List.filter (fun r -> r.Outcome <> DeliveredOutcome.Committed)

    /// `true` when every table committed.
    let isComplete (delivery: Delivery) : bool = List.isEmpty (failed delivery)

/// The work a hook runs: handed the application's services and the
/// completed delivery. Its failure is caught, logged and audited.
type DeliveryCompletedCallback = IServiceProvider -> Delivery -> Async<unit>

/// A registered callback and the name its failures are logged and audited
/// under.
[<NoEquality; NoComparison>]
type DeliveryCompletedHook = {
    Name: string
    Callback: DeliveryCompletedCallback
}

/// The source module a hook's failure is audited under, and its event type.
[<Literal>]
let DeliverySourceModule = "_platform.delivery"

[<Literal>]
let CallbackFailedEventType = "DeliveryCallbackFailed"

// ─── The collector a unit of work carries ──────────────────────────────

/// What a completed delivery is handed to: the hooks and what they run
/// with. Supplied by the decorated ingestor that recorded the runs.
[<NoEquality; NoComparison>]
type internal DeliverySink = {
    Hooks: DeliveryCompletedHook list
    Services: IServiceProvider
    Logger: ILogger
    Events: IEventStore option
}

/// The runs one unit of work records, in settling order. Closed exactly
/// once; a run settling after the close belongs to no delivery.
type internal DeliveryCollector(origin: DeliveryOrigin) =
    let gate = obj ()
    let runs = ResizeArray<string * DeliveredRun>()
    let mutable sink: DeliverySink option = None
    let mutable closed = false

    member _.Origin = origin

    member _.Record(scopeId: string, run: DeliveredRun, by: DeliverySink) =
        lock gate (fun () ->
            if not closed then
                runs.Add((scopeId, run))

                if sink.IsNone then
                    sink <- Some by)

    /// Close the collector: its runs and sink the first time, when it
    /// recorded any; `None` otherwise.
    member _.Close() : ((string * DeliveredRun) list * DeliverySink) option =
        lock gate (fun () ->
            if closed then
                None
            else
                closed <- true

                match sink with
                | Some s when runs.Count > 0 -> Some(List.ofSeq runs, s)
                | _ -> None)

/// The collector of the unit of work in progress — ambient, like
/// `JobProgressScope` (GP 7), so the ingestor records into it with no
/// signature change.
module internal DeliveryScope =

    let private ambient = AsyncLocal<DeliveryCollector>()

    let current () : DeliveryCollector option =
        match box ambient.Value with
        | null -> None
        | _ -> Some ambient.Value

    /// Make `collector` ambient until disposed, restoring the prior value.
    let push (collector: DeliveryCollector) : IDisposable =
        let prior = ambient.Value
        ambient.Value <- collector

        { new IDisposable with
            member _.Dispose() = ambient.Value <- prior
        }

// ─── Firing ────────────────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private originText (origin: DeliveryOrigin) =
    match origin with
    | DeliveryOrigin.Request(m, p) -> sprintf "%s %s" m p
    | DeliveryOrigin.Job(handler, jobId) -> sprintf "job %O (%s)" jobId handler

let private warn (logger: ILogger) (message: string) =
    try
        logger.Warn message
    with _ ->
        ()

/// Log and audit one hook's failure. Never throws.
let private callbackFailed (sink: DeliverySink) (delivery: Delivery) (hook: DeliveryCompletedHook) (ex: exn) = async {
    let cause = ProviderExceptions.unwrap ex

    warn
        sink.Logger
        (sprintf
            "[DeliveryCompleted] the '%s' callback failed for delivery %O (%s) in scope '%s' (%s: %s); the delivery is unaffected."
            hook.Name
            delivery.DeliveryId
            (originText delivery.Origin)
            delivery.Scope.ScopeId
            (cause.GetType().Name)
            cause.Message)

    match sink.Events with
    | None -> ()
    | Some events ->
        let payload =
            JsonSerializer.Serialize(
                {|
                    DeliveryId = delivery.DeliveryId
                    Callback = hook.Name
                    Origin = originText delivery.Origin
                    Tables = delivery.Runs |> List.map _.Table
                    ExceptionType = cause.GetType().Name
                    Message = cause.Message
                |},
                jsonOptions
            )

        match!
            Async.Catch(
                events.Write(Events.create delivery.Scope.ScopeId DeliverySourceModule CallbackFailedEventType payload)
            )
        with
        | Choice1Of2() -> ()
        | Choice2Of2 auditEx ->
            let auditCause = ProviderExceptions.unwrap auditEx

            warn
                sink.Logger
                (sprintf
                    "[DeliveryCompleted] the audit of the '%s' callback's failure could not be written (%s)."
                    hook.Name
                    (auditCause.GetType().Name))
}

/// Complete `collector`'s delivery under `scope`: call every hook once, in
/// registration order, when the unit of work recorded a run. Never throws.
let internal complete (collector: DeliveryCollector) (scope: ResolvedScope) : Async<unit> = async {
    match collector.Close() with
    | None -> ()
    | Some(recorded, sink) ->
        if scope.IsAnonymous then
            warn
                sink.Logger
                (sprintf
                    "[DeliveryCompleted] a delivery of %d table(s) from %s resolved no scope; no callback was run."
                    recorded.Length
                    (originText collector.Origin))
        else
            // A run recorded for another scope is not this delivery's: a
            // hook runs under the delivery's scope and sees only its runs.
            let mine, foreign =
                recorded |> List.partition (fun (scopeId, _) -> scopeId = scope.ScopeId)

            if not (List.isEmpty foreign) then
                warn
                    sink.Logger
                    (sprintf
                        "[DeliveryCompleted] %d run(s) from %s were recorded for a scope other than the delivery's '%s' and are not reported."
                        foreign.Length
                        (originText collector.Origin)
                        scope.ScopeId)

            if not (List.isEmpty mine) then
                let delivery = {
                    DeliveryId = Guid.NewGuid()
                    Scope = scope
                    Origin = collector.Origin
                    Runs = mine |> List.map snd
                    CompletedAt = DateTime.UtcNow
                }

                for hook in sink.Hooks do
                    match! Async.Catch(async { return! hook.Callback sink.Services delivery }) with
                    | Choice1Of2() -> ()
                    | Choice2Of2 ex -> do! callbackFailed sink delivery hook ex
}

// ─── The two seams that open and record a delivery ─────────────────────

/// A job's attempt as a delivery: the runs `inner` records are delivered,
/// under the job's carried `JobContext.Scope`, once the attempt returns. The
/// job result is `inner`'s, untouched; an attempt that throws delivers
/// nothing (the scheduler retries it as before). A deployment registering no
/// hook records nothing, so the wrapper only sets and clears the ambient
/// collector.
let aroundJob (handlerName: string) (inner: IJobHandler) : IJobHandler =
    { new IJobHandler with
        member _.Execute ctx = async {
            let collector = DeliveryCollector(DeliveryOrigin.Job(handlerName, ctx.JobId))

            let! result = async {
                use _ = DeliveryScope.push collector
                return! inner.Execute ctx
            }

            do! complete collector ctx.Scope
            return result
        }
    }

let private deliveredRun (sourceId: DataSourceId) (table: string) (result: Result<IngestionRun, IngestionError>) =
    match result with
    | Ok run -> {
        DataSourceId = run.DataSourceId
        Table = run.Table
        RunId = Some run.RunId
        ResultObjectId = run.ResultObjectId
        SettledAt = run.CompletedAt |> Option.defaultValue DateTime.UtcNow
        Outcome =
            match run.Status, run.Error with
            | IngestionStatus.Succeeded, _ -> DeliveredOutcome.Committed
            | _, Some err -> DeliveredOutcome.Failed err
            | status, None -> DeliveredOutcome.Failed(UnexpectedFailure(sprintf "the run settled as %A" status))
      }
    | Error err -> {
        DataSourceId = sourceId
        Table = table
        RunId = None
        ResultObjectId = None
        SettledAt = DateTime.UtcNow
        Outcome = DeliveredOutcome.Failed err
      }

/// `inner`, recording every settled `RunIngestion` into the delivery in
/// progress. `inner` itself when no hook is registered.
let internal recording
    (hooks: DeliveryCompletedHook list)
    (services: IServiceProvider)
    (logger: ILogger)
    (events: IEventStore option)
    (inner: IDataIngestor)
    : IDataIngestor =
    match hooks with
    | [] -> inner
    | _ ->
        let sink = {
            Hooks = hooks
            Services = services
            Logger = logger
            Events = events
        }

        { new IDataIngestor with
            member _.RunIngestion(scopeId, sourceId, table) = async {
                let! result = inner.RunIngestion(scopeId, sourceId, table)

                match DeliveryScope.current () with
                | Some collector -> collector.Record(scopeId, deliveredRun sourceId table result, sink)
                | None -> ()

                return result
            }

            member _.GetRecentRuns(scopeId, sourceId, count) =
                inner.GetRecentRuns(scopeId, sourceId, count)
        }

/// The hooks registered with `services` (`ServerApp.withDeliveryCompleted`).
let internal hooksOf (services: IServiceProvider) : DeliveryCompletedHook list =
    match services.GetService(typeof<Collections.Generic.IEnumerable<DeliveryCompletedHook>>) with
    | :? Collections.Generic.IEnumerable<DeliveryCompletedHook> as hooks -> List.ofSeq hooks
    | _ -> []