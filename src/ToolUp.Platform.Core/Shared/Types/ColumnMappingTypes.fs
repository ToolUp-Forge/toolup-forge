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

// ─── Data-quality scan + remediation ──────────────────────────────

/// Day/month ordering for a date column whose values don't make the
/// order self-evident (every part ≤ 12). Resolves the
/// American (`MonthFirst`) vs rest-of-world (`DayFirst`) ambiguity; a
/// 4-digit-year-first column is `YearFirst` (ISO).
type DateOrder =
    | DayFirst
    | MonthFirst
    | YearFirst

/// A normalising cell transform applied to a *source* column before the
/// value is mapped — so the file handed to `DataType.Process` carries
/// clean, typed values (real numbers, ISO dates) rather than display
/// text. Transforms are persisted inside the `ColumnMapping`, so a
/// re-imported structure is cleaned automatically.
type CellTransform =
    /// Strip leading/trailing whitespace.
    | Trim
    /// Remove thousands separators between digits (`1,234.56` → `1234.56`).
    | StripThousandsSeparators
    /// Remove a leading/trailing currency symbol (the stripped symbol is
    /// retained for the column's unit label).
    | StripCurrency of symbol: string
    /// Remove a trailing percent sign (`12%` → `12`). The numeric value is
    /// kept as-is; the `%` is retained for the column's unit label.
    | StripPercent
    /// European decimal convention → dot (`1.234,56` → `1234.56`).
    | DecimalCommaToDot
    /// Replace Excel's text-guard leading apostrophe (`'42` → `42`).
    | StripLeadingApostrophe
    /// Blank out null-marker tokens (`N/A`, `-`, `NULL`, …) to empty.
    | BlankNullMarkers of markers: string list
    /// Normalise booleans (`Y`/`Yes`/`1`/`true` → `true`, etc.).
    | NormaliseBoolean
    /// Parse a date under the given order and re-emit as ISO `yyyy-MM-dd`.
    | ParseDateToIso of order: DateOrder

/// A detected data-quality problem in a source column.
type IssueKind =
    /// Values are numeric but rendered as text (currency, thousands
    /// separators, percent, stray whitespace, Excel apostrophe).
    | NumbersFormattedAsText
    /// A date column whose day/month order is genuinely ambiguous (every
    /// value ≤ 12/12). Needs an explicit user choice.
    | AmbiguousDateFormat
    /// A date column whose order is self-evident (some value > 12) — the
    /// fix is auto-resolvable, surfaced for transparency.
    | ResolvedDateFormat
    /// Null-marker tokens present alongside real values.
    | NullMarkersPresent
    /// Leading/trailing whitespace on values.
    | LeadingTrailingWhitespace

/// One detected issue plus its remediation.
type ColumnIssue = {
    Kind: IssueKind
    /// Human-readable description for the review UI.
    Detail: string
    /// A few example raw values exhibiting the issue.
    Examples: string list
    /// The transform(s) that fix it.
    Suggested: CellTransform list
    /// `true` for the safe, high-confidence fixes (pre-checked in the
    /// review step); `false` for fixes that change values materially or
    /// need a decision (an ambiguous date).
    Safe: bool
    /// `true` when the user MUST decide before proceeding (ambiguous
    /// date order). Blocks the review step's "Continue".
    NeedsChoice: bool
}

/// The data-quality profile of one source column.
type ColumnProfile = {
    Column: string
    /// Type inferred *after* applying the suggested safe transforms.
    InferredType: ColumnType
    /// A currency/percent symbol stripped from the values, retained so
    /// the mapping UI can label the column (`Price ($)`) and keep `$` vs
    /// `£` columns distinguishable.
    DetectedUnit: string option
    Issues: ColumnIssue list
}

/// The persisted, reusable mapping. Keyed by `(Fingerprint, TargetTypeId)`
/// within a storage scope; crosses the wire via `IColumnMappingApi`.
/// `FieldToColumn` maps schema-field name → source-CSV column name;
/// `Transforms` maps source-CSV column name → the remediation applied to
/// its cells before mapping.
type ColumnMapping = {
    Fingerprint: string
    TargetTypeId: DataTypeId
    FieldToColumn: Map<string, string>
    /// Per-source-column data-quality remediation, applied during rewrite.
    Transforms: Map<string, CellTransform list>
    /// The source CSV's header set at save time — kept for display and
    /// to detect when a re-used fingerprint's headers have drifted.
    SourceHeaders: string list
    CreatedBy: string
    CreatedAt: DateTime
}