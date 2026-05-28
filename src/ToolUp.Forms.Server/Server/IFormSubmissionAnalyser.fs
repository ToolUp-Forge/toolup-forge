module ToolUp.Forms.IFormSubmissionAnalyser

open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.AggregationTypes

// ─── Phase 21b — `IFormSubmissionAnalyser` extension point ──────────
//
// Reserved interface for companion-supplied submission analysis —
// sentiment, NLP topic clustering, language detection, etc. The
// SDK ships no built-in implementation; the slot exists so future
// AI-backed analysers (likely living in a `src/FormsAnalysers/`
// directory paralleling `src/AIProviders/`) can plug into the
// aggregation pipeline without renegotiating the wire format.
//
// `IFormApi.GetAggregations` resolves all registered
// `IFormSubmissionAnalyser` instances from DI, runs each against
// the per-field text values, and folds the outputs into
// `AggregationSummary.AnalyserOutputs` keyed by `(fieldKey,
// analyserName)`. Renderer treats the empty default as "no
// analyser configured" and skips the section.
//
// **Phase 9c portability** — six rules honoured by construction:
//   1. Identity by value — `analyserName: string`, no live handles.
//   2. Async at every boundary — `Analyse` returns `Async<_>`.
//   3. Retry / supervision as data — failures returned as `Error`,
//      not callbacks. The aggregator logs and skips on failure.
//   4. Stateless between invocations — each `Analyse` call carries
//      its own input; no in-memory state assumed across calls.
//   5. No cross-shard ordering — analyser results are independent.
//   6. Precision N/A — no time semantics.

type IFormSubmissionAnalyser =
    /// Stable name for the analyser. Echoed onto every produced
    /// `AnalyserOutput`; used as a deduplication key in
    /// `AggregationSummary.AnalyserOutputs`. Must be unique across
    /// registered analysers within a deployment — a duplicate-name
    /// registration overwrites silently (the implementation
    /// concern is left to the consumer's DI registration order).
    abstract Name: string

    /// `Some` analyser-specific output for the field, or `None`
    /// when the field's `FieldKind` isn't applicable (a sentiment
    /// analyser returns `None` for `NumberField`, for instance).
    /// Errors are returned as `Some payload` carrying an analyser-
    /// specific error shape, OR as `None` if the failure is total
    /// — the aggregator never propagates the failure further.
    abstract Analyse:
        schema: FormSchema * field: FieldSchema * submissions: Submission list -> Async<AnalyserOutput option>