// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The facts companion's audit events: a fact import accepted or refused,
/// and a grounding certificate issued. Registered from
/// `FactsCompose.withFactStore`; Core never names them.
module ToolUp.Facts.FactsAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// one attempt to import a fact from a peer deployment under a
/// grounding certificate, accepted or refused.
///
/// **Recorded on the accepted verdict too**, the Phase 657 / 680
/// discipline: a trail carrying only refusals cannot distinguish a
/// deployment whose imports were all sound from one whose import door was
/// never composed, and those are the two states an auditor most needs to
/// tell apart.
///
/// **PII-free by construction.** Identifiers, a metric id, a rendered
/// subject reference, and disclosure stances. The imported fact's VALUE
/// never rides this row — nor does it appear in the certificate, which
/// carries chain structure only.
///
/// **Both stances are recorded because the pair is the claim.** `Declared`
/// is what the peer sealed into its certificate; `Effective` is what the
/// fact landed under. An import may narrow and may never widen, so a row
/// where `Effective` is more permissive than `Declared` is a defect
/// visible from the trail alone, with no access to the door's code.
type FactImportPayload = {
    /// The peer whose key material the certificate was checked against —
    /// the name the importing deployment composed the anchor under, not a
    /// value read out of the offered document.
    PeerId: string
    /// The signing-key id the peer's anchor names.
    PeerKeyId: string
    /// The root the peer's certificate is issued over. Empty when the
    /// certificate could not be read at all.
    CertificateRoot: string
    /// The content-addressed reference recorded as the imported fact's
    /// provenance (`MethodRef.Imported`). Empty on a refusal that never
    /// reached a readable certificate.
    CertificateRef: string
    /// The content-addressed fact id the door re-derived from the offered
    /// identity tuple — the value compared against `CertificateRoot`.
    DerivedFactId: string
    /// The id of the fact actually asserted locally. Differs from
    /// `DerivedFactId` by construction: the local assertion's method is
    /// `Imported`, which participates in the content address. Empty on
    /// every refusal path, where nothing was asserted.
    ImportedFactId: string
    /// Readable subject reference (`hierarchy/level>level`).
    Subject: string
    /// Registered metric id of the offered fact.
    Metric: string
    /// The stance the peer sealed into the certificate
    /// (`Surfaceable` / `Internal` / `Restricted(policy)`). Empty when the
    /// certificate was never read.
    DeclaredDisclosure: string
    /// The stance the import landed under — the conservative floor of the
    /// declared stance and the anchor's ceiling. Never wider than
    /// `DeclaredDisclosure`. Empty on a refusal.
    EffectiveDisclosure: string
    /// The attestation level the peer's certificate claims, as its stable
    /// wire name — present only when the offered document was the
    /// levels-bound projection, and recorded on a refusal on level grounds
    /// as well as on an accepted import.
    ///
    /// **Empty means the document claimed no level, never that it claimed
    /// the weakest one.** A certificate carrying a detached seal makes no
    /// statement about the signing key's custody at all, and defaulting
    /// this field to a level nobody claimed would put an assertion into the
    /// trail that no signature covers — which is the one thing an audit row
    /// must never do.
    AttestationLevel: string
    /// The typed refusal, rendered. Empty on an accepted import.
    Reason: string
    OccurredAt: DateTimeOffset
}

/// one grounding certificate was issued.
///
/// **The issuance log is what makes a certificate enumerable.** A holder
/// has always been able to verify the certificate in their hand; nobody
/// could ask the other question — *what has this deployment certified?* —
/// and a certificate that was issued and then quietly disowned left no
/// trace at all. One row per issuance turns the audit trail into the
/// deployment's own certificate log, and under a chained ledger that log
/// is tamper-evident: a suppressed issuance is a break the chain verifier
/// positions, not an absence nobody can see.
///
/// **Identifiers only — never the body.** A digest, the subject content
/// id, the signing-key id, and which seal was used. That is deliberate
/// and it is the whole reason this row is safe to keep: the certificate
/// body carries a provenance chain filtered through the disclosure
/// predicate, and copying any of it onto an audit row would move that
/// content to a surface the predicate never ran at. The digest is
/// sufficient for inclusion — a holder recomputes it from the bytes they
/// hold — and insufficient for anything else, which is exactly the
/// property wanted.
type CertificateIssuedPayload = {
    /// Lowercase-hex SHA-256 over the certificate's canonical signed bytes
    /// — the same digest a holder recomputes from the document they hold,
    /// so an inclusion check needs nothing from the issuer to run.
    Digest: string
    /// The subject the certificate is issued over: the answer message id
    /// or the fact content id at the chain root.
    Subject: string
    /// The signing-key id bound into the signed body. Names WHICH key
    /// sealed it, so a rotation leaves the log still readable.
    KeyId: string
    /// Which issue path sealed it: `"detached-jws"` (the direct
    /// `IArtefactSigner` path) or `"application-seal"` (the attested path,
    /// whose envelope also carries the purpose and attestation level).
    /// A discriminator rather than a lookup, because the two make
    /// different claims and an enumerator should not have to fetch the
    /// document to tell them apart.
    Seal: string
    /// The certificate interchange format version the body declared.
    Format: string
    OccurredAt: DateTimeOffset
}

/// Every audit event the ToolUp.Facts companion records.
type FactsAuditEvent =
    /// a fact offered by a peer verified against that peer's
    /// certificate, re-derived to the id the certificate names, and landed
    /// under a stance no wider than the one the peer declared.
    | FactImportAccepted of FactImportPayload
    /// an offered fact was refused at the import door and
    /// nothing was asserted. The unverifiable / tampered / id-mismatched
    /// signal, on the same audited path as every other refusal.
    | FactImportRefused of FactImportPayload
    /// a grounding certificate was issued. Identifiers only:
    /// the certificate digest, its subject, the sealing key id. This is
    /// the row that makes issuance enumerable and a suppressed
    /// certificate visible.
    | CertificateIssued of CertificateIssuedPayload

module FactsAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.Facts"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.Facts family: its kinds and its codec.
    let family: AuditEventFamily<FactsAuditEvent> = {
        Registrant = Registrant
        Kinds = [
            kind "FactImportAccepted"
            kind "FactImportRefused"
            kind "CertificateIssued"
        ]
        Encode =
            function
            | FactImportAccepted p -> "FactImportAccepted", AuditPayloadJson.write p
            | FactImportRefused p -> "FactImportRefused", AuditPayloadJson.write p
            | CertificateIssued p -> "CertificateIssued", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "FactImportAccepted" -> FactImportAccepted(AuditPayloadJson.read payload)
                | "FactImportRefused" -> FactImportRefused(AuditPayloadJson.read payload)
                | "CertificateIssued" -> CertificateIssued(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.Facts audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: FactsAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : FactsAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.Facts events.
let (|FactsAudit|_|) (audit: AuditEvent) : FactsAuditEvent option = FactsAuditEvent.tryRead audit