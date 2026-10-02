// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.WebhookDispatcherTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Reflection
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── Phase 968 (a) — the dequeue loop backs off, and ends when closed ───
//
// The dispatcher's `ExecuteAsync` read its queue in a loop that caught
// every exception except cancellation, logged it, and read again at once.
// A read that fails on every call therefore spun a core and wrote one
// error line per call. Two arms pin that:
//
// - a reader that raises on every call while staying OPEN (the shape of a
//   transient store failure, should the queue ever be backed by one) is
//   called a handful of times in the window, not tens of thousands, and
//   the failure streak is logged once;
// - a channel that is COMPLETED (closed for good) ends the loop, through
//   the real service: a completed channel raises `ChannelClosedException`
//   on every read, so the old loop spun on it forever.
//
// The dispatcher's own channel is private and nothing in the tree
// completes it, so the second arm reaches it by reflection. If the field
// is renamed the arm fails loudly rather than passing vacuously.

/// A logger that counts every line and keeps the first thousand. Bounded,
/// because the loop this pack pins wrote one line per failed read: kept
/// whole, a hot loop's log is millions of entries in the test window.
type private CountingLogger() =
    let lines = ConcurrentQueue<string * string>()
    let mutable total = 0

    let add level (m: string) =
        if Interlocked.Increment(&total) <= 1000 then
            lines.Enqueue(level, m)

    member _.Lines = List.ofSeq lines
    member _.Total = Volatile.Read(&total)

    member _.Count(level: string, fragment: string) =
        lines
        |> Seq.filter (fun (l, m) -> l = level && m.Contains fragment)
        |> Seq.length

    member _.CountAny(fragment: string) =
        lines |> Seq.filter (fun (_, m) -> m.Contains fragment) |> Seq.length

    interface ILogger with
        member _.Debug m = add "debug" m
        member _.Info m = add "info" m
        member _.Warn m = add "warn" m
        member _.Error(m, _) = add "error" m

/// An open reader whose `ReadAsync` raises for the first `failFor` calls
/// (every call when `None`), then yields one item, then waits for the
/// stopping token. Its `Completion` never completes, so it is never
/// "closed for good". Counts every read.
type private FailingReader(failFor: int option) =
    inherit ChannelReader<int>()
    let mutable calls = 0
    member _.Calls = Volatile.Read(&calls)

    override _.TryRead(item: byref<int>) =
        item <- 0
        false

    override _.WaitToReadAsync(_ct: CancellationToken) = ValueTask<bool>(false)

    override _.ReadAsync(ct: CancellationToken) =
        let n = Interlocked.Increment(&calls)

        match failFor with
        | Some k when n = k + 1 -> ValueTask<int>(42)
        | Some k when n > k ->
            ValueTask<int>(
                task {
                    do! Task.Delay(Timeout.Infinite, ct)
                    return 0
                }
            )
        | _ -> ValueTask<int>(Task.FromException<int>(InvalidOperationException "the queue store is down"))

let private waitUntil (timeoutMs: int) (condition: unit -> bool) = async {
    let sw = Diagnostics.Stopwatch.StartNew()

    while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
        do! Async.Sleep 20

    return condition ()
}

/// Cancel `cts` when the arm exits, failed or not, so a loop an assertion
/// abandoned does not keep spinning under the rest of the pack.
let private stopOnExit (cts: CancellationTokenSource) =
    { new IDisposable with
        member _.Dispose() = cts.Cancel()
    }

/// Start the loop on the thread pool. A loop that never yields would
/// otherwise run on the test's own thread and hang it instead of failing.
let private startLoop (log: CountingLogger) (reader: ChannelReader<'T>) (handle: 'T -> unit) (ct: CancellationToken) =
    Task.Run(fun () -> WebhookDispatcher.runDequeueLoop (log :> ILogger) reader handle ct :> Task)

let private loopTests =
    testList "the dequeue loop" [
        testCaseAsync "a read that raises on every call is not retried in a hot loop, and is not logged per failure"
        <| async {
            let reader = FailingReader None
            let log = CountingLogger()
            use cts = new CancellationTokenSource()
            use _stop = stopOnExit cts

            let loop = startLoop log reader ignore cts.Token

            do! Async.Sleep 1600
            let calls = reader.Calls

            // 100 + 200 + 400 + 800 ms of backoff fit in the window: about
            // five reads. Without a backoff the loop reads again at once,
            // which is tens of thousands of reads in the same window.
            Expect.isGreaterThanOrEqual calls 3 "the loop keeps trying"
            Expect.isLessThanOrEqual log.Total 2 "one line for the streak, not one per read"
            Expect.isLessThanOrEqual calls 8 "and backs off between tries"

            Expect.equal
                (log.Count("error", "event=dequeue_loop_error"))
                1
                "the failure streak is logged once, when it starts"

            // A stop issued while the loop sleeps (now 1.6 s) is honoured
            // at once, not after the sleep.
            let sw = Diagnostics.Stopwatch.StartNew()
            cts.Cancel()
            let! finished = waitUntil 1000 (fun () -> loop.IsCompleted)
            Expect.isTrue finished "the stopping token cuts the backoff short"
            Expect.isLessThan sw.ElapsedMilliseconds 1000L "promptly"
        }

        testCaseAsync "the backoff resets when the read answers again, and the recovery is logged once"
        <| async {
            let reader = FailingReader(Some 3)
            let log = CountingLogger()
            let handled = ConcurrentQueue<int>()
            use cts = new CancellationTokenSource()
            use _stop = stopOnExit cts

            let loop = startLoop log reader handled.Enqueue cts.Token

            let! recovered = waitUntil 5000 (fun () -> log.Count("info", "event=dequeue_loop_recovered") > 0)

            cts.Cancel()
            let! finished = waitUntil 1000 (fun () -> loop.IsCompleted)

            Expect.isTrue recovered "the loop reached the read that succeeds"
            Expect.isTrue finished "the loop stops on the token"
            Expect.equal (List.ofSeq handled) [ 42 ] "the item read after the streak is handled"
            Expect.equal (log.Count("error", "event=dequeue_loop_error")) 1 "one line when the streak started"
            Expect.equal (log.Count("info", "event=dequeue_loop_recovered")) 1 "one line when it ended"

            Expect.stringContains
                (log.Lines
                 |> List.find (fun (_, m) -> m.Contains "dequeue_loop_recovered")
                 |> snd)
                "failures=3"
                "the recovery names the streak's length"
        }

        testCaseAsync "a channel completed with an error ends the loop, and says so once"
        <| async {
            let channel = Channel.CreateUnbounded<int>()
            channel.Writer.Complete(InvalidOperationException "the writer gave up")
            let log = CountingLogger()
            use cts = new CancellationTokenSource()
            use _stop = stopOnExit cts

            let loop = startLoop log channel.Reader ignore cts.Token

            let! finished = waitUntil 1000 (fun () -> loop.IsCompleted)
            Expect.isTrue finished "a closed channel ends the loop without a stop"
            Expect.equal (log.CountAny "event=dequeue_loop_error") 0 "no per-read error lines"

            Expect.equal
                (log.Count("error", "event=dequeue_loop_closed"))
                1
                "the faulted close is logged once, as an error"
        }
    ]

// ─── Through the real service ────────────────────────────────────────

let private emptySecretStore =
    { new ISecretStore with
        member _.GetSecret(_, _) = async.Return None
        member _.SetSecret(_, _, _) = async.Return(Ok())
        member _.DeleteSecret(_, _) = async.Return(Ok())
        member _.ListKeys _ = async.Return []
    }

let private permissiveRateLimiter =
    { new IRateLimiter with
        member _.Wait(_key) = async.Return Proceed
    }

let private buildService (logger: ILogger) =
    let storage = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

    WebhookDispatcher.create
        (WebhookRegistry.createRegistry storage)
        (WebhookRegistry.createDeliveryLog storage)
        (InMemoryEventStore.InMemoryEventStore() :> IEventStore)
        (new HttpClient())
        WebhookRetryPolicy.defaults
        logger
        (NoOpActivitySink() :> IActivitySink)
        emptySecretStore
        (fun () -> permissiveRateLimiter)
        { AllowedHosts = [] }

/// Complete the service's private queue, as a writer that is done would.
let private completeQueue (svc: WebhookDispatcher.WebhookDispatcherService) =
    let field =
        typeof<WebhookDispatcher.WebhookDispatcherService>
            .GetField("queue", BindingFlags.Instance ||| BindingFlags.NonPublic)

    if isNull field then
        failtest "the dispatcher's queue field was not found; this arm needs updating with the rename"

    let channel = field.GetValue svc
    let writer = channel.GetType().GetProperty("Writer").GetValue channel

    let completed =
        writer.GetType().GetMethod("TryComplete").Invoke(writer, [| (null: exn) |]) :?> bool

    Expect.isTrue completed "the queue was open and is now complete"

let private serviceTests =
    testList "the dispatcher service" [
        testCaseAsync "a queue closed for good ends ExecuteAsync instead of spinning on it"
        <| async {
            let log = CountingLogger()
            use svc = buildService log
            completeQueue svc
            use cts = new CancellationTokenSource()
            use _stop = stopOnExit cts
            let hosted = svc :> IHostedService
            // `BackgroundService.StartAsync` runs `ExecuteAsync` up to its
            // first real await. A loop spinning on a closed queue never
            // reaches one, so the start itself would not return: run it on
            // the thread pool and observe it.
            let start = Task.Run(fun () -> hosted.StartAsync cts.Token)
            let! started = waitUntil 1000 (fun () -> start.IsCompleted)

            let! ended =
                waitUntil 1000 (fun () -> started && not (isNull svc.ExecuteTask) && svc.ExecuteTask.IsCompleted)

            let errorLines = log.CountAny "event=dequeue_loop_error"
            let total = log.Total
            cts.Cancel()
            let! _ = waitUntil 2000 (fun () -> start.IsCompleted)
            do! hosted.StopAsync CancellationToken.None |> Async.AwaitTask

            Expect.isTrue started "StartAsync returns: the loop does not spin on a closed queue"
            Expect.equal errorLines 0 "a closed queue is not an error per read"
            Expect.isLessThanOrEqual total 1 "one line in all"
            Expect.isTrue ended "the loop ended on its own"
            Expect.equal (log.Count("info", "event=dequeue_loop_closed")) 1 "the close is logged once"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 968 — (a) the webhook dispatcher's dequeue loop" [ loopTests; serviceTests ]