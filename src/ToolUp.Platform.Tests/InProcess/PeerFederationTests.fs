module ToolUp.Platform.Tests.InProcess.PeerFederationTests

open System
open System.Threading
open Expecto
open ToolUp.InterPlatform

// ─── Phase 18c — federation primitives (fan-out + cascade) ───────────
//
// Pure in-process coverage of `IPeerFanout` (scatter to N peers, collect
// a total per-peer result map under timeout / quorum / first-success
// policies) and `IPeerCascade` (next-hop routing bookkeeping over the
// foundation's Route / HopsRemaining semantics, with loop / budget guards
// firing caller-side before the wire). No TestServer: the orchestration
// is pure over a caller-supplied call thunk, so the substrate is exercised
// directly without standing up a peer host.

let private peer id = { PeerId = id; DisplayName = id }

let private target id = {
    Peer = peer id
    BaseUrl = sprintf "https://%s.example" id
}

let private ctx route hops = {
    Peer = peer (List.head route)
    User = Anonymous
    ContractVersion = { Major = 1; Minor = 0 }
    Route = route
    RootRequestId = "root-123"
    ParentRequestId = None
    HopsRemaining = hops
}

// ─── Fan-out ──────────────────────────────────────────────────────────

let fanoutTests =
    let fanout = PeerFanout.create ()

    testList "Phase 18c IPeerFanout" [
        testCaseAsync "empty target list returns empty map"
        <| async {
            let! result = fanout.Fanout([], FanoutPolicy.all, (fun _ -> async { return Ok 1 }))
            Expect.isEmpty result "no targets ⇒ empty map"
        }

        testCaseAsync "all-policy collects every peer's Ok result"
        <| async {
            let targets = [ target "a"; target "b"; target "c" ]
            let call (t: TargetPeer) = async { return Ok(t.Peer.PeerId.Length) }
            let! result = fanout.Fanout(targets, FanoutPolicy.all, call)
            Expect.equal result.Count 3 "map total over targets"
            Expect.equal result[peer "a"] (Ok 1) "a answered Ok"
            Expect.equal result[peer "b"] (Ok 1) "b answered Ok"
            Expect.equal result[peer "c"] (Ok 1) "c answered Ok"
        }

        testCaseAsync "partial failure is preserved per-peer, never collapsed"
        <| async {
            let targets = [ target "a"; target "b"; target "c" ]

            let call (t: TargetPeer) = async {
                if t.Peer.PeerId = "b" then
                    return Error(PeerHandler "b refused")
                else
                    return Ok 7
            }

            let! result = fanout.Fanout(targets, FanoutPolicy.all, call)
            Expect.equal result[peer "a"] (Ok 7) "a Ok"
            Expect.equal result[peer "b"] (Error(PeerHandler "b refused")) "b's error preserved"
            Expect.equal result[peer "c"] (Ok 7) "c Ok"
        }

        testCaseAsync "an exception in the call thunk becomes a transport error, not a throw"
        <| async {
            let targets = [ target "a"; target "boom" ]

            let call (t: TargetPeer) = async {
                if t.Peer.PeerId = "boom" then
                    return failwith "kaboom"
                else
                    return Ok 1
            }

            let! result = fanout.Fanout(targets, FanoutPolicy.all, call)
            Expect.equal result[peer "a"] (Ok 1) "healthy peer unaffected"

            match result[peer "boom"] with
            | Error(PeerTransport msg) -> Expect.stringContains msg "kaboom" "exception captured as transport error"
            | other -> failtestf "expected PeerTransport, got %A" other
        }

        testCaseAsync "first-success returns the immediate Ok; map stays total over targets"
        <| async {
            let targets = [ target "fast"; target "slow1"; target "slow2" ]

            let call (t: TargetPeer) = async {
                if t.Peer.PeerId = "fast" then
                    return Ok 42
                else
                    do! Async.Sleep 1000
                    return Ok 0
            }

            let! result = fanout.Fanout(targets, FanoutPolicy.firstSuccess, call)
            Expect.equal result.Count 3 "map total over targets even on early return"
            Expect.equal result[peer "fast"] (Ok 42) "the immediate success is captured"
        }

        testCaseAsync "quorum returns once k peers answer; the k fast peers are captured"
        <| async {
            let targets = [ target "f1"; target "f2"; target "slow" ]

            let call (t: TargetPeer) = async {
                if t.Peer.PeerId = "slow" then
                    do! Async.Sleep 1000
                    return Ok 0
                else
                    return Ok 9
            }

            let! result = fanout.Fanout(targets, FanoutPolicy.quorum 2, call)
            Expect.equal result.Count 3 "map total over targets"
            Expect.equal result[peer "f1"] (Ok 9) "first quorum member captured"
            Expect.equal result[peer "f2"] (Ok 9) "second quorum member captured"
        }

        testCaseAsync "timeout records the unanswered peer as an error, not an Ok"
        <| async {
            let targets = [ target "fast"; target "slow" ]

            let call (t: TargetPeer) = async {
                if t.Peer.PeerId = "slow" then
                    do! Async.Sleep 2000
                    return Ok 0
                else
                    return Ok 1
            }

            let! result = fanout.Fanout(targets, FanoutPolicy.withTimeout (TimeSpan.FromMilliseconds 100.0), call)
            Expect.equal result[peer "fast"] (Ok 1) "fast peer answered before the deadline"

            match result[peer "slow"] with
            | Error _ -> ()
            | Ok _ -> failtest "the timed-out peer must not surface as Ok"
        }

        // ─── Phase 313 — max-concurrency bound ───────────────────────

        testCaseAsync "MaxConcurrency caps in-flight calls and still returns a total map"
        <| async {
            let targets = [ for i in 1..6 -> target (sprintf "p%d" i) ]
            let inFlight = ref 0
            let peakInFlight = ref 0

            let call (_: TargetPeer) = async {
                let now = Interlocked.Increment(&inFlight.contents)
                // Non-atomic max is fine: `inFlight` is atomic, and any
                // breach of the bound would have to be observed by at
                // least one of the six samples.
                if now > peakInFlight.Value then
                    peakInFlight.Value <- now

                do! Async.Sleep 60
                Interlocked.Decrement(&inFlight.contents) |> ignore
                return Ok 1
            }

            let policy = FanoutPolicy.all |> FanoutPolicy.withMaxConcurrency 2
            let! result = fanout.Fanout(targets, policy, call)
            Expect.equal result.Count 6 "map total over targets under the bound"
            Expect.isLessThanOrEqual peakInFlight.Value 2 "never more than two calls in flight"

            for i in 1..6 do
                Expect.equal result[peer (sprintf "p%d" i)] (Ok 1) (sprintf "p%d answered Ok" i)
        }

        testCaseAsync "no bound (the default) launches every target at once"
        <| async {
            let targets = [ for i in 1..6 -> target (sprintf "p%d" i) ]
            let inFlight = ref 0
            let peakInFlight = ref 0

            let call (_: TargetPeer) = async {
                let now = Interlocked.Increment(&inFlight.contents)

                if now > peakInFlight.Value then
                    peakInFlight.Value <- now

                do! Async.Sleep 60
                Interlocked.Decrement(&inFlight.contents) |> ignore
                return Ok 1
            }

            let! result = fanout.Fanout(targets, FanoutPolicy.all, call)
            Expect.equal result.Count 6 "map total over targets"
            Expect.isGreaterThan peakInFlight.Value 2 "unbounded default is not throttled"
        }

        testCaseAsync "the bound composes with quorum — early return still yields a total map"
        <| async {
            let targets = [ for i in 1..6 -> target (sprintf "p%d" i) ]

            let call (t: TargetPeer) = async {
                if t.Peer.PeerId = "p1" || t.Peer.PeerId = "p2" then
                    return Ok 9
                else
                    do! Async.Sleep 2000
                    return Ok 0
            }

            let policy = FanoutPolicy.quorum 2 |> FanoutPolicy.withMaxConcurrency 2
            let! result = fanout.Fanout(targets, policy, call)
            Expect.equal result.Count 6 "map total over targets even on early return under a bound"
            Expect.equal result[peer "p1"] (Ok 9) "first quorum member captured"
            Expect.equal result[peer "p2"] (Ok 9) "second quorum member captured"

            // p3–p6 were still queued behind the bound when the quorum
            // fired, so they must read as not-awaited, never as Ok.
            for i in 3..6 do
                match result[peer (sprintf "p%d" i)] with
                | Error _ -> ()
                | Ok _ -> failtestf "queued peer p%d must not surface as Ok" i
        }

        testCaseAsync "a bound above the target count degrades to unbounded"
        <| async {
            let targets = [ target "a"; target "b" ]
            let call (t: TargetPeer) = async { return Ok t.Peer.PeerId }
            let policy = FanoutPolicy.all |> FanoutPolicy.withMaxConcurrency 99
            let! result = fanout.Fanout(targets, policy, call)
            Expect.equal result.Count 2 "clamped to targetCount, map still total"
            Expect.equal result[peer "a"] (Ok "a") "a answered Ok"
            Expect.equal result[peer "b"] (Ok "b") "b answered Ok"
        }

        testCaseAsync "a non-positive bound clamps to one rather than deadlocking"
        <| async {
            let targets = [ target "a"; target "b"; target "c" ]
            let call (t: TargetPeer) = async { return Ok t.Peer.PeerId }
            let policy = FanoutPolicy.all |> FanoutPolicy.withMaxConcurrency 0
            let! result = fanout.Fanout(targets, policy, call)
            Expect.equal result.Count 3 "clamped to 1, every peer still answers"
            Expect.equal result[peer "b"] (Ok "b") "serialised fan-out still completes"
        }
    ]

// ─── Cascade ──────────────────────────────────────────────────────────

let cascadeTests =
    let cascade = PeerCascade.create ()

    testList "Phase 18c IPeerCascade" [
        testCase "next-hop appends the local peer, decrements the budget, preserves the root id"
        <| fun () ->
            let inbound = ctx [ "a" ] 5

            match cascade.NextHop(inbound, peer "b", target "c") with
            | Ok outbound ->
                Expect.equal outbound.Route [ "a"; "b" ] "local peer appended to the route"
                Expect.equal outbound.HopsRemaining 4 "hop budget decremented"
                Expect.equal outbound.Peer (peer "b") "calling peer re-keyed to the forwarder"
                Expect.equal outbound.ParentRequestId (Some "root-123") "parent set to the cascade root"
                Expect.equal outbound.RootRequestId "root-123" "root id preserved across the hop"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "forwarding to a peer already on the route is a structured loop error"
        <| fun () ->
            let inbound = ctx [ "a"; "b" ] 5
            // c forwards back to a — a is already on the route.
            match cascade.NextHop(inbound, peer "c", target "a") with
            | Error(PeerLoopDetected route) -> Expect.contains route "a" "loop names the repeated peer"
            | other -> failtestf "expected PeerLoopDetected, got %A" other

        testCase "forwarding when the local peer would repeat the route is a loop error"
        <| fun () ->
            // b is already on the route and is also the forwarder ⇒ dup.
            let inbound = ctx [ "a"; "b" ] 5

            match cascade.NextHop(inbound, peer "b", target "c") with
            | Error(PeerLoopDetected _) -> ()
            | other -> failtestf "expected PeerLoopDetected, got %A" other

        testCase "forwarding with no hop budget is PeerHopLimitExceeded"
        <| fun () ->
            let inbound = ctx [ "a" ] 0

            match cascade.NextHop(inbound, peer "b", target "c") with
            | Error PeerHopLimitExceeded -> ()
            | other -> failtestf "expected PeerHopLimitExceeded, got %A" other

        testCaseAsync "Forward invokes the call with the derived context on the Ok path"
        <| async {
            let inbound = ctx [ "a" ] 3
            let observed = ref None

            let call (outbound: PeerCallContext) = async {
                observed.Value <- Some outbound
                return Ok "done"
            }

            let! result = cascade.Forward(inbound, peer "b", target "c", call)
            Expect.equal result (Ok "done") "call result flows back"

            match observed.Value with
            | Some o -> Expect.equal o.Route [ "a"; "b" ] "call saw the derived (extended) route"
            | None -> failtest "Forward must invoke the call on the Ok path"
        }

        testCaseAsync "Forward short-circuits a loop without invoking the call"
        <| async {
            let inbound = ctx [ "a"; "b" ] 3
            let invoked = ref false

            let call (_: PeerCallContext) = async {
                invoked.Value <- true
                return Ok "should not happen"
            }

            let! result = cascade.Forward(inbound, peer "c", target "a", call)

            match result with
            | Error(PeerLoopDetected _) -> ()
            | other -> failtestf "expected PeerLoopDetected, got %A" other

            Expect.isFalse invoked.Value "a doomed hop never reaches the wire"
        }
    ]