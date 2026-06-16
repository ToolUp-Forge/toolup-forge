// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System.Security.Cryptography
open System.Text
open ToolUp.Platform

// ─── Phase 165 — default module-binding verifier ────────────────────────
//
// The server-side default implementation of `IModuleBindingVerifier`
// (declared tier-shared in `ToolUp.Platform.Core`). It verifies a module's
// `ModuleBindingStamp` against a value-typed *set* of trust anchors, so a
// deployment can carry several anchors (mixed symmetric + asymmetric) and a
// stamp is admitted when *any* anchor verifies it.
//
// **No new crypto.** The asymmetric path reuses the Phase 40 detached-JWS
// primitives verbatim (`Jws.decode` / `Jws.verify` from `SigningInternals`,
// the same code `DefaultArtefactVerifier` validates against; the canonical
// JWS shape is the public `JwsBuilder` surface a stamp producer assembles
// with). The symmetric path is a plain `HMACSHA256` compared in constant
// time. Living inside this companion (which already references
// `ToolUp.Platform.Server` and BouncyCastle) is what makes the internal JWS
// primitives reachable without widening any public surface — and it keeps
// the crypto off the Fable client tier, since Core carries only the
// abstract contract.

/// A trust anchor the verifier checks a presented stamp against. The set is
/// value-typed (records / DU cases carrying key material by value), so
/// multiple anchors compose and `Verify` does no I/O.
type ModuleBindingAnchor =
    /// Asymmetric public verify key for a `JwsStamp`. `publicKey` follows
    /// the Phase 40 `StoredSigningKey.PublicKey` convention:
    /// SubjectPublicKeyInfo DER for `EcdsaP256`, the raw 32-byte public key
    /// for `Ed25519`. `keyId` is a diagnostic label; the JWS header's `kid`
    /// is not required to match it (the key material is what verifies).
    | AsymmetricAnchor of keyId: string * algorithm: SigningAlgorithm * publicKey: byte[]
    /// Symmetric MAC key for a `MacStamp`. `keyId` is matched against the
    /// stamp's declared key id so a deployment with several MAC anchors
    /// checks the right one first; `key` is the shared HMAC-SHA256 secret.
    | SymmetricAnchor of keyId: string * key: byte[]

/// Default `IModuleBindingVerifier`. Construct over the deployment's trust
/// anchors (`DefaultModuleBindingVerifier.create anchors`). Stateless and
/// synchronous — every `Verify` re-checks the presented stamp against the
/// anchor set with no cached state.
type DefaultModuleBindingVerifier(anchors: ModuleBindingAnchor list) =

    /// Canonical bytes the stamp is signed / MAC'd over: the UTF-8 module
    /// identifier. Recomputed from the module being gated (never the
    /// stamp), so a stamp minted for module A cannot be replayed onto B.
    static let canonicalBytes (moduleId: string) : byte[] = Encoding.UTF8.GetBytes moduleId

    let asymmetricAnchors =
        anchors
        |> List.choose (function
            | AsymmetricAnchor(keyId, alg, pk) -> Some(keyId, alg, pk)
            | SymmetricAnchor _ -> None)

    let symmetricAnchors =
        anchors
        |> List.choose (function
            | SymmetricAnchor(keyId, key) -> Some(keyId, key)
            | AsymmetricAnchor _ -> None)

    /// Try every asymmetric anchor whose algorithm matches the JWS header
    /// against the decoded detached JWS. Admitted if any verifies.
    let verifyJws (moduleId: string) (detachedJws: string) : BindingOutcome =
        match Jws.decode detachedJws with
        | Error _ -> Rejected "module-binding stamp is not a well-formed detached JWS"
        | Ok decoded ->
            let bytes = canonicalBytes moduleId

            let verified =
                asymmetricAnchors
                |> List.exists (fun (_, alg, pk) ->
                    // Only attempt anchors whose algorithm matches the
                    // header — avoids feeding a raw Ed25519 key into the
                    // ECDSA importer (and vice versa).
                    alg = decoded.Algorithm
                    && (match Jws.verify decoded pk bytes with
                        | Ok() -> true
                        | Error _ -> false))

            if verified then
                Allowed
            else
                Rejected "no configured trust anchor verifies the module-binding stamp"

    /// Recompute the HMAC over the canonical bytes and constant-time-compare
    /// it against the presented tag. Matching anchors are tried first by
    /// `keyId`, then any remaining symmetric anchor (so a tag minted under a
    /// rotated id still verifies if its key is still anchored).
    let verifyMac (moduleId: string) (keyId: string) (tag: string) : BindingOutcome =
        let expected = canonicalBytes moduleId

        let presented =
            try
                Some(JwsBuilder.base64UrlDecode tag)
            with _ ->
                None

        match presented with
        | None -> Rejected "module-binding MAC tag is not valid base64url"
        | Some presentedBytes ->
            let candidates =
                let matchingFirst, rest =
                    symmetricAnchors |> List.partition (fun (id, _) -> id = keyId)

                matchingFirst @ rest

            let verified =
                candidates
                |> List.exists (fun (_, key) ->
                    use hmac = new HMACSHA256(key)
                    let computed = hmac.ComputeHash(expected)

                    CryptographicOperations.FixedTimeEquals(
                        System.ReadOnlySpan<byte>(computed),
                        System.ReadOnlySpan<byte>(presentedBytes)
                    ))

            if verified then
                Allowed
            else
                Rejected "no configured trust anchor verifies the module-binding stamp"

    interface IModuleBindingVerifier with
        member _.Verify(moduleId: string, stamp: ModuleBindingStamp option) : BindingOutcome =
            match stamp with
            // Absent stamp → Allowed (policy permitting). This verifier
            // admits unstamped modules; a deployment that wants every module
            // stamped layers that policy above the verifier.
            | None -> Allowed
            | Some(JwsStamp detachedJws) -> verifyJws moduleId detachedJws
            | Some(MacStamp(keyId, tag)) -> verifyMac moduleId keyId tag

module DefaultModuleBindingVerifier =
    /// Construct a verifier over the deployment's trust anchors. An empty
    /// anchor set admits unstamped modules but rejects every stamped one
    /// (nothing can verify) — the "binding configured but no matching
    /// anchor" fail-closed case.
    let create (anchors: ModuleBindingAnchor list) : IModuleBindingVerifier =
        DefaultModuleBindingVerifier(anchors) :> IModuleBindingVerifier