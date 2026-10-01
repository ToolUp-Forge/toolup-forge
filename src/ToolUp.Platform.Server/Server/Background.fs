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