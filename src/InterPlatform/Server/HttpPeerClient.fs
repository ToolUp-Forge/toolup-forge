// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading

// ─── Layer 4 — default initiator transport ───────────────────────────
//
// `HttpPeerClient` is the default `IPeerClient`: it posts a JSON-RPC 2.0
// request to a target peer's `/peer/v1/{contractId}` route over an
// injected `HttpClient`, minting a fresh bearer token from
// `IPeerAuthProvider` on every call. The typed proxy
// (`JsonRpcPeerClient.create`) is built on top of this transport.
//
// Stateless between calls (GP 12 rule 4): every `Invoke` mints its own
// token, builds its own request, and reads its own response — no cached
// credential, so a rotated signing key flows through immediately. A
// transport-level failure (connection, timeout, non-JSON body) collapses
// to `PeerTransport`; a structured `PeerError` the receiver returned is
// reconstructed from the JSON-RPC error body's `Data` field, so the
// caller sees the same DU case the receiver raised.
//
// Phase 312 — bounded and genuinely cancellable. Every request is issued
// under a `CancellationToken` linked from three sources: the workflow's
// ambient token, the caller's optional explicit token, and the policy's
// per-call deadline. The three non-answers stay apart (see
// `PeerTransportOutcome`): a deadline expiry is a `PeerTransport`
// carrying the timeout prefix, a caller-side cancellation completes the
// computation as cancelled rather than as any value at all, and
// everything else is the peer's own failure. Before this, `SendAsync`
// took no token, so a fan-out that stopped awaiting a peer left its
// socket held for the client's default 100 s — the latency was hidden,
// the resources were not reclaimed.
//
// Phase 339 — and no request leaves here over cleartext. The URL was
// built from `target.BaseUrl` verbatim with no scheme check, so an
// `http://` peer — configured by hand or resolved from the registry —
// put a freshly-minted deployment-vouching bearer token on the path in
// the clear. The gate sits at TWO layers, mirroring the three
// `isAcceptableKeyFetchUrl` uses on the OIDC side: at the top of each
// `IPeerClient` method, where refusing happens BEFORE
// `IssuePeerToken` so no token exists to leak; and inside `send`, the
// single choke point every request passes through, so a future method
// on this transport cannot be added ungated. See
// `PeerTransportSecurity`.

/// `IPeerClient` over `HttpClient`. `localPeer` is the identity the poll
/// leg vouches for (the invoke leg takes its caller from the propagated
/// `PeerCallContext`, which the typed proxy populates). `policy` bounds
/// each call (Phase 312).
type HttpPeerClient
    (httpClient: HttpClient, auth: IPeerAuthProvider, localPeer: PeerIdentity, policy: PeerTransportPolicy) =

    let mediaType = "application/json"

    /// Complete the surrounding workflow as CANCELLED — not as a value,
    /// not as an exception. `Async.FromContinuations` is the only way to
    /// reach the cancellation continuation deliberately; re-raising the
    /// `OperationCanceledException` instead would travel the *exception*
    /// path, where `DefaultPeerFanout`'s per-peer guard would catch it
    /// and write a fabricated `Error` into the result slot of a peer
    /// nobody is waiting for — turning a cancelled call into an answer
    /// that counts toward quorum.
    static let cancelledCall (firedToken: CancellationToken) : Async<Result<string, PeerError>> =
        Async.FromContinuations(fun (_, _, ccont) -> ccont (OperationCanceledException firedToken))

    /// Reconstruct the caller-facing result string (or structured error)
    /// from a JSON-RPC response body.
    let parseResponse (body: string) : Result<string, PeerError> =
        try
            let response = JsonRpc.deserialize<JsonRpcResponse> body

            match response.Result, response.Error with
            | Some result, _ -> Ok result
            | None, Some err ->
                match err.Data with
                | Some data ->
                    try
                        Error(JsonRpc.deserialize<PeerError> data)
                    with _ ->
                        Error(PeerTransport err.Message)
                | None -> Error(PeerTransport err.Message)
            | None, None -> Error(PeerDeserialization "JSON-RPC response carried neither result nor error")
        with ex ->
            Error(PeerDeserialization ex.Message)

    let sendOverWire
        (httpMethod: HttpMethod)
        (url: string)
        (token: string)
        (jsonBody: string option)
        (callerToken: CancellationToken)
        =
        async {
            // GP 7 — the ambient token is how cancellation reaches the
            // wire without every intermediate signature carrying one.
            // `IPeerFanout` starts each peer call with `Async.Start(work,
            // cts.Token)` and `IRoundOrchestrator` dispatches under the
            // run's token, so reading it here is what makes their
            // `cts.Cancel()` abort a socket rather than merely stop
            // awaiting it.
            let! ambientToken = Async.CancellationToken

            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(ambientToken, callerToken)

            let deadline = policy.CallTimeout

            match deadline with
            | Some d when d > TimeSpan.Zero -> linked.CancelAfter d
            | _ -> ()

            /// Which side gave up. Read only after an
            /// `OperationCanceledException`, and it is the whole reason
            /// the deadline lives on its own source rather than on
            /// `HttpClient.Timeout`: with a client-level timeout both
            /// answers are the same exception and this question has no
            /// answer at all.
            let callerWentAway () =
                ambientToken.IsCancellationRequested || callerToken.IsCancellationRequested

            try
                use request = new HttpRequestMessage(httpMethod, url)
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

                match jsonBody with
                | Some body -> request.Content <- new StringContent(body, Encoding.UTF8, mediaType)
                | None -> ()

                let! response = httpClient.SendAsync(request, linked.Token) |> Async.AwaitTask
                let! body = response.Content.ReadAsStringAsync linked.Token |> Async.AwaitTask
                return parseResponse body
            with
            | :? OperationCanceledException ->
                match deadline with
                | Some d when not (callerWentAway ()) ->
                    // Nothing else on the linked source could have
                    // fired: the peer was still working when *our*
                    // deadline elapsed. A distinct, recognisable
                    // outcome — `PeerTransportOutcome.isTimeout` — and
                    // deliberately NOT a retry signal, since the peer
                    // may yet complete the call.
                    return Error(PeerTransportOutcome.timedOut d)
                | _ ->
                    // The caller went away (or there was no deadline to
                    // fire, which leaves only the caller). Cancelled is
                    // not an answer — complete as cancelled.
                    let fired =
                        if ambientToken.IsCancellationRequested then
                            ambientToken
                        else
                            callerToken

                    return! cancelledCall fired
            | ex ->
                // The peer's own failure: connection refused, DNS,
                // non-JSON body. Unchanged from pre-312.
                return Error(PeerTransport ex.Message)
        }

    /// Phase 339 — the transport's single choke point. Every request
    /// this client issues goes through here, so "no peer request leaves
    /// this transport in the clear" is a property of the transport
    /// rather than of each method remembering to call the gate — a
    /// method added later cannot be added ungated.
    ///
    /// The check runs against the FULL request URL, not just
    /// `target.BaseUrl`, so a base that passes on its own but composes
    /// into something that does not is caught too. A refused call
    /// allocates nothing further — no linked token source, no request
    /// message (GP 13).
    let send
        (httpMethod: HttpMethod)
        (url: string)
        (token: string)
        (jsonBody: string option)
        (callerToken: CancellationToken)
        =
        async {
            match PeerTransportSecurity.check policy url with
            | Error refusal -> return Error refusal
            | Ok() -> return! sendOverWire httpMethod url token jsonBody callerToken
        }

    /// The pre-312 shape, on `PeerTransportPolicy.defaults` — a
    /// secondary constructor rather than an optional argument, because
    /// F# folds an optional argument into one widened constructor and
    /// erases the narrower signature from the emitted public surface
    /// (the same discipline `DefaultRoundOrchestrator` adopted). Every
    /// existing `HttpPeerClient(client, auth, id)` call site compiles
    /// and behaves exactly as it did (GP 11).
    new(httpClient: HttpClient, auth: IPeerAuthProvider, localPeer: PeerIdentity) =
        HttpPeerClient(httpClient, auth, localPeer, PeerTransportPolicy.defaults)

    interface IPeerClient with
        member _.Invoke
            (
                target: TargetPeer,
                contractId: string,
                methodName: string,
                payload: PeerWirePayload,
                ?cancellationToken: CancellationToken
            ) =
            async {
                let callerToken = defaultArg cancellationToken CancellationToken.None

                // Phase 339 — refused BEFORE the token is minted, which
                // is the whole difference between this gate and the one
                // inside `send`. A token that is never issued cannot
                // leak, cannot be replayed, and does not consume the
                // signing key's rotation window.
                match PeerTransportSecurity.check policy target.BaseUrl with
                | Error refusal -> return Error refusal
                | Ok() ->
                    let! tokenResult = auth.IssuePeerToken(payload.Context.Peer, target.Peer, payload.Context.User)

                    match tokenResult with
                    | Error e -> return Error e
                    | Ok token ->
                        let url = $"{target.BaseUrl}/peer/v1/{contractId}"
                        let envelope = JsonRpc.request payload.Context.RootRequestId methodName payload
                        return! send HttpMethod.Post url token (Some(JsonRpc.serialize envelope)) callerToken
            }

        member _.PollJob
            (target: TargetPeer, contractId: string, jobId: PeerJobId, ?cancellationToken: CancellationToken)
            =
            async {
                let callerToken = defaultArg cancellationToken CancellationToken.None

                // Phase 339 — the poll leg mints its own token on every
                // call (GP 12 rule 4 — stateless between calls), so it
                // is gated on exactly the same terms as `Invoke`. A poll
                // loop against a cleartext peer would otherwise mint a
                // fresh token per iteration.
                match PeerTransportSecurity.check policy target.BaseUrl with
                | Error refusal -> return Error refusal
                | Ok() ->
                    let! tokenResult = auth.IssuePeerToken(localPeer, target.Peer, Anonymous)

                    match tokenResult with
                    | Error e -> return Error e
                    | Ok token ->
                        let url = $"{target.BaseUrl}/peer/v1/{contractId}/jobs/{jobId}"
                        let! parsed = send HttpMethod.Get url token None callerToken

                        return
                            match parsed with
                            | Error e -> Error e
                            | Ok statusJson ->
                                try
                                    Ok(JsonRpc.deserialize<PeerJobStatus<string>> statusJson)
                                with ex ->
                                    Error(PeerDeserialization ex.Message)
            }