// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Layer 4 — default peer auth provider ────────────────────────────
//
// `JwtPeerAuthProvider` is the BCL-only, fail-closed default
// implementation of `IPeerAuthProvider`. It mints and validates HS256
// bearer tokens using a symmetric per-peer signing key read from
// `ISecretStore` under the reserved `_platform` scope at
// `peers/{peerId}/signing-key`. The same secret is shared (out of band)
// with the peers a deployment talks to; both sides sign / verify with it.
//
// **Fail closed.** Every validation path returns `Error (PeerUnauthorized
// …)` on any defect — malformed token, unknown issuer, missing signing
// key, `alg` other than HS256 (alg-confusion defence), bad signature,
// missing / expired `exp`, future `nbf`, empty delegation chain, or a
// delegation signature that does not verify. There is no path that
// returns a principal for an unverified credential, and no "auth
// disabled" mode. Signature comparison is constant-time
// (`CryptographicOperations.FixedTimeEquals`). The signing key is read
// from `ISecretStore` on every call (GP 12 rule 4) so a rotated key
// flows through immediately with no cached state.

/// HS256 JWT helpers. The base64url codec, HMAC, HS256 alg-check,
/// fixed-time compare, clock-skew constant and the Result CE are the
/// shared SDK primitives in `ToolUp.Platform.Base64Url` /
/// `ToolUp.Platform.JwtCrypto`; only the peer-specific glue (split,
/// payload decode, claim reads, exp/nbf, audience binding, the
/// issue-side encode path and the delegation string compare) stays here.
module private PeerJwt =

    /// Reserved SDK-level secret scope (see `ISecretStore`).
    let platformScope = "_platform"

    /// Secret-store key holding a peer's symmetric HS256 signing key.
    let signingKeyFor (peerId: string) = $"peers/{peerId}/signing-key"

    /// Constant-time comparison of two already-encoded strings (UTF-8
    /// bytes). Used for the delegation signature; delegates to the shared
    /// BCL-backed `JwtCrypto.fixedTimeEquals` (which is itself
    /// length-safe in constant time).
    let constantTimeEquals (expected: string) (actual: string) =
        JwtCrypto.fixedTimeEquals (Encoding.UTF8.GetBytes expected) (Encoding.UTF8.GetBytes actual)

    /// Parse a JWT into its three Base64URL-encoded parts.
    let split (token: string) =
        match token.Split('.') with
        | [| header; payload; signature |] -> Ok(header, payload, signature)
        | _ -> Error "Invalid JWT format"

    /// Verify the HS256 signature in constant time.
    let verifySignature (secret: byte[]) (header: string) (payload: string) (signature: string) =
        let message = Encoding.UTF8.GetBytes($"{header}.{payload}")
        let expected = JwtCrypto.computeHmac secret message
        let actual = Base64Url.decode signature

        if JwtCrypto.fixedTimeEquals expected actual then
            Ok()
        else
            Error "Invalid signature"

    /// Decode the payload into a JsonDocument.
    let decodePayload (payload: string) =
        try
            let bytes = Base64Url.decode payload
            let json = Encoding.UTF8.GetString(bytes)
            Ok(JsonDocument.Parse(json))
        with ex ->
            Error $"Failed to decode JWT payload: {ex.Message}"

    /// A token with no `exp` is rejected — "no expiry" is never a safe
    /// default for a bearer credential.
    let checkExpiry (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty("exp") with
        | true, expElem ->
            let exp = expElem.GetInt64()
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if exp + JwtCrypto.clockSkewSeconds < now then
                Error "Token expired"
            else
                Ok()
        | false, _ -> Error "Missing 'exp' claim"

    /// Absent `nbf` is fine (the claim is optional); a present future
    /// `nbf` rejects the token.
    let checkNotBefore (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty("nbf") with
        | true, nbfElem ->
            let nbf = nbfElem.GetInt64()
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if nbf - JwtCrypto.clockSkewSeconds > now then
                Error "Token not yet valid (nbf)"
            else
                Ok()
        | false, _ -> Ok()

    /// Extract a string claim, returning None if missing.
    let getClaim (name: string) (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty(name) with
        | true, elem when elem.ValueKind = JsonValueKind.String -> Some(elem.GetString())
        | _ -> None

    /// Bind the token's `aud` claim to this receiver's own peer id. When
    /// the receiver has declared an identity (`expectedAudience` non-
    /// empty — i.e. `withLocalPeer` was composed), every inbound token
    /// MUST carry an `aud` equal to it, fixed-time compared. A token with
    /// no `aud`, or one minted *for a different peer* that happens to
    /// share the issuer's signing key, is rejected — the confused-deputy
    /// / cross-receiver-replay defence the `aud` claim exists for. An
    /// `expectedAudience` of "" (no local identity composed) cannot bind
    /// audience and falls back to the pre-Phase-130 behaviour; the
    /// migration doc flags composing `LocalPeer` to activate the check.
    let checkAudience (expectedAudience: string) (doc: JsonDocument) =
        if String.IsNullOrEmpty expectedAudience then
            Ok()
        else
            match getClaim "aud" doc with
            | Some aud when constantTimeEquals expectedAudience aud -> Ok()
            | Some _ -> Error "Token audience does not match this peer"
            | None -> Error "Missing 'aud' claim"

    /// Mint a signed HS256 token. `uctx` carries the serialised
    /// `UserContext` as a string claim (round-tripped through the
    /// universal converter set so the DU survives the wire). Lifetime is
    /// five minutes — a peer token is minted per call, not cached.
    let encode (secret: byte[]) (caller: PeerIdentity) (audience: PeerIdentity) (user: UserContext) =
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

        let header = JsonObject()
        header["alg"] <- JsonValue.Create("HS256")
        header["typ"] <- JsonValue.Create("JWT")

        let payload = JsonObject()
        payload["iss"] <- JsonValue.Create(caller.PeerId)
        payload["aud"] <- JsonValue.Create(audience.PeerId)
        payload["name"] <- JsonValue.Create(caller.DisplayName)
        payload["uctx"] <- JsonValue.Create(JsonRpc.serialize user)
        payload["iat"] <- JsonValue.Create(now)
        payload["exp"] <- JsonValue.Create(now + 300L)
        payload["nbf"] <- JsonValue.Create(now)

        let encodedHeader = Base64Url.encode (Encoding.UTF8.GetBytes(header.ToJsonString()))

        let encodedPayload =
            Base64Url.encode (Encoding.UTF8.GetBytes(payload.ToJsonString()))

        let signingInput = $"{encodedHeader}.{encodedPayload}"

        let signature =
            Base64Url.encode (JwtCrypto.computeHmac secret (Encoding.UTF8.GetBytes signingInput))

        $"{signingInput}.{signature}"

    /// Canonical byte string a delegating peer signs over: the subject
    /// joined to the ordered delegation chain. Deterministic so both
    /// sides produce the same HMAC input.
    let canonicalAssertion (assertion: DelegatedAssertion) =
        let chain = String.concat ">" assertion.DelegationChain
        $"{assertion.Subject}|{chain}"

/// BCL-only, fail-closed HS256 implementation of `IPeerAuthProvider`.
/// Reads the symmetric per-peer signing key from `ISecretStore` on every
/// call; there is no cached key and no "auth disabled" path.
///
/// `expectedAudience` is this receiver's own peer id (composed via
/// `PeerServerApp.withLocalPeer`). When non-empty, `ValidatePeerToken`
/// binds every inbound token's `aud` claim to it (Phase 130 — confused-
/// deputy / cross-receiver-replay defence). The parameter is optional so
/// existing call sites that constructed `JwtPeerAuthProvider(secrets)`
/// keep compiling and keep their pre-Phase-130 behaviour (audience
/// binding off — GP 11); a receiver that never composed a `LocalPeer`
/// identity cannot bind audience.
type JwtPeerAuthProvider(secrets: ISecretStore, ?expectedAudience: string) =
    let expectedAudience = defaultArg expectedAudience ""

    interface IPeerAuthProvider with
        member _.IssuePeerToken(caller: PeerIdentity, audience: PeerIdentity, user: UserContext) = async {
            let! secretOpt = secrets.GetSecret(PeerJwt.platformScope, PeerJwt.signingKeyFor caller.PeerId)

            match secretOpt with
            | None -> return Error(PeerUnauthorized $"No signing key registered for peer '{caller.PeerId}'")
            | Some secret ->
                let token = PeerJwt.encode (Encoding.UTF8.GetBytes secret) caller audience user
                return Ok token
        }

        member _.ValidatePeerToken(token: string) = async {
            match PeerJwt.split token with
            | Error e -> return Error(PeerUnauthorized e)
            | Ok(header, payload, signature) ->
                match PeerJwt.decodePayload payload with
                | Error e -> return Error(PeerUnauthorized e)
                | Ok doc ->
                    use _ = doc

                    match PeerJwt.getClaim "iss" doc with
                    | None -> return Error(PeerUnauthorized "Missing 'iss' claim")
                    | Some iss ->
                        let! secretOpt = secrets.GetSecret(PeerJwt.platformScope, PeerJwt.signingKeyFor iss)

                        match secretOpt with
                        | None -> return Error(PeerUnauthorized $"No signing key registered for peer '{iss}'")
                        | Some secret ->
                            let secretBytes = Encoding.UTF8.GetBytes secret

                            let validated = JwtCrypto.result {
                                do! JwtCrypto.checkHs256Alg header
                                do! PeerJwt.verifySignature secretBytes header payload signature
                                do! PeerJwt.checkExpiry doc
                                do! PeerJwt.checkNotBefore doc
                                do! PeerJwt.checkAudience expectedAudience doc
                                return ()
                            }

                            match validated with
                            | Error e -> return Error(PeerUnauthorized e)
                            | Ok() ->
                                let name = PeerJwt.getClaim "name" doc |> Option.defaultValue iss

                                let user =
                                    match PeerJwt.getClaim "uctx" doc with
                                    | Some u ->
                                        try
                                            JsonRpc.deserialize<UserContext> u
                                        with _ ->
                                            Anonymous
                                    | None -> Anonymous

                                return
                                    Ok {
                                        Caller = { PeerId = iss; DisplayName = name }
                                        User = user
                                    }
        }

        member _.VerifyDelegation(assertion: DelegatedAssertion) = async {
            match List.tryLast assertion.DelegationChain with
            | None -> return Error(PeerUnauthorized "Delegation chain is empty")
            | Some delegatingPeer ->
                let! secretOpt = secrets.GetSecret(PeerJwt.platformScope, PeerJwt.signingKeyFor delegatingPeer)

                match secretOpt with
                | None ->
                    return Error(PeerUnauthorized $"No signing key registered for delegating peer '{delegatingPeer}'")
                | Some secret ->
                    let canonical = PeerJwt.canonicalAssertion assertion

                    let expected =
                        Base64Url.encode (
                            JwtCrypto.computeHmac (Encoding.UTF8.GetBytes secret) (Encoding.UTF8.GetBytes canonical)
                        )

                    if PeerJwt.constantTimeEquals expected assertion.Signature then
                        return Ok()
                    else
                        return Error(PeerUnauthorized "Delegation signature verification failed")
        }