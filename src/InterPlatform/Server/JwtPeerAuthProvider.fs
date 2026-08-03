// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.Auth
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
//
// **Replay defence + call scoping (Phase 338).** Every minted token now
// carries a `jti` nonce. When a `PeerTokenPolicy` supplies an
// `IPeerReplayGuard`, `ValidatePeerToken` claims that nonce *after* the
// signature, `exp`, `nbf` and `aud` have all checked out and refuses a
// nonce that was already spent inside the freshness window — so a
// captured token is single-use rather than a bearer capability for its
// whole 300 s + skew lifetime. Under `ContractBoundCalls` a token may
// additionally carry a `cid` claim naming the one contract it may be
// spent against (`IPeerCallScopedAuth`). Both are off by default: the
// default `PeerTokenPolicy.unscoped` validates exactly as before
// (GP 11) and consults no store at all (GP 13).
//
// **Identity sanitisation (Phase 334).** The peer id that selects the
// signing key is a path segment of the `ISecretStore` key, and on both
// validation paths it is wholly unverified at that moment — the token's
// self-asserted `iss`, and the last hop of a wire-supplied delegation
// chain. Both now go through the platform `IdentitySanitiser` charset
// policy BEFORE the key is built, the same one `PeerBearerAuthMiddleware`
// applies to `X-Peer-Name` and the Entra decorator applies to
// `oid` / `sub` / `tid`. The ISSUE path is deliberately unguarded: its
// caller id comes from the deployment's own composition, not the wire,
// and refusing to mint would break a deployment whose local id is odd
// but whose peers never see it (GP 11). See `PeerJwt.isWellFormedPeerId`.
//
// **Symmetric, and that is a topology decision (Phase 343).** One shared
// secret per peer means a single receiver compromise leaks every trusted
// peer's key, and any key-holder can mint tokens AS that peer — fine for
// the strict 1:1 pairing this provider was written for, a poor trade for
// a hub with many spokes. `AsymmetricPeerAuthProvider` (the sibling file)
// is the GP 1 companion for that shape: the receiver holds only public
// keys, so compromising it forges nothing. This provider stays the
// default and is unchanged by that phase (GP 11).
//
// **Claim order is load-bearing.** The replay claim is the LAST check.
// Claiming before the signature verifies would let an unauthenticated
// attacker burn seen-set entries with forged tokens — turning the
// defence into the denial-of-service vector it exists to prevent.
//
// **Asserted end-user context (Phase 330).** The `uctx` claim rides
// inside the caller's own signed payload, so the outer signature proves
// the *calling peer* sent it and nothing more. Two consequences the
// provider owns:
//
//   * An unreadable `uctx` is now an explicit `PeerUnauthorized` rather
//     than a silent downgrade to `Anonymous`. A tampered or
//     shape-mismatched claim is exactly the case a downgrade hides, and
//     "anonymous caller" is not a safe reading of "I could not parse
//     what this peer asserted". An ABSENT claim is still `Anonymous` —
//     nothing was asserted, so nothing is being ignored (GP 11).
//   * A `Delegated` assertion is returned to the host **unverified** —
//     `VerifyDelegation` is a separate member precisely so the provider
//     stays a stateless, policy-free validator (GP 12 rule 4). The host
//     seam (`JsonRpcPeerHost`) is what MUST call it before acting on a
//     delegated originator; see the contract note on `IPeerAuthProvider`.

/// HS256 JWT helpers. The base64url codec, HMAC, HS256 alg-check,
/// fixed-time compare, clock-skew constant and the Result CE are the
/// shared SDK primitives in `ToolUp.Platform.Base64Url` /
/// `ToolUp.Platform.JwtCrypto`; only the peer-specific glue (split,
/// payload decode, claim reads, exp/nbf, audience binding, the
/// issue-side encode path and the delegation string compare) stays here.
///
/// **`internal`, not `private`, since Phase 343.** The asymmetric
/// provider (`AsymmetricPeerAuthProvider`) differs from this one in
/// exactly three places — which secret it reads, which `alg` it accepts,
/// and how it checks the signature — and in nothing else. Everything
/// after the signature (`exp` / `nbf` / `aud` / `cid` / `uctx` / the
/// replay claim, in that order) is the same decision made about the same
/// claim set, so it is shared rather than copied: a check added here is
/// automatically run by both providers, and the two cannot drift into
/// disagreeing about what a valid peer token is. Assembly-internal only
/// — none of this reaches the public surface.
module internal PeerJwt =

    /// Reserved SDK-level secret scope (see `ISecretStore`).
    let platformScope = "_platform"

    /// Secret-store key holding a peer's symmetric HS256 signing key.
    let signingKeyFor (peerId: string) = $"peers/{peerId}/signing-key"

    /// Phase 334 — a peer id is a PATH SEGMENT of the `ISecretStore` key
    /// `signingKeyFor` builds, and on the *validation* paths it arrives
    /// wholly unverified: the token's own self-asserted `iss`, and the
    /// last hop of a wire-supplied delegation chain. It is unverified by
    /// construction, because the key that would verify it is precisely
    /// what the id is being used to look up.
    ///
    /// So the shape is checked before the interpolation, using the same
    /// platform `IdentitySanitiser` charset policy
    /// `PeerBearerAuthMiddleware` applies to `X-Peer-Name` (Phase 137)
    /// and the Entra decorator applies to `oid` / `sub` / `tid`
    /// (Phase 334) — one sanitiser, one rule set, so the three federated
    /// identity boundaries cannot drift apart. A traversal-shaped, NUL-
    /// bearing, control-character or Windows-reserved id can no longer
    /// address a key outside the peer key space of an `ISecretStore`
    /// companion that maps keys onto a filesystem or URL path.
    ///
    /// The sanitiser returns valid input unchanged, so a well-formed
    /// peer id resolves byte-for-byte the same key it always did
    /// (GP 11), and a deployment that never receives a malformed id
    /// cannot tell this guard is here (GP 13).
    let isWellFormedPeerId (peerId: string) : bool =
        IdentitySanitiser.sanitiseScopeId peerId |> Result.isOk

    /// Phase 334 — the single wording for a rejected issuer. Deliberately
    /// does NOT echo the offending value: it flows into logs and audit,
    /// and an attacker-controlled string does not belong there (the same
    /// reason `IdentitySanitiser` categorises rather than quotes).
    let malformedIssuerMessage =
        "Rejected 'iss' claim — a peer id must be alphanumerics, '-', '_' or '.' (no leading period, no path separator, no control character) before it is used to address a signing key"

    /// Phase 334 — the delegation-chain counterpart of
    /// `malformedIssuerMessage`. Same policy, same non-echoing posture.
    let malformedDelegatingPeerMessage =
        "Rejected delegating peer — a peer id must be alphanumerics, '-', '_' or '.' (no leading period, no path separator, no control character) before it is used to address a signing key"

    /// Minimum per-peer signing-key length, in bytes of its UTF-8 encoding.
    /// An HMAC-SHA256 key below the 32-byte SHA-256 block offers less than
    /// the full keyspace, and a blank key is publicly computable — a peer
    /// whose stored key is `""` (or a placeholder a few bytes long) would
    /// otherwise mint / accept a valid-but-public MAC. Mirrored — not
    /// shared — with the identical guard in `ToolUp.Stripe.Webhook` and
    /// `ToolUp.Stripe.TierToken`; the packages are deliberately decoupled.
    [<Literal>]
    let minSigningKeyBytes = 32

    /// A per-peer signing key must be present and at least
    /// `minSigningKeyBytes` bytes. Applied to the per-call `GetSecret`
    /// read on every issue / validate / delegation path so blank or
    /// too-short signing material fails closed (GP 4) rather than
    /// producing a valid-but-public HMAC.
    let signingKeyIsStrong (secret: string) : bool =
        not (String.IsNullOrEmpty secret)
        && Encoding.UTF8.GetByteCount secret >= minSigningKeyBytes

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
    ///
    /// **Phase 343 — the signature segment is decoded with `tryDecode`.**
    /// It is attacker-controlled and arrives before anything has
    /// authenticated it, and `Base64Url.decode` throws `FormatException`
    /// on a segment that is not valid base64url. Nothing between here and
    /// the Giraffe handler caught that: `JwtCrypto.result` is a `Result`
    /// computation expression, not an exception boundary, so the throw
    /// escaped `ValidatePeerToken` and the host answered **500** where
    /// every other bad credential answers 401. Fail-closed either way —
    /// but a status-code difference an unauthenticated caller can trigger
    /// at will is a free oracle separating "malformed encoding" from
    /// "wrong key", and it is removed by returning the SAME
    /// `"Invalid signature"` a well-formed-but-wrong signature returns.
    ///
    /// The HMAC is still computed first, before the decode is inspected,
    /// so the work done is unchanged by whether the segment parses.
    let verifySignature (secret: byte[]) (header: string) (payload: string) (signature: string) =
        let message = Encoding.UTF8.GetBytes($"{header}.{payload}")
        let expected = JwtCrypto.computeHmac secret message

        match Base64Url.tryDecode signature with
        | Some actual when JwtCrypto.fixedTimeEquals expected actual -> Ok()
        | _ -> Error "Invalid signature"

    /// Phase 343 — the algorithm gate for a provider whose `alg` is not
    /// HS256. `JwtCrypto.checkHs256Alg` is deliberately hard-wired to the
    /// symmetric algorithm (it *is* the alg-confusion defence for the
    /// symmetric validators); an asymmetric provider needs the same shape
    /// against its own single accepted algorithm. Same posture: exactly
    /// one algorithm is accepted, named by the caller, a header that will
    /// not parse is an `Error` rather than an exception, and the
    /// offending value is not echoed back (it is attacker-controlled and
    /// flows into logs).
    let checkAlg (expected: string) (header: string) : Result<unit, string> =
        try
            use doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.decode header))

            match doc.RootElement.TryGetProperty "alg" with
            | true, algElem when algElem.ValueKind = JsonValueKind.String ->
                if algElem.GetString() = expected then
                    Ok()
                else
                    Error $"Unsupported JWT algorithm (this receiver accepts only {expected})"
            | true, _ -> Error "JWT header 'alg' field is not a string"
            | false, _ -> Error "JWT header missing 'alg' field"
        with ex ->
            Error $"Failed to parse JWT header: {ex.Message}"

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
    ///
    /// **Phase 309 — an empty `expectedAudience` is a deliberately weaker
    /// posture, not a neutral one.** Because `ValidatePeerToken` finds the
    /// signing key by the token's own `iss`, a token peer X minted for
    /// receiver Y is accepted verbatim by any other receiver Z that also
    /// trusts X — unless Z binds `aud = Z`. So the empty case is exactly
    /// the confused-deputy exposure the claim exists to close, and it is
    /// preserved here only for compatibility (GP 11). This function stays
    /// policy-free: it is handed an expected audience and cannot tell a
    /// host-only deployment's deliberate choice from a composition
    /// oversight. Deciding which it is needs the hosted-contract set, so
    /// the enforcement lives at the composition seam —
    /// `PeerCompose.PeerServerApp.enforceAudienceBinding`, which warns at
    /// startup and, under `withStrictAudienceBinding`, refuses to start.
    let checkAudience (expectedAudience: string) (doc: JsonDocument) =
        if String.IsNullOrEmpty expectedAudience then
            Ok()
        else
            match getClaim "aud" doc with
            | Some aud when constantTimeEquals expectedAudience aud -> Ok()
            | Some _ -> Error "Token audience does not match this peer"
            | None -> Error "Missing 'aud' claim"

    /// Phase 338 — the single wording for "replay defence is on and this
    /// token has no usable nonce", so an absent claim and a blank one
    /// cannot disagree about why they were refused.
    let missingNonceMessage =
        "Missing 'jti' claim — this receiver enforces single-use peer tokens, and a token with no nonce cannot be de-duplicated"

    /// Phase 338 — the instant a validated token stops being accepted:
    /// its `exp` plus the same clock skew `checkExpiry` allows. Past
    /// that, a replayed nonce is refused by `exp` anyway, so the
    /// seen-set entry carries no information and may be reclaimed.
    /// `None` when `exp` is absent or unreadable — unreachable in the
    /// validation path (`checkExpiry` runs first and rejects both), and
    /// treated as a refusal rather than an unbounded claim if it ever is.
    let freshnessDeadline (doc: JsonDocument) : DateTimeOffset option =
        match doc.RootElement.TryGetProperty("exp") with
        | true, expElem ->
            try
                Some(DateTimeOffset.FromUnixTimeSeconds(expElem.GetInt64() + JwtCrypto.clockSkewSeconds))
            with _ ->
                None
        | false, _ -> None

    /// Phase 338 — bind the token to the contract it is being spent
    /// against. Under `UnscopedCalls` the `cid` claim is neither minted
    /// nor examined, so this is `Ok` unconditionally and the pre-338
    /// path is byte-for-byte preserved (GP 11).
    ///
    /// Under `ContractBoundCalls`, `expected` is `Some` on the scoped
    /// validation path and `None` on the plain `ValidatePeerToken` one.
    /// A `cid`-carrying token arriving on the plain path is REFUSED
    /// rather than waved through: this receiver cannot see which
    /// contract the call is for, so it cannot honour a binding the
    /// issuer asked for, and accepting anyway would make the claim
    /// decorative. A token with no `cid` was never claimed to be bound,
    /// so nothing is being ignored and it proceeds on the other checks.
    /// Compared in constant time, matching `checkAudience`.
    let checkContractScope (scope: PeerCallScope) (expected: string option) (doc: JsonDocument) =
        match scope with
        | UnscopedCalls -> Ok()
        | ContractBoundCalls ->
            match expected, getClaim "cid" doc with
            | Some contractId, Some cid when constantTimeEquals contractId cid -> Ok()
            | Some _, Some _ -> Error "Token is bound to a different contract"
            | Some _, None ->
                Error "Missing 'cid' claim — this receiver binds peer tokens to the contract they are spent against"
            | None, Some _ ->
                Error
                    "Contract-bound token presented on an unscoped validation path — validate it through IPeerCallScopedAuth.ValidateScopedPeerToken"
            | None, None -> Ok()

    /// Phase 330 — the single wording for "this token asserts an end-user
    /// context that cannot be read", so the two malformed shapes (a
    /// non-string claim, and a string that will not deserialise) cannot
    /// disagree about why they were refused.
    let malformedUserContextMessage =
        "Malformed 'uctx' claim — the asserted end-user context did not deserialise, and this receiver refuses rather than downgrading a tampered claim to anonymous"

    /// Phase 330 — read the `uctx` claim into the `UserContext` the
    /// receiver will act on.
    ///
    /// An **absent** claim is `Anonymous`. A token that never asserted an
    /// end user is not asserting a tampered one, and a plain
    /// deployment-to-deployment token legitimately carries no `uctx` at
    /// all — so this path is byte-for-byte the pre-330 behaviour (GP 11).
    ///
    /// A **present but unreadable** claim is an explicit reject. It used
    /// to degrade silently to `Anonymous`, which is the wrong answer in
    /// both directions: the claim sits inside the signed payload, so an
    /// unreadable value means either the two ends disagree about the wire
    /// shape or the payload was hand-built — and continuing as "anonymous
    /// caller" lets the call proceed under an identity nobody asserted,
    /// while hiding the disagreement that produced it. Both malformed
    /// shapes are covered: a `uctx` that is not a JSON string at all, and
    /// a string whose contents will not round-trip back to a
    /// `UserContext`.
    let readUserContext (doc: JsonDocument) : Result<UserContext, string> =
        match doc.RootElement.TryGetProperty "uctx" with
        | false, _ -> Ok Anonymous
        | true, elem when elem.ValueKind <> JsonValueKind.String -> Error malformedUserContextMessage
        | true, elem ->
            try
                let parsed = JsonRpc.deserialize<UserContext> (elem.GetString())

                // A converter that answers `null` rather than throwing
                // would otherwise hand a null DU to the dispatch path,
                // where it reads as neither `Anonymous` nor a case.
                if obj.ReferenceEquals(parsed, null) then
                    Error malformedUserContextMessage
                else
                    Ok parsed
            with _ ->
                Error malformedUserContextMessage

    /// Phase 338 — claim the token's `jti` against the replay guard.
    /// Runs only once every other check has passed (see the claim-order
    /// note in this file's header). Without a guard composed this is
    /// `Ok` without touching the token at all, so the `jti` claim is
    /// inert for a deployment that has not opted in.
    ///
    /// Phase 343 — lifted out of `JwtPeerAuthProvider` so the asymmetric
    /// provider makes the identical claim, in the identical order.
    let claimNonce (policy: PeerTokenPolicy) (doc: JsonDocument) : Async<Result<unit, string>> = async {
        match policy.ReplayGuard with
        | None -> return Ok()
        | Some guard ->
            match getClaim "jti" doc with
            | None -> return Error missingNonceMessage
            | Some jti when String.IsNullOrWhiteSpace jti -> return Error missingNonceMessage
            | Some jti ->
                match freshnessDeadline doc with
                | None -> return Error "Token carries no usable 'exp' claim to bound its replay-guard entry"
                | Some deadline ->
                    let! verdict = guard.ClaimTokenId(jti, deadline)

                    match verdict with
                    | ReplayFirstUse -> return Ok()
                    | ReplayDetected ->
                        return
                            Error "Peer token replayed — its 'jti' has already been spent inside the freshness window"
                    | ReplayGuardUnavailable reason ->
                        // Fail CLOSED. A guard that cannot see its state has
                        // no basis for calling a token fresh, and answering
                        // "not seen" here would silently restore the pre-338
                        // posture exactly when it is under attack.
                        return
                            Error
                                $"Replay guard unavailable ({reason}) — refusing the call rather than accepting an unchecked token"
    }

    /// Phase 343 — everything a validated peer token is checked for
    /// *after* its signature: the time and binding claims, the asserted
    /// end-user context, the replay claim, and the principal that comes
    /// out the far side. Shared verbatim by the HS256 and asymmetric
    /// providers, which differ only in how they establish the signature.
    ///
    /// **The order is the contract, not an implementation detail.** The
    /// `uctx` read precedes the nonce claim so a token about to be
    /// rejected on its payload shape does not spend a seen-set entry on
    /// the way out, and the nonce claim is last so only an otherwise-
    /// valid token consumes one. Sharing the function is what keeps that
    /// ordering true of every provider rather than of whichever one was
    /// edited most recently.
    let finishValidation
        (expectedAudience: string)
        (policy: PeerTokenPolicy)
        (expectedContract: string option)
        (iss: string)
        (doc: JsonDocument)
        : Async<Result<PeerPrincipal, PeerError>> =
        async {
            let claims = JwtCrypto.result {
                do! checkExpiry doc
                do! checkNotBefore doc
                do! checkAudience expectedAudience doc
                do! checkContractScope policy.CallScope expectedContract doc
                return ()
            }

            match claims with
            | Error e -> return Error(PeerUnauthorized e)
            | Ok() ->
                match readUserContext doc with
                | Error e -> return Error(PeerUnauthorized e)
                | Ok user ->
                    let! claimed = claimNonce policy doc

                    match claimed with
                    | Error e -> return Error(PeerUnauthorized e)
                    | Ok() ->
                        let name = getClaim "name" doc |> Option.defaultValue iss

                        return
                            Ok {
                                Caller = { PeerId = iss; DisplayName = name }
                                User = user
                            }
        }

    /// Phase 343 — the claim set and its `header.payload` signing input,
    /// single-sourced across every signing algorithm. `encode` (HS256)
    /// and the asymmetric provider differ only in the `alg` they name and
    /// in what they do with these bytes; the claims themselves are one
    /// definition, so a claim added for one provider is minted by both.
    ///
    /// `uctx` carries the serialised `UserContext` as a string claim
    /// (round-tripped through the universal converter set so the DU
    /// survives the wire). Lifetime is five minutes — a peer token is
    /// minted per call, not cached.
    ///
    /// Phase 338 — a `jti` nonce is minted UNCONDITIONALLY, whether or
    /// not this deployment enforces replay defence. A receiver ignores
    /// claims it does not know, so the extra claim is inert against a
    /// pre-338 peer; minting it always is what lets a fleet upgrade
    /// first and switch enforcement on second, rather than needing both
    /// halves of every peer pair to move in one step. `contractId` is
    /// `Some` only on the call-scoped issue path.
    let signingInput
        (alg: string)
        (caller: PeerIdentity)
        (audience: PeerIdentity)
        (user: UserContext)
        (contractId: string option)
        =
        let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

        let header = JsonObject()
        header["alg"] <- JsonValue.Create(alg)
        header["typ"] <- JsonValue.Create("JWT")

        let payload = JsonObject()
        payload["iss"] <- JsonValue.Create(caller.PeerId)
        payload["aud"] <- JsonValue.Create(audience.PeerId)
        payload["name"] <- JsonValue.Create(caller.DisplayName)
        payload["uctx"] <- JsonValue.Create(JsonRpc.serialize user)
        payload["iat"] <- JsonValue.Create(now)
        payload["exp"] <- JsonValue.Create(now + 300L)
        payload["nbf"] <- JsonValue.Create(now)
        payload["jti"] <- JsonValue.Create(Guid.NewGuid().ToString "N")

        match contractId with
        | Some cid -> payload["cid"] <- JsonValue.Create(cid)
        | None -> ()

        let encodedHeader = Base64Url.encode (Encoding.UTF8.GetBytes(header.ToJsonString()))

        let encodedPayload =
            Base64Url.encode (Encoding.UTF8.GetBytes(payload.ToJsonString()))

        $"{encodedHeader}.{encodedPayload}"

    /// Phase 343 — assemble the compact serialisation from a signing
    /// input and its raw signature bytes. Trivial, and shared for the
    /// same reason `signingInput` is: the wire shape is one definition.
    let assemble (input: string) (signature: byte[]) = $"{input}.{Base64Url.encode signature}"

    /// Mint a signed HS256 token over the shared claim set.
    let encode
        (secret: byte[])
        (caller: PeerIdentity)
        (audience: PeerIdentity)
        (user: UserContext)
        (contractId: string option)
        =
        let input = signingInput "HS256" caller audience user contractId
        assemble input (JwtCrypto.computeHmac secret (Encoding.UTF8.GetBytes input))

    /// Canonical byte string a delegating peer signs over: the subject
    /// joined to the ordered delegation chain. Deterministic so both
    /// sides produce the same HMAC input.
    let canonicalAssertion (assertion: DelegatedAssertion) =
        let chain = String.concat ">" assertion.DelegationChain
        $"{assertion.Subject}|{chain}"

/// BCL-only, fail-closed HS256 implementation of `IPeerAuthProvider`
/// (and, since Phase 338, of `IPeerCallScopedAuth`). Reads the symmetric
/// per-peer signing key from `ISecretStore` on every call; there is no
/// cached key and no "auth disabled" path.
///
/// `expectedAudience` is this receiver's own peer id (composed via
/// `PeerServerApp.withLocalPeer`). When non-empty, `ValidatePeerToken`
/// binds every inbound token's `aud` claim to it (Phase 130 — confused-
/// deputy / cross-receiver-replay defence). An `expectedAudience` of ""
/// keeps the pre-Phase-130 behaviour (audience binding off — GP 11); a
/// receiver that never composed a `LocalPeer` identity cannot bind
/// audience. **That empty case is a weaker posture, not a neutral one**
/// (see `PeerJwt.checkAudience`) — Phase 309 makes a composition that
/// lands in it say so at startup, and lets a deployment turn it into a
/// compose-time failure with `PeerServerApp.withStrictAudienceBinding`.
/// The provider itself is unchanged by that phase: it stays a stateless,
/// policy-free validator (GP 12 rule 4).
///
/// `policy` (Phase 338) carries the replay guard and the call-scope
/// mode. Its default — `PeerTokenPolicy.unscoped`, what the
/// two-argument constructors supply — consults no store and examines no
/// `jti` / `cid`, so an existing deployment validates exactly as it did
/// before (GP 11) and pays nothing (GP 13).
///
/// **The constructors are explicit overloads, not optional arguments.**
/// F# folds `?policy` into ONE widened constructor, which erases the
/// pre-338 `(ISecretStore, string option)` signature from the emitted
/// surface — source-compatible, but a binary break the public-API
/// baseline correctly reports as a removal. The separate three-argument
/// form keeps this phase's surface diff purely additive.
type JwtPeerAuthProvider(secrets: ISecretStore, expectedAudience: string, policy: PeerTokenPolicy) =

    /// The pre-338 call shapes, unchanged: `JwtPeerAuthProvider(secrets)`
    /// (audience unbound) and `JwtPeerAuthProvider(secrets, "peer-c")`
    /// (Phase 130 audience binding), both on the unscoped policy.
    new(secrets: ISecretStore, ?expectedAudience: string) =
        JwtPeerAuthProvider(secrets, defaultArg expectedAudience "", PeerTokenPolicy.unscoped)

    /// Mint a token, optionally bound to one contract id. Shared by the
    /// unscoped and call-scoped issue paths so the signing-key strength
    /// guard cannot drift between them.
    member private _.Issue
        (caller: PeerIdentity, audience: PeerIdentity, user: UserContext, contractId: string option)
        : Async<Result<string, PeerError>> =
        async {
            let! secretOpt = secrets.GetSecret(PeerJwt.platformScope, PeerJwt.signingKeyFor caller.PeerId)

            match secretOpt with
            | None -> return Error(PeerUnauthorized $"No signing key registered for peer '{caller.PeerId}'")
            | Some secret when not (PeerJwt.signingKeyIsStrong secret) ->
                // Blank / too-short signing material fails closed — issuing a
                // token signed with an empty or weak key would produce a
                // valid-but-publicly-forgeable MAC.
                return
                    Error(
                        PeerUnauthorized
                            $"Signing key for peer '{caller.PeerId}' is empty or below the {PeerJwt.minSigningKeyBytes}-byte minimum — refusing to issue a token signed with a weak key"
                    )
            | Some secret ->
                let token =
                    PeerJwt.encode (Encoding.UTF8.GetBytes secret) caller audience user contractId

                return Ok token
        }

    /// The whole validation path. `expectedContract` is `Some` on the
    /// call-scoped entry point and `None` on the plain one; every other
    /// check is identical, so the two cannot drift.
    member private _.Validate
        (token: string, expectedContract: string option)
        : Async<Result<PeerPrincipal, PeerError>> =
        async {
            match PeerJwt.split token with
            | Error e -> return Error(PeerUnauthorized e)
            | Ok(header, payload, signature) ->
                match PeerJwt.decodePayload payload with
                | Error e -> return Error(PeerUnauthorized e)
                | Ok doc ->
                    use _ = doc

                    match PeerJwt.getClaim "iss" doc with
                    | None -> return Error(PeerUnauthorized "Missing 'iss' claim")
                    // Phase 334 — FIRST, before the id is interpolated into
                    // the secret-store key path. Ordering matters: a
                    // traversal-shaped `iss` must never reach `GetSecret`,
                    // even to miss, because a path-mapping companion
                    // resolves the traversal on the way to deciding that.
                    | Some iss when not (PeerJwt.isWellFormedPeerId iss) ->
                        return Error(PeerUnauthorized PeerJwt.malformedIssuerMessage)
                    | Some iss ->
                        let! secretOpt = secrets.GetSecret(PeerJwt.platformScope, PeerJwt.signingKeyFor iss)

                        match secretOpt with
                        | None -> return Error(PeerUnauthorized $"No signing key registered for peer '{iss}'")
                        | Some secret when not (PeerJwt.signingKeyIsStrong secret) ->
                            // Fail closed on a blank / too-short stored key: verifying
                            // against an empty key would accept a publicly-computable
                            // HMAC, so any inbound token would "validate".
                            return
                                Error(
                                    PeerUnauthorized
                                        $"Signing key for peer '{iss}' is empty or below the {PeerJwt.minSigningKeyBytes}-byte minimum — refusing to validate a token against a weak key"
                                )
                        | Some secret ->
                            let secretBytes = Encoding.UTF8.GetBytes secret

                            let verified = JwtCrypto.result {
                                do! JwtCrypto.checkHs256Alg header
                                do! PeerJwt.verifySignature secretBytes header payload signature
                                return ()
                            }

                            match verified with
                            | Error e -> return Error(PeerUnauthorized e)
                            // Phase 343 — everything after the signature (the
                            // time / binding claims, then the asserted end-user
                            // context, then the replay claim, in that order) is
                            // the shared tail every provider runs. See
                            // `PeerJwt.finishValidation` for why the ordering is
                            // part of the contract.
                            | Ok() -> return! PeerJwt.finishValidation expectedAudience policy expectedContract iss doc
        }

    interface IPeerAuthProvider with
        member this.IssuePeerToken(caller: PeerIdentity, audience: PeerIdentity, user: UserContext) =
            this.Issue(caller, audience, user, None)

        member this.ValidatePeerToken(token: string) = this.Validate(token, None)

        member _.VerifyDelegation(assertion: DelegatedAssertion) = async {
            match List.tryLast assertion.DelegationChain with
            | None -> return Error(PeerUnauthorized "Delegation chain is empty")
            // Phase 334 — the delegation chain is wire-supplied and, like
            // `iss`, is unverified at the moment it is used to address the
            // key that would verify it. Same guard, same rule set.
            | Some delegatingPeer when not (PeerJwt.isWellFormedPeerId delegatingPeer) ->
                return Error(PeerUnauthorized PeerJwt.malformedDelegatingPeerMessage)
            | Some delegatingPeer ->
                let! secretOpt = secrets.GetSecret(PeerJwt.platformScope, PeerJwt.signingKeyFor delegatingPeer)

                match secretOpt with
                | None ->
                    return Error(PeerUnauthorized $"No signing key registered for delegating peer '{delegatingPeer}'")
                | Some secret when not (PeerJwt.signingKeyIsStrong secret) ->
                    // Fail closed: a blank / too-short delegating-peer key would
                    // verify a publicly-computable delegation signature.
                    return
                        Error(
                            PeerUnauthorized
                                $"Signing key for delegating peer '{delegatingPeer}' is empty or below the {PeerJwt.minSigningKeyBytes}-byte minimum — refusing to verify a delegation against a weak key"
                        )
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

    // Phase 338 — the call-scoped half. Under the default
    // `UnscopedCalls` policy both members behave exactly like their
    // unscoped counterparts (no `cid` is minted, none is examined), so a
    // caller may hold this seam unconditionally and let the composed
    // policy decide whether binding is in force.
    interface IPeerCallScopedAuth with
        member this.IssueScopedPeerToken
            (caller: PeerIdentity, audience: PeerIdentity, user: UserContext, contractId: string)
            =
            let bound =
                match policy.CallScope with
                | UnscopedCalls -> None
                | ContractBoundCalls -> Some contractId

            this.Issue(caller, audience, user, bound)

        member this.ValidateScopedPeerToken(token: string, contractId: string) =
            let expected =
                match policy.CallScope with
                | UnscopedCalls -> None
                | ContractBoundCalls -> Some contractId

            this.Validate(token, expected)