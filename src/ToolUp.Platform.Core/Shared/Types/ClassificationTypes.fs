// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 41 — data-classification substrate, shared types ────────────
//
// Tag entity / module fields with a sensitivity classification and gate
// access / audit / encryption per classification. The SDK ships an
// opinionated six-level DU; jurisdictional translation tables
// (GDPR vs DPDPA vs HIPAA tiers) are deployment-specific and layer on
// top. Field-level only in the MVP (no cell-level-within-matrix).

/// Sensitivity classification for a field. Ordered loosely from least to
/// most sensitive; `isSensitive` draws the line the default gate uses.
type ClassificationLevel =
    /// Non-sensitive; readable by anyone who can read the entity.
    | Public
    /// Internal / commercially confidential; not special-category data.
    | Confidential
    /// Monetary / financial figures (revenue, balances, pricing).
    | Financial
    /// Regulated data attracting a specific compliance regime
    /// (e.g. health, legal-hold) that isn't itself personal data.
    | Regulatory
    /// Personally identifiable information (name, email, address).
    | Pii
    /// Special-category / sensitive personal information under GDPR
    /// Art. 9 (health, biometric, political, etc.).
    | Spi

module ClassificationLevel =
    /// Stable case-name string for audit payloads + config persistence.
    let name =
        function
        | Public -> "Public"
        | Confidential -> "Confidential"
        | Financial -> "Financial"
        | Regulatory -> "Regulatory"
        | Pii -> "Pii"
        | Spi -> "Spi"

    let tryParse (s: string) : ClassificationLevel option =
        match s with
        | "Public" -> Some Public
        | "Confidential" -> Some Confidential
        | "Financial" -> Some Financial
        | "Regulatory" -> Some Regulatory
        | "Pii" -> Some Pii
        | "Spi" -> Some Spi
        | _ -> None

    /// True for classifications the default gate treats as needing
    /// elevated access (`Financial` / `Regulatory` / `Pii` / `Spi`).
    /// `Public` / `Confidential` are readable by any authenticated caller
    /// under the default policy.
    let isSensitive =
        function
        | Public
        | Confidential -> false
        | Financial
        | Regulatory
        | Pii
        | Spi -> true

    /// True for personal-data classifications (`Pii` / `Spi`) — the
    /// lookup key for DSAR "find all PII for subject X".
    let isPersonalData =
        function
        | Pii
        | Spi -> true
        | _ -> false

/// A single field's classification within an entity type.
type FieldClassification = {
    /// Registered entity-type name (matches the `Type` field convention
    /// used by `IEntityStore`).
    EntityName: string
    /// Dotted path into the (possibly nested) record — e.g. `"Email"`,
    /// `"Profile.HomeAddress"`.
    FieldPath: string
    /// Sensitivity level.
    Level: ClassificationLevel
    /// When `true`, the gate emits a `ClassifiedFieldRead` audit event on
    /// every read of this field, not only on writes.
    AuditOnRead: bool
    /// Per-classification encryption key id (Phase 22 integration). When
    /// `Some`, the entity-store decorator routes encrypt / decrypt for
    /// this field through the named KEK; `None` uses the deployment
    /// master key.
    EncryptionKeyId: string option
}

module FieldClassification =
    /// Construct a classification with the common defaults
    /// (`AuditOnRead = false`, no per-classification key).
    let create (entityName: string) (fieldPath: string) (level: ClassificationLevel) : FieldClassification = {
        EntityName = entityName
        FieldPath = fieldPath
        Level = level
        AuditOnRead = false
        EncryptionKeyId = None
    }

    let withAuditOnRead (c: FieldClassification) = { c with AuditOnRead = true }
    let withEncryptionKey (keyId: string) (c: FieldClassification) = { c with EncryptionKeyId = Some keyId }

    /// Module-author API — declare classifications for `'Entity` using
    /// the type name as the entity name. `fields` is a list of
    /// `(fieldPath, level)` pairs. Flows into the runtime classifier
    /// registry at compose time.
    let inline attach<'Entity> (fields: (string * ClassificationLevel) list) : FieldClassification list =
        fields |> List.map (fun (path, level) -> create typeof<'Entity>.Name path level)

/// A classified-field match for a DSAR subject lookup. Powers Phase 9h
/// Article 15 export (which fields to pull) + Article 17 erasure (which
/// fields to redact / tombstone).
type FieldHit = {
    EntityName: string
    FieldPath: string
    Level: ClassificationLevel
    /// The subject the lookup was performed for. Echoed so a batched
    /// lookup result is self-describing.
    SubjectId: string
}