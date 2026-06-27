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

/// A reusable **conversion recipe** — the saved, replayable definition of
/// how a CSV of a given column-structure is turned into a platform data
/// type. It is more than a column mapping: it bundles the two named parts
/// of the ingestion/conversion stage — the field **Mapping** and the
/// data-quality **Remediation**. Keyed by `(Fingerprint, TargetTypeId)`
/// within a storage scope; crosses the wire via `IConversionApi`.
type Conversion = {
    Fingerprint: string
    TargetTypeId: DataTypeId
    /// Schema-field name → source-CSV column name.
    Mapping: Map<string, string>
    /// Source-CSV column name → the remediation transforms applied to its
    /// cells before mapping.
    Remediation: Map<string, CellTransform list>
    /// The source CSV's header set at save time — kept for display and
    /// to detect when a re-used fingerprint's headers have drifted.
    SourceHeaders: string list
    CreatedBy: string
    CreatedAt: DateTime
}

/// Provenance for a single ingested data object: the record, attached to
/// the produced file, of the conversion that created it. Marks the
/// conversion + remediation as part of the ingestion stage so it is
/// transparent and auditable — distinct from the reusable `Conversion`
/// recipe (which is keyed by structure for replay).
type ConversionRecord = {
    /// The produced (derived) data-object file name in the store.
    ProducedFile: string
    /// The original uploaded file name.
    SourceFile: string
    Fingerprint: string
    TargetTypeId: DataTypeId
    /// Schema-field name → source column it was drawn from.
    Mapping: Map<string, string>
    /// Human-readable per-column remediation steps applied (e.g.
    /// "Amount: stripped $, removed thousands separators").
    RemediationSteps: string list
    ConvertedBy: string
    ConvertedAt: DateTime
}

// ─── Dry-run validation (Phase 218) ───────────────────────────────

/// One failing cell in a dry-run validation, flattened to wire-safe
/// primitives so the report crosses `IConversionApi` to the Fable
/// wizard without dragging the server-only validation engine's types
/// into the client. Mirrors the errors-as-data shape of the platform's
/// per-cell validation report (GP 12.3) — a failing cell is data, not a
/// thrown exception.
type DryRunCellIssue = {
    /// 1-based row number as the user sees it in a spreadsheet — the
    /// canonical header is row 1, so the first data row is row 2.
    Row: int
    /// The target schema field whose mapped cell failed.
    Column: string
    /// Human-readable statement of what the schema expected
    /// (e.g. "a number", "a date").
    Expected: string
    /// The cell text (post-remediation, pre-commit) that failed.
    Actual: string
    /// The specific constraint violated (e.g. "required value
    /// missing"), or `None` when the value simply isn't the declared
    /// type.
    Violation: string option
}

/// One structural failure (something wrong with a whole row, or a
/// mapped-away required column) surfaced by a dry-run validation.
type DryRunRowIssue = {
    /// 1-based row number; `0` for a header / schema-level structural
    /// problem that isn't tied to a single data row.
    Row: int
    Detail: string
}

/// The value-typed dry-run validation report shown between confirm-
/// mapping and commit: pass/fail counts plus a capped sample of failing
/// cells (the UI groups them by column). Wire-safe + Fable-friendly — no
/// server-only types cross the boundary. `CommitBlocked` carries the
/// server-side policy verdict so the wizard's commit honours it.
type DryRunReport = {
    TargetTypeId: DataTypeId
    /// Data rows examined (excludes the header).
    TotalRows: int
    /// Rows where every mapped cell bound cleanly — would commit.
    PassedRows: int
    /// Rows with at least one failing cell or a structural failure.
    FailedRows: int
    /// Capped sample of failing cells — the "here's why and where"
    /// detail behind the `FailedRows` tally.
    CellIssues: DryRunCellIssue list
    /// Capped sample of structural row failures.
    RowIssues: DryRunRowIssue list
    /// `true` when more failing cells exist than the sample carries, so
    /// `CellIssues` is a prefix, not the whole file's.
    Truncated: bool
    /// Server-side policy verdict (`ServerConfig.MappingDryRun`):
    /// `true` = the configured policy blocks commit because rows would
    /// fail. The default policy is warn-only, so this is `false` unless
    /// a deployment opted into blocking.
    CommitBlocked: bool
}

/// The dry-run request: the confirmed conversion recipe (mapping +
/// remediation + target type) plus the raw source CSV whose *mapped*
/// shape is validated. Crosses `IConversionApi.ValidateConversion`.
type DryRunValidationRequest = {
    Conversion: Conversion
    RawCsv: string
}