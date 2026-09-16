// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AIChatWorker

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Platform

// ─── Supervised agent-loop worker (Phase 6k) ─────────────────────
//
// The assistant API's SubmitMessage (the record `AIAssistantHandler.makeAssistantApi`
// builds) used to fire the agent loop with a bare `Async.Start`. Three things follow from that, and all three
// are silent:
//
//   1. **No shutdown story.** A process restart mid-conversation
//      abandons the computation with no terminal event, so the client
//      sits on an open stream until its 60 s watchdog calls it a hang.
//      The user is told nothing; the operator sees nothing.
//   2. **No admission control.** Every accepted request starts work
//      immediately, so a burst is absorbed by the thread pool and the
//      provider's rate limiter rather than refused at the door — the
//      failure surfaces late, as timeouts, far from its cause.
//   3. **No lifecycle boundary for the background DI scope.** Each
//      terminal branch of the loop disposed `bgScope` itself; a branch
//      added without one leaks a scope per turn, and nothing says so.
//
// This module is the boundary those three need. A bounded channel of
// work items, a fixed number of runner loops draining it, and an
// `IHostedService` lifecycle so the host's shutdown reaches the work.
//
// ## Why not a JobDefinition on IJobScheduler
//
// The phase offered that as option (b), for the good reason that it
// would reuse retry, dead-letter and lifecycle-event machinery already
// built. It is not available to this work item, and the obstacle is
// structural rather than effort: a `JobDefinition` handler is keyed by
// a string and receives a SERIALISED payload, by design — rule 4 of
// the portability rules requires handlers to be stateless between
// invocations precisely so a distributed scheduler may run them
// anywhere. An agent turn closes over a live event sink (the SSE
// broadcast or the typed stream's channel), a live DI scope, and a
// `CancellationTokenSource` registered in a per-process registry that
// the cancel endpoint must be able to find. None of that survives
// serialisation, and making it survive is a redesign of how a turn
// streams, not a registration change.
//
// So this is option (a): in-process, co-located with the HTTP tier by
// construction, and honest about it.
//
// ## Why it is NOT gated by ProcessProfileGate
//
// Every other background subsystem registers through
// `ProcessProfileGate.shouldRegisterBackgroundService`, which returns
// false for `WebOnly` on the principle that a sibling worker silo
// drains the persistent stores. That principle does not apply here and
// applying it would break chat outright: the work is bound to the
// request's own process — its DI scope, its SSE manager, its
// cancellation registry — so there is no sibling silo that could drain
// it. A `WebOnly` deployment serves chat requests and must therefore
// run this worker.
//
// The corollary is `TryEnqueue` returning `NotRunning` rather than
// throwing: in a composition where the worker was never started (a
// serverless host that gates hosted services off, a test standing up
// the handler without `compose`), the caller falls back to the
// pre-6k `Async.Start` path. That is not a silent degradation — the
// fallback is at the call site, logged, and the behaviour it falls
// back TO is exactly what shipped before this phase.

/// One agent turn, queued.
///
/// `Run` takes the worker's shutdown token so the loop can bail
/// cleanly; `Reject` is how the worker tells the client the turn will
/// not happen (it emits the terminal `AITaskFailed`); `Dispose`
/// releases the turn's background DI scope and runs on the worker
/// boundary whichever way the turn ended.
type AIChatWorkItem = {
    /// The turn's task id. Logged, and the only identity the worker
    /// has — it deliberately knows nothing else about the turn.
    TaskId: Guid
    /// A short fingerprint for the trace line. Must not carry prompt
    /// content or credentials.
    Fingerprint: string
    /// The agent loop. Handles its own exceptions; the worker's own
    /// guard exists only so one turn's escape cannot take the runner
    /// loop down with it.
    Run: CancellationToken -> Async<unit>
    /// Emit the terminal failure event for a turn that will not run.
    /// Called with the reason.
    Reject: string -> unit
    /// Release the turn's background DI scope. Called exactly once,
    /// whether the turn ran, was rejected, or threw.
    Dispose: unit -> unit
}

/// What the worker did with an offered item. The caller acts on all
/// three — none of them is a "probably fine" case.
type EnqueueOutcome =
    /// Queued. The worker owns the item's lifecycle from here.
    | Accepted
    /// The queue is full. The caller should surface a visible failure
    /// rather than queue behind an unbounded backlog.
    | QueueFull
    /// No worker is running in this composition (never started, or
    /// already stopped). The caller falls back to running the turn
    /// itself.
    | NotRunning

/// Message the worker emits as the terminal failure of a turn it
/// refuses at shutdown. Public so a test can assert on it without
/// restating the string.
[<Literal>]
let ShuttingDownReason = "Server is shutting down"

/// Message for a turn refused because the queue is full.
[<Literal>]
let QueueFullReason =
    "The server is busy and could not start this request. Please try again."

/// Supervised runner for queued agent turns.
///
/// `maxConcurrency` bounds how many turns run at once;
/// `queueCapacity` bounds how many may wait. Both are constructor
/// parameters rather than `ServerConfig` fields on purpose: this
/// phase adds no opt-in surface (GP 13), and the defaults are chosen
/// to be indistinguishable from the pre-6k `Async.Start` behaviour for
/// any deployment not already in trouble. A deployment that wants
/// different numbers constructs the worker itself.
type AIChatWorker(logger: ILogger, ?maxConcurrency: int, ?queueCapacity: int) =

    let maxConcurrency = defaultArg maxConcurrency 64
    let capacity = defaultArg queueCapacity 256

    let channel =
        Channel.CreateBounded<AIChatWorkItem>(
            BoundedChannelOptions(
                capacity,
                // `TryWrite` returns false when full rather than
                // dropping or blocking — the caller needs that false
                // to fail the turn visibly.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            )
        )

    /// Cancels the in-flight turns. Fired when the host's shutdown
    /// budget runs out, not when shutdown begins — a turn part-way
    /// through a provider call is given the budget to finish.
    let inFlightCts = new CancellationTokenSource()

    /// 0 = not started, 1 = running, 2 = stopping or stopped.
    let state = ref 0

    /// Set before the channel is completed at shutdown, so an item a
    /// runner dequeues during the drain is rejected rather than run.
    let rejecting = ref 0

    let runners = ResizeArray<Task>()

    let trace (message: string) = Logger.trace logger "ai.agent" message

    /// Run one item to completion and release its scope, whatever
    /// happened. This is the single disposal point the phase asked
    /// for: the turn's own terminal branches no longer each have to
    /// remember.
    let runItem (item: AIChatWorkItem) : Task = task {
        try
            try
                trace $"worker start (taskId={item.TaskId}, {item.Fingerprint})"
                do! item.Run inFlightCts.Token |> Async.StartAsTask
                trace $"worker done (taskId={item.TaskId})"
            with ex ->
                // The turn owns its own error reporting; anything
                // escaping to here is a defect in that reporting,
                // and the one thing that must not follow is a
                // runner loop dying and taking every later turn
                // with it.
                logger.Error($"AI chat worker: turn {item.TaskId} escaped its own error handling", Some ex)
        finally
            item.Dispose()
    }

    let rejectItem (item: AIChatWorkItem) (reason: string) =
        try
            try
                trace $"worker reject (taskId={item.TaskId}, reason={reason})"
                item.Reject reason
            with ex ->
                logger.Error($"AI chat worker: failed to report refusal for turn {item.TaskId}", Some ex)
        finally
            item.Dispose()

    /// Refuse everything currently queued, without waiting for a runner
    /// to become free.
    ///
    /// This is not an optimisation. Every runner may be occupied by an
    /// in-flight turn for the whole of the host's shutdown budget — a
    /// provider call can easily outlast it — and a queued turn that is
    /// only refused when a runner frees up is, in that case, never
    /// refused at all. The client then sits on an open stream until its
    /// watchdog gives up, which is exactly the silent hang the worker
    /// exists to remove. Refusing at the point of shutdown makes the
    /// terminal event independent of runner availability.
    ///
    /// Safe against a runner dequeuing concurrently: a channel hands
    /// each item to exactly one reader, and a runner that wins the race
    /// sees `rejecting` set and refuses it identically.
    let drainQueued () =
        let mutable queued = Unchecked.defaultof<AIChatWorkItem>

        while channel.Reader.TryRead(&queued) do
            rejectItem queued ShuttingDownReason

    let runnerLoop () : Task = task {
        let mutable go = true

        while go do
            // `CancellationToken.None`, deliberately: the loop must
            // keep draining after the writer is completed so the
            // queued turns get their terminal event. `ReadAsync`
            // throws only once the channel is completed AND empty,
            // which is exactly the exit condition wanted.
            let! next = Async.Catch(channel.Reader.ReadAsync().AsTask() |> Async.AwaitTask)

            match next with
            | Choice1Of2 item ->
                if Volatile.Read(&rejecting.contents) <> 0 then
                    rejectItem item ShuttingDownReason
                else
                    do! runItem item
            | Choice2Of2 _ -> go <- false
    }

    /// Offer a turn to the worker. Never blocks and never throws.
    member _.TryEnqueue(item: AIChatWorkItem) : EnqueueOutcome =
        if Volatile.Read(&state.contents) <> 1 then NotRunning
        elif channel.Writer.TryWrite item then Accepted
        else QueueFull

    /// True once `StartAsync` has run and before `StopAsync` begins.
    member _.IsRunning = Volatile.Read(&state.contents) = 1

    /// Turns queued but not yet picked up. Read by tests and by the
    /// dev diagnostics surface; not a control.
    member _.QueueDepth = channel.Reader.Count

    interface IHostedService with
        member _.StartAsync(_cancellationToken: CancellationToken) =
            if Interlocked.CompareExchange(&state.contents, 1, 0) = 0 then
                for _ in 1..maxConcurrency do
                    runners.Add(runnerLoop ())

                trace $"worker started (concurrency={maxConcurrency}, queueCapacity={capacity})"

            Task.CompletedTask

        member _.StopAsync(cancellationToken: CancellationToken) =
            task {
                Volatile.Write(&state.contents, 2)

                // Order matters. Mark rejecting BEFORE completing the
                // writer, so no runner can dequeue a queued item in the
                // window between the two and start a turn the host is
                // about to kill.
                Volatile.Write(&rejecting.contents, 1)
                channel.Writer.TryComplete() |> ignore

                // Refuse the backlog now, not when a runner frees up.
                drainQueued ()

                let drained = Task.WhenAll(runners.ToArray())

                // Give the in-flight turns the host's shutdown budget.
                // `Task.Delay(Infinite, ct)` completes when the budget
                // expires; `WhenAny` does not throw on it.
                let! _ =
                    Task.WhenAny(
                        drained,
                        Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(fun (_: Task) -> ())
                    )

                if not drained.IsCompleted then
                    // Budget exhausted. Cancel what is still running so
                    // the turns bail at their next cancellation check
                    // rather than being abandoned mid-provider-call.
                    trace "worker shutdown budget exhausted; cancelling in-flight turns"

                    try
                        inFlightCts.Cancel()
                    with _ ->
                        ()

                    // Best-effort settle; the host is going away either
                    // way and must not be held here.
                    let! _ = Task.WhenAny(drained, Task.Delay 2_000)
                    ()

                trace "worker stopped"
            }
            :> Task

    interface IDisposable with
        member _.Dispose() =
            try
                inFlightCts.Cancel()
            with _ ->
                ()

            inFlightCts.Dispose()

/// Run a work item WITHOUT a worker — the pre-6k `Async.Start` path,
/// kept as the fallback for a composition that never started one.
///
/// It is a named function rather than an inline `Async.Start` at the
/// call site so the scope-disposal guarantee is the same on both
/// paths: whichever route a turn takes, `Dispose` runs exactly once.
let runDetached (item: AIChatWorkItem) : unit =
    async {
        try
            do! item.Run CancellationToken.None
        finally
            item.Dispose()
    }
    |> Async.Start