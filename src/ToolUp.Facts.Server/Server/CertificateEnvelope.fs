// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Facts.CertificateEnvelope

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Grounding certificate as a standard signed statement ───────────────
//
// A grounding certificate already verifies offline — but only against
// this substrate's own verifier and its bespoke canonical-JSON shape. The
// industry-standard artefact for exactly this claim is the DSSE-wrapped
// in-toto Statement, and both are open, vendor-neutral specifications:
//
//   * **subject** — the certificate's root: the fact or answer the
//     certificate is issued over. A fact id is content-addressed
//     (`FactId = hash(subject, metric, period, method, inputHashes)`), so
//     it is deployment-independent by construction and a holder
//     claim-checks the statement against the id they already hold, with
//     nothing to translate.
//   * **predicate** — the certificate itself: body (chain nodes, method
//     identities, disclosure stances, policy refs, deployment key id)
//     plus the detached-JWS seal it was issued with, so a holder can
//     check either path from the one document.
//   * **predicateType** — a versioned URI naming that shape, published as
//     an open interchange format.
//
// **Selective disclosure carries over unchanged.** The envelope wraps the
// body the issuer produced; a withheld fact is already collapsed to id +
// policy ref + stance before it ever reaches here, and no fact value
// appears in a certificate at all. Wrapping cannot widen disclosure
// because it does not re-derive anything.
//
// **Nothing here is reachable unless a deployment asks for it.** No
// composition changes, no DI registration, no hosted service: a
// deployment that never exports an envelope pays nothing (GP 13), and a
// deployment that never issues certificates is untouched.
//
// The format doc is `docs/security/grounding-certificate-envelope.md`.

/// The versioned predicate type URI — the open interchange identifier for
/// a grounding certificate carried as an in-toto predicate. A verifier
/// keys on this, so it does not change without a new version segment and
/// a compatibility note.
[<Literal>]
let PredicateType = "https://toolup-forge.io/attestations/grounding-certificate/v1"

/// The in-toto digest-algorithm key used when the certificate root is a
/// content-addressed id — a SHA-256 hex digest, which is what
/// `FactId.compute` produces.
[<Literal>]
let ContentAddressedDigestKey = "sha256"

/// The digest-algorithm key used when the root is NOT a content hash (an
/// answer's conversation-message id, which is an opaque store identity).
/// Naming such a value `sha256` would be a false claim, so it gets a key
/// that says what it is. The digest VALUE is the id verbatim either way —
/// that is what makes the subject checkable by a holder who has only the
/// id.
[<Literal>]
let OpaqueIdDigestKey = "toolupContentId"

let private jsonOptions = FableConverters.create ()

/// `true` when `id` has the shape of a lowercase SHA-256 hex digest — the
/// discriminator between the two digest keys above. A shape test, not a
/// trust decision: it decides how the id is LABELLED, never whether the
/// statement is believed.
let private isSha256Hex (id: string) =
    not (isNull id)
    && id.Length = 64
    && id |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

/// The in-toto subject for a certificate root.
let subjectFor (root: string) : InTotoSubject = {
    Name = root
    Digest = [
        (if isSha256Hex root then
             ContentAddressedDigestKey
         else
             OpaqueIdDigestKey),
        root
    ]
}

/// The predicate JSON for a certificate — the certificate record itself
/// (body + detached-JWS seal) in the same versioned shape the substrate
/// already publishes as its open interchange format.
let predicateJson (certificate: GroundingCertificate) : string =
    JsonSerializer.Serialize(certificate, jsonOptions)

/// The in-toto statement JSON for a certificate, unsigned. Exposed for
/// tests and for a caller that wants to sign through its own path.
let statementJson (certificate: GroundingCertificate) : string =
    DsseEnvelope.statementJson [ subjectFor certificate.Body.Root ] PredicateType (predicateJson certificate)

/// Export a certificate as a signed DSSE envelope. One signature, over
/// the statement's pre-authentication encoding, from the deployment's own
/// key (`DsseEnvelopeSigning.fromSecretStore` over the same key id the
/// certificate issuer signs with).
let export
    (signer: IStatementEnvelopeSigner)
    (certificate: GroundingCertificate)
    : Async<Result<DsseEnvelope, string>> =
    DsseEnvelope.sign signer [ subjectFor certificate.Body.Root ] PredicateType (predicateJson certificate)

/// What a holder requires of an envelope. `expectedRoot` is the fact /
/// answer id the holder independently possesses; passing `None` skips the
/// subject check, which is only right when the caller has no independent
/// handle on the artefact.
let expectation (expectedRoot: string option) : EnvelopeExpectation = {
    PredicateType = PredicateType
    SubjectDigest = expectedRoot
}

/// Verify an envelope **offline** against a public key and read the
/// certificate back. The certificate is returned only on a complete pass
/// — signature valid over this envelope's PAE, predicate type as
/// expected, and (when one was supplied) the subject digest matching the
/// root the holder brought. Every refusal is a typed
/// `EnvelopeVerdict`, so no caller can read a failure as a pass.
///
/// Needs the envelope and the public key, nothing else: no fact store, no
/// deployment access, no network.
let verifyAndRead
    (publicKey: PublicKeyMetadata)
    (expectedRoot: string option)
    (envelope: DsseEnvelope)
    : Result<GroundingCertificate, EnvelopeVerdict> =
    match DsseEnvelopeSigning.verify publicKey (expectation expectedRoot) envelope with
    | Error verdict -> Error verdict
    | Ok predicate ->
        try
            match JsonSerializer.Deserialize<GroundingCertificate>(predicate, jsonOptions) with
            | certificate when obj.ReferenceEquals(certificate.Body.Format, null) ->
                Error(EnvelopeMalformed "predicate is not a grounding certificate (no format discriminator)")
            | certificate when certificate.Body.Format <> GroundingCertificate.Format ->
                Error(EnvelopeMalformed $"unsupported certificate format: {certificate.Body.Format}")
            | certificate -> Ok certificate
        with ex ->
            Error(EnvelopeMalformed $"predicate is not a readable grounding certificate: {ex.Message}")

/// Verify an envelope supplied as JSON text — the shape a holder receives
/// on the wire or as a file.
let verifyAndReadJson
    (publicKey: PublicKeyMetadata)
    (expectedRoot: string option)
    (json: string)
    : Result<GroundingCertificate, EnvelopeVerdict> =
    match DsseEnvelope.parse json with
    | Error e -> Error(EnvelopeMalformed e)
    | Ok envelope -> verifyAndRead publicKey expectedRoot envelope

/// The exact bytes the certificate's own detached-JWS seal covers, once
/// the certificate has been read back out of a verified envelope. A
/// holder with an `IArtefactVerifier` can check that second, independent
/// seal (`GroundingCertificate.verify`) — the envelope carries it rather
/// than replacing it.
let sealedBytes (certificate: GroundingCertificate) : byte[] =
    GroundingCertificate.canonicalBytes certificate.Body