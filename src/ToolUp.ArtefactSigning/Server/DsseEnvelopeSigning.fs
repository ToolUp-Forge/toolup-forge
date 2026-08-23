// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.DsseEnvelopeSigning

open System
open System.Security.Cryptography
open System.Text
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Signers
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Signed-statement envelope — the crypto half (composition glue) ─────
//
// `ToolUp.Platform.Server` owns the envelope FORMAT (`DsseEnvelope`: the
// in-toto statement, the DSSE Pre-Authentication Encoding, the structural
// checks) and carries no signing dependency, so a substrate can emit an
// envelope without pulling key management into its package (GP 1). This
// module fills the two ends the format cannot: a signer over the
// deployment's own key material, and an **offline** verifier that needs
// only the envelope plus a public key.
//
// **Why the glue lives here, not in `ToolUp.Platform.Server`** — the same
// reason `SignedExportBundle` gives: `ToolUp.ArtefactSigning` already
// `ProjectReference`s `ToolUp.Platform.Server`, so the dependency cannot
// run the other way, and the SDK core must stay signer-free. Ed25519 also
// needs BouncyCastle, which lives only in this package.
//
// **Why this is not an `IArtefactSigner` adapter.** `IArtefactSigner`
// emits a detached JWS: its signature covers `b64url(header) + "." +
// b64url(artefact)`, not the artefact. DSSE requires the signature to
// cover the PAE itself — that is precisely what lets unmodified standard
// tooling verify it. The two seams sign different messages, so one cannot
// be expressed in terms of the other, and wrapping a JWS in a DSSE
// envelope would produce a document no DSSE implementation accepts. What
// IS shared is everything that makes it "the deployment's signature": the
// same `ISecretStore` key material under the same key id, the same
// algorithm set, and the same public key served by the Phase 40
// `/_platform/signing-key/{keyId}` endpoint — so a holder resolves one
// key and checks either artefact.
//
// **Signature encoding on the wire** (`signatures[].sig`, standard
// base64):
//   * `EcdsaP256` → ASN.1 DER `SEQUENCE { INTEGER r, INTEGER s }`, the
//     encoding the in-toto / DSSE ecosystem's ECDSA verifiers expect.
//     Note this deliberately differs from the JWS ES256 shape (raw r‖s)
//     the detached-JWS path uses — different specification, different
//     convention.
//   * `Ed25519` → the raw 64-byte signature.

// ── raw sign / verify over an arbitrary message ─────────────────────────
//
// Distinct from `SigningInternals.Jws`, which signs the JWS signing input
// derived from a message. These sign the message verbatim, which is what
// DSSE's PAE requires.

let private signEcdsaDer (pkcs8: byte[]) (message: byte[]) : byte[] =
    use ec = ECDsa.Create()
    ec.ImportPkcs8PrivateKey(ReadOnlySpan<byte>(pkcs8)) |> ignore
    ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)

let private verifyEcdsaDer (spkiPem: string) (message: byte[]) (signature: byte[]) : bool =
    use ec = ECDsa.Create()
    ec.ImportFromPem(spkiPem)
    ec.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)

let private signEd25519 (rawPriv: byte[]) (message: byte[]) : byte[] =
    let signer = Ed25519Signer()
    signer.Init(true, Ed25519PrivateKeyParameters(rawPriv, 0))
    signer.BlockUpdate(message, 0, message.Length)
    signer.GenerateSignature()

/// Recover the raw 32-byte Ed25519 public key from a SubjectPublicKeyInfo
/// PEM. The Ed25519 SPKI is a fixed 44-byte DER document whose last 32
/// bytes are the key (RFC 8410), so the extraction is a length check plus
/// a slice — no ASN.1 parser needed.
let private ed25519RawFromPem (pem: string) : Result<byte[], string> =
    try
        let body =
            pem.Split('\n')
            |> Array.filter (fun l -> not (l.TrimStart().StartsWith("-----")))
            |> Array.map _.Trim()
            |> String.concat ""

        let der = Convert.FromBase64String body

        if der.Length <> 44 then
            Error $"expected a 44-byte Ed25519 SubjectPublicKeyInfo, found {der.Length} bytes"
        else
            Ok der[12..]
    with ex ->
        Error $"could not read Ed25519 public key: {ex.Message}"

let private verifyEd25519 (rawPub: byte[]) (message: byte[]) (signature: byte[]) : bool =
    let verifier = Ed25519Signer()
    verifier.Init(false, Ed25519PublicKeyParameters(rawPub, 0))
    verifier.BlockUpdate(message, 0, message.Length)
    verifier.VerifySignature(signature)

// ── signer ──────────────────────────────────────────────────────────────

/// An `IStatementEnvelopeSigner` over the deployment's own signing key —
/// the same `ISecretStore` material, key id and algorithm the Phase 40
/// `IArtefactSigner` uses, so one composed key story covers both the
/// detached-JWS artefacts and the DSSE envelopes.
///
/// Stateless between calls (GP 12 rule 4): every sign re-reads the key,
/// so rotating it in the store takes effect immediately. A key absent from
/// a writable store is auto-provisioned on first use, exactly as the
/// artefact signer does; a read-only store with no seeded key surfaces as
/// `Error`, never an exception.
let fromSecretStore (secrets: ISecretStore) (keyId: string) (algorithm: SigningAlgorithm) : IStatementEnvelopeSigner =
    { new IStatementEnvelopeSigner with
        member _.KeyId() = keyId

        member _.SignPreAuthenticated(pae: byte[]) : Async<Result<EnvelopeKeySignature, string>> = async {
            match! SigningKeyMaterial.loadOrCreate secrets keyId algorithm with
            | Error e -> return Error $"signing key unavailable: {e}"
            | Ok key ->
                try
                    let signature =
                        match key.Algorithm with
                        | EcdsaP256 -> signEcdsaDer key.PrivateKey pae
                        | Ed25519 -> signEd25519 key.PrivateKey pae

                    return Ok { KeyId = keyId; Signature = signature }
                with ex ->
                    return Error $"crypto failure: {ex.Message}"
        }
    }

// ── offline verification ────────────────────────────────────────────────

/// Verify an envelope's signature against a public key. **Offline** — the
/// envelope and the key are the whole input; no store, no deployment, no
/// network. Structural checks (payload type, predicate type, subject
/// digest) are the caller's `DsseEnvelope.checkShape` pass; this is the
/// cryptographic half.
let verifySignature (publicKey: PublicKeyMetadata) (envelope: DsseEnvelope) : EnvelopeVerdict =
    match DsseEnvelope.signatureFor publicKey.KeyId envelope with
    | None -> EnvelopeUnsignedForKey publicKey.KeyId
    | Some entry ->
        match DsseEnvelope.signatureBytes entry, DsseEnvelope.paeOf envelope with
        | Error e, _ -> EnvelopeMalformed e
        | _, Error e -> EnvelopeMalformed e
        | Ok signature, Ok pae ->
            try
                // An unreadable verifying key is reported as such, never as
                // a failed signature: "I cannot check this" and "this is
                // wrong" are different answers, and collapsing them would
                // send a holder hunting for tampering that never happened.
                let checked' =
                    match publicKey.Algorithm with
                    | EcdsaP256 -> Ok(verifyEcdsaDer publicKey.Pem pae signature)
                    | Ed25519 ->
                        ed25519RawFromPem publicKey.Pem
                        |> Result.map (fun raw -> verifyEd25519 raw pae signature)

                match checked' with
                | Error reason -> EnvelopeMalformed $"verifying key unreadable: {reason}"
                | Ok true -> EnvelopeValid
                | Ok false -> EnvelopeSignatureInvalid
            with _ ->
                // A malformed signature blob makes the primitive throw.
                // That is a failed verification, not an unreadable
                // envelope — never a pass either way.
                EnvelopeSignatureInvalid

/// Full offline verification. Returns the statement's raw predicate JSON
/// **only** on a complete pass, so no caller can reach an unverified
/// predicate by ignoring a verdict (the no-unverified-read-as-pass
/// discipline).
///
/// **The signature is checked FIRST, then the shape**, and the order is
/// load-bearing rather than incidental: it is what makes the structural
/// verdicts mean what they say. `EnvelopeSubjectMismatch` claims "a
/// correctly-signed statement about a different artefact" — a claim that
/// would be false if the subject were compared before anyone established
/// the document was signed at all, and a holder told their document is
/// about the wrong artefact draws a very different conclusion from one
/// told it does not verify.
let verify
    (publicKey: PublicKeyMetadata)
    (expectation: EnvelopeExpectation)
    (envelope: DsseEnvelope)
    : Result<string, EnvelopeVerdict> =
    match verifySignature publicKey envelope with
    | EnvelopeValid ->
        match DsseEnvelope.checkShape expectation envelope with
        | Error verdict -> Error verdict
        | Ok statement -> Ok statement.PredicateJson
    | verdict -> Error verdict

/// Verify an envelope supplied as JSON text — the shape a holder actually
/// receives (a `.dsse.json` file, an HTTP body). Parse failures are
/// `EnvelopeMalformed`, never a pass.
let verifyJson
    (publicKey: PublicKeyMetadata)
    (expectation: EnvelopeExpectation)
    (json: string)
    : Result<string, EnvelopeVerdict> =
    match DsseEnvelope.parse json with
    | Error e -> Error(EnvelopeMalformed e)
    | Ok envelope -> verify publicKey expectation envelope

/// The UTF-8 statement bytes an envelope carries, WITHOUT verifying
/// anything. Named so a call site reads as the hazard it is — for
/// diagnostics and error reporting only. Every path that acts on a
/// statement goes through `verify`.
let readUnverifiedStatement (envelope: DsseEnvelope) : Result<string, string> =
    DsseEnvelope.payloadBytes envelope |> Result.map Encoding.UTF8.GetString