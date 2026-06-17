// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Shared types for the CSV column-mapping Data Manager — the opt-in
/// front stage that lets an arbitrary CSV be coerced into a registered
/// `DataType` by mapping the type's schema fields to the CSV's actual
/// columns. The mapping is keyed to the CSV's column-structure
/// (`Fingerprint`) so the same shape auto-applies on subsequent uploads.
///
/// Lives in `Core` (Fable-safe) so the smart-match engine
/// (`ColumnMapping.fs`) and the persisted/reusable `ColumnMapping`
/// record are visible to both the client wizard and the server store.
module ColumnMappingTypes

open System
open DataManagementTypes

/// Confidence / risk classification for a single auto-suggested
/// field→column match. The client surfaces every non-`Confident` flag
/// in a "review these" list so the user double-checks the heuristic's
/// guesses before ingesting.
type ColumnMatchFlag =
    /// High-confidence match — name and type both line up.
    | Confident
    /// Best candidate scored below the confidence threshold. Likely
    /// right but worth a glance.
    | LowConfidence
    /// Name matched well but the CSV column's inferred type disagrees
    /// with the schema field's declared type (e.g. text where a number
    /// is expected).
    | TypeMismatch
    /// Two or more CSV columns scored almost equally for this field —
    /// the engine guessed, but the alternative is nearly as plausible.
    | Ambiguous
    /// A `Required` schema field with no plausible CSV column. Blocks
    /// confirmation until the user picks one manually.
    | Unmatched

/// One schema field's auto-suggested mapping result.
type FieldSuggestion = {
    /// The target schema field this suggestion is for.
    Field: DataTypeColumn
    /// Best-guess CSV column. `None` only when `Flag = Unmatched`.
    SuggestedColumn: string option
    /// Combined confidence score in [0.0, 1.0] (name similarity
    /// weighted by type compatibility).
    Score: float
    /// Risk classification driving the "review these" surface.
    Flag: ColumnMatchFlag
    /// Other plausible CSV columns, best-first (for the override
    /// dropdown), excluding `SuggestedColumn`.
    Alternatives: string list
}

/// The full auto-mapping result for one (target type, CSV) pairing.
type MappingSuggestion = {
    TargetTypeId: DataTypeId
    Fields: FieldSuggestion list
    /// Column-structure key for the source CSV (see
    /// `ColumnMapping.Fingerprint.ofHeaders`).
    Fingerprint: string
}

/// The persisted, reusable mapping. Keyed by `Fingerprint` within a
/// storage scope; crosses the wire via `IColumnMappingApi`. `FieldToColumn`
/// maps schema-field name → source-CSV column name.
type ColumnMapping = {
    Fingerprint: string
    TargetTypeId: DataTypeId
    FieldToColumn: Map<string, string>
    /// The source CSV's header set at save time — kept for display and
    /// to detect when a re-used fingerprint's headers have drifted.
    SourceHeaders: string list
    CreatedBy: string
    CreatedAt: DateTime
}