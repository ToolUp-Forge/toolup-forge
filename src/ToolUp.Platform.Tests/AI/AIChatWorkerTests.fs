module ToolUp.Platform.Tests.AI.AIChatWorkerTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.AI

// ─── The supervised agent-loop worker (Phase 6k) ─────────────────────
//
// `AIAssistantHandler.SubmitMessage` used to fire the agent loop with a
// bare `Async.Start`, which has no shutdown story, no admission control
// and no lifecycle boundary for the turn's background DI scope. These
// tests cover the three properties the worker adds, each of which is
// false of `Async.Start` by construction:
//
//   * a turn queued when the host shuts down gets a TERMINAL event
//     ("Server is shutting down") instead of being abandoned to the
//     client's 60 s watchdog;
//   * a turn offered to a full queue is REFUSED visibly instead of
//     piling onto an unbounded backlog;
//   * the turn's scope is released on the worker boundary exactly once,
//     whichever way the turn ended - so a terminal branch added later
//     cannot leak a scope per turn by forgetting to dispose one.
//
// The composition-level fact these rest on - that the worker is
// registered WITHOUT `ProcessProfileGate`, because an agent turn is
// bound to the process that accepted the request - is asserted in
// `AICompose` by construction and explained there; it has no runtime
// surface to test against here.

let private nullLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A work item recording what the worker did to it. `Run` completes
/// when `release` is set (or when the worker's token fires), so a test
/// can hold a turn in flight.
type private ProbeItem(release: ManualResetEventSlim) =
    let disposals = ref 0
    let rejections = ResizeArray<string>()
    let started = new ManualResetEventSlim(false)
    let taskId = Guid.NewGuid()

    member _.TaskId = taskId
    member _.Disposals = Volatile.Read(&disposals.contents)

    member _.Rejections = lock rejections (fun () -> rejections |> Seq.toList)

    member _.Started = started

    member this.Item: AIChatWorker.AIChatWorkItem = {
        TaskId = taskId
        Fingerprint = "probe"
        Run =
            fun ct -> async {
                started.Set()

                // Completes on release OR on the worker's cancellation,
                // whichever comes first - which is what lets one test
                // assert the shutdown token really reaches the turn. The
                // 30 s cap is a test-hang guard, never the mechanism
                // under test; every assertion below has a shorter budget.
                WaitHandle.WaitAny([| release.WaitHandle; ct.WaitHandle |], 30_000) |> ignore
                return ()
            }
        Reject = fun reason -> lock rejections (fun () -> rejections.Add reason)
        Dispose = fun () -> Interlocked.Increment(&disposals.contents) |> ignore
    }

/// A work item whose `Run` completes immediately.
let private quickItem () =
    let disposals = ref 0
    let ran = ref 0
    let rejections = ResizeArray<string>()

    let item: AIChatWorker.AIChatWorkItem = {
        TaskId = Guid.NewGuid()
        Fingerprint = "quick"
        Run =
            fun _ -> async {
                Interlocked.Increment(&ran.contents) |> ignore
                return ()
            }
        Reject = fun reason -> lock rejections (fun () -> rejections.Add reason)
        Dispose = fun () -> Interlocked.Increment(&disposals.contents) |> ignore
    }

    item, disposals, ran, rejections

let private waitUntil (timeoutMs: int) (predicate: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable ok = predicate ()

    while not ok && DateTime.UtcNow < deadline do
        Thread.Sleep 5
        ok <- predicate ()

    ok

let tests =
    testList "AIChatWorker (Phase 6k)" [

        testCase "an item offered before Start is not accepted"
        <| fun () ->
            use worker = new AIChatWorker.AIChatWorker(nullLogger)
            let item, _, ran, _ = quickItem ()

            Expect.equal (worker.TryEnqueue item) AIChatWorker.NotRunning "no worker is running yet"
            Expect.equal (Volatile.Read(&ran.contents)) 0 "and nothing ran"

        testCase "an enqueued turn runs and its scope is released exactly once"
        <| fun () ->
            use worker = new AIChatWorker.AIChatWorker(nullLogger)
            (worker :> IHostedService).StartAsync CancellationToken.None |> ignore

            let item, disposals, ran, _ = quickItem ()

            Expect.equal (worker.TryEnqueue item) AIChatWorker.Accepted "queued"

            Expect.isTrue
                (waitUntil 5_000 (fun () -> Volatile.Read(&disposals.contents) > 0))
                "the turn ran and its scope was released"

            Expect.equal (Volatile.Read(&ran.contents)) 1 "the turn body ran once"

            // Exactly once is the point. Before this phase each terminal
            // branch of the turn disposed its own scope, so the count
            // depended on which branch was taken - and a branch added
            // without one leaked a scope per turn, silently.
            Thread.Sleep 100
            Expect.equal (Volatile.Read(&disposals.contents)) 1 "released exactly once"

        testCase "a turn still queued at shutdown is failed with a terminal event"
        <| fun () ->
            // Concurrency 1 so the second item is provably still in the
            // queue when shutdown begins.
            use worker = new AIChatWorker.AIChatWorker(nullLogger, maxConcurrency = 1)
            (worker :> IHostedService).StartAsync CancellationToken.None |> ignore

            use release = new ManualResetEventSlim(false)
            let held = ProbeItem(release)
            let queued = ProbeItem(release)

            Expect.equal (worker.TryEnqueue held.Item) AIChatWorker.Accepted "first queued"
            Expect.isTrue (held.Started.Wait 5_000) "the first turn is in flight"

            Expect.equal (worker.TryEnqueue queued.Item) AIChatWorker.Accepted "second queued behind it"

            // Shutdown with an uncancelled token: the in-flight turn is
            // given the budget, the QUEUED one is refused straight away.
            let stopping = (worker :> IHostedService).StopAsync CancellationToken.None

            Expect.isTrue
                (waitUntil 5_000 (fun () -> not (List.isEmpty queued.Rejections)))
                "the queued turn was told why it will not run"

            Expect.equal
                queued.Rejections
                [ AIChatWorker.ShuttingDownReason ]
                "and the reason is the terminal message the client renders inline"

            Expect.equal queued.Disposals 1 "a refused turn releases its scope too"

            // Let the held turn finish so StopAsync can complete.
            release.Set()
            stopping.Wait 10_000 |> ignore

        testCase "an offered turn is refused when the queue is full"
        <| fun () ->
            use worker =
                new AIChatWorker.AIChatWorker(nullLogger, maxConcurrency = 1, queueCapacity = 1)

            (worker :> IHostedService).StartAsync CancellationToken.None |> ignore

            use release = new ManualResetEventSlim(false)
            let held = ProbeItem(release)

            Expect.equal (worker.TryEnqueue held.Item) AIChatWorker.Accepted "in flight"
            Expect.isTrue (held.Started.Wait 5_000) "the runner is occupied"

            // One slot; fill it, then the next offer must be refused.
            let filler = ProbeItem(release)
            Expect.equal (worker.TryEnqueue filler.Item) AIChatWorker.Accepted "the single queue slot"

            let overflow, _, ran, _ = quickItem ()

            Expect.equal
                (worker.TryEnqueue overflow)
                AIChatWorker.QueueFull
                "the queue is full and says so, instead of absorbing the turn"

            Expect.equal (Volatile.Read(&ran.contents)) 0 "the refused turn never started"

            release.Set()

            (worker :> IHostedService).StopAsync(CancellationToken.None).Wait 10_000
            |> ignore

        testCase "an in-flight turn is cancelled once the shutdown budget expires"
        <| fun () ->
            use worker = new AIChatWorker.AIChatWorker(nullLogger, maxConcurrency = 1)
            (worker :> IHostedService).StartAsync CancellationToken.None |> ignore

            // Never released: the ONLY way this turn can finish is the
            // worker's own cancellation reaching it.
            use neverReleased = new ManualResetEventSlim(false)
            let held = ProbeItem(neverReleased)

            Expect.equal (worker.TryEnqueue held.Item) AIChatWorker.Accepted "in flight"
            Expect.isTrue (held.Started.Wait 5_000) "the turn started"

            // An already-cancelled token models a shutdown budget that
            // has run out.
            use expired = new CancellationTokenSource()
            expired.Cancel()

            (worker :> IHostedService).StopAsync(expired.Token).Wait 15_000 |> ignore

            Expect.isTrue
                (waitUntil 5_000 (fun () -> held.Disposals > 0))
                "the abandoned turn was cancelled and its scope released"

        testCase "TryEnqueue reports NotRunning once the worker has stopped"
        <| fun () ->
            use worker = new AIChatWorker.AIChatWorker(nullLogger)
            (worker :> IHostedService).StartAsync CancellationToken.None |> ignore

            (worker :> IHostedService).StopAsync(CancellationToken.None).Wait 10_000
            |> ignore

            let item, _, ran, _ = quickItem ()

            // The caller's response to `NotRunning` is to run the turn
            // itself, which is why this must not silently look like
            // `Accepted`: a turn that is neither queued nor run is the
            // silent hang in its purest form.
            Expect.equal (worker.TryEnqueue item) AIChatWorker.NotRunning "stopped worker accepts nothing"
            Expect.equal (Volatile.Read(&ran.contents)) 0 "and ran nothing"

        testCase "runDetached still releases the turn's scope"
        <| fun () ->
            // The fallback path for a composition with no worker. It has
            // to carry the same disposal guarantee, or the fallback
            // reintroduces the leak the worker was added to remove.
            let item, disposals, ran, _ = quickItem ()

            AIChatWorker.runDetached item

            Expect.isTrue
                (waitUntil 5_000 (fun () -> Volatile.Read(&disposals.contents) > 0))
                "the detached turn released its scope"

            Expect.equal (Volatile.Read(&ran.contents)) 1 "and ran once"
    ]