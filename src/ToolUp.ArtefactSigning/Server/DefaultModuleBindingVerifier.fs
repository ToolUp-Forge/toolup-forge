// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.IO
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

// ─── Phase 216 — module-SBOM canonical bytes ────────────────────────────
//
// The bytes an SBOM signature covers. A single source of truth shared by the
// verifier (here) and any deploy-time stamper that mints an SBOM stamp (e.g.
// the `toolup stamp --sbom-*` path, which replicates this with pure BCL and
// is pinned to it by a round-trip test). Order-independent (components are
// sorted) and unambiguous (control-character separators), so re-ordering or
// re-serialising the SBOM does not change the signed bytes, but altering any
// component field does.
module ModuleSbomSigning =
    // ASCII control-character separators so a component field can never collide
    // with one: 0x1f unit (between a component's fields), 0x1d group (between
    // components), 0x1e record (module id vs the SBOM body).
    let private unitSep = string (char 0x1f)
    let private groupSep = string (char 0x1d)
    let private recordSep = string (char 0x1e)

    /// Canonical bytes the SBOM signature is computed over: the module id, then
    /// the sorted components, each rendered Name/Version/Sha256 and joined.
    /// Bound to `moduleId` so an SBOM stamp minted for one module cannot be
    /// replayed onto another.
    let canonicalBytes (moduleId: string) (sbom: ModuleSbom) : byte[] =
        let render (c: ModuleSbomComponent) =
            String.concat unitSep [ c.Name; c.Version; c.Sha256 ]

        let body =
            sbom.Components
            |> List.map render
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> String.concat groupSep

        Encoding.UTF8.GetBytes(moduleId + recordSep + body)

/// Default `IModuleBindingVerifier`. Construct over the deployment's trust
/// anchors (`DefaultModuleBindingVerifier.create anchors`). Stateless and
/// synchronous — every `Verify` re-checks the presented stamp against the
/// anchor set with no cached state.
///
/// **Phase 215 — optional revocation + transparency seams.** A deployment
/// may additionally compose an `IBindingRevocationList` (consulted after a
/// stamp verifies, before it admits — a revoked anchor/stamp denies a
/// cryptographically-valid stamp) and an `IBindingTransparencyLog` (every
/// admit/deny is recorded). Both default to `None`: with neither configured
/// the gate is byte-for-byte the pre-215 path — no revocation check, no
/// record, no `stampId` computation (GP 13).
type DefaultModuleBindingVerifier
    (anchors: ModuleBindingAnchor list, ?revocation: IBindingRevocationList, ?transparencyLog: IBindingTransparencyLog)
    =

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

    let cryptoRejected = "no configured trust anchor verifies the module-binding stamp"

    /// Try every asymmetric anchor whose algorithm matches the JWS header
    /// against the decoded detached JWS over `bytes`. On success returns the
    /// *verifying* anchor's key id (for the revocation check + transparency
    /// record). `bytes` is the signed payload — the module-id canonical bytes
    /// for a module stamp, or the SBOM canonical bytes for an SBOM stamp.
    let verifyJws (bytes: byte[]) (detachedJws: string) : BindingOutcome * string option =
        match Jws.decode detachedJws with
        | Error _ -> Rejected "module-binding stamp is not a well-formed detached JWS", None
        | Ok decoded ->
            let verifyingKeyId =
                asymmetricAnchors
                |> List.tryPick (fun (keyId, alg, pk) ->
                    // Only attempt anchors whose algorithm matches the
                    // header — avoids feeding a raw Ed25519 key into the
                    // ECDSA importer (and vice versa).
                    if alg = decoded.Algorithm then
                        match Jws.verify decoded pk bytes with
                        | Ok() -> Some keyId
                        | Error _ -> None
                    else
                        None)

            match verifyingKeyId with
            | Some keyId -> Allowed, Some keyId
            | None -> Rejected cryptoRejected, None

    /// Recompute the HMAC over the canonical bytes and constant-time-compare
    /// it against the presented tag. Matching anchors are tried first by
    /// `keyId`, then any remaining symmetric anchor (so a tag minted under a
    /// rotated id still verifies if its key is still anchored). On success
    /// returns the *verifying* anchor's key id.
    let verifyMac (expected: byte[]) (keyId: string) (tag: string) : BindingOutcome * string option =
        let presented =
            try
                Some(JwsBuilder.base64UrlDecode tag)
            with _ ->
                None

        match presented with
        | None -> Rejected "module-binding MAC tag is not valid base64url", None
        | Some presentedBytes ->
            let candidates =
                let matchingFirst, rest =
                    symmetricAnchors |> List.partition (fun (id, _) -> id = keyId)

                matchingFirst @ rest

            let verifyingKeyId =
                candidates
                |> List.tryPick (fun (id, key) ->
                    use hmac = new HMACSHA256(key)
                    let computed = hmac.ComputeHash(expected)

                    if
                        CryptographicOperations.FixedTimeEquals(
                            ReadOnlySpan<byte>(computed),
                            ReadOnlySpan<byte>(presentedBytes)
                        )
                    then
                        Some id
                    else
                        None)

            match verifyingKeyId with
            | Some id -> Allowed, Some id
            | None -> Rejected cryptoRejected, None

    /// Verify any `ModuleBindingStamp` over the given signed `bytes` against
    /// the anchor set — the shared primitive behind both the module-id stamp
    /// (Phase 165) and the SBOM stamp (Phase 216).
    let verifyStamp (bytes: byte[]) (stamp: ModuleBindingStamp) : BindingOutcome * string option =
        match stamp with
        | JwsStamp detachedJws -> verifyJws bytes detachedJws
        | MacStamp(keyId, tag) -> verifyMac bytes keyId tag

    interface IModuleBindingVerifier with
        member _.Verify(moduleId: string, stamp: ModuleBindingStamp option) : BindingOutcome =
            // Crypto outcome + the verifying anchor's key id (None on an
            // absent stamp or a stamp no anchor verified). Absent stamp →
            // Allowed (policy permitting): this verifier admits unstamped
            // modules; a deployment that wants every module stamped layers
            // that policy above the verifier.
            let cryptoOutcome, anchorId =
                match stamp with
                | None -> Allowed, None
                | Some s -> verifyStamp (canonicalBytes moduleId) s

            match revocation, transparencyLog with
            // GP 13 zero-cost path: neither seam configured ⇒ byte-for-byte
            // the pre-215 gate — no revocation check, no record, no stampId
            // computation.
            | None, None -> cryptoOutcome
            | _ ->
                let stampId = stamp |> Option.map ModuleBindingRevocation.stampId

                // Revocation precedence: a revoked anchor/stamp denies a
                // cryptographically-valid stamp; an absent/empty list admits
                // exactly as the crypto decided.
                let finalOutcome =
                    match cryptoOutcome, revocation, anchorId, stampId with
                    | Allowed, Some rev, Some aid, Some sid when rev.IsRevoked(aid, sid) ->
                        Rejected(sprintf "module-binding stamp is revoked under anchor '%s'" aid)
                    | _ -> cryptoOutcome

                // Record the decision (startup-time, not hot-path — await so
                // the record is durable before the module loads).
                transparencyLog
                |> Option.iter (fun log ->
                    let admitted, reason =
                        match finalOutcome with
                        | Allowed -> true, None
                        | Rejected r -> false, Some r

                    Async.RunSynchronously(
                        log.Record {
                            ModuleId = moduleId
                            AnchorId = anchorId
                            StampId = stampId
                            Admitted = admitted
                            Reason = reason
                            TimestampUtc = DateTimeOffset.UtcNow
                        }
                    ))

                finalOutcome

    // ─── Phase 216 — SBOM verification ──────────────────────────────────
    interface IModuleSbomVerifier with
        member _.VerifySbom(moduleId: string, sbom: ModuleSbomStamp) : BindingOutcome =
            // The SBOM signature is the same stamp shape, minted under the same
            // anchor set, over the SBOM's canonical bytes — so tampering with
            // any component (name / version / hash) alters the bytes and the
            // signature fails. Reuses the exact module-stamp verify primitive.
            let outcome, _ =
                verifyStamp (ModuleSbomSigning.canonicalBytes moduleId sbom.Sbom) sbom.Signature

            outcome

module DefaultModuleBindingVerifier =
    /// Construct a verifier over the deployment's trust anchors. An empty
    /// anchor set admits unstamped modules but rejects every stamped one
    /// (nothing can verify) — the "binding configured but no matching
    /// anchor" fail-closed case. No revocation list / transparency log:
    /// byte-for-byte the pre-215 gate (GP 13).
    let create (anchors: ModuleBindingAnchor list) : IModuleBindingVerifier =
        DefaultModuleBindingVerifier(anchors) :> IModuleBindingVerifier

    /// Construct a verifier that additionally consults a revocation list
    /// (a revoked anchor/stamp denies a valid stamp) and records every
    /// admit/deny to a transparency log. Pass `BindingRevocationList.none` /
    /// `BindingTransparencyLog.none` to opt into one seam without the other.
    let createWith
        (anchors: ModuleBindingAnchor list)
        (revocation: IBindingRevocationList)
        (transparencyLog: IBindingTransparencyLog)
        : IModuleBindingVerifier =
        DefaultModuleBindingVerifier(anchors, revocation, transparencyLog) :> IModuleBindingVerifier

    /// Phase 216 — verify a module's signed SBOM against the anchor set.
    /// `Allowed` when the SBOM signature verifies over its canonical bytes,
    /// `Rejected` when it is tampered or no anchor verifies it. Independent of
    /// the module-stamp gate: a deployment can verify an SBOM with the same
    /// anchors it gates module loads with.
    let verifySbom (anchors: ModuleBindingAnchor list) (moduleId: string) (sbom: ModuleSbomStamp) : BindingOutcome =
        (DefaultModuleBindingVerifier(anchors) :> IModuleSbomVerifier).VerifySbom(moduleId, sbom)

// ─── Phase 215 — signed revocation-list loader ──────────────────────────
//
// The revocation list is only trustworthy when its signature is verified —
// an unsigned list an attacker can overwrite (or delete) would silently
// un-revoke a compromised key. This loader verifies a detached JWS over the
// revocation JSON (the Phase 40 primitives, same shape `JwsBuilder`
// produces) against a configured public verify key *before* parsing, and
// fails closed on any signature / parse failure rather than admitting an
// empty list. The crypto-free format + parser live in
// `ModuleBindingRevocation` (`ToolUp.Platform.Server`).
module SignedRevocationList =

    /// Verify a detached JWS over the revocation-list JSON bytes against
    /// `publicKey` (SPKI DER for ECDSA, raw 32 bytes for Ed25519), then
    /// parse into an `IBindingRevocationList`. Fail-closed: a mismatched
    /// algorithm, a bad signature, or malformed JSON is an `Error`.
    let verifyAndParse
        (publicKey: byte[])
        (algorithm: SigningAlgorithm)
        (json: string)
        (detachedJws: string)
        : Result<IBindingRevocationList, string> =
        match Jws.decode detachedJws with
        | Error e -> Error(sprintf "revocation-list signature is malformed: %s" (VerificationError.describe e))
        | Ok decoded ->
            if decoded.Algorithm <> algorithm then
                Error(
                    sprintf
                        "revocation-list signature algorithm mismatch (header %s, anchor expects %s)"
                        (SigningAlgorithm.jwsAlg decoded.Algorithm)
                        (SigningAlgorithm.jwsAlg algorithm)
                )
            else
                match Jws.verify decoded publicKey (Encoding.UTF8.GetBytes json) with
                | Error e ->
                    Error(sprintf "revocation-list signature did not verify: %s" (VerificationError.describe e))
                | Ok() -> ModuleBindingRevocation.parseList json

    /// Load + verify a signed revocation list from `jsonPath` and its
    /// detached-JWS sidecar `jwsPath`. Fail-closed on a missing/unreadable
    /// file or a failed signature. An operator that genuinely wants "no
    /// revocations" ships a signed empty list, never a missing file — so
    /// `verifyAndParse`'s caller can distinguish "no list configured"
    /// (compose `BindingRevocationList.none`) from "list present but
    /// untrusted" (this `Error`).
    let loadSigned
        (publicKey: byte[])
        (algorithm: SigningAlgorithm)
        (jsonPath: string)
        (jwsPath: string)
        : Result<IBindingRevocationList, string> =
        try
            let json = File.ReadAllText jsonPath
            let jws = (File.ReadAllText jwsPath).Trim()
            verifyAndParse publicKey algorithm json jws
        with ex ->
            Error(sprintf "failed to read signed revocation list: %s" ex.Message)