// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.LogStoreLogger

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

// ─── Phase 828 — LogStoreLogger (the write side of the log store) ───────
//
// A decorator over the resolved `ILogger` that forwards every line to the
// logger it wraps AND records it in the `ILogStore`. Composition, not
// replacement (GP 6): whichever logger the deployment resolved —
// `ConsoleLogger`, `JsonConsoleLogger`, an app-supplied one — keeps
// emitting exactly what it emitted, and the store is added beside it.
//
// **Why a decorator over `ILogger` and not an `ILoggerProvider`.** The
// phase text called for an `ILoggerProvider`; this SDK has no such type.
// `ILogger` (`Core/Shared/Interfaces/ILogger.fs`) plus the optional
// `ITraceLogger` capability IS the logging seam, and every SDK call site
// writes through it. Decorating that seam is the same mechanism the phase
// asked for, spelled in the vocabulary this codebase actually has;
// introducing a parallel `Microsoft.Extensions.Logging` provider would
// capture a different set of lines and leave the SDK's own writes
// unstored.
//
// **The write never blocks the caller.** `ILogger`'s methods return
// `unit`, and a store write is a disk write; doing it inline would put
// SQLite on the request path. Lines go to a BOUNDED channel drained by one
// background writer, which also means appends reach the store serialised
// and in emission order (what `ILogStore`'s tie-break rule promises).
//
// **Overflow drops, loudly and at the tail.** A bounded channel that is
// full drops the newest line rather than blocking the request that emitted
// it or growing without limit. The count of dropped lines is reported
// through the wrapped logger on the next successful drain, so a deployment
// whose log rate outruns its disk learns that from its own logs rather
// than from a silently-incomplete search.
//
// **What is recorded is what was EMITTED, not what the console printed.**
// The wrapped logger applies its own level floor and trace-category
// whitelist internally, and `ILogger` gives a decorator no way to observe
// which calls it suppressed. So every call reaches the store, level
// included as a column: search-time filtering keeps more than the console
// shows, retention bounds the cost, and the alternative — re-implementing
// the floor here — would guess at a policy the wrapped logger owns.

/// How many un-drained lines the decorator holds before it starts dropping
/// the newest. Roughly a second of a very chatty deployment.
[<Literal>]
let DefaultCapacity = 10000

/// How long `Dispose` waits for the queue to drain before giving up, so a
/// shutdown is never held open by a slow disk.
let private drainTimeout = TimeSpan.FromSeconds 5.0

/// Decorator over `ILogger` (and `ITraceLogger`) that mirrors every line
/// into an `ILogStore`. Construct through `LogStoreLogger.create`;
/// `Dispose` drains what is queued, within `drainTimeout`.
type LogStoreLogger(inner: ILogger, store: ILogStore, ?capacity: int, ?loggerName: string) =
    let name = defaultArg loggerName "toolup"

    let channel =
        Channel.CreateBounded<LogRecord>(
            BoundedChannelOptions(
                defaultArg capacity DefaultCapacity,
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            )
        )

    let dropped = ref 0

    let cts = new CancellationTokenSource()

    let entryOf (level: LogLevel) (message: string) (ex: exn option) =
        let context = LoggerScope.current ()

        {
            TimestampUtc = LogRecord.truncateToSecond DateTime.UtcNow
            Level = level
            Logger = name
            Message = message
            ScopeId = context |> Map.tryFind LoggerScope.ScopeId
            CorrelationId = context |> Map.tryFind LoggerScope.RequestId
            Error = ex |> Option.map string
        }

    let enqueue (level: LogLevel) (message: string) (ex: exn option) =
        if not (channel.Writer.TryWrite(entryOf level message ex)) then
            Interlocked.Increment(&dropped.contents) |> ignore

    /// Report-and-reset the drop counter through the wrapped logger. Read
    /// once per drained line; the exchange makes the report at-most-once
    /// per batch of drops rather than once per line.
    let reportDrops () =
        let n = Interlocked.Exchange(&dropped.contents, 0)

        if n > 0 then
            inner.Warn
                $"[LogStore] event=dropped count={n} reason=queue_full — the log store's write queue overflowed; those lines were not recorded."

    let drain: Task = task {
        try
            let reader = channel.Reader
            let mutable running = true

            while running do
                let! available = reader.WaitToReadAsync(cts.Token).AsTask()

                if not available then
                    running <- false
                else
                    let mutable entry = Unchecked.defaultof<LogRecord>

                    while reader.TryRead(&entry) do
                        let pending = entry

                        try
                            do! store.Append pending |> Async.StartAsTask
                        with ex ->
                            // A failing store must never take the
                            // process with it — the wrapped logger is
                            // still the system of record for the line.
                            inner.Error("[LogStore] event=append_failed", Some ex)

                    reportDrops ()
        with
        | :? OperationCanceledException -> ()
        | ex -> inner.Error("[LogStore] event=drain_loop_error", Some ex)
    }

    interface ILogger with
        member _.Debug message =
            inner.Debug message
            enqueue LogLevel.Debug message None

        member _.Info message =
            inner.Info message
            enqueue LogLevel.Info message None

        member _.Warn message =
            inner.Warn message
            enqueue LogLevel.Warn message None

        member _.Error(message, ex) =
            inner.Error(message, ex)
            enqueue LogLevel.Error message ex

    interface ITraceLogger with
        member _.Trace(category, message) =
            // Forward only when the wrapped logger offers the capability —
            // `Logger.trace` would otherwise have no-opped entirely.
            match box inner with
            | :? ITraceLogger as traceable -> traceable.Trace(category, message)
            | _ -> ()

            // The category rides the scope map the same way
            // `JsonConsoleLogger` puts it in `context`, so a stored trace
            // line is searchable by the text of its category.
            use _ = LoggerScope.push [ "trace_category", category ]
            enqueue LogLevel.Trace message None

    interface IDisposable with
        member _.Dispose() =
            channel.Writer.TryComplete() |> ignore

            // Give the drain a bounded window to finish what is queued,
            // then stop it regardless.
            drain.Wait(drainTimeout) |> ignore
            cts.Cancel()
            cts.Dispose()

/// Wrap `inner` so every line it receives is also recorded in `store`.
let create (inner: ILogger) (store: ILogStore) : ILogger =
    new LogStoreLogger(inner, store) :> ILogger