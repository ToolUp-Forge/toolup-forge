module ToolUp.Platform.Tests.InProcess.PeerTransportTlsTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.Tests.Contracts

// ─── Phase 339 — peer transport TLS enforcement ──────────────────────
//
// Every outbound peer leg mints a fresh HS256 bearer from
// `IPeerAuthProvider` and puts it in an `Authorization` header. The URL
// was built from `target.BaseUrl` verbatim with no scheme check at all,
// in four places — the contract invoke, the job poll, the capability
// handshake and the profile fetch — so an `http://` peer put a token
// that vouches for the WHOLE DEPLOYMENT on the path in the clear. One
// observation is peer impersonation until the signing key rotates.
//
// The accept rule mirrors `isAcceptableKeyFetchUrl` on the OIDC side:
// **https anywhere, http to a loopback host**. Loopback is not a
// courtesy — the dev inner loop and the in-repo suites address peers as
// `http://localhost:PORT` — and everything else is refused.
//
// **Two things every refusal case here is paired with.**
//
//   1. A LEGITIMATE-PATH control (https, and loopback http) answering
//      `Ok` through the same stub, so "refused" cannot quietly mean
//      "refuses everything".
//   2. A PRE-339-POSTURE control: the SAME cleartext call under
//      `PeerTransportPolicy.allowInsecureTransport`, ADMITTED and
//      answered. That is the negative control that makes the refusal
//      attributable to the scheme rather than to anything else the
//      fixture does.
//
// And the refusal claims are MEASURED, not inferred from the `Error`
// case: the stub handler counts requests it saw and the stub auth
// provider counts tokens it minted, so "refused before a token was
// transmitted" is read off two counters rather than assumed from a DU.
// An `Error` alone would go green against a transport that had simply
// broken.

// ─── Fixtures ─────────────────────────────────────────────────────────

/// Counts what actually reached the wire, and answers everything with a
/// JSON-RPC success. Deliberately permissive: if a request arrives, the
/// call succeeds — so a case that expects a refusal fails loudly rather
/// than passing because the stub happened to error.
type private CountingHandler() =
    inherit HttpMessageHandler()

    let seen = ConcurrentBag<string>()

    member _.Urls = seen |> List.ofSeq
    member _.RequestCount = seen |> Seq.length

    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> = task {
        seen.Add(request.RequestUri.ToString())

        let body =
            if request.RequestUri.AbsolutePath.Contains "/jobs/" then
                let status: PeerJobStatus<string> = Completed "job-answer"
                JsonRpc.serialize (JsonRpc.success "root-339" status)
            else
                JsonRpc.serialize (JsonRpc.success "root-339" "wire-answer")

        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        return response
    }

/// Counts tokens minted. The whole point of gating before
/// `IssuePeerToken` is that a refused call never reaches here, so this
/// counter staying at zero is the "no token was transmitted" claim in
/// its strongest available form: none was even created.
type private CountingAuth() =
    let mutable minted = 0

    member _.Minted = minted

    interface IPeerAuthProvider with
        member _.IssuePeerToken(_, _, _) = async {
            Interlocked.Increment(&minted) |> ignore
            return Ok "stub-token"
        }

        member _.ValidatePeerToken _ = async { return Error(PeerUnauthorized "not used") }
        member _.VerifyDelegation _ = async { return Ok() }

let private peer (id: string) : PeerIdentity = { PeerId = id; DisplayName = id }

let private targetAt (url: string) : TargetPeer = {
    Peer = peer "counterparty"
    BaseUrl = url
}

let private localId = peer "local-339"
let private contractId = "probe"

let private payload: PeerWirePayload = {
    Context = {
        Peer = localId
        User = Anonymous
        ContractVersion = { Major = 1; Minor = 0 }
        Route = [ localId.PeerId ]
        RootRequestId = "root-339"
        ParentRequestId = None
        HopsRemaining = 4
    }
    Arguments = "[]"
}

/// One invoke through a real `HttpPeerClient`, reporting the result plus
/// the two counters that decide whether anything left the process.
let private invokeAt (policy: PeerTransportPolicy) (url: string) = async {
    let handler = new CountingHandler()
    use client = new HttpClient(handler)
    let auth = CountingAuth()
    let transport = HttpPeerClient(client, auth, localId, policy) :> IPeerClient

    let! result = transport.Invoke(targetAt url, contractId, "Measure", payload)
    return result, handler.RequestCount, auth.Minted
}

let private pollAt (policy: PeerTransportPolicy) (url: string) = async {
    let handler = new CountingHandler()
    use client = new HttpClient(handler)
    let auth = CountingAuth()
    let transport = HttpPeerClient(client, auth, localId, policy) :> IPeerClient

    let! result = transport.PollJob(targetAt url, contractId, Guid.NewGuid())
    return result, handler.RequestCount, auth.Minted
}

let private insecure =
    PeerTransportPolicy.allowInsecureTransport PeerTransportPolicy.defaults

// ─── (1) The accept rule itself ───────────────────────────────────────

let acceptRuleTests =
    testList "Phase 339 — which peer URLs may carry a bearer token" [

        test "https is accepted on any host, and http only on loopback" {
            let accepted = [
                "https://peer.example.com"
                "https://peer.example.com:8443"
                "https://localhost:5001"
                "http://localhost"
                "http://localhost:13001"
                "http://127.0.0.1:5000"
                "http://[::1]:5000"
            ]

            for url in accepted do
                Expect.isTrue
                    (PeerTransportSecurity.isAcceptablePeerUrl PeerTransportPolicy.defaults url)
                    $"'{url}' is safe to send a peer token to"
        }

        test "cleartext to a non-loopback host is refused, and so is anything unclassifiable" {
            let refused = [
                // The headline case: the token would cross a network.
                "http://peer.example.com"
                "http://peer.example.com:8080"
                "http://10.0.0.7:5000"
                // A hostname that merely LOOKS loopback resolves
                // wherever its owner points it.
                "http://localhost.attacker.example"
                "http://127.0.0.1.nip.io"
                // Not http at all, and not something the substrate can
                // make any promise about.
                "ftp://peer.example.com"
                "peer.example.com"
                "loopback"
                ""
            ]

            for url in refused do
                Expect.isFalse
                    (PeerTransportSecurity.isAcceptablePeerUrl PeerTransportPolicy.defaults url)
                    $"'{url}' must not carry a peer token"
        }

        test "CONTROL — the pre-339 posture accepts every one of them" {
            // The negative control for the whole rule. Without it, the
            // list above would pass just as happily against a predicate
            // that returned `false` unconditionally.
            let all = [
                "http://peer.example.com"
                "ftp://peer.example.com"
                "loopback"
                ""
                "https://peer.example.com"
            ]

            for url in all do
                Expect.isTrue
                    (PeerTransportSecurity.isAcceptablePeerUrl insecure url)
                    $"'{url}' is admitted under the opt-out — the pre-339 behaviour, verbatim"
        }

        test "the refusal is classifiable, and is not confusable with a timeout" {
            let refusal = PeerTransportSecurity.refused "http://peer.example.com"

            Expect.isTrue (PeerTransportSecurity.isRefusal refusal) "a cleartext refusal classifies as one"

            Expect.isFalse
                (PeerTransportOutcome.isTimeout refusal)
                "…and is not mistaken for a Phase 312 deadline expiry"

            let timeout = PeerTransportOutcome.timedOut (TimeSpan.FromSeconds 5.0)

            Expect.isFalse (PeerTransportSecurity.isRefusal timeout) "…nor a deadline expiry for a cleartext refusal"

            Expect.isFalse
                (PeerTransportSecurity.isRefusal (PeerUnauthorized "nope"))
                "…and a non-transport error is neither"
        }

        test "the default policy enforces; only the opt-out relaxes it" {
            Expect.isFalse
                PeerTransportPolicy.defaults.AllowInsecureTransport
                "enforcement is the default — a deployment that composes nothing is protected"

            Expect.isFalse
                PeerTransportPolicy.unbounded.AllowInsecureTransport
                "…and dropping the deadline does not silently drop the scheme check with it"

            Expect.isTrue insecure.AllowInsecureTransport "the opt-out is the only way to turn it off"
        }
    ]

// ─── (2) The outbound transport ───────────────────────────────────────

let transportTests =
    testList "Phase 339 — HttpPeerClient refuses cleartext before it mints a token" [

        testCaseAsync "an http:// peer is refused with nothing minted and nothing sent"
        <| async {
            let! result, requests, minted = invokeAt PeerTransportPolicy.defaults "http://peer.example.com"

            match result with
            | Ok value -> failtestf "a cleartext peer must not be called, got %s" value
            | Error e ->
                Expect.isTrue (PeerTransportSecurity.isRefusal e) $"the refusal is the cleartext one — got %A{e}"

            // The two measurements. `Error` alone would be satisfied by
            // a transport that had simply broken.
            Expect.equal requests 0 "no request reached the wire"
            Expect.equal minted 0 "…and no bearer token was ever minted, so none could have leaked"
        }

        testCaseAsync "CONTROL — the SAME call under the pre-339 posture is admitted and answered"
        <| async {
            // The load-bearing control: identical URL, identical
            // fixture, only the policy differs. If this were red too,
            // the case above would be measuring the fixture rather than
            // the enforcement.
            let! result, requests, minted = invokeAt insecure "http://peer.example.com"

            match result with
            | Ok json -> Expect.equal (JsonRpc.deserialize<string> json) "wire-answer" "the cleartext call round-trips"
            | Error e -> failtestf "the opt-out must restore the pre-339 behaviour exactly, got %A" e

            Expect.equal requests 1 "the request reached the wire"
            Expect.equal minted 1 "…carrying a minted token, exactly as before this phase"
        }

        testCaseAsync "CONTROL — an https:// peer is unchanged"
        <| async {
            let! result, requests, minted = invokeAt PeerTransportPolicy.defaults "https://peer.example.com"

            match result with
            | Ok json -> Expect.equal (JsonRpc.deserialize<string> json) "wire-answer" "the https call round-trips"
            | Error e -> failtestf "an https peer must be unaffected (GP 11), got %A" e

            Expect.equal requests 1 "the request reached the wire"
            Expect.equal minted 1 "…with its token minted as usual"
        }

        testCaseAsync "CONTROL — a loopback http:// peer still works, with no opt-out composed"
        <| async {
            // The dev / inner-loop story. This is why the accept rule is
            // https-or-loopback rather than https-only: breaking
            // `http://localhost` would have made the default posture
            // unusable locally, which is how a security default gets
            // turned off wholesale.
            let! result, requests, minted = invokeAt PeerTransportPolicy.defaults "http://localhost:13001"

            match result with
            | Ok json -> Expect.equal (JsonRpc.deserialize<string> json) "wire-answer" "the loopback call round-trips"
            | Error e -> failtestf "a loopback dev peer must not need an opt-out, got %A" e

            Expect.equal requests 1 "the loopback request reached the wire"
            Expect.equal minted 1 "…with its token minted as usual"
        }

        testCaseAsync "the poll leg is gated on the same terms"
        <| async {
            // A poll LOOP against a cleartext peer would mint a fresh
            // token per iteration, so leaving this leg ungated would be
            // worse than leaving the invoke leg ungated.
            let! result, requests, minted = pollAt PeerTransportPolicy.defaults "http://peer.example.com"

            match result with
            | Ok status -> failtestf "a cleartext poll must not be issued, got %A" status
            | Error e -> Expect.isTrue (PeerTransportSecurity.isRefusal e) $"the poll refuses too — got %A{e}"

            Expect.equal requests 0 "no poll reached the wire"
            Expect.equal minted 0 "…and no token was minted for it"
        }

        testCaseAsync "CONTROL — the poll leg answers over https"
        <| async {
            let! result, requests, _ = pollAt PeerTransportPolicy.defaults "https://peer.example.com"

            match result with
            | Ok(Completed value) -> Expect.equal value "job-answer" "the https poll round-trips"
            | other -> failtestf "the https poll must answer, got %A" other

            Expect.equal requests 1 "the poll reached the wire"
        }

        testCaseAsync "the pre-312 three-argument constructor enforces too"
        <| async {
            // `HttpPeerClient(client, auth, id)` runs on
            // `PeerTransportPolicy.defaults`, so the constructor every
            // pre-312 call site uses picks the enforcement up without
            // any edit — which is the point of putting the posture on
            // the policy record rather than on a new parameter.
            let handler = new CountingHandler()
            use client = new HttpClient(handler)
            let auth = CountingAuth()
            let transport = HttpPeerClient(client, auth, localId) :> IPeerClient

            let! result = transport.Invoke(targetAt "http://peer.example.com", contractId, "Measure", payload)

            Expect.isTrue
                (match result with
                 | Error e -> PeerTransportSecurity.isRefusal e
                 | Ok _ -> false)
                "the narrow constructor gets the default posture"

            Expect.equal handler.RequestCount 0 "and sends nothing"
            Expect.equal auth.Minted 0 "and mints nothing"
        }
    ]

// ─── (3) The handshake's profile fetch ────────────────────────────────

let handshakeFetchTests =
    testList "Phase 339 — the handshake fetches are gated on the same rule" [

        testCaseAsync "the capability-profile fetch refuses a cleartext peer before minting"
        <| async {
            // Usually the FIRST call made to a newly configured peer, so
            // it is where a cleartext `BaseUrl` is most likely to be
            // noticed — and it carries the same deployment-vouching
            // bearer the contract transport does.
            let handler = new CountingHandler()
            use http = new HttpClient(handler)
            let auth = CountingAuth()

            let fetchCapabilities (_: TargetPeer) = async { return Ok([]: CapabilityList) }

            let! result =
                PeerRemoteProfile.fetch
                    http
                    PeerTransportPolicy.defaults
                    PeerRemoteProfile.FailClosedProfile
                    fetchCapabilities
                    (auth :> IPeerAuthProvider)
                    localId
                    (targetAt "http://peer.example.com")

            match result with
            | Error(HandshakeRejected message) ->
                Expect.stringContains
                    message
                    PeerTransportSecurity.RefusalPrefix
                    "the handshake reports the cleartext refusal, not a generic rejection"
            | other -> failtestf "expected a HandshakeRejected carrying the cleartext refusal, got %A" other

            Expect.equal handler.RequestCount 0 "no profile request reached the wire"
            Expect.equal auth.Minted 0 "…and no token was minted for it"
        }

        testCaseAsync "CONTROL — the same fetch over https reaches the wire"
        <| async {
            // Without this, "the profile fetch refused" would pass
            // against a fetch that had stopped issuing requests at all.
            let handler = new CountingHandler()
            use http = new HttpClient(handler)
            let auth = CountingAuth()

            let fetchCapabilities (_: TargetPeer) = async { return Ok([]: CapabilityList) }

            let! _ =
                PeerRemoteProfile.fetch
                    http
                    PeerTransportPolicy.defaults
                    PeerRemoteProfile.FailClosedProfile
                    fetchCapabilities
                    (auth :> IPeerAuthProvider)
                    localId
                    (targetAt "https://peer.example.com")

            Expect.equal handler.RequestCount 1 "the https profile fetch was issued"
            Expect.equal auth.Minted 1 "…with a minted token"
        }

        testCaseAsync "CONTROL — the pre-339 posture admits the cleartext profile fetch"
        <| async {
            let handler = new CountingHandler()
            use http = new HttpClient(handler)
            let auth = CountingAuth()

            let fetchCapabilities (_: TargetPeer) = async { return Ok([]: CapabilityList) }

            let! _ =
                PeerRemoteProfile.fetch
                    http
                    insecure
                    PeerRemoteProfile.FailClosedProfile
                    fetchCapabilities
                    (auth :> IPeerAuthProvider)
                    localId
                    (targetAt "http://peer.example.com")

            Expect.equal handler.RequestCount 1 "the opt-out restores the pre-339 fetch exactly"
            Expect.equal auth.Minted 1 "…including minting its token"
        }
    ]

// ─── (4) The peer directory ───────────────────────────────────────────

let registryTests =
    testList "Phase 339 — the peer directory will not record a cleartext peer" [

        testCaseAsync "Register refuses a non-loopback http:// BaseUrl"
        <| async {
            // The registry is the main way a `TargetPeer` reaches the
            // transport WITHOUT passing through a composition, so
            // refusing here turns "every call to this peer will fail"
            // into "this entry was never accepted", at the moment an
            // admin surface can still say so.
            let blobs =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let registry = BlobPeerRegistry(blobs) :> IPeerRegistry

            let! result = registry.Register(targetAt "http://peer.example.com")

            match result with
            | Ok() -> failtest "a cleartext peer must not be recorded"
            | Error e -> Expect.isTrue (PeerTransportSecurity.isRefusal e) $"the registry refuses it — got %A{e}"

            let! listed = registry.List()
            Expect.isEmpty listed "…and nothing was written"
        }

        testCaseAsync "CONTROL — https and loopback peers register, and resolve back"
        <| async {
            let blobs =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let registry = BlobPeerRegistry(blobs) :> IPeerRegistry

            let secure = {
                Peer = peer "secure-peer"
                BaseUrl = "https://peer.example.com"
            }

            let local = {
                Peer = peer "local-peer"
                BaseUrl = "http://localhost:13001"
            }

            let! first = registry.Register secure
            Expect.isTrue (Result.isOk first) "an https peer registers"

            let! second = registry.Register local
            Expect.isTrue (Result.isOk second) "…and so does a loopback dev peer, with no opt-out"

            let! resolved = registry.Resolve "secure-peer"

            Expect.equal
                (resolved |> Option.map _.BaseUrl)
                (Some "https://peer.example.com")
                "…and resolves back intact"

            let! listed = registry.List()
            Expect.equal (List.length listed) 2 "both entries are in the directory"
        }

        testCaseAsync "CONTROL — the pre-339 posture records the cleartext peer, and reads stay ungated"
        <| async {
            // Two claims in one, and the second is the compatibility
            // story: a directory written before this phase must still
            // RESOLVE, because an entry that vanished would be far
            // harder to diagnose than a call that refuses and names the
            // URL. So the read path is deliberately not gated — proved
            // here by resolving, through a DEFAULT-policy registry, an
            // entry only the opt-out could have written.
            let blobs =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let permissive = BlobPeerRegistry(blobs, insecure) :> IPeerRegistry
            let legacyEntry = targetAt "http://peer.example.com"

            let! written = permissive.Register legacyEntry
            Expect.isTrue (Result.isOk written) "the opt-out restores the pre-339 write"

            let enforcing = BlobPeerRegistry(blobs) :> IPeerRegistry
            let! resolved = enforcing.Resolve "counterparty"

            Expect.equal
                (resolved |> Option.map _.BaseUrl)
                (Some "http://peer.example.com")
                "an enforcing registry still READS a pre-339 entry — the refusal belongs to the call, not the lookup"
        }
    ]

// ─── (5) The composition seam ─────────────────────────────────────────

let composeTests =
    testList "Phase 339 — the composition's transport posture" [

        test "a default composition enforces, and withInsecurePeerTransport opts out" {
            let app = PeerServerApp.create ()

            Expect.isFalse
                app.TransportPolicy.AllowInsecureTransport
                "PeerServerApp.create () enforces — the posture is not something to remember to switch on"

            let opted = app |> PeerServerApp.withInsecurePeerTransport

            Expect.isTrue opted.TransportPolicy.AllowInsecureTransport "…and the opt-out is a named, greppable act"

            Expect.equal
                opted.TransportPolicy.CallTimeout
                app.TransportPolicy.CallTimeout
                "…which changes nothing else about the transport policy"
        }

        test "withTransportPolicy REPLACES the record, opt-out included" {
            // The documented ordering hazard, pinned rather than left to
            // a doc comment: a composition that opts out and then sets a
            // deadline has silently re-enabled enforcement.
            let clobbered =
                PeerServerApp.create ()
                |> PeerServerApp.withInsecurePeerTransport
                |> PeerServerApp.withTransportPolicy (
                    PeerTransportPolicy.defaults
                    |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromSeconds 5.0)
                )

            Expect.isFalse
                clobbered.TransportPolicy.AllowInsecureTransport
                "a later withTransportPolicy discards the opt-out — compose the policy whole instead"

            let kept =
                PeerServerApp.create ()
                |> PeerServerApp.withTransportPolicy (
                    PeerTransportPolicy.defaults
                    |> PeerTransportPolicy.withCallTimeout (TimeSpan.FromSeconds 5.0)
                )
                |> PeerServerApp.withInsecurePeerTransport

            Expect.isTrue clobbered.TransportPolicy.CallTimeout.IsSome "…the deadline it set is in force"
            Expect.isTrue kept.TransportPolicy.AllowInsecureTransport "…and the documented order keeps both"
            Expect.equal kept.TransportPolicy.CallTimeout (Some(TimeSpan.FromSeconds 5.0)) "…both, together"
        }

        test "the advisory names the posture and the lever that drops it" {
            let advisory = PeerServerApp.insecureTransportAdvisory

            Expect.stringContains advisory "AllowInsecureTransport" "the advisory names the setting"
            Expect.stringContains advisory "withInsecurePeerTransport" "…and the compose call that set it"
            Expect.stringContains advisory "localhost" "…and states that loopback needs no opt-out"
        }

        test "the refusal message names the URL, the risk and the lever" {
            let message = PeerTransportSecurity.refusalMessage "http://peer.example.com"

            Expect.stringContains message "http://peer.example.com" "an operator can see WHICH peer to fix"
            Expect.stringContains message "https" "…what to change it to"
            Expect.stringContains message "localhost" "…that a local dev peer needs no change"
            Expect.stringContains message "allowInsecureTransport" "…and the opt-out, if the path is trusted otherwise"
        }
    ]