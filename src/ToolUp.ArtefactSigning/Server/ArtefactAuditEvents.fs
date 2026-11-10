// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The artefact-signing companion's audit events: an artefact signed and a
/// signing key rotated. The companion has no compose step, so a deployment
/// that composes a signer registers the family itself:
/// `ServerApp.withAuditEvents (AuditEventFamily.registration ArtefactAuditEvent.family)`.
module ToolUp.ArtefactSigning.ArtefactAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// `IArtefactSigner.Sign` succeeded. Reserved `SourceModule =
/// "_platform.signing"`. PII-free + secret-free: only the key id,
/// algorithm name, and a SHA-256 digest of the signed artefact travel —
/// never the artefact bytes nor any key material.
type ArtefactSignedPayload = {
    /// Actor who invoked the signer. `"system"` for automated signing
    /// pipelines; the authenticated user's id for operator-initiated
    /// signs.
    Actor: string
    /// Active signing-key id the artefact was signed under.
    KeyId: string
    /// `SigningAlgorithm.name` — `"EcdsaP256"` or `"Ed25519"`.
    Algorithm: string
    /// Lowercase-hex SHA-256 of the signed artefact bytes. Lets a
    /// compliance audit prove "this exact artefact was signed under this
    /// key" without the bytes entering the audit trail.
    ArtefactSha256: string
}

/// A new signing key became active for `IArtefactSigner.Sign`, rotating
/// out a prior key (whose public component remains discoverable for
/// archival verification). Reserved `SourceModule = "_platform.signing"`.
/// Emitted by the rotation helper; the in-process default does not
/// auto-rotate, so this fires only on an explicit operator rotation.
type SigningKeyRotatedPayload = {
    /// Actor who triggered the rotation.
    Actor: string
    /// Key id rotated out of active signing. `None` for the very first
    /// key activation (no predecessor).
    OldKeyId: string option
    /// Key id now active for signing.
    NewKeyId: string
    /// `SigningAlgorithm.name` of the new active key.
    Algorithm: string
}

/// Every audit event the ToolUp.ArtefactSigning companion records.
type ArtefactAuditEvent =
    /// `IArtefactSigner.Sign` produced a detached-JWS
    /// signature over an arbitrary artefact. Reserved `SourceModule =
    /// "_platform.signing"`. Payload carries the key id + artefact
    /// SHA-256, never the bytes. Distinct from the Phase 30a
    /// module-distribution family, which Phase 625 renamed to
    /// `ModuleArtefactSigned` precisely because a one-vowel difference
    /// was not a safe way to tell two security events apart.
    | ArtefactSigned of ArtefactSignedPayload
    /// a new artefact-signing key became active, rotating out
    /// a predecessor whose public key remains discoverable for archival
    /// verification. Reserved `SourceModule = "_platform.signing"`.
    | SigningKeyRotated of SigningKeyRotatedPayload

module ArtefactAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.ArtefactSigning"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.ArtefactSigning family: its kinds and its codec.
    let family: AuditEventFamily<ArtefactAuditEvent> = {
        Registrant = Registrant
        Kinds = [ kind "ArtefactSigned"; kind "SigningKeyRotated" ]
        Encode =
            function
            | ArtefactSigned p -> "ArtefactSigned", AuditPayloadJson.write p
            | SigningKeyRotated p -> "SigningKeyRotated", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "ArtefactSigned" -> ArtefactSigned(AuditPayloadJson.read payload)
                | "SigningKeyRotated" -> SigningKeyRotated(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.ArtefactSigning audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: ArtefactAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : ArtefactAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.ArtefactSigning events.
let (|ArtefactAudit|_|) (audit: AuditEvent) : ArtefactAuditEvent option = ArtefactAuditEvent.tryRead audit