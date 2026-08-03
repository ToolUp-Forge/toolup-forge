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

// ─── Phase 314 — cascade-aware typed proxy forwarding ─────────────────
//
// The typed proxy has two entry points and the difference is the whole
// phase: `create` starts a NEW cascade root, `forward` CONTINUES an
// inbound one. Coverage is pure in-process over a capturing
// `IPeerClient` — what matters is the `PeerCallContext` the proxy hands
// the transport, and whether it reaches the transport at all.

/// A minimal record-of-functions contract. Must not be `private`:
/// `FSharpType.IsRecord` reads a private-representation record as a
/// non-record and the proxy build fails.
type EchoContract = { Echo: string -> Async<string> }

/// `IPeerClient` that records the context of every call it is handed and
/// answers with a canned serialised result. Standing in for the wire lets
/// the test assert both what crossed it and that nothing did.
type private CapturingPeerClient() =
    let calls = ResizeArray<PeerCallContext>()

    member _.Calls = calls |> List.ofSeq
    member _.Last = calls |> Seq.tryLast

    interface IPeerClient with
        member _.Invoke(_target, _contractId, _methodName, payload, ?_cancellationToken) = async {
            lock calls (fun () -> calls.Add payload.Context)
            return Ok(JsonRpc.serialize "echoed")
        }

        member _.PollJob(_target, _contractId, _jobId, ?_cancellationToken) = async {
            return Error(PeerTransport "the echo contract has no long-running method")
        }

let proxyForwardingTests =

    /// A proxy config for `localPeer` calling `next`, over `client`.
    let configFor (client: IPeerClient) (localPeer: string) (next: string) : PeerProxyConfig = {
        Client = client
        Target = target next
        Caller = peer localPeer
        User = Anonymous
        Version = { Major = 1; Minor = 0 }
        ContractId = "echo"
        HopBudget = 8
    }

    /// Run `call` and return the `PeerError` it short-circuited with, or
    /// fail the test if it did not raise one.
    let expectPeerError (call: Async<string>) = async {
        let! outcome = Async.Catch call

        return
            match outcome with
            | Choice1Of2 value -> failtestf "expected a PeerInvocationException, got Ok %A" value
            | Choice2Of2(PeerInvocationException e) -> e
            | Choice2Of2 ex -> failtestf "expected a PeerInvocationException, got %A" ex
    }

    testList "Phase 314 cascade-aware typed proxy forwarding" [

        // ─── `create` is unchanged: a fresh cascade root ──────────────

        testCaseAsync "a bare create still mints a fresh cascade root per call"
        <| async {
            let client = CapturingPeerClient()
            let proxy = JsonRpcPeerClient.create<EchoContract> (configFor client "a" "b")

            let! first = proxy.Echo "one"
            let! second = proxy.Echo "two"
            Expect.equal first "echoed" "the canned result deserialises"
            Expect.equal second "echoed" "the canned result deserialises"

            match client.Calls with
            | [ c1; c2 ] ->
                Expect.equal c1.Route [ "a" ] "route reset to the caller alone"
                Expect.equal c1.HopsRemaining 8 "hop budget seeded from HopBudget"
                Expect.equal c1.ParentRequestId None "a root call has no parent"
                Expect.notEqual c1.RootRequestId c2.RootRequestId "each create call is its own cascade root"
            | other -> failtestf "expected two recorded calls, got %i" (List.length other)
        }

        // ─── `forward` continues the inbound cascade ──────────────────

        testCaseAsync "forward preserves the root id, extends the route, and decrements the budget"
        <| async {
            let client = CapturingPeerClient()
            // b is handling a's call and forwards to c.
            let inbound = ctx [ "a" ] 5

            let proxy =
                JsonRpcPeerClient.forward<EchoContract> inbound (configFor client "b" "c")

            let! result = proxy.Echo "onward"
            Expect.equal result "echoed" "the forwarded call resolves normally"

            match client.Last with
            | Some outbound ->
                Expect.equal outbound.RootRequestId "root-123" "root id preserved across the hop (GP 7)"
                Expect.equal outbound.Route [ "a"; "b" ] "forwarding peer appended to the route"
                Expect.equal outbound.HopsRemaining 4 "hop budget decremented, not reset to HopBudget"
                Expect.equal outbound.ParentRequestId (Some "root-123") "parent set to the cascade root"
                Expect.equal outbound.Peer (peer "b") "calling peer re-keyed to the forwarder"
            | None -> failtest "the forwarded call must reach the transport"
        }

        testCaseAsync "a two-hop cascade forwarded through the proxy keeps one root id end to end"
        <| async {
            let client = CapturingPeerClient()

            // Hop 1: b forwards a's inbound call to c.
            let atB = ctx [ "a" ] 5
            let bProxy = JsonRpcPeerClient.forward<EchoContract> atB (configFor client "b" "c")
            let! _ = bProxy.Echo "hop-1"

            // Hop 2: c receives exactly what b put on the wire, and
            // forwards it onward to d.
            let atC =
                match client.Last with
                | Some ctx -> ctx
                | None -> failtest "hop 1 must reach the transport"

            let cProxy = JsonRpcPeerClient.forward<EchoContract> atC (configFor client "c" "d")
            let! _ = cProxy.Echo "hop-2"

            match client.Calls with
            | [ hop1; hop2 ] ->
                Expect.equal hop1.RootRequestId "root-123" "hop 1 carries the cascade root"
                Expect.equal hop2.RootRequestId "root-123" "hop 2 carries the SAME cascade root"
                Expect.equal hop1.Route [ "a"; "b" ] "hop 1 route"
                Expect.equal hop2.Route [ "a"; "b"; "c" ] "hop 2 route threaded, not reset"
                Expect.equal hop1.HopsRemaining 4 "hop 1 budget"
                Expect.equal hop2.HopsRemaining 3 "hop 2 budget keeps counting down"
            | other -> failtestf "expected two recorded hops, got %i" (List.length other)
        }

        testCaseAsync "forward carries the proxy config's user + version, not the inbound leg's"
        <| async {
            let client = CapturingPeerClient()

            let inbound = {
                ctx [ "a" ] 5 with
                    User =
                        Direct {
                            Subject = "u-1"
                            Issuer = "a"
                            DisplayName = None
                        }
                    ContractVersion = { Major = 1; Minor = 0 }
            }

            let config = {
                configFor client "b" "c" with
                    Version = { Major = 2; Minor = 0 }
            }

            let proxy = JsonRpcPeerClient.forward<EchoContract> inbound config
            let! _ = proxy.Echo "onward"

            match client.Last with
            | Some outbound ->
                Expect.equal outbound.User Anonymous "the forwarding leg vouches for its own configured identity"
                Expect.equal outbound.ContractVersion { Major = 2; Minor = 0 } "the version negotiated for THIS leg"
                Expect.equal outbound.RootRequestId "root-123" "cascade fields still come from the inbound context"
            | None -> failtest "the forwarded call must reach the transport"
        }

        // ─── Doomed hops never leave the forwarding peer ──────────────

        testCaseAsync "a loop short-circuits to PeerLoopDetected before the wire round-trip"
        <| async {
            let client = CapturingPeerClient()
            // c is handling a call that already traversed a → b, and
            // tries to forward back to a.
            let inbound = ctx [ "a"; "b" ] 5

            let proxy =
                JsonRpcPeerClient.forward<EchoContract> inbound (configFor client "c" "a")

            let! error = expectPeerError (proxy.Echo "doomed")

            match error with
            | PeerLoopDetected route -> Expect.contains route "a" "the loop names the repeated peer"
            | other -> failtestf "expected PeerLoopDetected, got %A" other

            Expect.isEmpty client.Calls "a doomed hop never leaves the forwarding peer"
        }

        testCaseAsync "an exhausted budget short-circuits to PeerHopLimitExceeded before the wire round-trip"
        <| async {
            let client = CapturingPeerClient()
            let inbound = ctx [ "a" ] 0

            let proxy =
                JsonRpcPeerClient.forward<EchoContract> inbound (configFor client "b" "c")

            let! error = expectPeerError (proxy.Echo "doomed")
            Expect.equal error PeerHopLimitExceeded "budget exhaustion is structured, not a transport failure"
            Expect.isEmpty client.Calls "a doomed hop never leaves the forwarding peer"
        }

        testCaseAsync "the HopBudget on a forwarding config is ignored — the cascade owns the budget"
        <| async {
            let client = CapturingPeerClient()
            let inbound = ctx [ "a" ] 1

            let config = {
                configFor client "b" "c" with
                    HopBudget = 99
            }

            let proxy = JsonRpcPeerClient.forward<EchoContract> inbound config

            let! _ = proxy.Echo "onward"

            match client.Last with
            | Some outbound ->
                Expect.equal outbound.HopsRemaining 0 "budget came from the inbound context, not HopBudget"
            | None -> failtest "the forwarded call must reach the transport"
        }
    ]