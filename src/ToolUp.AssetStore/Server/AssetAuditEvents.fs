// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The asset store companion's audit events: an asset uploaded and an asset
/// deleted. Registered from `withAssetStore`; Core never names them.
module ToolUp.AssetStore.AssetAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// Emitted by `IAssetStore.Upload` on success. PII-free —
/// `OriginalFilename` is the only user-supplied string and is the
/// uploader's own filename (the data already exists in their
/// scope; the audit echoes it for forensic linkage). Excludes
/// `AltText` and `Caption` deliberately.
type AssetUploadedPayload = {
    /// Actor who performed the upload. Resolved server-side from
    /// `AccessContext.UserId`.
    UserId: UserId
    /// Asset id minted by the store.
    AssetId: string
    /// SHA-256 of the original bytes (hex, lowercase). Two uploads
    /// of identical bytes share this — operators reconciling
    /// storage footprint with record count grep on this value.
    ContentHash: string
    /// User's original filename. Echoed verbatim from the upload.
    OriginalFilename: string
    /// MIME type sniffed at upload.
    MimeType: string
    /// Size of the original bytes.
    SizeBytes: int64
    /// Derivative profile pinned to the record.
    Profile: string
}

/// Emitted by `IAssetStore.Delete` on a record that existed.
/// Idempotent deletes of unknown ids do NOT emit (the delete
/// returned `Ok` after a no-op — there's no state change to record).
type AssetDeletedPayload = {
    UserId: UserId
    AssetId: string
    ContentHash: string
}

/// Every audit event the ToolUp.AssetStore companion records.
type AssetAuditEvent =
    /// `IAssetStore.Upload` succeeded. PII-free; alt-text
    /// and caption are deliberately excluded (treated as user
    /// content). Reserved `SourceModule = "_platform.assets"`.
    | AssetUploaded of AssetUploadedPayload
    /// `IAssetStore.Delete` removed an existing record.
    /// Idempotent deletes of unknown ids do not emit. Reserved
    /// `SourceModule = "_platform.assets"`.
    | AssetDeleted of AssetDeletedPayload

module AssetAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.AssetStore"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.AssetStore family: its kinds and its codec.
    let family: AuditEventFamily<AssetAuditEvent> = {
        Registrant = Registrant
        Kinds = [ kind "AssetUploaded"; kind "AssetDeleted" ]
        Encode =
            function
            | AssetUploaded p -> "AssetUploaded", AuditPayloadJson.write p
            | AssetDeleted p -> "AssetDeleted", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "AssetUploaded" -> AssetUploaded(AuditPayloadJson.read payload)
                | "AssetDeleted" -> AssetDeleted(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.AssetStore audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: AssetAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : AssetAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.AssetStore events.
let (|AssetAudit|_|) (audit: AuditEvent) : AssetAuditEvent option = AssetAuditEvent.tryRead audit