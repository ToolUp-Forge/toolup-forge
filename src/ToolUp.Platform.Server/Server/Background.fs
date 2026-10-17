// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 869 — fire-and-forget work that cannot take the process down and
/// that a service can account for at shutdown.
///
/// `Async.Start` leaves nothing to observe a throw (an escaping exception is
/// an unhandled thread-pool exception: the host dies, not the one piece of
/// work), and nothing to wait for or cancel when the host stops. `start`
/// closes both gaps at once: it catches and logs under the caller's label,
/// runs the work under its `DrainSet`'s token — so cancellation reaches the
/// work through its async context, with no signature change anywhere below
/// it — and tracks it until it ends, so `StopAsync` can wait for it within
/// the host's shutdown window.
module ToolUp.Platform.Background

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks

/// The work one service has started through `start` and not yet seen end.
/// Each service owns its own set: its `StopAsync` decides, in its own order,
/// when to `Cancel` the set and how long to `WaitAsync` it.
type DrainSet() =
    let running = ConcurrentDictionary<int64, Task>()
    let mutable nextId = 0L
    let cts = new CancellationTokenSource()

    /// The token every piece of work started through this set runs under.
    member _.Token = cts.Token

    /// How many started pieces of work have not yet ended.
    member _.Count =
        running.Values |> Seq.filter (fun t -> not t.IsCompleted) |> Seq.length

    /// Signal every running piece of work to stop. Cooperative: it lands at
    /// the work's next async bind. Work started after this call does not run.
    member _.Cancel() = cts.Cancel()

    /// Track `task` until it ends.
    member internal _.Track(task: Task) =
        let id = Interlocked.Increment(&nextId)
        running[id] <- task

        task.ContinueWith(
            (fun (_: Task) -> running.TryRemove id |> ignore),
            TaskContinuationOptions.ExecuteSynchronously
        )
        |> ignore

    /// Wait until every piece of work running now — and any started while
    /// waiting — has ended, or until `cancellationToken` fires (the end of the
    /// shutdown window). `true` when the set drained, `false` when the window
    /// closed first.
    member _.WaitAsync(cancellationToken: CancellationToken) : Task<bool> = task {
        let window = Task.Delay(Timeout.Infinite, cancellationToken)
        let mutable result = None

        while result.IsNone do
            let pending =
                running.Values |> Seq.filter (fun t -> not t.IsCompleted) |> Array.ofSeq

            if pending.Length = 0 then
                result <- Some true
            elif cancellationToken.IsCancellationRequested then
                result <- Some false
            else
                let! _ = Task.WhenAny(Task.WhenAll pending, window)
                ()

        return result.Value
    }

/// Start `work` without awaiting it, under `drain`'s token and tracked by it.
/// A throw is caught and logged at `Error` with `label` as the message, so one
/// failed piece of work never becomes an unhandled exception that takes the
/// process down. Work offered after `drain` was cancelled is not started, and
/// that is logged too: a stopping service accepts nothing new.
let start (drain: DrainSet) (logger: ILogger) (label: string) (work: Async<unit>) : unit =
    if drain.Token.IsCancellationRequested then
        logger.Warn(sprintf "%s — not started: the service is stopping" label)
    else
        let guarded = async {
            try
                do! work
            with ex ->
                logger.Error(label, Some ex)
        }

        drain.Track(Async.StartAsTask(guarded, cancellationToken = drain.Token))

/// Phase 1006 / 1024 / 1025 — the warning a failed fire-and-forget write is
/// logged as, without a request in hand. It names the source, the write,
/// where it failed (`onWhat`, e.g. `" on GET /x"`, or `""`), the exception
/// type seen through an async boundary's wrapper, and what the failure did
/// NOT change (`unaffected`, e.g. `"the response"`). A logger that itself
/// throws is ignored. `SurfaceEnforcement.writeFailureWarner` is the
/// request-bound face of this one formatter.
let internal writeFailureLogger
    (source: string)
    (logger: ILogger option)
    (onWhat: string)
    (unaffected: string)
    : string -> exn -> unit =
    fun what ex ->
        // The cause, not the `AggregateException` an `Async.AwaitTask`
        // boundary wraps it in (Phase 972).
        let cause = ToolUp.Platform.ProviderExceptions.unwrap ex

        try
            logger
            |> Option.iter (fun log ->
                log.Warn(
                    sprintf
                        "%s: the %s write failed%s (%s: %s); %s is unaffected."
                        source
                        what
                        onWhat
                        (cause.GetType().Name)
                        cause.Message
                        unaffected
                ))
        with _ ->
            ()

/// Phase 1006 / 1024 — start a write without awaiting it, observing its
/// failure. The ONE helper for this shape: the surface denial trail, every
/// fire-and-forget write in `ScopeResolutionMiddleware` (Middleware.fs), and
/// the conversation and result stores' event and lineage writes (Phase 1025)
/// go through it. Service-lifetime work with a stop to drain for uses
/// `start` above instead.
///
/// A bare `Async.Start` of a write that fails is not merely untidy. A
/// failure inside plain async code is rethrown on a thread-pool thread and
/// terminates the host process; a failure behind a task boundary
/// (`Async.AwaitTask`) surfaces as an unobserved task exception instead.
/// Under `Async.Catch` neither escapes: the failure is handed to `warn`
/// with the write's name, the caller never waits on the write and never
/// changes because of it. A write that throws synchronously, before an
/// `Async` exists, is the caller's `try ... with ex -> warn what ex`.
let internal startAudit (warn: string -> exn -> unit) (what: string) (work: Async<unit>) : unit =
    async {
        match! Async.Catch work with
        | Choice1Of2() -> ()
        | Choice2Of2 ex -> warn what ex
    }
    |> Async.Start