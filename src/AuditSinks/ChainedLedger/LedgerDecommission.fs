// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuditSinks.LedgerDecommission

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.ChainedLedger
open ToolUp.Platform.AuditSinks.LedgerChain
open ToolUp.Platform.BlobStorage

// ─── The destruction certificate ────────────────────────────────────────
//
// An engagement-scoped deployment ends, and the buyer asks for the one
// artefact nothing in this SDK produced until now: *show me it is gone.*
// Not a ticket, not an email — a document they can take away and check
// years later, against public key material, with no access to the
// deployment that made it and no trust in the party that ran it.
//
// The three claims that document has to carry, and the reason each is
// separately necessary:
//
//   1. **The chain is CLOSED at head H.** Phase 678's terminal op says
//      so, and says it over framed bytes that include the record count —
//      so a chain later truncated, extended, or re-presented at a
//      different length contradicts the op rather than agreeing with it.
//   2. **That head is SIGNED.** The chain alone is tamper-evident against
//      an editor who cannot rewrite the tail (`LedgerChain`'s own header
//      states the bound exactly); the head signature is what pins the
//      chain the ledger actually wrote. Without it, "closed at H" is a
//      claim about a head anyone could have produced.
//   3. **The RETIREMENT is bound to that head.** A closure proves a ledger
//      ended; it does not, by itself, say which deployment ended. The
//      retirement reference names the sealed deploy record, and this
//      certificate proves the two describe one act rather than two
//      documents that happen to travel together.
//
// **It rides Phase 677's export machinery whole (GP 1).** The DSSE
// envelope, the in-toto statement assembly, the PAE, the SHA-256 content
// id and the `IStatementEnvelopeSigner` seam are all that phase's, used
// as they are. What is added here is a predicate type and a verifier —
// a shape, not a primitive.
//
// **What the certificate does NOT prove.** Written plainly, because a
// reader who over-reads it is worse off than one who knows its bound, and
// because "destruction certificate" is a name that invites over-reading.
// It proves that the party holding the signing key asserted, at a
// recorded moment, that this deployment's audit ledger was closed at a
// named head and its deploy record retired. It proves nothing about
// whether the data was erased, whether a copy of the image survives, or
// whether the deployment stopped serving. Those are operational facts,
// and no signature over a digest can reach them. What the deployment CAN
// enforce from this document is the next boot: Phase 657's preflight
// refuses to serve a retired record (`DeployRetirement`), which is the
// difference between a claim and a control.
//
// **Nothing here is reachable unless a deployment asks for it (GP 13).**
// No composition changes, no DI registration, no route, no key material:
// a deployment that never decommissions never constructs any of this, and
// one that closes an unsigned ledger gets an honest `DecommissionUnproven`
// rather than a false pass.

// ── The certificate ─────────────────────────────────────────────────────

/// The closure of one deployment's audit ledger, as a self-contained
/// document.
///
/// Self-contained is the whole point: a holder needs this value and a
/// public key, and nothing else. The head is carried verbatim from the
/// ledger — never re-taken — so the signature inside it remains the one
/// Phase 658 minted and stays checkable against the same key.
type DecommissionCertificate = {
    /// Wire version of this shape, so a reader refuses a document it was
    /// not written to interpret rather than parsing it hopefully.
    SchemaVersion: int
    /// The terminal op that closed the chain.
    TerminalOp: LedgerTerminalOp
    /// The ledger head the terminal op closed at, verbatim.
    Head: LedgerHead
    /// The retirement reference the sealed deploy record carries.
    Retirement: DeployRetirement
}

/// What a decommission certificate was found to be.
///
/// Five cases, and none can be read as another. In particular
/// `DecommissionUnproven` is NOT a failure and NOT a pass: it is the
/// answer for a document whose structure is sound and whose signatures
/// this holder could not check — the state a holder reaches by bringing
/// no key, and the state an unsigned closure is in permanently.
type DecommissionVerification =
    /// Closed at the named head, both signatures valid, retirement bound.
    /// The only verdict a relying party should act on as proof.
    | DecommissionVerified of deployId: string * recordCount: int64 * headDigest: string * keyId: string
    /// Structurally sound, and a signature did not verify or could not be
    /// checked. Carries the head's status and the terminal op's,
    /// separately — they are signed over different bytes and can fail
    /// independently.
    | DecommissionUnproven of headSignature: HeadSignatureStatus * opSignature: HeadSignatureStatus
    /// The document does not show a closed chain: the terminal op's
    /// digest does not recompute, or it does not close the head the
    /// certificate carries.
    | DecommissionNotClosed of detail: string
    /// The retirement does not belong to the closure it travels with.
    | DecommissionUnbound of detail: string
    /// The document could not be READ far enough to judge — a malformed
    /// envelope, a statement of another shape, an unparseable predicate.
    /// Never a pass.
    | DecommissionUnreadable of position: string * reason: string

module DecommissionVerification =
    /// `true` only for `DecommissionVerified`. Exists so no caller writes
    /// `<> DecommissionNotClosed _` and treats an unproven document as
    /// proof.
    let isVerified =
        function
        | DecommissionVerified _ -> true
        | _ -> false

    let private signatureLabel =
        function
        | HeadUnsigned -> "unsigned"
        | HeadSignatureValid(keyId, algorithm) -> sprintf "valid (%s / %s)" keyId algorithm
        | HeadSignatureInvalid(keyId, algorithm) -> sprintf "INVALID (%s / %s)" keyId algorithm
        | HeadSignatureUnverifiable(algorithm, reason) -> sprintf "unverifiable (%s): %s" algorithm reason

    let describe =
        function
        | DecommissionVerified(deployId, recordCount, headDigest, keyId) ->
            sprintf
                "decommission verified for deploy %s: the ledger is closed at %d/%s and signed under key %s"
                deployId
                recordCount
                headDigest
                keyId
        | DecommissionUnproven(headSignature, opSignature) ->
            sprintf
                "decommission unproven: ledger head signature %s; terminal op signature %s"
                (signatureLabel headSignature)
                (signatureLabel opSignature)
        | DecommissionNotClosed detail -> sprintf "the certificate does not show a closed chain: %s" detail
        | DecommissionUnbound detail -> sprintf "the retirement is not bound to this closure: %s" detail
        | DecommissionUnreadable(position, reason) -> sprintf "unreadable at %s: %s" position reason

/// The wire version this module writes and reads.
[<Literal>]
let SchemaVersion = 1

let private jsonOptions = FableConverters.create ()

// ── Producing ───────────────────────────────────────────────────────────

/// The retirement reference implied by a terminal op.
///
/// A total function of the op, which is what makes the binding checkable
/// rather than asserted: a holder recomputes it from the op in the
/// certificate and compares, so a retirement naming a different head, a
/// different actor or a different moment is a finding rather than a
/// second opinion.
let retirementFor (op: LedgerTerminalOp) : DeployRetirement =
    DeployRetirement.create
        op.DeployRecordDigest
        op.Digest
        op.HeadDigest
        op.RecordCount
        op.ClosedBy
        op.ClosedAt
        op.Reason

/// The certificate for a closed ledger, read from storage.
///
/// Refuses an OPEN ledger, and refuses one whose head disagrees with its
/// terminal op — both for the reason Phase 677 refuses to export from a
/// broken chain: a document a counterparty can never make sound is worse
/// than no document, because it will be filed as though it were one.
let certificateFor
    (settings: ChainedLedgerSettings)
    (storage: IBlobStorage)
    : Async<Result<DecommissionCertificate, string>> =
    async {
        match! ChainedLedger.readTerminalOp settings storage with
        | Error message -> return Error message
        | Ok None ->
            return
                Error
                    "refusing to issue a decommission certificate: this ledger is open — close it with ChainedLedger.close first"
        | Ok(Some op) ->
            match! ChainedLedger.read settings storage with
            | Error message -> return Error message
            | Ok stored ->
                match stored.Head with
                | None -> return Error "refusing to issue a decommission certificate: the ledger has no head pointer"
                | Some head ->
                    if not (LedgerTerminalOp.closesChain head.RecordCount head.HeadDigest op) then
                        return
                            Error(
                                sprintf
                                    "refusing to issue a decommission certificate: the terminal op closes %d/%s and the head pointer records %d/%s"
                                    op.RecordCount
                                    op.HeadDigest
                                    head.RecordCount
                                    head.HeadDigest
                            )
                    else
                        return
                            Ok {
                                SchemaVersion = SchemaVersion
                                TerminalOp = op
                                Head = head
                                Retirement = retirementFor op
                            }
    }

// ── The verifier ────────────────────────────────────────────────────────

/// The status of one detached signature over `bytes`, in the ledger's own
/// vocabulary.
///
/// Shared by the head and the terminal op because the question is
/// identical — present and valid, present and wrong, present and
/// uncheckable, or absent — and a second vocabulary saying the same four
/// things would only give a caller two ways to spell one answer.
let private signatureStatus
    (verifier: ILedgerHeadVerifier option)
    (keyId: string option)
    (algorithm: string option)
    (signature: string option)
    (bytes: byte[])
    : Async<HeadSignatureStatus> =
    async {
        match signature, keyId, algorithm with
        | None, _, _ -> return HeadUnsigned
        | Some _, None, _
        | Some _, _, None ->
            return HeadSignatureUnverifiable("unknown", "a signature is present without a key id or algorithm")
        | Some signature, Some keyId, Some algorithm ->
            match verifier with
            | None -> return HeadSignatureUnverifiable(algorithm, "signed, but no verifier was supplied")
            | Some verifier ->
                // A signature that is not decodable is UNVERIFIABLE, not
                // invalid: "I could not read this" and "this is wrong" are
                // different answers, and the ledger's own vocabulary keeps
                // them apart.
                let decoded =
                    try
                        Ok(Convert.FromBase64String signature)
                    with ex ->
                        Error ex.Message

                match decoded with
                | Error reason ->
                    return HeadSignatureUnverifiable(algorithm, sprintf "signature is not readable: %s" reason)
                | Ok raw ->
                    match! verifier.Verify(keyId, algorithm, bytes, raw) with
                    | Ok true -> return HeadSignatureValid(keyId, algorithm)
                    | Ok false -> return HeadSignatureInvalid(keyId, algorithm)
                    | Error reason -> return HeadSignatureUnverifiable(algorithm, reason)
    }

/// Verify a decommission certificate — cold, from the document alone.
///
/// **Every question is answered from the certificate plus public key
/// material.** No storage is read, no ledger is consulted, nothing is
/// re-signed: a holder years later, with no access to the deployment,
/// asks exactly what the deployment could ask.
///
/// The order is deliberate. Structure first (is this a closed chain?),
/// then binding (is the retirement this closure's?), then signatures —
/// so a document that is internally wrong is reported where it is wrong,
/// rather than as a signature failure that would send a reader looking
/// for a key problem.
///
/// Pass `None` for the verifier when there is no key material to hand: a
/// structurally sound document then reports `DecommissionUnproven` with
/// the reason, never a quiet pass.
let verifyCertificate
    (verifier: ILedgerHeadVerifier option)
    (certificate: DecommissionCertificate)
    : Async<DecommissionVerification> =
    async {
        if certificate.SchemaVersion <> SchemaVersion then
            return
                DecommissionUnreadable(
                    "certificate/schemaVersion",
                    sprintf
                        "the certificate declares schema version %d, this reader understands %d"
                        certificate.SchemaVersion
                        SchemaVersion
                )
        elif obj.ReferenceEquals(certificate.Head, null) then
            return DecommissionUnreadable("certificate/head", "the certificate carries no ledger head")
        elif obj.ReferenceEquals(certificate.TerminalOp, null) then
            return DecommissionUnreadable("certificate/terminalOp", "the certificate carries no terminal op")
        elif obj.ReferenceEquals(certificate.Retirement, null) then
            return DecommissionUnreadable("certificate/retirement", "the certificate carries no retirement")
        else
            let op = certificate.TerminalOp
            let head = certificate.Head

            if not (LedgerTerminalOp.digestHolds op) then
                return
                    DecommissionNotClosed(
                        sprintf
                            "the terminal op stores digest %s and recomputes to %s"
                            op.Digest
                            (computeTerminalDigest op)
                    )
            elif not (LedgerTerminalOp.closesChain head.RecordCount head.HeadDigest op) then
                return
                    DecommissionNotClosed(
                        sprintf
                            "the terminal op closes %d/%s and the head it travels with records %d/%s"
                            op.RecordCount
                            op.HeadDigest
                            head.RecordCount
                            head.HeadDigest
                    )
            elif certificate.Retirement <> retirementFor op then
                // Compared against the retirement the op IMPLIES, so every
                // field is checked at once — the record digest, the
                // terminal-op digest, the head, the count, the actor, the
                // moment and the reason. A per-field ladder here would be
                // a list of ways to forget one.
                return
                    DecommissionUnbound(
                        sprintf
                            "the retirement names record %s / terminal op %s / head %d/%s, and the terminal op in this certificate implies record %s / terminal op %s / head %d/%s"
                            certificate.Retirement.DeployRecordDigest
                            certificate.Retirement.TerminalOpDigest
                            certificate.Retirement.LedgerRecordCount
                            certificate.Retirement.LedgerHeadDigest
                            op.DeployRecordDigest
                            op.Digest
                            op.RecordCount
                            op.HeadDigest
                    )
            else
                let! headStatus =
                    signatureStatus
                        verifier
                        head.KeyId
                        head.Algorithm
                        head.Signature
                        (headBytes head.RecordCount head.HeadDigest)

                let! opStatus = signatureStatus verifier op.KeyId op.Algorithm op.Signature (terminalBytes op)

                match headStatus, opStatus with
                | HeadSignatureValid(headKeyId, _), HeadSignatureValid _ ->
                    return DecommissionVerified(op.DeployId, op.RecordCount, op.HeadDigest, headKeyId)
                | _ -> return DecommissionUnproven(headStatus, opStatus)
    }

// ── The certificate as a signed, stock-verifiable statement ─────────────

/// The versioned predicate type URI, in the shape this SDK's other
/// attestations use.
///
/// Its own type, and not the scoped export's: a verifier keys on
/// `predicateType` to decide what shape it is about to read, and a
/// deployment's END is a different claim from one party's slice of its
/// ledger.
[<Literal>]
let PredicateType =
    "https://toolup-forge.io/attestations/deployment-decommission/v1"

/// The in-toto subject name for a decommission certificate.
[<Literal>]
let SubjectName = "deployment-decommission"

/// The canonical form the content id addresses and the predicate
/// carries — one string, so the bytes a stock tool hashes and the bytes
/// inside the statement cannot differ.
let canonicalForm (certificate: DecommissionCertificate) : string =
    JsonSerializer.Serialize(certificate, jsonOptions) |> canonicaliseJson

/// The canonical bytes a stock DSSE tool hashes to check the subject
/// claim.
let canonicalBytes (certificate: DecommissionCertificate) : byte[] =
    Encoding.UTF8.GetBytes(canonicalForm certificate)

/// The content id: lowercase-hex SHA-256 over the canonical bytes.
let contentId (certificate: DecommissionCertificate) : string =
    DsseEnvelope.sha256Hex (canonicalBytes certificate)

/// The in-toto subject: the certificate's content id under the `sha256`
/// digest key, which is what the id genuinely is.
let subjectFor (certificate: DecommissionCertificate) : InTotoSubject = {
    Name = SubjectName
    Digest = [ "sha256", contentId certificate ]
}

/// The predicate JSON — the canonical form, verbatim.
let predicateJson (certificate: DecommissionCertificate) : string = canonicalForm certificate

/// The unsigned in-toto statement. Exposed for tests and for a caller
/// that signs through its own path.
let statementJson (certificate: DecommissionCertificate) : string =
    DsseEnvelope.statementJson [ subjectFor certificate ] PredicateType (predicateJson certificate)

/// Wrap a decommission certificate as a DSSE-signed in-toto statement.
///
/// **A third signature, over a third claim.** This one binds THIS
/// DOCUMENT to the key that issued it; the head signature inside binds
/// the source chain, and the terminal op's binds the closure. All three
/// are carried, none is re-expressed in terms of another, and a holder
/// checks each for the claim it actually makes.
let sign
    (signer: IStatementEnvelopeSigner)
    (certificate: DecommissionCertificate)
    : Async<Result<DsseEnvelope, string>> =
    DsseEnvelope.sign signer [ subjectFor certificate ] PredicateType (predicateJson certificate)

/// What a holder requires of a decommission envelope. `expectedContentId`
/// is an id the holder independently possesses; `None` skips the subject
/// check, which is right only when it has no independent handle.
let expectation (expectedContentId: string option) : EnvelopeExpectation = {
    PredicateType = PredicateType
    SubjectDigest = expectedContentId
}

/// Read a certificate out of a predicate that has already been
/// signature-verified, or out of the crypto-free document reader below.
let readCertificate (predicate: string) : Result<DecommissionCertificate, EnvelopeVerdict> =
    try
        let certificate =
            JsonSerializer.Deserialize<DecommissionCertificate>(predicate, jsonOptions)

        if obj.ReferenceEquals(certificate.TerminalOp, null) then
            Error(EnvelopeMalformed "predicate is not a decommission certificate (no terminal op)")
        elif obj.ReferenceEquals(certificate.Head, null) then
            Error(EnvelopeMalformed "predicate is not a decommission certificate (no ledger head)")
        elif obj.ReferenceEquals(certificate.Retirement, null) then
            Error(EnvelopeMalformed "predicate is not a decommission certificate (no retirement)")
        else
            Ok certificate
    with ex ->
        Error(EnvelopeMalformed(sprintf "predicate is not a readable decommission certificate: %s" ex.Message))

/// Read a decommission certificate out of a DSSE document **without
/// checking the envelope's signature**, and verify the certificate
/// inside it.
///
/// **Named for the hazard**, exactly as Phase 677's `verifyDocument` is.
/// The envelope signature is what says who ISSUED the document; the
/// verdict here says whether the closure it describes holds and whether
/// its own two internal signatures check out. A holder that wants both
/// runs the stock DSSE signature check alongside — which it can, because
/// the envelope is an ordinary DSSE envelope over an ordinary in-toto
/// statement.
let verifyDocument (verifier: ILedgerHeadVerifier option) (json: string) : Async<DecommissionVerification> = async {
    match DsseEnvelope.parse json with
    | Error reason ->
        return DecommissionUnreadable("document/envelope", sprintf "the DSSE envelope could not be read: %s" reason)
    | Ok envelope ->
        if envelope.PayloadType <> DsseEnvelope.InTotoPayloadType then
            return
                DecommissionUnreadable(
                    "document/payloadType",
                    sprintf
                        "the envelope declares payload type '%s' where an in-toto statement is '%s'"
                        envelope.PayloadType
                        DsseEnvelope.InTotoPayloadType
                )
        else
            match DsseEnvelope.readStatement envelope with
            | Error verdict -> return DecommissionUnreadable("document/statement", EnvelopeVerdict.describe verdict)
            | Ok statement ->
                if statement.PredicateType <> PredicateType then
                    return
                        DecommissionUnreadable(
                            "document/predicateType",
                            sprintf
                                "the statement declares predicate type '%s', which is not the decommission type '%s' — a reader is told what it is holding rather than what it is not"
                                statement.PredicateType
                                PredicateType
                        )
                else
                    match readCertificate statement.PredicateJson with
                    | Error verdict ->
                        return DecommissionUnreadable("document/predicate", EnvelopeVerdict.describe verdict)
                    | Ok certificate ->
                        match! verifyCertificate verifier certificate with
                        | DecommissionVerified _ as verified ->
                            // The subject is the holder's claim check
                            // and it is checked LAST, so a document
                            // that is internally broken is reported
                            // where it broke rather than as a subject
                            // mismatch.
                            let addressed = contentId certificate

                            if statement.SubjectDigests |> List.contains addressed then
                                return verified
                            else
                                return
                                    DecommissionUnreadable(
                                        "document/subject",
                                        sprintf
                                            "the statement publishes subject digest(s) '%s' and the certificate inside it is addressed '%s' — a correctly-shaped statement about a different decommission"
                                            (statement.SubjectDigests |> String.concat ", ")
                                            addressed
                                    )
                        | other -> return other
}