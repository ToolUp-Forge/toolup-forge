module ToolUp.Platform.Tests.InProcess.PeerTransportTimeoutTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.InterPlatform

// ─── Phase 312 — peer transport timeout + cancellation propagation ───
//
// `HttpPeerClient.send` called `SendAsync` with no `CancellationToken`,
// so a fan-out that satisfied `firstSuccess` / `quorum` / its deadline
// returned promptly while every peer it stopped awaiting kept a socket
// for the shared client's 100 s default. The latency was hidden; the
// resources were not reclaimed.
//
// **Every cancellation claim here is MEASURED, not inferred.** The stub
// handler waits on the token it was actually handed and records, in a
// `finally`, whether that token fired — a `finally` specifically because
// F# `Async` cancellation bypasses `with` handlers, so a probe that
// concluded "cancelled" from a caught exception type would be asserting
// something other than what it set out to. A probe that inferred
// cancellation from the *shape of the result map* would be worse still:
// the map already looked right before this phase.
//
// The three non-answers are pinned apart, because collapsing them is the
// defect this phase is really about:
//
//   * deadline expiry     → `Error` classified by
//                           `PeerTransportOutcome.isTimeout`
//   * caller cancellation → the computation completes as CANCELLED, no
//                           value at all
//   * peer failure        → the receiver's own structured `PeerError`
//
// Each probe is paired with a control that would go green for the wrong
// reason if the transport had simply broken and started refusing (or
// cancelling) everything.

// ─── Fixtures ─────────────────────────────────────────────────────────

/// One outbound request as the wire saw it. `TokenFired` is the whole
/// measurement: it is written in a `finally` around the wait, so it
/// records the token's state at the moment the request stopped, whatever
/// stopped it. `Stopped` completes at that same moment — answered,
/// aborted, or the hang elapsing — so a test can AWAIT the request's own
/// end rather than polling a wall clock for it.
type private CallProbe(url: string) =
    let stopped =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Url = url
    member val TokenFired = false with get, set
    member val Answered = false with get, set
    member _.Stopped: Task<unit> = stopped.Task
    member _.MarkStopped() = stopped.TrySetResult(()) |> ignore

/// Stub transport routing by host:
///   * `fast.test`  — answers at once,
///   * `fail.test`  — answers a JSON-RPC error carrying a structured
///                    `PeerHandler`, i.e. a genuine peer failure,
///   * `slow.test`  — waits on the token it was handed.
///
/// The slow wait is BOUNDED (`hangFor`). An unbounded one would turn a
/// regression — the token never reaching `SendAsync` — into a hung suite
/// rather than a red one, and a hang is the least actionable failure a
/// test can produce. The bound must sit far ABOVE `eventCap`, never at
/// it (see `hangBudget`): equal budgets race, and the loser is a red run
/// with no regression behind it.
///
/// `fastAnswersAfterArrivalOf` orders the fixture for early-return
/// cases: the fast answer is held until the named host's request is
/// genuinely on the wire. `DefaultPeerFanout` launches children with
/// `Async.Start` and cancels a still-QUEUED child before it ever
/// launches (documented behaviour — it lands as not-awaited and sends
/// nothing), so without this ordering the fast answer can win the
/// launch race on a loaded scheduler and the slow request then NEVER
/// reaches the wire: there is nothing on the socket to cancel, and a
/// test asserting cancellation-reach burns its whole budget waiting for
/// a request that does not exist. That launch race — not slow
/// cancellation delivery — was the original ~10 s flake.
type private ProbeHandler(hangFor: TimeSpan, ?fastAnswersAfterArrivalOf: string) =
    inherit HttpMessageHandler()

    let probes = ConcurrentBag<CallProbe>()

    // Per-host arrival events, so a test can await "a request for this
    // host reached the wire" instead of polling the probe bag for it.
    let arrivals = ConcurrentDictionary<string, TaskCompletionSource<CallProbe>>()

    let arrivalFor (host: string) =
        arrivals.GetOrAdd(
            host,
            fun _ -> TaskCompletionSource<CallProbe>(TaskCreationOptions.RunContinuationsAsynchronously)
        )

    let respond (body: string) =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        response

    member _.Probes = probes |> List.ofSeq

    member this.ProbeFor(host: string) =
        this.Probes |> List.tryFind (fun p -> p.Url.Contains host)

    /// Completes the moment the first request for `host` reaches the
    /// stub transport, carrying that request's probe.
    member _.Arrived(host: string) : Task<CallProbe> = (arrivalFor host).Task

    override _.SendAsync(request: HttpRequestMessage, ct: CancellationToken) : Task<HttpResponseMessage> = task {
        let url = request.RequestUri.ToString()
        let probe = CallProbe(url)
        probes.Add probe
        (arrivalFor request.RequestUri.Host).TrySetResult probe |> ignore

        try
            if url.Contains "fail.test" then
                probe.Answered <- true

                return
                    respond (
                        JsonRpc.serialize (JsonRpc.failure "root-312" (PeerHandler "the receiver's handler failed"))
                    )
            elif url.Contains "slow.test" then
                try
                    do! Task.Delay(hangFor, ct)
                finally
                    // The measurement. Written whether the wait elapsed
                    // or was aborted, so the two are distinguishable.
                    probe.TokenFired <- ct.IsCancellationRequested

                probe.Answered <- true
                return respond (JsonRpc.serialize (JsonRpc.success "root-312" "slow-answer"))
            else
                // The opt-in ordering (see the type doc): make "the slow
                // request is in flight when the early return fires" true
                // by construction. Bounded by `hangFor` under `ct`, so a
                // slow child that never launches at all still produces a
                // red run rather than a hung one.
                match fastAnswersAfterArrivalOf with
                | Some host ->
                    let! _ = Task.WhenAny((arrivalFor host).Task :> Task, Task.Delay(hangFor, ct))
                    ()
                | None -> ()

                probe.Answered <- true
                return respond (JsonRpc.serialize (JsonRpc.success "root-312" "fast-answer"))
        finally
            // Outside the slow leg's own `finally`, so `TokenFired` is
            // already written by the time an awaiter of `Stopped`
            // resumes — an abort completes the event too.
            probe.MarkStopped()
    }

/// Mints a token without a secret store — the auth leg is not what these
/// cases are about.
type private StubAuth() =
    interface IPeerAuthProvider with
        member _.IssuePeerToken(_, _, _) = async { return Ok "stub-token" }
        member _.ValidatePeerToken _ = async { return Error(PeerUnauthorized "not used") }
        member _.VerifyDelegation _ = async { return Ok() }

let private peer (id: string) : PeerIdentity = { PeerId = id; DisplayName = id }

/// Phase 339 — `https`, because the transport now refuses to send a peer
/// bearer token to a non-loopback cleartext host before it mints one.
/// The stub handler below never performs a TLS handshake, so the scheme
/// changes nothing these cases measure; it is only what lets them reach
/// the wire at all. (`PeerTransportTlsTests` is where the refusal itself
/// is measured.)
let private targetAt (id: string) (host: string) : TargetPeer = {
    Peer = peer id
    BaseUrl = $"https://{host}"
}

let private fastPeer = targetAt "fast-peer" "fast.test"
let private slowPeer = targetAt "slow-peer" "slow.test"
let private failPeer = targetAt "fail-peer" "fail.test"
let private localId = peer "local-312"
let private contractId = "probe"

let private payload: PeerWirePayload = {
    Context = {
        Peer = localId
        User = Anonymous
        ContractVersion = { Major = 1; Minor = 0 }
        Route = [ localId.PeerId ]
        RootRequestId = "root-312"
        ParentRequestId = None
        HopsRemaining = 4
    }
    Arguments = "[]"
}

/// How long a hanging peer holds its socket open. Deliberately far
/// longer than `eventCap` below — the two durations must never be close
/// enough to race. When both were 10 s, a loaded machine could delay
/// cancellation just past the hang's own expiry: the delay then
/// completed normally, `TokenFired` was never set, and the (then
/// polling) observer burned its whole budget and reported false — a
/// ~10.0 s flake with no regression behind it. An abandoned hang never
/// outlives its test: every case disposes its `HttpClient`, which
/// cancels pending requests.
let private hangBudget = TimeSpan.FromSeconds 60.0

/// The cap on every fixture-event await below. Generous, because its
/// only job is to turn a genuine liveness failure into a red run rather
/// than a hung suite — it is never the measurement itself. Must stay
/// comfortably SHORTER than `hangBudget`, so a hang elapsing on its own
/// can never masquerade as the event it guards.
let private eventCap = TimeSpan.FromSeconds 30.0

/// Await an event the fixture itself completes — event-driven, never a
/// polled wall clock, so a saturated scheduler delays the await instead
/// of failing it. On cap expiry the failure attributes to DELIVERY —
/// the event never fired, so the claim under test was never measured at
/// all — and deliberately does not restate that claim.
let private awaited (whatNeverHappened: string) (event: Task<'T>) : Async<'T> = async {
    let! winner = Task.WhenAny(event :> Task, Task.Delay eventCap) |> Async.AwaitTask

    if not (obj.ReferenceEquals(winner, event)) then
        failtest
            $"%s{whatNeverHappened} within %.0f{eventCap.TotalSeconds} s — the fixture event never fired, so cancellation (or the scheduler) never delivered it; a liveness stall, not a measured verdict on the behaviour under test"

    return! Async.AwaitTask event
}

let private transportOn (handler: ProbeHandler) (policy: PeerTransportPolicy) =
    let client = new HttpClient(handler)
    client, HttpPeerClient(client, StubAuth(), localId, policy) :> IPeerClient

// ─── (1) The per-call deadline ────────────────────────────────────────

let deadlineTests =
    testList "Phase 312 — an outbound peer call honours a bounded deadline" [

        testCaseAsync "a deadline expiry reaches the socket and reports as a TIMEOUT"
        <| async {
            let handler = new ProbeHandler(hangBudget)

            let client, transport =
                transportOn
                    handler
                    (PeerTransportPolicy.defaults
                     |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromMilliseconds 200.0))

            use _client = client

            let! result = transport.Invoke(slowPeer, contractId, "Measure", payload)

            match result with
            | Ok value -> failtestf "the hanging peer must not answer Ok, got %s" value
            | Error e ->
                Expect.isTrue
                    (PeerTransportOutcome.isTimeout e)
                    $"the deadline is reported as a timeout, distinguishable from a peer failure — got %A{e}"

            // The claim the DU case alone cannot make: the request was
            // ABORTED, not merely stopped being awaited. Before this
            // phase the socket stayed held for the client's 100 s
            // default and this assertion is what goes red if the token
            // stops reaching `SendAsync`.
            let! slow =
                handler.Arrived "slow.test"
                |> awaited "the request never reached the stub transport"

            do!
                slow.Stopped
                |> awaited
                    "the hanging request never stopped — the deadline's cancellation was never delivered to the socket"

            Expect.isTrue slow.TokenFired "the deadline cancelled the in-flight request — the handler's own token fired"
            Expect.isFalse slow.Answered "…and the aborted request never completed its response"
        }

        testCaseAsync "CONTROL — a peer that answers within the deadline is unaffected"
        <| async {
            // Without this, "the slow peer timed out" would pass just as
            // happily against a transport that had started cancelling
            // everything.
            let handler = new ProbeHandler(hangBudget)

            let client, transport =
                transportOn
                    handler
                    (PeerTransportPolicy.defaults
                     |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromMilliseconds 200.0))

            use _client = client

            let! result = transport.Invoke(fastPeer, contractId, "Measure", payload)

            match result with
            | Ok json ->
                Expect.equal (JsonRpc.deserialize<string> json) "fast-answer" "the fast peer round-trips intact"
            | Error e -> failtestf "the fast peer must answer Ok, got %A" e

            let fast = handler.ProbeFor "fast.test" |> Option.get
            Expect.isFalse fast.TokenFired "…and its request was never cancelled"
        }

        testCaseAsync "CONTROL — a peer FAILURE stays a peer failure, not a timeout"
        <| async {
            // The third leg of the trichotomy. A receiver that answers
            // with a structured error must not be reclassified by the
            // deadline machinery.
            let handler = new ProbeHandler(hangBudget)

            let client, transport =
                transportOn
                    handler
                    (PeerTransportPolicy.defaults
                     |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromMilliseconds 200.0))

            use _client = client

            let! result = transport.Invoke(failPeer, contractId, "Measure", payload)

            match result with
            | Error(PeerHandler message) ->
                Expect.stringContains message "handler failed" "the receiver's own error survives the transport"

                Expect.isFalse
                    (PeerTransportOutcome.isTimeout (PeerHandler message))
                    "a peer failure is not classified as a timeout"
            | other -> failtestf "expected the receiver's structured PeerHandler, got %A" other
        }

        testCaseAsync "CONTROL — the pre-312 three-argument constructor still composes and still answers"
        <| async {
            // GP 11 as a compile-AND-behaviour claim: the constructor
            // every existing call site uses is still there, and the
            // default policy does not cancel an ordinary call.
            let handler = new ProbeHandler(hangBudget)
            use client = new HttpClient(handler)
            let transport = HttpPeerClient(client, StubAuth(), localId) :> IPeerClient

            let! result = transport.Invoke(fastPeer, contractId, "Measure", payload)
            Expect.isTrue (Result.isOk result) "the default policy leaves an ordinary call exactly as it was"

            let fast = handler.ProbeFor "fast.test" |> Option.get
            Expect.isFalse fast.TokenFired "…and cancels nothing"
        }

        testCaseAsync "the poll leg is bounded on the same terms as the invoke leg"
        <| async {
            let handler = new ProbeHandler(hangBudget)

            let client, transport =
                transportOn
                    handler
                    (PeerTransportPolicy.defaults
                     |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromMilliseconds 200.0))

            use _client = client

            let! result = transport.PollJob(slowPeer, contractId, Guid.NewGuid())

            match result with
            | Error e -> Expect.isTrue (PeerTransportOutcome.isTimeout e) $"a poll times out too — got %A{e}"
            | Ok status -> failtestf "the hanging poll must not answer, got %A" status
        }
    ]

// ─── (2) Cancellation reaches the wire ────────────────────────────────

let cancellationTests =
    testList "Phase 312 — cancelling a peer call cancels the request, not just the wait" [

        testCaseAsync "cancelling the caller's workflow aborts the in-flight request"
        <| async {
            // `PeerTransportPolicy.unbounded` is load-bearing: with no
            // deadline in play, the only thing that can abort this
            // request is the caller's own token, so the measurement
            // below cannot be satisfied by the deadline machinery
            // instead.
            let handler = new ProbeHandler(hangBudget)
            let client, transport = transportOn handler PeerTransportPolicy.unbounded
            use _client = client
            use cts = new CancellationTokenSource()

            let running =
                Async.StartAsTask(
                    transport.Invoke(slowPeer, contractId, "Measure", payload),
                    cancellationToken = cts.Token
                )

            // Cancel only once the request is genuinely on the wire —
            // otherwise the probe could pass by cancelling before
            // anything was sent, which proves nothing about reach.
            let! slow =
                handler.Arrived "slow.test"
                |> awaited "the request never reached the stub transport"

            cts.Cancel()

            do!
                slow.Stopped
                |> awaited
                    "the hanging request never stopped — the caller's cancellation was never delivered to the socket"

            Expect.isTrue slow.TokenFired "the caller's cancellation reached the socket — the handler's own token fired"

            // …and the computation completed as CANCELLED. Not `Ok`, not
            // `Error`: a cancelled call is not an answer, and one that
            // returned a value could be counted toward a fan-out quorum
            // or folded into a round.
            let! outcome = async {
                try
                    let! r = Async.AwaitTask running
                    return Choice1Of2 r
                with ex ->
                    return Choice2Of2 ex
            }

            match outcome with
            | Choice2Of2 ex ->
                Expect.isTrue
                    (ex :? OperationCanceledException)
                    $"a cancelled call completes as cancelled, not as a transport error — got %s{ex.GetType().Name}"
            | Choice1Of2 value -> failtestf "a cancelled call must not produce a value, got %A" value
        }

        testCaseAsync "an explicitly-supplied token cancels the call without a fabricated transport error"
        <| async {
            // The new optional parameter, for a caller that HOLDS a
            // token without running under it (an ASP.NET handler's
            // `RequestAborted`). The ambient token here is untouched, so
            // only the explicit one can act.
            let handler = new ProbeHandler(hangBudget)
            let client, transport = transportOn handler PeerTransportPolicy.unbounded
            use _client = client
            use cts = new CancellationTokenSource()
            cts.Cancel()

            let running =
                Async.StartAsTask(transport.Invoke(slowPeer, contractId, "Measure", payload, cts.Token))

            let! outcome = async {
                try
                    let! r = Async.AwaitTask running
                    return Choice1Of2 r
                with ex ->
                    return Choice2Of2 ex
            }

            match outcome with
            | Choice2Of2 ex ->
                Expect.isTrue
                    (ex :? OperationCanceledException)
                    $"the explicit token cancels the call — got %s{ex.GetType().Name}"
            | Choice1Of2 value ->
                failtestf
                    "an already-cancelled token must not yield a value — an OperationCanceledException escaping as PeerTransport is the defect. Got %A"
                    value
        }
    ]

// ─── (3) The fan-out's early return now reclaims sockets ──────────────

let fanoutReachTests =
    testList "Phase 312 — a fan-out early return cancels the peers it stops awaiting" [

        testCaseAsync "firstSuccess over one fast and one hanging peer cancels the hanging request"
        <| async {
            // The headline case. `unbounded` again, so the abort can
            // only have come from `DefaultPeerFanout`'s own `cts` —
            // reaching the socket through the ambient token, which is
            // the coupling this phase makes real.
            //
            // The fast answer is held until the slow request is ON the
            // wire, because the claim is about cancelling an IN-FLIGHT
            // request: the fan-out legitimately cancels a still-queued
            // child before launch, and a fast peer that wins that launch
            // race outright leaves nothing on the socket to measure.
            let handler = new ProbeHandler(hangBudget, fastAnswersAfterArrivalOf = "slow.test")

            let client, transport = transportOn handler PeerTransportPolicy.unbounded
            use _client = client

            let fanout = PeerFanout.create ()

            let call (t: TargetPeer) =
                transport.Invoke(t, contractId, "Measure", payload)

            let! results = fanout.Fanout([ fastPeer; slowPeer ], FanoutPolicy.firstSuccess, call)

            // Unchanged surface: the map is still total over the
            // targets, and the un-awaited peer still carries a
            // descriptive error rather than vanishing.
            Expect.equal (Map.count results) 2 "the result map stays total over the targets"
            Expect.isTrue (Result.isOk results[fastPeer.Peer]) "the fast peer answered Ok"

            match results[slowPeer.Peer] with
            | Ok value -> failtestf "the cut-short peer must not report success, got %s" value
            | Error(PeerTransport message) ->
                Expect.stringContains message "not awaited" "the cut-short peer carries the not-awaited descriptor"
            | Error other -> failtestf "expected the not-awaited transport error, got %A" other

            // The measurement. The fan-out returns the instant the fast
            // peer answers, so the abort is necessarily observed after
            // the call above — await the request's own end rather than
            // asserting it happened by any particular instant.
            let! slow =
                handler.Arrived "slow.test"
                |> awaited "the hanging peer's request never reached the stub transport"

            do!
                slow.Stopped
                |> awaited
                    "the hanging peer's request never stopped — the fan-out's early-return cancellation was never delivered to the socket"

            Expect.isTrue
                slow.TokenFired
                "the early return cancelled the hanging peer's request instead of leaving its socket held for the client's default timeout"
        }

        testCaseAsync "CONTROL — FanoutPolicy.all cancels nobody and every peer answers"
        <| async {
            // Without this, "the hanging request was cancelled" would
            // pass against a fan-out that cancelled every child
            // unconditionally. A short hang so both peers genuinely
            // complete.
            let handler = new ProbeHandler(TimeSpan.FromMilliseconds 50.0)
            let client, transport = transportOn handler PeerTransportPolicy.unbounded
            use _client = client

            let fanout = PeerFanout.create ()

            let call (t: TargetPeer) =
                transport.Invoke(t, contractId, "Measure", payload)

            let! results = fanout.Fanout([ fastPeer; slowPeer ], FanoutPolicy.all, call)

            Expect.equal (Map.count results) 2 "the result map is total"
            Expect.isTrue (Result.isOk results[fastPeer.Peer]) "the fast peer answered"
            Expect.isTrue (Result.isOk results[slowPeer.Peer]) "…and so did the slow one, having been left alone"

            let slow = handler.ProbeFor "slow.test" |> Option.get
            Expect.isFalse slow.TokenFired "no early return, so nothing was cancelled"
            Expect.isTrue slow.Answered "…and the slow request completed its response"
        }
    ]