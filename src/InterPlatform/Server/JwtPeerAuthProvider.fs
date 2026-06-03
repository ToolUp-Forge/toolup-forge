// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Text
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
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

/// Result computation expression for chaining the synchronous validation
/// steps (after the async secret lookup has produced the signing key).
type private PeerResultBuilder() =
    member _.Bind(m, f) =
        match m with
        | Ok x -> f x
        | Error e -> Error e

    member _.Return(x) = Ok x
    member _.ReturnFrom(m) = m
    member _.Zero() = Ok()

/// HS256 JWT helpers (BCL only — no package dependencies). Mirrors the
/// `StaticJwtAuthProvider` validator, extended with the issue-side encode
/// path and a constant-time string comparison used for delegation.
module private PeerJwt =

    let result = PeerResultBuilder()

    /// Reserved SDK-level secret scope (see `ISecretStore`).
    let platformScope = "_platform"

    /// Secret-store key holding a peer's symmetric HS256 signing key.
    let signingKeyFor (peerId: string) = $"peers/{peerId}/signing-key"

    let base64UrlEncode (bytes: byte[]) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let base64UrlDecode (input: string) =
        let s = input.Replace('-', '+').Replace('_', '/')

        let s =
            match s.Length % 4 with
            | 2 -> s + "=="
            | 3 -> s + "="
            | _ -> s

        Convert.FromBase64String(s)

    let computeHmac (secret: byte[]) (message: byte[]) =
        use hmac = new HMACSHA256(secret)
        hmac.ComputeHash(message)

    /// Constant-time comparison of two already-encoded strings (UTF-8
    /// bytes, length-checked first). Used for the delegation signature.
    let constantTimeEquals (expected: string) (actual: string) =
        let e = Encoding.UTF8.GetBytes expected
        let a = Encoding.UTF8.GetBytes actual

        e.Length = a.Length
        && CryptographicOperations.FixedTimeEquals(ReadOnlySpan e, ReadOnlySpan a)

    /// Parse a JWT into its three Base64URL-encoded parts.
    let split (token: string) =
        match token.Split('.') with
        | [| header; payload; signature |] -> Ok(header, payload, signature)
        | _ -> Error "Invalid JWT format"

    /// Refuse anything but `alg: HS256` before touching the signature —
    /// defence in depth against algorithm-confusion (`alg: none`,
    /// `alg: RS256` against an HS256 secret, etc.).
    let checkAlgorithm (header: string) =
        try
            let bytes = base64UrlDecode header
            let json = Encoding.UTF8.GetString(bytes)
            use doc = JsonDocument.Parse json

            match doc.RootElement.TryGetProperty("alg") with
            | true, algElem when algElem.ValueKind = JsonValueKind.String ->
                let alg = algElem.GetString()

                if alg = "HS256" then
                    Ok()
                else
                    Error $"Unsupported JWT algorithm '{alg}' (only HS256 is accepted)"
            | true, _ -> Error "JWT header 'alg' field is not a string"
            | false, _ -> Error "JWT header missing 'alg' field"
        with ex ->
            Error $"Failed to parse JWT header: {ex.Message}"

    /// Verify the HS256 signature in constant time.
    let verifySignature (secret: byte[]) (header: string) (payload: string) (signature: string) =
        let message = Encoding.UTF8.GetBytes($"{header}.{payload}")
        let expected = computeHmac secret message
        let actual = base64UrlDecode signature

        if
            expected.Length = actual.Length
            && CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, ReadOnlySpan actual)
        then
            Ok()
        else
            Error "Invalid signature"

    /// Decode the payload into a JsonDocument.
    let decodePayload (payload: string) =
        try
            let bytes = base64UrlDecode payload
            let json = Encoding.UTF8.GetString(bytes)
            Ok(JsonDocument.Parse(json))
        with ex ->
            Error $"Failed to decode JWT payload: {ex.Message}"

    /// Clock-skew tolerance applied to `exp` / `nbf` — second-precision,
    /// matching the JWT standard's lower bound (GP 12 rule 6).
    let clockSkewSeconds = 60L

    /// A token with no `exp` is rejected — "no expiry" is never a safe
    /// default for a bearer credential.
    let checkExpiry (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty("exp") with
        | true, expElem ->
            let exp = expElem.GetInt64()
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

            if exp + clockSkewSeconds < now then
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

            if nbf - clockSkewSeconds > now then
                Error "Token not yet valid (nbf)"
            else
                Ok()
        | false, _ -> Ok()

    /// Extract a string claim, returning None if missing.
    let getClaim (name: string) (doc: JsonDocument) =
        match doc.RootElement.TryGetProperty(name) with
        | true, elem when elem.ValueKind = JsonValueKind.String -> Some(elem.GetString())
        | _ -> None

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

        let encodedHeader = base64UrlEncode (Encoding.UTF8.GetBytes(header.ToJsonString()))

        let encodedPayload =
            base64UrlEncode (Encoding.UTF8.GetBytes(payload.ToJsonString()))

        let signingInput = $"{encodedHeader}.{encodedPayload}"

        let signature =
            base64UrlEncode (computeHmac secret (Encoding.UTF8.GetBytes signingInput))

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
type JwtPeerAuthProvider(secrets: ISecretStore) =
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

                            let validated = PeerJwt.result {
                                do! PeerJwt.checkAlgorithm header
                                do! PeerJwt.verifySignature secretBytes header payload signature
                                do! PeerJwt.checkExpiry doc
                                do! PeerJwt.checkNotBefore doc
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
                        PeerJwt.base64UrlEncode (
                            PeerJwt.computeHmac (Encoding.UTF8.GetBytes secret) (Encoding.UTF8.GetBytes canonical)
                        )

                    if PeerJwt.constantTimeEquals expected assertion.Signature then
                        return Ok()
                    else
                        return Error(PeerUnauthorized "Delegation signature verification failed")
        }