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
// **Claim order is load-bearing.** The replay claim is the LAST check.
// Claiming before the signature verifies would let an unauthenticated
// attacker burn seen-set entries with forged tokens — turning the
// defence into the denial-of-service vector it exists to prevent.

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

    /// Mint a signed HS256 token. `uctx` carries the serialised
    /// `UserContext` as a string claim (round-tripped through the
    /// universal converter set so the DU survives the wire). Lifetime is
    /// five minutes — a peer token is minted per call, not cached.
    ///
    /// Phase 338 — a `jti` nonce is minted UNCONDITIONALLY, whether or
    /// not this deployment enforces replay defence. A receiver ignores
    /// claims it does not know, so the extra claim is inert against a
    /// pre-338 peer; minting it always is what lets a fleet upgrade
    /// first and switch enforcement on second, rather than needing both
    /// halves of every peer pair to move in one step. `contractId` is
    /// `Some` only on the call-scoped issue path.
    let encode
        (secret: byte[])
        (caller: PeerIdentity)
        (audience: PeerIdentity)
        (user: UserContext)
        (contractId: string option)
        =
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
        payload["jti"] <- JsonValue.Create(Guid.NewGuid().ToString "N")

        match contractId with
        | Some cid -> payload["cid"] <- JsonValue.Create(cid)
        | None -> ()

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
/// audience.
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

    /// Phase 338 — claim the token's `jti` against the replay guard.
    /// Runs only once every other check has passed (see the claim-order
    /// note in this file's header). Without a guard composed this is
    /// `Ok` without touching the token at all, so the `jti` claim is
    /// inert for a deployment that has not opted in.
    member private _.ClaimNonce(doc: JsonDocument) : Async<Result<unit, string>> = async {
        match policy.ReplayGuard with
        | None -> return Ok()
        | Some guard ->
            match PeerJwt.getClaim "jti" doc with
            | None -> return Error PeerJwt.missingNonceMessage
            | Some jti when String.IsNullOrWhiteSpace jti -> return Error PeerJwt.missingNonceMessage
            | Some jti ->
                match PeerJwt.freshnessDeadline doc with
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

    /// The whole validation path. `expectedContract` is `Some` on the
    /// call-scoped entry point and `None` on the plain one; every other
    /// check is identical, so the two cannot drift.
    member private this.Validate
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

                            let validated = JwtCrypto.result {
                                do! JwtCrypto.checkHs256Alg header
                                do! PeerJwt.verifySignature secretBytes header payload signature
                                do! PeerJwt.checkExpiry doc
                                do! PeerJwt.checkNotBefore doc
                                do! PeerJwt.checkAudience expectedAudience doc
                                do! PeerJwt.checkContractScope policy.CallScope expectedContract doc
                                return ()
                            }

                            match validated with
                            | Error e -> return Error(PeerUnauthorized e)
                            | Ok() ->
                                // LAST: only an otherwise-valid token gets to
                                // consume a seen-set entry.
                                let! claimed = this.ClaimNonce doc

                                match claimed with
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

    interface IPeerAuthProvider with
        member this.IssuePeerToken(caller: PeerIdentity, audience: PeerIdentity, user: UserContext) =
            this.Issue(caller, audience, user, None)

        member this.ValidatePeerToken(token: string) = this.Validate(token, None)

        member _.VerifyDelegation(assertion: DelegatedAssertion) = async {
            match List.tryLast assertion.DelegationChain with
            | None -> return Error(PeerUnauthorized "Delegation chain is empty")
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