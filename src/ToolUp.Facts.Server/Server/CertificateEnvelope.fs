// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Facts.CertificateEnvelope

open System
open System.Text.Json
open System.Text.Json.Nodes
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
// **Two projections, one subject.** The direct certificate projects
// through the predicate type immediately below; the attested certificate
// — the same body sealed through the application signing seam — projects
// through its own predicate type at the foot of this file. They differ
// only in what travels alongside the body, and they publish the SAME
// in-toto subject, because the body builder is shared and both bodies are
// byte-identical. A holder therefore claim-checks either document against
// the one root id they already possess. See the file's closing section.
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

// ─── The attested certificate's projection (Phase 692) ──────────────────
//
// Everything above wraps a `GroundingCertificate` — the body sealed with a
// detached JWS through `IArtefactSigner`. A holder of the ATTESTED form
// (Phase 682: the same body sealed through the application signing seam,
// with the purpose and the attestation level framed into the signed bytes)
// had no standard-tooling carrier at all, and had to fall back to the
// direct path to get one — which means falling back to a document that
// does not carry the level. The levels-bound certificate was the one a
// relying party most needed to be able to read with stock tooling, and it
// was the one that could not be exported.
//
// Phase 682 pinned that both issuers produce **byte-identical bodies**, so
// this is a projection rather than a second format: same subject, same
// canonical body, same envelope family, same verification path. What
// differs is the predicate — and that difference is why it gets its own
// predicate type rather than reusing the one above. A verifier keys on
// `predicateType` to decide what shape it is about to read; two different
// shapes under one URI would make that key meaningless, and the existing
// `EnvelopePredicateTypeMismatch` verdict exists for exactly this.
//
// **The level and key id are SURFACED, not asserted.** Both are already
// inside the signed bytes — the level in the seam's framing, the key id in
// the body — which is what makes them trustworthy. But they are reachable
// there only by a reader that understands the seam's framing and this
// SDK's DU serialisation, and the point of a standard-tooling carrier is
// that such a reader is not required. So the predicate publishes both as
// plain strings beside the certificate, in the stable wire names the
// signature covers (`AttestationLevel.name`). They are a projection of the
// seal, never an independent claim: `verifyAndReadAttested` reconciles
// every surfaced field against the sealed certificate and refuses a
// document where they disagree, so a reader who trusts a surfaced field is
// never trusting something the seal did not cover.

/// The versioned predicate type URI for an ATTESTED grounding certificate
/// — the levels-bound form carried as an in-toto predicate. Deliberately
/// distinct from `PredicateType`: the predicate shape differs, and a
/// verifier keys on this URI to know what it is reading.
[<Literal>]
let AttestedPredicateType =
    "https://toolup-forge.io/attestations/attested-grounding-certificate/v1"

/// The predicate member carrying the certificate record itself.
[<Literal>]
let CertificateField = "certificate"

/// The predicate member carrying the attestation level, as the stable wire
/// name the signature covers (`AttestationLevel.name`).
[<Literal>]
let AttestationLevelField = "attestationLevel"

/// The predicate member carrying the purpose the seal was minted for.
[<Literal>]
let PurposeField = "purpose"

/// The predicate member carrying the signing-key id bound into the body.
[<Literal>]
let SigningKeyIdField = "signingKeyId"

/// The predicate JSON for an attested certificate: the certificate record,
/// plus the three facts the seal binds published as plain strings for a
/// reader that does not know this SDK's framing.
let attestedPredicateJson (certificate: AttestedGroundingCertificate) : string =
    let o = JsonObject()

    o[CertificateField] <- JsonNode.Parse(JsonSerializer.Serialize(certificate, jsonOptions))
    o[AttestationLevelField] <- JsonValue.Create(AttestationLevel.name certificate.Envelope.Level)
    o[PurposeField] <- JsonValue.Create(certificate.Envelope.Purpose)
    o[SigningKeyIdField] <- JsonValue.Create(certificate.Body.DeploymentKeyId)
    o.ToJsonString()

/// The in-toto statement JSON for an attested certificate, unsigned.
/// Exposed for tests and for a caller signing through its own path.
let attestedStatementJson (certificate: AttestedGroundingCertificate) : string =
    DsseEnvelope.statementJson
        [ subjectFor certificate.Body.Root ]
        AttestedPredicateType
        (attestedPredicateJson certificate)

/// Export an attested certificate as a signed DSSE envelope. The same
/// subject, the same envelope machinery and the same signing seam the
/// direct projection uses — only the predicate differs.
let exportAttested
    (signer: IStatementEnvelopeSigner)
    (certificate: AttestedGroundingCertificate)
    : Async<Result<DsseEnvelope, string>> =
    DsseEnvelope.sign
        signer
        [ subjectFor certificate.Body.Root ]
        AttestedPredicateType
        (attestedPredicateJson certificate)

/// What a holder requires of an attested envelope. `expectedRoot` is the
/// fact / answer id the holder independently possesses — the SAME value
/// they would bring to `expectation`, because both projections publish the
/// same subject over the same body.
let attestedExpectation (expectedRoot: string option) : EnvelopeExpectation = {
    PredicateType = AttestedPredicateType
    SubjectDigest = expectedRoot
}

/// Read an attested certificate out of a predicate that has ALREADY been
/// verified, reconciling every surfaced field against the sealed
/// certificate.
///
/// A surfaced field that is absent, or that contradicts the seal, is
/// `EnvelopeMalformed` — not a signature failure and not a pass. The
/// document may be perfectly signed by the envelope key and still be
/// unreadable as an attested certificate, because it says two
/// incompatible things about the same certificate and nothing here can
/// choose between them. "I cannot read this" is the honest verdict, and
/// the reason is named in full.
let private readAttestedPredicate (predicate: string) : Result<AttestedGroundingCertificate, EnvelopeVerdict> =
    try
        let node = JsonNode.Parse predicate

        match node[CertificateField] with
        | null ->
            Error(EnvelopeMalformed $"predicate is not an attested grounding certificate (no '{CertificateField}')")
        | certificateNode ->
            let certificate =
                JsonSerializer.Deserialize<AttestedGroundingCertificate>(certificateNode.ToJsonString(), jsonOptions)

            if obj.ReferenceEquals(certificate.Body.Format, null) then
                Error(EnvelopeMalformed "predicate is not an attested grounding certificate (no format discriminator)")
            elif certificate.Body.Format <> GroundingCertificate.Format then
                Error(EnvelopeMalformed $"unsupported certificate format: {certificate.Body.Format}")
            else
                let surfaced (field: string) =
                    match node[field] with
                    | null -> None
                    | value ->
                        try
                            Some(value.GetValue<string>())
                        with _ ->
                            None

                let reconciled =
                    [
                        AttestationLevelField, AttestationLevel.name certificate.Envelope.Level
                        PurposeField, certificate.Envelope.Purpose
                        SigningKeyIdField, certificate.Body.DeploymentKeyId
                    ]
                    |> List.tryPick (fun (field, sealedValue) ->
                        match surfaced field with
                        | None -> Some $"predicate omits the surfaced '{field}' the attested projection publishes"
                        | Some value when value <> sealedValue ->
                            Some
                                $"predicate's surfaced '{field}' is '{value}' but the sealed certificate carries '{sealedValue}'"
                        | Some _ -> None)

                match reconciled with
                | Some reason -> Error(EnvelopeMalformed reason)
                | None -> Ok certificate
    with ex ->
        Error(EnvelopeMalformed $"predicate is not a readable attested grounding certificate: {ex.Message}")

/// Verify an attested certificate's envelope **offline** against a public
/// key and read the certificate back.
///
/// The SAME verification path the direct projection uses
/// (`DsseEnvelopeSigning.verify`) — signature over the PAE first, then
/// payload type, predicate type and subject digest — so the tamper
/// verdicts are the same distinct ones, produced by the same code, and
/// there is no third implementation to keep in step.
///
/// **What "offline" covers, precisely.** The envelope signature is checked
/// against the public key and nothing else: no store, no deployment, no
/// network. The certificate's own APPLICATION seal is a second,
/// independent check the returned document carries rather than replaces —
/// and that one is deliberately NOT offline, because refusing a revoked
/// key means consulting the recorded key history
/// (`GroundingCertificate.verifyAttested` over a composed
/// `IApplicationSigner`). A holder with only a public key gets the
/// envelope's answer; a holder with the deployment's key history gets
/// both.
let verifyAndReadAttested
    (publicKey: PublicKeyMetadata)
    (expectedRoot: string option)
    (envelope: DsseEnvelope)
    : Result<AttestedGroundingCertificate, EnvelopeVerdict> =
    match DsseEnvelopeSigning.verify publicKey (attestedExpectation expectedRoot) envelope with
    | Error verdict -> Error verdict
    | Ok predicate -> readAttestedPredicate predicate

/// Verify an attested envelope supplied as JSON text — the shape a holder
/// receives on the wire or as a file.
let verifyAndReadAttestedJson
    (publicKey: PublicKeyMetadata)
    (expectedRoot: string option)
    (json: string)
    : Result<AttestedGroundingCertificate, EnvelopeVerdict> =
    match DsseEnvelope.parse json with
    | Error e -> Error(EnvelopeMalformed e)
    | Ok envelope -> verifyAndReadAttested publicKey expectedRoot envelope

/// The exact payload bytes the attested certificate's own application seal
/// covers, once it has been read back out of a verified envelope — the
/// canonical body bytes, which is what `GroundingCertificate.verifyAttested`
/// and `IApplicationSigner.VerifyPayload` take.
///
/// Note the seal does not cover these bytes alone: it covers their framing
/// with the purpose and the attestation level
/// (`ApplicationPayload.canonicalBytes`), which is what makes those two
/// facts trustworthy rather than decorative. The framing is the seam's to
/// build, so a holder hands over the payload and lets it frame.
///
/// The value is byte-identical to `sealedBytes` over a direct certificate
/// with the same body — that identity is Phase 682's, and this projection
/// inherits rather than re-establishes it.
let attestedSealedBytes (certificate: AttestedGroundingCertificate) : byte[] =
    GroundingCertificate.canonicalBytes certificate.Body

// ─── Routing between the two projections (Phase 710) ────────────────────
//
// Two projections now publish over one subject, and a reader that holds
// both readers has to decide which to run. The decision is made HERE, on
// the `predicateType` the document declares, and the shape of that decision
// is as load-bearing as the readers themselves:
//
//   * **The document nominates nothing.** A caller does not say which
//     projection to expect, so a peer cannot steer a reader by asserting a
//     shape beside its document. What the caller supplies is a key; what
//     the document supplies is a claim about its own shape, and every route
//     re-establishes that claim INSIDE the signed bytes before anything is
//     believed.
//   * **There is no try-one-then-the-other.** A fallback reader produces a
//     verdict naming whichever attempt happened to run second, which is a
//     lie about what was checked: a holder told "predicate type mismatch,
//     expected the attested type" would conclude their document was the
//     wrong shape when in fact it was the right shape with a bad signature.
//     One document, one route, one verdict.
//   * **A third shape is refused as a third shape**, not as a mismatch
//     against whichever of the two was tried. `UnknownProjection` carries
//     the type it read, so an operator holding a statement from some other
//     tool is told what they are holding rather than what they are not.
//
// **Reading a field out of the UNVERIFIED payload is safe here, and only
// because of what follows it.** The surrounding discipline is that a
// document never nominates how it is checked, and this reads a document's
// own claim before any signature has been established — so it is worth
// saying exactly why that is not a hole. Routing chooses which
// `EnvelopeExpectation` is applied; every route then verifies the signature
// over the PAE FIRST and re-checks the predicate type against that
// expectation inside the signed statement. A document that lies about its
// own predicate type is therefore routed to a reader that refuses it. The
// worst a liar achieves is being refused with one verdict rather than
// another, and every route is fail-closed.

/// Which of the two published projections a document declares itself to
/// be — read from its own statement, and believed only to the extent of
/// choosing which fail-closed reader runs.
type DeclaredProjection =
    /// The direct projection (`PredicateType`) — a certificate sealed with
    /// a detached JWS, carrying no attestation level.
    | DirectProjection
    /// The attested projection (`AttestedPredicateType`) — the same body
    /// sealed through the application signing seam, with the attestation
    /// level bound into the signed bytes.
    | AttestedProjection
    /// Neither: a statement of some other shape entirely. Names the type it
    /// declared, because "this is not a certificate I publish a reader for"
    /// and "this is the wrong one of my two" are different facts.
    | UnknownProjection of predicateType: string

/// The projection a DSSE document declares. `EnvelopeMalformed` when the
/// document cannot be read far enough to have declared anything at all —
/// never a pass, and never a guess at which projection was meant.
let declaredProjection (json: string) : Result<DeclaredProjection, EnvelopeVerdict> =
    match DsseEnvelope.parse json with
    | Error e -> Error(EnvelopeMalformed e)
    | Ok envelope ->
        match DsseEnvelope.readStatement envelope with
        | Error verdict -> Error verdict
        | Ok statement ->
            if statement.PredicateType = PredicateType then
                Ok DirectProjection
            elif statement.PredicateType = AttestedPredicateType then
                Ok AttestedProjection
            else
                Ok(UnknownProjection statement.PredicateType)

/// The holder's expectation for a declared projection — the second,
/// crypto-free self-consistency check a reader applies once the signature
/// is established. Kept beside the routing so the two cannot drift: a leg
/// added here without its expectation would check the wrong predicate type
/// against the signed statement.
let expectationFor (projection: DeclaredProjection) (expectedRoot: string option) : EnvelopeExpectation option =
    match projection with
    | DirectProjection -> Some(expectation expectedRoot)
    | AttestedProjection -> Some(attestedExpectation expectedRoot)
    | UnknownProjection _ -> None