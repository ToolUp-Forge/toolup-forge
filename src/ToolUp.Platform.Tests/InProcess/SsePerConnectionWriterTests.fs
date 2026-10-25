module ToolUp.Platform.Tests.InProcess.SsePerConnectionWriterTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open ToolUp.Platform

// ─── SSEConnectionManager - per-connection writers (Phase 6k) ────────
//
// Before this phase `Broadcast` fanned out with `Task.WhenAll` over
// every connection in a scope and awaited them all under a 5 s
// per-write cap. One wedged subscriber therefore cost EVERY other
// subscriber in the scope up to 5 s per event - a healthy tab running
// 5 s behind per frame, which the client's 60 s watchdog eventually
// reported as a hang on the connection that was working fine.
//
// Each connection now owns a bounded queue and one reader loop.
// `Broadcast` is a per-connection `TryWrite`.
//
// These are go-red tests, not pins. Each fails against the pre-6k
// implementation:
//
//   * "a stalled subscriber does not delay its siblings" blocks one
//     sink for longer than the per-write cap and asserts the broadcast
//     call itself returns promptly. Under `Task.WhenAll` the call
//     awaited the stall.
//   * "a stalled subscriber's frames still arrive in order once it
//     unblocks" asserts the queue is a queue - the pre-6k path had no
//     queue at all, so frames sent during a stall were simply written
//     late or lost to the cap.
//   * "a subscriber that never drains is evicted" asserts the
//     full-queue eviction the bounded channel introduces.
//
// The 5 s per-write cap itself is UNCHANGED and still governs an
// individual write; what changed is that it is no longer anybody
// else's latency.

/// A sink whose writes block on a gate the test controls, and which
/// records the frames it did complete. Reference-identity only - the
/// manager matches connections by instance.
type private GatedSink(disconnect: CancellationToken) =
    let gate = new ManualResetEventSlim(true)
    let received = ResizeArray<string>()

    member _.Block() = gate.Reset()
    member _.Release() = gate.Set()

    member _.Received = lock received (fun () -> received |> Seq.toList)

    interface IConnectionSink with
        member _.DisconnectToken = disconnect

        member _.WriteFrame(bytes, ct) = async {
            // Waits on the gate, honouring the manager's linked
            // per-write token so the 5 s cap can still fire.
            gate.Wait ct
            lock received (fun () -> received.Add(Encoding.UTF8.GetString bytes))
        }

/// A sink that never completes a write until the per-write cap fires.
/// Models the wedged tab: socket open, nobody reading.
type private WedgedSink(disconnect: CancellationToken) =
    interface IConnectionSink with
        member _.DisconnectToken = disconnect

        member _.WriteFrame(_, ct) = async {
            // Blocks until the manager's linked per-write token fires
            // (the 5 s cap, or the connection's own disconnect). The
            // queue therefore fills long before that, which is the
            // eviction path under test.
            let! _ = Async.AwaitWaitHandle ct.WaitHandle
            return ()
        }

/// Phase 870 — a response body that behaves like Kestrel's on the one point this pin
/// needs: a write or flush issued while another is still in flight
/// faults, as a faulted `Task` (the way an async method faults), rather
/// than being serialised for the caller. `MemoryStream` completes every
/// write synchronously, so it can never show the race.
type internal SerialOnlyBody() =
    inherit Stream()

    let buffer = new MemoryStream()
    let busy = ref 0

    static member val ConcurrentWriteMessage = "Phase 870 fake: concurrent writes are not supported."

    member _.Text = lock buffer (fun () -> Text.Encoding.UTF8.GetString(buffer.ToArray()))

    member private _.Guarded(append: unit -> unit) : Task = task {
        if Interlocked.CompareExchange(&busy.contents, 1, 0) <> 0 then
            raise (InvalidOperationException SerialOnlyBody.ConcurrentWriteMessage)

        try
            do! Task.Yield()
            lock buffer append
        finally
            Volatile.Write(&busy.contents, 0)
    }

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(_: byte[], _: int, _: int) : int = raise (NotSupportedException())
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())

    override _.Write(bytes: byte[], offset: int, count: int) =
        lock buffer (fun () -> buffer.Write(bytes, offset, count))

    override this.WriteAsync(bytes: byte[], offset: int, count: int, _: CancellationToken) : Task =
        let copy = Array.sub bytes offset count
        this.Guarded(fun () -> buffer.Write(copy, 0, copy.Length))

    override this.WriteAsync(bytes: ReadOnlyMemory<byte>, _: CancellationToken) : ValueTask =
        let copy = bytes.ToArray()
        ValueTask(this.Guarded(fun () -> buffer.Write(copy, 0, copy.Length)))

    override this.FlushAsync(_: CancellationToken) : Task = this.Guarded ignore

let private payload (s: string) = Encoding.UTF8.GetBytes s

// ─── Phase 1064 — the notification stream has one writer ─────────────
//
// `/api/notifications` used to write each envelope straight to the
// response body from the channel callback (`WriteAsync |> ignore`, then
// `FlushAsync |> ignore`, inside `try … with _ -> ()`), beside a 15 s
// keepalive loop of its own and the manager's 30 s keepalive through the
// `ConnectionWriter` `manager.Add` had wrapped the sink in: three writers
// on one response, two of them unobserved. Phase 870 made the live-session
// stream single-writer; this pins the notification stream to the same bar
// over the same faulting body.

let private waitUntilTrue (timeoutMs: int) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep 5

    condition ()

let private envelopeSeqs (text: string) =
    Text.RegularExpressions.Regex.Matches(text, "n=(\d+);")
    |> Seq.map (fun m -> int m.Groups[1].Value)
    |> Seq.toList

let tests =
    testList "SsePerConnectionWriter" [

        testCase "a stalled subscriber does not delay its siblings"
        <| fun () ->
            use mgr = new SSEConnectionManager()
            use cts = new CancellationTokenSource()

            let stalled = GatedSink(cts.Token)
            let healthy = GatedSink(cts.Token)

            mgr.Add("scope-A", stalled) |> ignore
            mgr.Add("scope-A", healthy) |> ignore

            // Wedge one connection for longer than the per-write cap so
            // a `Task.WhenAll` fan-out could not possibly have returned.
            stalled.Block()

            let sw = Stopwatch.StartNew()
            mgr.Broadcast("scope-A", payload "data: one\n\n")
            sw.Stop()

            Expect.isLessThan sw.ElapsedMilliseconds 1000L "Broadcast returns without waiting on the stalled connection"

            // And the healthy connection really did get the frame -
            // returning fast by dropping everyone's work would satisfy
            // the assertion above and be worthless.
            let delivered =
                let deadline = DateTime.UtcNow.AddSeconds 5.0
                let mutable got = false

                while not got && DateTime.UtcNow < deadline do
                    if not (List.isEmpty healthy.Received) then
                        got <- true
                    else
                        Thread.Sleep 5

                got

            Expect.isTrue delivered "the healthy connection received the frame while the other was stalled"
            Expect.isEmpty stalled.Received "the stalled connection has not written yet"

            stalled.Release()

        testCase "a stalled subscriber's frames still arrive, in order, once it unblocks"
        <| fun () ->
            use mgr = new SSEConnectionManager()
            use cts = new CancellationTokenSource()

            let slow = GatedSink(cts.Token)
            mgr.Add("scope-B", slow) |> ignore

            slow.Block()

            for i in 1..5 do
                mgr.Broadcast("scope-B", payload (sprintf "data: %d\n\n" i))

            Expect.isEmpty slow.Received "nothing written while the sink is blocked"

            slow.Release()

            Expect.isTrue (mgr.WaitForDelivery()) "the queue drains once the sink unblocks"

            let expected = [ 1..5 ] |> List.map (sprintf "data: %d\n\n")

            // Per-connection ORDER is the property SSE delta streams
            // depend on, and the single reader loop is what preserves
            // it. Cross-connection order never was guaranteed.
            Expect.equal slow.Received expected "every queued frame arrived, in send order"

        testCase "a subscriber that never drains is evicted rather than retried forever"
        <| fun () ->
            use mgr = new SSEConnectionManager()
            use cts = new CancellationTokenSource()

            let wedged = WedgedSink(cts.Token)
            mgr.Add("scope-C", wedged) |> ignore

            Expect.equal (mgr.Snapshot().RegisteredScopes["scope-C"]) 1 "registered before the flood"

            // Deeper than the manager's internal queue bound, so the
            // channel fills and the connection is classified dead. The
            // pre-6k path had no queue and so no such classification -
            // it just paid the 5 s cap on every event, forever.
            for i in 1..1000 do
                mgr.Broadcast("scope-C", payload (sprintf "data: %d\n\n" i))

            let evicted =
                let deadline = DateTime.UtcNow.AddSeconds 10.0
                let mutable gone = false

                while not gone && DateTime.UtcNow < deadline do
                    match mgr.Snapshot().RegisteredScopes.TryFind "scope-C" with
                    | None
                    | Some 0 -> gone <- true
                    | Some _ -> Thread.Sleep 10

                gone

            Expect.isTrue evicted "a connection that never drains is dropped from the scope"

        testCase "an already-disconnected subscriber is evicted without a write attempt"
        <| fun () ->
            use mgr = new SSEConnectionManager()
            use live = new CancellationTokenSource()
            use dead = new CancellationTokenSource()

            let goner = GatedSink(dead.Token)
            let stayer = GatedSink(live.Token)

            mgr.Add("scope-D", goner) |> ignore
            mgr.Add("scope-D", stayer) |> ignore

            dead.Cancel()

            mgr.Broadcast("scope-D", payload "data: x\n\n")
            Expect.isTrue (mgr.WaitForDelivery()) "the live connection drains"

            Expect.equal (mgr.Snapshot().RegisteredScopes["scope-D"]) 1 "the cancelled connection is gone"
            Expect.isEmpty goner.Received "and was never written to"
            Expect.equal stayer.Received [ "data: x\n\n" ] "the live connection got the frame"

        testCase "Remove stops the connection's writer loop"
        <| fun () ->
            use mgr = new SSEConnectionManager()
            use cts = new CancellationTokenSource()

            let sink = GatedSink(cts.Token)
            mgr.Add("scope-E", sink) |> ignore

            mgr.Broadcast("scope-E", payload "data: before\n\n")
            Expect.isTrue (mgr.WaitForDelivery()) "delivered while registered"

            mgr.Remove("scope-E", sink)
            mgr.Broadcast("scope-E", payload "data: after\n\n")

            // Give a would-be write time to land before asserting it did
            // not: an immediate assertion would pass even if the loop
            // were still running.
            Thread.Sleep 100

            Expect.equal sink.Received [ "data: before\n\n" ] "nothing is written to a removed connection"

        testCase
            "a burst of envelopes to one /api/notifications connection arrives complete and in order, with no unobserved write fault (Phase 1064)"
        <| fun () ->
            let frames = 200
            let bursts = 10
            let unobserved = ref 0

            let onUnobserved =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    let ours =
                        args.Exception.Flatten().InnerExceptions
                        |> Seq.exists (fun e -> e.Message = SerialOnlyBody.ConcurrentWriteMessage)

                    if ours then
                        Interlocked.Increment(&unobserved.contents) |> ignore)

            TaskScheduler.UnobservedTaskException.AddHandler onUnobserved

            let failures =
                try
                    [
                        for burst in 1..bursts do
                            use manager = new SSEConnectionManager()

                            let channel =
                                NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

                            let handler =
                                NotificationHandler.notificationHandler channel manager QueryParamFallback

                            use cts = new CancellationTokenSource()
                            let ctx = DefaultHttpContext()
                            ctx.Request.Method <- "GET"
                            ctx.Request.Path <- PathString "/api/notifications"

                            ctx.Features.Set<IHttpRequestLifetimeFeature>(
                                HttpRequestLifetimeFeature(RequestAborted = cts.Token)
                            )

                            ctx.Items["ToolUp.UserId"] <- "u-1064"
                            let body = new SerialOnlyBody()
                            ctx.Response.Body <- body
                            let serving = handler (fun c -> Task.FromResult(Some c)) ctx

                            if not (waitUntilTrue 5000 (fun () -> body.Text.Contains ": ready")) then
                                failtestf "burst %d: the stream never opened" burst

                            for i in 0 .. frames - 1 do
                                channel.Publish(
                                    "u-1064",
                                    Notification.SystemMessage(SystemMessageLevel.Info, $"n=%d{i};")
                                )
                                |> Async.RunSynchronously

                            waitUntilTrue 2000 (fun () -> (envelopeSeqs body.Text).Length >= frames)
                            |> ignore

                            cts.Cancel()
                            serving.GetAwaiter().GetResult() |> ignore

                            let seen = envelopeSeqs body.Text

                            if seen <> [ 0 .. frames - 1 ] then
                                yield burst, seen.Length
                    ]
                finally
                    GC.Collect()
                    GC.WaitForPendingFinalizers()
                    GC.Collect()
                    TaskScheduler.UnobservedTaskException.RemoveHandler onUnobserved

            Expect.isEmpty
                failures
                $"every burst delivers all %d{frames} envelopes in order (burst, envelopes seen) — %d{failures.Length} of %d{bursts} bursts failed, %d{unobserved.Value} unobserved write faults"

            Expect.equal unobserved.Value 0 "no envelope write faults inside an unobserved task"
    ]