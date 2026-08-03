// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 343 — asymmetric peer auth (the blast-radius companion) ───
//
// `JwtPeerAuthProvider` signs and verifies with ONE shared symmetric
// secret per peer, held by both ends. That is a fine trade for a strict
// 1:1 pairing and a poor one for anything wider, for two reasons that are
// worth separating:
//
//   * **Compromise scope.** A receiver that trusts N peers holds N live
//     signing keys. Reading its `ISecretStore` yields the material to
//     mint tokens as *every one of them*, against every other receiver
//     that trusts the same peers — so the blast radius of one breach is
//     the whole federation, not one link.
//   * **Attribution.** Because both ends hold the same key, a token
//     proves only that SOMEONE holding it signed the payload. The
//     receiver can mint tokens indistinguishable from the caller's own,
//     so "peer X said this" is not a claim either side can stand behind
//     against the other.
//
// Under an asymmetric key both collapse. The private key never leaves the
// deployment that owns it; every receiver holds only public keys, which
// are not secrets at all. Compromising a receiver yields nothing that can
// forge anything, and a verified signature is attributable to exactly one
// key-holder.
//
// **GP 1 companion, not a replacement.** `JwtPeerAuthProvider` remains
// the default and is byte-for-byte unchanged (GP 11); a deployment that
// never constructs this type pays nothing (GP 13). Everything else about
// the token — claim set, `exp` / `nbf` / `aud` binding, the Phase 338
// `jti` replay claim and `cid` contract binding, the Phase 330 `uctx`
// handling, the Phase 334 issuer sanitisation — is literally the shared
// `PeerJwt` code path, so the two providers cannot drift into disagreeing
// about what a valid peer token is. The ONLY differences are which secret
// is read, which `alg` is accepted, and how the signature is checked.
//
// **BCL crypto, no new dependency.** `ECDsa` / `RSA` with
// `ImportFromPem` cover ES256 and RS256 outright; this is the same
// primitive set `OidcAuthProvider.verifyJws` already uses for the
// asymmetric IdP path, down to ES256's IEEE-P1363 fixed-field signature
// transport. BouncyCastle is present in this project for the Phase 18f
// commutative cipher, which needs raw curve-point arithmetic the BCL does
// not expose; ordinary sign / verify is not that case, and reaching for a
// vendor primitive where the BCL suffices would widen the OSS supply-
// chain surface for nothing (GP 1).
//
// **Key material lives in `ISecretStore`, read per call** (GP 12 rule 4),
// under the reserved `_platform` scope:
//
//     peers/{peerId}/signing-private-key   PEM PKCS#8 — THIS deployment's
//                                          own key, and no other peer's
//     peers/{peerId}/signing-public-key    PEM SPKI — one per trusted
//                                          peer, including this one when
//                                          it validates its own tokens
//
// Deliberately distinct key names from the symmetric
// `peers/{peerId}/signing-key`: a deployment mid-migration holds both,
// and a shared name would make "which provider is this store configured
// for" unanswerable.
//
// **No signature comparison happens here.** The symmetric provider
// compares MACs in constant time because equality against a secret-
// derived value is the whole check; asymmetric verification is a
// primitive that answers a bool from public material, so there is no
// secret-dependent comparison to make constant-time. The constant-time
// compares the shared code path still performs (`aud`, `cid`, the
// delegation signature on the symmetric side) are `PeerJwt`'s, backed by
// `JwtCrypto.fixedTimeEquals`.

/// The asymmetric JWS algorithm an `AsymmetricPeerAuthProvider` mints and
/// accepts. **Exactly one per provider instance.** A provider that
/// accepted several would have to pick between them by reading the
/// token's own `alg` header, which is precisely the algorithm-confusion
/// surface the single-algorithm gate exists to close.
type PeerSignatureAlgorithm =
    /// ECDSA on NIST P-256 with SHA-256. The default choice: 64-byte
    /// signatures, small keys, and no key-size parameter to get wrong.
    | PeerEs256
    /// RSASSA-PKCS1-v1_5 with SHA-256. For a deployment whose key
    /// custody (an HSM, an existing PKI) is RSA-shaped.
    | PeerRs256

[<RequireQualifiedAccess>]
module PeerSignatureAlgorithm =

    /// The JWS `alg` header value, which is also what the receiver's
    /// single-algorithm gate compares against.
    let jwsName (algorithm: PeerSignatureAlgorithm) =
        match algorithm with
        | PeerEs256 -> "ES256"
        | PeerRs256 -> "RS256"

/// PEM key custody for the asymmetric provider: where the material lives
/// and what counts as usable. Assembly-internal — the key *names* are
/// part of the deployment contract and are documented in the migration
/// doc and this file's header, but nothing here belongs on the public
/// surface.
module internal PeerAsymmetricKeys =

    /// Secret-store key holding a peer's PEM SPKI **public** key — the
    /// only material a receiver needs, and not a secret.
    let publicKeyFor (peerId: string) = $"peers/{peerId}/signing-public-key"

    /// Secret-store key holding THIS deployment's PEM PKCS#8 **private**
    /// key. Never another peer's: if a second peer's private key is in
    /// this store, the topology has reverted to the shared-secret blast
    /// radius this provider exists to remove.
    let privateKeyFor (peerId: string) = $"peers/{peerId}/signing-private-key"

    /// Minimum RSA modulus, in bits. 1024-bit RSA is publicly factorable
    /// at state scale and deprecated everywhere it still appears; a key
    /// below this fails closed (GP 4) for the same reason the symmetric
    /// provider refuses a signing key under 32 bytes — signing material
    /// that is present but too weak produces a valid-looking signature
    /// with none of the strength the caller assumes.
    [<Literal>]
    let minRsaKeyBits = 2048

    /// P-256 is the only curve `PeerEs256` names, so a PEM carrying
    /// P-384 / P-521 is refused rather than silently signed with a
    /// SHA-256 digest the JWS `alg` does not describe.
    [<Literal>]
    let es256KeyBits = 256

    /// Import a PEM into an `ECDsa` / `RSA` and check its shape matches
    /// the algorithm. Any import failure — wrong key type, a public key
    /// on a signing path, malformed PEM — surfaces as the same
    /// categorised `Error`, never an exception: this runs on inbound,
    /// attacker-influenced paths, and an escaping exception is exactly
    /// the 500-instead-of-401 defect the rest of this phase closes.
    let private withEcdsa (pem: string) (f: ECDsa -> Result<'T, string>) : Result<'T, string> =
        try
            use ec = ECDsa.Create()
            ec.ImportFromPem pem

            if ec.KeySize <> es256KeyBits then
                Error $"ES256 requires a NIST P-256 key; this key is {ec.KeySize}-bit"
            else
                f ec
        with _ ->
            Error "Unusable EC key material"

    let private withRsa (pem: string) (f: RSA -> Result<'T, string>) : Result<'T, string> =
        try
            use rsa = RSA.Create()
            rsa.ImportFromPem pem

            if rsa.KeySize < minRsaKeyBits then
                Error $"RS256 requires at least a {minRsaKeyBits}-bit key; this key is {rsa.KeySize}-bit"
            else
                f rsa
        with _ ->
            Error "Unusable RSA key material"

    /// Sign `message` with a PEM private key. ES256's JWS transport is
    /// the IEEE-P1363 fixed-field `r ‖ s` concatenation (64 bytes for
    /// P-256), NOT the ASN.1 DER form `ECDsa` produces by default —
    /// getting that wrong yields a signature every conformant verifier
    /// rejects, which is why the format is named explicitly here and in
    /// `verify`.
    let sign (algorithm: PeerSignatureAlgorithm) (pem: string) (message: byte[]) : Result<byte[], string> =
        match algorithm with
        | PeerEs256 ->
            withEcdsa pem (fun ec ->
                Ok(ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)))
        | PeerRs256 ->
            withRsa pem (fun rsa -> Ok(rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)))

    /// Verify a base64url JWS signature over `message` with a PEM public
    /// key. The signature segment is decoded with `tryDecode` for the
    /// Phase 343 reason: it is attacker-controlled, and a throwing decode
    /// on this path is a 500 that tells an unauthenticated caller its
    /// encoding was the problem. Every failure — malformed encoding,
    /// unusable key, genuine mismatch — returns the same wording.
    let verify
        (algorithm: PeerSignatureAlgorithm)
        (pem: string)
        (message: byte[])
        (signature: string)
        : Result<unit, string> =
        let invalid = Error "Invalid signature"

        match Base64Url.tryDecode signature with
        | None -> invalid
        | Some raw ->
            let verified =
                match algorithm with
                | PeerEs256 ->
                    withEcdsa pem (fun ec ->
                        Ok(
                            ec.VerifyData(
                                message,
                                raw,
                                HashAlgorithmName.SHA256,
                                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                            )
                        ))
                | PeerRs256 ->
                    withRsa pem (fun rsa ->
                        Ok(rsa.VerifyData(message, raw, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)))

            match verified with
            | Ok true -> Ok()
            | Ok false -> invalid
            // A key the receiver cannot use is a configuration fault, not
            // a caller fault, so it says so rather than hiding behind
            // "Invalid signature" — the operator is the audience here and
            // the token was never going to be accepted either way.
            | Error e -> Error e

/// Asymmetric (`ES256` / `RS256`) implementation of `IPeerAuthProvider`
/// and `IPeerCallScopedAuth` — the GP 1 companion to the symmetric
/// `JwtPeerAuthProvider` default, for topologies beyond a strict 1:1
/// pairing. See this file's header for the blast-radius argument and the
/// `ISecretStore` key layout.
///
/// `expectedAudience`, `policy` and every claim check behave exactly as
/// they do on the symmetric provider — the code is literally shared
/// (`PeerJwt.finishValidation`), so Phase 130 audience binding, Phase 338
/// replay defence and contract binding, and Phase 330 `uctx` handling all
/// apply here unchanged and are configured the same way.
///
/// **A receiver needs no private key at all.** Composing this with a
/// secret store holding only `peers/*/signing-public-key` entries yields
/// a deployment that validates every trusted peer's tokens and can forge
/// none of them; `IssuePeerToken` from such a store fails closed, which
/// is the correct answer for a host-only receiver.
///
/// **The constructors are explicit overloads, not optional arguments.**
/// F# folds `?policy` into one widened constructor, which erases the
/// narrower signatures from the emitted surface — source-compatible, but
/// a binary break the public-API baseline correctly reports as a removal.
type AsymmetricPeerAuthProvider
    (secrets: ISecretStore, expectedAudience: string, algorithm: PeerSignatureAlgorithm, policy: PeerTokenPolicy) =

    let algName = PeerSignatureAlgorithm.jwsName algorithm

    /// Audience unbound, unscoped policy — the minimum a caller-only
    /// deployment needs.
    new(secrets: ISecretStore, algorithm: PeerSignatureAlgorithm) =
        AsymmetricPeerAuthProvider(secrets, "", algorithm, PeerTokenPolicy.unscoped)

    /// Phase 130 audience binding on the unscoped policy.
    new(secrets: ISecretStore, expectedAudience: string, algorithm: PeerSignatureAlgorithm) =
        AsymmetricPeerAuthProvider(secrets, expectedAudience, algorithm, PeerTokenPolicy.unscoped)

    /// Mint a token, optionally bound to one contract id. Shared by the
    /// unscoped and call-scoped issue paths so the key handling cannot
    /// drift between them.
    member private _.Issue
        (caller: PeerIdentity, audience: PeerIdentity, user: UserContext, contractId: string option)
        : Async<Result<string, PeerError>> =
        async {
            let! pem = secrets.GetSecret(PeerJwt.platformScope, PeerAsymmetricKeys.privateKeyFor caller.PeerId)

            match pem with
            | None ->
                return
                    Error(
                        PeerUnauthorized
                            $"No private signing key registered for peer '{caller.PeerId}' (expected a PEM PKCS#8 key at {PeerAsymmetricKeys.privateKeyFor caller.PeerId})"
                    )
            | Some pem ->
                let input = PeerJwt.signingInput algName caller audience user contractId

                match PeerAsymmetricKeys.sign algorithm pem (Encoding.UTF8.GetBytes input) with
                | Error e ->
                    return
                        Error(
                            PeerUnauthorized
                                $"Refusing to mint a token for peer '{caller.PeerId}' — {e}. Signing material that is present but unusable must fail closed rather than produce a signature no peer will accept"
                        )
                | Ok signature -> return Ok(PeerJwt.assemble input signature)
        }

    /// The whole validation path. `expectedContract` is `Some` on the
    /// call-scoped entry point and `None` on the plain one. Structurally
    /// the symmetric provider's `Validate` with the key read and the
    /// signature check swapped; everything after the signature is the
    /// shared `PeerJwt.finishValidation`.
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
                    // Phase 334 — sanitise BEFORE the id is interpolated
                    // into the secret-store key path, for the same reason
                    // the symmetric provider does: a path-mapping
                    // `ISecretStore` companion resolves a traversal on the
                    // way to deciding the key is absent.
                    | Some iss when not (PeerJwt.isWellFormedPeerId iss) ->
                        return Error(PeerUnauthorized PeerJwt.malformedIssuerMessage)
                    | Some iss ->
                        let! pem = secrets.GetSecret(PeerJwt.platformScope, PeerAsymmetricKeys.publicKeyFor iss)

                        match pem with
                        | None -> return Error(PeerUnauthorized $"No public key registered for peer '{iss}'")
                        | Some pem ->
                            let verified = JwtCrypto.result {
                                do! PeerJwt.checkAlg algName header

                                do!
                                    PeerAsymmetricKeys.verify
                                        algorithm
                                        pem
                                        (Encoding.UTF8.GetBytes $"{header}.{payload}")
                                        signature

                                return ()
                            }

                            match verified with
                            | Error e -> return Error(PeerUnauthorized e)
                            | Ok() -> return! PeerJwt.finishValidation expectedAudience policy expectedContract iss doc
        }

    interface IPeerAuthProvider with
        member this.IssuePeerToken(caller: PeerIdentity, audience: PeerIdentity, user: UserContext) =
            this.Issue(caller, audience, user, None)

        member this.ValidatePeerToken(token: string) = this.Validate(token, None)

        member _.VerifyDelegation(assertion: DelegatedAssertion) = async {
            match List.tryLast assertion.DelegationChain with
            | None -> return Error(PeerUnauthorized "Delegation chain is empty")
            | Some delegatingPeer when not (PeerJwt.isWellFormedPeerId delegatingPeer) ->
                return Error(PeerUnauthorized PeerJwt.malformedDelegatingPeerMessage)
            | Some delegatingPeer ->
                let! pem = secrets.GetSecret(PeerJwt.platformScope, PeerAsymmetricKeys.publicKeyFor delegatingPeer)

                match pem with
                | None ->
                    return Error(PeerUnauthorized $"No public key registered for delegating peer '{delegatingPeer}'")
                | Some pem ->
                    let canonical = Encoding.UTF8.GetBytes(PeerJwt.canonicalAssertion assertion)

                    match PeerAsymmetricKeys.verify algorithm pem canonical assertion.Signature with
                    | Ok() -> return Ok()
                    | Error _ -> return Error(PeerUnauthorized "Delegation signature verification failed")
        }

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