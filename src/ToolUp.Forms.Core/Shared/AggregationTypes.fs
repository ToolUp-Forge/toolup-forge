// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.AggregationTypes

open System
open ToolUp.Forms.FormSchema

// ─── Phase 21b — Survey aggregation types ────────────────────────────
//
// Typed shapes returned by `IFormApi.GetAggregations`. Owner/Admin
// query the rolled-up answer distribution for a Publishable form
// without enumerating individual submissions in the wire format.
//
// Per-field aggregation kind matches `FieldKind`:
//   * Numeric → mean / median / stddev / min / max / count
//   * Choice / Bool → vote count per option
//   * Text / FileField / EntityRefField / NestedFormField → response
//     count + (text only) the first N responses for spot-check
//   * Date / DateTime → min / max / count
//
// All counts include only non-`Draft` submissions so the dashboard
// reflects committed responses.

/// Per-respondent status row in `AggregationSummary.Recipients`.
/// Captures invited recipients that have / haven't responded so the
/// dashboard can render a response-progress table without joining
/// two separate APIs client-side. `RespondedAt = None` for
/// outstanding recipients.
type RecipientResponseStatus = {
    /// Opaque handle from the original `IssueRecipient.Handle`. May
    /// be an email, a hashed panel id, or any string the issuer
    /// chose. Renderer should not assume it's an email.
    Handle: string
    /// `IShareTokenStore.ShareTokenClaim.TokenId`. Same value
    /// `IPublicFormApi.SubmitWithToken` records on the persisted
    /// `Submission.Author = InvitedRespondent (tokenId, _)`.
    TokenId: string
    IssuedAt: DateTimeOffset
    ExpiresAt: DateTimeOffset
    /// `Some at` when `IShareTokenStore.MarkUsed` succeeded for this
    /// token (matches `Submission.SubmittedAt` of the linked
    /// submission); `None` when the recipient hasn't responded yet.
    RespondedAt: DateTimeOffset option
    /// `true` when the underlying `ShareTokenClaim.Revoked` is set —
    /// the link is dead but historical context is preserved.
    Revoked: bool
}

/// Per-numeric-field rolled-up stats. `StdDev` is the population
/// standard deviation over the answered values; `Median` is the
/// 50th percentile (linear interpolation on even-count populations).
/// All `option`-shaped because zero answered values means none of
/// the stats are defined; the renderer surfaces `--`.
type NumericAggregation = {
    Count: int
    Mean: float option
    Median: float option
    StdDev: float option
    Min: float option
    Max: float option
}

/// Per-choice-field vote table. `Counts` carries one entry per
/// option from the schema (zero for unselected options); the renderer
/// can compute percentages from `TotalVotes` (which differs from the
/// submission count for `MultiChoiceField` since one submission can
/// vote for many options).
type ChoiceAggregation = {
    Counts: Map<string, int>
    TotalVotes: int
}

/// Per-bool-field tally.
type BoolAggregation = { TrueCount: int; FalseCount: int }

/// Per-text-field summary. `ResponseCount` is the number of non-
/// empty responses; `Sample` is the first 10 responses verbatim
/// (truncated to 200 chars each) for the dashboard's spot-check
/// surface. Full export goes through `IFormApi.ListSubmissions`.
type TextAggregation = {
    ResponseCount: int
    Sample: string list
}

/// Per-date-field min / max range and answered-count.
type DateAggregation = {
    Count: int
    Min: DateTimeOffset option
    Max: DateTimeOffset option
}

/// Per-field aggregation discriminator. Renderer matches on this to
/// pick the right rendering shape; missing field kinds (FileField,
/// EntityRefField, NestedFormField) use `OpaqueAggregation` which
/// just carries the count.
type FieldAggregation =
    | NumericFieldAggregation of NumericAggregation
    | ChoiceFieldAggregation of ChoiceAggregation
    | BoolFieldAggregation of BoolAggregation
    | TextFieldAggregation of TextAggregation
    | DateFieldAggregation of DateAggregation
    /// Catch-all for kinds without a meaningful aggregation shape
    /// (file uploads, nested submissions, entity refs). Carries the
    /// answered-count only.
    | OpaqueAggregation of count: int

/// Reserved `IFormSubmissionAnalyser` extension slot. Default
/// aggregations don't run sentiment / NLP — that's an opt-in
/// follow-up. When a deployment registers an analyser companion (an
/// AI-backed `SentimentAnalyser`, for example), per-text-field
/// rollups carry `Some` here. Renderer treats absent values as
/// "no analyser configured".
type AnalyserOutput = {
    /// Free-form analyser identifier (`"sentiment-claude"`,
    /// `"sentiment-vader"`, etc.) so the dashboard can label which
    /// analyser produced the result.
    AnalyserName: string
    /// Phase 21c follow-up — the schema field key this output is
    /// attributed to. Recovers `(fieldKey, analyserName)` association
    /// when the cache round-trips a per-analyser `AnalyserOutput list`
    /// (the analyser may return `None` for some fields, so the cached
    /// list is sparser than the schema's field list and can't be
    /// re-zipped). Empty string for pre-cache-wiring blobs deserialised
    /// from older deployments.
    FieldKey: string
    /// Analyser-specific JSON payload. Renderer is analyser-aware
    /// or treats it as an opaque blob to display verbatim.
    Payload: string
}

/// Top-level rolled-up summary. Returned by
/// `IFormApi.GetAggregations`. `FieldAggregations` is keyed by
/// `FieldSchema.Key` matching the schema's field order;
/// `Recipients` lists every invited recipient (responded or not);
/// `Analyser*` slots are `[]` until a `IFormSubmissionAnalyser` is
/// registered server-side.
type AggregationSummary = {
    SchemaId: FormSchemaId
    SchemaVersion: int
    /// Total submissions counted (state ≠ Draft).
    SubmissionCount: int
    /// Total tokens issued for this schema (`IShareTokenStore.
    /// ListByResource` size). Includes revoked tokens.
    InvitedCount: int
    /// `SubmissionCount / InvitedCount` (None when InvitedCount = 0
    /// to avoid division-by-zero); renderer formats as percentage.
    ResponseRate: float option
    Recipients: RecipientResponseStatus list
    FieldAggregations: Map<string, FieldAggregation>
    /// Analyser outputs keyed by `(fieldKey, analyserName)`. Reserved
    /// for the future `IFormSubmissionAnalyser` extension; default
    /// `Map.empty`.
    AnalyserOutputs: Map<string * string, AnalyserOutput>
}