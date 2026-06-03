// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System.Net.Http
open System.Net.Http.Headers
open System.Text

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

/// `IPeerClient` over `HttpClient`. `localPeer` is the identity the poll
/// leg vouches for (the invoke leg takes its caller from the propagated
/// `PeerCallContext`, which the typed proxy populates).
type HttpPeerClient(httpClient: HttpClient, auth: IPeerAuthProvider, localPeer: PeerIdentity) =

    let mediaType = "application/json"

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

    let send (httpMethod: HttpMethod) (url: string) (token: string) (jsonBody: string option) = async {
        try
            use request = new HttpRequestMessage(httpMethod, url)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)

            match jsonBody with
            | Some body -> request.Content <- new StringContent(body, Encoding.UTF8, mediaType)
            | None -> ()

            let! response = httpClient.SendAsync request |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return parseResponse body
        with ex ->
            return Error(PeerTransport ex.Message)
    }

    interface IPeerClient with
        member _.Invoke(target: TargetPeer, contractId: string, methodName: string, payload: PeerWirePayload) = async {
            let! tokenResult = auth.IssuePeerToken(payload.Context.Peer, target.Peer, payload.Context.User)

            match tokenResult with
            | Error e -> return Error e
            | Ok token ->
                let url = $"{target.BaseUrl}/peer/v1/{contractId}"
                let envelope = JsonRpc.request payload.Context.RootRequestId methodName payload
                return! send HttpMethod.Post url token (Some(JsonRpc.serialize envelope))
        }

        member _.PollJob(target: TargetPeer, contractId: string, jobId: PeerJobId) = async {
            let! tokenResult = auth.IssuePeerToken(localPeer, target.Peer, Anonymous)

            match tokenResult with
            | Error e -> return Error e
            | Ok token ->
                let url = $"{target.BaseUrl}/peer/v1/{contractId}/jobs/{jobId}"
                let! parsed = send HttpMethod.Get url token None

                return
                    match parsed with
                    | Error e -> Error e
                    | Ok statusJson ->
                        try
                            Ok(JsonRpc.deserialize<PeerJobStatus<string>> statusJson)
                        with ex ->
                            Error(PeerDeserialization ex.Message)
        }