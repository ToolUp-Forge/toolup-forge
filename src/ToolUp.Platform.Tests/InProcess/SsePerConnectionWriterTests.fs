module ToolUp.Platform.Tests.InProcess.SsePerConnectionWriterTests

open System
open System.Diagnostics
open System.Text
open System.Threading
open Expecto
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

let private payload (s: string) = Encoding.UTF8.GetBytes s

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
    ]