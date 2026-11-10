// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The passkey auth provider's audit events: a credential registered and a
/// credential removed. Registered from `withPasskey`; Core never names them.
module ToolUp.AuthProviders.Passkey.PasskeyAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// a WebAuthn passkey credential was enrolled for a user via
/// the passkey auth companion's registration ceremony. PII-free apart
/// from the platform `UserId` (already the audit actor). The credential
/// id is truncated so the trail correlates without persisting the full
/// authenticator handle. Source-module label `_platform.auth.passkey`.
type PasskeyCredentialRegisteredPayload = {
    UserId: UserId
    /// First 12 chars of the base64url credential id — correlatable,
    /// non-reversible to the raw authenticator handle.
    CredentialIdPrefix: string
    /// How the registration was authorised: `ExistingSession` /
    /// `Bootstrap` / `PendingInvite` / `OpenRegistration`.
    Grant: string
}

/// a passkey credential was removed for a user (self-service
/// deregistration or admin revocation). Source-module label
/// `_platform.auth.passkey`.
type PasskeyCredentialRemovedPayload = {
    UserId: UserId
    /// First 12 chars of the base64url credential id.
    CredentialIdPrefix: string
}

/// Every audit event the ToolUp.AuthProviders.Passkey companion records.
type PasskeyAuditEvent =
    /// a WebAuthn passkey credential was enrolled via the
    /// passkey auth companion's registration ceremony. Recorded under
    /// `_platform` scope; source-module `_platform.auth.passkey`.
    | PasskeyCredentialRegistered of PasskeyCredentialRegisteredPayload
    /// a passkey credential was removed for a user.
    | PasskeyCredentialRemoved of PasskeyCredentialRemovedPayload

module PasskeyAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.AuthProviders.Passkey"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.AuthProviders.Passkey family: its kinds and its codec.
    let family: AuditEventFamily<PasskeyAuditEvent> = {
        Registrant = Registrant
        Kinds = [ kind "PasskeyCredentialRegistered"; kind "PasskeyCredentialRemoved" ]
        Encode =
            function
            | PasskeyCredentialRegistered p -> "PasskeyCredentialRegistered", AuditPayloadJson.write p
            | PasskeyCredentialRemoved p -> "PasskeyCredentialRemoved", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "PasskeyCredentialRegistered" -> PasskeyCredentialRegistered(AuditPayloadJson.read payload)
                | "PasskeyCredentialRemoved" -> PasskeyCredentialRemoved(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.AuthProviders.Passkey audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: PasskeyAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : PasskeyAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.AuthProviders.Passkey events.
let (|PasskeyAudit|_|) (audit: AuditEvent) : PasskeyAuditEvent option = PasskeyAuditEvent.tryRead audit