// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.InterPlatform.PeerRemoteProfile

open System.Net.Http
open System.Net.Http.Headers
open ToolUp.Platform

// ─── Phase 343 — the outbound capability-profile fetch, fail-closed ──
//
// The handshake's `NegotiateMethod` asks a target peer for its
// `PeerProfile` (`GET /peer/v1/capabilities/profile`) and resolves the
// method's lifecycle status — `Active` / `Deprecated` / `Removed` — from
// what comes back. Phase 18d added a compatibility fallback: on ANY
// non-2xx, degrade to the bare `GET /peer/v1/capabilities` list and map
// it through `fromCapabilityList`, so a peer predating the profile route
// could still be negotiated against.
//
// **That fallback is a lifecycle-masking primitive**, and it triggers on
// a status code the answering side chooses. A receiver that wants a
// deprecation window ignored, or an on-path attacker with nothing but the
// ability to spoil one response, returns a 500 — and the caller stops
// asking about method lifecycle altogether and proceeds. The degraded
// profile carries no method list at all, so the `Deprecated` the peer
// actually declared becomes `MethodNotAdvertised`, which the negotiation
// contract documents as "fall back to contract-version negotiation" —
// i.e. call it anyway. Nothing logs a downgrade, because from the
// caller's point of view nothing failed.
//
// **So the default is now to fail closed**: a non-2xx profile response is
// a `PeerHandshakeError` naming the status, and no profile is synthesised.
// This CAN refuse a negotiation that previously succeeded — see the
// migration doc for the rollout order — and that is the point: a
// handshake that cannot read the receiver's lifecycle declarations should
// say so rather than quietly assume the most permissive answer.
//
// **Why fail-closed rather than "preserve the last-known profile".** A
// cache would need an invalidation story, and a stale cached profile is
// itself a way to miss a deprecation — it trades a fetch-time masking
// vector for a lifetime-of-the-cache one. `IPeerHandshake` is stateless
// between calls by contract (GP 12 rule 4), and this keeps it that way: a
// caller that genuinely wants to pin a negotiated result already holds it
// itself.
//
// **The pre-343 behaviour survives as an explicit opt-in**
// (`PeerServerApp.withLegacyProfileFallback`), for the one deployment
// shape it was written for: federating with a genuinely pre-18d peer that
// cannot serve the route. Naming it is the whole value — a composition
// that accepts lifecycle masking now says so in its own source, rather
// than inheriting it as a default nobody chose.

/// What a receiver does with a non-2xx response to the profile fetch.
type RemoteProfileFallback =
    /// **The default.** A non-2xx is a handshake error; no profile is
    /// synthesised, and no method's lifecycle status is assumed.
    | FailClosedProfile
    /// Pre-343 behaviour: degrade to the bare `CapabilityList` (every
    /// contract advertised, no method lifecycle at all). Opt-in, for
    /// federating with peers that predate the profile route.
    | LegacyCapabilityFallback

/// The refusal wording, single-sourced so the diagnosis a caller sees
/// names both the status that triggered it and the lever that restores
/// the old behaviour. Deliberately does NOT echo the response body: it is
/// remote-controlled text on its way into a log.
let refusalMessage (statusCode: int) =
    $"peer capability-profile fetch returned HTTP {statusCode}. Refusing to degrade to the bare capability list: that fallback drops every per-method lifecycle declaration, so a Deprecated or Removed method would be negotiated as though it were advertised — a downgrade the answering side (or anything on the path) can trigger by choosing a status code. Compose PeerServerApp.withLegacyProfileFallback to restore the pre-343 degrade for a peer that genuinely predates GET /peer/v1/capabilities/profile."

/// Decide what a non-2xx profile response means. `Some err` ⇒ fail
/// closed with this error; `None` ⇒ the caller may degrade to the bare
/// capability list. Pure and total, so the decision is unit-testable
/// without a transport and cannot differ between the code path and the
/// thing that documents it.
let nonSuccessOutcome (fallback: RemoteProfileFallback) (statusCode: int) : PeerHandshakeError option =
    match fallback with
    | LegacyCapabilityFallback -> None
    | FailClosedProfile -> Some(HandshakeRejected(refusalMessage statusCode))

/// The handshake's outbound profile fetch. Mints a per-call token
/// vouching for `localIdentity`, reads
/// `GET /peer/v1/capabilities/profile`, and applies `fallback` to a
/// non-2xx. A transport failure is `HandshakeUnreachable`; an auth
/// failure minting the token is `HandshakeRejected`.
///
/// `fetchCapabilities` is the bare `/peer/v1/capabilities` fetch, passed
/// in rather than reimplemented — it is only ever consulted on the
/// legacy-fallback path, so a fail-closed composition never issues the
/// second request at all.
///
/// Parameterised on `HttpClient` so the whole path, including the status
/// handling, is exercisable against a stub `HttpMessageHandler` — the
/// alternative is asserting on the pure decision and *hoping* the
/// transport wires it up the same way.
let fetch
    (http: HttpClient)
    (fallback: RemoteProfileFallback)
    (fetchCapabilities: TargetPeer -> Async<Result<CapabilityList, PeerHandshakeError>>)
    (auth: IPeerAuthProvider)
    (localIdentity: PeerIdentity)
    (target: TargetPeer)
    : Async<Result<PeerProfile, PeerHandshakeError>> =
    async {
        let! tokenResult = auth.IssuePeerToken(localIdentity, target.Peer, Anonymous)

        match tokenResult with
        | Error e -> return Error(HandshakeRejected(JsonRpc.errorMessage e))
        | Ok token ->
            try
                use request =
                    new HttpRequestMessage(HttpMethod.Get, $"{target.BaseUrl}/peer/v1/capabilities/profile")

                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                let! response = http.SendAsync request |> Async.AwaitTask
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok(JsonRpc.deserialize<PeerProfile> body)
                else
                    match nonSuccessOutcome fallback (int response.StatusCode) with
                    | Some refusal -> return Error refusal
                    | None ->
                        let! caps = fetchCapabilities target
                        return caps |> Result.map PeerCapabilityNegotiation.fromCapabilityList
            with ex ->
                return Error(HandshakeUnreachable ex.Message)
    }